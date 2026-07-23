param(
    [string]$ExecutablePath = (Join-Path $PSScriptRoot "WindowsAIStatusbar.exe")
)

$source = (Resolve-Path -LiteralPath $ExecutablePath).Path
$installDirectory = [IO.Path]::GetFullPath(
    (Join-Path $env:LOCALAPPDATA "WindowsAIStatusbar"))
$expectedDirectory = [IO.Path]::GetFullPath(
    (Join-Path $env:LOCALAPPDATA "WindowsAIStatusbar"))
if ($installDirectory -ne $expectedDirectory) {
    throw "Unexpected installation directory."
}

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

New-Item -ItemType Directory -Force -Path $installDirectory | Out-Null
$target = Join-Path $installDirectory "WindowsAIStatusbar.exe"
Copy-Item -LiteralPath $source -Destination $target -Force

$runPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
New-ItemProperty `
    -Path $runPath `
    -Name "WindowsAIStatusbar" `
    -Value "`"$target`" --startup" `
    -PropertyType String `
    -Force | Out-Null
Remove-ItemProperty `
    -Path $runPath `
    -Name "ClaudeCodexLimits" `
    -ErrorAction SilentlyContinue

Start-Process -FilePath $target -ArgumentList "--startup"
Write-Host "Windows AI Statusbar installed successfully."
Write-Host "Installed path: $target"
