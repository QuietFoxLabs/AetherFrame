#!/usr/bin/env bash
# The server health monitor (.github/workflows/server-health.yml; decision "Watching the server" in
# docs/networking/DecisionRegister.md; docs/networking/Runbook.md, "Health alerts"). It asks the
# deployed server's GET /v1/health, which answers exactly {"worker":bool,"images":bool,"backup":bool},
# and keeps one issue labelled server-health open while a check fails.
#
#   server-health.sh run
#       Skips while a deploy runs. Otherwise looks at the server, and when a check fails, looks again
#       RECHECK_SECONDS later: a check is failing when it failed both times. Then opens, updates,
#       comments on or closes the alert issue. With DRY_RUN=true it prints the decision and changes
#       nothing. It fails only when it couldn't act (an API error, an alert issue it can't read),
#       never because the server is unhealthy.
#
#   server-health.sh decide
#       The decision alone, for one look, with nothing fetched. Prints the action and the failing
#       checks on one line, as run would act on them: "none", "open worker", "update images,backup",
#       "comment worker", "close", or "error" (with the reason on standard error, and exit code 1).
#         HEALTH_CODE    the HTTP status of /v1/health, 000 when there was no answer
#         HEALTH_BODY    its body
#         STATUS_CODE    the HTTP status of /v1/status, needed when /v1/health didn't answer 200 with
#                        a valid body
#         ALERT_CHECKS   the checks the open alert issue lists, comma-separated; empty when none is
#                        open
#
# The checks: worker, images and backup are /v1/health's own values, failing when false;
# "unreachable" is a server that didn't answer (nor did /v1/status); "answer" is a server whose
# /v1/status answers but whose /v1/health answered with something else (an error, a timeout, or a
# body that isn't exactly the three values). A /v1/health that answers 404 is a server deployed
# before it existed, judged by /v1/status alone.
#
# The issue says only what /v1/health says, that the server didn't answer or answered with something
# else, and fixed text: never an answer's body, curl's output, an address, or a time other than the
# checks' UTC times. Nothing here prints an answer's body.
#
# Environment for run: GITHUB_REPOSITORY (OWNER/REPO) and GH_TOKEN; DRY_RUN ("true" or "false");
# for tests, NOW (an ISO 8601 UTC time, such as 2026-10-02T10:22:00Z) and RECHECK_SECONDS (120).
set -euo pipefail
# Command substitutions, such as count="$(gh api ... | jq ...)", stop on a failed command too.
shopt -s inherit_errexit

# The sharing server the plugin talks to (AetherFrame/Services/Network/Transport/SharingDeployment.cs).
# test-server-health.sh checks that the two agree.
host=plates.aetherframe.dev
# Who the alert is for: mentioned and assigned.
owner=richhiiee
label=server-health
# The alert issue's first three lines: the marker, then the state the next run reads back.
marker='<!-- server-health -->'
checks_pattern='^<!-- server-health-checks: ([a-z,]+) -->$'
since_pattern='^<!-- server-health-since: ([0-9TZ:-]+) -->$'
last_checked_prefix='- Last checked: '
iso_pattern='^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$'
# Every check, in the order they are listed.
all_checks=(unreachable answer worker images backup)

fail() {
  echo "::error::$*" >&2
  exit 1
}

# The time now, ISO 8601 UTC: NOW when it is set (tests), the clock otherwise.
now() {
  if [[ -n ${NOW:-} ]]; then
    printf '%s\n' "$NOW"
  else
    date -u +%Y-%m-%dT%H:%M:%SZ
  fi
}

# ISO 8601 UTC time $1 as people read it: 2026-10-02 10:22 UTC.
readable() {
  date -u -d "$1" '+%Y-%m-%d %H:%M UTC'
}

# Checks list $1 (comma-separated) in the order of all_checks, without repeats. Returns 1 for a name
# that isn't a check.
canonical_checks() {
  local name given out=()
  IFS=, read -ra given <<< "$1"
  for name in "${given[@]}"; do
    if [[ " ${all_checks[*]} " != *" $name "* ]]; then
      return 1
    fi
  done
  for name in "${all_checks[@]}"; do
    if [[ ",$1," == *",$name,"* ]]; then
      out+=("$name")
    fi
  done
  (IFS=,; printf '%s\n' "${out[*]}")
}

