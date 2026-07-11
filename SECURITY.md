# Security Policy

## Reporting a vulnerability

Please do not publish security vulnerabilities, credential exposure, or
privacy leaks in a public issue.

Use GitHub's **Security → Advisories → New draft security advisory** feature
for private disclosure. Include:

- the affected version;
- steps to reproduce;
- the expected and actual behavior;
- the potential security or privacy impact.

Do not include real access tokens, `auth.json`, account identifiers, or other
credentials in the report. Replace sensitive values with obvious placeholders.

## Security boundaries

Codex Rate Monitor:

- does not open, parse, copy, or print `auth.json`;
- does not store access tokens or account identifiers;
- writes redacted local diagnostic logs containing rate-limit values and event
  metadata under `%LOCALAPPDATA%\CodexRateMonitor\logs`;
- automatically deletes diagnostic logs after the configured retention period
  (7 days by default);
- communicates with a locally launched `codex app-server` process over
  redirected standard input/output;
- checks the public `D1NOOO/codex-usage-monitor` GitHub Releases API and, only
  after the user chooses Update, downloads the Release ZIP and checksum file;
- verifies the downloaded ZIP against `SHA256SUMS.txt` before replacing files;
- stores display and diagnostic preferences in `settings.json`;
- optionally writes one current-user startup entry under
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

The Codex CLI/app-server remains responsible for authentication and any
network communication with OpenAI.

## Supported versions

Security fixes are provided for the latest release only.
