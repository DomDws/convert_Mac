using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace Convert.Core;

public static partial class PdfText
{
    /// <summary>The page's paragraphs. Empty for a page without text.</summary>
    public static List<TextParagraph> Paragraphs(Page page)
    {
        // PdfPig already places letters as the page is shown, /Rotate applied, with the visible
        // area's bottom left at (0, 0) and y growing upward.
        var turn = Turn(page.Letters);
        var v = page.CropBox.GetVisibleBounds(page.Rotation);
        var frame = Turned(new Box(v.Left, v.Bottom, v.Right, v.Top), turn);
        return Paragraphs(Lines(page.Letters, turn), frame);
    }

    /// <summary>One letter, measured as the line it's part of will be.</summary>
    private readonly record struct Glyph(string Value, Box Box, double Size, TextStyle Style);

    /// <summary>
    /// The page's lines in reading order. The Mac gets lines from PDFKit; here they're made from the
    /// letters, in the order the PDF draws them, which is the order it was written in: letters next
    /// to each other on the same row (a word, a run of dots, a footnote mark) are one line, and a
    /// letter that sits elsewhere starts the next. Spaces are put back where the PDF leaves a gap
    /// instead of drawing one. Every letter is in exactly one line, so no word is lost or doubled.
    /// </summary>
    private static List<Line> Lines(IReadOnlyList<Letter> letters, int turn)
    {
        var lines = new List<Line>();
        var styles = new Dictionary<(string?, bool, bool, Rgb, double), TextStyle>();
        List<Glyph>? current = null;
        var parts = new List<(string Text, TextStyle Style)>();
        var box = default(Box);
        var spread = false;
        var space = false;

        void Finish()
        {
            if (current != null) lines.Add(Described(current, parts, box, spread));
            current = null;
            parts = [];
        }

        foreach (var letter in letters)
        {
            var g = Measure(letter, turn, styles);
            if (g.Value.All(char.IsWhiteSpace))
            {
                space = current != null;
                continue;
            }
            if (current != null && (SameRow(box, g.Box) || Leftward(box, current[^1], g)))
            {
                var last = current[^1];
                // Drawn twice, slightly offset, to look bold: the copy isn't more text.
                if (current.TakeLast(200).Any(o => o.Value == g.Value && Math.Abs(o.Box.MinX - g.Box.MinX) < 0.1 * g.Size
                                                   && Math.Abs(o.Box.MinY - g.Box.MinY) < 0.1 * g.Size))
                    continue;
                var gap = Leftward(box, last, g) ? last.Box.MinX - g.Box.MaxX : g.Box.MinX - last.Box.MaxX;
                spread |= gap > 1.5 * g.Box.Height;
                if (space || gap > 0.2 * Math.Max(g.Size, last.Size)) parts.Add((" ", last.Style));
                current.Add(g);
                box = box.Union(g.Box);
            }
            else
            {
                Finish();
                current = [g];
                box = g.Box;
                spread = false;
            }
            parts.Add((g.Value, g.Style));
            space = false;
        }
        Finish();
        return lines;
    }

    /// <summary>Two pieces of the same printed line: they overlap vertically and the second is to the right.</summary>
    private static bool SameRow(Box a, Box b)
    {
        var overlap = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
        return overlap > 0.5 * Math.Min(a.Height, b.Height) && b.MinX > a.MinX;
    }

    /// <summary>
    /// Arabic or Hebrew drawn in reading order: the next letter sits just left of the line so far,
    /// on the same baseline. That goes on the line rather than starting one per letter. (A new line
    /// starts far to the left, at the margin, and further down.)
    /// </summary>
    private static bool Leftward(Box line, Glyph last, Glyph g) =>
        g.Box.MinX < line.MinX && g.Box.MaxX <= last.Box.MinX + 0.1 * g.Size && g.Box.MaxX > last.Box.MinX - g.Size
        && Math.Abs(g.Box.MinY - last.Box.MinY) < 0.2 * g.Size;

