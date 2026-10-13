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
using System.Security.Cryptography;
using System.Text;

namespace TS3AudioBot.Web
{
	/// <summary>Shared-key login with short-lived, individually revocable sessions.</summary>
	public sealed class AuthService
	{
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
		private const int MaxFailures = 5;
		private const int MaxTrackedAddresses = 4096;
		private const int MaxSessions = 2048;
		private const int MaxCredentialLength = 256;
		private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(2);
		private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(8);
		private readonly object sync = new object();
		private readonly TimeProvider clock;
		private readonly string key;
		private readonly Dictionary<string, DateTimeOffset> sessions = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
		private readonly Dictionary<string, (int fails, DateTimeOffset lastFailure, DateTimeOffset lockedUntil)> failures =
			new Dictionary<string, (int, DateTimeOffset, DateTimeOffset)>(StringComparer.Ordinal);

		public AuthService(string baseDir, TimeProvider? clock = null)
		{
			this.clock = clock ?? TimeProvider.System;
			SecretsStore.Init(baseDir);
			key = SecretsStore.Get("api_key") ?? string.Empty;
			if (string.IsNullOrWhiteSpace(key))
			{
				key = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
				SecretsStore.Set("api_key", key);
				Log.Warn("New web api access key saved to websecrets.ini. Read it from that owner-only file; never publish it in logs.");
			}
			if (key.Length > MaxCredentialLength)
				throw new InvalidOperationException("The configured API key exceeds the supported credential length.");
		}

		public bool AuthEnabled => !string.IsNullOrEmpty(key);

		/// <summary>Login state is bounded and updates atomically, including concurrent failures.</summary>
		public string? Login(string? credential, string ip)
		{
			lock (sync)
			{
				var now = clock.GetUtcNow();
				foreach (var address in failures.Where(x => x.Value.lastFailure + LockoutDuration <= now).Select(x => x.Key).ToArray())
					failures.Remove(address);
				if (failures.TryGetValue(ip, out var failure) && failure.lockedUntil > now)
					return null;

				if (!FixedTimeEquals(credential, key))
				{
					if (!failures.ContainsKey(ip) && failures.Count >= MaxTrackedAddresses)
						return null;
					var count = failure.fails + 1;
					failures[ip] = (count, now, count >= MaxFailures ? now + LockoutDuration : DateTimeOffset.MinValue);
					Log.Warn("Failed web login from {0} (attempt {1}/{2})", ip, count, MaxFailures);
					return null;
				}

				failures.Remove(ip);
				foreach (var token in sessions.Where(x => x.Value <= now).Select(x => x.Key).ToArray())
					sessions.Remove(token);
				if (sessions.Count >= MaxSessions)
					return null;

				// Fresh randomness prevents logins in the same second sharing a session.
				var session = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
				sessions.Add(session, now + TokenLifetime);
				return session;
			}
		}

		public bool ValidateToken(string? token)
		{
			if (string.IsNullOrEmpty(token) || token.Length > MaxCredentialLength)
				return false;
			lock (sync)
			{
				if (!sessions.TryGetValue(token, out var expiry))
					return false;
				if (expiry <= clock.GetUtcNow())
				{
					sessions.Remove(token);
					return false;
				}
				return true;
			}
		}

		public void RevokeToken(string? token)
		{
			if (string.IsNullOrEmpty(token) || token.Length > MaxCredentialLength)
				return;
			lock (sync) sessions.Remove(token);
		}

		/// <summary>The raw shared key remains supported in X-API-Key for trusted scripts.</summary>
		public bool ValidateAny(string? credential) => ValidateToken(credential) || FixedTimeEquals(credential, key);

		private static bool FixedTimeEquals(string? a, string? b)
		{
			if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || a.Length > MaxCredentialLength)
				return false;
			return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
		}

		private static string Base64UrlEncode(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
	}
}
