# Terminal 2: Vite serves React directly with HMR; /api is proxied to :5000.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location (Join-Path $root 'frontend')
if (-not (Test-Path 'node_modules')) {
    npm install
    if ($LASTEXITCODE -ne 0) { throw 'npm install failed.' }
}
Write-Host 'React -> http://127.0.0.1:5173' -ForegroundColor Cyan
Write-Host 'ASP.NET Core must run in another terminal on http://127.0.0.1:5000' -ForegroundColor Cyan
npm run dev
