import Foundation
import SwiftUI

@MainActor
final class ConvertModel: ObservableObject {
    enum Phase { case idle, running, done }

    @Published private(set) var files: [URL] = []
    @Published private(set) var skipped = 0
    @Published var targetID = "" { didSet { if oldValue != targetID { loadSettings() } } }
    @Published var settings = ConvSettings() { didSet { if !loading { saveSettings() } } }
    @Published private(set) var phase: Phase = .idle
    @Published private(set) var progress = 0.0
    /// True while the running conversion can't say how far along it is (one document → PDF, one font).
    @Published private(set) var indeterminate = false
    @Published private(set) var currentName = ""
    @Published private(set) var outputs: [URL] = []
    @Published private(set) var errors: [String] = []
    @Published private(set) var stopped = false
    @Published private(set) var trashed = 0
    /// Set when outputs couldn't go next to their originals (read-only folder) and were saved here instead.
    @Published private(set) var savedElsewhere: URL?
    @Published private(set) var doneMessage: String?
    /// Move each original to the Trash once its conversion succeeded (remembered).
    @Published var trashOriginals = UserDefaults.standard.bool(forKey: "trashOriginals") {
        didSet { UserDefaults.standard.set(trashOriginals, forKey: "trashOriginals") }
    }

    var onClose: () -> Void = {}
    private var loading = false
    private let runner = Runner()

    init(files: [URL]) {
        add(files)
        loadSettings()
    }

    /// What each file is, decided once when it's added (telling can mean opening the file).
    private var kindOf: [URL: MediaKind] = [:]
    var kinds: Set<MediaKind> { Set(files.compactMap { kindOf[$0] }) }
    var groups: [(OutGroup, [Target])] { Targets.groups(for: kinds) }
    var target: Target? { Targets.byID(targetID) }
    var primaryKind: MediaKind? { kinds.count == 1 ? kinds.first : nil }
    private var groupKey: String { kinds.map(\.rawValue).sorted().joined(separator: "+") }

    func add(_ urls: [URL]) {
        guard phase != .running else { return }
        if phase == .done { reset() }
        for u in urls where !files.contains(u) {
            if let kind = MediaKind.of(u) {
                kindOf[u] = kind
                files.append(u)
            } else {
                skipped += 1
            }
        }
        chooseTarget()
    }

    /// ffmpeg or cwebp may have been installed (or removed) while the window was in the background:
    /// redraw, so the ffmpeg warning and the WebP format follow.
    func toolsMayHaveChanged() {
        objectWillChange.send()
        if phase == .idle { chooseTarget() }
    }

    private func reset() {
        phase = .idle
        outputs = []
        errors = []
        progress = 0
        stopped = false
        trashed = 0
        savedElsewhere = nil
        doneMessage = nil
    }

    private func chooseTarget() {
        let available = groups.flatMap(\.1)
        guard !available.isEmpty else {
            targetID = ""
            return
        }
        if available.contains(where: { $0.id == targetID }) { return }
        let last = UserDefaults.standard.string(forKey: "lastTarget.\(groupKey)")
        let exts = Set(files.map { $0.pathExtension.lowercased() })
        targetID =
            available.first(where: { $0.id == last })?.id
            ?? Targets.preferred(for: kinds, sourceExts: exts, among: available)?.id
            ?? available[0].id
    }

    private func loadSettings() {
        guard let t = target else { return }
        UserDefaults.standard.set(t.id, forKey: "lastTarget.\(groupKey)")
        var s = ConvSettings()
        if let data = UserDefaults.standard.data(forKey: "settings.\(t.id)"),
            let saved = try? JSONDecoder().decode(ConvSettings.self, from: data)
        {
            s = saved
        }
        if !t.bitrateOptions.isEmpty, !t.bitrateOptions.contains(s.audioBitrate) { s.audioBitrate = t.defaultBitrate }
        if !t.sampleRates.contains(s.sampleRate) { s.sampleRate = 0 }
        loading = true
        settings = s
        loading = false
    }

    private func saveSettings() {
        guard let t = target, let data = try? JSONEncoder().encode(settings) else { return }
        UserDefaults.standard.set(data, forKey: "settings.\(t.id)")
    }

    func start() {
        guard let t = target, phase == .idle, !files.isEmpty else { return }
        reset()
        runner.reset()
        phase = .running
        // Document → PDF happens in a helper process that reports no progress, and neither does
        // compressing a font; with several files the bar still moves once per file.
        indeterminate = (t.id == "pdf-doc" || t.group == .font) && files.count == 1
        let list = files
        let s = settings
        let runner = runner
        let trash = trashOriginals
        Task {
            let n = Double(list.count)
            for (i, file) in list.enumerated() {
                if stopped { break }
                currentName = file.lastPathComponent
                progress = Double(i) / n
                let report: @Sendable (Double) -> Void = { [weak self] p in
                    Task { @MainActor in
                        guard let self, self.phase == .running else { return }
                        self.progress = (Double(i) + p) / n
                    }
                }
                do {
                    let made = try await Converter.convert(file, to: t, settings: s, runner: runner, progress: report)
                    outputs += made
                    if let moved = made.first(where: { OutputFolder.isFallback($0, for: file) }) {
                        savedElsewhere = moved.deletingLastPathComponent()
                    }
                    if trash, Self.mayTrash(file, outputs: made, stopped: stopped) {
                        do {
                            try FileManager.default.trashItem(at: file, resultingItemURL: nil)
                            trashed += 1
                        } catch {
                            errors.append("\(file.lastPathComponent): converted, but couldn't move it to the Trash")
                        }
                    }
                } catch {
                    if !stopped { errors.append("\(file.lastPathComponent): \(error.localizedDescription)") }
                }
            }
            progress = 1
            indeterminate = false
            if !outputs.isEmpty, errors.isEmpty, !stopped {
                doneMessage = EasterEggs.doneMessage(count: outputs.count)
            } else if !outputs.isEmpty {
                doneMessage = EasterEggs.standardDoneMessage(count: outputs.count)
            }
            phase = .done
        }
    }

    /// Whether an original may go to the Trash (which keeps it recoverable): only once its finished,
    /// separate output sits next to it. Never after Stop, and never when the output had to be saved
    /// somewhere else, so the original and its conversion don't end up in different places.
    nonisolated static func mayTrash(_ original: URL, outputs: [URL], stopped: Bool) -> Bool {
        !stopped && !outputs.isEmpty && !outputs.contains(original)
            && outputs.allSatisfy { FileManager.default.fileExists(atPath: $0.path) }
            && !outputs.contains { OutputFolder.isFallback($0, for: original) }
    }

    func cancel() {
        guard phase == .running else { return }
        stopped = true
        runner.cancel()
    }
}
