using System.IO;
using System.Runtime.InteropServices;

namespace optimizerDuck.Common.Helpers;

/// <summary>
///     Gives the windowed app a text output for command line runs: the console it was started
///     from when there is one, otherwise standard output as redirected by the caller.
/// </summary>
public static class ConsoleBridge
{
    private const int AttachParentProcess = -1;

    /// <summary>Attaches to the parent console when possible and returns a writer to it.</summary>
    public static TextWriter Open()
    {
        // A redirected standard output already has a handle; attaching would replace nothing.
        if (!Console.IsOutputRedirected)
            AttachConsole(AttachParentProcess);

        var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        writer.WriteLine();
        return writer;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);
}
