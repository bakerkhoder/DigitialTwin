$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location (Join-Path $root 'src\DigitalTwin.Api')
Write-Host '1. Restore / compile backend' -ForegroundColor Cyan
dotnet restore
if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
dotnet build --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Backend build failed' }
Set-Location (Join-Path $root 'frontend')
Write-Host '2. Build frontend' -ForegroundColor Cyan
if (-not (Test-Path 'node_modules')) {
    npm install
    if ($LASTEXITCODE -ne 0) { throw 'npm install failed' }
}
npm run build
if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed' }
Write-Host 'Both builds completed. Start backend and frontend in separate terminals: scripts\start-backend.ps1 and scripts\start-frontend.ps1' -ForegroundColor Green
