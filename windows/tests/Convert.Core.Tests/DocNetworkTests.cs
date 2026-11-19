using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Convert.Core;
using Xunit;
using Xunit.Abstractions;

namespace Convert.Core.Tests;

/// <summary>
/// A stand-in for "the internet": a TCP listener on loopback that counts every connection made to it
/// and answers HTTP requests with something plausible, so a page that loads a stylesheet also gets to
/// load what that stylesheet points at. Counting connections rather than requests also catches a
/// preconnect that never sends one.
/// </summary>
public sealed class Sink : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private int connections;

    public const string NoRequest = "(connection, no request)";

    public ConcurrentQueue<string> Requests { get; } = new();

    public Sink()
    {
        listener.Start();
        _ = AcceptAsync();
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public string Url => $"http://127.0.0.1:{Port}";
    public int Connections => Volatile.Read(ref connections);

    public string Report() => $"{Connections} connection(s): {string.Join(" | ", Requests)}";

    private async Task AcceptAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token); }
            catch (Exception) { return; }
            Interlocked.Increment(ref connections);
            _ = Task.Run(() => Answer(client));
        }
    }

    private void Answer(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 2000;
                var stream = client.GetStream();
                var buffer = new byte[8192];
                var n = stream.Read(buffer);
                var line = Encoding.ASCII.GetString(buffer, 0, n).Split("\r\n")[0];
                Requests.Enqueue(line.Length > 0 ? line : NoRequest);
                var path = line.Split(' ').ElementAtOrDefault(1) ?? "/";
                var (type, body) = path switch
                {
                    _ when path.EndsWith(".css") => ("text/css", $"@import url('{Url}/nested.css'); body {{ background: url('{Url}/nested.png'); }}"),
                    _ when path.EndsWith(".png") => ("image/png", ""),
                    _ => ("text/html", $"<img src='{Url}/from-frame.png'>"),
                };
                var bytes = Encoding.UTF8.GetBytes(body);
                stream.Write(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"));
                stream.Write(bytes);
            }
            catch (Exception) { }
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
    }
}

/// <summary>
/// "Nothing leaves the computer", for documents: whatever a document points at, converting it makes no
/// network connection, through WebView2 (→ PDF) or LibreOffice (→ Word and the rest). Then each guard
/// on its own, for what it's there for, so one can't hide a hole in another.
/// </summary>
[Collection("Documents")]
public sealed class DocNetworkTests(ITestOutputHelper log) : IDisposable
{
    // Outside Convert's temp folder, which the page is allowed to read from.
    private readonly string dir = Directory.CreateTempSubdirectory("convert-doc-net-").FullName;

    public void Dispose() => Paths.TryDelete(dir);

    /// <summary>
    /// In TEST-NET-1, which is never routed, so an attempt to reach it hangs in SynSent where it can be
    /// seen. A new one for every test: Windows remembers for a while that a server didn't answer and
    /// then doesn't try again, which would hide an attempt.
    /// </summary>
    private readonly string unroutable = $"192.0.2.{Random.Shared.Next(1, 255)}";

    private string Remote(Sink s, bool preconnect = true, bool unc = true) => $$"""
        <link rel="stylesheet" href="{{s.Url}}/link.css">
        {{(preconnect ? $"""<link rel="preconnect" href="http://localhost:{s.Port}"><link rel="dns-prefetch" href="http://localhost:{s.Port}">""" : "")}}
        <link rel="prefetch" href="{{s.Url}}/prefetch">
        <link rel="preload" as="image" href="{{s.Url}}/preload.png">
        <link rel="icon" href="{{s.Url}}/favicon.png">
        <meta http-equiv="refresh" content="0; url={{s.Url}}/refresh">
        <style>
          @import url("{{s.Url}}/import.css");
          @font-face { font-family: Remote; src: url("{{s.Url}}/font.woff2"); }
          h1 { font-family: Remote; background: url("{{s.Url}}/bg.png"); }
        </style>
        <h1>Title</h1>
        <img src="{{s.Url}}/img.png" alt="img">
        <img srcset="{{s.Url}}/srcset.png 2x" alt="srcset">
        <picture><source srcset="{{s.Url}}/picture.png"><img alt="picture"></picture>
        <iframe src="{{s.Url}}/iframe"></iframe>
        <iframe src="mailto:someone@example.com"></iframe>
        <object data="{{s.Url}}/object"></object>
        <embed src="{{s.Url}}/embed">
        <video poster="{{s.Url}}/poster.png" src="{{s.Url}}/video.mp4"></video>
        <audio src="{{s.Url}}/audio.mp3" preload="auto"></audio>
        <svg width="10" height="10"><image href="{{s.Url}}/svg.png" width="10" height="10"/></svg>
        <input type="image" src="{{s.Url}}/input.png">
        <a href="{{s.Url}}/link" ping="{{s.Url}}/ping">a link</a>
        <div style="background-image: url('{{s.Url}}/inline.png'); width: 10px; height: 10px"></div>
        {{(unc ? $"""
            <img src="\\{unroutable}\share\unc.png" alt="unc">
            <img src="file://{unroutable}/share/file-unc.png" alt="file-unc">
            <link rel="stylesheet" href="file://{unroutable}/share/unc.css">
            <link rel="stylesheet" href="//{unroutable}/share/relative.css">
            """ : "")}}
        """;

