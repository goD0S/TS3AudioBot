using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TS3AudioBot.Config;
using TS3AudioBot.Dependency;
using TS3AudioBot.Web;
using TS3AudioBot.Web.Api;

namespace TS3ABotUnitTests
{
	[TestFixture, NonParallelizable]
	public class WebSecurityTests
	{
		private string directory;
		private const string Key = "test-only-shared-key-never-a-production-credential";
		private TestClock clock;
		private AuthService auth;
		private AuthService previousAuth;

		[SetUp]
		public void SetUp()
		{
			directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "web-security-" + Guid.NewGuid());
			Directory.CreateDirectory(directory);
			SecretsStore.Init(directory);
			SecretsStore.Set("api_key", Key);
			clock = new TestClock();
			auth = new AuthService(directory, clock);
			previousAuth = ApiV2.Auth;
			ApiV2.Auth = auth;
		}

		[TearDown]
		public void TearDown()
		{
			ApiV2.Auth = previousAuth;
			Directory.Delete(directory, true);
		}

		[Test]
		public void SessionsAreUniqueAndIndividuallyRevocable()
		{
			var first = auth.Login(Key, "client");
			var second = auth.Login(Key, "client");
			Assert.IsNotNull(first);
			Assert.AreNotEqual(first, second);
			Assert.IsTrue(auth.ValidateToken(first));
			auth.RevokeToken(first);
			Assert.IsFalse(auth.ValidateToken(first));
			Assert.IsTrue(auth.ValidateToken(second));
		}

		[Test]
		public void SessionsExpireAtEightHoursAndDoNotSurviveRestart()
		{
			var token = auth.Login(Key, "client");
			Assert.IsFalse(new AuthService(directory, clock).ValidateToken(token));
			clock.Advance(TimeSpan.FromHours(8));
			Assert.IsFalse(auth.ValidateToken(token));
		}

		[Test]
		public void WrongKeysAreLockedOutAndRecoverAfterTheWindow()
		{
			for (int i = 0; i < 5; i++) Assert.IsNull(auth.Login("wrong", "client"));
			Assert.IsNull(auth.Login(Key, "client"));
			Assert.IsNotNull(auth.Login(Key, "another-client"));
			clock.Advance(TimeSpan.FromMinutes(2));
			Assert.IsNotNull(auth.Login(Key, "client"));
		}

		[Test]
		public void ConcurrentFailuresCannotLoseTheLockout()
		{
			Parallel.For(0, 32, _ => auth.Login("wrong", "client"));
			Assert.IsNull(auth.Login(Key, "client"));
			clock.Advance(TimeSpan.FromMinutes(2));
			Assert.IsNotNull(auth.Login(Key, "client"));
		}

		[Test]
		public void OldSignedTokensAndOversizedCredentialsAreRejected()
		{
			Assert.IsFalse(auth.ValidateToken("9999999999.old-signed-token"));
			Assert.IsFalse(auth.ValidateToken(Key));
			Assert.IsTrue(auth.ValidateAny(Key));
			Assert.IsFalse(auth.ValidateAny(new string('x', 100000)));
			Assert.IsNull(auth.Login(null, "client"));
		}

		[Test]
		public void QueryCredentialsCannotAuthorizeAndHeadersStillWork()
		{
			var token = auth.Login(Key, "client");
			var ctx = new DefaultHttpContext();
			ctx.Request.QueryString = new QueryString("?key=" + token);
			Assert.IsFalse(WebServer.IsAuthorized(ctx, auth));
			ctx.Request.QueryString = new QueryString("?key=" + Key);
			Assert.IsFalse(WebServer.IsAuthorized(ctx, auth));
			ctx.Request.Headers["X-API-Key"] = Key;
			Assert.IsTrue(WebServer.IsAuthorized(ctx, auth));
			ctx.Request.Headers["Authorization"] = "Bearer invalid";
			Assert.IsFalse(WebServer.IsAuthorized(ctx, auth));
			ctx.Request.Headers["Authorization"] = "Bearer " + token;
			Assert.IsTrue(WebServer.IsAuthorized(ctx, auth));
		}

