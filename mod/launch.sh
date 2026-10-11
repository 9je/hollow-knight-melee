#!/bin/bash
# Launch Hollow Knight for a FoxMelee test without friends seeing it: back up the saves, set Steam to
# Invisible, then start the game. Steam must already be running and logged in.
# Invisible hides your online status and what you're playing from friends. It does not hide playtime or
# "recent activity" on your profile page: for that, set Steam > Profile > Edit Profile > Privacy Settings >
# Game details to Private (once, by hand).
set -euo pipefail
saves="$HOME/.config/unity3d/Team Cherry/Hollow Knight"
backup="$HOME/Documents/hollow-knight-save-backup-$(date +%Y%m%d-%H%M%S)"
if [ -d "$saves" ]; then cp -a "$saves" "$backup" && diff -rq "$saves" "$backup" >/dev/null && echo "saves backed up to $backup"; fi
command -v steam >/dev/null || { echo "steam not found on PATH" >&2; exit 1; }
pgrep -x steam >/dev/null || { echo "start Steam (and log in) first" >&2; exit 1; }
steam steam://friends/status/invisible
echo "Steam status set to Invisible; check the Friends list shows you as Invisible before playing."
read -r -p "Press Enter to launch Hollow Knight (Ctrl-C to stop) "
steam steam://rungameid/367520
