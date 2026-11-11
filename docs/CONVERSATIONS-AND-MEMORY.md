# Phase 4: persistent conversations and explicit personal memory

Conversations now survive backend restarts. SQLite stores the visible transcript and the native MAF session snapshot together after a successful turn. A separate Saved memory page lets the user explicitly remember, edit or forget response preferences and personal facts. The existing local chat model is reused; memory retrieval needs no hosted API, embedding model or Qdrant collection.

## What changed and why

| Files | Change | Purpose |
|---|---|---|
| `Persistence/LocalStateDatabase.cs`, API `.csproj` | Added pinned `Microsoft.Data.Sqlite` 9.0.9, versioned transactional schema migrations, WAL and foreign keys. Connections and write transactions are short. | Keep durable state locally without adding an ORM or another service. |
| `Persistence/ConversationStore.cs` | Stores conversation metadata, messages, attachment names, private-context diagnostics and opaque MAF snapshots. A completed user/assistant pair and snapshot commit in one transaction. | Restore both the UI and actual agent context; avoid incomplete tool turns after failures. |
| `Agents/SessionRegistry.cs` | Rehydrates snapshots through MAF, caches sessions and synchronizes inference/deletion per conversation. Persisted deletion markers prevent stale requests/imports from recreating deleted IDs. | Resume context correctly after restart and keep deletion reliable. |
| `AI/Guardrails/GuardrailAgentRunner.cs` | Saves after output validation. A failed or cancelled turn evicts its mutated cached session; the next turn can restore the previous successful snapshot. Output that requires context removal saves no reusable snapshot. | Prevent failed, rejected or redacted hidden model output from entering future durable context. |
| `Endpoints/ChatEndpoints.cs` | Adds list/detail/import/clear endpoints and extends existing deletion to durable records. Passes attachment names into transcript saving; deleted IDs return HTTP 410 on a late chat request. | Give the frontend authoritative history and meaningful deletion behavior. |
| `Persistence/MemoryStore.cs`, `Services/MemoryTools.cs`, `Endpoints/MemoryEndpoints.cs` | Adds explicit memory CRUD, Unicode SQLite FTS5 search, bounded keyword retrieval and a read-only `SearchMemories` tool. | Let the user control what becomes long-term personal memory and retrieve relevant facts locally. |
| `Agents/SavedMemoryContextProvider.cs` | Adds up to three recent response preferences and two matching facts through transient MAF context instructions. Trace metadata records match count only. | Apply preferences automatically without copying the memory catalogue into each saved conversation. |
| `Agents/AgentInstructions.cs`, `AI/Guardrails/ToolAuthorizationService.cs` | Registers read-only memory search and explains memory references and explicit UI writes. | Keep ordinary chat from silently creating memories or claiming it saved/forgot something it did not change. |
| `Program.cs`, `Models/TwinSettings.cs`, `appsettings.json` | Registers the database/stores/provider/tools/routes; adds `Twin:StateDatabase`. | Keep construction at the host boundary and database location configurable. |
| `frontend/src/lib/conversations.ts`, `useConversationHistory.ts`, `api.ts` | Validates/imports previous browser history once; loads server summaries and selected messages; defines persistence DTOs. | Preserve existing messages, avoid browser storage limits and load only the selected transcript. |
| `frontend/src/App.tsx`, `components/Chat.tsx` | Uses API history, handles loading, orders recent conversations by activity, and reports deletion failures. Blocks submission while the selected history is loading. | Avoid displaying missing context or falsely claiming failed deletions succeeded. |
| `frontend/src/components/Memory.tsx`, `styles.css` | Adds Saved memory with preference/fact selection and Remember/Edit/Forget controls. | Give the user visible control over retained personal information. |
| New persistence tests, shared test harnesses, frontend verification script/package script | Tests real SQLite files, native MAF serialization, host recreation, deletion races, memory retrieval/forgetting, migration and UI rendering. | Verify restart behavior using the actual storage/framework rather than an in-memory substitute. |

## Storage and lifecycle

The default file is `data/state/developertwin.db`, resolved relative to the repository root. Configure an absolute path or repository-relative path with:

```json
"Twin": {
  "StateDatabase": "./data/state/developertwin.db"
}
```

The directory is created at startup and is already covered by the repository's `data/` ignore rule. Migrations use SQLite `user_version`; version 1 creates conversation/base-memory tables and version 2 adds memory kinds and the search index. A newer unsupported schema fails startup explicitly.

