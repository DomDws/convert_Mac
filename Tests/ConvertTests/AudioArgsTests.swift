import Testing

@testable import Convert

/// The ffmpeg arguments for audio targets. These check the flags that decide the result
/// (codec, sample format, bit depth, rate, channels), not the whole argument list.
struct AudioArgsTests {
    private let lossless = ["wav", "aiff", "flac", "alac", "wv"]
    private let cdSource = Probe(sampleRate: 44100, hires: false, hasAudio: true, ok: true)
    private let hiResSource = Probe(sampleRate: 96000, hires: true, hasAudio: true, ok: true)

    private func args(
        _ id: String, source: Probe, bitDepth: Int = 0, sampleRate: Int = 0,
        channels: Int = 0, bitrate: Int? = nil
    ) throws -> [String] {
        let target = try #require(Targets.byID(id), "no target \(id)")
        var s = ConvSettings()
        s.bitDepth = bitDepth
        s.sampleRate = sampleRate
        s.channels = channels
        s.audioBitrate = bitrate ?? target.defaultBitrate
        return FF.audioArgs(target, s, source)
    }

    /// The value following `flag`, or nil if the flag isn't there.
    private func value(_ flag: String, _ a: [String]) -> String? {
        guard let i = a.firstIndex(of: flag), i + 1 < a.count else { return nil }
        return a[i + 1]
    }

    private func count(_ flag: String, _ a: [String]) -> Int { a.filter { $0 == flag }.count }

    /// What a lossless target must ask ffmpeg for to write `bits`-bit audio.
    private func expectDepth(
        _ id: String, _ a: [String], bits: Int, sourceLocation: SourceLocation = #_sourceLocation
    ) {
        let deep = bits == 24
        switch id {
        case "wav":
            #expect(value("-c:a", a) == (deep ? "pcm_s24le" : "pcm_s16le"), "\(id)", sourceLocation: sourceLocation)
        case "aiff":
            #expect(value("-c:a", a) == (deep ? "pcm_s24be" : "pcm_s16be"), "\(id)", sourceLocation: sourceLocation)
        case "flac":
            #expect(value("-sample_fmt", a) == (deep ? "s32" : "s16"), "\(id)", sourceLocation: sourceLocation)
        case "alac", "wv":
            #expect(value("-sample_fmt", a) == (deep ? "s32p" : "s16p"), "\(id)", sourceLocation: sourceLocation)
        default:
            Issue.record("not a lossless target: \(id)", sourceLocation: sourceLocation)
        }
        // 24-bit audio stored in 32-bit samples needs its real depth spelled out (WavPack writes 32-bit otherwise).
        if ["flac", "alac", "wv"].contains(id) {
            #expect(value("-bits_per_raw_sample", a) == (deep ? "24" : nil), "\(id)", sourceLocation: sourceLocation)
        }
    }

    // MARK: Source detection

    @Test func hiResDetection() {
        // (sample_fmt, bits_per_sample, bits_per_raw_sample) as ffprobe reports them
        let deep: [(String, Int, Int)] = [
            ("s32", 24, 24),  // 24-bit WAV
            ("s32", 32, 32),  // 32-bit integer WAV
            ("flt", 32, 0),  // 32-bit float WAV
            ("dbl", 64, 0),  // 64-bit float WAV
            ("s32", 0, 24),  // 24-bit FLAC
            ("s32p", 0, 24),  // 24-bit ALAC
        ]
        let shallow: [(String, Int, Int)] = [
            ("s16", 16, 0),  // 16-bit WAV
            ("s16", 0, 16),  // 16-bit FLAC
            ("fltp", 0, 0),  // MP3, AAC, Opus: decoded to float, but not high resolution
            ("flt", 0, 0),
            ("", 0, 0),  // nothing known
        ]
        for (fmt, bps, raw) in deep {
            #expect(FF.isHiRes(sampleFormat: fmt, bitsPerSample: bps, bitsPerRawSample: raw), "\(fmt) \(bps) \(raw)")
        }
        for (fmt, bps, raw) in shallow {
            #expect(!FF.isHiRes(sampleFormat: fmt, bitsPerSample: bps, bitsPerRawSample: raw), "\(fmt) \(bps) \(raw)")
        }
    }

    // MARK: Bit depth

