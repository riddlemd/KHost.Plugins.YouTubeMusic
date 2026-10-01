// KHost's YouTube Music helper: music.youtube.com in a WKWebView, in an app of its own with one Dock
// icon, driven by the KHost plugin over JSON lines. Protocol (one JSON object per line):
//   plugin -> helper  {"id":1,"cmd":"state"}  {"id":2,"cmd":"level","value":0.5}
//                     {"id":3,"cmd":"load","url":"https://music.youtube.com/...","value":0}  (value: level to hold at, as --initial-level)
//   helper -> plugin  {"event":"hello","protocol":1,"version":"...","owner":"plugin"|"user","pid":123}
//                     {"id":1,"ok":true,"result":{...}}  {"id":2,"ok":false,"error":"..."}
//                     {"event":"busy"}  (another instance already serves; this one exits 3)
// Commands: hello, state, play, pause, next, level, keepAlive, load, show, hide, quit.
//
// --khost-stdio   the plugin launched this process and speaks on stdin/stdout; stdin closing means
//                 the host has gone, and the helper quits with it.
// --background    start without taking focus or covering anything (the window is ordered back).
// --url <url>     open at this music.youtube.com address instead of the home page.
// --initial-level <0..1>  hold every media element at this level until the first "level" command,
//                 so a launch that autoplays starts where the plugin's fade-in starts, not at full.
//
// Whoever launched it, the helper also listens on a Unix socket in ~/Library/Caches/<bundle id>/
// (owner-only), so a host can drive a copy the user opened from the Dock, and a second copy can
// tell that one is already up.
//
// The app icon is never shipped: it is fetched at runtime from music.youtube.com's own web app
// manifest (apple-touch-icon, then favicon, as fallbacks), the way a browser's "install as app"
// does it, cached under ~/Library/Caches/<bundle id>/app-icon.png, and written onto the bundle on
// disk with NSWorkspace.setIcon so Finder and the Dock-when-not-running show it too. See AppIcon.

import Cocoa
import WebKit

let kProtocol = 1
let kHome = URL(string: "https://music.youtube.com/")!
let kFallbackSafariVersion = "26.0"

let arguments = CommandLine.arguments
let launchedByPlugin = arguments.contains("--khost-stdio")
let startInBackground = arguments.contains("--background")
let startURL: URL? = {
    guard let index = arguments.firstIndex(of: "--url"), index + 1 < arguments.count else { return nil }
    return musicURL(arguments[index + 1])
}()

let initialLevel: Double? = {
    guard let index = arguments.firstIndex(of: "--initial-level"), index + 1 < arguments.count,
          let level = Double(arguments[index + 1]), level.isFinite else { return nil }
    return min(1, max(0, level))
}()

let bundleId = Bundle.main.bundleIdentifier ?? "com.khost.youtube-music-helper"
let helperVersion = (Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String) ?? "0"
let appName = (Bundle.main.object(forInfoDictionaryKey: "CFBundleDisplayName") as? String) ?? "YouTube Music"

/// write(2), not FileHandle: the host's end of stderr closes with the host, and FileHandle raises an
/// Objective-C exception on EPIPE, which turned every quit-with-the-host into a crash.
func log(_ text: String) {
    _ = writeAll(STDERR_FILENO, Data("[ytm-helper] \(text)\n".utf8))
}

func musicURL(_ text: String) -> URL? {
    guard let url = URL(string: text), url.scheme == "https", url.host == "music.youtube.com" else { return nil }
    return url
}

/// Google refuses sign-in to an embedded browser it does not recognise; Safari's own string, with
/// this Mac's Safari version, is what lets it through.
func safariUserAgent() -> String {
    let candidates = ["/Applications/Safari.app", "/System/Volumes/Preboot/Cryptexes/App/System/Applications/Safari.app"]
    var version = kFallbackSafariVersion

    for path in candidates {
        if let info = NSDictionary(contentsOfFile: path + "/Contents/Info.plist"),
           let found = info["CFBundleShortVersionString"] as? String, !found.isEmpty {
            version = found
            break
        }
    }

    return "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/\(version) Safari/605.1.15"
}

