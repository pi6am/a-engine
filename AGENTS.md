# a-engine — Dynamic Text Adventure Engine

A data-driven text adventure engine and runtime, inspired by z-engine but with no
virtual machine: runtime behavior is driven by an extensible library of handlers
and policies. The world is a tree of objects typed by composable, data-driven
"modules"; scenarios (initial world + rules) are JSON; agents (player or AI) act
through the same affordances and perceive through sensory signals.

## Build & Test

```bash
dotnet build          # build the solution (a-engine.slnx, .NET 10 XML format)
dotnet test           # run all xUnit tests
dotnet run --project src/AEngine.Cli   # play the MVP scenario (text-first REPL)
dotnet run --project src/AEngine.Cli -- scenarios/npc   # play the NPC demo scenario
dotnet run --project src/AEngine.Cli -- adventure.scen  # or a zip-packaged scenario (any extension)
dotnet run --project src/AEngine.Cli -- rpg.png         # or an image card (png/jpeg metadata)
dotnet run --project src/AEngine.Util -- card pack scenarios/rpg rpg.png   # pack a scenario into an image
dotnet run --project src/AEngine.Util -- card unpack rpg.png scenarios/rpg # ... and back
dotnet run --project src/AEngine.Util -- card info rpg.png                # inspect a card without unpacking
dotnet run --project src/AEngine.Cli -- --debug-api   # also serve the debug REST API
dotnet run --project src/AEngine.Cli -- --llm-endpoint http://127.0.0.1:5001 --llm-model NAME   # LLM planning
cd client && npm install && npm run dev   # debug web client (needs the CLI with --debug-api)
```

Targets **net10.0**. The SDK is .NET 10; only the 10.0 runtime is installed, so do
not retarget to net8.0 without installing its runtime.

## Layout

```
src/AEngine.Core/         # engine: World/, Modules/, Actions/, Signals/, Policies/, Runtime/, Scenarios/
src/AEngine.Cli/          # text-first console REPL (slash commands, LLM planning, real-time mode)
src/AEngine.DebugServer/  # debug REST API (System.Net.HttpListener, loopback only)
src/AEngine.Util/         # developer utilities (card pack/unpack for image scenarios)
src/AEngine.Llm/          # LLM harness: client, planner, parser, executor, LlmPolicy, Narrator
client/                   # debug web client (Vue 3 + Vite + TypeScript, vue-only dep)
scenarios/mvp/            # MVP scenario: two rooms, locked door, key in a drawer
scenarios/npc/            # NPC demo: kitchen/dining hall, auto-policy cook
scenarios/rpg/            # RPG systems demo: dueling arena, stats, combat, grappling, body parts
scenarios/nail/           # full quest: barter, stealth/combat paths, persuasion-gated ritual, game over
tests/AEngine.Tests/      # xUnit, includes scripted-playthrough integration test
docs/                     # deep-dive documentation (see below)
```

## Documentation map

Keep `AGENTS.md` high-level. The details live in `docs/` — consult them when
working in that area, and update them when the behavior changes:

- `docs/architecture.md` — world model, modules, actions, speech, signals,
  posture, clothing, reactions, policies, agent memory, runtime, scenarios
- `docs/rpg-systems.md` — the staged opt-in RPG mechanics (checks, combat,
  grappling, body parts, crunch levels)
- `docs/llm.md` — LLM client/planner/parser/executor, NPC policy, narration
- `docs/cli.md` — REPL rendering model, ConsolePrompt, slash commands, real-time
- `docs/debug-api.md` — debug REST API endpoints and the web client

## Architecture in one paragraph each

- **World model** — `WorldObject`s in a single tree rooted at `world`; objects
  exist once and share state by id reference (two `portal` door-sides share one
  `doorstate` object). `World` supports full runtime mutation (create/destroy/
  move/modules/fields), safe mid-game.
- **Modules** — data-driven composable types: typed fields (per-object override
  → module default) plus affordances (`verb`, `handler` id, signals, duration,
  posture gates). The registry supports runtime register/update/unregister.
- **Actions** — affordances resolve handler **string ids** through a replaceable
  `HandlerRegistry` (the extension seam). Results are three-valued
  (Success/Noop/Failure; noops consume no turn). The resolver lists actions
  filtered by *observable* state; `ResolvePotential` skips that filtering.
- **Signals** — ephemeral visual/audible observations delivered to per-agent
  queues (and agent memory), with room-granular propagation gated by portal-side
  transmission fields; templates format `{agent}`/`{target}`/`{arg}`/`{container}`
  placeholders.
- **Posture & clothing** — containment-derived postures (sit/lie/prone/carried)
  gate affordances via data; garments wear onto data-driven body regions.
- **RPG systems** — staged, opt-in modules (stats/checks → opposed checks →
  health → combat → grappling → body parts + crunch levels); scenarios that
  don't reference them are unaffected. See `docs/rpg-systems.md`.
- **Reactions** — affordances can telegraph and park while the target agent
  picks a response (dodge/block/parry…), with data-driven defaults and policy
  support. See `docs/architecture.md`.
- **Policies** — agents with `agent.policy != "player"` act autonomously through
  the same affordances via `IAgentPolicy` resolved through `PolicyRegistry`
  (built-ins `random`, `auto`; `llm` in AEngine.Llm).
- **LLM harness** — free-text player planning, LLM NPC policy, and room
  narration, all through one OpenAI-compatible client abstraction. See
  `docs/llm.md`.
- **Runtime** — `GameEngine` + `TurnManager` run turn-based and real-time modes;
  actions leave the actor busy for their data-driven duration. `Scheduler`
  exists for long-running actions (unused so far).
- **Debug tooling** — loopback-only REST API (off by default) plus a Vue web
  client. See `docs/debug-api.md`.

## Conventions

- System.Text.Json only; no third-party runtime dependencies — the debug server
  uses `System.Net.HttpListener` from the base class library (no ASP.NET).
- New world behavior goes in data (modules/scenarios) first; new *verbs* get an
  `IActionHandler` registered in `HandlerRegistry`, not hardcoded branches.
- Concurrent world access (e.g. from the debug HTTP server) must take
  `lock (engine.SyncRoot)`; `TurnManager` already does.
- Tests are required for engine changes; the MVP playthrough
  (`tests/AEngine.Tests/MvpPlaythroughTests.cs`) must stay green.
- Never commit without asking first. The user prefers to playtest changes
  before they are committed — present the change, wait for an explicit
  "commit" instruction. (Automated tests passing is not a substitute.)
- Commit logical changes separately: one commit per feature/fix, staged
  explicitly by path — never batch unrelated changes into a single commit
  (`git add -A` after a long multi-feature session is how that happens).
- Do not commit build artifacts (`bin/`, `obj/`, `*.dll`) — see `.gitignore`.

## Future goals (NOT yet implemented — don't assume these exist)

- **LLM integration** — the harness is live (planning, NPC policy, room and
  event narration; see `docs/llm.md`). Still planned: guided world
  expansion for *open* scenarios, speech variants (Shout/Whisper), provider
  config files, streaming, retries/backoff.
- **Autonomous agents** — NPCs act through policies; planned: perception-driven
  policies (the random policy ignores signals), agenda-driven NPCs,
  multi-player (multiple players controlling different agents).
- **Real-time mode** — implemented in the CLI (see `docs/cli.md`); planned:
  pacing the player's own multi-step plans by action duration, multi-player.
- **Custom conflict/skill handlers** — the RPG stages cover the first cases;
  custom handlers still register into `HandlerRegistry` and wire from data.
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
