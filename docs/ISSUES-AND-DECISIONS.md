# Issue and decision ledger — DeveloperTwin v4 (2026-11-02)

| ID | Problem or decision | Current state | Implementation / remaining check |
|---|---|---|---|
| DT-001 | NuGet `UglyToad.PdfPig` floating prerelease | Source fixed earlier | Stable `PdfPig` pinned. Verify restore on Windows. |
| DT-002 | Conserve hosted-model quota for specialized tasks | Local-first chat | Default chat uses local qwen3:8b; OpenRouter remains available for image/PDF analysis and explicit chat opt-in. |
| DT-003 | Image understanding | Retained | OpenRouter vision endpoint. Vision model may require an active multimodal free route. |
| DT-004 | Imported source files break `dotnet build` | **Fixed in source** | Workspace moved to top-level `data/workspace` + csproj compile exclusion for legacy workspace. Confirm regression test. |
| DT-005 | Qdrant connection refused `127.0.0.1:6333` | **Diagnosed / health UI added** | Start `docker compose up -d`. Confirm local Docker and embeddings. |
| DT-006 | Backend service monolith | **Refactored in source** | Split services, models, endpoints, agents; ~110-line Program.cs. Compile check pending. |
| DT-007 | Frontend was plain JS | **React rewrite in source** | React + TypeScript + Vite, chat, multi-upload, connection status, project explorer, approvals. npm build pending. |
| DT-008 | PDF/PPTX exports were absent | **Implemented in source** | QuestPDF + OpenXML SDK; test PDF and PPTX opening on Windows. SVG is schematic, not generative imagery. |
| DT-009 | Big ZIP/rar limit | **Partially addressed** | 50 MB ZIP; new approved local folder connection with no copy; RAR unsupported. |
| DT-010 | Azure DevOps ticket demo overscoped | **Removed** | No remote ticket publisher or credentials in v4. Handoff approval instead. |
| DT-011 | Human approval real use case | **Starter demo** | Agent proposes a handoff; user approves/rejects export. Native MAF workflow HITL still future. |
| DT-012 | Source isolation / privacy | **Starter safeguards** | Allowlist, text-only file reads, skip symlinks/binary/secrets. Auth/per-user isolation still required. |
| DT-013 | Session persistence | **Pending** | In-memory MAF sessions; React browser history is not equivalent to persisted agent sessions. |
| DT-014 | SSE streaming | **Pending** | Spinner and cancellation only; add MAF streaming in later iteration. |
| DT-015 | Dockerless vector fallback | **Not planned** | Qdrant is explicit for training; clear health diagnostics rather than hiding missing infrastructure. |

| DT-016 | Blank UI after successful APIs | **Mitigation in v4.1, exact browser cause pending** | Added React ErrorBoundary, robust localStorage load/write, explicit browser Console diagnostics. Verify on user's Windows browser. |
| DT-017 | Dev frontend built into backend on every start | **Fixed in v4.1 source** | Separate Vite `npm run dev` (:5173), ASP.NET `dotnet watch` (:5000), optional `build:api` for deployment. |
| DT-018 | Qdrant port 6333 bind already in use | **Startup mitigated** | Reuse reachable Qdrant; don't launch duplicate container. Verify existing collection ownership. |
| DT-019 | PowerPoint Text.Space compile error and nullability warnings | **Corrected in v4.1 source; runtime unverified** | Removed unsupported initializer, added template part guards. User must run verification. |

## Scope principles
1. Demonstrate one genuine MAF-hosted agent with typed C# tools before adding more agents.
2. Keep uploaded external project source inert and physically separate from app builds.
3. Keep local chat and embeddings as defaults; use hosted vision/PDF analysis where needed and make hosted chat opt-in.
4. Keep features small enough to defend in 15 minutes.
5. When Codex changes files, update this ledger and run `scripts/verify-v4.ps1`.

