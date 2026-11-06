#!/usr/bin/env bash
# Bygger Forever Scout.app för macOS – körs på en Mac (Xcode Command Line Tools + .NET SDK 10).
#
#   client/mac/build.sh [alternativ]
#
#   --arch universal|arm64|x64   universal (standard): båda arkitekturerna + en liten universell startare
#   --sign "Developer ID Application: Namn (TEAMID)"   kodsignera med hardened runtime (annars ad hoc, bara lokalt)
#   --notarize <profil>          notarisera med xcrun notarytool (--keychain-profile <profil>) och stapla biljetten
#   --dmg                        gör också en signerad (och notariserad, om --notarize) .dmg för nedladdningssidan
#   --release-key <privat.pem>   kör sist scripts/scout-sign.mjs --mac på den färdiga zip:en (vår signatur ovanpå Apples)
#   --skip-tests                 hoppa över dotnet test
#
# Utdata i client/mac/dist/: Forever Scout.app, ForeverScout-mac-<version>.zip (det som laddas upp och signeras),
# och med --dmg ForeverScout-<version>.dmg. Se client/mac/README.md för hela flödet.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GUIDE="$(cd "$HERE/../.." && pwd)"
PROJ="$HERE/ForeverScout.Mac/ForeverScout.Mac.csproj"
PKG="$HERE/packaging"
BUILD="$HERE/build"
DIST="$HERE/dist"
APP_NAME="Forever Scout.app"
BUNDLE_ID="se.wow4ever.foreverscout"

ARCH="universal"
IDENTITY=""
NOTARY_PROFILE=""
MAKE_DMG=0
RELEASE_KEY=""
RUN_TESTS=1

while [[ $# -gt 0 ]]; do
  case "$1" in
    --arch) ARCH="$2"; shift 2 ;;
    --sign) IDENTITY="$2"; shift 2 ;;
    --notarize) NOTARY_PROFILE="$2"; shift 2 ;;
    --dmg) MAKE_DMG=1; shift ;;
    --release-key) RELEASE_KEY="$2"; shift 2 ;;
    --skip-tests) RUN_TESTS=0; shift ;;
    -h|--help) sed -n '2,16p' "$0"; exit 0 ;;
    *) echo "Okänt argument: $1" >&2; exit 2 ;;
  esac
done

fail() { echo "build.sh: $*" >&2; exit 1; }
step() { printf '\n==> %s\n' "$*"; }

[[ "$(uname -s)" == "Darwin" ]] || fail "körs på macOS (codesign, notarytool, ditto och iconutil finns bara där)"
case "$ARCH" in universal|arm64|x64) ;; *) fail "--arch universal|arm64|x64" ;; esac
command -v dotnet >/dev/null || fail "dotnet saknas – installera .NET SDK 10 (https://dot.net)"
xcode-select -p >/dev/null 2>&1 || fail "Xcode Command Line Tools saknas – kör: xcode-select --install"
[[ -n "$NOTARY_PROFILE" && -z "$IDENTITY" ]] && fail "--notarize kräver --sign (Apple notariserar bara Developer ID-signerade appar)"
[[ -n "$RELEASE_KEY" && -z "$NOTARY_PROFILE" ]] && fail "--release-key kräver --notarize: vår signatur ska ligga på den FÄRDIGA, notariserade zip:en"
if [[ -n "$IDENTITY" ]]; then
  security find-identity -v -p codesigning | grep -F -- "$IDENTITY" >/dev/null || fail "hittar inte signeringsidentiteten \"$IDENTITY\" i nyckelringen (security find-identity -v -p codesigning)"
fi

# Ingen telemetri från verktygen under bygget.
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 AVALONIA_TELEMETRY_OPTOUT=1

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJ" | head -1)"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "kunde inte läsa <Version> ur $PROJ"
BUILD_NO="$(date -u +%Y%m%d%H%M)"
echo "Forever Scout $VERSION (build $BUILD_NO), arkitektur: $ARCH"

