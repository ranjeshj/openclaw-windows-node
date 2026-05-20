#!/bin/bash
# Provision the 'openclaw' user inside the WSL distro. Mirrors the user
# creation that the tray's LocalGatewaySetup performs.
set -euo pipefail

if ! id -u openclaw >/dev/null 2>&1; then
  useradd -m -s /bin/bash openclaw
  usermod -aG sudo openclaw
  echo 'openclaw ALL=(ALL) NOPASSWD:ALL' > /etc/sudoers.d/openclaw
  chmod 440 /etc/sudoers.d/openclaw
fi
mkdir -p /opt/openclaw
chown openclaw:openclaw /opt/openclaw
