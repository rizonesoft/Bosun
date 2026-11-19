#!/bin/bash
# Installs the WSL side. usage: install.sh <bosun.exe path> <log dir>
set -eu
exe="$1"
logs="$2"
distro="${WSL_DISTRO_NAME:-}"
# The logger's record names are keyed by this name, so it is given to the service explicitly, never guessed there.
# The whole value, never one line of it (round 6: a second line passed through grep and became a unit directive).
if ! [[ "$distro" =~ ^[A-Za-z0-9._-]{1,64}$ ]]; then
  echo "install.sh: WSL_DISTRO_NAME is missing or not a usable distro name (got: '$distro'); run it inside the WSL distro" >&2
  exit 1
fi
here="$(cd "$(dirname "$0")" && pwd)"

mkdir -p "$HOME/bin" "$HOME/.config/bosun" "$HOME/.local/share/bosun" "$HOME/.config/systemd/user"
install -m 755 "$here/bosun" "$HOME/bin/bosun"
install -m 644 "$here/bosun_log.py" "$HOME/.local/share/bosun/bosun_log.py"
install -m 755 "$here/enable-write-budget.sh" "$HOME/.local/share/bosun/enable-write-budget.sh"
printf 'BOSUN_EXE=%q\n' "$exe" > "$HOME/.config/bosun/env"

cat > "$HOME/.config/systemd/user/bosun-log.service" <<EOF
[Unit]
Description=Bosun WSL memory logger

[Service]
Environment=BOSUN_DISTRO=$distro
ExecStart=/usr/bin/python3 -I %h/.local/share/bosun/bosun_log.py "$logs"
Restart=on-failure
RestartSec=30
Nice=10

[Install]
WantedBy=default.target
EOF

systemctl --user daemon-reload
systemctl --user enable bosun-log.service >/dev/null 2>&1
systemctl --user restart bosun-log.service
systemctl --user --no-pager --lines=0 status bosun-log.service | sed -n '1,3p'
case ":$PATH:" in
  *":$HOME/bin:"*) ;;
  *) echo "note: $HOME/bin is not on PATH in this shell" ;;
esac
