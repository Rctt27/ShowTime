# In-App Update Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When a newer GitHub release exists, the tray / menu bar menu offers « Installer la version X.Y.Z »; one click downloads it, quits, replaces the app in place, relaunches it and deletes the old one, on Windows and macOS.

**Architecture:** The rules live in a new core namespace `ShowTime.Core.Updates`: version comparison, reading GitHub's JSON, the check schedule, the `Updater` state machine, the menu entry's text, the download check and the replacement scripts as text. Ports (`IReleaseFeed`, `IUpdateDownloader`, `IUpdateInstaller`, `IUpdateHost`) are implemented by Infrastructure (HTTP, zip, Windows installer) and by the Mac app (ditto, codesign). Each app only adds a menu item, a settings checkbox, startup arguments and wiring.

**Tech Stack:** .NET 10, `HttpClient` (`SocketsHttpHandler`), `System.Text.Json` source generation, `System.IO.Compression`, `System.IO.Abstractions`, xunit v3 + Shouldly + NSubstitute + `FakeTimeProvider`; PowerShell 5.1 on Windows, `/bin/sh`, `ditto`, `codesign` on macOS.

**Spec:** `docs/superpowers/specs/2026-11-11-in-app-update-design.md`

## Global Constraints

- Source: `GET https://api.github.com/repos/Rctt27/ShowTime/releases/latest`, unauthenticated, `Accept: application/vnd.github+json`, `User-Agent: ShowTime/<version>`. Check timeout 10 s.
- Assets: `ShowTime-X.Y.Z-windows-x64.zip`, `ShowTime-X.Y.Z-macos.zip`. The Windows zip has a top-level `ShowTime\` folder; install its content.
- Versions: `MAJOR.MINOR.PATCH`, tag `vX.Y.Z`; anything with a suffix (`-beta`) is ignored. Current version = the assembly version (`<Version>` in `Directory.Build.props`).
- Schedule: first check 5 s after startup, then every 6 h, and on menu opening when the last successful answer is older than 15 min; never two checks at once.
- Setting: key `CheckForUpdates`, `true` by default on both platforms; a file without it loads as `true`. Checkbox: « Rechercher automatiquement les mises à jour ».
- Menu texts (French, exact): « Installer la version X.Y.Z », « Téléchargement de la version X.Y.Z… NN % », « Installation après l'enregistrement… », « Installer la version X.Y.Z (téléchargée) », « Installation de la version X.Y.Z… », « Rechercher une mise à jour », « Recherche d'une mise à jour… ». Tooltip: « ShowTime — version X.Y.Z disponible ».
- Messages (French, exact): « ShowTime est à jour (version A.B.C). », « La mise à jour n'a pas pu être téléchargée : {raison} », « ShowTime ne peut pas se remplacer dans ce dossier : la page de la version X.Y.Z s'ouvre dans le navigateur. », « La version X.Y.Z n'a pas pu démarrer ; ShowTime est resté en version A.B.C. », « Impossible de vérifier les mises à jour : {raison} » (manual check only).
- Confirmation: the new version writes the file passed as `--update-confirm <path>`; without it after 60 s the script restores the old version and relaunches it with `--update-failed X.Y.Z`.
- Layout: Windows staging `<install>\.update-X.Y.Z\`, backup `<install>\.old\`; macOS staging `<parent of bundle>/.ShowTime-update-X.Y.Z/`, backup `<parent>/.ShowTime-old.app`. Script log: `<LogsFolder>/update.log`.
- Automatic checks never show a dialog; failures go to the log only.
- Core: no I/O, 90 % line coverage; JSON through generated metadata (trim analyzer runs on core and infrastructure). UI strings French, code and comments English. `src/ShowTime.Mac` style by hand (`using System…` first, then alphabetical).
- Tests: CLAUDE.md forbids running tests locally without the user's go. Each "run" step below means: locally if the user allowed it, otherwise push the branch and `gh workflow run CI --ref in-app-update`, then read the job's result. `ShowTime.Windows.Tests` and `ShowTime.Platform.Tests` are never run locally.
- Every commit ends with the session's `Co-Authored-By` and `Claude-Session` lines.

## Review Focus

1. **Offline, rate-limited (HTTP 403) or slow GitHub at startup or menu opening** → the menu opens instantly, nothing is shown, no dialog; the next opening retries. Pinned in Task 5 (`An_automatic_check_that_fails_shows_nothing_and_retries_on_next_menu_opening`).
2. **Latest release not newer, a pre-release, or lacking this platform's asset or its digest** (including a local build ahead of GitHub, e.g. 0.8.0 running while 0.7.0 is latest) → no item. Pinned in Task 2 (`Parse_returns_null_when_…`) and Task 5 (`An_older_or_equal_release_offers_nothing`).
3. **Double click on the item, click during a check, setting unticked during a download** → one download only, which continues. Pinned in Task 6 (`A_second_install_click_while_downloading_does_nothing`, `Unticking_the_setting_does_not_stop_a_download`).
4. **ShowTime quit or crashing mid-download, or install interrupted** → no half-written app; leftovers deleted at next normal start, but not while the script runs. Pinned in Task 6 (`Dispose_cancels_the_download_and_deletes_the_staging_folder`) and Task 3 (`Leftovers_are_kept_while_an_update_is_being_confirmed`).
5. **Install path with spaces, apostrophes and accents** (`C:\Users\Zoé\Mes outils\ShowTime d'été`, `/Users/zoé/Applications/Show Time.app`) → scripts quote correctly. Pinned in Task 3 (`Scripts_quote_paths_with_spaces_apostrophes_and_accents`) and the integration runs of Task 7.

