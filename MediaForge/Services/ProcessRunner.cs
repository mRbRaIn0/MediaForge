using System.Diagnostics;
using System.Text;

namespace MediaForge.Services;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool Cancelled = false);

public static class ProcessRunner
{
    public static async Task<ProcessResult> CaptureAsync(
        string executable, IEnumerable<string> arguments, string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var result = await RunStreamingAsync(executable, arguments,
            line => { stdout.AppendLine(line); return Task.CompletedTask; },
            line => { stderr.AppendLine(line); return Task.CompletedTask; },
            workingDirectory, cancellationToken).ConfigureAwait(false);
        return result with { StandardOutput = stdout.ToString(), StandardError = stderr.ToString() };
    }

    public static async Task<ProcessResult> RunStreamingAsync(
        string executable,
        IEnumerable<string> arguments,
        Func<string, Task>? stdout = null,
        Func<string, Task>? stderr = null,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException($"{executable} konnte nicht gestartet werden.");

        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* Prozess ist bereits beendet. */ }
        });

        var outTask = PumpAsync(process.StandardOutput, stdout, cancellationToken);
        var errTask = PumpAsync(process.StandardError, stderr, cancellationToken);
        var cancelled = false;
        try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { cancelled = true; }
        try { await Task.WhenAll(outTask, errTask).ConfigureAwait(false); }
        catch (OperationCanceledException) { cancelled = true; }

        var code = cancelled ? -1 : process.ExitCode;
        return new ProcessResult(code, string.Empty, string.Empty, cancelled);
    }

    private static async Task PumpAsync(StreamReader reader, Func<string, Task>? handler, CancellationToken token)
    {
        var buffer = new StringBuilder();
        var chars = new char[512];
        while (!token.IsCancellationRequested)
        {
            // Never capture WPF's dispatcher for long-running external tool output.
            var read = await reader.ReadAsync(chars.AsMemory(0, chars.Length), token).ConfigureAwait(false);
            if (read == 0) break;
            for (var i = 0; i < read; i++)
            {
                var ch = chars[i];
                if (ch is '\r' or '\n')
                {
                    if (buffer.Length > 0)
                    {
                        if (handler is not null) await handler(buffer.ToString()).ConfigureAwait(false);
                        buffer.Clear();
                    }
                }
                else if (ch != '\0') buffer.Append(ch);
            }
        }
        if (buffer.Length > 0 && handler is not null) await handler(buffer.ToString()).ConfigureAwait(false);
    }
}
