using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ShowTime.Core.Updates;

/// <summary>What the replacement script does: the paths, the process to wait for and the commands to relaunch with.</summary>
/// <param name="ProcessId">The running ShowTime, which quits right after starting the script.</param>
/// <param name="StagedApp">Windows: the extracted folder holding <c>ShowTime.exe</c>; macOS: the extracted <c>ShowTime.app</c>.</param>
/// <param name="ConfirmFile">Written by the new version once started.</param>
/// <param name="LogFile">The script's own log.</param>
/// <param name="LaunchNew">The program and arguments starting the new version, which confirms through <paramref name="ConfirmFile"/>.</param>
/// <param name="LaunchOld">The program and arguments starting the restored version, which tells that the update failed.</param>
public sealed record ReplacementPlan(int ProcessId, UpdateLayout Layout, string StagedApp, SemanticVersion From, SemanticVersion To,
    string ConfirmFile, string LogFile, TimeSpan ConfirmTimeout, IReadOnlyList<string> LaunchNew, IReadOnlyList<string> LaunchOld)
{
    public const string ConfirmArgument = "--update-confirm";
    public const string FailedArgument = "--update-failed";

    /// <summary>Written by the script before anything else: ShowTime quits only once it is there.</summary>
    public string StartedFile => Layout.StagingFolder(To) + (Layout.Platform == UpdatePlatform.WindowsX64 ? @"\started" : "/started");

    /// <summary>The plan of an update of <paramref name="layout"/> from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static ReplacementPlan For(int processId, UpdateLayout layout, string stagedApp, SemanticVersion from, SemanticVersion to, string logFile)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var staging = layout.StagingFolder(to);
        string confirm;
        string[] start;
        if (layout.Platform == UpdatePlatform.WindowsX64)
        {
            confirm = staging + @"\confirmed";
            start = [layout.AppPath + @"\ShowTime.exe"];
        }
        else
        {
            confirm = staging + "/confirmed";
            start = ["open", "-n", layout.AppPath, "--args"];
        }

        return new ReplacementPlan(processId, layout, stagedApp, from, to, confirm, logFile, TimeSpan.FromSeconds(60),
            [.. start, ConfirmArgument, confirm], [.. start, FailedArgument, to.ToString()]);
    }
}

/// <summary>
/// The text of the script that replaces ShowTime once it has quit: PowerShell 5.1 on Windows, <c>/bin/sh</c> on macOS.
/// It waits for the process, moves the installed items aside, moves the staged ones in, starts the new version and waits
/// for its confirmation; on a failure or without it, it stops the new version, puts back exactly what it moved and
/// starts the old version again. Every step goes to the plan's log.
/// </summary>
public static partial class ReplacementScript
{
    /// <summary>How long the script waits for the running ShowTime to quit before giving up without touching anything.</summary>
    private const int QuitSeconds = 30;

    // PowerShell ends a single-quoted string at any of these, and reads two of them in a row as one.
    private static readonly char[] PowerShellQuotes = ['\'', '‘', '’', '‚', '‛'];

    public static string ForWindows(ReplacementPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var layout = plan.Layout;
        return Fill(WindowsTemplate.ReplaceLineEndings("\r\n"), new Dictionary<string, string>
        {
            ["FROM"] = plan.From.ToString(),
            ["TO"] = plan.To.ToString(),
            ["PID"] = plan.ProcessId.ToString(CultureInfo.InvariantCulture),
            ["QUIT"] = QuitSeconds.ToString(CultureInfo.InvariantCulture),
            ["INSTALL"] = PowerShellQuote(layout.AppPath),
            ["BACKUP"] = PowerShellQuote(layout.BackupPath),
            ["MARKER"] = PowerShellQuote(layout.RestoreFailedMarker),
            ["STAGED"] = PowerShellQuote(plan.StagedApp),
            ["STAGING"] = PowerShellQuote(layout.StagingFolder(plan.To)),
            ["ITEMS"] = string.Join(", ", layout.Items.Select(PowerShellQuote)),
            ["CONFIRM"] = PowerShellQuote(plan.ConfirmFile),
            ["STARTED"] = PowerShellQuote(plan.StartedFile),
            ["TIMEOUT"] = Seconds(plan.ConfirmTimeout),
            ["LOG"] = PowerShellQuote(plan.LogFile),
            ["NEWFILE"] = PowerShellQuote(plan.LaunchNew[0]),
            ["NEWARGS"] = PowerShellQuote(CommandLine(plan.LaunchNew.Skip(1))),
            ["OLDFILE"] = PowerShellQuote(plan.LaunchOld[0]),
            ["OLDARGS"] = PowerShellQuote(CommandLine(plan.LaunchOld.Skip(1))),
        });
    }