---

### Task 1: The `CheckForUpdates` setting

**Files:**
- Modify: `src/ShowTime.Core/Settings/AppSettings.cs`, `src/ShowTime.Core/Settings/SettingsEditor.cs` (`SettingsForm`, `From`, `ApplyTo`)
- Modify: `tests/ShowTime.Core.Tests/Settings/SettingsEditorTests.cs`, `tests/ShowTime.Infrastructure.IntegrationTests/Settings/JsonSettingsStoreTests.cs`

**Interfaces:**
- Produces: `AppSettings.CheckForUpdates : bool` (default `true`); `SettingsForm` gains a last positional parameter `bool CheckForUpdates`.

- [ ] **Step 1: Write the failing tests**
  - `SettingsEditorTests.From_and_ApplyTo_round_trip_every_field`: set `_settings.CheckForUpdates = false`, assert `copy.CheckForUpdates.ShouldBeFalse()`.
  - `SettingsEditorTests.Updates_are_checked_by_default`: `new AppSettings(SettingsDefaults.ForWindows("x")).CheckForUpdates.ShouldBeTrue()` and same for `ForMacOS`.
  - `JsonSettingsStoreTests`: in the v0.1.0 load test add `s.CheckForUpdates.ShouldBeTrue();`; in `A_saved_file_has_the_keys_…` add `"CheckForUpdates"` to the expected keys with the comment `// Deliberate change: the update check is a new key, which older files simply lack.`
- [ ] **Step 2: Run them, expect compile failure** — `dotnet test --project tests/ShowTime.Core.Tests` → `CS1061 'AppSettings' does not contain a definition for 'CheckForUpdates'`.
- [ ] **Step 3: Implement** — property with doc comment `/// <summary>Whether ShowTime looks for a newer version by itself (at startup, every 6 hours, on opening the menu).</summary>`; thread it through `SettingsForm`. Fix every `new SettingsForm(` call site (both settings windows pass the current value for now; Tasks 8 and 9 add the checkbox).
- [ ] **Step 4: Run both test projects** — PASS.
- [ ] **Step 5: Commit** — `Add the CheckForUpdates setting`.

### Task 2: Versions and GitHub's release JSON

**Files:**
- Create: `src/ShowTime.Core/Updates/SemanticVersion.cs`, `src/ShowTime.Core/Updates/GitHubRelease.cs` (with `ReleaseInfo`, `UpdatePlatform` and an internal source-generated `UpdateJsonContext`)
- Test: `tests/ShowTime.Core.Tests/Updates/SemanticVersionTests.cs`, `tests/ShowTime.Core.Tests/Updates/GitHubReleaseTests.cs`, fixture `tests/ShowTime.Core.Tests/Updates/release-v0.6.3.json` (copy of `gh api repos/Rctt27/ShowTime/releases/latest` for v0.6.3, trimmed to `tag_name`, `draft`, `prerelease`, `html_url`, `assets[].name/size/digest/browser_download_url`), copied to output.

**Interfaces:**
- Produces:
  - `public readonly record struct SemanticVersion(int Major, int Minor, int Patch) : IComparable<SemanticVersion>` with `static bool TryParse(string? text, out SemanticVersion version)` (accepts `0.6.3` and `v0.6.3`), `static SemanticVersion From(System.Version assemblyVersion)`, `<`, `>`, `<=`, `>=`, `ToString()` → `"0.6.3"`.
  - `public enum UpdatePlatform { WindowsX64, MacOS }`
  - `public sealed record ReleaseInfo(SemanticVersion Version, Uri DownloadUrl, long Size, string Sha256, Uri PageUrl)` — `Sha256` lowercase hex without the `sha256:` prefix.
  - `public static class GitHubRelease { public static readonly Uri LatestUrl; public static string AssetName(SemanticVersion version, UpdatePlatform platform); public static ReleaseInfo? Parse(string json, UpdatePlatform platform); }`

