using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Convert.Core;

public static partial class PdfText
{
    /// <summary>
    /// A PDF font name ("ABCDEF+Arial-BoldMT", "TimesNewRomanPS-ItalicMT", "Helvetica-Oblique")
    /// as the family a document names ("Arial", "Times New Roman", "Helvetica") and whether it's bold
    /// or italic. Word, LibreOffice and RTF want those separately, and only find a font by its family.
    /// <paramref name="bold"/> and <paramref name="italic"/> are what the font's own description says.
    /// </summary>
    public static (string Family, bool Bold, bool Italic) FontFace(string? pdfName, bool bold = false, bool italic = false)
    {
        // A subset (only the letters the PDF uses) has six capitals and a plus sign in front.
        var name = SubsetPrefix().Replace(pdfName ?? "", "");
        var cut = name.IndexOfAny(['-', ',']);
        var family = cut < 0 ? name : name[..cut];
        var style = cut < 0 ? "" : name[(cut + 1)..];
        bold |= BoldStyle().IsMatch(style);
        italic |= ItalicStyle().IsMatch(style);
        family = PostScriptSuffix().Replace(family.Trim(), "");
        if (family.Length == 0) return ("Helvetica", bold, italic);
        // PostScript names leave out the spaces: put them back the way the installed font spells
        // its name, or else before each capital that follows a small letter.
        return (InstalledFamilies.Value.TryGetValue(family.Replace(" ", ""), out var installed) ? installed : CamelCase().Replace(family, " "),
            bold, italic);
    }

    [GeneratedRegex(@"^[A-Z]{6}\+")]
    private static partial Regex SubsetPrefix();

    [GeneratedRegex("bold|black|heavy|(?-i:Bd)$", RegexOptions.IgnoreCase)]
    private static partial Regex BoldStyle();

    // "It" as in Adobe's MinionPro-It and MinionPro-BoldIt.
    [GeneratedRegex("italic|oblique|(?-i:It)$", RegexOptions.IgnoreCase)]
    private static partial Regex ItalicStyle();

    // ArialMT, TimesNewRomanPSMT, TimesNewRomanPS-BoldMT.
    [GeneratedRegex("(?<=[a-z])(PS)?MT$|(?<=[a-z])PS$")]
    private static partial Regex PostScriptSuffix();

    [GeneratedRegex("(?<=[a-z])(?=[A-Z])")]
    private static partial Regex CamelCase();

    /// <summary>The installed fonts' family names, by name without spaces ("TimesNewRoman" → "Times New Roman").</summary>
    private static readonly Lazy<Dictionary<string, string>> InstalledFamilies = new(() =>
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
                // "Cambria & Cambria Math (TrueType)", "Segoe UI Bold (TrueType)": a family with a style
                // is listed too, but a lookup is only ever made with a family.
                foreach (var entry in key?.GetValueNames() ?? [])
                    foreach (var font in FontFileSuffix().Replace(entry, "").Split(" & "))
                        names.TryAdd(font.Trim().Replace(" ", ""), font.Trim());
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return names;
    });

    [GeneratedRegex(@"\s*\([^)]*\)\s*$")]
    private static partial Regex FontFileSuffix();
}
