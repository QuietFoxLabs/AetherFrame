#!/usr/bin/env bash
# The git and GitHub side of publishing AetherFrame's custom Dalamud repository
# (.github/workflows/publish-custom-repository.yml; docs/CustomRepository.md, "Publishing"). This script
# only gathers facts and files, and writes the one commit. Every decision is made by
# AetherFrame.ReleaseTools, which reads what the script fetched and refuses anything it cannot verify.
#
#   custom-repository.sh prepare <new work directory>
#       Reads the published pluginmaster.json from the publication branch, asks plan-publication which
#       releases the request needs, fetches each one, and runs prepare-publication. The prepared files
#       land in <work>/publication; base, changed and sha256 in <work>/outputs.
#
#   custom-repository.sh publish <new work directory>
#       Everything prepare does, again from scratch. Then it requires the branch to still be at
#       EXPECTED_BASE, the new pluginmaster.json to be EXPECTED_SHA256, and the publication record
#       (summary.json: every release's id, publish time and package hash) to be EXPECTED_SUMMARY_SHA256,
#       which is what the approved run showed, and commits exactly the two prepared files, pushed as a
#       fast-forward.
#
#   custom-repository.sh commit <prepared publication directory> <base commit | none>
#       Only the commit and push of publish, for a publication directory prepared earlier.
#
# Environment:
#   RELEASE_TOOLS       path to the built AetherFrame.ReleaseTools.dll
#   PUBLICATION_BRANCH  the branch pluginMasterUrl serves; the tool refuses any other
#   DEFAULT_BRANCH      the branch releases are cut from
#   VERSION, CHANNEL    the request; ROLLBACK is "true" or "false"
#   GITHUB_REPOSITORY   OWNER/REPO for the GitHub API; GH_TOKEN authenticates the API and the push
#   RUN_URL             optional: the workflow run, recorded in the commit message
#   EXPECTED_BASE, EXPECTED_SHA256, EXPECTED_SUMMARY_SHA256   what publish must find (commit: the SHA-256)
#   GITHUB_OUTPUT, GITHUB_STEP_SUMMARY   optional, set by GitHub Actions
#
# Run it from the root of a clone whose remote "origin" is the repository.
set -euo pipefail
# Command substitutions, such as base="$(read_branch ...)", stop on a failed command too.
shopt -s inherit_errexit

version_pattern='^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})$'
branch_pattern='^[a-z0-9]+(-[a-z0-9]+)*$'
commit_pattern='^[0-9a-f]{40}$'
sha256_pattern='^[0-9a-f]{64}$'
name_pattern='^[A-Za-z][A-Za-z0-9._-]{0,63}$'

fail() {
  echo "::error::$*" >&2
  exit 1
}

require_env() {
  local name
  for name in "$@"; do
    [ -n "${!name:-}" ] || fail "$name is not set."
  done
}

# The value of name=value line $1 in outputs file $2.
output_value() {
  sed -n "s/^$1=//p" "$2"
}

# Prints the publication branch's commit, or "none" when the branch does not exist yet, and copies the
# pluginmaster.json it holds to $1/pluginmaster.json.
read_branch() {
  local out="$1" listing status=0 base
  mkdir -p "$out"
  listing="$(git ls-remote --exit-code --heads origin "refs/heads/$PUBLICATION_BRANCH")" || status=$?
  if [ "$status" -eq 2 ]; then
    echo none
    return
  fi

  [ "$status" -eq 0 ] || fail "could not read origin/$PUBLICATION_BRANCH (git ls-remote exit code $status)."
  if [ "$(printf '%s\n' "$listing" | wc -l)" -ne 1 ] || [ "$(printf '%s' "$listing" | cut -f2)" != "refs/heads/$PUBLICATION_BRANCH" ]; then
    fail "origin lists more than one ref for $PUBLICATION_BRANCH."
  fi

  base="$(printf '%s' "$listing" | cut -f1)"
  [[ $base =~ $commit_pattern ]] || fail "origin/$PUBLICATION_BRANCH resolves to '$base', not a commit id."
  git fetch --quiet --no-tags origin "+refs/heads/$PUBLICATION_BRANCH:refs/remotes/origin/$PUBLICATION_BRANCH"
  [ "$(git rev-parse "refs/remotes/origin/$PUBLICATION_BRANCH")" = "$base" ] || fail "origin/$PUBLICATION_BRANCH moved while it was being read; run again."
  git cat-file blob "$base:pluginmaster.json" > "$out/pluginmaster.json" \
    || fail "origin/$PUBLICATION_BRANCH has no pluginmaster.json at $base; that branch was not written by this workflow."
  echo "$base"
}

