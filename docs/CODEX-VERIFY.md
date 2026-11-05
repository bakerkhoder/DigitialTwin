# Codex handoff — verify DeveloperTwin v4 on Windows

Run this task in the freshly extracted `developer-digital-twin` repository. Do not implement unrelated features or reintroduce the Azure DevOps publisher.

> Build and validate a .NET 9 / React + Vite application using Microsoft Agent Framework. Start by running `scripts/verify-v4.ps1` and address only real compiler/dependency errors. Keep the hosted `builder.AddAIAgent("DeveloperTwin")` registration, typed tools via `AIFunctionFactory.Create`, and keyed `AIAgent` injection. Preserve OpenRouter hosted-first and the installed local Ollama fallback. Do not download any new models. Verify `dotnet run`, Qdrant health, local and ZIP project inspection, and report export in PDF/PPTX/SVG/PNG; explicitly open each file and check integrity, not just HTTP 200. Improve error handling only where required to pass checks. Keep `data/workspace` outside the API compilation directory and `Compile Remove="workspace/**/*.cs"` intact. Do not restore Azure DevOps ticket publishing. Update `docs/ISSUES-AND-DECISIONS.md` with each change, exact command, and result. Run the tests in `docs/END-TO-END-TESTS.md` and mark any blocked tests honestly.

## Important debugging order

1. `dotnet restore` / `dotnet build` — report exact first compiler error before editing.
2. `npm install` / `npm run build` — fix TypeScript typing or package compatibility.
3. `docker compose up -d`; check Qdrant REST and local Ollama models.
4. Run API `http://127.0.0.1:5000` and inspect React.
5. Test imported ZIP followed by fresh `dotnet build` (critical regression).
6. Connect one **allowlisted** real Windows folder (no source copying).
7. Embed two sample text files and search.
8. Test chat and screenshots with configured OpenRouter key.
9. Export Markdown, PDF, PowerPoint, SVG and PNG; inspect outputs in real programs.
10. Test manual and agent-proposed handoff approval.

## Don't change silently

- Any new cloud provider or model must be configurable and respect privacy boundaries.
- No arbitrary shell command execution from tool calls.
- No local-project path access without explicit `AllowedProjectRoots`.
- No model-generated code should be compiled as part of DeveloperTwin itself.
- Keep frontend component files and backend service files distinct.
