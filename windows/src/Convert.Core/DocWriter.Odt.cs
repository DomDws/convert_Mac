using System.IO.Compression;
using System.Text;

namespace Convert.Core;

public static partial class DocWriter
{
    private const string OdtNamespaces =
        """xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0" """ +
        """xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:fo="urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0" """ +
        """xmlns:svg="urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0" office:version="1.2" """;

    private static void WriteOdt(Stream stream, IReadOnlyList<TextParagraph> paragraphs)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        // Readers recognise the format by this: the first entry, stored as it is, its name and
        // contents at fixed places in the file.
        Entry(zip, "mimetype", "application/vnd.oasis.opendocument.text", CompressionLevel.NoCompression);
        Entry(zip, "META-INF/manifest.xml",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0" manifest:version="1.2">
            <manifest:file-entry manifest:full-path="/" manifest:version="1.2" manifest:media-type="application/vnd.oasis.opendocument.text"/>
            <manifest:file-entry manifest:full-path="content.xml" manifest:media-type="text/xml"/>
            <manifest:file-entry manifest:full-path="styles.xml" manifest:media-type="text/xml"/>
            </manifest:manifest>
            """);
        // Paragraphs say how much space follows them; by default there's none.
        Entry(zip, "styles.xml",
            $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <office:document-styles {OdtNamespaces}>
            <office:styles>
            <style:default-style style:family="paragraph"><style:paragraph-properties fo:margin-top="0pt" fo:margin-bottom="0pt"/><style:text-properties fo:font-size="12pt"/></style:default-style>
            </office:styles>
            </office:document-styles>
            """);

        // ODF keeps formatting in named styles: one per kind of paragraph and per look of a run.
        var paragraphStyles = new Dictionary<(double, bool), string>();
        var textStyles = new Dictionary<TextStyle, string>();
        var fonts = new SortedSet<string>(StringComparer.Ordinal);
        var body = new StringBuilder();
        foreach (var p in paragraphs)
        {
            if (!paragraphStyles.TryGetValue((p.SpaceAfter, p.Centred), out var pName))
                paragraphStyles[(p.SpaceAfter, p.Centred)] = pName = $"P{paragraphStyles.Count + 1}";
            body.Append($"""<text:p text:style-name="{pName}">""");
            // Spaces collapse in ODF as in HTML: a space at the start or after another one needs text:s.
            var afterSpace = true;
            foreach (var run in p.Runs)
            {
                if (!textStyles.TryGetValue(run.Style, out var tName)) textStyles[run.Style] = tName = $"T{textStyles.Count + 1}";
                fonts.Add(run.Style.Font);
                body.Append($"""<text:span text:style-name="{tName}">""");
                foreach (var c in Xml(run.Text))
                {
                    if (c == ' ')
                    {
                        body.Append(afterSpace ? "<text:s/>" : " ");
                        afterSpace = true;
                        continue;
                    }
                    afterSpace = c == '\t';
                    if (c == '\t') body.Append("<text:tab/>");
                    else body.Append(c);
                }
                body.Append("</text:span>");
            }
            body.Append("</text:p>\n");
        }

        var content = new StringBuilder();
        content.Append($"""<?xml version="1.0" encoding="UTF-8"?>""").Append('\n');
        content.Append($"<office:document-content {OdtNamespaces}>\n<office:font-face-decls>");
        foreach (var f in fonts)
            content.Append($"""<style:font-face style:name="{Xml(f)}" svg:font-family="'{Xml(f.Replace("'", ""))}'"/>""");
        content.Append("</office:font-face-decls>\n<office:automatic-styles>\n");
        foreach (var ((space, centred), name) in paragraphStyles)
        {
            content.Append($"""<style:style style:name="{name}" style:family="paragraph"><style:paragraph-properties fo:margin-top="0pt" fo:margin-bottom="{Num(space)}pt" """);
            content.Append(centred ? """fo:text-align="center"/></style:style>""" : "/></style:style>").Append('\n');
        }
        foreach (var (s, name) in textStyles)
        {
            var font = Xml(s.Font);
            var size = $"{Num(s.Size)}pt";
            var weight = s.Bold ? "bold" : "normal";
            var slant = s.Italic ? "italic" : "normal";
            // Each property three times: for Latin text, for Chinese, Japanese and Korean, and for right-to-left scripts.
            content.Append($"""<style:style style:name="{name}" style:family="text"><style:text-properties """)
                .Append($"""style:font-name="{font}" style:font-name-asian="{font}" style:font-name-complex="{font}" """)
                .Append($"""fo:font-size="{size}" style:font-size-asian="{size}" style:font-size-complex="{size}" """)
                .Append($"""fo:font-weight="{weight}" style:font-weight-asian="{weight}" style:font-weight-complex="{weight}" """)
                .Append($"""fo:font-style="{slant}" style:font-style-asian="{slant}" style:font-style-complex="{slant}" """)
                .Append($"""fo:color="#{s.Color.Hex.ToLowerInvariant()}"/></style:style>""").Append('\n');
        }
        content.Append("</office:automatic-styles>\n<office:body><office:text>\n").Append(body).Append("</office:text></office:body></office:document-content>\n");
        Entry(zip, "content.xml", content.ToString());
    }
}
