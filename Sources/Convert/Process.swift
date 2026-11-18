import Foundation

struct ConvertError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}

enum Tools {
    static func find(_ name: String) -> String? {
        for dir in ["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"] {
            let path = "\(dir)/\(name)"
            if FileManager.default.isExecutableFile(atPath: path) { return path }
        }
        return nil
    }
    static let ffmpeg = find("ffmpeg")
    static let ffprobe = find("ffprobe")
    static let cwebp = find("cwebp")
}

func lastLine(_ data: Data) -> String? {
    String(decoding: data, as: UTF8.self)
        .split(separator: "\n")
        .map { $0.trimmingCharacters(in: .whitespaces) }
        .last { !$0.isEmpty }
}

/// Runs a tool to completion and returns stdout. Throws with the last stderr line on failure.
@discardableResult
func runSync(_ exe: String, _ args: [String]) throws -> String {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: exe)
    p.arguments = args
    let out = Pipe()
    let err = Pipe()
    p.standardOutput = out
    p.standardError = err
    p.standardInput = FileHandle.nullDevice
    try p.run()
    let data = out.fileHandleForReading.readDataToEndOfFile()
    let errData = err.fileHandleForReading.readDataToEndOfFile()
    p.waitUntilExit()
    guard p.terminationStatus == 0 else {
        throw ConvertError(lastLine(errData) ?? "\(URL(fileURLWithPath: exe).lastPathComponent) failed")
    }
    return String(decoding: data, as: UTF8.self)
}

/// Where converted files go: next to the original, or a fallback folder when that isn't possible.
enum OutputFolder {
    /// Used when the original's folder can't be written to (a read-only disk or disk image, no permission).
    /// Downloads; `CONVERT_FALLBACK_DIR` replaces it for tests.
    static var fallback: URL {
        if let dir = ProcessInfo.processInfo.environment["CONVERT_FALLBACK_DIR"], !dir.isEmpty {
            return URL(fileURLWithPath: dir, isDirectory: true)
        }
        return FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Downloads", isDirectory: true)
    }

    /// The folder to write a conversion of `input` into.
    static func directory(for input: URL, fallback: URL = OutputFolder.fallback) throws -> URL {
        let dir = input.deletingLastPathComponent()
        if FileManager.default.isWritableFile(atPath: dir.path) { return dir }
        guard FileManager.default.isWritableFile(atPath: fallback.path) else {
            throw ConvertError(
                "Can't save the converted file: the original's folder is read-only, and "
                    + "“\(name(of: fallback))” can't be written to either")
        }
        return fallback
    }

    /// Whether `output` was written somewhere other than next to `input`.
    static func isFallback(_ output: URL, for input: URL) -> Bool {
        output.deletingLastPathComponent().standardizedFileURL.path
            != input.deletingLastPathComponent().standardizedFileURL.path
    }

    /// The folder's name as Finder shows it.
    static func name(of folder: URL) -> String { FileManager.default.displayName(atPath: folder.path) }
}

/// Next free file name next to the input (or in the fallback folder): "name.ext", then "name 2.ext", …
func uniqueOutput(
    for input: URL, ext: String, suffix: String = "", fallback: URL = OutputFolder.fallback
) throws -> URL {
    let dir = try OutputFolder.directory(for: input, fallback: fallback)
    let base = input.deletingPathExtension().lastPathComponent + suffix
    var url = dir.appendingPathComponent(base).appendingPathExtension(ext)
    var n = 2
    while FileManager.default.fileExists(atPath: url.path) {
        url = dir.appendingPathComponent("\(base) \(n)").appendingPathExtension(ext)
        n += 1
    }
    return url
}

func uniqueFolder(for input: URL, suffix: String, fallback: URL = OutputFolder.fallback) throws -> URL {
    let dir = try OutputFolder.directory(for: input, fallback: fallback)
    let base = input.deletingPathExtension().lastPathComponent + suffix
    var url = dir.appendingPathComponent(base, isDirectory: true)
    var n = 2
    while FileManager.default.fileExists(atPath: url.path) {
        url = dir.appendingPathComponent("\(base) \(n)", isDirectory: true)
        n += 1
    }
    return url
}

private final class DataBox: @unchecked Sendable {
    private var data = Data()
    private let lock = NSLock()
    func append(_ d: Data) {
        lock.lock()
        data.append(d)
        lock.unlock()
    }
    var value: Data {
        lock.lock()
        defer { lock.unlock() }
        return data
    }
}

/// Runs ffmpeg-style processes with live progress and cancellation.
final class Runner: @unchecked Sendable {
    private var current: Process?
    private let lock = NSLock()

    func cancel() {
        lock.lock()
        current?.terminate()
        lock.unlock()
    }

    func run(
        _ exe: String, _ args: [String], duration: Double,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws {
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            let p = Process()
            p.executableURL = URL(fileURLWithPath: exe)
            p.arguments = args
            let out = Pipe()
            let err = Pipe()
            p.standardOutput = out
            p.standardError = err
            p.standardInput = FileHandle.nullDevice
            let errBox = DataBox()
            err.fileHandleForReading.readabilityHandler = { errBox.append($0.availableData) }
            out.fileHandleForReading.readabilityHandler = { h in
                guard duration > 0 else { return }
                let text = String(decoding: h.availableData, as: UTF8.self)
                for line in text.split(separator: "\n") where line.hasPrefix("out_time_us=") {
                    if let us = Double(line.dropFirst("out_time_us=".count)), us >= 0 {
                        progress(min(1, us / 1_000_000 / duration))
                    }
                }
            }
            p.terminationHandler = { [weak self] proc in
                out.fileHandleForReading.readabilityHandler = nil
                err.fileHandleForReading.readabilityHandler = nil
                self?.lock.lock()
                self?.current = nil
                self?.lock.unlock()
                if proc.terminationStatus == 0 {
                    cont.resume()
                } else if proc.terminationReason == .uncaughtSignal {
                    cont.resume(throwing: ConvertError("Stopped"))
                } else {
                    cont.resume(
                        throwing: ConvertError(
                            lastLine(errBox.value) ?? "ffmpeg exited with code \(proc.terminationStatus)"))
                }
            }
            lock.lock()
            do {
                try p.run()
                current = p
                lock.unlock()
            } catch {
                lock.unlock()
                cont.resume(throwing: error)
            }
        }
    }
}