/// A sockaddr_un holds 104 bytes; a home folder long enough to overflow it gets no socket, and the
/// plugin works the same path out and skips it too.
func socketPath() -> String? {
    let path = NSHomeDirectory() + "/Library/Caches/" + bundleId + "/khost.sock"
    return path.utf8.count < 104 ? path : nil
}

/// Holds every media element on the page at `level` until the page script's first "level" command
/// lets go (it sets __khostLevelHeld to false), so a list that autoplays starts where the plugin's
/// fade-in starts rather than at the player's own volume. Null removes the hold. Takes effect from
/// the next page load.
func holdLevel(_ level: Double?, in controller: WKUserContentController) {
    controller.removeAllUserScripts()
    guard let level else { return }

    // Capturing listeners and the prototype run before the player hears anything.
    let hold = """
    (function (level) {
      window.__khostLevelHeld = true;
      var proto = HTMLMediaElement.prototype;
      var volume = Object.getOwnPropertyDescriptor(proto, 'volume');
      var play = proto.play;
      function held() { return window.__khostLevelHeld !== false; }
      // Every level the page sets lands as the held one, and a new element (which starts at
      // full) is brought down before it can sound.
      Object.defineProperty(proto, 'volume', {
        configurable: true, enumerable: volume.enumerable, get: volume.get,
        set: function (value) { volume.set.call(this, held() ? level : value); }
      });
      proto.play = function () {
        if (held()) volume.set.call(this, level);
        return play.apply(this, arguments);
      };
      ['loadedmetadata', 'play', 'playing'].forEach(function (name) {
        document.addEventListener(name, function (event) {
          if (held() && event.target instanceof HTMLMediaElement) volume.set.call(event.target, level);
        }, true);
      });
    })(\(level));
    """
    controller.addUserScript(WKUserScript(source: hold, injectionTime: .atDocumentStart, forMainFrameOnly: true, in: .page))
}

// MARK: - App icon

/// The Dock icon this helper shows while it runs, and the one Finder shows for its bundle on disk:
/// fetched at runtime from music.youtube.com's own web app manifest, the way a browser's "install
/// as app" does it, so no Google artwork is ever committed to this repo or shipped in the plugin's
/// zip. Falls back to apple-touch-icon, then favicon, then the generic app icon when none of that
/// is reachable and nothing was cached from an earlier run.
final class AppIcon {
    private let bundleId: String
    private let session = URLSession(configuration: .ephemeral)

    init(bundleId: String) { self.bundleId = bundleId }

    private var cachePath: String { NSHomeDirectory() + "/Library/Caches/" + bundleId + "/app-icon.png" }

    /// Shows the cached icon immediately — a cold cache leaves the generic one until the fetch
    /// below lands, or forever when this Mac is offline — then goes to fetch the live one.
    func apply() {
        if let data = FileManager.default.contents(atPath: cachePath), let image = NSImage(data: data) {
            NSApp.applicationIconImage = image
        }

        DispatchQueue.global(qos: .utility).async { [weak self] in self?.fetchAndApply() }
    }