- [ ] **Step 1: Write the failing tests**
  - `TryParse_reads_plain_and_v_prefixed_versions` (`"0.6.3"`, `"v0.6.3"` → `new(0,6,3)`).
  - `TryParse_rejects` theory: `"0.7.0-beta"`, `"1.2"`, `"v"`, `""`, `null`, `"1.2.3.4"`, `"a.b.c"`.
  - `Versions_compare_numerically`: `new(0,10,0) > new(0,9,9)`, `new(1,0,0) > new(0,99,99)`, equality.
  - `From_drops_the_revision`: `SemanticVersion.From(new Version(0,6,3,0))` → `0.6.3`.
  - `AssetName_matches_the_release_workflow`: `ShowTime-0.6.3-windows-x64.zip`, `ShowTime-0.6.3-macos.zip`.
  - `Parse_reads_the_v0_6_3_release` for both platforms: version `0.6.3`, the asset's URL, size and digest (no prefix), `PageUrl` = `html_url`.
  - `Parse_returns_null_when_…` theory: invalid JSON, `prerelease: true`, `draft: true`, tag `v0.7.0-rc1`, no asset for the platform, asset without `digest`, digest not `sha256:`.
- [ ] **Step 2: Run, expect compile failure.**
- [ ] **Step 3: Implement** — DTOs `internal sealed record` with `[JsonPropertyName]`, `[JsonSerializable(typeof(GitHubReleaseDto))] internal sealed partial class UpdateJsonContext : JsonSerializerContext`; `Parse` catches `JsonException` and returns null.
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** — `Read versions and GitHub releases in the core`.

### Task 3: Layout, replacement scripts, startup arguments, leftovers

**Files:**
- Create: `src/ShowTime.Core/Updates/UpdateLayout.cs`, `src/ShowTime.Core/Updates/ReplacementScript.cs`, `src/ShowTime.Core/Updates/UpdateStartup.cs`
- Test: `tests/ShowTime.Core.Tests/Updates/UpdateLayoutTests.cs`, `.../ReplacementScriptTests.cs`, `.../UpdateStartupTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record UpdateLayout(UpdatePlatform Platform, string AppPath)   // Windows: the exe's folder; macOS: the .app bundle
  {
      public string StagingFolder(SemanticVersion version);   // see Global Constraints
      public string BackupPath { get; }
      public IReadOnlyList<string> Items { get; }              // Windows: ShowTime.exe, LISEZ-MOI.txt, app; macOS: the bundle itself
      public IEnumerable<string> Leftovers(IFileSystem fileSystem); // existing staging folders and backup
  }
  public sealed record ReplacementPlan(int ProcessId, UpdateLayout Layout, string StagedApp, SemanticVersion From, SemanticVersion To,
      string ConfirmFile, string LogFile, TimeSpan ConfirmTimeout, IReadOnlyList<string> LaunchNew, IReadOnlyList<string> LaunchOld)
  {
      public static ReplacementPlan For(int processId, UpdateLayout layout, string stagedApp, SemanticVersion from, SemanticVersion to, string logFile);
      // ConfirmFile = <staging>/confirmed, ConfirmTimeout = 60 s,
      // Windows LaunchNew = [<app>\ShowTime.exe, --update-confirm, <confirm>], LaunchOld = [<app>\ShowTime.exe, --update-failed, X.Y.Z]
      // macOS  LaunchNew = [open, -n, <bundle>, --args, --update-confirm, <confirm>], LaunchOld = [open, -n, <bundle>, --args, --update-failed, X.Y.Z]
  }
  public static class ReplacementScript { public static string ForWindows(ReplacementPlan plan); public static string ForMacOS(ReplacementPlan plan); }
  public sealed record UpdateArguments(string? ConfirmFile, SemanticVersion? FailedVersion)
  {
      public static UpdateArguments Parse(IReadOnlyList<string> args);
  }
  public static class UpdateStartup
  {
      // Writes the confirm file, or warns « La version X.Y.Z n'a pas pu démarrer… », or deletes leftovers (only when no ConfirmFile).
      public static void Run(UpdateArguments arguments, UpdateLayout layout, IFileSystem fileSystem, IUserDialogs dialogs, SemanticVersion current, ILogger log);
  }
  ```
- Script behaviour (both): log every step to `LogFile`; wait for `ProcessId` to exit (poll 200 ms, give up after 30 s → log and exit without touching anything); move each of `Items` into `BackupPath`; move the staged items into place; run `LaunchNew`; wait up to `ConfirmTimeout` for `ConfirmFile`; on success delete `BackupPath` and the staging folder; on any failure or timeout kill the new process (Windows: `Get-Process ShowTime` whose `Path` starts with the app folder; macOS: `pkill -f "<bundle>/Contents/MacOS/ShowTime"`), remove what was moved in, move the backup back, delete the staging folder, run `LaunchOld`. PowerShell: single-quoted literals with `'` doubled; sh: single-quoted with `'\''`.

