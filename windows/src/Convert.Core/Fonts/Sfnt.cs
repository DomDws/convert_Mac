using System.Buffers.Binary;
using System.Text;

namespace Convert.Core;

// Mirrors Sources/Convert/SFNT.swift.

public static class FontError
{
    public static ConvertException Damaged() => new("This font file is damaged or incomplete");
    public static ConvertException NotAFont() => new("This isn't a TrueType, OpenType, WOFF or WOFF2 font");
}

/// <summary>
/// Reads the big-endian numbers font files are made of. Reading past the end throws instead of
/// crashing, so a damaged or cut-off font becomes an error message.
/// </summary>
public sealed class ByteReader
{
    private readonly byte[] bytes;

    /// <summary>Reads <paramref name="count"/> bytes starting at <paramref name="from"/> (everything after it when null).</summary>
    public ByteReader(byte[] bytes, long from = 0, long? count = null)
    {
        // Offsets and sizes come straight from the file, as unsigned 32-bit numbers: long, so adding
        // them can't wrap around.
        var end = from + (count ?? bytes.Length - from);
        if (from < 0 || end < from || end > bytes.Length) throw FontError.Damaged();
        this.bytes = bytes;
        Pos = (int)from;
        End = (int)end;
    }

    public int Pos { get; private set; }
    public int End { get; }
    public int Remaining => End - Pos;

    public int U8()
    {
        if (Remaining < 1) throw FontError.Damaged();
        return bytes[Pos++];
    }

    public int U16()
    {
        if (Remaining < 2) throw FontError.Damaged();
        var v = bytes[Pos] << 8 | bytes[Pos + 1];
        Pos += 2;
        return v;
    }

    public int I16() => (short)U16();

    public long U32()
    {
        if (Remaining < 4) throw FontError.Damaged();
        var v = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(Pos, 4));
        Pos += 4;
        return v;
    }

    public ReadOnlySpan<byte> Take(long count)
    {
        if (count < 0 || count > Remaining) throw FontError.Damaged();
        var span = bytes.AsSpan(Pos, (int)count);
        Pos += (int)count;
        return span;
    }

    public void Skip(long count) => Take(count);
}

/// <summary>A growing buffer of big-endian numbers: how fonts are written.</summary>
public sealed class ByteWriter(int capacity = 256)
{
    private byte[] buffer = new byte[Math.Max(capacity, 16)];

    public int Count { get; private set; }

    public byte this[int index]
    {
        get => index < Count ? buffer[index] : throw new ArgumentOutOfRangeException(nameof(index));
        set => buffer[index < Count ? index : throw new ArgumentOutOfRangeException(nameof(index))] = value;
    }

    public ReadOnlySpan<byte> Span => buffer.AsSpan(0, Count);

    private Span<byte> Grow(int n)
    {
        if (n > Array.MaxLength - Count) throw FontError.Damaged();   // nothing real comes near 2 GB
        if (Count + n > buffer.Length)
        {
            var size = (int)Math.Min(Array.MaxLength, Math.Max((long)Count + n, 2L * buffer.Length));
            Array.Resize(ref buffer, size);
        }
        var span = buffer.AsSpan(Count, n);
        Count += n;
        return span;
    }

    public void Put8(int value) => Grow(1)[0] = (byte)value;

    /// <summary>The low 16 bits, so a negative number goes in as two's complement.</summary>
    public void Put16(long value) => BinaryPrimitives.WriteUInt16BigEndian(Grow(2), (ushort)value);

    public void Put32(long value) => BinaryPrimitives.WriteUInt32BigEndian(Grow(4), (uint)value);

    public void Put(ReadOnlySpan<byte> data) => data.CopyTo(Grow(data.Length));

    /// <summary>Font tables start on 4-byte boundaries; the gap is filled with zeros.</summary>
    public void Pad4()
    {
        while (Count % 4 != 0) Put8(0);
    }

    public byte[] ToArray() => buffer.AsSpan(0, Count).ToArray();

    /// <summary>Empties the buffer and keeps its memory, for one that's filled again and again.</summary>
    public void Clear() => Count = 0;
}

/// <summary>A four-letter table name, such as <c>glyf</c> or <c>head</c>.</summary>
public readonly record struct FontTag(uint Value) : IComparable<FontTag>
{
    public static implicit operator FontTag(string text)
    {
        if (text.Length != 4 || text.Any(c => c > 0x7F)) throw new ArgumentException("a font tag has four letters", nameof(text));
        return new((uint)text[0] << 24 | (uint)text[1] << 16 | (uint)text[2] << 8 | text[3]);
    }

    public int CompareTo(FontTag other) => Value.CompareTo(other.Value);

    public override string ToString() => new([(char)(byte)(Value >> 24), (char)(byte)(Value >> 16), (char)(byte)(Value >> 8), (char)(byte)Value]);
}

