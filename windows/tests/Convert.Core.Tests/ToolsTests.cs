using Convert.Core;
using Xunit;

namespace Convert.Core.Tests;

/// <summary>
/// Running the external tools (ffmpeg, ffprobe, soffice): finding them, reading their output, and
/// coping when one of them can't do what was asked.
/// </summary>
public sealed class ToolsTests : IDisposable
{
    private readonly TestFolder dir = new();

    public void Dispose() => dir.Dispose();

    private static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    /// <summary>
    /// A stand-in for a chatty tool: writes 1 MB to stderr (far more than a pipe holds), then one
    /// last line, and exits with <paramref name="status"/>.
    /// </summary>
    private static string[] Chatty(string lastLine, int status) =>
    [
        "-NoProfile", "-NonInteractive", "-Command",
        $"[Console]::Error.WriteLine('x' * 1048576); [Console]::Out.WriteLine('to stdout'); [Console]::Error.WriteLine('{lastLine}'); exit {status}",
    ];

    // Finding tools

    [Fact]
    public void AToolInstalledLaterIsFoundWithoutARestart()
    {
        Assert.Null(Tools.Find("ffmpeg.exe", [dir.Path]));
        // "winget install Gyan.FFmpeg" happens while the app is open.
        var tool = dir.Write("ffmpeg.exe", "not really");
        Assert.Equal(tool, Tools.Find("ffmpeg.exe", [dir.Path]));
        // And uninstalled again.
        File.Delete(tool);
        Assert.Null(Tools.Find("ffmpeg.exe", [dir.Path]));
    }

    [Fact]
    public void FirstDirectoryWins()
    {
        var other = Directory.CreateDirectory(dir.File("other")).FullName;
        dir.Write("soffice.exe", "x");
        File.WriteAllText(Path.Combine(other, "soffice.exe"), "x");
        Assert.Equal(Path.Combine(other, "soffice.exe"), Tools.Find("soffice.exe", [other, dir.Path]));
        Assert.Equal(dir.File("soffice.exe"), Tools.Find("soffice.exe", [dir.File("missing"), dir.Path, other]));
    }

    [Fact]
    public void AToolInTheCurrentFolderIsNotPickedUp()
    {
        // PATH can hold "." or an empty entry: the folder Convert was started in, which is where the
        // files to convert are. Whoever sent the files could have put an ffmpeg.exe next to them.
        dir.Write("ffmpeg.exe", "planted");
        var before = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = dir.Path;
            Assert.Null(Tools.Find("ffmpeg.exe", [".", "", "tools", @"\"]));
        }
        finally { Environment.CurrentDirectory = before; }
    }

    [Fact]
    public void TheSearchStartsNextToTheAppAndCoversThePath()
    {
        var dirs = Tools.Directories().ToList();
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "tools"), dirs[0]);
        Assert.Contains(Environment.SystemDirectory, dirs, StringComparer.OrdinalIgnoreCase);
        Assert.EndsWith(@"Microsoft\WinGet\Links", dirs[^1]);
    }

    [Fact]
    public void AToolThatChangesIsADifferentTool()
    {
        // What was found out about a tool (its encoders, whether the GPU works with it) is asked again when this changes.
        var tool = dir.Write("ffmpeg.exe", "version one");
        var first = Tools.Stamp(tool);
        Assert.Equal(first, Tools.Stamp(tool));
        File.WriteAllText(tool, "version two, longer");
        Assert.NotEqual(first, Tools.Stamp(tool));
    }

    [Fact]
    public void OfferedFormatsAreTheMacsMinusWhatThisPcCantWrite()
    {
        // The Mac's list at 0.4.0 (Sources/Convert/Formats.swift), in its order.
        string[] mac =
        [
            "mp4-h264", "mp4-hevc", "mov-prores", "webm", "mkv", "mp4-av1", "gif",
            "mp3", "m4a", "he-aac", "wav", "aiff", "flac", "alac", "opus", "ogg", "wma", "ac3", "wv", "mp2",
            "jpg", "png", "heic", "avif", "webp", "tiff", "gif-img", "bmp", "pdf-img",
            "pdf-doc", "docx", "doc", "odt", "rtf", "txt", "html",
            "pdf-png", "pdf-jpg", "pdf-docx", "pdf-odt", "pdf-rtf", "pdf-txt",
            "font-woff2", "font-woff", "font-desktop",
        ];
        Assert.Equal(mac, Targets.Catalogue.Select(t => t.Id));
        // Only these may be missing here, and only for the reasons windows/README.md gives.
        string[] mayBeHidden = ["he-aac", "heic", "avif", "webp"];
        var offered = Targets.All.Select(t => t.Id).ToList();
        Assert.Equal(mac.Where(offered.Contains), offered);
        Assert.Subset(mayBeHidden.ToHashSet(), mac.Except(offered).ToHashSet());
        Assert.Equal(Tools.HasEncoder("libfdk_aac"), offered.Contains("he-aac"));
    }

    // Reading output

    [Fact(Timeout = 60_000)]
    public async Task AToolThatFloodsStderrStillFinishes()
    {
        var stdout = await Task.Run(() => Proc.RunSync(PowerShell, Chatty("done", 0)));
        Assert.Equal("to stdout", stdout.Trim());
    }

    [Fact(Timeout = 60_000)]
    public async Task AFloodedFailureStillReportsItsLastLine()
    {
        var error = await Assert.ThrowsAsync<ConvertException>(() => Task.Run(() => Proc.RunSync(PowerShell, Chatty("the actual reason", 2))));
        Assert.Equal("the actual reason", error.Message);
    }

