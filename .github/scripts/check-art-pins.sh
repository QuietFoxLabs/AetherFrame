#!/usr/bin/env bash
# Art on demand ("Art on demand" in docs/networking/DecisionRegister.md): every hosted artwork file
# in AetherFrame/Assets/ArtFiles.txt is downloaded by players from the commit the table pins it to,
# at https://raw.githubusercontent.com/QuietFoxLabs/AetherFrame/<commit>/AetherFrame/Assets/<path>.
# So each pinned commit must be in this branch's history (a squash or rebase that dropped it would
# break every plugin naming it), and must hold exactly the table's bytes at that path. Also checks
# that the table, the compiled table and the files agree (tools/art/write_art_files.py).
# Needs the full history: the workflow checks out with fetch-depth 0.
set -euo pipefail

table=AetherFrame/Assets/ArtFiles.txt
failures=0
checked=0

while read -r commit sha length path; do
  case "$commit" in
    '' | '#'*) continue ;;
  esac

  checked=$((checked + 1))
  if ! git merge-base --is-ancestor "$commit" HEAD; then
    echo "::error file=$table::$path is pinned to $commit, which isn't in this branch's history"
    failures=$((failures + 1))
    continue
  fi

  object="$commit:AetherFrame/Assets/$path"
  if ! size=$(git cat-file -s "$object" 2>/dev/null); then
    echo "::error file=$table::$path isn't at $commit"
    failures=$((failures + 1))
    continue
  fi

  actual=$(git cat-file blob "$object" | sha256sum | cut -d' ' -f1)
  if [ "$actual" != "$sha" ] || [ "$size" != "$length" ]; then
    echo "::error file=$table::$path at $commit is $size bytes, SHA-256 $actual, not the table's $length bytes, $sha"
    failures=$((failures + 1))
  fi
done < "$table"

python3 tools/art/write_art_files.py

if [ "$failures" -ne 0 ]; then
  echo "$failures of $checked hosted files failed their pin check."
  exit 1
fi

echo "$checked hosted files: every pin is in this branch's history and holds the table's bytes."
