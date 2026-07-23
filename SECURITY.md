# Security

## Data handling

Windows AI Statusbar does not collect prompts, conversations, model responses,
browser cookies, or analytics.

The Claude OAuth access token is read from the official local Claude credential
store, held only in process memory, and sent only to Anthropic's usage endpoint.
It is never written to settings or logs.

Codex credentials are not read by the application. Rate limits are requested
through the installed official `codex app-server`.

## Reporting a concern

Open a private security advisory in this repository. Do not include access
tokens, credential files, prompts, or other personal data in the report.
