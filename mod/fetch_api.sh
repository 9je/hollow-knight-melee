#!/bin/bash
# Fetch the Hollow Knight Modding API (pinned) into mod/lib/api. Not committed (contains game code).
set -euo pipefail
cd "$(dirname "$0")"
url="https://github.com/hk-modding/api/releases/download/1.5.78.11833-77/moddingapi.v77.linux.zip"
sum="5d2327d76b2c48e2694e43e90689d867eaa9ac1d9e6fd2998c93087de7b61ddb"
mkdir -p lib
curl -sSfL "$url" -o lib/api.zip
echo "$sum  lib/api.zip" | sha256sum -c --quiet
rm -rf lib/api && mkdir -p lib/api && unzip -q -o lib/api.zip -d lib/api && rm lib/api.zip
