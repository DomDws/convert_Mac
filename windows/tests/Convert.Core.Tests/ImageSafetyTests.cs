using System.IO.Compression;
using System.Net;
using System.Text;
using Convert.Core;
using ImageMagick;
using Xunit;

namespace Convert.Core.Tests;

/// <summary>
/// Images come from anywhere. A crafted one must not make Convert read other files, reach the
/// network, run ImageMagick's scripting formats, or fill the disk; and an ordinary one must convert
/// whatever its name looks like.
/// </summary>
public sealed class ImageSafetyTests : IDisposable
{
    private readonly string dir = Paths.TempDir();

    public void Dispose() => Paths.TryDelete(dir);

    private static string Convert(string input, string id = "png") =>
        ImageConv.Convert(input, Targets.Catalogue.First(t => t.Id == id), new ConvSettings(), new Runner());

    private string Png(string name, IMagickColor<byte> color, uint size = 8)
    {
        var path = Path.Combine(dir, name);
        using var img = new MagickImage(color, size, size);
        using var f = File.Create(path);
        img.Write(f, MagickFormat.Png);
        return path;
    }

    private string[] Entries => Directory.GetFileSystemEntries(dir).Select(n => Path.GetFileName(n)!).Order().ToArray();

    // Names

    [Theory]
    [InlineData("photo [1].png")]          // ImageMagick reads [1] as "the second frame"
    [InlineData("100%done %d.png")]        // … and %d as a place for a frame number
    [InlineData("scan@2x.png")]
    [InlineData("-loud-.png")]
    public void OddFileNamesAreJustNames(string name)
    {
        var input = Png(name, MagickColors.Teal);
        var out_ = Convert(input, "jpg");
        Assert.Equal(Path.ChangeExtension(input, ".jpg"), out_);
        using var img = new MagickImage(File.ReadAllBytes(out_));
        Assert.Equal(8u, img.Width);
    }

    // Formats that are really scripts

    [Fact]
    public void AScriptNamedLikeAPictureIsNotRun()
    {
        // MSL is ImageMagick's scripting language: this one would copy a picture to a place of its choosing.
        var secret = Png("secret.png", MagickColors.Red);
        var planted = Path.Combine(dir, "planted.png");
        var msl = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <image>
              <read filename="{secret}" />
              <write filename="{planted}" />
            </image>
            """;
        foreach (var name in new[] { "holiday.jpg", "holiday.png", "holiday.gif", "holiday.tga", "holiday.psd" })
        {
            var input = Path.Combine(dir, name);
            File.WriteAllText(input, msl);
            Assert.Throws<ConvertException>(() => Convert(input));
            Assert.False(File.Exists(planted), $"{name} ran as a script");
            File.Delete(input);
        }
        Assert.Equal(["secret.png"], Entries);
    }

    [Fact]
    public void ADrawingScriptCannotPullInOtherFiles()
    {
        // MVG is ImageMagick's drawing language; "image" places another file, or a URL, in the picture.
        var secret = Png("secret.png", MagickColors.Red);
        var input = Path.Combine(dir, "drawing.jpg");
        File.WriteAllText(input, $"push graphic-context\nviewbox 0 0 8 8\nimage over 0,0 8,8 '{secret}'\npop graphic-context\n");
        Assert.Throws<ConvertException>(() => Convert(input));
        Assert.Equal(["drawing.jpg", "secret.png"], Entries);
    }

    // SVG

    [Fact]
    public void AnSvgReachesNeitherTheNetworkNorFilesElsewhere()
    {
        // An SVG can point at other pictures. Ones next to it are part of the drawing; a web address,
        // or a file somewhere else on the PC, is not.
        using var listener = new Listener();
        var elsewhere = Paths.TempDir();
        try
        {
            var secret = Path.Combine(elsewhere, "secret.png");
            File.Copy(Png("red.png", MagickColors.Red), secret);
            File.Delete(Path.Combine(dir, "red.png"));
            var references = new[]
            {
                $"{listener.Url}picture.png", new Uri(secret).AbsoluteUri, secret,
                Path.GetRelativePath(dir, secret), @"\\127.0.0.1\C$\Windows\win.ini",
            };
            var n = 0;
            foreach (var reference in references)
            {
                var input = Path.Combine(dir, $"drawing {++n}.svg");
                File.WriteAllText(input, $"""
                    <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="8" height="8">
                      <style>@import url({listener.Url}style.css);</style>
                      <rect width="8" height="8" fill="white" />
                      <image width="8" height="8" xlink:href="{reference}" />
                    </svg>
                    """);
                string out_;
                try { out_ = Convert(input, "bmp"); }
                catch (ConvertException) { continue; }   // refusing the whole drawing is fine too
                using var result = new MagickImage(File.ReadAllBytes(out_));
                var centre = result.GetPixels().GetPixel(4, 4).ToColor()!;
                Assert.False(centre.R > 200 && centre.G < 50 && centre.B < 50, $"{reference} was drawn into the picture");
            }
            Assert.Equal(0, listener.Requests);
        }
        finally { Paths.TryDelete(elsewhere); }
    }

    [Fact]
    public void EveryAcceptedImageExtensionHasADecoder()
    {
        foreach (var ext in MediaKinds.Extensions[MediaKind.Image]) Assert.True(ImageConv.CanRead(ext), ext);
    }

    [Fact]
    public void APictureUnderAnotherPicturesNameStillConverts()
    {
        // As on the Mac: the converters go by the contents when those are a well-known format.
        var png = Png("really a png.jpg", MagickColors.Teal);
        using var img = new MagickImage(File.ReadAllBytes(Convert(png, "bmp")));
        Assert.Equal(8u, img.Width);
    }

    [Fact]
    public void SvgContentUnderAnotherNameReachesNothing()
    {
        using var listener = new Listener();
        var secret = Png("secret.png", MagickColors.Red);
        var svg = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="8" height="8">
              <image width="8" height="8" xlink:href="{new Uri(secret).AbsoluteUri}" />
              <image width="8" height="8" xlink:href="{listener.Url}picture.png" />
              <image width="8" height="8" href="{listener.Url}picture2.png" />
            </svg>
            """;
        foreach (var name in new[] { "logo.png", "logo.jpg", "logo.psd", "logo.tga" })
        {
            var input = Path.Combine(dir, name);
            File.WriteAllText(input, svg);
            Assert.Throws<ConvertException>(() => Convert(input));
            File.Delete(input);
        }
        Assert.Equal(0, listener.Requests);
        Assert.Equal(["secret.png"], Entries);
    }

