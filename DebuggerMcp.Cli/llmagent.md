# `llmagent` — Interactive LLM Agent Workflow (CLI Copilot)

This document explains how **`llmagent`** works in `dbg-mcp` (DebuggerMcp.Cli): what the agent can do, how it uses tools, how it stays grounded (evidence ledger + checkpoints + juror), and how to use it effectively to reach correct conclusions without getting stuck in loops.

> `llmagent` is **interactive**: you guide the investigation. It differs from `analyze ai`, which is a **single-goal**, server-driven sampling loop that must converge to a final `analysis.aiAnalysis` result.

---

## Quick Orientation

### What `llmagent` is

`llmagent` is an interactive, tool-using copilot built into the CLI. It:

- Uses your current **server/session/dump scope** as context
- Can call MCP tools (e.g., `report_get`, `exec`) and the CLI will execute them
- Tracks evidence (`E1`, `E2`, …) and a compact checkpoint so the agent can keep making progress even if earlier messages are pruned
- When you ask for a *conclusion*, can run an additional tool-disabled **juror** pass to sanity-check the answer (and at most once) gather missing evidence

### What `llmagent` is not

- It is not a background batch analysis tool (that’s `analyze ai`).
- It is not forced-tool execution: the model decides when to call tools (`tool_choice="auto"`), and you steer the conversation.
- It is not a “guaranteed one-shot root cause”: it can explore, iterate, and refine as you ask follow-up questions.

---

## Starting `llmagent`

Prerequisites:

1. Connect to a server: `connect <url>`
2. Open a dump: `open <dumpId>`
3. Configure an LLM provider (OpenRouter/OpenAI/Anthropic):
   - OpenRouter: set `OPENROUTER_API_KEY` (recommended) + optionally `OPENROUTER_MODEL`
   - OpenAI: set `OPENAI_API_KEY` + `llm provider openai`
   - Anthropic: set `ANTHROPIC_API_KEY` + `llm provider anthropic`

Start interactive mode:

```text
dbg-mcp> llmagent
llmagent> what happened with this dump?
```

`llmagent` supports slash commands:

```text
/help
/tools
/reset
/reset conversation
/exit
```

Reset semantics:

- `/reset conversation` clears only the LLM conversation turns (keeps CLI transcript context).
- `/reset` clears the LLM conversation *and* inserts a reset marker into the transcript context for this dump/session.
- Both resets also clear the internal checkpoint/evidence state for the current server/session/dump scope.

---

## Tools Available to the Agent

In `llmagent`, the model can request these tools (the CLI executes them):

- `report_index` — small “index” of the current report (summary + table of contents)
- `report_get` — fetch a section of the canonical crash report JSON by path (paged, projectable, filterable)
- `exec` — run a debugger command (LLDB/WinDbg/SOS) in the active session
- `analyze` — run an automated analysis pass (crash/perf/cpu/allocations/gc/contention/security)
- `inspect_object` — inspect a managed object at an address (ClrMD) and return JSON
- `clr_stack` — managed stacks via ClrMD (fast; useful when debugger output is noisy)

Tip: attach local files/reports to your prompt with `@./path` to provide extra context.

---

## How `llmagent` Builds Context

Each `llmagent` prompt is converted into a set of messages for the LLM:

1. A **system prompt** describes:
   - the tool contracts (e.g., `report_get.path` required, no array slices like `[0:10]`, cursor rules)
   - SOS/debugger safety rules (e.g., don’t re-load SOS if `metadata.sosLoaded=true`)
   - evidence-driven expectations (don’t invent output; call tools when needed)
2. A **transcript context** block is included (bounded):
   - recent CLI history for this server/session/dump (open/exec/analyze/report output, etc.)
3. Your **current user prompt**
4. A compact **internal checkpoint** (when present) is inserted near the top to preserve stable “memory” across pruning.

This context is intentionally bounded to keep prompts stable and avoid runaway token growth.

---

## The Agent Loop (What Happens After You Press Enter)

The agent runs an iterative tool-calling loop:

1. The CLI sends messages + tool schemas to the provider (`tool_choice="auto"`).
2. The model may return:
   - a normal assistant message (no tools), or
   - one or more tool calls
3. If tools are requested:
   - the CLI executes each tool call (subject to confirmation settings)
   - tool outputs are:
     - redacted for obvious secrets
     - truncated for model safety
   - tool outputs are appended back into the message history
4. The loop repeats until:
   - the model stops requesting tools and produces an answer, or
   - the iteration budget is reached, or
   - the loop guard decides it is stuck

### Iteration limits and “stops”

`llmagent` is bounded to avoid infinite loops:

- The interactive run is allowed a larger iteration budget than a single-shot chat.
- If the loop hits its iteration limit, the CLI returns the last model text and (when available) a “Suggested next” action extracted from the internal checkpoint.