    public static string ForMacOS(ReplacementPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var layout = plan.Layout;
        return Fill(MacTemplate.ReplaceLineEndings("\n"), new Dictionary<string, string>
        {
            ["FROM"] = plan.From.ToString(),
            ["TO"] = plan.To.ToString(),
            ["PID"] = plan.ProcessId.ToString(CultureInfo.InvariantCulture),
            ["QUIT"] = QuitSeconds.ToString(CultureInfo.InvariantCulture),
            ["BUNDLE"] = ShellQuote(layout.AppPath),
            ["BACKUP"] = ShellQuote(layout.BackupPath),
            ["MARKER"] = ShellQuote(layout.RestoreFailedMarker),
            ["STAGED"] = ShellQuote(plan.StagedApp),
            ["STAGING"] = ShellQuote(layout.StagingFolder(plan.To)),
            ["CONFIRM"] = ShellQuote(plan.ConfirmFile),
            ["STARTED"] = ShellQuote(plan.StartedFile),
            ["TIMEOUT"] = Seconds(plan.ConfirmTimeout),
            ["LOG"] = ShellQuote(plan.LogFile),
            ["NEW"] = string.Join(' ', plan.LaunchNew.Select(ShellQuote)),
            ["OLD"] = string.Join(' ', plan.LaunchOld.Select(ShellQuote)),
            // pkill -f takes an extended regular expression, matched against the process's command line.
            ["PROCESS"] = ShellQuote(EscapeRegex(layout.AppPath + "/Contents/MacOS/ShowTime")),
        });
    }

    /// <summary>A PowerShell single-quoted literal: its quotes doubled, typographic ones included.</summary>
    private static string PowerShellQuote(string value)
    {
        var quoted = new StringBuilder("'");
        foreach (var c in value)
        {
            quoted.Append(c);
            if (PowerShellQuotes.Contains(c))
                quoted.Append(c);
        }

        return quoted.Append('\'').ToString();
    }

    /// <summary>A POSIX shell single-quoted word: a quote closes it, is escaped, and opens it again.</summary>
    private static string ShellQuote(string value) => "'" + value.Replace("'", @"'\''", StringComparison.Ordinal) + "'";

    /// <summary>Arguments joined into one Windows command line, quoted as <c>CommandLineToArgvW</c> splits them.</summary>
    private static string CommandLine(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(WindowsArgument));

    private static string WindowsArgument(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
            return argument;

        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            // Backslashes before a quote are doubled, and the quote escaped; elsewhere they are literal.
            quoted.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes).Append(c);
            backslashes = 0;
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private static string EscapeRegex(string text) => RegexMetacharacter().Replace(text, @"\$0");

    /// <summary>Whole seconds, rounded up: the shell only counts in seconds.</summary>
    private static string Seconds(TimeSpan timeout) =>
        Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

    private static string Fill(string template, Dictionary<string, string> values) =>
        Placeholder().Replace(template, match => values[match.Groups[1].Value]);

