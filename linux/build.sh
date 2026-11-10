#!/usr/bin/env bash
# Bygger Forever Scout för Linux – går att köra på Linux, macOS och Windows (Git Bash). Kräver .NET SDK 10 och Python 3.
#
#   client/linux/build.sh [alternativ]
#
#   --arch x64|arm64             x64 (standard)
#   --skip-tests                 hoppa över dotnet test
#
# Utdata i client/linux/dist/: ForeverScout-linux-<arch>-<version>.tar.gz med mappen ForeverScout/ (fristående –
# inget .NET att installera). Det som släpps är en AppImage av den: packaging/make-appimage.sh (körs på Linux),
# som sedan signeras med scripts/scout-sign.mjs --linux. Se client/linux/README.md.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GUIDE="$(cd "$HERE/../.." && pwd)"
PROJ="$HERE/ForeverScout.Linux/ForeverScout.Linux.csproj"
BUILD="$HERE/build"
DIST="$HERE/dist"

ARCH="x64"
RUN_TESTS=1

while [[ $# -gt 0 ]]; do
  case "$1" in
    --arch) ARCH="$2"; shift 2 ;;
    --skip-tests) RUN_TESTS=0; shift ;;
    -h|--help) sed -n '2,11p' "$0"; exit 0 ;;
    *) echo "Okänt argument: $1" >&2; exit 2 ;;
  esac
done

fail() { echo "build.sh: $*" >&2; exit 1; }
step() { printf '\n==> %s\n' "$*"; }

case "$ARCH" in x64|arm64) ;; *) fail "--arch x64|arm64" ;; esac
command -v dotnet >/dev/null || fail "dotnet saknas – installera .NET SDK 10 (https://dot.net)"
PY="$(command -v python3 || command -v python || true)"
[[ -n "$PY" ]] || fail "python3 saknas (packar tar.gz med rätt rättigheter, även på Windows)"

# Ingen telemetri från verktygen under bygget.
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 AVALONIA_TELEMETRY_OPTOUT=1

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJ" | head -1)"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "kunde inte läsa <Version> ur $PROJ"
echo "Forever Scout $VERSION för Linux, arkitektur: $ARCH"

if [[ $RUN_TESTS -eq 1 ]]; then
  step "Tester (Core + macOS/Linux)"
  dotnet test "$HERE/../mac/tests" -c Release
fi

APP="$BUILD/ForeverScout"
rm -rf "$BUILD" && mkdir -p "$APP" "$DIST"

step "dotnet publish linux-$ARCH"
dotnet publish "$PROJ" -c Release -r "linux-$ARCH" --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o "$APP"
rm -f "$APP"/*.pdb
[[ -f "$APP/ForeverScout" ]] || fail "publiceringen gav ingen ForeverScout-binär"

cp "$HERE/../mac/ForeverScout.Mac/Assets/app-256.png" "$APP/ForeverScout.png"
cp "$HERE/packaging/install.sh" "$APP/install.sh"
cp "$HERE/packaging/LAS-MIG.md" "$APP/LAS-MIG.md"

OUT="$DIST/ForeverScout-linux-$ARCH-$VERSION.tar.gz"
step "Packar $(basename "$OUT")"
# Python i stället för tar: rättigheterna (0755 för binären och skripten, 0644 för resten) sätts uttryckligen, så att
# filen blir likadan oavsett om den byggs på Linux, macOS eller Windows (NTFS har ingen körbar-bit). Ägare 0/0, sorterat.
"$PY" - "$APP" "$OUT" <<'PYEOF'
import gzip, os, sys, tarfile
src, out = sys.argv[1], sys.argv[2]
EXEC = {"ForeverScout", "install.sh", "createdump"}
with open(out, "wb") as raw, gzip.GzipFile(fileobj=raw, mode="wb", mtime=0, filename="") as gz, tarfile.open(fileobj=gz, mode="w", format=tarfile.PAX_FORMAT) as tar:
    d = tarfile.TarInfo("ForeverScout"); d.type = tarfile.DIRTYPE; d.mode = 0o755; tar.addfile(d)
    for root, dirs, files in os.walk(src):
        dirs.sort()
        rel = os.path.relpath(root, src).replace(os.sep, "/")
        base = "ForeverScout" if rel == "." else "ForeverScout/" + rel
        if rel != ".":
            d = tarfile.TarInfo(base); d.type = tarfile.DIRTYPE; d.mode = 0o755; tar.addfile(d)
        for name in sorted(files):
            p = os.path.join(root, name)
            if os.path.islink(p): raise SystemExit("symlänk i paketet: " + p)
            ti = tarfile.TarInfo(base + "/" + name)
            ti.size = os.path.getsize(p)
            ti.mode = 0o755 if name in EXEC else 0o644
            ti.mtime = int(os.path.getmtime(p))
            with open(p, "rb") as f: tar.addfile(ti, f)
PYEOF

if command -v sha256sum >/dev/null; then sha256sum "$OUT"; fi

echo
echo "Klart: $OUT"
echo "Nästa steg (på Linux): packaging/make-appimage.sh \"$OUT\" <runtime-x86_64> ForeverScout-x86_64.AppImage"
