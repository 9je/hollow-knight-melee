#!/bin/bash
# For building without the game (CI): the Modding API (mod/lib/api, as fetch_api.sh) and the vanilla game's reference
# assemblies (mod/lib/vanilla: the archive HKBuildUtils 0.6.1 uses), both pinned by SHA-256. Not committed (game code).
# Build with: dotnet build mod/FoxMod -p:HKManaged=$PWD/mod/lib/vanilla
set -euo pipefail
cd "$(dirname "$0")"
[ -f lib/api/Assembly-CSharp.dll ] || bash fetch_api.sh
[ -f lib/vanilla/UnityEngine.CoreModule.dll ] && exit 0   # already here (CI's cache)
url="https://files.catbox.moe/i4sdl6.zip"
sum="15dbd6a5668c722c9b1e585b944756063a7f4b41f9c0e161a3c7649f1d3ab08e"
mkdir -p lib
curl -sSfL --retry 4 "$url" -o lib/vanilla.zip
echo "$sum  lib/vanilla.zip" | sha256sum -c --quiet
rm -rf lib/vanilla && mkdir -p lib/vanilla && unzip -q -o lib/vanilla.zip -d lib/vanilla && rm lib/vanilla.zip
