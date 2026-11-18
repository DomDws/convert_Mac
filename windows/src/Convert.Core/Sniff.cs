using System.Numerics;
using System.Text;

namespace Convert.Core;

/// <summary>
/// Looks at a file's first bytes to tell what it really is, for when the name can't be trusted:
/// <c>.ts</c> is both TypeScript and MPEG transport stream, and files get renamed to the wrong extension.
/// </summary>
/// <remarks>
/// It only speaks up when it's sure. Anything it doesn't recognise is left to the converters.
/// Mirrors Sources/Convert/Sniff.swift. The Explorer menu doesn't use it: it goes by the name alone,
/// to stay fast (see <see cref="MediaKinds"/>).
/// </remarks>
public static class Sniff
{
    /// <param name="Name">Reads after "its contents are …": "WAV audio", "a JPEG image".</param>
    /// <param name="Ext">The extension such a file usually has, when there's an obvious one to suggest.</param>
    /// <param name="Kinds">The kinds of file this can be converted as. MP4, for one, can hold just audio.</param>
    public sealed record Format(string Name, string? Ext, IReadOnlySet<MediaKind> Kinds);

    public const int HeaderLength = 4096;

    /// <summary>The first bytes of a file, or null if it can't be read (missing, a folder, no permission).</summary>
    public static byte[]? Header(string path)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[HeaderLength];
            var n = f.ReadAtLeast(buffer, HeaderLength, throwOnEndOfStream: false);
            return buffer[..n];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The kind of file this is, going by its name, and by its contents where the name is ambiguous:
    /// <c>.ts</c> is a video transport stream, or TypeScript.
    /// </summary>
    public static MediaKind? KindOf(string path)
    {
        if (Path.GetExtension(path).Equals(".ts", StringComparison.OrdinalIgnoreCase)
            && Header(path) is { Length: > 0 } head && !IsTransportStream(head) && LooksLikeText(head))
            return MediaKind.Document;
        return MediaKinds.Of(path);
    }

    // Checks

    /// <summary>
    /// Throws when the contents clearly belong to a different kind of file than the name says,
    /// e.g. audio renamed to <c>.jpg</c>, so the user gets told that instead of a vague decode error.
    /// </summary>
    public static void Verify(string path, MediaKind kind)
    {
        if (Header(path) is not { } head || FormatOf(head) is not { } actual || actual.Kinds.Contains(kind)) return;
        // Text may start with anything, including another format's magic letters.
        if (kind == MediaKind.Document && LooksLikeText(head)) return;
        var message = $"This isn't really a {Path.GetExtension(path).ToLowerInvariant()} file: its contents are {actual.Name}.";
        if (actual.Ext != null) message += $" Rename it to .{actual.Ext} and convert it again.";
        throw new ConvertException(message);
    }

    /// <summary>
    /// MPEG transport stream: a sync byte (0x47) at the start of every packet. Packets are 188 bytes;
    /// M2TS adds a 4-byte prefix (192), some broadcasts 16 bytes of error correction (204).
    /// </summary>
    public static bool IsTransportStream(ReadOnlySpan<byte> b)
    {
        foreach (var (first, size) in new[] { (0, 188), (4, 192), (0, 204) })
        {
            var syncs = 0;
            var all = true;
            for (var i = first; i < b.Length && syncs < 4; i += size, syncs++)
            {
                if (b[i] != 0x47) { all = false; break; }
            }
            if (all && syncs >= 2) return true;
        }
        return false;
    }

    /// <summary>Text has no control bytes apart from whitespace (and escape, for coloured logs). UTF-16 is told by its mark.</summary>
    public static bool LooksLikeText(ReadOnlySpan<byte> d)
    {
        if (d.StartsWith<byte>([0xFF, 0xFE]) || d.StartsWith<byte>([0xFE, 0xFF])) return true;
        foreach (var c in d)
        {
            if (c < 0x09 || c is >= 0x0E and <= 0x1A || c is >= 0x1C and <= 0x1F) return false;
        }
        return true;
    }

    // Formats

    private static readonly IReadOnlySet<MediaKind> Image = new HashSet<MediaKind> { MediaKind.Image };
    private static readonly IReadOnlySet<MediaKind> Audio = new HashSet<MediaKind> { MediaKind.Audio };
    private static readonly IReadOnlySet<MediaKind> Video = new HashSet<MediaKind> { MediaKind.Video };
    private static readonly IReadOnlySet<MediaKind> AudioVideo = new HashSet<MediaKind> { MediaKind.Audio, MediaKind.Video };
    private static readonly IReadOnlySet<MediaKind> Font = new HashSet<MediaKind> { MediaKind.Font };
    private static readonly IReadOnlySet<MediaKind> Document = new HashSet<MediaKind> { MediaKind.Document };

