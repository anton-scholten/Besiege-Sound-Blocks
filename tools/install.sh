#!/usr/bin/env bash
#
# Builds the mod and installs it into Besiege, so it can be tried in the game.
#
#   ./tools/install.sh            build, then copy into Besiege_Data/Mods/SoundBlocks
#   ./tools/install.sh --no-build install what is already built
#
# Besiege reads mods once at startup, so the game has to be restarted afterwards.
# Set BESIEGE_DIR if the install is not auto-detected.

set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [[ "${1:-}" != "--no-build" ]]; then
    "$REPO_DIR/tools/build.sh"
fi

find_besiege() {
    if [[ -n "${BESIEGE_DIR:-}" ]]; then echo "$BESIEGE_DIR"; return; fi
    local candidates=(
        "$HOME/.steam/steam/steamapps/common/Besiege"
        "$HOME/.local/share/Steam/steamapps/common/Besiege"
    )
    local vdf
    for vdf in "$HOME/.steam/steam/steamapps/libraryfolders.vdf" \
               "$HOME/.local/share/Steam/steamapps/libraryfolders.vdf"; do
        [[ -f "$vdf" ]] || continue
        while read -r lib; do candidates+=("$lib/steamapps/common/Besiege"); done \
            < <(grep -oE '"path"[[:space:]]+"[^"]+"' "$vdf" | sed -E 's/.*"([^"]+)"$/\1/')
    done
    local dir
    for dir in "${candidates[@]}"; do
        [[ -d "$dir/Besiege_Data" ]] && { echo "$dir"; return; }
    done
    return 1
}

if ! BESIEGE="$(find_besiege)"; then
    echo "Could not find Besiege. Set BESIEGE_DIR to your install directory." >&2
    exit 1
fi

DEST="$BESIEGE/Besiege_Data/Mods/SoundBlocks"

if [[ ! -f "$REPO_DIR/SoundBlocks.dll" ]]; then
    echo "SoundBlocks.dll is missing; run ./tools/build.sh first." >&2
    exit 1
fi

# Only the files the game loads. Everything else in the repo -- the sources, the
# tools, the promo art, the unpacked sound library -- is not part of the mod.
mkdir -p "$DEST"
cp "$REPO_DIR/Mod.xml" "$REPO_DIR/SoundBlock.xml" "$REPO_DIR/SoundBlocks.dll" "$DEST/"
rm -rf "$DEST/Resources"
cp -r "$REPO_DIR/Resources" "$DEST/"

echo "Installed to $DEST"
if pgrep -x Besiege >/dev/null 2>&1 || pgrep -f 'Besiege\.x86' >/dev/null 2>&1; then
    echo "Besiege is running; restart it to pick this up."
fi
