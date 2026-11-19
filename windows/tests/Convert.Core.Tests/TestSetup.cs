using System.Runtime.CompilerServices;
using Convert.Core;

namespace Convert.Core.Tests;

internal static class TestSetup
{
    /// <summary>
    /// The tests make pictures with ImageMagick themselves. Its limits have to be in place before
    /// its first use, as they are in the app, where every use goes through Convert.Core.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize() => ImageConv.Setup();
}
