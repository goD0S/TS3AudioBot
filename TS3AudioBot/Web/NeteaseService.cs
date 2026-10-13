// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TS3AudioBot.Dependency;
using TS3AudioBot.Helper;
using TS3AudioBot.Localization;
using TS3AudioBot.ResourceFactories;
using TS3AudioBot.Playlists;
using TS3AudioBot.ResourceFactories.Netease;

namespace TS3AudioBot.Web.Api
{
	public sealed class NeteaseImportResult
	{
		public string ListId { get; set; } = string.Empty;
		public string Title { get; set; } = string.Empty;
		public string? CoverUrl { get; set; }
		public int TrackCount { get; set; }
		public int VipCount { get; set; }
	}

	public sealed class NeteaseMigrateResult
	{
		public int PlaylistsScanned { get; set; }
		public int ItemsConverted { get; set; }
		public int ItemsFailed { get; set; }
		public List<string> PlaylistsChanged { get; set; } = new List<string>();
	}

	/// <summary>
	/// Higher level netease operations: importing playlists with rich metadata and
	/// migrating legacy "outer url" media entries to proper netease resources.
	/// </summary>
	public static class NeteaseService
	{
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

		private static readonly Regex OuterUrlIdRegex = new Regex(
			@"music\.163\.com/song/media/outer/url\?id=(\d+)",
			Helper.Util.DefaultRegexConfig);
		private static readonly Regex NeteaseIdRegex = new Regex(@"(\d{5,15})", Helper.Util.DefaultRegexConfig);

		public static async Task<NeteaseImportResult> ImportPlaylist(Bot bot, NeteaseResolver netease, string listId, string url, bool replace)
		{
			var playlistManager = bot.Injector.GetModuleOrThrow<PlaylistManager>();
			var resolver = bot.Injector.GetModuleOrThrow<ResolveContext>();

			var playlist = await resolver.LoadPlaylistFrom(url, "netease").ConfigureAwait(false);

			// best effort metadata for the list itself
			string? cover = null;
			string? creator = null;
			var idMatch = NeteaseIdRegex.Matches(url);
			if (idMatch.Count > 0 && long.TryParse(idMatch[idMatch.Count - 1].Groups[1].Value, out var neteaseId))
			{
				try
				{
					var meta = await NeteaseApi.GetPlaylistMeta(neteaseId, NeteaseCookieContext.Current).ConfigureAwait(false);
					cover = meta.CoverUrl;
					creator = meta.CreatorName;
					if (!string.IsNullOrEmpty(meta.Name))
						playlist.Title = meta.Name;
				}
				catch (AudioBotException ex) { Log.Warn("Could not fetch playlist meta: {0}", ex.Message); }
			}

			if (playlistManager.ExistsPlaylist(listId))
			{
				if (!replace)
					throw Error.LocalStr($"歌单 '{listId}' 已存在，请换一个 ID 或选择覆盖");
				playlistManager.DeletePlaylist(listId).UnwrapThrow();
			}
			playlistManager.CreatePlaylist(listId, playlist.Title).UnwrapThrow();
			playlistManager.ModifyPlaylist(listId, pl => pl.AddRange(playlist.Items).UnwrapThrow()).UnwrapThrow();
			playlistManager.SetPlaylistMeta(listId, meta =>
			{
				meta.Title = playlist.Title;
				meta.CoverUrl = cover;
				meta.Source = url;
				meta.Creator = creator;
			}).UnwrapThrow();

			var vipCount = playlist.Items
				.Select(i => i.AudioResource)
				.Count(res => res.Get("fee") == "1");

			// while the user's cookie is still in scope, resolve vip track urls so
			// the full versions get cached; the cookie itself is never persisted
			// warm the cache: vip tracks with the user's cookie if provided in this
			// request, everything else anonymously
			var cookie = NeteaseCookieContext.Current;
			_ = Task.Run(() => WarmCacheAsync(netease, cookie ?? "", playlist.Items.ToList()));

			return new NeteaseImportResult
			{
				ListId = listId,
				Title = playlist.Title,
				CoverUrl = cover,
				TrackCount = playlist.Items.Count,
				VipCount = vipCount,
			};
		}

