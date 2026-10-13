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
using System.Threading.Tasks;
using TS3AudioBot.Audio;
using TS3AudioBot.CommandSystem;
using TS3AudioBot.Config;
using TS3AudioBot.Localization;
using TS3AudioBot.Playlists;
using TS3AudioBot.ResourceFactories;
using TS3AudioBot.ResourceFactories.Netease;
using TS3AudioBot.Web.Api;
using TSLib;

namespace TS3AudioBot
{
	/// <summary>
	/// Modernized chat commands: queue management, local cache and
	/// netease shortcuts. Complements the classic MainCommands.
	/// </summary>
	public static class NeteaseChatCommands
	{
		internal static ICommandBag Bag { get; } = new NeteaseChatCommandsBag();

		private class NeteaseChatCommandsBag : ICommandBag
		{
			public IReadOnlyCollection<BotCommand> BagCommands { get; } =
				CommandManager.GetBotCommands(null, typeof(NeteaseChatCommands)).ToArray();

			// rights also declared in DefaultRights.toml; declaring them here
			// makes the rights system aware of them even with older rights files
			public IReadOnlyCollection<string> AdditionalRights { get; } = new[]
			{
				"cmd.queue",
				"cmd.queue.jump",
				"cmd.queue.clear",
				"cmd.cache",
				"cmd.cache.clear",
				"cmd.wy",
				"cmd.wy.import",
				"cmd.wy.search",
				"cmd.wy.cookiehelp",
			};
		}

		private const int QueuePageSize = 10;

		// ---------- queue ----------

		[Command("queue", "查看当前队列（!queue 页码，每页 10 首）")]
		public static string CommandQueue(PlaylistManager playlistManager, PlayManager playManager, int page = 0)
		{
			var list = playlistManager.CurrentList;
			if (list.Items.Count == 0)
				return "队列为空，用 !play <链接> 或 !wy import <歌单> 添加歌曲";

			var current = playManager.CurrentPlayData;
			var currentIndex = playlistManager.Index;
			int pages = (list.Items.Count + QueuePageSize - 1) / QueuePageSize;
			if (page < 0) page = 0;
			if (page >= pages) page = pages - 1;

			var sb = new System.Text.StringBuilder();
			sb.AppendLine($"[队列 {list.Items.Count} 首 · 第 {page + 1}/{pages} 页 · 随机:{(playlistManager.Random ? "开" : "关")} 循环:{(playlistManager.Loop != Playlists.LoopMode.Off ? "开" : "关")}]");
			var start = page * QueuePageSize;
			for (int i = start; i < Math.Min(start + QueuePageSize, list.Items.Count); i++)
			{
				var marker = i == currentIndex ? "▶" : (i < currentIndex ? " " : " ");
				sb.AppendLine($"{marker} {i + 1}. {list.Items[i].AudioResource.ResourceTitle}");
			}
			sb.Append($"提示: !queue jump <序号> 跳播 · !queue nextpage 下一页");
			return sb.ToString();
		}

		[Command("queue nextpage", "队列下一页")]
		public static string CommandQueueNextPage(PlaylistManager playlistManager, PlayManager playManager, int currentPage = -1)
		{
			var pages = (playlistManager.CurrentList.Items.Count + QueuePageSize - 1) / QueuePageSize;
			var around = playlistManager.Index / QueuePageSize;
			var page = currentPage >= 0 ? currentPage + 1 : around + 1;
			return CommandQueue(playlistManager, playManager, page >= pages ? 0 : page);
		}

		[Command("queue jump", "保留队列，从第 N 首开始播放")]
		public static async Task<string> CommandQueueJump(PlaylistManager playlistManager, PlayManager playManager, InvokerData invoker, int index)
		{
			var list = playlistManager.CurrentList;
			if (index < 1 || index > list.Items.Count)
				throw new CommandException($"序号超出范围（1-{list.Items.Count}）", CommandExceptionReason.CommandError);
			playlistManager.Index = index - 1;
			await playManager.Play(invoker).ConfigureAwait(false);
			var item = playlistManager.Current;
			return $"▶ 从第 {index} 首开始: {item?.AudioResource.ResourceTitle}（队列保持 {list.Items.Count} 首）";
		}

