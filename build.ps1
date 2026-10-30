# build.ps1 - Run this on personal laptop to produce the install package.
# Output: dist\VSDebugBridgeSetup\  (copy this folder to office laptop)

param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root  = $PSScriptRoot
$vsix  = Join-Path $root "VSDebugBridge"
$dist  = Join-Path $root "dist\VSDebugBridgeSetup"

Write-Host "`n=== VS Debug Bridge - Build & Package ===" -ForegroundColor Cyan

# 1. Find MSBuild
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found. Is Visual Studio installed?" }
$vsPath  = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
$msbuild = Join-Path $vsPath "MSBuild\Current\Bin\MSBuild.exe"
if (-not (Test-Path $msbuild)) { throw "MSBuild.exe not found at: $msbuild" }
Write-Host "MSBuild: $msbuild" -ForegroundColor Gray

# 2. Build VSIX
Write-Host "`n[1/3] Building VSDebugBridge ($Configuration)..." -ForegroundColor Yellow
& $msbuild "$vsix\VSDebugBridge.csproj" /p:Configuration=$Configuration /p:DeployExtension=false /v:minimal
if ($LASTEXITCODE -ne 0) { throw "MSBuild failed (exit $LASTEXITCODE)" }

$builtVsix = "$vsix\bin\$Configuration\VSDebugBridge.vsix"
if (-not (Test-Path $builtVsix)) { throw "Expected VSIX not found: $builtVsix" }
Write-Host "VSIX built: $builtVsix" -ForegroundColor Green

# 3. Package
Write-Host "`n[2/3] Packaging..." -ForegroundColor Yellow
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist | Out-Null

Copy-Item $builtVsix "$dist\VSDebugBridge.vsix"

$mcpDest = "$dist\mcp-server"
New-Item -ItemType Directory -Path $mcpDest | Out-Null
Copy-Item "$root\mcp-server\package.json" $mcpDest
Copy-Item "$root\mcp-server\index.js"     $mcpDest
if (Test-Path "$root\mcp-server\README.md") { Copy-Item "$root\mcp-server\README.md" $mcpDest }

# 4. Write Install.ps1
Write-Host "`n[3/3] Writing Install.ps1..." -ForegroundColor Yellow

$ExtensionId = "VSDebugBridge.8B5C6E1A-3D2F-4A7C-9F8E-1B6D4A2E5C90"

$installScript = @"
# Install.ps1 - Run on the OFFICE LAPTOP (no admin needed).
# Automatically uninstalls any existing version before reinstalling.

`$ErrorActionPreference = "Stop"
`$here = `$PSScriptRoot
`$ExtensionId = "$ExtensionId"

Write-Host ""
Write-Host "=== VS Debug Bridge - Office Install ===" -ForegroundColor Cyan

# --- 1. VSIX install ---
Write-Host ""
Write-Host "[1/3] Installing VS extension..." -ForegroundColor Yellow

`$vsixFile = Join-Path `$here "VSDebugBridge.vsix"
if (-not (Test-Path `$vsixFile)) { throw "VSDebugBridge.vsix not found next to this script." }

`$vswhere = "`${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path `$vswhere)) { throw "Visual Studio not found on this machine." }

`$vsPath   = & `$vswhere -latest -products * -property installationPath
`$vsixInst = Join-Path `$vsPath "Common7\IDE\VSIXInstaller.exe"
if (-not (Test-Path `$vsixInst)) { throw "VSIXInstaller.exe not found at: `$vsixInst" }

Write-Host "VSIXInstaller: `$vsixInst" -ForegroundColor Gray
Write-Host "Visual Studio must be CLOSED." -ForegroundColor Gray

# Uninstall existing
Write-Host "Checking for existing installation..." -ForegroundColor Gray
`$u = Start-Process -FilePath `$vsixInst -ArgumentList "/quiet /uninstall:`$ExtensionId" -Wait -PassThru
if (`$u.ExitCode -eq 0)    { Write-Host "Previous version uninstalled." -ForegroundColor Gray }
elseif (`$u.ExitCode -eq 2002) { Write-Host "No previous version - clean install." -ForegroundColor Gray }
else { Write-Host "WARNING: Uninstall exit code `$(`$u.ExitCode) - continuing." -ForegroundColor Yellow }

# Install fresh
Write-Host "Installing..." -ForegroundColor Gray
`$i = Start-Process -FilePath `$vsixInst -ArgumentList "/quiet ```"`$vsixFile```"" -Wait -PassThru
if (`$i.ExitCode -notin @(0, 1001)) {
    throw "VSIXInstaller failed (exit `$(`$i.ExitCode)).`nTry manually: ```"`$vsixInst```" ```"`$vsixFile```""
}
Write-Host "Extension installed. Restart Visual Studio for it to load." -ForegroundColor Green

# --- 2. npm install ---
Write-Host ""
Write-Host "[2/3] Installing MCP server dependencies..." -ForegroundColor Yellow

`$mcpDir = Join-Path `$here "mcp-server"
if (-not (Test-Path `$mcpDir)) { throw "mcp-server\ folder not found next to this script." }

`$nodCmd = Get-Command node -ErrorAction SilentlyContinue
`$node = if (`$nodCmd) { `$nodCmd.Source } else { `$null }
if (-not `$node) { throw "Node.js not found. Install from https://nodejs.org (no admin needed with the user installer)." }
Write-Host "Node: `$node" -ForegroundColor Gray

Push-Location `$mcpDir
try {
    & npm install --omit=dev
    if (`$LASTEXITCODE -ne 0) { throw "npm install failed." }
} finally { Pop-Location }
Write-Host "Dependencies installed." -ForegroundColor Green

# --- 3. Register MCP server ---
Write-Host ""
Write-Host "[3/3] Registering MCP server with Claude Code..." -ForegroundColor Yellow

`$claudeCmd = Get-Command claude -ErrorAction SilentlyContinue
`$claude = if (`$claudeCmd) { `$claudeCmd.Source } else { `$null }
if (-not `$claude) {
    Write-Host "WARNING: claude CLI not found in PATH." -ForegroundColor Yellow
    Write-Host "Run this manually after installing Claude Code CLI:" -ForegroundColor Yellow
    Write-Host "  claude mcp add vs-debugger node ```"`$mcpDir\index.js```"" -ForegroundColor White
} else {
    Write-Host "claude: `$claude" -ForegroundColor Gray
    & claude mcp add vs-debugger node "`$mcpDir\index.js"
    if (`$LASTEXITCODE -eq 0) { Write-Host "MCP server registered." -ForegroundColor Green }
    else { Write-Host "Already registered or check with: claude mcp list" -ForegroundColor Yellow }
}

Write-Host ""
Write-Host "=== Install complete ===" -ForegroundColor Cyan
Write-Host "Next steps:"
Write-Host "  1. Restart Visual Studio"
Write-Host "  2. Open your solution"
Write-Host "  3. Debug menu -> Claude Debug Control should now appear"
"@

$installScript | Set-Content "$dist\Install.ps1" -Encoding UTF8

# Summary
Write-Host "`n=== Package ready ===" -ForegroundColor Cyan
Write-Host "Output: $dist" -ForegroundColor Green
Write-Host ""
Get-ChildItem $dist -Recurse | ForEach-Object {
    Write-Host "  $($_.FullName.Substring($dist.Length + 1))"
}
Write-Host ""
Write-Host "Copy the '$dist' folder to your office laptop and run Install.ps1 there." -ForegroundColor Green
