using System.Collections.Concurrent;
using System.Diagnostics;
using Convert.Core;
using Xunit;
using Xunit.Abstractions;
using static Convert.Core.Tests.FontTestKit;

namespace Convert.Core.Tests;

/// <summary>The fonts that come with Windows, so the tests don't depend on font files in the repository.</summary>
public static class SystemFonts
{
    public static readonly string Folder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    public static IEnumerable<string> All =>
        Directory.Exists(Folder)
            ? Directory.EnumerateFiles(Folder).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc" or ".otc")
                .Order(StringComparer.OrdinalIgnoreCase)
            : [];

    /// <summary>
    /// The first of <paramref name="names"/> that's installed; <c>*.ext</c> stands for any font of that
    /// kind, the smallest first, so a Windows without the usual fonts still gets tested.
    /// </summary>
    public static string? Pick(params string[] names)
    {
        foreach (var name in names)
        {
            if (name.StartsWith("*.", StringComparison.Ordinal))
            {
                var any = All.Where(f => Path.GetExtension(f).Equals(name[1..], StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => new FileInfo(f).Length).FirstOrDefault();
                if (any != null) return any;
            }
            else if (File.Exists(Path.Combine(Folder, name)))
            {
                return Path.Combine(Folder, name);
            }
        }
        return null;
    }
}

/// <summary>A test that needs one of these system fonts, and is skipped (visibly) when none is installed.</summary>
public sealed class SystemFontFactAttribute : FactAttribute
{
    public SystemFontFactAttribute(params string[] names)
    {
        if (SystemFonts.Pick(names) == null) Skip = $"None of {string.Join(", ", names)} is installed in {SystemFonts.Folder}";
    }
}

/// <summary>Every installed font: slow, so it only runs with <c>CONVERT_FONT_SWEEP=1</c>.</summary>
public sealed class FontSweepFactAttribute : FactAttribute
{
    public FontSweepFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CONVERT_FONT_SWEEP") != "1") Skip = "Set CONVERT_FONT_SWEEP=1 to convert every installed font";
    }
}

public sealed class FontSystemTests(ITestOutputHelper output) : IDisposable
{
    private readonly string dir = Paths.TempDir();

    public void Dispose() => Paths.TryDelete(dir);

