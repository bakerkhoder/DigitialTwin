# DeveloperTwin: MAF Tools, Search Verification and RAG Demo

**Scope**: one hosted Microsoft Agent Framework agent, five registered function tools, read-only project inspection, Qdrant knowledge retrieval and opt-in public web search. The sample knowledge documents are **fictional training data**; they do not implement access control.

## What the agent can call (real code contracts)

All five are registered in `src/DigitalTwin.Api/Program.cs` with `.WithAITool(AIFunctionFactory.Create(...))`. Their C# method signatures generate the JSON parameter schema exposed to the model. `[Description]` provides method and argument semantics. Required parameters have no default; optional arguments have defaults. **These aren't HTTP endpoints.** MAF invokes these methods after the model selects a tool and generates arguments.

| Tool | Parameters exposed to agent | Use when | Do not use when |
|---|---|---|---|
| `ListProject` | `relativePath: string = "."` — e.g. `projects/project-abc`, `linked-abc` | Structure of an already imported/connected project | Answering a policy document question or requesting current docs |
| `ReadFile` | `relativePath: string` (required) — e.g. `linked-abc/src/Program.cs` | Inspecting a specific small source/config file found by `ListProject` | Arbitrary filesystem reads, secrets, or executing code |
| `SearchKnowledge` | `query: string` (required, 1–500 chars); `maxResults: int = 5` (1–5) | Internal uploaded policies, business requirements, runbooks and handoff notes | Current public release versions and public docs |
| `SearchWeb` | `query: string` (required, 3–220 chars); `maxResults: int = 3` (1–5); `freshness: string = "any"` (`any`, `day`, `week`, `month`) | Official current PUBLIC docs, recent vulnerabilities, package releases | Private content, project paths, internal rules already in knowledge |
| `ProposeHandoff` | `title: string` (required, <=120 chars); `content: string` (required Markdown, <=35000 chars) | User explicitly requests a report proposal for review | Ordinary chat, direct report download, or granting project permissions |

### JSON-like calls the MODEL might choose

```
ListProject({ "relativePath": "linked-abc" })
ReadFile({ "relativePath": "linked-abc/src/Program.cs" })
SearchKnowledge({ "query": "Which officer approves an ERP request for $62,000?", "maxResults": 3 })
SearchWeb({ "query": "site:learn.microsoft.com agent framework function tools C#", "maxResults": 3, "freshness": "any" })
ProposeHandoff({ "title": "ProcureStudio handoff", "content": "# Purpose\n..." })
```

Tool call output is data for the model, not authority. The business services validate paths, lengths, permissions, and arguments. MAF and the model select tools *probabilistically*; good descriptions guide selection, but do not guarantee it. A constrained production scenario can force tool availability/selection using controlled orchestration or explicit application routing.

## Configure public web search

Choose ONE provider. If both keys exist, Tavily is preferred. Never put keys in React, appsettings.json, documents or Git. Set the key in the **same PowerShell process that launches the .NET backend**:

```powershell
cd E:\MafTraining\developer-digital-twin\src\DigitalTwin.Api
$secure = Read-Host 'Tavily API key' -AsSecureString
$env:TAVILY_API_KEY = [System.Net.NetworkCredential]::new('', $secure).Password
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet watch run --no-launch-profile --urls http://127.0.0.1:5000
```

For Brave instead, use `BRAVE_SEARCH_API_KEY`. The search service implements the documented Tavily `POST /search` with Bearer authorization, and Brave `GET /res/v1/web/search` with `X-Subscription-Token`. It maps `any/day/week/month` into provider-specific filters, returns compact titles, snippets and HTTPS URLs, limits provider waits to 12 seconds, and returns structured errors for quotas or authentication rather than crashing a chat turn.

## Independently verify provider vs agent behavior

Open a second terminal in the repository root:

```powershell
.\scripts\test-web-search.ps1
# After provider passes, optionally test agent's model-driven tool selection:
.\scripts\test-web-search.ps1 -ProbeAgent
```

The script checks `/api/tools/web-search/status`, calls `/api/dev/tools/search-web` independently, and verifies it returned HTTPS results. This DEV-only endpoint only runs from loopback. It consumes provider quota; remove/disable it in deployed systems. With `-ProbeAgent` it asks MAF an official documentation question and compares the search tool call counter before and after.

Manual inspection:

