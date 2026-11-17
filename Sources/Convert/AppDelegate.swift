import AppKit
import SwiftUI

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    private var windows: [NSWindow: ConvertModel] = [:]

    func applicationDidFinishLaunching(_ notification: Notification) {
        buildMenu()
        // Launched by double-click (no files)? Show an empty drop panel.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.8) { [weak self] in
            guard let self, self.windows.isEmpty else { return }
            self.openPanel([])
        }
    }

    func application(_ application: NSApplication, open urls: [URL]) {
        openPanel(urls)
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if !flag { openPanel([]) }
        return true
    }

    func openPanel(_ urls: [URL]) {
        // Reuse an empty drop panel if one is open.
        if !urls.isEmpty, let (win, model) = windows.first(where: { $0.value.files.isEmpty && $0.value.phase == .idle }) {
            model.add(urls)
            win.makeKeyAndOrderFront(nil)
            NSApp.activate()
            return
        }
        let model = ConvertModel(files: urls)
        let host = NSHostingController(rootView: ContentView(model: model))
        host.sizingOptions = [.preferredContentSize]
        let win = NSWindow(contentViewController: host)
        win.styleMask = [.titled, .closable, .miniaturizable]
        win.title = "Convert"
        win.isReleasedWhenClosed = false
        win.delegate = self
        model.onClose = { [weak win] in win?.close() }
        windows[win] = model
        if let last = windows.keys.first(where: { $0 !== win && $0.isVisible }) {
            win.setFrameTopLeftPoint(win.cascadeTopLeft(from: NSPoint(x: last.frame.minX, y: last.frame.maxY)))
        } else {
            win.center()
        }
        win.makeKeyAndOrderFront(nil)
        NSApp.activate()
    }

    func windowWillClose(_ notification: Notification) {
        guard let win = notification.object as? NSWindow else { return }
        windows[win]?.cancel()
        windows[win] = nil
    }

    @objc func openDocument(_ sender: Any?) {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = true
        if panel.runModal() == .OK { openPanel(panel.urls) }
    }

    private func buildMenu() {
        let main = NSMenu()

        let appItem = NSMenuItem()
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "About Convert", action: #selector(NSApplication.orderFrontStandardAboutPanel(_:)), keyEquivalent: "")
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "Hide Convert", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        appMenu.addItem(withTitle: "Quit Convert", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu
        main.addItem(appItem)

        let fileItem = NSMenuItem()
        let fileMenu = NSMenu(title: "File")
        fileMenu.addItem(withTitle: "Open…", action: #selector(openDocument(_:)), keyEquivalent: "o")
        fileMenu.addItem(withTitle: "Close", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        fileItem.submenu = fileMenu
        main.addItem(fileItem)

        let editItem = NSMenuItem()
        let editMenu = NSMenu(title: "Edit")
        editMenu.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        editMenu.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = editMenu
        main.addItem(editItem)

        NSApp.mainMenu = main
    }
}
