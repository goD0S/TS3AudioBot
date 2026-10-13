// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TS3AudioBot.ResourceFactories.Netease
{
	public sealed class CacheStats
	{
		public long FileCount { get; set; }
		public long VipCount { get; set; }
		public long TotalBytes { get; set; }
		public long MaxBytes { get; set; }
		public string Path { get; set; } = string.Empty;
		public int ActiveDownloads { get; set; }
	}

	/// <summary>
	/// Local audio cache for netease songs, keyed by song id.
	/// Everything gets cached except trial clips. VIP songs are stored with a
	/// .vip marker and are evicted last (free songs are trivially re-resolvable,
	/// while a cached vip full version required someone's cookie once).
	/// </summary>
	public sealed class NeteaseCache : IDisposable
	{
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

		private readonly string cacheDir;
		private readonly long maxBytes;
		private readonly bool enabled;
		private readonly HttpClient http;
		private readonly SemaphoreSlim downloadSlots = new SemaphoreSlim(2, 2);
		private readonly ConcurrentDictionary<long, byte> activeDownloads = new ConcurrentDictionary<long, byte>();
		private readonly object evictLock = new object();
		private const int MaxQueuedDownloads = 128;
		private const long MaxSongBytes = 256L * 1024 * 1024;

		public NeteaseCache(bool enabled, string cacheDir, long maxBytesMb, HttpClient? httpClient = null)
		{
			this.enabled = enabled;
			this.cacheDir = cacheDir;
			this.maxBytes = maxBytesMb * 1024 * 1024;
			http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
			if (enabled)
			{
				try { Directory.CreateDirectory(cacheDir); }
				catch (Exception ex)
				{
					Log.Warn(ex, "Could not create netease cache dir, caching disabled");
					this.enabled = false;
				}
			}
		}

		public bool Enabled => enabled;

		private string VipFileFor(long songId) => Path.Combine(cacheDir, songId + ".vip.mp3");
		private string FreeFileFor(long songId) => Path.Combine(cacheDir, songId + ".mp3");

		/// <summary>Returns the cached file path, or null on cache miss. Touches the access time.</summary>
		public string? TryGet(long songId)
		{
			if (!enabled)
				return null;
			foreach (var file in new[] { VipFileFor(songId), FreeFileFor(songId) })
			{
				if (File.Exists(file))
				{
					try { File.SetLastAccessTimeUtc(file, DateTime.UtcNow); } catch (IOException) { }
					return file;
				}
			}
			return null;
		}

		/// <summary>True if a download for this song is currently running or queued.</summary>
		public bool IsDownloading(long songId) => activeDownloads.ContainsKey(songId);

		/// <summary>Starts a background download. Returns false if already downloading.</summary>
		public bool QueueDownload(long songId, string url, bool isVip)
		{
			if (!enabled)
				return false;
			var target = isVip ? VipFileFor(songId) : FreeFileFor(songId);
			if (TryGet(songId) != null)
				return false;
			lock (evictLock)
			{
				if (activeDownloads.Count >= MaxQueuedDownloads || !activeDownloads.TryAdd(songId, 0))
					return false;
			}

			_ = Task.Run(async () =>
			{
				try
				{
					await downloadSlots.WaitAsync().ConfigureAwait(false);
					try
					{
						await DownloadTo(songId, url, isVip).ConfigureAwait(false);
						Log.Info("Cached netease song {0}{1}", songId, isVip ? " (vip)" : "");
						EvictIfNeeded();
					}
					finally { downloadSlots.Release(); }
				}
				catch (Exception ex)
				{
					Log.Warn("Failed to cache netease song {0}: {1}", songId, ex.Message);
					try { File.Delete(target + ".part"); } catch (IOException) { }
				}
				finally { activeDownloads.TryRemove(songId, out _); }
			});
			return true;
		}

		internal async Task DownloadTo(long songId, string url, bool isVip)
		{
			var target = isVip ? VipFileFor(songId) : FreeFileFor(songId);
			var temp = target + ".part";
			using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
			var limit = Math.Min(maxBytes, MaxSongBytes);
			try
			{
				using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
				resp.EnsureSuccessStatusCode();
				if (resp.Content.Headers.ContentLength > limit)
					throw new IOException("song exceeds the cache download size limit");
				using var src = await resp.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
				using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
				{
					var buffer = new byte[1 << 16];
					long total = 0;
					int read;
					while ((read = await src.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false)) > 0)
					{
						total += read;
						if (total > limit) throw new IOException("song exceeds the cache download size limit");
						await dst.WriteAsync(buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
					}
				}
				// Reject placeholder/empty files before publishing them to the cache.
				if (new FileInfo(temp).Length < 16 * 1024)
					throw new IOException("downloaded file too small, probably a placeholder");
				File.Move(temp, target, overwrite: true);
			}
			catch
			{
				try { File.Delete(temp); } catch (IOException) { }
				throw;
			}
		}

		private void EvictIfNeeded()
		{
			if (!enabled)
				return;
			lock (evictLock)
			{
				var files = SafeFiles();
				long total = files.Sum(f => f.Length);
				if (total <= maxBytes)
					return;

				// evict free songs first (LRU), vip songs last (LRU)
				foreach (var file in files.Where(f => !f.Name.Contains(".vip.")).OrderBy(f => f.LastAccessTimeUtc)
					.Concat(files.Where(f => f.Name.Contains(".vip.")).OrderBy(f => f.LastAccessTimeUtc)))
				{
					if (total <= maxBytes)
						break;
					try
					{
						total -= file.Length;
						file.Delete();
						Log.Debug("Evicted netease cache file {0}", file.Name);
					}
					catch (IOException) { }
				}
			}
		}

		public void Clear()
		{
			lock (evictLock)
			{
				foreach (var file in SafeFiles())
				{
					try { file.Delete(); } catch (IOException) { }
				}
			}
		}

		public CacheStats GetStats()
		{
			var files = SafeFiles();
			return new CacheStats
			{
				FileCount = files.Length,
				VipCount = files.Count(f => f.Name.Contains(".vip.")),
				TotalBytes = files.Sum(f => f.Length),
				MaxBytes = maxBytes,
				Path = cacheDir,
				ActiveDownloads = activeDownloads.Count,
			};
		}

		private FileInfo[] SafeFiles()
		{
			try
			{
				var di = new DirectoryInfo(cacheDir);
				if (!di.Exists)
					return Array.Empty<FileInfo>();
				return di.GetFiles("*.mp3");
			}
			catch (IOException) { return Array.Empty<FileInfo>(); }
		}

		public void Dispose()
		{
			downloadSlots.Dispose();
		}
	}
}
