# Fictional Demo Knowledge for DeveloperTwin

Seven safe-to-share `.txt` files for the Knowledge Library's batch-ingestion screen. **All names, figures, systems, limits and policies are invented for training**. Do not treat these as real company approvals or regulatory advice.

Quick demonstration:
1. Start Qdrant (`docker compose up -d`) and ensure the existing `nomic-embed-text:latest` model is available in Ollama.
2. Visit Knowledge Library and upload all seven `.txt` files. You should see nonzero chunks stored for each file.
3. In Assistant, ask: "According to our procurement policy, who approves $62,000, and what quote evidence is needed? Cite the file."
4. Ask: "What must a new developer know about ProcureStudio, and which test cases should they run?"
5. For the web-search demo, separately configure Tavily or Brave as documented in `docs/TOOL-CATALOG-AND-DEMO.md`.

**Key distinction**: Qdrant retrieves uploaded facts; only deterministic C# services can enforce access control. The policy file is reference data, not security configuration.

For Phase 3, upload `08-adversarial-instructions.txt` separately. It should show **Review required** and stay out of search until reviewed. Its instructions remain untrusted even after review; see [the guardrail demo](../docs/GUARDRAILS-AND-POISONING-PREVENTION.md). Security-training documents can trigger this heuristic review flow too.
