using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;
using Windows.Management.Deployment;

namespace Convert;

/// <summary>
/// Adds "Convert…" to Explorer's right-click menu, and takes it out again: what
/// <c>Convert.exe --register-menu</c> and <c>--unregister-menu</c> do, for the installer.
/// </summary>
/// <remarks>
/// Windows 11 only shows menu entries of apps that have a package identity. Convert gets one by
/// registering the small package next to Convert.exe (Convert.msix: a manifest and logos) with
/// this folder as the place where the program really is. Windows only accepts the package if it
/// trusts whoever signed it.
/// </remarks>
internal static class ShellMenu
{
    public const string PackageFile = "Convert.msix";

    /// <summary>The app's folder, without a trailing backslash.</summary>
    private static string AppDir => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    /// <summary>0 when the entry is in the menu, 3 when Windows doesn't trust the package's signature, 1 for anything else.</summary>
    public static async Task<int> RegisterAsync()
    {
        try
        {
            var msix = Path.Combine(AppDir, PackageFile);
            var (name, publisher) = Identity(msix);
            var manager = new PackageManager();
            // An earlier registration goes first: Windows refuses a package of the same version with
            // other contents, and an older Convert may have been signed by someone else.
            await RemoveAsync(manager, name, publisher);
            await manager.AddPackageByUriAsync(new Uri(msix), new AddPackageOptions { ExternalLocationUri = new Uri(AppDir) });
            Console.WriteLine("Convert… is in Explorer's right-click menu.");
            return 0;
        }
        catch (Exception e)
        {
            var untrusted = IsUntrusted(e);
            Console.WriteLine("Couldn't add Convert to Explorer's right-click menu: "
                              + (untrusted ? "Windows doesn't trust the signature of this copy of Convert." : FirstLine(e.Message)));
            return untrusted ? 3 : 1;
        }
    }

    public static async Task<int> UnregisterAsync()
    {
        try
        {
            var msix = Path.Combine(AppDir, PackageFile);
            var (name, publisher) = File.Exists(msix) ? Identity(msix) : ("Convert", null);
            var removed = await RemoveAsync(new PackageManager(), name, publisher);
            StopMenuHosts();
            Console.WriteLine(removed > 0 ? "Convert… is no longer in Explorer's right-click menu." : "Convert wasn't in Explorer's right-click menu.");
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine("Couldn't take Convert out of Explorer's right-click menu: " + FirstLine(e.Message));
            return 1;
        }
    }

    /// <summary>
    /// Removes this Convert's package: one of that name that points at this folder, or that has the
    /// publisher of the package file here. Another program that happens to be called Convert is not ours.
    /// </summary>
    private static async Task<int> RemoveAsync(PackageManager manager, string name, string? publisher)
    {
        var ours = manager.FindPackagesForUser("")
            .Where(p => p.Id.Name == name && (p.Id.Publisher == publisher || PointsHere(p)))
            .Select(p => p.Id.FullName).ToList();
        foreach (var fullName in ours) await manager.RemovePackageAsync(fullName);
        return ours.Count;
    }

    private static bool PointsHere(Windows.ApplicationModel.Package p)
    {
        try
        {
            return p.EffectiveExternalPath is { Length: > 0 } path
                   && string.Equals(Path.TrimEndingDirectorySeparator(path), AppDir, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }   // a package Windows can't describe isn't ours
    }

    /// <summary>The name and publisher the package file declares.</summary>
    private static (string Name, string Publisher) Identity(string msix)
    {
        if (!File.Exists(msix)) throw new FileNotFoundException($"{PackageFile} is missing from {AppDir}. Install Convert again.");
        using var zip = ZipFile.OpenRead(msix);
        var entry = zip.GetEntry("AppxManifest.xml") ?? throw new InvalidDataException($"{PackageFile} has no manifest.");
        using var stream = entry.Open();
        var identity = XDocument.Load(stream).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Identity")
                       ?? throw new InvalidDataException($"{PackageFile} has no identity.");
        return ((string?)identity.Attribute("Name") ?? "", (string?)identity.Attribute("Publisher") ?? "");
    }

    /// <summary>
    /// Explorer runs the menu entry in a helper process (dllhost.exe) that can stay around for a
    /// while and keeps Convert's files open. Ending it lets them be replaced or removed.
    /// </summary>
    private static void StopMenuHosts()
    {
        var prefix = AppDir + Path.DirectorySeparatorChar;
        foreach (var p in Process.GetProcessesByName("dllhost"))
        {
            try
            {
                if (p.Modules.Cast<ProcessModule>().Any(m => m.FileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) p.Kill();
            }
            catch (Exception) { }   // somebody else's, or already gone
            finally { p.Dispose(); }
        }
    }

    /// <summary>
    /// The certificate that signed the package isn't trusted here, or the package isn't signed at all.
    /// Windows reports that as its own error with the certificate error named in the text.
    /// </summary>
    private static bool IsUntrusted(Exception e)
    {
        // Untrusted root, broken chain, no signature, distrusted publisher.
        uint[] codes = [0x800B0109, 0x800B010A, 0x800B0100, 0x800B0111];
        return codes.Contains(unchecked((uint)e.HResult))
               || codes.Any(c => e.Message.Contains($"0x{c:X8}", StringComparison.OrdinalIgnoreCase));
    }

    private static string FirstLine(string s) => s.Split('\n')[0].Trim();
}
