// Interim: belongs in WinBackground's Shell (background-tools-lib). Shell.Run there can't send stdin, set the
// working folder, or read and write UTF-8 (it uses the console's code page), all of which the after-run hook needs.
// Move this into Shell.Run as optional parameters and delete this copy.
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using WinBackground;

namespace OrgDirectory.Interim;

public static class ShellRun
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>subprocess.run(args, input=stdin, cwd=..., capture_output=True, text=True, encoding="utf-8",
    /// errors="replace", timeout=..., creationflags=CREATE_NO_WINDOW): no window, UTF-8 both ways (bad bytes
    /// replaced), "\r\n" read as "\n". Throws TimeoutException (after killing the process tree) and
    /// Win32Exception (the program can't be started) as subprocess raised TimeoutExpired and OSError.</summary>
    public static Completed Run(IReadOnlyList<string> args, string? stdin, string? workingDirectory, TimeSpan timeout)
    {
        if (args.Count == 0)
            throw new ArgumentException("no program to run");
        var info = new ProcessStartInfo(args[0])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        if (workingDirectory is not null)
            info.WorkingDirectory = workingDirectory;
        foreach (var arg in args.Skip(1))
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Couldn't start {args[0]}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            if (stdin is not null)
                process.StandardInput.Write(stdin.Replace("\n", "\r\n"));   // text mode, as Python wrote it
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // the program exited without reading its input: Python ignores a broken pipe here too
        }
        if (!process.WaitForExit(timeout))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException(
                $"Command '{PyRepr(args)}' timed out after {timeout.TotalSeconds:0} seconds");
        }
        process.WaitForExit();
        return new Completed(process.ExitCode, Normalise(output.Result), Normalise(error.Result));
    }

    /// <summary>An OSError's text as Python shows it: "[WinError 2] The system cannot find the file specified".</summary>
    public static string WinErrorText(Win32Exception error)
    {
        var message = new Win32Exception(error.NativeErrorCode).Message.TrimEnd(' ', '\t', '\r', '\n', '.');
        return $"[WinError {error.NativeErrorCode}] {message}";
    }

    private static string PyRepr(IReadOnlyList<string> args) =>
        "[" + string.Join(", ", args.Select(arg => "'" + arg.Replace("\\", "\\\\").Replace("'", "\\'") + "'")) + "]";

    private static string Normalise(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
}
