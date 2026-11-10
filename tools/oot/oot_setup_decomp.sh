#!/usr/bin/env bash
# Sauberer Weg, Schritt 1: Decomp (zeldaret/oot, CC0) an festem Stand holen und mit der EIGENEN ROM fuellen.
# Danach baut tools/oot/oot_build.py daraus das ganze Valheim-Paket. Laeuft unter Linux oder Windows-WSL (Ubuntu).
#
#   bash oot_setup_decomp.sh <eigene-rom.z64> [zielordner]
#
# Unterstuetzt: GameCube "Collector's Edition" Europa (gc-eu), MD5 der ROM wird geprueft.
# Braucht: git, python3 (+venv), gcc/make (fuer die kleinen Decomp-Werkzeuge), ffmpeg (fuer Sounds).
set -euo pipefail

ROM="${1:?Pfad zur eigenen ROM fehlt}"
DIR="${2:-oot-decomp}"
VER=gc-eu
COMMIT=14759abf9abb4295de142cdc1452998630ea70fb      # zeldaret/oot, Stand 18.10.2026 (mit diesem Stand gebaut und getestet)

if [ ! -d "$DIR/.git" ]; then
  git clone https://github.com/zeldaret/oot.git "$DIR"
fi
cd "$DIR"
git fetch -q origin || true
git checkout -q "$COMMIT"

mkdir -p "baseroms/$VER"
cp "$ROM" "baseroms/$VER/baserom.z64"
# Pruefsumme der komprimierten ROM (Datei der Decomp)
want=$(cut -d' ' -f1 "baseroms/$VER/checksum-compressed.md5")
have=$(md5sum "baseroms/$VER/baserom.z64" | cut -d' ' -f1)
if [ "$want" != "$have" ]; then
  echo "Falsche ROM: MD5 $have, erwartet $want (OoT GameCube Collector's Edition, Europa)." >&2
  exit 1
fi

# Python-Umgebung direkt (das Haupt-Makefile der Decomp verlangt sonst eine MIPS-Toolchain, die wir nicht brauchen)
test -d .venv || python3 -m venv .venv
.venv/bin/pip install -q -r requirements.txt
make -C tools
PY=.venv/bin/python3
EX=extracted/$VER
$PY tools/decompress_baserom.py $VER
$PY tools/extract_baserom.py baseroms/$VER/baserom-decompressed.z64 $EX/baserom -v $VER
# nur die Szenen, die das Paket braucht (Objekte liest der Exporter direkt aus $EX/baserom)
$PY -m tools.assets.extract $EX/baserom $EX -v $VER -s '(spot00|spot04|link_home|kokiri_shop|ydan|ydan_boss)_(scene|room_[0-9]+)' -r -j4
$PY tools/extract_text.py $EX/baserom $EX/text -v $VER
$PY tools/extract_audio.py -b $EX/baserom -o $EX -v $VER --read-xml
echo "Decomp fertig: $(pwd)"
echo "Weiter: python3 tools/oot/oot_build.py $(pwd) <ausgabe>"