if [[ $RUN_TESTS -eq 1 ]]; then
  step "Tester (Core + macOS)"
  dotnet test "$HERE/tests" -c Release
fi

rm -rf "$BUILD" && mkdir -p "$BUILD" "$DIST"
APP="$BUILD/$APP_NAME"
MACOS="$APP/Contents/MacOS"
mkdir -p "$MACOS" "$APP/Contents/Resources"

publish() { # $1 = arm64|x64, $2 = målkatalog
  step "dotnet publish osx-$1"
  dotnet publish "$PROJ" -c Release -r "osx-$1" --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o "$2"
  rm -f "$2"/*.pdb
}

if [[ "$ARCH" == "universal" ]]; then
  publish arm64 "$MACOS/arm64"
  publish x64 "$MACOS/x64"
  step "Universell startare (arm64 + x86_64)"
  clang -arch arm64 -arch x86_64 -mmacosx-version-min=14.0 -O2 -Wall -Werror -o "$MACOS/ForeverScout" "$PKG/launcher.c"
  lipo -info "$MACOS/ForeverScout"
else
  publish "$ARCH" "$MACOS"
fi

step "Paketet"
sed -e "s/__VERSION__/$VERSION/" -e "s/__BUILD__/$BUILD_NO/" "$PKG/Info.plist" > "$APP/Contents/Info.plist"
plutil -lint "$APP/Contents/Info.plist"
printf 'APPL????' > "$APP/Contents/PkgInfo"
if [[ -d "$HERE/assets/AppIcon.iconset" ]] && command -v iconutil >/dev/null; then
  iconutil -c icns -o "$APP/Contents/Resources/AppIcon.icns" "$HERE/assets/AppIcon.iconset"
else
  cp "$HERE/assets/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"
fi
# Bort med utökade attribut (t.ex. karantän från nedladdade paket) innan signering.
xattr -cr "$APP"

if [[ -n "$IDENTITY" ]]; then
  step "Kodsignering (Developer ID, hardened runtime, tidsstämpel)"
  ENT="$PKG/entitlements.plist"
  # Inifrån och ut: varje fil under Contents/MacOS (dylibs, .NET-binärer, createdump, .dll-filer), sedan paketet.
  while IFS= read -r -d '' f; do
    codesign --force --timestamp --options runtime --entitlements "$ENT" --sign "$IDENTITY" "$f"
  done < <(find "$MACOS" -type f ! -path "$MACOS/ForeverScout" -print0)
  codesign --force --timestamp --options runtime --entitlements "$ENT" --sign "$IDENTITY" "$APP"
  codesign --verify --deep --strict --verbose=2 "$APP"
  codesign -dv --verbose=2 "$APP" 2>&1 | grep -E "Identifier|TeamIdentifier|Runtime|Timestamp"
else
  step "Ad hoc-signering (bara för att köra lokalt – inte för distribution)"
  codesign --force --deep --sign - "$APP"
fi

ZIP="$DIST/ForeverScout-mac-$VERSION.zip"
if [[ -n "$NOTARY_PROFILE" ]]; then
  step "Notarisering (xcrun notarytool, profil $NOTARY_PROFILE)"
  SUBMIT="$BUILD/notarize-upload.zip"
  ditto -c -k --sequesterRsrc --keepParent "$APP" "$SUBMIT"
  xcrun notarytool submit "$SUBMIT" --keychain-profile "$NOTARY_PROFILE" --wait --timeout 60m | tee "$BUILD/notarytool.log"
  grep -q "status: Accepted" "$BUILD/notarytool.log" || fail "notariseringen godkändes inte – se: xcrun notarytool log <id> --keychain-profile $NOTARY_PROFILE"
  step "Stapla biljetten"
  xcrun stapler staple "$APP"
  xcrun stapler validate "$APP"
  spctl --assess --type execute --verbose=4 "$APP"
fi

step "Zip (det som laddas upp, efter staplingen)"
rm -rf "$DIST/$APP_NAME" "$ZIP"
ditto "$APP" "$DIST/$APP_NAME"
ditto -c -k --sequesterRsrc --keepParent "$APP" "$ZIP"
shasum -a 256 "$ZIP"

if [[ $MAKE_DMG -eq 1 ]]; then
  step "DMG"
  DMG="$DIST/ForeverScout-$VERSION.dmg"
  STAGE="$BUILD/dmg"
  rm -rf "$STAGE" "$DMG" && mkdir -p "$STAGE"
  ditto "$APP" "$STAGE/$APP_NAME"
  ln -s /Applications "$STAGE/Applications"
  # Utseende: vår ikon på skivan (.VolumeIcon.icns) och ett fönster med bakgrund (assets/dmg-background*.png, 640×400)
  # där appen och Program-genvägen står på var sin sida om pilen. Själva .dmg-filen får alltid Finders standardikon –
  # webbläsare tar bort egna filikoner vid nedladdning. Misslyckas Finder-skriptet blir det en vanlig DMG.
  mkdir -p "$STAGE/.background"
  tiffutil -cathidpicheck "$HERE/assets/dmg-background.png" "$HERE/assets/dmg-background@2x.png" -out "$STAGE/.background/bg.tiff"
  cp "$HERE/assets/AppIcon.icns" "$STAGE/.VolumeIcon.icns"
  RW="$BUILD/dmg-rw.dmg"
  MNT="$BUILD/dmg-mnt"
  rm -rf "$RW" "$MNT"
  hdiutil create -volname "Forever Scout" -srcfolder "$STAGE" -ov -format UDRW "$RW"
  hdiutil attach -readwrite -noverify -noautoopen -mountpoint "$MNT" "$RW" >/dev/null
  xcrun SetFile -a C "$MNT" 2>/dev/null || echo "build.sh: SetFile saknas – skivan får standardikonen" >&2
  osascript <<'APPLESCRIPT' || echo "build.sh: Finder-layouten misslyckades (ge Terminal behörighet att styra Finder) – vanlig DMG" >&2
tell application "Finder"
  tell disk "Forever Scout"
    open
    set current view of container window to icon view
    set toolbar visible of container window to false
    set statusbar visible of container window to false
    set the bounds of container window to {200, 120, 840, 548}
    set opts to the icon view options of container window
    set arrangement of opts to not arranged
    set icon size of opts to 128
    set text size of opts to 13
    set background picture of opts to file ".background:bg.tiff"
    set position of item "Forever Scout.app" of container window to {170, 196}
    set position of item "Applications" of container window to {470, 196}
    close
    open
    update without registering applications
    delay 2
    close
  end tell
end tell
APPLESCRIPT
  sync
  hdiutil detach "$MNT" >/dev/null || hdiutil detach -force "$MNT" >/dev/null
  hdiutil convert "$RW" -format UDZO -imagekey zlib-level=9 -ov -o "$DMG"
  rm -f "$RW"
  if [[ -n "$IDENTITY" ]]; then
    codesign --force --timestamp --sign "$IDENTITY" "$DMG"
    if [[ -n "$NOTARY_PROFILE" ]]; then
      xcrun notarytool submit "$DMG" --keychain-profile "$NOTARY_PROFILE" --wait --timeout 60m
      xcrun stapler staple "$DMG"
    fi
  fi
fi

if [[ -n "$RELEASE_KEY" ]]; then
  step "Vår signatur (scout-sign.mjs --mac) på den färdiga zip:en"
  command -v node >/dev/null || fail "node saknas (behövs för scripts/scout-sign.mjs)"
  (cd "$GUIDE" && node scripts/scout-sign.mjs --key "$RELEASE_KEY" --version "$VERSION" --mac "$ZIP")
  echo "Klart: public/scout/version.json och public/scout/ForeverScout-mac.zip – driftsätt båda (se README)."
fi

echo
echo "Klart: $DIST/$APP_NAME"
echo "       $ZIP"
