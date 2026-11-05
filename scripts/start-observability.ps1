$ErrorActionPreference = 'Stop'
# Aspire shows traces + metrics + structured logs; localhost ports only.
# Existing Qdrant stays on 6333; Aspire OTLP uses 4317 and UI uses 18888.
$running = docker ps --filter 'name=^/aspire-dashboard$' --format '{{.Names}}'
$created = docker ps -a --filter 'name=^/aspire-dashboard$' --format '{{.Names}}'
if ($running -eq 'aspire-dashboard') {
    Write-Host 'Existing Aspire container is running.'
} elseif ($created -eq 'aspire-dashboard') {
    docker start aspire-dashboard | Out-Null
} else {
    docker run -d --name aspire-dashboard `
        -p 127.0.0.1:18888:18888 `
        -p 127.0.0.1:4317:18889 `
        -p 127.0.0.1:4318:18890 `
        mcr.microsoft.com/aspire/dashboard:13 | Out-Null
}
Write-Host 'Aspire dashboard: http://127.0.0.1:18888'
Write-Host 'Login URL/token: docker logs aspire-dashboard'
Write-Host 'In the BACKEND terminal, set these BEFORE starting dotnet:'
Write-Host '  $env:Observability__OtlpEndpoint = "http://127.0.0.1:4317"'
Write-Host '  $env:Observability__ExportLogs = "true"'
Write-Host 'Leave GenAI sensitive data disabled. Restart backend to apply settings.'
