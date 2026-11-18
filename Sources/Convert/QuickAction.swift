import Foundation

/// The Finder "Convert…" Quick Action: a tiny Automator workflow in ~/Library/Services
/// that hands the selected files to this app. Installed from inside the app (or by build.sh).
enum QuickAction {
    static let bundleID = "local.convert.app"

    static var url: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Services/Convert.workflow", isDirectory: true)
    }

    static var isInstalled: Bool {
        FileManager.default.fileExists(atPath: url.appendingPathComponent("Contents/document.wflow").path)
    }

    /// The file types an installed Quick Action is offered for; nil when there's none to read.
    static func installedTypes(at url: URL = QuickAction.url) -> [String]? {
        guard let data = try? Data(contentsOf: url.appendingPathComponent("Contents/Info.plist")),
            let info = try? PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any],
            let services = info["NSServices"] as? [[String: Any]]
        else { return nil }
        return services.first?["NSSendFileTypes"] as? [String]
    }

    /// A Quick Action installed by an older version doesn't list the file types added since
    /// (fonts, in 0.4), so Finder wouldn't offer "Convert…" for them. It's written again when
    /// the app opens. One the user removed stays removed.
    static func updateIfOutdated(at url: URL = QuickAction.url) {
        guard let types = installedTypes(at: url), Set(types) != Set(FileTypes.identifiers) else { return }
        try? install(at: url)
    }

    static func install(at url: URL = QuickAction.url) throws {
        let contents = url.appendingPathComponent("Contents", isDirectory: true)
        try FileManager.default.createDirectory(at: contents, withIntermediateDirectories: true)

        let info: [String: Any] = [
            "CFBundleName": "Convert",
            "NSServices": [
                [
                    "NSMenuItem": ["default": "Convert…"],
                    "NSMessage": "runWorkflowAsService",
                    "NSRequiredContext": ["NSApplicationIdentifier": "com.apple.finder"],
                    "NSSendFileTypes": FileTypes.identifiers,
                    "NSIconName": "NSActionTemplate",
                ]
            ],
        ]
        let uuid = { UUID().uuidString }
        let action: [String: Any] = [
            "AMAccepts": ["Container": "List", "Optional": true, "Types": ["com.apple.cocoa.string"]],
            "AMActionVersion": "2.0.3",
            "AMApplication": ["Automator"],
            "AMParameterProperties": [
                "COMMAND_STRING": [:], "CheckedForUserDefaultShell": [:],
                "inputMethod": [:], "shell": [:], "source": [:],
            ],
            "AMProvides": ["Container": "List", "Types": ["com.apple.cocoa.string"]],
            "ActionBundlePath": "/System/Library/Automator/Run Shell Script.action",
            "ActionName": "Run Shell Script",
            "ActionParameters": [
                "COMMAND_STRING": "open -b \(bundleID) \"$@\"\n",
                "CheckedForUserDefaultShell": true,
                "inputMethod": 1,  // pass files as arguments
                "shell": "/bin/zsh",
                "source": "",
            ],
            "BundleIdentifier": "com.apple.RunShellScript",
            "CFBundleVersion": "2.0.3",
            "CanShowSelectedItemsWhenRun": false,
            "CanShowWhenRun": true,
            "Category": ["AMCategoryUtilities"],
            "Class Name": "RunShellScriptAction",
            "InputUUID": uuid(), "OutputUUID": uuid(), "UUID": uuid(),
            "Keywords": ["Shell", "Script"],
            "UnlocalizedApplications": ["Automator"],
            "isViewVisible": 1,
        ]
        let document: [String: Any] = [
            "AMApplicationBuild": "523",
            "AMApplicationVersion": "2.10",
            "AMDocumentVersion": "2",
            "actions": [["action": action, "isViewVisible": 1]],
            "connectors": [:],
            "workflowMetaData": [
                "applicationBundleIDsByPath": [:],
                "applicationPaths": [],
                "inputTypeIdentifier": "com.apple.Automator.fileSystemObject",
                "outputTypeIdentifier": "com.apple.Automator.nothing",
                "presentationMode": 15,
                "processesInput": false,
                "serviceInputTypeIdentifier": "com.apple.Automator.fileSystemObject",
                "serviceOutputTypeIdentifier": "com.apple.Automator.nothing",
                "serviceProcessesInput": false,
                "systemImageName": "NSActionTemplate",
                "useAutomaticInputType": false,
                "workflowTypeIdentifier": "com.apple.Automator.servicesMenu",
            ],
        ]
        try write(info, to: contents.appendingPathComponent("Info.plist"))
        try write(document, to: contents.appendingPathComponent("document.wflow"))
        if url == QuickAction.url { refreshServices() }
    }

    static func uninstall() throws {
        if FileManager.default.fileExists(atPath: url.path) {
            try FileManager.default.removeItem(at: url)
        }
        refreshServices()
    }

    private static func write(_ plist: [String: Any], to file: URL) throws {
        let data = try PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0)
        try data.write(to: file, options: .atomic)
    }

    private static func refreshServices() {
        _ = try? runSync("/System/Library/CoreServices/pbs", ["-update"])
    }
}
