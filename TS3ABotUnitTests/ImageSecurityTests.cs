using NUnit.Framework;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.IO;
using System.Threading.Tasks;
using TS3AudioBot.Helper;

namespace TS3ABotUnitTests
{
	[TestFixture]
	public class ImageSecurityTests
	{
		[Test]
		public async Task ThumbnailDecoderStillResizesStaticImagesAfterSecurityUpgrade()
		{
			using var image = new Image<Rgba32>(640, 320);
			using var input = new MemoryStream();
			image.SaveAsPng(input);
			input.Position = 0;
			using var result = await ImageUtil.ResizeImageSave(input);
			using var decoded = Image.Load(result.Stream);
			Assert.AreEqual("image/jpeg", result.Mime);
			Assert.AreEqual(320, decoded.Width);
			Assert.AreEqual(160, decoded.Height);
		}

		[Test]
		public async Task ThumbnailDecoderPreservesAnimatedImagesAfterSecurityUpgrade()
		{
			using var image = new Image<Rgba32>(8, 8);
			image.Frames.AddFrame(image.Frames.RootFrame);
			using var input = new MemoryStream();
			image.SaveAsGif(input);
			input.Position = 0;
			using var result = await ImageUtil.ResizeImageSave(input);
			using var decoded = Image.Load(result.Stream);
			Assert.AreEqual("image/gif", result.Mime);
			Assert.AreEqual(2, decoded.Frames.Count);
		}
	}
}
