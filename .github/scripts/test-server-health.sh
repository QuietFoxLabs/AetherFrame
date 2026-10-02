#!/usr/bin/env bash
# Tests the server health monitor, .github/scripts/server-health.sh, with a fake gh and a fake
# curl first on PATH (.github/workflows/build.yml runs it). Each case sets the answers the server
# and GitHub give, runs the monitor with the time fixed and no wait between its two looks, and
# checks what it decided and every change it asked GitHub for. One case checks that the server it
# asks is the one the plugin talks to (SharingDeployment.cs). Needs jq. Run it from the root of the
# repository.
set -euo pipefail
shopt -s inherit_errexit

if ! command -v jq > /dev/null; then
  echo "The monitor and these tests need jq."
  exit 1
fi

script="$PWD/.github/scripts/server-health.sh"
deployment=AetherFrame/Services/Network/Transport/SharingDeployment.cs
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir "$work/bin"

# The fake gh records each call as one line of $FAKE_DIR/gh.log (a newline in an argument written as
# \n) and each -f field's value in $FAKE_DIR/call-<line>.<field>. It answers the deploy runs from
# runs.json (runs.<n>.json for the nth time asked, when that file exists), the open issues from
# issues.json, and each write with issue #41; fail-runs, fail-issues and fail-writes make those fail.
cat > "$work/bin/gh" <<'FAKE'
#!/usr/bin/env bash
set -euo pipefail
n=$(($(wc -l < "$FAKE_DIR/gh.log") + 1))
line=gh
for arg in "$@"; do
  line+=" ${arg//$'\n'/\\n}"
done
printf '%s\n' "$line" >> "$FAKE_DIR/gh.log"
write=false
previous=''
for arg in "$@"; do
  if [[ $arg == --method || $arg == -X ]]; then
    write=true
  fi
  if [[ $previous == -f ]]; then
    field="${arg%%=*}"
    printf '%s' "${arg#*=}" > "$FAKE_DIR/call-$n.${field//[^a-z_]/}"
  fi
  previous="$arg"
done
if [[ ${1:-} == label && ${2:-} == create ]]; then
  exit 0
fi
if [[ ${1:-} != api ]]; then
  echo "gh $*" >> "$FAKE_DIR/unexpected"
  exit 99
fi
if [[ $write == true ]]; then
  if [[ -e $FAKE_DIR/fail-writes ]]; then
    echo "HTTP 500" >&2
    exit 1
  fi
  echo '{"number":41}'
  exit 0
fi
case "${2:-}" in
  */actions/workflows/deploy.yml/runs'?status=in_progress')
    if [[ -e $FAKE_DIR/fail-runs ]]; then
      echo "HTTP 502" >&2
      exit 1
    fi
    asked=$(grep -c -F 'actions/workflows/deploy.yml/runs' "$FAKE_DIR/gh.log")
    if [[ -e $FAKE_DIR/runs.$asked.json ]]; then
      cat "$FAKE_DIR/runs.$asked.json"
    else
      cat "$FAKE_DIR/runs.json"
    fi
    ;;
  */issues'?state=open&labels=server-health&creator=github-actions%5Bbot%5D&per_page=100')
    if [[ -e $FAKE_DIR/fail-issues ]]; then
      echo "HTTP 502" >&2
      exit 1
    fi
    cat "$FAKE_DIR/issues.json"
    ;;
  *)
    echo "gh $*" >> "$FAKE_DIR/unexpected"
    exit 99
    ;;
esac
FAKE

# The fake curl records each call as one line of $FAKE_DIR/curl.log, and gives the nth request for
# /v1/<endpoint> the answer in <endpoint>.<n>.code (the HTTP status and curl's exit status) and
# <endpoint>.<n>.body.
cat > "$work/bin/curl" <<'FAKE'
#!/usr/bin/env bash
set -euo pipefail
printf 'curl %s\n' "$*" >> "$FAKE_DIR/curl.log"
output=''
url=''
while (($#)); do
  case "$1" in
    --output | -o) output="$2"; shift 2 ;;
    --write-out | -w | --proto | --max-time | --max-filesize) shift 2 ;;
    https://* | http://*) url="$1"; shift ;;
    *) shift ;;
  esac