### Auto-continue when the model “plans” but doesn’t call tools

Some models respond with text like:

> “Let me check the MethodTable:”

…but emit **no tool calls**, which would normally end the agent loop prematurely.

To reduce this failure mode, the runner will (once) automatically nudge the model to continue and call tools if the response clearly looks like an unfinished follow-up (e.g., ends with `:` / `...` / `…`).

---

## Evidence Ledger (`E#`) and Checkpoints

`llmagent` keeps a lightweight “working memory” per server/session/dump scope:

### Evidence ledger (`E1`, `E2`, …)

- Every executed tool result is tracked as evidence with:
  - a stable normalized tool key (tool name + canonical args)
  - a small preview excerpt
  - tags (e.g., “BASELINE_EXC_MESSAGE”)
  - seen counts (useful when the model repeats calls)

This ledger is used to:

- avoid re-learning the same facts after context pruning
- detect no-progress loops (repeating identical calls without new evidence)

### Checkpoints

The CLI periodically synthesizes a compact checkpoint that includes:

- whether baseline evidence is complete
- a short evidence index (recent/high-signal items)
- do-not-repeat guidance for immediate duplicates
- suggested next steps when the loop is stuck or stopped

The checkpoint is inserted into the next prompt as an internal system message, so the model can “recover” context even after pruning.

---

## Baseline Enforcement (When You Ask for a Conclusion)

`llmagent` uses a prompt classifier to detect when your prompt is *conclusion-seeking* (examples):

- “what happened?”
- “root cause?”
- “analyze this crash”
- “recommendations?”

For conclusion-seeking prompts, the runner enforces a minimum baseline set (e.g., `metadata`, `analysis.summary`, `analysis.exception.type/message/hResult`, short stack).

If the model tries to answer without requesting tools and the baseline is missing, the CLI will:

1. Prefetch the missing baseline once (by executing the planned `report_get` calls), then
2. Let the model continue with evidence in-hand

If the model still refuses to call tools and baseline is missing, the CLI returns a “baseline incomplete” message with the missing items and a suggested next call.

Goal: avoid confident conclusions built on too little evidence.

---

## Juror Pass (Conclusion Quality Guard)

For conclusion-seeking prompts, `llmagent` can run a second pass:

- Tools are disabled
- The “juror” evaluates whether the conclusion is evidence-backed and whether there are obvious missing checks

If the juror says confidence is low and provides bounded missing-evidence steps, the CLI runs **one correction cycle**:

1. Feed juror feedback back to the agent
2. Allow up to a small number of extra tool calls
3. Require the updated conclusion to cite explicit evidence IDs

This is intentionally bounded (at most one correction cycle) to avoid loops and surprise costs.

---

## Tool Confirmation and Safety

Depending on your settings:

- Tool calls may require confirmation (`llm set-agent-confirm true`)
- In interactive `llmagent`, the CLI temporarily disables confirmations by default so the agent can act autonomously, then restores your previous setting when you exit

If a tool call is denied, the model receives an error string and should proceed with alternatives or ask for guidance.

---

## Recommended Workflow (High ROI)

### 1) Start broad, then narrow

Good first prompt:

```text
llmagent> what happened with this dump?
```

Then steer into a hypothesis:

```text
llmagent> does this look like trimming, version mismatch, or instrumentation? show evidence
```

### 2) Ask for a conclusion only after evidence exists

```text
llmagent> what’s the most likely root cause? cite the report evidence
```

This triggers baseline enforcement + (potentially) a juror pass.

### 3) When the model gets stuck, steer it

If the agent stops with a “Suggested next”:

- paste a short “continue” prompt
- or explicitly ask it to follow the suggestion

Example:

```text
llmagent> follow the suggested next step and then update the conclusion
```

### 4) Use `/reset conversation` for a clean slate without losing CLI context

If the agent got confused but you want to keep the CLI transcript context:

```text
llmagent> /reset conversation
llmagent> start over: summarize the crash and the key evidence
```

---

## Tracing and Debugging

When enabled, `llmagent` writes HTTP request/response traces for LLM calls. The session header prints the trace folder path.

Notes:

- Traces may contain sensitive data from tool outputs and prompts.
- Delete trace folders when you’re done sharing/debugging.

---

## When to Use `llmagent` vs `analyze ai`

Use `llmagent` when:

- you want an interactive copilot
- you’re exploring multiple angles or asking iterative follow-ups
- you want to guide the investigation manually

Use `analyze ai` when:

- you want a single, shareable report result (`analysis.aiAnalysis`) with stable evidence/hypothesis IDs
- you want the server to enforce a complete end-to-end pipeline (including summary rewrite + thread narrative)
- you want disk caching of AI analysis results

