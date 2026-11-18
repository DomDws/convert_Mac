using System.Runtime.InteropServices;
using Convert.Core;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Convert.Core.Tests;

/// <summary>
/// "Move original to Recycle Bin" never deletes for good. Where there is no Recycle Bin for a file
/// (a network share, a file too big for the bin), the file stays where it is.
/// </summary>
public sealed class RecycleTests : IDisposable
{
    private readonly string dir = Paths.TempDir();

    public void Dispose() => Paths.TryDelete(dir);

    /// <summary>The same folder through the PC's own administrative share: a network path, with no Recycle Bin.</summary>
    private string? NetworkPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.Length < 3 || full[1] != ':') return null;
        var unc = $@"\\localhost\{full[0]}${full[2..]}";
        return File.Exists(unc) ? unc : null;
    }

    [Fact]
    public void AFileOnANetworkShareIsLeftWhereItIs()
    {
        var file = Path.Combine(dir, "on a share.txt");
        File.WriteAllText(file, "keep me");
        // Not every PC lets its user reach the administrative share; without one there's nothing to test with.
        if (NetworkPath(file) is not { } shared) return;

        Assert.False(FileOps.Recycle(shared));
        Assert.True(File.Exists(file));
        Assert.Equal("keep me", File.ReadAllText(file));
        Assert.Equal(["on a share.txt"], Directory.GetFileSystemEntries(dir).Select(Path.GetFileName));
    }

    private const long Huge = 4L << 40;

    /// <summary>4 TB on paper, nothing on disk: a sparse file. Bigger than any Recycle Bin's limit.</summary>
    private string? HugeFile()
    {
        var file = Path.Combine(dir, "huge.bin");
        using var f = new FileStream(file, FileMode.CreateNew, FileAccess.ReadWrite);
        if (!DeviceIoControl(f.SafeFileHandle, FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero)) return null;
        try { f.SetLength(Huge); }
        catch (IOException) { return null; }   // a file system that can't hold a file this size
        return file;
    }

    /// <summary>Windows used to stop here with a "permanently delete it?" dialog. Nothing may ask, or delete.</summary>
    private void ExpectLeftAlone(string file, bool checkBinSize)
    {
        var recycle = Task.Run(() => FileOps.Recycle(file, checkBinSize));
        Assert.True(recycle.Wait(TimeSpan.FromSeconds(30)), "Recycle is waiting for an answer to a dialog");
        Assert.False(recycle.Result);
        Assert.True(File.Exists(file));
        Assert.Equal(Huge, new FileInfo(file).Length);
        Assert.Equal(["huge.bin"], Directory.GetFileSystemEntries(dir).Select(Path.GetFileName));
    }

    [Fact]
    public void AFileTooBigForTheRecycleBinIsLeftWhereItIs()
    {
        if (HugeFile() is { } file) ExpectLeftAlone(file, checkBinSize: true);
    }

    [Fact]
    public void AFileWindowsDeletesInsteadOfRecyclingIsPutBack()
    {
        // Without the size check Windows goes ahead and deletes the file; it has to be back afterwards.
        if (HugeFile() is { } file) ExpectLeftAlone(file, checkBinSize: false);
    }

    [Fact]
    public void AMissingFileIsNotRecycled()
    {
        Assert.False(FileOps.Recycle(Path.Combine(dir, "gone.txt")));
    }

    /// <summary>
    /// Puts a small file in this PC's real Recycle Bin, so it only runs when asked for:
    /// set CONVERT_TEST_RECYCLE=1.
    /// </summary>
    [Fact]
    public void ALocalFileGoesToTheRecycleBin()
    {
        if (Environment.GetEnvironmentVariable("CONVERT_TEST_RECYCLE") != "1") return;
        var file = Path.Combine(dir, $"convert-recycle-test-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "restore me");
        Assert.True(FileOps.Recycle(file));
        Assert.Empty(Directory.GetFileSystemEntries(dir));
    }

    private const uint FSCTL_SET_SPARSE = 0x000900C4;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
}
