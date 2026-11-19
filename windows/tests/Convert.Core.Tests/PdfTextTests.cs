using Xunit;

namespace Convert.Core.Tests;

/// <summary>
/// PDF → Plain Text, ported from the Mac app's PDFTextTests.swift, and the paragraph rules of
/// <see cref="PdfText"/> on lines placed by hand.
/// </summary>
public sealed class PdfTextTests : IDisposable
{
    private readonly string dir = Paths.TempDir();

    public void Dispose() => Paths.TryDelete(dir);

    private string MakePdf(string? text = null)
    {
        var path = Path.Combine(dir, "sample.pdf");
        File.WriteAllBytes(path, PdfMaker.Make([text == null ? [] : [new PdfLine(text, 100, 20)]]));
        return path;
    }

    private static Target TargetFor(string id) => Targets.Catalogue.Single(t => t.Id == id);

    [Fact]
    public async Task GraphicsOnlyPdfDoesNotCreateTextFile()
    {
        var input = MakePdf();
        var e = await Assert.ThrowsAsync<ConvertException>(() =>
            PdfConv.ConvertAsync(input, TargetFor("pdf-txt"), new ConvSettings(), new Runner(), _ => { }));
        Assert.Equal("This PDF contains no extractable text", e.Message);
        Assert.False(File.Exists(Path.ChangeExtension(input, "txt")));
    }

    [Fact]
    public async Task ExportsSelectableText()
    {
        var outputs = await PdfConv.ConvertAsync(MakePdf("Sample text"), TargetFor("pdf-txt"), new ConvSettings(), new Runner(), _ => { });
        Assert.Equal("Sample text", File.ReadAllText(Assert.Single(outputs)).Trim());
    }

    // Paragraph rules, on lines placed by hand (x, baseline y and width in points, y growing upward)

    private static readonly TextStyle Body = new("Helvetica", 12, false, false, Rgb.Black);
    private static readonly Box Letter = new(0, 0, 612, 792);

    private static PdfText.Line L(string text, double x, double y, double width, double size = 12, bool spread = false) => new()
    {
        Box = new Box(x, y - 0.2 * size, x + width, y + 0.75 * size),
        Runs = [new TextRun(text, Body with { Size = size })],
        Text = text,
        Size = size,
        Spread = spread,
    };

    private static List<string> Paragraphs(params PdfText.Line[] lines) =>
        PdfText.Paragraphs(lines, Letter).Select(p => p.Text).ToList();

    [Fact]
    public void LineEndingEarlyEndsTheParagraph()
    {
        var found = Paragraphs(
            L("a full line of text that runs all the way", 72, 700, 460),
            L("short end.", 72, 686, 60),
            L("Next paragraph without any gap above it", 72, 672, 460),
            L("and its last line.", 72, 658, 100));
        Assert.Equal(["a full line of text that runs all the way short end.", "Next paragraph without any gap above it and its last line."], found);
    }

    [Fact]
    public void FirstLineIndentStartsAParagraph()
    {
        var found = Paragraphs(
            L("one full line of a paragraph that is long", 72, 700, 460),
            L("ends here, full width too, like the first", 72, 686, 460),
            L("Indented start of the next one, also long", 90, 672, 442),
            L("and the rest of it.", 72, 658, 100));
        Assert.Equal(2, found.Count);
        Assert.StartsWith("Indented", found[1]);
    }

    [Fact]
    public void ParagraphGoesOnInTheNextColumn()
    {
        var found = Paragraphs(
            L("left column line one, full width", 72, 700, 220),
            L("left column line two, full width", 72, 686, 220),
            L("right column carries on the same", 320, 700, 220),
            L("sentence to its end.", 320, 686, 120));
        Assert.Equal(["left column line one, full width left column line two, full width right column carries on the same sentence to its end."], found);
    }

    [Fact]
    public void NumberedItemsAreParagraphsButADateIsNot()
    {
        var list = Paragraphs(
            L("1. The first item of a list, long enough to wrap", 72, 700, 460),
            L("onto a second line that hangs under the text.", 86, 686, 300),
            L("2. The second item.", 72, 672, 120),
            L("3. The third item.", 72, 658, 120));
        Assert.Equal(3, list.Count);
        Assert.StartsWith("2.", list[1]);

        var sentence = Paragraphs(
            L("The meeting was moved several times and is now on", 72, 700, 460),
            L("31. Dezember in the afternoon, which suits everyone", 72, 686, 460),
            L("well enough.", 72, 672, 80));
        Assert.Single(sentence);
    }

