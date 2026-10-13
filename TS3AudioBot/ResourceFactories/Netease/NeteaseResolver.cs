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
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TS3AudioBot.Config;
using TS3AudioBot.Helper;
using TS3AudioBot.Localization;
using TS3AudioBot.Playlists;

namespace TS3AudioBot.ResourceFactories.Netease
{
	/// <summary>
	/// Plays songs and playlists from NetEase Cloud Music (music.163.com).
	/// Direct urls are resolved shortly before playback, since netease audio links
	/// are signed and expire after a short time. VIP tracks require an optional
	/// MUSIC_U cookie and are cached to disk once resolved.
	/// </summary>
	public sealed class NeteaseResolver : IResourceResolver, IPlaylistResolver, IThumbnailResolver, ISearchResolver
	{
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

		private const string AddCover = "cover";
		private const string AddArtist = "artist";
		private const string AddAlbum = "album";
		private const string AddFee = "fee";
		private const string AddDuration = "duration";

		private static readonly Regex SongLinkRegex = new Regex(
			@"(?:https?://)?(?:y\.|m\.)?music\.163\.com/(?:#/(?:m/)?|m/)?song(?:\?[^ ]*?id=|/)(\d+)",
			Util.DefaultRegexConfig);
		private static readonly Regex PlaylistLinkRegex = new Regex(
			@"(?:https?://)?(?:y\.|m\.)?music\.163\.com/(?:#/(?:my/m/music/|m/)?|my/m/music/|m/)?playlist(?:\?[^ ]*?id=|/)(\d+)",
			Util.DefaultRegexConfig);
		/// <summary>Bare numbers are treated as song ids, but only when nothing else claims them.</summary>
		private static readonly Regex BareSongIdRegex = new Regex(@"^(\d{6,13})$", Util.DefaultRegexConfig);

		private const string ResolverName = "netease";

		private readonly NeteaseCache cache;

		public string ResolverFor => ResolverName;
		public int Bitrate { get; }

		public NeteaseResolver(ConfFactories conf)
		{
			var neteaseConf = conf.Netease;
			Bitrate = neteaseConf.Bitrate.Value;
			var cacheDir = Path.Combine(Directory.GetCurrentDirectory(), BotPaths.Cache, ResolverName);
			cache = new NeteaseCache(neteaseConf.CacheEnabled.Value, cacheDir, neteaseConf.CacheMaxMb.Value);
			allowTrial = neteaseConf.AllowTrial.Value;
		}

		private readonly bool allowTrial;

		/// <summary>
		/// The cookie is user supplied per operation (never stored server side):
		/// see <see cref="NeteaseCookieContext"/>; empty for anonymous access.
		/// </summary>
		private static string Cookie => NeteaseCookieContext.Current ?? string.Empty;

		public MatchCertainty MatchResource(ResolveContext ctx, string uri)
		{
			if (SongLinkRegex.IsMatch(uri))
				return MatchCertainty.Always;
			if (BareSongIdRegex.IsMatch(uri.Trim()))
				return MatchCertainty.OnlyIfLast;
			return MatchCertainty.Never;
		}

		public MatchCertainty MatchPlaylist(ResolveContext ctx, string uri)
			=> PlaylistLinkRegex.IsMatch(uri) ? MatchCertainty.Always : MatchCertainty.Never;

		public async Task<PlayResource> GetResource(ResolveContext ctx, string uri)
		{
			var match = SongLinkRegex.Match(uri);
			if (!match.Success)
			{
				match = BareSongIdRegex.Match(uri.Trim());
				if (!match.Success)
					throw Error.LocalStr(strings.error_media_invalid_uri);
			}
			return await ResolveSong(ctx, long.Parse(match.Groups[1].Value)).ConfigureAwait(false);
		}

		public async Task<PlayResource> GetResourceById(ResolveContext ctx, AudioResource resource)
		{
			if (!long.TryParse(resource.ResourceId, out var songId))
				throw Error.LocalStr(strings.error_media_invalid_uri);
			return await ResolveSong(ctx, songId).ConfigureAwait(false);
		}

		public string RestoreLink(ResolveContext ctx, AudioResource resource)
			=> $"https://music.163.com/song?id={resource.ResourceId}";