## Validation performed in this environment
- Parsed the .csproj XML and configuration JSON.
- Transpiled every TS/TSX source with the installed TypeScript compiler; **zero syntax diagnostics**. This is not a full `npm run build` and cannot validate missing packages or types.
- Verified the source ZIP has no runtime `data/workspace` or API `bin/obj` directories.
- **Not performed:** .NET build, NuGet restore, npm dependency installation, real Qdrant, OpenRouter, PPTX opening, PDF rendering and browser end-to-end tests (required toolchain/network access unavailable).

## 2026-11-02 — v4.2 follow-up

- **DT-016 — React chat blank screen:** Browser shows `TypeError: destroy is not a function` and the React `useEffect` cleanup warning in `<Chat>`. **Fix in source:** changed the implicit-return scrolling effect to a block-body effect in `frontend/src/components/Chat.tsx`. Requires browser smoke test on Windows; keep Strict Mode enabled. See `docs/REACT-EFFECT-CRASH-FIX.md`.

## v4.2 Web tools + demo knowledge (2026-11-05)
- DT-021: SearchWeb input schema expanded (`query`, `maxResults`, `freshness`), provider errors handled as safe structured results, capped public search context.
- DT-022: Added non-secret provider-use diagnostics and a development-only loopback test endpoint so provider connectivity is verifiable separately from MAF tool selection.
- DT-023: Tool-selection instructions and `[Description]` parameters improved for all five agent tools; no forced web search on private/RAG questions.
- DT-024: Added seven fictional `.txt` knowledge documents and end-to-end demo test prompts.
- Pending verification: Windows .NET restore/build, real Tavily or Brave response with user key, OpenRouter/ollama model actually invokes SearchWeb, knowledge retrieval quality after ingestion.
- Security boundary: demo policies in Qdrant are NOT access-control enforcement; true policy-based HITL remains future work.

## 2026-11-11 — reranking and knowledge lifecycle

- Added `KnowledgeRetrievalService` and replaceable local/no-op rerankers under `AI/Retrieval`. The existing MAF `SearchKnowledge` tool retrieves ten active/legacy candidates and selects up to five after validated structured scoring. Diagnostics preserve both scores and both ranks; retrieval and reranking have separate telemetry spans.
- Added deprecate/restore in Qdrant and the knowledge UI. Search prefilters active or missing-status payloads so legacy points remain visible. Re-upload preserves deprecated status. Delete still removes vectors and text.
- Added revision/pipeline checks before embedding. Identical uploads skip embedding and session invalidation; legacy pipeline metadata is upgraded on the next upload. Replacements retain retry-safe point IDs and remove obsolete chunks.
- Shared extraction now handles searchable PDF and DOCX locally. Scanned PDF/image extraction needs explicit per-upload permission before a hosted provider is used. Oversized extracted content is rejected rather than silently truncated.
- Verification: `dotnet test tests/DigitalTwin.Api.Tests --no-restore --nologo --verbosity minimal` passed 36 regression tests (one opt-in test skipped); `npm.cmd run build` in `frontend` passed; opt-in `TWIN_RUN_LIVE_RERANK_EVAL=1` with `dotnet test tests/DigitalTwin.Api.Tests --no-build --no-restore --filter 'Category=Live' --logger 'console;verbosity=detailed'` passed separately. The three live qwen3:8b questions each promoted the expected passage to rank 1.
- Initial cold qwen3:8b evaluation exceeded 45 seconds, so the bounded configurable timeout is now 90 seconds. The installed llama3.2:1b failed the relevance fixture and was not selected. No additional model download or paid model API was introduced.
- Docker's Linux engine was stopped and port 6333 was unavailable. Live Qdrant integration, browser interaction and hosted OCR were not claimed as verified. See [RERANKING-AND-KNOWLEDGE-LIFECYCLE.md](RERANKING-AND-KNOWLEDGE-LIFECYCLE.md) for API details, migration behavior, exact commands and limitations.
- Scope agreed with the user: first two phases only. Persistent memory, full guardrail/poisoning review, broader agent-registration refactoring and A2A are deferred.
