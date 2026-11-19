using System.Buffers.Binary;

namespace Convert.Core;

/// <summary>
/// WOFF2: a font's tables in one Brotli stream, with the glyph outlines repacked first so they
/// compress better. The smallest web font format; every current browser reads it.
/// </summary>
/// <remarks>
/// Repacking is lossless for what a glyph is (its points, contours, instructions and bounding box)
/// but not for its bytes: a font unwrapped from WOFF2 draws the same and may differ byte for byte.
/// Mirrors Sources/Convert/WOFF2.swift.
/// </remarks>
public static class Woff2
{
    public static readonly uint Signature = ((FontTag)"wOF2").Value;

    /// <summary>Table names the format writes as one number instead of four letters. The order is the format's.</summary>
    public static readonly FontTag[] KnownTags = [.. new[]
    {
        "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm", "glyf", "loca", "prep",
        "CFF ", "VORG", "EBDT", "EBLC", "gasp", "hdmx", "kern", "LTSH", "PCLT", "VDMX", "vhea", "vmtx", "BASE",
        "GDEF", "GPOS", "GSUB", "EBSC", "JSTF", "MATH", "CBDT", "CBLC", "COLR", "CPAL", "SVG ", "sbix", "acnt",
        "avar", "bdat", "bloc", "bsln", "cvar", "fdsc", "feat", "fmtx", "fvar", "gvar", "hsty", "just", "lcar",
        "mort", "morx", "opbd", "prop", "trak", "Zapf", "Silf", "Glat", "Gloc", "Feat", "Sill",
    }.Select(t => (FontTag)t)];

    private static bool IsGlyphTable(FontTag tag) => tag == "glyf" || tag == "loca";

    // Reading

    private sealed record Entry(FontTag Tag, int Version, bool Transformed, int Length, int Stored);

    /// <summary>The font inside a .woff2 file.</summary>
    public static Sfnt Decode(byte[] bytes)
    {
        var r = new ByteReader(bytes);
        if (r.U32() != Signature) throw FontError.NotAFont();
        var flavor = (uint)r.U32();
        if (flavor == Sfnt.Collection) throw new ConvertException("WOFF2 files that hold several fonts aren't supported");
        if (!Sfnt.Flavors.Contains(flavor)) throw FontError.Damaged();
        r.Skip(4);   // length
        var count = r.U16();
        if (count is <= 0 or > Sfnt.MaxTables) throw FontError.Damaged();
        r.Skip(6);   // reserved, totalSfntSize
        var compressedSize = r.U32();
        r.Skip(24);   // version, metadata and private data

        var entries = new List<Entry>();
        var seen = new HashSet<FontTag>();
        long total = 0;
        for (var i = 0; i < count; i++)
        {
            var flags = r.U8();
            var tag = (flags & 0x3F) == 0x3F ? new FontTag((uint)r.U32()) : KnownTags[flags & 0x3F];
            var version = flags >> 6;
            // Versions 1 and 2 of the glyph tables are reserved: data this code can't know how to read.
            if (IsGlyphTable(tag) && version is not (0 or 3)) throw FontError.Damaged();
            var length = Base128(r);
            // Glyph tables are repacked unless marked otherwise (3); every other table only when marked.
            var transformed = IsGlyphTable(tag) ? version == 0 : version != 0;
            var stored = transformed ? Base128(r) : length;
            total += stored;
            if (total > Woff.MaxFontSize || length > Woff.MaxFontSize || !seen.Add(tag)) throw FontError.Damaged();
            entries.Add(new Entry(tag, version, transformed, (int)length, (int)stored));
        }

        var stream = Brotli.Decode(r.Take(compressedSize), total);
        var storedData = new Dictionary<FontTag, byte[]>();
        var offset = 0;
        foreach (var e in entries)
        {
            storedData[e.Tag] = stream.AsSpan(offset, e.Stored).ToArray();
            offset += e.Stored;
        }
        bool IsRepacked(FontTag tag) => entries.Any(e => e.Tag == tag && e.Transformed);

        Glyphs? glyphs = null;
        if (IsRepacked("glyf"))
        {
            if (!IsRepacked("loca") || storedData["loca"].Length != 0) throw FontError.Damaged();
            glyphs = UnpackGlyphs(storedData["glyf"]);
            // The format requires the size `loca` is given to be the one the glyphs make.
            if (entries.First(e => e.Tag == "loca").Length != glyphs.Loca.Length) throw FontError.Damaged();
        }
        else if (IsRepacked("loca"))
        {
            throw FontError.Damaged();
        }

        var tables = new List<SfntTable>();
        foreach (var e in entries)
        {
            var data = storedData[e.Tag];
            if (e.Transformed)
            {
                if (e.Tag == "glyf") data = glyphs!.Glyf;
                else if (e.Tag == "loca") data = glyphs!.Loca;
                else if (e.Tag == "hmtx" && e.Version == 1)
                {
                    if (!storedData.TryGetValue("hhea", out var hhea) || hhea.Length < 36 || glyphs == null) throw FontError.Damaged();
                    data = UnpackMetrics(data, hhea[34] << 8 | hhea[35], glyphs.LeftEdges);
                }
                else
                {
                    throw FontError.Damaged();   // a repacking newer than this code
                }
            }
            tables.Add(new SfntTable(e.Tag, data));
        }
        return new Sfnt(flavor, tables);
    }

