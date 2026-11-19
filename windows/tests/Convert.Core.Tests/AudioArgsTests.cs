using Convert.Core;
using Xunit;

namespace Convert.Core.Tests;

/// <summary>
/// The ffmpeg arguments for audio targets. These check the flags that decide the result
/// (codec, sample format, bit depth, rate, channels), not the whole argument list.
/// </summary>
public sealed class AudioArgsTests
{
    private static readonly string[] Lossless = ["wav", "aiff", "flac", "alac", "wv"];
    private static readonly string[] Lossy = ["mp3", "m4a", "he-aac", "opus", "ogg", "wma", "ac3", "mp2"];
    private static readonly Probe CdSource = new(SampleRate: 44100, HiRes: false, HasAudio: true, Ok: true);
    private static readonly Probe HiResSource = new(SampleRate: 96000, HiRes: true, HasAudio: true, Ok: true);

    private static List<string> Args(string id, Probe source, int bitDepth = 0, int sampleRate = 0, int channels = 0, int? bitrate = null)
    {
        // The catalogue, not what this PC offers: HE-AAC's arguments are right whether or not ffmpeg can run them here.
        var target = Targets.Catalogue.Single(t => t.Id == id);
        var s = new ConvSettings { BitDepth = bitDepth, SampleRate = sampleRate, Channels = channels, AudioBitrate = bitrate ?? target.DefaultBitrate };
        return FF.AudioArgs(target, s, source);
    }

    /// <summary>The value following <paramref name="flag"/>, or null if the flag isn't there.</summary>
    private static string? Value(string flag, List<string> a)
    {
        var i = a.IndexOf(flag);
        return i >= 0 && i + 1 < a.Count ? a[i + 1] : null;
    }

    private static int Count(string flag, List<string> a) => a.Count(x => x == flag);

    /// <summary>What a lossless target must ask ffmpeg for to write <paramref name="bits"/>-bit audio.</summary>
    private static void ExpectDepth(string id, List<string> a, int bits)
    {
        var deep = bits == 24;
        switch (id)
        {
            case "wav": Assert.Equal(deep ? "pcm_s24le" : "pcm_s16le", Value("-c:a", a)); break;
            case "aiff": Assert.Equal(deep ? "pcm_s24be" : "pcm_s16be", Value("-c:a", a)); break;
            case "flac": Assert.Equal(deep ? "s32" : "s16", Value("-sample_fmt", a)); break;
            case "alac" or "wv": Assert.Equal(deep ? "s32p" : "s16p", Value("-sample_fmt", a)); break;
            default: Assert.Fail($"not a lossless target: {id}"); break;
        }
        // 24-bit audio stored in 32-bit samples needs its real depth spelled out (WavPack writes 32-bit otherwise).
        if (id is "flac" or "alac" or "wv") Assert.Equal(deep ? "24" : null, Value("-bits_per_raw_sample", a));
    }

    // Source detection

    [Theory]
    // (sample_fmt, bits_per_sample, bits_per_raw_sample) as ffprobe reports them
    [InlineData("s32", 24, 24)]    // 24-bit WAV
    [InlineData("s32", 32, 32)]    // 32-bit integer WAV
    [InlineData("flt", 32, 0)]     // 32-bit float WAV
    [InlineData("dbl", 64, 0)]     // 64-bit float WAV
    [InlineData("s32", 0, 24)]     // 24-bit FLAC
    [InlineData("s32p", 0, 24)]    // 24-bit ALAC
    public void MoreThanSixteenBitsIsHiRes(string format, int bitsPerSample, int bitsPerRawSample) =>
        Assert.True(FF.IsHiRes(format, bitsPerSample, bitsPerRawSample));

    [Theory]
    [InlineData("s16", 16, 0)]     // 16-bit WAV
    [InlineData("s16", 0, 16)]     // 16-bit FLAC
    [InlineData("fltp", 0, 0)]     // MP3, AAC, Opus: decoded to float, but not high resolution
    [InlineData("flt", 0, 0)]
    [InlineData("", 0, 0)]         // nothing known
    public void LossySourcesAndSixteenBitAreNot(string format, int bitsPerSample, int bitsPerRawSample) =>
        Assert.False(FF.IsHiRes(format, bitsPerSample, bitsPerRawSample));

    // Bit depth

    [Fact]
    public void MatchSourceKeepsSixteenBit()
    {
        foreach (var id in Lossless) ExpectDepth(id, Args(id, CdSource), bits: 16);
    }

