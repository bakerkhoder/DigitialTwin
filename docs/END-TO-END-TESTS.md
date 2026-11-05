# DeveloperTwin v4 — Windows acceptance tests

Record result and screenshots against each case. Do not assume passing without running it.

## Build (release blocker)
- [ ] `dotnet --info` shows .NET SDK 9.
- [ ] `dotnet restore` succeeds; NuGet packages match pinned versions.
- [ ] `dotnet build` succeeds before importing projects.
- [ ] `npm install` and `npm run build` succeeds from `frontend`.
- [ ] `dotnet run --urls http://127.0.0.1:5000` serves React, not the fallback page.

## Project compilation regression
- [ ] Import the same `ProcureStudio` ZIP that caused the `McpServerResourceAttribute` compile errors in v3.
- [ ] Confirm imported source files are under `data/workspace/projects/...` outside the API .csproj directory.
- [ ] Run `dotnet build` again: it MUST NOT compile imported projects or show errors about their namespaces.
- [ ] If old `src/DigitalTwin.Api/workspace` contains `.cs`, confirm `<Compile Remove="workspace/**/*.cs"/>` excludes them.

## Large folder linking
- [ ] Add a specific folder parent (NOT the whole disk) to `Twin:AllowedProjectRoots`.
- [ ] Connect an existing absolute folder, verify tree appears and ID starts with `linked-`.
- [ ] Restart app, verify linked IDs survive in `data/workspace/connected-projects.json`.
- [ ] Try linking a folder outside roots: 400 and a clear security message.
- [ ] Confirm `bin`, `obj`, `node_modules` are ignored and no code is executed.

## Knowledge RAG
- [ ] With Qdrant stopped, `/api/knowledge/health` returns `qdrant:false`, and UI gives `docker compose up -d` hint.
- [ ] Start Docker Qdrant; open `http://localhost:6333/dashboard`.
- [ ] Start Ollama and verify `nomic-embed-text:latest` installed.
- [ ] Upload `policies.txt` and `good-to-know.txt`; both return chunk counts.
- [ ] Query a phrase present in `policies.txt`; result includes source filename and similar passage.
- [ ] Ask a grounded question in chat with Use knowledge enabled, verify attribution.
- [ ] Upload a scanned PDF: verify it reports missing extractable text or uses vision only for explicitly supported image files (no OCR guarantee).

## Chat and multimodal
- [ ] Set `OPENROUTER_API_KEY`, use a currently active free tool-capable model.
- [ ] Chat request shows animated indicator until reply/error; can Stop and retry.
- [ ] Attach 2 text files in one chat request, inspect returned analysis.
- [ ] Attach a screenshot to chat, verify configured vision model works (free vision routing may fail on some providers).
- [ ] Turn OpenRouter off: local qwen3:8b chat remains usable; no local vision model expected.
- [ ] Public search tool uses Tavily/Brave when configured and has no secret code in query.

## Export and approval
- [ ] Ask agent to summarize a small test project as a Markdown onboarding report.
- [ ] Export MD, PDF, PPTX, SVG and PNG, open each in appropriate app, confirm content is readable.
- [ ] Check a long handoff PDF paginates and the PPTX opens in Microsoft PowerPoint without repair warnings.
- [ ] Ask for a proposed handoff, open Approvals, review title/body, approve PDF download.
- [ ] Reject a different proposal, verify it disappears without generating a file.
- [ ] Confirm there is no Azure DevOps publisher or live remote action.

## Security checks
- [ ] ZIP archive with `../` segments is rejected.
- [ ] ZIP with `bin/obj` folders has those folders skipped.
- [ ] Reject `.env`/private key source reading with `ReadFile`.
- [ ] Cross-origin mutation to localhost API is denied; do not treat this as production authentication.
- [ ] Confirm `data/` and `.env` never enter Git.
