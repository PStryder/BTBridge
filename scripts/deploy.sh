#!/usr/bin/env bash
# Build BTBridge and link it into the game's ModTek Mods folder (macOS and Linux).
# The Windows equivalent is deploy.ps1.
#
# Mods/BTBridge is a symlink to mod/BTBridge/bin/Release, so every rebuild is live on the
# next game launch. Close the game before building.
#
# Usage:
#   scripts/deploy.sh                  build, then (re)create the symlink
#   scripts/deploy.sh --no-build       just (re)create the symlink
#   scripts/deploy.sh --undeploy       remove the symlink only (never the build output)
#   scripts/deploy.sh --game-dir DIR   the Steam BATTLETECH folder (default: $BATTLETECH_DIR, else
#                                      the usual Steam location for this OS)
#   BTBRIDGE_MODS_DIR=...              override the Mods folder (default: <game dir>/Mods)
#
# Untested on a real Mac so far: see the README's macOS notes.
set -euo pipefail

build=1
undeploy=0
game_dir="${BATTLETECH_DIR:-}"

while [ $# -gt 0 ]; do
    case "$1" in
        --no-build) build=0 ;;
        --undeploy) undeploy=1 ;;
        --game-dir) shift; game_dir="${1:?--game-dir needs a path}" ;;
        -h|--help) sed -n '2,16p' "$0"; exit 0 ;;
        *) echo "unknown option: $1 (see --help)" >&2; exit 2 ;;
    esac
    shift
done

if [ -z "$game_dir" ]; then
    case "$(uname -s)" in
        Darwin) game_dir="$HOME/Library/Application Support/Steam/steamapps/common/BATTLETECH" ;;
        Linux)  game_dir="$HOME/.local/share/Steam/steamapps/common/BATTLETECH" ;;
        *) echo "unrecognised OS; pass --game-dir (on Windows use scripts/deploy.ps1)" >&2; exit 2 ;;
    esac
fi

repo="$(cd "$(dirname "$0")/.." && pwd)"
project="$repo/mod/BTBridge"
output="$project/bin/Release"
mods_dir="${BTBRIDGE_MODS_DIR:-$game_dir/Mods}"
link="$mods_dir/BTBridge"

remove_link() {
    if [ -L "$link" ]; then
        rm "$link"   # removes the link itself, never what it points to
        echo "removed symlink $link"
    elif [ -e "$link" ]; then
        echo "$link exists and is not a symlink; refusing to delete it" >&2
        exit 1
    fi
}

if [ "$undeploy" -eq 1 ]; then
    remove_link
    exit 0
fi

if [ ! -d "$game_dir" ]; then
    echo "game folder not found: $game_dir (pass --game-dir or set BATTLETECH_DIR)" >&2
    exit 1
fi
if [ ! -d "$mods_dir/ModTek" ]; then
    echo "ModTek isn't installed in $mods_dir (expected $mods_dir/ModTek). Install ModTek first; see the README." >&2
    exit 1
fi

if [ "$build" -eq 1 ]; then
    if pgrep -f "BattleTech" >/dev/null 2>&1; then
        echo "BattleTech is running; close it before building (the DLL is in use)" >&2
        exit 1
    fi
    dotnet build "$project" -c Release -nologo -p:GameDir="$game_dir"
fi

if [ ! -f "$output/BTBridge.dll" ] || [ ! -f "$output/mod.json" ]; then
    echo "no build output in $output; build first (drop --no-build)" >&2
    exit 1
fi

remove_link
ln -s "$output" "$link"
echo "linked $link -> $output"