		[Test]
		public async Task LogoutEndpointInvalidatesThePresentedSession()
		{
			var token = auth.Login(Key, "client");
			var ctx = Request("POST");
			ctx.Request.Headers["Authorization"] = "Bearer " + token;
			Assert.IsTrue(WebServer.IsAuthorized(ctx, auth));
			await ApiV2.HandleAsync(ctx, new CoreInjector(), "/auth/logout");
			Assert.AreEqual(200, ctx.Response.StatusCode);
			Assert.IsFalse(WebServer.IsAuthorized(ctx, auth));
			Assert.IsFalse(ApiV2.IsPublicPath("/auth/logout"));
		}

		[TestCase("null"), TestCase("{"), TestCase("[]")]
		public void MalformedLoginBodiesAreRejected(string body)
		{
			var ctx = Request("POST");
			ctx.Request.ContentType = "application/json";
			ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
			Assert.ThrowsAsync<BadHttpRequestException>(() => ApiV2.HandleAsync(ctx, new CoreInjector(), "/auth/login"));
		}

		[Test]
		public void LoginRequiresJsonContentType()
		{
			var ctx = Request("POST");
			ctx.Request.ContentType = "text/plain";
			ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
			var error = Assert.ThrowsAsync<BadHttpRequestException>(() => ApiV2.HandleAsync(ctx, new CoreInjector(), "/auth/login"));
			Assert.AreEqual(415, error.StatusCode);
		}

		[Test]
		public void InvalidPersonalCookieIsRejectedBeforeCallingTheProvider()
		{
			var ctx = Request("POST");
			ctx.Request.ContentType = "application/json";
			ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"cookie\":\"value; injected=other\"}"));
			var error = Assert.ThrowsAsync<BadHttpRequestException>(() => ApiV2.HandleAsync(ctx, new CoreInjector(), "/netease/cookie/check"));
			Assert.AreEqual(400, error.StatusCode);
		}

		[Test]
		public async Task RevocationClosesAnAlreadyOpenEventStream()
		{
			var token = auth.Login(Key, "client");
			var ctx = Request("GET");
			ctx.Request.Headers["Authorization"] = "Bearer " + token;
			using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
			ctx.RequestAborted = cancel.Token;
			using var body = new ObservedStream();
			ctx.Response.Body = body;
			var events = ApiV2.HandleAsync(ctx, new CoreInjector(), "/events");
			await body.FirstFlush.Task;
			auth.RevokeToken(token);
			Assert.AreSame(events, await Task.WhenAny(events, Task.Delay(5000)));
			await events;
		}

		[TestCase(false), TestCase(true)]
		public void MissingOrInvalidTlsCertificateFailsBeforeStartup(bool invalidFile)
		{
			var path = Path.Combine(directory, "invalid.pfx");
			if (invalidFile) File.WriteAllText(path, "not a certificate");
			var config = new ConfWeb();
			config.UseSsl.Value = true;
			config.SslCert.Value = path;
			using var server = new WebServer(config, new CoreInjector());
			Assert.Throws<InvalidOperationException>(() => server.StartWebServerInternal());
		}

		[TestCase("*"), TestCase("https://*.example.test"), TestCase("https://example.test/path"), TestCase("https://user@example.test")]
		public void CorsRejectsWildcardsPathsAndCredentials(string origin)
		{
			Assert.Throws<ArgumentException>(() => WebServer.ValidateCorsOrigins(new[] { origin }));
		}

		[Test]
		public void CorsDefaultsToSameOriginAndAcceptsExactConfiguredOrigins()
		{
			Assert.IsEmpty(WebServer.ValidateCorsOrigins(Array.Empty<string>()));
			CollectionAssert.AreEqual(new[] { "https://example.test:8443" }, WebServer.ValidateCorsOrigins(new[] { "https://example.test:8443/" }));
		}

		private static DefaultHttpContext Request(string method)
		{
			var ctx = new DefaultHttpContext();
			ctx.Request.Method = method;
			ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
			ctx.Response.Body = new MemoryStream();
			return ctx;
		}

		private sealed class TestClock : TimeProvider
		{
			private DateTimeOffset now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
			public override DateTimeOffset GetUtcNow() => now;
			public void Advance(TimeSpan duration) => now += duration;
		}

		private sealed class ObservedStream : MemoryStream
		{
			public readonly TaskCompletionSource<bool> FirstFlush = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			public override Task FlushAsync(CancellationToken token)
			{
				FirstFlush.TrySetResult(true);
				return base.FlushAsync(token);
			}
		}
	}
}