/// <summary>One table of a font. Tables are never changed in place: fonts in a collection can share them.</summary>
public sealed record SfntTable(FontTag Tag, byte[] Data);

/// <summary>
/// A TrueType or OpenType font as a list of tables: the format inside .ttf and .otf files, and what
/// WOFF and WOFF2 wrap. Convert moves tables between these containers; it never redraws a glyph.
/// </summary>
public sealed class Sfnt(uint flavor, List<SfntTable> tables)
{
    /// <summary>The first four bytes: 0x00010000 or <c>true</c> for TrueType outlines, <c>OTTO</c> for CFF outlines.</summary>
    public uint Flavor { get; set; } = flavor;

    /// <summary>In the order their data is stored in the file (the directory is sorted by name when written).</summary>
    public List<SfntTable> Tables { get; set; } = tables;

    public const uint TrueType = 0x0001_0000;
    public static readonly uint AppleTrueType = ((FontTag)"true").Value;
    public static readonly uint OpenType = ((FontTag)"OTTO").Value;
    public static readonly uint Collection = ((FontTag)"ttcf").Value;

    /// <summary>The kinds of single font there are. A web font claiming anything else can't become a .ttf or .otf.</summary>
    public static readonly uint[] Flavors = [TrueType, AppleTrueType, OpenType];

    /// <summary>Nothing sensible has more tables; a larger count means the file isn't a font.</summary>
    public const int MaxTables = 1024;

    public byte[]? this[FontTag tag] => Tables.FirstOrDefault(t => t.Tag == tag)?.Data;

    /// <summary>CFF outlines are what .otf files hold; fonts with TrueType outlines are .ttf.</summary>
    public string FileExtension => this["CFF "] != null || this["CFF2"] != null ? "otf" : "ttf";

    // Reading

    /// <summary>The fonts in a .ttf or .otf file (one), or in a .ttc or .otc collection (several).</summary>
    public static List<Sfnt> Fonts(byte[] bytes)
    {
        var r = new ByteReader(bytes);
        if (r.Remaining < 12) throw FontError.NotAFont();
        if (r.U32() != Collection) return [Read(bytes, 0)];
        r.Skip(4);   // version
        var count = r.U32();
        if (count is <= 0 or > 4096) throw FontError.Damaged();
        // The fonts in a collection share tables, and each gets its own copy. Real collections come
        // to under ten times the file; a small file listing the same tables thousands of times would
        // otherwise use up all memory. (Here a table the fonts share is read once and shared, so
        // memory stays near the file's size, but the limit still keeps the work in proportion.)
        var fonts = new List<Sfnt>();
        var shared = new Dictionary<(long, long), byte[]>();
        long total = 0;
        for (var i = 0; i < count; i++)
        {
            var font = Read(bytes, r.U32(), shared);
            total += font.Tables.Sum(t => (long)t.Data.Length);
            if (total > 32L * bytes.Length) throw FontError.Damaged();
            fonts.Add(font);
        }
        return fonts;
    }

    /// <summary>Reads the font whose table directory starts at <paramref name="offset"/>.</summary>
    public static Sfnt Read(byte[] bytes, long offset) => Read(bytes, offset, null);

    private static Sfnt Read(byte[] bytes, long offset, Dictionary<(long, long), byte[]>? shared)
    {
        var r = new ByteReader(bytes, offset);
        var flavor = (uint)r.U32();
        if (!Flavors.Contains(flavor)) throw FontError.NotAFont();
        var count = r.U16();
        if (count is <= 0 or > MaxTables) throw FontError.Damaged();
        r.Skip(6);   // searchRange, entrySelector, rangeShift
        var found = new List<(long Offset, SfntTable Table)>();
        var seen = new HashSet<FontTag>();
        long total = 0;
        for (var i = 0; i < count; i++)
        {
            var tag = new FontTag((uint)r.U32());
            r.Skip(4);   // checksum: recomputed when the font is written
            var start = r.U32();
            var length = r.U32();
            // Tables don't overlap, so together they fit in the file (twice over, for the odd old
            // font whose tables share data); more is a directory built to make copies without end.
            // The font is also held to the size WOFF and WOFF2 allow, which keeps it to one array.
            total += length;
            if (start + length > bytes.Length || total > 2L * bytes.Length || total > Woff.MaxFontSize || !seen.Add(tag))
                throw FontError.Damaged();
            byte[] data;
            if (shared == null) data = bytes.AsSpan((int)start, (int)length).ToArray();
            else if (!shared.TryGetValue((start, length), out data!))
                shared[(start, length)] = data = bytes.AsSpan((int)start, (int)length).ToArray();
            found.Add((start, new SfntTable(tag, data)));
        }
        // Stable, so tables that share an offset (empty ones) keep their directory order.
        return new Sfnt(flavor, [.. found.OrderBy(f => f.Offset).Select(f => f.Table)]);
    }

