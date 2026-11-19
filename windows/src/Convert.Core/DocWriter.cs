using System.Globalization;
using System.Text;

namespace Convert.Core;

/// <summary>A colour as documents store it: 8 bits per channel, sRGB.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = new(0, 0, 0);
    public string Hex => $"{R:X2}{G:X2}{B:X2}";
}

/// <summary>How a run of text looks: font family (without "Bold" and the like), size in points, colour.</summary>
public sealed record TextStyle(string Font, double Size, bool Bold, bool Italic, Rgb Color);

public sealed record TextRun(string Text, TextStyle Style);

/// <summary>A paragraph: its runs, the space after it in points, and whether it's centred.</summary>
public sealed record TextParagraph(IReadOnlyList<TextRun> Runs, double SpaceAfter, bool Centred)
{
    public string Text => string.Concat(Runs.Select(r => r.Text));
}

/// <summary>
/// Writes paragraphs of styled text as Word (DOCX), OpenDocument (ODT) or Rich Text (RTF). Just what
/// PDF → text needs: paragraphs with alignment and spacing, runs with font, size, bold, italic and colour.
/// The Mac app gets these from AppKit; Windows has nothing built in that writes them.
/// </summary>
public static partial class DocWriter
{
    public static void Write(Stream stream, string ext, IReadOnlyList<TextParagraph> paragraphs)
    {
        switch (ext)
        {
            case "docx": WriteDocx(stream, paragraphs); break;
            case "odt": WriteOdt(stream, paragraphs); break;
            case "rtf": WriteRtf(stream, paragraphs); break;
            default: throw new ArgumentException($"can't write .{ext}", nameof(ext));
        }
    }

    /// <summary>
    /// Text for XML content or an attribute value. Characters XML 1.0 can't hold (most control
    /// characters, U+FFFE, U+FFFF, half a surrogate pair) are left out: a parser would refuse the file.
    /// </summary>
    internal static string Xml(string text) =>
        Valid(text).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>The text without what XML 1.0 can't hold.</summary>
    internal static string Valid(string text)
    {
        var s = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\t' or '\n' or '\r': s.Append(c); break;
                case < ' ' or '￾' or '￿': break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) s.Append(c).Append(text[++i]);
                    else if (!char.IsSurrogate(c)) s.Append(c);
                    break;
            }
        }
        return s.ToString();
    }

    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The size in half points, as Word and RTF store it, within what Word accepts.</summary>
    private static int HalfPoints(double size) => Math.Clamp((int)Math.Round(size * 2, MidpointRounding.AwayFromZero), 2, 3276);

    private static int Twips(double points) => (int)Math.Round(points * 20, MidpointRounding.AwayFromZero);

    private static void Entry(System.IO.Compression.ZipArchive zip, string name, string text,
        System.IO.Compression.CompressionLevel level = System.IO.Compression.CompressionLevel.Optimal)
    {
        using var w = new StreamWriter(zip.CreateEntry(name, level).Open(), new UTF8Encoding(false));
        w.Write(text);
    }
}