# The checks in both lists $1 and $2 (comma-separated).
common_checks() {
  local name out=()
  for name in "${all_checks[@]}"; do
    if [[ ",$1," == *",$name,"* && ",$2," == *",$name,"* ]]; then
      out+=("$name")
    fi
  done
  (IFS=,; printf '%s\n' "${out[*]}")
}

# Asks https://$host$1 once, writing the body to file $2. Prints the HTTP status, or 000 when there
# was no answer: no connection, a timeout, or an answer over 4 KiB. Redirects are never followed, and
# curl's own messages are dropped.
fetch() {
  local code
  : > "$2"
  if ! code="$(curl --proto =https --max-time 20 --max-filesize 4096 --silent --output "$2" --write-out '%{http_code}' "https://$host$1" 2>/dev/null)"; then
    code=000
  fi
  if [[ ! $code =~ ^[0-9]{3}$ ]]; then
    code=000
  fi
  printf '%s\n' "$code"
}

# What /v1/health's body (file $1) says: "ok:" and the names of its false values, comma-separated,
# when it is one JSON object with exactly the keys worker, images and backup, each true or false;
# "bad" for anything else. jq's messages are dropped, since they can quote the body.
parse_health() {
  local result
  if ! result="$(head -c 4096 "$1" | jq --raw-output --slurp '
      if length != 1 then "bad"
      else .[0]
        | if type != "object" then "bad"
          elif keys != ["backup", "images", "worker"] then "bad"
          elif any(.[]; type != "boolean") then "bad"
          else "ok:" + ([to_entries[] | select(.value == false) | .key] | join(","))
          end
      end' 2>/dev/null)"; then
    result=bad
  fi
  if [[ $result == ok:* ]]; then
    printf 'ok:%s\n' "$(canonical_checks "${result#ok:}")"
  else
    printf 'bad\n'
  fi
}

# The failing checks one look at the server shows, comma-separated and empty when healthy, from
# /v1/health's HTTP status $1 and parsed body $2 (parse_health), and /v1/status's HTTP status $3.
# Prints "status?" when only /v1/status can tell and $3 is empty.
judge() {
  local health_http="$1" parsed="$2" status_http="$3"
  if [[ $health_http == 200 && $parsed == ok:* ]]; then
    printf '%s\n' "${parsed#ok:}"
  elif [[ -z $status_http ]]; then
    printf 'status?\n'
  elif [[ $status_http != 200 ]]; then
    printf 'unreachable\n'
  elif [[ $health_http == 404 ]]; then
    printf '\n'
  else
    printf 'answer\n'
  fi
}

# The action for failing checks $1 (empty when healthy; "mixed" when checks failed both times but no
# one check did) and the open alert issue's checks $2 (empty when none is open).
choose() {
  local failing="$1" listed="$2"
  if [[ $failing == mixed ]]; then
    echo none
  elif [[ -z $failing && -n $listed ]]; then
    echo close
  elif [[ -z $failing ]]; then
    echo none
  elif [[ -z $listed ]]; then
    echo "open $failing"
  elif [[ $failing == "$listed" ]]; then
    echo "update $failing"
  else
    echo "comment $failing"
  fi
}

# One look at the server: prints its failing checks, comma-separated, empty when healthy.
look() {
  local health_http parsed status_http failing
  health_http="$(fetch /v1/health "$scratch/health")"
  parsed="$(parse_health "$scratch/health")"
  failing="$(judge "$health_http" "$parsed" '')"
  if [[ $failing == 'status?' ]]; then
    status_http="$(fetch /v1/status "$scratch/status")"
    failing="$(judge "$health_http" "$parsed" "$status_http")"
  fi
  printf '%s\n' "$failing"
}

# Failing checks $1 (as look prints them, or "mixed") in words, for the log.
in_words() {
  if [[ -z $1 ]]; then
    echo healthy
  elif [[ $1 == mixed ]]; then
    echo "failing, but no one check failed both times"
  else
    echo "failing: ${1//,/, }"
  fi
}

# Whether a run of the deploy workflow is in progress.
deploy_running() {
  local count
  count="$(gh api "repos/$GITHUB_REPOSITORY/actions/workflows/deploy.yml/runs?status=in_progress" | jq --raw-output '.total_count')" \
    || fail "Couldn't list the deploy workflow's runs."
  [[ $count =~ ^[0-9]+$ ]] || fail "The deploy workflow's runs couldn't be read."
  [[ $count -gt 0 ]]
}

# The open alert issue: of the open issues labelled server-health, the one this workflow opened (as
# the Actions bot) that carries the marker. Sets issue_number, issue_body, issue_checks and
# issue_since, all empty when none is open.
find_alert() {
  local issues count line checks_lines=0 since_lines=0 last_checked_lines=0
  issue_number='' issue_body='' issue_checks='' issue_since=''
  issues="$(gh api "repos/$GITHUB_REPOSITORY/issues?state=open&labels=$label&creator=github-actions%5Bbot%5D&per_page=100")" \
    || fail "Couldn't list the open $label issues."
  issues="$(jq --compact-output '
      if type != "array" then error("not a list") else . end
      | [.[] | select(.pull_request == null and .user.login == "github-actions[bot]")
        | {number, body: ((.body // "") | gsub("\r"; ""))}
        | select(.body | split("\n") | any(. == "<!-- server-health -->"))]' <<< "$issues")" \
    || fail "The open $label issues couldn't be read."
  count="$(jq length <<< "$issues")"
  if [[ $count == 0 ]]; then
    return
  fi
  [[ $count == 1 ]] || fail "$count open $label issues carry its marker. Close all but one."

  issue_number="$(jq --raw-output '.[0].number' <<< "$issues")"
  [[ $issue_number =~ ^[1-9][0-9]*$ ]] || fail "The open $label issue's number couldn't be read."
  issue_body="$(jq --raw-output '.[0].body' <<< "$issues")"
  while IFS= read -r line; do
    if [[ $line =~ $checks_pattern ]]; then
      issue_checks="$(canonical_checks "${BASH_REMATCH[1]}")" || issue_checks=''
      checks_lines=$((checks_lines + 1))
    elif [[ $line =~ $since_pattern ]]; then
      issue_since="${BASH_REMATCH[1]}"
      since_lines=$((since_lines + 1))
    elif [[ $line == "$last_checked_prefix"* ]]; then
      last_checked_lines=$((last_checked_lines + 1))
    fi
  done <<< "$issue_body"
  if [[ $checks_lines != 1 || $since_lines != 1 || $last_checked_lines != 1 || -z $issue_checks || ! $issue_since =~ $iso_pattern ]]; then
    fail "Issue #$issue_number carries the $label marker, but not the state this workflow writes in it. Fix or close it."
  fi
}

# What failing check $1 means, for the issue: fixed text only.
meaning() {
  case "$1" in
    unreachable) echo "**The server didn't answer.** Neither \`/v1/health\` nor \`/v1/status\` answered, so no one can share or view Plates." ;;
    answer) echo "**The server answered with something else.** \`/v1/status\` answers, but \`/v1/health\` didn't give its usual answer." ;;
    worker) echo "**\`worker\` is false.** No image worker run has connected for 2 minutes, so publishes with images get \"try again later\"." ;;
    images) echo "**\`images\` is false.** The server's own test image didn't come back right twice in a row, or not for 3 hours." ;;
    backup) echo "**\`backup\` is false.** The last backup run failed (the day's copy, or the deletion of old copies), or none has finished for 3 hours." ;;
  esac
}