    private func fetchAndApply() {
        guard let data = downloadIcon(), let image = NSImage(data: data) else { return }

        let path = cachePath
        try? FileManager.default.createDirectory(atPath: (path as NSString).deletingLastPathComponent,
                                                  withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        try? data.write(to: URL(fileURLWithPath: path), options: .atomic)

        DispatchQueue.main.async {
            NSApp.applicationIconImage = image
            // Finder and the Dock-when-not-running read this off the bundle on disk, not the
            // running process; HelperInstaller.TreeHash skips the "Icon\r" file this call writes,
            // or every start would see a changed tree against the icon-less shipped build and
            // reinstall right over it.
            if !NSWorkspace.shared.setIcon(image, forFile: Bundle.main.bundlePath, options: []) {
                log("setIcon on \(Bundle.main.bundlePath) failed")
            }
        }
    }

    private func downloadIcon() -> Data? {
        iconFromManifest()
            ?? iconFromLinkTag(rel: "apple-touch-icon")
            ?? iconFromLinkTag(rel: "icon")
            ?? URL(string: "https://music.youtube.com/favicon.ico").flatMap(get)
    }

    /// The manifest the page itself links as `rel="manifest"`; icons carry their square size in
    /// `sizes`, so the largest non-maskable PNG is the one a Dock icon wants.
    private func iconFromManifest() -> Data? {
        guard let url = URL(string: "https://music.youtube.com/manifest.webmanifest"), let data = get(url),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let icons = json["icons"] as? [[String: Any]], !icons.isEmpty else { return nil }

        let pngs = icons.filter { ($0["type"] as? String ?? "image/png") == "image/png" }
        let ranked = (pngs.isEmpty ? icons : pngs).sorted { squareSize($0) > squareSize($1) }
        let best = ranked.first { ($0["purpose"] as? String ?? "any") != "maskable" } ?? ranked.first

        guard let src = best?["src"] as? String, let iconUrl = URL(string: src) else { return nil }
        return get(iconUrl)
    }

    private func squareSize(_ icon: [String: Any]) -> Int {
        guard let sizes = icon["sizes"] as? String, let width = sizes.split(separator: "x").first else { return 0 }
        return Int(width) ?? 0
    }

    /// A page the manifest fetch could not read still names an icon in its own `<head>`.
    private func iconFromLinkTag(rel: String) -> Data? {
        guard let home = URL(string: "https://music.youtube.com/"), let data = get(home),
              let html = String(data: data, encoding: .utf8) else { return nil }

        let pattern = "<link[^>]*rel=\"\(rel)\"[^>]*href=\"([^\"]+)\"[^>]*>"
        guard let regex = try? NSRegularExpression(pattern: pattern),
              let match = regex.firstMatch(in: html, range: NSRange(html.startIndex..., in: html)),
              let range = Range(match.range(at: 1), in: html),
              let iconUrl = URL(string: String(html[range]), relativeTo: home) else { return nil }

        return get(iconUrl.absoluteURL)
    }

    /// Synchronous: every call here already runs on the background queue `apply()` started, one
    /// request at a time, with a short timeout so an offline room gives up quickly.
    private func get(_ url: URL) -> Data? {
        var request = URLRequest(url: url, timeoutInterval: 8)
        request.setValue(safariUserAgent(), forHTTPHeaderField: "User-Agent")

        var result: Data?
        let group = DispatchGroup()
        group.enter()
        session.dataTask(with: request) { data, response, _ in
            if let http = response as? HTTPURLResponse, http.statusCode == 200,
               let data, !data.isEmpty, data.count < 4_000_000 {
                result = data
            }
            group.leave()
        }.resume()
        _ = group.wait(timeout: .now() + 10)
        return result
    }
}

// MARK: - Line transport

protocol Sink: AnyObject {
    func send(_ object: [String: Any])
}

func encodeLine(_ object: [String: Any]) -> Data? {
    guard JSONSerialization.isValidJSONObject(object),
          var data = try? JSONSerialization.data(withJSONObject: object, options: []) else { return nil }
    data.append(0x0A)
    return data
}

/// Writes the whole buffer or gives up; a reader that has gone away must not take the app with it.
func writeAll(_ fd: Int32, _ data: Data) -> Bool {
    data.withUnsafeBytes { raw -> Bool in
        guard var pointer = raw.baseAddress else { return true }
        var left = raw.count
        while left > 0 {
            let written = write(fd, pointer, left)
            if written < 0 {
                if errno == EINTR { continue }
                return false
            }
            left -= written
            pointer = pointer.advanced(by: written)
        }
        return true
    }
}

final class StdoutSink: Sink {
    func send(_ object: [String: Any]) {
        if let line = encodeLine(object) { _ = writeAll(STDOUT_FILENO, line) }
    }
}

final class SocketClient: Sink {
    let fd: Int32
    var buffer = Data()
    var source: DispatchSourceRead?

    init(fd: Int32) { self.fd = fd }