    [Fact]
    public void MatchSourceKeepsHiResAtTwentyFourBit()
    {
        foreach (var id in Lossless) ExpectDepth(id, Args(id, HiResSource), bits: 24);
    }

    [Fact]
    public void ForcedDepthOverridesSource()
    {
        foreach (var id in Lossless)
        {
            ExpectDepth(id, Args(id, HiResSource, bitDepth: 16), bits: 16);
            ExpectDepth(id, Args(id, CdSource, bitDepth: 24), bits: 24);
        }
    }

    [Fact]
    public void LossyTargetsNeverSetBitDepth()
    {
        foreach (var id in Lossy)
        {
            var a = Args(id, HiResSource, bitDepth: 24);
            Assert.Null(Value("-bits_per_raw_sample", a));
            Assert.Null(Value("-sample_fmt", a));
        }
    }

    // Sample rate

    [Fact]
    public void LossyTargetsCapHiResSourcesAt48k()
    {
        foreach (var id in Lossy.Where(id => id != "opus"))
        {
            Assert.Equal("48000", Value("-ar", Args(id, HiResSource)));
            Assert.Null(Value("-ar", Args(id, CdSource)));
        }
        // Opus resamples internally and has no sample-rate setting.
        Assert.Null(Value("-ar", Args("opus", HiResSource)));
    }

    [Fact]
    public void LosslessTargetsKeepTheSourceRate()
    {
        foreach (var id in Lossless) Assert.Null(Value("-ar", Args(id, HiResSource)));
    }

    [Fact]
    public void ChosenSampleRateWins()
    {
        Assert.Equal("48000", Value("-ar", Args("flac", HiResSource, sampleRate: 48000)));
        var mp3 = Args("mp3", HiResSource, sampleRate: 44100);
        Assert.Equal("44100", Value("-ar", mp3));
        Assert.Equal(1, Count("-ar", mp3));
    }

    [Fact]
    public void UnofferedSampleRateIsIgnored()
    {
        // 96 kHz isn't offered for lossy targets: fall back to the 48 kHz cap.
        Assert.Equal("48000", Value("-ar", Args("mp3", HiResSource, sampleRate: 96000)));
        Assert.Null(Value("-ar", Args("opus", CdSource, sampleRate: 48000)));
    }

    // Channels and codecs

    [Fact]
    public void Channels()
    {
        Assert.Null(Value("-ac", Args("mp3", CdSource)));
        Assert.Equal("1", Value("-ac", Args("mp3", CdSource, channels: 1)));
        Assert.Equal("2", Value("-ac", Args("flac", CdSource, channels: 2)));
        // There's no channel setting for Vorbis: ffmpeg's own encoder is always stereo, libvorbis follows the source.
        var ogg = Args("ogg", CdSource, channels: 1);
        Assert.True(Count("-ac", ogg) <= 1);
        Assert.NotEqual("1", Value("-ac", ogg));
    }

    [Fact]
    public void Codecs()
    {
        // As on the Mac, except where the Mac uses Apple's encoders (aac_at): ffmpeg's own AAC, and
        // libfdk_aac for HE-AAC, which only some ffmpeg builds have (the format is hidden without it).
        var codecs = new Dictionary<string, string>
        {
            ["mp3"] = "libmp3lame",
            ["m4a"] = "aac",
            ["he-aac"] = "libfdk_aac",
            ["opus"] = "libopus",
            ["wma"] = "wmav2",
            ["ac3"] = "ac3",
            ["mp2"] = "mp2",
            ["flac"] = "flac",
            ["alac"] = "alac",
            ["wv"] = "wavpack",
        };
        foreach (var (id, codec) in codecs) Assert.Equal(codec, Value("-c:a", Args(id, CdSource)));
        Assert.Contains(Value("-c:a", Args("ogg", CdSource)), new[] { "libvorbis", "vorbis" });
        Assert.Equal("aac_he", Value("-profile:a", Args("he-aac", CdSource)));
    }

    [Fact]
    public void Mp3BitrateModes()
    {
        var vbr = Args("mp3", CdSource, bitrate: 0);
        Assert.Equal("0", Value("-q:a", vbr));
        Assert.Null(Value("-b:a", vbr));
        var cbr = Args("mp3", CdSource, bitrate: 320);
        Assert.Equal("320k", Value("-b:a", cbr));
        Assert.Null(Value("-q:a", cbr));
    }

    [Fact]
    public void FfmpegIsNeverToldToOverwrite()
    {
        Assert.DoesNotContain("-y", FF.Common);
    }
}
