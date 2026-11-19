using System.IO.Compression;
using System.Text;
using Xunit;

namespace Convert.Core.Tests;

/// <summary>
/// PDF → Word, OpenDocument and Rich Text, ported from the Mac app's PDFWordTests.swift. Each test
/// writes its own PDF into a fresh temporary folder, so nothing depends on files elsewhere.
/// </summary>
public sealed class PdfWordTests : IDisposable
{
    private readonly string dir = Paths.TempDir();

    public void Dispose() => Paths.TryDelete(dir);

    // Writing test PDFs

    /// <summary>A paragraph as a PDF lays it out: lines 14 points apart, all but the last one as wide as the widest.</summary>
    private static List<PdfLine> Paragraph(string[] lines, double top)
    {
        var widest = lines[..^1].Select(l => PdfMaker.Width(l, 12)).DefaultIfEmpty(0).Max();
        return lines.Select((s, i) => new PdfLine(s, top - i * 14) { Width = i < lines.Length - 1 ? widest : null }).ToList();
    }

    private string MakePdf(params IReadOnlyList<PdfLine>[] pages) => Save("sample.pdf", PdfMaker.Make(pages));

    private string Save(string name, byte[] pdf)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, pdf);
        return path;
    }

    // Converting and reading back

    private static Target TargetFor(string id) => Targets.Catalogue.Single(t => t.Id == id);

    private static async Task<string> ConvertTo(string input, string id, Runner? runner = null, Action<double>? progress = null)
    {
        var outputs = await PdfConv.ConvertAsync(input, TargetFor(id), new ConvSettings(), runner ?? new Runner(), progress ?? (_ => { }));
        return Assert.Single(outputs);
    }

    private static List<string> Paragraphs(string path) =>
        PdfDocReader.Read(path).Select(p => p.Text).Where(t => t.Length > 0).ToList();

    private static string[] Words(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private List<string> Files => Directory.GetFiles(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList()!;

    private static readonly string[] First =
    [
        "The quick brown fox jumps over the lazy dog and then keeps on running far",
        "through the quiet forest until the warm evening light slowly fades away",
        "into the night.",
    ];
    private static readonly string[] Second =
    [
        "A second paragraph starts here, after a gap, and it has a few more words",
        "to fill the line.",
    ];

    // Ported from the Mac

    [Fact]
    public async Task LinesBecomeParagraphsWithEveryWordInOrder()
    {
        var input = MakePdf([.. Paragraph(First, 700), .. Paragraph(Second, 640)]);
        var found = Paragraphs(await ConvertTo(input, "pdf-docx"));
        Assert.Equal(2, found.Count);
        Assert.Equal([Words(string.Join(" ", First)), Words(string.Join(" ", Second))], found.Select(Words));
    }

    [Fact]
    public async Task LargerHeadingIsItsOwnParagraph()
    {
        var heading = new PdfLine("Introduction", 720, Size: 20, Font: "Helvetica-Bold");
        var found = Paragraphs(await ConvertTo(MakePdf([heading, .. Paragraph(First, 700)]), "pdf-docx"));
        Assert.Equal("Introduction", found[0]);
        Assert.Equal(2, found.Count);
    }

    [Fact]
    public async Task HyphenAtLineEndJoinsWithoutSpace()
    {
        string[] lines =
        [
            "Every line of this paragraph is long enough to run across the page until exam-",
            "ple words follow on the next line.",
        ];
        var text = string.Join("\n", Paragraphs(await ConvertTo(MakePdf(Paragraph(lines, 700)), "pdf-docx")));
        Assert.Contains("exam-ple words", text);
        Assert.DoesNotContain("exam- ple", text);
    }

    [Fact]
    public async Task BulletsEachStartAParagraph()
    {
        string[] items = [$"• {First[0]}", $"• {First[1]}", $"• {Second[0]}"];
        var widest = items.Max(s => PdfMaker.Width(s, 12));
        var lines = items.Select((s, i) => new PdfLine(s, 700 - i * 14) { Width = widest }).ToList();
        var found = Paragraphs(await ConvertTo(MakePdf(lines), "pdf-docx"));
        Assert.Equal(3, found.Count);
        Assert.All(found, p => Assert.StartsWith("•", p));
    }

    [Fact]
    public async Task EachPageEndsAParagraphAndReportsProgress()
    {
        var input = MakePdf(Paragraph(First, 700), Paragraph(Second, 700));
        var seen = new List<double>();
        var found = Paragraphs(await ConvertTo(input, "pdf-docx", progress: v => { lock (seen) seen.Add(v); }));
        Assert.Equal([Words(string.Join(" ", First)), Words(string.Join(" ", Second))], found.Select(Words));
        Assert.Equal([0.5, 1.0], seen);
    }

    /// <summary>The Mac checks this off the main thread; here every conversion runs on the thread pool, as in the app.</summary>
    [Theory]
    [InlineData("pdf-docx", "PK")]
    [InlineData("pdf-odt", "PK")]
    [InlineData("pdf-rtf", @"{\rtf")]
    public async Task WritesEachFormat(string id, string magic)
    {
        var out_ = await ConvertTo(MakePdf(Paragraph(First, 700)), id);
        Assert.Equal("." + TargetFor(id).Ext, Path.GetExtension(out_));
        Assert.Equal(Words(string.Join(" ", First)), Words(string.Join(" ", Paragraphs(out_))));
        Assert.Equal(magic, Encoding.ASCII.GetString(File.ReadAllBytes(out_).AsSpan(0, magic.Length)));
    }

    [Theory]
    [InlineData("pdf-docx")]
    [InlineData("pdf-odt")]
    [InlineData("pdf-rtf")]
    public async Task KeepsBoldSizeAndColour(string id)
    {
        var heading = new PdfLine("Introduction", 720, Size: 20, Font: "Helvetica-Bold") { Color = (0.8, 0, 0) };
        var found = PdfDocReader.Read(await ConvertTo(MakePdf([heading, .. Paragraph(First, 690)]), id));

        var title = found[0].Runs[0];
        Assert.Equal("Introduction", title.Text);
        Assert.Equal(20, title.Size);
        Assert.True(title.Bold);
        Assert.Equal("CC0000", title.Color);
        Assert.Equal("Helvetica", title.Font);

        // Body text has no colour of its own: it comes out black.
        var body = found[1].Runs.Single(r => r.Text.Contains("quick"));
        Assert.Equal(12, body.Size);
        Assert.False(body.Bold);
        Assert.False(body.Italic);
        Assert.Equal("000000", body.Color);
    }

    [Fact]
    public async Task PdfWithoutTextThrowsAndLeavesNoFile()
    {
        var input = MakePdf([[]]);
        var e = await Assert.ThrowsAsync<ConvertException>(() => ConvertTo(input, "pdf-docx"));
        Assert.Equal("This PDF contains no extractable text", e.Message);
        Assert.Equal(["sample.pdf"], Files);
    }

    [Fact]
    public async Task StoppedConversionLeavesNoFile()
    {
        var runner = new Runner();
        runner.Cancel();
        await Assert.ThrowsAsync<StoppedException>(() => ConvertTo(MakePdf(Paragraph(First, 700)), "pdf-rtf", runner));
        Assert.Equal(["sample.pdf"], Files);
    }

    [Fact]
    public async Task PasswordProtectedPdfThrows()
    {
        var input = Save("locked.pdf", PdfMaker.Make([[new PdfLine("Hidden", 100)]], password: "secret"));
        // A real encrypted file: with its password, it reads.
        using (var doc = UglyToad.PdfPig.PdfDocument.Open(input, new UglyToad.PdfPig.ParsingOptions { Password = "secret" }))
            Assert.Equal("Hidden", doc.GetPage(1).Text);
        var e = await Assert.ThrowsAsync<ConvertException>(() => ConvertTo(input, "pdf-docx"));
        Assert.Equal("This PDF is password-protected", e.Message);
        Assert.Equal(["locked.pdf"], Files);
    }

    [Fact]
    public async Task UnreadableFileThrows()
    {
        var input = Path.Combine(dir, "broken.pdf");
        File.WriteAllText(input, "not a pdf");
        var e = await Assert.ThrowsAsync<ConvertException>(() => ConvertTo(input, "pdf-odt"));
        Assert.Equal("Couldn't open this PDF", e.Message);
    }

    // Windows only: the writers are Convert's own

    [Theory]
    [InlineData("pdf-docx")]
    [InlineData("pdf-odt")]
    [InlineData("pdf-rtf")]
    public async Task ScannedPdfHasNoText(string id)
    {
        var input = Save("scan.pdf", PdfMaker.Make([[]], image: true));
        var e = await Assert.ThrowsAsync<ConvertException>(() => ConvertTo(input, id));
        Assert.Equal("This PDF contains no extractable text", e.Message);
        Assert.Equal(["scan.pdf"], Files);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StopPartwayLeavesNoFile(int afterPages)
    {
        var input = MakePdf(Paragraph(First, 700), Paragraph(Second, 700));
        var runner = new Runner();
        var pages = 0;
        await Assert.ThrowsAsync<StoppedException>(() => ConvertTo(input, "pdf-odt", runner, _ =>
        {
            if (++pages == afterPages) runner.Cancel();
        }));
        Assert.Equal(["sample.pdf"], Files);
    }

    [Fact]
    public async Task NeverOverwrites()
    {
        var input = MakePdf(Paragraph(First, 700));
        File.WriteAllText(Path.Combine(dir, "sample.docx"), "mine");
        var out_ = await ConvertTo(input, "pdf-docx");
        Assert.Equal("sample 2.docx", Path.GetFileName(out_));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(dir, "sample.docx")));
    }

    [Theory]
    [InlineData("pdf-docx")]
    [InlineData("pdf-odt")]
    [InlineData("pdf-rtf")]
    public async Task KeepsItalicFontAndSpacing(string id)
    {
        PdfLine[] lines =
        [
            new("Plain words first.", 700),
            new("Then slanted words in another font.", 640, Font: "Times-Italic"),
            new("Bold and slanted.", 580, Size: 14, Font: "Helvetica-BoldOblique") { Color = (0, 0, 0.6) },
        ];
        var found = PdfDocReader.Read(await ConvertTo(MakePdf(lines), id));
        Assert.Equal(["Plain words first.", "Then slanted words in another font.", "Bold and slanted."], found.Select(p => p.Text));
        var times = Assert.Single(found[1].Runs);
        Assert.Equal(("Times", false, true), (times.Font, times.Bold, times.Italic));
        var both = Assert.Single(found[2].Runs);
        Assert.Equal(("Helvetica", true, true, 14.0, "000099"), (both.Font, both.Bold, both.Italic, both.Size, both.Color));
        // Half the text size after each paragraph.
        Assert.Equal([6.0, 6.0, 7.0], found.Select(p => p.SpaceAfter));
    }

    [Theory]
    [InlineData("pdf-docx")]
    [InlineData("pdf-odt")]
    [InlineData("pdf-rtf")]
    public async Task CentresASingleCentredLine(string id)
    {
        var title = "A Title";
        // Centred over the text below it, which is what counts (not the paper).
        var x = 72 + (First[..^1].Max(l => PdfMaker.Width(l, 12)) - PdfMaker.Width(title, 16)) / 2;
        var input = MakePdf([new PdfLine(title, 740, x, 16), .. Paragraph(First, 700)]);
        var found = PdfDocReader.Read(await ConvertTo(input, id));
        Assert.Equal(2, found.Count);
        Assert.True(found[0].Centred);
        Assert.False(found[1].Centred);
    }

    [Fact]
    public async Task NearWhiteTextBecomesBlack()
    {
        var input = MakePdf([new PdfLine("White on a dark picture", 700) { Color = (1, 1, 1) }, new PdfLine("Grey stays", 640) { Color = (0.5, 0.5, 0.5) }]);
        var found = PdfDocReader.Read(await ConvertTo(input, "pdf-docx"));
        Assert.Equal(["000000", "808080"], found.Select(p => Assert.Single(p.Runs).Color));
    }

    [Fact]
    public async Task RotatedPageReadsLeftToRight()
    {
        // Landscape: 792 wide and 612 high as shown.
        var input = Save("sample.pdf", PdfMaker.Make([[.. Paragraph(First, 500), .. Paragraph(Second, 440)]], rotate: 90));
        var found = Paragraphs(await ConvertTo(input, "pdf-rtf"));
        Assert.Equal([Words(string.Join(" ", First)), Words(string.Join(" ", Second))], found.Select(Words));
    }

    [Fact]
    public async Task RightToLeftLineStaysOneParagraph()
    {
        var input = MakePdf([new PdfLine("shalom olam and more words of a line", 700) { RightToLeft = true }, new PdfLine("Next", 640)]);
        var found = Paragraphs(await ConvertTo(input, "pdf-docx"));
        Assert.Equal(["shalom olam and more words of a line", "Next"], found);
    }

    [Fact]
    public async Task OdtMimetypeComesFirstAndStored()
    {
        var bytes = File.ReadAllBytes(await ConvertTo(MakePdf(Paragraph(First, 700)), "pdf-odt"));
        const string mime = "application/vnd.oasis.opendocument.text";
        // The local file header of the zip's first entry: signature, flags, method, sizes, name, extra field.
        Assert.Equal(0x04034b50u, BitConverter.ToUInt32(bytes, 0));
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 6) & 8);   // Sizes in the header, no data descriptor after.
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 8));        // Stored.
        Assert.Equal((uint)mime.Length, BitConverter.ToUInt32(bytes, 18));
        Assert.Equal((uint)mime.Length, BitConverter.ToUInt32(bytes, 22));
        Assert.Equal(8, BitConverter.ToUInt16(bytes, 26));
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 28));       // No extra field.
        Assert.Equal("mimetype" + mime, Encoding.ASCII.GetString(bytes, 30, 8 + mime.Length));
        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.Equal("mimetype", zip.Entries[0].FullName);
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("odt")]
    [InlineData("rtf")]
    public void WritersEscapeWhatTheFormatCantHold(string ext)
    {
        var style = new TextStyle("Fish & <Chips>", 11, false, false, Rgb.Black);
        var text = "a & b < c > d \"e\" {f} \\g\tTab  two\u0001\uFFFF\uD800 é 中文 😀";
        var path = Path.Combine(dir, "out." + ext);
        using (var f = File.Create(path)) DocWriter.Write(f, ext, [new TextParagraph([new TextRun(text, style)], 6, false)]);
        var found = Assert.Single(PdfDocReader.Read(path));
        Assert.Equal("a & b < c > d \"e\" {f} \\g\tTab  two é 中文 😀", found.Text);
        Assert.Equal("Fish & <Chips>", Assert.Single(found.Runs).Font);
    }
}
