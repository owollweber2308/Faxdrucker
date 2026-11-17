#!/usr/bin/env bash
# Baut spandsp und faxshim.dll unter MSYS2/MINGW64 und sammelt alle benötigten DLLs
# in native/win-x64/. Wird von GitHub Actions aufgerufen, funktioniert auch lokal in MSYS2.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/native/win-x64"
SPANDSP_COMMIT="${SPANDSP_COMMIT:-8f1e1646bdec99eac5fd2cd92c35563f736b9b89}"
mkdir -p "$OUT"

if pkg-config --exists spandsp; then
    echo "spandsp bereits vorhanden: $(pkg-config --modversion spandsp)"
elif pacman -Si mingw-w64-x86_64-spandsp >/dev/null 2>&1; then
    pacman -S --noconfirm --needed mingw-w64-x86_64-spandsp
else
    echo "Baue spandsp aus dem Quelltext (Commit $SPANDSP_COMMIT)…"
    WORK="$(mktemp -d)"
    git -C "$WORK" init -q
    git -C "$WORK" fetch -q --depth 1 https://github.com/freeswitch/spandsp "$SPANDSP_COMMIT"
    git -C "$WORK" checkout -q FETCH_HEAD
    (
        cd "$WORK"
        ./bootstrap.sh
        ./configure --prefix=/mingw64 --enable-shared --disable-static
        make -C src -j"$(nproc)"
        make -C src install
    )
fi

make -C "$ROOT/native/faxshim" clean
make -C "$ROOT/native/faxshim" OS=Windows_NT CC=gcc
cp "$ROOT/native/faxshim/faxshim.dll" "$OUT/"

# Abhängige MinGW-DLLs (spandsp, libtiff, zlib, …) neben faxshim.dll legen
ldd "$ROOT/native/faxshim/faxshim.dll" | awk '{print $3}' | grep -i '/mingw64/' | while read -r dll; do
    cp -v "$dll" "$OUT/"
done

echo "Inhalt von $OUT:"
ls -la "$OUT"
