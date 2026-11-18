import Foundation
import Testing

@testable import Convert

/// The Finder Quick Action lists the file types it's offered for. One installed by an older
/// version has to pick up the types added since, or Finder never shows "Convert…" for them.
final class QuickActionTests {
    private let workflow: URL

    init() {
        workflow = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString)
            .appendingPathComponent("Convert.workflow", isDirectory: true)
    }

    deinit {
        try? FileManager.default.removeItem(at: workflow.deletingLastPathComponent())
    }

    @Test func installedActionListsEveryAcceptedType() throws {
        try QuickAction.install(at: workflow)
        #expect(QuickAction.installedTypes(at: workflow) == FileTypes.identifiers)
    }

    @Test func outdatedActionIsRewritten() throws {
        try QuickAction.install(at: workflow)
        // What 0.3 installed: everything but fonts.
        let info = workflow.appendingPathComponent("Contents/Info.plist")
        let older = FileTypes.accepted.filter { $0.kind != .font }.flatMap(\.identifiers)
        let plist: [String: Any] = ["NSServices": [["NSSendFileTypes": older]]]
        try PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0).write(to: info)
        #expect(QuickAction.installedTypes(at: workflow) == older)

        QuickAction.updateIfOutdated(at: workflow)
        #expect(QuickAction.installedTypes(at: workflow) == FileTypes.identifiers)
    }

    @Test func currentActionIsLeftAlone() throws {
        try QuickAction.install(at: workflow)
        let document = workflow.appendingPathComponent("Contents/document.wflow")
        let before = try Data(contentsOf: document)  // holds fresh UUIDs on every install
        QuickAction.updateIfOutdated(at: workflow)
        #expect(try Data(contentsOf: document) == before)
    }

    @Test func removedActionStaysRemoved() {
        QuickAction.updateIfOutdated(at: workflow)
        #expect(!FileManager.default.fileExists(atPath: workflow.path))
    }
}
