// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;
using System.Threading;

namespace TS3AudioBot.ResourceFactories.Netease
{
	/// <summary>
	/// Ambient, per-call MUSIC_U cookie. Users provide their own cookie for a
	/// single operation (e.g. one playlist import); it is never persisted on
	/// the server and cleared when the operation finishes ("用完即焚").
	/// </summary>
	public static class NeteaseCookieContext
	{
		private static readonly AsyncLocal<string?> Cookie = new AsyncLocal<string?>();
		private static readonly string? Empty = null;

		/// <summary>The cookie of the current async flow, or null.</summary>
		public static string? Current => Cookie.Value;

		public static IDisposable? Use(string? cookie)
		{
			var previous = Cookie.Value;
			Cookie.Value = string.IsNullOrWhiteSpace(cookie) ? null : cookie.Trim();
			if (Cookie.Value is null)
				return null;
			return new Restore(previous);
		}

		private sealed class Restore : IDisposable
		{
			private readonly string? _previous;
			public Restore(string? previous) => _previous = previous;
			public void Dispose() => Cookie.Value = _previous;
		}
	}
}
