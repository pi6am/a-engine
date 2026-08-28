# a-engine — Dynamic Text Adventure Engine

A data-driven text adventure engine and runtime, inspired by z-engine but with no
virtual machine: runtime behavior is driven by an extensible library of runners and
handlers. Built in stages; stage 1 (core + MVP scenario) is complete.

## Build & Test

```bash
dotnet build          # build the solution (a-engine.slnx, .NET 10 XML format)
dotnet test           # run all xUnit tests
dotnet run --project src/AEngine.Cli   # play the MVP scenario (menu-driven)
```

Targets **net10.0**. The SDK is .NET 10; only the 10.0 runtime is installed, so do
not retarget to net8.0 without installing its runtime.

## Layout

```
src/AEngine.Core/     # engine: World/, Modules/, Actions/, Runtime/, Scenarios/
src/AEngine.Cli/      # menu-driven console REPL
scenarios/mvp/        # MVP scenario: modules.json + world.json
tests/AEngine.Tests/  # xUnit, includes scripted-playthrough integration test
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

## Conventions

- System.Text.Json only; no third-party runtime dependencies.
- New world behavior goes in data (modules/scenarios) first; new *verbs* get an
  `IActionHandler` registered in `HandlerRegistry`, not hardcoded branches.
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
