# DeveloperTwin v5 API acceptance checks (Windows PowerShell 5.1+).
# This executes real Qdrant/Ollama/model/report operations against localhost.
param([string]$BaseUrl = 'http://127.0.0.1:5000')
$ErrorActionPreference = 'Stop'
function Assert([bool]$condition, [string]$message) {
    if (-not $condition) { throw "FAIL: $message" }
    Write-Host "PASS: $message" -ForegroundColor Green
}
$health = Invoke-RestMethod "$BaseUrl/api/knowledge/health"
Assert ($health.qdrant -and $health.ollama) 'Qdrant and Ollama are reachable'
$source = 'v5-smoke-' + [guid]::NewGuid().ToString('N') + '.txt'
$file = Join-Path $env:TEMP $source
try {
    Set-Content -Path $file -Value (("OLD_RETIRED_POLICY_TOKEN: Manager approves over 700 dollars. `n") * 60) -Encoding UTF8
    $url = "$BaseUrl/api/knowledge/batch"
    $output = & curl.exe -sS --fail-with-body -F "files=@$file;filename=$source" $url
    if ($LASTEXITCODE -ne 0) { throw "Upload v1 failed: $output" }
    $first = $output | ConvertFrom-Json
    Assert (-not $first.results[0].error) 'First knowledge revision indexed'
    $oldCount = $first.results[0].chunks
    # Re-upload SAME filename, shorter content. Old surplus vectors must disappear.
    Set-Content -Path $file -Value 'NEW_CURRENT_POLICY_TOKEN: Director approves over 900 dollars.' -Encoding UTF8
    $output = & curl.exe -sS --fail-with-body -F "files=@$file;filename=$source" $url
    if ($LASTEXITCODE -ne 0) { throw "Upload v2 failed: $output" }
    $second = $output | ConvertFrom-Json
    Assert (-not $second.results[0].error) 'Updated knowledge revision indexed'
    Assert ($second.results[0].chunks -lt $oldCount) 'Second revision is shorter'
    $docs = Invoke-RestMethod "$BaseUrl/api/knowledge/documents"
    $match = @($docs | Where-Object { $_.source -eq $source })
    Assert ($match.Count -eq 1) 'Only one document entry remains for same filename'
    Assert ($match[0].chunks -eq $second.results[0].chunks) 'Old surplus vectors were removed'
    Invoke-RestMethod "$BaseUrl/api/knowledge/documents/$($match[0].id)" -Method DELETE | Out-Null
    $docs = Invoke-RestMethod "$BaseUrl/api/knowledge/documents"
    Assert (@($docs | Where-Object { $_.source -eq $source }).Count -eq 0) 'Delete removed all document vectors'
    Write-Host 'Knowledge upsert/delete checks passed.' -ForegroundColor Green
} finally { Remove-Item $file -ErrorAction SilentlyContinue }
$body = @{ title='V5 Smoke Test'; content="# Handoff`nPurpose: test PDF and PPTX.`n📌 Purpose`n## Architecture`nReact and .NET 9."; format='pdf' }
foreach ($format in 'pdf','pptx') {
    $body.format = $format
    $path = Join-Path $env:TEMP "DeveloperTwin-v5-smoke.$format"
    try {
        Invoke-WebRequest "$BaseUrl/api/reports/export" -UseBasicParsing -Method POST -ContentType 'application/json' -Body ($body | ConvertTo-Json) -OutFile $path
        Assert ((Get-Item $path).Length -gt 1000) "$format report generated (verify opening file manually)"
    } finally { Remove-Item $path -ErrorAction SilentlyContinue }
}
$status = Invoke-RestMethod "$BaseUrl/api/tools/web-search/status"
Write-Host ('Web search provider: ' + $status.provider + '; configured=' + $status.configured)
if (-not $status.configured) { Write-Warning 'Set TAVILY_API_KEY in the backend terminal, restart backend, run scripts/test-web-search.ps1' }
Write-Host 'Manual checks still required: React scroll, background messages across tabs, deleting conversation history, PDF/PPTX open successfully, and Aspire traces.' -ForegroundColor Yellow
