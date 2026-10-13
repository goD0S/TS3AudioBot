// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using TS3AudioBot.Helper;
using TS3AudioBot.Localization;

namespace TS3AudioBot.ResourceFactories.Netease
{
	public sealed class NeteasePlaylistInfo
	{
		public long Id { get; set; }
		public string Name { get; set; } = string.Empty;
		public string CoverUrl { get; set; } = string.Empty;
		public string CreatorName { get; set; } = string.Empty;
		public int TrackCount { get; set; }
	}

	public sealed class NeteaseSong
	{
		public long Id { get; set; }
		public string Name { get; set; } = string.Empty;
		public string Artists { get; set; } = string.Empty;
		public string Album { get; set; } = string.Empty;
		public string CoverUrl { get; set; } = string.Empty;
		public TimeSpan Duration { get; set; }
		/// <summary>0 = free, 1 = VIP, 4 = album purchase, 8 = low quality free.</summary>
		public int Fee { get; set; }
		public bool IsVip => Fee == 1;
	}

	public sealed class NeteaseSongUrl
	{
		public long Id { get; set; }
		public string? Url { get; set; }
		public int Br { get; set; }
		public string? Type { get; set; }
		/// <summary>True when the url only serves a short trial clip (vip song without login).</summary>
		public bool IsTrial { get; set; }
		public string? Level { get; set; }
	}

	public static class NeteaseApi
	{
		private const string ApiBase = "https://music.163.com";
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

		private static readonly HttpClient Http = new HttpClient(new HttpClientHandler
		{
			AllowAutoRedirect = false,
		})
		{
			Timeout = TimeSpan.FromSeconds(15),
		};

