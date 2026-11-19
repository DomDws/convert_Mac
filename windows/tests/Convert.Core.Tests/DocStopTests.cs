using System.Diagnostics;
using Convert.Core;
using Xunit;
using Xunit.Abstractions;

namespace Convert.Core.Tests;

/// <summary>
/// Stop works for every kind of document conversion and ends what's running, and a stopped or failed
/// conversion leaves nothing behind: no output, no partial PDF, no temp folder.
/// </summary>
[Collection("Documents")]
public sealed class DocStopTests(ITestOutputHelper log) : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("convert-doc-stop-").FullName;
    private readonly string temp = Directory.CreateTempSubdirectory("convert-doc-stop-temp-").FullName;

    public void Dispose()
    {
        Paths.TryDelete(dir);
        Paths.TryDelete(temp);
    }

    private string[] Contents => Directory.EnumerateFileSystemEntries(dir).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal).ToArray();

    private string[] TempContents => Directory.EnumerateFileSystemEntries(temp, "*", SearchOption.AllDirectories).ToArray();

    private Task<string> Convert(string input, string target, Runner runner) =>
        DocConv.ConvertAsync(input, Targets.ById(target)!, new ConvSettings(), runner, new DocOptions { TempParent = temp });

    private string Make(string name)
    {
        var path = Path.Combine(dir, name);
        switch (Path.GetExtension(name))
        {
            case ".docx": DocTestFiles.Docx(path, "Some notes."); break;
            case ".html": File.WriteAllText(path, "<!doctype html><title>Page</title><h1>Page</h1><p>Some notes.</p>"); break;
            case ".md": File.WriteAllText(path, "# Notes\n\nSome **notes**.\n"); break;
            default: File.WriteAllText(path, "Some notes.\n"); break;
        }
        return path;
    }

    public static TheoryData<string, string> Kinds => new()
    {
        { "notes.md", "pdf-doc" }, { "notes.md", "html" }, { "notes.md", "txt" },
        { "notes.txt", "pdf-doc" }, { "notes.txt", "html" }, { "notes.txt", "txt" },
        { "page.html", "pdf-doc" }, { "page.html", "html" },
        { "notes.md", "docx" }, { "page.html", "odt" }, { "notes.txt", "rtf" }, { "letter.docx", "pdf-doc" },
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task StoppedBeforeLeavesNothing(string name, string target)
    {
        var input = Make(name);
        if (DocConv.NeedsLibreOffice(input, Targets.ById(target)!) && Tools.LibreOffice == null) return;
        var runner = new Runner();
        runner.Cancel();
        await Assert.ThrowsAsync<StoppedException>(() => Convert(input, target, runner));
        Assert.Equal([name], Contents);
        Assert.Empty(TempContents);
    }

    /// <summary>Waits until a file or folder with this name shows up in the conversion's temp folder.</summary>
    private async Task WaitForTemp(string name, Task conversion)
    {
        var watch = Stopwatch.StartNew();
        while (!Directory.EnumerateFileSystemEntries(temp, name, SearchOption.AllDirectories).Any())
        {
            Assert.False(conversion.IsCompleted, $"finished before {name} showed up");
            Assert.True(watch.Elapsed < TimeSpan.FromMinutes(2), $"{name} never showed up");
            await Task.Delay(20);
        }
    }

    /// <summary>Stops the conversion, and returns how long it took to end.</summary>
    private static async Task<TimeSpan> Stop(Runner runner, Task conversion)
    {
        Assert.False(conversion.IsCompleted, "finished before Stop");
        var watch = Stopwatch.StartNew();
        runner.Cancel();
        await Assert.ThrowsAsync<StoppedException>(() => conversion);
        return watch.Elapsed;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StoppingWhilePrintingEndsItAndLeavesNothing(bool a4)
    {
        var input = DocTestFiles.LongMarkdown(Path.Combine(dir, "long.md"));
        var runner = new Runner();
        var s = new ConvSettings { PaperA4 = a4 };
        var conversion = DocConv.ConvertAsync(input, Targets.ById("pdf-doc")!, s, runner, new DocOptions { TempParent = temp });
        // The page is loaded once the probe has been: Stop comes while it loads or prints.
        await WaitForTemp("probe.html", conversion);
        await Task.Delay(300);
        var took = await Stop(runner, conversion);
        log.WriteLine($"ended {took.TotalMilliseconds:0} ms after Stop");
        Assert.True(took < TimeSpan.FromSeconds(6), $"took {took}");
        // A PDF the browser was still writing would turn up now.
        await Task.Delay(3000);
        Assert.Equal(["long.md"], Contents);
        Assert.Empty(TempContents);
    }

    /// <summary>
    /// The LibreOffice processes this test started: soffice.exe below this process, and the soffice.bin
    /// below that. (Other LibreOffice processes on the computer aren't ours to look at.)
    /// </summary>
    private static List<Process> OurLibreOffice()
    {
        var parents = Toolhelp.ParentIds();
        bool Ours(int pid)
        {
            for (var hops = 0; hops < 4 && parents.TryGetValue(pid, out var parent); hops++, pid = parent)
                if (parent == Environment.ProcessId) return true;
            return false;
        }
        return [.. Process.GetProcessesByName("soffice").Concat(Process.GetProcessesByName("soffice.bin")).Where(p => Ours(p.Id))];
    }

    [LibreOfficeFact]
    public async Task StoppingLibreOfficeEndsItAndLeavesNothing()
    {
        var input = Make("notes.txt");
        var runner = new Runner();
        var conversion = Convert(input, "docx", runner);
        await WaitForTemp("lo-profile", conversion);
        // Until soffice.bin is up and working.
        var watch = Stopwatch.StartNew();
        List<Process> running;
        while (!(running = OurLibreOffice()).Any(p => p.ProcessName == "soffice.bin"))
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "soffice.bin never started");
            await Task.Delay(50);
        }
        await Task.Delay(500);
        var took = await Stop(runner, conversion);
        log.WriteLine($"ended {took.TotalMilliseconds:0} ms after Stop; was running: {string.Join(", ", running.Select(p => $"{p.ProcessName} {p.Id}"))}");
        Assert.True(took < TimeSpan.FromSeconds(6), $"took {took}");
        foreach (var p in running) Assert.True(p.WaitForExit(2000), $"{p.ProcessName} {p.Id} still running");
        Assert.Equal(["notes.txt"], Contents);
        Assert.Empty(TempContents);
    }

    [LibreOfficeFact]
    public async Task StoppingWhileCleaningHtmlForLibreOfficeLeavesNothing()
    {
        var input = Path.Combine(dir, "long.md");
        DocTestFiles.LongMarkdown(input);
        var runner = new Runner();
        var conversion = Convert(input, "docx", runner);
        await WaitForTemp("probe.html", conversion);
        await Task.Delay(300);
        var took = await Stop(runner, conversion);
        Assert.True(took < TimeSpan.FromSeconds(6), $"took {took}");
        await Task.Delay(1000);
        Assert.Equal(["long.md"], Contents);
        Assert.Empty(TempContents);
    }

    [LibreOfficeFact]
    public async Task FailedLibreOfficeConversionLeavesNothing()
    {
        var input = Path.Combine(dir, "broken.docx");
        File.WriteAllBytes(input, [0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4, 5]);
        var e = await Assert.ThrowsAsync<ConvertException>(() => Convert(input, "pdf-doc", new Runner()));
        log.WriteLine(e.Message);
        Assert.Equal(["broken.docx"], Contents);
        Assert.Empty(TempContents);
    }

    [Theory]
    [InlineData("pdf-doc")]
    [InlineData("html")]
    [InlineData("txt")]
    public async Task AFileThatVanishedSaysSo(string target)
    {
        var input = Path.Combine(dir, "gone.md");
        var e = await Assert.ThrowsAsync<ConvertException>(() => Convert(input, target, new Runner()));
        Assert.Equal("The file no longer exists", e.Message);
        Assert.Empty(Contents);
        Assert.Empty(TempContents);
    }

    /// <summary>
    /// The output's name is taken when the conversion is done, and the file is created only if that name
    /// is still free: one that turned up meanwhile is left alone.
    /// </summary>
    [Fact]
    public async Task AFileThatTurnsUpMeanwhileIsntOverwritten()
    {
        var input = DocTestFiles.LongMarkdown(Path.Combine(dir, "long.md"), sections: 50);
        var conversion = Convert(input, "pdf-doc", new Runner());
        await WaitForTemp("probe.html", conversion);
        File.WriteAllText(Path.Combine(dir, "long.pdf"), "mine");
        var output = await conversion;
        Assert.Equal("long 2.pdf", Path.GetFileName(output));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(dir, "long.pdf")));
        Assert.Empty(TempContents);
    }
}

/// <summary>Which process started which: Process doesn't say.</summary>
internal static class Toolhelp
{
    public static Dictionary<int, int> ParentIds()
    {
        var map = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return map;
        try
        {
            var entry = new ProcessEntry { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ProcessEntry>() };
            for (var ok = Process32First(snapshot, ref entry); ok; ok = Process32Next(snapshot, ref entry))
                map[(int)entry.ProcessId] = (int)entry.ParentProcessId;
        }
        finally { CloseHandle(snapshot); }
        return map;
    }

    private const uint SnapProcess = 2;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "Process32FirstW")]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "Process32NextW")]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}