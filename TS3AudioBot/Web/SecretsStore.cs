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
using System.Text;

namespace TS3AudioBot.Web
{
	/// <summary>
	/// Small key/value store for secrets (web api key, netease cookie) kept in
	/// websecrets.ini next to the main config, restricted to the owner.
	/// </summary>
	public static class SecretsStore
	{
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
		private static readonly object FileLock = new object();
		private static string? _path;

		public static void Init(string baseDir) => _path = Path.Combine(baseDir, "websecrets.ini");

		public static string? Get(string key)
		{
			if (_path is null)
				throw new InvalidOperationException("SecretsStore not initialized");
			try
			{
				lock (FileLock)
				{
					if (!File.Exists(_path))
						return null;
					foreach (var line in File.ReadAllLines(_path))
					{
						var idx = line.IndexOf('=');
						if (idx <= 0)
							continue;
						if (string.Equals(line.Substring(0, idx).Trim(), key, StringComparison.Ordinal))
							return line.Substring(idx + 1).Trim();
					}
					return null;
				}
			}
			catch (Exception ex)
			{
				Log.Warn(ex, "Could not read secret '{0}'", key);
				return null;
			}
		}

		public static void Set(string key, string value)
		{
			if (_path is null)
				throw new InvalidOperationException("SecretsStore not initialized");
			try
			{
				lock (FileLock)
				{
					var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
					if (File.Exists(_path))
					{
						foreach (var line in File.ReadAllLines(_path))
						{
							var idx = line.IndexOf('=');
							if (idx <= 0)
								continue;
							entries[line.Substring(0, idx).Trim()] = line.Substring(idx + 1).Trim();
						}
					}
					entries[key] = value;

					var sb = new StringBuilder();
					sb.AppendLine("# TS3AudioBot secrets - do not share this file");
					foreach (var kvp in entries)
						sb.Append(kvp.Key).Append(" = ").AppendLine(kvp.Value);
					File.WriteAllText(_path, sb.ToString());

					try
					{
						if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
							File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
					}
					catch (Exception ex) { Log.Debug(ex, "Could not restrict secrets file mode"); }
				}
			}
			catch (Exception ex)
			{
				Log.Error(ex, "Could not persist secret '{0}'", key);
				throw;
			}
		}
	}
}
