using System.Buffers;
using System.IO.Compression;

namespace Convert.Core;

/// <summary>Brotli, the compression inside WOFF2: the encoder and decoder that come with .NET.</summary>
public static class Brotli
{
    /// <summary>The highest setting, the one the Mac gets from the <c>brotli</c> tool.</summary>
    public const int Quality = 11;

    /// <summary>
    /// The largest window the format allows (16 MB), as the <c>brotli</c> tool uses by default: the
    /// encoder can then refer back across a whole font, however large.
    /// </summary>
    public const int Window = 24;

    /// <summary>
    /// Unpacks a stream that must come to exactly <paramref name="size"/> bytes. Memory grows with
    /// what actually comes out, not with <paramref name="size"/>, which a damaged file can set to anything.
    /// </summary>
    public static byte[] Decode(ReadOnlySpan<byte> data, long size)
    {
        if (data.IsEmpty || size <= 0 || size > Array.MaxLength) throw FontError.Damaged();
        using var decoder = new BrotliDecoder();
        var out_ = new byte[Math.Min(size, 1 << 16)];
        var written = 0;
        Span<byte> spare = stackalloc byte[1];
        while (true)
        {
            if (written == out_.Length && written < size) Array.Resize(ref out_, (int)Math.Min(size, 2L * out_.Length));
            // Once the size is reached, anything more that comes out is damage.
            var destination = written < out_.Length ? out_.AsSpan(written) : spare;
            var status = decoder.Decompress(data, destination, out var consumed, out var produced);
            if (written == out_.Length && produced > 0) throw FontError.Damaged();
            data = data[consumed..];
            written += produced;
            switch (status)
            {
                case OperationStatus.Done:
                    return written == size ? out_ : throw FontError.Damaged();
                case OperationStatus.DestinationTooSmall when consumed > 0 || produced > 0:
                    continue;
                default:
                    // A broken stream, one cut short, or one that stopped going anywhere.
                    throw FontError.Damaged();
            }
        }
    }

    /// <summary>All of <paramref name="data"/> in one go, for tests and small inputs.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> data, int quality = Quality)
    {
        var out_ = new byte[BrotliEncoder.GetMaxCompressedLength(data.Length)];
        if (!BrotliEncoder.TryCompress(data, out_, out var written, quality, Window))
            throw new ConvertException("Couldn't compress this font");
        return out_[..written];
    }

    /// <summary>
    /// The smallest stream .NET's encoder makes: byte for byte what <c>brotli --quality=11</c> makes,
    /// as both are Google's library. Stop is noticed at once.
    /// </summary>
    /// <remarks>
    /// At this setting the encoder gathers up to 16 MB before it starts, so it works in few, long
    /// calls that can't be interrupted (for most fonts one, at the very end: several seconds, minutes
    /// for a large CJK font). Forcing it to work in small pieces would let it be stopped between them,
    /// but makes the file about 1% larger. So it works on a thread of its own: Stop doesn't wait for
    /// it, and it gives up at the next piece of input, with what it made thrown away.
    /// </remarks>
    public static byte[] Best(byte[] data, Runner runner)
    {
        runner.CheckCancelled();
        var work = Task.Factory.StartNew(() => Compress(data, () => runner.IsCancelled), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            work.Wait(runner.Token);
        }
        catch (OperationCanceledException)
        {
            throw new StoppedException();
        }
        catch (AggregateException) { }   // rethrown as it was below
        return work.GetAwaiter().GetResult() ?? throw new StoppedException();
    }

    /// <summary>How much input the encoder is given at a time.</summary>
    private const int Piece = 1 << 16;

    /// <summary>Null when <paramref name="stopped"/> says so between pieces.</summary>
    private static byte[]? Compress(byte[] data, Func<bool> stopped)
    {
        using var encoder = new BrotliEncoder(Quality, Window);
        var out_ = new ByteWriter(data.Length / 2 + 1024);
        var buffer = new byte[1 << 16];
        var pos = 0;
        while (true)
        {
            if (stopped()) return null;
            var take = Math.Min(Piece, data.Length - pos);
            var final = pos + take == data.Length;
            var status = encoder.Compress(data.AsSpan(pos, take), buffer, out var consumed, out var written, final);
            out_.Put(buffer.AsSpan(0, written));
            pos += consumed;
            switch (status)
            {
                case OperationStatus.Done when final && pos == data.Length:
                    return out_.ToArray();
                case OperationStatus.Done or OperationStatus.DestinationTooSmall:
                    continue;
                default:
                    throw new ConvertException("Couldn't compress this font");
            }
        }
    }
}