    // Writing

    /// <summary>The sum of a table's bytes as big-endian 32-bit numbers, the way fonts checksum their tables.</summary>
    public static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var i = 0;
        for (; i + 4 <= data.Length; i += 4) sum += BinaryPrimitives.ReadUInt32BigEndian(data[i..]);
        uint last = 0;
        for (var k = 0; k < 4; k++) last = last << 8 | (i + k < data.Length ? data[i + k] : 0u);
        return sum + last;
    }

    /// <summary>
    /// A table as it's checksummed and stored: <c>head</c> has its whole-file checksum field cleared,
    /// since that one is filled in last. Other tables are returned as they are, not copied.
    /// </summary>
    public static byte[] Stored(SfntTable table)
    {
        if (table.Tag != "head" || table.Data.Length < 12) return table.Data;
        var data = (byte[])table.Data.Clone();
        data.AsSpan(8, 4).Clear();
        return data;
    }

    /// <summary>
    /// The font as a .ttf or .otf file. A well-formed font read with <see cref="Read(byte[], long)"/>
    /// comes out byte for byte as it went in.
    /// </summary>
    public byte[] Data()
    {
        var count = Tables.Count;
        var power = 1;
        var exponent = 0;
        while (power * 2 <= count)
        {
            power *= 2;
            exponent++;
        }
        var stored = Tables.Select(Stored).ToList();
        var size = 12L + 16L * count + stored.Sum(d => (d.Length + 3L) & ~3L);
        if (size > Array.MaxLength) throw FontError.Damaged();
        var out_ = new ByteWriter((int)size);
        out_.Put32(Flavor);
        out_.Put16(count);
        out_.Put16(power * 16);
        out_.Put16(exponent);
        out_.Put16(count * 16 - power * 16);

        long offset = 12 + 16 * count;
        long? headOffset = null;
        var records = new List<(FontTag Tag, uint Checksum, long Offset, int Length)>();
        for (var i = 0; i < count; i++)
        {
            var data = stored[i];
            if (Tables[i].Tag == "head" && data.Length >= 12) headOffset = offset;
            records.Add((Tables[i].Tag, Checksum(data), offset, data.Length));
            offset += (data.Length + 3L) & ~3L;
        }
        foreach (var rec in records.OrderBy(x => x.Tag))
        {
            out_.Put32(rec.Tag.Value);
            out_.Put32(rec.Checksum);
            out_.Put32(rec.Offset);
            out_.Put32(rec.Length);
        }
        foreach (var data in stored)
        {
            out_.Put(data);
            out_.Pad4();
        }
        var bytes = out_.ToArray();
        if (headOffset is { } head)
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan((int)head + 8, 4), 0xB1B0_AFBA - Checksum(bytes));
        return bytes;
    }

    // Names

    /// <summary>
    /// The font's PostScript name ("Helvetica-Bold"), or its full name: what the fonts taken out of
    /// a collection are called. Null when the font doesn't say.
    /// </summary>
    public string? Name => NameRecord(6) ?? NameRecord(4);

    private static readonly Encoding Utf16 = new UnicodeEncoding(bigEndian: true, byteOrderMark: false);
    private static readonly Encoding MacRoman = CodePagesEncodingProvider.Instance.GetEncoding(10000) ?? Encoding.Latin1;

    private string? NameRecord(int id)
    {
        if (this["name"] is not { } table) return null;
        (int Rank, string Text)? best = null;
        try
        {
            var r = new ByteReader(table);
            r.U16();
            var count = r.U16();
            var strings = r.U16();
            for (var i = 0; i < count; i++)
            {
                var (platform, encoding, language) = (r.U16(), r.U16(), r.U16());
                var (nameId, length, offset) = (r.U16(), r.U16(), r.U16());
                if (nameId != id || strings + offset + length > table.Length) continue;
                var raw = table.AsSpan(strings + offset, length);
                // Windows English first, then any Unicode name, then the old Mac Roman one.
                int rank;
                string? text;
                switch (platform, encoding)
                {
                    case (3, 1) or (3, 10):
                        rank = language == 0x409 ? 0 : 1;
                        text = Utf16Text(raw);
                        break;
                    case (0, _):
                        rank = 2;
                        text = Utf16Text(raw);
                        break;
                    case (1, 0):
                        rank = 3;
                        text = MacRoman.GetString(raw);
                        break;
                    default:
                        continue;
                }
                if (!string.IsNullOrEmpty(text) && (best == null || rank < best.Value.Rank)) best = (rank, text);
            }
        }
        catch (ConvertException) { }   // a name table cut short: whatever was read before it
        return best?.Text;
    }

    /// <summary>Null for an odd number of bytes, which isn't UTF-16 text.</summary>
    private static string? Utf16Text(ReadOnlySpan<byte> raw) => raw.Length % 2 == 0 ? Utf16.GetString(raw) : null;
}
