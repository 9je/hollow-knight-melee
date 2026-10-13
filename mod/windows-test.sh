#!/bin/bash
# The Windows installer, end to end on real Windows (winvm: ~/tools/winvm, a local throwaway VM; it is shut down after).
# Usage: mod/windows-test.sh [installer.exe]   (default: the latest release's). Results in build/windows-test/.
# Puts into the VM, only for this run: the installer, the game's code (mod/lib/vanilla) and your ISO (dev.toml's iso).
set -euo pipefail
cd "$(dirname "$0")/.."
command -v winvm >/dev/null || { echo "winvm isn't installed (see ~/tools/winvm)"; exit 1; }
iso=$(python3 -c "import tomllib;print(tomllib.load(open('dev.toml','rb'))['iso'])")
[ -f "$iso" ] || { echo "no ISO at $iso (dev.toml)"; exit 1; }
[ -f mod/lib/vanilla/Assembly-CSharp.dll ] || { echo "mod/lib/vanilla is missing: bash mod/fetch_refs.sh"; exit 1; }
work=$(mktemp -d); trap 'rm -rf "$work"' EXIT
exe=${1:-}
if [ -z "$exe" ]; then
    tag=$(gh release list -L 1 --json tagName -q '.[0].tagName')
    echo "installer: release $tag"
    gh release download "$tag" -p HollowKnightMeleeInstaller.exe -D "$work"
    exe=$work/HollowKnightMeleeInstaller.exe
fi
out=build/windows-test
rm -rf "$out"; mkdir -p build
winvm run mod/windows-test.ps1 --timeout 90 \
    --in "$exe:C:/winvm/HollowKnightMeleeInstaller.exe" \
    --in "mod/lib/vanilla:C:/winvm/Managed" \
    --in "$iso:C:/winvm/melee.iso" \
    --out "C:/winvm/results:$out" && rc=0 || rc=$?
echo; cat "$out/checks.txt" 2>/dev/null || echo "(no results came back)"
exit $rc