    private string Write(string name, string text)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private string Html(Sink sink, bool preconnect = true, bool unc = true) =>
        Write("page.html", $"<!doctype html><html><head><meta charset=utf-8></head><body>{Remote(sink, preconnect, unc)}</body></html>");

    private string Markdown(Sink sink) => Write("notes.md", $"""
        # Notes

        ![remote]({sink.Url}/md-image.png)

        ![reference][r]

        ![unc](//{unroutable}/share/md-unc.png)

        [r]: {sink.Url}/md-reference.png

        <img src="{sink.Url}/md-raw.png">

        {Remote(sink)}
        """);

    private string Docx(Sink sink) => DocTestFiles.Docx(Path.Combine(dir, "linked.docx"), "Linked pictures",
        [$"{sink.Url}/docx-linked.png", $"file://{unroutable}/share/docx-unc.png"],
        template: $"{sink.Url}/template.dotx", fieldPicture: $"{sink.Url}/docx-field.png");

    private static Task<string> Convert(string input, string target, DocGuards guards = DocGuards.All) =>
        DocConv.ConvertAsync(input, Targets.ById(target)!, new ConvSettings(), new Runner(), new DocOptions { Guards = guards });

    /// <summary>Converts while watching for a connection attempt to <see cref="unroutable"/> (SMB, WebDAV or anything else).</summary>
    private async Task<(string? output, Exception? error, List<string> attempts)> ConvertWatchingUnc(Func<Task<string>> convert)
    {
        var attempts = new ConcurrentDictionary<string, bool>();
        using var watching = new CancellationTokenSource();
        var watcher = Task.Run(async () =>
        {
            do
            {
                foreach (var c in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections())
                {
                    if (c.RemoteEndPoint.Address.ToString() == unroutable) attempts[$"{c.RemoteEndPoint} {c.State}"] = true;
                }
                try { await Task.Delay(50, watching.Token); }
                catch (OperationCanceledException) { }
            } while (!watching.IsCancellationRequested);
        });
        string? output = null;
        Exception? error = null;
        try { output = await convert(); }
        catch (Exception e) { error = e; }
        // A connection started just before the end is still in SynSent for a few seconds, and requests
        // already under way when the page closed still arrive.
        await Task.Delay(500);
        watching.Cancel();
        await watcher;
        return (output, error, attempts.Keys.Order().ToList());
    }

    private async Task<string> AssertNoNetwork(Sink sink, string input, string target, DocGuards guards = DocGuards.All)
    {
        var (output, error, unc) = await ConvertWatchingUnc(() => Convert(input, target, guards));
        log.WriteLine($"{sink.Report()}; to {unroutable}: [{string.Join(", ", unc)}]; error: {error?.Message}");
        Assert.True(sink.Connections == 0, sink.Report());
        Assert.Empty(unc);
        if (error != null) throw error;
        Assert.True(File.Exists(output));
        return output!;
    }

    // Everything on, as the app runs

    [Fact]
    public async Task HtmlToPdfLoadsNothingRemote()
    {
        using var sink = new Sink();
        await AssertNoNetwork(sink, Html(sink), "pdf-doc");
    }

