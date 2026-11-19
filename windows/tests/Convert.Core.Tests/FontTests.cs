using System.Diagnostics;
using System.Text;
using Convert.Core;
using Xunit;
using static Convert.Core.Tests.FontTestKit;

namespace Convert.Core.Tests;

/// <summary>Ported from Tests/ConvertTests/FontTests.swift, against the font built in <see cref="FontTestKit"/>.</summary>
public sealed class FontTests : IDisposable
{
    private readonly string dir = Paths.TempDir();

    public void Dispose() => Paths.TryDelete(dir);

    private List<string> Contents => [.. Directory.EnumerateFileSystemEntries(dir).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal)];

    private string File_(string name, byte[] bytes)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static List<string> Convert(string input, string id, Runner? runner = null) =>
        FontConv.Convert(input, Targets.ById(id) ?? throw new InvalidOperationException(id), runner ?? new Runner());

    private static List<Sfnt> Read(string path) => FontConv.Read(File.ReadAllBytes(path));

    private static void SameBytes(byte[]? want, byte[]? got, object what) =>
        Assert.True(want == null ? got == null : got != null && want.AsSpan().SequenceEqual(got), $"{what} differs");

    /// <summary>
    /// Same tables in the same order; for WOFF2 (<paramref name="glyphsOnly"/>), the glyph tables only
    /// as glyphs, and the order is the format's own.
    /// </summary>
    private static void ExpectSame(Sfnt got, Sfnt want, bool glyphsOnly = false)
    {
        Assert.Equal(want.Flavor, got.Flavor);
        if (glyphsOnly) Assert.Equal(Tags(want).ToHashSet(), Tags(got).ToHashSet());
        else Assert.Equal(Tags(want), Tags(got));
        foreach (var t in want.Tables.Where(t => !(glyphsOnly && (t.Tag == "glyf" || t.Tag == "loca")))) SameBytes(t.Data, got[t.Tag], t.Tag);
        if (want["glyf"] != null)
        {
            Assert.Equal(ParseGlyphs(want), ParseGlyphs(got));
            Assert.True(LocaIsValid(got));
        }
    }

    // The test font itself

    [Fact]
    public void TheTestFontReadsBackAsBuilt()
    {
        foreach (var isLong in new[] { false, true })
        {
            var font = MakeFont(longLoca: isLong);
            Assert.Equal(ExpectedGlyphs, ParseGlyphs(font));
            Assert.True(LocaIsValid(font));
        }
    }

    // SFNT

    [Fact]
    public void WellFormedFontsComeOutByteForByte()
    {
        foreach (var font in new[] { MakeFont(), MakeFont(longLoca: true), MakeCFFFont(), MakeFont(flavor: Sfnt.AppleTrueType) })
        {
            var bytes = SfntFile(font);
            var read = Sfnt.Fonts(bytes);
            Assert.Single(read);
            Assert.Equal(Tags(font), Tags(read[0]));
            Assert.Equal(bytes, read[0].Data());
        }
    }

    [Fact]
    public void ChecksumsAndTheWholeFileChecksumAreRight()
    {
        var out_ = MakeFont().Data();
        Assert.Equal(0xB1B0_AFBAu, Sum32(out_));
        var r = new ByteReader(out_, 4);
        var count = r.U16();
        r.Skip(6);
        long last = 0;
        for (var i = 0; i < count; i++)
        {
            var tag = r.U32();
            var checksum = (uint)r.U32();
            var offset = (int)r.U32();
            var length = (int)r.U32();
            Assert.True(tag > last);
            Assert.Equal(0, offset % 4);
            last = tag;
            var table = new SfntTable(new FontTag((uint)tag), out_.AsSpan(offset, length).ToArray());
            Assert.True(checksum == Sum32(ZeroedHead(table)), $"{table.Tag}");
            Assert.Equal(Sum32(table.Data), Sfnt.Checksum(table.Data));
        }
        Assert.Equal("ttf", MakeFont().FileExtension);
        Assert.Equal("otf", MakeCFFFont().FileExtension);
    }

    [Fact]
    public void CollectionsReadAsSeparateFontsSharingTables()
    {
        var a = MakeFont(names: [new("Alpha")]);
        var b = MakeFont(names: [new("Beta")]);
        var fonts = Sfnt.Fonts(CollectionFile([a, b]));
        Assert.Equal(["Alpha", "Beta"], fonts.Select(f => f.Name));
        SameBytes(a["glyf"], fonts[0]["glyf"], "glyf");
        SameBytes(b["glyf"], fonts[1]["glyf"], "glyf");
        Assert.Equal(Tags(b).ToHashSet(), Tags(fonts[1]).ToHashSet());
        foreach (var t in b.Tables) SameBytes(t.Data, fonts[1][t.Tag], t.Tag);
    }

    [Fact]
    public void NamesPreferWindowsEnglishThenMacRoman()
    {
        static string? Name(NameRecord[]? records) => MakeFont([new TestGlyph.Empty()], names: records, noNames: records == null).Name;
        Assert.Null(Name(null));
        Assert.Null(Name([new("Family only", Id: 1)]));
        Assert.Equal("Café-Mac", Name([new("Café-Mac", Platform: 1, Encoding: 0, Language: 0)]));
        Assert.Equal("English", Name([new("Mac", Platform: 1, Encoding: 0, Language: 0), new("Français", Language: 0x40C), new("English")]));
        Assert.Equal("Unicode", Name([new("Unicode", Platform: 0, Encoding: 3, Language: 0)]));
        // The full name stands in for a missing PostScript name.
        Assert.Equal("Test Regular", Name([new("Test Regular", Id: 4)]));
        Assert.Equal("PS", Name([new("Full", Id: 4), new("PS", Id: 6)]));
    }

    // WOFF

    [Fact]
    public void WoffRoundTripsToTheSameFile()
    {
        foreach (var font in new[] { MakeFont(), MakeCFFFont() })
        {
            var bytes = SfntFile(font);
            var original = Sfnt.Fonts(bytes)[0];
            var back = Woff.Decode(Woff.Encode(original));
            Assert.Equal(Tags(original), Tags(back));
            foreach (var t in original.Tables) SameBytes(t.Data, back[t.Tag], t.Tag);
            Assert.Equal(bytes, back.Data());
        }
    }

    [Fact]
    public void WoffHeaderAndDirectoryFollowTheSpec()
    {
        var font = MakeFont();
        var w = Woff.Encode(font);
        var r = new ByteReader(w);
        Assert.Equal(Woff.Signature, r.U32());
        Assert.Equal(Sfnt.TrueType, r.U32());
        Assert.Equal(w.Length, r.U32());
        Assert.Equal(font.Tables.Count, r.U16());
        Assert.Equal(0, r.U16());
        Assert.Equal(SfntFile(font).Length, r.U32());
        Assert.Equal(2, r.U16());   // the font's revision, 2.5
        Assert.Equal(0x8000, r.U16());
        for (var i = 0; i < 5; i++) Assert.Equal(0, r.U32());
        long last = -1;
        var first = long.MaxValue;
        foreach (var _ in font.Tables)
        {
            var tag = new FontTag((uint)r.U32());
            var (offset, stored, length, checksum) = (r.U32(), r.U32(), r.U32(), r.U32());
            var table = font.Tables.Single(t => t.Tag == tag);
            Assert.True(tag.Value > last);
            last = tag.Value;
            first = Math.Min(first, offset);
            Assert.Equal(0, offset % 4);
            Assert.True(offset + stored <= w.Length);
            Assert.Equal(table.Data.Length, length);
            Assert.True(stored <= length);
            Assert.Equal(Sum32(ZeroedHead(table)), (uint)checksum);
            if (tag == CustomTag)
            {
                // Noise doesn't compress, so it's stored as it is.
                Assert.Equal(length, stored);
                Assert.Equal(Noise, w.AsSpan((int)offset, (int)stored).ToArray());
            }
            if (tag == "glyf") Assert.True(stored < length);
        }
        Assert.Equal(44 + 20 * font.Tables.Count, first);
        Assert.Equal(0, w.Length % 4);
    }

    // WOFF2 glyph transform

    [Fact]
    public void GlyphsRepackAndUnpackToTheSameGlyphs()
    {
        foreach (var isLong in new[] { false, true })
        {
            var font = MakeFont(longLoca: isLong);
            var packed = Woff2.PackGlyphs(font);
            Assert.NotNull(packed);
            var back = Woff2.UnpackGlyphs(packed);
            var rebuilt = Replacing(Replacing(font, "glyf", back.Glyf), "loca", back.Loca);
            Assert.Equal(ExpectedGlyphs, ParseGlyphs(rebuilt));
            Assert.True(LocaIsValid(rebuilt));
            Assert.Equal(GlyphSet.Select(LeftEdge), back.LeftEdges);
        }
        Assert.Null(Woff2.PackGlyphs(MakeCFFFont()));
    }

    [Fact]
    public void Woff2RoundTripsEveryTable()
    {
        foreach (var isLong in new[] { false, true })
        {
            var font = MakeFont(longLoca: isLong);
            var p = Woff2.Prepare(font);
            // `loca` straight after `glyf`, both transformed; the signature is gone.
            var order = p.Entries.Select(e => e.Tag).ToList();
            Assert.Equal(order.IndexOf("glyf") + 1, order.IndexOf("loca"));
            Assert.Equal([0, 0], p.Entries.Where(e => e.Tag == "glyf" || e.Tag == "loca").Select(e => e.Version));
            Assert.DoesNotContain((FontTag)"DSIG", order);

            var bytes = Woff2.Assemble(p, Brotli.Encode(p.Stream, FastBrotli));
            var r = new ByteReader(bytes);
            Assert.Equal(Woff2.Signature, r.U32());
            Assert.Equal(Sfnt.TrueType, r.U32());
            Assert.Equal(bytes.Length, r.U32());
            Assert.Equal(p.Entries.Count, r.U16());
            Assert.Equal(0, bytes.Length % 4);

            var back = Woff2.Decode(bytes);
            var want = font.Tables.Where(t => t.Tag != "DSIG").ToList();
            Assert.Equal(want.Select(t => t.Tag).ToHashSet(), Tags(back).ToHashSet());
            foreach (var t in want.Where(t => t.Tag != "glyf" && t.Tag != "loca" && t.Tag != "head")) SameBytes(t.Data, back[t.Tag], t.Tag);
            // The head is marked as having been through a lossless transform (bit 11).
            var head = (byte[])font["head"]!.Clone();
            head[16] |= 0x08;
            SameBytes(head, back["head"], "head");
            SameBytes(Noise, back[CustomTag], CustomTag);
            Assert.Equal(ExpectedGlyphs, ParseGlyphs(back));
            Assert.True(LocaIsValid(back));
            // And the .ttf made from it reads again.
            Assert.Equal(ExpectedGlyphs, ParseGlyphs(Sfnt.Fonts(back.Data())[0]));
        }
    }

    [Fact]
    public void GlyphsThatCantBeRepackedGoInUntransformed()
    {
        // `loca` longer than the glyphs need, and short offsets that 4-byte padding would overflow.
        var tooLong = MakeFont(extraLoca: 2);
        var tight = MakeFont([.. Enumerable.Repeat<TestGlyph>(new TestGlyph.Simple([[new(1, 1)]]), 7000)], pad: 2);
        Assert.True(tight["glyf"]!.Length <= 0x1FFFE);
        foreach (var font in new[] { tooLong, tight })
        {
            Assert.Throws<ConvertException>(() => Woff2.PackGlyphs(font));
            var p = Woff2.Prepare(font);
            Assert.Equal([3, 3], p.Entries.Where(e => e.Tag == "glyf" || e.Tag == "loca").Select(e => e.Version));
            var back = Woff2.Decode(Woff2.Assemble(p, Brotli.Encode(p.Stream, FastBrotli)));
            SameBytes(font["glyf"], back["glyf"], "glyf");
            SameBytes(font["loca"], back["loca"], "loca");
        }
        // Hand-packed glyphs that overflow short offsets are rejected on the way in, too.
        var packed = Woff2.PackGlyphs(tight["glyf"]!, tight["loca"]!, 7000, 0);
        Assert.Throws<ConvertException>(() => Woff2.UnpackGlyphs(packed));
    }

    [Fact]
    public void MetricsTransformDecodesInEveryCombination()
    {
        var font = MakeFont();
        var metrics = GlyphSet.Length - 3;
        var p = Woff2.Prepare(font);
        var advances = new ByteWriter();
        for (var i = 0; i < metrics; i++) advances.Put16(500 + i);
        byte[] Bearings(int from, int to)
        {
            var b = new ByteWriter();
            for (var g = from; g < to; g++) b.Put16(LeftEdge(GlyphSet[g]));
            return b.ToArray();
        }
        var proportional = Bearings(0, metrics);
        var monospaced = Bearings(metrics, GlyphSet.Length);
        byte[] A = advances.ToArray();
        foreach (var (flags, rest) in new[] { (1, monospaced), (2, proportional), (3, Array.Empty<byte>()) })
        {
            var bytes = Woff2File(p, "hmtx", 1, [(byte)flags, .. A, .. rest]);
            SameBytes(font["hmtx"], Woff2.Decode(bytes)["hmtx"], $"flags {flags}");
        }
        // Nothing left out, a reserved bit, or cut short: rejected.
        foreach (var bad in new byte[][] { [0, .. A, .. proportional, .. monospaced], [4, .. A], [3, .. A[..^1]] })
        {
            var bytes = Woff2File(p, "hmtx", 1, bad);
            Assert.Throws<ConvertException>(() => Woff2.Decode(bytes));
        }
        // Without transformed glyphs there are no left edges to rebuild bearings from.
        var plain = Woff2.Prepare(MakeFont(extraLoca: 2));
        var noGlyphs = Woff2File(plain, "hmtx", 1, [3, .. A]);
        Assert.Throws<ConvertException>(() => Woff2.Decode(noGlyphs));
    }

    [Fact]
    public void Woff2RejectsWhatTheSpecRejects()
    {
        var p = Woff2.Prepare(MakeFont());
        // Reserved glyph transform versions, a transformed `loca` of the wrong size, an unknown transform.
        foreach (var (tag, version, length) in new (FontTag, int, int?)[] { ("glyf", 1, null), ("glyf", 2, null), ("loca", 0, 6), ("name", 1, null) })
        {
            var bytes = Woff2File(p, tag, version, length: length);
            Assert.Throws<ConvertException>(() => Woff2.Decode(bytes));
        }
        // Several fonts in one file.
        var collection = Woff2File(MakeFont());
        Encoding.ASCII.GetBytes("ttcf").CopyTo(collection, 4);
        var error = Assert.Throws<ConvertException>(() => Woff2.Decode(collection));
        Assert.Equal("WOFF2 files that hold several fonts aren't supported", error.Message);
    }

    // Variable-length numbers

    [Fact]
    public void Base128RoundTripsAndRejectsBadEncodings()
    {
        foreach (var value in new long[] { 0, 1, 127, 128, 16383, 16384, 0x1F_FFFF, 0x20_0000, 0xFFF_FFFF, 0x1000_0000, 0xFFFF_FFFF })
        {
            var bytes = new ByteWriter();
            Woff2.PutBase128(bytes, value);
            var r = new ByteReader(bytes.ToArray());
            Assert.Equal(value, Woff2.Base128(r));
            Assert.Equal(0, r.Remaining);
        }
        // A leading zero, more than 32 bits, six bytes, cut short.
        foreach (var bad in new byte[][] { [0x80, 0x01], [0x90, 0x80, 0x80, 0x80, 0x00], [0x81, 0x80, 0x80, 0x80, 0x80, 0x00], [0x81], [] })
            Assert.Throws<ConvertException>(() => Woff2.Base128(new ByteReader(bad)));
    }

    [Fact]
    public void Short255RoundTripsAndRejectsTruncation()
    {
        foreach (var value in new[] { 0, 1, 252, 253, 254, 255, 505, 506, 507, 761, 762, 763, 1000, 65535 })
        {
            var bytes = new ByteWriter();
            Woff2.PutShort255(bytes, value);
            var r = new ByteReader(bytes.ToArray());
            Assert.Equal(value, Woff2.Short255(r));
            Assert.Equal(0, r.Remaining);
        }
        var three = new ByteReader([253, 0x01, 0x00, 254, 0, 255, 0]);
        Assert.Equal([256, 506, 253], new[] { Woff2.Short255(three), Woff2.Short255(three), Woff2.Short255(three) });
        foreach (var bad in new byte[][] { [253, 0x01], [254], [255], [] })
            Assert.Throws<ConvertException>(() => Woff2.Short255(new ByteReader(bad)));
    }

    // Brotli

    [Fact]
    public void BrotliRoundTripsAndChecksTheSize()
    {
        var data = SfntFile(MakeFont());
        var small = Brotli.Encode(data);
        Assert.Equal(data, Brotli.Decode(small, data.Length));
        foreach (var size in new[] { data.Length - 1, data.Length + 1, 0 })
            Assert.Throws<ConvertException>(() => Brotli.Decode(small, size));
        Assert.Throws<ConvertException>(() => Brotli.Decode(Noise, 1000));
        Assert.Throws<ConvertException>(() => Brotli.Decode([], 10));
        Assert.Throws<ConvertException>(() => Brotli.Decode(small.AsSpan(0, small.Length - small.Length / 2), data.Length));
        // The encoder the conversions use makes the same stream as the one-off call, and it unpacks again.
        var best = Brotli.Best(data, new Runner());
        Assert.Equal(small, best);
        Assert.Equal(data, Brotli.Decode(best, data.Length));
        var stopped = new Runner();
        stopped.Cancel();
        Assert.Throws<StoppedException>(() => Brotli.Best(data, stopped));
    }

    [Fact]
    public void BrotliOutputIsLargerThanDeclaredIsRejectedWithoutGrowingPastIt()
    {
        // Ten megabytes of zeros come from a few bytes; a size of one byte must not take them all in.
        var bomb = Brotli.Encode(new byte[10 << 20]);
        Assert.True(bomb.Length < 100);
        Assert.Throws<ConvertException>(() => Brotli.Decode(bomb, 1));
        Assert.Throws<ConvertException>(() => Brotli.Decode(bomb, (10 << 20) - 1));
        Assert.Equal(10 << 20, Brotli.Decode(bomb, 10 << 20).Length);
    }

    // Damaged and hostile files

    private static readonly (string Name, byte[] Bytes)[] Samples = MakeSamples();

    private static (string, byte[])[] MakeSamples()
    {
        var font = MakeFont();
        return
        [
            ("ttf", SfntFile(font)), ("woff", Woff.Encode(font)), ("woff2", Woff2File(font)),
            ("ttc", CollectionFile([font, MakeFont(noNames: true)])),
        ];
    }

    /// <summary>
    /// Reads <paramref name="bytes"/> as a font and, when that works, writes every format: nothing may
    /// throw anything but the error message a damaged file gets.
    /// </summary>
    private static void Survive(byte[] bytes)
    {
        List<Sfnt> fonts;
        try { fonts = FontConv.Read(bytes); }
        catch (ConvertException) { return; }
        foreach (var font in fonts)
        {
            _ = font.Data();
            _ = Woff.Encode(font);
            _ = Woff2.Prepare(font);
            _ = font.Name;
        }
    }

    [Fact]
    public Task EveryCutOffFileFailsCleanly() => Within(TimeSpan.FromMinutes(2), () =>
    {
        foreach (var (name, bytes) in Samples)
        {
            Assert.Equal(name == "ttc" ? 2 : 1, FontConv.Read(bytes).Count);
            for (var n = 0; n < bytes.Length; n++) Survive(bytes[..n]);
        }
    });

    [Fact]
    public Task CorruptedBytesFailCleanly() => Within(TimeSpan.FromMinutes(2), () =>
    {
        var rng = new Seeded(2024);
        byte[] Corrupt(byte[] bytes)
        {
            var b = (byte[])bytes.Clone();
            for (var i = 1 + rng.Next() % 3; i > 0; i--) b[rng.Next() % b.Length] ^= (byte)(1 + rng.Next() % 255);
            return b;
        }
        // Fewer for the desktop formats: most damaged ones still read, and are then written as WOFF2.
        var rounds = new Dictionary<string, int> { ["ttf"] = 1000, ["woff"] = 1500, ["woff2"] = 1500, ["ttc"] = 500 };
        foreach (var (name, bytes) in Samples)
        {
            for (var i = 0; i < rounds[name]; i++) Survive(Corrupt(bytes));
        }
        // Inside the parts compression hides: the glyph tables, the transformed glyphs, the WOFF2 stream.
        var font = MakeFont();
        for (var i = 0; i < 800; i++)
        {
            try { Woff2.PackGlyphs(Replacing(font, "glyf", Corrupt(font["glyf"]!))); }
            catch (ConvertException) { }
        }
        var packed = Woff2.PackGlyphs(font)!;
        for (var i = 0; i < 2000; i++)
        {
            try { Woff2.UnpackGlyphs(Corrupt(packed)); }
            catch (ConvertException) { }
        }
        var p = Woff2.Prepare(font);
        var stream = p.Stream;
        for (var i = 0; i < 500; i++)
        {
            p.Stream = Corrupt(stream);
            Survive(Woff2.Assemble(p, Brotli.Encode(p.Stream, FastBrotli)));
        }
    });

    [Fact]
    public Task HostileCountsSizesAndOffsetsAreRejected() => Within(TimeSpan.FromMinutes(1), () =>
    {
        var (ttf, woff, woff2) = (Samples[0].Bytes, Samples[1].Bytes, Samples[2].Bytes);
        static byte[] Patched(byte[] bytes, int offset, byte[] value)
        {
            var b = (byte[])bytes.Clone();
            value.CopyTo(b, offset);
            return b;
        }
        var hostile = new (string, byte[])[]
        {
            ("sfnt numTables", Patched(ttf, 4, [0xFF, 0xFF])),
            ("sfnt offset past the end", Patched(ttf, 12 + 8, [0xFF, 0xFF, 0xFF, 0xF0])),
            ("woff numTables", Patched(woff, 12, [0xFF, 0xFF])),
            ("woff origLength", Patched(woff, 44 + 12, [0xFF, 0xFF, 0xFF, 0xFF])),
            ("woff overlapping tables", Patched(woff, 44 + 20 + 4, woff[48..52])),
            ("woff2 numTables", Patched(woff2, 12, [0xFF, 0xFF])),
            ("woff2 compressed size", Patched(woff2, 20, [0xFF, 0xFF, 0xFF, 0x00])),
        };
        foreach (var (name, bytes) in hostile)
        {
            var e = Record.Exception(() => FontConv.Read(bytes));
            Assert.True(e is ConvertException, $"{name}: {e?.GetType().Name ?? "no error"}");
        }

        // A directory listing the whole file as a table, over and over.
        var copies = new ByteWriter();
        copies.Put32(Sfnt.TrueType);
        foreach (var v in new[] { 1024, 0, 0, 0 }) copies.Put16(v);
        for (var i = 0; i < 1024; i++)
        {
            foreach (var v in new long[] { 0x4100_0000 + i, 0, 0, 12 + 16 * 1024 }) copies.Put32(v);
        }
        Assert.Throws<ConvertException>(() => Sfnt.Fonts(copies.ToArray()));
        // A collection naming the same font thousands of times.
        var many = new ByteWriter();
        many.Put(Encoding.ASCII.GetBytes("ttcf"));
        many.Put32(0x0001_0000);
        many.Put32(4096);
        for (var i = 0; i < 4096; i++) many.Put32(12 + 4 * 4096);
        many.Put(ttf);
        Assert.Throws<ConvertException>(() => Sfnt.Fonts(many.ToArray()));

        // Tables claiming a gigabyte, with almost no data behind them.
        var started = Stopwatch.StartNew();
        var p = Woff2.Prepare(MakeCFFFont());
        p.Entries.Add(new("Huge", 0, (1 << 30) - 2000, null));
        Assert.Throws<ConvertException>(() => Woff2.Decode(Woff2.Assemble(p, Brotli.Encode(p.Stream, FastBrotli))));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));
        // WOFF tables claiming far more than zlib can make of their bytes, and unpacked bit by bit up
        // to the size given, never more.
        var bomb = Woff.Deflate(new byte[1 << 20]);
        var woffBomb = new ByteWriter();
        woffBomb.Put32(Woff.Signature);
        woffBomb.Put32(Sfnt.TrueType);
        woffBomb.Put32(44 + 20 + bomb.Length);
        woffBomb.Put16(1);
        woffBomb.Put(new byte[30]);
        foreach (var v in new long[] { ((FontTag)"glyf").Value, 44 + 20, bomb.Length, (1 << 20) - 1, 0 }) woffBomb.Put32(v);
        woffBomb.Put(bomb);
        Assert.Throws<ConvertException>(() => Woff.Decode(woffBomb.ToArray()));

        // Transformed glyphs that claim more than their streams hold.
        var header = new ByteWriter();
        foreach (var v in new[] { 0, 0, 65535, 0 }) header.Put16(v);
        for (var i = 0; i < 7; i++) header.Put32(0);
        Assert.Throws<ConvertException>(() => Woff2.UnpackGlyphs(header.ToArray()));
        var contours = header.ToArray();
        new byte[] { 0, 1 }.CopyTo(contours, 4);
        new byte[] { 0, 0, 0, 2 }.CopyTo(contours, 8);
        new byte[] { 0, 0, 0, 4 }.CopyTo(contours, 28);
        Assert.Throws<ConvertException>(() => Woff2.UnpackGlyphs([.. contours, 0x7F, 0xFF, 0, 0, 0, 0]));
    });

    [Fact]
    public Task GlyphsWithBillionsOfPointsAreRefused() => Within(TimeSpan.FromMinutes(1), () =>
    {
        // 65,535 points whose steps are all zero take ~530 bytes as TrueType (repeated flags, no
        // coordinates); 300 of them would be 20 million points to repack.
        var one = Encode(new TestGlyph.Simple([[.. Enumerable.Repeat(new Pt(0, 0), 65535)]]));
        var font = MakeFont([.. Enumerable.Repeat<TestGlyph>(new TestGlyph.Empty(), 300)], longLoca: true);
        var loca = new ByteWriter();
        for (var g = 0; g <= 300; g++) loca.Put32(g * one.Length);
        font = Replacing(Replacing(font, "glyf", [.. Enumerable.Repeat(one, 300).SelectMany(b => b)]), "loca", loca.ToArray());
        Assert.True(one.Length % 2 == 0 && one.Length < 600);
        Assert.Throws<ConvertException>(() => Woff2.PackGlyphs(font));
        Assert.Contains(Woff2.Prepare(font).Entries, e => e.Tag == "glyf" && e.Version == 3);
    });

    // Converting files

    [Fact]
    public void DesktopFontBecomesWebFontsNextToIt()
    {
        var input = File_("Test.ttf", SfntFile(MakeFont()));
        var woff2 = Convert(input, "font-woff2");
        var woff = Convert(input, "font-woff");
        Assert.Equal(["Test.woff2"], woff2.Select(Path.GetFileName));
        Assert.Equal(["Test.woff"], woff.Select(Path.GetFileName));
        Assert.Equal(["Test.ttf", "Test.woff", "Test.woff2"], Contents);
        var original = Read(input)[0];
        ExpectSame(Read(woff[0])[0], original);
        // WOFF2 drops the signature and marks the head as transformed.
        var want = new Sfnt(original.Flavor, [.. original.Tables.Where(t => t.Tag != "DSIG")]);
        var head = (byte[])want["head"]!.Clone();
        head[16] |= 0x08;
        want = Replacing(want, "head", head);
        ExpectSame(Read(woff2[0])[0], want, glyphsOnly: true);
    }

    [Fact]
    public void WebFontsBecomeTTFOrOTFByOutlines()
    {
        var ttf = Convert(File_("Glyphs.woff2", Woff2File(MakeFont())), "font-desktop");
        Assert.Equal(["Glyphs.ttf"], ttf.Select(Path.GetFileName));
        Assert.Equal(ExpectedGlyphs, ParseGlyphs(Read(ttf[0])[0]));
        var otf = Convert(File_("Curves.woff2", Woff2File(MakeCFFFont())), "font-desktop");
        Assert.Equal(["Curves.otf"], otf.Select(Path.GetFileName));
        SameBytes(MakeCFFFont()["CFF "], Read(otf[0])[0]["CFF "], "CFF ");
        var fromWoff = Convert(File_("Old.woff", Woff.Encode(MakeCFFFont())), "font-desktop");
        Assert.Equal(["Old.otf"], fromWoff.Select(Path.GetFileName));
    }

    [Fact]
    public void SameFormatIsWrittenAgainUnderTheNextFreeName()
    {
        var font = Sfnt.Fonts(SfntFile(MakeFont()))[0];
        var cases = new (string Name, byte[] Bytes, string Id)[]
        {
            ("a.ttf", SfntFile(MakeFont()), "font-desktop"), ("b.otf", SfntFile(MakeCFFFont()), "font-desktop"),
            ("c.woff", Woff.Encode(font), "font-woff"), ("d.woff2", Woff2File(font), "font-woff2"),
        };
        foreach (var (name, bytes, id) in cases)
        {
            var input = File_(name, bytes);
            var out_ = Convert(input, id);
            var ext = Path.GetExtension(name);
            Assert.Equal([$"{Path.GetFileNameWithoutExtension(name)} 2{ext}"], out_.Select(Path.GetFileName));
            Assert.Equal(bytes, File.ReadAllBytes(input));
            ExpectSame(Read(out_[0])[0], Read(input)[0], glyphsOnly: ext == ".woff2");
        }
        // A well-formed desktop font comes out as an exact copy.
        Assert.Equal(SfntFile(MakeFont()), File.ReadAllBytes(Path.Combine(dir, "a 2.ttf")));
    }

    [Fact]
    public void CollectionBecomesAFolderOfFontsNamedAfterThem()
    {
        Sfnt[] fonts =
        [
            MakeFont(names: [new("Alpha")]), MakeFont(names: [new("Alpha")]), MakeFont(noNames: true),
            MakeFont(names: [new("Beta/Bold:Italic")]),
        ];
        var input = File_("Family.ttc", CollectionFile(fonts));
        var out_ = Convert(input, "font-woff2");
        Assert.Equal(["Family fonts"], out_.Select(Path.GetFileName));
        var files = Directory.GetFiles(out_[0]).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["Alpha 2.woff2", "Alpha.woff2", "Beta-Bold-Italic.woff2", "Family 3.woff2"], files);
        foreach (var f in files) Assert.Equal(ExpectedGlyphs, ParseGlyphs(Read(Path.Combine(out_[0], f!))[0]));

        // A name that another font's renaming would take is still never written twice.
        var clash = new[] { "X 2", "X", "X" }.Select(n => MakeFont(names: [new(n)])).ToArray();
        var desktop = Convert(File_("Clash.ttc", CollectionFile(clash)), "font-desktop");
        Assert.Equal(["X 2.ttf", "X 3.ttf", "X.ttf"], Directory.GetFiles(desktop[0]).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        // Nothing else was left next to the folders.
        Assert.Equal(["Clash fonts", "Clash.ttc", "Family fonts", "Family.ttc"], Contents);

        // Converted again: a new folder, the old one untouched.
        var again = Convert(input, "font-woff2");
        Assert.Equal(["Family fonts 2"], again.Select(Path.GetFileName));
        Assert.Equal(4, Directory.GetFiles(out_[0]).Length);
    }

    [Fact]
    public void FontNamesBecomeFileNamesWindowsAccepts()
    {
        Assert.Null(FontConv.FileName(null));
        Assert.Null(FontConv.FileName(" .. "));
        Assert.Equal("Beta-Bold-Italic", FontConv.FileName("Beta/Bold:Italic"));
        Assert.Equal("a-b-c-d-e-f-g-h", FontConv.FileName("a<b>c\"d|e?f*g\\h"));
        Assert.Equal("Tab-Line-Zero-Width", FontConv.FileName("Tab\tLine\nZero​Width"));
        Assert.Equal("Trailing", FontConv.FileName(" Trailing. . "));
        Assert.Equal("_CON", FontConv.FileName("CON"));
        Assert.Equal("_nul.Bold", FontConv.FileName("nul.Bold"));
        Assert.Equal("_COM1", FontConv.FileName("COM1"));
        Assert.Equal("Console", FontConv.FileName("Console"));
        // At most 100 characters, an emoji counting as one, and no space left at the cut.
        var cut = FontConv.FileName(new string('x', 99) + " yyy");
        Assert.Equal(new string('x', 99), cut);
        var emoji = FontConv.FileName(string.Concat(Enumerable.Repeat("😀", 120)))!;
        Assert.Equal(200, emoji.Length);
        foreach (var name in new[] { "Beta/Bold:Italic", "a<b>c", " Trailing. ", "CON", "😀😀" })
        {
            var file = Path.Combine(dir, FontConv.FileName(name) + ".ttf");
            File.WriteAllBytes(file, [1]);
            Assert.True(File.Exists(file), name);
        }
    }

    [Fact]
    public void StoppedConversionsLeaveNothingBehind()
    {
        var single = File_("One.ttf", SfntFile(MakeFont()));
        var family = File_("Two.ttc", CollectionFile([MakeFont(), MakeFont(noNames: true)]));
        var before = Contents;
        var runner = new Runner();
        runner.Cancel();
        foreach (var input in new[] { single, family })
        {
            foreach (var id in new[] { "font-woff2", "font-woff", "font-desktop" })
                Assert.Throws<StoppedException>(() => Convert(input, id, runner));
        }
        Assert.Equal(before, Contents);
    }

    [Fact]
    public void FilesThatArentFontsSaySo()
    {
        var text = File_("notes.ttf", Encoding.ASCII.GetBytes("Just some notes, not a font.\n"));
        Assert.Equal(FontError.NotAFont().Message, Assert.Throws<ConvertException>(() => Convert(text, "font-woff2")).Message);
        var damaged = File_("cut.ttf", Samples[0].Bytes[..200]);
        Assert.Equal(FontError.Damaged().Message, Assert.Throws<ConvertException>(() => Convert(damaged, "font-woff")).Message);
        var gone = Assert.Throws<ConvertException>(() => Convert(Path.Combine(dir, "missing.ttf"), "font-woff"));
        Assert.Equal("Couldn't open this font", gone.Message);
        Assert.Equal(["cut.ttf", "notes.ttf"], Contents);
    }

    // Recognising fonts

    [Fact]
    public void FontSignaturesAreRecognised()
    {
        var ttf = Samples[0].Bytes;
        var apple = (byte[])ttf.Clone();
        Encoding.ASCII.GetBytes("true").CopyTo(apple, 0);
        var cases = new (byte[], string)[]
        {
            (ttf, "ttf"), (apple, "ttf"), (SfntFile(MakeCFFFont()), "otf"), (Samples[3].Bytes, "ttc"),
            (Samples[1].Bytes, "woff"), (Samples[2].Bytes, "woff2"),
        };
        foreach (var (bytes, ext) in cases)
        {
            var format = Sniff.FormatOf(bytes);
            Assert.Equal(ext, format?.Ext);
            Assert.Equal([MediaKind.Font], format!.Kinds);
        }
        // Other binary data can start with 00 01 00 00; its "table count" doesn't add up.
        foreach (var other in new byte[][] { [0, 1, 0, 0, 0x12, 0x34, 0, 0], [0, 1, 0, 0, 0, 5, 0, 0x41], [0, 1, 0, 0, 0, 0, 0, 0] })
        {
            byte[] bytes = [.. other, .. Enumerable.Repeat((byte)0x20, 56)];
            Assert.False(Sniff.FormatOf(bytes)?.Kinds.Contains(MediaKind.Font) == true, System.Convert.ToHexString(other));
        }
    }

    [Fact]
    public void FontExtensionsAreFonts()
    {
        foreach (var ext in new[] { "ttf", "otf", "ttc", "otc", "woff", "woff2", "TTF" })
            Assert.Equal(MediaKind.Font, MediaKinds.Of(Path.Combine(dir, $"font.{ext}")));
        var renamed = File_("song.mp3", Samples[0].Bytes);
        var error = Assert.Throws<ConvertException>(() => Sniff.Verify(renamed, MediaKind.Audio));
        Assert.Equal("This isn't really a .mp3 file: its contents are a TrueType font. Rename it to .ttf and convert it again.", error.Message);
        Sniff.Verify(File_("web.woff", Samples[2].Bytes), MediaKind.Font);
    }
}