		/// <summary>
		/// Rewrites legacy playlists which reference netease songs via expiring
		/// "outer url" media links into proper netease resources with metadata.
		/// </summary>
		public static async Task<NeteaseMigrateResult> MigrateLegacyUrls(Bot bot, NeteaseResolver netease)
		{
			var playlistManager = bot.Injector.GetModuleOrThrow<PlaylistManager>();
			var result = new NeteaseMigrateResult();

			foreach (var (listId, _) in playlistManager.GetAllPlaylistMetas().ToList())
			{
				result.PlaylistsScanned++;
				var playlistRes = playlistManager.LoadPlaylist(listId);
				if (!playlistRes.Ok)
					continue;
				var playlist = playlistRes.Value;

				// collect netease outer-url ids per index
				var found = new Dictionary<int, long>();
				for (int i = 0; i < playlist.Items.Count; i++)
				{
					var res = playlist.Items[i].AudioResource;
					if (!string.Equals(res.AudioType, "media", StringComparison.OrdinalIgnoreCase))
						continue;
					var m = OuterUrlIdRegex.Match(res.ResourceId);
					if (m.Success && long.TryParse(m.Groups[1].Value, out var songId))
						found[i] = songId;
				}
				if (found.Count == 0)
					continue;

				var songs = await NeteaseApi.GetSongs(found.Values.Distinct(), NeteaseCookieContext.Current).ConfigureAwait(false);
				var byId = songs.ToDictionary(s => s.Id, s => s);

				int converted = 0, failed = 0;
				var items = playlist.Items.ToList();
				foreach (var kvp in found)
				{
					if (byId.TryGetValue(kvp.Value, out var song))
					{
						items[kvp.Key] = new PlaylistItem(ToResource(song));
						converted++;
					}
					else failed++;
				}

				if (converted > 0)
				{
					playlistManager.ModifyPlaylist(listId, pl =>
					{
						pl.Clear();
						pl.AddRange(items).UnwrapThrow();
					}).UnwrapThrow();
					result.PlaylistsChanged.Add(listId);
					result.ItemsConverted += converted;
				}
				result.ItemsFailed += failed;
			}

			Log.Info("Netease migration done: {0} converted, {1} failed across {2} playlists",
				result.ItemsConverted, result.ItemsFailed, result.PlaylistsChanged.Count);
			return result;
		}

		/// <summary>Preview what an import would bring in, without writing anything.</summary>
		public static async Task<NeteaseImportResult> PreviewPlaylist(NeteaseResolver netease, string url)
		{
			var idMatch = NeteaseIdRegex.Matches(url);
			if (idMatch.Count == 0)
				throw Error.LocalStr("无法从链接中解析出歌单 ID");
			var neteaseId = long.Parse(idMatch[idMatch.Count - 1].Groups[1].Value);

			var meta = await NeteaseApi.GetPlaylistMeta(neteaseId, NeteaseCookieContext.Current).ConfigureAwait(false);
			var trackIds = await NeteaseApi.GetPlaylistTrackIds(neteaseId, NeteaseCookieContext.Current).ConfigureAwait(false);
			var songs = await NeteaseApi.GetSongs(trackIds, NeteaseCookieContext.Current).ConfigureAwait(false);

			return new NeteaseImportResult
			{
				ListId = string.Empty,
				Title = meta.Name,
				CoverUrl = meta.CoverUrl,
				TrackCount = trackIds.Count,
				VipCount = songs.Count(s => s.IsVip),
			};
		}

		private const int MaxWarmTracks = 300;

		/// <summary>
		/// Two-phase cache warm-up after an import: vip tracks are resolved with
		/// the user's cookie (their one-time use), everything else afterwards
		/// completely anonymously.
		/// </summary>
		private static async Task WarmCacheAsync(NeteaseResolver netease, string cookie, IList<PlaylistItem> items)
		{
			var cache = netease.Cache;
			if (!cache.Enabled)
				return;
			int vipWarmed = 0, freeWarmed = 0;
			var pending = new List<(long songId, bool isVip)>();

			foreach (var item in items)
			{
				if (pending.Count >= MaxWarmTracks)
					break;
				var res = item.AudioResource;
				if (!string.Equals(res.AudioType, "netease", StringComparison.OrdinalIgnoreCase))
					continue;
				if (!long.TryParse(res.ResourceId, out var songId) || songId == 0)
					continue;
				if (cache.TryGet(songId) != null)
					continue;
				pending.Add((songId, res.Get("fee") == "1"));
			}

			// phase 1: vip songs with the user's cookie
			foreach (var (songId, isVip) in pending.Where(p => p.isVip))
			{
				if (string.IsNullOrEmpty(cookie))
					break; // no cookie in scope: vip full versions can't be fetched
				if (await WarmOne(netease, cache, songId, isVip, cookie).ConfigureAwait(false)) vipWarmed++;
			}
			// phase 2: everything else, anonymously
			foreach (var (songId, isVip) in pending.Where(p => !p.isVip))
			{
				if (await WarmOne(netease, cache, songId, isVip, null).ConfigureAwait(false)) freeWarmed++;
			}
			Log.Info("Netease cache warm-up started: {0} vip + {1} free tracks", vipWarmed, freeWarmed);
		}

		private static async Task<bool> WarmOne(NeteaseResolver netease, NeteaseCache cache, long songId, bool isVip, string? cookie)
		{
			try
			{
				var url = await NeteaseApi.GetSongUrl(songId, netease.Bitrate, cookie).ConfigureAwait(false);
				if (url?.Url == null)
					return false;
				if (url.IsTrial) // never cache trial clips
					return false;
				return cache.QueueDownload(songId, url.Url, isVip);
			}
			catch (Exception ex)
			{
				Log.Debug("Could not warm netease song {0}: {1}", songId, ex.Message);
				return false;
			}
		}

		private static AudioResource ToResource(NeteaseSong song)
		{
			var res = new AudioResource(
				song.Id.ToString(),
				string.IsNullOrEmpty(song.Artists) ? song.Name : $"{song.Artists} - {song.Name}",
				"netease");
			res.Add("artist", song.Artists);
			res.Add("album", song.Album);
			res.Add("cover", song.CoverUrl);
			res.Add("fee", song.Fee.ToString());
			res.Add("duration", ((int)song.Duration.TotalSeconds).ToString());
			return res;
		}

	}
}
