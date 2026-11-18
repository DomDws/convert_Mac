import CoreGraphics
import Foundation
import Testing

@testable import Convert

/// Where converted files go: next to the original, or the fallback folder (Downloads) when the
/// original's folder is read-only. The smoke test runs real conversions out of a read-only folder.
final class OutputFolderTests {
    private let source: URL
    private let fallback: URL

    init() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        source = root.appendingPathComponent("source")
        fallback = root.appendingPathComponent("fallback")
        try FileManager.default.createDirectory(at: source, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: fallback, withIntermediateDirectories: true)
    }

    deinit {
        makeWritable(source, true)
        makeWritable(fallback, true)
        try? FileManager.default.removeItem(at: source.deletingLastPathComponent())
    }

    private func makeWritable(_ folder: URL, _ writable: Bool) {
        try? FileManager.default.setAttributes([.posixPermissions: writable ? 0o755 : 0o555], ofItemAtPath: folder.path)
    }

    private func original(_ name: String = "photo.png") throws -> URL {
        let url = source.appendingPathComponent(name)
        try Data("x".utf8).write(to: url)
        return url
    }

    private func touch(_ url: URL) throws { try Data("x".utf8).write(to: url) }

    @Test func outputGoesNextToTheOriginal() throws {
        let input = try original()
        let out = try uniqueOutput(for: input, ext: "jpg", fallback: fallback)
        #expect(out == source.appendingPathComponent("photo.jpg"))
        #expect(!OutputFolder.isFallback(out, for: input))
        // A taken name gets a number.
        try touch(out)
        #expect(try uniqueOutput(for: input, ext: "jpg", fallback: fallback).lastPathComponent == "photo 2.jpg")
    }

    @Test func readOnlyFolderFallsBack() throws {
        let input = try original()
        makeWritable(source, false)
        let out = try uniqueOutput(for: input, ext: "jpg", fallback: fallback)
        #expect(out == fallback.appendingPathComponent("photo.jpg"))
        #expect(OutputFolder.isFallback(out, for: input))
        // The same "name 2.ext" rule applies in the fallback folder.
        try touch(out)
        try touch(fallback.appendingPathComponent("photo 2.jpg"))
        #expect(try uniqueOutput(for: input, ext: "jpg", fallback: fallback).lastPathComponent == "photo 3.jpg")
    }

    @Test func pageFoldersFallBackToo() throws {
        let input = try original("scan.pdf")
        makeWritable(source, false)
        let folder = try uniqueFolder(for: input, suffix: " pages", fallback: fallback)
        #expect(folder.path == fallback.appendingPathComponent("scan pages").path)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: false)
        #expect(try uniqueFolder(for: input, suffix: " pages", fallback: fallback).lastPathComponent == "scan pages 2")
    }

    @Test func errorNamesThePermissionProblemWhenNothingIsWritable() throws {
        let input = try original()
        makeWritable(source, false)
        makeWritable(fallback, false)
        let error = #expect(throws: ConvertError.self) {
            try uniqueOutput(for: input, ext: "jpg", fallback: fallback)
        }
        #expect(error?.message.contains("read-only") == true)
        #expect(error?.message.contains("fallback") == true)
        #expect(error?.message.contains("JPEG") == false)
    }

    @Test func imageWriteFailureBlamesTheFolderNotTheFormat() throws {
        // What used to say "This Mac can't write JPEG".
        makeWritable(source, false)
        let space = try #require(CGColorSpace(name: CGColorSpace.sRGB))
        let context = try #require(
            CGContext(
                data: nil, width: 2, height: 2, bitsPerComponent: 8, bytesPerRow: 0, space: space,
                bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue))
        let image = try #require(context.makeImage())
        let error = #expect(throws: ConvertError.self) {
            try ImageConv.write(
                image, to: source.appendingPathComponent("out.jpg"), type: .jpeg, label: "JPEG", props: [:])
        }
        #expect(error?.message.contains("can't be written to") == true)
    }

    // MARK: Move original to Trash

    @Test func originalIsOnlyTrashedWhenItsOutputSitsNextToIt() throws {
        let input = try original()
        let beside = source.appendingPathComponent("photo.jpg")
        try touch(beside)
        #expect(ConvertModel.mayTrash(input, outputs: [beside], stopped: false))

        // Saved to the fallback folder: the original stays.
        let elsewhere = fallback.appendingPathComponent("photo.jpg")
        try touch(elsewhere)
        #expect(!ConvertModel.mayTrash(input, outputs: [elsewhere], stopped: false))

        // Stopped, nothing written, output missing, or output is the original itself: never.
        #expect(!ConvertModel.mayTrash(input, outputs: [beside], stopped: true))
        #expect(!ConvertModel.mayTrash(input, outputs: [], stopped: false))
        #expect(!ConvertModel.mayTrash(input, outputs: [source.appendingPathComponent("gone.jpg")], stopped: false))
        #expect(!ConvertModel.mayTrash(input, outputs: [input], stopped: false))
    }
}
