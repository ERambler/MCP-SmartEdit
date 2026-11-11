# SmartEdit MCP installer (agent-friendly, idempotent)
# Downloads the latest release into %LOCALAPPDATA%\SmartEdit\bin, runs the selftest,
# prints ready-to-paste MCP client configs.
# Requirement: .NET 10 runtime (the single framework-dependent artifact).
# Usage:  irm https://raw.githubusercontent.com/ERambler/MCP-SmartEdit/main/install/install.ps1 | iex
$ErrorActionPreference = 'Stop'
$repo  = 'ERambler/MCP-SmartEdit'
$bin   = Join-Path $env:LOCALAPPDATA 'SmartEdit\bin'
$zip   = Join-Path $env:TEMP 'smartedit-release.zip'

Write-Host "== SmartEdit MCP installer ==" -ForegroundColor Cyan

# 0) .NET 10 runtime — обязателен (единственный ассет — framework-dependent)
try { $null = & dotnet --list-runtimes 2>$null | Select-String 'Microsoft.NETCore.App.* 10\.' }
catch {}
if (-not $?) {
    Write-Host "ERROR: .NET 10 runtime not found. Install it first:" -ForegroundColor Red
    Write-Host "  https://aka.ms/dotnet/10.0/runtime-windows-x64" -ForegroundColor Yellow
    exit 1
}

# 1) latest release asset (win-x64 zip)
$rel = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest"
$asset = $rel.assets | Where-Object { $_.name -like 'SmartEdit-win-x64-*.zip' } | Select-Object -First 1
if (-not $asset) { Write-Host "ERROR: no win-x64 asset in latest release ($($rel.tag_name))" -ForegroundColor Red; exit 1 }
Write-Host "Downloading $($asset.name) ($([math]Round($asset.size/1KB)) KB) ..."

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
