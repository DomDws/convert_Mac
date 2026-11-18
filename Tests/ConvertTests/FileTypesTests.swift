import Foundation
import Testing

@testable import Convert

/// `FileTypes` is the one list of what Convert accepts. The app bundle's Info.plist can't read it,
/// so this fails when the two drift apart.
struct FileTypesTests {
    private var infoPlist: URL {
        URL(fileURLWithPath: #filePath)  // Tests/ConvertTests/FileTypesTests.swift
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("Resources/Info.plist")
    }

    @Test func bundleDeclaresExactlyTheAcceptedTypes() throws {
        let plist = try PropertyListSerialization.propertyList(from: Data(contentsOf: infoPlist), format: nil)
        let info = try #require(plist as? [String: Any])
        let documentTypes = try #require(info["CFBundleDocumentTypes"] as? [[String: Any]])
        let declared = documentTypes.flatMap { $0["LSItemContentTypes"] as? [String] ?? [] }
        #expect(
            Set(declared) == Set(FileTypes.identifiers),
            "Resources/Info.plist and FileTypes.accepted (Formats.swift) must list the same content types")
        #expect(declared.count == Set(declared).count, "a content type is listed twice in Info.plist")
    }

    @Test func everyKindHasContentTypes() {
        for kind in MediaKind.allCases {
            #expect(FileTypes.accepted.contains { $0.kind == kind && !$0.identifiers.isEmpty }, "\(kind)")
        }
        #expect(FileTypes.identifiers.count == Set(FileTypes.identifiers).count)
    }

    @Test func kindsByFileName() {
        let expected: [(String, MediaKind?)] = [
            ("song.mp3", .audio), ("song.WAV", .audio), ("song.flac", .audio), ("song.opus", .audio),
            ("song.wv", .audio), ("song.caf", .audio), ("song.m4a", .audio),
            ("clip.mp4", .video), ("clip.mov", .video), ("clip.mkv", .video), ("clip.webm", .video),
            ("clip.avi", .video), ("clip.m2ts", .video),
            ("photo.jpg", .image), ("photo.png", .image), ("photo.heic", .image), ("photo.tiff", .image),
            ("photo.gif", .image),
            ("paper.pdf", .pdf),
            ("notes.md", .document), ("notes.markdown", .document), ("notes.txt", .document),
            ("letter.docx", .document), ("letter.doc", .document), ("letter.odt", .document),
            ("letter.rtf", .document), ("page.html", .document), ("main.swift", .document),
            ("archive.zip", nil), ("disk.dmg", nil), ("noextension", nil), ("file.unknownext", nil),
        ]
        for (name, kind) in expected {
            #expect(MediaKind.of(URL(fileURLWithPath: "/tmp/\(name)")) == kind, "\(name)")
        }
    }
}