    func send(_ object: [String: Any]) {
        if let line = encodeLine(object) { _ = writeAll(fd, line) }
    }
}

final class SocketServer {
    private let path: String
    private var listenFd: Int32 = -1
    private var listenSource: DispatchSourceRead?
    private var clients: [Int32: SocketClient] = [:]
    private let onLine: (String, Sink) -> Void
    private let onConnect: (Sink) -> Void

    init(path: String, onConnect: @escaping (Sink) -> Void, onLine: @escaping (String, Sink) -> Void) {
        self.path = path
        self.onConnect = onConnect
        self.onLine = onLine
    }

    static func address(_ path: String) -> sockaddr_un? {
        var address = sockaddr_un()
        address.sun_family = sa_family_t(AF_UNIX)
        let bytes = Array(path.utf8)
        guard bytes.count < MemoryLayout.size(ofValue: address.sun_path) else { return nil }
        withUnsafeMutableBytes(of: &address.sun_path) { raw in
            raw.copyBytes(from: bytes)
            raw[bytes.count] = 0
        }
        address.sun_len = UInt8(MemoryLayout<sockaddr_un>.size)
        return address
    }

    /// True when something already answers on the path: another copy of the helper.
    static func isServed(_ path: String) -> Bool {
        guard var address = address(path) else { return false }
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { return false }
        defer { close(fd) }
        let result = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) }
        }
        return result == 0
    }

    /// Sends one line and leaves without waiting for the answer.
    static func request(_ path: String, _ object: [String: Any]) {
        guard var address = address(path), let line = encodeLine(object) else { return }
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { return }
        defer { close(fd) }
        let result = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) }
        }
        if result == 0 { _ = writeAll(fd, line) }
    }

    func start() -> Bool {
        let directory = (path as NSString).deletingLastPathComponent
        try? FileManager.default.createDirectory(atPath: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])

        // A crash leaves the file behind; nothing answers on it, or isServed would have said so.
        unlink(path)

        guard var address = SocketServer.address(path) else { return false }
        listenFd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard listenFd >= 0 else { return false }

        let old = umask(0o177)
        let bound = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(listenFd, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) }
        }
        umask(old)

        guard bound == 0, listen(listenFd, 4) == 0 else {
            log("socket: could not listen on \(path) (errno \(errno))")
            close(listenFd)
            listenFd = -1
            return false
        }

        chmod(path, 0o600)

        let source = DispatchSource.makeReadSource(fileDescriptor: listenFd, queue: .main)
        source.setEventHandler { [weak self] in self?.accept() }
        source.resume()
        listenSource = source
        log("socket: listening on \(path)")
        return true
    }

    func stop() {
        guard listenFd >= 0 else { return }
        listenSource?.cancel()
        close(listenFd)
        listenFd = -1
        unlink(path)
    }

    private func accept() {
        let fd = Darwin.accept(listenFd, nil, nil)
        guard fd >= 0 else { return }

        // The file is owner-only already; the peer check holds even if its mode is ever loosened.
        var uid: uid_t = 0
        var gid: gid_t = 0
        guard getpeereid(fd, &uid, &gid) == 0, uid == getuid() else {
            close(fd)
            return
        }

        var on: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &on, socklen_t(MemoryLayout<Int32>.size))

        let client = SocketClient(fd: fd)
        let source = DispatchSource.makeReadSource(fileDescriptor: fd, queue: .main)
        source.setEventHandler { [weak self, weak client] in
            guard let self, let client else { return }
            var chunk = [UInt8](repeating: 0, count: 16384)
            let count = read(fd, &chunk, chunk.count)
            if count <= 0 {
                self.drop(client)
                return
            }
            client.buffer.append(contentsOf: chunk[0..<count])
            if client.buffer.count > 1_048_576 {
                self.drop(client)
                return
            }
            while let newline = client.buffer.firstIndex(of: 0x0A) {
                let line = String(decoding: client.buffer[client.buffer.startIndex..<newline], as: UTF8.self)
                client.buffer.removeSubrange(client.buffer.startIndex...newline)
                self.onLine(line, client)
            }
        }
        client.source = source
        clients[fd] = client
        source.resume()
        onConnect(client)
    }

    private func drop(_ client: SocketClient) {
        client.source?.cancel()
        close(client.fd)
        clients.removeValue(forKey: client.fd)
    }
}