# The alert issue's title, for failing checks $1 since ISO time $2.
alert_title() {
  printf 'Server health: %s failing since %s\n' "${1//,/, }" "$(readable "$2")"
}

# The alert issue's body, for failing checks $1, failing since ISO time $2 and last checked at $3.
alert_body() {
  local name names
  IFS=, read -ra names <<< "$1"
  printf '%s\n' "$marker" "<!-- server-health-checks: $1 -->" "<!-- server-health-since: $2 -->"
  printf '@%s, the server health check failed twice in a row.\n\n' "$owner"
  printf -- '- Failing since: %s\n' "$(readable "$2")"
  printf -- '%s%s\n\n' "$last_checked_prefix" "$(readable "$3")"
  printf 'What failed:\n\n'
  for name in "${names[@]}"; do
    printf -- '- %s\n' "$(meaning "$name")"
  done
  printf '\nWhat to do: see [Health alerts](%s) in the runbook.\n\n' "$runbook"
  printf 'The **Server health** workflow keeps this issue: it updates "Last checked" while the problem lasts, comments when the failing checks change, and closes the issue when the server is healthy again.\n\n'
  printf 'When the failing checks change, it rewrites this text, so put notes in a comment.\n'
}

# Body $1 with its "Last checked" line set to ISO time $2, and nothing else changed.
with_last_checked() {
  local line
  while IFS= read -r line; do
    if [[ $line == "$last_checked_prefix"* ]]; then
      line="$last_checked_prefix$(readable "$2")"
    fi
    printf '%s\n' "$line"
  done <<< "$1"
}

