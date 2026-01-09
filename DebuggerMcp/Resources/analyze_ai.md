# `analyze(kind="ai")` (AI Crash Analysis) — How It Works

This document explains how **AI Crash Analysis** works in the Debugger MCP Server when you run `analyze(kind="ai")` (or `analyze ai` in the CLI). It covers the analysis pipeline, the tools the model can call, the “working memory” features (checkpoints / evidence ledger / hypotheses), and how the system converges on a root cause.

> This is **server-driven** AI analysis via MCP sampling: the server orchestrates an iterative investigation loop, and the connected MCP client provides the LLM.

---

## Quick Orientation

### What `analyze(kind="ai")` is

`analyze(kind="ai")` is an **iterative, tool-driven root cause investigation**:

1. The server generates the canonical crash report JSON (the same foundation used by `analyze(kind="crash")`).
2. The server starts an MCP sampling loop (`sampling/createMessage`) where the LLM can call tools to fetch more evidence.
3. The server maintains a stable working memory (evidence ledger + hypotheses + checkpoints) to reduce loops and variance.
4. The result is returned as the canonical crash report JSON enriched with `analysis.aiAnalysis`.
5. Two additional bounded passes polish the report:
   - rewrite `analysis.summary.description` / `analysis.summary.recommendations`
   - generate a thread narrative (`analysis.threads.summary.description` + `analysis.aiAnalysis.threadNarrative`)

### What `analyze(kind="ai")` is not

- It is not a chat copilot (that’s `llmagent` in the CLI).
- It is not “freeform reasoning” without evidence: the system is designed to **ground conclusions in tool outputs**.

---

## Inputs and Outputs

### Inputs

- An **open dump** in an existing session (`dump(action="open")` must have been run).
- Optional run knobs:
  - `maxIterations`, `maxTokens` (passed via the MCP tool call)
  - `includeWatches`, `includeSecurity`
  - `refreshCache` to ignore cached AI analysis
  - Optional cache key fields (`llmProvider`, `llmModel`, `llmReasoningEffort`) to keep per-model caches separate

### Outputs (where to look in the report)

`analyze(kind="ai")` returns the **canonical JSON report** with these key additions/overrides:

- `analysis.aiAnalysis.rootCause`
- `analysis.aiAnalysis.confidence`
- `analysis.aiAnalysis.reasoning`
- `analysis.aiAnalysis.evidence` (human-readable list)
- `analysis.aiAnalysis.evidenceLedger` (structured evidence IDs: `E1`, `E2`, …)
- `analysis.aiAnalysis.hypotheses` (hypothesis IDs: `H1`, `H2`, …)
- `analysis.aiAnalysis.judge` (internal “judge” selection of the best hypothesis)
- `analysis.aiAnalysis.summary` (the summary rewrite pass output; also applied to `analysis.summary.*` when successful)
- `analysis.aiAnalysis.threadNarrative` (thread narrative pass output; also applied to `analysis.threads.summary.description` when successful)

---

## The Pipeline (High-Level)

### Phase 0 — Initial crash report (server)

The server first generates a structured crash report (JSON) that becomes the **source of truth** for subsequent `report_get(path=...)` calls. This report includes:

- summary / exception / stack
- runtime + platform info
- modules / assemblies (as available)
- threads + key thread summaries
- memory / synchronization / async / security (depending on options)

### Phase 1 — AI investigation loop (sampling + tools)

The server then begins the main AI analysis pass:

- The LLM receives an initial prompt containing:
  - a compact summary of the crash
  - guidance on tool usage and reporting constraints
  - the ability to call tools to fetch more evidence
- The LLM iterates:
  - **fetch evidence** (via tools like `report_get` / `exec`)
  - **form and update competing hypotheses**
  - **record evidence** with stable IDs
  - eventually **finalize** via `analysis_complete(...)`

### Phase 2 — Judge step (internal)

After the investigation has enough evidence (or when the run must be finalized due to budgets), the server runs a bounded judge step:

- The LLM is asked to pick the **best-supported hypothesis** and reject alternatives.
- Output is captured via `analysis_judge_complete(...)`, referencing evidence IDs (`E#`).

This produces a “decision record” that is easier to audit than a single freeform explanation.

