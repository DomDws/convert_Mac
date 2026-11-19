using System.Diagnostics;
using Convert.Core;
using ImageMagick;
using Xunit;

namespace Convert.Core.Tests;

/// <summary>Stop works for every kind of conversion, and a stopped or failed one leaves nothing half-written behind.</summary>
public sealed class StopTests : IDisposable
{
    private readonly TestFolder dir = new();

    public void Dispose() => dir.Dispose();

    private static readonly (double, double) Page = (200, 200);
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    private static readonly string WaitFor = Path.Combine(Environment.SystemDirectory, "waitfor.exe");

    private static Target Target(string id) => Targets.Catalogue.Single(t => t.Id == id);

    private string MakePdf(params (double, double)[] pages) => TestPdf.Write(dir.File("doc.pdf"), pages);

    private string MakePng(string name = "photo.png")
    {
        using var img = new MagickImage(MagickColors.DodgerBlue, 16, 16);
        using var f = File.Create(dir.File(name));
        img.Write(f, MagickFormat.Png);
        return dir.File(name);
    }

    // PDF → images

    [Fact]
    public async Task PdfPagesConvertIntoAFolder()
    {
        var outputs = await PdfConv.ConvertAsync(MakePdf(Page, Page, Page), Target("pdf-png"), new ConvSettings(), new Runner(), _ => { });
        Assert.Equal(["doc pages"], outputs.Select(Path.GetFileName));
        Assert.Equal(["page 1.png", "page 2.png", "page 3.png"], Directory.GetFiles(dir.File("doc pages")).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task StoppingPdfPagesEndsWithinOnePageAndRemovesTheFolder()
    {
        var input = MakePdf(Page, Page, Page, Page, Page);
        var runner = new Runner();
        var rendered = 0;
        await Assert.ThrowsAsync<StoppedException>(() =>
            // Stop arrives while the first page is being finished.
            PdfConv.ConvertAsync(input, Target("pdf-png"), new ConvSettings(), runner, _ =>
            {
                rendered++;
                runner.Cancel();
            }));
        Assert.Equal(1, rendered);
        Assert.Equal(["doc.pdf"], dir.Contents);
    }

    [Fact]
    public async Task PdfPagesFailingPartwayRemovesTheFolder()
    {
        // The second page is too small to have a single pixel, so it can't be rendered.
        var input = MakePdf(Page, (0.01, 0.01), Page);
        var rendered = 0;
        var error = await Assert.ThrowsAsync<ConvertException>(() =>
            PdfConv.ConvertAsync(input, Target("pdf-jpg"), new ConvSettings(), new Runner(), _ => rendered++));
        Assert.Equal("Page 2 has no size", error.Message);
        Assert.Equal(1, rendered);
        Assert.Equal(["doc.pdf"], dir.Contents);
    }

    [Fact]
    public async Task StoppedSinglePagePdfLeavesNoFile()
    {
        var input = MakePdf(Page);
        var runner = new Runner();
        runner.Cancel();
        foreach (var id in new[] { "pdf-png", "pdf-txt", "pdf-docx", "pdf-odt", "pdf-rtf" })
            await Assert.ThrowsAsync<StoppedException>(() => PdfConv.ConvertAsync(input, Target(id), new ConvSettings(), runner, _ => { }));
        Assert.Equal(["doc.pdf"], dir.Contents);
    }

    // Images and documents

    [Fact]
    public void StoppedImageConversionLeavesNoFile()
    {
        var input = MakePng();
        var runner = new Runner();
        runner.Cancel();
        Assert.Throws<StoppedException>(() => ImageConv.Convert(input, Target("jpg"), new ConvSettings(), runner));
        Assert.Equal(["photo.png"], dir.Contents);

        // The same call goes through once Stop is cleared for the next batch.
        runner.Reset();
        Assert.Equal("photo.jpg", Path.GetFileName(ImageConv.Convert(input, Target("jpg"), new ConvSettings(), runner)));
        Assert.Equal(["photo.jpg", "photo.png"], dir.Contents);
    }

    [Fact]
    public async Task StoppedDocumentConversionLeavesNoFile()
    {
        var input = dir.Write("notes.txt", "Some notes.\n");
        var runner = new Runner();
        runner.Cancel();
        foreach (var id in new[] { "rtf", "html", "txt", "pdf-doc" })
            await Assert.ThrowsAsync<StoppedException>(() => DocConv.ConvertAsync(input, Target(id), new ConvSettings(), runner));
        Assert.Equal(["notes.txt"], dir.Contents);
    }

    [Fact]
    public async Task FailedConversionOfAFileThatVanishedLeavesNoFile()
    {
        // The original vanished before its turn came (a batch can take a while).
        foreach (var (name, id) in new[] { ("gone.txt", "html"), ("gone.png", "jpg"), ("gone.pdf", "pdf-png"), ("gone.wav", "mp3"), ("gone.ttf", "font-woff") })
        {
            var error = await Assert.ThrowsAsync<ConvertException>(() =>
                Converter.ConvertAsync(dir.File(name), Target(id), new ConvSettings(), new Runner(), _ => { }));
            Assert.Equal("The file no longer exists", error.Message);
        }
        Assert.Empty(dir.Contents);
    }

    // The whole conversion, and external tools

    [Fact]
    public async Task StoppedConversionsThrowStoppedForEveryKind()
    {
        var png = MakePng();
        var pdf = MakePdf(Page, Page);
        var text = dir.Write("notes.txt", "Some notes.\n");
        var wav = dir.Write("tone.wav", TestMedia.Wav());
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var font = dir.File("font.ttf");
        File.Copy(Path.Combine(fonts, "arial.ttf"), font);
        var before = dir.Contents;
        var runner = new Runner();
        runner.Cancel();
        foreach (var (input, id) in new[]
        {
            (png, "jpg"), (png, "pdf-img"), (pdf, "pdf-png"), (pdf, "pdf-txt"), (pdf, "pdf-docx"), (text, "docx"), (text, "pdf-doc"),
            (wav, "mp3"), (wav, "flac"), (font, "font-woff2"), (font, "font-woff"), (font, "font-desktop"),
        })
        {
            await Assert.ThrowsAsync<StoppedException>(() => Converter.ConvertAsync(input, Target(id), new ConvSettings(), runner, _ => { }));
        }
        Assert.Equal(before, dir.Contents);
    }

    [Fact]
    public async Task StopKillsARunningTool()
    {
        var runner = new Runner();
        var started = Stopwatch.StartNew();
        _ = Task.Run(async () =>
        {
            await Task.Delay(300);
            runner.Cancel();
        });
        // cmd starts the tool that does the waiting, as soffice.exe starts soffice.bin: both have to go.
        await Assert.ThrowsAsync<StoppedException>(() => runner.RunAsync(Cmd, ["/c", WaitFor, "/t", "30", "ConvertStopTest"], 0, _ => { }));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"took {started.Elapsed}");
        // And a tool isn't even started once Stop was pressed.
        await Assert.ThrowsAsync<StoppedException>(() => runner.RunAsync(Cmd, ["/c", "exit 0"], 0, _ => { }));
        Assert.Throws<StoppedException>(() => Proc.RunSync(Cmd, ["/c", "exit 0"], runner: runner));
    }

    [Fact]
    public async Task StopEndsAToolThatIsWaitedForToo()
    {
        var runner = new Runner();
        var started = Stopwatch.StartNew();
        _ = Task.Run(async () =>
        {
            await Task.Delay(300);
            runner.Cancel();
        });
        await Assert.ThrowsAsync<StoppedException>(() => Task.Run(() => Proc.RunSync(Cmd, ["/c", WaitFor, "/t", "30", "ConvertStopTest2"], runner: runner)));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"took {started.Elapsed}");
    }

