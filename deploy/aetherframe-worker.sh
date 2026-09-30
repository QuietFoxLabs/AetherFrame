#!/usr/bin/env bash
# Runs the AetherFrame image worker (decision I2; docs/networking/Runbook.md): one container per
# run, each ended from outside after at most AETHERFRAME_WORKER_LIFE seconds (60) however it
# behaves, then a fresh one. A run takes at most one job and ends when it has answered; a run an
# exploit controls can't outlive its life, and nothing of one container survives into the next.
# systemd runs this as aetherframe-worker.service; it reads the deployed version from the .env
# file the deploy workflow writes, so a new deployment's worker starts with the next run.
set -uo pipefail

env_file="${AETHERFRAME_ENV_FILE:-/opt/aetherframe/.env}"
life="${AETHERFRAME_WORKER_LIFE:-60}"
volume="${AETHERFRAME_SOCKET_VOLUME:-aetherframe_sockets}"

while true; do
  version="$(sed -n 's/^AETHERFRAME_VERSION=\([0-9A-Za-z._-]*\)$/\1/p' "$env_file" 2>/dev/null | head -n 1)"
  if [[ -z "$version" ]]; then
    sleep 5
    continue
  fi

  name="aetherframe-worker-$$-$RANDOM"
  docker run --rm --name "$name" \
    --network none --read-only --tmpfs /tmp:size=16m \
    --cap-drop ALL --security-opt no-new-privileges:true \
    --memory 512m --memory-swap 512m --pids-limit 64 --oom-score-adj 1000 \
    --ulimit nofile=256:256 --ulimit core=0:0 \
    --env AETHERFRAME_IMAGE_SOCKET=/run/aetherframe/images.sock \
    --env DOTNET_GCHeapHardLimit=0x10000000 \
    --volume "$volume:/run/aetherframe:ro" \
    --log-driver "${AETHERFRAME_WORKER_LOG_DRIVER:-journald}" \
    "aetherframe-worker:$version" &
  runner=$!

  ( sleep "$life"; docker kill "$name" >/dev/null 2>&1 ) &
  killer=$!

  wait "$runner"
  kill "$killer" 2>/dev/null
  wait "$killer" 2>/dev/null
  docker rm -f "$name" >/dev/null 2>&1
  sleep 0.2
done
