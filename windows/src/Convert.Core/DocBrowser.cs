using System.Text;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Convert.Core;

/// <summary>
/// A hidden WebView2 (part of Windows 11, unlike Edge itself, which can be uninstalled) that loads a
/// document from disk and nothing else, to print it to PDF or to read it back as clean HTML.
/// WebView2 needs a window and a message loop, so each use runs on its own STA thread with a hidden window.
/// </summary>
/// <remarks>
/// Scripts are off, but a page still loads what it points at: pictures, stylesheets, fonts, frames,
/// a meta refresh, a <c>\\server\share</c> path (which also hands the user's login hash to that
/// server), a preconnect. Against that, each on its own for what it covers (see <see cref="DocGuards"/>):
/// every request goes through <see cref="Allowed"/>, which lets only files in the document's folder and
/// the temp folder through (this also stops <c>file://server/…</c> before Windows tries SMB); and the
/// browser itself gets a proxy that isn't there and no name lookups, for what no request filter sees
/// (preconnects, the runtime's own background requests).
/// </remarks>
internal static class DocBrowser
{
    /// <summary>
    /// Port 1 on loopback: nothing listens there, so every connection is refused at once. Loopback goes
    /// through it too (&lt;-loopback&gt;), so a document can't reach a service on this computer either.
    /// </summary>
    private const string DeadProxy = "--proxy-server=http://127.0.0.1:1 --proxy-bypass-list=<-loopback>";

    private const string NoNameLookup = "--host-resolver-rules=\"MAP * ~NOTFOUND\"";

    /// <summary>Otherwise the runtime fetches its experiment configuration (config.edge.skype.com) on start.</summary>
    private const string NoBackgroundRequests = "--disable-background-networking";

    /// <summary>
    /// The window is never shown, so Chromium treats the page as in the background and gives it the
    /// lowest priority. On a busy computer, loading a page then took minutes instead of a second.
    /// </summary>
    private const string FullSpeed = "--disable-renderer-backgrounding --disable-background-timer-throttling --disable-backgrounding-occluded-windows";

    /// <summary>The longest a document may take to load and print.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(3);

    public static Task PrintAsync(string page, string pdf, bool a4, IReadOnlyList<string> readable, Runner runner, DocOptions options) =>
        RunAsync(page, readable, runner, options, async (env, web) =>
        {
            var print = env.CreatePrintSettings();
            print.ShouldPrintHeaderAndFooter = false;
            print.ShouldPrintBackgrounds = true;
            // Inches. Exact, so the PDF says A4 or Letter; the window's size and the display's scaling don't matter.
            print.PageWidth = a4 ? 210 / 25.4 : 8.5;
            print.PageHeight = a4 ? 297 / 25.4 : 11;
            print.MarginTop = print.MarginBottom = print.MarginLeft = print.MarginRight = 20 / 25.4;
            await web.ExecuteScriptAsync(LinkScript);
            if (!await web.PrintToPdfAsync(pdf, print) || !File.Exists(pdf)) throw new ConvertException("Couldn't create the PDF");
        });

    /// <summary>
    /// Runs in the page before it's printed. A link to a file would become that file's full path in the
    /// PDF (C:\Users\name\…), useless anywhere else, so it goes. A link within the page (#section) is
    /// pointed at the page itself again, as the &lt;base&gt; that finds the pictures pointed it away, so it
    /// stays a link within the PDF.
    /// </summary>
    private const string LinkScript = """
        for (const a of document.querySelectorAll('a[href], area[href]')) {
          const href = a.getAttribute('href').trim();
          if (href.startsWith('#')) a.setAttribute('href', location.href.split('#')[0] + href);
          else if (a.protocol === 'file:') a.removeAttribute('href');
        }
        """;

