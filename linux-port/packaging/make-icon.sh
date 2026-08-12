#!/usr/bin/env bash
# Draws the application icon.
#
# Generated rather than committed as a binary: it is a handful of shapes, and a script can be
# read and changed in a way a checked-in PNG cannot. Grim Dawn's palette — parchment and gold on
# near-black — so it does not look foreign next to the game.
#
# The shape is a stash chest: this manages a stash, and a chest reads at 16 px where anything
# more detailed does not.

set -euo pipefail
OUT="${1:?usage: make-icon.sh <output.png>}"
SIZE="${2:-256}"

SVG="$(mktemp --suffix=.svg)"
trap 'rm -f "$SVG"' EXIT

cat > "$SVG" <<'ICON'
<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  <defs>
    <linearGradient id="lid" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#4a4038"/>
      <stop offset="1" stop-color="#2a251f"/>
    </linearGradient>
    <linearGradient id="body" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#332c25"/>
      <stop offset="1" stop-color="#1e1b17"/>
    </linearGradient>
  </defs>

  <rect width="256" height="256" rx="52" fill="#14120f"/>

  <!-- chest body -->
  <rect x="40" y="112" width="176" height="88" rx="10" fill="url(#body)" stroke="#c9a227" stroke-width="6"/>
  <!-- lid -->
  <path d="M40 112 a88 60 0 0 1 176 0 z" fill="url(#lid)" stroke="#c9a227" stroke-width="6"/>
  <!-- banding -->
  <rect x="112" y="60" width="32" height="140" fill="#c9a227" opacity="0.22"/>
  <!-- lock -->
  <rect x="112" y="128" width="32" height="30" rx="6" fill="#c9a227"/>
  <circle cx="128" cy="140" r="5" fill="#14120f"/>

  <!-- the loot that makes it worth managing -->
  <circle cx="76"  cy="150" r="9" fill="#5a8fd0"/>
  <circle cx="180" cy="150" r="9" fill="#d09a2c"/>
  <circle cx="76"  cy="176" r="7" fill="#3fae5a"/>
  <circle cx="180" cy="176" r="7" fill="#bb77ee"/>
</svg>
ICON

if command -v rsvg-convert >/dev/null 2>&1; then
    rsvg-convert -w "$SIZE" -h "$SIZE" "$SVG" -o "$OUT"
elif command -v magick >/dev/null 2>&1; then
    magick -background none "$SVG" -resize "${SIZE}x${SIZE}" "$OUT"
elif command -v convert >/dev/null 2>&1; then
    convert -background none "$SVG" -resize "${SIZE}x${SIZE}" "$OUT"
elif command -v inkscape >/dev/null 2>&1; then
    inkscape "$SVG" --export-type=png --export-filename="$OUT" -w "$SIZE" -h "$SIZE" >/dev/null 2>&1
else
    echo "error: need one of rsvg-convert, magick, convert or inkscape to render the icon" >&2
    exit 1
fi

echo "  icon: $OUT ($(identify -format '%wx%h' "$OUT" 2>/dev/null || echo "$SIZE×$SIZE"))"

# Panel-sized renders alongside the main one. A toolkit downscaling 256px to 22px produces a
# noticeably softer result than rendering the shapes at that size, and desktop icon themes are
# built around having the sizes available.
if [ "$SIZE" = "256" ]; then
    for small in 32 48 64 128; do
        SMALL_OUT="$(dirname "$OUT")/$(basename "$OUT" .png)-$small.png"
        if command -v rsvg-convert >/dev/null 2>&1; then
            rsvg-convert -w "$small" -h "$small" "$SVG" -o "$SMALL_OUT"
        elif command -v magick >/dev/null 2>&1; then
            magick -background none "$SVG" -resize "${small}x${small}" "$SMALL_OUT"
        elif command -v convert >/dev/null 2>&1; then
            convert -background none "$SVG" -resize "${small}x${small}" "$SMALL_OUT"
        fi
    done
    echo "  also: 32, 48, 64, 128 px"
fi
