using System.IO.Compression;
using System.Text;
using Convert.Core;
using UglyToad.PdfPig;
using Xunit;
using Xunit.Abstractions;

namespace Convert.Core.Tests;

/// <summary>What document conversions produce: paper size, pictures, text in any script, and nothing about where the original was.</summary>
[Collection("Documents")]
public sealed class DocOutputTests(ITestOutputHelper log) : IDisposable
{
    // A folder name that can't turn up in a document by chance.
    private const string Secret = "Private Folder 7f3a";
    private readonly string root = Directory.CreateTempSubdirectory("convert-doc-out-").FullName;
    private string Dir => Directory.CreateDirectory(Path.Combine(root, Secret)).FullName;

    public void Dispose() => Paths.TryDelete(root);

    private static Task<string> Convert(string input, string target, ConvSettings? s = null, DocOptions? options = null) =>
        DocConv.ConvertAsync(input, Targets.ById(target)!, s ?? new ConvSettings(), new Runner(), options ?? new DocOptions());

    private string Write(string name, string text, bool bom = false)
    {
        var path = Path.Combine(Dir, name);
        File.WriteAllText(path, text, new UTF8Encoding(bom));
        return path;
    }

    private string Picture(string name)
    {
        var path = Path.Combine(Dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, DocTestFiles.Png);
        return path;
    }

    // Paper

    [Theory]
    [InlineData(true, "")]
    [InlineData(false, "")]
    // As at 200 % display scaling: the paper is the same.
    [InlineData(true, "--force-device-scale-factor=2")]
    [InlineData(false, "--force-device-scale-factor=2")]
    public async Task PdfHasThePaperSizeAsked(bool a4, string browserArguments)
    {
        var md = Write("paper.md", "# Paper\n\nOne page.\n");
        var pdf = await Convert(md, "pdf-doc", new ConvSettings { PaperA4 = a4 }, new DocOptions { BrowserArguments = browserArguments });
        using var doc = PdfDocument.Open(pdf);
        var box = doc.GetPage(1).MediaBox.Bounds;
        var raw = System.Text.RegularExpressions.Regex.Match(Encoding.Latin1.GetString(File.ReadAllBytes(pdf)), @"/MediaBox \[[^\]]*\]").Value;
        log.WriteLine($"{(a4 ? "A4" : "Letter")} {browserArguments}: {raw}");
        var (w, h) = a4 ? (595.28, 841.89) : (612.0, 792.0);
        Assert.InRange(box.Width, w - 1, w + 1);
        Assert.InRange(box.Height, h - 1, h + 1);
    }

    // Pictures

    /// <summary>The 2×2 pictures in the PDF. A picture that didn't load is drawn as a broken-picture icon, which is a picture too.</summary>
    private static int OurPictures(PdfDocument doc) => doc.GetPage(1).GetImages().Count(i => i.WidthInSamples == 2 && i.HeightInSamples == 2);

    [Fact]
    public async Task PicturesNextToTheDocumentAreInThePdf()
    {
        Picture("pic.png");
        Picture(Path.Combine("img", "sub.png"));
        var md = Write("pictures.md", "# Pictures\n\n![a picture](pic.png)\n\n![in a folder](img/sub.png)\n");
        var html = Write("pictures.html", "<!doctype html><title>Pictures</title><img src=\"pic.png\" width=50><img src=\"img/sub.png\" width=50>");
        foreach (var input in new[] { md, html })
        {
            using var doc = PdfDocument.Open(await Convert(input, "pdf-doc"));
            Assert.Equal(2, OurPictures(doc));
        }
    }

    [Fact]
    public async Task APictureOutsideTheDocumentsFolderIsntLoaded()
    {
        var outside = Path.Combine(root, "outside.png");
        File.WriteAllBytes(outside, DocTestFiles.Png);
        var html = Write("outside.html", $"<!doctype html><title>Outside</title><img src=\"../outside.png\" width=50><img src=\"{new Uri(outside).AbsoluteUri}\" width=50><img src=\"pic.png\" width=50>");
        Picture("pic.png");
        using var doc = PdfDocument.Open(await Convert(html, "pdf-doc"));
        Assert.Equal(1, OurPictures(doc));
    }

