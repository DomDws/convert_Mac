using System.IO.Compression;

namespace Convert.Core;

/// <summary>WOFF 1.0: a font's tables, each compressed on its own with zlib. Every browser reads it.</summary>
/// <remarks>Mirrors Sources/Convert/WOFF.swift.</remarks>
public static class Woff
{
    public static readonly uint Signature = ((FontTag)"wOFF").Value;

    /// <summary>No font comes near this; a file that claims more is damaged (or built to use up memory).</summary>
    public const int MaxFontSize = 1 << 30;

    /// <summary>
    /// The font inside a .woff file. Its metadata and private data blocks, if any, have no place
    /// in a .ttf or .otf and are left behind.
    /// </summary>
    public static Sfnt Decode(byte[] bytes)
    {
        var r = new ByteReader(bytes);
        if (r.U32() != Signature) throw FontError.NotAFont();
        var flavor = (uint)r.U32();
        if (!Sfnt.Flavors.Contains(flavor)) throw FontError.Damaged();
        r.Skip(4);   // length
        var count = r.U16();
        if (count is <= 0 or > Sfnt.MaxTables) throw FontError.Damaged();
        r.Skip(30);   // reserved, totalSfntSize, version, metadata and private data
        var found = new List<(long Offset, long Stored, FontTag Tag, int Length)>();
        var seen = new HashSet<FontTag>();
        long total = 0;
        for (var i = 0; i < count; i++)
        {
            var tag = new FontTag((uint)r.U32());
            var offset = r.U32();
            var stored = r.U32();
            var length = r.U32();
            r.Skip(4);   // checksum: recomputed when the font is written
            total += length;
            // zlib can't shrink anything more than about 1032 times, so a table claiming more is
            // damaged, and isn't given the memory it asks for.
            if (stored > length || length > stored * 1100 + 1024 || total > MaxFontSize || !seen.Add(tag))
                throw FontError.Damaged();
            _ = new ByteReader(bytes, offset, stored);   // the block is in the file
            found.Add((offset, stored, tag, (int)length));
        }
        var tables = found.OrderBy(t => t.Offset).ToList();
        // Tables never share data; one block unpacked over and over would be a way to use up memory.
        // Checked before anything is unpacked, so such a file costs nothing.
        long end = 0;
        foreach (var t in tables.Where(t => t.Stored > 0))
        {
            if (t.Offset < end) throw FontError.Damaged();
            end = t.Offset + t.Stored;
        }
        return new Sfnt(flavor, [.. tables.Select(t =>
        {
            var block = bytes.AsSpan((int)t.Offset, (int)t.Stored);
            // A table that compression didn't make smaller is stored as it is.
            return new SfntTable(t.Tag, t.Stored == t.Length ? block.ToArray() : Inflate(bytes, (int)t.Offset, (int)t.Stored, t.Length));
        })]);
    }

    /// <summary>
    /// The font as a .woff file. Tables keep their order, so unwrapping it gives back the same font.
    /// Stop is noticed between tables.
    /// </summary>
    public static byte[] Encode(Sfnt font, Runner? runner = null)
    {
        var count = font.Tables.Count;
        var blocks = new List<byte[]>();
        var records = new List<(FontTag Tag, long Offset, int Stored, int Length, uint Checksum)>();
        long offset = 44 + 20 * count;
        long sfntSize = 12 + 16 * count;
        foreach (var table in font.Tables)
        {
            runner?.CheckCancelled();
            // Stored as the font has it; only the checksum leaves out the whole-file checksum in `head`.
            var data = table.Data;
            var packed = Deflate(data, runner);
            var block = packed.Length < data.Length ? packed : data;
            records.Add((table.Tag, offset, block.Length, data.Length, Sfnt.Checksum(Sfnt.Stored(table))));
            blocks.Add(block);
            offset += (block.Length + 3L) & ~3L;
            sfntSize += (data.Length + 3L) & ~3L;
        }
        if (offset > Array.MaxLength) throw FontError.Damaged();
        // The WOFF version is the font's own revision number, as other tools write it.
        var revision = font["head"] is { Length: >= 8 } head ? head.AsSpan(4, 4).ToArray() : new byte[4];

        var out_ = new ByteWriter((int)offset);
        out_.Put32(Signature);
        out_.Put32(font.Flavor);
        out_.Put32(offset);
        out_.Put16(count);
        out_.Put16(0);
        out_.Put32(sfntSize);
        out_.Put(revision);
        for (var i = 0; i < 5; i++) out_.Put32(0);   // no metadata, no private data
        foreach (var rec in records.OrderBy(x => x.Tag))
        {
            out_.Put32(rec.Tag.Value);
            out_.Put32(rec.Offset);
            out_.Put32(rec.Stored);
            out_.Put32(rec.Length);
            out_.Put32(rec.Checksum);
        }
        foreach (var block in blocks)
        {
            out_.Put(block);
            out_.Pad4();
        }
        return out_.ToArray();
    }

    // zlib

    /// <summary>
    /// The outlines of a large font take seconds to compress, so they go in a megabyte at a time,
    /// with Stop noticed in between. zlib makes the same bytes however its input is split.
    /// </summary>
    public static byte[] Deflate(byte[] data, Runner? runner = null)
    {
        using var buffer = new MemoryStream();
        using (var z = new ZLibStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            for (var pos = 0; pos < data.Length; pos += 1 << 20)
            {
                runner?.CheckCancelled();
                z.Write(data, pos, Math.Min(1 << 20, data.Length - pos));
            }
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Unpacks a zlib block that must come to exactly <paramref name="size"/> bytes. Memory grows with
    /// what actually comes out, not with the size the file claims.
    /// </summary>
    public static byte[] Inflate(byte[] bytes, int offset, int count, int size)
    {
        try
        {
            using var z = new ZLibStream(new MemoryStream(bytes, offset, count, writable: false), CompressionMode.Decompress);
            var out_ = new byte[Math.Min(size, 1 << 16)];
            var written = 0;
            while (true)
            {
                if (written == out_.Length)
                {
                    // Full: anything more than the size given is damage.
                    if (written == size) return z.ReadByte() < 0 ? out_ : throw FontError.Damaged();
                    Array.Resize(ref out_, (int)Math.Min(size, 2L * out_.Length));
                }
                var n = z.Read(out_, written, out_.Length - written);
                if (n == 0) throw FontError.Damaged();   // cut short
                written += n;
            }
        }
        catch (InvalidDataException) { throw FontError.Damaged(); }
    }
}