Snapshots use the installed MAF version's `SerializeSessionAsync` and `DeserializeSessionAsync`, preserving native tool messages and call IDs. See [Microsoft's session lifecycle documentation](https://learn.microsoft.com/en-us/agent-framework/concepts/agents/conversations/session). Database work uses short synchronous operations because [Microsoft.Data.Sqlite does not provide asynchronous SQLite I/O](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async); no transaction stays open during inference.

The database contains conversation text and serialized context, potentially including extracted attachment text and retrieved project/document excerpts. It is local, unencrypted application data. Raw temporary attachment files are not copied into the database. Deleting a conversation removes its transcript and snapshot; only its identifier remains as a deletion marker. This is logical application deletion rather than secure disk erasure.

Failed/cancelled turns are displayed as temporary errors by the current UI, but only successful turns become durable conversation messages. A browser reload therefore returns to the last completed stored turn.

Knowledge changes increment a persistent context generation and clear snapshots. Memory edits and forgetting do the same atomically with the memory mutation. An in-flight run holding older evidence can finish its visible reply, but its snapshot cannot restore stale context afterward. Existing visible transcripts remain readable; the next agent turn starts fresh after these invalidations.

The application continues to assume one localhost user and one API process. These routes share the existing localhost-origin policy; production accounts and per-user/tenant storage isolation remain separate work. Per-conversation locks are process-local.

## Existing browser history

On the first updated frontend load, valid UUID-keyed conversations from the previous `developertwin-v4-chat-demo` browser key are imported before server history loads. Import is transactional and idempotent, skips existing or deleted IDs, retains Unicode/attachment names/error markers and creates **display history only**. Old browser records never contained native MAF state, so their earlier hidden tool context cannot be recovered. New turns gain durable native snapshots normally.

The frontend removes its previous browser-history key only after import and a successful server listing. An import failure keeps the old browser data and displays a retryable error. Browser storage is then used only for the active conversation identifier and migration marker.

## Using Saved memory

1. Open **Saved memory** in the sidebar.
2. Select **Response preference** for answer style, language or units, or **Personal fact** for information to retrieve when relevant.
3. Enter the content and select **Remember**. The latest three preferences are supplied automatically; matching facts use local keyword search, capped at two per request.
4. Use **Edit** to correct an entry or **Forget** to delete it. Forgetting also removes the search-index entry and invalidates older agent context that may contain it.

The agent can use `SearchMemories` for additional personal questions, returning at most five entries; an empty query lists recent entries. Keyword search can miss paraphrases; this phase does not introduce semantic embeddings for personal memory. Memories remain reference data and cannot grant tool permissions. User messages and model answers are never automatically extracted into the memory table.

Deleting conversation history and forgetting personal memory are separate user actions. Forgetting does not rewrite previous visible chat messages that mention the same fact.

## API

| Method/path | Purpose |
|---|---|
| `GET /api/agent/sessions` | List metadata, newest activity first; never exposes native snapshots. |
| `GET /api/agent/sessions/{id}` | Load a visible transcript. |
| `POST /api/agent/sessions/import` | Import up to 30 previous browser conversations; never imports tool/native state. |
| `DELETE /api/agent/sessions/{id}` | Delete one transcript and its native context under its run lock. |
| `DELETE /api/agent/sessions` | Clear saved and currently cached conversations; retain personal memories. |
| `GET /api/memories` | List saved personal memories. |
| `POST /api/memories` | Remember `{ "content": "Use Celsius", "kind": "preference" }`. |
| `PUT /api/memories/{id}` | Edit existing content/kind. |
| `DELETE /api/memories/{id}` | Forget a memory and invalidate older agent context. |

Memory content is limited to 1,000 characters and uses the existing credential screening. Query text and CRUD values are parameterized. Full-text query operators are parsed as keyword text rather than accepted as executable FTS syntax.

## Verification

```powershell
dotnet test tests/DigitalTwin.Api.Tests --artifacts-path data/memory-verification -p:UseAppHost=false --no-restore --nologo --verbosity minimal
npm.cmd --prefix frontend run build
npm.cmd --prefix frontend run test:persistence
npm.cmd --prefix frontend run test:guardrails
```

The API suite passed 133 tests with four opt-in live tests skipped. The new tests use isolated real SQLite files and scripted models, including host restart through real HTTP routes. Frontend production build and both renderer/migration checks passed. Restart the backend and refresh Vite; standalone hosting also requires the existing frontend `build:api` step to serve the updated UI.

A live smoke check used the installed `qwen3:8b`, an isolated database under `data/memory-verification/smoke/` and API port 5098. It saved the conversation-only codename `Cedar-Phase4` and an explicit `Cedar Tester` address preference, stopped the API process, then started a new process against that database. The restored transcript had two messages; the follow-up correctly returned both the codename and saved name. After Forget, `/api/memories` returned `[]`, and a same-session memory question reported no saved preferred name instead of repeating the deleted value. The smoke conversation was deleted and the isolated API stopped afterward. No user knowledge documents were changed.
