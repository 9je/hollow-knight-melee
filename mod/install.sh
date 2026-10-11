#!/bin/bash
# Install the Modding API and FoxMelee into Hollow Knight (1.5.78.11833, native Linux). Backs up the game's
# Managed folder (once) and your saves (every run) first. usage: mod/install.sh [HK folder]
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
hk="${1:-$HOME/.steam/steam/steamapps/common/Hollow Knight}"
managed="$hk/hollow_knight_Data/Managed"
[ -f "$managed/Assembly-CSharp.dll" ] || { echo "not a Hollow Knight folder: $hk" >&2; exit 1; }
saves="${HK_SAVES:-$HOME/.config/unity3d/Team Cherry/Hollow Knight}"   # overridable for dry runs
backups="${BACKUP_DIR:-$HOME/Documents}"
stamp="$(date +%Y%m%d-%H%M%S)"
if [ -d "$saves" ]; then cp -a "$saves" "$backups/hollow-knight-save-backup-$stamp"; echo "saves backed up to $backups/hollow-knight-save-backup-$stamp"; fi
[ -d "$managed.vanilla" ] || { cp -a "$managed" "$managed.vanilla"; echo "vanilla Managed backed up to $managed.vanilla"; }
[ -f "$repo/mod/lib/api/Assembly-CSharp.dll" ] || bash "$repo/mod/fetch_api.sh"
make -s -C "$repo/core" shared
(cd "$repo/mod" && ~/.dotnet/dotnet build FoxMod -c Release -nologo -v q)
cp -f "$repo"/mod/lib/api/* "$managed/"
dest="$managed/Mods/FoxMelee"
mkdir -p "$dest"
out="$repo/mod/FoxMod/bin/Release/net472"
cp -f "$out/FoxMod.dll" "$out/FoxCore.dll" "$out/Clipper2Lib.dll" "$dest/"
cp -f "$repo/core/build/libmelee_core.so" "$repo/core/build/gen-core/mem1.bin" "$dest/"
iso="$(python3 -c "import tomllib;print(tomllib.load(open('$repo/dev.toml','rb'))['iso'])" 2>/dev/null || true)"
settings="$saves/FoxMelee.GlobalSettings.json"
if [ -n "$iso" ] && [ ! -f "$settings" ]; then
  mkdir -p "$saves"
  printf '{\n  "IsoPath": "%s",\n  "WorldScale": 6.0,\n  "CameraMargin": 40.0,\n  "ShowPlaceholder": true,\n  "KeyboardFallback": true\n}\n' "$iso" > "$settings"
  echo "wrote $settings"
fi
echo "installed FoxMelee into $dest"
