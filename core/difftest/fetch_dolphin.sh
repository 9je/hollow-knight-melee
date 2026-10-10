#!/bin/bash
# Sparse clone of Dolphin's interpreter sources at the pinned commit (GPL-2.0-or-later, not committed).
set -euo pipefail
cd "$(dirname "$0")"
REV=e6f3ae17627e4344da95b13424af5baf4c892b08
[ -d dolphin-src/.git ] && [ "$(git -C dolphin-src rev-parse HEAD)" = "$REV" ] && exit 0
rm -rf dolphin-src
git clone --filter=blob:none --no-checkout https://github.com/dolphin-emu/dolphin.git dolphin-src
git -C dolphin-src sparse-checkout set Source/Core/Common Source/Core/Core/PowerPC
git -C dolphin-src checkout -q "$REV"