# About how long $1 seconds is, in words: "15 minutes", "1 hour", "3 days".
duration() {
  local seconds="$1" minutes count unit
  minutes=$(((seconds + 30) / 60))
  if ((minutes < 1)); then
    minutes=1
  fi
  if ((minutes < 60)); then
    count=$minutes unit=minute
  elif ((minutes < 48 * 60)); then
    count=$(((minutes + 30) / 60)) unit=hour
  else
    count=$(((minutes + 720) / 1440)) unit=day
  fi
  if ((count != 1)); then
    unit+=s
  fi
  printf '%s %s\n' "$count" "$unit"
}

# Opens the alert issue for failing checks $1, failing since ISO time $2 and last checked at $3.
open_alert() {
  local number title body
  gh label create "$label" --repo "$GITHUB_REPOSITORY" --force --color B60205 \
    --description "An alert from the Server health workflow" > /dev/null \
    || fail "Couldn't create the $label label."
  title="$(alert_title "$1" "$2")"
  body="$(alert_body "$1" "$2" "$3")"
  number="$(gh api --method POST "repos/$GITHUB_REPOSITORY/issues" -f title="$title" -f body="$body" \
    -f "labels[]=$label" -f "assignees[]=$owner" | jq --raw-output '.number')" \
    || fail "Couldn't open the alert issue."
  echo "Opened issue #$number."
}

# The same checks still fail: the open issue's "Last checked" line becomes ISO time $1.
update_alert() {
  local body
  body="$(with_last_checked "$issue_body" "$1")"
  gh api --method PATCH "repos/$GITHUB_REPOSITORY/issues/$issue_number" -f body="$body" > /dev/null \
    || fail "Couldn't update issue #$issue_number."
  echo "Updated issue #$issue_number."
}

# Other checks fail now, $1, at ISO time $2: a comment says so, and the title and body list them.
change_alert() {
  local title body
  gh api --method POST "repos/$GITHUB_REPOSITORY/issues/$issue_number/comments" \
    -f body="At $(readable "$2") the failing checks changed to: ${1//,/, }." > /dev/null \
    || fail "Couldn't comment on issue #$issue_number."
  title="$(alert_title "$1" "$issue_since")"
  body="$(alert_body "$1" "$issue_since" "$2")"
  gh api --method PATCH "repos/$GITHUB_REPOSITORY/issues/$issue_number" -f title="$title" -f body="$body" > /dev/null \
    || fail "Couldn't update issue #$issue_number."
  echo "Commented on and updated issue #$issue_number."
}

