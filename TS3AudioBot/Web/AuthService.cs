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
using System.Security.Cryptography;
using System.Text;

namespace TS3AudioBot.Web
{
	/// <summary>
	/// Shared-secret authentication for the web api.
	/// The key is stored in websecrets.toml (generated on first start) and meant
	/// to be shared with a trusted group of users. Login issues an HMAC-signed
	/// bearer token; failed attempts are rate limited per ip.
	/// </summary>
	public sealed class AuthService
	{
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

		private const int MaxFailures = 5;
		private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(2);
		private static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(30);

		private string _key = string.Empty;
		private byte[] _hmacKey = Array.Empty<byte>();

		private readonly ConcurrentDictionary<string, (int fails, DateTime lockedUntil)> _failures =
			new ConcurrentDictionary<string, (int, DateTime)>();

		public AuthService(string baseDir)
		{
			SecretsStore.Init(baseDir);
			LoadOrCreate();
		}

		public bool AuthEnabled => !string.IsNullOrEmpty(_key);

		private void LoadOrCreate()
		{
			_key = SecretsStore.Get("api_key") ?? string.Empty;

			if (string.IsNullOrWhiteSpace(_key))
			{
				var bytes = RandomNumberGenerator.GetBytes(32);
				_key = Base64UrlEncode(bytes);
				SecretsStore.Set("api_key", _key);
				Log.Warn("==========================================================");
				Log.Warn(" New web api access key generated and saved to websecrets.toml:");
				Log.Warn("   {0}", _key);
				Log.Warn(" Share this key only with users who may control the bot.");
				Log.Warn("==========================================================");
			}

			_hmacKey = SHA256.HashData(Encoding.UTF8.GetBytes("ts3ab-token:" + _key));
		}

		/// <summary>Verifies the shared key and returns a signed token, or null when rejected.</summary>
		public string? Login(string? key, string ip)
		{
			if (!AuthEnabled)
				return string.Empty; // auth disabled => anonymous ok

			var now = DateTime.UtcNow;
			if (_failures.TryGetValue(ip, out var entry) && entry.lockedUntil > now)
				return null;

			if (!FixedTimeEquals(key, _key))
			{
				int prevFails;
				if (entry.lockedUntil > now)
					prevFails = entry.fails;             // currently locked out
				else if (entry.lockedUntil != DateTime.MinValue)
					prevFails = 0;                       // a previous lockout expired
				else
					prevFails = entry.fails;             // never locked; keep counting
				int newFails = prevFails + 1;
				_failures[ip] = newFails >= MaxFailures
					? (newFails, now + LockoutDuration)
					: (newFails, DateTime.MinValue);
				Log.Warn("Failed web login from {0} (attempt {1}/{2})", ip, newFails, MaxFailures);
				return null;
			}

			_failures.TryRemove(ip, out _);
			var expiry = DateTimeOffset.UtcNow.Add(TokenLifetime).ToUnixTimeSeconds();
			var payload = expiry.ToString(System.Globalization.CultureInfo.InvariantCulture);
			var sig = B64Url(HMAC(payload));
			return payload + "." + sig;
		}

		public bool ValidateToken(string? token)
		{
			if (!AuthEnabled)
				return true;
			if (string.IsNullOrEmpty(token))
				return false;
			var dot = token.IndexOf('.');
			if (dot <= 0 || dot == token.Length - 1)
				return false;
			var payload = token.Substring(0, dot);
			var sig = token.Substring(dot + 1);
			if (!long.TryParse(payload, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var expiry))
				return false;
			if (expiry < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
				return false;
			var expected = B64Url(HMAC(payload));
			return FixedTimeEquals(sig, expected);
		}

		/// <summary>Accepts either a valid token or the raw shared key (for scripts / sse query param).</summary>
		public bool ValidateAny(string? credential)
			=> ValidateToken(credential) || FixedTimeEquals(credential, _key);

		private byte[] HMAC(string payload)
			=> new HMACSHA256(_hmacKey).ComputeHash(Encoding.UTF8.GetBytes(payload));

		private static bool FixedTimeEquals(string? a, string? b)
		{
			if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
				return false;
			var ba = Encoding.UTF8.GetBytes(a);
			var bb = Encoding.UTF8.GetBytes(b);
			return CryptographicOperations.FixedTimeEquals(ba, bb);
		}

		private static string B64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

		private static string Base64UrlEncode(byte[] data) => B64Url(data);
	}
}
