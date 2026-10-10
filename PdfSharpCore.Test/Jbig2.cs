using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;
using PdfSharpCore.Test.Helpers;
using Xunit;

namespace PdfSharpCore.Test
{
    public class Jbig2
    {
        [Theory]
        [InlineData(1, 1)]
        [InlineData(7, 3)]
        [InlineData(8, 8)]
        [InlineData(31, 5)]
        [InlineData(32, 40)]
        [InlineData(33, 17)]
        [InlineData(100, 64)]
        [InlineData(613, 211)]
        public void EncoderRoundTrip(int width, int height)
        {
            foreach (var pixels in new[]
                     {
                         TextLikePattern(width, height),
                         NoisePattern(width, height, 1),
                         new bool[width * height],
                         Enumerable.Repeat(true, width * height).ToArray()
                     })
            {
                foreach (bool oneIsBlack in new[] { true, false })
                {
                    int stride = (width + 7) / 8 + 3; // Extra padding to test stride handling
                    var packed = Pack(pixels, width, height, stride, oneIsBlack, false);

                    var encoded = Jbig2Encoder.EncodeGenericRegion(packed, width, height, stride, oneIsBlack);
                    var decoded = Jbig2TestDecoder.Decode(encoded, out int decodedWidth, out int decodedHeight);

                    decodedWidth.Should().Be(width);
                    decodedHeight.Should().Be(height);
                    decoded.Should().Equal(pixels);
                }
            }
        }

        [Fact]
        public void EncoderIgnoresPaddingBits()
        {
            int width = 13, height = 9;
            var pixels = TextLikePattern(width, height);
            int stride = (width + 7) / 8;
            var packedZeroPadding = Pack(pixels, width, height, stride, true, false);
            var packedOnePadding = Pack(pixels, width, height, stride, true, true);

            Jbig2Encoder.EncodeGenericRegion(packedOnePadding, width, height, stride, true)
                .Should().Equal(Jbig2Encoder.EncodeGenericRegion(packedZeroPadding, width, height, stride, true));
        }

        [Fact]
        public void BilevelImageUsesJbig2()
        {
            int width = 850, height = 1100;
            var pixels = TextLikePattern(width, height);

            var image = SaveAndGetImage(pixels, width, height, true);

            image.Elements.GetName(PdfImage.Keys.Filter).Should().Be("/JBIG2Decode");
            image.Elements.ContainsKey(PdfDictionary.PdfStream.Keys.DecodeParms).Should().BeFalse();
            image.Elements.GetName(PdfImage.Keys.ColorSpace).Should().Be("/DeviceGray");
            image.Elements.GetInteger(PdfImage.Keys.BitsPerComponent).Should().Be(1);
            image.Elements.GetInteger(PdfImage.Keys.Width).Should().Be(width);
            image.Elements.GetInteger(PdfImage.Keys.Height).Should().Be(height);

            var decoded = Jbig2TestDecoder.Decode(image.Stream.Value, out _, out _);
            decoded.Should().Equal(pixels);
        }

        [Fact]
        public void BilevelImageWithJbig2Disabled()
        {
            int width = 850, height = 1100;
            var pixels = TextLikePattern(width, height);

            var image = SaveAndGetImage(pixels, width, height, false);

            image.Elements[PdfImage.Keys.Filter].ToString().Should().NotContain("JBIG2");
        }

        private static PdfDictionary SaveAndGetImage(bool[] pixels, int width, int height, bool enableJbig2)
        {
            var document = new PdfDocument();
            document.Options.EnableJbig2CompressionForBilevelImages = enableJbig2;
            var page = document.AddPage();
            using (var gfx = XGraphics.FromPdfPage(page))
            using (var xImage = XImage.FromImageSource(new BilevelImageSource(pixels, width, height)))
            {
                gfx.DrawImage(xImage, 0, 0, page.Width, page.Height);
            }
            var stream = new MemoryStream();
            document.Save(stream, false);

            stream.Position = 0;
            var readDocument = Pdf.IO.PdfReader.Open(stream, Pdf.IO.PdfDocumentOpenMode.Import);
            return readDocument.Internals.GetAllObjects()
                .OfType<PdfDictionary>()
                .Single(dict => dict.Elements.GetName(PdfImage.Keys.Subtype) == "/Image");
        }