		[Command("queue clear", "清空队列（不停止当前播放）")]
		public static string CommandQueueClear(PlaylistManager playlistManager)
		{
			int n = playlistManager.CurrentList.Items.Count;
			playlistManager.Clear();
			return $"已清空队列（{n} 首）";
		}

		// ---------- cache ----------

		[Command("cache", "查看本地缓存状态")]
		public static string CommandCache(ResourceResolver resolver)
		{
			var cache = resolver.NeteaseCacheOrNull();
			if (cache is null || !cache.Enabled)
				return "缓存未启用";
			var s = cache.GetStats();
			return $"本地缓存: {s.FileCount} 首（VIP {s.VipCount}）· {s.TotalBytes / 1024 / 1024} MB / {s.MaxBytes / 1024 / 1024} MB" +
				(s.ActiveDownloads > 0 ? $" · 正在下载 {s.ActiveDownloads} 首" : "") +
				"\nVIP 完整版最晚清理；用 !cache clear 可清空";
		}

		[Command("cache clear", "清空本地歌曲缓存")]
		public static string CommandCacheClear(ResourceResolver resolver)
		{
			var cache = resolver.NeteaseCacheOrNull();
			if (cache is null)
				return "缓存未启用";
			var stats = cache.GetStats();
			cache.Clear();
			return $"已清空 {stats.FileCount} 首缓存（{stats.TotalBytes / 1024 / 1024} MB）";
		}

		// ---------- netease ----------

		[Command("wy import", "导入网易云歌单: !wy import <歌单链接或ID> [保存ID]")]
		public static async Task<string> CommandWyImport(Bot bot, PlaylistManager playlistManager, ResolveContext resolver, ResourceResolver resourceResolver, string url, string? listId = null)
		{
			var netease = resourceResolver.NeteaseResolverOrNull();
			if (netease is null)
				throw new CommandException("netease 模块不可用", CommandExceptionReason.CommandError);

			if (string.IsNullOrWhiteSpace(listId))
				listId = "wy_" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 100000;
			var result = await NeteaseService.ImportPlaylist(bot, netease, listId, NormalizeNetease(url), replace: true).ConfigureAwait(false);
			return $"已导入「{result.Title}」: {result.TrackCount} 首（VIP {result.VipCount} 首）\n" +
				$"播放: !list play {result.ListId} · 随机: !list play {result.ListId} random";
		}

		[Command("wy search", "搜索网易云歌曲: !wy search <关键词>，然后用 !play <编号id> 播放")]
		public static async Task<string> CommandWySearch(ResourceResolver resourceResolver, string keyword)
		{
			var netease = resourceResolver.NeteaseResolverOrNull();
			var cookie = NeteaseCookieContext.Current;
			var songs = await NeteaseApi.Search(keyword, 10, cookie).ConfigureAwait(false);
			if (songs.Count == 0)
				return "没有找到结果";
			var sb = new System.Text.StringBuilder();
			sb.AppendLine($"[网易云搜索: {keyword}]");
			for (int i = 0; i < songs.Count; i++)
			{
				var s = songs[i];
				sb.AppendLine($"{i + 1}. {(s.IsVip ? "[VIP] " : "")}{s.Artists} - {s.Name}  (id: {s.Id})");
			}
			sb.Append("播放: !play <id>（VIP 歌需已缓存或网页端带 cookie 导入过）");
			return sb.ToString();
		}

		[Command("wy cookiehelp", "如何获取 MUSIC_U cookie（网页端使用）")]
		public static string CommandWyCookieHelp()
			=> "VIP 歌曲需要 MUSIC_U cookie（等同登录凭证，请勿外发）:\n" +
			   "1. 电脑浏览器登录 music.163.com\n" +
			   "2. F12 → Application → Cookies → music.163.com\n" +
			   "3. 复制 MUSIC_U 的值\n" +
			   "4. 粘贴到网页控制台的「网易云」页（用完即焚，不保存在服务器）\n" +
			   "注意: 群聊里发 cookie 等于把账号给别人，请只在自己和 bot 的私聊里操作或直接用网页端";

		private static string NormalizeNetease(string input)
		{
			input = input.Trim();
			var idMatch = System.Text.RegularExpressions.Regex.Match(input, @"(\d{5,15})");
			if (idMatch.Success && !input.Contains("song"))
				return $"https://music.163.com/playlist?id={idMatch.Groups[1].Value}";
			return input;
		}
	}
}
