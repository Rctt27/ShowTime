using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using ShowTime.Core.Ports;

namespace ShowTime.Mac.Platform;

/// <summary>
/// Answers a frozen main thread from a background thread, with tools that work while AppKit cannot: macOS's
/// <c>sample</c> writes where each thread is to the log folder, to join to a report; a dialog shown by
/// <c>osascript</c>, a process of its own, takes the keyboard back from ShowTime and offers to force it to quit.
/// Nothing is quit without the user's click. ShowTime, a menu bar app, is not listed by ⌥⌘⎋.
/// </summary>
internal sealed class MacHangResponder(IAppPaths paths, ILogger<MacHangResponder> log) : IHangResponder
{
    private const string Wait = "Attendre";
    private const string ForceQuit = "Forcer l'arrêt";
    private static readonly TimeSpan SampleTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FfmpegGrace = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private Process? _dialog;

    public void Hung(TimeSpan silence, bool first)
    {
        if (log.IsEnabled(LogLevel.Warning))
            log.LogWarning("The main thread has not answered for {Seconds:F0} s", silence.TotalSeconds);
        if (first)
            Sample();
        if (Ask(silence) == ForceQuit)
            Quit();
    }

    public void Recovered(TimeSpan silence)
    {
        if (log.IsEnabled(LogLevel.Information))
            log.LogInformation("The main thread answers again after {Seconds:F0} s", silence.TotalSeconds);
        // The question no longer stands: the dialog closes by itself.
        lock (_gate)
        {
            try
            {
                _dialog?.Kill();
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // Already closed.
            }
        }
    }

    /// <summary>Three seconds of every thread's stack, the main thread's first: what it is stuck in.</summary>
    private void Sample()
    {
        var file = Path.Combine(paths.LogsFolder,
            string.Create(CultureInfo.InvariantCulture, $"hang-{DateTime.Now:yyyyMMdd-HHmmss}.txt"));
        Run("/usr/bin/sample", [Environment.ProcessId.ToString(CultureInfo.InvariantCulture), "3", "-file", file], SampleTimeout);
        if (File.Exists(file))
        {
            if (log.IsEnabled(LogLevel.Warning))
                log.LogWarning("Where ShowTime is stuck: {File}", file);
        }
        else
        {
            log.LogWarning("sample could not record where ShowTime is stuck");
        }
    }

    /// <returns>The button clicked; null when the dialog was closed by <see cref="Recovered"/> or could not open.</returns>
    private string? Ask(TimeSpan silence)
    {
        var seconds = silence.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture);
        var message = $"ShowTime ne répond plus depuis {seconds} secondes.\n\n« {ForceQuit} » ferme ShowTime ; " +
            "une vidéo en cours d'enregistrement est arrêtée et gardée.";
        var start = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false, RedirectStandardOutput = true };
        string[] script =
        [
            "on run argv",
            "activate",
            $"display dialog (item 1 of argv) with title \"ShowTime\" buttons {{\"{Wait}\", \"{ForceQuit}\"}} default button \"{Wait}\" with icon caution",
            "return button returned of result",
            "end run"
        ];
        foreach (var line in script)
        {
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add(line);
        }
        // The message comes as an argument, never inside the script: no quoting to get wrong.
        start.ArgumentList.Add(message);

        Process? dialog;
        try
        {
            dialog = Process.Start(start);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            log.LogWarning(ex, "The dialog offering to force ShowTime to quit could not open");
            return null;
        }
        if (dialog is null)
            return null;
        lock (_gate)
            _dialog = dialog;
        try
        {
            var answer = dialog.StandardOutput.ReadToEnd().Trim();
            dialog.WaitForExit();
            return answer;
        }
        finally
        {
            lock (_gate)
                _dialog = null;
            dialog.Dispose();
        }
    }

    private void Quit()
    {
        log.LogWarning("Forced to quit by the user");
        var pid = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        // ffmpeg ends a recording on SIGINT as on "q": it writes the MP4's index, and the video stays readable.
        Run("/usr/bin/pkill", ["-INT", "-P", pid, "-x", "ffmpeg"], TimeSpan.FromSeconds(2));
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < FfmpegGrace && Run("/usr/bin/pgrep", ["-P", pid, "-x", "ffmpeg"], TimeSpan.FromSeconds(2)) == 0)
            Thread.Sleep(200);
        // Not Environment.Exit, whose handlers could wait for the frozen main thread.
        using var self = Process.GetCurrentProcess();
        self.Kill();
    }

    /// <returns>The tool's exit code; null when it could not run or did not end in time.</returns>
    private static int? Run(string tool, string[] arguments, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(tool) { UseShellExecute = false };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return null;
            if (process.WaitForExit(timeout))
                return process.ExitCode;
            process.Kill();
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }
}