    /// <summary>The line with its text and its main text size.</summary>
    private static Line Described(List<Glyph> glyphs, List<(string Text, TextStyle Style)> parts, Box box, bool spread)
    {
        var runs = new List<TextRun>();
        foreach (var (text, style) in parts)
        {
            if (runs.Count > 0 && runs[^1].Style == style) runs[^1] = runs[^1] with { Text = runs[^1].Text + text };
            else runs.Add(new TextRun(text, style));
        }
        if (runs.Count > 0) runs[^1] = runs[^1] with { Text = runs[^1].Text.TrimEnd() };
        var text_ = string.Concat(runs.Select(r => r.Text));
        var size = glyphs.GroupBy(g => g.Size).OrderByDescending(s => s.Sum(g => g.Value.Length)).First().Key;
        return new Line
        {
            Box = box,
            Runs = runs,
            Text = text_,
            Size = size,
            Spread = spread || box.Width > 1.5 * size * Characters(text_).Count,
        };
    }

    private static Glyph Measure(Letter l, int turn, Dictionary<(string?, bool, bool, Rgb, double), TextStyle> styles)
    {
        var size = l.PointSize > 0 && double.IsFinite(l.PointSize) ? Math.Round(l.PointSize, 2) : 12;
        var start = Turned(l.StartBaseLine, turn);
        var end = Turned(l.EndBaseLine, turn);
        // The loose box reaches from the font's descent to its ascent, the same for every letter of
        // a font, as a PDF viewer's lines do. A font that says nothing sensible about either gets
        // the usual proportions.
        var r = l.GlyphRectangleLoose;
        var box = Bounds([Turned(r.BottomLeft, turn), Turned(r.BottomRight, turn), Turned(r.TopLeft, turn), Turned(r.TopRight, turn)]);
        if (!(box.Height > 0.3 * size && box.Height < 3 * size) || !double.IsFinite(box.Width))
            box = new Box(Math.Min(start.X, end.X), start.Y - 0.22 * size, Math.Max(start.X, end.X), start.Y + 0.78 * size);

        var details = l.FontDetails;
        var key = (l.FontName, details?.IsBold ?? false, details?.IsItalic ?? false, Colour(l), size);
        if (!styles.TryGetValue(key, out var style))
        {
            var (family, bold, italic) = FontFace(key.FontName, key.Item2, key.Item3);
            styles[key] = style = new TextStyle(family, size, bold, italic, key.Item4);
        }
        return new Glyph(l.Value ?? "", box, size, style);
    }

    private static Rgb Colour(Letter l)
    {
        try
        {
            if (l.Color == null) return Rgb.Black;
            var (r, g, b) = l.Color.ToRGBValues();
            static byte B(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
            return new Rgb(B(r), B(g), B(b));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Rgb.Black;   // A pattern or a colour space PdfPig can't turn into RGB.
        }
    }

    /// <summary>
    /// How far to turn the page (clockwise, in degrees) so that most of its text runs left to
    /// right: none, unless the page was printed sideways without saying so (a landscape table on
    /// portrait paper, or vertical Japanese).
    /// </summary>
    private static int Turn(IReadOnlyList<Letter> letters)
    {
        var counts = new int[4];
        foreach (var l in letters)
        {
            var dx = l.EndBaseLine.X - l.StartBaseLine.X;
            var dy = l.EndBaseLine.Y - l.StartBaseLine.Y;
            if (dx == 0 && dy == 0 || string.IsNullOrWhiteSpace(l.Value)) continue;
            var quarter = (int)Math.Round(Math.Atan2(dy, dx) / (Math.PI / 2));
            counts[(quarter + 4) % 4]++;
        }
        var most = Array.IndexOf(counts, counts.Max());
        return counts[most] > counts[0] ? most * 90 : 0;
    }

    private static PdfPoint Turned(PdfPoint p, int turn) => turn switch
    {
        90 => new PdfPoint(p.Y, -p.X),
        180 => new PdfPoint(-p.X, -p.Y),
        270 => new PdfPoint(-p.Y, p.X),
        _ => p,
    };

    private static Box Turned(Box b, int turn) =>
        Bounds([Turned(new PdfPoint(b.MinX, b.MinY), turn), Turned(new PdfPoint(b.MaxX, b.MaxY), turn)]);

    private static Box Bounds(PdfPoint[] points) =>
        new(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
}
