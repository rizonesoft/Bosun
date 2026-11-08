#!/bin/bash
# Gives user jobs the io controller, so `bosun run` can count what a job writes in WSL and stop one over its budget.
# systemd hands user sessions only cpu, memory and pids by default, and only root can change that. Run once with sudo.
set -eu
[ "$(id -u)" = 0 ] || { echo "run this with sudo" >&2; exit 1; }
mkdir -p /etc/systemd/system/user@.service.d
printf '[Service]\nDelegate=cpu cpuset io memory pids\n' > /etc/systemd/system/user@.service.d/bosun-io.conf
systemctl daemon-reload
echo "Done. Run 'wsl --shutdown' from Windows, then open WSL again, so your session starts with the io controller."
