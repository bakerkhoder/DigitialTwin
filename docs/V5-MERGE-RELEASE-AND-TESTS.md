# DeveloperTwin v5 — Consolidation, verification and explanation

This is a **full source snapshot** based on the last packaged MAF + React + OpenTelemetry edition, not the later local files on your PC. No automatic access to your local unshared edits is possible. Back up your working directory first.

## Implemented in this snapshot

- Microsoft Agent Framework hosted `DeveloperTwin` agent, in-memory sessions and typed C# tools.
- OpenRouter-first chat with optional Ollama (`qwen3:8b`) fallback, local `nomic-embed-text` and Qdrant.
- Tavily/Brave **explicit C# web tool** registered via `AIFunctionFactory`, selected by the model only for public, current facts. Provider status endpoint, development smoke test and clear instructions when the API key is absent.
- Qdrant file listing, per-document deletion (vectors + text payload), and **same-filename re-indexing that deletes stale chunks**. Handles legacy points indexed before v5 (grouped by `source`).
- Chat-history deletion from browser state, plus best-effort deletion of the app's in-memory session registry reference. **This does not purge upstream hosted-model logs or telemetry**.
- Per-thread pending requests in React: moving to another chat or page does not abort the original request. The app must remain open and backend available. Reloading/closing the tab can still terminate the in-flight HTTP request; fully durable background jobs are a later feature.
- PDF export: replace decorative emoji with readable ASCII tokens, use system font fallback for Windows training, present API error on failures. For production Arabic/Unicode, bundle supported font families yourself, and review QuestPDF licensing.
- PPTX export: OpenXML modifies an **expandable** MemoryStream and disposes presentation before returning bytes.
- Reports approvals: preserve pending request if export fails, preview Markdown rather than escaped JSON. This remains the **handoff-export demo**, NOT native MAF approval with a policy engine.
- Image-generation **optional** via Cloudflare Workers AI or explicitly opted-in paid OpenRouter; regular SVG/PNG diagram is deterministic. Not masked editing.
- Shared scrolling CSS for Knowledge, Projects and Approvals, refined deletion UI, React error boundary.
- OpenTelemetry ActivitySource, metric instruments, OTLP exporter and Aspire dashboard script.

## Qdrant document lifecycle

1. Read a document, chunk it and embed it locally using `nomic-embed-text`.
2. Compute content revision SHA-256, create versioned point UUIDs and payload (`source`, `documentId`, `revision`, `indexedAtUtc`, `text`, `chunk`).
3. Scroll existing point IDs for that source, prepare all new embeddings before writing, then upsert new points (wait=true) and delete stale previous point IDs (wait=true). If an upsert fails, attempt cleanup and preserve older points.
4. List all indexed documents by scrolling Qdrant and grouping by source filename; deleting removes every vector and payload with the requested source's exact ID.
5. Every successful knowledge change invalidates in-memory agent session references, preventing subsequent requests from inheriting obsolete retrieved snippets. This does not change already-rendered chat answers; ask a fresh grounded question after updating knowledge.
6. `Twin:KnowledgeMinScore` defaults to 0.30 and filters low-similarity passages. Tune it for the embedding model and your sample questions; similarity alone does not prove a claim.
7. **Same filename means same logical document** in this educational, single-user app. Use unique filenames for different docs. The mutation semaphore prevents overlapping uploads/deletions in the same process. Qdrant updates are not a global atomic multi-step transaction; a power failure mid-reindex may require retrying. Source-level collection deletion is not performed.
8. Deleting a document does not remove already generated answers from past chat history, the original file on disk, provider logs, or exports. Regenerate answers after changing knowledge.

## Tool-call architecture and free web search

