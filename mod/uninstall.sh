#!/bin/bash
# Restore the vanilla Managed folder saved by install.sh. usage: mod/uninstall.sh [HK folder]
set -euo pipefail
hk="${1:-$HOME/.steam/steam/steamapps/common/Hollow Knight}"
managed="$hk/hollow_knight_Data/Managed"
[ -d "$managed.vanilla" ] || { echo "no backup at $managed.vanilla" >&2; exit 1; }
rm -rf "$managed" && mv "$managed.vanilla" "$managed"
echo "restored vanilla Hollow Knight (saves untouched; backups are in ~/Documents)"