		private static HttpRequestMessage Request(HttpMethod method, string url, string? cookie)
		{
			var msg = new HttpRequestMessage(method, url);
			msg.Headers.TryAddWithoutValidation("User-Agent",
				"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
			msg.Headers.TryAddWithoutValidation("Referer", "https://music.163.com/");
			var cookieValue = "os=pc; appver=2.10.13";
			if (!string.IsNullOrWhiteSpace(cookie))
				cookieValue += $"; MUSIC_U={cookie.Trim()}";
			msg.Headers.TryAddWithoutValidation("Cookie", cookieValue);
			return msg;
		}

		private static async Task<JObject> GetJson(string url, string? cookie)
		{
			using var resp = await Http.SendAsync(Request(HttpMethod.Get, url, cookie)).ConfigureAwait(false);
			var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
			if (!resp.IsSuccessStatusCode)
				throw Error.LocalStr($"Netease API returned HTTP {(int)resp.StatusCode}");
			try
			{
				var json = JObject.Parse(body);
				if (json.Value<int?>("code") is int code && code != 200)
					throw Error.LocalStr($"Netease API error code {code}");
				return json;
			}
			catch (Newtonsoft.Json.JsonReaderException ex)
			{
				Log.Debug(ex, "Invalid JSON from netease api");
				throw Error.Exception(ex).LocalStr("Netease API returned invalid data");
			}
		}

		public static async Task<NeteasePlaylistInfo> GetPlaylistMeta(long playlistId, string? cookie)
		{
			// v6 keeps the full track id list in trackIds; full track data is paginated.
			var json = await GetJson($"{ApiBase}/api/v6/playlist/detail?id={playlistId}&n=0", cookie).ConfigureAwait(false);
			var pl = json["playlist"] as JObject;
			if (pl is null)
				throw Error.LocalStr("Netease playlist not found (possibly private)");

			return new NeteasePlaylistInfo
			{
				Id = pl.Value<long?>("id") ?? playlistId,
				Name = pl.Value<string>("name") ?? $"Playlist {playlistId}",
				CoverUrl = pl.Value<string>("coverImgUrl") ?? string.Empty,
				CreatorName = (pl["creator"] as JObject)?.Value<string>("nickname") ?? string.Empty,
				TrackCount = pl.Value<int?>("trackCount") ?? 0,
			};
		}

		public static async Task<IList<long>> GetPlaylistTrackIds(long playlistId, string? cookie)
		{
			var json = await GetJson($"{ApiBase}/api/v6/playlist/detail?id={playlistId}&n=1000", cookie).ConfigureAwait(false);
			var pl = json["playlist"] as JObject;
			var ids = (pl?["trackIds"] as JArray)?.Select(t => t.Value<long?>("id") ?? 0).Where(i => i != 0).ToList();
			if (ids is null || ids.Count == 0)
			{
				// fallback: playlist might expose full track objects instead
				ids = (pl?["tracks"] as JArray)?.Select(t => t.Value<long?>("id") ?? 0).Where(i => i != 0).ToList();
			}
			if (ids is null || ids.Count == 0)
				throw Error.LocalStr("Netease playlist is empty or inaccessible");
			return ids;
		}

		/// <summary>Fetches song details in batches (the api allows large batches, we chunk defensively).</summary>
		public static async Task<IList<NeteaseSong>> GetSongs(IEnumerable<long> songIds, string? cookie)
		{
			var result = new List<NeteaseSong>();
			var all = songIds.Distinct().ToArray();
			for (int offset = 0; offset < all.Length; offset += 500)
			{
				var chunk = all.Skip(offset).Take(500);
				var c = JsonSerializeIds(chunk);
				var json = await GetJson($"{ApiBase}/api/v3/song/detail?c={Uri.EscapeDataString(c)}&n=1000", cookie).ConfigureAwait(false);
				var songs = json["songs"];
				if (songs is null)
					continue;
				foreach (var s in songs)
				{
					var song = ParseSong(s);
					if (song != null)
						result.Add(song);
				}
			}
			return result;
		}

		public static async Task<NeteaseSong?> GetSong(long songId, string? cookie)
			=> (await GetSongs(new[] { songId }, cookie).ConfigureAwait(false)).FirstOrDefault();

		public static async Task<NeteaseSongUrl?> GetSongUrl(long songId, int bitrate, string? cookie)
		{
			var json = await GetJson(
				$"{ApiBase}/api/song/enhance/player/url?id={songId}&ids=%5B{songId}%5D&br={bitrate}", cookie)
				.ConfigureAwait(false);
			var data = (json["data"] as JArray)?.FirstOrDefault();
			if (data is null)
				return null;
			return new NeteaseSongUrl
			{
				Id = data.Value<long?>("id") ?? songId,
				Url = NormalizeUrl(data.Value<string>("url")),
				Br = data.Value<int?>("br") ?? 0,
				Type = data.Value<string>("type"),
				Level = data.Value<string>("level"),
				IsTrial = data["freeTrialInfo"] is Newtonsoft.Json.Linq.JObject,
			};
		}

		private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, LyricLine[]> LyricCache =
			new System.Collections.Concurrent.ConcurrentDictionary<long, LyricLine[]>();

		public sealed class LyricLine
		{
			public TimeSpan Time { get; }
			public string Text { get; }
			public LyricLine(TimeSpan time, string text) { Time = time; Text = text; }
		}

		/// <summary>Fetches the timestamped lyric lines of a song (works anonymously).</summary>
		public static async Task<LyricLine[]?> GetLyricLinesAsync(long songId)
		{
			if (LyricCache.TryGetValue(songId, out var cached))
				return cached;
			try
			{
				var json = await GetJson($"{ApiBase}/api/song/lyric?id={songId}&lv=1&kv=1&tv=-1", null).ConfigureAwait(false);
				var lrc = (json["lrc"] as JObject)?["lyric"]?.Value<string>();
				if (string.IsNullOrWhiteSpace(lrc))
				{
					LyricCache[songId] = Array.Empty<LyricLine>(); // instrumental / no lyrics
					return Array.Empty<LyricLine>();
				}

				var lines = new List<LyricLine>();
				foreach (var rawLine in lrc.Split('\n'))
				{
					var line = rawLine.TrimEnd('\r').Trim();
					if (line.Length == 0)
						continue;
					if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^\[(ti|ar|al|by|offset|re|ve):", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
						continue;

					// a line may carry several timestamps: [00:01.00][03:30.00] text
					var times = new List<TimeSpan>();
					while (true)
					{
						var m = System.Text.RegularExpressions.Regex.Match(line, @"^\[(\d+):(\d+)(?:[.:](\d+))?\]");
						if (!m.Success)
							break;
						var t = new TimeSpan(0, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
						if (m.Groups[3].Success)
							t += TimeSpan.FromMilliseconds(int.Parse(m.Groups[3].Value.PadRight(3, '0')));
						times.Add(t);
						line = line.Substring(m.Length).Trim();
					}
					if (times.Count == 0 || line.Length == 0)
						continue;
					if (line.Length > 60)
						line = line.Substring(0, 60) + "…";
					foreach (var t in times)
						lines.Add(new LyricLine(t, line));
				}

				var result = lines.OrderBy(l => l.Time).ToArray();
				LyricCache[songId] = result;
				return result;
			}
			catch (Exception ex)
			{
				Log.Warn("Could not fetch lyrics for {0}: {1}", songId, ex.Message);
				return null;
			}
		}

		/// <summary>Validates a MUSIC_U cookie; returns the account nickname and vip state.</summary>
		public static async Task<(string nickname, bool vip)?> GetAccountProfile(string cookie)
		{
			try
			{
				var json = await GetJson($"{ApiBase}/api/nuser/account/get", cookie).ConfigureAwait(false);
				var profile = json["profile"] as Newtonsoft.Json.Linq.JObject;
				if (profile is null)
					return null;
				var nickname = profile.Value<string>("nickname") ?? "?";
				var account = json["account"] as Newtonsoft.Json.Linq.JObject;
				var vipType = account?.Value<int?>("vipType") ?? 0;
				return (nickname, vipType > 0);
			}
			catch (AudioBotException)
			{
				return null;
			}
		}

		public static async Task<IList<NeteaseSong>> Search(string keyword, int limit, string? cookie)
		{
			var json = await GetJson(
				$"{ApiBase}/api/search/get?s={Uri.EscapeDataString(keyword)}&type=1&limit={limit}&offset=0", cookie)
				.ConfigureAwait(false);
			var songsJson = json["result"]?["songs"] as JArray;
			if (songsJson is null)
				return Array.Empty<NeteaseSong>();

			// search results have a slightly different shape (artist/album instead of ar/al)
			var list = new List<NeteaseSong>();
			foreach (var s in songsJson)
			{
				var artists = string.Join("/", (s["artists"] as JArray)?.Select(a => a.Value<string>("name") ?? "") ?? Array.Empty<string>());
				list.Add(new NeteaseSong
				{
					Id = s.Value<long?>("id") ?? 0,
					Name = s.Value<string>("name") ?? string.Empty,
					Artists = artists,
					Album = s["album"]?.Value<string>("name") ?? string.Empty,
					Duration = TimeSpan.FromMilliseconds(s.Value<double?>("duration") ?? 0),
					Fee = s.Value<int?>("fee") ?? 0,
					CoverUrl = string.Empty,
				});
			}
			return list;
		}

		private static NeteaseSong? ParseSong(JToken s)
		{
			var id = s.Value<long?>("id");
			if (id is null || id == 0)
				return null;
			var artists = string.Join("/", s["ar"]?.Select(a => a.Value<string>("name") ?? "") ?? Array.Empty<string>());
			return new NeteaseSong
			{
				Id = id.Value,
				Name = s.Value<string>("name") ?? string.Empty,
				Artists = artists,
				Album = s["al"]?.Value<string>("name") ?? string.Empty,
				CoverUrl = s["al"]?.Value<string>("picUrl") ?? string.Empty,
				Duration = TimeSpan.FromMilliseconds(s.Value<double?>("dt") ?? 0),
				Fee = s.Value<int?>("fee") ?? 0,
			};
		}

		private static string JsonSerializeIds(IEnumerable<long> ids)
			=> "[" + string.Join(",", ids.Select(i => "{\"id\":" + i + "}")) + "]";

		private static string? NormalizeUrl(string? url)
		{
			if (string.IsNullOrEmpty(url))
				return null;
			// netease serves http which some setups dislike; the CDN also accepts https
			if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
				url = "https://" + url.Substring(7);
			return url;
		}
	}
}
