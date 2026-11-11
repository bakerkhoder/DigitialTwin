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
| DT-013 | Session persistence | **Implemented and tested** | SQLite stores transcripts and native MAF snapshots; restored across new agent/HTTP-host instances. Browser history imports once. See `CONVERSATIONS-AND-MEMORY.md`. |
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

## 2026-11-11 — live Qdrant verification

- Added two opt-in `LiveKnowledgeIntegrationTests` using generated `developertwin-test-{guid}` collections and real `nomic-embed-text` embeddings plus local `qwen3:8b` scoring. Both tests passed; all three evaluation questions retained the correct passage at rank 1 after real vector retrieval and reranking, without fallback.
- Verified unchanged uploads make zero embedding/upsert requests; deprecate/restore filtering; status preservation on re-upload; revision replacement; obsolete-chunk removal; and hard deletion. Verified scrolling and lifecycle mutations for 205 legacy points with no status metadata.
- Both test collections were deleted and confirmed absent. The existing `developer_knowledge` collection remained at three points. The user's running API was left running, and `/api/knowledge/health` reported Qdrant and Ollama reachable.
- Command: `$env:TWIN_RUN_LIVE_QDRANT_TESTS = '1'; dotnet test tests/DigitalTwin.Api.Tests --artifacts-path data/live-qdrant-verification -p:UseAppHost=false --filter 'Category=LiveQdrant' --logger 'console;verbosity=detailed'`. Separate build output avoids replacing the locked executable of the running development API. Restore used installed dependencies; the NuGet vulnerability-feed lookup was unavailable.
- Next planned phase remains guardrails and poisoning prevention, followed by durable sessions/memory and agent composition cleanup. This follow-up added verification only; those later phases have not been implemented.
- The subsequent default regression run passed 36 tests and skipped the three opt-in live tests as intended. No frontend or production-code changes required another frontend build.

## 2026-11-11 — Phase 3 guardrails and poisoning prevention

- Added native MAF run/function middleware and bounded chat middleware under `AI/Guardrails`. All chat routes use one guarded execution service, with tool/model/repetition/deadline/context/output limits and safe handling of rejected session history.
- Added backend-owned public-search and handoff capabilities. Public search is explicitly enabled per request and denied after private evidence enters the conversation. Direct web/handoff methods enforce the same capability checks. Model arguments and document text cannot grant permissions or approve folders/documents.
- Centralized message/upload checks, protected sensitive path segments, redacted recognizable credentials in source evidence and output, and suppressed automatic external Markdown image requests. Handoff proposal permission is a structured API flag; the existing UI Request review action remains available.
- Added document screening and Qdrant review-state prefiltering. Suspicious uploads stay pending until exact-revision review; changed content resets review, stale approval returns 409, and credential-bearing documents must be cleaned and re-uploaded. Added a review UI and harmless adversarial demo file. Existing documents re-index on their next upload; no bulk migration ran.
- Verification: 89 offline tests passed (four opt-in tests skipped), including native/hosted MAF, HTTP, adversarial tool requests, workspace escapes, egress counters, output/citation checks and review conflicts. `npm.cmd --prefix frontend run build` and `npm.cmd --prefix frontend run test:guardrails` passed. Three opt-in live Qdrant tests passed separately, including the new review lifecycle scenario. All temporary collections were deleted; existing knowledge retained its three points.
- Commands: `dotnet test tests/DigitalTwin.Api.Tests --artifacts-path data/guardrail-verification -p:UseAppHost=false --no-restore --nologo --verbosity minimal`; live run with `TWIN_RUN_LIVE_QDRANT_TESTS=1`, `--no-build --filter 'Category=LiveQdrant' --logger 'console;verbosity=detailed'`. A separate production startup on port 5098 verified the actual local chat reply `READY` and HTTP 403 for a cross-site review attempt; the smoke server was stopped afterward.
- NuGet vulnerability-feed lookup was unavailable, while cached dependencies restored and compiled successfully. No new dependencies, model downloads or paid model services were introduced. Interactive browser review/checkbox use was not exercised; renderer output and HTTP behavior were tested.
- Detection and redaction are heuristic; citation checks enforce minimum source presence rather than semantic truth. Backend tool authorization remains independent. Durable sessions/privacy markers and long-term memory are Phase 4. See [GUARDRAILS-AND-POISONING-PREVENTION.md](GUARDRAILS-AND-POISONING-PREVENTION.md).

## 2026-11-11 — simplify public search and fix answer formatting

