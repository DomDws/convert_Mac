using System.Text;

namespace Convert.Core;

/// <summary>
/// A long selection handed from the context menu to Convert.exe as a file (one path per line),
/// because a command line holds about 32,000 characters and a few hundred paths don't fit.
/// </summary>
/// <remarks>Linked into Convert.Shell as source, like <see cref="MediaKinds"/>: nothing else from Core.</remarks>
public static class SelectionList
{
    private const string Prefix = "convert-";
    private const string Suffix = ".txt";

    /// <summary>Writes the paths to a new file in the temp folder and returns its path.</summary>
    public static string Write(IEnumerable<string> paths)
    {
        var list = Path.Combine(Path.GetTempPath(), $"{Prefix}{Guid.NewGuid():N}{Suffix}");
        File.WriteAllLines(list, paths, new UTF8Encoding(false));
        return list;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is a file <see cref="Write"/> made: in the temp folder, with its
    /// kind of name. Only such a file is deleted after reading. <c>--files</c> can be given any path,
    /// and Convert must not delete a file just because it was pointed at it.
    /// </summary>
    public static bool IsOurs(string path)
    {
        string full, temp;
        try
        {
            full = Path.GetFullPath(path);
            temp = Path.GetFullPath(Path.GetTempPath());
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }

        var dir = Path.GetDirectoryName(full);
        if (dir == null || !string.Equals(Path.TrimEndingDirectorySeparator(dir), Path.TrimEndingDirectorySeparator(temp), StringComparison.OrdinalIgnoreCase))
            return false;
        var name = Path.GetFileName(full);
        if (name.Length != Prefix.Length + 32 + Suffix.Length) return false;
        if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !name.EndsWith(Suffix, StringComparison.Ordinal)) return false;
        return name.AsSpan(Prefix.Length, 32).IndexOfAnyExcept("0123456789abcdef") < 0;
    }

    /// <summary>The paths in a list file. The file is deleted afterwards if it's one of ours.</summary>
    public static List<string> Read(string path)
    {
        var lines = new List<string>();
        try
        {
            lines.AddRange(File.ReadAllLines(path).Where(l => l.Length > 0));
            if (IsOurs(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return lines;
    }
}
