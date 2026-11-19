using System.IO.Compression;
using System.Text;
using Convert.Core;
using Xunit;

namespace Convert.Core.Tests;

/// <summary>
/// The document tests run one class at a time: the LibreOffice and WebView2 processes a Stop test looks
/// for are then its own, and timings aren't skewed by another class converting at the same time.
/// </summary>
[CollectionDefinition("Documents", DisableParallelization = true)]
public sealed class DocumentsCollection;

/// <summary>A test that needs LibreOffice, which is optional: without it, it's skipped rather than failed.</summary>
public sealed class LibreOfficeFactAttribute : FactAttribute
{
    public LibreOfficeFactAttribute()
    {
        if (Tools.LibreOffice == null) Skip = "LibreOffice isn't installed";
    }
}

/// <summary>Documents made up for the tests.</summary>
public static class DocTestFiles
{
    /// <summary>A 2×2 PNG.</summary>
    public static readonly byte[] Png = System.Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFklEQVR4nGP8z8DAwMDAxMDAwMDAAAANHQEDasKb6QAAAABJRU5ErkJggg==");

    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>
    /// A Word document with a paragraph of text and, for each of <paramref name="linkedPictures"/>, a
    /// picture that's a link to that address rather than embedded; <paramref name="template"/> is
    /// linked as its template, <paramref name="fieldPicture"/> pulled in by an INCLUDEPICTURE field.
    /// </summary>
    public static string Docx(string path, string text, string[]? linkedPictures = null, string? template = null, string? fieldPicture = null)
    {
        linkedPictures ??= [];
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string xml)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            w.Write(xml);
        }
        Add("[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
            <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
            <Default Extension="xml" ContentType="application/xml"/>
            <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
            <Override PartName="/word/settings.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml"/>
            </Types>
            """);
        Add("_rels/.rels", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
            <Relationship Id="rId1" Type="{R}/officeDocument" Target="word/document.xml"/>
            </Relationships>
            """);
        var rels = string.Concat(linkedPictures.Select((url, i) =>
            $"<Relationship Id=\"rIdPicture{i}\" Type=\"{R}/image\" Target=\"{url}\" TargetMode=\"External\"/>\n"));
        Add("word/_rels/document.xml.rels", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
            <Relationship Id="rIdSettings" Type="{R}/settings" Target="settings.xml"/>
            {rels}</Relationships>
            """);
        if (template != null)
        {
            Add("word/_rels/settings.xml.rels", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rIdTemplate" Type="{R}/attachedTemplate" Target="{template}" TargetMode="External"/>
                </Relationships>
                """);
        }
        Add("word/settings.xml", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:settings xmlns:w="{W}" xmlns:r="{R}">{(template != null ? "<w:attachedTemplate r:id=\"rIdTemplate\"/>" : "")}</w:settings>
            """);
        static string Picture(int i) => $"""
            <w:p><w:r><w:drawing><wp:inline><wp:extent cx="914400" cy="914400"/><wp:docPr id="{i + 1}" name="Picture {i + 1}"/>
            <a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture">
            <pic:pic><pic:nvPicPr><pic:cNvPr id="{i + 1}" name="p{i}.png"/><pic:cNvPicPr/></pic:nvPicPr>
            <pic:blipFill><a:blip r:link="rIdPicture{i}"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>
            <pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="914400" cy="914400"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr>
            </pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>
            """;
        var field = fieldPicture == null ? ""
            : $"<w:p><w:fldSimple w:instr=\"INCLUDEPICTURE &quot;{fieldPicture}&quot; \\d\"><w:r><w:t>field</w:t></w:r></w:fldSimple></w:p>";
        Add("word/document.xml", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:document xmlns:w="{W}" xmlns:r="{R}"
              xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
              xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
              xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture">
            <w:body>
            <w:p><w:r><w:t>{System.Security.SecurityElement.Escape(text)}</w:t></w:r></w:p>
            {string.Concat(linkedPictures.Select((_, i) => Picture(i)))}
            {field}
            </w:body></w:document>
            """);
        return path;
    }

    /// <summary>A Markdown file long enough that printing it takes a while.</summary>
    public static string LongMarkdown(string path, int sections = 400)
    {
        var md = new StringBuilder();
        for (var i = 1; i <= sections; i++)
        {
            md.Append($"## Section {i}\n\nSome **bold** text, some `code`, and a list:\n\n- one\n- two\n- three\n\n");
            md.Append("| a | b | c |\n|---|---|---|\n| 1 | 2 | 3 |\n| 4 | 5 | 6 |\n\n");
        }
        File.WriteAllText(path, md.ToString());
        return path;
    }
}
