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
  path=${path%$'\r'}
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

"${PYTHON:-python3}" tools/art/write_art_files.py

# What players already download never changes: every file the base branch's table names is still
# named, at the same commit, with the same bytes. The base is the merge base with master (on a
# push to master, the commit before the merge).
base=$(git merge-base origin/master HEAD 2>/dev/null || true)
if [ "$base" = "$(git rev-parse HEAD)" ]; then
  base=$(git rev-parse HEAD^1 2>/dev/null || true)
fi

base_failures=0
if [ -n "$base" ] && git cat-file -e "$base:$table" 2>/dev/null; then
  while read -r commit sha length path; do
    path=${path%$'\r'}
    case "$commit" in
      '' | '#'*) continue ;;
    esac

    if ! grep -qxF "$commit $sha $length $path" <(tr -d '\r' < "$table"); then
      echo "::error file=$table::$path was hosted at $commit as $length bytes, $sha, before this change; hosted files never change or go"
      base_failures=$((base_failures + 1))
    fi
  done < <(git show "$base:$table")
  echo "Checked against the table at $base: nothing hosted there changed or went, unless reported above."
else
  echo "No earlier table to check against (no base commit, or the base has no $table)."
fi

if [ "$base_failures" -ne 0 ]; then
  echo "$base_failures files the base branch hosted changed or went."
fi

if [ "$failures" -ne 0 ]; then
  echo "$failures of $checked hosted files failed their pin check."
fi

if [ "$failures" -ne 0 ] || [ "$base_failures" -ne 0 ]; then
  exit 1
fi

echo "$checked hosted files: every pin is in this branch's history and holds the table's bytes."