    @Test func matchSourceKeepsSixteenBit() throws {
        for id in lossless { expectDepth(id, try args(id, source: cdSource), bits: 16) }
    }

    @Test func matchSourceKeepsHiResAtTwentyFourBit() throws {
        for id in lossless { expectDepth(id, try args(id, source: hiResSource), bits: 24) }
    }

    @Test func forcedDepthOverridesSource() throws {
        for id in lossless {
            expectDepth(id, try args(id, source: hiResSource, bitDepth: 16), bits: 16)
            expectDepth(id, try args(id, source: cdSource, bitDepth: 24), bits: 24)
        }
    }

    @Test func lossyTargetsNeverSetBitDepth() throws {
        for id in ["mp3", "m4a", "he-aac", "opus", "ogg", "wma", "ac3", "mp2"] {
            let a = try args(id, source: hiResSource, bitDepth: 24)
            #expect(value("-bits_per_raw_sample", a) == nil, "\(id)")
            #expect(value("-sample_fmt", a) == nil, "\(id)")
        }
    }

    // MARK: Sample rate

    @Test func lossyTargetsCapHiResSourcesAt48k() throws {
        for id in ["mp3", "m4a", "he-aac", "ogg", "wma", "ac3", "mp2"] {
            let hiRes = try args(id, source: hiResSource)
            let cd = try args(id, source: cdSource)
            #expect(value("-ar", hiRes) == "48000", "\(id)")
            #expect(value("-ar", cd) == nil, "\(id)")
        }
        // Opus resamples internally and has no sample-rate setting.
        let opus = try args("opus", source: hiResSource)
        #expect(value("-ar", opus) == nil)
    }

    @Test func losslessTargetsKeepTheSourceRate() throws {
        for id in lossless {
            let a = try args(id, source: hiResSource)
            #expect(value("-ar", a) == nil, "\(id)")
        }
    }

    @Test func chosenSampleRateWins() throws {
        let flac = try args("flac", source: hiResSource, sampleRate: 48000)
        #expect(value("-ar", flac) == "48000")
        let mp3 = try args("mp3", source: hiResSource, sampleRate: 44100)
        #expect(value("-ar", mp3) == "44100")
        #expect(count("-ar", mp3) == 1)
    }

    @Test func unofferedSampleRateIsIgnored() throws {
        // 96 kHz isn't offered for lossy targets: fall back to the 48 kHz cap.
        let mp3 = try args("mp3", source: hiResSource, sampleRate: 96000)
        #expect(value("-ar", mp3) == "48000")
        let opus = try args("opus", source: cdSource, sampleRate: 48000)
        #expect(value("-ar", opus) == nil)
    }

    // MARK: Channels and codecs

    @Test func channels() throws {
        let same = try args("mp3", source: cdSource)
        #expect(value("-ac", same) == nil)
        let mono = try args("mp3", source: cdSource, channels: 1)
        #expect(value("-ac", mono) == "1")
        let stereo = try args("flac", source: cdSource, channels: 2)
        #expect(value("-ac", stereo) == "2")
        // The Vorbis encoder is always stereo; there's no channel setting for it.
        let ogg = try args("ogg", source: cdSource, channels: 1)
        #expect(value("-ac", ogg) == "2")
        #expect(count("-ac", ogg) == 1)
    }

    @Test func codecs() throws {
        let codecs = [
            "mp3": "libmp3lame", "m4a": "aac_at", "he-aac": "aac_at", "opus": "libopus", "ogg": "vorbis",
            "wma": "wmav2", "ac3": "ac3", "mp2": "mp2", "flac": "flac", "alac": "alac", "wv": "wavpack",
        ]
        for (id, codec) in codecs {
            let a = try args(id, source: cdSource)
            #expect(value("-c:a", a) == codec, "\(id)")
        }
        let heAAC = try args("he-aac", source: cdSource)
        #expect(value("-profile:a", heAAC) == "4")
    }

    @Test func mp3BitrateModes() throws {
        let vbr = try args("mp3", source: cdSource, bitrate: 0)
        #expect(value("-q:a", vbr) == "0")
        #expect(value("-b:a", vbr) == nil)
        let cbr = try args("mp3", source: cdSource, bitrate: 320)
        #expect(value("-b:a", cbr) == "320k")
        #expect(value("-q:a", cbr) == nil)
    }
}