# Healthy again at ISO time $1: a comment says so, and the issue is closed as completed.
close_alert() {
  local healthy_at since_at
  healthy_at="$(date -u -d "$1" +%s)"
  since_at="$(date -u -d "$issue_since" +%s)"
  gh api --method POST "repos/$GITHUB_REPOSITORY/issues/$issue_number/comments" \
    -f body="Healthy again at $(readable "$1"); it failed for about $(duration "$((healthy_at - since_at))")." > /dev/null \
    || fail "Couldn't comment on issue #$issue_number."
  gh api --method PATCH "repos/$GITHUB_REPOSITORY/issues/$issue_number" -f state=closed -f state_reason=completed > /dev/null \
    || fail "Couldn't close issue #$issue_number."
  echo "Closed issue #$issue_number."
}

skip() {
  echo "::notice::A deploy is running, so the server isn't checked this time."
  echo "Decision: skip"
}

run() {
  local recheck="${RECHECK_SECONDS:-120}" dry first first_at second failing checked_at action checks
  [[ ${GITHUB_REPOSITORY:-} =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || fail "GITHUB_REPOSITORY is not OWNER/REPO."
  [[ $recheck =~ ^[0-9]{1,4}$ ]] || fail "RECHECK_SECONDS is not a number of seconds."
  [[ -z ${NOW:-} || $NOW =~ $iso_pattern ]] || fail "NOW is not an ISO 8601 UTC time."
  case "${DRY_RUN:-}" in
    true) dry=true ;;
    false | '') dry=false ;;
    *) fail "DRY_RUN is neither true nor false." ;;
  esac
  runbook="https://github.com/$GITHUB_REPOSITORY/blob/master/docs/networking/Runbook.md#health-alerts"

  if deploy_running; then
    skip
    return
  fi

  first_at="$(now)"
  first="$(look)"
  failing="$first"
  checked_at="$first_at"
  if [[ -n $first ]]; then
    echo "$(readable "$first_at"): $(in_words "$first"). Looking again in $recheck seconds."
    sleep "$recheck"
    # A deploy started meanwhile restarts the server: it would look like an outage.
    if deploy_running; then
      skip
      return
    fi
    checked_at="$(now)"
    second="$(look)"
    if [[ -z $second ]]; then
      failing=''
    else
      failing="$(common_checks "$first" "$second")"
      if [[ -z $failing ]]; then
        failing=mixed
      fi
    fi
  fi
  echo "$(readable "$checked_at"): $(in_words "$failing")."

  find_alert
  action="$(choose "$failing" "$issue_checks")"
  echo "Decision: $action"
  if [[ $dry == true ]]; then
    echo "Dry run: nothing changed."
    return
  fi

  read -r action checks <<< "$action"
  case "$action" in
    open) open_alert "$checks" "$first_at" "$checked_at" ;;
    update) update_alert "$checked_at" ;;
    comment) change_alert "$checks" "$checked_at" ;;
    close) close_alert "$checked_at" ;;
  esac
}

decide_error() {
  echo error
  fail "$*"
}

decide() {
  local parsed failing listed
  [[ ${HEALTH_CODE:-} =~ ^[0-9]{3}$ ]] || decide_error "HEALTH_CODE is not an HTTP status."
  [[ -z ${STATUS_CODE:-} || $STATUS_CODE =~ ^[0-9]{3}$ ]] || decide_error "STATUS_CODE is not an HTTP status."
  printf '%s' "${HEALTH_BODY:-}" > "$scratch/health"
  parsed="$(parse_health "$scratch/health")"
  failing="$(judge "$HEALTH_CODE" "$parsed" "${STATUS_CODE:-}")"
  [[ $failing != 'status?' ]] || decide_error "STATUS_CODE is needed: /v1/health didn't answer 200 with a valid body."
  listed="$(canonical_checks "${ALERT_CHECKS:-}")" || decide_error "ALERT_CHECKS lists something that isn't a check."
  choose "$failing" "$listed"
}

scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT

case "${1:-}" in
  run) run ;;
  decide) decide ;;
  *)
    echo "Usage: server-health.sh run | decide" >&2
    exit 2
    ;;
esac
