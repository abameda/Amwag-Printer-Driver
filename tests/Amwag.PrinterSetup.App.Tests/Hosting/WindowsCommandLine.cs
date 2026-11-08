using System.Runtime.InteropServices;
using System.Text;

namespace Amwag.PrinterSetup.App.Tests.Hosting;

/// <summary>Splits an argument string the way the C runtime (and CommandLineToArgvW, after argv[0]) does.</summary>
internal static class WindowsCommandLine
{
    public static IReadOnlyList<string> Split(string arguments)
    {
        var result = new List<string>();
        int i = 0;
        while (true)
        {
            while (i < arguments.Length && arguments[i] is ' ' or '\t')
                i++;
            if (i >= arguments.Length)
                return result;

            var current = new StringBuilder();
            bool quoted = false;
            while (i < arguments.Length && (quoted || arguments[i] is not (' ' or '\t')))
            {
                char c = arguments[i];
                if (c == '\\')
                {
                    int start = i;
                    while (i < arguments.Length && arguments[i] == '\\')
                        i++;
                    int count = i - start;
                    if (i < arguments.Length && arguments[i] == '"')
                    {
                        current.Append('\\', count / 2);
                        if (count % 2 == 1)
                        {
                            current.Append('"');
                            i++;
                        }
                    }
                    else
                    {
                        current.Append('\\', count);
                    }
                    continue;
                }

                if (c == '"')
                {
                    if (quoted && i + 1 < arguments.Length && arguments[i + 1] == '"')
                    {
                        current.Append('"');
                        i += 2;
                        continue;
                    }
                    quoted = !quoted;
                    i++;
                    continue;
                }

                current.Append(c);
                i++;
            }

            result.Add(current.ToString());
        }
    }

    /// <summary>The real Windows parser (shell32 CommandLineToArgvW), skipping the program name.</summary>
    public static IReadOnlyList<string> SplitWithWindows(string arguments)
    {
        nint argv = CommandLineToArgvW("x.exe " + arguments, out int count);
        if (argv == 0)
            throw new InvalidOperationException("CommandLineToArgvW failed: " + Marshal.GetLastPInvokeError());
        try
        {
            var result = new List<string>(count);
            for (int index = 1; index < count; index++)
                result.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size))!);
            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
