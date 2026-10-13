// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TS3AudioBot.Config;
using TS3AudioBot.Dependency;

namespace TS3AudioBot.Web
{
	public sealed class WebServer : IDisposable
	{
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

		private CancellationTokenSource? cancelToken;
		private readonly ConfWeb config;
		private readonly CoreInjector coreInjector;
		private Api.WebApi? api;
		private WebApplication? app;
		private bool sslBound;

		public WebServer(ConfWeb config, CoreInjector coreInjector)
		{
			this.config = config;
			this.coreInjector = coreInjector;
		}

		// TODO write server to be reload-able
		public void StartWebServer()
		{
			var startWebServer = false;

			if (config.Api.Enabled || config.Interface.Enabled)
			{
				if (!config.Api.Enabled)
					Log.Warn("The api is required for the webinterface to work properly; The api is now implicitly enabled. Enable the api in the config to remove this warning.");

				if (!coreInjector.TryCreate<Api.WebApi>(out var api))
					throw new Exception("Could not create Api object.");

				this.api = api;
				startWebServer = true;
			}

			if (startWebServer)
			{
				StartWebServerInternal();
			}
		}

		public string? FindWebFolder()
		{
			var webData = config.Interface;
			if (string.IsNullOrEmpty(webData.Path))
			{
				for (int i = 0; i < 5; i++)
				{
					var up = Path.Combine(Enumerable.Repeat("..", i).ToArray());
					var checkDir = Path.Combine(up, "WebInterface");
					if (Directory.Exists(checkDir))
						return Path.GetFullPath(checkDir);
				}

				// Assembly.Location is null/empty when running from a single-file bundle
				var asmLocation = typeof(Core).Assembly.Location;
				if (!string.IsNullOrEmpty(asmLocation))
				{
					var asmPath = Path.GetDirectoryName(asmLocation)!;
					var asmWebPath = Path.GetFullPath(Path.Combine(asmPath, "WebInterface"));
					if (Directory.Exists(asmWebPath))
						return asmWebPath;
				}
			}
			else if (Directory.Exists(webData.Path))
			{
				return Path.GetFullPath(webData.Path);
			}

			return null;
		}

		private void StartWebServerInternal()
		{
			cancelToken?.Cancel();
			cancelToken?.Dispose();
			cancelToken = new CancellationTokenSource();

			var auth = new AuthService(Directory.GetCurrentDirectory());
			Api.ApiV2.Auth = auth;

			var builder = WebApplication.CreateBuilder(new WebApplicationOptions
			{
				Args = Array.Empty<string>(),
				ContentRootPath = Directory.GetCurrentDirectory(),
			});

			builder.WebHost.ConfigureKestrel(kestrel =>
			{
				kestrel.Limits.MaxRequestBodySize = 30_000_000; // 30 MiB
				// the legacy v1 api writes responses synchronously
				kestrel.AllowSynchronousIO = true;

				// HTTPS (with the legacy v1 api on the same port, plain http is off)
				if (config.UseSsl.Value)
				{
					var cert = GetOrCreateCertificate(config);
					if (cert != null)
					{
						kestrel.ListenAnyIP(config.Port.Value, listen => listen.UseHttps(cert));
						sslBound = true;
					}
				}
			});
			builder.Logging.ClearProviders();

			builder.Services.AddCors(options =>
			{
				options.AddPolicy("TS3AB", policy =>
				{
					policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
				});
			});

			var app = builder.Build();
			this.app = app;

			app.UseCors("TS3AB");

			var apiLocal = api;
			app.Use(async (ctx, next) =>
			{
				if (ctx.Request.Path.StartsWithSegments("/api"))
				{
					var subPath = ctx.Request.Path.Value!.Substring("/api".Length);

					// login/state must be reachable without credentials
					var v2 = subPath.StartsWith("/v2", StringComparison.Ordinal);
					if (v2 && Api.ApiV2.IsPublicPath(subPath.Substring(3)))
					{
						try { await Api.ApiV2.HandleAsync(ctx, coreInjector, subPath.Substring(3)); }
						catch (Exception ex) { Log.Error("Error in api v2: {0}", ex.ToString()); }
						return;
					}

					// shared-key gate for EVERYTHING under /api (the v1 bridge can
					// execute commands and would be a remote code execution hole)
					if (!IsAuthorized(ctx, auth))
					{
						ctx.Response.StatusCode = 401;
						ctx.Response.ContentType = "application/json";
						await ctx.Response.WriteAsync("{\"error\":\"需要密钥登录\"}");
						return;
					}

					if (subPath.StartsWith("/v2", StringComparison.Ordinal))
					{
						try
						{
							await Api.ApiV2.HandleAsync(ctx, coreInjector, subPath.Substring(3));
						}
						catch (Exception ex)
						{
							Log.Error("Error in api v2: {0}", ex.ToString());
							if (!ctx.Response.HasStarted)
							{
								ctx.Response.StatusCode = 500;
								ctx.Response.ContentType = "application/json";
								await ctx.Response.WriteAsync("{\"error\":\"服务器内部错误: " + ex.Message.Replace("\"", "'") + "\"}");
							}
						}
						return;
					}

					if (apiLocal != null)
					{
						// legacy v1 command bridge; it expects the path without the /api prefix
						ctx.Request.Path = subPath;
						using var _ = NLog.MappedDiagnosticsLogicalContext.SetScoped("BotId", "Api");
						await Log.SwallowAsync(() => apiLocal.ProcessApiV1Call(ctx));
						return;
					}
				}
				await next();
			});

			if (config.Interface.Enabled)
			{
				var baseDir = FindWebFolder();
				if (baseDir is null)
				{
					Log.Error("Can't find a WebInterface path to host. Try specifying the path to host in the config");
				}
				else
				{
					app.UseFileServer(new FileServerOptions
					{
						FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(baseDir),
						EnableDefaultFiles = true,
					});
				}
			}

			Log.Info("Starting Webserver on {0} port {1}", sslBound ? "HTTPS" : "HTTP", config.Port.Value);
			StartCertificateReloadWatch();
			var token = cancelToken.Token;
			_ = Task.Run(async () =>
			{
				try
				{
					if (!sslBound) // https listener was configured directly on kestrel
					{
						var addrs = config.Hosts.Value;
						if (addrs.Contains("*"))
							app.Urls.Add($"http://*:{config.Port.Value}");
						else if (addrs.Count == 1 && addrs[0] == "localhost")
							app.Urls.Add($"http://127.0.0.1:{config.Port.Value}");
						else
							foreach (var uri in addrs)
								app.Urls.Add(new UriBuilder(uri.Contains("://") ? uri : "http://" + uri) { Port = config.Port.Value }.Uri.AbsoluteUri);
					}

					await app.RunAsync().ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					Log.Error(ex, "The webserver could not be started");
				}
			});
		}