    [Fact(Timeout = 60_000)]
    public async Task TheProgressRunnerCopesWithAFloodToo()
    {
        await new Runner().RunAsync(PowerShell, Chatty("done", 0), 0, _ => { });
        var error = await Assert.ThrowsAsync<ConvertException>(() => new Runner().RunAsync(PowerShell, Chatty("the actual reason", 2), 0, _ => { }));
        Assert.Equal("the actual reason", error.Message);
    }

    [Fact]
    public async Task OnlyTheEndOfTheErrorOutputIsKept()
    {
        using var flood = new StringReader(new string('x', 5 * Proc.KeptErrorOutput) + "\nthe last line\n");
        var kept = await Proc.ReadTailAsync(flood);
        Assert.Equal(Proc.KeptErrorOutput, kept.Length);
        Assert.Equal("the last line", Proc.LastLine(kept));
    }

    [Fact]
    public void AToolWithNoErrorOutputGetsAGenericMessage()
    {
        var error = Assert.Throws<ConvertException>(() => Proc.RunSync(Cmd, ["/c", "exit 1"]));
        Assert.Equal("cmd failed", error.Message);
    }

    [Fact]
    public void AToolThatIsNotThereIsAnErrorNotACrash()
    {
        Assert.Throws<ConvertException>(() => Proc.RunSync(dir.File("missing.exe"), []));
    }

    // ffmpeg progress

    private const string Report = """
        frame=10
        out_time_us=2500000
        progress=continue
        frame=20
        out_time_us=5000000
        out_time_us=N/A
        progress=continue
        out_time_us=12000000
        progress=end

        """;

    private static readonly double[] Expected = [0.25, 0.5, 1];

    [Fact]
    public void ProgressFromWholeLines()
    {
        Assert.Equal(Expected, new ProgressParser(10).Feed(Report));
        // ffmpeg on Windows ends its lines the Windows way.
        Assert.Equal(Expected, new ProgressParser(10).Feed(Report.ReplaceLineEndings("\r\n")));
    }

    [Fact]
    public void ProgressLinesSplitAcrossReadsAreNotLost()
    {
        var text = Report.ReplaceLineEndings("\n");
        // Every possible chunk size, including one character at a time: the result never changes.
        for (var size = 1; size <= text.Length; size++)
        {
            var parser = new ProgressParser(10);
            var fractions = new List<double>();
            for (var start = 0; start < text.Length; start += size)
                fractions.AddRange(parser.Feed(text.AsSpan(start, Math.Min(size, text.Length - start))));
            Assert.Equal(Expected, fractions);
        }
    }

    [Fact]
    public void ProgressNeedsAKnownDurationAndACompleteLine()
    {
        Assert.Empty(new ProgressParser(0).Feed(Report));
        var parser = new ProgressParser(10);
        Assert.Empty(parser.Feed("out_time_us=5000000"));   // no newline yet
        Assert.Equal([0.5], parser.Feed("\n"));
    }

    // Hardware encoder fallback

    private sealed class FallbackFailure(List<bool> attempts, Exception error) : Exception(error.Message, error)
    {
        public List<bool> Attempts { get; } = attempts;
    }

    /// <summary>The <c>hardware</c> setting of each encode attempt, in order.</summary>
    private static async Task<List<bool>> Encode(string id, bool hardware, Func<ConvSettings, bool> failing, Runner? runner = null)
    {
        var target = Targets.Catalogue.Single(t => t.Id == id);
        var attempts = new List<bool>();
        try
        {
            await Converter.WithSoftwareFallback(target, new ConvSettings { Hardware = hardware }, runner ?? new Runner(), s =>
            {
                attempts.Add(s.Hardware);
                return failing(s) ? Task.FromException(new ConvertException("Error while opening encoder")) : Task.CompletedTask;
            });
        }
        catch (Exception e) { throw new FallbackFailure(attempts, e); }
        return attempts;
    }

    [Fact]
    public async Task HardwareEncoderFailingFallsBackToSoftware()
    {
        // The hardware attempt fails, the software one works: the conversion succeeds.
        // (No ProRes here: unlike on the Mac, it has no hardware encoder on Windows.)
        foreach (var id in new[] { "mp4-h264", "mp4-hevc", "mkv" })
            Assert.Equal([true, false], await Encode(id, hardware: true, s => s.Hardware));
    }

    [Fact]
    public async Task NoRetryWhenTheFirstAttemptWorks()
    {
        Assert.Equal([true], await Encode("mp4-h264", hardware: true, _ => false));
    }

    [Fact]
    public async Task NoRetryWithoutAHardwareEncoderInPlay()
    {
        // Hardware already off, and formats that never had it: one attempt, and its error is reported.
        foreach (var (id, hardware) in new[] { ("mp4-h264", false), ("webm", true), ("mp3", true), ("mov-prores", true) })
        {
            var failure = await Assert.ThrowsAsync<FallbackFailure>(() => Encode(id, hardware, _ => true));
            Assert.Equal([hardware], failure.Attempts);
        }
    }

    [Fact]
    public async Task SoftwareFailureIsReportedWhenBothFail()
    {
        var failure = await Assert.ThrowsAsync<FallbackFailure>(() => Encode("mp4-hevc", hardware: true, _ => true));
        Assert.Equal([true, false], failure.Attempts);
    }

    [Fact]
    public async Task NoRetryAfterStop()
    {
        var runner = new Runner();
        var attempts = new List<bool>();
        await Assert.ThrowsAsync<StoppedException>(() =>
            Converter.WithSoftwareFallback(Targets.Catalogue.Single(t => t.Id == "mp4-h264"), new ConvSettings(), runner, s =>
            {
                attempts.Add(s.Hardware);
                runner.Cancel();
                return Task.FromException(new StoppedException());
            }));
        Assert.Equal([true], attempts);
    }
}
