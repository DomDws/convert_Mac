using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Convert.Core;

/// <summary>A rectangle on a PDF page, in points, with y growing upward as in PDF.</summary>
public readonly record struct Box(double MinX, double MinY, double MaxX, double MaxY)
{
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
    public double MidX => (MinX + MaxX) / 2;
    public Box Union(Box b) => new(Math.Min(MinX, b.MinX), Math.Min(MinY, b.MinY), Math.Max(MaxX, b.MaxX), Math.Max(MaxY, b.MaxY));
}

/// <summary>
/// A PDF page's text as paragraphs, for PDF → Word, OpenDocument and Rich Text. A port of the Mac
/// app's <c>PDFText.swift</c>; the paragraph rules are the same, only where the lines come from differs
/// (PdfPig's letters here, PDFKit's lines there; see PdfText.Lines.cs).
/// <para>
/// A PDF only knows where each line of text sits on the page, not which lines form a paragraph.
/// So lines are joined back into paragraphs unless something marks a break: a gap clearly larger
/// than the page's usual line spacing, a change of text size (headings), a line ending early
/// although the next line's first word would have fit, a first-line indent, or a list marker.
/// </para>
/// </summary>
public static partial class PdfText
{
    /// <summary>One line of text: what it says, in which fonts, and where it sits on the page.</summary>
    public sealed class Line
    {
        public required Box Box { get; init; }
        public required IReadOnlyList<TextRun> Runs { get; init; }
        public string Text { get; init; } = "";
        /// <summary>The line's main text size: the size most of its letters have.</summary>
        public double Size { get; init; } = 12;
        /// <summary>Spread far wider than its text: an entry in a table of contents, a row of a table.</summary>
        public bool Spread { get; init; }
    }

    /// <summary>The lines' paragraphs. Empty when there are no lines.</summary>
    public static List<TextParagraph> Paragraphs(IReadOnlyList<Line> lines, Box page)
    {
        var out_ = new List<TextParagraph>();
        var layout = new Layout(lines, page);
        var start = 0;
        for (var i = 1; i < lines.Count; i++)
        {
            if (!layout.StartsParagraph(i, start)) continue;
            out_.Add(Paragraph(layout, start, i));
            start = i;
        }
        if (lines.Count > 0) out_.Add(Paragraph(layout, start, lines.Count));
        return out_;
    }

    // Paragraph breaks

    /// <summary>A page's lines with what's needed to judge them: line spacing and where the text is.</summary>
    public sealed class Layout
    {
        private readonly IReadOnlyList<Line> lines;
        public IReadOnlyList<Line> Lines => lines;
        /// <summary>The usual distance from one line to the next, per text size.</summary>
        private readonly Dictionary<int, double> pitch = [];
        /// <summary>Where the page's text runs from and to, or the page's edges when there's little text.</summary>
        private readonly double left, right;
        /// <summary>Per line, where lines like it end: the right edge of its column, or of its box of text.</summary>
        private readonly List<double> ends = [];

        public Layout(IReadOnlyList<Line> lines, Box page)
        {
            this.lines = lines;
            var textLeft = lines.Count > 0 ? lines.Min(l => l.Box.MinX) : page.MinX;
            var textRight = lines.Count > 0 ? lines.Max(l => l.Box.MaxX) : page.MaxX;
            var wide = textRight - textLeft > 0.5 * page.Width;
            left = wide ? textLeft : page.MinX;
            right = wide ? textRight : page.MaxX;
            // The lower quartile, so pages with many short paragraphs still measure the distance
            // between lines rather than the gaps between paragraphs.
            var drops = new Dictionary<int, List<double>>();
            for (var i = 1; i < lines.Count; i++)
            {
                var (p, c) = (lines[i - 1], lines[i]);
                if (Key(p.Size) != Key(c.Size)) continue;
                var drop = p.Box.MinY - c.Box.MinY;
                if (drop > 0 && drop < 3 * c.Size)
                {
                    if (!drops.TryGetValue(Key(c.Size), out var list)) drops[Key(c.Size)] = list = [];
                    list.Add(drop);
                }
            }
            foreach (var (key, list) in drops)
            {
                list.Sort();
                pitch[key] = list[list.Count / 4];
            }

            // Lines following one another at the usual spacing form a block. Where they fill it,
            // the block's right edge is where its lines end (in a narrow box beside a picture, say).
            // Where most stop well short (code, verse, short paragraphs), the page's other text of
            // that size shows where the column ends.
            var start = 0;
            for (var i = 0; i < lines.Count; i++)
            {
                if (i + 1 < lines.Count && Follows(lines[i], lines[i + 1])) continue;
                var block = lines.Skip(start).Take(i + 1 - start).ToList();
                var blockEnd = Reach(block.Select(l => l.Box.MaxX));
                var from = block.Min(l => l.Box.MinX);
                var middle = block.Select(l => l.Box.MaxX).Order().ElementAt(block.Count / 2);
                var filled = middle > from + 0.8 * (blockEnd - from);
                ends.AddRange(block.Select(l => filled ? blockEnd : PageEnd(l)));
                start = i + 1;
            }
        }