// MARK: - App

final class Helper: NSObject, NSApplicationDelegate, WKNavigationDelegate, WKUIDelegate, NSWindowDelegate {
    var window: NSWindow!
    var web: WKWebView!
    var popups: [NSWindow: WKWebView] = [:]
    var server: SocketServer?
    var activity: NSObjectProtocol?
    var appIcon: AppIcon?
    let userAgent = safariUserAgent()
    let stdout = StdoutSink()
    lazy var pageSource: String = {
        guard let url = Bundle.main.url(forResource: "youtube-music-page", withExtension: "js"),
              let text = try? String(contentsOf: url, encoding: .utf8) else {
            log("page script missing from the bundle")
            return "(function () { return JSON.stringify({ page: true, ok: false, error: 'page script missing' }); })"
        }
        return text
    }()

    func applicationDidFinishLaunching(_ notification: Notification) {
        appIcon = AppIcon(bundleId: bundleId)
        appIcon?.apply()

        buildMenu()

        let configuration = WKWebViewConfiguration()
        // Persistent, and per bundle id: the Google sign-in lives here and nowhere else.
        configuration.websiteDataStore = .default()
        configuration.mediaTypesRequiringUserActionForPlayback = []
        configuration.preferences.javaScriptCanOpenWindowsAutomatically = true

        holdLevel(initialLevel, in: configuration.userContentController)

        web = WKWebView(frame: NSRect(x: 0, y: 0, width: 1100, height: 760), configuration: configuration)
        web.customUserAgent = userAgent
        web.navigationDelegate = self
        web.uiDelegate = self
        web.allowsBackForwardNavigationGestures = true
        if #available(macOS 13.3, *) { web.isInspectable = true }

        window = NSWindow(contentRect: web.frame, styleMask: [.titled, .closable, .miniaturizable, .resizable],
                          backing: .buffered, defer: false)
        window.title = appName
        window.contentView = web
        window.isReleasedWhenClosed = false
        window.delegate = self
        window.setFrameAutosaveName("YouTubeMusicPlayer")
        if !window.setFrameUsingName("YouTubeMusicPlayer") { window.center() }

        if startInBackground {
            // Up and playing behind the karaoke screen rather than over it.
            window.orderBack(nil)
        } else {
            show()
        }

        // App Nap would throttle the page's timers and this process's own once the window is hidden.
        activity = ProcessInfo.processInfo.beginActivity(
            options: [.userInitiatedAllowingIdleSystemSleep], reason: "Playing break music for KHost")

        web.load(URLRequest(url: startURL ?? kHome))
        log("started v\(helperVersion) pid \(getpid()) owner \(launchedByPlugin ? "plugin" : "user") ua \(userAgent)")

        if let path = socketPath() {
            let server = SocketServer(path: path,
                                      onConnect: { [weak self] sink in self?.sendHello(sink) },
                                      onLine: { [weak self] line, sink in self?.handle(line, sink) })
            if server.start() { self.server = server }
        }

        if launchedByPlugin {
            sendHello(stdout)
            readStandardInput()
        }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        show()
        return true
    }

    func applicationWillTerminate(_ notification: Notification) {
        server?.stop()
        log("quit")
    }

    /// The close button hides the player; it keeps playing, and the Dock icon brings it back.
    func windowShouldClose(_ sender: NSWindow) -> Bool {
        if sender === window {
            window.orderOut(nil)
            return false
        }
        return true
    }

    func windowWillClose(_ notification: Notification) {
        if let closing = notification.object as? NSWindow, closing !== window {
            popups.removeValue(forKey: closing)
        }
    }