		/// <summary>
		/// Loads the configured certificate, or generates a persistent self-signed
		/// one (webssl.pfx next to the config) when none is set.
		/// </summary>
		private System.Security.Cryptography.X509Certificates.X509Certificate2? GetOrCreateCertificate(ConfWeb config)
		{
			try
			{
				var certPath = config.SslCert.Value;
				if (!string.IsNullOrWhiteSpace(certPath))
				{
					if (!File.Exists(certPath))
					{
						Log.Error("Configured ssl_cert does not exist: {0}", certPath);
						return null;
					}
					Log.Info("Using configured ssl certificate: {0}", certPath);

					// PEM (e.g. acme.sh fullchain.cer) needs its matching key file
					var ext = Path.GetExtension(certPath).ToLowerInvariant();
					if (ext is ".pem" or ".cer" or ".crt")
					{
						var keyPath = FindPemKeyFile(certPath);
						if (keyPath is null)
						{
							Log.Error("No matching .key file found next to {0}", certPath);
							return null;
						}
						Log.Info("Using pem key: {0}", keyPath);
						var pemCert = string.IsNullOrWhiteSpace(config.SslPassword.Value)
							? global::System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPemFile(certPath, keyPath)
							: global::System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromEncryptedPemFile(certPath, config.SslPassword.Value, keyPath);
						// rebuild as pfx so kestrel can use it on all platforms
						return pemCert.Export(global::System.Security.Cryptography.X509Certificates.X509ContentType.Pfx) is var exported
							? new System.Security.Cryptography.X509Certificates.X509Certificate2(exported)
							: pemCert;
					}

					return string.IsNullOrWhiteSpace(config.SslPassword.Value)
						? new System.Security.Cryptography.X509Certificates.X509Certificate2(certPath)
						: new System.Security.Cryptography.X509Certificates.X509Certificate2(certPath, config.SslPassword.Value);
				}

				var autoPath = Path.Combine(Directory.GetCurrentDirectory(), "webssl.pfx");
				if (File.Exists(autoPath))
					return new System.Security.Cryptography.X509Certificates.X509Certificate2(autoPath);

				Log.Info("Generating new self-signed certificate for https ({0}) ...", autoPath);
				using var ecdsa = global::System.Security.Cryptography.ECDsa.Create(global::System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
				var request = new global::System.Security.Cryptography.X509Certificates.CertificateRequest(
					new global::System.Security.Cryptography.X509Certificates.X500DistinguishedName("CN=TS3AudioBot"),
					ecdsa, global::System.Security.Cryptography.HashAlgorithmName.SHA256);
				var san = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
				san.AddDnsName("localhost");
				san.AddIpAddress(System.Net.IPAddress.Loopback);
				san.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
				foreach (var ip in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()))
				{
					try { san.AddIpAddress(ip); } catch (Exception) { }
				}
				request.CertificateExtensions.Add(san.Build());
				var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
				var pfxBytes = cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx);
				File.WriteAllBytes(autoPath, pfxBytes);
				try
				{
					if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
						File.SetUnixFileMode(autoPath, System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite);
				}
				catch (Exception ex) { Log.Debug(ex, "Could not restrict cert file mode"); }
				Log.Info("Self-signed certificate saved (valid 10 years). Browsers will show a one-time trust warning.");
				return new System.Security.Cryptography.X509Certificates.X509Certificate2(autoPath);
			}
			catch (Exception ex)
			{
				Log.Error(ex, "Could not set up the https certificate; falling back to http");
				return null;
			}
		}

