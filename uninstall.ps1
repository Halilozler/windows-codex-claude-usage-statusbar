param(
    [switch]$RemoveSettings
)

$running = @(
    Get-Process -Name WindowsAIStatusbar, ClaudeCodexLimits `
        -ErrorAction SilentlyContinue
)
if ($running.Count -gt 0) {
    $running | Stop-Process -Force
    foreach ($process in $running) {
        $process.WaitForExit()
    }
}

$runPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
Remove-ItemProperty `
    -Path $runPath `
    -Name "WindowsAIStatusbar" `
    -ErrorAction SilentlyContinue
Remove-ItemProperty `
    -Path $runPath `
    -Name "ClaudeCodexLimits" `
    -ErrorAction SilentlyContinue

$installDirectory = [IO.Path]::GetFullPath(
    (Join-Path $env:LOCALAPPDATA "WindowsAIStatusbar"))
$expectedDirectory = [IO.Path]::GetFullPath(
    (Join-Path $env:LOCALAPPDATA "WindowsAIStatusbar"))
if ($installDirectory -ne $expectedDirectory) {
    throw "Unexpected installation directory."
}
if (Test-Path -LiteralPath $installDirectory) {
    Remove-Item -LiteralPath $installDirectory -Recurse -Force
}

if ($RemoveSettings) {
    $settingsDirectory = [IO.Path]::GetFullPath(
        (Join-Path $env:LOCALAPPDATA "ClaudeCodexLimits"))
    $expectedSettingsDirectory = [IO.Path]::GetFullPath(
        (Join-Path $env:LOCALAPPDATA "ClaudeCodexLimits"))
    if ($settingsDirectory -ne $expectedSettingsDirectory) {
        throw "Unexpected settings directory."
    }
    if (Test-Path -LiteralPath $settingsDirectory) {
        Remove-Item -LiteralPath $settingsDirectory -Recurse -Force
    }
}

Write-Host "Windows AI Statusbar uninstalled successfully."
