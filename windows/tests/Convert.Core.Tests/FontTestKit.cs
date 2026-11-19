using System.Numerics;
using System.Text;
using Convert.Core;

namespace Convert.Core.Tests;

// Ported from Tests/ConvertTests/FontTests.swift: a small TrueType font built by hand, and a way of
// reading glyphs back that doesn't use the code under test.

/// <summary>One outline point, in font units.</summary>
public readonly record struct Pt(int X, int Y, bool On = true);

/// <summary>A glyph as the tests describe it. A null box means "what the points say".</summary>
public abstract record TestGlyph
{
    public sealed record Empty : TestGlyph;

    /// <summary>Zero contours but a 10-byte header: bytes in the file, nothing to draw.</summary>
    public sealed record ZeroContours : TestGlyph;

    public sealed record Simple(Pt[][] Contours, byte[]? Code = null, int[]? Box = null, bool Overlap = false) : TestGlyph;

    public sealed record Composite(byte[] Components, byte[]? Code, int[] Box) : TestGlyph;
}

public static class FontTestKit
{
    private static string Hex(ReadOnlySpan<byte> b) => System.Convert.ToHexString(b);

    private static string Join(IEnumerable<int> values) => string.Join(",", values);

    // What has to survive every conversion, written out so two glyphs compare as text.

    public const string Empty = "empty";

    public static string Simple(IEnumerable<int> ends, IEnumerable<Pt> points, ReadOnlySpan<byte> code, IEnumerable<int> box, bool overlap) =>
        $"simple ends {Join(ends)} points {string.Join(" ", points.Select(p => $"{p.X}/{p.Y}{(p.On ? "" : "~")}"))} code {Hex(code)} box {Join(box)} overlap {overlap}";

    public static string Composite(ReadOnlySpan<byte> components, ReadOnlySpan<byte> code, IEnumerable<int> box) =>
        $"composite {Hex(components)} code {Hex(code)} box {Join(box)}";

