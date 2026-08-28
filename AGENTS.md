# a-engine — Dynamic Text Adventure Engine

A data-driven text adventure engine and runtime, inspired by z-engine but with no
virtual machine: runtime behavior is driven by an extensible library of runners and
handlers. Built in stages; stage 1 (core + MVP scenario) and the LLM harness
stage (player free-text planning + LLM-driven NPC policy) are complete.

## Build & Test

```bash
dotnet build          # build the solution (a-engine.slnx, .NET 10 XML format)
dotnet test           # run all xUnit tests
dotnet run --project src/AEngine.Cli   # play the MVP scenario (menu-driven)
dotnet run --project src/AEngine.Cli -- scenarios/npc   # play the NPC demo scenario
dotnet run --project src/AEngine.Cli -- --debug-api   # also serve the debug REST API
dotnet run --project src/AEngine.Cli -- --llm-endpoint http://127.0.0.1:5001 --llm-model NAME   # LLM planning
cd client && npm install && npm run dev   # debug web client (needs the CLI with --debug-api)
```

Targets **net10.0**. The SDK is .NET 10; only the 10.0 runtime is installed, so do
not retarget to net8.0 without installing its runtime.

## Layout

```
src/AEngine.Core/         # engine: World/, Modules/, Actions/, Signals/, Policies/, Runtime/, Scenarios/
src/AEngine.Cli/          # menu-driven console REPL (optional LLM free-text planning)
src/AEngine.DebugServer/  # debug REST API (System.Net.HttpListener, loopback only)
src/AEngine.Llm/          # LLM harness: OpenAI-compatible client, planner, parser, executor, LlmPolicy
client/                   # debug web client (Vue 3 + Vite + TypeScript, vue-only dep)
scenarios/mvp/            # MVP scenario: modules.json + world.json
scenarios/npc/            # NPC demo: kitchen/dining hall, random-policy cook, signals
tests/AEngine.Tests/      # xUnit, includes scripted-playthrough integration test
```

## Architecture (as implemented)

- **World model** — `WorldObject`s form a single tree rooted at `world`; a flat
  id→object index lives in `World`. Objects exist exactly once. Cross-cutting state
  (e.g. a door's open/locked flags) lives in its own object and is referenced **by
  id** from other objects (e.g. two `portal` door-sides share one `doorstate`
  object via their `stateRef` field). Attribute values are `JsonElement`; a string
  holding another object's id is a reference by convention (`ref`-typed module
  fields).
- **Runtime mutation** — `World` exposes `CreateObject`, `DestroyObject`
  (recursive), `MoveObject` (cycle-checked), `AddModule`, `RemoveModule`,
  `SetAttribute`, `SetFieldOverride`. All are safe to call mid-game.
- **Modules** — composable, data-driven types (`scenarios/mvp/modules.json`):
  `{ id, name, fields: [{name, type, default}], affordances: [{verb, handler,
  prompt?, signals?}] }`. Field types: `string | int | bool | ref`. Field
  resolution: per-object override → module default. `ModuleRegistry` supports
  register/update/unregister at runtime. An affordance's optional `prompt`
  marks the verb as taking a free-text argument (surfaced to the handler as
  `ActionContext.Args["text"]`); its optional `signals` list declares the
  sensory signals emitted on success.
- **Actions** — module affordances name a `handler` **string id**, resolved through
  `HandlerRegistry` (handlers are replaceable at runtime — this is the extension
  seam). Built-ins: look, go, open, close, take, drop, unlock, lock, inventory,
  say. `ActionResolver` enumerates the actions currently available to an agent as
  structured `(verb, target, label, handlerId, moduleId, prompt?)` entries.
  Listings are filtered by **observable** state: `open`/`close` follow the
  visible open state, take/drop follow held — while `unlock`/`lock` are always
  listed since lock state is not observable. `ResolvePotential` returns the same
  set without open/close state filtering, so a generated-but-redundant plan line
  still resolves (and noops at runtime). Results are three-valued
  (`ActionOutcome.Success | Noop | Failure`): redundant attempts whose end state
  already holds are **noops** (no turn consumed, no signals, not a failure —
  plan executors skip over them), wrong-state/missing-key attempts are failures
  (turn consumed). `look` exits show `open`/`closed` only (never "locked").
