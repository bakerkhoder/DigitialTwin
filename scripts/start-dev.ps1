# Backward-compatible command: start only the API in this terminal.
# React must run separately via scripts\start-frontend.ps1.
& (Join-Path $PSScriptRoot 'start-backend.ps1')
