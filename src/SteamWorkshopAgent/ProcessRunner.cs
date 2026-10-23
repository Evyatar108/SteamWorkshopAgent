using System.Diagnostics;
using System.Text;

namespace SteamWorkshopAgent;

public sealed class ProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        TimeSpan? timeout = null,
        int maxOutputChars = 12000,
        CancellationToken cancellationToken = default)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        process.StartInfo.WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardInput = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;

        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        if (environment != null)
        {
            foreach (var item in environment)
                process.StartInfo.Environment[item.Key] = item.Value;
        }

        var stdout = new BoundedTextBuffer(maxOutputChars);
        var stderr = new BoundedTextBuffer(maxOutputChars);
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
                stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                stderr.AppendLine(e.Data);
        };

        var stopwatch = Stopwatch.StartNew();
        if (!process.Start())
            throw new InvalidOperationException($"Failed to start process: {fileName}");

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(60);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(effectiveTimeout);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            await Task.WhenAll(
                process.WaitForExitAsync(CancellationToken.None),
                Task.Delay(10, CancellationToken.None));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKill(process);
        }

        stopwatch.Stop();
        return new ProcessResult(
            timedOut ? -1 : process.ExitCode,
            stdout.ToString(),
            stderr.ToString(),
            stopwatch.ElapsedMilliseconds,
            timedOut,
            stdout.Truncated,
            stderr.Truncated,
            stdout.TotalChars,
            stderr.TotalChars);
    }

    public string? FindOnPath(string executable)
    {
        return FindOnPath(executable, Environment.GetEnvironmentVariable("PATH") ?? "",
            Environment.GetEnvironmentVariable("PATHEXT"), OperatingSystem.IsWindows());
    }

    internal static string? FindOnPath(string executable, string searchPath, string? pathExt, bool windows)
    {
        var paths = searchPath
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var suffixes = windows && !Path.HasExtension(executable)
            ? new[] { "" }.Concat((pathExt ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            : new[] { "" };

        foreach (var path in paths)
        {
            foreach (var suffix in suffixes)
            {
                var candidate = Path.Combine(path.Trim('"'), executable + suffix);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort cleanup after timeout.
        }
    }

    private sealed class BoundedTextBuffer(int maxChars)
    {
        private readonly StringBuilder builder = new();

        public long TotalChars { get; private set; }

        public bool Truncated { get; private set; }

        public void AppendLine(string line)
        {
            var value = line + Environment.NewLine;
            TotalChars += value.Length;
            builder.Append(value);

            if (builder.Length <= maxChars)
                return;

            var removeChars = builder.Length - maxChars;
            builder.Remove(0, removeChars);
            Truncated = true;
        }

        public override string ToString()
        {
            if (!Truncated)
                return builder.ToString();

            return $"[truncated; kept last {maxChars} of {TotalChars} chars]{Environment.NewLine}{builder}";
        }
    }
}
