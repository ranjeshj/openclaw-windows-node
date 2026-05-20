#!/bin/bash
# Provision the WSL distro for the gateway-compat test harness.
# Runs as root inside the distro. Idempotent.
set -euo pipefail

if ! id -u openclaw >/dev/null 2>&1; then
  useradd -m -s /bin/bash openclaw
  usermod -aG sudo openclaw
  echo 'openclaw ALL=(ALL) NOPASSWD:ALL' > /etc/sudoers.d/openclaw
  chmod 440 /etc/sudoers.d/openclaw
fi

install -d -m 0755 -o openclaw -g openclaw /opt/openclaw
install -d -m 0755 -o openclaw -g openclaw /var/openclaw-test

if ! command -v curl >/dev/null 2>&1 || ! command -v node >/dev/null 2>&1; then
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -qq
  apt-get install -y -qq --no-install-recommends curl ca-certificates nodejs
fi

command -v curl >/dev/null 2>&1 || { echo "curl missing after install" >&2; exit 1; }
command -v node >/dev/null 2>&1 || { echo "nodejs missing after install" >&2; exit 1; }
id -u openclaw >/dev/null 2>&1   || { echo "openclaw user missing"      >&2; exit 1; }

echo "setup-distro.sh OK"