- [ ] **Step 1: Write the failing tests**
  - `UpdateLayoutTests`: Windows `C:\Tools\ShowTime` → staging `C:\Tools\ShowTime\.update-0.7.0`, backup `C:\Tools\ShowTime\.old`, items as above; macOS `/Applications/ShowTime.app` → `/Applications/.ShowTime-update-0.7.0`, `/Applications/.ShowTime-old.app`; `Leftovers` finds `.update-0.6.9` and `.old` in a `MockFileSystem` and ignores other folders.
  - `ReplacementScriptTests`: each script contains the PID, the 60 s timeout, both launch commands, and the log path; `Scripts_quote_paths_with_spaces_apostrophes_and_accents` with the paths of Review Focus 5 (Windows: `'C:\Users\Zoé\Mes outils\ShowTime d''été'`; sh: `'/Users/zoé/Applications/Show Time.app'` and an apostrophe rendered `'\''`).
  - `UpdateStartupTests`: `--update-confirm <f>` writes `<f>` and keeps leftovers (`Leftovers_are_kept_while_an_update_is_being_confirmed`); `--update-failed 0.7.0` shows the warning with the current version and deletes leftovers; no argument deletes leftovers; `Parse` ignores unknown arguments and a malformed version.
- [ ] **Step 2: Run, expect failures.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** — `Generate the replacement scripts and handle startup after an update`.

### Task 4: HTTP feed and downloader (Infrastructure)

**Files:**
- Create: `src/ShowTime.Core/Ports/IUpdatePorts.cs` (all four update ports, see Interfaces), `src/ShowTime.Infrastructure/Updates/GitHubReleaseFeed.cs`, `src/ShowTime.Infrastructure/Updates/HttpUpdateDownloader.cs`
- Test: `tests/ShowTime.Infrastructure.IntegrationTests/Updates/GitHubReleaseFeedTests.cs`, `.../Updates/HttpUpdateDownloaderTests.cs` (a stub `HttpMessageHandler`, no real network)

**Interfaces:**
- Produces (in `IUpdatePorts.cs`):
  ```csharp
  public interface IReleaseFeed { Task<string> GetLatestAsync(CancellationToken cancellationToken); }
  public interface IUpdateDownloader { Task DownloadAsync(Uri url, string destinationFile, IProgress<double> progress, CancellationToken cancellationToken); }
  public interface IUpdateInstaller
  {
      UpdatePlatform Platform { get; }
      UpdateLayout Layout { get; }                 // Task 3
      bool CanReplaceApp();                        // packaged install in a writable folder
      Task<string> StageAsync(string zipFile, string stagingFolder, CancellationToken cancellationToken); // returns the staged app; throws UpdateException
      void StartReplacement(ReplacementPlan plan); // Task 3; detached; the app quits right after
  }
  public interface IUpdateHost
  {
      bool TryCloseWindows();   // editors and players, asking about unsaved work; false when the user cancelled
      void Quit();
      void OpenInBrowser(Uri url);
  }
  public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);
  ```
- Produces: `GitHubReleaseFeed(HttpMessageHandler handler, SemanticVersion current) : IReleaseFeed, IDisposable` (10 s timeout, headers from Global Constraints, non-success status → `HttpRequestException`); `HttpUpdateDownloader(HttpMessageHandler handler) : IUpdateDownloader, IDisposable` (no timeout, `ResponseHeadersRead`, progress 0..1 from `Content-Length`, deletes the partial file on any failure or cancellation).

- [ ] **Step 1: Write the failing tests**
  - `The_feed_asks_GitHub_for_the_latest_release`: stub records the request; assert URL = `GitHubRelease.LatestUrl`, `User-Agent` = `ShowTime/0.6.3`, `Accept` = `application/vnd.github+json`; returns the body.
  - `The_feed_throws_on_403`: `HttpRequestException`.
  - `The_download_writes_the_file_and_reports_progress`: 1 MiB body in 64 KiB chunks; file bytes equal; last progress `1.0`, values increasing.
  - `A_cancelled_download_leaves_no_file` and `A_failed_download_leaves_no_file` (stub throws mid-stream).
- [ ] **Step 2: Run, expect compile failure.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** — `Fetch releases and downloads over HTTP`.

### Task 5: Checking for an update (`Updater`, check half) and the menu entry

