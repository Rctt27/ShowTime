# Runs the packaged ShowTime.exe --diagnostics and requires that it uses the ffmpeg and the Whisper model of its app
# folder, and that Whisper's native library loads from there. The runner has no microphone, so the report fails
# elsewhere: only these two lines count.
#
# Usage: check-windows-package.ps1 <folder holding ShowTime.exe>
param([Parameter(Mandatory)][string]$Folder)
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path $Folder).Path
$app = Join-Path $root 'app'
$report = Join-Path $env:LOCALAPPDATA 'ShowTime\logs\diagnostics.txt'
Remove-Item $report -ErrorAction SilentlyContinue

$process = Start-Process (Join-Path $root 'ShowTime.exe') -ArgumentList '--diagnostics' -PassThru
if (-not $process.WaitForExit(300000)) {
    $process.Kill()
    throw 'ShowTime.exe --diagnostics did not finish within 5 minutes'
}
$text = Get-Content $report -Raw
Write-Output $text

$expected = @("[ok]   ffmpeg: $app\ffmpeg.exe", "[ok]   transcription: $app\models\")
$missing = $expected | Where-Object { -not $text.Contains($_) }
if ($missing) { throw "The packaged ShowTime does not use its app folder; missing: $($missing -join ' | ')" }
