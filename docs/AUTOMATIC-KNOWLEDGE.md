# Automatic knowledge selection and personal questions

The chat composer no longer has a **Use knowledge** checkbox. The agent chooses conversation context, saved memory, uploaded knowledge and public search according to the question. Personal information supplied in an uploaded profile is available through the same knowledge retrieval tool used for requirements and policies.

## Cause of the reported behavior

The previous instructions directed personal questions to `SearchMemories` and told the agent to stop when no saved memory matched. That incorrectly treated the separate memory table as the only source of personal information, even when an uploaded profile contained the answer.

The checkbox also added a mandatory `SearchKnowledge` requirement to every message, including greetings and a direct statement of the user's name. When the model used conversation evidence instead, output validation replaced the answer with “knowledge base was not searched” and discarded its context. A live regression check also reproduced a separate citation-format failure: retrieval succeeded, but an omitted filename caused another replacement message.

## Exact changes and purpose

| Files | Change | Purpose |
|---|---|---|
| `frontend/src/components/Chat.tsx` | Removed checkbox state/control and its submit argument; the research shortcut now only fills the draft. | Make knowledge retrieval automatic rather than a manual mode. |
| `frontend/src/App.tsx`, `styles.css` | Removed outgoing `useKnowledge` JSON/form values, handler arguments and obsolete checkbox styling. | Remove the mode across both ordinary messages and attachments. |
| `Endpoints/ChatEndpoints.cs` | Accepts the old JSON `UseKnowledge` field for compatibility, but ignores it and old multipart values; stops adding a required-search prefix and document-only validation flag. | Older browser clients sending true must not block conversation facts, greetings or attachments. |
| `Agents/AgentInstructions.cs` | Prioritizes current conversation/attachment facts and corrections; directs unanswered personal questions to saved memory and uploaded profiles/CVs/notes; asks for actual filenames. | Answer identity/background questions from all available authorized sources, without inventing information or inferring identity from unrelated people. |
| `Services/MemoryTools.cs` | Clarifies that an empty memory result should lead to uploaded-knowledge search when the conversation lacks the fact. | Avoid a premature “I cannot access personal information” response or directing every name question to settings. |
| `AI/Retrieval/KnowledgeRetrievalService.cs` | Describes knowledge search as appropriate for both documents and personal profile questions. | Help the local model select the existing tool for names, roles and background as well as business/technical questions. |
| `Agents/KnowledgeAnswerPresentation.cs`, `AI/Guardrails/GuardrailChatClient.cs`, `OutputValidationService.cs` | Adds actual retrieved filenames as **Documents consulted** when the model omits them, instead of replacing the answer with a citation error. The formatted response enters native MAF history. An unavailable web request does not overwrite an answer that has retrieved document evidence. | Preserve useful answers and their context while exposing the real documents the agent consulted. |
| `AutomaticKnowledgeTests.cs`, `GuardrailAgentTests.cs`, shared HTTP test host | Tests conversation names with old true/false flags, greetings, multipart compatibility, empty-memory-to-profile retrieval, literal filename formatting and corrected citation history. | Reproduce the reported failures through real HTTP/MAF paths. |
| `LivePersonalKnowledgeTests.cs`, shared live Qdrant fixture | Exercises actual local inference, embeddings, reranking and temporary profile retrieval, conversation corrections and greetings. | Verify the model's real source selection, beyond scripted tool tests. |
| `frontend/scripts/verify-guardrails.mjs` | Checks that neither public-web nor knowledge selection needs a checkbox. | Prevent the removed controls from returning. |

## Source behavior

- A name or correction explicitly supplied in the conversation is usable immediately and takes precedence over an older document or saved memory.
- Saved preferences remain transient MAF context. `SearchMemories` retrieves other explicitly saved facts. An empty result there does not establish that uploaded knowledge is empty.
- When a personal question has no answer in the conversation/memory, the agent searches uploaded profiles, CVs or personal notes. When answering from documents, it cites actual source filenames.
- Requirements, policies and uploaded-document questions use the existing knowledge search automatically. Greetings and ordinary conversation do not require a retrieval call.
- Public questions still use `SearchWeb` when current information is needed. The user's identity and private personal details belong in authorized local sources rather than public web queries.

No eager full-knowledge search or separate classification model was added to every request. Tool choice remains with the MAF agent, using the existing local model. Source annotations add no inference call. A filename annotation describes documents actually consulted; it is not independent verification of every generated claim.

Existing retrieval filters still determine which documents are available. A document must be active and satisfy its review requirements. This change does not make missing/deprecated/pending documents searchable, manufacture a profile, or silently create a permanent memory from conversation text. The model can use an introduced name within the conversation without saving it as cross-conversation memory.

## Validation

```powershell
dotnet test tests/DigitalTwin.Api.Tests --artifacts-path data/memory-verification -p:UseAppHost=false --no-restore --nologo --verbosity minimal
npm.cmd --prefix frontend run build
npm.cmd --prefix frontend run test:guardrails
npm.cmd --prefix frontend run test:persistence

# Opt-in local integration; creates/deletes only its own unique temporary collection.
$env:TWIN_RUN_LIVE_QDRANT_TESTS = '1'
dotnet test tests/DigitalTwin.Api.Tests --artifacts-path data/memory-verification -p:UseAppHost=false --no-build --no-restore --filter FullyQualifiedName~LivePersonalKnowledgeTests
```

The default suite passed 140 tests, with five opt-in live tests skipped. Frontend production build and both renderer/migration checks passed. The live test uses an isolated profile with a synthetic name, an empty personal-memory table, the real `qwen3:8b`, local embeddings and reranking. It verifies name/background lookup with source filenames, a later user correction and a greeting without unnecessary knowledge retrieval.

Restart the backend and refresh the frontend. Existing clients may still send the obsolete flag; the updated backend ignores it. Standalone API hosting also needs the existing frontend `build:api` step to publish the updated composer.