**Files:**
- Create: `src/ShowTime.Core/Updates/Updater.cs`, `src/ShowTime.Core/Updates/UpdateState.cs`, `src/ShowTime.Core/Updates/UpdateMenuEntry.cs`
- Modify: `tests/ShowTime.Core.Tests/Fakes.cs` (add `FakeReleaseFeed`, `FakeUpdateDownloader`, `FakeUpdateInstaller`, `FakeUpdateHost`)
- Test: `tests/ShowTime.Core.Tests/Updates/UpdaterCheckTests.cs`, `.../Updates/UpdateMenuEntryTests.cs`

**Interfaces:**
- Consumes: Task 1 setting, Task 2 types, Task 4 ports.
- Produces:
  ```csharp
  public enum UpdateStage { Idle, Checking, Available, Downloading, WaitingForRecording, Downloaded, Installing }
  public sealed record UpdateState(UpdateStage Stage, ReleaseInfo? Release, double Progress);
  public sealed class Updater(AppSettings settings, IReleaseFeed feed, IUpdateDownloader downloader, IUpdateInstaller installer,
      IUpdateHost host, IUserDialogs dialogs, IRecordingStatus recording, IUiThread uiThread, IFileSystem fileSystem, IAppPaths paths,
      SemanticVersion current, TimeProvider clock, ILogger<Updater> log) : IDisposable
  {
      public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5), Interval = TimeSpan.FromHours(6), Freshness = TimeSpan.FromMinutes(15);
      public UpdateState State { get; }
      public event EventHandler? Changed;      // raised on the UI thread
      public void Start();                     // schedules startup and periodic checks (skipped while !settings.CheckForUpdates)
      public void MenuOpening();               // checks when automatic and the last success is older than Freshness
      public Task CheckNowAsync();             // the manual item: reports « à jour » or the error by dialog
      public Task InstallAsync();              // Task 6
  }
  public sealed record UpdateMenuEntry(bool Visible, string Text, bool Enabled)
  {
      public static UpdateMenuEntry For(UpdateState state, bool automatic);
      public static string? ToolTip(UpdateState state);   // « ShowTime — version X.Y.Z disponible » from Available to Installing
  }
  ```
- Timers through `clock.CreateTimer`, callbacks through `uiThread.Post`.

- [ ] **Step 1: Write the failing tests** (`FakeTimeProvider`, `ImmediateUiThread`, `FakeReleaseFeed` returning the v0.6.3 fixture or throwing)
  - `The_first_check_runs_5_seconds_after_start` (none at 4.9 s, one at 5 s) and `Checks_repeat_every_6_hours`.
  - `A_newer_release_becomes_available` (current 0.6.2 → `Available`, release 0.6.3, `Changed` raised).
  - `An_older_or_equal_release_offers_nothing` (current 0.6.3 and 0.8.0 → `Idle`).
  - `Opening_the_menu_checks_only_when_the_answer_is_older_than_15_minutes` (14 min: no call; 15 min: call).
  - `Only_one_check_runs_at_a_time` (feed held by a `TaskCompletionSource`; `MenuOpening` twice → one call).
  - `An_automatic_check_that_fails_shows_nothing_and_retries_on_next_menu_opening` (feed throws `HttpRequestException` → `Idle`, no dialog, a log line; next `MenuOpening` calls again even within 15 min).
  - `Nothing_is_checked_automatically_when_the_setting_is_off` (no call at startup, after 6 h, on menu opening).
  - `A_manual_check_says_when_ShowTime_is_up_to_date` (`dialogs.ShowInfo("ShowTime est à jour (version 0.6.3).")`) and `A_manual_check_reports_its_failure` (`ShowError` starting « Impossible de vérifier les mises à jour : »).
  - `UpdateMenuEntryTests`: one test per row — automatic+Idle/Checking → hidden; manual+Idle → « Rechercher une mise à jour » enabled; manual+Checking → « Recherche d'une mise à jour… » disabled; Available → « Installer la version 0.6.3 » enabled; Downloading at 0.42 → « Téléchargement de la version 0.6.3… 42 % » disabled; WaitingForRecording → « Installation après l'enregistrement… » disabled; Downloaded → « Installer la version 0.6.3 (téléchargée) » enabled; Installing → « Installation de la version 0.6.3… » disabled; `ToolTip` null when Idle.
- [ ] **Step 2: Run, expect failures.**
- [ ] **Step 3: Implement** the check half of `Updater` and `UpdateMenuEntry`; `InstallAsync` throws `NotImplementedException` until Task 6.
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** — `Check for a newer version in the core`.

### Task 6: Installing (`Updater.InstallAsync`) and the download check

**Files:**
- Create: `src/ShowTime.Core/Updates/DownloadCheck.cs`
- Modify: `src/ShowTime.Core/Updates/Updater.cs`
- Test: `tests/ShowTime.Core.Tests/Updates/DownloadCheckTests.cs`, `.../Updates/UpdaterInstallTests.cs`