    [LibreOfficeFact]
    public async Task PicturesNextToTheDocumentAreEmbeddedInWord()
    {
        Picture("pic.png");
        var md = Write("pictures.md", "# Pictures\n\n![a picture](pic.png)\n");
        using var zip = ZipFile.OpenRead(await Convert(md, "docx"));
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("word/media/"));
    }

    // Text

    private const string Mixed = "Grüße aus Zürich, señor. Ελληνικά, Кириллица, 東京, emoji 🎉.";

    [Theory]
    [InlineData("notes.txt", false)]
    [InlineData("notes.txt", true)]
    [InlineData("notes.md", false)]
    [InlineData("notes.md", true)]
    // Not a video: anything that isn't a known document is text.
    [InlineData("script.ts", false)]
    [InlineData("server.log", true)]
    public async Task Utf8TextConvertsWithOrWithoutBom(string name, bool bom)
    {
        var input = Write(name, $"{Mixed}\n", bom);
        var html = File.ReadAllText(await Convert(input, "html"), Encoding.UTF8);
        Assert.Contains(Mixed, html);
        Assert.DoesNotContain('﻿', html);
        var txt = File.ReadAllText(await Convert(input, "txt"), Encoding.UTF8);
        Assert.Contains(Mixed, txt);
        using var doc = PdfDocument.Open(await Convert(input, "pdf-doc"));
        var text = doc.GetPage(1).Text;
        log.WriteLine(text);
        Assert.Contains("Grüße", text);
        Assert.Contains("señor", text);
    }

    // Privacy

    /// <summary>The ways a path can end up in a file: as typed, as a URL, and in UTF-16.</summary>
    private static IEnumerable<string> Telltales(string folder)
    {
        var url = new Uri(folder + Path.DirectorySeparatorChar).AbsoluteUri;
        yield return Secret;
        yield return Uri.EscapeDataString(Secret);
        yield return folder;
        yield return folder.Replace('\\', '/');
        yield return url;
        if (Environment.UserName.Length >= 4) yield return Environment.UserName;
        yield return Path.GetTempPath().TrimEnd('\\');
    }

    private static void AssertSaysNothingAbout(string file, string folder)
    {
        var texts = new List<(string where, string text)>();
        void Add(string where, byte[] bytes)
        {
            texts.Add((where, Encoding.UTF8.GetString(bytes)));
            texts.Add((where + " (UTF-16)", Encoding.Unicode.GetString(bytes)));
            texts.Add((where + " (UTF-16BE)", Encoding.BigEndianUnicode.GetString(bytes)));
        }
        var bytes = File.ReadAllBytes(file);
        Add(Path.GetFileName(file), bytes);
        if (bytes.AsSpan().StartsWith("PK"u8))
        {
            using var zip = ZipFile.OpenRead(file);
            foreach (var entry in zip.Entries)
            {
                using var s = entry.Open();
                using var m = new MemoryStream();
                s.CopyTo(m);
                Add($"{Path.GetFileName(file)}/{entry.FullName}", m.ToArray());
            }
        }
        if (file.EndsWith(".pdf"))
        {
            using var doc = PdfDocument.Open(file);
            var info = doc.Information;
            texts.Add(("PDF info", $"{info.Title}|{info.Author}|{info.Subject}|{info.Keywords}|{info.Creator}|{info.Producer}"));
        }
        foreach (var telltale in Telltales(folder))
        {
            foreach (var (where, text) in texts)
                Assert.False(text.Contains(telltale, StringComparison.OrdinalIgnoreCase), $"{where} contains \"{telltale}\"");
        }
    }

    [Theory]
    [InlineData("notes.md", "html")]
    [InlineData("notes.md", "txt")]
    [InlineData("notes.md", "pdf-doc")]
    [InlineData("notes.txt", "html")]
    [InlineData("notes.txt", "pdf-doc")]
    [InlineData("page.html", "pdf-doc")]
    [InlineData("notes.md", "docx")]
    [InlineData("notes.md", "odt")]
    [InlineData("notes.md", "rtf")]
    [InlineData("page.html", "docx")]
    [InlineData("notes.txt", "docx")]
    public async Task OutputSaysNothingAboutWhereTheOriginalWas(string name, string target)
    {
        Picture("pic.png");
        var input = name switch
        {
            "notes.md" => Write(name, "# Notes\n\nSee ![a picture](pic.png) and [the other file](other.md).\n"),
            "page.html" => Write(name, "<!doctype html><h1>Page</h1><img src=\"pic.png\"><a href=\"other.html\">other</a>"),
            _ => Write(name, "Some notes.\n"),
        };
        if (DocConv.NeedsLibreOffice(input, Targets.ById(target)!) && Tools.LibreOffice == null) return;
        var output = await Convert(input, target);
        AssertSaysNothingAbout(output, Dir);
    }
}
