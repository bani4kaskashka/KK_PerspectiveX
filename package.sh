#!/bin/sh
# Builds the two release zips (KK and KKS) from the Release DLL.
# Same DLL byte-for-byte in both; only the filename prefix differs, per the
# Illusion convention (KK_ = Koikatsu, KKS_ = Koikatsu Sunshine). Each zip has
# the BepInEx/plugins/ folder layout so users can drop it straight into the game.
set -e
ROOT="$(cd "$(dirname "$0")" && pwd)"
OUT="$ROOT/src/KK_PerspectiveX/bin/Release"
DLL="$OUT/KK_PerspectiveX.dll"
DIST="$ROOT/dist"

[ -f "$DLL" ] || { echo "Build first: cd src/KK_PerspectiveX && dotnet build -c Release"; exit 1; }

VER="$(grep -o '<Version>[^<]*' "$ROOT/src/KK_PerspectiveX/KK_PerspectiveX.csproj" | sed 's/<Version>//')"
[ -n "$VER" ] || { echo "Could not read <Version> from csproj"; exit 1; }

rm -rf "$DIST"
mkdir -p "$DIST"

# Zip a directory's contents portably (zip > bsdtar > python3).
# $1 = absolute output .zip, $2 = dir to run from, $3 = entry to add.
zipdir() {
    out="$1"; from="$2"; entry="$3"
    if command -v zip >/dev/null 2>&1; then
        (cd "$from" && zip -r -q "$out" "$entry")
    elif command -v bsdtar >/dev/null 2>&1; then
        (cd "$from" && bsdtar -a -cf "$out" "$entry")
    elif command -v python3 >/dev/null 2>&1; then
        (cd "$from" && python3 -m zipfile -c "$out" "$entry")
    else
        echo "Need one of: zip, bsdtar, python3"; exit 1
    fi
}

# $1 = game prefix (KK / KKS)
make_zip() {
    prefix="$1"
    stage="$DIST/stage/BepInEx/plugins"
    mkdir -p "$stage"
    cp "$DLL" "$stage/${prefix}_PerspectiveX.dll"
    zip="$DIST/${prefix}_PerspectiveX_v${VER}.zip"
    zipdir "$zip" "$DIST/stage" BepInEx
    rm -rf "$DIST/stage"
    echo "  $zip"
}

echo "Packaging v$VER:"
make_zip KK
make_zip KKS
echo "Done."
