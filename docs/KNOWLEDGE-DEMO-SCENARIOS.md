# DeveloperTwin Demo Knowledge — Seven Fictional TXT Files

All documents belong to **Northstar Demo Industries / ProcureStudio**, fictional projects created for training. They are not real ERP or company policies, and **uploading a policy TXT does not enforce authorization**. Upload all seven through Knowledge Library, check successful ingestion, and then ask the questions below.

## Why seven documents instead of one big file?

This is a RAG demonstration. A single answer can require material from multiple independent sources. Developers can show that Ollama `nomic-embed-text` embeds document chunks into vectors, Qdrant stores/searches those vectors, and MAF's `SearchKnowledge(query,maxResults)` returns relevant snippets. An answer should cite the matching source filename. It should not pretend indexed policy text is executable configuration.

| Filename | Exact business facts / test questions | Expected answer |
|---|---|---|
| `01-procurement-approval-policy.txt` | Who approves a PO for $62,000, and how many vendor quotes are required? | CFO approval; at least 3 independent vendor quotes (amount above $50,000 and $25,000). |
| `02-erp-purchase-request-requirements.txt` | Can employee alter a submitted PR? What requirements remain unresolved? | Changes frozen after submission unless rejected/explicit change request; rejected override, multi-currency rate selection, serial vs parallel approval remain open. |
| `03-procurestudio-architecture-notes.txt` | Why use the outbox pattern with RabbitMQ? | To avoid DB commit succeeding while message publish fails; outbox written transactionally and delivered later. |
| `04-engineering-incident-playbook.txt` | How do I resolve Qdrant port 6333 connection refused? | Inspect Docker/port, start or reuse existing container, validate health and ingestion. |
| `05-data-classification-and-ai-use-policy.txt` | Can confidential ERP source be sent to OpenRouter? | Not without separate authorized agreement/data-flow approval; keep local by default. This answer does not enforce access. |
| `06-api-contracts-and-acceptance-tests.txt` | What should happen if a $38,000 request contains only two quotes? | 422, unless approved single-source exception. |
| `07-new-developer-onboarding.txt` | Plan my first three days on ProcureStudio. | Day 1 business/policies, Day 2 code/architecture, Day 3 create/test request workflows. |

## Three scripted live demos

### Demo A — Internal policy (SearchKnowledge only)

> "According to our uploaded policy DEMO-POL-001, who must approve PO-1003 for $62,000 and how many quotes are required? Cite the filename and policy conditions."

Expected: CFO + three independent vendor quotes; citation of `01-procurement-approval-policy.txt`. In Aspire, locate `agent.run` and `tool.SearchKnowledge`. May include retrieval/embedding HTTP spans. If the agent didn't use the tool, check `developertwin.tool.calls` metrics and model tool-calling support.

### Demo B — Live external research (SearchWeb only)

> "Find the CURRENT official Microsoft Agent Framework .NET documentation for function tools and show two source URLs, without referring to our internal ERP documents."

Expected: Tavily/Brave tool usage and external HTTPS URLs. Requires Tavily/Brave key. OpenRouter alone does not provide this configured search tool; it provides the inference model. Use `/api/tools/web-search/status` and the development-only smoke test if the tool was skipped.

### Demo C — Combining sources (both tools)

> "Compare our procurement approval thresholds from DEMO-POL-001 against the CURRENT Microsoft Agent Framework human-in-the-loop workflow API. Give an implementation outline; clearly distinguish internal policy facts from official Microsoft implementation details."

Expected: `SearchKnowledge` for financial thresholds and `SearchWeb` for external APIs. Distinguish business rules, proposed code, and current official docs. In Aspire find one logical agent trace with evidence of both tools if the model selected them. A forced-test workflow may be needed if the free chat model skips one.

## Bonus: Developer handoff

> "Use the ProcureStudio documents to draft a handoff for a new full-stack engineer covering system purpose, React/.NET/PostgreSQL/RabbitMQ stack, modules, main REST routes, approvals, outbox, tests and known questions. Cite filenames."

Expected: cross-document retrieval from architecture, API, requirements, onboarding and policy TXT files. Use PDF/PPTX export only when your exporters have passed Unicode/template tests; approvals are not required simply to download a report.

## How to explain the RAG pipeline in an interview

1. User uploads TXT docs in React Knowledge Library.
2. ASP.NET extracts text, chunks it with overlap, and sends chunks to local `nomic-embed-text` via Ollama.
3. Qdrant stores embedding vectors with source filename and chunk payload.
4. An internal-policy question makes the MAF agent call `SearchKnowledge` when the model chooses the tool.
5. Backend embeds the question with the **same** model, queries Qdrant for nearest vectors, and returns relevant passages.
6. MAF passes tool results into the model context to produce a grounded answer.
7. OpenTelemetry shows the server span, agent span, tool span, embedding call and Qdrant HTTP call, without exposing raw document text as span tags.

**Caveat:** The current ingestion implementation uses basic fixed-length text chunks and does not enforce a policy from uploaded TXT documents. It is adequate for a training RAG demo, not a complete enterprise retrieval/authorization engine.