    // Writing

    /// <param name="Stored">The size in the stream, for repacked tables only.</param>
    public sealed record PreparedEntry(FontTag Tag, int Version, int Length, int? Stored);

    /// <summary>
    /// A font ready to be written as WOFF2, short of compressing <see cref="Stream"/>: that step
    /// takes a while, so the caller does it (see <see cref="Brotli"/>).
    /// </summary>
    public sealed class Prepared(uint flavor, byte[] revision, long sfntSize)
    {
        public uint Flavor { get; } = flavor;
        public byte[] Revision { get; } = revision;
        public long SfntSize { get; } = sfntSize;
        public List<PreparedEntry> Entries { get; set; } = [];
        public byte[] Stream { get; set; } = [];
    }

    /// <summary>Repacking a large font's glyphs takes seconds; <paramref name="runner"/>'s Stop is noticed while it goes on.</summary>
    public static Prepared Prepare(Sfnt font, Runner? runner = null)
    {
        // A digital signature can't survive repacking, so the format has it removed, and has the
        // font marked as having been through a lossless transform (bit 11 of the head flags).
        var tables = font.Tables.Where(t => t.Tag != "DSIG").OrderBy(t => t.Tag).ToList();
        // The format wants `loca` straight after `glyf`, so decoders can rebuild it from the glyphs.
        var loca = tables.FindIndex(t => t.Tag == "loca");
        var glyf = tables.FindIndex(t => t.Tag == "glyf");
        if (loca >= 0 && glyf >= 0 && glyf < loca)
        {
            var t = tables[loca];
            tables.RemoveAt(loca);
            tables.Insert(glyf + 1, t);
        }
        var head = tables.FindIndex(t => t.Tag == "head");
        if (head >= 0 && tables[head].Data.Length >= 18)
        {
            var data = (byte[])tables[head].Data.Clone();
            data[16] |= 0x08;
            tables[head] = tables[head] with { Data = data };
        }
        var p = new Prepared(
            font.Flavor,
            font["head"] is { Length: >= 8 } h ? h.AsSpan(4, 4).ToArray() : new byte[4],
            tables.Aggregate(12L + 16L * tables.Count, (sum, t) => sum + ((t.Data.Length + 3L) & ~3L)));
        // Glyphs that can't be repacked safely (an unusual or damaged glyph table) go in as they are.
        byte[]? packed;
        try { packed = PackGlyphs(font, runner); }
        catch (ConvertException) { packed = null; }
        var stream = new ByteWriter((int)Math.Min(Array.MaxLength, tables.Sum(t => (long)t.Data.Length) + (packed?.Length ?? 0)));
        foreach (var table in tables)
        {
            if (table.Tag == "glyf" && packed != null)
            {
                p.Entries.Add(new(table.Tag, 0, table.Data.Length, packed.Length));
                stream.Put(packed);
            }
            else if (table.Tag == "loca" && packed != null)
            {
                p.Entries.Add(new(table.Tag, 0, table.Data.Length, 0));
            }
            else
            {
                p.Entries.Add(new(table.Tag, IsGlyphTable(table.Tag) ? 3 : 0, table.Data.Length, null));
                stream.Put(table.Data);
            }
        }
        p.Stream = stream.ToArray();
        return p;
    }