    /// <summary>The format these first bytes belong to, or null when they aren't one of the well-known ones below.</summary>
    public static Format? FormatOf(ReadOnlySpan<byte> d)
    {
        var b = d[..Math.Min(d.Length, 64)];
        var bytes = b.ToArray();
        bool At(int offset, params byte[] magic) => bytes.Length >= offset + magic.Length && bytes.AsSpan(offset, magic.Length).SequenceEqual(magic);
        bool Text(int offset, string text) => At(offset, Encoding.ASCII.GetBytes(text));
        bool Zero(int from, int to) => bytes.AsSpan(from, to - from + 1).IndexOfAnyExcept((byte)0) < 0;

        // Images
        if (At(0, 0xFF, 0xD8, 0xFF)) return new("a JPEG image", "jpg", Image);
        if (At(0, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)) return new("a PNG image", "png", Image);
        if (Text(0, "GIF87a") || Text(0, "GIF89a")) return new("a GIF image", "gif", Image);
        // Also most camera raw files.
        if (At(0, 0x49, 0x49, 0x2A, 0x00) || At(0, 0x4D, 0x4D, 0x00, 0x2A)) return new("a TIFF image", "tiff", Image);
        if (Text(0, "BM") && bytes.Length >= 18 && Zero(6, 9) && bytes[14] is 12 or 40 or 52 or 56 or 108 or 124 && Zero(15, 17))
            return new("a BMP image", "bmp", Image);

        // RIFF and IFF containers
        if (Text(0, "RIFF") || Text(0, "RF64"))
        {
            if (Text(8, "WAVE")) return new("WAV audio", "wav", Audio);
            if (Text(8, "AVI ")) return new("an AVI video", "avi", Video);
            if (Text(8, "WEBP")) return new("a WebP image", "webp", Image);
        }
        if (Text(0, "FORM") && (Text(8, "AIFF") || Text(8, "AIFC"))) return new("AIFF audio", "aiff", Audio);

        // MP4 family: the brand says what's inside. Unknown brands (camera raw, …) are left alone.
        if (Text(4, "ftyp") && bytes.Length >= 12)
        {
            var brand = Encoding.Latin1.GetString(bytes, 8, 4);
            if (brand is "heic" or "heix" or "heim" or "heis" or "hevc" or "hevx" or "hevm" or "hevs" or "mif1" or "msf1")
                return new("a HEIC image", "heic", Image);
            if (brand is "avif" or "avis") return new("an AVIF image", "avif", Image);
            if (brand == "qt  ") return new("a QuickTime movie", "mov", AudioVideo);
            if (brand.StartsWith("M4A", StringComparison.Ordinal) || brand.StartsWith("M4B", StringComparison.Ordinal))
                return new("M4A audio", "m4a", AudioVideo);
            if (brand is "isom" or "mp41" or "mp42" or "avc1" or "M4V " or "f4v " or "dash" or "mmp4" or "MSNV" or "av01"
                || new[] { "iso", "3gp", "3g2" }.Any(p => brand.StartsWith(p, StringComparison.Ordinal)))
                return new("an MP4 video", "mp4", AudioVideo);
            return null;
        }
        if (Text(4, "moov") || Text(4, "mdat") || Text(4, "wide")) return new("a QuickTime movie", "mov", AudioVideo);

        // Audio
        if (Text(0, "fLaC")) return new("FLAC audio", "flac", Audio);
        if (Text(0, "ID3") && bytes.Length >= 4 && bytes[3] is >= 2 and <= 4) return new("MP3 audio", "mp3", Audio);
        if (Text(0, "wvpk")) return new("WavPack audio", "wv", Audio);
        // An MPEG audio frame (MP3, or AAC in ADTS): 11 sync bits, then a valid bitrate and sample rate.
        if (bytes.Length >= 3 && bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0 && bytes[1] < 0xFE
            && (bytes[2] & 0xF0) != 0xF0 && (bytes[2] & 0x0C) != 0x0C)
            return new("MPEG audio (MP3 or AAC)", null, Audio);

        // Audio or video
        if (Text(0, "OggS")) return new("an Ogg file", "ogg", AudioVideo);
        if (At(0, 0x1A, 0x45, 0xDF, 0xA3)) return new("a Matroska or WebM file", "mkv", AudioVideo);
        if (At(0, 0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11)) return new("a Windows Media file", "wmv", AudioVideo);
        if (At(0, 0x46, 0x4C, 0x56, 0x01)) return new("a Flash video", "flv", Video);
        if (At(0, 0x00, 0x00, 0x01, 0xBA) || At(0, 0x00, 0x00, 0x01, 0xB3)) return new("an MPEG video", "mpg", Video);
        if (IsTransportStream(d)) return new("an MPEG transport stream", "ts", Video);

        // Fonts. A plain TrueType file starts with a version number, so its table count has to add up too.
        if (Text(0, "wOF2")) return new("a WOFF2 font", "woff2", Font);
        if (Text(0, "wOFF")) return new("a WOFF font", "woff", Font);
        if (Text(0, "OTTO")) return new("an OpenType font", "otf", Font);
        if (Text(0, "ttcf")) return new("a font collection", "ttc", Font);
        if ((At(0, 0x00, 0x01, 0x00, 0x00) || Text(0, "true")) && bytes.Length >= 12)
        {
            var tables = bytes[4] << 8 | bytes[5];
            var searchRange = bytes[6] << 8 | bytes[7];
            if (tables is > 0 and < 256 && searchRange == 16 << BitOperations.Log2((uint)tables))
                return new("a TrueType font", "ttf", Font);
        }

        // Documents. A PDF can also be behind an image type (Illustrator files), so it passes for both.
        if (Text(0, "%PDF-")) return new("a PDF", "pdf", new HashSet<MediaKind> { MediaKind.Pdf, MediaKind.Image });
        if (Text(0, "{\\rtf")) return new("a Rich Text document", "rtf", Document);
        if (At(0, 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1))
            return new("an old Microsoft Office document", null, new HashSet<MediaKind> { MediaKind.Document, MediaKind.Image });
        if (At(0, 0x50, 0x4B, 0x03, 0x04)) return new("a ZIP archive or Office document", null, Document);
        if (Text(0, "bplist00")) return new("a property list or web archive", null, Document);
        return null;
    }
}