    [Fact]
    public void BulletsSpreadLinesAndSizeChangesBreak()
    {
        Assert.Equal(3, Paragraphs(
            L("Heading", 72, 720, 100, 18),
            L("• one item that is long enough to be a full line", 72, 700, 460),
            L("• another item that is also a whole line wide", 72, 686, 460)).Count);
        // A table row: its cells far apart.
        Assert.Equal(2, Paragraphs(
            L("Name Price", 72, 700, 460, spread: true),
            L("Apples 3.20", 72, 686, 460, spread: true)).Count);
    }

    [Fact]
    public void TwoCentredTitleLinesStaySeparate()
    {
        var found = PdfText.Paragraphs(
        [
            L("A Long Title", 250, 740, 112),
            L("In Two Lines", 256, 726, 100),
            L("Body text that runs across the whole width of", 72, 700, 468),
            L("the column, so the page has a left and right.", 72, 686, 468),
        ], Letter);
        Assert.Equal(3, found.Count);
        Assert.True(found[0].Centred);
        Assert.True(found[1].Centred);
        Assert.False(found[2].Centred);
    }

    [Fact]
    public void JoinsLinesAsTheMacDoes()
    {
        static string Join(string a, string b) =>
            Assert.Single(Paragraphs(L(a, 72, 700, 460), L(b, 72, 686, 200))).Replace(a, "A|").Replace(b, "|B");
        Assert.Equal("A||B", Join("a line ending in a hyphen exam-", "ple"));
        Assert.Equal("A||B", Join("or in a soft hyphen exam­", "ple"));
        Assert.Equal("A| |B", Join("a range from 2-", "3 stays apart"));
        Assert.Equal("A||B", Join("日本語の文章は", "空白なしで続く"));
        Assert.Equal("A| |B", Join("plain words", "with a space"));
    }

    [Fact]
    public void ControlCharactersGoAndBreaksBecomeSpaces()
    {
        var p = Assert.Single(PdfText.Paragraphs([new PdfText.Line
        {
            Box = L("x", 72, 700, 100).Box,
            Runs = [new TextRun("one\r\ntwo\u0001\u001Fthree\tfour", Body)],
            Text = "one two three four",
        }], Letter));
        Assert.Equal("one twothree\tfour", p.Text);
        Assert.Equal(6, p.SpaceAfter);
    }

    [Theory]
    [InlineData("1. Item", true)]
    [InlineData("2) Item", true)]
    [InlineData("a) Item", true)]
    [InlineData("(iv) Item", true)]
    [InlineData("iv. Item", true)]
    [InlineData("3.1 Section", true)]
    [InlineData("3.1.2. Section", true)]
    [InlineData("1.", false)]
    [InlineData("1000. Item", false)]
    [InlineData("Hello world", false)]
    [InlineData("12:30 today", false)]
    public void Numbered(string text, bool expected) => Assert.Equal(expected, PdfText.IsNumbered(text));

    [Theory]
    [InlineData("• Item", true)]
    [InlineData("▪Item", true)]
    [InlineData("- Item", true)]
    [InlineData("– Item", true)]
    [InlineData("* Item", true)]
    [InlineData("-Item", false)]
    [InlineData("Item", false)]
    [InlineData("", false)]
    public void Bulleted(string text, bool expected) => Assert.Equal(expected, PdfText.IsBulleted(text));

    [Theory]
    [InlineData("ABCDEF+Arial-BoldMT", "Arial", true, false)]
    [InlineData("ArialMT", "Arial", false, false)]
    [InlineData("TimesNewRomanPS-ItalicMT", "Times New Roman", false, true)]
    [InlineData("TimesNewRomanPS-BoldItalicMT", "Times New Roman", true, true)]
    [InlineData("TimesNewRomanPSMT", "Times New Roman", false, false)]
    [InlineData("Helvetica-BoldOblique", "Helvetica", true, true)]
    [InlineData("Times-Roman", "Times", false, false)]
    [InlineData("Arial,BoldItalic", "Arial", true, true)]
    [InlineData("QWERTY+MinionPro-It", "Minion Pro", false, true)]
    [InlineData("MinionPro-SemiboldIt", "Minion Pro", true, true)]
    [InlineData("HelveticaNeue-Light", "Helvetica Neue", false, false)]
    [InlineData("CMR10", "CMR10", false, false)]
    [InlineData("", "Helvetica", false, false)]
    [InlineData(null, "Helvetica", false, false)]
    public void FontNames(string? pdfName, string family, bool bold, bool italic) =>
        Assert.Equal((family, bold, italic), PdfText.FontFace(pdfName));

    [Fact]
    public void FontsOwnDescriptionCounts() => Assert.Equal(("Calibri", true, true), PdfText.FontFace("Calibri", bold: true, italic: true));
}