    /// <summary>The .woff2 file, given <c>p.Stream</c> compressed with Brotli.</summary>
    public static byte[] Assemble(Prepared p, ReadOnlySpan<byte> compressed)
    {
        var directory = new ByteWriter();
        foreach (var e in p.Entries)
        {
            var known = Array.IndexOf(KnownTags, e.Tag);
            directory.Put8((known >= 0 ? known : 0x3F) | e.Version << 6);
            if (known < 0) directory.Put32(e.Tag.Value);
            PutBase128(directory, e.Length);
            if (e.Stored is { } stored) PutBase128(directory, stored);
        }
        var length = (48L + directory.Count + compressed.Length + 3) & ~3L;
        var out_ = new ByteWriter((int)Math.Min(Array.MaxLength, length));
        out_.Put32(Signature);
        out_.Put32(p.Flavor);
        out_.Put32(length);
        out_.Put16(p.Entries.Count);
        out_.Put16(0);
        // Browsers reject a font that claims to unpack smaller than the file (possible for a tiny font).
        out_.Put32(Math.Max(p.SfntSize, length));
        out_.Put32(compressed.Length);
        out_.Put(p.Revision);
        for (var i = 0; i < 5; i++) out_.Put32(0);   // no metadata, no private data
        out_.Put(directory.Span);
        out_.Put(compressed);
        out_.Pad4();
        return out_.ToArray();
    }

    // Numbers of variable length

    /// <summary>A length in 1 to 5 bytes, seven bits each, the highest first.</summary>
    public static long Base128(ByteReader r)
    {
        long value = 0;
        for (var i = 0; i < 5; i++)
        {
            var b = r.U8();
            // No leading zeros, and no more than 32 bits.
            if ((i == 0 && b == 0x80) || (value & 0xFE00_0000) != 0) throw FontError.Damaged();
            value = value << 7 | (long)(b & 0x7F);
            if ((b & 0x80) == 0) return value;
        }
        throw FontError.Damaged();
    }

    public static void PutBase128(ByteWriter out_, long value)
    {
        var shift = 28;
        while (shift > 0 && value >> shift == 0) shift -= 7;
        for (; shift > 0; shift -= 7) out_.Put8((int)(0x80 | (value >> shift) & 0x7F));
        out_.Put8((int)(value & 0x7F));
    }

    /// <summary>A 16-bit number in 1 to 3 bytes; small values, the common case, take one.</summary>
    public static int Short255(ByteReader r)
    {
        var code = r.U8();
        return code switch
        {
            253 => r.U16(),
            254 => r.U8() + 506,
            255 => r.U8() + 253,
            _ => code,
        };
    }

    public static void PutShort255(ByteWriter out_, int value)
    {
        if (value < 253)
        {
            out_.Put8(value);
        }
        else if (value < 506)
        {
            out_.Put8(255);
            out_.Put8(value - 253);
        }
        else if (value < 762)
        {
            out_.Put8(254);
            out_.Put8(value - 506);
        }
        else
        {
            out_.Put8(253);
            out_.Put16(value);
        }
    }

    // Glyphs

    /// <summary>The glyph tables as a .ttf holds them, plus each glyph's left edge (the metrics need it).</summary>
    public sealed record Glyphs(byte[] Glyf, byte[] Loca, int[] LeftEdges);

    private const int OnCurve = 0x01;
    private const int XShort = 0x02;
    private const int YShort = 0x04;
    private const int Repeats = 0x08;
    private const int XSameOrPositive = 0x10;
    private const int YSameOrPositive = 0x20;
    private const int OverlapSimple = 0x40;

