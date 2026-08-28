# a-engine — Dynamic Text Adventure Engine

A data-driven text adventure engine and runtime, inspired by z-engine but with no
virtual machine: runtime behavior is driven by an extensible library of runners and
handlers. Built in stages; stage 1 (core + MVP scenario) is complete.

## Build & Test

```bash
dotnet build          # build the solution (a-engine.slnx, .NET 10 XML format)
dotnet test           # run all xUnit tests
dotnet run --project src/AEngine.Cli   # play the MVP scenario (menu-driven)
dotnet run --project src/AEngine.Cli -- --debug-api   # also serve the debug REST API
cd client && npm install && npm run dev   # debug web client (needs the CLI with --debug-api)
```

Targets **net10.0**. The SDK is .NET 10; only the 10.0 runtime is installed, so do
not retarget to net8.0 without installing its runtime.

## Layout

```
src/AEngine.Core/         # engine: World/, Modules/, Actions/, Runtime/, Scenarios/
src/AEngine.Cli/          # menu-driven console REPL
src/AEngine.DebugServer/  # debug REST API (System.Net.HttpListener, loopback only)
client/                   # debug web client (Vue 3 + Vite + TypeScript, vue-only dep)
scenarios/mvp/            # MVP scenario: modules.json + world.json
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
  `{ id, name, fields: [{name, type, default}], affordances: [{verb, handler}] }`.
  Field types: `string | int | bool | ref`. Field resolution: per-object override →
  module default. `ModuleRegistry` supports register/update/unregister at runtime.
- **Actions** — module affordances name a `handler` **string id**, resolved through
  `HandlerRegistry` (handlers are replaceable at runtime — this is the extension
  seam). Built-ins: look, go, open, close, take, drop, unlock, lock, inventory.
  `ActionResolver` enumerates the actions currently available to an agent as
  structured `(verb, target, label, handlerId)` entries, filtered by world state.
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
- Do not commit build artifacts (`bin/`, `obj/`, `*.dll`) — see `.gitignore`.

## Future goals (NOT yet implemented — don't assume these exist)

- **LLM integration** — parse player natural language into actions, translate
  outcomes into narration, and guided world expansion for *open* scenarios.
  Seams: `HandlerRegistry` id indirection; `ActionResolver` already returns
  structured data suitable for an LLM prompt. No LLM code exists yet.
- **Autonomous agents** — the `agent` module exists, but agents are only
  player-controlled today. Planned: AI-driven agents (monsters, agenda-driven
  NPCs) acting through the same affordances; inherently multi-player capable
  (multiple players controlling different agents).
- **Real-time mode** — `TimeMode.RealTime` is a config stub. Planned: short turns
  with auto-pass, suitable for simultaneous multi-player action.
- **Custom conflict/skill handlers** — e.g. lockpicking resolved by an RPG
  stats+skills system, pickpocket/combat adjudication; handlers registered into
  `HandlerRegistry` and wired from data.
- **Long-running actions** — `Scheduler` exists but nothing schedules multi-turn
  actions yet.
