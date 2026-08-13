#!/usr/bin/env bash
# Produces the application icon.
#
# Upstream's, when the submodule holding it is checked out: IAGrim/gd.ico is its
# <ApplicationIcon>, and it carries 16, 32, 64, 128 and 256 px renditions. This is the same tool,
# so it should look like it in a dock and an app menu rather than like something unrelated that
# happens to open the same collection.
#
# Extracted at build time, never committed — the rule the hook, the injector and the help page
# all follow. The output path is gitignored.
#
# Without the submodule it draws a stash chest instead, so a bare checkout still builds. That is
# the fallback rather than the intent: a handful of shapes in Grim Dawn's palette, in a script
# that can be read and changed in a way a checked-in binary cannot.

set -euo pipefail
OUT="${1:?usage: make-icon.sh <output.png>}"
SIZE="${2:-256}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
UPSTREAM_ICON="$ROOT/iagd/IAGrim/gd.ico"

# --- upstream's icon, at each size it actually ships -------------------------------------
#
# Taken from the .ico's own renditions rather than downscaling one of them: the small sizes in
# an .ico are drawn for those pixel counts, and a 256->32 resample throws that away.
if [ -f "$UPSTREAM_ICON" ] && command -v python3 >/dev/null 2>&1 \
   && python3 -c "import PIL" >/dev/null 2>&1; then
    mkdir -p "$(dirname "$OUT")"
    python3 - "$UPSTREAM_ICON" "$OUT" "$SIZE" <<'EXTRACT'
import sys
from PIL import Image

source, out, size = sys.argv[1], sys.argv[2], int(sys.argv[3])
icon = Image.open(source)
available = sorted(icon.info.get("sizes", []))


def render(target, path):
    """The .ico's own rendition at this size, or the nearest one above it resampled down."""
    exact = (target, target) in available
    icon.size = (target, target) if exact else max(available)
    image = icon.convert("RGBA")
    if not exact:
        image = image.resize((target, target), Image.LANCZOS)
    image.save(path)
    return exact


render(size, out)
print(f"  icon: {out} ({size}x{size}, from upstream's gd.ico)")

# Panel sizes alongside the main one: desktop icon themes expect them present, and a toolkit
# downscaling 256 px to 22 px is worse than the rendition the author drew.
if size == 256:
    from pathlib import Path
    stem = Path(out)
    drawn = []
    for small in (32, 48, 64, 128):
        exact = render(small, stem.with_name(f"{stem.stem}-{small}.png"))
        drawn.append(f"{small}{'' if exact else '*'}")
    print(f"  also: {', '.join(drawn)} px  (* resampled, not in the .ico)")
EXTRACT
    exit 0
fi

if [ -f "$UPSTREAM_ICON" ]; then
    echo "  icon: upstream's gd.ico found but python3/Pillow is not available; drawing instead" >&2
else
    echo "  icon: upstream's gd.ico not found; drawing instead" >&2
    echo "        run: git submodule update --init --recursive" >&2
fi

# --- the fallback ------------------------------------------------------------------------

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