    /// <summary>
    /// Far more points than any real font has (the largest have a few million). Without a limit a
    /// small file could describe billions of points, through glyphs whose points take no bytes.
    /// </summary>
    public const int MaxPoints = 1 << 24;

    /// <summary>How far a composite glyph's component record goes on after its flags and glyph number.</summary>
    private static int ComponentSize(int flags)
    {
        var size = (flags & 0x0001) != 0 ? 4 : 2;   // the two arguments, as words or bytes
        if ((flags & 0x0008) != 0) size += 2;   // one scale
        else if ((flags & 0x0040) != 0) size += 4;   // x and y scale
        else if ((flags & 0x0080) != 0) size += 8;   // 2×2 transform
        return size;
    }

    private static int Be16(ReadOnlySpan<byte> b, int at) => b[at] << 8 | b[at + 1];

    /// <summary>
    /// Repacks the <c>glyf</c> table the WOFF2 way: the same kind of data from all glyphs goes together
    /// (contour counts, point counts, point flags, coordinates, …), which Brotli compresses far
    /// better than glyph after glyph. <c>loca</c> isn't stored at all; it's rebuilt from the glyphs.
    /// </summary>
    /// <returns>
    /// Null for a font without TrueType outlines. Throws when the glyph table can't be repacked with
    /// certainty: the result is unpacked again and must repack to the same bytes.
    /// </returns>
    public static byte[]? PackGlyphs(Sfnt font, Runner? runner = null)
    {
        if (font["glyf"] is not { } glyf || font["loca"] is not { } loca || font["head"] is not { Length: >= 54 } head
            || font["maxp"] is not { Length: >= 6 } maxp)
            return null;
        var longOffsets = Be16(head, 50);
        var count = Be16(maxp, 4);
        if (longOffsets > 1 || loca.Length != (count + 1) * (longOffsets == 0 ? 2 : 4)) throw FontError.Damaged();
        var packed = PackGlyphs(glyf, loca, count, longOffsets, runner);
        var back = UnpackGlyphs(packed, runner);
        if (back.Loca.Length != loca.Length
            || !PackGlyphs(back.Glyf, back.Loca, count, longOffsets, runner).AsSpan().SequenceEqual(packed))
            throw FontError.Damaged();
        return packed;
    }

    /// <summary>How many glyphs go by between looks at Stop.</summary>
    private const int GlyphsBetweenChecks = 256;

