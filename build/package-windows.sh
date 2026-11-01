#!/usr/bin/env bash
# Builds the folder of the Windows zip: ShowTime.exe and LISEZ-MOI.txt at its root, so that ShowTime.exe is the one
# thing to open, and everything it needs in app (AppPaths.WindowsShippedFolder): ffmpeg.exe, Whisper's native
# libraries in runtimes\win-x64, the model in models, and the licence notices. Run from the repository's root.
#
# Usage: package-windows.sh <dest-dir>
set -euo pipefail

if [ $# -ne 1 ]; then
  echo "usage: $0 <dest-dir>" >&2
  exit 2
fi
dest=$1
app="$dest/app"
rm -rf "$dest"

dotnet publish src/ShowTime.Windows -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o "$app"
mv "$app/ShowTime.exe" "$dest/"
cp build/LISEZ-MOI.txt "$dest/"
# ShowTime itself never downloads anything: ffmpeg and the model are pinned and checked by these scripts.
bash build/fetch-ffmpeg.sh windows "$app"
bash build/fetch-whisper-model.sh "$app/models"
cp THIRD-PARTY-NOTICES.md "$app/"
cp -r licenses "$app/"

test -f "$dest/ShowTime.exe" && test -f "$app/ffmpeg.exe" && test -f "$app/runtimes/win-x64/whisper.dll" &&
  test -f "$app/licenses/GPL-3.0.txt" && ls "$app"/models/*.bin > /dev/null
# Nothing else at the root: a file publish adds there would make ShowTime.exe harder to find.
extra=$(ls -A "$dest" | grep -vxE 'ShowTime\.exe|LISEZ-MOI\.txt|app' || true)
if [ -n "$extra" ]; then
  echo "unexpected at the root of $dest: $extra" >&2
  exit 1
fi