### Phase 3 — Summary rewrite pass (bounded)

The server runs a separate sampling pass to rewrite:

- `analysis.summary.description`
- `analysis.summary.recommendations`

This pass is intentionally bounded and converges quickly. It is designed to produce a human-friendly “top of report” summary that is still evidence-backed.

### Phase 4 — Thread narrative pass (bounded)

Finally, the server runs a thread narrative pass to answer: “What was the process doing when the dump was taken?”

Outputs:

- `analysis.aiAnalysis.threadNarrative`
- `analysis.threads.summary.description` (populated/overwritten on success)

This pass is also bounded to avoid tool-call loops.

---

## Tools Available During AI Sampling

During `analyze(kind="ai")`, the model can call two categories of tools:

### Evidence-gathering tools

These fetch facts from the report/debugger:

- `report_get({path,...})` — read from the canonical crash report JSON (paged, projectable, filterable)
- `exec({command})` — run a debugger command (LLDB/WinDbg/SOS) in the active session
- `inspect({address,maxDepth?})` — inspect a managed object at an address (ClrMD-based when available)
- `get_thread_stack({threadId})` — get a full stack for a specific thread from the report

### Orchestration and meta tools

These are used to converge and keep stable state:

- `analysis_complete({rootCause,confidence,reasoning,evidence,...})` — ends the investigation loop when the model is ready to conclude
- `checkpoint_complete({facts,hypotheses,evidence,doNotRepeat,nextSteps})` — internal checkpoint (“working memory” snapshot), used periodically so the server can prune older context without losing progress
- `analysis_hypothesis_register({hypotheses:[...]})` — register competing hypotheses (bounded; do not spam)
- `analysis_hypothesis_score({updates:[...]})` — update hypothesis confidence and link evidence IDs
- `analysis_evidence_add({items:[...]})` — annotate existing evidence items (see evidence provenance)
- `analysis_judge_complete({...})` — internal judge completion (selects the best hypothesis and rejects alternatives)

The summary rewrite and thread narrative passes use dedicated completion tools:

- `analysis_summary_rewrite_complete({description,recommendations})`
- `analysis_thread_narrative_complete({description,confidence})`

---

## “Working Memory”: Checkpoints, Evidence Ledger, Hypotheses

AI sampling needs to handle two hard constraints:

1. **LLMs forget**: older tool outputs are pruned to keep context bounded.
2. **LLMs loop**: some models repeat the same tool calls or “meta bookkeeping” without progressing.

To stabilize the run, the server maintains a structured working memory.

### Evidence ledger (`E#`)

The evidence ledger is a bounded list of stable items like:

- `E12` — “MissingMethodException: ConcurrentDictionary.TryGetValue not found …”

Evidence IDs are intended to be:

- stable across context pruning
- reusable in hypotheses and final conclusions
- auditable (each item includes a source, and may include tool-call hashes)

### Hypotheses (`H#`)

Hypotheses capture “competing explanations” the model is considering, for example:

- `H1` — “Assembly version mismatch”
- `H2` — “IL rewriting / profiler instrumentation”
- `H3` — “Trimming removed required method”

The model can register hypotheses and later score/link them to evidence IDs.

### Checkpoints (`checkpoint_complete`)

Every N iterations (default is 4, configurable), the server requests an internal checkpoint:

- captures facts, hypotheses, evidence, and hard “doNotRepeat” tool-call keys
- provides a short list of `nextSteps` that are high-signal and non-duplicative
- encodes tool contracts (“don’t call report_get with missing path”, “no array slice indices”, “cursor must match identical query shape”, etc.)

The server can also inject deterministic checkpoints to preserve progress even when a provider cannot support full tool history.

---

## Evidence Provenance (Anti-Poisoning)

By default, AI sampling runs with **evidence provenance enabled**.

What this means:

- The server auto-records evidence ledger items directly from tool outputs (e.g., from `report_get`, `exec`, `inspect`).
- The model can use `analysis_evidence_add` only to *annotate* existing evidence (tags, why-it-matters), not to inject new “facts”.

This prevents “evidence poisoning” where a model could fabricate findings and store them as evidence.

---

## How the System Converges on Root Cause (Process)

The investigation loop is designed to follow an evidence-first methodology:

