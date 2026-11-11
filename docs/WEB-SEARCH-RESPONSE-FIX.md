# Public search and response-quality fixes

The user reported blocked weather questions, empty answers and responses explaining unrelated escaped JSON. This follow-up removes the broad search restriction and fixes the model's evidence input.

## What changed and why

| Files | Behavior change | Purpose |
| --- | --- | --- |
| `ToolAuthorizationService.cs` | Removed the opt-in and private-conversation checks for public search. Outgoing queries still reject recognizable credentials/local file paths. | Ordinary public questions work in every conversation. |
| `Chat.tsx`, `App.tsx`, `api.ts` | Removed the Public web search checkbox, disabled state and new-conversation message. | Users can simply ask their question. |
| `ToolEvidenceFormatter.cs`, `GuardrailAgentRunner.cs` | Converted search/knowledge transport JSON into readable excerpts, filenames and URLs instead of serializing it inside another JSON string. | The model answers from evidence rather than explaining JSON or Unicode escapes. |
| `OllamaChatClient.cs`, `Program.cs` | Added a local provider adapter that sends plain native tool messages, disables thinking by default, and uses an 8192-token context. Original MAF call ids/history remain intact. | Avoid redundant SDK result wrappers, context clipping and thinking-only output. |
| `GuardrailChatClient.cs` | Added one recovery call for an empty final answer, with tools disabled. Tool-call responses do not trigger recovery. | Reduce empty answers without repeating side effects or creating a retry loop. |
| `PublicAnswerPresentation.cs`, chat middleware | Overly long weather answers get one tool-free formatting pass with a small JSON schema. The server renders the summary, up to three bullets, Celsius-first paired units and links selected by actual source number. If the model omits citations, real retrieved links are appended. | Keep weather replies readable and prevent invented citation URLs. This can add one local inference call, but no additional search. |
| `AgentInstructions.cs` | Explicitly asks the agent to search current public questions, answer the latest question in the user's language, use concise Markdown, label locations, prefer Celsius for weather and copy actual source URLs. | Avoid blanket internet refusals, unrelated previous topics and long weather reports. |
| `WebSearchTools.cs` | Broadened the tool description beyond framework documentation; expanded excerpts from 500 to 1400 characters; raised the provider timeout from 12 to 25 seconds; added one automatic retry for temporary failures; caller cancellation propagates. Status diagnostics expose the effective timeout, attempt limit and retry count. | Supply useful evidence for general questions, recover temporary failures within the same turn and identify which policy the running backend uses. |
| Tool evidence/context and chat middleware | A failed search without fresh sources returns its actual cause, such as a provider timeout, rather than a fabricated current answer or a blanket internet-access denial. | Distinguish provider availability from application permission. |
| `TwinSettings.cs`, `appsettings.json`, runner | Added local thinking/context settings and current date/time in the configured chat timezone. | Keep “today” grounded in the current date rather than model training knowledge. |

## Configuration

```json
"ChatModel": "qwen3:8b",
"EnableChatThinking": false,
"ChatContextLength": 8192,
"ChatTimeZone": "Asia/Beirut"
```

No new model or provider was added. Hosted chat selection remains explicit; the local adapter also applies when the existing hosted-to-local fallback is used. `EnableChatThinking=true` allows the original thinking behavior if desired. The local adapter uses the installed SDK's native request factory to set Ollama's [thinking option](https://docs.ollama.com/capabilities/thinking).

The old `allowWebSearch` request property is accepted for compatibility but does not gate search, including when older clients send `false`. The remaining session context classification is diagnostic only.

The relaxed policy does not claim to detect every piece of confidential text or encoded data. Keep private material out of public queries. The existing document review, approved project roots, recognizable-credential redaction, handoff approval and execution budgets are separate controls and remain in place.

## Validation

```powershell
dotnet test tests/DigitalTwin.Api.Tests --artifacts-path data/web-response-verification -p:UseAppHost=false
npm.cmd --prefix frontend run build
npm.cmd --prefix frontend run test:guardrails
```

The default suite passed 116 tests, with four opt-in live tests skipped. New checks cover automatic search after private evidence, older clients sending `allowWebSearch=false`, readable Arabic/Unicode excerpts, specific provider errors, one bounded empty-answer recovery, no repeated tool side effects, and the actual Ollama wire payload (`think=false`, context size and plain tool text). They also verify structured weather rendering, actual-source indices, corrected conversation history, source-link fallback and rejection of invented live facts after a search timeout. `WebSearchRetryTests.cs` adds 17 cases for temporary failure recovery, the two-attempt limit, immediate credential/quota errors, malformed provider responses, caller cancellation and recovered evidence reaching the native MAF agent within one user turn. These retry tests use an HTTP test handler and no real provider credits. Frontend build and renderer checks passed for the earlier UI changes; this retry follow-up changes no frontend files.

Live verification uses the existing local `qwen3:8b` and configured Exa provider through an isolated API on port 5098. It submits the exact reported weather question through `/api/agent/ask`, then a follow-up in the same session. Provider status and actual replies are checked, rather than inferring success from HTTP 200 alone. A real provider timeout observed during testing prompted the 25-second allowance and specific-error handling. No existing knowledge documents are changed. These checks verify the app's retrieval/answer flow and presentation; they do not independently establish the accuracy of every external forecast.

The final live run returned a summary with three weather bullets and actual retrieved links for `how is the weather in lebanon today?`. The same-session `and for Beirut?` follow-up returned a Celsius forecast with three bullets and its source link, even with the old opt-in flag set to false. Provider diagnostics showed two calls and `lastOutcome=ok`. The isolated smoke server was stopped afterward.

Restart the backend and refresh the frontend to load these changes. Standalone hosting from port 5000 also needs the existing frontend publish script; Vite development reads the updated components directly.

## Temporary provider failures

The reported first attempt failed at about 12 seconds, while the second returned Exa HTTP 200. That indicates a provider request failure rather than a denied search; the screenshot alone does not establish the exact exception. A 12-second failure may also come from an older backend process. The rebuilt backend reports its effective policy at `/api/tools/web-search/status`: `requestTimeoutSeconds=25` and `maxAttempts=2`.

Timeouts, connection failures and HTTP 408/500/502/503/504 receive one automatic retry after a 250 ms delay. The retry creates a fresh request inside the existing `SearchWeb` invocation, so it needs no extra user message or model tool selection. Authentication errors, quota/rate-limit errors, other permanent HTTP errors and invalid JSON return immediately. Cancellation stops the pending work. If both attempts fail, the final response reports that failure instead of promising an unperformed retry.

The tradeoff is at most one extra provider request, which can consume provider quota, and up to approximately 50.25 seconds of provider waiting when both attempts time out. The existing overall agent deadline still applies. `providerCalls` and `providerRetries` are cumulative since backend startup; `lastOutcome` reports the last completed search. The `tool.SearchWeb` trace records attempt count and timeout without recording queries or keys. A recovered first HTTP attempt can still appear red as a child span while the overall tool succeeds.

Post-retry live verification on the isolated API confirmed `requestTimeoutSeconds=25` and `maxAttempts=2`. The original weather question with the legacy opt-in flag set to false returned a Markdown summary and a retrieved source link; Exa diagnostics reported `providerCalls=1`, `providerRetries=0`, `lastOutcome=ok`. No transient failure happened in that live request; failure/recovery behavior is verified with the controlled HTTP tests described above. The isolated API was stopped afterward.