    /// <summary>
    /// Reads <paramref name="page"/> back as HTML for LibreOffice: no scripts, frames, objects, stylesheet
    /// links, meta tags, CSS <c>url()</c>s or remote pictures; pictures that loaded (so: local ones)
    /// embedded as data. Links to the web stay.
    /// </summary>
    public static Task CleanAsync(string page, string feed, IReadOnlyList<string> readable, Runner runner, DocOptions options) =>
        RunAsync(page, readable, runner, options, async (_, web) =>
        {
            var nonce = Guid.NewGuid().ToString("N");
            // Page scripts are off; scripts run by the host still work.
            var json = await web.ExecuteScriptAsync(CleanScript.Replace("NONCE", nonce, StringComparison.Ordinal));
            var result = JsonSerializer.Deserialize<Cleaned>(json, JsonOptions) ?? throw new ConvertException("Couldn't read this document");
            var html = new StringBuilder(result.Html);
            for (var i = 0; i < result.Images.Length; i++)
                html.Replace($"src=\"convert-picture-{nonce}-{i}\"", Embedded(result.Images[i], readable) is { } data ? $"src=\"{data}\"" : "");
            File.WriteAllText(feed, html.ToString(), new UTF8Encoding(false));
        });

    private sealed record Cleaned(string Html, string[] Images);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>A loaded picture as a data URL, if it's one of the files the page may read.</summary>
    private static string? Embedded(string url, IReadOnlyList<string> readable)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return url;
        if (!Allowed(url, readable) || !Uri.TryCreate(url, UriKind.Absolute, out var u) || !u.IsFile) return null;
        var type = Path.GetExtension(u.LocalPath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            _ => null,
        };
        if (type == null) return null;
        try { return $"data:{type};base64,{System.Convert.ToBase64String(File.ReadAllBytes(u.LocalPath))}"; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Runs in the loaded page. Removes everything that would make LibreOffice load something, and marks
    /// each picture that loaded with a placeholder the host swaps for the picture's data.
    /// </summary>
    private const string CleanScript = """
        (() => {
          for (const e of [...document.querySelectorAll('script, noscript, link, meta, base, iframe, frame, frameset, object, embed, applet, video, audio, source, track, template, portal')]) e.remove();
          const fetches = /url\(|image\(|image-set\(|@import|\\/i;
          const cleanStyle = style => { for (const p of [...style]) if (fetches.test(style.getPropertyValue(p))) style.removeProperty(p); };
          const cleanRules = owner => {
            for (let i = owner.cssRules.length - 1; i >= 0; i--) {
              const r = owner.cssRules[i];
              if (r.style) cleanStyle(r.style);
              if (r.cssRules) cleanRules(r);
              if (r instanceof CSSImportRule || r instanceof CSSFontFaceRule || (!r.style && !r.cssRules && fetches.test(r.cssText))) owner.deleteRule(i);
            }
          };
          for (const s of [...document.querySelectorAll('style')]) {
            try { cleanRules(s.sheet); s.textContent = [...s.sheet.cssRules].map(r => r.cssText).join('\n'); } catch { s.remove(); continue; }
            if (fetches.test(s.textContent)) s.remove();
          }
          const urls = new Set(['src', 'srcset', 'href', 'poster', 'data', 'background', 'action', 'formaction', 'cite', 'longdesc', 'lowsrc', 'dynsrc', 'codebase', 'archive', 'classid', 'usemap', 'manifest', 'ping', 'icon', 'profile', 'imagesrcset']);
          const images = [];
          for (const el of document.querySelectorAll('*')) {
            for (const a of [...el.attributes]) {
              const n = a.name.toLowerCase();
              if (n.startsWith('on')) el.removeAttribute(a.name);
              else if (n === 'style') { cleanStyle(el.style); if (fetches.test(el.getAttribute('style') || '')) el.removeAttribute(a.name); }
              else if (n === 'href' && (el.localName === 'a' || el.localName === 'area')) {
                // Links to the web and within the page stay; one to a file would keep its full path.
                if (!a.value.trim().startsWith('#') && !/^(https?|mailto):$/.test(el.protocol)) el.removeAttribute(a.name);
              }
              else if (n === 'src' && el.localName === 'img' && el.complete && el.naturalWidth > 0) {
                images.push(el.currentSrc || el.src);
                el.setAttribute('src', 'convert-picture-NONCE-' + (images.length - 1));
              }
              else if (urls.has(n) || n.endsWith(':href')) el.removeAttribute(a.name);
            }
          }
          const charset = document.createElement('meta');
          charset.setAttribute('charset', 'utf-8');
          document.head.prepend(charset);
          return { html: '<!DOCTYPE html>\n' + document.documentElement.outerHTML, images };
        })()
        """;

    /// <summary>
    /// Whether the page may load <paramref name="url"/>: a file inside one of <paramref name="readable"/>,
    /// not reached through a symbolic link or junction (which could point at a share), or data that's
    /// already in the page.
    /// </summary>
    internal static bool Allowed(string url, IReadOnlyList<string> readable)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !u.IsFile) return false;
        string path;
        try { path = Path.GetFullPath(u.LocalPath); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        foreach (var folder in readable)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            // A drive's root keeps its separator ("C:\").
            var inside = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (!path.StartsWith(inside, StringComparison.OrdinalIgnoreCase)) continue;
            return !ThroughLink(path, root);
        }
        return false;
    }

    private static bool ThroughLink(string path, string root)
    {
        try
        {
            for (var p = path; p.Length > root.Length; p = Path.GetDirectoryName(p)!)
            {
                FileSystemInfo info = Directory.Exists(p) ? new DirectoryInfo(p) : new FileInfo(p);
                if (info.Exists && info.LinkTarget != null) return true;
            }
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }
    }

    private static string Arguments(DocGuards guards, string extra)
    {
        var args = new List<string> { NoBackgroundRequests, FullSpeed };
        if (guards.HasFlag(DocGuards.BrowserProxy)) args.Add(DeadProxy);
        if (guards.HasFlag(DocGuards.NoNameLookup)) args.Add(NoNameLookup);
        if (extra.Length > 0) args.Add(extra);
        return string.Join(' ', args);
    }

    /// <summary>
    /// One browser process serves every WebView2 with the same data folder, and refuses one that asks for
    /// other arguments than it was started with (an older Convert still open, a test with a guard off).
    /// So the folder goes with the arguments.
    /// </summary>
    private static string DataFolder(string arguments)
    {
        var hash = System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(arguments)))[..12];
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Convert", "WebView2", hash);
    }

    private static async Task RunAsync(string page, IReadOnlyList<string> readable, Runner runner, DocOptions options,
                                       Func<CoreWebView2Environment, CoreWebView2, Task> work)
    {
        runner.CheckCancelled();
        var session = new Session(page, readable, options.Guards, Arguments(options.Guards, options.BrowserArguments), work);
        var thread = new Thread(session.Run) { IsBackground = true, Name = "Convert WebView2" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        using var timeout = new CancellationTokenSource(Limit);
        using (runner.Token.Register(() => session.Abort(new StoppedException())))
        using (timeout.Token.Register(() => session.Abort(new ConvertException("This document took too long to load"))))
        {
            try
            {
                await session.Done;
            }
            finally
            {
                // Stopped: don't return before the page is closed, or it could still write into the temp folder.
                await Task.WhenAny(session.Closed, Task.Delay(TimeSpan.FromSeconds(5)));
            }
        }
    }

    /// <summary>One page in one hidden WebView2. Everything but <see cref="Abort"/> runs on its own STA thread.</summary>
    private sealed class Session(string page, IReadOnlyList<string> readable, DocGuards guards, string arguments,
                                 Func<CoreWebView2Environment, CoreWebView2, Task> work)
    {
        private readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Dispatcher? dispatcher;
        private HwndSource? window;
        private CoreWebView2Controller? controller;
        private bool finished;

        // The one navigation that may happen next, and the one under way.
        private string? expected;
        private ulong? navigation;
        private TaskCompletionSource<bool>? loaded;
        private readonly HashSet<string> filtered = new(StringComparer.OrdinalIgnoreCase);

        public Task Done => done.Task;
        public Task Closed => closed.Task;

        public void Run()
        {
            lock (this) dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(StartAsync);
            Dispatcher.Run();
            closed.TrySetResult();
        }

        /// <summary>Ends the session from any thread: the caller hears of it at once, the page is closed right after.</summary>
        public void Abort(Exception reason)
        {
            done.TrySetException(reason);
            Dispatcher? d;
            lock (this) d = dispatcher;
            d?.BeginInvoke(() => Finish(reason));
        }

        private void Finish(Exception? error)
        {
            if (finished) return;
            finished = true;
            try { controller?.Close(); }
            catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
            window?.Dispose();
            if (error == null) done.TrySetResult();
            else done.TrySetException(error);
            dispatcher!.BeginInvokeShutdown(DispatcherPriority.Normal);
        }

        private async Task StartAsync()
        {
            try
            {
                // Stopped before this thread got going.
                if (done.Task.IsCompleted)
                {
                    Finish(null);
                    return;
                }
                window = new HwndSource(new HwndSourceParameters("Convert PDF") { Width = 900, Height = 1200, WindowStyle = 0 });
                CoreWebView2Environment env;
                try
                {
                    var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = arguments };
                    env = await CoreWebView2Environment.CreateAsync(null, DataFolder(arguments), options);
                }
                catch (WebView2RuntimeNotFoundException) { throw new ConvertException("Creating PDFs needs the Microsoft Edge WebView2 Runtime"); }
                if (finished) return;
                controller = await env.CreateCoreWebView2ControllerAsync(window.Handle);
                if (finished)
                {
                    controller.Close();
                    return;
                }
                var web = controller.CoreWebView2;
                Lock(env, web);

                // Only go on if the filter sees requests for files: on a runtime where it didn't, a
                // document's \\server\share pictures would load unchecked.
                var probe = Path.Combine(Path.GetDirectoryName(page)!, "probe.html");
                File.WriteAllText(probe, "<!doctype html><img src=\"probe.png\">");
                if (guards.HasFlag(DocGuards.RequestFilter))
                {
                    await LoadAsync(web, probe);
                    if (!filtered.Contains("probe.html") || !filtered.Contains("probe.png"))
                        throw new ConvertException("Creating PDFs needs a newer Microsoft Edge WebView2 Runtime. Update Windows, then try again");
                }
                if (finished) return;
                if (!await LoadAsync(web, page)) throw new ConvertException("Couldn't load the document");
                if (finished) return;
                await work(env, web);
                Finish(null);
            }
            catch (Exception e)
            {
                Finish(e is ConvertException or StoppedException ? e : new ConvertException($"Couldn't create the PDF ({e.Message})"));
            }
        }

        private Task<bool> LoadAsync(CoreWebView2 web, string file)
        {
            expected = file;
            navigation = null;
            loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            web.Navigate(new Uri(file).AbsoluteUri);
            return loaded.Task;
        }

        private void Lock(CoreWebView2Environment env, CoreWebView2 web)
        {
            web.Settings.IsScriptEnabled = false;
            // Otherwise SmartScreen is asked about every page that isn't a file (nav-edge.smartscreen.microsoft.com).
            try { web.Settings.IsReputationCheckingRequired = false; }
            catch (NotImplementedException) { }   // an older runtime; the dead proxy still stops it

            // The document itself, and nothing after it: a meta refresh or anything else that would go
            // to another page is cancelled.
            web.NavigationStarting += (_, e) =>
            {
                if (expected != null && navigation == null && IsFile(e.Uri, expected))
                {
                    navigation = e.NavigationId;
                    expected = null;
                }
                else e.Cancel = true;
            };
            web.NavigationCompleted += (_, e) =>
            {
                if (e.NavigationId == navigation) loaded?.TrySetResult(e.IsSuccess);
            };
            web.FrameNavigationStarting += (_, e) =>
            {
                if (guards.HasFlag(DocGuards.RequestFilter) && !Allowed(e.Uri, readable)) e.Cancel = true;
            };
            // A frame pointing at a zip would save it to Downloads; a mailto: or ms-settings: one would
            // ask to start another app.
            web.DownloadStarting += (_, e) =>
            {
                e.Cancel = true;
                e.Handled = true;
            };
            web.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
            web.NewWindowRequested += (_, e) => e.Handled = true;
            web.PermissionRequested += (_, e) =>
            {
                e.State = CoreWebView2PermissionState.Deny;
                e.Handled = true;
            };

            if (!guards.HasFlag(DocGuards.RequestFilter)) return;
            // All: also what workers ask for, not only the page and its frames.
            try { web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All); }
            catch (NotImplementedException) { web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All); }
            web.WebResourceRequested += (_, e) =>
            {
                var url = e.Request.Uri;
                if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.IsFile) filtered.Add(Path.GetFileName(u.LocalPath));
                if (!Allowed(url, readable)) e.Response = env.CreateWebResourceResponse(null, 403, "Blocked", "");
            };
        }

        private static bool IsFile(string url, string path) =>
            Uri.TryCreate(url, UriKind.Absolute, out var u) && u.IsFile
            && string.Equals(Path.GetFullPath(u.LocalPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
    }
}
