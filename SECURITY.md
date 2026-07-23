# Security

## Data handling

Windows AI Statusbar does not collect prompts, conversations, model responses,
browser cookies, or analytics.

The Claude OAuth access and refresh tokens are read from the official local
Claude credential store. They are sent only to Anthropic's official token and
usage endpoints. When a session is refreshed, the new credentials are written
atomically back to Claude's own credential store and are never copied into
application settings, caches, or logs.
It is never written to settings or logs.

Codex credentials are not read by the application. Rate limits are requested
through the installed official `codex app-server`.

## Reporting a concern

Open a private security advisory in this repository. Do not include access
tokens, credential files, prompts, or other personal data in the report.