- **Signals** — ephemeral sensory observations (`SignalSense.Visual | Audible`)
  delivered by `SignalBus` on `GameEngine` into per-agent in-memory queues
  (`Emit`/`Drain`/`Peek`). After a successful action, `TurnManager.PerformAction`
  looks up the affordance's signal specs and each observing agent (any object
  with the `agent` module except the actor) receives the single highest-priority
  receivable signal (ties: first listed); texts format `{agent}`/`{target}`/
  `{arg}` placeholders. Propagation: same room → all senses; one portal away →
  a sense passes only if the portal **side in the origin room** transmits it
  (`portal` fields `transmitVisual`/`transmitAudio`: `always | whenOpen | never`,
  defaults `whenOpen`/`always`; `whenOpen` reads the shared doorstate via that
  side's own `stateRef`); farther rooms get nothing. One-way propagation (e.g.
  a one-way mirror) is pure data on the two sides. Signals delivered through a
  portal get a directional suffix naming the side in the observer's room
  ("… through the wooden door to the south."), suppressed when the signal's
  target is that same door ("the cook opens the wooden door."). An action
  targeting a portal manifests on both sides of the door: observers in the
  other side's room perceive it as a same-room event (visual included),
  transmission rules notwithstanding. Signal specs have a `scope`:
  unset = normal propagation; `departure`/`arrival` are delivered only on
  portal traversal (a successful `go`) to observers in the room left / the
  room entered, with `{exitPortal}`/`{exitDirection}`/`{entryPortal}`/
  `{entryDirection}` placeholders ("the cook exits through the wooden door to
  the south." / "the cook enters from the wooden door to the north.").
- **Policies & NPC turns** — agents with `agent.policy != "player"` are
  autonomous. `IAgentPolicy.ChooseActionAsync` picks one of the resolved
  actions; policies resolve by string id through `PolicyRegistry` (replaceable
  at runtime — the LLM-policy seam, mirroring `HandlerRegistry`). The built-in
  `random` policy picks uniformly via `GameEngine.Random` (settable; seed it in
  tests) and supplies canned phrases for `say`. `TurnManager.RunNpcTurns()`
  runs an async-ready pipeline per NPC: start the selection and skip the turn →
  skip while the task is in flight → when complete, re-resolve and execute only
  if the chosen `(verb, targetId)` is still available (stale choices are
  discarded). The CLI calls `RunNpcTurns()` after each player action and prints
  the player's drained signals as `You see: …`/`You hear: …` lines.
- **LLM harness** (`src/AEngine.Llm`, no third-party deps) — one machinery
  serves both player free text and NPC decisions. `OpenAiCompatibleClient`
  POSTs `{BaseUrl}/v1/chat/completions` (OpenAI chat schema, optional Bearer
  key; works against KoboldCPP/llama.cpp, OpenRouter, Kimi, DeepSeek);
  `FakeLlmClient` queues canned responses for tests. `AgentContextBuilder`
  renders the **public** world view only (room, visible items with
  closed-container contents hidden, exits open/closed, inventory, action menu
  labels; NPC extras: `agent` module `character`/`goals` fields + drained
  signals). `LlmPlanner` builds the system/user prompts (output contract: one
  action per line, exactly as listed); `PlanParser` tolerantly strips
  numbering/bullets/prose (keeps lines starting with a known verb);
  `PlanExecutor` matches each line against **currently** available actions
  (case-insensitive label equality, then normalized containment) and stops on
  no-match or failure — conditional availability (unlock → open → go) resolves
  at execution time. `LlmPolicy` (id `llm`) asks for a full plan on first
  selection, caches steps, pops them matched against current availability; a
  stale step discards the plan remainder and re-plans next selection.
- **Runtime** — `GameEngine` ties everything together; `TurnManager` is turn-based;
  `Scheduler` is a wake-up queue for long-running actions; `TimeMode`
  (`TurnBased`/`RealTime`) is settable but real-time is not yet implemented.
- **Scenarios** — JSON files defining modules and an initial world tree;
  `ScenarioLoader` composes multiple files in order (later overrides by id).
- **Debug REST API** — `AEngine.DebugServer.DebugServer` serves the live world
  over HTTP for dev tooling. Built on `System.Net.HttpListener` (base class
  library, zero extra dependencies), bound to **loopback only**. Enable it in
  the CLI with `--debug-api` (default port 5050), `--debug-api=PORT`, or
  `--debug-port N`. **Off by default; unauthenticated — never expose it beyond
  localhost.** Endpoints (JSON in/out, camelCase):
  `GET /api/health`; `GET /api/engine` (time mode, current turn, pending
  scheduler entries); `GET /api/world/tree`; `GET /api/objects`;
  `GET /api/objects/{id}` (attributes + modules with resolved field values);
  `POST /api/objects` `{id, parentId, name?, description?}`;
  `DELETE /api/objects/{id}` (recursive); `POST /api/objects/{id}/move`;
  `PUT|DELETE /api/objects/{id}/attributes/{name}`;
  `PUT|DELETE /api/objects/{id}/modules/{moduleId}`;
  `PUT /api/objects/{id}/modules/{moduleId}/fields/{field}`;
  `GET /api/modules`; `GET /api/actions?agentId=`;
  `POST /api/actions/execute` `{agentId, verb, targetId}` → runs the resolved
  menu action through `TurnManager.PerformAction` (advances the turn),
  200 → `{success, message, turn}`, unknown agent or unavailable action → 404.
  Errors: unknown id → 404,
  cycle/duplicate/root-guard → 409, bad JSON → 400, wrong method → 405.
  Permissive CORS (any origin, OPTIONS preflight) for the browser client.
  All world access (HTTP and REPL alike) is serialized on `GameEngine.SyncRoot`.
- **Debug web client** (`client/`) — Vue 3 + Vite + TypeScript, manually
  scaffolded (runtime dep: `vue` only; dev: vite, @vitejs/plugin-vue,
  typescript, vue-tsc). Scripts: `npm run dev` (proxies `/api` →
  `http://127.0.0.1:5050`), `npm run build` (`vue-tsc --noEmit && vite build`),
  `npm run preview`. It expects the CLI running with `--debug-api`; the API
  base URL is editable in the header (default `http://127.0.0.1:5050`,
  persisted to localStorage). Views: world tree, object editor (attributes,
  modules + field overrides, move/delete/create child), engine panel, actions
  panel (execute via `POST /api/actions/execute`). Manual refresh + optional
  ~2s auto-poll; no server push.

## Conventions

- System.Text.Json only; no third-party runtime dependencies — the debug server
  uses `System.Net.HttpListener` from the base class library (no ASP.NET).
- New world behavior goes in data (modules/scenarios) first; new *verbs* get an
  `IActionHandler` registered in `HandlerRegistry`, not hardcoded branches.
- Concurrent world access (e.g. from the debug HTTP server) must take
  `lock (engine.SyncRoot)`; `TurnManager` already does.
- Tests are required for engine changes; the MVP playthrough
  (`tests/AEngine.Tests/MvpPlaythroughTests.cs`) must stay green.
- Commit logical changes separately: one commit per feature/fix, staged
  explicitly by path — never batch unrelated changes into a single commit
  (`git add -A` after a long multi-feature session is how that happens).
- Do not commit build artifacts (`bin/`, `obj/`, `*.dll`) — see `.gitignore`.

## Future goals (NOT yet implemented — don't assume these exist)

- **LLM integration** — partially implemented: the harness (`AEngine.Llm`)
  parses player free text into action plans and drives NPCs via `LlmPolicy`;
  see "LLM harness" above. CLI options `--llm-endpoint/--llm-model/
  --llm-api-key` (env fallbacks `AENGINE_LLM_ENDPOINT/MODEL/API_KEY`); with an
  endpoint configured, non-numeric input is planned and executed stepwise
  (menu numbers still work). Live verification against a real server (e.g.
  KoboldCPP) is manual. Still planned: narration (LLM translating outcomes
  into prose), guided world expansion for *open* scenarios, `say` with
  LLM-chosen phrases inside plans (plans can't yet carry free-text args),
  provider config files, streaming, retries/backoff.
- **Autonomous agents** — partially implemented: NPCs with
  `agent.policy != "player"` act through the same affordances via
  `IAgentPolicy` (built-ins: `random` in Core, `llm` in AEngine.Llm); see
  "Policies & NPC turns" above. Planned: perception-driven policies (the
  random policy ignores signals), agenda-driven NPCs, multi-player (multiple
  players controlling different agents).
- **Real-time mode** — `TimeMode.RealTime` is a config stub. Planned: short turns
  with auto-pass, suitable for simultaneous multi-player action.
- **Custom conflict/skill handlers** — e.g. lockpicking resolved by an RPG
  stats+skills system, pickpocket/combat adjudication; handlers registered into
  `HandlerRegistry` and wired from data.
- **Long-running actions** — `Scheduler` exists but nothing schedules multi-turn
  actions yet.
- **Signals in the debug web client** — a `GET /api/signals?agentId=` peek
  endpoint + panel would slot in (`SignalBus.Peek` already exists).
- **Signal intensity & attenuation** — generalize transmission: emitters get an
  `intensity` (audible and visual), transmitters (portal sides) get an
  `attenuation` (abstract decibels). Loud sounds (gunshots) carry several rooms;
  soft sounds may not leave the room. Signal specs could declare multiple
  representations chosen by surviving intensity — full fidelity up close
  ("the old cook says: "Hm, where did I put it?""), degraded at range
  ("someone says something"). Propagation would extend beyond adjacent rooms,
  attenuating per hop.
