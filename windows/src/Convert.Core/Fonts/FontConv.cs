using System.Globalization;
using System.Text;

namespace Convert.Core;

/// <summary>Fonts: between desktop fonts (.ttf, .otf, and .ttc/.otc collections) and web fonts (WOFF, WOFF2).</summary>
/// <remarks>
/// These are all the same font tables in different wrappers, so converting is lossless and needs
/// no external tool. What it doesn't do is turn TrueType outlines into CFF ones or back: a font
/// that comes out of a web font is a .ttf or an .otf depending on which it was made from.
/// Mirrors Sources/Convert/FontConv.swift.
/// </remarks>
public static class FontConv
{
    /// <summary>The fonts a file holds, whatever its name says: one, or several for a collection.</summary>
    public static List<Sfnt> Read(byte[] bytes)
    {
        if (bytes.Length < 4) throw FontError.NotAFont();
        var magic = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
        if (magic == Woff.Signature) return [Woff.Decode(bytes)];
        if (magic == Woff2.Signature) return [Woff2.Decode(bytes)];
        return Sfnt.Fonts(bytes);
    }

    /// <summary>One font in the target's format, and the extension that goes with it.</summary>
    public static (byte[] Bytes, string Ext) Write(Sfnt font, Target t, Runner runner)
    {
        switch (t.Id)
        {
            case "font-woff2":
                var prepared = Woff2.Prepare(font, runner);
                runner.CheckCancelled();
                return (Woff2.Assemble(prepared, Brotli.Best(prepared.Stream, runner)), "woff2");
            case "font-woff":
                return (Woff.Encode(font, runner), "woff");
            default:
                return (font.Data(), font.FileExtension);
        }
    }

    /// <summary>
    /// Converts a font file. A collection becomes a folder with one file per font, named after the fonts.
    /// A font already in the target format is written again (recompressed, or a clean copy), like
    /// every other kind of file.
    /// </summary>
    public static List<string> Convert(string input, Target t, Runner runner)
    {
        runner.CheckCancelled();
        byte[] data;
        try { data = File.ReadAllBytes(input); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
                                      or System.Security.SecurityException)
        {
            throw new ConvertException("Couldn't open this font");
        }
        try
        {
            return Convert(input, data, t, runner);
        }
        catch (Exception e) when (e is OverflowException or IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
        {
            // Every read is checked, so this would be a mistake in the code rather than in the file;
            // still, the file is what can't be converted.
            throw FontError.Damaged();
        }
    }

    private static List<string> Convert(string input, byte[] data, Target t, Runner runner)
    {
        var fonts = Read(data);
        if (fonts.Count == 1)
        {
            var (bytes, ext) = Write(fonts[0], t, runner);
            runner.CheckCancelled();
            var out_ = Paths.UniqueOutput(input, ext);
            Paths.WriteNew(out_, bytes);
            return [out_];
        }

        // The fonts are written into a folder of their own that only gets its name once all of them
        // are in: a failed or stopped conversion leaves nothing, and nothing is ever written into a
        // folder that was already there. One font is held in memory at a time.
        var folder = Paths.UniqueFolder(input, " fonts");
        var parent = Path.GetDirectoryName(folder)!;
        var work = Path.Combine(parent, $".convert-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < fonts.Count; i++)
            {
                runner.CheckCancelled();
                var (bytes, ext) = Write(fonts[i], t, runner);
                var base_ = FileName(fonts[i].Name) ?? $"{Path.GetFileNameWithoutExtension(input)} {i + 1}";
                // Two fonts can share a name (or one can be called what another was renamed to).
                var name = base_;
                for (var n = 2; !used.Add(name); n++) name = $"{base_} {n}";
                Paths.WriteNew(Path.Combine(work, $"{name}.{ext}"), bytes);
            }
            runner.CheckCancelled();
            // Moving fails rather than merge with a folder of that name made in the meantime;
            // then the next free name is taken.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.Move(work, folder);
                    return [folder];
                }
                catch (IOException) when (attempt < 10 && (Directory.Exists(folder) || File.Exists(folder)))
                {
                    folder = Paths.UniqueFolder(input, " fonts");
                }
            }
        }
        finally
        {
            Paths.TryDelete(work);
        }
    }

    private static readonly string[] DeviceNames =
    [
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        .. Enumerable.Range(0, 10).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" }),
        "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³",
    ];

    /// <summary>
    /// A font's name as a file name: nothing a Windows file name can't have, null when nothing is left.
    /// Windows also refuses names ending in a dot or space, and device names such as <c>CON</c>
    /// (with any extension), which go in with a <c>_</c> in front.
    /// </summary>
    public static string? FileName(string? name)
    {
        if (name == null) return null;
        var safe = new StringBuilder(name.Length);
        foreach (var c in name.EnumerateRunes())
        {
            if (IsUnsafe(c)) safe.Append('-');
            else safe.Append(c.ToString());
        }
        var text = Trim(safe.ToString());
        if (text.Length == 0) return null;
        // At most 100 characters as they're seen, so an accent or emoji isn't cut in two.
        var info = new StringInfo(text);
        if (info.LengthInTextElements > 100) text = Trim(info.SubstringByTextElements(0, 100));
        var stem = text.Split('.')[0].TrimEnd(' ');
        if (DeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) text = "_" + text;
        return text;
    }

    /// <summary>The Mac's list (<c>/ : \</c>, control and format characters, line breaks) and what else Windows refuses.</summary>
    private static bool IsUnsafe(Rune c) =>
        c.Value is '/' or ':' or '\\' or '<' or '>' or '"' or '|' or '?' or '*' or 0x85 or 0x2028 or 0x2029
        || Rune.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format;

    /// <summary>Spaces and dots off both ends.</summary>
    private static string Trim(string s)
    {
        static bool Trimmed(char c) => c == '.' || char.IsWhiteSpace(c);
        int start = 0, end = s.Length;
        while (start < end && Trimmed(s[start])) start++;
        while (end > start && Trimmed(s[end - 1])) end--;
        return s[start..end];
    }
}
