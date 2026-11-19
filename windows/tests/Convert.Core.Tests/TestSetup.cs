using System.Runtime.CompilerServices;
using Convert.Core;

namespace Convert.Core.Tests;

internal static class TestSetup
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // The tests make pictures with ImageMagick themselves. Its limits have to be in place before
        // its first use, as they are in the app, where every use goes through Convert.Core.
        ImageConv.Setup();

        // The test host draws PDF pages and probes the HEIC encoder, so it ends like the app does:
        // at once. Left to exit the usual way, it sometimes never does on PCs with Intel graphics
        // (see AppExit), and "dotnet test" then waits forever for a process nobody can end.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => AppExit.Now(Environment.ExitCode);
    }
}
