#!/usr/bin/env bash
# Publishes the interface and the command line of RomSieve as self-contained single files for one platform, and
# packs them with the README, the license, the licenses of the third-party components they embed
# (THIRD-PARTY-NOTICES.txt and licenses/, written by the publish: see src/ThirdPartyNotices.targets) and the user guide
# in HTML (docs/index.html, built by scripts/build-docs.sh) into dist/RomSieve-<version>-<rid>.zip (Windows) or
# .tar.gz (Linux), next to its SHA-256 checksum (.sha256).
# Used by .github/workflows/release.yml; also runs locally (Git Bash on Windows).
#
#   scripts/package-release.sh <rid> <version> [output directory]
#   scripts/package-release.sh win-x64 1.2.0
#   scripts/package-release.sh linux-x64 1.2.0-beta1 dist
#
# Build a Linux archive on Linux: on a Windows file system, the executables would lose their execute permission.
# The guide is built with pandoc, unless DOCS_HTML_DIR names a guide already built (the release workflow builds it once).
# The tools are checked before the build, which takes minutes: a missing one is named at once, not at the end.
set -euo pipefail

rid="${1:?usage: package-release.sh <rid> <version> [output directory]}"
version="${2:?usage: package-release.sh <rid> <version> [output directory]}"

case "$rid" in
  win-*) exe=".exe" ;;
  *) exe="" ;;
esac

# Prints the first tool able to write a zip archive whose entries use "/" separators, or nothing
zip_tool() {
  if command -v zip >/dev/null 2>&1; then
    echo zip
  # On Windows, python3 and python may be the Microsoft Store shortcut, which only offers to install Python
  elif python3 -c "" >/dev/null 2>&1; then
    echo python3
  elif python -c "" >/dev/null 2>&1; then
    echo python
  elif command -v pwsh >/dev/null 2>&1; then
    echo pwsh
  fi
}

missing=()
command -v dotnet >/dev/null 2>&1 || missing+=("the .NET 10 SDK (dotnet)")
if [ -z "${DOCS_HTML_DIR:-}" ] && ! command -v "${PANDOC:-pandoc}" >/dev/null 2>&1; then
  missing+=("pandoc 3.1 or later, for the user guide (or its path in the PANDOC variable)")
fi
if [ -n "$exe" ]; then
  zipper="$(zip_tool)"
  [ -n "$zipper" ] || missing+=("zip, Python 3 or PowerShell 7 (pwsh), to write the .zip archive")
else
  command -v tar >/dev/null 2>&1 || missing+=("tar")
fi
command -v sha256sum >/dev/null 2>&1 || missing+=("sha256sum")
if [ ${#missing[@]} -gt 0 ]; then
  echo "error: missing tools:" >&2
  printf '  - %s\n' "${missing[@]}" >&2
  exit 1
fi
if [ -z "$exe" ]; then
  case "$(uname -s)" in
    MINGW* | MSYS* | CYGWIN*)
      echo "error: build the $rid archive on Linux: from Git Bash, its executables would lose their execute permission" >&2
      exit 1
      ;;
  esac
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="${3:-$root/dist}"
mkdir -p "$out"
out="$(cd "$out" && pwd)"

name="RomSieve-$version-$rid"
stage="$out/$name"
rm -rf "$stage"
mkdir -p "$stage"

publish() {
  dotnet publish "$root/$1" -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:Version="$version" -p:DebugType=none -p:DebugSymbols=false \
    --nologo -v q -o "$stage"
}

publish src/RomSieve.App/RomSieve.App.csproj
mv -f "$stage/RomSieve.App$exe" "$stage/RomSieve$exe"
publish src/RomSieve.Cli/RomSieve.Cli.csproj
cp "$root/README.md" "$root/LICENSE" "$stage/"

# Only the two executables, the README, the license, the third-party notices and the guide are shipped
find "$stage" -maxdepth 1 -type f ! -name "RomSieve$exe" ! -name "romsieve-cli$exe" \
  ! -name README.md ! -name LICENSE ! -name THIRD-PARTY-NOTICES.txt -delete

# The licenses of the embedded components must accompany every copy: an archive without them is never built
for notices in THIRD-PARTY-NOTICES.txt licenses/dotnet-runtime-THIRD-PARTY-NOTICES.txt; do
  if [ ! -s "$stage/$notices" ]; then
    echo "error: $notices is missing from the published files" >&2
    exit 1
  fi
done
chmod 644 "$stage/THIRD-PARTY-NOTICES.txt" "$stage"/licenses/*

if [ -n "${DOCS_HTML_DIR:-}" ]; then
  cp -r "$DOCS_HTML_DIR" "$stage/docs"
else
  bash "$root/scripts/build-docs.sh" "$stage/docs" > /dev/null
fi
# The README of the archive leads to the HTML pages of the guide instead of the Markdown ones
sed -i -E -e 's#\(docs/README\.md\)#(docs/index.html)#g' -e 's#\((docs/(en|fr)/[^)#]*)\.md([)#])#(\1.html\3#g' "$stage/README.md"

cd "$out"
if [ -n "$exe" ]; then
  archive="$name.zip"
  rm -f "$archive"
  # Entries must use "/" separators to extract everywhere (Windows PowerShell 5 would write "\")
  case "$zipper" in
    zip) zip -q -r "$archive" "$name" ;;
    python3 | python) "$zipper" -m zipfile -c "$archive" "$name" ;;
    pwsh) pwsh -NoProfile -Command "Compress-Archive -Path '$name' -DestinationPath '$archive'" ;;
  esac
else
  archive="$name.tar.gz"
  chmod +x "$name/RomSieve" "$name/romsieve-cli"
  tar -czf "$archive" "$name"
fi

# Standard "<hash>  <name>" line, checked by: sha256sum -c <archive>.sha256
sha256sum "$archive" | sed 's/ \*/  /' > "$archive.sha256"
rm -rf "$stage"
echo "$out/$archive"
