using System.Runtime.InteropServices;

namespace MegaPDF.App;

/// <summary>
/// Splits a raw Win32 command line the way <c>CommandLineToArgvW</c> would (#348 phase 2).
///
/// A redirected activation's <c>LaunchActivatedEventArgs.Arguments</c> is one string, not an
/// argv array — .NET's own <see cref="Environment.GetCommandLineArgs"/> only parses *this*
/// process's actual command line, so it is no help for a string handed over from another
/// process. A naive <c>Split(' ')</c> would cut a quoted path with a space in it in half; this
/// calls the same shell32 function Windows itself uses to turn a command line into argv, so a
/// path is never split just because it contains a space.
/// </summary>
internal static class CommandLine
{
    public static string[] Parse(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return [];

        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero)
            return [];
        try
        {
            var result = new string[count];
            for (var i = 0; i < count; i++)
            {
                var ptr = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                result[i] = Marshal.PtrToStringUni(ptr) ?? "";
            }
            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string cmdLine, out int numArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
