# Security Policy

## Reporting a Vulnerability

If you discover a security vulnerability in IdolClick, please report it responsibly.

**Do NOT open a public GitHub issue for security vulnerabilities.**

Instead, please email **idolclick@jobi.dev** or use [GitHub's private vulnerability reporting](https://github.com/jobijoy/IdolClick/security/advisories).

### What to Include

- Description of the vulnerability
- Steps to reproduce
- Potential impact
- Suggested fix (if any)

## Security Considerations

IdolClick interacts with the Windows desktop at a system level. Key security properties:

### API Keys
- LLM API keys are stored locally in `config.json` and an encrypted local store (`.kv/`)
- Never commit API keys to version control
- The `.gitignore` excludes `.kv/`, `bin/`, and log files

### Automation Safety
- **Kill switch** (Ctrl+Alt+Escape) immediately stops all automation
- **Target lock** pins execution to a specific window handle
- **Process allowlist** restricts which applications can be automated
- **Actionability checks** verify elements are visible and enabled before interaction
- **Forbidden actions** list blocks dangerous action types

### Plugin Security
- Plugins execute PowerShell or .NET code with the same privileges as IdolClick
- Only install plugins from trusted sources
- Review plugin code before enabling

## Supported Versions

| Version | Supported |
|---------|-----------|
| 1.x     | Yes       |
| < 1.0   | No        |