- At the user's request, removed the public-search checkbox and whole-conversation/private-evidence restriction. Public search is automatic, including after document/project questions. The legacy `allowWebSearch` field is accepted but ignored. Recognizable credential/local-path query checks remain; this policy no longer claims to detect arbitrary confidential or encoded text.
- Removed redundant JSON evidence wrapping. Added readable source excerpts and an Ollama adapter that sends native plain-text tool results without changing MAF's stored call ids. Disabled local thinking by default and set the local context to 8192 tokens to reduce thinking-only answers and context clipping. No model or dependency download was added.
- Extracted the shared answer instructions to `Agents/AgentInstructions.cs`: use live search for changing public facts, focus on the latest question, keep the user's language, and avoid explaining transport JSON. Added the current date/time in `Twin:ChatTimeZone` (Asia/Beirut by default).
- Added a single tool-free recovery for an empty final answer. Long weather replies get a bounded structured presentation pass and server-rendered Markdown with actual source links; the corrected response enters conversation history. This may add one local model call, without repeating searches or other tools. Safe trace counters describe formatting/model/source counts without capturing prompts or source content.
- Increased search-provider timeout from 12 to 25 seconds after observing a real timeout. If fresh public evidence is unavailable, the app states that specific failure instead of inventing updated facts or claiming general internet access is unavailable.
- Validation: `dotnet test tests/DigitalTwin.Api.Tests --artifacts-path data/web-response-verification -p:UseAppHost=false --no-restore --nologo --verbosity minimal` passed 99 tests (four opt-in tests skipped). `npm.cmd --prefix frontend run build` and `npm.cmd --prefix frontend run test:guardrails` passed. Live local Ollama/Exa requests exercise the original weather question and same-session follow-ups without the old opt-in flag. The NuGet vulnerability-feed lookup remained unavailable; installed dependencies compiled successfully.
- Exact file/behavior/purpose mapping and run instructions: [WEB-SEARCH-RESPONSE-FIX.md](WEB-SEARCH-RESPONSE-FIX.md). SQLite conversation/memory work remains the next phase; it is not part of this fix.
- Final live verification: the original Lebanon weather question and same-session Beirut follow-up both produced three-bullet Markdown summaries with real source links. The follow-up used Celsius; Exa diagnostics reported two requests and `lastOutcome=ok`. The isolated API was stopped after validation.

### Recover temporary web-search failures within the same turn

- The reported trace showed a failed Exa request after about 12 seconds followed by a successful HTTP 200 on the next user turn. `WebSearchTools.cs` now retries a timeout, connection failure or HTTP 408/500/502/503/504 once, using a fresh request after 250 ms. Permanent errors, rejected keys, exhausted quota and malformed JSON do not trigger retries. Caller cancellation and the overall agent deadline still stop work.
- Added the effective timeout (25 seconds per attempt), attempt limit (two) and cumulative retry count to `/api/tools/web-search/status`, plus attempt metadata on the tool trace. This makes it possible to distinguish an older backend process from the current retry policy. The tradeoff is one possible extra provider request and a longer wait when both attempts time out.
- `WebSearchRetryTests.cs` covers recovery, the attempt limit, permanent errors, cancellation and recovered evidence reaching MAF without another user message. Validation: the default API suite passed 116 tests, with four opt-in live tests skipped. No model, frontend or search permission changes were needed for this follow-up.
- A live weather smoke request against the rebuilt isolated API returned a summary and a retrieved link. Diagnostics confirmed a 25-second timeout, two-attempt limit, one provider call, zero retries and `lastOutcome=ok`; controlled HTTP tests cover the retry itself. The isolated API was stopped after validation.

### Phase 4: durable conversation history and explicit personal memory

- Added local SQLite state, schema migrations, atomic transcript/native-session saves and native MAF restoration. Failed turns evict uncommitted context while preserving the last successful snapshot. Deletion waits for active runs and leaves identifier-only markers to prevent resurrection by stale requests/imports.
- Connected frontend history to list/detail/delete APIs and imported valid previous browser records idempotently. Import restores display messages; earlier native tool context was never stored in the browser and cannot be reconstructed from it. History loading/deletion errors are visible.
- Added explicit Remember/Edit/Forget controls for response preferences and personal facts. MAF receives bounded transient preferences/relevant facts and a read-only memory search tool. SQLite FTS5 retrieves keywords locally; no new model or hosted memory service is required. Memory edits/deletion and knowledge changes invalidate durable context, including snapshots saved by older in-flight runs.
- Validation: 133 API tests passed, with four opt-in live tests skipped. New cases use real SQLite, native MAF and HTTP host recreation. Production frontend build, persistence/migration renderer checks and existing guardrail renderer checks passed.
- Exact file/change/purpose mapping, API contracts and storage limitations: [CONVERSATIONS-AND-MEMORY.md](CONVERSATIONS-AND-MEMORY.md).
- Live verification with local `qwen3:8b` and a separate SQLite database restored a conversation-only codename and saved name preference across a real API process restart. Forget emptied the catalogue and the next same-session answer reported no saved preferred name. The smoke conversation was deleted and the isolated API stopped afterward.

### Automatic knowledge selection and personal-profile fallback

- Removed the Use knowledge composer checkbox and outgoing mode flag, including attachment requests. Legacy JSON/form flags are accepted but ignored so a greeting or user-supplied name cannot become a mandatory document verification request.
- Corrected instructions and tool descriptions: use the current conversation first, check saved memories and uploaded profiles/notes before declaring a personal fact unavailable, and prefer current user corrections over older notes. No additional classification model or eager retrieval on every message was introduced.
- A live regression exposed a second problem: successful profile retrieval still lost its answer when the local model omitted a filename citation. The chat presentation layer now appends actual retrieved filenames as Documents consulted and preserves that formatted answer in native history. This displays consulted sources rather than claiming independent factual verification.
- Validation: 140 default API tests passed, five opt-in live tests skipped; frontend production build and guardrail/persistence renderer checks passed. Added a real local-model/Qdrant profile-selection regression with conversation correction and greeting checks. Exact file/change/purpose mapping: [AUTOMATIC-KNOWLEDGE.md](AUTOMATIC-KNOWLEDGE.md).
