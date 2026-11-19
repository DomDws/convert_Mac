using System.Text;

namespace Convert.Core;

public static partial class DocWriter
{
    private static void WriteRtf(Stream stream, IReadOnlyList<TextParagraph> paragraphs)
    {
        var fonts = new List<string>();
        var colours = new List<Rgb>();
        static int IndexOf<T>(List<T> list, T item)
        {
            var i = list.IndexOf(item);
            if (i < 0)
            {
                list.Add(item);
                i = list.Count - 1;
            }
            return i;
        }

        var body = new StringBuilder();
        foreach (var p in paragraphs)
        {
            body.Append(@"\pard\sa").Append(Twips(p.SpaceAfter)).Append(p.Centred ? @"\qc" : @"\ql");
            foreach (var run in p.Runs)
            {
                var s = run.Style;
                // Colour 0 is "automatic", so the table's own entries start at 1.
                body.Append(@"\plain\f").Append(IndexOf(fonts, s.Font)).Append(@"\fs").Append(HalfPoints(s.Size))
                    .Append(s.Bold ? @"\b" : "").Append(s.Italic ? @"\i" : "").Append(@"\cf").Append(IndexOf(colours, s.Color) + 1)
                    .Append(' ').Append(Rtf(run.Text));
            }
            // The paragraph mark keeps the last run's look, so an empty line after it is as tall as its text.
            body.Append(@"\par").Append('\n');
        }

        var doc = new StringBuilder(@"{\rtf1\ansi\ansicpg1252\deff0\uc1").Append('\n');
        doc.Append(@"{\fonttbl");
        for (var i = 0; i < fonts.Count; i++) doc.Append(@"{\f").Append(i).Append(@"\fnil ").Append(Rtf(fonts[i])).Append(";}");
        doc.Append("}\n").Append(@"{\colortbl;");
        foreach (var c in colours) doc.Append(@"\red").Append(c.R).Append(@"\green").Append(c.G).Append(@"\blue").Append(c.B).Append(';');
        doc.Append("}\n").Append(body).Append("}\n");
        stream.Write(Encoding.ASCII.GetBytes(doc.ToString()));
    }

    /// <summary>
    /// Text for RTF, which is ASCII: anything else goes in as \u with the UTF-16 code unit as a
    /// signed number (two for a character outside the BMP), then "?" for readers that don't know \u.
    /// </summary>
    private static string Rtf(string text)
    {
        var s = new StringBuilder(text.Length);
        // What XML can't hold makes no sense in RTF either (half a surrogate pair, U+FFFF).
        foreach (var c in Valid(text))
        {
            switch (c)
            {
                case '\\' or '{' or '}': s.Append('\\').Append(c); break;
                case '\t': s.Append(@"\tab "); break;
                case < ' ': break;
                case <= '~': s.Append(c); break;
                default: s.Append(@"\u").Append((short)c).Append('?'); break;
            }
        }
        return s.ToString();
    }
}