    public static byte[] PackGlyphs(byte[] glyf, byte[] loca, int count, int longOffsets, Runner? runner = null)
    {
        var offsets = new ByteReader(loca);
        long end = longOffsets == 0 ? offsets.U16() * 2 : offsets.U32();

        var contourCounts = new ByteWriter();
        var pointCounts = new ByteWriter();
        var flags = new ByteWriter();
        var coordinates = new ByteWriter();
        var composites = new ByteWriter();
        var boxes = new ByteWriter();
        var instructions = new ByteWriter();
        var hasBox = new byte[((count + 31) >> 5) << 2];
        var overlaps = new byte[(count + 7) >> 3];
        var anyOverlap = false;
        long totalPoints = 0;
        // Real glyphs take a few bytes a point. Ones that take next to none (repeated flags, no
        // steps) are only made to cost time; they can go in untransformed, which loses nothing.
        var pointLimit = Math.Min(MaxPoints, 4L * glyf.Length);
        var pointFlags = new List<int>();
        var dx = new List<int>();
        var dy = new List<int>();
        Span<byte> computed = stackalloc byte[8];

        for (var g = 0; g < count; g++)
        {
            if (g % GlyphsBetweenChecks == 0) runner?.CheckCancelled();
            var start = end;
            end = longOffsets == 0 ? offsets.U16() * 2 : offsets.U32();
            if (start > end) throw FontError.Damaged();
            if (start == end)
            {
                contourCounts.Put16(0);
                continue;
            }
            var r = new ByteReader(glyf, start, end - start);
            var contours = r.I16();
            var box = r.Take(8);
            if (contours == 0)
            {
                contourCounts.Put16(0);
                continue;
            }

            if (contours < 0)
            {
                // A composite: its component records go in as they are, the box is always kept.
                contourCounts.Put16(-1);
                hasBox[g >> 3] |= (byte)(0x80 >> (g & 7));
                boxes.Put(box);
                var first = r.Pos;
                var hasInstructions = false;
                while (true)
                {
                    var component = r.U16();
                    r.Skip(2 + ComponentSize(component));
                    if ((component & 0x0100) != 0) hasInstructions = true;
                    if ((component & 0x0020) == 0) break;
                }
                composites.Put(glyf.AsSpan(first, r.Pos - first));
                if (hasInstructions)
                {
                    var length = r.U16();
                    PutShort255(coordinates, length);
                    instructions.Put(r.Take(length));
                }
                continue;
            }

            contourCounts.Put16(contours);
            var last = -1;
            for (var c = 0; c < contours; c++)
            {
                var endPoint = r.U16();
                if (endPoint < last) throw FontError.Damaged();
                PutShort255(pointCounts, endPoint - last);
                last = endPoint;
            }
            var points = last + 1;
            totalPoints += points;
            if (totalPoints > pointLimit) throw FontError.Damaged();
            var codeLength = r.U16();
            var code = r.Take(codeLength);

            pointFlags.Clear();
            while (pointFlags.Count < points)
            {
                var flag = r.U8();
                pointFlags.Add(flag);
                if ((flag & Repeats) != 0)
                {
                    var times = r.U8();
                    if (pointFlags.Count + times > points) throw FontError.Damaged();
                    for (var k = 0; k < times; k++) pointFlags.Add(flag);
                }
            }
            dx.Clear();
            dy.Clear();
            foreach (var flag in pointFlags)
            {
                if ((flag & XShort) != 0)
                {
                    var b = r.U8();
                    dx.Add((flag & XSameOrPositive) != 0 ? b : -b);
                }
                else
                {
                    dx.Add((flag & XSameOrPositive) == 0 ? r.I16() : 0);
                }
            }
            foreach (var flag in pointFlags)
            {
                if ((flag & YShort) != 0)
                {
                    var b = r.U8();
                    dy.Add((flag & YSameOrPositive) != 0 ? b : -b);
                }
                else
                {
                    dy.Add((flag & YSameOrPositive) == 0 ? r.I16() : 0);
                }
            }

            // Sums of up to 65,536 steps of at most 32,768 each: well within an int.
            int x = 0, y = 0;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            for (var i = 0; i < points; i++)
            {
                x += dx[i];
                y += dy[i];
                (minX, minY, maxX, maxY) = (Math.Min(minX, x), Math.Min(minY, y), Math.Max(maxX, x), Math.Max(maxY, y));
                PutPoint((pointFlags[i] & OnCurve) != 0, dx[i], dy[i], flags, coordinates);
            }
            PutShort255(coordinates, codeLength);
            instructions.Put(code);
            if ((pointFlags[0] & OverlapSimple) != 0)
            {
                overlaps[g >> 3] |= (byte)(0x80 >> (g & 7));
                anyOverlap = true;
            }
            // The box is left out when it's what the points say, which is nearly always.
            BinaryPrimitives.WriteInt16BigEndian(computed, (short)minX);
            BinaryPrimitives.WriteInt16BigEndian(computed[2..], (short)minY);
            BinaryPrimitives.WriteInt16BigEndian(computed[4..], (short)maxX);
            BinaryPrimitives.WriteInt16BigEndian(computed[6..], (short)maxY);
            if (!box.SequenceEqual(computed))
            {
                hasBox[g >> 3] |= (byte)(0x80 >> (g & 7));
                boxes.Put(box);
            }
        }

        var out_ = new ByteWriter();
        out_.Put16(0);
        out_.Put16(anyOverlap ? 1 : 0);
        out_.Put16(count);
        out_.Put16(longOffsets);
        var boxStream = new ByteWriter(hasBox.Length + boxes.Count);
        boxStream.Put(hasBox);
        boxStream.Put(boxes.Span);
        ByteWriter[] streams = [contourCounts, pointCounts, flags, coordinates, composites, boxStream, instructions];
        foreach (var s in streams) out_.Put32(s.Count);
        foreach (var s in streams) out_.Put(s.Span);
        if (anyOverlap) out_.Put(overlaps);
        return out_.ToArray();
    }

