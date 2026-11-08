> **Consolidated v5:** See [docs/V5-MERGE-RELEASE-AND-TESTS.md](docs/V5-MERGE-RELEASE-AND-TESTS.md) for feature inventory, missing production requirements, run commands, re-index/delete semantics and the free web-search setup. This archive merges source versions available to this conversation; it cannot include unshared edits on your own PC.

# DeveloperTwin AI v4 — engineering agent (MAF + React)

**Goal:** A teachable local-first engineering assistant. Inspect ZIP projects or explicitly approved folders, ask a hosted Microsoft Agent Framework agent to explain them, use a RAG knowledge base, and export useful technical handoffs as **Markdown, PDF, PPTX, SVG or PNG**. The Approval Center gates folder scans and handoff exports (not Azure DevOps).

> **Training sample, not production-ready.** The .NET and React build still needs to be verified on the user's Windows machine. Do not expose to a LAN/public endpoint or ingest unapproved confidential code into OpenRouter.

## Recommended development workflow (v4.1)

**Two independent servers.** The React app lives at `http://127.0.0.1:5173` with hot reloading and `/api` requests proxied to the ASP.NET Core/MAF API at `http://127.0.0.1:5000`. You do not need to rebuild React when developing.

In **Terminal 1** (from the project root), set `OPENROUTER_API_KEY` in *that terminal* if you want OpenRouter, then run:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\start-backend.ps1
```

Alternatively, store the key outside the repository in .NET user-secrets:

```powershell
dotnet user-secrets set "Twin:OpenRouterApiKey" "<your-key>" --project .\src\DigitalTwin.Api
```

The backend uses `OPENROUTER_API_KEY` when set; otherwise it uses the bound `Twin:OpenRouterApiKey` setting. The key enables OpenRouter-backed image/PDF analysis; ordinary chat still uses local Ollama by default. To explicitly opt into OpenRouter for chat, set `Twin:Provider` to `openrouter` (or `auto`). Do not put keys in `TwinSettings.cs` or commit them in `appsettings.json`.

In **Terminal 2**:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\start-frontend.ps1
```

Open **http://127.0.0.1:5173**. `scripts/start-dev.ps1` is now a backward-compatible backend-only entry point. `scripts/verify-v4.ps1` still builds both projects, but builds React to `frontend/dist`. Only the optional `scripts/publish-frontend.ps1` copies a release build into `src/DigitalTwin.Api/wwwroot` for standalone hosting on port 5000.

**If the screen blanks out:** open DevTools (`F12`) → **Console**, copy the *first red JavaScript error*. v4.1 adds an error boundary and guards browser conversation storage, so an unexpected render error should display a diagnostic rather than a blank screen. See `docs/DEV-MODE-DEBUGGING.md`.

**Qdrant port conflict:** another project may already run it on 6333; the backend startup script now reuses an existing healthy Qdrant instead of starting a second container. Check `http://127.0.0.1:6333/collections` and `docker ps` if unsure.

---

## 1. What changed from v3?

- **Critical fix:** Runtime data now lives in `data/workspace/` outside `src/DigitalTwin.Api`; `.csproj` additionally excludes legacy `workspace/**/*.cs`. ZIP contents are never built with DeveloperTwin.
- **Large local projects:** `POST /api/projects/connect` keeps direct connection for folders under `Twin:AllowedProjectRoots`. Folders outside those roots are queued for human review; approval grants read-only access only to that requested folder. No copying or upload.
- **React 19 + TypeScript + Vite frontend:** chat, multi-attachments, loading, cancellation, knowledge with health checks, project scanning, and approvals. Backend routes remain ASP.NET Core Minimal APIs.
- **Smaller C# classes:** `WorkspaceTools`, `ProjectImporter`, `ProjectCatalog`, `KnowledgeStore`, `VisionTools`, `WebSearchTools`, `ReportExporter`, `ApprovalQueue` are separate services; Minimal API endpoints are grouped by feature.
- **Real report files:** `ReportExporter` generates PDF (QuestPDF), PowerPoint (OpenXML SDK), SVG vector diagram and Markdown. Agent writes report content; export logic uses deterministic code.
- **Simpler approvals:** Agent can propose a handoff; human approves or rejects PDF export. Azure DevOps publishing removed for now.
- **Local-first chat:** ordinary chat uses installed `qwen3:8b` by default, even when an OpenRouter key is configured. OpenRouter remains available for image/PDF analysis and can be explicitly selected for chat; local `nomic-embed-text` embeddings remain unchanged.

