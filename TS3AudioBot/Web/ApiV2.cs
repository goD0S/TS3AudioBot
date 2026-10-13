// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TS3AudioBot.Audio;
using TS3AudioBot.Config;
using TS3AudioBot.Dependency;
using TS3AudioBot.Helper;
using TS3AudioBot.Playlists;
using TS3AudioBot.ResourceFactories;
using TS3AudioBot.ResourceFactories.Netease;
using TSLib;
using TSLib.Full;
using TSLib.Full.Book;

namespace TS3AudioBot.Web.Api
{
	/// <summary>
	/// Modern JSON API for the web interface. Lives next to the legacy v1 command
	/// bridge under /api/v2 and exposes player controls, queue, playlists,
	/// netease import/migration and the local cache.
	/// </summary>
	public static class ApiV2
	{
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
		private static readonly JsonSerializer Json = new JsonSerializer { NullValueHandling = NullValueHandling.Ignore, ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver() };
		private static readonly JsonSerializerSettings CamelSettings = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore, ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver() };

		private static CoreInjector injector = null!;

		private sealed class Route
		{
			public string Method;
			public string[] Segments;
			public Func<HttpContext, Dictionary<string, string>, Task> Handler;
			public Route(string method, string pattern, Func<HttpContext, Dictionary<string, string>, Task> handler)
			{
				Method = method;
				Segments = pattern.Trim('/').Split('/');
				Handler = handler;
			}
		}

		private static readonly List<Route> routes = new List<Route>
		{
			new Route("POST", "/auth/login", (c, p) => AuthLogin(c)),
			new Route("GET", "/auth/state", (c, _) => AuthState(c)),
			new Route("GET", "/status", (c, _) => Status(c)),
			new Route("GET", "/cache", (c, _) => GetCache(c)),
			new Route("DELETE", "/cache", (c, _) => ClearCache(c)),
			new Route("GET", "/bot/{id}/player", (c, p) => PlayerState(c)),
			new Route("POST", "/bot/{id}/play", (c, p) => Play(c)),
			new Route("POST", "/bot/{id}/playnext", (c, p) => PlayNext(c)),
			new Route("POST", "/bot/{id}/pause", (c, p) => Pause(c)),
			new Route("POST", "/bot/{id}/stop", (c, p) => Stop(c)),
			new Route("POST", "/bot/{id}/next", (c, p) => Next(c)),
			new Route("POST", "/bot/{id}/previous", (c, p) => Previous(c)),
			new Route("POST", "/bot/{id}/volume", (c, p) => Volume(c)),
			new Route("POST", "/bot/{id}/seek", (c, p) => Seek(c)),
			new Route("GET", "/bot/{id}/queue", (c, p) => GetQueue(c)),
			new Route("POST", "/bot/{id}/queue", (c, p) => Enqueue(c)),
			new Route("POST", "/bot/{id}/queue/{index}/play", (c, p) => PlayQueueIndex(c)),
			new Route("DELETE", "/bot/{id}/queue/{index}", (c, p) => RemoveQueueItem(c)),
			new Route("GET", "/bot/{id}/playlists", (c, p) => GetPlaylists(c)),
			new Route("GET", "/bot/{id}/playlists/{listId}", (c, p) => GetPlaylist(c)),
			new Route("POST", "/bot/{id}/playlists/{listId}/play", (c, p) => PlayPlaylist(c)),
			new Route("DELETE", "/bot/{id}/playlists/{listId}", (c, p) => DeletePlaylist(c)),
			new Route("POST", "/bot/{id}/netease/import", (c, p) => NeteaseImport(c)),
			new Route("POST", "/bot/{id}/netease/migrate", (c, p) => NeteaseMigrate(c)),
			new Route("GET", "/bot/{id}/netease/search", (c, p) => NeteaseSearch(c)),
			new Route("GET", "/netease/preview", (c, p) => NeteasePreview(c)),
			new Route("GET", "/netease/config", (c, _) => NeteaseConfigGet(c)),
			new Route("POST", "/netease/cookie/check", (c, _) => NeteaseCookieCheck(c)),
			new Route("GET", "/bots", (c, _) => BotsList(c)),
			new Route("POST", "/bots/create", (c, _) => BotsCreate(c)),
			new Route("POST", "/bot/{id}/connect", (c, _) => BotConnect(c)),
			new Route("POST", "/bot/{id}/disconnect", (c, _) => BotDisconnect(c)),
			new Route("POST", "/bot/{id}/tsname", (c, _) => BotTsName(c)),
			new Route("POST", "/bot/{id}/channel", (c, _) => BotChannel(c)),
			new Route("POST", "/bot/{id}/autorun", (c, _) => BotAutoRun(c)),
			new Route("DELETE", "/bot/{id}", (c, _) => BotDelete(c)),
			new Route("GET", "/events", (c, p) => Events(c)),
		};