# Fetches release $1 (a version) of plugin $2 into directory $3, laid out as ReleaseVerifier reads it.
fetch_release() {
  local release_version="$1" internal_name="$2" dest="$3" tag object_type commit on_default=false
  [[ $release_version =~ $version_pattern ]] || fail "'$release_version' is not a MAJOR.MINOR.PATCH version."
  [[ $internal_name =~ $name_pattern ]] || fail "'$internal_name' is not a plugin internal name."
  tag="v$release_version"
  mkdir -p "$dest/source"

  # The release, from the endpoint that serves only published releases: a draft is never found here.
  if ! gh api "repos/$GITHUB_REPOSITORY/releases/tags/$tag" > "$dest/release.json"; then
    rm -f "$dest/release.json"
    fail "GitHub has no published release for $tag, or the request failed (see above). A draft release is never eligible: publish it on GitHub first."
  fi

  gh release download "$tag" --repo "$GITHUB_REPOSITORY" --dir "$dest" --pattern "$internal_name-$release_version.zip" --pattern SHA256SUMS.txt \
    || fail "could not download the assets of $tag."

  # The tag as it is on origin: annotated or not, the commit it points at, and whether the default
  # branch contains that commit.
  git fetch --quiet --no-tags --force origin "+refs/tags/$tag:refs/tags/$tag" || fail "origin has no tag $tag."
  object_type="$(git cat-file -t "refs/tags/$tag")"
  commit="$(git rev-parse --verify --quiet "refs/tags/$tag^{commit}")" || fail "$tag does not point at a commit."
  [[ $object_type =~ ^(commit|tag)$ ]] || fail "$tag is a $object_type object."
  [[ $commit =~ $commit_pattern ]] || fail "$tag resolves to '$commit'."
  if git merge-base --is-ancestor "$commit" "refs/remotes/origin/$DEFAULT_BRANCH"; then
    on_default=true
  fi

  printf '{\n  "tag": "%s",\n  "objectType": "%s",\n  "commit": "%s",\n  "defaultBranch": "%s",\n  "onDefaultBranch": %s\n}\n' \
    "$tag" "$object_type" "$commit" "$DEFAULT_BRANCH" "$on_default" > "$dest/tag.json"

  # The release's own sources, byte for byte from the tagged commit: the version, the project file and
  # the changelog the package is checked against.
  git cat-file blob "$commit:Version.props" > "$dest/source/Version.props"
  git cat-file blob "$commit:$internal_name/$internal_name.csproj" > "$dest/source/$internal_name.csproj"
  git cat-file blob "$commit:CHANGELOG.md" > "$dest/source/CHANGELOG.md"
}