    [Fact]
    public async Task MarkdownToPdfLoadsNothingRemote()
    {
        using var sink = new Sink();
        await AssertNoNetwork(sink, Markdown(sink), "pdf-doc");
    }

    [LibreOfficeFact]
    public async Task HtmlToDocxLoadsNothingRemoteAndLinksNothing()
    {
        using var sink = new Sink();
        var docx = await AssertNoNetwork(sink, Html(sink), "docx");
        // Nor does the new document link to anything: Word would fetch that when it's opened.
        using var zip = ZipFile.OpenRead(docx);
        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".rels")))
        {
            using var reader = new StreamReader(entry.Open());
            foreach (var rel in System.Text.RegularExpressions.Regex.Matches(reader.ReadToEnd(), "<Relationship [^>]*>").Select(m => m.Value))
            {
                // A hyperlink is fine: it's only followed when clicked.
                if (rel.Contains("TargetMode=\"External\"")) Assert.EndsWith("/hyperlink\"", System.Text.RegularExpressions.Regex.Match(rel, "Type=\"[^\"]*\"").Value);
            }
        }
    }

    [LibreOfficeFact]
    public async Task MarkdownToDocxLoadsNothingRemote()
    {
        using var sink = new Sink();
        await AssertNoNetwork(sink, Markdown(sink), "docx");
    }

    [LibreOfficeFact]
    public async Task DocxWithLinkedPicturesToPdfLoadsNothingRemote()
    {
        using var sink = new Sink();
        await AssertNoNetwork(sink, Docx(sink), "pdf-doc");
    }

    [LibreOfficeFact]
    public async Task DocxWithLinkedPicturesToOdtLoadsNothingRemote()
    {
        using var sink = new Sink();
        await AssertNoNetwork(sink, Docx(sink), "odt");
    }

    // Each guard alone

    /// <summary>
    /// Without any guard, the same page does reach the sink and the share: the tests above would see a leak.
    /// </summary>
    [Fact]
    public async Task WithoutGuardsThePageReachesTheNetwork()
    {
        using var sink = new Sink();
        var (_, _, unc) = await ConvertWatchingUnc(() => Convert(Html(sink), "pdf-doc", DocGuards.None));
        log.WriteLine($"{sink.Report()}; to {unroutable}: [{string.Join(", ", unc)}]");
        Assert.True(sink.Connections > 0);
        Assert.NotEmpty(unc);
    }

    /// <summary>
    /// The request filter stops every request, file shares included. What it never sees are connections
    /// opened ahead of time: for a &lt;link rel="preconnect"&gt;, but also for a frame or a refresh that's
    /// about to be cancelled. Those carry no request and are left to the proxy and the name rules.
    /// </summary>
    [Fact]
    public async Task RequestFilterAloneStopsEveryRequest()
    {
        using var sink = new Sink();
        var (output, error, unc) = await ConvertWatchingUnc(() => Convert(Html(sink), "pdf-doc", DocGuards.RequestFilter));
        log.WriteLine($"{sink.Report()}; to {unroutable}: [{string.Join(", ", unc)}]; error: {error?.Message}");
        Assert.All(sink.Requests, r => Assert.Equal(Sink.NoRequest, r));
        Assert.Empty(unc);
        Assert.Null(error);
        Assert.True(File.Exists(output));
    }

    /// <summary>The proxy stops all web traffic, preconnects included. It can't do anything about file shares.</summary>
    [Fact]
    public async Task ProxyAloneStopsAllWebTraffic()
    {
        using var sink = new Sink();
        await AssertNoNetwork(sink, Html(sink, unc: false), "pdf-doc", DocGuards.BrowserProxy);
    }

    /// <summary>With no names resolving, nothing is reached either, not even an address written as numbers.</summary>
    [Fact]
    public async Task NameRulesAloneStopAllWebTraffic()
    {
        using var sink = new Sink();
        await AssertNoNetwork(sink, Html(sink, unc: false), "pdf-doc", DocGuards.NoNameLookup);
    }

    /// <summary>Without its guards, LibreOffice does fetch what an HTML file links to.</summary>
    [LibreOfficeFact]
    public async Task WithoutGuardsLibreOfficeReachesTheNetwork()
    {
        using var sink = new Sink();
        var (_, _, unc) = await ConvertWatchingUnc(() => Convert(Html(sink), "docx", DocGuards.Browser));
        log.WriteLine($"{sink.Report()}; to {unroutable}: [{string.Join(", ", unc)}]");
        Assert.True(sink.Connections > 0);
        Assert.NotEmpty(unc);
    }

    /// <summary>The cleaned HTML that LibreOffice gets points at nothing, file shares included.</summary>
    [LibreOfficeFact]
    public async Task CleanHtmlAloneKeepsLibreOfficeOffline()
    {
        using var sink = new Sink();
        await AssertNoNetwork(sink, Html(sink), "docx", DocGuards.Browser | DocGuards.CleanHtml);
    }

    /// <summary>LibreOffice's proxy stops its web traffic. File shares are what the cleaning is for.</summary>
    [LibreOfficeFact]
    public async Task OfficeProxyAloneStopsLibreOfficeWebTraffic()
    {
        using var sink = new Sink();
        await AssertNoNetwork(sink, Html(sink, unc: false), "docx", DocGuards.Browser | DocGuards.OfficeProxy);
    }

    // What else a page could do

    /// <summary>A frame pointing at a file WebView2 can't show would save it to Downloads.</summary>
    [Fact]
    public async Task AFrameCantDownloadAFile()
    {
        var name = $"convert-test-{Guid.NewGuid():N}";
        File.WriteAllBytes(Path.Combine(dir, name + ".zip"), [0x50, 0x4B, 0x05, 0x06, .. new byte[18]]);
        var page = Write("frame.html", $"<!doctype html><p>A frame<iframe src=\"{name}.zip\"></iframe>");
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        try
        {
            Assert.True(File.Exists(await Convert(page, "pdf-doc")));
            await Task.Delay(500);
            Assert.Empty(Directory.Exists(downloads) ? Directory.GetFiles(downloads, name + "*") : []);
        }
        finally
        {
            if (Directory.Exists(downloads)) foreach (var f in Directory.GetFiles(downloads, name + "*")) File.Delete(f);
        }
    }

    // Which files a page may load

    [Theory]
    [InlineData("pic.png", true)]
    [InlineData("sub/pic.png", true)]
    [InlineData("../pic.png", false)]
    [InlineData("sub/../../pic.png", false)]
    public void APageReadsOnlyFromItsFolders(string relative, bool allowed)
    {
        var url = new Uri(new Uri(dir + Path.DirectorySeparatorChar), relative).AbsoluteUri;
        Assert.Equal(allowed, DocBrowser.Allowed(url, [dir]));
    }

    [Theory]
    [InlineData("file://192.0.2.1/share/pic.png")]
    [InlineData("file:////192.0.2.1/share/pic.png")]
    [InlineData("http://127.0.0.1/pic.png")]
    [InlineData("https://example.com/pic.png")]
    [InlineData("ftp://example.com/pic.png")]
    [InlineData("blob:null/1234")]
    [InlineData("not a url")]
    public void NothingElse(string url) => Assert.False(DocBrowser.Allowed(url, [dir]));

    [Fact]
    public void DataInThePageIsFine() => Assert.True(DocBrowser.Allowed("data:image/png;base64,iVBORw0KGgo=", [dir]));

    [Fact]
    public void ADocumentAtADrivesRootSeesItsPictures() => Assert.True(DocBrowser.Allowed("file:///C:/pic.png", [@"C:\"]));

    /// <summary>A junction inside the folder could lead anywhere, a share included.</summary>
    [Fact]
    public void NotThroughAJunction()
    {
        var elsewhere = Directory.CreateTempSubdirectory("convert-doc-elsewhere-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(elsewhere, "pic.png"), DocTestFiles.Png);
            var junction = Path.Combine(dir, "link");
            using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", junction, elsewhere]) { CreateNoWindow = true, UseShellExecute = false })!)
                p.WaitForExit();
            Assert.True(Directory.Exists(junction));
            Assert.False(DocBrowser.Allowed(new Uri(Path.Combine(junction, "pic.png")).AbsoluteUri, [dir]));
        }
        finally
        {
            Paths.TryDelete(Path.Combine(dir, "link"));
            Paths.TryDelete(elsewhere);
        }
    }
}