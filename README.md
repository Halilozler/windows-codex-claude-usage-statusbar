# Windows AI Statusbar

A compact Windows 11 taskbar monitor for Claude and Codex usage limits.

![Windows AI Statusbar taskbar preview](docs/taskbar-preview.png)

## Features

- Live Claude 5-hour and weekly usage through the signed-in Claude OAuth session.
- Codex limits through the official local `codex app-server`.
- Remaining or used quota display modes.
- Segmented bars, percentage, and reset countdown.
- Claude and Codex can be shown or hidden independently.
- Transparent taskbar-native appearance with supplied Claude and Codex logos.
- Automatically follows the Windows taskbar:
  - stays attached when the taskbar is clicked or another application opens;
  - hides with an auto-hidden taskbar;
  - stays behind exclusive or borderless full-screen applications.
- Optional start with Windows.
- No prompt, conversation, model response, or browser-cookie collection.

## Requirements

- Windows 11 x64.
- [.NET 10 Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/10.0).
- Claude Code installed and signed in once with `claude auth login`.
- Codex CLI installed and signed in.

The Claude terminal does not need to remain open after authentication.

## Install from a release

1. Download `WindowsAIStatusbar.exe` and `install.ps1` from the latest release.
2. Keep both files in the same folder.
3. Open PowerShell in that folder and run:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```

The installer copies the executable to:

```text
%LOCALAPPDATA%\WindowsAIStatusbar\WindowsAIStatusbar.exe
```

It also starts the application and enables start with Windows for the current
user. Administrator privileges are not required.

## Manual install

1. Create `%LOCALAPPDATA%\WindowsAIStatusbar`.
2. Copy `WindowsAIStatusbar.exe` into that directory.
3. Run the executable.
4. Right-click the taskbar panel and enable **Start with Windows**.

## Uninstall

Run:

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```

The default uninstall keeps local display settings and the Claude fallback
cache. To remove those as well:

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -RemoveSettings
```

## Build from source

```powershell
dotnet build .\WindowsAIStatusbar.csproj -c Release
dotnet publish .\WindowsAIStatusbar.csproj -c Release -r win-x64 `
  --self-contained false -p:PublishSingleFile=true -o .\publish
```

The published executable is `publish\WindowsAIStatusbar.exe`.

## Usage

- Left-click the taskbar panel to open the glass details window.
- Right-click it for refresh, display, provider visibility, startup, and exit.
- Select **Remaining** or **Used** in the details window.
- Clear **Claude** or **Codex** under **SHOW** to remove that provider from the
  taskbar. At least one provider remains enabled so the application stays
  accessible.
- The panel refreshes every 30 seconds and countdowns update every second.
- Claude limits come from two sources. While Claude Code is in use, its own
  rate-limit payload arrives through the status-line bridge within seconds, at
  no request cost. Usage from anywhere else is caught by a live read of the
  Anthropic account, which is rate-limited hard by that endpoint (measured: one
  request per ~90 seconds), so reads are throttled to one every 2 minutes above
  80 percent quota, 2.5 minutes above 50 percent, and 5 minutes otherwise.
- The **LIVE** / **LOCAL** badge and the note in the details window always show
  which source and which age is on screen.
- **Refresh** forces an early live read, subject to a short floor that grows if
  Anthropic returns HTTP 429.

## Security model

- No third-party runtime packages are used.
- The Claude OAuth token is read from the official local Claude credential
  store, used only in memory, and sent only to Anthropic's usage endpoint.
- Tokens are never written to application settings or logs.
- Codex credentials are not read directly; the installed Codex CLI performs
  the local rate-limit request.
- The application stores only display settings and a fallback usage cache.
- The release executable is reproducible from the source in this repository.

## Notes

- This is an independent utility and is not affiliated with Anthropic or
  OpenAI.
- Claude and Codex logos are trademarks of their respective owners.
- The executable is not code-signed. Verify the SHA-256 value published with
  each release if Windows displays an unknown-publisher warning.
