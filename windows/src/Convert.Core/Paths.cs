using System.Runtime.InteropServices;

namespace Convert.Core;

/// <summary>Where converted files go: next to the original, or a fallback folder when that isn't possible.</summary>
public static class OutputFolder
{
    /// <summary>
    /// Used when the original's folder can't be written to (a read-only share, a disc or mounted ISO,
    /// no permission). Downloads; <c>CONVERT_FALLBACK_DIR</c> replaces it for tests.
    /// </summary>
    public static string Fallback
    {
        get
        {
            if (Environment.GetEnvironmentVariable("CONVERT_FALLBACK_DIR") is { Length: > 0 } dir) return Path.GetFullPath(dir);
            if (SHGetKnownFolderPath(Downloads, 0, IntPtr.Zero, out var path) == 0 && path != IntPtr.Zero)
            {
                try { return Marshal.PtrToStringUni(path)!; }
                finally { Marshal.FreeCoTaskMem(path); }
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    /// <summary>The folder to write a conversion of <paramref name="input"/> into.</summary>
    public static string For(string input, string? fallback = null)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(input))!;
        if (IsWritable(dir)) return dir;
        fallback ??= Fallback;
        if (!IsWritable(fallback))
            throw new ConvertException(
                $"Can't save the converted file: the original's folder is read-only, and “{Name(fallback)}” can't be written to either");
        return fallback;
    }

    /// <summary>
    /// Whether a file can be created in <paramref name="dir"/>. The only way to know is to try:
    /// the read-only attribute means nothing on folders, and permissions don't know about write-protected
    /// media or what a file server will allow.
    /// </summary>
    public static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".convert-{Guid.NewGuid():N}.tmp");
            using var f = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
                                      or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>Whether <paramref name="output"/> was written somewhere other than next to <paramref name="input"/>.</summary>
    public static bool IsFallback(string output, string input) =>
        !string.Equals(Folder(output), Folder(input), StringComparison.OrdinalIgnoreCase);

    private static string Folder(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "");

    /// <summary>The folder's name as Explorer shows it.</summary>
    public static string Name(string folder)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
        return name.Length > 0 ? name : folder;
    }

    private static readonly Guid Downloads = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
}

public static class Paths
{
    /// <summary>Next free file name next to the input (or in the fallback folder): "name.ext", then "name 2.ext", …</summary>
    public static string UniqueOutput(string input, string ext, string suffix = "", string? fallback = null)
    {
        var dir = OutputFolder.For(input, fallback);
        var name = Path.GetFileNameWithoutExtension(input) + suffix;
        var path = Path.Combine(dir, $"{name}.{ext}");
        for (var n = 2; File.Exists(path) || Directory.Exists(path); n++)
            path = Path.Combine(dir, $"{name} {n}.{ext}");
        return path;
    }

    public static string UniqueFolder(string input, string suffix, string? fallback = null)
    {
        var dir = OutputFolder.For(input, fallback);
        var name = Path.GetFileNameWithoutExtension(input) + suffix;
        var path = Path.Combine(dir, name);
        for (var n = 2; File.Exists(path) || Directory.Exists(path); n++)
            path = Path.Combine(dir, $"{name} {n}");
        return path;
    }

    /// <summary>
    /// Creates the output file for writing. Fails if something took the name since it was chosen,
    /// so nothing is ever overwritten.
    /// </summary>
    public static FileStream Create(string path) => new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

    /// <summary>
    /// Writes a new output file. If writing fails or is stopped partway, the file is removed again;
    /// if the name was taken in the meantime, nothing is touched.
    /// </summary>
    public static void WriteNew(string path, Action<FileStream> write)
    {
        var file = Create(path);
        try
        {
            using (file) write(file);
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    /// <inheritdoc cref="WriteNew(string, Action{FileStream})"/>
    public static async Task WriteNewAsync(string path, Func<FileStream, Task> write)
    {
        var file = Create(path);
        try
        {
            await using (file) await write(file);
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    public static void WriteNew(string path, byte[] bytes) => WriteNew(path, f => f.Write(bytes));

    /// <summary>Convert's own temp folder. Each conversion that needs one gets a folder of its own in it.</summary>
    public static string TempRoot => Path.Combine(Path.GetTempPath(), "Convert");

    public static string TempDir()
    {
        var d = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    public static void TryDelete(string? path)
    {
        if (path == null) return;
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
