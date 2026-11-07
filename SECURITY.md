# Security policy

Bosun runs other people's commands, reads process details and, in the planned panel, holds a Claude API key. If you find a way to run a command you shouldn't, read or change something outside your own user account, or expose that key, please tell us privately.

## Which versions get fixes?

| Version | Security fixes |
| ------- | -------------- |
| `main` | Yes, as there is no release yet |

## How do I report a vulnerability?

This repository is private for now, and GitHub offers private vulnerability reporting only on public repositories. If you can read this, you have access through the [Rizonetech organization](https://github.com/rizonetech), so contact one of its owners directly and ask for a private channel.

If the repository becomes public, use GitHub private vulnerability reporting instead: open the repository's **Security** tab and choose **Report a vulnerability**. Only the maintainers see the report.

Do not open a public issue, pull request or discussion about a vulnerability.

A useful report includes the commit you tested, the steps, and what an attacker gains.

## What happens next?

- We aim to respond within 3 working days, confirming we received the report and who is handling it.
- We reproduce the problem, agree on its severity with you, and keep you updated in the private advisory until it is fixed.
- We ask you to keep the details private until the fix ships. We credit you in the advisory if you want that.

## What is in scope?

- `bosun.exe`, `bosun-logger.exe`, the WSL shim and the logger service.
- The installer in `scripts/install.ps1` and `wsl/install.sh`.
- Argument handling in `bosun run`, including batch files.
- Bosun Panel and its key handling, once they ship.

A process running as the same user can already do anything that user can, so a problem that needs that level of access is out of scope unless Bosun gives it more.
