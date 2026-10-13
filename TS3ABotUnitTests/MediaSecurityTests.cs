using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TS3AudioBot.Audio;
using TS3AudioBot.ResourceFactories;
using TS3AudioBot.ResourceFactories.Netease;

namespace TS3ABotUnitTests
{
	[TestFixture]
	public class MediaSecurityTests
	{
		private string directory;
		[SetUp]
		public void SetUp()
		{
			directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "media-security-" + Guid.NewGuid());
			Directory.CreateDirectory(directory);
		}
		[TearDown]
		public void TearDown() => Directory.Delete(directory, true);

		[Test]
		public void RelativeMediaPathsCannotEscapeTheConfiguredDirectory()
		{
			var root = Path.Combine(directory, "music");
			var sibling = Path.Combine(directory, "music-private");
			Directory.CreateDirectory(root);
			Directory.CreateDirectory(sibling);
			File.WriteAllText(Path.Combine(root, "song.mp3"), "fixture");
			File.WriteAllText(Path.Combine(directory, "secret.mp3"), "fixture");
			File.WriteAllText(Path.Combine(sibling, "secret.mp3"), "fixture");
			Assert.IsNotNull(MediaResolver.TryInPath(root, "song.mp3"));
			Assert.IsNull(MediaResolver.TryInPath(root, Path.Combine("..", "secret.mp3")));
			Assert.IsNull(MediaResolver.TryInPath(root, Path.Combine("..", "music-private", "secret.mp3")));
			Assert.IsNull(MediaResolver.TryInPath(root, Path.Combine(sibling, "secret.mp3")));
		}

		[Test]
		public void FfmpegInputCannotIntroduceAdditionalArguments()
		{
			const string input = "song\" -f wav /tmp/unwanted-file \".mp3";
			var args = FfmpegProducer.BuildArguments(input, TimeSpan.Zero);
			Assert.AreEqual(input, args[Array.IndexOf(args, "-i") + 1]);
			Assert.AreEqual(1, args.Count(a => a == "-f"));
			Assert.AreEqual("pipe:1", args.Last());
			Assert.IsFalse(args.Contains("/tmp/unwanted-file"));
		}

		[Test]
		public void YoutubeSearchAndPythonPathsRemainSingleArguments()
		{
			const string script = "C:\\tool directory\\youtube_dl\\__main__.py";
			const string query = "ytsearch10:hello\" --exec unexpected \"world";
			var args = YoutubeDlHelper.BuildArguments(script, new[] { "--dump-single-json", "--", query });
			var info = YoutubeDlHelper.CreateStartInfo("python", args);
			Assert.IsFalse(info.UseShellExecute);
			Assert.IsEmpty(info.Arguments);
			CollectionAssert.AreEqual(new[] { script, "--dump-single-json", "--", query }, info.ArgumentList);
		}

		[TestCase(true), TestCase(false)]
		public void OversizedCacheDownloadsAreRejectedAndPartialFilesRemoved(bool knownLength)
		{
			using var client = new HttpClient(new FixtureHandler(new byte[1024 * 1024 + 1], knownLength));
			using var cache = new NeteaseCache(true, directory, 1, client);
			Assert.ThrowsAsync<IOException>(() => cache.DownloadTo(123, "https://fixture.invalid/song", false));
			Assert.IsEmpty(Directory.GetFiles(directory));
		}

		[Test]
		public async Task ValidCacheDownloadIsPublishedAfterCompletion()
		{
			using var client = new HttpClient(new FixtureHandler(new byte[32 * 1024], false));
			using var cache = new NeteaseCache(true, directory, 1, client);
			await cache.DownloadTo(123, "https://fixture.invalid/song", false);
			Assert.IsNotNull(cache.TryGet(123));
			Assert.IsFalse(File.Exists(Path.Combine(directory, "123.mp3.part")));
		}

		[TestCase("abc\r\nInjected: value"), TestCase("abc; other=value"), TestCase("abc,other=value"), TestCase("abc value"), TestCase("abc\"value")]
		public void PersonalCookieValuesCannotInjectHeadersOrAdditionalCookies(string cookie)
		{
			Assert.Throws<ArgumentException>(() => NeteaseApi.NormalizeCookie(cookie));
		}

		[Test]
		public void EmptyCookiesAreOmittedAndOversizedCookiesAreRejected()
		{
			Assert.IsNull(NeteaseApi.NormalizeCookie("  "));
			Assert.AreEqual("valid-cookie==", NeteaseApi.NormalizeCookie(" valid-cookie== "));
			Assert.Throws<ArgumentException>(() => NeteaseApi.NormalizeCookie(new string('x', 8193)));
		}

		[Test]
		public async Task ProviderResponseCookiesAreNotReusedForAnotherRequest()
		{
			using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
			var listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			var address = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";
			var received = new List<string>();
			var server = Serve();
			try
			{
				using var client = NeteaseApi.CreateHttpClient();
				using var first = new HttpRequestMessage(HttpMethod.Get, address);
				first.Headers.Add("Cookie", "os=pc; MUSIC_U=caller-a");
				using var firstResponse = await client.SendAsync(first, deadline.Token);
				using var second = new HttpRequestMessage(HttpMethod.Get, address);
				second.Headers.Add("Cookie", "os=pc");
				using var secondResponse = await client.SendAsync(second, deadline.Token);
				await server;
				StringAssert.Contains("MUSIC_U=caller-a", received[0]);
				StringAssert.DoesNotContain("MUSIC_U", received[1]);
			}
			finally { deadline.Cancel(); listener.Stop(); }

			async Task Serve()
			{
				for (int i = 0; i < 2; i++)
				{
					using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
					using var stream = socket.GetStream();
					using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
					var headers = new StringBuilder();
					string line;
					while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(deadline.Token))) headers.AppendLine(line);
					received.Add(headers.ToString());
					var reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nSet-Cookie: MUSIC_U=provider-session; Path=/\r\nConnection: close\r\n\r\n");
					await stream.WriteAsync(reply.AsMemory(), deadline.Token);
				}
			}
		}

		private sealed class FixtureHandler : HttpMessageHandler
		{
			private readonly byte[] bytes;
			private readonly bool knownLength;
			public FixtureHandler(byte[] bytes, bool knownLength) { this.bytes = bytes; this.knownLength = knownLength; }
			protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
			{
				HttpContent content = knownLength ? (HttpContent)new ByteArrayContent(bytes) : new UnknownLengthContent(bytes);
				return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
			}
		}

		private sealed class UnknownLengthContent : HttpContent
		{
			private readonly byte[] bytes;
			public UnknownLengthContent(byte[] bytes) { this.bytes = bytes; }
			protected override bool TryComputeLength(out long length) { length = 0; return false; }
			protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) => stream.WriteAsync(bytes, 0, bytes.Length);
			protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes));
		}
	}
}