## 2. What you already need

- Windows with **.NET 9 SDK**, **Node.js 20+**, **Docker Desktop**, and **Ollama**.
- Installed Ollama models: `qwen3:8b` and `nomic-embed-text:latest` are sufficient. `llama3.2:1b` can stay installed unused.
- An OpenRouter API key for image/PDF analysis (free-model limits and availability change). Chat uses local `qwen3:8b` by default. To opt into OpenRouter chat, set `Twin:Provider` to `openrouter` (requires the key) or `auto`; set `Twin:OpenRouterChatModel` to an active tool-capable model. Avoid relying on `openrouter/free` for guaranteed tool calling.
- Image analysis also requires `OPENROUTER_API_KEY` and a vision-capable `Twin:OpenRouterVisionModel`, or an installed vision-capable Ollama model selected with `Twin:VisionModel`. The default text model `qwen3:8b` cannot analyze images.
- Docker Desktop running. Qdrant uses the bundled `docker-compose.yml`.
- Optional `TAVILY_API_KEY` or `BRAVE_SEARCH_API_KEY` for public web search.

## 3. Start on Windows (PowerShell)

Open a terminal in the project root:

```powershell
# In this shell session, set your secret locally (don't commit it)
$secureKey = Read-Host 'OpenRouter API key' -AsSecureString
$env:OPENROUTER_API_KEY = [System.Net.NetworkCredential]::new('', $secureKey).Password
# Optional: $env:TAVILY_API_KEY = Read-Host 'Tavily key'

# Start Ollama normally, then check installed models:
ollama list

# Build frontend and run the app; the script also starts Qdrant
powershell -ExecutionPolicy Bypass -File .\scripts\start-dev.ps1
```

Open **http://127.0.0.1:5000**. React is built automatically by the helper. If you prefer two terminals for UI hot-reload:

```powershell
# Terminal A (backend)
cd src\DigitalTwin.Api
dotnet run --urls http://127.0.0.1:5000

# Terminal B (React live dev server)
cd frontend
npm install
npm run dev
# Open http://127.0.0.1:5173 (Vite proxies /api to localhost:5000)
```

Build check: `powershell -ExecutionPolicy Bypass -File .\scripts\verify-v4.ps1`.

**Note:** `npm install` requires access to npm. An ordinary `dotnet run` without first building React shows a helpful “frontend not built” page.

## 4. How to fix knowledge ingestion

Qdrant is not an LLM: it stores vectors (numeric document embeddings), source metadata and text chunks. **Ollama** locally computes vectors using `nomic-embed-text`. The **MAF agent** uses `SearchKnowledge` to retrieve relevant chunks. When users ask it questions, the agent can cite the original filenames.

```powershell
docker compose up -d
docker compose ps
Invoke-RestMethod http://127.0.0.1:6333/collections
Invoke-RestMethod http://127.0.0.1:11434/api/tags
```

You can also inspect Qdrant at **http://127.0.0.1:6333/dashboard**. If it isn't reachable, start Docker Desktop and inspect `docker compose logs qdrant`. Health status appears above the upload control in Knowledge Library. No collection needs to be created manually: `KnowledgeStore.EnsureCollection` creates it when needed.

1. Upload 2 text/PDF files in **Knowledge** (up to 15 files, 8 MB each).
2. Click **Embed into knowledge base**.
3. Inspect per-file success/error. Try **Semantic knowledge search**.
4. Go to **Assistant**, enable **Use knowledge**, and ask a question based on the source documents.

**PDF analysis:** Every PDF uploaded in chat or Knowledge is sent to OpenRouter as a PDF attachment and analyzed with `Twin:OpenRouterVisionModel`. The request explicitly selects OpenRouter's `cloudflare-ai` PDF parser to avoid its default per-page Mistral OCR charge; model inference is still subject to the selected model's pricing and limits. PDF contents leave the local machine. Arabic, scanned pages, and visual content can be analyzed, but extraction quality and page/context limits depend on the parser and model. Keep `TextExtractor` for local text/code files.

**Image analysis:** chat attachments and image ingestion in Knowledge both call `VisionTools`. With the hosted provider, set `OPENROUTER_API_KEY` and choose a vision-capable `Twin:OpenRouterVisionModel`; with Ollama, set `Twin:VisionModel` to a locally installed vision model (the app does not download one). A missing or failing provider now produces an actionable API error instead of an unhandled HTTP 500.

## 5. ZIP and large local folders

