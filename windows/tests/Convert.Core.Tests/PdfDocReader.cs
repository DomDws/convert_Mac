using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Convert.Core.Tests;

internal sealed record ReadRun(string Text, string Font, double Size, bool Bold, bool Italic, string Color);

internal sealed record ReadParagraph(List<ReadRun> Runs, double SpaceAfter, bool Centred)
{
    public string Text => string.Concat(Runs.Select(r => r.Text));
}

/// <summary>
/// Reads what <see cref="DocWriter"/> writes back into paragraphs and runs, independently of it:
/// DOCX and ODT by unzipping and parsing the XML (which fails on malformed XML), RTF with a small
/// parser of its own.
/// </summary>
internal static class PdfDocReader
{
    public static List<ReadParagraph> Read(string path) => Path.GetExtension(path) switch
    {
        ".docx" => Docx(path),
        ".odt" => Odt(path),
        ".rtf" => Rtf(File.ReadAllText(path, Encoding.ASCII)),
        var ext => throw new ArgumentException($"can't read {ext}"),
    };

    private static XDocument Part(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidDataException($"{name} is missing");
        using var s = entry.Open();
        return XDocument.Load(s);
    }

    // DOCX

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static List<ReadParagraph> Docx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        // The parts every reader needs, each well-formed XML.
        var types = Part(zip, "[Content_Types].xml");
        if (!types.ToString().Contains("/word/document.xml")) throw new InvalidDataException("document.xml has no content type");
        Part(zip, "_rels/.rels");
        Part(zip, "word/_rels/document.xml.rels");
        Part(zip, "word/styles.xml");
        var doc = Part(zip, "word/document.xml");
        static bool On(XElement? e) => e != null && (string?)e.Attribute(W + "val") is null or "1" or "true" or "on";
        return doc.Descendants(W + "p").Select(p =>
        {
            var pPr = p.Element(W + "pPr");
            var after = (double?)pPr?.Element(W + "spacing")?.Attribute(W + "after") ?? 0;
            var centred = (string?)pPr?.Element(W + "jc")?.Attribute(W + "val") == "center";
            var runs = p.Elements(W + "r").Select(r =>
            {
                var rPr = r.Element(W + "rPr");
                var text = string.Concat(r.Elements().Select(e => e.Name == W + "t" ? e.Value : e.Name == W + "tab" ? "\t" : ""));
                return new ReadRun(text,
                    (string?)rPr?.Element(W + "rFonts")?.Attribute(W + "ascii") ?? "",
                    ((double?)rPr?.Element(W + "sz")?.Attribute(W + "val") ?? 24) / 2,
                    On(rPr?.Element(W + "b")), On(rPr?.Element(W + "i")),
                    ((string?)rPr?.Element(W + "color")?.Attribute(W + "val") ?? "000000").ToUpperInvariant());
            }).ToList();
            return new ReadParagraph(runs, after / 20, centred);
        }).ToList();
    }

    // ODT

    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";

    private static List<ReadParagraph> Odt(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        Part(zip, "META-INF/manifest.xml");
        Part(zip, "styles.xml");
        var content = Part(zip, "content.xml");
        var styles = content.Descendants(Style + "style").ToDictionary(s => (string)s.Attribute(Style + "name")!);
        static double Points(string? v) => v == null ? 0 : double.Parse(v.Replace("pt", ""), CultureInfo.InvariantCulture);
        return content.Descendants(Office + "text").Single().Elements(Text + "p").Select(p =>
        {
            var pProps = styles[(string)p.Attribute(Text + "style-name")!].Element(Style + "paragraph-properties");
            var runs = p.Elements(Text + "span").Select(span =>
            {
                var t = styles[(string)span.Attribute(Text + "style-name")!].Element(Style + "text-properties")!;
                var text = string.Concat(span.Nodes().Select(n => n switch
                {
                    XText x => x.Value,
                    XElement e when e.Name == Text + "s" => new string(' ', (int?)e.Attribute(Text + "c") ?? 1),
                    XElement e when e.Name == Text + "tab" => "\t",
                    _ => "",
                }));
                return new ReadRun(text, (string?)t.Attribute(Style + "font-name") ?? "", Points((string?)t.Attribute(Fo + "font-size")),
                    (string?)t.Attribute(Fo + "font-weight") == "bold", (string?)t.Attribute(Fo + "font-style") == "italic",
                    ((string?)t.Attribute(Fo + "color") ?? "#000000").TrimStart('#').ToUpperInvariant());
            }).ToList();
            return new ReadParagraph(runs, Points((string?)pProps?.Attribute(Fo + "margin-bottom")),
                (string?)pProps?.Attribute(Fo + "text-align") == "center");
        }).ToList();
    }

    // RTF

    private sealed record CharState(int Font = 0, int HalfPoints = 24, bool Bold = false, bool Italic = false, int Colour = 0, string Dest = "");

    private static List<ReadParagraph> Rtf(string rtf)
    {
        if (!rtf.StartsWith(@"{\rtf1", StringComparison.Ordinal)) throw new InvalidDataException("not RTF");
        var fonts = new Dictionary<int, string>();
        var colours = new List<string>();
        var (red, green, blue) = (0, 0, 0);
        var fontName = new StringBuilder();
        var stack = new Stack<CharState>();
        var state = new CharState();
        var paragraphs = new List<ReadParagraph>();
        var runs = new List<ReadRun>();
        var (spaceAfter, centred) = (0.0, false);
        var skip = 0;   // Characters after \u still to leave out (the fallback "?").

        void Add(string s)
        {
            if (skip > 0)
            {
                skip--;
                return;
            }
            switch (state.Dest)
            {
                case "fonttbl":
                    foreach (var c in s)
                    {
                        if (c == ';')
                        {
                            fonts[state.Font] = fontName.ToString().Trim();
                            fontName.Clear();
                        }
                        else fontName.Append(c);
                    }
                    return;
                case "colortbl":
                    // Colour 0, "auto", is the empty entry before the first ";": read as black here.
                    if (s == ";")
                    {
                        colours.Add($"{red:X2}{green:X2}{blue:X2}");
                        (red, green, blue) = (0, 0, 0);
                    }
                    return;
                case "skip":
                    return;
            }
            var look = new ReadRun("", fonts.GetValueOrDefault(state.Font, ""), state.HalfPoints / 2.0, state.Bold, state.Italic,
                state.Colour < colours.Count ? colours[state.Colour] : "000000");
            if (runs.Count > 0 && runs[^1] with { Text = "" } == look) runs[^1] = runs[^1] with { Text = runs[^1].Text + s };
            else runs.Add(look with { Text = s });
        }

        for (var i = 0; i < rtf.Length; i++)
        {
            var c = rtf[i];
            if (c == '{')
            {
                stack.Push(state);
                continue;
            }
            if (c == '}')
            {
                state = stack.Pop();
                continue;
            }
            if (c is '\r' or '\n') continue;
            if (c != '\\')
            {
                Add(c.ToString());
                continue;
            }
            var next = rtf[++i];
            if (next is '\\' or '{' or '}')
            {
                Add(next.ToString());
                continue;
            }
            if (next == '*')
            {
                state = state with { Dest = "skip" };
                continue;
            }
            if (next == '\'')
            {
                Add(((char)int.Parse(rtf.AsSpan(i + 1, 2), NumberStyles.HexNumber)).ToString());
                i += 2;
                continue;
            }
            var start = i;
            while (i < rtf.Length && char.IsAsciiLetter(rtf[i])) i++;
            var word = rtf[start..i];
            var numStart = i;
            if (i < rtf.Length && rtf[i] == '-') i++;
            while (i < rtf.Length && char.IsAsciiDigit(rtf[i])) i++;
            int? arg = i > numStart ? int.Parse(rtf[numStart..i], CultureInfo.InvariantCulture) : null;
            if (i >= rtf.Length || rtf[i] != ' ') i--;   // A space only ends the control word; anything else is read next.
            switch (word)
            {
                case "fonttbl" or "colortbl": state = state with { Dest = word }; break;
                case "stylesheet" or "info": state = state with { Dest = "skip" }; break;
                case "f": state = state with { Font = arg ?? 0 }; break;
                case "fs": state = state with { HalfPoints = arg ?? 24 }; break;
                case "b": state = state with { Bold = arg != 0 }; break;
                case "i": state = state with { Italic = arg != 0 }; break;
                case "cf": state = state with { Colour = arg ?? 0 }; break;
                case "red": red = arg ?? 0; break;
                case "green": green = arg ?? 0; break;
                case "blue": blue = arg ?? 0; break;
                case "plain": state = new CharState(Dest: state.Dest); break;
                case "pard": (spaceAfter, centred) = (0, false); break;
                case "sa": spaceAfter = (arg ?? 0) / 20.0; break;
                case "qc": centred = true; break;
                case "ql": centred = false; break;
                case "tab": Add("\t"); break;
                case "u":
                    var unit = arg ?? 0;
                    Add(((char)(unit < 0 ? unit + 65536 : unit)).ToString());
                    skip = 1;
                    break;
                case "par":
                    paragraphs.Add(new ReadParagraph(runs, spaceAfter, centred));
                    runs = [];
                    break;
            }
        }
        if (stack.Count != 0) throw new InvalidDataException("unbalanced braces");
        return paragraphs;
    }
}