    /// <summary>The reverse of <see cref="PackGlyphs(Sfnt)"/>: <c>glyf</c> and <c>loca</c> as a .ttf holds them.</summary>
    public static Glyphs UnpackGlyphs(byte[] packed, Runner? runner = null)
    {
        var header = new ByteReader(packed);
        header.Skip(2);
        var options = header.U16();
        var count = header.U16();
        var longOffsets = header.U16();
        if (longOffsets > 1) throw FontError.Damaged();
        long next = header.Pos + 28;
        ByteReader Stream(long size)
        {
            var s = new ByteReader(packed, next, size);
            next += size;
            return s;
        }
        var contourCounts = Stream(header.U32());
        var pointCounts = Stream(header.U32());
        var flags = Stream(header.U32());
        var coordinates = Stream(header.U32());
        var composites = Stream(header.U32());
        var boxes = Stream(header.U32());
        var instructions = Stream(header.U32());
        var hasBox = boxes.Take(((count + 31) >> 5) << 2).ToArray();
        var overlapStream = Stream((options & 1) != 0 ? (count + 7) >> 3 : 0);
        var overlaps = overlapStream.Take(overlapStream.Remaining).ToArray();

        var glyf = new ByteWriter();
        var offsets = new List<int>(count + 1);
        var leftEdges = new List<int>(count);
        long totalPoints = 0;
        var endPoints = new List<int>();
        var xs = new List<int>();
        var ys = new List<int>();
        var flagBytes = new ByteWriter();
        var xBytes = new ByteWriter();
        var yBytes = new ByteWriter();
        for (var g = 0; g < count; g++)
        {
            if (g % GlyphsBetweenChecks == 0) runner?.CheckCancelled();
            offsets.Add(glyf.Count);
            var contours = contourCounts.I16();
            var explicitBox = (hasBox[g >> 3] & (0x80 >> (g & 7))) != 0;
            if (contours == 0)
            {
                if (explicitBox) throw FontError.Damaged();
                leftEdges.Add(0);
                continue;
            }

            if (contours == -1)
            {
                if (!explicitBox) throw FontError.Damaged();
                var box = boxes.Take(8);
                leftEdges.Add((short)Be16(box, 0));
                glyf.Put16(-1);
                glyf.Put(box);
                var first = composites.Pos;
                var hasInstructions = false;
                while (true)
                {
                    var component = composites.U16();
                    composites.Skip(2 + ComponentSize(component));
                    if ((component & 0x0100) != 0) hasInstructions = true;
                    if ((component & 0x0020) == 0) break;
                }
                glyf.Put(packed.AsSpan(first, composites.Pos - first));
                if (hasInstructions)
                {
                    var length = Short255(coordinates);
                    glyf.Put16(length);
                    glyf.Put(instructions.Take(length));
                }
                glyf.Pad4();
                continue;
            }
            if (contours < 0) throw FontError.Damaged();

            endPoints.Clear();
            var points = 0;
            for (var c = 0; c < contours; c++)
            {
                points += Short255(pointCounts);
                if (points is <= 0 or > 0x10000) throw FontError.Damaged();
                endPoints.Add(points - 1);
            }
            totalPoints += points;
            if (totalPoints > MaxPoints) throw FontError.Damaged();
            var pointFlags = flags.Take(points);
            xs.Clear();
            ys.Clear();
            int x = 0, y = 0;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            for (var i = 0; i < points; i++)
            {
                var (stepX, stepY) = Point(pointFlags[i] & 0x7F, coordinates);
                x += stepX;
                y += stepY;
                xs.Add(x);
                ys.Add(y);
                (minX, minY, maxX, maxY) = (Math.Min(minX, x), Math.Min(minY, y), Math.Max(maxX, x), Math.Max(maxY, y));
            }
            var codeLength = Short255(coordinates);
            var code = instructions.Take(codeLength);

            glyf.Put16(contours);
            if (explicitBox)
            {
                var box = boxes.Take(8);
                leftEdges.Add((short)Be16(box, 0));
                glyf.Put(box);
            }
            else
            {
                leftEdges.Add(minX);
                foreach (var v in new[] { minX, minY, maxX, maxY }) glyf.Put16(v);
            }
            foreach (var endPoint in endPoints) glyf.Put16(endPoint);
            glyf.Put16(codeLength);
            glyf.Put(code);

            // Points the way TrueType stores them: a flag byte each (runs of the same flag are
            // written once, with a count), then the x steps, then the y steps, in as few bytes as fit.
            flagBytes.Clear();
            xBytes.Clear();
            yBytes.Clear();
            var lastFlag = -1;
            var run = 0;
            int lastX = 0, lastY = 0;
            for (var i = 0; i < points; i++)
            {
                var flag = (pointFlags[i] & 0x80) == 0 ? OnCurve : 0;
                if (i == 0 && g >> 3 < overlaps.Length && (overlaps[g >> 3] & (0x80 >> (g & 7))) != 0) flag |= OverlapSimple;
                var stepX = xs[i] - lastX;
                var stepY = ys[i] - lastY;
                if (stepX == 0)
                {
                    flag |= XSameOrPositive;
                }
                else if (stepX is > -256 and < 256)
                {
                    flag |= XShort | (stepX > 0 ? XSameOrPositive : 0);
                    xBytes.Put8(Math.Abs(stepX));
                }
                else
                {
                    xBytes.Put16(stepX);
                }
                if (stepY == 0)
                {
                    flag |= YSameOrPositive;
                }
                else if (stepY is > -256 and < 256)
                {
                    flag |= YShort | (stepY > 0 ? YSameOrPositive : 0);
                    yBytes.Put8(Math.Abs(stepY));
                }
                else
                {
                    yBytes.Put16(stepY);
                }
                if (flag == lastFlag && run != 255)
                {
                    flagBytes[flagBytes.Count - 1] |= Repeats;
                    run++;
                }
                else
                {
                    if (run != 0) flagBytes.Put8(run);
                    flagBytes.Put8(flag);
                    run = 0;
                }
                lastFlag = flag;
                lastX = xs[i];
                lastY = ys[i];
            }
            if (run != 0) flagBytes.Put8(run);
            glyf.Put(flagBytes.Span);
            glyf.Put(xBytes.Span);
            glyf.Put(yBytes.Span);
            glyf.Pad4();
        }
        offsets.Add(glyf.Count);

        // Short offsets count in pairs of bytes and stop at 16 bits.
        if (longOffsets == 0 && glyf.Count > 0x1FFFE) throw FontError.Damaged();
        var loca = new ByteWriter(offsets.Count * (longOffsets == 0 ? 2 : 4));
        foreach (var offset in offsets)
        {
            if (longOffsets == 0) loca.Put16(offset >> 1);
            else loca.Put32(offset);
        }
        return new Glyphs(glyf.ToArray(), loca.ToArray(), [.. leftEdges]);
    }

