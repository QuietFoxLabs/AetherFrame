#!/usr/bin/env bash
# One-time setup of an AetherFrame host (docs/networking/Runbook.md, "Setting up the server").
# Run as root on a fresh Ubuntu 24.04 server:
#   sudo bash host-setup.sh "<the deploy key's public line, ssh-ed25519 ...>"
# It installs Docker, keeps logs 14 days (decision S5), opens only SSH, HTTP and HTTPS, turns on
# automatic security updates, and creates the deploy user the deploy workflow signs in as.
set -euo pipefail

if [[ $EUID -ne 0 ]]; then
  echo "Run this as root (sudo bash host-setup.sh ...)." >&2
  exit 1
fi

deploy_key="${1:-}"
if [[ "$deploy_key" != ssh-ed25519\ * ]]; then
  echo "Give the deploy key's public line (it starts with ssh-ed25519) as the one argument." >&2
  exit 1
fi

export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y docker.io docker-compose-v2 ufw unattended-upgrades
systemctl enable --now docker

# Logs are kept 14 days, deleted by time (S5): every container logs to the journal.
mkdir -p /etc/systemd/journald.conf.d
cat > /etc/systemd/journald.conf.d/aetherframe.conf <<'CONF'
[Journal]
MaxRetentionSec=14day
CONF
systemctl restart systemd-journald

# Only SSH, HTTP and HTTPS (TCP and QUIC).
ufw allow OpenSSH
ufw allow 80/tcp
ufw allow 443/tcp
ufw allow 443/udp
ufw --force enable

# Security updates install themselves.
dpkg-reconfigure -f noninteractive unattended-upgrades

# The deploy user: it can run Docker, and signs in only with the deploy key.
if ! id aetherframe-deploy >/dev/null 2>&1; then
  useradd --create-home --shell /bin/bash --groups docker aetherframe-deploy
fi
install -d -m 700 -o aetherframe-deploy -g aetherframe-deploy /home/aetherframe-deploy/.ssh
printf '%s\n' "$deploy_key" > /home/aetherframe-deploy/.ssh/authorized_keys
chown aetherframe-deploy:aetherframe-deploy /home/aetherframe-deploy/.ssh/authorized_keys
chmod 600 /home/aetherframe-deploy/.ssh/authorized_keys

# The deployment's folder, and the configuration the operator edits: the testers' Lodestone ids.
install -d -m 755 -o aetherframe-deploy -g aetherframe-deploy /opt/aetherframe /opt/aetherframe/config
if [[ ! -f /opt/aetherframe/config/aetherframe.json ]]; then
  cat > /opt/aetherframe/config/aetherframe.json <<'JSON'
{
  "AetherFrame": {
    "AllowedLodestoneIds": []
  }
}
JSON
  chown aetherframe-deploy:aetherframe-deploy /opt/aetherframe/config/aetherframe.json
fi

# The image worker's runs (I2): a service that starts one container per job, and ends each from
# outside after at most 60 seconds. The deploy workflow copies aetherframe-worker.sh beside it.
cat > /etc/systemd/system/aetherframe-worker.service <<'UNIT'
[Unit]
Description=AetherFrame image worker runs, one container per job (decision I2)
After=docker.service
Requires=docker.service

[Service]
User=aetherframe-deploy
ExecStart=/opt/aetherframe/aetherframe-worker.sh
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload
systemctl enable aetherframe-worker.service

echo "Done. Next: the GitHub environment and its secrets (docs/networking/Runbook.md)."
