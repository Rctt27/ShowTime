using System.Diagnostics;
using System.Text;
using ShowTime.Core.Updates;

namespace ShowTime.Infrastructure.Updates;

/// <summary>Writes the replacement script and starts it apart from ShowTime, which quits right after.</summary>
public static class ScriptLauncher
{
    /// <summary>
    /// Writes <paramref name="text"/> in <paramref name="folder"/> and returns the file: <c>update.ps1</c> in UTF-8 with
    /// a BOM on Windows, without which PowerShell 5.1 reads the accents of a path in the ANSI code page; <c>update.sh</c>
    /// in UTF-8 without one on macOS.
    /// </summary>
    public static string Write(UpdatePlatform platform, string folder, string text)
    {
        Directory.CreateDirectory(folder);
        var windows = platform == UpdatePlatform.WindowsX64;
        var file = Path.Combine(folder, windows ? "update.ps1" : "update.sh");
        File.WriteAllText(file, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: windows));
        return file;
    }

    /// <summary>Starts the script without a window; it outlives ShowTime, which does not wait for it.</summary>
    public static Process StartDetached(UpdatePlatform platform, string scriptFile)
    {
        ProcessStartInfo info;
        if (platform == UpdatePlatform.WindowsX64)
        {
            info = new ProcessStartInfo("powershell.exe") { CreateNoWindow = true };
            foreach (var argument in (string[])["-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", scriptFile])
                info.ArgumentList.Add(argument);
        }
        else
        {
            // nohup keeps it alive when ShowTime quits; its own output goes nowhere, the script logs to its file.
            info = new ProcessStartInfo("/usr/bin/nohup");
            foreach (var argument in (string[])["/bin/sh", "-c", "exec /bin/sh \"$0\" </dev/null >/dev/null 2>&1", scriptFile])
                info.ArgumentList.Add(argument);
        }

        info.UseShellExecute = false;
        return Process.Start(info) ?? throw new InvalidOperationException("The update script did not start.");
    }
}
