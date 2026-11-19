using Convert.Core;
using ImageMagick;
using Xunit;

namespace Convert.Core.Tests;

/// <summary>Files that aren't what a converter expects: empty ones, very long paths, names a tool might misread.</summary>
public sealed class OddFilesTests : IDisposable
{
    private readonly TestFolder dir = new();

    public void Dispose() => dir.Dispose();

    private static Task<List<string>> Run(string input, string id) =>
        Converter.ConvertAsync(input, Targets.Catalogue.Single(t => t.Id == id), new ConvSettings(), new Runner(), _ => { });

    private static byte[] PngBytes()
    {
        using var img = new MagickImage(MagickColors.Coral, 12, 12);
        return img.ToByteArray(MagickFormat.Png);
    }

    [Theory]
    [InlineData("empty.png", "jpg")]
    [InlineData("empty.jpg", "png")]
    [InlineData("empty.heic", "png")]
    [InlineData("empty.pdf", "pdf-png")]
    [InlineData("empty.pdf", "pdf-txt")]
    [InlineData("empty.pdf", "pdf-docx")]
    [InlineData("empty.ttf", "font-woff2")]
    [InlineData("empty.woff2", "font-desktop")]
    [InlineData("empty.wav", "mp3")]
    [InlineData("empty.mp4", "gif")]
    public async Task AnEmptyFileIsAnErrorThatSaysSo(string name, string id)
    {
        if (Targets.Catalogue.Single(t => t.Id == id).Group is OutGroup.Audio or OutGroup.Video && Tools.FFmpeg == null) return;
        var input = dir.Write(name, Array.Empty<byte>());
        var error = await Assert.ThrowsAsync<ConvertException>(() => Run(input, id));
        // A sentence for the user, not an exception's own words.
        Assert.DoesNotContain("Parameter", error.Message);
        Assert.DoesNotContain("Exception", error.Message);
        Assert.Equal([name], dir.Contents);
    }

    [Fact]
    public async Task AnEmptyTextFileIsAnEmptyDocument()
    {
        var input = dir.Write("empty.txt", "");
        Assert.Equal("empty.html", Path.GetFileName((await Run(input, "html")).Single()));
    }

    /// <summary>A folder whose path is longer than the 260 characters old Windows programs stop at.</summary>
    private string DeepFolder()
    {
        var path = dir.Path;
        while (path.Length < 280) path = Path.Combine(path, "a folder with a rather long name " + path.Length);
        return Directory.CreateDirectory(path).FullName;
    }

    [Fact]
    public async Task AVeryLongPathConverts()
    {
        var deep = DeepFolder();
        var png = Path.Combine(deep, "picture.png");
        File.WriteAllBytes(png, PngBytes());
        Assert.Equal(Path.Combine(deep, "picture.jpg"), (await Run(png, "jpg")).Single());

        var md = Path.Combine(deep, "notes.md");
        File.WriteAllText(md, "# Notes\n\nSome text.\n");
        Assert.Equal(Path.Combine(deep, "notes.html"), (await Run(md, "html")).Single());

        var pdf = TestPdf.Write(Path.Combine(deep, "doc.pdf"), (200, 200));
        Assert.Equal(Path.Combine(deep, "doc.png"), (await Run(pdf, "pdf-png")).Single());
        Assert.Equal(Path.Combine(deep, "doc.txt"), (await Run(pdf, "pdf-txt")).Single());
    }

    [Fact]
    public async Task AVeryLongPathConvertsWithFfmpegToo()
    {
        if (Tools.FFmpeg == null) return;
        var wav = Path.Combine(DeepFolder(), "tone.wav");
        File.WriteAllBytes(wav, TestMedia.Wav());
        var mp3 = (await Run(wav, "mp3")).Single();
        Assert.True(new FileInfo(mp3).Length > 0);
    }

    [Theory]
    [InlineData("-leading dash.wav")]
    [InlineData("100%done %d %03d %s.wav")]
    [InlineData("it's 'quoted' & (odd) [1] @x #y;z.wav")]
    [InlineData("naïve café – Zürich 東京.wav")]
    public async Task NamesFfmpegMightMisreadAreJustNames(string name)
    {
        if (Tools.FFmpeg == null) return;
        var wav = dir.Write(name, TestMedia.Wav());
        var mp3 = (await Run(wav, "mp3")).Single();
        Assert.Equal(Path.ChangeExtension(wav, ".mp3"), mp3);
        Assert.True(new FileInfo(mp3).Length > 0);
        Assert.Equal(new[] { name, Path.GetFileName(mp3) }.Order(StringComparer.Ordinal), dir.Contents);
    }
}
