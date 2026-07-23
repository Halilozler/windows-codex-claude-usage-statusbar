# Changelog

## 3.0.2

- Fixed Claude showing stale numbers. The Anthropic usage endpoint accepts
  roughly one request every 90 seconds, so the 30-second poll was answered with
  HTTP 429 almost every time and the panel silently fell back to a status-line
  cache that could be hours old.
- Throttled live Claude reads and served the held reading in between, so timer
  ticks and status-line writes no longer spend requests. The interval adapts to
  how full the quota is: 2 minutes above 80 percent, 2.5 minutes above
  50 percent, 5 minutes otherwise.
- Treated a status-line write from the last 2 minutes as live rather than
  cached. It carries Claude Code's own rate-limit payload, so while Claude Code
  is in use the panel now follows along within seconds at no request cost.
- Added exponential backoff after HTTP 429, applied to manual refreshes too.
- Cached each successful live reading to disk, so a restart no longer falls
  back to whatever the status-line bridge last wrote.
- Labelled cached readings with their real age instead of claiming they were
  live, and dropped cached windows whose reset time had already passed.
- Detected an expired local Claude session before making a request, and
  reported it instead of failing with a bare HTTP status.

## 3.0.1

- Prevented desktop and taskbar focus transitions from being mistaken for
  full-screen applications.
- Removed the brief hide/show flicker when clicking the Windows desktop.

## 3.0.0

- Renamed the application and executable to Windows AI Statusbar.
- Converted the complete user interface and documentation to English.
- Attached the overlay lifecycle to the Windows taskbar.
- Kept the panel stable when the taskbar is clicked or applications open.
- Hid the panel for auto-hidden taskbars and full-screen applications.
- Added independent Claude and Codex visibility controls.
- Added release installation and uninstallation scripts.