done
endpoint="${url##*/}"
n=1
if [[ -e $FAKE_DIR/$endpoint.count ]]; then
  n=$(($(cat "$FAKE_DIR/$endpoint.count") + 1))
fi
printf '%s\n' "$n" > "$FAKE_DIR/$endpoint.count"
if [[ ! -e $FAKE_DIR/$endpoint.$n.code ]]; then
  echo "curl $url, request $n" >> "$FAKE_DIR/unexpected"
  exit 99
fi
read -r code status < "$FAKE_DIR/$endpoint.$n.code"
if [[ -n $output ]]; then
  cat "$FAKE_DIR/$endpoint.$n.body" > "$output"
fi
printf '%s' "$code"
exit "$status"
FAKE

chmod +x "$work/bin/gh" "$work/bin/curl"
for tool in gh curl; do
  if [[ "$(PATH="$work/bin:$PATH" command -v "$tool")" != "$work/bin/$tool" ]]; then
    echo "The fake $tool isn't the first $tool on PATH, so the monitor can't be tested here."
    exit 1
  fi
done

# Not the real repository: even a real gh couldn't change anything there.
repository=test-owner/test-repo
healthy='{"worker":true,"images":true,"backup":true}'
no_worker='{"worker":false,"images":true,"backup":true}'
no_images='{"worker":true,"images":false,"backup":true}'
# Put in the bodies of answers: no output and no change may ever hold it.
secret=fd1f6b2c-health-body
cases=0
failures=0

# Starts case $1: no deploy running, no alert issue, no answers yet, and the time 10:22 UTC.
begin() {
  name="$1"
  cases=$((cases + 1))
  case_failures=$failures
  dir="$work/case-$cases"
  now=2026-10-02T10:22:00Z
  mkdir "$dir"
  printf '%s\n' '{"total_count":0,"workflow_runs":[]}' > "$dir/runs.json"
  printf '%s\n' '[]' > "$dir/issues.json"
  : > "$dir/gh.log"
  : > "$dir/curl.log"
}

# The next answer to a request for /v1/$1: HTTP status $2 (000 for none) with body $3, curl exiting
# with $4 (0 by default).
answer() {
  local n=1
  while [[ -e $dir/$1.$n.code ]]; do
    n=$((n + 1))
  done
  printf '%s %s\n' "$2" "${4:-0}" > "$dir/$1.$n.code"
  printf '%s' "${3:-}" > "$dir/$1.$n.body"
}

# The open issues: #41, by the Actions bot, with the body in file $1; #40, with the marker but by
# someone else; and #39, a pull request by the bot with the marker. Only #41 counts.
issue_list() {
  jq --null-input --rawfile body "$1" '[
      {number: 41, user: {login: "github-actions[bot]"}, body: $body},
      {number: 40, user: {login: "someone"}, body: "<!-- server-health -->"},
      {number: 39, user: {login: "github-actions[bot]"}, pull_request: {}, body: "<!-- server-health -->"}
    ]' > "$dir/issues.json"
}

# An open alert issue, #41, for failing checks $1 since ISO time $2, in the form the monitor writes,
# with a line the owner added.
alert() {
  printf '%s\n' '<!-- server-health -->' "<!-- server-health-checks: $1 -->" "<!-- server-health-since: $2 -->" \
    '@richhiiee, the server health check failed twice in a row.' '' '- Failing since: earlier' \
    '- Last checked: earlier' '' 'A line the owner added.' > "$dir/alert.body"
  issue_list "$dir/alert.body"
}

# Runs the monitor for this case with DRY_RUN $1: its output in $dir/out, its exit status in $status.
monitor() {
  status=0
  PATH="$work/bin:$PATH" FAKE_DIR="$dir" GH_TOKEN=not-a-token GITHUB_REPOSITORY="$repository" NOW="$now" \
    RECHECK_SECONDS=0 DRY_RUN="$1" bash "$script" run > "$dir/out" 2>&1 || status=$?
}

