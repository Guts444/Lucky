# Security Policy

Lucky is a local-first desktop agent harness. Users configure their own model providers, API keys, workspace folders, MCP servers, and optional Docker sandbox. Treat that trust boundary carefully.

## Supported versions

Lucky is under active pre-release development. Security fixes are applied on the default branch (`main`). There is not yet a long-term support (LTS) release train.

## What Lucky stores locally

By design, durable user data lives under the current Windows user profile, not in this repository:

| Data | Location (typical) | Protection |
| --- | --- | --- |
| Settings, projects, chats, memories | `%LOCALAPPDATA%\Lucky\lucky-state.json` | API keys and MCP launch config via Windows DPAPI (current user) |
| ChatGPT / Codex helper state | `%LOCALAPPDATA%\Lucky\CodexHome` | OAuth material as DPAPI-protected blob when at rest |

The repository must never contain live API keys, OAuth tokens, chat transcripts, or personal workspace state.

## Reporting a vulnerability

**Do not open a public GitHub issue for security vulnerabilities.**

Please report privately using one of:

1. [GitHub private vulnerability reporting](https://github.com/Guts444/Lucky/security/advisories/new) for this repository (preferred when enabled), or
2. Email the maintainer via the address on the [GitHub profile](https://github.com/Guts444) if private reporting is unavailable.

Include:

- A clear description of the issue and impact
- Steps to reproduce, or a minimal proof of concept
- Affected commit or release, if known
- Whether you plan to disclose publicly and on what timeline

You should receive an acknowledgment when practical. Please give a reasonable window to investigate and ship a fix before public disclosure.

## Out of scope (for reports)

- Issues that require the user to deliberately enable `FullAccess`, install a malicious MCP server, or run untrusted PowerShell on their own machine, unless Lucky misrepresents those risks or fails to honor documented access-level gates
- Vulnerabilities only present in third-party model providers, SearXNG instances, Docker, or MCP servers Lucky does not ship
- Social engineering of API keys outside Lucky's storage/protection path

## Hardening expectations for contributors

- Never commit secrets, `.env` files, personal `docs/private/` notes, or real `lucky-state.json` dumps
- Do not log or display raw API keys, OAuth tokens, or MCP launch secrets
- Prefer loopback defaults for local tools; require explicit user configuration for remote endpoints
- Keep access-level gates enforced in code, not only in prompts
- When adding network, filesystem, shell, MCP, or sandbox capabilities, document limits in `README.md` / `docs/ARCHITECTURE.md` and add tests where feasible
