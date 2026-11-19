using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;

[assembly: InternalsVisibleTo("Convert.Core.Tests")]

namespace Convert.Core;

/// <summary>
/// What keeps a document from reaching the network while it's converted. Each one is enough on its
/// own for what it covers; they're all on, and tests switch them off one at a time to show that.
/// </summary>
[Flags]
internal enum DocGuards
{
    None = 0,
    /// <summary>WebView2: every request is checked, and only files in the document's folder and the conversion's temp folder load.</summary>
    RequestFilter = 1,
    /// <summary>WebView2: all web traffic goes to a proxy that isn't there.</summary>
    BrowserProxy = 2,
    /// <summary>WebView2: no host name resolves.</summary>
    NoNameLookup = 4,
    /// <summary>LibreOffice: all web traffic goes to a proxy that isn't there.</summary>
    OfficeProxy = 8,
    /// <summary>LibreOffice: HTML and Markdown go in with everything but text, links and local pictures taken out.</summary>
    CleanHtml = 16,
    Browser = RequestFilter | BrowserProxy | NoNameLookup,
    All = Browser | OfficeProxy | CleanHtml,
}

/// <summary>How a document conversion runs. Only tests change it.</summary>
internal sealed record DocOptions
{
    public DocGuards Guards { get; init; } = DocGuards.All;

    /// <summary>Added to WebView2's command line, to pretend a scaled display for one.</summary>
    public string BrowserArguments { get; init; } = "";

    /// <summary>Where the conversion's temp folder goes, so a test can see that nothing stays behind.</summary>
    public string? TempParent { get; init; }
}

/// <summary>
/// Documents. Markdown, plain text and HTML → PDF/HTML/TXT work with what every Windows 11 has
/// (Markdig + WebView2). Word/ODT/RTF need LibreOffice, like Excel/PowerPoint would.
/// Every result is made in a temp folder and only then copied to its place, so a stopped or failed
/// conversion leaves nothing behind, and nothing is ever overwritten.
/// </summary>
public static partial class DocConv
{
    private enum Src { Markdown, Text, Html, Office }

