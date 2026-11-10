#!/usr/bin/env bash
# Valfritt: lägger Forever Scout i programmenyn (KDE, GNOME m.fl.). Appen fungerar utan detta – starta då
# ./ForeverScout direkt. Skriver EN fil: ~/.local/share/applications/se.wow4ever.foreverscout.desktop.
# Ta bort den igen med: ./install.sh --remove
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APPS="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
FILE="$APPS/se.wow4ever.foreverscout.desktop"

if [[ "${1:-}" == "--remove" ]]; then
  rm -f "$FILE"
  echo "Borttagen: $FILE"
  exit 0
fi

[[ -x "$HERE/ForeverScout" ]] || { echo "Hittar inte $HERE/ForeverScout (kör skriptet från den uppackade mappen)." >&2; exit 1; }
case "$HERE" in *$'\n'*|*'"'*|*'`'*|*'$'*|*'\'*|*'%'*) echo "Mappens sökväg innehåller tecken som inte går att skriva i en programstartare. Flytta mappen och försök igen." >&2; exit 1 ;; esac

mkdir -p "$APPS"
cat > "$FILE" <<EOF
[Desktop Entry]
Type=Application
Name=Forever Scout
Comment=Laddar upp addon-data till Forever-guiden (guide.wow4ever.se)
Exec="$HERE/ForeverScout"
Icon=$HERE/ForeverScout.png
Terminal=false
Categories=Game;Utility;
StartupWMClass=ForeverScout
EOF
echo "Klart: Forever Scout finns nu i programmenyn."
echo "($FILE)"
