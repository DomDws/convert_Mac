using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Convert.Core;

namespace Convert.Core.Tests;

/// <summary>A folder of its own for one test, gone afterwards.</summary>
public sealed class TestFolder : IDisposable
{
    public string Path { get; } = Paths.TempDir();

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string Write(string name, byte[] bytes)
    {
        System.IO.File.WriteAllBytes(File(name), bytes);
        return File(name);
    }

    public string Write(string name, string text) => Write(name, Encoding.UTF8.GetBytes(text));

    /// <summary>The names in the folder, sorted.</summary>
    public string[] Contents => Directory.GetFileSystemEntries(Path).Select(p => System.IO.Path.GetFileName(p)!).Order(StringComparer.Ordinal).ToArray();

    public void Dispose()
    {
        TestAccess.SetWritable(Path, true);
        Paths.TryDelete(Path);
    }
}

public static class TestAccess
{
    /// <summary>
    /// Makes a folder refuse new files and folders (or accept them again), the way a read-only share or
    /// a folder without permission does. Works without administrator rights: it's our own folder.
    /// </summary>
    public static void SetWritable(string folder, bool writable)
    {
        var info = new DirectoryInfo(folder);
        if (!info.Exists) return;
        var security = info.GetAccessControl();
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories, AccessControlType.Deny);
        if (writable) security.RemoveAccessRule(rule);
        else security.AddAccessRule(rule);
        info.SetAccessControl(security);
    }
}

public static class TestPdf
{
    /// <summary>
    /// Writes a PDF with one page per given size, in points. Each page has a grey square in its lower
    /// left quarter and the words "Page N".
    /// </summary>
    public static string Write(string path, params (double Width, double Height)[] pages)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Count {pages.Length} /Kids [{string.Join(" ", pages.Select((_, i) => $"{4 + 2 * i} 0 R"))}] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        for (var i = 0; i < pages.Length; i++)
        {
            var (w, h) = pages[i];
            var content = string.Create(CultureInfo.InvariantCulture,
                $"0.5 g 0 0 {w / 2:0.###} {h / 2:0.###} re f BT /F1 12 Tf 10 10 Td (Page {i + 1}) Tj ET");
            objects.Add(string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w:0.###} {h:0.###}] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + 2 * i} 0 R >>"));
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append(CultureInfo.InvariantCulture, $"{offset:0000000000} 00000 n \n");
        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, pdf.ToString(), Encoding.ASCII);
        return path;
    }
}

public static class TestMedia
{
    /// <summary>A short 16-bit mono WAV: silence, but a real file.</summary>
    public static byte[] Wav(int samples = 4410)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + samples * 2);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);      // PCM
        w.Write((short)1);      // mono
        w.Write(44100);
        w.Write(44100 * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(samples * 2);
        w.Write(new byte[samples * 2]);
        return ms.ToArray();
    }
}