**ZIP workflow:** import a `.zip` up to 50 MB; `ProjectImporter` limits file count, uncompressed size, extensions, traversal, symlinks, and generated folders. Extracted files are under **`data/workspace/projects/project-...`**, NOT under `src/DigitalTwin.Api`.

**Large project workflow (no ZIP):** configure any pre-approved roots in `Twin:AllowedProjectRoots`:

```json
"AllowedProjectRoots": ["E:\\MafTraining", "D:\\WorkProjects"]
```

Enter an absolute folder path in **Projects & folders**. Paths under an allowed root connect immediately as before. Paths outside all allowed roots appear in the **Approval Center**. Rejecting leaves the folder unconnected; approving revalidates and scans it, then grants read-only access only to that exact folder and its descendants. Its `linked-xxxxxxxxxx` ID is stored in `data/workspace/connected-projects.json`. Do not add broad roots such as an entire system drive.

Existing v3 imported-project directories under `src/DigitalTwin.Api/workspace/` do **not** need to be copied; they will now be excluded from compilation. You can re-import them to the new workspace to make them visible to the v4 UI. Once backed up, remove the legacy directory to prevent confusion.

## 6. Handoff export example

1. Import/link a project and click **Ask agent to analyze this project**.
2. Let the agent inspect the project with `ListProject` and `ReadFile`.
3. When its Markdown report is complete, use **MD / PDF / PPTX / PNG / SVG** above the chat input.
4. For the approval demonstration, click **Request review** above chat, or ask: **“Propose this handoff for approval. The title is ProcureStudio onboarding. Include architecture, dependencies, setup, security notes, and risks.”**
5. Open **Approvals**, preview and approve/reject. Approval generates a PDF download (no email or external publishing).

SVG/PNG diagrams are **simplified, deterministic diagrams based on headings**, not a generative image model. PDF and PPTX are real binary files. In this sample PPTX is a straightforward text slide deck, not a visually polished AI slide designer.

## 7. Project structure and reasons

```
frontend/                         React UI (view state + calls to HTTP API)
  src/App.tsx                      Navigation, sessions, provider health, request lifecycle
  src/components/Chat.tsx          Markdown chat, attachments, exports, thinking/cancel UI
  src/components/Knowledge.tsx     Batch ingestion, health, retrieval
  src/components/Projects.tsx      ZIP imports, allowlisted local folders, tree view
  src/components/Approvals.tsx     Manual review/approve/reject handoff
  src/lib/api.ts                   HTTP/error handling & file downloads
src/DigitalTwin.Api/
  Program.cs                       Dependency injection, IChatClient, MAF AddAIAgent, HTTP host
  Agents/                           Model fallback and AgentSession tracking
  Endpoints/                        Thin Minimal API transport adapters
  Models/                           Typed settings, DTOs
  Services/                         Business behavior / local tools / integrations
  DigitalTwin.Api.csproj           Pinned NuGet dependencies + workspace compile exclusion
  wwwroot/                          Built React files (generated by Vite)
data/workspace/                    Runtime files (gitignored, outside the API compile glob)
docs/                              MAF concept map, training plan, issue log, tests
scripts/                           PowerShell developer scripts
```

The design purposefully has **one hosted agent**, not unnecessary microservices or a pretend multi-agent architecture. MAF reasoning/tool orchestration is separate from deterministic C# services. The approval queue is application-level HITL, not a checkpointed MAF workflow.

## 8. Current limits / warnings

- **Build unverified here:** This workspace has no `dotnet` executable and cannot access npm/NuGet networks; run `verify-v4.ps1` and share errors if any.
- Browser localStorage stores chat history only for this demo (including possible sensitive text). Do not use this sample with confidential corporate content or expose it without authorization and durable protected storage.
- Agent sessions and approvals are memory-only and reset on backend restart. Approval endpoint lacks real authentication; localhost and origin checks are **not a substitute** for auth.
- Not implemented: SSE token streaming, native MAF checkpointed workflows, OCR, DOCX input, persistent secure user accounts, full repository indexing or generative PNG artwork.
- OpenRouter free model tool calling and rate limits vary; the configured model may disappear or not support all required features. The UI displays the effective provider from `/api/status`.
- QuestPDF **evaluation license** is set for this training sample. Check actual licensing before commercial or public-sector deployment.
- Reports should be reviewed before handing to new staff; LLM-derived facts can be wrong and diagrams are illustrative.

See **docs/MAF-EXPLAINED.md**, **docs/END-TO-END-TESTS.md**, **docs/CODEX-VERIFY.md** and **docs/ISSUES-AND-DECISIONS.md**.
