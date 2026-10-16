#!/bin/bash
# Install the Modding API and FoxMelee into Hollow Knight (1.5.78.11833, native Linux). Backs up the game's
# Managed folder and your saves first (each once). usage: mod/install.sh [HK folder]
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
hk="${1:-$HOME/.steam/steam/steamapps/common/Hollow Knight}"
managed="$hk/hollow_knight_Data/Managed"
[ -f "$managed/Assembly-CSharp.dll" ] || { echo "not a Hollow Knight folder: $hk" >&2; exit 1; }
# the running game has the core loaded: copying over it crashes the game
if pgrep -f 'hollow_knight\.x86_64' >/dev/null; then echo "Hollow Knight is running: quit it first (installing over the loaded mod crashes it)" >&2; exit 1; fi
saves="${HK_SAVES:-$HOME/.config/unity3d/Team Cherry/Hollow Knight}"   # overridable for dry runs
backups="${BACKUP_DIR:-$HOME/Documents}"
backup="$backups/hollow-knight-save-backup"   # one backup, made once; kept as long as it exists
if [ -d "$saves" ] && [ ! -d "$backup" ]; then cp -a "$saves" "$backup"; echo "saves backed up to $backup"; fi
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
# Fox's model from your ISO (never fatal: without it the mod draws a placeholder)
venv="${TOOLS_VENV:-$repo/tools/.venv}"
if [ ! -x "$venv/bin/ssbmhk-extract-fox" ] || [ ! -x "$venv/bin/ssbmhk-extract-sfx" ] || [ ! -x "$venv/bin/ssbmhk-extract-stocks" ] || [ ! -x "$venv/bin/ssbmhk-extract-css" ]; then
  { [ -x "$venv/bin/python" ] || python3 -m venv "$venv"; } && "$venv/bin/pip" install -q -e "$repo/tools" \
    || echo "could not set up the tools environment ($venv)"
fi
if [ -n "$iso" ] && [ -f "$iso" ] && [ -x "$venv/bin/ssbmhk-extract-fox" ]; then
  "$venv/bin/ssbmhk-extract-fox" "$iso" --out "$dest" || echo "fox.model not extracted: the mod draws a placeholder"
else
  echo "fox.model not extracted (no ISO in dev.toml or no tools environment): the mod draws a placeholder"
fi
# Melee's sound effects from your ISO (never fatal: without them the mod is silent)
if [ -n "$iso" ] && [ -f "$iso" ] && [ -x "$venv/bin/ssbmhk-extract-sfx" ]; then
  "$venv/bin/ssbmhk-extract-sfx" "$iso" --out "$dest" || echo "Melee sounds not extracted: the character is silent"
else
  echo "Melee sounds not extracted (no ISO in dev.toml or no tools environment): the character is silent"
fi
# Melee's stock icons from your ISO (the character choice and Iselda's Marth; never fatal)
if [ -n "$iso" ] && [ -f "$iso" ] && [ -x "$venv/bin/ssbmhk-extract-stocks" ]; then
  "$venv/bin/ssbmhk-extract-stocks" "$iso" --out "$dest" || echo "stocks.bin not extracted: characters are shown by name only"
fi
# Melee's character select (its tiles, every costume's portrait and stock icon) from your ISO; never fatal. Sheik has none
# in Melee: the core renders her in each costume and once facing the camera (frame_dump's raster) and the select gets a
# tile and portraits made of the renders, her name in the other tiles' lettering (as the Windows installer does).
if [ -n "$iso" ] && [ -f "$iso" ] && [ -x "$venv/bin/ssbmhk-extract-css" ]; then
  sheik=()
  tmp=$(mktemp -d)
  for k in 0 1 2 3 4; do
    if make -s -C "$repo/core" frame-dump CHAR=19 SCRIPT=.......... OUT="$tmp/frame.bin" COSTUME=$k RASTER="$tmp/sheik$k.rgba" >/dev/null 2>&1; then sheik+=("$tmp/sheik$k.rgba"); else break; fi
  done
  make -s -C "$repo/core" frame-dump CHAR=19 SCRIPT=l.. OUT="$tmp/frame.bin" COSTUME=0 RASTER="$tmp/facing.rgba" >/dev/null 2>&1 || true
  [ "${#sheik[@]}" -eq 5 ] && [ -f "$tmp/facing.rgba" ] || { echo "Sheik not rendered for the select: she gets Zelda's portrait"; sheik=(); }
  if [ "${#sheik[@]}" -gt 0 ]; then
    "$venv/bin/ssbmhk-extract-css" "$iso" --out "$dest" --sheik-frames "${sheik[@]}" --sheik-head "$tmp/facing.rgba" || echo "css.bin not extracted: the character select shows stock icons only"
  else
    "$venv/bin/ssbmhk-extract-css" "$iso" --out "$dest" || echo "css.bin not extracted: the character select shows stock icons only"
  fi
  rm -rf "$tmp"
fi
settings="$saves/FoxMod.GlobalSettings.json"
if [ -n "$iso" ]; then   # set IsoPath when it is missing or empty; keep every other setting
  mkdir -p "$saves"
  python3 - "$settings" "$iso" <<'PY'
import json, os, sys
path, iso = sys.argv[1], sys.argv[2]
s = json.load(open(path)) if os.path.exists(path) else {}
if not s.get("IsoPath"):
    s["IsoPath"] = iso
    json.dump(s, open(path, "w"), indent=2)
    print("set IsoPath in", path)
PY
fi
echo "installed FoxMelee into $dest"