prepare() {
  local work="$1" base releases internal_name release_version
  require_env RELEASE_TOOLS PUBLICATION_BRANCH DEFAULT_BRANCH VERSION CHANNEL ROLLBACK GITHUB_REPOSITORY
  [[ $PUBLICATION_BRANCH =~ $branch_pattern ]] || fail "PUBLICATION_BRANCH '$PUBLICATION_BRANCH' is not a publication branch name."
  [[ $DEFAULT_BRANCH =~ $branch_pattern ]] || fail "DEFAULT_BRANCH '$DEFAULT_BRANCH' is not a branch name this script accepts."
  [ ! -e "$work" ] || fail "$work already exists; give a new work directory."
  mkdir -p "$work/releases"

  local plan_current=(--no-current) prepare_current=(--no-current) rollback_option=() run=()
  case "$ROLLBACK" in
    true) rollback_option=(--rollback) ;;
    false) ;;
    *) fail "ROLLBACK is '$ROLLBACK', not true or false." ;;
  esac

  if [ -n "${RUN_URL:-}" ]; then
    run=(--run-url "$RUN_URL")
  fi

  git fetch --quiet --no-tags origin "+refs/heads/$DEFAULT_BRANCH:refs/remotes/origin/$DEFAULT_BRANCH"
  base="$(read_branch "$work/current")"
  if [ "$base" != none ]; then
    plan_current=(--current "$work/current/pluginmaster.json")
    prepare_current=(--current "$work/current/pluginmaster.json" --current-commit "$base")
  fi

  echo "Publication branch: origin/$PUBLICATION_BRANCH is at $base."
  dotnet "$RELEASE_TOOLS" plan-publication --config distribution/repository.json --branch "$PUBLICATION_BRANCH" \
    "${plan_current[@]}" --version "$VERSION" --channel "$CHANNEL" "${rollback_option[@]}" --github-output "$work/plan"
  releases="$(output_value releases "$work/plan")"
  internal_name="$(output_value internal-name "$work/plan")"
  local release_list=()
  read -r -a release_list <<< "$releases"
  [ "${#release_list[@]}" -gt 0 ] || fail "plan-publication named no releases."

  for release_version in "${release_list[@]}"; do
    echo "Fetching v$release_version."
    fetch_release "$release_version" "$internal_name" "$work/releases/v$release_version"
  done

  echo "base=$base" > "$work/outputs"
  dotnet "$RELEASE_TOOLS" prepare-publication --config distribution/repository.json --branch "$PUBLICATION_BRANCH" \
    "${prepare_current[@]}" --version "$VERSION" --channel "$CHANNEL" "${rollback_option[@]}" \
    --releases "$work/releases" --branch-readme distribution/plugin-repository/README.md \
    --output "$work/publication" "${run[@]}" --github-output "$work/outputs"
}

# Commits the two files in $1/branch on top of $2 (or as the branch's first commit) and pushes it.
commit_publication() {
  local publication="$1" base="$2" files actual readme_blob master_blob tree commit remote
  require_env PUBLICATION_BRANCH EXPECTED_SHA256
  [[ $PUBLICATION_BRANCH =~ $branch_pattern ]] || fail "PUBLICATION_BRANCH '$PUBLICATION_BRANCH' is not a publication branch name."
  [[ $EXPECTED_SHA256 =~ $sha256_pattern ]] || fail "EXPECTED_SHA256 is not a SHA-256."
  [ "$base" = none ] || [[ $base =~ $commit_pattern ]] || fail "base '$base' is neither none nor a commit id."

  # Exactly the two prepared files, and the pluginmaster.json that was approved.
  files="$(find "$publication/branch" -mindepth 1 -printf '%P\n' | LC_ALL=C sort | tr '\n' ' ')"
  [ "$files" = "README.md pluginmaster.json " ] || fail "the prepared branch files are '$files', not README.md and pluginmaster.json."
  actual="$(sha256sum "$publication/branch/pluginmaster.json" | cut -d' ' -f1)"
  [ "$actual" = "$EXPECTED_SHA256" ] || fail "the prepared pluginmaster.json is $actual, not the approved $EXPECTED_SHA256."

  # The commit's tree holds those two files and nothing else, byte for byte (no line ending filters).
  readme_blob="$(git hash-object -w --no-filters "$publication/branch/README.md")"
  master_blob="$(git hash-object -w --no-filters "$publication/branch/pluginmaster.json")"
  tree="$(printf '100644 blob %s\tREADME.md\n100644 blob %s\tpluginmaster.json\n' "$readme_blob" "$master_blob" | git mktree)"
  local parent=()
  if [ "$base" != none ]; then
    parent=(-p "$base")
  fi

  commit="$(git commit-tree "$tree" "${parent[@]}" -F "$publication/commit-message.txt")"

  # A plain push, never forced. Git refuses it unless it fast-forwards the branch from $base, so a
  # branch that moved since it was read (or, for the first publication, appeared) is never overwritten.
  # The token reaches git through the GitHub CLI's credential helper, for this one command only.
  git -c credential.helper='' -c 'credential.helper=!gh auth git-credential' push --quiet origin "$commit:refs/heads/$PUBLICATION_BRANCH" \
    || fail "the push was refused, so nothing was written. origin/$PUBLICATION_BRANCH has probably moved since $base was read; run the workflow again."

  remote="$(git ls-remote --heads origin "refs/heads/$PUBLICATION_BRANCH" | cut -f1)"
  [ "$remote" = "$commit" ] || fail "after the push, origin/$PUBLICATION_BRANCH is at '$remote', not $commit."
  [ "$(git cat-file blob "$commit:pluginmaster.json" | sha256sum | cut -d' ' -f1)" = "$EXPECTED_SHA256" ] || fail "the pushed pluginmaster.json is not the approved file."
  echo "Published: origin/$PUBLICATION_BRANCH is now $commit (was $base); pluginmaster.json SHA-256 $EXPECTED_SHA256."
  PUBLISHED_COMMIT="$commit"
}