		/// <summary>Finds the private key file belonging to a pem certificate.</summary>
		private static string? FindPemKeyFile(string certPath)
		{
			var dir = Path.GetDirectoryName(certPath) ?? ".";
			var sameName = Path.ChangeExtension(certPath, ".key");
			if (File.Exists(sameName))
				return sameName;
			try
			{
				return new DirectoryInfo(dir).GetFiles("*.key")
					.OrderByDescending(f => f.LastWriteTimeUtc)
					.FirstOrDefault()?.FullName;
			}
			catch (Exception ex)
			{
				Log.Warn(ex, "Could not look for a key file next to {0}", certPath);
				return null;
			}
		}

		/// <summary>Shared-key check: Bearer token, X-API-Key or ?key= (sse/scripts).</summary>
		private DateTime? _certStamp;
		private System.Threading.Timer? _certReloadTimer;
		private int _reloading;

		private void StartCertificateReloadWatch()
		{
			if (!config.UseSsl.Value || string.IsNullOrWhiteSpace(config.SslCert.Value))
				return;
			_certStamp = CurrentCertStamp();
			// acme.sh renews the certificate files; pick them up within a few hours
			_certReloadTimer = new System.Threading.Timer(_ =>
			{
				if (Interlocked.Exchange(ref _reloading, 1) == 1)
					return;
				try
				{
					var stamp = CurrentCertStamp();
					if (stamp.HasValue && _certStamp.HasValue && stamp.Value != _certStamp.Value)
					{
						Log.Info("Certificate file changed, reloading web server ...");
						StopWebApp();
						StartWebServerInternal();
						_certStamp = CurrentCertStamp();
						Log.Info("Web server reloaded with the new certificate");
					}
				}
				catch (Exception ex)
				{
					Log.Error(ex, "Certificate reload failed; keeping the old listener");
					try { StartWebServerInternal(); } catch (Exception ex2) { Log.Error(ex2, "Web server restart after failed reload also failed"); }
				}
				finally { Interlocked.Exchange(ref _reloading, 0); }
			}, null, TimeSpan.FromHours(6), TimeSpan.FromHours(6));
		}

		private DateTime? CurrentCertStamp()
		{
			try
			{
				var path = config.SslCert.Value;
				if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
					return null;
				var stamp = File.GetLastWriteTimeUtc(path);
				var key = FindPemKeyFile(path);
				if (key != null)
					stamp = stamp > File.GetLastWriteTimeUtc(key) ? stamp : File.GetLastWriteTimeUtc(key);
				return stamp;
			}
			catch (Exception) { return null; }
		}

		private void StopWebApp()
		{
			try { app?.StopAsync().Wait(TimeSpan.FromSeconds(5)); } catch (Exception) { }
			try { app?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); } catch (Exception) { }
			app = null;
		}

		private static bool IsAuthorized(HttpContext ctx, AuthService auth)
		{
			if (!auth.AuthEnabled)
				return true;

			var header = ctx.Request.Headers["Authorization"].ToString();
			if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
				return auth.ValidateToken(header.Substring(7).Trim());

			var apiKey = ctx.Request.Headers["X-API-Key"].ToString();
			if (!string.IsNullOrWhiteSpace(apiKey))
				return auth.ValidateAny(apiKey.Trim());

			var queryKey = ctx.Request.Query["key"].ToString();
			if (!string.IsNullOrWhiteSpace(queryKey))
				return auth.ValidateAny(queryKey.Trim());

			return false;
		}

		public void OnShutdown()
		{
			Log.Info("WebServer has closed");
		}

		public void Dispose()
		{
			Log.Info("WebServer is closing");

			try { app?.StopAsync().Wait(TimeSpan.FromSeconds(3)); } catch (Exception) { }
			try { app?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch (Exception) { }
			app = null;

			cancelToken?.Cancel();
			cancelToken?.Dispose();
			cancelToken = null;
		}
	}
}
