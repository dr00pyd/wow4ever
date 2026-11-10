#!/usr/bin/env bash
# Gör en AppImage av den färdiga tar.gz:en från build.sh – en enda körbar fil som går på alla distributioner.
# Körs på Linux (kräver mksquashfs ur squashfs-tools). Inget annat verktyg behövs: en AppImage är AppImage-projektets
# startare (runtime) följd av en squashfs-avbild med appen.
#
#   client/linux/packaging/make-appimage.sh <ForeverScout-linux-x64-x.y.z.tar.gz> <runtime-x86_64> <ut.AppImage>
#
# <runtime-x86_64> hämtas från https://github.com/AppImage/type2-runtime/releases (filen runtime-x86_64). Skriptet
# skriver ut dess SHA-256 så att den kan antecknas i släppanteckningarna.
set -euo pipefail

TAR="${1:?tar.gz från build.sh}"
RUNTIME="${2:?AppImage-startaren (runtime-x86_64)}"
OUT="${3:?utfil (.AppImage)}"
fail() { echo "make-appimage.sh: $*" >&2; exit 1; }

command -v mksquashfs >/dev/null || fail "mksquashfs saknas (paketet squashfs-tools)"
[[ -f "$TAR" ]] || fail "hittar inte $TAR"
[[ "$(head -c 4 "$RUNTIME" | od -An -tx1 | tr -d ' \n')" == "7f454c46" ]] || fail "$RUNTIME är inte en ELF-fil"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
APPDIR="$WORK/ForeverScout.AppDir"
mkdir -p "$APPDIR/usr/lib"
tar xzf "$TAR" -C "$APPDIR/usr/lib"
[[ -x "$APPDIR/usr/lib/ForeverScout/ForeverScout" ]] || fail "tar.gz:en innehåller ingen körbar ForeverScout/ForeverScout"
# I en AppImage behövs varken menyskriptet eller läs-mig-filen (filen startas direkt).
rm -f "$APPDIR/usr/lib/ForeverScout/install.sh"

cat > "$APPDIR/AppRun" <<'EOF'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/lib/ForeverScout/ForeverScout" "$@"
EOF
chmod 755 "$APPDIR/AppRun"

cat > "$APPDIR/se.wow4ever.foreverscout.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=Forever Scout
Comment=Laddar upp addon-data till Forever-guiden (guide.wow4ever.se)
Exec=ForeverScout
Icon=foreverscout
Terminal=false
Categories=Game;Utility;
StartupWMClass=ForeverScout
EOF
cp "$APPDIR/usr/lib/ForeverScout/ForeverScout.png" "$APPDIR/foreverscout.png"
cp "$APPDIR/foreverscout.png" "$APPDIR/.DirIcon"

mksquashfs "$APPDIR" "$WORK/app.squashfs" -root-owned -noappend -comp zstd -Xcompression-level 19 -b 1M -mkfs-time 0 -quiet -no-progress
cat "$RUNTIME" "$WORK/app.squashfs" > "$OUT"
chmod 755 "$OUT"

echo "runtime  $(sha256sum "$RUNTIME" | cut -d' ' -f1)"
echo "appimage $(sha256sum "$OUT" | cut -d' ' -f1)  $(du -h "$OUT" | cut -f1)  $OUT"
