# DeveloperTwin v4.1 — development servers and frontend debugging

## Why the screen went blank even though APIs returned 200

`dotnet build` and `npm run build` only prove compilation/bundling. A React component can still throw at runtime. A browser Console error—not ASP.NET Core HTTP logs—is needed to identify the exact exception. Response `304 Not Modified` is normal caching. The source had two fragile paths: unvalidated conversation records loaded from `localStorage`, and an unguarded `localStorage.setItem(...)` in an effect, which can throw `QuotaExceededError` after the first render when history contains large reports. v4.1 guards these, adds an ErrorBoundary, and displays a warning when history cannot be persisted. **These were plausible faults, not a proven diagnosis of the user's exact browser crash.**

## Development setup

1. Start Docker Desktop and Ollama if using local embeddings or fallback.
2. Terminal A: set `$env:OPENROUTER_API_KEY` in that same terminal, then `powershell -ExecutionPolicy Bypass -File .\scripts\start-backend.ps1`.
3. Terminal B: `powershell -ExecutionPolicy Bypass -File .\scripts\start-frontend.ps1`.
4. Browse `http://127.0.0.1:5173`. Vite proxies `/api/*` to port 5000. Backend API remains on port 5000.

You can also execute `dotnet watch run --no-launch-profile --urls http://127.0.0.1:5000` inside `src/DigitalTwin.Api`, and `npm run dev` inside `frontend` directly.

## Debug a React crash

1. Open F12 → **Console**, select **Errors**, and find the earliest red exception and its React component stack.
2. F12 → **Network**, reload and inspect `/api/status` and `/api/knowledge/health`. A HTTP 200 does not mean `qdrant: true`.
3. Try an Incognito/InPrivate window. If it works there, old browser storage may be involved.
4. Use the ErrorBoundary's **Clear saved conversations and reload** only if the saved conversations are expendable; it clears only the v4 conversation keys.
5. If React shows nothing even in InPrivate, check browser extensions and the Console for asset-import / JavaScript syntax errors. Save the exact red error to diagnose.

## Qdrant port conflict

`docker compose up -d` failing with `bind: address already in use` means another process/container is already bound to 127.0.0.1:6333. If `Invoke-RestMethod http://127.0.0.1:6333/collections` succeeds, reuse it instead of launching another. Use `docker ps --format "table {{.Names}}\t{{.Ports}}"` to identify the owner. Never stop a shared Qdrant container without checking that other projects don't depend on it.

## Build vs run

- `scripts/verify-v4.ps1`: restore/build .NET and type-check/build React into `frontend/dist`.
- `scripts/start-backend.ps1`: starts .NET API, does not build or publish React.
- `scripts/start-frontend.ps1`: Vite HMR on port 5173.
- `scripts/publish-frontend.ps1`: optional release build placed in backend `wwwroot`, enabling one-server demo on port 5000.

## Why this separation is good engineering

- React remains an independent client with its own `package.json`, development server and tests.
- MAF stays behind ASP.NET Core Minimal API endpoints; clients never receive model API credentials.
- The Vite development proxy avoids browser CORS complexity while keeping the same `/api/*` URLs used by the release build.
- An error boundary catches render/lifecycle crashes; API requests require independent error handling.
