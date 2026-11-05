# Run from any folder, AFTER starting the backend as Development on 127.0.0.1:5000.
# This makes a real search-provider request and therefore consumes a small amount of quota.
param(
  [string]$BaseUrl = 'http://127.0.0.1:5000',
  [switch]$ProbeAgent
)
$ErrorActionPreference = 'Stop'

Write-Host '1. Check configured search provider (no secrets returned)' -ForegroundColor Cyan
$status = Invoke-RestMethod "$BaseUrl/api/tools/web-search/status"
$status | ConvertTo-Json
if (-not $status.configured) {
  throw 'Web search is OFF. Set TAVILY_API_KEY or BRAVE_SEARCH_API_KEY in the BACKEND terminal, restart .NET, and retry.'
}

Write-Host '2. Call WebSearchTools DIRECTLY through the Development-only API' -ForegroundColor Cyan
$request = @{ query='site:learn.microsoft.com/en-us/agent-framework/ agents tools C#'; maxResults=3; freshness='any' } | ConvertTo-Json
try {
  $result = Invoke-RestMethod "$BaseUrl/api/dev/tools/search-web" -Method Post -ContentType 'application/json' -Body $request
} catch {
  throw 'Development test route is unavailable. Start the backend with $env:ASPNETCORE_ENVIRONMENT="Development" and bind 127.0.0.1 only.'
}
$result | ConvertTo-Json -Depth 8
if ($result.status -ne 'ok' -or @($result.results).Count -lt 1) {
  throw "Provider test did not return a public result. Status: $($result.status); message: $($result.message)"
}
Write-Host "Provider works: $(@($result.results).Count) HTTPS result(s)." -ForegroundColor Green

if ($ProbeAgent) {
  Write-Host '3. Ask MAF agent a CURRENT PUBLIC question; verify it invokes SearchWeb' -ForegroundColor Cyan
  $before = Invoke-RestMethod "$BaseUrl/api/tools/web-search/status"
  $ask = @{ sessionId = [guid]::NewGuid().ToString(); message = 'Search official Microsoft Learn documentation for current C# Microsoft Agent Framework function tools. Cite two HTTPS URLs and tell me what you verified online.' } | ConvertTo-Json
  $answer = Invoke-RestMethod "$BaseUrl/api/agent/ask" -Method Post -ContentType 'application/json' -Body $ask
  Write-Host $answer.reply
  $after = Invoke-RestMethod "$BaseUrl/api/tools/web-search/status"
  if ($after.providerCalls -le $before.providerCalls) {
    Write-Warning 'The LLM did not invoke SearchWeb. The provider works, but MODEL tool-choice is not yet verified. Check model function-calling support and agent instructions.'
  } else {
    Write-Host 'Agent actually invoked SearchWeb. Check URLs and contents for relevance.' -ForegroundColor Green
  }
}