The official [MAF hosted web search documentation](https://learn.microsoft.com/en-us/agent-framework/agents/tools/web-search?pivots=programming-language-csharp) shows a `HostedWebSearchTool` with a **supported provider**, notably Microsoft Foundry. That tool is NOT magically free or provider-independent: a generic OpenRouter `IChatClient` using a free model may not support it. To keep the training app understandable and model-portable, this project exposes an ordinary typed C# `SearchWeb(query, maxResults, freshness)` function using Tavily or Brave API. Web search remains **off until a search-provider key is set in the backend process**. The agent may call it zero or more times; do not claim tool execution solely from model output.

Tavily advertises **1,000 free API credits/month**, account registration and an API key required. OpenRouter's hosted web search / server tools generally have separate pricing and are not implied by a free LLM model. Never send confidential code/paths/credentials to public search.

### Configure Tavily (recommended)

1. Register at <https://www.tavily.com/> and create a free API key.
2. In the **same PowerShell terminal** as the backend:

```powershell
$secure = Read-Host 'Tavily API key' -AsSecureString
$env:TAVILY_API_KEY = [System.Net.NetworkCredential]::new('', $secure).Password
$env:ASPNETCORE_ENVIRONMENT = 'Development'
.\scripts\start-backend.ps1
```

3. Open `http://127.0.0.1:5000/api/tools/web-search/status`. Expect `provider=tavily`, `configured=true`.
4. Run `scripts/test-web-search.ps1 -ProbeAgent` from another terminal. Its first test probes Tavily without an LLM; the second checks whether the MAF model actually called `SearchWeb`. If the first works but the second doesn't, try a tool-calling-compatible model and check agent instructions.
5. Test question: “Find current official Microsoft Agent Framework web-search documentation and cite two URLs.”

For OpenRouter chat, set `OPENROUTER_API_KEY` in the same backend terminal. If absent, `Twin:Provider=auto` selects your installed Ollama model.

## Run locally

```powershell
cd E:\MafTraining\developer-twin-v5-consolidated
.\scripts\start-backend.ps1       # Terminal 1: :5000; starts/reuses Qdrant
.\scripts\start-frontend.ps1      # Terminal 2: :5173 with Vite HMR
.\scripts\start-observability.ps1 # Optional Terminal 3: Aspire
.\scripts\smoke-test-v5.ps1       # Terminal 4: actual API checks
```

Use **port 5173** for normal frontend development; port 5000 serves the API. Set `AllowedProjectRoots` in `src/DigitalTwin.Api/appsettings.json` before connecting large local folders. Do not store keys in Git or commit your data/workspace, React built output, `bin`, `obj`, node_modules or real confidential documents.

## Manual acceptance checks

- Upload fictional `01-procurement-approval-policy.txt`; verify it appears in Indexed documents with source and chunk count; search `$62,000` threshold.
- Replace it with a shorter TXT using the same filename; verify the chunk count shrinks and previous version no longer appears in search. Delete; verify no indexed chunks returned for its source.
- Start long assistant chat; switch to Knowledge or a second chat; return and verify the original answer has arrived. **Do not refresh browser mid-request for this test**.
- Delete one chat; verify it disappears and does not return on refresh; clear all; verify empty history.
- Export PDF including 📌 and PPTX and confirm files open in a reader, not merely that HTTP status is 200.
- Check all pages scroll at 1366×768 and on mobile viewport; inspect Chrome DevTools console for red errors.
- Enable OTLP and inspect one trace, tool timings, and request logs in Aspire.
- Configure Tavily and verify source URLs via live provider smoke test.

## Remaining limits to describe honestly at assessment

- The runtime has not been independently compiled/tested in this tool environment (no .NET SDK, npm registry unreachable). Do not claim it is end-to-end certified until `verify-v4.ps1`, `smoke-test-v5.ps1`, and UI checks pass on Windows.
- Native MAF resumable HITL with policy-based file-access approvals is **not yet built**. The existing approval screen is a simple report-export queue.
- Chat and approval state remain single-user, local/dev-oriented; add authenticated persistent storage, authorization, and proper jobs for browser reload resilience before production.
- No guarantee that all scripts/packages stay compatible indefinitely; verify pinned package versions and licensing.
- Image editing/inpainting masks and real editable PDF manipulation are not implemented.