    public static int[] Bounds(Pt[] points) =>
        points.Length == 0 ? [0, 0, 0, 0] : [points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y)];

    public static string Expected(TestGlyph g)
    {
        switch (g)
        {
            case TestGlyph.Simple s:
                var ends = new List<int>();
                foreach (var c in s.Contours) ends.Add((ends.Count > 0 ? ends[^1] : -1) + c.Length);
                var points = s.Contours.SelectMany(c => c).ToArray();
                return Simple(ends, points, s.Code ?? [], s.Box ?? Bounds(points), s.Overlap);
            case TestGlyph.Composite c:
                return Composite(c.Components, c.Code ?? [], c.Box);
            default:
                return Empty;
        }
    }

    /// <summary>The left side bearing a glyph has (and the one WOFF2's hmtx transform rebuilds): its xMin.</summary>
    public static int LeftEdge(TestGlyph g) => g switch
    {
        TestGlyph.Simple s => (s.Box ?? Bounds([.. s.Contours.SelectMany(c => c)]))[0],
        TestGlyph.Composite c => c.Box[0],
        _ => 0,
    };

    /// <summary>A glyph the plain TrueType way: runs of identical flags use the repeat flag (at most 255 more).</summary>
    public static byte[] Encode(TestGlyph g)
    {
        var out_ = new ByteWriter();
        switch (g)
        {
            case TestGlyph.ZeroContours:
                out_.Put(new byte[10]);
                break;
            case TestGlyph.Composite c:
                out_.Put16(-1);
                foreach (var v in c.Box) out_.Put16(v);
                out_.Put(c.Components);
                if (c.Code != null)
                {
                    out_.Put16(c.Code.Length);
                    out_.Put(c.Code);
                }
                break;
            case TestGlyph.Simple s:
                var points = s.Contours.SelectMany(p => p).ToArray();
                out_.Put16(s.Contours.Length);
                foreach (var v in s.Box ?? Bounds(points)) out_.Put16(v);
                var end = -1;
                foreach (var contour in s.Contours)
                {
                    end += contour.Length;
                    out_.Put16(end);
                }
                var code = s.Code ?? [];
                out_.Put16(code.Length);
                out_.Put(code);
                var flags = new List<byte>();
                var xs = new ByteWriter();
                var ys = new ByteWriter();
                var last = new Pt(0, 0);
                static byte Delta(int d, byte @short, byte same, ByteWriter bytes)
                {
                    if (d == 0) return same;
                    if (Math.Abs(d) < 256)
                    {
                        bytes.Put8(Math.Abs(d));
                        return (byte)(@short | (d > 0 ? same : 0));
                    }
                    bytes.Put16(d);
                    return 0;
                }
                for (var i = 0; i < points.Length; i++)
                {
                    var p = points[i];
                    var f = (byte)(p.On ? 1 : 0);
                    if (i == 0 && s.Overlap) f |= 0x40;
                    f |= Delta(p.X - last.X, 0x02, 0x10, xs);
                    f |= Delta(p.Y - last.Y, 0x04, 0x20, ys);
                    flags.Add(f);
                    last = p;
                }
                for (var i = 0; i < flags.Count;)
                {
                    var n = 0;
                    while (i + n + 1 < flags.Count && flags[i + n + 1] == flags[i] && n < 255) n++;
                    if (n > 0)
                    {
                        out_.Put8(flags[i] | 0x08);
                        out_.Put8(n);
                    }
                    else
                    {
                        out_.Put8(flags[i]);
                    }
                    i += n + 1;
                }
                out_.Put(xs.Span);
                out_.Put(ys.Span);
                break;
        }
        return out_.ToArray();
    }

    /// <summary>One composite component record: flags, glyph, arguments (words or bytes), then the transform words.</summary>
    public static byte[] Component(int flags, int glyph, int[] args, int[]? transform = null)
    {
        var out_ = new ByteWriter();
        out_.Put16(flags);
        out_.Put16(glyph);
        foreach (var a in args)
        {
            if ((flags & 1) != 0) out_.Put16(a);
            else out_.Put8(a);
        }
        foreach (var t in transform ?? []) out_.Put16(t);
        return out_.ToArray();
    }

    /// <summary>Glyphs that between them hit the edge cases of the glyph transform.</summary>
    public static readonly TestGlyph[] GlyphSet = BuildGlyphSet();

    private static TestGlyph[] BuildGlyphSet()
    {
        // Every boundary of the triplet encoding, there and back, on and off the curve.
        var steps = new List<Pt> { new(0, 0) };
        foreach (var m in new[] { 1, 64, 65, 768, 769, 1279, 1280, 4095, 4096, 32767 })
        {
            foreach (var (dx, dy) in new[] { (0, m), (m, 0), (m, m), (m, 1), (1, m) })
            {
                foreach (var (sx, sy) in new[] { (1, 1), (-1, 1), (1, -1), (-1, -1) })
                {
                    steps.Add(new(sx * dx, sy * dy, steps.Count % 3 != 0));
                    steps.Add(new(0, 0, steps.Count % 2 == 0));
                }
            }
        }
        // -32768, the one step a 16-bit number can't take back.
        Pt[] extremes = [new(16384, 16384), new(-16384, -16384, false), new(16383, 16383)];
        static Pt[] Square(int x, int y, int size) => [new(x, y), new(x + size, y), new(x + size, y + size, false), new(x, y + size)];
        static TestGlyph Line(int n) => new TestGlyph.Simple([[.. Enumerable.Range(1, n).Select(i => new Pt(i, 0))]]);
        byte[] scaled =
        [
            .. Component(0x0001 | 0x0002 | 0x0008 | 0x0020, 2, [-300, 1000], [0x2000]),
            .. Component(0x0002 | 0x0040, 3, [5, -7], [0x4000, 0x1000]),
        ];
        byte[] matrix =
        [
            .. Component(0x0002 | 0x0080 | 0x0100 | 0x0020, 1, [1, 2], [0x4000, 0, 0x0100, 0x4000]),
            .. Component(0x0001 | 0x0200, 4, [0, 3]),
        ];
        return
        [
            new TestGlyph.Empty(),
            new TestGlyph.Simple([[.. steps], extremes]),
            new TestGlyph.Simple([Square(0, 0, 100)], Code: [0xB0, 0x05, 0x2B, 0x00]),
            new TestGlyph.Simple([Square(10, 20, 50)], Box: [-5, 0, 300, 400]),
            new TestGlyph.Simple([Square(0, 0, 200), Square(50, 50, 100)], Overlap: true),
            Line(256),   // one flag and a repeat count of exactly 255
            Line(257),
            Line(600),
            new TestGlyph.ZeroContours(),
            new TestGlyph.Composite(scaled, null, [-300, 0, 1200, 1100]),
            new TestGlyph.Composite(matrix, [0x40, 0x01, 0x00, 0xB0], [0, 0, 100, 100]),
            new TestGlyph.Composite(Component(0x0102, 2, [7, 7]), [], [7, 7, 107, 107]),
            new TestGlyph.Simple([[new(3, 4)]]),
        ];
    }

    public static List<string> ExpectedGlyphs => [.. GlyphSet.Select(Expected)];

    /// <summary>A fixed-seed generator, so every run tests the same bytes.</summary>
    public sealed class Seeded(ulong seed)
    {
        private ulong state = seed;

        public int Next()
        {
            state = state * 6_364_136_223_846_793_005UL + 1_442_695_040_888_963_407UL;
            return (int)(state >> 33);
        }
    }

    public static readonly byte[] Noise = MakeNoise();

    private static byte[] MakeNoise()
    {
        var rng = new Seeded(7);
        return [.. Enumerable.Range(0, 301).Select(_ => (byte)rng.Next())];
    }

    public sealed record NameRecord(string Text, int Platform = 3, int Encoding = 1, int Language = 0x409, int Id = 6);

    private static readonly Encoding MacRoman = CodePagesEncodingProvider.Instance.GetEncoding(10000)!;

    public static byte[] NameTable(NameRecord[] records)
    {
        var strings = new ByteWriter();
        var out_ = new ByteWriter();
        out_.Put16(0);
        out_.Put16(records.Length);
        out_.Put16(6 + 12 * records.Length);
        foreach (var r in records)
        {
            var bytes = (r.Platform == 1 ? MacRoman : new UnicodeEncoding(true, false)).GetBytes(r.Text);
            foreach (var v in new[] { r.Platform, r.Encoding, r.Language, r.Id, bytes.Length, strings.Count }) out_.Put16(v);
            strings.Put(bytes);
        }
        out_.Put(strings.Span);
        return out_.ToArray();
    }

    public static readonly FontTag CustomTag = new(0x01FF_7F20);

    /// <summary>
    /// A TrueType font holding <paramref name="glyphs"/>, with metrics, names, a table of incompressible
    /// bytes under an arbitrary tag, an empty table and a signature. Tables are stored in an order
    /// that isn't sorted.
    /// </summary>
    public static Sfnt MakeFont(TestGlyph[]? glyphs = null, bool longLoca = false, int pad = 4, int extraLoca = 0,
        NameRecord[]? names = null, bool noNames = false, uint flavor = Sfnt.TrueType)
    {
        glyphs ??= GlyphSet;
        names ??= noNames ? null : [new NameRecord("Test-Regular")];
        var glyf = new ByteWriter();
        var loca = new ByteWriter();
        foreach (var g in glyphs)
        {
            if (longLoca) loca.Put32(glyf.Count);
            else loca.Put16(glyf.Count / 2);
            glyf.Put(Encode(g));
            while (glyf.Count % pad != 0) glyf.Put8(0);
        }
        if (longLoca) loca.Put32(glyf.Count);
        else loca.Put16(glyf.Count / 2);
        loca.Put(new byte[extraLoca]);

        var head = new ByteWriter();
        foreach (var v in new long[] { 0x0001_0000, 0x0002_8000, 0x1234_5678, 0x5F0F_3CF5 }) head.Put32(v);
        head.Put16(0x000B);
        head.Put16(1000);
        head.Put(new byte[16]);
        foreach (var v in new[] { -300, -200, 1300, 1200, 0, 8, 2, longLoca ? 1 : 0, 0 }) head.Put16(v);
        var metrics = Math.Max(1, glyphs.Length - 3);
        var hhea = new ByteWriter();
        hhea.Put32(0x0001_0000);
        hhea.Put(new byte[30]);
        hhea.Put16(metrics);
        var maxp = new ByteWriter();
        maxp.Put32(0x0001_0000);
        maxp.Put16(glyphs.Length);
        maxp.Put(new byte[26]);
        var hmtx = new ByteWriter();
        for (var i = 0; i < glyphs.Length; i++)
        {
            if (i < metrics) hmtx.Put16(500 + i);
            hmtx.Put16(LeftEdge(glyphs[i]));
        }
        var tables = new List<SfntTable>
        {
            new("head", head.ToArray()), new("hhea", hhea.ToArray()), new("maxp", maxp.ToArray()), new("hmtx", hmtx.ToArray()),
        };
        if (names != null) tables.Add(new("name", NameTable(names)));
        tables.AddRange(
        [
            new(CustomTag, Noise), new("Zero", []), new("loca", loca.ToArray()), new("glyf", glyf.ToArray()),
            new("DSIG", [0, 0, 0, 1, 0, 0, 0, 0]),
        ]);
        return new Sfnt(flavor, tables);
    }

    /// <summary>A font with CFF outlines: no glyph tables, just bytes under <c>CFF </c> this code never looks into.</summary>
    public static Sfnt MakeCFFFont()
    {
        var font = MakeFont([new TestGlyph.Empty()], flavor: Sfnt.OpenType);
        font.Tables.RemoveAll(t => t.Tag == "glyf" || t.Tag == "loca" || t.Tag == "DSIG");
        font.Tables[2] = font.Tables[2] with { Data = [0, 0, 0x50, 0, 0, 1] };   // maxp 0.5
        // Before the empty table, which would otherwise share its place in the file.
        var zero = font.Tables.FindIndex(t => t.Tag == "Zero");
        font.Tables.Insert(zero, new SfntTable("CFF ", Encoding.ASCII.GetBytes("not really CFF, but nobody reads it")));
        return font;
    }

    /// <summary>A copy that can be changed without changing <paramref name="font"/>.</summary>
    public static Sfnt Copy(Sfnt font) => new(font.Flavor, [.. font.Tables]);

    public static Sfnt Replacing(Sfnt font, FontTag tag, byte[] data) =>
        new(font.Flavor, [.. font.Tables.Select(t => t.Tag == tag ? t with { Data = data } : t)]);

    // Reading back, independently of the code under test

    public static uint Sum32(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            uint word = 0;
            for (var k = 0; k < 4; k++) word = word << 8 | (i + k < data.Length ? data[i + k] : 0u);
            sum += word;
        }
        return sum;
    }

    public static byte[] ZeroedHead(SfntTable t)
    {
        var d = (byte[])t.Data.Clone();
        if (t.Tag == "head") d.AsSpan(8, 4).Clear();
        return d;
    }

    /// <summary>The .ttf file for <paramref name="font"/>, written the textbook way: directory sorted by tag, data in the font's order.</summary>
    public static byte[] SfntFile(Sfnt font)
    {
        var n = font.Tables.Count;
        var log = BitOperations.Log2((uint)n);
        var range = 16 << log;
        var out_ = new ByteWriter();
        out_.Put32(font.Flavor);
        foreach (var v in new[] { n, range, log, n * 16 - range }) out_.Put16(v);
        var offsets = new Dictionary<FontTag, int>();
        var offset = 12 + 16 * n;
        foreach (var t in font.Tables)
        {
            offsets[t.Tag] = offset;
            offset += (t.Data.Length + 3) / 4 * 4;
        }
        foreach (var t in font.Tables.OrderBy(t => t.Tag))
        {
            foreach (var v in new long[] { t.Tag.Value, Sum32(ZeroedHead(t)), offsets[t.Tag], t.Data.Length }) out_.Put32(v);
        }
        foreach (var t in font.Tables)
        {
            out_.Put(ZeroedHead(t));
            out_.Pad4();
        }
        var bytes = out_.ToArray();
        if (offsets.TryGetValue("head", out var head))
        {
            var adjustment = 0xB1B0_AFBA - Sum32(bytes);
            for (var k = 0; k < 4; k++) bytes[head + 8 + k] = (byte)(adjustment >> (24 - 8 * k));
        }
        return bytes;
    }

    /// <summary>A .ttc of <paramref name="fonts"/>; identical tables are stored once and shared, as real collections do.</summary>
    public static byte[] CollectionFile(Sfnt[] fonts)
    {
        var out_ = new ByteWriter();
        out_.Put(Encoding.ASCII.GetBytes("ttcf"));
        out_.Put32(0x0001_0000);
        out_.Put32(fonts.Length);
        var directories = 12 + 4 * fonts.Length;
        foreach (var f in fonts)
        {
            out_.Put32(directories);
            directories += 12 + 16 * f.Tables.Count;
        }
        var data = new ByteWriter();
        var stored = new Dictionary<string, int>();
        foreach (var f in fonts)
        {
            out_.Put32(f.Flavor);
            foreach (var v in new[] { f.Tables.Count, 0, 0, 0 }) out_.Put16(v);
            foreach (var t in f.Tables.OrderBy(t => t.Tag))
            {
                var key = Hex(t.Data);
                if (!stored.TryGetValue(key, out var at))
                {
                    stored[key] = at = directories + data.Count;
                    data.Put(t.Data);
                    data.Pad4();
                }
                foreach (var v in new long[] { t.Tag.Value, 0, at, t.Data.Length }) out_.Put32(v);
            }
        }
        out_.Put(data.Span);
        return out_.ToArray();
    }

    private static int Be16(byte[] b, int at) => b[at] << 8 | b[at + 1];

    public static List<string> ParseGlyphs(Sfnt font)
    {
        var offsets = GlyphOffsets(font);
        return [.. Enumerable.Range(0, offsets.Length - 1).Select(g => ParseGlyph(font["glyf"]!, offsets, g))];
    }

    /// <summary>Where each glyph starts in <c>glyf</c>, and where the last one ends.</summary>
    public static long[] GlyphOffsets(Sfnt font)
    {
        var isLong = font["head"]![51] == 1;
        var count = Be16(font["maxp"]!, 4);
        var l = new ByteReader(font["loca"]!);
        return [.. Enumerable.Range(0, count + 1).Select(_ => isLong ? l.U32() : l.U16() * 2L)];
    }

    public static string ParseGlyph(byte[] glyf, long[] offsets, int g)
    {
        if (offsets[g] == offsets[g + 1]) return Empty;
        var r = new ByteReader(glyf, offsets[g], offsets[g + 1] - offsets[g]);
        var contours = r.I16();
        int[] box = [r.I16(), r.I16(), r.I16(), r.I16()];
        if (contours == 0) return Empty;
        if (contours < 0)
        {
            var first = r.Pos;
            var instructions = false;
            while (true)
            {
                var f = r.U16();
                r.Skip(2 + ((f & 1) != 0 ? 4 : 2) + ((f & 8) != 0 ? 2 : (f & 0x40) != 0 ? 4 : (f & 0x80) != 0 ? 8 : 0));
                if ((f & 0x100) != 0) instructions = true;
                if ((f & 0x20) == 0) break;
            }
            var components = glyf.AsSpan(first, r.Pos - first);
            return Composite(components, instructions ? r.Take(r.U16()) : [], box);
        }
        var ends = Enumerable.Range(0, contours).Select(_ => r.U16()).ToArray();
        var code = r.Take(r.U16()).ToArray();
        var flags = new List<int>();
        while (flags.Count < ends[^1] + 1)
        {
            var f = r.U8();
            flags.Add(f);
            if ((f & 8) != 0) flags.AddRange(Enumerable.Repeat(f, r.U8()));
        }
        int[] Coordinates(int @short, int same)
        {
            var v = 0;
            return [.. flags.Select(f =>
            {
                if ((f & @short) != 0)
                {
                    var b = r.U8();
                    v += (f & same) != 0 ? b : -b;
                }
                else if ((f & same) == 0)
                {
                    v += r.I16();
                }
                return v;
            })];
        }
        var xs = Coordinates(2, 0x10);
        var ys = Coordinates(4, 0x20);
        var points = flags.Select((f, i) => new Pt(xs[i], ys[i], (f & 1) != 0));
        return Simple(ends, points, code, box, (flags[0] & 0x40) != 0);
    }

    /// <summary><c>loca</c> agrees with <c>glyf</c>: numGlyphs + 1 offsets in the head's format, never going back, ending at the end.</summary>
    public static bool LocaIsValid(Sfnt font)
    {
        var isLong = font["head"]![51] == 1;
        var loca = font["loca"]!;
        var count = Be16(font["maxp"]!, 4);
        if (loca.Length != (count + 1) * (isLong ? 4 : 2)) return false;
        var r = new ByteReader(loca);
        var offsets = Enumerable.Range(0, count + 1).Select(_ => isLong ? r.U32() : r.U16() * 2L).ToArray();
        return offsets.Zip(offsets.Skip(1)).All(p => p.First <= p.Second) && offsets[^1] == font["glyf"]!.Length;
    }

    public static List<FontTag> Tags(Sfnt font) => [.. font.Tables.Select(t => t.Tag)];

    /// <summary>A fast setting, for files the tests make by the hundred; how well they compress doesn't matter.</summary>
    public const int FastBrotli = 4;

    public static byte[] Woff2File(Sfnt font)
    {
        var p = Woff2.Prepare(font);
        return Woff2.Assemble(p, Brotli.Encode(p.Stream, FastBrotli));
    }

    /// <summary><paramref name="p"/> written out with one table's stored bytes and transform version replaced, to build files by hand.</summary>
    public static byte[] Woff2File(Woff2.Prepared p, FontTag tag, int version, byte[]? data = null, int? length = null)
    {
        var chunks = new List<byte[]>();
        var offset = 0;
        foreach (var e in p.Entries)
        {
            var size = e.Stored ?? e.Length;
            chunks.Add(p.Stream.AsSpan(offset, size).ToArray());
            offset += size;
        }
        var i = p.Entries.FindIndex(e => e.Tag == tag);
        if (i < 0) throw new ArgumentException($"no {tag} table");
        if (data != null) chunks[i] = data;
        var transformed = tag == "glyf" || tag == "loca" ? version != 3 : version != 0;
        var entries = p.Entries.ToList();
        entries[i] = new(tag, version, length ?? entries[i].Length, transformed ? chunks[i].Length : null);
        var q = new Woff2.Prepared(p.Flavor, p.Revision, p.SfntSize) { Entries = entries, Stream = [.. chunks.SelectMany(c => c)] };
        return Woff2.Assemble(q, Brotli.Encode(q.Stream, FastBrotli));
    }

    /// <summary>Runs <paramref name="body"/> and fails when it takes longer than <paramref name="limit"/>: a hang is a failure too.</summary>
    public static async Task Within(TimeSpan limit, Action body)
    {
        var work = Task.Run(body);
        if (await Task.WhenAny(work, Task.Delay(limit)) != work) throw new TimeoutException($"took longer than {limit}");
        await work;
    }
}