# Checks that the command in $2... succeeds; $1 says what that shows.
expect() {
  local what="$1"
  shift
  if ! "$@"; then
    echo "FAIL: $name: $what"
    failures=$((failures + 1))
  fi
}

# The decision the monitor printed.
decided() {
  sed -n 's/^Decision: //p' "$dir/out"
}

# How many changes the monitor asked GitHub for: writes through the API, and labels created.
writes() {
  grep -c -e '^gh api --method ' -e '^gh label create ' "$dir/gh.log" || true
}

# The line in gh.log of the one call of gh api --method $1 to path $2 in the repository, or nothing
# when there isn't exactly one.
call() {
  local found
  found="$(grep -n -F -e "gh api --method $1 repos/$repository/$2 " "$dir/gh.log" | cut -d: -f1)" || true
  if [[ $found =~ ^[0-9]+$ ]]; then
    printf '%s\n' "$found"
  fi
}

# Field $2 of the gh call on line $1 of gh.log, as the monitor sent it.
field() {
  cat "$dir/call-$1.$2" 2>/dev/null || true
}

# How many times the monitor printed or sent part of an answer's body.
leaks() {
  cat "$dir/out" "$dir/gh.log" "$dir"/call-* 2>/dev/null | grep -c -F -e "$secret" || true
}

# How many requests to the server weren't HTTPS only, bounded in time and size, and without
# following redirects.
loose_requests() {
  local line count=0
  while IFS= read -r line; do
    if [[ $line != *' --proto =https '* || $line != *' --max-time 20 '* || $line != *' --max-filesize 4096 '* || $line == *' --location'* || $line == *' -L'* ]]; then
      count=$((count + 1))
    fi
  done < "$dir/curl.log"
  printf '%s\n' "$count"
}

# The checks every case shares, and the monitor's output when one of the case's checks failed.
finish() {
  expect "asks nothing unexpected" test ! -e "$dir/unexpected"
  expect "never prints or sends an answer's body" test "$(leaks)" = 0
  expect "asks the server over HTTPS only, bounded, without following redirects" test "$(loose_requests)" = 0
  if ((failures > case_failures)) && [[ -e $dir/out ]]; then
    sed 's/^/    | /' "$dir/out"
  fi
}

# Checks that decide, with the environment in $2..., prints $1 and exits 0, or exits 1 for "error".
decides() {
  local wanted="$1" decision code=0 wanted_code=0
  shift
  decision="$(env -u HEALTH_CODE -u HEALTH_BODY -u STATUS_CODE -u ALERT_CHECKS "$@" bash "$script" decide 2> /dev/null)" || code=$?
  if [[ $wanted == error ]]; then
    wanted_code=1
  fi
  if [[ $decision != "$wanted" || $code != "$wanted_code" ]]; then
    echo "FAIL: $name: decide with $* printed '$decision' and exited $code, not '$wanted'"
    failures=$((failures + 1))
  fi
}

begin "healthy, no alert issue: nothing"
answer health 200 "$healthy"
monitor false
expect "exits 0" test "$status" = 0
expect "decides none" test "$(decided)" = none
expect "changes nothing" test "$(writes)" = 0
expect "looks once" test "$(cat "$dir/health.count")" = 1
expect "doesn't ask /v1/status" test ! -e "$dir/status.count"
finish