    private List<string> Contents => [.. Directory.EnumerateFileSystemEntries(dir).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal)];

    private const string TrueTypes = "arial.ttf";
    private const string Collections = "cambria.ttc";

    /// <summary>What a round trip found out about one font.</summary>
    private sealed record Result(bool Transformed);

    private static void Same(Sfnt want, Sfnt got, string what, bool exact)
    {
        Assert.True(want.Flavor == got.Flavor, $"{what}: flavor");
        if (exact) Assert.True(Tags(want).SequenceEqual(Tags(got)), $"{what}: table order");
        else Assert.True(Tags(want).ToHashSet().SetEquals(Tags(got)), $"{what}: tables");
        // The whole-file checksum in `head` belongs to the file the font was last written to.
        foreach (var t in want.Tables)
        {
            Assert.True(got[t.Tag] is { } data && (t.Tag == "head" ? ZeroedHead(t).AsSpan().SequenceEqual(ZeroedHead(t with { Data = data }))
                : data.AsSpan().SequenceEqual(t.Data)), $"{what}: {t.Tag} differs");
        }
    }

    /// <summary>A font written as a .ttf, read again; the file's checksum is right.</summary>
    private static Sfnt AsFile(Sfnt font)
    {
        var bytes = font.Data();
        if (font["head"] is { Length: >= 12 }) Assert.Equal(0xB1B0_AFBAu, Sum32(bytes));
        return Sfnt.Fonts(bytes)[0];
    }

    /// <summary>
    /// TTF → WOFF → TTF and TTF → WOFF2 → TTF, with every table required to match the original:
    /// exactly through WOFF, and through WOFF2 the glyph tables as glyphs when they were repacked.
    /// </summary>
    private static Result RoundTrip(Sfnt original, string what, byte[]? woff2 = null)
    {
        var fromWoff = Woff.Decode(Woff.Encode(original));
        Same(original, fromWoff, $"{what} via WOFF", exact: true);
        Same(original, AsFile(fromWoff), $"{what} via WOFF as .ttf", exact: true);

        var p = Woff2.Prepare(original);
        woff2 ??= Woff2.Assemble(p, Brotli.Encode(p.Stream, FastBrotli));
        var transformed = p.Entries.Any(e => e.Tag == "glyf" && e.Version == 0);
        var tables = original.Tables.Where(t => t.Tag != "DSIG").Select(t =>
        {
            if (t.Tag != "head" || t.Data.Length < 18) return t;
            var head = (byte[])t.Data.Clone();
            head[16] |= 0x08;
            return t with { Data = head };
        });
        var want = new Sfnt(original.Flavor, [.. tables.Where(t => !transformed || (t.Tag != "glyf" && t.Tag != "loca"))]);
        var fromWoff2 = Woff2.Decode(woff2);
        foreach (var (font, how) in new[] { (fromWoff2, "via WOFF2"), (AsFile(fromWoff2), "via WOFF2 as .ttf") })
        {
            Same(want, new Sfnt(font.Flavor, [.. font.Tables.Where(t => want[t.Tag] != null)]), $"{what} {how}", exact: false);
            Assert.True(font.Tables.Count == want.Tables.Count + (transformed ? 2 : 0), $"{what} {how}: table count");
            if (!transformed) continue;
            var (a, b) = (GlyphOffsets(original), GlyphOffsets(font));
            Assert.True(a.Length == b.Length, $"{what} {how}: glyph count");
            for (var g = 0; g < a.Length - 1; g++)
            {
                var (x, y) = (ParseGlyph(original["glyf"]!, a, g), ParseGlyph(font["glyf"]!, b, g));
                Assert.True(x == y, $"{what} {how}: glyph {g} differs\n{x}\n{y}");
            }
            Assert.True(LocaIsValid(font), $"{what} {how}: loca");
        }
        return new Result(transformed);
    }

    [SystemFontFact(TrueTypes, "segoeui.ttf", "times.ttf", "*.ttf")]
    public void TrueTypeFontBecomesWebFontsAndBack()
    {
        // The real conversions, Brotli at its best included, from a copy (C:\Windows\Fonts isn't writable).
        var input = Path.Combine(dir, "Font.ttf");
        File.Copy(SystemFonts.Pick(TrueTypes, "segoeui.ttf", "times.ttf", "*.ttf")!, input);
        var original = Sfnt.Fonts(File.ReadAllBytes(input))[0];
        var woff2 = FontConv.Convert(input, Targets.ById("font-woff2")!, new Runner()).Single();
        var woff = FontConv.Convert(input, Targets.ById("font-woff")!, new Runner()).Single();
        Assert.True(RoundTrip(original, "font", File.ReadAllBytes(woff2)).Transformed);
        Same(original, FontConv.Read(File.ReadAllBytes(woff))[0], "WOFF file", exact: true);
        var fromWoff2 = FontConv.Convert(woff2, Targets.ById("font-desktop")!, new Runner()).Single();
        var fromWoff = FontConv.Convert(woff, Targets.ById("font-desktop")!, new Runner()).Single();
        Assert.Equal(["Font 2.ttf", "Font 3.ttf", "Font.ttf", "Font.woff", "Font.woff2"], Contents);
        // Unwrapping WOFF gives back the font as it went in.
        Assert.Equal(original.Data(), File.ReadAllBytes(fromWoff));
        Assert.Equal("Font 2.ttf", Path.GetFileName(fromWoff2));
        output.WriteLine($"{Path.GetFileName(SystemFonts.Pick(TrueTypes, "*.ttf"))}: {new FileInfo(input).Length} bytes, WOFF {new FileInfo(woff).Length}, WOFF2 {new FileInfo(woff2).Length}");
    }

    [SystemFontFact(Collections, "msgothic.ttc", "*.ttc")]
    public void CollectionBecomesAFolderOfFontsThatRoundTrip()
    {
        var input = Path.Combine(dir, "Family.ttc");
        File.Copy(SystemFonts.Pick(Collections, "msgothic.ttc", "*.ttc")!, input);
        var fonts = FontConv.Read(File.ReadAllBytes(input));
        Assert.True(fonts.Count > 1);
        for (var i = 0; i < fonts.Count; i++) RoundTrip(fonts[i], $"font {i}");
        var folder = FontConv.Convert(input, Targets.ById("font-desktop")!, new Runner()).Single();
        Assert.Equal(["Family fonts", "Family.ttc"], Contents);
        var files = Directory.GetFiles(folder);
        Assert.Equal(fonts.Count, files.Length);
        foreach (var font in fonts)
        {
            var file = files.Single(f => Path.GetFileNameWithoutExtension(f) == FontConv.FileName(font.Name));
            Same(font, Sfnt.Fonts(File.ReadAllBytes(file))[0], Path.GetFileName(file), exact: true);
        }
    }

    [SystemFontFact("DavidCLM-Medium.otf", "*.otf")]
    public void OpenTypeFontWithCffOutlinesRoundTrips()
    {
        var font = Sfnt.Fonts(File.ReadAllBytes(SystemFonts.Pick("DavidCLM-Medium.otf", "*.otf")!))[0];
        Assert.False(RoundTrip(font, "font").Transformed);
        Assert.Equal("otf", Woff2.Decode(Woff2File(font)).FileExtension);
    }

    /// <summary>Stop while a real font is being compressed: noticed within about a second, nothing left behind.</summary>
    [SystemFontFact(TrueTypes, "segoeui.ttf", "times.ttf")]
    public Task StopDuringAConversionLeavesTheFolderAsItWas() =>
        StopDuring(SystemFonts.Pick(TrueTypes, "segoeui.ttf", "times.ttf")!, "font-woff2");

    [SystemFontFact(Collections, "msgothic.ttc")]
    public Task StopDuringACollectionLeavesTheFolderAsItWas() =>
        StopDuring(SystemFonts.Pick(Collections, "msgothic.ttc")!, "font-woff2");

    /// <summary>A font so large that repacking its glyphs and compressing them for WOFF take seconds each.</summary>
    [SystemFontFact("simsunb.ttf", "malgun.ttf", "msyh.ttc")]
    public async Task StopDuringALargeFontLeavesTheFolderAsItWas()
    {
        var font = SystemFonts.Pick("simsunb.ttf", "malgun.ttf", "msyh.ttc")!;
        await StopDuring(font, "font-woff2", 300, 1000, 2500);
        await StopDuring(font, "font-woff", 300, 1000);
    }

    private async Task StopDuring(string font, string id, params int[] delays)
    {
        var input = Path.Combine(dir, "Font" + Path.GetExtension(font));
        if (!File.Exists(input)) File.Copy(font, input);
        var before = Contents;
        // Looked up first: finding which formats this PC can write asks ffmpeg and Magick.NET, which takes a while.
        var target = Targets.ById(id)!;
        foreach (var delay in delays.Length > 0 ? delays : [0, 20, 100, 400])
        {
            var runner = new Runner();
            // A thread of its own, so the time measured is Stop's and not a wait for a busy thread pool.
            var work = Task.Factory.StartNew(() => FontConv.Convert(input, target, runner), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await Task.Delay(delay);
            Assert.False(work.IsCompleted, "finished before it could be stopped");
            var stopping = Stopwatch.StartNew();
            runner.Cancel();
            var e = await Record.ExceptionAsync(() => work);
            output.WriteLine($"{Path.GetFileName(font)} to {id}, stopped after {delay} ms: noticed in {stopping.ElapsedMilliseconds} ms");
            Assert.IsType<StoppedException>(e);
            Assert.True(stopping.Elapsed < TimeSpan.FromSeconds(1.5), $"took {stopping.ElapsedMilliseconds} ms to stop");
            Assert.Equal(before, Contents);
        }
    }

    [FontSweepFact]
    public void EveryInstalledFontRoundTrips()
    {
        var files = SystemFonts.All.ToList();
        var failures = new ConcurrentBag<string>();
        var (fonts, transformed, untransformed, cff, unreadable) = (0, 0, 0, 0, 0);
        var clock = Stopwatch.StartNew();
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, file =>
        {
            List<Sfnt> read;
            try { read = FontConv.Read(File.ReadAllBytes(file)); }
            catch (ConvertException e)
            {
                Interlocked.Increment(ref unreadable);
                failures.Add($"{Path.GetFileName(file)}: {e.Message}");
                return;
            }
            for (var i = 0; i < read.Count; i++)
            {
                Interlocked.Increment(ref fonts);
                try
                {
                    var result = RoundTrip(read[i], $"{Path.GetFileName(file)}#{i}");
                    if (result.Transformed) Interlocked.Increment(ref transformed);
                    else if (read[i]["glyf"] != null) Interlocked.Increment(ref untransformed);
                    else Interlocked.Increment(ref cff);
                }
                catch (Exception e)
                {
                    failures.Add($"{Path.GetFileName(file)}#{i}: {e.GetType().Name}: {e.Message}");
                }
            }
        });
        output.WriteLine($"{files.Count} files, {fonts} fonts in {clock.Elapsed.TotalSeconds:F0} s: {transformed} with repacked glyphs, "
            + $"{untransformed} with glyphs left as they were, {cff} without TrueType glyphs, {unreadable} files unreadable, {failures.Count} failures");
        foreach (var f in failures.Order()) output.WriteLine(f);
        Assert.Empty(failures);
    }
}
