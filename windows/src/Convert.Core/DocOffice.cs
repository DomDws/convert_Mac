using System.Text;

namespace Convert.Core;

public static partial class DocConv
{
    /// <summary>The longest LibreOffice may take for one file.</summary>
    private const int LibreOfficeLimitMs = 180_000;

    /// <summary>
    /// Settings for the throwaway LibreOffice profile each conversion gets. LibreOffice has no offline
    /// switch; its web traffic (a linked stylesheet in HTML, say) honours this proxy, which isn't there.
    /// The proxy doesn't cover <c>file://server/…</c> links: those are why HTML is cleaned first.
    /// </summary>
    private static string ProfileSettings(DocGuards guards)
    {
        var items = new StringBuilder();
        void Set(string path, string name, string value) =>
            items.Append($"<item oor:path=\"{path}\"><prop oor:name=\"{name}\" oor:op=\"fuse\"><value>{value}</value></prop></item>\n");
        if (guards.HasFlag(DocGuards.OfficeProxy))
        {
            const string inet = "/org.openoffice.Inet/Settings";
            Set(inet, "ooInetProxyType", "2");   // manual
            Set(inet, "ooInetHTTPProxyName", "127.0.0.1");
            Set(inet, "ooInetHTTPProxyPort", "1");
            Set(inet, "ooInetHTTPSProxyName", "127.0.0.1");
            Set(inet, "ooInetHTTPSProxyPort", "1");
            Set(inet, "ooInetNoProxy", "");   // loopback too
        }
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <oor:items xmlns:oor="http://openoffice.org/2001/registry" xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
            {items}</oor:items>
            """;
    }

    private static async Task<string> LibreOfficeAsync(string feed, Src src, Target t, string tmp, Runner runner, DocGuards guards)
    {
        var soffice = Tools.LibreOffice ?? throw new ConvertException(LibreOfficeHint);
        var (ext, filter) = t.Id switch
        {
            "pdf-doc" => ("pdf", "pdf:writer_pdf_Export"),
            "docx" => ("docx", "docx:MS Word 2007 XML"),
            "doc" => ("doc", "doc:MS Word 97"),
            "odt" => ("odt", "odt:writer8"),
            "rtf" => ("rtf", "rtf:Rich Text Format"),
            "txt" => ("txt", "txt:Text (encoded):UTF8"),
            "html" => ("html", "html:XHTML Writer File:UTF8"),
            _ => throw new ConvertException($"Can't convert documents to {t.Label}"),
        };
        var profile = Path.Combine(tmp, "lo-profile");
        Directory.CreateDirectory(Path.Combine(profile, "user"));
        File.WriteAllText(Path.Combine(profile, "user", "registrymodifications.xcu"), ProfileSettings(guards), Utf8);
        var outDir = Directory.CreateDirectory(Path.Combine(tmp, "out")).FullName;
        var args = new List<string>
        {
            "--headless", "--norestore", "--nologo", "--nodefault", "--nolockcheck",
            $"-env:UserInstallation={new Uri(profile).AbsoluteUri}",
        };
        // Without these, HTML opens in Writer/Web and plain text guesses its encoding.
        if (src == Src.Html) args.Add("--infilter=HTML (StarWriter)");
        if (src == Src.Text) args.Add("--infilter=Text (encoded):UTF8");
        args.AddRange(["--convert-to", filter, "--outdir", outDir, feed]);

        // With the runner, Stop ends soffice.exe and the soffice.bin it started.
        await Task.Run(() => Proc.RunSync(soffice, args, LibreOfficeLimitMs, runner));
        return Directory.EnumerateFiles(outDir, "*." + ext).FirstOrDefault()
               ?? throw new ConvertException($"LibreOffice couldn't convert this file to {t.Label}");
    }
}
