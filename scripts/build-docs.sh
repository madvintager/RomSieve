#!/usr/bin/env bash
# Converts the Markdown user guide (docs/) into HTML pages readable offline in a browser, for the release archives:
# <output>/index.html (docs/README.md), <output>/en/*.html, <output>/fr/*.html, <output>/images/ and guide.css.
# Links between pages lead to the .html files; every page links back to the contents of its language and to the same
# page in the other language. Then checks that every local link and image of the pages exists, anchors included.
# Needs pandoc (3.1 or later) on the PATH, or in the PANDOC variable.
#
#   scripts/build-docs.sh <output directory>
#   scripts/build-docs.sh dist/docs
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="${1:?usage: build-docs.sh <output directory>}"
pandoc="${PANDOC:-pandoc}"
if ! command -v "$pandoc" >/dev/null 2>&1; then
  echo "pandoc not found: install it, or give its path in the PANDOC variable" >&2
  exit 1
fi

rm -rf "$out"
mkdir -p "$out/en" "$out/fr"
out="$(cd "$out" && pwd)"
filter="$root/scripts/docs/guide.lua"

cp "$root/scripts/docs/guide.css" "$out/"
cp -r "$root/docs/images" "$out/"

# convert <source relative to docs/> <target> <lang> <href of the stylesheet> [pandoc options]
convert() {
  local source="$1" target="$2" lang="$3" css="$4"
  shift 4
  (cd "$root/docs" && "$pandoc" "$source" -f gfm -t html5 --standalone --lua-filter "$filter" \
    --css "$css" -M lang="$lang" "$@" -o "$target")
}

convert README.md "$out/index.html" en guide.css -M pagetitle="RomSieve documentation"

for lang in en fr; do
  case "$lang" in
    en) other=fr contents="Contents" other_label="Français" ;;
    fr) other=en contents="Sommaire" other_label="English" ;;
  esac
  for page in "$root/docs/$lang"/*.md; do
    name="$(basename "$page" .md)"
    nav=()
    if [ "$name" != index ]; then
      nav=(-M contents-href=index.html -M contents-label="$contents")
    fi
    if [ -f "$root/docs/$other/$name.md" ]; then
      nav+=(-M other-href="../$other/$name.html" -M other-label="$other_label")
    fi
    convert "$lang/$name.md" "$out/$lang/$name.html" "$lang" ../guide.css "${nav[@]}"
  done
done

# Every local link, image and anchor of the pages must exist
bash "$root/scripts/docs/check-links.sh" "$out"
echo "$out"
