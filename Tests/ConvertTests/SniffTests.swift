import Foundation
import Testing

@testable import Convert

/// Telling what a file is from its contents: `.ts` (TypeScript or transport stream), and misnamed files.
final class SniffTests {
    private let dir: URL

    init() throws {
        dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: false)
    }

    deinit {
        try? FileManager.default.removeItem(at: dir)
    }

    private func file(_ name: String, _ bytes: [UInt8]) throws -> URL {
        let url = dir.appendingPathComponent(name)
        try Data(bytes).write(to: url)
        return url
    }

    private func file(_ name: String, _ text: String) throws -> URL { try file(name, Array(text.utf8)) }

    /// `packets` transport-stream packets of `size` bytes, each starting with the sync byte after `prefix` bytes.
    private func transportStream(packets: Int = 4, size: Int = 188, prefix: Int = 0) -> [UInt8] {
        var bytes: [UInt8] = []
        for i in 0..<packets {
            var packet = [UInt8](repeating: UInt8(truncatingIfNeeded: i &* 31 &+ 1), count: size)
            packet[prefix] = 0x47
            packet[prefix + 1] = 0x00  // binary, never mistaken for text
            bytes += packet
        }
        return bytes
    }

    private let wav: [UInt8] = Array("RIFF".utf8) + [0x24, 0x08, 0, 0] + Array("WAVEfmt ".utf8) + [16, 0, 0, 0, 1, 0]
    private let png: [UInt8] = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13]
    private func mp4(brand: String) -> [UInt8] { [0, 0, 0, 0x20] + Array("ftyp\(brand)".utf8) + [0, 0, 2, 0] }

    // MARK: .ts

    @Test func typeScriptIsADocument() throws {
        let code = try file("app.ts", "export const answer: number = 42;\nconsole.log(answer);\n")
        #expect(MediaKind.of(code) == .document)
        // Starting with the sync byte's letter isn't enough to be a transport stream.
        let g = try file("greeting.ts", "Greeter.hello();\n" + String(repeating: "// padding line\n", count: 40))
        #expect(MediaKind.of(g) == .document)
    }

    @Test func transportStreamIsVideo() throws {
        #expect(MediaKind.of(try file("clip.ts", transportStream())) == .video)
        #expect(MediaKind.of(try file("bluray.ts", transportStream(size: 192, prefix: 4))) == .video)
        #expect(Sniff.isTransportStream(Data(transportStream(packets: 2))))
        #expect(!Sniff.isTransportStream(Data(transportStream(packets: 1))))
    }

    @Test func unreadableOrEmptyTSFallsBackToTheExtension() throws {
        #expect(MediaKind.of(try file("empty.ts", [UInt8]())) == .video)
        #expect(MediaKind.of(dir.appendingPathComponent("missing.ts")) == .video)
        // Binary that isn't a transport stream: still video, ffmpeg gets to decide.
        #expect(MediaKind.of(try file("odd.ts", [0x00, 0x01, 0x02, 0x03, 0xFF])) == .video)
    }

    @Test func typeScriptConvertsToPlainText() async throws {
        let code = try file("app.ts", "export const answer: number = 42;\n")
        let target = try #require(Targets.byID("txt"))
        let outputs = try await Converter.convert(code, to: target, settings: ConvSettings(), runner: Runner()) { _ in }
        let text = try String(contentsOf: #require(outputs.first), encoding: .utf8)
        #expect(text.contains("answer: number = 42"))
    }

    // MARK: Misnamed files

    @Test func audioNamedAsImageSaysSo() throws {
        let fake = try file("song.jpg", wav)
        let error = #expect(throws: ConvertError.self) { try Sniff.verify(fake, as: .image) }
        #expect(
            error?.message
                == "This isn't really a .jpg file: its contents are WAV audio. Rename it to .wav and convert it again.")
    }

    @Test func conversionReportsTheMismatchInsteadOfADecodeFailure() async throws {
        let fake = try file("song.JPG", wav)
        let target = try #require(Targets.byID("png"))
        let error = await #expect(throws: ConvertError.self) {
            try await Converter.convert(fake, to: target, settings: ConvSettings(), runner: Runner()) { _ in }
        }
        #expect(error?.message.contains("isn't really a .jpg file") == true)
        #expect(error?.message.contains("WAV audio") == true)
        #expect(try FileManager.default.contentsOfDirectory(atPath: dir.path) == ["song.JPG"])
    }

    @Test func otherKindsAreCaughtToo() throws {
        #expect(throws: ConvertError.self) { try Sniff.verify(try file("a.mp3", png), as: .audio) }
        #expect(throws: ConvertError.self) { try Sniff.verify(try file("a.pdf", png), as: .pdf) }
        #expect(throws: ConvertError.self) { try Sniff.verify(try file("a.png", mp4(brand: "isom")), as: .image) }
        // A binary file posing as text.
        #expect(throws: ConvertError.self) { try Sniff.verify(try file("a.txt", png), as: .document) }
    }

    @Test func matchingAndUnknownContentsPass() throws {
        // Same kind under another name: the converters read these by content anyway.
        try Sniff.verify(try file("photo.jpg", png), as: .image)
        // MP4 can be audio only, or video.
        try Sniff.verify(try file("song.m4a", mp4(brand: "isom")), as: .audio)
        try Sniff.verify(try file("clip.mp4", mp4(brand: "M4A ")), as: .video)
        // Unknown brands (camera raw in an MP4-style container) and unknown bytes are left to the converters.
        try Sniff.verify(try file("raw.cr3", mp4(brand: "crx ")), as: .image)
        try Sniff.verify(try file("mystery.jpg", [0x01, 0x02, 0x03, 0x04]), as: .image)
        try Sniff.verify(try file("empty.jpg", [UInt8]()), as: .image)
        try Sniff.verify(dir.appendingPathComponent("missing.jpg"), as: .image)
    }

    @Test func textThatStartsLikeAnotherFormatIsStillText() throws {
        try Sniff.verify(try file("cars.txt", "BMW and Audi are car makers.\n"), as: .document)
        try Sniff.verify(try file("tags.txt", "ID3 is how MP3 files carry their titles.\n"), as: .document)
        try Sniff.verify(try file("riff.md", "RIFF is a container format; WAVE is one use of it.\n"), as: .document)
        try Sniff.verify(try file("utf16.txt", [0xFF, 0xFE, 0x48, 0x00, 0x69, 0x00]), as: .document)
        #expect(Sniff.format(Data("BMW and Audi are car makers.\n".utf8)) == nil)
        #expect(Sniff.format(Data([0xFF, 0xFE, 0x48, 0x00, 0x69, 0x00])) == nil)
    }

    @Test func knownFormats() {
        let cases: [([UInt8], String, Set<MediaKind>)] = [
            ([0xFF, 0xD8, 0xFF, 0xE0], "a JPEG image", [.image]),
            (png, "a PNG image", [.image]),
            (Array("GIF89a".utf8), "a GIF image", [.image]),
            (wav, "WAV audio", [.audio]),
            (Array("fLaC".utf8) + [0, 0, 0, 34], "FLAC audio", [.audio]),
            (Array("ID3".utf8) + [4, 0, 0, 0, 0, 0, 0], "MP3 audio", [.audio]),
            ([0xFF, 0xFB, 0x90, 0x64], "MPEG audio (MP3 or AAC)", [.audio]),
            (Array("OggS".utf8) + [0, 2], "an Ogg file", [.audio, .video]),
            ([0x1A, 0x45, 0xDF, 0xA3], "a Matroska or WebM file", [.audio, .video]),
            (mp4(brand: "heic"), "a HEIC image", [.image]),
            (mp4(brand: "qt  "), "a QuickTime movie", [.audio, .video]),
            (Array("%PDF-1.7\n".utf8), "a PDF", [.pdf, .image]),
            (transportStream(), "an MPEG transport stream", [.video]),
        ]
        for (bytes, name, kinds) in cases {
            let format = Sniff.format(Data(bytes))
            #expect(format?.name == name)
            #expect(format?.kinds == kinds, "\(name)")
        }
    }
}
