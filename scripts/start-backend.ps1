# Terminal 1: start only the .NET API with hot-reload support.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $env:ASPNETCORE_ENVIRONMENT) { $env:ASPNETCORE_ENVIRONMENT = 'Development' }

# Don't try to start a second Docker container if Qdrant is already bound
# to port 6333 (including a container from the previous DeveloperTwin version).
try {
    Invoke-WebRequest 'http://127.0.0.1:6333/collections' -UseBasicParsing -TimeoutSec 3 | Out-Null
    Write-Host 'Qdrant already reachable on port 6333; reusing it.' -ForegroundColor Green
}
catch {
    Write-Host 'Qdrant unavailable; attempting docker compose up -d...' -ForegroundColor Yellow
    Push-Location $root
    try {
        docker compose up -d
        if ($LASTEXITCODE -ne 0) { Write-Warning 'Could not start Qdrant. Check Docker Desktop and port 6333.' }
    } finally { Pop-Location }
}
if (-not $env:OPENROUTER_API_KEY) {
    Write-Host 'OPENROUTER_API_KEY is not set in this terminal; app configuration or .NET user-secrets may still provide it.' -ForegroundColor DarkGray
}
if (-not $env:TAVILY_API_KEY -and -not $env:BRAVE_SEARCH_API_KEY) {
    Write-Warning 'Web search: OFF. Set TAVILY_API_KEY (free quota available) in THIS terminal before starting the backend.'
}
Set-Location (Join-Path $root 'src\DigitalTwin.Api')
Write-Host 'Backend -> http://127.0.0.1:5000' -ForegroundColor Cyan
Write-Host 'Frontend (separate terminal) -> run scripts\start-frontend.ps1; browse http://127.0.0.1:5173' -ForegroundColor Cyan
dotnet watch run --no-launch-profile --urls http://127.0.0.1:5000