### 1) Establish the baseline

Start with the smallest set of report evidence that anchors the crash:

- `metadata` (debugger/runtime context)
- `analysis.summary` (crash type + top-level counts)
- `analysis.environment` (OS/arch/runtime)
- `analysis.exception.*` (type/message/HResult)
- `analysis.exception.stackTrace` (first ~8 frames)

### 2) Generate competing hypotheses early

Register a small set of plausible explanations (2–4 is usually enough). This avoids tunnel vision and helps the judge step later.

### 3) Gather discriminating evidence

Use tools that *separate* hypotheses. Examples:

- **Version mismatch**: enumerate assemblies/modules and compare versions/paths.
- **Trimming**: inspect method metadata and confirm whether expected methods exist for the instantiated generic type.
- **IL rewriting / profiler instrumentation**: dump method desc / IL / native code state; check if the executing method is R2R/JIT; look for mismatched metadata.
- **Memory corruption**: look for inconsistent method tables / invalid pointers / contradictory symbols.

### 4) Update hypothesis confidence using evidence IDs

As new evidence arrives, update hypotheses with explicit evidence citations (`E#`).

### 5) Judge selection

Once evidence is sufficient, the judge step selects the best-supported hypothesis and explicitly rejects alternatives with contradictions.

### 6) Finalize the conclusion (`analysis_complete`)

The model ends the loop with:

- `rootCause` + `confidence`
- `reasoning` that cites evidence IDs and/or tool outputs
- `evidence` list (human-readable)
- recommendations (actionable next steps)

The server validates completion payloads and can enforce “minimum evidence” expectations before accepting a high-confidence conclusion.

---

## Loop Guards and Budgets (Why the Run Stops)

AI sampling is intentionally bounded to prevent runaway cost and infinite loops. The orchestrator enforces:

- max iterations
- max tool calls total
- max tool uses per iteration (guards against a single response containing hundreds of repeated calls)
- max consecutive “no progress” iterations
- separate budgets for:
  - summary rewrite pass
  - thread narrative pass
  - meta-tool calls

If a budget is hit, the server finalizes with the best evidence-backed result it can produce.

---

## Provider Quirks and Compatibility Features

### Tool-choice fallback (OpenRouter)

Some OpenRouter models reject OpenAI-style `tool_choice="required"` during tool-driven sampling (often returning a 404 mentioning `tool_choice`).

The server detects this and retries with `tool_choice="auto"`, then caches that capability for the remainder of the run to avoid repeated failures and budget waste.

### Structured tool history fallback

Some providers/models cannot handle full structured tool history in sampling requests. When detected, the server switches to a **checkpoint-only history mode**:

- checkpoints become the primary carry-forward memory
- the run remains evidence-driven, but with smaller message histories

---

## Tracing and Debugging (Optional)

AI sampling can emit trace logs and files to help debug tool loops and provider quirks.

Common environment variables:

- `DEBUGGER_MCP_AI_SAMPLING_TRACE=true` (log previews)
- `DEBUGGER_MCP_AI_SAMPLING_TRACE_FILES=true` (write full payloads)
- `DEBUGGER_MCP_AI_SAMPLING_TRACE_MAX_FILE_BYTES=2000000` (per-file cap)
- `DEBUGGER_MCP_AI_SAMPLING_CHECKPOINT_EVERY_ITERATIONS=4` (override checkpoint interval; default is 4)
- `DEBUGGER_MCP_AI_EVIDENCE_PROVENANCE=false` (evidence provenance is enabled by default; set to `false` to disable)
- `DEBUGGER_MCP_AI_EVIDENCE_EXCERPT_MAX_CHARS=2048` (max chars stored per auto evidence finding)

Trace files are written under the configured log storage directory (often `LOG_STORAGE_PATH/ai-sampling`).

> Trace files may contain sensitive debugger output. Treat them like crash dumps: store carefully and delete when done.

---

## Caching Behavior

`analyze(kind="ai")` supports a disk cache keyed by:

- `userId`
- `dumpId`
- optional LLM cache key (provider/model/reasoning effort)
- whether watches/security were included

Use `refreshCache=true` (or CLI `--refresh`) to force a fresh AI analysis run.
