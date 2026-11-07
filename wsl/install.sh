#!/bin/bash
# Installs the WSL side. usage: install.sh <devmode.exe path> <log dir>
set -eu
exe="$1"
logs="$2"
here="$(cd "$(dirname "$0")" && pwd)"

mkdir -p "$HOME/bin" "$HOME/.config/devmode" "$HOME/.local/share/devmode" "$HOME/.config/systemd/user"
install -m 755 "$here/devmode" "$HOME/bin/devmode"
install -m 644 "$here/devmode_log.py" "$HOME/.local/share/devmode/devmode_log.py"
printf 'DEVMODE_EXE=%q\n' "$exe" > "$HOME/.config/devmode/env"

cat > "$HOME/.config/systemd/user/devmode-log.service" <<EOF
[Unit]
Description=DevMode WSL memory logger

[Service]
ExecStart=/usr/bin/python3 -I %h/.local/share/devmode/devmode_log.py "$logs"
Restart=on-failure
RestartSec=30
Nice=10

[Install]
WantedBy=default.target
EOF

systemctl --user daemon-reload
systemctl --user enable devmode-log.service >/dev/null 2>&1
systemctl --user restart devmode-log.service
systemctl --user --no-pager --lines=0 status devmode-log.service | sed -n '1,3p'
case ":$PATH:" in
  *":$HOME/bin:"*) ;;
  *) echo "note: $HOME/bin is not on PATH in this shell" ;;
esac