- `GET /api/status` exposes `webSearch: tavily|brave|off` — configuration, not actual health.
- `GET /api/tools/web-search/status` exposes `configured`, `providerCalls`, `lastOutcome` — actual attempts/last outcome (process-local; resets on restart).
- If direct provider test passes but agent call count doesn't increase, inspect `IChatClient` model function-calling support and the instruction/parameter descriptions. A model answering from memory does **not** prove tool calling.
- If status is `off`, an OpenRouter API key alone is not enough; search needs its own Tavily or Brave key.
- Returning source URLs is not the same as verifying entire pages; snippets alone may omit important details.

## Demo knowledge files

Located in `demo-knowledge/` (upload them with the Knowledge Library's multi-file upload). They are **examples created for this project**, not real confidential data.

1. `01-procurement-approval-policy.txt`: threshold routing, evidence and audit rules.
2. `02-erp-purchase-request-requirements.txt`: business requirements and open questions.
3. `03-procurestudio-architecture-notes.txt`: fictional React/.NET/PostgreSQL/RabbitMQ design.
4. `04-engineering-incident-playbook.txt`: troubleshooting Qdrant, compilation and providers.
5. `05-data-classification-and-ai-use-policy.txt`: data classification and proposed approval policy.
6. `06-api-contracts-and-acceptance-tests.txt`: sample routes, error codes and tests.
7. `07-new-developer-onboarding.txt`: three-day handoff checklist.

### Sequence for your assessment demo

1. Ensure Qdrant and Ollama embeddings are healthy (`GET /api/knowledge/health`). Upload all seven small `.txt` files under **Knowledge library**.
2. Ask: **"According to the uploaded procurement policy, who approves a $62,000 PO and how many vendor quotes are needed? Cite the source."** Expected: `SearchKnowledge` and reference `01-procurement-approval-policy.txt`; CFO plus at least three quotes.
3. Ask: **"What are the open questions in the purchase-request requirements?"** Expected: `SearchKnowledge`, reference `02-erp-purchase-request-requirements.txt`.
4. Ask: **"Summarize our data classification policy. Can confidential ERP files be sent to OpenRouter?"** Expected: `SearchKnowledge`, reference `05-data-classification-and-ai-use-policy.txt`. This is a *policy answer*, not enforcement.
5. Ask: **"Inspect the connected ProcureStudio repository and identify the main backend modules and dependencies."** Expected: `ListProject` then selective `ReadFile` (requires actual imported/linked codebase).
6. Ask: **"Look up the current official Microsoft Agent Framework C# function-tool documentation and cite two source URLs."** Expected: `SearchWeb` (not knowledge).
7. Ask: **"Compare our purchase-approval documentation with the CURRENT MAF approach to human-in-the-loop approvals; distinguish our internal rules from the framework's external implementation."** Expected: both `SearchKnowledge` AND `SearchWeb`.
8. Ask: **"Prepare a concise new-developer handoff for ProcureStudio and submit it for human review."** Expected: knowledge/project tools as needed, followed by `ProposeHandoff`.
9. Ask: **"Explain the purpose of a primary key in a relational database."** Expected: direct model answer, normally without tools.

### Important architecture boundary

`05-data-classification-and-ai-use-policy.txt` is indexed as untrusted **reference knowledge**. Uploading it does not configure `AllowedProjectRoots`, grant/revoke permissions, enforce policy-based approvals or block an unsafe API call. Paths under configured roots connect directly; the Approval Center grants explicit access to a specific outside folder and is separate from policy-based authorization. Implement the proposed policy-based project-access gate in a deterministic `PolicyService` and native MAF human-in-the-loop workflow when building that next milestone.

## Relevant Microsoft Learn

- [Microsoft Agent Framework — Add tools](https://learn.microsoft.com/en-us/agent-framework/get-started/add-tools)
- [Using function tools](https://learn.microsoft.com/en-us/agent-framework/agents/tools/function-tools)
- [Agent Framework — tools overview](https://learn.microsoft.com/en-us/agent-framework/agents/tools/)
- [Human-in-the-loop](https://learn.microsoft.com/en-us/agent-framework/workflows/human-in-the-loop)

Provider references:
- [Tavily search API](https://docs.tavily.com/documentation/api-reference/endpoint/search)
- [Brave Web Search API](https://api-dashboard.search.brave.com/app/documentation/web-search)
