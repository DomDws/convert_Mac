using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Convert.Core;

/// <summary>Ends the process once its work is done.</summary>
public static class AppExit
{
    /// <summary>
    /// Ends the process at once, with nothing left for Windows to unwind.
    /// </summary>
    /// <remarks>
    /// A process that has drawn anything (a window, a PDF page, a HEIC probe) has the graphics driver
    /// loaded, and a normal exit runs that driver's shutdown code last of all. On PCs with Intel
    /// graphics that code can take seconds, and now and then it never returns: the process then stays
    /// behind for good, invisible, with one thread spinning at full speed and no way to end it short
    /// of signing out. Everything Convert has to finish is finished before this is called, so the
    /// process is simply ended instead.
    /// </remarks>
    public static void Now(int exitCode)
    {
        try
        {
            Console.Out.Flush();
            Console.Error.Flush();
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        ImageConv.CleanUp();
        TerminateProcess(Process.GetCurrentProcess().Handle, unchecked((uint)exitCode));
        Environment.Exit(exitCode);   // not reached
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