command="${1:-}"
case "$command" in
  prepare)
    [ $# -eq 2 ] || fail "usage: custom-repository.sh prepare <new work directory>"
    prepare "$2"
    if [ -n "${GITHUB_OUTPUT:-}" ]; then
      cat "$2/outputs" >> "$GITHUB_OUTPUT"
    fi

    if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
      cat "$2/publication/report.md" >> "$GITHUB_STEP_SUMMARY"
    fi
    ;;

  publish)
    [ $# -eq 2 ] || fail "usage: custom-repository.sh publish <new work directory>"
    require_env EXPECTED_BASE EXPECTED_SHA256 EXPECTED_SUMMARY_SHA256
    prepare "$2"
    base="$(output_value base "$2/outputs")"
    sha256="$(output_value sha256 "$2/outputs")"
    summary_sha256="$(output_value summary_sha256 "$2/outputs")"
    [ "$base" = "$EXPECTED_BASE" ] \
      || fail "origin/$PUBLICATION_BRANCH is at $base, not at the $EXPECTED_BASE the approved run prepared from: something else changed it. Nothing was written; run the workflow again."
    [ "$sha256" = "$EXPECTED_SHA256" ] \
      || fail "the pluginmaster.json derived now ($sha256) is not the one the approved run showed ($EXPECTED_SHA256): a release changed in between. Nothing was written; run the workflow again and review the new result."
    # The same file can describe a different ZIP (the file names each release's download address, not
    # its hash), so the whole record must match too: a release asset replaced while the run waited for
    # approval stops the publication even when the replacement is a valid package.
    [ "$summary_sha256" = "$EXPECTED_SUMMARY_SHA256" ] \
      || fail "the publication record derived now ($summary_sha256) is not the one the approved run showed ($EXPECTED_SUMMARY_SHA256): a release or its assets changed in between. Nothing was written; run the workflow again and review the new result."
    commit_publication "$2/publication" "$base"
    if [ -n "${GITHUB_OUTPUT:-}" ]; then
      echo "commit=$PUBLISHED_COMMIT" >> "$GITHUB_OUTPUT"
    fi

    if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
      {
        echo "## Published"
        echo
        echo "Commit \`$PUBLISHED_COMMIT\` on \`$PUBLICATION_BRANCH\` (previous: \`$base\`), pluginmaster.json SHA-256 \`$EXPECTED_SHA256\`."
        echo "Dalamud sees it once GitHub's raw file cache expires, within about five minutes."
      } >> "$GITHUB_STEP_SUMMARY"
    fi
    ;;

  commit)
    [ $# -eq 3 ] || fail "usage: custom-repository.sh commit <prepared publication directory> <base commit | none>"
    commit_publication "$2" "$3"
    ;;

  *)
    fail "usage: custom-repository.sh prepare|publish <new work directory>, or commit <publication directory> <base | none>"
    ;;
esac
