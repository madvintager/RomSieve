#!/usr/bin/env bash
# Checks the HTML pages of a folder (the user guide of scripts/build-docs.sh, the website of scripts/build-site.sh):
# every local link and image must exist, and every anchor must be an id of its page. A target outside the folder (the
# README of a release archive) and the links to the web are not checked.
#
#   scripts/docs/check-links.sh <folder>
set -euo pipefail

out="$(cd "${1:?usage: check-links.sh <folder>}" && pwd)"
errors=0
while IFS= read -r html; do
  dir="$(dirname "$html")"
  while IFS= read -r ref; do
    case "$ref" in
      http:* | https:* | mailto:*) continue ;;
    esac
    path="${ref%%#*}"
    anchor=""
    [[ "$ref" == *"#"* ]] && anchor="${ref#*#}"
    if [ -z "$path" ]; then
      file="$html"
    else
      file="$(cd "$dir" && realpath -m "$path")"
      case "$file" in
        "$out"/*) ;;
        *) continue ;;
      esac
    fi
    if [ ! -e "$file" ]; then
      echo "${html#"$out"/}: missing target $ref" >&2
      errors=$((errors + 1))
    elif [ -n "$anchor" ] && ! grep -q "id=\"$anchor\"" "$file"; then
      echo "${html#"$out"/}: missing anchor $ref" >&2
      errors=$((errors + 1))
    fi
  done < <(grep -oE '(href|src|srcset)="[^"]*"' "$html" | sed -E 's/^(href|src|srcset)="(.*)"$/\2/')
done < <(find "$out" -name '*.html')

if [ "$errors" -gt 0 ]; then
  echo "$errors broken link(s) in $out" >&2
  exit 1
fi
