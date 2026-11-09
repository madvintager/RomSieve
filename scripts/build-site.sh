#!/usr/bin/env bash
# Builds the website published on GitHub Pages (.github/workflows/pages.yml): the home page of site/ in English
# (<output>/index.html) and French (<output>/fr/index.html), with the icon of the application, and the HTML user guide
# in <output>/guide/. The version given is written into the home pages, whose buttons download its two archives from
# GitHub Releases. The screenshots of the home pages are those of the guide, in WebP at the width shown
# (scripts/docs/site-images.py, which needs Pillow). For search engines, every page of the guide gets its canonical
# address and the same page in the other language, and sitemap.xml lists the pages; the home pages carry theirs, with
# their social previews (site/social.jpg) and structured data. Then checks that every local link and image of the site
# exists, anchors included.
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

# Screenshots of the home page: those of the guide, lighter (WebP at the width shown)
python=""
for candidate in python3 python; do
  if "$candidate" -c "import PIL.WebPImagePlugin" > /dev/null 2>&1; then
    python="$candidate"
    break
  fi
done
if [ -z "$python" ]; then
  echo "Pillow not found: install it (pip install pillow, or the python3-pil package)" >&2
  exit 1
fi
"$python" "$root/scripts/docs/site-images.py" "$out/guide/images" "$out/images"

# Search engines: the address of each page, and for the guide the same page in the other language
site_url="https://madvintager.github.io/RomSieve"
while IFS= read -r page; do
  rel="${page#"$out"/}"
  links="<link rel=\"canonical\" href=\"$site_url/$rel\">"
  case "$rel" in
    guide/en/* | guide/fr/*)
      name="${rel#guide/??/}"
      if [ -f "$out/guide/en/$name" ] && [ -f "$out/guide/fr/$name" ]; then
        links="$links<link rel=\"alternate\" hreflang=\"en\" href=\"$site_url/guide/en/$name\">"
        links="$links<link rel=\"alternate\" hreflang=\"fr\" href=\"$site_url/guide/fr/$name\">"
        links="$links<link rel=\"alternate\" hreflang=\"x-default\" href=\"$site_url/guide/en/$name\">"
      fi
      ;;
  esac
  sed -i "s#</head>#$links</head>#" "$page"
done < <(find "$out/guide" -name '*.html')

# Plan of the site for search engines: the two home pages, then every page of the guide
{
  echo '<?xml version="1.0" encoding="UTF-8"?>'
  echo '<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">'
  echo "  <url><loc>$site_url/</loc></url>"
  echo "  <url><loc>$site_url/fr/</loc></url>"
  (cd "$out" && find guide -name '*.html' | LC_ALL=C sort) | while IFS= read -r rel; do
    echo "  <url><loc>$site_url/$rel</loc></url>"
  done
  echo '</urlset>'
} > "$out/sitemap.xml"

bash "$root/scripts/docs/check-links.sh" "$out"
echo "$out"