begin "the server it asks is the plugin's (SharingDeployment.cs)"
plugin_host="$(sed -n 's/.*DeploymentName\.Parse("\([^"]*\)").*/\1/p' "$deployment")"
expect "SharingDeployment.cs names one server" test "$(printf '%s\n' "$plugin_host" | grep -c -E '^[a-z0-9.-]+$')" = 1
answer health 200 "$no_worker"
answer health 200 "$no_worker"
monitor true
expect "asks /v1/health there, both times" test "$(grep -c -F -e " https://$plugin_host/v1/health" "$dir/curl.log")" = 2
expect "asks nothing else" test "$(grep -c -v -F -e " https://$plugin_host/v1/health" "$dir/curl.log")" = 0
finish

begin "one failure, then healthy: nothing"
answer health 200 "$no_worker"
answer health 200 "$healthy"
monitor false
expect "exits 0" test "$status" = 0
expect "decides none" test "$(decided)" = none
expect "changes nothing" test "$(writes)" = 0
expect "looks twice" test "$(cat "$dir/health.count")" = 2
finish

begin "two failures, no alert issue: opens one"
answer health 200 "$no_worker"
answer health 200 "$no_worker"
monitor false
opened="$(call POST issues)"
label_line="$(grep -n -F -e "gh label create server-health --repo $repository --force " "$dir/gh.log" | cut -d: -f1)" || true
expect "exits 0" test "$status" = 0
expect "decides to open one for worker" test "$(decided)" = "open worker"
expect "opens one issue" test -n "$opened"
expect "creates the label first" test "${label_line:-99}" -lt "${opened:-0}"
expect "titles it" test "$(field "$opened" title)" = "Server health: worker failing since 2026-10-02 10:22 UTC"
expect "labels it" test "$(field "$opened" labels)" = server-health
expect "assigns the owner" test "$(field "$opened" assignees)" = richhiiee
expect "mentions the owner" grep -q -F -e '@richhiiee,' "$dir/call-$opened.body"
expect "carries the marker first" test "$(head -n 1 "$dir/call-$opened.body")" = '<!-- server-health -->'
expect "records the checks" grep -q -x -F -e '<!-- server-health-checks: worker -->' "$dir/call-$opened.body"
expect "records the time" grep -q -x -F -e '<!-- server-health-since: 2026-10-02T10:22:00Z -->' "$dir/call-$opened.body"
expect "says when it was last checked" grep -q -x -F -e '- Last checked: 2026-10-02 10:22 UTC' "$dir/call-$opened.body"
expect "says what failed" grep -q -F -e 'No image worker run has connected for 2 minutes' "$dir/call-$opened.body"
expect "links the runbook" grep -q -F -e "(https://github.com/$repository/blob/master/docs/networking/Runbook.md#health-alerts)" "$dir/call-$opened.body"
expect "ends by saying it rewrites the text, so notes go in comments" test "$(tail -n 1 "$dir/call-$opened.body")" = 'When the failing checks change, it rewrites this text, so put notes in a comment.'
expect "changes nothing else" test "$(writes)" = 2
finish
cp "$dir/call-$opened.body" "$work/opened.body" 2>/dev/null || : > "$work/opened.body"

begin "the issue it opened, read back 15 minutes later: Last checked only"
now=2026-10-02T10:37:00Z
issue_list "$work/opened.body"
answer health 200 "$no_worker"
answer health 200 "$no_worker"
monitor false
edited="$(call PATCH issues/41)"
expect "exits 0" test "$status" = 0
expect "decides to update it" test "$(decided)" = "update worker"
expect "edits only its Last checked line" cmp -s <(sed 's/^- Last checked: .*/- Last checked: 2026-10-02 10:37 UTC/' "$work/opened.body") "$dir/call-$edited.body"
expect "changes nothing else" test "$(writes)" = 1
finish

begin "the same failure, alert issue open: Last checked only"
alert worker 2026-10-02T10:07:00Z
answer health 200 "$no_worker"
answer health 200 "$no_worker"
monitor false
edited="$(call PATCH issues/41)"
expect "exits 0" test "$status" = 0
expect "decides to update it" test "$(decided)" = "update worker"
expect "edits the issue" test -n "$edited"
expect "sets Last checked" grep -q -x -F -e '- Last checked: 2026-10-02 10:22 UTC' "$dir/call-$edited.body"
expect "keeps the rest" grep -q -x -F -e '- Failing since: earlier' "$dir/call-$edited.body"
expect "keeps the owner's line" grep -q -x -F -e 'A line the owner added.' "$dir/call-$edited.body"
expect "keeps the title" test ! -e "$dir/call-$edited.title"
expect "doesn't comment" test -z "$(call POST issues/41/comments)"
expect "changes nothing else" test "$(writes)" = 1
finish

begin "the same failure, alert issue open with Windows line ends: Last checked only"
alert worker 2026-10-02T10:07:00Z
sed 's/$/\r/' "$dir/alert.body" > "$dir/alert-crlf.body"
issue_list "$dir/alert-crlf.body"
answer health 200 "$no_worker"
answer health 200 "$no_worker"
monitor false
expect "exits 0" test "$status" = 0
expect "decides to update it" test "$(decided)" = "update worker"
expect "changes one thing" test "$(writes)" = 1
finish

begin "other checks failing, alert issue open: a comment"
alert worker 2026-10-02T10:07:00Z
answer health 200 "$no_images"
answer health 200 "$no_images"
monitor false
commented="$(call POST issues/41/comments)"
edited="$(call PATCH issues/41)"
expect "exits 0" test "$status" = 0
expect "decides to comment" test "$(decided)" = "comment images"
expect "says what changed" test "$(field "$commented" body)" = "At 2026-10-02 10:22 UTC the failing checks changed to: images."
expect "retitles it, since the first failure" test "$(field "$edited" title)" = "Server health: images failing since 2026-10-02 10:07 UTC"
expect "records the new checks" grep -q -x -F -e '<!-- server-health-checks: images -->' "$dir/call-$edited.body"
expect "keeps the first failure's time" grep -q -x -F -e '<!-- server-health-since: 2026-10-02T10:07:00Z -->' "$dir/call-$edited.body"
expect "sets Last checked" grep -q -x -F -e '- Last checked: 2026-10-02 10:22 UTC' "$dir/call-$edited.body"
expect "rewrites the body, as its footer says" grep -q -x -F -e 'When the failing checks change, it rewrites this text, so put notes in a comment.' "$dir/call-$edited.body"
expect "rewrites it whole, dropping the owner's line" test "$(grep -c -x -F -e 'A line the owner added.' "$dir/call-$edited.body")" = 0
expect "rewrites the stored lines too" test "$(grep -c -x -F -e '- Failing since: earlier' "$dir/call-$edited.body")" = 0
expect "changes nothing else" test "$(writes)" = 2
finish

begin "healthy, alert issue open: closes it"
alert worker 2026-10-02T09:07:00Z
answer health 200 "$healthy"
monitor false
commented="$(call POST issues/41/comments)"
closed="$(call PATCH issues/41)"
expect "exits 0" test "$status" = 0
expect "decides to close it" test "$(decided)" = close
expect "says so" test "$(field "$commented" body)" = "Healthy again at 2026-10-02 10:22 UTC; it failed for about 1 hour."
expect "closes it" test "$(field "$closed" state)" = closed
expect "as completed" test "$(field "$closed" state_reason)" = completed
expect "changes nothing else" test "$(writes)" = 2
finish

begin "healthy after a quarter of an hour, alert issue open: closes it"
alert images,backup 2026-10-02T10:07:00Z
answer health 200 "$healthy"
monitor false
expect "says for how long" test "$(field "$(call POST issues/41/comments)" body)" = "Healthy again at 2026-10-02 10:22 UTC; it failed for about 15 minutes."
finish

begin "/v1/health answers 404 and /v1/status answers: nothing"
answer health 404 "Not Found $secret"
answer status 200 '{"protocolVersion":32769}'
monitor false
expect "exits 0" test "$status" = 0
expect "decides none" test "$(decided)" = none
expect "asks /v1/status" test "$(cat "$dir/status.count")" = 1
expect "changes nothing" test "$(writes)" = 0
finish

begin "/v1/health answers 404 and /v1/status doesn't answer: unreachable"
answer health 404 ''
answer status 502 "Bad Gateway $secret"
answer health 404 ''
answer status 000 '' 28
monitor false
opened="$(call POST issues)"
expect "exits 0" test "$status" = 0
expect "decides to open one for unreachable" test "$(decided)" = "open unreachable"
expect "titles it" test "$(field "$opened" title)" = "Server health: unreachable failing since 2026-10-02 10:22 UTC"
expect "says the server didn't answer" grep -q -F -e "The server didn't answer." "$dir/call-$opened.body"
finish

begin "a deploy is running: skips"
printf '%s\n' '{"total_count":1,"workflow_runs":[{"id":1}]}' > "$dir/runs.json"
answer health 200 "$no_worker"
monitor false
expect "exits 0" test "$status" = 0
expect "decides to skip" test "$(decided)" = skip
expect "says why" grep -q -F -e '::notice::A deploy is running' "$dir/out"
expect "doesn't ask the server" test ! -s "$dir/curl.log"
expect "asks GitHub nothing more" test "$(wc -l < "$dir/gh.log")" = 1
finish

begin "a deploy starts between the two looks: skips"
printf '%s\n' '{"total_count":1,"workflow_runs":[{"id":1}]}' > "$dir/runs.2.json"
alert worker 2026-10-02T10:07:00Z
answer health 200 "$no_worker"
monitor false
expect "exits 0" test "$status" = 0
expect "decides to skip" test "$(decided)" = skip
expect "looks once" test "$(cat "$dir/health.count")" = 1
expect "changes nothing" test "$(writes)" = 0
finish

for body in "{\"worker\":true,\"images\":\"$secret" "{\"worker\":true,\"images\":true,\"backup\":true,\"$secret\":true}" \
  '{"worker":"true","images":true,"backup":true}' '{"worker":true,"images":true}' "$healthy$healthy" '[true,true,true]' \
  '{"worker":1,"images":true,"backup":null}' "<html>$secret</html>" ''; do
  begin "/v1/health answers 200 with something else ($body), /v1/status answers: answer"
  answer health 200 "$body"
  answer status 200 '{}'
  answer health 200 "$body"
  answer status 200 '{}'
  monitor false
  opened="$(call POST issues)"
  expect "exits 0" test "$status" = 0
  expect "decides to open one for answer" test "$(decided)" = "open answer"
  expect "says it answered with something else" grep -q -F -e 'The server answered with something else.' "$dir/call-$opened.body"
  finish
done

begin "/v1/health answers 503 or not at all, /v1/status answers: answer"
answer health 503 "Service Unavailable $secret"
answer status 200 '{}'
answer health 000 '' 28
answer status 200 '{}'
monitor false
expect "exits 0" test "$status" = 0
expect "decides to open one for answer" test "$(decided)" = "open answer"
finish

begin "no answer at all: unreachable"
answer health 000 '' 28
answer status 000 '' 7
answer health 000 '' 6
answer status 000 '' 28
monitor false
opened="$(call POST issues)"
expect "exits 0" test "$status" = 0
expect "decides to open one for unreachable" test "$(decided)" = "open unreachable"
expect "titles it" test "$(field "$opened" title)" = "Server health: unreachable failing since 2026-10-02 10:22 UTC"
finish

begin "two checks failing: one issue for both"
answer health 200 '{"backup":false,"worker":true,"images":false}'
answer health 200 '{"backup":false,"worker":true,"images":false}'
monitor false
opened="$(call POST issues)"
expect "decides to open one for both" test "$(decided)" = "open images,backup"
expect "names both in the title" test "$(field "$opened" title)" = "Server health: images, backup failing since 2026-10-02 10:22 UTC"
expect "says what images means" grep -q -F -e "The server's own test image didn't come back right" "$dir/call-$opened.body"
expect "says what backup means" grep -q -F -e 'The last backup run failed' "$dir/call-$opened.body"
expect "says when backup turns false" grep -q -F -e 'or none has finished for 3 hours.' "$dir/call-$opened.body"
finish

begin "only the check that failed both times counts"
answer health 200 '{"worker":false,"images":false,"backup":true}'
answer health 200 "$no_worker"
monitor false
expect "decides to open one for worker" test "$(decided)" = "open worker"
finish

begin "a different failure each time: nothing"
answer health 000 '' 28
answer status 000 '' 28
answer health 200 "$no_worker"
monitor false
expect "exits 0" test "$status" = 0
expect "decides none" test "$(decided)" = none
expect "changes nothing" test "$(writes)" = 0
finish

begin "a dry run: decides, and changes nothing"
answer health 200 "$no_worker"
answer health 200 "$no_worker"
monitor true
expect "exits 0" test "$status" = 0
expect "decides to open one for worker" test "$(decided)" = "open worker"
expect "says it's a dry run" grep -q -x -F -e 'Dry run: nothing changed.' "$dir/out"
expect "changes nothing" test "$(writes)" = 0
finish

begin "the deploy runs can't be listed: the monitor fails"
touch "$dir/fail-runs"
monitor false
expect "fails" test "$status" != 0
expect "says why" grep -q -F -e "::error::Couldn't list the deploy workflow's runs." "$dir/out"
finish

begin "the open issues can't be listed: the monitor fails"
touch "$dir/fail-issues"
answer health 200 "$no_worker"
answer health 200 "$no_worker"
monitor false
expect "fails" test "$status" != 0
expect "says why" grep -q -F -e "::error::Couldn't list the open server-health issues." "$dir/out"
expect "changes nothing" test "$(writes)" = 0
finish

begin "an alert issue without its state: the monitor fails"
printf '%s\n' '<!-- server-health -->' 'Someone rewrote this.' > "$dir/edited.body"
issue_list "$dir/edited.body"
answer health 200 "$healthy"
monitor false
expect "fails" test "$status" != 0
expect "names the issue" grep -q -F -e '::error::Issue #41 carries the server-health marker' "$dir/out"
expect "changes nothing" test "$(writes)" = 0
finish

begin "two alert issues open: the monitor fails"
alert worker 2026-10-02T10:07:00Z
jq '. + [.[0] | .number = 42]' "$dir/issues.json" > "$dir/two.json"
mv "$dir/two.json" "$dir/issues.json"
answer health 200 "$healthy"
monitor false
expect "fails" test "$status" != 0
expect "changes nothing" test "$(writes)" = 0
finish

begin "GitHub refuses a change: the monitor fails"
touch "$dir/fail-writes"
alert worker 2026-10-02T10:07:00Z
answer health 200 "$healthy"
monitor false
expect "fails" test "$status" != 0
expect "says why" grep -q -F -e "::error::Couldn't comment on issue #41." "$dir/out"
finish

begin "decide, for one look"
decides none HEALTH_CODE=200 HEALTH_BODY="$healthy"
decides "open worker" HEALTH_CODE=200 HEALTH_BODY="$no_worker"
decides "open worker,images,backup" HEALTH_CODE=200 HEALTH_BODY='{"worker":false,"images":false,"backup":false}'
decides "open worker,backup" HEALTH_CODE=200 HEALTH_BODY='{"backup":false,"worker":false,"images":true}'
decides "update worker" HEALTH_CODE=200 HEALTH_BODY="$no_worker" ALERT_CHECKS=worker
decides "comment worker" HEALTH_CODE=200 HEALTH_BODY="$no_worker" ALERT_CHECKS=images
decides close HEALTH_CODE=200 HEALTH_BODY="$healthy" ALERT_CHECKS=worker
decides none HEALTH_CODE=404 STATUS_CODE=200
decides "open unreachable" HEALTH_CODE=404 STATUS_CODE=502
decides "open unreachable" HEALTH_CODE=000 STATUS_CODE=000
decides "open answer" HEALTH_CODE=200 HEALTH_BODY='{"worker":true}' STATUS_CODE=200
decides "open answer" HEALTH_CODE=503 STATUS_CODE=200
decides error HEALTH_CODE=200 HEALTH_BODY='not json'
decides error HEALTH_CODE=200 HEALTH_BODY="$healthy" ALERT_CHECKS=everything
decides error HEALTH_CODE=ok
finish

if ((failures > 0)); then
  echo "$failures checks failed, in $cases cases."
  exit 1
fi
echo "The server health monitor passed all $cases cases."
