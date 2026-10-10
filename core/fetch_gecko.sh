#!/bin/bash
# Fetch Slippi's netplay Gecko INI (pinned commit), verify it, and keep only the gameplay codes listed in
# runtime/slippi_gameplay.txt. Not committed: see plan 1c.
set -euo pipefail
out="$1"
url="https://raw.githubusercontent.com/project-slippi/Ishiiruka/60f7b63496fb6ec7b9180a04f16f3edc0ad89fe2/Data/Sys/GameSettings/GALE01r2.ini"
sum="b30b294df5c0d92deb3129afdfbc894a48aabeb1bce0cf92cce8c4e5408a2587"
mkdir -p "$(dirname "$out")"
curl -sSfL "$url" -o "$out.ini"
echo "$sum  $out.ini" | sha256sum -c --quiet
../tools/.venv/bin/python -m ssbmhk_tools.recomp.gecko "$out.ini" runtime/slippi_gameplay.txt "$out.tmp"
mv "$out.tmp" "$out"