    func show() {
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    // MARK: menu

    func buildMenu() {
        let main = NSMenu()

        let appItem = NSMenuItem()
        main.addItem(appItem)
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "About \(appName)", action: #selector(NSApplication.orderFrontStandardAboutPanel(_:)), keyEquivalent: "")
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "Hide \(appName)", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        let others = appMenu.addItem(withTitle: "Hide Others", action: #selector(NSApplication.hideOtherApplications(_:)), keyEquivalent: "h")
        others.keyEquivalentModifierMask = [.command, .option]
        appMenu.addItem(withTitle: "Show All", action: #selector(NSApplication.unhideAllApplications(_:)), keyEquivalent: "")
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "Quit \(appName)", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu

        // Without an Edit menu, typing a password works but pasting one does not.
        let editItem = NSMenuItem()
        main.addItem(editItem)
        let edit = NSMenu(title: "Edit")
        edit.addItem(withTitle: "Undo", action: Selector(("undo:")), keyEquivalent: "z")
        let redo = edit.addItem(withTitle: "Redo", action: Selector(("redo:")), keyEquivalent: "z")
        redo.keyEquivalentModifierMask = [.command, .shift]
        edit.addItem(.separator())
        edit.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = edit

        let viewItem = NSMenuItem()
        main.addItem(viewItem)
        let view = NSMenu(title: "View")
        view.addItem(withTitle: "Reload", action: #selector(WKWebView.reload(_:)), keyEquivalent: "r")
        view.addItem(withTitle: "Back", action: #selector(WKWebView.goBack(_:)), keyEquivalent: "[")
        view.addItem(withTitle: "Forward", action: #selector(WKWebView.goForward(_:)), keyEquivalent: "]")
        view.addItem(withTitle: "YouTube Music Home", action: #selector(goHome), keyEquivalent: "0")
        viewItem.submenu = view

        let windowItem = NSMenuItem()
        main.addItem(windowItem)
        let windowMenu = NSMenu(title: "Window")
        windowMenu.addItem(withTitle: "Minimize", action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m")
        windowMenu.addItem(withTitle: "Close", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        windowMenu.addItem(withTitle: "Show Player", action: #selector(showPlayer), keyEquivalent: "1")
        windowItem.submenu = windowMenu

        NSApp.mainMenu = main
        NSApp.windowsMenu = windowMenu
    }

    @objc func goHome() { web.load(URLRequest(url: kHome)) }
    @objc func showPlayer() { show() }

    // MARK: control

    func sendHello(_ sink: Sink) {
        sink.send([
            "event": "hello",
            "protocol": kProtocol,
            "version": helperVersion,
            "owner": launchedByPlugin ? "plugin" : "user",
            "pid": Int(getpid()),
        ])
    }

    func readStandardInput() {
        let sink = stdout
        let thread = Thread { [weak self] in
            while let line = readLine(strippingNewline: true) {
                DispatchQueue.main.async { self?.handle(line, sink) }
            }
            // The host has gone: its end of the pipe closes with it, however it ended.
            DispatchQueue.main.async {
                log("stdin closed; quitting with the host")
                NSApp.terminate(nil)
            }
        }
        thread.name = "khost-stdin"
        thread.start()
    }

    func handle(_ line: String, _ sink: Sink) {
        guard !line.trimmingCharacters(in: .whitespaces).isEmpty else { return }

        guard let data = line.data(using: .utf8),
              let message = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else {
            sink.send(["ok": false, "error": "not a JSON object"])
            return
        }

        let id = message["id"] ?? NSNull()
        let command = message["cmd"] as? String ?? ""

        func reply(_ ok: Bool, _ extra: [String: Any] = [:]) {
            var object = extra
            object["id"] = id
            object["ok"] = ok
            sink.send(object)
        }

        switch command {
        case "hello":
            sendHello(sink)
            reply(true)

        case "state", "play", "pause", "next", "level", "keepAlive":
            runPage(command, message["value"]) { [weak self] result, error in
                guard var result else {
                    reply(false, ["error": error ?? "no answer from the page"])
                    return
                }
                if command == "state", let self {
                    result["windowVisible"] = self.window.isVisible
                }
                reply(true, ["result": result])
            }

        case "load":
            guard let text = message["url"] as? String, let url = musicURL(text) else {
                reply(false, ["error": "url must be https://music.youtube.com/..."])
                return
            }
            // "value" is the level to hold the new page at until the plugin's first level command.
            let hold = (message["value"] as? NSNumber).map { min(1, max(0, $0.doubleValue)) }
            holdLevel(hold, in: web.configuration.userContentController)
            web.load(URLRequest(url: url))
            reply(true)

        case "show":
            // Setup asks for the home page so a sign-in has somewhere to start; music already on
            // music.youtube.com is left playing.
            if message["home"] as? Bool == true, web.url?.host != "music.youtube.com" { goHome() }
            show()
            reply(true)

        case "hide":
            window.orderOut(nil)
            reply(true)

        case "quit":
            reply(true)
            DispatchQueue.main.async { NSApp.terminate(nil) }

        default:
            reply(false, ["error": "unknown command \(command)"])
        }
    }

    func runPage(_ command: String, _ value: Any?, _ done: @escaping ([String: Any]?, String?) -> Void) {
        let body = "return (" + pageSource + ")(command, argument);"
        web.callAsyncJavaScript(body, arguments: ["command": command, "argument": value ?? NSNull()], in: nil, in: .page) { result in
            switch result {
            case .success(let value):
                guard let text = value as? String, let data = text.data(using: .utf8),
                      let object = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else {
                    done(nil, "the page answered with something that is not JSON")
                    return
                }
                done(object, nil)
            case .failure(let error):
                done(nil, error.localizedDescription)
            }
        }
    }

    // MARK: navigation and sign-in popups

    func webViewWebContentProcessDidTerminate(_ webView: WKWebView) {
        log("web content process ended; reloading")
        webView.reload()
    }

    func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction,
                 decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        // A sign-in that finishes in a popup lands back on music.youtube.com: take it in the player.
        if webView !== web, action.targetFrame?.isMainFrame ?? true, let url = action.request.url, url.host == "music.youtube.com" {
            web.load(URLRequest(url: url))
            webView.window?.close()
            decisionHandler(.cancel)
            return
        }
        decisionHandler(.allow)
    }

    func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration,
                 for action: WKNavigationAction, windowFeatures: WKWindowFeatures) -> WKWebView? {
        let frame = NSRect(x: 0, y: 0,
                           width: CGFloat(truncating: windowFeatures.width ?? 500),
                           height: CGFloat(truncating: windowFeatures.height ?? 650))
        let child = WKWebView(frame: frame, configuration: configuration)
        child.customUserAgent = userAgent
        child.navigationDelegate = self
        child.uiDelegate = self

        let popup = NSWindow(contentRect: frame, styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        popup.title = "Sign in"
        popup.contentView = child
        popup.isReleasedWhenClosed = false
        popup.delegate = self
        popups[popup] = child
        popup.center()
        popup.makeKeyAndOrderFront(nil)
        return child
    }

    func webViewDidClose(_ webView: WKWebView) {
        if let closing = webView.window, closing !== window { closing.close() }
    }

    func webView(_ webView: WKWebView, runJavaScriptAlertPanelWithMessage message: String,
                 initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping () -> Void) {
        let alert = NSAlert()
        alert.messageText = message
        alert.runModal()
        completionHandler()
    }

    func webView(_ webView: WKWebView, runJavaScriptConfirmPanelWithMessage message: String,
                 initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping (Bool) -> Void) {
        let alert = NSAlert()
        alert.messageText = message
        alert.addButton(withTitle: "OK")
        alert.addButton(withTitle: "Cancel")
        completionHandler(alert.runModal() == .alertFirstButtonReturn)
    }
}

// MARK: - Start

// A pipe whose reader has gone must fail the write, not kill the process.
signal(SIGPIPE, SIG_IGN)

if let path = socketPath(), SocketServer.isServed(path) {
    // One player per Mac: the plugin connects to the copy already up instead.
    if launchedByPlugin {
        StdoutSink().send(["event": "busy"])
        exit(3)
    }
    // Opened by hand while a copy runs: bring that one forward rather than doing nothing.
    SocketServer.request(path, ["id": 0, "cmd": "show"])
    exit(0)
}

let app = NSApplication.shared
app.setActivationPolicy(.regular)
let helper = Helper()
app.delegate = helper
app.run()