    private static Src SourceOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".md" or ".markdown" => Src.Markdown,
        ".txt" or ".text" => Src.Text,
        ".html" or ".htm" => Src.Html,
        ".docx" or ".doc" or ".odt" or ".rtf" => Src.Office,
        // Anything else (source code, logs, a .ts that is TypeScript) is plain text.
        _ => Src.Text,
    };

    public const string LibreOfficeHint = "Needs LibreOffice: winget install TheDocumentFoundation.LibreOffice";

    /// <summary>Whether converting this file to this target needs LibreOffice.</summary>
    public static bool NeedsLibreOffice(string input, Target t) => (SourceOf(input), t.Id) switch
    {
        (Src.Office, _) => true,
        (_, "pdf-doc" or "html") => false,
        (Src.Markdown or Src.Text, "txt") => false,
        _ => true,
    };

    public static Task<string> ConvertAsync(string input, Target t, ConvSettings s, Runner runner) =>
        ConvertAsync(input, t, s, runner, new DocOptions());

    internal static async Task<string> ConvertAsync(string input, Target t, ConvSettings s, Runner runner, DocOptions options)
    {
        runner.CheckCancelled();
        var src = SourceOf(input);
        if (NeedsLibreOffice(input, t) && Tools.LibreOffice == null) throw new ConvertException(LibreOfficeHint);

        var tmp = options.TempParent is { } parent ? Directory.CreateDirectory(Path.Combine(parent, Guid.NewGuid().ToString("N"))).FullName : Paths.TempDir();
        string? output = null;
        try
        {
            var made = await MakeAsync(input, src, t, s, runner, options, tmp);
            runner.CheckCancelled();
            output = await PublishAsync(made, input, t.Ext, runner);
            runner.CheckCancelled();
            return output;
        }
        catch (Exception) when (runner.IsCancelled)
        {
            // Whatever went wrong after Stop is because of Stop.
            Paths.TryDelete(output);
            throw new StoppedException();
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException && !File.Exists(input))
        {
            // Gone before its turn came: a batch can take a while.
            throw new ConvertException("The file no longer exists");
        }
        finally
        {
            await DeleteTempAsync(tmp);
        }
    }

    /// <summary>Makes the converted file inside <paramref name="tmp"/> and returns its path.</summary>
    private static async Task<string> MakeAsync(string input, Src src, Target t, ConvSettings s, Runner runner, DocOptions options, string tmp)
    {
        // What the page being printed may load: the document's own folder (its pictures) and this
        // conversion's temp folder (the page itself). Nothing else, and nothing from the network.
        string[] readable = [FolderOf(input), tmp];
        switch (t.Id)
        {
            case "pdf-doc" when src != Src.Office:
                {
                    var page = Path.Combine(tmp, "page.html");
                    File.WriteAllText(page, PrintableHtml(input, src, s.PaperA4), Utf8);
                    var pdf = Path.Combine(tmp, "out.pdf");
                    await DocBrowser.PrintAsync(page, pdf, s.PaperA4, readable, runner, options);
                    return pdf;
                }
            case "html" when src is Src.Markdown or Src.Text or Src.Html:
                {
                    var made = Path.Combine(tmp, "out.html");
                    if (src == Src.Html) File.Copy(input, made);
                    else File.WriteAllText(made, StandaloneHtml(input, src, head: ""), Utf8);
                    return made;
                }
            case "txt" when src is Src.Markdown or Src.Text:
                {
                    var made = Path.Combine(tmp, "out.txt");
                    if (src == Src.Text) File.Copy(input, made);
                    else File.WriteAllText(made, Markdown.ToPlainText(File.ReadAllText(input), Pipeline).Trim() + "\n", Utf8);
                    return made;
                }
            default:
                {
                    var feed = input;
                    if (src is Src.Markdown or Src.Html)
                    {
                        // LibreOffice fetches the stylesheets an HTML file links to, from the web or from a
                        // share, and keeps pictures as links to wherever they are. So it gets a copy that
                        // WebView2 has read (with the same locks as for printing) and cleaned: no
                        // stylesheets, frames or remote pictures, and local pictures embedded. Markdown goes
                        // in as rendered HTML so headings, lists and tables survive.
                        var page = Path.Combine(tmp, "page.html");
                        File.WriteAllText(page, PrintableHtml(input, src, a4: null), Utf8);
                        var dir = Directory.CreateDirectory(Path.Combine(tmp, "feed")).FullName;
                        feed = Path.Combine(dir, Path.GetFileNameWithoutExtension(input) + ".html");
                        if (options.Guards.HasFlag(DocGuards.CleanHtml))
                            await DocBrowser.CleanAsync(page, feed, readable, runner, options);
                        else
                            File.Copy(page, feed);
                        src = Src.Html;
                    }
                    return await LibreOfficeAsync(feed, src, t, tmp, runner, options.Guards);
                }
        }
    }

    private static string FolderOf(string path) => Path.GetDirectoryName(Path.GetFullPath(path))!;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Copies the finished file to the next free name next to the original. A copy rather than a move, so
    /// the new file gets the permissions of the folder it lands in, not those of the temp folder.
    /// </summary>
    private static async Task<string> PublishAsync(string made, string input, string ext, Runner runner)
    {
        for (var attempt = 1; ; attempt++)
        {
            var dest = Paths.UniqueOutput(input, ext);
            try
            {
                await Paths.WriteNewAsync(dest, async file =>
                {
                    await using var from = File.OpenRead(made);
                    await from.CopyToAsync(file, runner.Token);
                });
                return dest;
            }
            catch (IOException) when (attempt < 20 && !runner.IsCancelled && (File.Exists(dest) || Directory.Exists(dest)))
            {
                // Something took the name after it was chosen: it's left alone, and the next name is tried.
            }
        }
    }

    /// <summary>
    /// Removes the conversion's temp folder. Right after Stop, the tool that was ended can still hold a
    /// file in it for a moment, so this tries again for a little while.
    /// </summary>
    private static async Task DeleteTempAsync(string tmp)
    {
        for (var i = 0; i < 50 && Directory.Exists(tmp); i++)
        {
            Paths.TryDelete(tmp);
            if (!Directory.Exists(tmp)) return;
            await Task.Delay(100);
        }
    }

    // Markdown / HTML

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private const string Css = """
        :root { color-scheme: light; }
        body { font: 11pt/1.55 "Segoe UI", system-ui, sans-serif; color: #1a1a1a; max-width: 46em; margin: 2em auto; padding: 0 1em; }
        @media print { body { max-width: none; margin: 0; padding: 0; } }
        h1, h2, h3, h4 { line-height: 1.25; margin: 1.4em 0 .5em; }
        h1 { font-size: 1.9em; } h2 { font-size: 1.45em; border-bottom: 1px solid #e3e3e3; padding-bottom: .2em; } h3 { font-size: 1.2em; }
        p, ul, ol, table, pre, blockquote { margin: 0 0 .9em; }
        a { color: #0a64c2; }
        code { font: .92em "Cascadia Mono", Consolas, monospace; background: #f3f3f3; padding: .1em .3em; border-radius: 3px; }
        pre { background: #f6f6f6; padding: .8em 1em; border-radius: 6px; overflow-x: auto; }
        pre code { background: none; padding: 0; }
        pre.plain { background: none; padding: 0; white-space: pre-wrap; font: 10pt/1.45 "Cascadia Mono", Consolas, monospace; }
        blockquote { border-left: 3px solid #d0d0d0; padding-left: 1em; color: #555; }
        table { border-collapse: collapse; } th, td { border: 1px solid #d6d6d6; padding: .35em .7em; } th { background: #f5f5f5; }
        img { max-width: 100%; }
        ul.contains-task-list { list-style: none; padding-left: 1.2em; }
        """;

    private static string Body(string input, Src src) => src switch
    {
        Src.Markdown => Markdown.ToHtml(File.ReadAllText(input), Pipeline),
        _ => $"<pre class=\"plain\">{Escape(File.ReadAllText(input))}</pre>",
    };

    private static string PageCss(bool a4) => $"@page {{ size: {(a4 ? "A4" : "Letter")}; }}";

    /// <summary>
    /// A complete HTML page for Markdown or text. <paramref name="head"/> goes first in the head; what's
    /// handed to the user gets none, so it says nothing about where the original was.
    /// </summary>
    private static string StandaloneHtml(string input, Src src, string head) => $"""
        <!doctype html>
        <html><head>{head}<meta charset="utf-8"><title>{Title(input)}</title>
        <style>{Css}</style></head>
        <body>
        {Body(input, src)}
        </body></html>
        """;

    private static string Title(string input) => Escape(Path.GetFileNameWithoutExtension(input));

    /// <summary>
    /// Only what HTML needs escaped. WebUtility.HtmlEncode also turns every letter beyond ASCII into a
    /// numeric entity, so "Grüße" would read "Gr&amp;#252;&amp;#223;e" in the file.
    /// </summary>
    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>
    /// The temporary copy that WebView2 loads: relative links resolved against the original's folder, and
    /// for printing the paper size. It never leaves the temp folder.
    /// </summary>
    private static string PrintableHtml(string input, Src src, bool? a4)
    {
        var baseHref = new Uri(FolderOf(input) + Path.DirectorySeparatorChar).AbsoluteUri;
        var inject = $"<base href=\"{Escape(baseHref)}\">";
        if (a4 is { } paper) inject += $"<style>{PageCss(paper)}</style>";
        if (src != Src.Html) return StandaloneHtml(input, src, inject);
        var html = File.ReadAllText(input);
        // Without a title of its own, the PDF would be titled after the temp copy, "page.html".
        if (!TitleTag().IsMatch(html)) inject += $"<title>{Title(input)}</title>";
        // Right after <head>, or <html>, or the doctype, so a <base> of the document's own comes later
        // and has no say. In front of the doctype it would put the page in quirks mode.
        var at = HeadTag().Match(html) is { Success: true } head ? head
            : HtmlTag().Match(html) is { Success: true } root ? root
            : Doctype().Match(html);
        return at.Success ? html.Insert(at.Index + at.Length, inject) : inject + html;
    }

    [GeneratedRegex("<head(\\s[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadTag();

    [GeneratedRegex("<html(\\s[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlTag();

    [GeneratedRegex("^\\s*<!doctype[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Doctype();

    [GeneratedRegex("<title[\\s>]", RegexOptions.IgnoreCase)]
    private static partial Regex TitleTag();
}