**Interfaces:**
- Consumes: Task 5; `UpdateLayout` and `ReplacementPlan.For(Environment.ProcessId, installer.Layout, stagedApp, current, release.Version, Path.Combine(paths.LogsFolder, "update.log"))` from Task 3.
- Produces: `public static class DownloadCheck { public static bool Matches(IFileSystem fileSystem, string file, long size, string sha256); }`

Flow of `InstallAsync` (only from `Available` or `Downloaded`; otherwise returns):
1. `!installer.CanReplaceApp()` → `dialogs.ShowInfo(« ShowTime ne peut pas se remplacer… »)`, `host.OpenInBrowser(release.PageUrl)`, stay `Available`.
2. From `Available`: `Downloading`; download to `<staging>/ShowTime.zip` with progress; `DownloadCheck.Matches` else `UpdateException("le fichier téléchargé est incomplet ou altéré")`; `installer.StageAsync`; delete the zip. Any failure: `ShowError(« La mise à jour n'a pas pu être téléchargée : » + message)`, staging folder deleted, back to `Available`.
3. While `recording.IsBusy`: `WaitingForRecording`, resume on `recording.StateChanged`.
4. `host.TryCloseWindows()` false → `Downloaded` (staged app kept). True → `Installing`, `installer.StartReplacement(plan)`, `host.Quit()`.

- [ ] **Step 1: Write the failing tests**
  - `DownloadCheckTests`: matching file → true; one byte changed → false; size differs → false; digest compared case-insensitively.
  - `Install_downloads_stages_replaces_and_quits` (call order on fakes: download, stage, `TryCloseWindows`, `StartReplacement`, `Quit`; states Downloading → Installing).
  - `Download_progress_is_reported` (fake reports 0.5 → `State.Progress == 0.5`).
  - `A_corrupt_download_is_refused` (digest mismatch → `ShowError`, staging deleted, `Available`, no `StartReplacement`).
  - `A_failed_staging_is_reported` (`UpdateException("signature")` → message contains it).
  - `An_unwritable_install_opens_the_release_page`.
  - `Install_waits_for_the_recording_to_end` (`FakeRecordingStatus.Set(true)` → `WaitingForRecording`; `Set(false)` → `Installing`).
  - `Cancelling_an_unsaved_editor_keeps_the_download` (`TryCloseWindows` false → `Downloaded`; second `InstallAsync` does not download again).
  - `A_second_install_click_while_downloading_does_nothing` (one download call).
  - `Unticking_the_setting_does_not_stop_a_download`.
  - `Dispose_cancels_the_download_and_deletes_the_staging_folder`.
- [ ] **Step 2: Run, expect failures.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — PASS, and core coverage ≥ 90 % (`dotnet test --project tests/ShowTime.Core.Tests --coverage --coverage-output-format cobertura --coverage-output core.cobertura.xml` then `python build/check-coverage.py TestResults/core.cobertura.xml 90`).
- [ ] **Step 5: Commit** — `Download, check and install an update in the core`.

### Task 7: Running the scripts for real, and the Windows installer

**Files:**
- Create: `src/ShowTime.Infrastructure/Updates/ScriptLauncher.cs`, `src/ShowTime.Infrastructure/Updates/WindowsUpdateInstaller.cs`
- Test: `tests/ShowTime.Infrastructure.IntegrationTests/Updates/ReplacementScriptRunTests.cs`, `.../Updates/WindowsUpdateInstallerTests.cs`

**Interfaces:**
- Consumes: Task 3.
- Produces:
  - `public static class ScriptLauncher { public static Process StartDetached(UpdatePlatform platform, string scriptFile); }` — Windows: `powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File <script>`, `CreateNoWindow`; macOS: `/usr/bin/nohup /bin/sh <script>`, output to `/dev/null`.
  - `public sealed class WindowsUpdateInstaller(IAppPaths paths, IFileSystem fileSystem, string appFolder) : IUpdateInstaller` — `CanReplaceApp`: `app\ffmpeg.exe` exists and a probe file can be created and deleted in `appFolder`; `StageAsync`: `ZipFile.ExtractToDirectory` into `<staging>\extracted`, strip the single top-level folder, require `ShowTime.exe`, else `UpdateException("l'archive ne contient pas ShowTime.exe")`; `StartReplacement`: writes `ReplacementScript.ForWindows` to `<WorkFolder>\update.ps1` (UTF-8 with BOM, so that PowerShell 5.1 reads accents) and starts it.

