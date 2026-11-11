# SmartEdit MCP installer (agent-friendly, idempotent)
# Downloads the latest release into %LOCALAPPDATA%\SmartEdit\bin, runs the selftest,
# prints ready-to-paste MCP client configs. Picks the self-contained build when no
# .NET 10 runtime is present (no runtime install needed).
# Usage:  irm https://raw.githubusercontent.com/ERambler/MCP-SmartEdit/main/install/install.ps1 | iex
$ErrorActionPreference = 'Stop'
$repo  = 'ERambler/MCP-SmartEdit'
$bin   = Join-Path $env:LOCALAPPDATA 'SmartEdit\bin'
$zip   = Join-Path $env:TEMP 'smartedit-release.zip'

Write-Host "== SmartEdit MCP installer ==" -ForegroundColor Cyan

# 0) .NET 10 runtime check → выбор ассета: self-contained без рантайма, framework-dependent с ним
$sc = $false
try { $null = & dotnet --list-runtimes 2>$null | Select-String 'Microsoft.NETCore.App.* 10\.' }
catch {}
if (-not $?) {
    $sc = $true
    Write-Host ".NET 10 runtime not found - using the self-contained build (no runtime needed)" -ForegroundColor Yellow
}

# 1) latest release asset (win-x64 zip; sc-вариант для машин без рантайма)
$rel = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest"
$pattern = if ($sc) { 'SmartEdit-sc-win-x64-*.zip' } else { 'SmartEdit-win-x64-*.zip' }
$asset = $rel.assets | Where-Object { $_.name -like $pattern } | Select-Object -First 1
if (-not $asset) {
    if ($sc) {
        Write-Host "ERROR: latest release ($($rel.tag_name)) has no self-contained asset and .NET 10 runtime is not installed." -ForegroundColor Red
        Write-Host "  Install the runtime: https://dotnet.microsoft.com/download/dotnet/10.0" -ForegroundColor Yellow
    } else {
        Write-Host "ERROR: no win-x64 asset in latest release ($($rel.tag_name))" -ForegroundColor Red
    }
    exit 1
}
Write-Host "Downloading $($asset.name) ($([math::Round($asset.size/1KB)) KB) ..."

# 2) unzip (kill a running server first — the exe is locked while in use)
Get-Process SmartEdit.Mcp -ErrorAction SilentlyContinue | Stop-Process -Force
Invoke-WebRequest $asset.browser_download_url -OutFile $zip
New-Item -ItemType Directory -Force -Path $bin | Out-Null
Expand-Archive $zip $bin -Force
Remove-Item $zip
$exe = Join-Path $bin 'SmartEdit.Mcp.exe'

# 3) selftest gate (exit code must be 0)
Write-Host "Running selftest (~30s, writes to %TEMP%\smartedit-selftest) ..."
& $exe selftest | Select-Object -Last 1
if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: selftest FAILED" -ForegroundColor Red; exit 1 }
Write-Host "Selftest OK. Binary: $exe" -ForegroundColor Green

# 4) client config snippets
Write-Host @"

Add to your MCP client config:

ZCode  (~/.zcode/cli/config.json):
  "mcp": {{ "servers": {{ "smartedit": {{ "command": "$($exe -replace '\\','\\')", "args": [] }} }} }}

Claude Desktop  (%APPDATA%\Claude\claude_desktop_config.json):
  "mcpServers": {{ "smartedit": {{ "command": "$($exe -replace '\\','\\')" }} }}

Cursor / any stdio client  (~/.cursor/mcp.json):
  "mcpServers": {{ "smartedit": {{ "command": "$($exe -replace '\\','\\')", "args": [] }} }}

Restart the client (new session picks the server up). Done.
"@ -ForegroundColor Cyan
