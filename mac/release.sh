#!/usr/bin/env bash
# Släpper Forever Scout för Mac i ett svep: låser upp nyckelringen, bygger, signerar, notariserar, gör zip + DMG,
# kontrollerar resultatet och skickar båda filerna till servern. Körs på MacBooken:
#
#   cd ~/wow4ever && git pull && bash mac/release.sh
#
#   --server användare@ip   serverns adress (sparas, behövs bara första gången – annars frågar skriptet)
#   --bara-skicka           bygg inte, skicka bara de färdiga filerna i mac/dist/ (om scp misslyckades förra gången)
#   --ny-server             fråga efter serverns adress igen
#
# Serverns adress står inte i git (repot är publikt): den ges med --server eller frågas efter en gång och sparas i
# ~/.config/forever-scout/release.conf.
# Där kan också IDENTITY (signeringsidentitetens hash) och NOTARY_PROFILE stå; annars tas Developer ID-certifikatet
# ur nyckelringen och profilen "forever-scout".
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJ="$HERE/ForeverScout.Mac/ForeverScout.Mac.csproj"
DIST="$HERE/dist"
KEYCHAIN="forever-scout.keychain-db"
CONF="$HOME/.config/forever-scout/release.conf"

ONLY_SEND=0
ASK_SERVER=0
NEW_SERVER=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --server) NEW_SERVER="${2:-}"; shift 2 ;;
    --bara-skicka) ONLY_SEND=1; shift ;;
    --ny-server) ASK_SERVER=1; shift ;;
    -h|--help) sed -n '2,14p' "$0"; exit 0 ;;
    *) echo "Okänt argument: $1" >&2; exit 2 ;;
  esac
done

fail() { printf '\nrelease.sh: %s\n' "$*" >&2; exit 1; }
step() { printf '\n==> %s\n' "$*"; }

[[ "$(uname -s)" == "Darwin" ]] || fail "körs på macOS"

SERVER="" IDENTITY="" NOTARY_PROFILE=""
# shellcheck disable=SC1090
[[ -f "$CONF" ]] && source "$CONF"
NOTARY_PROFILE="${NOTARY_PROFILE:-forever-scout}"
if [[ -n "$NEW_SERVER" || -z "$SERVER" || $ASK_SERVER -eq 1 ]]; then
  if [[ -n "$NEW_SERVER" ]]; then SERVER="$NEW_SERVER"
  else read -r -p "Serverns adress (användare@ip, t.ex. namn@192.168.1.10): " SERVER
  fi
  [[ "$SERVER" =~ ^[A-Za-z0-9._-]+@[A-Za-z0-9.-]+$ ]] || fail "adressen ska se ut som användare@ip"
  mkdir -p "$(dirname "$CONF")"
  { echo "SERVER=$SERVER"; [[ -n "$IDENTITY" ]] && echo "IDENTITY=$IDENTITY"; echo "NOTARY_PROFILE=$NOTARY_PROFILE"; } > "$CONF"
  chmod 600 "$CONF"
  echo "Sparat i $CONF"
fi

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJ" | head -1)"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "kunde inte läsa <Version> ur $PROJ"
ZIP="$DIST/ForeverScout-mac-$VERSION.zip"
DMG="$DIST/ForeverScout-$VERSION.dmg"
APP="$DIST/Forever Scout.app"
echo "Forever Scout för Mac $VERSION → $SERVER"

if [[ $ONLY_SEND -eq 0 ]]; then
  step "Låser upp nyckelringen (skriv nyckelringens lösenord)"
  security unlock-keychain "$KEYCHAIN"
  # Lång timeout så att den inte låses igen medan Apple notariserar.
  security set-keychain-settings -t 7200 "$KEYCHAIN" || true

  if [[ -z "$IDENTITY" ]]; then
    IDENTITY="$(security find-identity -v -p codesigning | awk '/Developer ID Application/ {print $2; exit}')"
    [[ -n "$IDENTITY" ]] || fail "hittar inget Developer ID Application-certifikat i nyckelringen"
  fi

  step "Bygger, signerar, notariserar och packar (5–30 min, låt Terminal vara)"
  bash "$HERE/build.sh" --sign "$IDENTITY" --notarize "$NOTARY_PROFILE" --dmg
fi

step "Kontrollerar"
[[ -f "$ZIP" ]] || fail "hittar inte $ZIP"
[[ -f "$DMG" ]] || fail "hittar inte $DMG"
if [[ -d "$APP" ]]; then
  spctl -a -t exec "$APP" || fail "macOS godkänner inte appen (spctl). Kör: spctl -a -vv -t exec \"$APP\""
  echo "Appen: notariserad och godkänd"
fi
xcrun stapler validate "$DMG" >/dev/null || fail "DMG:n saknar Apples notariseringsbiljett. Kör: xcrun stapler validate \"$DMG\""
echo "DMG:n: notariseringsbiljetten sitter på"
ZIP_SHA="$(shasum -a 256 "$ZIP" | awk '{print $1}')"
DMG_SHA="$(shasum -a 256 "$DMG" | awk '{print $1}')"

step "Skickar till $SERVER (skriv serverns lösenord om det frågas)"
if ! scp -o ConnectTimeout=10 "$ZIP" "$DMG" "$SERVER:~/"; then
  cat >&2 <<EOF

Kunde inte nå servern. Är du inte hemma? Gör så här i stället:
  1. Kopiera de två filerna till Windows-servern, till D:\\webb\\wowlan\\guide\\client\\mac\\
       $ZIP
       $DMG
  2. Eller kör igen hemifrån utan att bygga om:  bash mac/release.sh --bara-skicka
EOF
  exit 1
fi

cat <<EOF

Klart! Mac $VERSION ligger på servern.
  zip  $ZIP_SHA
  dmg  $DMG_SHA

Skriv till Claude:  Mac $VERSION ligger på servern
EOF