    /// <summary>
    /// One point's step from the previous point, in the format's own 1- to 4-byte encoding:
    /// the flag's low seven bits say how many bytes follow and how the step is spread over them
    /// (short steps, the common case, take the fewest).
    /// </summary>
    private static (int Dx, int Dy) Point(int flag, ByteReader r)
    {
        static int Signed(int signs, int value) => (signs & 1) != 0 ? value : -value;
        if (flag < 10) return (0, Signed(flag, ((flag & 14) << 7) + r.U8()));
        if (flag < 20) return (Signed(flag, (((flag - 10) & 14) << 7) + r.U8()), 0);
        if (flag < 84)
        {
            var high = flag - 20;
            var low = r.U8();
            return (Signed(flag, 1 + (high & 0x30) + (low >> 4)), Signed(flag >> 1, 1 + ((high & 0x0C) << 2) + (low & 0x0F)));
        }
        if (flag < 120)
        {
            var high = flag - 84;
            var x = r.U8();
            var y = r.U8();
            return (Signed(flag, 1 + ((high / 12) << 8) + x), Signed(flag >> 1, 1 + (((high % 12) >> 2) << 8) + y));
        }
        if (flag < 124)
        {
            var a = r.U8();
            var b = r.U8();
            var c = r.U8();
            return (Signed(flag, (a << 4) + (b >> 4)), Signed(flag >> 1, ((b & 0x0F) << 8) + c));
        }
        var x16 = r.U16();
        var y16 = r.U16();
        return (Signed(flag, x16), Signed(flag >> 1, y16));
    }