        /// <summary>Where the page's lines of this size that start no further left mostly end.</summary>
        private double PageEnd(Line line) =>
            Reach(lines.Where(o => Key(o.Size) == Key(line.Size) && o.Box.MinX > line.Box.MinX - 0.15 * line.Size
                                   && o.Box.MinX < line.Box.MaxX)
                       .Select(o => o.Box.MaxX));

        /// <summary>
        /// How far these lines reach, leaving out the furthest tenth: a line running into the
        /// margin, or across two columns, doesn't count.
        /// </summary>
        private static double Reach(IEnumerable<double> ends)
        {
            var sorted = ends.Order().ToList();
            return sorted.Count == 0 ? 0 : sorted[sorted.Count - 1 - sorted.Count / 10];
        }

        /// <summary>The line <paramref name="c"/> comes right below <paramref name="p"/>, in the same text size and at the usual distance.</summary>
        private bool Follows(Line p, Line c)
        {
            var drop = p.Box.MinY - c.Box.MinY;
            return Key(p.Size) == Key(c.Size) && drop > 0
                && drop <= (pitch.TryGetValue(Key(c.Size), out var usual) ? usual : 1.2 * c.Size) + 0.25 * c.Size;
        }

        /// <summary>Whether line <paramref name="i"/> begins a new paragraph; the current one began at line <paramref name="start"/>.</summary>
        public bool StartsParagraph(int i, int start)
        {
            var p = lines[i - 1];
            var c = lines[i];
            var size = Math.Max(p.Size, c.Size);
            if (Math.Abs(p.Size - c.Size) >= Math.Max(1, 0.1 * size) || p.Spread || c.Spread) return true;
            if (IsCentred(i - 1) && IsCentred(i)) return true;   // Lines of a title.

            // Further down than usual, or back up the page: a new paragraph. Except at the top of
            // the next column, where a paragraph can go on.
            var nextColumn = p.Box.MinY <= c.Box.MinY && c.Box.MinX > p.Box.MaxX - size;
            if (!Follows(p, c) && !nextColumn) return true;

            if (IsBulleted(c.Text)) return true;
            if (IsNumbered(c.Text) && (IsListItem(lines[start].Text) || Hangs(i))) return true;

            if (nextColumn)
            {
                // Indented compared with the column's next line.
                if (i + 1 < lines.Count && c.Box.MinX > lines[i + 1].Box.MinX + 0.6 * size) return true;
            }
            else
            {
                // Indented: a new paragraph. Unless it's a hanging indent: the second line of a list
                // item, or of a term and its explanation (were that first line short, the last check
                // still ends the paragraph).
                var hanging = i - 1 == start || IsListItem(lines[start].Text);
                if (c.Box.MinX > p.Box.MinX + 0.6 * size && !hanging) return true;
                // Back to the left after the second line: the end of a list item or an indented block.
                if (c.Box.MinX < p.Box.MinX - 0.6 * size && i - 1 > start) return true;
            }

            // The previous line ended early although this line's first word would have fit after it.
            return p.Box.MaxX + FirstWordWidth(c) < ends[i - 1] - 0.5 * size;
        }

        /// <summary>
        /// The line after <paramref name="i"/> is indented, as list items' following lines are
        /// ("31. Dezember" in the middle of a sentence isn't a list).
        /// </summary>
        private bool Hangs(int i) => i + 1 < lines.Count && lines[i + 1].Box.MinX > lines[i].Box.MinX + 0.6 * lines[i].Size;

        /// <summary>
        /// A short line in the middle of the page's text, starting neither at its left edge nor
        /// where the lines around it start (those would be a narrow column that happens to be centred).
        /// </summary>
        public bool IsCentred(int i)
        {
            var line = lines[i];
            var box = line.Box;
            var aligned = new[] { i - 1, i + 1 }.Any(j => j >= 0 && j < lines.Count && Math.Abs(lines[j].Box.MinX - box.MinX) < 0.15 * line.Size);
            return !aligned && box.MinX > left + line.Size && box.Width < 0.7 * (right - left)
                && Math.Abs(box.MidX - (left + right) / 2) < Math.Max(3, 0.5 * line.Size);
        }

