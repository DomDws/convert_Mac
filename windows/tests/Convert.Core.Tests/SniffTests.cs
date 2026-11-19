using System.Text;
using Convert.Core;
using Xunit;

namespace Convert.Core.Tests;

/// <summary>Telling what a file is from its contents: <c>.ts</c> (TypeScript or transport stream), and misnamed files.</summary>
public sealed class SniffTests : IDisposable
{
    private readonly TestFolder dir = new();

    public void Dispose() => dir.Dispose();

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    /// <summary><paramref name="packets"/> transport-stream packets of <paramref name="size"/> bytes, each starting with the sync byte after <paramref name="prefix"/> bytes.</summary>
    private static byte[] TransportStream(int packets = 4, int size = 188, int prefix = 0)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < packets; i++)
        {
            var packet = Enumerable.Repeat(unchecked((byte)(i * 31 + 1)), size).ToArray();
            packet[prefix] = 0x47;
            packet[prefix + 1] = 0x00;   // binary, never mistaken for text
            bytes.AddRange(packet);
        }
        return [.. bytes];
    }

    private static readonly byte[] Wav = [.. Ascii("RIFF"), 0x24, 0x08, 0, 0, .. Ascii("WAVEfmt "), 16, 0, 0, 0, 1, 0];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];
    private static byte[] Mp4(string brand) => [0, 0, 0, 0x20, .. Ascii("ftyp" + brand), 0, 0, 2, 0];

    // .ts

    [Fact]
    public void TypeScriptIsADocument()
    {
        var code = dir.Write("app.ts", "export const answer: number = 42;\nconsole.log(answer);\n");
        Assert.Equal(MediaKind.Document, Sniff.KindOf(code));
        // Starting with the sync byte's letter isn't enough to be a transport stream.
        var g = dir.Write("greeting.ts", "Greeter.hello();\n" + string.Concat(Enumerable.Repeat("// padding line\n", 40)));
        Assert.Equal(MediaKind.Document, Sniff.KindOf(g));
    }

    [Fact]
    public void TransportStreamIsVideo()
    {
        Assert.Equal(MediaKind.Video, Sniff.KindOf(dir.Write("clip.ts", TransportStream())));
        Assert.Equal(MediaKind.Video, Sniff.KindOf(dir.Write("bluray.ts", TransportStream(size: 192, prefix: 4))));
        Assert.True(Sniff.IsTransportStream(TransportStream(packets: 2)));
        Assert.False(Sniff.IsTransportStream(TransportStream(packets: 1)));
    }

    [Fact]
    public void UnreadableOrEmptyTsFallsBackToTheExtension()
    {
        Assert.Equal(MediaKind.Video, Sniff.KindOf(dir.Write("empty.ts", Array.Empty<byte>())));
        Assert.Equal(MediaKind.Video, Sniff.KindOf(dir.File("missing.ts")));
        // Binary that isn't a transport stream: still video, ffmpeg gets to decide.
        Assert.Equal(MediaKind.Video, Sniff.KindOf(dir.Write("odd.ts", [0x00, 0x01, 0x02, 0x03, 0xFF])));
    }

    [Fact]
    public void TheExplorerMenuGoesByTheNameAlone()
    {
        // It has to answer at once for any selection, so it never opens a file.
        Assert.Equal(MediaKind.Video, MediaKinds.Of(dir.Write("app.ts", "export const answer = 42;\n")));
    }

    [Fact]
    public async Task TypeScriptConvertsToPlainText()
    {
        var code = dir.Write("app.ts", "export const answer: number = 42;\n");
        var outputs = await Converter.ConvertAsync(code, Targets.ById("txt")!, new ConvSettings(), new Runner(), _ => { });
        Assert.Contains("answer: number = 42", File.ReadAllText(outputs.Single()));
    }

    // Misnamed files

    [Fact]
    public void AudioNamedAsImageSaysSo()
    {
        var fake = dir.Write("song.jpg", Wav);
        var error = Assert.Throws<ConvertException>(() => Sniff.Verify(fake, MediaKind.Image));
        Assert.Equal("This isn't really a .jpg file: its contents are WAV audio. Rename it to .wav and convert it again.", error.Message);
    }

    [Fact]
    public async Task ConversionReportsTheMismatchInsteadOfADecodeFailure()
    {
        var fake = dir.Write("song.JPG", Wav);
        var error = await Assert.ThrowsAsync<ConvertException>(() =>
            Converter.ConvertAsync(fake, Targets.ById("png")!, new ConvSettings(), new Runner(), _ => { }));
        Assert.Contains("isn't really a .jpg file", error.Message);
        Assert.Contains("WAV audio", error.Message);
        Assert.Equal(["song.JPG"], dir.Contents);
    }

    [Fact]
    public void OtherKindsAreCaughtToo()
    {
        Assert.Throws<ConvertException>(() => Sniff.Verify(dir.Write("a.mp3", Png), MediaKind.Audio));
        Assert.Throws<ConvertException>(() => Sniff.Verify(dir.Write("a.pdf", Png), MediaKind.Pdf));
        Assert.Throws<ConvertException>(() => Sniff.Verify(dir.Write("a.png", Mp4("isom")), MediaKind.Image));
        Assert.Throws<ConvertException>(() => Sniff.Verify(dir.Write("a.ttf", Png), MediaKind.Font));
        // A binary file posing as text.
        Assert.Throws<ConvertException>(() => Sniff.Verify(dir.Write("a.txt", Png), MediaKind.Document));
    }

    [Fact]
    public void MatchingAndUnknownContentsPass()
    {
        // Same kind under another name: the converters read these by content anyway.
        Sniff.Verify(dir.Write("photo.jpg", Png), MediaKind.Image);
        // MP4 can be audio only, or video.
        Sniff.Verify(dir.Write("song.m4a", Mp4("isom")), MediaKind.Audio);
        Sniff.Verify(dir.Write("clip.mp4", Mp4("M4A ")), MediaKind.Video);
        // Unknown brands (camera raw in an MP4-style container) and unknown bytes are left to the converters.
        Sniff.Verify(dir.Write("raw.cr3", Mp4("crx ")), MediaKind.Image);
        Sniff.Verify(dir.Write("mystery.jpg", [0x01, 0x02, 0x03, 0x04]), MediaKind.Image);
        Sniff.Verify(dir.Write("empty.jpg", Array.Empty<byte>()), MediaKind.Image);
        Sniff.Verify(dir.File("missing.jpg"), MediaKind.Image);
    }

    [Fact]
    public void TextThatStartsLikeAnotherFormatIsStillText()
    {
        Sniff.Verify(dir.Write("cars.txt", "BMW and Audi are car makers.\n"), MediaKind.Document);
        Sniff.Verify(dir.Write("tags.txt", "ID3 is how MP3 files carry their titles.\n"), MediaKind.Document);
        Sniff.Verify(dir.Write("riff.md", "RIFF is a container format; WAVE is one use of it.\n"), MediaKind.Document);
        Sniff.Verify(dir.Write("utf16.txt", [0xFF, 0xFE, 0x48, 0x00, 0x69, 0x00]), MediaKind.Document);
        Assert.Null(Sniff.FormatOf(Ascii("BMW and Audi are car makers.\n")));
        Assert.Null(Sniff.FormatOf([0xFF, 0xFE, 0x48, 0x00, 0x69, 0x00]));
    }

    [Fact]
    public void KnownFormats()
    {
        (byte[] Bytes, string Name, MediaKind[] Kinds)[] cases =
        [
            ([0xFF, 0xD8, 0xFF, 0xE0], "a JPEG image", [MediaKind.Image]),
            (Png, "a PNG image", [MediaKind.Image]),
            (Ascii("GIF89a"), "a GIF image", [MediaKind.Image]),
            (Wav, "WAV audio", [MediaKind.Audio]),
            ([.. Ascii("fLaC"), 0, 0, 0, 34], "FLAC audio", [MediaKind.Audio]),
            ([.. Ascii("ID3"), 4, 0, 0, 0, 0, 0, 0], "MP3 audio", [MediaKind.Audio]),
            ([0xFF, 0xFB, 0x90, 0x64], "MPEG audio (MP3 or AAC)", [MediaKind.Audio]),
            ([.. Ascii("OggS"), 0, 2], "an Ogg file", [MediaKind.Audio, MediaKind.Video]),
            ([0x1A, 0x45, 0xDF, 0xA3], "a Matroska or WebM file", [MediaKind.Audio, MediaKind.Video]),
            (Mp4("heic"), "a HEIC image", [MediaKind.Image]),
            (Mp4("qt  "), "a QuickTime movie", [MediaKind.Audio, MediaKind.Video]),
            (Ascii("%PDF-1.7\n"), "a PDF", [MediaKind.Pdf, MediaKind.Image]),
            (TransportStream(), "an MPEG transport stream", [MediaKind.Video]),
            ([.. Ascii("wOF2"), 0, 1, 0, 0], "a WOFF2 font", [MediaKind.Font]),
            ([.. Ascii("OTTO"), 0, 9, 0, 0x80, 0, 3, 0, 0x10], "an OpenType font", [MediaKind.Font]),
            ([0, 1, 0, 0, 0, 17, 1, 0, 0, 4, 0, 16], "a TrueType font", [MediaKind.Font]),
        ];
        foreach (var (bytes, name, kinds) in cases)
        {
            var format = Sniff.FormatOf(bytes);
            Assert.Equal(name, format?.Name);
            Assert.True(format!.Kinds.SetEquals(kinds), name);
        }
        // A version number alone isn't a font: the table count has to add up too.
        Assert.Null(Sniff.FormatOf([0, 1, 0, 0, 0, 17, 0, 0x40, 0, 4, 0, 16]));
    }

    [Fact]
    public void RealFontsAreRecognised()
    {
        foreach (var name in new[] { "arial.ttf", "cambria.ttc", "segoeui.ttf" })
        {
            var font = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), name);
            if (!File.Exists(font)) continue;
            Assert.Contains(MediaKind.Font, Sniff.FormatOf(Sniff.Header(font)!)!.Kinds);
            Sniff.Verify(font, MediaKind.Font);
        }
    }
}