    [GeneratedRegex(@"@([A-Z]+)@")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"[\\.^$*+?()\[\]{}|]")]
    private static partial Regex RegexMetacharacter();

    private const string WindowsTemplate = """
        # Replaces ShowTime @FROM@ by @TO@, then checks that the new version starts. Written by ShowTime, run hidden.
        $ErrorActionPreference = 'Stop'
        $processId = @PID@
        $install = @INSTALL@
        $backup = @BACKUP@
        $marker = @MARKER@
        $staged = @STAGED@
        $staging = @STAGING@
        $items = @(@ITEMS@)
        $confirm = @CONFIRM@
        $confirmSeconds = @TIMEOUT@
        $log = @LOG@
        $newFile = @NEWFILE@
        $newArguments = @NEWARGS@
        $started = @STARTED@

        # First of all: ShowTime only quits once it sees this file, so that a script blocked by the system (execution
        # policy, application control) leaves it running instead of quitting for nothing.
        try { [System.IO.File]::WriteAllText($started, 'started') } catch { exit 3 }
        $oldFile = @OLDFILE@
        $oldArguments = @OLDARGS@

        function Write-Log([string]$message) {
            try {
                $line = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ' ' + $message + [Environment]::NewLine
                [System.IO.File]::AppendAllText($log, $line)
            } catch { }
        }

        function Test-Entry([string]$path) {
            return [System.IO.File]::Exists($path) -or [System.IO.Directory]::Exists($path)
        }

        # A file the old process held may stay locked for a moment after it quit: a move is tried for 3 s.
        function Move-Entry([string]$from, [string]$to) {
            for ($attempt = 1; $true; $attempt++) {
                try {
                    if ([System.IO.Directory]::Exists($from)) {
                        [System.IO.Directory]::Move($from, $to)
                    } else {
                        [System.IO.File]::Move($from, $to)
                    }
                    Write-Log ('Moved ' + $from + ' to ' + $to)
                    return
                } catch {
                    if ($attempt -ge 10) { throw }
                    Start-Sleep -Milliseconds 300
                }
            }
        }

        function Remove-Entry([string]$path) {
            try {
                if ([System.IO.Directory]::Exists($path)) {
                    [System.IO.Directory]::Delete($path, $true)
                    Write-Log ('Deleted ' + $path)
                } elseif ([System.IO.File]::Exists($path)) {
                    [System.IO.File]::Delete($path)
                    Write-Log ('Deleted ' + $path)
                }
            } catch {
                Write-Log ('Could not delete ' + $path + ': ' + $_.Exception.Message)
            }
        }

        # Arguments are one command line, already quoted.
        function Start-Command([string]$file, [string]$arguments) {
            $info = New-Object System.Diagnostics.ProcessStartInfo
            $info.FileName = $file
            $info.Arguments = $arguments
            $info.UseShellExecute = $false
            return [System.Diagnostics.Process]::Start($info)
        }

        try { [void][System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($log)) } catch { }
        # Out of the install folder, which must not be held by this process nor by the versions it starts.
        [System.IO.Directory]::SetCurrentDirectory([System.IO.Path]::GetTempPath())
        Write-Log ('Update of ' + $install + ' from @FROM@ to @TO@')

        $deadline = (Get-Date).AddSeconds(@QUIT@)
        while ($null -ne (Get-Process -Id $processId -ErrorAction SilentlyContinue)) {
            if ((Get-Date) -gt $deadline) {
                Write-Log ('ShowTime (process ' + $processId + ') did not quit within @QUIT@ s: nothing was changed')
                exit 1
            }
            Start-Sleep -Milliseconds 200
        }
        Write-Log ('ShowTime (process ' + $processId + ') has quit')

        $movedOut = @()
        $movedIn = @()
        $process = $null
        $confirmed = $false
        try {
            if (Test-Entry $backup) {
                Write-Log ('Deleting the earlier backup ' + $backup)
                [System.IO.Directory]::Delete($backup, $true)
            }
            [void][System.IO.Directory]::CreateDirectory($backup)
            foreach ($item in $items) {
                $path = [System.IO.Path]::Combine($install, $item)
                if (Test-Entry $path) {
                    Move-Entry $path ([System.IO.Path]::Combine($backup, $item))
                    $movedOut += $item
                } else {
                    Write-Log ($path + ' is absent: skipped')
                }
            }
            foreach ($item in $items) {
                $path = [System.IO.Path]::Combine($staged, $item)
                if (Test-Entry $path) {
                    Move-Entry $path ([System.IO.Path]::Combine($install, $item))
                    $movedIn += $item
                } else {
                    Write-Log ($path + ' is absent: skipped')
                }
            }

            Write-Log 'Starting version @TO@'
            $process = Start-Command $newFile $newArguments
            $deadline = (Get-Date).AddSeconds($confirmSeconds)
            while (-not [System.IO.File]::Exists($confirm)) {
                if ($process.HasExited -and -not [System.IO.File]::Exists($confirm)) {
                    throw 'version @TO@ quit without confirming its start'
                }
                if ((Get-Date) -gt $deadline) {
                    throw ('version @TO@ did not confirm its start within ' + $confirmSeconds + ' s')
                }
                Start-Sleep -Milliseconds 200
            }
            $confirmed = $true
        } catch {
            Write-Log ('Failed: ' + $_.Exception.Message)
        }

        if ($confirmed) {
            Write-Log 'Version @TO@ has started'
            Remove-Entry $backup
            Remove-Entry $staging
            Write-Log 'Update done'
            exit 0
        }

        Write-Log 'Restoring version @FROM@'
        try {
            if ($null -ne $process -and -not $process.HasExited) {
                $process.Kill()
                [void]$process.WaitForExit(5000)
            }
        } catch {
            Write-Log ('Could not stop the new version: ' + $_.Exception.Message)
        }
        try {
            $prefix = $install + '\'
            Get-Process -Name 'ShowTime' -ErrorAction SilentlyContinue |
                Where-Object { $_.Path -and $_.Path.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase) } |
                ForEach-Object {
                    Write-Log ('Stopping process ' + $_.Id)
                    $_.Kill()
                    [void]$_.WaitForExit(5000)
                }
        } catch {
            Write-Log ('Could not stop ShowTime: ' + $_.Exception.Message)
        }

        $restored = $true
        for ($i = $movedIn.Count - 1; $i -ge 0; $i--) {
            $item = $movedIn[$i]
            try {
                Move-Entry ([System.IO.Path]::Combine($install, $item)) ([System.IO.Path]::Combine($staged, $item))
            } catch {
                $restored = $false
                Write-Log ('Could not remove the new ' + $item + ': ' + $_.Exception.Message)
            }
        }
        for ($i = $movedOut.Count - 1; $i -ge 0; $i--) {
            $item = $movedOut[$i]
            $path = [System.IO.Path]::Combine($install, $item)
            try {
                if (Test-Entry $path) { throw ($path + ' is still there') }
                Move-Entry ([System.IO.Path]::Combine($backup, $item)) $path
            } catch {
                $restored = $false
                Write-Log ('Could not put back ' + $item + ': ' + $_.Exception.Message)
            }
        }
        # Started again, the old version would delete the backup as a leftover, and it may hold the only copy of its files.
        # The marker tells every later start to keep it and say where it is.
        if (-not $restored) {
            Write-Log ('Could not restore version @FROM@: it stays in ' + $backup + ' and version @TO@ in ' + $staging + '; ShowTime is not started again')
            try {
                [System.IO.File]::WriteAllText($marker, $backup)
            } catch {
                Write-Log ('Could not write ' + $marker + ': ' + $_.Exception.Message)
            }
            exit 2
        }
        Remove-Entry $backup
        Remove-Entry $staging

        Write-Log 'Starting version @FROM@ again'
        try {
            [void](Start-Command $oldFile $oldArguments)
        } catch {
            Write-Log ('Could not start version @FROM@: ' + $_.Exception.Message)
        }
        exit 1

        """;

    private const string MacTemplate = """
        #!/bin/sh
        # Replaces ShowTime @FROM@ by @TO@, then checks that the new version starts. Written by ShowTime, run detached.
        pid=@PID@
        bundle=@BUNDLE@
        backup=@BACKUP@
        marker=@MARKER@
        staged=@STAGED@
        staging=@STAGING@
        confirm=@CONFIRM@
        confirm_seconds=@TIMEOUT@
        log=@LOG@
        started=@STARTED@

        # First of all: ShowTime only quits once it sees this file.
        echo started > "$started" 2>/dev/null || exit 3

        say() {
          printf '%s %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$1" >> "$log"
        }

        moved_out=no
        moved_in=no
        new_pid=

        # Waits up to 5 s for the new version to quit.
        wait_for_new_version_to_quit() {
          tries=0
          while pgrep -f @PROCESS@ >/dev/null 2>&1; do
            if [ "$tries" -ge 25 ]; then
              return 1
            fi
            tries=$((tries + 1))
            sleep 0.2
          done
          return 0
        }

        # Stops the new version, puts back what was moved, starts the old version again.
        roll_back() {
          say "Failed: $1"
          say "Restoring version @FROM@"
          if [ -n "$new_pid" ]; then
            kill "$new_pid" 2>/dev/null
          fi
          # The old version quits at once while a process with its bundle identifier is running.
          pkill -f @PROCESS@ 2>/dev/null
          if ! wait_for_new_version_to_quit; then
            say "The new version did not quit: killing it"
            pkill -9 -f @PROCESS@ 2>/dev/null
            wait_for_new_version_to_quit || say "The new version is still running"
          fi
          restored=yes
          if [ "$moved_in" = yes ]; then
            if [ ! -e "$staged" ] && mv "$bundle" "$staged"; then
              say "Moved $bundle to $staged"
            else
              restored=no
              say "Could not remove the new version from $bundle"
            fi
          fi
          if [ "$moved_out" = yes ]; then
            if [ ! -e "$bundle" ] && mv "$backup" "$bundle"; then
              say "Moved $backup to $bundle"
            else
              restored=no
              say "Could not put back $bundle"
            fi
          fi
          # Started again, the old version would delete the backup as a leftover, and it may be its only copy.
          # The marker tells every later start to keep it and say where it is.
          if [ "$restored" = no ]; then
            say "Could not restore version @FROM@: it stays in $backup and version @TO@ in $staging; ShowTime is not started again"
            printf '%s\n' "$backup" > "$marker" || say "Could not write $marker"
            exit 2
          fi
          rm -rf "$backup"
          rm -rf "$staging"
          say "Starting version @FROM@ again"
          @OLD@ </dev/null >/dev/null 2>&1 &
          exit 1
        }

        mkdir -p "$(dirname "$log")" 2>/dev/null
        say "Update of $bundle from @FROM@ to @TO@"

        deadline=$(( $(date +%s) + @QUIT@ ))
        while kill -0 "$pid" 2>/dev/null; do
          if [ "$(date +%s)" -ge "$deadline" ]; then
            say "ShowTime (process $pid) did not quit within @QUIT@ s: nothing was changed"
            exit 1
          fi
          sleep 0.2
        done
        say "ShowTime (process $pid) has quit"

        if [ -e "$backup" ]; then
          say "Deleting the earlier backup $backup"
          rm -rf "$backup" || roll_back "could not delete $backup"
        fi
        if [ -e "$bundle" ]; then
          mv "$bundle" "$backup" || roll_back "could not move $bundle to $backup"
          moved_out=yes
          say "Moved $bundle to $backup"
        else
          say "$bundle is absent: skipped"
        fi
        if [ -e "$staged" ]; then
          mv "$staged" "$bundle" || roll_back "could not move $staged to $bundle"
          moved_in=yes
          say "Moved $staged to $bundle"
        else
          say "$staged is absent: skipped"
        fi

        say "Starting version @TO@"
        # Run directly, so that $! is the new version's process (or open's), which a failure stops.
        @NEW@ </dev/null >/dev/null 2>&1 &
        new_pid=$!
        deadline=$(( $(date +%s) + confirm_seconds ))
        until [ -e "$confirm" ]; do
          if [ "$(date +%s)" -ge "$deadline" ]; then
            roll_back "version @TO@ did not confirm its start within $confirm_seconds s"
          fi
          sleep 0.2
        done

        say "Version @TO@ has started"
        rm -rf "$backup" "$staging"
        say "Update done"
        exit 0

        """;
}
