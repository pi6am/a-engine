# LLM harness and narration

`src/AEngine.Llm` — no third-party dependencies. One machinery serves
player free-text planning, NPC decisions, and narration. CLI options
`--llm-endpoint/--llm-model/--llm-api-key` (env fallbacks
`AENGINE_LLM_ENDPOINT/MODEL/API_KEY`); with an endpoint configured,
non-numeric input is planned and executed stepwise (menu numbers still
work; input exactly matching an action label, e.g. "wait", runs directly
without an LLM call). Live verification against a real server (e.g.
KoboldCPP) is manual.

## Client and context

`OpenAiCompatibleClient` POSTs `{BaseUrl}/v1/chat/completions` (OpenAI chat
schema, optional Bearer key; works against KoboldCPP/llama.cpp, OpenRouter,
Kimi, DeepSeek); `FakeLlmClient` queues canned responses for tests.
`AgentContextBuilder` renders the **public** world view only (room, visible
items with closed-container contents hidden, exits open/closed, inventory,
action menu labels; NPC extras: `agent` module `character`/`goals` fields +
the agent's memory of recent observations and actions).

## Planning and execution

`LlmPlanner` builds the system/user prompts (output contract: one action
per line, exactly as listed); `PlanParser` tolerantly strips
numbering/bullets/prose (keeps lines starting with a known verb — the
defaults union the scenario's currently available verbs, so RPG verbs like
`attack` survive, and parameterized labels with filled-in arguments parse:
"Attack the arena duelist in the head"); `PlanExecutor` matches each line
against **currently** available actions (case-insensitive label equality,
then normalized containment) and stops on no-match or failure — conditional
availability (unlock → open → go) resolves at execution time.

## NPC policy

`LlmPolicy` (id `llm`) asks for a full plan on first selection, caches
steps, pops them matched against current availability; a stale step
discards the plan remainder and re-plans next selection. New observed
signals (anything pending in the agent's signal queue — it is drained into
the context whenever a plan is made) interrupt the cached plan and trigger
an immediate re-plan, so agents respond to being spoken to instead of
carrying out a stale plan.

## Room narration

`Narrator`, driven by the CLI's `/narrate` scope (`room`|`all` narrate
rooms; `actions` is reserved), rewrites the raw look render into prose,
both on arrival and on an explicit `look` (room name printed as a title,
then the paragraph; the prompt demands factual fidelity — exits,
open/closed states, contents, who is present and wearing what — in a
concise second-person paragraph). Per-room cache: an unchanged raw render
replays the cached narration with no LLM call; a changed one is re-narrated
with the previous raw text and narration in the prompt, so the prose stays
consistent and calls out what changed ("the loaf of bread is gone").
Purely presentational (the engine and agent memory keep the raw text);
empty reply or transport error falls back to the raw render silently.
Turn-based arrival awaits the narration; the real-time timer narrates in
the background (never blocking the world clock) and prints above the prompt
when ready.

## Planned

Action narration (/narrate actions|all covers it once wired), guided world
expansion for *open* scenarios, speech variants (Shout/Whisper — need
per-spec propagation overrides; say already carries free-text speech),
provider config files, streaming, retries/backoff.