    // Sizes

    [Fact]
    public async Task AnImageClaimingAbsurdDimensionsIsRefusedQuickly()
    {
        // A few hundred bytes that say "900,000 × 900,000 pixels": 3 TB once unpacked.
        foreach (var (name, bytes) in new[] { ("huge.png", HugePng(900_000, 900_000)), ("huge.bmp", HugeBmp(200_000, 200_000)) })
        {
            var input = Path.Combine(dir, name);
            File.WriteAllBytes(input, bytes);
            // Fails the test with a timeout if the picture is still being unpacked after a minute.
            await Task.Run(() => Assert.Throws<ConvertException>(() => Convert(input, "jpg"))).WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal([name], Entries);
            File.Delete(input);
        }
    }

    [Fact]
    public void ImageMagicksOwnLimitsAreInForce()
    {
        // The second line of defence, should a decoder ever be reached that shouldn't be.
        var drawing = Encoding.ASCII.GetBytes("push graphic-context\nviewbox 0 0 8 8\nfill red\nrectangle 0,0 8,8\npop graphic-context\n");
        var refused = Assert.ThrowsAny<MagickException>(() => new MagickImage(drawing, MagickFormat.Mvg));
        Assert.Contains("policy", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.ThrowsAny<MagickException>(() => new MagickImage(MagickColors.Red, 200_000, 4));
    }

    [Fact]
    public void ALargeButRealImageStillConverts()
    {
        // 12,000 × 9,000 is a 108-megapixel phone photo.
        var input = Path.Combine(dir, "big.png");
        using (var img = new MagickImage(MagickColors.SteelBlue, 12_000, 9_000))
        using (var f = File.Create(input))
            img.Write(f, MagickFormat.Png);
        var out_ = ImageConv.Convert(input, Targets.Catalogue.First(t => t.Id == "jpg"), new ConvSettings { MaxSize = 512 }, new Runner());
        using var small = new MagickImage(File.ReadAllBytes(out_));
        Assert.Equal(512u, small.Width);
    }

    // Helpers

    private static byte[] HugePng(uint width, uint height)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            ms.Write(BigEndian((uint)data.Length));
            ms.Write(body);
            ms.Write(BigEndian(Crc32(body)));
        }
        Chunk("IHDR", [.. BigEndian(width), .. BigEndian(height), 8, 0, 0, 0, 0]);   // 8-bit grey
        using (var data = new MemoryStream())
        {
            using (var z = new ZLibStream(data, CompressionLevel.Optimal, leaveOpen: true)) z.Write(new byte[1 << 20]);
            Chunk("IDAT", data.ToArray());
        }
        Chunk("IEND", []);
        return ms.ToArray();
    }

    private static byte[] HugeBmp(int width, int height)
    {
        var b = new byte[54 + 1024];
        b[0] = (byte)'B';
        b[1] = (byte)'M';
        BitConverter.GetBytes(54).CopyTo(b, 10);        // pixel data offset
        BitConverter.GetBytes(40).CopyTo(b, 14);        // header size
        BitConverter.GetBytes(width).CopyTo(b, 18);
        BitConverter.GetBytes(height).CopyTo(b, 22);
        BitConverter.GetBytes((short)1).CopyTo(b, 26);  // planes
        BitConverter.GetBytes((short)24).CopyTo(b, 28); // bits per pixel
        return b;
    }

    private static byte[] BigEndian(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }
}

/// <summary>A web server on this PC that counts what reaches it. Nothing ever should.</summary>
internal sealed class Listener : IDisposable
{
    private readonly HttpListener http = new();
    private int requests;

    public string Url { get; }
    public int Requests => Volatile.Read(ref requests);

    public Listener()
    {
        for (var attempt = 0; ; attempt++)
        {
            var port = Random.Shared.Next(20000, 60000);
            Url = $"http://127.0.0.1:{port}/";
            http.Prefixes.Clear();
            http.Prefixes.Add(Url);
            try
            {
                http.Start();
                break;
            }
            catch (HttpListenerException) when (attempt < 20) { }
        }
        _ = Task.Run(async () =>
        {
            while (http.IsListening)
            {
                HttpListenerContext context;
                try { context = await http.GetContextAsync(); }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
                Interlocked.Increment(ref requests);
                context.Response.StatusCode = 404;
                context.Response.Close();
            }
        });
    }

    public void Dispose()
    {
        try { http.Stop(); }
        catch (ObjectDisposedException) { }
        http.Close();
    }
}