- [ ] **Step 1: Write the failing tests** (real temp folders, `TempFolder.cs`)
  - `ReplacementScriptRunTests` (Windows uses PowerShell, others `/bin/sh`; the "old process" is a short-lived process such as `ping -n 3 127.0.0.1` / `sleep 2`; `LaunchNew` is a shell one-liner writing the confirm file; an install folder named `Show Time d'été`):
    - `A_confirmed_update_replaces_the_app_and_deletes_the_old_one` (new marker file present, backup and staging gone, log written).
    - `An_unconfirmed_update_restores_the_old_version` (`LaunchNew` does nothing, timeout 3 s → old marker back, staging gone, `LaunchOld` ran: it writes a file the test checks).
    - `User_files_in_the_install_folder_are_kept` (a `notes.txt` beside `ShowTime.exe` survives).
  - `WindowsUpdateInstallerTests`: a zip built in the test with `ShowTime\ShowTime.exe`, `ShowTime\app\ffmpeg.exe` → staged path contains `ShowTime.exe` at its root; a zip without it → `UpdateException`; `CanReplaceApp` false without `app\ffmpeg.exe` (a development build) and true with it.
- [ ] **Step 2: Run, expect failures.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — PASS on the three CI runners (Windows, macOS, Ubuntu).
- [ ] **Step 5: Commit** — `Run the replacement scripts and install on Windows`.

### Task 8: Windows app

**Files:**
- Modify: `src/ShowTime.Windows/App.xaml.cs` (composition, startup arguments), `src/ShowTime.Windows/Tray/TrayController.cs`, `src/ShowTime.Windows/Tray/TrayIcon.cs`, `src/ShowTime.Windows/Settings/SettingsWindow.xaml(.cs)`
- Create: `src/ShowTime.Windows/Adapters/WpfUpdateHost.cs`
- Test: `tests/ShowTime.Windows.Tests` — the settings window round-trips the new checkbox (existing settings-window test pattern).

**Interfaces:**
- Consumes: `Updater`, `UpdateMenuEntry`, `UpdateStartup`, `WindowsUpdateInstaller`, `GitHubReleaseFeed`, `HttpUpdateDownloader`.
- `WpfUpdateHost : IUpdateHost` — `TryCloseWindows` closes every `Window` like the end of `TrayController.Quit` (stop and return false on the first that stays open); `Quit` = `Application.Current.Shutdown()`; `OpenInBrowser` = `Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })`.

- [ ] **Step 1: Write the failing Windows test** for the checkbox (`CheckForUpdatesBox`, « Rechercher automatiquement les mises à jour », next to `StartupBox`).
- [ ] **Step 2: Wire it**
  - Composition: `SemanticVersion.From(typeof(App).Assembly.GetName().Version!)`, `UpdateLayout(UpdatePlatform.WindowsX64, AppContext.BaseDirectory)`, `new SocketsHttpHandler()` for both HTTP classes, `Updater` singleton.
  - `OnStartup`: parse `UpdateArguments` from `e.Args`; when `ConfirmFile` is set, a mutex still held by the old process is retried for up to 10 s before giving up; after the `TrayController`, `UpdateStartup.Run(...)`, then `updater.Start()`.
  - `TrayController`: an `_updateItem` first in the menu, followed by a separator shown with it; `tray.Menu.Opening += (_, _) => _updater.MenuOpening()`; on `_updater.Changed` (and on settings saved) apply `UpdateMenuEntry.For(_updater.State, _settings.CheckForUpdates)` to the item and `UpdateMenuEntry.ToolTip` to the tray text, keeping « ShowTime — enregistrement en cours » while recording. A click calls `InstallAsync` when Available/Downloaded, else `CheckNowAsync`. Adding the item while the strip is open works with `ContextMenuStrip` (`Items` changes relayout it).
- [ ] **Step 3: Run CI** (`gh workflow run CI --ref in-app-update`) — windows, package-windows jobs PASS.
- [ ] **Step 4: Commit** — `Offer and install updates from the Windows tray`.

### Task 9: macOS app

**Files:**
- Create: `src/ShowTime.Platform.MacOS/CodeSignature.cs`, `src/ShowTime.Mac/Platform/MacUpdateInstaller.cs`, `src/ShowTime.Mac/Adapters/MacUpdateHost.cs`
- Modify: `src/ShowTime.Mac/Composition.cs`, `src/ShowTime.Mac/Main.cs`, `src/ShowTime.Mac/AppDelegate.cs`, `src/ShowTime.Mac/StatusMenu.cs`, `src/ShowTime.Mac/Settings/SettingsWindowController.cs`
- Test: `tests/ShowTime.Platform.Tests/MacOS/CodeSignatureTests.cs` (pure parsing, runs on every OS)