		/// <summary>Paths reachable without authentication.</summary>
		public static bool IsPublicPath(string subPath)
		{
			var trimmed = subPath.TrimEnd('/');
			return trimmed.Equals("/auth/login", StringComparison.OrdinalIgnoreCase)
				|| trimmed.Equals("/auth/state", StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>Handles a request whose path starts with /api/v2 (the prefix is already stripped).</summary>
		public static async Task HandleAsync(HttpContext ctx, CoreInjector coreInjector, string subPath)
		{
			injector = coreInjector;
			var path = subPath.TrimEnd('/');
			var parts = path.Trim('/').Split('/');
			foreach (var route in routes)
			{
				if (route.Method != ctx.Request.Method || route.Segments.Length != parts.Length)
					continue;
				var values = new Dictionary<string, string>();
				bool ok = true;
				for (int i = 0; i < parts.Length; i++)
				{
					if (route.Segments[i].StartsWith('{'))
						values[route.Segments[i].Trim('{', '}')] = Uri.UnescapeDataString(parts[i]);
					else if (!string.Equals(route.Segments[i], parts[i], StringComparison.OrdinalIgnoreCase))
					{
						ok = false;
						break;
					}
				}
				if (!ok)
					continue;

				foreach (var kvp in values)
					ctx.Request.RouteValues[kvp.Key] = kvp.Value;
				await route.Handler(ctx, values).ConfigureAwait(false);
				return;
			}

			await WriteError(ctx, "unknown api endpoint", 404).ConfigureAwait(false);
		}

		private static Bot? GetBot(int id) => injector.GetModule<BotManager>()?.GetBotLock(id);

		private static NeteaseResolver? NeteaseResolver() =>
			injector.GetModule<ResourceResolver>()?.NeteaseResolverOrNull();

		private static Bot GetBotOrFirst(int id)
		{
			var manager = injector.GetModuleOrThrow<BotManager>();
			if (id >= 0)
				return manager.GetBotLock(id) ?? throw Error.LocalStr($"Bot {id} 不存在或未运行");
			var first = manager.GetBotInfolist().FirstOrDefault();
			if (first?.Id is null)
				throw Error.LocalStr("当前没有正在运行的 bot");
			return manager.GetBotLock(first.Id.Value) ?? throw Error.LocalStr("当前没有正在运行的 bot");
		}

		private static PlaylistManager PlaylistManagerOf(Bot bot) => bot.Injector.GetModuleOrThrow<PlaylistManager>();
		private static PlayManager PlayManagerOf(Bot bot) => bot.Injector.GetModuleOrThrow<PlayManager>();
		private static Player PlayerOf(Bot bot) => bot.Injector.GetModuleOrThrow<Player>();
		private static ResolveContext ResolverOf(Bot bot) => bot.Injector.GetModuleOrThrow<ResolveContext>();

		private static async Task WriteJson(HttpContext ctx, object data)
		{
			ctx.Response.ContentType = "application/json";
			ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
			await ctx.Response.WriteAsync(JsonConvert.SerializeObject(data, CamelSettings)).ConfigureAwait(false);
		}

		private static Task WriteError(HttpContext ctx, string message, int code = 400)
		{
			ctx.Response.StatusCode = code;
			return WriteJson(ctx, new { error = message });
		}

		private static async Task<T> ReadBody<T>(HttpContext ctx) where T : new()
		{
			try
			{
				using var sr = new System.IO.StreamReader(ctx.Request.Body);
				using var jr = new JsonTextReader(sr);
				return Json.Deserialize<T>(jr) ?? new T();
			}
			catch (Exception)
			{
				return new T();
			}
		}

		// ---------- status / cache ----------

		private static async Task Status(HttpContext ctx)
		{
			var manager = injector.GetModule<BotManager>();
			var bots = new List<object>();
			if (manager != null)
			{
				foreach (var info in manager.GetBotInfolist())
				{
					object state = new { info.Id, info.Name, info.Server, status = info.Status.ToString() };
					if (info.Id is int id)
					{
						var bot = manager.GetBotLock(id);
						if (bot != null)
							state = Merge(state, PlayerStateDto(bot));
					}
					bots.Add(state);
				}
			}

			await WriteJson(ctx, new
			{
				version = Environment.SystemData.AssemblyData.ToString(),
				bots,
			}).ConfigureAwait(false);
		}

		private static object PlayerStateDto(Bot bot)
		{
			var playManager = PlayManagerOf(bot);
			var player = PlayerOf(bot);
			var current = playManager.CurrentPlayData;
			return new
			{
				playing = current != null,
				paused = player.Paused,
				volume = (int)Math.Round(player.Volume),
				position = player.Position?.TotalSeconds,
				length = player.Length?.TotalSeconds,
				current = current == null ? null : new
				{
					title = current.ResourceData.ResourceTitle,
					audioType = current.ResourceData.AudioType,
					artist = current.ResourceData.Get("artist"),
					cover = current.ResourceData.Get("cover"),
					link = current.SourceLink,
				},
			};
		}

		private static object Merge(object a, object b)
		{
			// naive json object merge for the status view
			var ja = JObject.FromObject(a);
			var jb = JObject.FromObject(b);
			ja.Merge(jb, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
			return ja;
		}

		private static async Task GetCache(HttpContext ctx)
		{
			var cache = injector.GetModule<ResourceResolver>()?.NeteaseCacheOrNull();
			if (cache is null)
			{
				await WriteError(ctx, "cache not available", 404).ConfigureAwait(false);
				return;
			}
			var stats = cache.GetStats();
			await WriteJson(ctx, new
			{
				enabled = stats.MaxBytes > 0,
				files = stats.FileCount,
				vipFiles = stats.VipCount,
				usedMb = stats.TotalBytes / 1024 / 1024,
				maxMb = stats.MaxBytes / 1024 / 1024,
				activeDownloads = stats.ActiveDownloads,
				path = stats.Path,
			}).ConfigureAwait(false);
		}

		private static async Task ClearCache(HttpContext ctx)
		{
			var cache = injector.GetModule<ResourceResolver>()?.NeteaseCacheOrNull();
			cache?.Clear();
			await WriteJson(ctx, new { ok = true }).ConfigureAwait(false);
		}

		// ---------- player ----------

		private static async Task PlayerState(HttpContext ctx)
		{
			var bot = GetBotOrFirst(ParseId(ctx));
			await WriteJson(ctx, PlayerStateDto(bot)).ConfigureAwait(false);
		}

		private static async Task Play(HttpContext ctx)
		{
			var bot = GetBotOrFirst(ParseId(ctx));
			var body = await ReadBody<PlayBody>(ctx).ConfigureAwait(false);
			var playManager = PlayManagerOf(bot);

			using var _cookieScope = NeteaseCookieContext.Use(HeaderCookie(ctx));
			try
			{
				if (!string.IsNullOrWhiteSpace(body.Url))
				{
					await playManager.Play(new InvokerData(Uid.Null), body.Url).ConfigureAwait(false);
				}
				else if (!playManager.IsPlaying)
				{
					await playManager.Play(new InvokerData(Uid.Null)).ConfigureAwait(false);
				}
				else
				{
					PlayerOf(bot).Paused = false;
				}
				await WriteJson(ctx, new { ok = true }).ConfigureAwait(false);
			}
			catch (AudioBotException ex)
			{
				await WriteError(ctx, ex.Message).ConfigureAwait(false);
			}
		}

		/// <summary>Inserts the song as the next one up. When nothing is playing it starts playing instead.</summary>
		private static async Task PlayNext(HttpContext ctx)
		{
			var bot = GetBotOrFirst(ParseId(ctx));
			var body = await ReadBody<PlayBody>(ctx).ConfigureAwait(false);
			if (string.IsNullOrWhiteSpace(body.Url))
			{
				await WriteError(ctx, "需要 url").ConfigureAwait(false);
				return;
			}

			var playManager = PlayManagerOf(bot);
			var playlistManager = PlaylistManagerOf(bot);

			using var _cookieScope = NeteaseCookieContext.Use(HeaderCookie(ctx));
			try
			{
				if (!playManager.IsPlaying)
				{
					await playManager.Play(new InvokerData(Uid.Null), body.Url).ConfigureAwait(false);
					await WriteJson(ctx, new { ok = true, mode = "play" }).ConfigureAwait(false);
					return;
				}

				// resolve now so metadata is captured; playback re-resolves the
				// url later (netease signed urls never go stale in the queue)
				var playResource = await ResolverOf(bot).Load(body.Url).ConfigureAwait(false);
				var item = PlaylistItem.From(playResource);

				int insertAt = Math.Min(playlistManager.Index + 1, playlistManager.CurrentList.Items.Count);
				playlistManager.ModifyPlaylist(".mix", list =>
				{
					if (insertAt > list.Items.Count)
						insertAt = list.Items.Count;
					list.Insert(insertAt, item).UnwrapThrow();
				});
				await WriteJson(ctx, new { ok = true, mode = "next", position = insertAt + 1 }).ConfigureAwait(false);
			}
			catch (AudioBotException ex)
			{
				await WriteError(ctx, ex.Message).ConfigureAwait(false);
			}
		}

		private static async Task Pause(HttpContext ctx)
		{
			var bot = GetBotOrFirst(ParseId(ctx));
			var body = await ReadBody<ValueBody>(ctx).ConfigureAwait(false);
			var player = PlayerOf(bot);
			player.Paused = body.Value is int v ? v != 0 : !player.Paused;
			await WriteJson(ctx, new { ok = true, paused = player.Paused }).ConfigureAwait(false);
		}

		private static async Task Stop(HttpContext ctx)
		{
			var bot = GetBotOrFirst(ParseId(ctx));
			await PlayManagerOf(bot).Stop().ConfigureAwait(false);
			await WriteJson(ctx, new { ok = true }).ConfigureAwait(false);
		}

		private static async Task Next(HttpContext ctx)
		{
			var bot = GetBotOrFirst(ParseId(ctx));
			await PlayManagerOf(bot).Next(new InvokerData(Uid.Null)).ConfigureAwait(false);
			await WriteJson(ctx, new { ok = true }).ConfigureAwait(false);
		}

		private static async Task Previous(HttpContext ctx)
		{
			var bot = GetBotOrFirst(ParseId(ctx));
			await PlayManagerOf(bot).Previous(new InvokerData(Uid.Null)).ConfigureAwait(false);
			await WriteJson(ctx, new { ok = true }).ConfigureAwait(false);
		}

		private static async Task Volume(HttpContext ctx)
		{
			var bot = GetBotOrFirst(ParseId(ctx));
			var body = await ReadBody<ValueBody>(ctx).ConfigureAwait(false);
			if (body.Value is int value)
				PlayerOf(bot).Volume = Math.Clamp(value, 0, 100);
			await WriteJson(ctx, new { ok = true, volume = (int)Math.Round(PlayerOf(bot).Volume) }).ConfigureAwait(false);
		}

		private static async Task Seek(HttpContext ctx)
		{
			var bot = GetBotOrFirst(ParseId(ctx));
			var body = await ReadBody<ValueBody>(ctx).ConfigureAwait(false);
			if (body.Value is int seconds)
				PlayerOf(bot).Seek(TimeSpan.FromSeconds(seconds));
			await WriteJson(ctx, new { ok = true }).ConfigureAwait(false);
		}

		// ---------- queue ----------

		private static async Task GetQueue(HttpContext ctx)
		{
			var bot = GetBotOrFirst(ParseId(ctx));
			var list = PlaylistManagerOf(bot).CurrentList;
			await WriteJson(ctx, new
			{
				title = list.Title,
				items = list.Items.Select((item, i) => ItemDto(item, i)),
			}).ConfigureAwait(false);
		}

		private static async Task Enqueue(HttpContext ctx)
		{
			var bot = GetBotOrFirst(ParseId(ctx));
			var body = await ReadBody<EnqueueBody>(ctx).ConfigureAwait(false);
			var playManager = PlayManagerOf(bot);
			var playlistManager = PlaylistManagerOf(bot);

			using var _cookieScope = NeteaseCookieContext.Use(HeaderCookie(ctx));
			try
			{
				if (!string.IsNullOrWhiteSpace(body.Url))
				{
					await playManager.Enqueue(new InvokerData(Uid.Null), body.Url).ConfigureAwait(false);
				}
				else if (!string.IsNullOrWhiteSpace(body.ListId))
				{
					var res = playlistManager.LoadPlaylist(body.ListId);
					if (!res.Ok)
					{
						await WriteError(ctx, res.Error.Str).ConfigureAwait(false);
						return;
					}
					await playManager.Enqueue(new InvokerData(Uid.Null), res.Value.Items).ConfigureAwait(false);
				}
				else
				{
					await WriteError(ctx, "需要 url 或 listId").ConfigureAwait(false);
					return;
				}
				await WriteJson(ctx, new { ok = true }).ConfigureAwait(false);
			}
			catch (AudioBotException ex)
			{
				await WriteError(ctx, ex.Message).ConfigureAwait(false);
			}
		}

		/// <summary>Keeps the current queue and jumps to the given index.</summary>
		private static async Task PlayQueueIndex(HttpContext ctx)
		{
			var bot = GetBotOrFirst(ParseId(ctx));
			var playlistManager = PlaylistManagerOf(bot);
			int index = ParseId(ctx, "index");
			var list = playlistManager.CurrentList;
			if (index < 0 || index >= list.Items.Count)
			{
				await WriteError(ctx, "index out of range").ConfigureAwait(false);
				return;
			}
			playlistManager.Index = index;
			await PlayManagerOf(bot).Play(new InvokerData(Uid.Null)).ConfigureAwait(false);
			await WriteJson(ctx, new { ok = true, index }).ConfigureAwait(false);
		}

		private static async Task RemoveQueueItem(HttpContext ctx)
		{
			var bot = GetBotOrFirst(ParseId(ctx));
			var playlistManager = PlaylistManagerOf(bot);
			int index = ParseId(ctx, "index");
			E<Localization.LocalStr>? err = null;
			playlistManager.ModifyPlaylist(".mix", list =>
			{
				if (index < 0 || index >= list.Items.Count)
					err = new Localization.LocalStr("index out of range");
				else
					list.RemoveAt(index);
			});
			if (err != null)
			{
				await WriteError(ctx, err.Value.Error.Str).ConfigureAwait(false);
				return;
			}
			await WriteJson(ctx, new { ok = true }).ConfigureAwait(false);
		}

		// ---------- playlists ----------

		private static object PlaylistDto(string id, PlaylistMeta meta) => new
		{
			id,
			title = meta.Title,
			count = meta.Count,
			cover = meta.CoverUrl,
			source = meta.Source,
			creator = meta.Creator,
			updated = meta.UpdatedAt,
		};

		private static async Task GetPlaylists(HttpContext ctx)
		{
			var bot = GetBotOrFirst(ParseId(ctx));
			var metas = PlaylistManagerOf(bot).GetAllPlaylistMetas();
			await WriteJson(ctx, new { playlists = metas.Select(m => PlaylistDto(m.Id, m.Meta)) }).ConfigureAwait(false);
		}

		private static async Task GetPlaylist(HttpContext ctx)
		{
			var bot = GetBotOrFirst(ParseId(ctx));
			var listId = (string?)ctx.Request.RouteValues["listId"] ?? "";
			var res = PlaylistManagerOf(bot).LoadPlaylist(listId);
			if (!res.Ok)
			{
				await WriteError(ctx, res.Error.Str, 404).ConfigureAwait(false);
				return;
			}
			var pl = res.Value;
			var meta = PlaylistManagerOf(bot).GetPlaylistMeta(listId);
			await WriteJson(ctx, new
			{
				id = listId,
				title = pl.Title,
				cover = meta?.CoverUrl,
				source = meta?.Source,
				creator = meta?.Creator,
				updated = meta?.UpdatedAt,
				count = pl.Items.Count,
				items = pl.Items.Select((item, i) => ItemDto(item, i)),
			}).ConfigureAwait(false);
		}

		private static async Task PlayPlaylist(HttpContext ctx)
		{
			var bot = GetBotOrFirst(ParseId(ctx));
			var listId = (string?)ctx.Request.RouteValues["listId"] ?? "";
			var body = await ReadBody<PlayPlaylistBody>(ctx).ConfigureAwait(false);
			var playManager = PlayManagerOf(bot);
			var playlistManager = PlaylistManagerOf(bot);

			var res = playlistManager.LoadPlaylist(listId);
			if (!res.Ok)
			{
				await WriteError(ctx, res.Error.Str, 404).ConfigureAwait(false);
				return;
			}
			var items = res.Value.Items;
			int startIndex = 0;
			if (body.Index is int idx && idx > 0 && idx < items.Count)
				startIndex = idx;

			PlaylistManagerOf(bot).Random = body.Shuffle ?? false;
			await playManager.Play(new InvokerData(Uid.Null), items, startIndex).ConfigureAwait(false);
			await WriteJson(ctx, new { ok = true, count = items.Count }).ConfigureAwait(false);
		}

		private static async Task DeletePlaylist(HttpContext ctx)
		{
			var bot = GetBotOrFirst(ParseId(ctx));
			var listId = (string?)ctx.Request.RouteValues["listId"] ?? "";
			var result = PlaylistManagerOf(bot).DeletePlaylist(listId);
			if (!result.Ok)
			{
				await WriteError(ctx, result.Error.Str).ConfigureAwait(false);
				return;
			}
			await WriteJson(ctx, new { ok = true }).ConfigureAwait(false);
		}

		// ---------- netease ----------

		private static async Task NeteaseImport(HttpContext ctx)
		{
			var bot = GetBotOrFirst(ParseId(ctx));
			var body = await ReadBody<ImportBody>(ctx).ConfigureAwait(false);
			if (string.IsNullOrWhiteSpace(body.Url))
			{
				await WriteError(ctx, "需要网易云歌单链接或 id").ConfigureAwait(false);
				return;
			}
			var url = NormalizeNeteaseUrl(body.Url);
			var listId = SanitizeListId(body.ListId);
			var netease = NeteaseResolver();
			if (netease is null)
			{
				await WriteError(ctx, "netease resolver 不可用", 500).ConfigureAwait(false);
				return;
			}
			try
			{
				// the user's cookie lives only for this import call
				using var _cookieScope = NeteaseCookieContext.Use(body.Cookie);
				var result = await NeteaseService.ImportPlaylist(bot, netease, listId, url, body.Replace ?? true).ConfigureAwait(false);
				await WriteJson(ctx, result).ConfigureAwait(false);
			}
			catch (AudioBotException ex)
			{
				await WriteError(ctx, ex.Message).ConfigureAwait(false);
			}
		}

		private static async Task NeteaseMigrate(HttpContext ctx)
		{
			var bot = GetBotOrFirst(ParseId(ctx));
			var netease = NeteaseResolver();
			if (netease is null)
			{
				await WriteError(ctx, "netease resolver 不可用", 500).ConfigureAwait(false);
				return;
			}
			try
			{
				using var _cookieScope = NeteaseCookieContext.Use(HeaderCookie(ctx));
				var result = await NeteaseService.MigrateLegacyUrls(bot, netease).ConfigureAwait(false);
				await WriteJson(ctx, result).ConfigureAwait(false);
			}
			catch (AudioBotException ex)
			{
				await WriteError(ctx, ex.Message).ConfigureAwait(false);
			}
		}

		private static async Task NeteaseSearch(HttpContext ctx)
		{
			var query = ctx.Request.Query["q"].ToString();
			if (string.IsNullOrWhiteSpace(query))
			{
				await WriteError(ctx, "缺少搜索词").ConfigureAwait(false);
				return;
			}
			try
			{
				var cookie = HeaderCookie(ctx);
				var songs = await NeteaseApi.Search(query, 20, cookie).ConfigureAwait(false);
				await WriteJson(ctx, new
				{
					songs = songs.Select(s => new
					{
						id = s.Id,
						name = s.Name,
						artists = s.Artists,
						album = s.Album,
						cover = s.CoverUrl,
						vip = s.IsVip,
						duration = (int)s.Duration.TotalSeconds,
						link = $"https://music.163.com/song?id={s.Id}",
					}),
				}).ConfigureAwait(false);
			}
			catch (AudioBotException ex)
			{
				await WriteError(ctx, ex.Message).ConfigureAwait(false);
			}
		}

		private static async Task NeteasePreview(HttpContext ctx)
		{
			var url = ctx.Request.Query["url"].ToString();
			if (string.IsNullOrWhiteSpace(url))
			{
				await WriteError(ctx, "缺少 url 参数").ConfigureAwait(false);
				return;
			}
			var netease = NeteaseResolver();
			if (netease is null)
			{
				await WriteError(ctx, "netease resolver 不可用", 500).ConfigureAwait(false);
				return;
			}
			try
			{
				using var _cookieScope = NeteaseCookieContext.Use(HeaderCookie(ctx));
				var result = await NeteaseService.PreviewPlaylist(netease, NormalizeNeteaseUrl(url)).ConfigureAwait(false);
				await WriteJson(ctx, result).ConfigureAwait(false);
			}
			catch (AudioBotException ex)
			{
				await WriteError(ctx, ex.Message).ConfigureAwait(false);
			}
		}

		// ---------- auth ----------

		public static Web.AuthService? Auth { get; set; }

		private static async Task AuthLogin(HttpContext ctx)
		{
			if (Auth is null || !Auth.AuthEnabled)
			{
				await WriteJson(ctx, new { ok = true, token = "" }).ConfigureAwait(false);
				return;
			}
			var body = await ReadBody<KeyBody>(ctx).ConfigureAwait(false);
			var ip = ctx.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
			var token = Auth.Login(body.Key, ip);
			if (token is null)
			{
				ctx.Response.StatusCode = 401;
				await WriteJson(ctx, new { error = "密钥错误或尝试过于频繁，请稍后再试" }).ConfigureAwait(false);
				return;
			}
			await WriteJson(ctx, new { ok = true, token }).ConfigureAwait(false);
		}

		private static async Task AuthState(HttpContext ctx)
		{
			var authEnabled = Auth?.AuthEnabled ?? false;
			await WriteJson(ctx, new { authRequired = authEnabled }).ConfigureAwait(false);
		}

		// ---------- netease runtime info ----------

		private static async Task NeteaseConfigGet(HttpContext ctx)
		{
			var netease = NeteaseResolver();
			if (netease is null)
			{
				await WriteError(ctx, "netease resolver 不可用", 500).ConfigureAwait(false);
				return;
			}
			// cookies are per-user and never stored server side; nothing to show here
			await WriteJson(ctx, new
			{
				cookiePolicy = "ephemeral",
				bitrate = netease.Bitrate,
			}).ConfigureAwait(false);
		}

		private static async Task NeteaseCookieCheck(HttpContext ctx)
		{
			var body = await ReadBody<CookieBody>(ctx).ConfigureAwait(false);
			var cookie = (body.Cookie ?? string.Empty).Trim();
			if (cookie.Length == 0)
			{
				await WriteJson(ctx, new { ok = false, message = "请先粘贴 MUSIC_U cookie" }).ConfigureAwait(false);
				return;
			}
			var profile = await NeteaseApi.GetAccountProfile(cookie).ConfigureAwait(false);
			if (profile is null)
				await WriteJson(ctx, new { ok = false, message = "cookie 无效或已过期" }).ConfigureAwait(false);
			else
				await WriteJson(ctx, new { ok = true, nickname = profile.Value.nickname, vip = profile.Value.vip }).ConfigureAwait(false);
		}

		// ---------- bot management ----------

		private static BotManager BotManager() => injector.GetModuleOrThrow<BotManager>();
		private static ConfRoot ConfRoot() => injector.GetModuleOrThrow<ConfRoot>();

		private static async Task BotsList(HttpContext ctx)
		{
			var manager = BotManager();
			var root = ConfRoot();
			var result = new List<object>();

			// templates on disk
			var templates = root.GetAllBots() ?? Array.Empty<ConfBot>();
			foreach (var conf in templates)
			{
				var tplName = conf.Name ?? "";
				Bot? running = null;
				foreach (var info in manager.GetBotInfolist())
				{
					if (info.Id is int id && manager.GetBotLock(id) is Bot b && string.Equals(b.Name, tplName, StringComparison.OrdinalIgnoreCase))
					{
						running = b;
						break;
					}
				}
				result.Add(BotDto(tplName, conf, running));
			}
			await WriteJson(ctx, new { bots = result }).ConfigureAwait(false);
		}

		private static object BotDto(string templateName, ConfBot conf, Bot? running)
		{
			ulong? channelId = null;
			string? channelName = null;
			if (running != null)
			{
				var book = running.Injector.GetModule<Connection>();
				var cur = book?.CurrentChannel();
				if (cur is not null)
				{
					channelId = cur.Id.Value;
					channelName = cur.Name;
				}
			}
			return new
			{
				name = templateName,
				tsName = conf.Connect.Name.Value,
				server = conf.Connect.Address.Value,
				defaultChannel = conf.Connect.Channel.Value,
				autoRun = conf.Run.Value,
				running = running != null,
				channelId,
				channelName,
			};
		}

		private static async Task BotsCreate(HttpContext ctx)
		{
			var manager = BotManager();
			var root = ConfRoot();
			var body = await ReadBody<CreateBotBody>(ctx).ConfigureAwait(false);

			var name = (body.Name ?? string.Empty).Trim();
			if (name.Length == 0)
			{
				await WriteError(ctx, "需要实例名 name").ConfigureAwait(false);
				return;
			}
			if (!Helper.Util.IsSafeFileName(name).Ok)
			{
				await WriteError(ctx, "实例名只能包含字母数字下划线连字符").ConfigureAwait(false);
				return;
			}

			ConfBot conf;
			bool existed = root.GetAllBots()?.Any(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)) ?? false;
			if (existed)
			{
				var existing = root.GetBotConfig(name);
				if (!existing.Ok)
				{
					await WriteError(ctx, existing.Error.Message).ConfigureAwait(false);
					return;
				}
				conf = existing.Value;
			}
			else
			{
				var created = root.CreateBotConfig(name);
				if (!created.Ok)
				{
					await WriteError(ctx, created.Error.Str).ConfigureAwait(false);
					return;
				}
				var loaded = root.GetBotConfig(name);
				if (!loaded.Ok)
				{
					await WriteError(ctx, loaded.Error.Message).ConfigureAwait(false);
					return;
				}
				conf = loaded.Value;
			}

			if (!string.IsNullOrWhiteSpace(body.Server))
				conf.Connect.Address.Value = body.Server!.Trim();
			if (!string.IsNullOrWhiteSpace(body.Channel))
				conf.Connect.Channel.Value = body.Channel!.Trim();
			if (!string.IsNullOrWhiteSpace(body.TsName))
				conf.Connect.Name.Value = body.TsName!.Trim();
			if (body.AutoRun is bool ar)
				conf.Run.Value = ar;
			conf.SaveWhenExists().UnwrapThrow();

			bool started = false;
			string? startError = null;
			if (conf.Run.Value || body.StartNow == true)
			{
				var already = manager.GetBotLock(name);
				if (already is null)
				{
					var res = await manager.RunBotTemplate(name).ConfigureAwait(false);
					started = res.Ok;
					if (!res.Ok) startError = res.Error;
				}
				else started = true;
			}
			await WriteJson(ctx, new { ok = true, name, created = !existed, started, error = startError }).ConfigureAwait(false);
		}

		private static async Task BotConnect(HttpContext ctx)
		{
			var manager = BotManager();
			var root = ConfRoot();
			var body = await ReadBody<ConnectBotBody>(ctx).ConfigureAwait(false);
			var name = ResolveTemplateName(manager, root, ParseId(ctx));
			if (name is null)
			{
				await WriteError(ctx, "找不到该 bot 实例").ConfigureAwait(false);
				return;
			}

			var confRes = root.GetBotConfig(name);
			if (!confRes.Ok)
			{
				await WriteError(ctx, confRes.Error.Message).ConfigureAwait(false);
				return;
			}
			var conf = confRes.Value;
			if (!string.IsNullOrWhiteSpace(body.Server))
				conf.Connect.Address.Value = body.Server!.Trim();
			if (!string.IsNullOrWhiteSpace(body.Channel))
				conf.Connect.Channel.Value = body.Channel!.Trim();
			conf.SaveWhenExists().UnwrapThrow();

			var existing = manager.GetBotLock(name);
			if (existing != null)
			{
				await manager.StopBot(existing).ConfigureAwait(false);
			}
			var res = await manager.RunBotTemplate(name).ConfigureAwait(false);
			if (!res.Ok)
			{
				await WriteError(ctx, res.Error).ConfigureAwait(false);
				return;
			}
			await WriteJson(ctx, new { ok = true }).ConfigureAwait(false);
		}

		private static async Task BotDisconnect(HttpContext ctx)
		{
			var manager = BotManager();
			var bot = manager.GetBotLock(ParseId(ctx));
			if (bot is null)
			{
				await WriteError(ctx, "该实例未在运行").ConfigureAwait(false);
				return;
			}
			await manager.StopBot(bot).ConfigureAwait(false);
			await WriteJson(ctx, new { ok = true }).ConfigureAwait(false);
		}

		private static async Task BotTsName(HttpContext ctx)
		{
			var manager = BotManager();
			var root = ConfRoot();
			var body = await ReadBody<NameBody>(ctx).ConfigureAwait(false);
			var name = (body.Name ?? string.Empty).Trim();
			if (name.Length == 0)
			{
				await WriteError(ctx, "name 不能为空").ConfigureAwait(false);
				return;
			}
			var template = ResolveTemplateName(manager, root, ParseId(ctx));
			if (template is null)
			{
				await WriteError(ctx, "找不到该 bot 实例").ConfigureAwait(false);
				return;
			}
			var confRes = root.GetBotConfig(template);
			if (!confRes.Ok)
			{
				await WriteError(ctx, confRes.Error.Message).ConfigureAwait(false);
				return;
			}
			confRes.Value.Connect.Name.Value = name;
			confRes.Value.SaveWhenExists().UnwrapThrow();

			var running = manager.GetBotLock(ParseId(ctx));
			if (running != null)
				await running.Injector.GetModuleOrThrow<Ts3Client>().ChangeName(name).ConfigureAwait(false);
			await WriteJson(ctx, new { ok = true }).ConfigureAwait(false);
		}

		private static async Task BotChannel(HttpContext ctx)
		{
			var manager = BotManager();
			var root = ConfRoot();
			var body = await ReadBody<ChannelBody>(ctx).ConfigureAwait(false);
			var template = ResolveTemplateName(manager, root, ParseId(ctx));
			if (template is null)
			{
				await WriteError(ctx, "找不到该 bot 实例").ConfigureAwait(false);
				return;
			}
			var confRes = root.GetBotConfig(template);
			if (!confRes.Ok)
			{
				await WriteError(ctx, confRes.Error.Message).ConfigureAwait(false);
				return;
			}
			if (!string.IsNullOrWhiteSpace(body.Channel))
			{
				confRes.Value.Connect.Channel.Value = body.Channel!.Trim();
				confRes.Value.SaveWhenExists().UnwrapThrow();
			}
			var running = manager.GetBotLock(ParseId(ctx));
			if (running != null && body.ChannelId is ulong cid)
				await running.Injector.GetModuleOrThrow<Ts3Client>().MoveTo((TSLib.ChannelId)cid).ConfigureAwait(false);
			await WriteJson(ctx, new { ok = true }).ConfigureAwait(false);
		}

		private static async Task BotAutoRun(HttpContext ctx)
		{
			var manager = BotManager();
			var root = ConfRoot();
			var body = await ReadBody<AutoRunBody>(ctx).ConfigureAwait(false);
			if (body.AutoRun is null)
			{
				await WriteError(ctx, "需要 autoRun 布尔值").ConfigureAwait(false);
				return;
			}
			var template = ResolveTemplateName(manager, root, ParseId(ctx));
			if (template is null)
			{
				await WriteError(ctx, "找不到该 bot 实例").ConfigureAwait(false);
				return;
			}
			var confRes = root.GetBotConfig(template);
			if (!confRes.Ok)
			{
				await WriteError(ctx, confRes.Error.Message).ConfigureAwait(false);
				return;
			}
			confRes.Value.Run.Value = body.AutoRun.Value;
			confRes.Value.SaveWhenExists().UnwrapThrow();
			await WriteJson(ctx, new { ok = true, autoRun = body.AutoRun.Value }).ConfigureAwait(false);
		}

		private static async Task BotDelete(HttpContext ctx)
		{
			var manager = BotManager();
			var root = ConfRoot();
			var template = ResolveTemplateName(manager, root, ParseId(ctx));
			if (template is null)
			{
				await WriteError(ctx, "找不到该 bot 实例").ConfigureAwait(false);
				return;
			}
			var running = manager.GetBotLock(template);
			if (running != null)
				await manager.StopBot(running).ConfigureAwait(false);
			var del = root.DeleteBotConfig(template);
			if (!del.Ok)
			{
				await WriteError(ctx, del.Error.Str).ConfigureAwait(false);
				return;
			}
			await WriteJson(ctx, new { ok = true }).ConfigureAwait(false);
		}

		/// <summary>{id} means the running bot id, or the index into the template list for stopped bots.</summary>
		private static string? ResolveTemplateName(BotManager manager, ConfRoot root, int id)
		{
			var running = manager.GetBotLock(id);
			if (running?.Name != null)
				return running.Name;
			var all = root.GetAllBots();
			if (all != null && id >= 0 && id < all.Length)
				return all[id].Name; // stable ordering, same as the list endpoint
			return null;
		}

		// ---------- SSE ----------

		private static async Task Events(HttpContext ctx)
		{
			ctx.Response.ContentType = "text/event-stream";
			ctx.Response.Headers["Cache-Control"] = "no-cache";
			ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";

			var token = ctx.RequestAborted;
			try
			{
				while (!token.IsCancellationRequested)
				{
					var manager = injector.GetModule<BotManager>();
					var bots = new List<object>();
					if (manager != null)
					{
						foreach (var info in manager.GetBotInfolist())
						{
							if (info.Id is int id && manager.GetBotLock(id) is Bot bot)
								bots.Add(new { id, name = info.Name, state = PlayerStateDto(bot) });
						}
					}
					var payload = JsonConvert.SerializeObject(new { bots, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
					await ctx.Response.WriteAsync($"data: {payload}\n\n", token).ConfigureAwait(false);
					await ctx.Response.Body.FlushAsync(token).ConfigureAwait(false);
					await Task.Delay(2000, token).ConfigureAwait(false);
				}
			}
			catch (OperationCanceledException) { }
		}

		// ---------- helpers ----------

		private static object ItemDto(PlaylistItem item, int index) => new
		{
			index,
			title = item.AudioResource.ResourceTitle,
			audioType = item.AudioResource.AudioType,
			resId = item.AudioResource.ResourceId,
			artist = item.AudioResource.Get("artist"),
			album = item.AudioResource.Get("album"),
			cover = item.AudioResource.Get("cover"),
			vip = item.AudioResource.Get("fee") == "1",
			duration = item.AudioResource.Get("duration"),
			link = item.AudioResource.AudioType == "netease"
				? $"https://music.163.com/song?id={item.AudioResource.ResourceId}"
				: null,
		};

		/// <summary>Per-request netease cookie (header); never stored.</summary>
		private static string? HeaderCookie(HttpContext ctx)
			=> ctx.Request.Headers.TryGetValue("X-Netease-Cookie", out var v) && !string.IsNullOrWhiteSpace(v)
				? v.ToString().Trim()
				: null;

		private static int ParseId(HttpContext ctx, string name = "id")
		{
			var raw = ctx.Request.RouteValues[name]?.ToString();
			return int.TryParse(raw, out var id) ? id : -1;
		}

		private static string NormalizeNeteaseUrl(string input)
		{
			input = input.Trim();
			var idMatch = new System.Text.RegularExpressions.Regex(@"(\d{5,15})").Match(input);
			if (idMatch.Success && !input.Contains("song?"))
				return $"https://music.163.com/playlist?id={idMatch.Groups[1].Value}";
			return input;
		}

		private static string SanitizeListId(string? input)
		{
			if (string.IsNullOrWhiteSpace(input))
				input = "netease_" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
			var sb = new System.Text.StringBuilder();
			foreach (var c in input.Trim())
			{
				sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
			}
			return sb.ToString();
		}

		// ---------- body dtos ----------

		private sealed class PlayBody { public string? Url { get; set; } }
		private sealed class KeyBody { public string? Key { get; set; } }
		private sealed class CookieBody { public string? Cookie { get; set; } }
		private sealed class CreateBotBody { public string? Name { get; set; } public string? Server { get; set; } public string? Channel { get; set; } public string? TsName { get; set; } public bool? AutoRun { get; set; } public bool? StartNow { get; set; } }
		private sealed class ConnectBotBody { public string? Server { get; set; } public string? Channel { get; set; } }
		private sealed class NameBody { public string? Name { get; set; } }
		private sealed class ChannelBody { public string? Channel { get; set; } public ulong? ChannelId { get; set; } }
		private sealed class AutoRunBody { public bool? AutoRun { get; set; } }
		private sealed class ValueBody { public int? Value { get; set; } }
		private sealed class EnqueueBody { public string? Url { get; set; } public string? ListId { get; set; } }
		private sealed class PlayPlaylistBody { public int? Index { get; set; } public bool? Shuffle { get; set; } }
		private sealed class ImportBody { public string? Url { get; set; } public string? ListId { get; set; } public bool? Replace { get; set; } public string? Cookie { get; set; } }
	}
}
