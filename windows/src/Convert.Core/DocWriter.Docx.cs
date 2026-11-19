using System.IO.Compression;
using System.Text;

namespace Convert.Core;

public static partial class DocWriter
{
    private const string WordNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>The smallest package Word opens without complaint: the document, its styles, and how they're tied together.</summary>
    private static void WriteDocx(Stream stream, IReadOnlyList<TextParagraph> paragraphs)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        Entry(zip, "[Content_Types].xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
            <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
            <Default Extension="xml" ContentType="application/xml"/>
            <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
            <Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>
            </Types>
            """);
        Entry(zip, "_rels/.rels",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
            <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
            </Relationships>
            """);
        Entry(zip, "word/_rels/document.xml.rels",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
            <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
            </Relationships>
            """);
        // Word's own defaults add space after every paragraph and widen the lines; each paragraph
        // here says how much space follows it, so the default is none.
        Entry(zip, "word/styles.xml",
            $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:styles xmlns:w="{WordNs}">
            <w:docDefaults>
            <w:rPrDefault><w:rPr><w:sz w:val="24"/><w:szCs w:val="24"/></w:rPr></w:rPrDefault>
            <w:pPrDefault><w:pPr><w:spacing w:after="0" w:line="240" w:lineRule="auto"/></w:pPr></w:pPrDefault>
            </w:docDefaults>
            <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:qFormat/></w:style>
            </w:styles>
            """);

        var doc = new StringBuilder();
        doc.Append($"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""").Append('\n');
        doc.Append($"""<w:document xmlns:w="{WordNs}"><w:body>""").Append('\n');
        foreach (var p in paragraphs)
        {
            doc.Append($"""<w:p><w:pPr><w:spacing w:after="{Twips(p.SpaceAfter)}"/>""");
            if (p.Centred) doc.Append("""<w:jc w:val="center"/>""");
            doc.Append("</w:pPr>");
            foreach (var run in p.Runs)
            {
                var s = run.Style;
                var font = Xml(s.Font);
                var size = HalfPoints(s.Size);
                doc.Append($"""<w:r><w:rPr><w:rFonts w:ascii="{font}" w:hAnsi="{font}" w:eastAsia="{font}" w:cs="{font}"/>""");
                if (s.Bold) doc.Append("<w:b/><w:bCs/>");
                if (s.Italic) doc.Append("<w:i/><w:iCs/>");
                doc.Append($"""<w:color w:val="{s.Color.Hex}"/><w:sz w:val="{size}"/><w:szCs w:val="{size}"/></w:rPr>""");
                // A tab inside w:t would show as a space; Word wants its own element.
                var pieces = run.Text.Split('\t');
                for (var i = 0; i < pieces.Length; i++)
                {
                    if (i > 0) doc.Append("<w:tab/>");
                    if (pieces[i].Length > 0) doc.Append($"""<w:t xml:space="preserve">{Xml(pieces[i])}</w:t>""");
                }
                doc.Append("</w:r>");
            }
            doc.Append("</w:p>\n");
        }
        doc.Append("</w:body></w:document>\n");
        Entry(zip, "word/document.xml", doc.ToString());
    }
}