**Interfaces:**
- Produces: `public static class CodeSignature { public static bool IsAdHoc(string codesignDisplayOutput); public static string? DesignatedRequirement(string codesignRequirementOutput); }` — `IsAdHoc` looks for the line `Signature=adhoc` in `codesign -dv --verbose=2` output; `DesignatedRequirement` returns the text after `designated => ` in `codesign -d -r-` output.
- `MacUpdateInstaller : IUpdateInstaller` — `Layout` = `UpdateLayout(UpdatePlatform.MacOS, NSBundle.MainBundle.BundlePath)`; `CanReplaceApp`: `Contents/MacOS/ffmpeg` exists and a probe file can be created in the bundle's parent and in the bundle; `StageAsync`: `ditto -x -k <zip> <staging>/extracted`, require `ShowTime.app`, `xattr -dr com.apple.quarantine`, then: running app ad hoc → new must verify with `codesign --verify --deep --strict` and be ad hoc; otherwise `codesign --verify --deep --strict -R=<designated requirement of the running app> <new>`; a failure throws `UpdateException("la signature de la nouvelle version ne correspond pas")`. `StartReplacement` writes `ReplacementScript.ForMacOS` to `<WorkFolder>/update.sh` and starts it with `ScriptLauncher`.
- `MacUpdateHost : IUpdateHost` — `TryCloseWindows` = `_editor.CloseAll()` (the video session is already idle when called); `Quit` sets `StatusMenu.QuitApproved = true` then `NSApplication.SharedApplication.Terminate`, and `ShouldTerminate` returns `Now` at once when `QuitApproved`; `OpenInBrowser` = `NSWorkspace.SharedWorkspace.OpenUrl`.

- [ ] **Step 1: Write the failing tests** — `CodeSignatureTests`: real outputs captured from v0.6.3 (signed `LocalTake`) and an ad hoc build, as string literals; `DesignatedRequirement` of a signed output is the `identifier "…" and certificate leaf = H"…"` line; null when absent; `IsAdHoc` true/false.
- [ ] **Step 2: Implement `CodeSignature`, run** `tests/ShowTime.Platform.Tests` through CI — PASS.
- [ ] **Step 3: Wire the app** — `Main`: parse `UpdateArguments`; when `ConfirmFile` is set, the single-instance check waits up to 10 s for the old process to leave; `AppDelegate.DidFinishLaunching` runs `UpdateStartup.Run` after building `StatusMenu`, then `updater.Start()`. `StatusMenu`: `_updateItem` under the « ShowTime X.Y.Z » line, with a separator; the `NSMenu` gets a delegate whose `MenuWillOpen` calls `MenuOpening`; `Changed` updates the item (an open `NSMenu` redraws a changed item) and the button's `ToolTip`. Settings: `_checkForUpdates` checkbox under `_startup`.
- [ ] **Step 4: Run CI** — macos-app job PASS (it builds and trims the app; a trim warning fails it).
- [ ] **Step 5: Commit** — `Offer and install updates from the macOS menu bar`.

### Task 10: Documentation

**Files:**
- Modify: `CLAUDE.md`, `README.md`, `docs/test-checklist.md`

- [ ] **Step 1: CLAUDE.md** — replace the opening paragraph's « Captures must never leave the machine: no network calls, no upload, no telemetry » with the local-first rule: captures, videos, subtitles, settings and the log stay on the machine; network calls are allowed when they improve the experience, and **every network call is listed in README's « Réseau » section, which must be updated in the same change as the code**. Keep the reasons for the OneDrive-free folder and the shipped ffmpeg / Whisper model. Add `Updates/` to the core's architecture bullet and a short « Updates » subsection (schedule, ports, scripts, confirmation, layout, `--update-confirm` / `--update-failed`).
- [ ] **Step 2: README.md** — new section « Réseau »: what goes out (the check to `api.github.com`: 5 s after startup, every 6 h, on opening the menu at most every 15 min, carrying only the IP address and `User-Agent: ShowTime/X.Y.Z`; the download from `github.com` / `objects.githubusercontent.com` on click), what never goes out (captures, videos, subtitles, settings, log), how to turn the check off, and what happens to a transcription under way when an update installs (as when quitting: say what `Transcriptions` does; check it in the code).
- [ ] **Step 3: test-checklist.md** — Windows and macOS lines: item appears with a newer release (test with a build whose `<Version>` is lowered, e.g. `-p:Version=0.0.1`); progress in the menu; install waits for a recording; unsaved editor question and resume without re-download; relaunch and old version gone; settings, login item and (macOS) Screen Recording / Microphone permissions kept; read-only folder opens the release page; offline: nothing shown; checkbox off: « Rechercher une mise à jour ».
- [ ] **Step 4: Commit** — `Document the network calls and the in-app update`.
- [ ] **Step 5: Open the PR** to `main`, wait for green CI, merge with `gh pr merge --merge --delete-branch`. The release (MINOR bump to 0.7.0, a new feature) is the user's call.