        /// <summary>Roughly how wide the line's first word is, plus a space before it.</summary>
        private static double FirstWordWidth(Line line)
        {
            var chars = Characters(line.Text);
            var word = chars.Count > 0 && IsCjk(chars[0]) ? 1 : chars.TakeWhile(ch => !IsWhite(ch)).Count();
            return line.Box.Width * (word + 1) / Math.Max(chars.Count, 1);
        }
    }

    /// <summary>Text sizes that differ by less than half a point count as the same.</summary>
    private static int Key(double size) => (int)Math.Round(size * 2, MidpointRounding.AwayFromZero);

    public static bool IsListItem(string text) => IsBulleted(text) || IsNumbered(text);

    /// <summary>Starts with a bullet, or a dash or star followed by text.</summary>
    public static bool IsBulleted(string text)
    {
        if (text.Length == 0) return false;
        if ("•◦▪▫‣⁃●○■□►▶✓✔·".Contains(text[0])) return true;
        return new[] { "- ", "– ", "— ", "* " }.Any(text.StartsWith);
    }

    /// <summary>Starts with a number or letter like "1." "2)" "a)" "(iv)" "3.1", followed by text.</summary>
    public static bool IsNumbered(string text)
    {
        var token = new string([.. text.TakeWhile(c => !char.IsWhiteSpace(c))]);
        return token.Length < text.Length && NumberedToken().IsMatch(token);
    }

    [GeneratedRegex(@"^(\(?([0-9]{1,3}|[A-Za-z]|[ivxIVX]{2,4})[.)]|\([0-9A-Za-z]{1,4}\)|[0-9]{1,3}(\.[0-9]{1,3})+\.?)$")]
    private static partial Regex NumberedToken();

    // Writing a paragraph

    /// <summary>Lines <paramref name="start"/> up to <paramref name="end"/> as one paragraph, joined with spaces.</summary>
    private static TextParagraph Paragraph(Layout layout, int start, int end)
    {
        var lines = layout.Lines;
        var runs = new List<TextRun>();
        void Add(string text, TextStyle style)
        {
            if (text.Length == 0) return;
            // Its own colour in a form documents store; neighbours that now look alike become one run.
            style = style with { Color = Readable(style.Color) };
            if (runs.Count > 0 && runs[^1].Style == style) runs[^1] = runs[^1] with { Text = runs[^1].Text + text };
            else runs.Add(new TextRun(text, style));
        }
        for (var i = start; i < end; i++)
        {
            if (i > start && runs.Count > 0)
                Add(Joiner(lines[i - 1], lines[i]), runs[^1].Style);
            // A break inside a line becomes a space; other control characters can't go in a document.
            foreach (var run in lines[i].Runs)
                Add(Controls().Replace(Breaks().Replace(run.Text, " "), ""), run.Style);
        }
        var centred = end - start == 1 && layout.IsCentred(start);
        return new TextParagraph(runs, lines[start].Size * 0.5, centred);
    }

    [GeneratedRegex(@"[\r\n  ]+")]
    private static partial Regex Breaks();

    [GeneratedRegex(@"[\x00-\x08\x0B-\x1F]")]
    private static partial Regex Controls();

    /// <summary>
    /// What goes between two lines of a paragraph: usually a space. Nothing after a hyphen (it
    /// stays), or between Chinese or Japanese characters.
    /// </summary>
    private static string Joiner(Line p, Line c)
    {
        var before = Characters(p.Text);
        var after = Characters(c.Text);
        if (before.Count == 0 || after.Count == 0) return " ";
        var last = before[^1];
        if (last is "-" or "‐" or "­" && before.Count > 1 && IsLetter(before[^2])) return "";
        if (IsCjk(last) && IsCjk(after[0])) return "";
        return " ";
    }

    /// <summary>The text's user-perceived characters (a letter with its accents, an emoji), as Swift counts them.</summary>
    private static List<string> Characters(string text)
    {
        var list = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext()) list.Add(e.GetTextElement());
        return list;
    }

    private static Rune First(string character)
    {
        Rune.DecodeFromUtf16(character, out var rune, out _);   // A broken surrogate reads as U+FFFD.
        return rune;
    }

    private static bool IsWhite(string character) => Rune.IsWhiteSpace(First(character));

    private static bool IsLetter(string character) => Rune.IsLetter(First(character));

    private static bool IsCjk(string character)
    {
        var v = First(character).Value;
        return v is >= 0x3000 and <= 0x30FF or >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF
            or >= 0xF900 and <= 0xFAFF or >= 0xFF00 and <= 0xFFEF;
    }

    /// <summary>
    /// The text's own colour, except near-white text, which was on a background that's gone now:
    /// that becomes black.
    /// </summary>
    private static Rgb Readable(Rgb c) =>
        0.2126 * c.R / 255 + 0.7152 * c.G / 255 + 0.0722 * c.B / 255 < 0.9 ? c : Rgb.Black;
}
