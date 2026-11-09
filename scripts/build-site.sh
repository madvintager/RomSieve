#!/usr/bin/env bash
# Builds the website published on GitHub Pages (.github/workflows/pages.yml): the home page of site/ in English
# (<output>/index.html) and French (<output>/fr/index.html), with the icon of the application, and the HTML user guide
# in <output>/guide/. The version given is written into the home pages, whose buttons download its two archives from
# GitHub Releases. Then checks that every local link and image of the site exists, anchors included.
# The guide is built by scripts/build-docs.sh (pandoc 3.1 or later on the PATH, or in the PANDOC variable), unless
# DOCS_HTML_DIR names a guide already built (the one of another version).
#
#   scripts/build-site.sh <output directory> <version>
#   scripts/build-site.sh dist/site 1.2.0
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="${1:?usage: build-site.sh <output directory> <version>}"
version="${2:?usage: build-site.sh <output directory> <version>}"
if ! [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
  echo "invalid version: $version (expected 1.2.0 or 1.2.0-beta1)" >&2
  exit 1
fi

rm -rf "$out"
mkdir -p "$out"
out="$(cd "$out" && pwd)"

if [ -n "${DOCS_HTML_DIR:-}" ]; then
  cp -r "$DOCS_HTML_DIR" "$out/guide"
else
  bash "$root/scripts/build-docs.sh" "$out/guide" > /dev/null
fi
# The guide links to the README of the release archive, which the site does not hold: online, it is the one of GitHub
while IFS= read -r page; do
  sed -i 's#href="\.\./\.\./README\.md"#href="https://github.com/madvintager/RomSieve\#building-from-source"#g' "$page"
done < <(find "$out/guide" -name '*.html')

cp -r "$root/site/." "$out/"
cp "$root/src/RomSieve.App/Assets/icon-128x128.png" "$out/icon.png"
while IFS= read -r page; do
  sed -i "s/@VERSION@/$version/g" "$page"
done < <(find "$out" -maxdepth 2 -name index.html ! -path "$out/guide/*")

bash "$root/scripts/docs/check-links.sh" "$out"
echo "$out"