        /// <summary>
        /// Lines of "glyphs" (made of strokes, with irregular spacing) separated by blank lines, so that TPGD has
        /// duplicate rows to work with.
        /// </summary>
        private static bool[] TextLikePattern(int width, int height)
        {
            const int glyphWidth = 9, glyphHeight = 14;
            var random = new Random(42);
            var glyphs = Enumerable.Range(0, 26).Select(_ => RandomGlyph(random, glyphWidth, glyphHeight)).ToArray();
            var pixels = new bool[width * height];
            for (int lineTop = 2; lineTop + glyphHeight < height; lineTop += 24)
            {
                int x = 1 + random.Next(3);
                while (x + glyphWidth < width)
                {
                    if (random.Next(6) != 0)
                    {
                        var glyph = glyphs[random.Next(glyphs.Length)];
                        for (int y = 0; y < glyphHeight; y++)
                        for (int dx = 0; dx < glyphWidth; dx++)
                        {
                            pixels[(lineTop + y) * width + x + dx] = glyph[y * glyphWidth + dx];
                        }
                    }
                    x += glyphWidth + 1 + random.Next(3);
                }
            }
            // Ensure odd sizes get some content at the edges too
            pixels[0] = true;
            pixels[width * height - 1] = true;
            return pixels;
        }

        private static bool[] RandomGlyph(Random random, int width, int height)
        {
            var glyph = new bool[width * height];
            for (int stroke = 0; stroke < 3; stroke++)
            {
                bool vertical = random.Next(2) == 0;
                int pos = random.Next(vertical ? width - 1 : height - 1);
                int start = random.Next(3), end = (vertical ? height : width) - random.Next(3);
                for (int i = start; i < end; i++)
                {
                    // Strokes are 2px thick
                    for (int t = 0; t < 2; t++)
                    {
                        glyph[vertical ? i * width + pos + t : (pos + t) * width + i] = true;
                    }
                }
            }
            return glyph;
        }

        private static bool[] NoisePattern(int width, int height, int seed)
        {
            var random = new Random(seed);
            return Enumerable.Range(0, width * height).Select(_ => random.Next(2) == 0).ToArray();
        }

        private static byte[] Pack(bool[] pixels, int width, int height, int stride, bool oneIsBlack, bool padWithOnes)
        {
            var data = new byte[stride * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < stride * 8; x++)
                {
                    bool bit = x < width ? pixels[y * width + x] == oneIsBlack : padWithOnes;
                    if (bit) data[y * stride + x / 8] |= (byte)(0x80 >> (x % 8));
                }
            }
            return data;
        }

        /// <summary>
        /// Provides a 1-bit image in the format PdfSharp expects from SaveAsPdfIndexedBitmap: rows bottom-up, no
        /// padding between rows, 1 = white.
        /// </summary>
        private class BilevelImageSource : IImageSource
        {
            private readonly bool[] _pixels;

            public BilevelImageSource(bool[] pixels, int width, int height)
            {
                _pixels = pixels;
                Width = width;
                Height = height;
            }

            public int Width { get; }
            public int Height { get; }
            public string Name => null;
            public XImageFormat ImageFormat => XImageFormat.Indexed;

            public void SaveAsJpeg(MemoryStream ms) => throw new NotSupportedException();

            public void SaveAsPdfBitmap(MemoryStream ms) => throw new NotSupportedException();

            public void SaveAsPdfIndexedBitmap(MemoryStream ms)
            {
                int stride = (Width + 7) / 8;
                var topDown = Pack(_pixels, Width, Height, stride, false, false);
                for (int y = Height - 1; y >= 0; y--)
                {
                    ms.Write(topDown, y * stride, stride);
                }
            }
        }
    }
}
