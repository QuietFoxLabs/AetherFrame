#!/usr/bin/env bash
# Art on demand ("Art on demand" in docs/networking/DecisionRegister.md): before a release, every
# hosted artwork file in AetherFrame/Assets/ArtFiles.txt is downloaded from the address players'
# plugins use, https://raw.githubusercontent.com/QuietFoxLabs/AetherFrame/<commit>/AetherFrame/Assets/<path>,
# and must be exactly the table's bytes. Downloads about 160 MB; needs curl and sha256sum.
set -euo pipefail

table=AetherFrame/Assets/ArtFiles.txt
scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT
failures=0
checked=0

while read -r commit sha length path; do
  path=${path%$'\r'}
  case "$commit" in
    '' | '#'*) continue ;;
  esac

  checked=$((checked + 1))
  url="https://raw.githubusercontent.com/QuietFoxLabs/AetherFrame/$commit/AetherFrame/Assets/$path"
  file="$scratch/file.png"
  rm -f "$file"
  if ! status=$(curl --silent --show-error --max-time 120 --retry 3 --retry-delay 5 --retry-all-errors --output "$file" --write-out '%{http_code}' "$url"); then
    echo "::error::$path couldn't be downloaded from $url"
    failures=$((failures + 1))
    continue
  fi

  if [ "$status" != "200" ]; then
    echo "::error::$path answered $status at $url"
    failures=$((failures + 1))
    continue
  fi

  size=$(wc -c < "$file" | tr -d ' ')
  actual=$(sha256sum "$file" | cut -d' ' -f1)
  if [ "$size" != "$length" ] || [ "$actual" != "$sha" ]; then
    echo "::error::$path at $url is $size bytes, SHA-256 $actual, not the table's $length bytes, $sha"
    failures=$((failures + 1))
  fi
done < "$table"

if [ "$failures" -ne 0 ]; then
  echo "$failures of $checked hosted files can't be downloaded as the table says."
  exit 1
fi

echo "$checked hosted files: each downloads from its pinned address as exactly the table's bytes."
