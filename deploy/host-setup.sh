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

# Logs are kept 14 days, deleted by time (S5): every container logs to the journal, whose files
# rotate daily so none outlives the 14 days, and nothing is copied to syslog, which keeps its own.
mkdir -p /etc/systemd/journald.conf.d
cat > /etc/systemd/journald.conf.d/aetherframe.conf <<'CONF'
[Journal]
MaxRetentionSec=14day
MaxFileSec=1day
ForwardToSyslog=no
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
# restrict: no forwarding, no terminal allocation; commands and file copies only.
printf 'restrict %s\n' "$deploy_key" > /home/aetherframe-deploy/.ssh/authorized_keys
chown aetherframe-deploy:aetherframe-deploy /home/aetherframe-deploy/.ssh/authorized_keys
chmod 600 /home/aetherframe-deploy/.ssh/authorized_keys

# The deployment's folder, and the configuration the operator edits: whether the server is open to
# everyone (Runbook, step 8), the Lodestone relay (step 7) and, while the server is closed, the
# allowed Lodestone ids (step 6).
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
# It runs as root: it finds the socket the server offers each run in the socket volume's folder,
# under /var/lib/docker, which only root can read. (Run as aetherframe-deploy, it never found one,
# and no image could be shared.) That gives the deploy user, who writes the script, nothing it
# hasn't already: it is in the docker group, which can do anything root can.
cat > /etc/systemd/system/aetherframe-worker.service <<'UNIT'
[Unit]
Description=AetherFrame image worker runs, one container per job (decision I2)
After=docker.service
Requires=docker.service
StartLimitIntervalSec=0

[Service]
ExecStart=/opt/aetherframe/aetherframe-worker.sh
ExecStopPost=/bin/sh -c 'docker ps -aq --filter name=^aetherframe-worker- | xargs -r docker rm -f'
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload

# It retries every 5 seconds until the first deploy copies its script in, and then runs.
systemctl enable --now aetherframe-worker.service

# The deploy user may restart the worker service, so a deploy's new script takes effect, and nothing else.
# Checked before it is put in place, so a bad file can never break sudo.
sudoers_draft="$(mktemp)"
cat > "$sudoers_draft" <<'SUDO'
aetherframe-deploy ALL=(root) NOPASSWD: /usr/bin/systemctl restart aetherframe-worker.service
SUDO
visudo -cf "$sudoers_draft"
install -m 440 -o root -g root "$sudoers_draft" /etc/sudoers.d/aetherframe-deploy
rm -f "$sudoers_draft"

echo "Done. Next: the GitHub environment and its secrets (docs/networking/Runbook.md)."