		private async Task<PlayResource> ResolveSong(ResolveContext ctx, long songId)
		{
			var song = await NeteaseApi.GetSong(songId, Cookie).ConfigureAwait(false);

			// cache hit: play the local file directly
			var cachedFile = cache.TryGet(songId);
			if (cachedFile != null)
				return new NeteasePlayResource(cachedFile, ToAudioResource(song), song.CoverUrl);

			// the user's cookie is only ever sent for vip songs; free songs are
			// resolved completely anonymously
			var cookie = song != null && song.IsVip ? Cookie : null;
			var urlData = await NeteaseApi.GetSongUrl(songId, Bitrate, cookie).ConfigureAwait(false);
			if (urlData?.Url is null)
			{
				if (song != null && song.IsVip && string.IsNullOrWhiteSpace(cookie))
					throw Error.LocalStr("这是一首 VIP 歌曲，无法匿名播放。请在 ts3audiobot.toml 的 [factories.netease] 中配置 MUSIC_U cookie。");
				throw Error.LocalStr("无法获取该歌曲的音频链接（可能没有版权或已下架）");
			}

			Log.Info("Netease song {0}: trial={1} level={2} br={3} cookie={4} fee={5}",
				songId, urlData.IsTrial, urlData.Level, urlData.Br, string.IsNullOrWhiteSpace(cookie) ? "none" : "set", song?.Fee);

			if (urlData.IsTrial)
			{
				// only a short trial clip; never cache it and refuse it unless explicitly allowed
				if (!allowTrial)
					throw Error.LocalStr("这是一首 VIP 歌曲，当前只能获取试听片段（已跳过）。配置 MUSIC_U cookie 后可播放完整版，或在 [factories.netease] 中设置 allow_trial = true 接受试听。");
				Log.Info("Only trial clip available for netease song {0} (no cookie)", songId);
				return new NeteasePlayResource(urlData.Url, ToAudioResource(song), song?.CoverUrl);
			}

			// cache everything resolvable; vip full versions are the scarce
			// resource (cached with the .vip marker => evicted last)
			if (cache.Enabled && song != null)
				cache.QueueDownload(songId, urlData.Url, song.IsVip);

			return new NeteasePlayResource(urlData.Url, ToAudioResource(song), song?.CoverUrl);
		}

		private static AudioResource ToAudioResource(NeteaseSong? song)
		{
			long id = song?.Id ?? 0;
			var res = new AudioResource(id.ToString(), SongTitle(song), ResolverName);
			if (song != null)
			{
				res.Add(AddArtist, song.Artists);
				res.Add(AddAlbum, song.Album);
				res.Add(AddCover, song.CoverUrl);
				res.Add(AddFee, song.Fee.ToString());
				res.Add(AddDuration, ((int)song.Duration.TotalSeconds).ToString());
			}
			return res;
		}

		private static string SongTitle(NeteaseSong? song)
		{
			if (song is null)
				return "NetEase song";
			return string.IsNullOrEmpty(song.Artists) ? song.Name : $"{song.Artists} - {song.Name}";
		}

		public async Task<Playlist> GetPlaylist(ResolveContext ctx, string url)
		{
			var match = PlaylistLinkRegex.Match(url);
			if (!match.Success)
				throw Error.LocalStr(strings.error_media_invalid_uri);

			var playlistId = long.Parse(match.Groups[1].Value);
			var cookie = Cookie;
			var meta = await NeteaseApi.GetPlaylistMeta(playlistId, Cookie).ConfigureAwait(false);
			var trackIds = await NeteaseApi.GetPlaylistTrackIds(playlistId, cookie).ConfigureAwait(false);
			var songs = await NeteaseApi.GetSongs(trackIds, Cookie).ConfigureAwait(false);
			var byId = songs.ToDictionary(s => s.Id, s => s);

			var playlist = new Playlist
			{
				Title = meta.Name,
			};
			foreach (var trackId in trackIds)
			{
				if (!byId.TryGetValue(trackId, out var song))
				{
					// keep unavailable tracks as placeholders so the order stays intact
					playlist.Add(new PlaylistItem(new AudioResource(trackId.ToString(), $"NetEase song {trackId}", ResolverName)));
					continue;
				}
				playlist.Add(new PlaylistItem(ToAudioResource(song)));
			}

			Log.Info("Loaded netease playlist {0} ({1}) with {2} tracks", meta.Name, playlistId, playlist.Items.Count);
			return playlist;
		}

		public async Task<IList<AudioResource>> Search(ResolveContext ctx, string query)
		{
			var songs = await NeteaseApi.Search(query, 20, Cookie).ConfigureAwait(false);
			return songs.Select(ToAudioResource).ToList();
		}

		public async Task GetThumbnail(ResolveContext ctx, PlayResource playResource, Func<Stream, Task> action)
		{
			var cover = playResource.AudioResource.Get(AddCover);
			if (string.IsNullOrEmpty(cover))
				throw Error.LocalStr(strings.error_media_image_not_found);
			await WebWrapper.Request(cover).ToStream(action).ConfigureAwait(false);
		}

		public NeteaseCache Cache => cache;

		public void Dispose() => cache.Dispose();
	}

	public class NeteasePlayResource : PlayResource
	{
		public string? CoverUrl { get; set; }

		public NeteasePlayResource(string uri, AudioResource baseData, string? coverUrl) : base(uri, baseData)
		{
			CoverUrl = coverUrl;
		}
	}
}
