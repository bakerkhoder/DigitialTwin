# Optional production/demo build: copy React into ASP.NET Core wwwroot.
# Normal development uses npm run dev and NEVER needs this script.
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'frontend')
if (-not (Test-Path 'node_modules')) {
    npm install
    if ($LASTEXITCODE -ne 0) { throw 'npm install failed.' }
}
npm run build:api
if ($LASTEXITCODE -ne 0) { throw 'Frontend deploy build failed.' }
Write-Host 'React published to ASP.NET Core wwwroot.' -ForegroundColor Green