    private static void PutPoint(bool onCurve, int dx, int dy, ByteWriter flags, ByteWriter bytes)
    {
        var x = Math.Abs(dx);
        var y = Math.Abs(dy);
        var curve = onCurve ? 0 : 128;
        var xSign = dx < 0 ? 0 : 1;
        var ySign = dy < 0 ? 0 : 1;
        var signs = xSign + 2 * ySign;
        if (dx == 0 && y < 1280)
        {
            flags.Put8(curve + ((y & 0xF00) >> 7) + ySign);
            bytes.Put8(y & 0xFF);
        }
        else if (dy == 0 && x < 1280)
        {
            flags.Put8(curve + 10 + ((x & 0xF00) >> 7) + xSign);
            bytes.Put8(x & 0xFF);
        }
        else if (x < 65 && y < 65)
        {
            flags.Put8(curve + 20 + ((x - 1) & 0x30) + (((y - 1) & 0x30) >> 2) + signs);
            bytes.Put8((((x - 1) & 0x0F) << 4) | ((y - 1) & 0x0F));
        }
        else if (x < 769 && y < 769)
        {
            flags.Put8(curve + 84 + 12 * (((x - 1) & 0x300) >> 8) + (((y - 1) & 0x300) >> 6) + signs);
            bytes.Put8((x - 1) & 0xFF);
            bytes.Put8((y - 1) & 0xFF);
        }
        else if (x < 4096 && y < 4096)
        {
            flags.Put8(curve + 120 + signs);
            bytes.Put8(x >> 4);
            bytes.Put8(((x & 0x0F) << 4) | (y >> 8));
            bytes.Put8(y & 0xFF);
        }
        else
        {
            flags.Put8(curve + 124 + signs);
            bytes.Put16(x);
            bytes.Put16(y);
        }
    }

    // Metrics

    /// <summary>
    /// <c>hmtx</c> with its left side bearings put back. An encoder may leave them out when they equal
    /// each glyph's left edge, which they nearly always do.
    /// </summary>
    public static byte[] UnpackMetrics(byte[] packed, int widths, int[] leftEdges)
    {
        var r = new ByteReader(packed);
        var flags = r.U8();
        var storedWithWidths = (flags & 1) == 0;
        var storedAlone = (flags & 2) == 0;
        if ((flags & 0xFC) != 0 || (storedWithWidths && storedAlone) || widths < 1 || widths > leftEdges.Length)
            throw FontError.Damaged();
        var advances = new int[widths];
        for (var i = 0; i < widths; i++) advances[i] = r.U16();
        var bearings = (int[])leftEdges.Clone();
        if (storedWithWidths)
        {
            for (var i = 0; i < widths; i++) bearings[i] = r.I16();
        }
        if (storedAlone)
        {
            for (var i = widths; i < leftEdges.Length; i++) bearings[i] = r.I16();
        }
        var out_ = new ByteWriter(4 * leftEdges.Length);
        for (var i = 0; i < leftEdges.Length; i++)
        {
            if (i < widths) out_.Put16(advances[i]);
            out_.Put16(bearings[i]);
        }
        return out_.ToArray();
    }
}
