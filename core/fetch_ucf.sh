#!/bin/bash
# Fetch Slippi's UCF 0.84 Gecko code list (pinned commit) and verify it. Not committed: see plan 1c.
set -euo pipefail
out="$1"
url="https://raw.githubusercontent.com/project-slippi/slippi-ssbm-asm/fcf47f10dc244152c2ebaa3a9dec142ea42243b7/Output/Console/g_ucf_084.txt"
sum="334897313f77302fa08f6bc35aee114fc5ecca0159213a3e10b17f28854af608"
mkdir -p "$(dirname "$out")"
curl -sSfL "$url" -o "$out.tmp"
echo "$sum  $out.tmp" | sha256sum -c --quiet
mv "$out.tmp" "$out"