    [Fact]
    public async Task AFailingToolReportsItsLastErrorLine()
    {
        var error = await Assert.ThrowsAsync<ConvertException>(() =>
            new Runner().RunAsync(Cmd, ["/c", "echo first 1>&2 & echo the reason 1>&2 & exit 3"], 0, _ => { }));
        Assert.Equal("the reason", error.Message);
    }

    [Fact]
    public async Task StoppingAVideoMidwayLeavesNoPartialFile()
    {
        if (Tools.FFmpeg is not { } ffmpeg) return;
        // A minute of video takes long enough to encode that Stop arrives in the middle.
        var input = dir.File("long.mp4");
        Proc.RunSync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30:duration=60",
            "-c:v", "mpeg4", "-q:v", "8", input]);
        var runner = new Runner();
        var sawProgress = new TaskCompletionSource();
        var conversion = Converter.ConvertAsync(input, Target("webm"), new ConvSettings(), runner, _ => sawProgress.TrySetResult());
        await Task.WhenAny(sawProgress.Task, conversion);
        Assert.True(File.Exists(dir.File("long.webm")), "the output should be there while it's being written");
        runner.Cancel();
        await Assert.ThrowsAsync<StoppedException>(() => conversion);
        Assert.Equal(["long.mp4"], dir.Contents);
    }
}
