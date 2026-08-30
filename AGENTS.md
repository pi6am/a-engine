# a-engine — Dynamic Text Adventure Engine

A data-driven text adventure engine and runtime, inspired by z-engine but with no
virtual machine: runtime behavior is driven by an extensible library of runners and
handlers. Built in stages; stage 1 (core + MVP scenario) and the LLM harness
stage (player free-text planning + LLM-driven NPC policy) are complete.

## Build & Test

```bash
dotnet build          # build the solution (a-engine.slnx, .NET 10 XML format)
dotnet test           # run all xUnit tests
dotnet run --project src/AEngine.Cli   # play the MVP scenario (text-first REPL)
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
src/AEngine.Cli/          # text-first console REPL (slash commands, optional LLM free-text
                          # planning, --real-time mode; the full room description prints
                          # only on arrival — tracked across the main loop and the
                          # real-time timer — while action results and observations print
                          # as they happen; ConsolePrompt does per-key line
                          # editing — cursor movement, mid-line insert, up/down history,
                          # slash-command completion popup with tab — and redraws the
                          # input line around background output). Output toggles live in
                          # OutputSettings, driven by slash commands: /showplan on|off
                          # (log the extracted LLM plan, default off), /narrate
                          # all|room|actions|off (placeholder for LLM narration)
src/AEngine.DebugServer/  # debug REST API (System.Net.HttpListener, loopback only)
src/AEngine.Llm/          # LLM harness: OpenAI-compatible client, planner, parser, executor, LlmPolicy
client/                   # debug web client (Vue 3 + Vite + TypeScript, vue-only dep)
scenarios/mvp/            # MVP scenario: modules.json + world.json
scenarios/npc/            # NPC demo: kitchen/dining hall, auto-policy cook, signals,
                          # sittable chairs, wearable clothing (apron, chef's hat)
scenarios/rpg/            # RPG systems demo: dueling arena + armory, stats/skills,
                          # check-gated lockpicking (strongbox with a rare rapier)
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
  prompt?, signals?, duration?, repeatBackoff?, repeatBackoffCap?, postures?,
  sameSupport?}] }`. Field
  types: `string | int | bool | ref | list` (list = string array; tolerates a
  comma-separated string). Field resolution: per-object override →
  module default. `ModuleRegistry`
  supports register/update/unregister at runtime. An affordance's optional
  `prompt` marks the verb as taking a free-text argument (surfaced to the
  handler as `ActionContext.Args["text"]`); its optional `signals` list
  declares the sensory signals emitted on success; its optional `duration`
  (seconds/turns, default 1) is how long the action takes — the actor is
  busy for that many turns. Handlers may override the duration dynamically
  via `ActionResult.Duration` — `say` scales with the length of the speech
  (1s + 0.05s/char, so a 60-char sentence takes ~4s). Idle verbs (look,
  wait) carry `repeatBackoff`: each consecutive repeat doubles the duration
  up to `repeatBackoffCap` (default 30), so a bored agent idles instead of
  thrashing its policy with LLM calls; the backoff is interruptible — a
  busy-but-idle agent wakes early when new signals arrive.
- **Actions** — module affordances name a `handler` **string id**, resolved through
  `HandlerRegistry` (handlers are replaceable at runtime — this is the extension
  seam). Built-ins: basic (flavor verbs, interpolates the verb into its
  message), look, go, open, close, take, drop, unlock, lock, pick, inventory,
  say, wait (`wait` just passes the turn; it is quiet — no signals), sit, lie,
  stand, wear, remove, shove, steal, examine. `examine` is universal: the
  resolver offers it for every visible object (room contents, agents and
  their pockets, open containers' contents, inventory) with no module of its
  own (moduleId "" — no signals, default duration, no check); its handler
  shows the full description, an agent's worn/carried items and posture, an
  openable's state and an open container's contents — but never lock state.
  Carried agents can't examine. `ActionResolver` enumerates the actions currently available to an agent as
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
  `Perception` (Core/Actions) is the shared observable-rendering helper used by
  `look`, `open`, and the LLM context builder: openables get ` (closed)` /
  ` (open)` annotations and an open container's contents list as separate
  entries ("You see: desk drawer (open), brass key (in desk drawer)"); `open`
  reports contents ("… There is a brass key inside.").
- **Speech** — `say` is an affordance of the `can_speak` module (agents without
  it can't speak), only ever offered from the agent's own modules. Its label is
  parameterized: `Say: {speech}`, or `Say [to <name>]: {speech}` per addressee
  when several other agents are in the room. Plan parsing is generous
  ("Say [to X]: \"...\"", quotes optional, colon optional); the parsed speech
  rides `AvailableAction.Text` into `Args["text"]`. Naming is
  **observer-relative**: every agent is the protagonist of their own perception
  (`Perception.NameFor` renders self as "you"); scenario data gives agents
  descriptive names (the player object is "the guest", not "you") so other
  agents' contexts and signals read correctly.
- **Signals** — ephemeral sensory observations (`SignalSense.Visual | Audible`)
  delivered by `SignalBus` on `GameEngine` into per-agent in-memory queues
  (`Emit`/`Drain`/`Peek`). Location is room-granular: `World.RoomOf` walks the
  parent chain to the nearest `room` module, so a carried agent (or one inside
  a container) acts from and observes in the carrier's room — a carried NPC's
  speech reaches the player holding it. After a successful action, `TurnManager.PerformAction`
  looks up the affordance's signal specs and each observing agent (any object
  with the `agent` module except the actor) receives the single highest-priority
  receivable signal (ties: first listed); texts format `{agent}`/`{target}`/
  `{arg}` placeholders, collapsing doubled articles when a template's own
  "the" meets a name that already carries one ("opens the the strongbox" →
  "opens the strongbox"). Propagation: same room → all senses; one portal away →
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
- **Posture** — sitting, lying, prone, and being carried: `Postures.Of`
  (Core/Actions) derives it — parent is an agent → `carried`; parent is
  furniture → the `agent` module's `posture` field (`sitting`/`lying`, set
  by the sit/lie handlers, cleared by stand/take/drop) so a bed can offer
  both; in a room the field also applies (`standing`, or `prone` after
  being shoved). Getting
  on/off furniture is ordinary affordances on composable modules —
  `sittable` (`sit`, and `stand` gated to `postures: ["sitting"]`) and
  `lyable` (`lie`, and `stand` gated to `["lying"]`), each with a
  `capacity` field; both modules carry their own `stand` so a lyable-only
  mat never traps anyone, and the posture gates guarantee only one `stand`
  is ever listed. Action compatibility is authored **on the affordance**:
  `postures` is an allow-list (absent = any posture; `go` declares
  `["standing"]` — you must stand up before leaving) and `sameSupport:
  true` requires the target to share the agent's parent (cuddle a bed-mate,
  not someone on a chair). Everything else keeps same-room reach while
  seated (open the drawer, read the book) with zero extra authoring.
  `ActionResolver` enforces the rules in both `Resolve` and
  `ResolvePotential`, so the CLI menu, LLM planner, NPC validation, and
  debug API all inherit them; furniture occupants are scanned as action
  targets and say-addressees (grandchildren of the room). While `carried`,
  only the agent's own verbs (look/inventory/wait/say) are offered — no
  escape until the carrier drops them. Perception renders posture
  everywhere: look and the LLM context open with "You are sitting on the
  chair." / "You are being carried by the guest.", and room listings show
  occupants container-style: "the old cook (sitting on the chair)".
- **Clothing** — garments have the `wearable` module (`regions`: the body
  regions they occupy; `worn` flag) and are **worn as children of the
  agent** (same containment as inventory) with `worn: true`. `wear`/`remove`
  handlers (`Clothing` helper in Core/Actions): wearing requires holding the
  garment and a `body` module whose `regions` list covers the garment's
  (no body, no wearing — a horse has `back`, not `top`); at most one worn
  garment per region (conflict = region-set intersection, so layering is
  the author's choice of region names — shirt `["top"]`, coat `["outer"]`,
  armor `["top","bottom"]`; sizes are expressible the same way, e.g.
  `giant_top`). `drop` refuses worn items. `remove` on a garment worn by
  *another* agent is an opposed pull (see RPG stage 2). Room listings stay compact;
  `look` (and the LLM context) adds a dressed line per agent ("the old cook
  is wearing an apron.") until an examine verb exists, and `inventory`
  splits "You are wearing: …" from "You are carrying: …".
- **RPG systems (staged, opt-in)** — being built stage by stage in
  `scenarios/rpg/` (dueling arena); simple scenarios never reference these
  modules. **Stage 1 (done):** stats/skills are map fields
  (`FieldType.Map`, string→int; `stats`/`skills` modules with a `values`
  map; undeclared names read as 0; `Stats.Get/Set` helpers). Affordances
  can declare a `check: { stat?, skill?, difficulty, failText? }` —
  `TurnManager.PerformAction` evaluates it before running the handler, so
  player plans, NPCs, and the debug API all respect it; a failed check
  consumes the turn, runs no handler, emits no signals. The dice formula
  is scenario data: a `rules` module (on the world root or a top-level
  object) sets `diceCount`/`diceSides` (default 1d20; 0d0 is diceless —
  used for deterministic tests); `Checks.Evaluate` returns the margin.
  The `pick` handler unlocks without a key once its check passes.
  **Stage 2 (done):** opposed checks (`check.opposed`: the defender — the
  target agent, or the agent holding the target item — rolls their own
  dice + stat/skill, actor must beat them; `check.failSignals` are emitted
  on failure so a botched pickpocketing rattles the victim). `prone` is a
  posture (stored on the agent even in a room; `Postures.Of` reads it) —
  `shove` (opposed, `shoveable` module) knocks a victim prone, and a
  self-targeted `stand` gated to `postures: ["prone"]` costs an action to
  get up (`go` already requires standing; prone agents show "(prone)" in
  room listings). `steal` (on the rpg scenario's portable module) takes an
  item from another agent's inventory, opposed by the holder's perception;
  the resolver scans other agents' pockets and restricts items held by
  another agent to steal-only — worn garments offer `remove` instead, an
  opposed pull rolled in the handler (combatant stats: strength/brawling
  vs agility) that lands the garment in the puller's inventory.
  **Stage 3 (done):** `health` module (`hp`/`maxHp`/`incapacitatedAt`,
  default threshold 0); `Damage.Apply` clamps hp at 0 and reports
  incapacitation once — a standing agent is knocked prone at the same
  moment (they crumple; seated/lying/carried agents stay where they are).
  An incapacitated agent can only `look` (resolver),
  gets no NPC turns, shows "(incapacitated)" in listings/examine, and
  offers no resistance to opposed checks (`Checks.EvaluateOpposed` treats
  their defense as 0 — robbing or stripping a downed foe auto-succeeds
  unless the check has a difficulty). No
  in-game damage source yet — combat lands next.
  **Stage 4 (done):** combat. `attackable` exposes `attack` (postures
  `["standing"]`); the attack handler rolls opposed in-code (the attacker's
  bonus depends on the wielded weapon — any worn `weapon`-module item;
  scenario data puts weapons on the `held` region so they stack with a
  glove on `hand` — else the `combatant` module's unarmed defaults); the
  defender's guard is their combatant `defenseStat`/`defenseSkill`
  (default agility). Damage is N + n d m (weapon `damageBonus`/
  `damageDice`/`damageSides`, e.g. greatsword 2d6) minus the defender's
  worn `armor.protection` total (region-scoped once the defender has body
  parts — stage 6), floored at 0; non-agent targets (training
  dummies) are auto-hit. Handlers roll on `ActionContext.Random` (the
  engine's seedable source). `failSignals` moved to the **affordance**
  level: any Failure result (gate or handler) emits them — a missed attack
  is observable ("{agent} swings at the {target} and misses."). The arena
  armory has a dagger (1d4), an arming sword (1d8), padded armor
  (protection 2), and the strongbox rapier (1d8+1).
  **Stage 5 (done):** grappling. `grappleable` exposes `grapple`
  (opposed, gated on the affordance): success hauls the victim into
  **forced carrying** — the carried-posture rules restrict them (own
  verbs only) plus `escape` from the agent-side `grappler` module, an
  opposed break-out rolled in the handler (its defender is the carrier,
  not the self-target; an incapacitated carrier can't hold anyone). The
  grappler gets `release` (set the victim down, standing) and `choke`
  (on `chokeable`) — a no-roll unarmed attack, combatant damage, armor
  ignored; victims choked unconscious stay in the grappler's grasp.
  **Stage 6 (done):** granular body parts and configurable crunch.
  A body part is a **child object** of the agent with a `bodypart` module
  (`region` — the wear region that armor must cover to protect it;
  `vital`; `crippleEffects`; `aimedPenalty` — the to-hit penalty for
  aiming at this part, default 4) plus its own `health` pool. Parts are
  anatomy, not items: the resolver and the inventory/examine/context
  listings skip them. Overall state derives from the parts (no global
  pool): a crippled vital part incapacitates, and the rules module's
  optional `shockThreshold` (percent of total part damage) incapacitates
  by accumulated trauma. Cripple effects are a small engine-known
  vocabulary fired on the >0 → 0 transition: `disarm`/`disarm_offhand`
  (the worn held-region item unwears and clatters to the floor), `prone`
  (they topple), `no_stand` (passive — the stand handler refuses).
  Attacks land on a part: aimed via the say-style free-text argument
  (label "Attack the arena duelist [in the {part}]" — parsed generously,
  verbatim `{part}` = unaimed, unknown part fails, an ambiguous side name
  ("arm") picks randomly among the matches, the part's own
  `bodypart.aimedPenalty` applies — arms 3, legs 4, head 6, torso 0 in
  the arena) or a uniform random part; armor soaks only when a worn garment
  covers the part's region. `choke` crushes the `chokeable` module's
  `part`. Agents without parts (the new training dummy, future slimes)
  keep the monolithic health pool, flat armor sum, and threshold
  incapacitation. The `rules` module's `crunch` field ("numeric" default
  | "descriptive") plus the `blowBands`/`conditionBands` map fields
  (label → percent, engine defaults glancing/solid/severe and slightly
  wounded/wounded/severely wounded) drive ALL damage and status reporting
  through one helper (`Condition` in Core/Actions): numeric attacks report
  "in the left arm … for 7 damage", and examine and inventory show the
  per-part fractions (pool fraction for part-less agents); descriptive
  categorizes blows by damage-vs-part-pool percent and shows condition
  words in room listings, examine, and inventory (nothing while unhurt).
- **Reactions (quick-time events)** — an affordance can declare a
  `reaction` spec (`window` in game seconds, `telegraph` signal text,
  `options`): when the action targets a non-incapacitated agent and ≥2
  options survive availability filtering (`requiresWornModule` — block
  needs a worn `shield`, parry a worn `weapon`), `PerformAction` parks
  it: the telegraph is observable, the actor is committed (busy, turn
  spent), and the defender picks a response before the check/handler
  resolve (`ReactionManager` on `GameEngine`). At resolution the actor's
  eligibility is re-validated (`ResolvePotential`) — an actor
  incapacitated, knocked prone, or grabbed during the window fizzles
  ("The moment passes."), mirroring how stale NPC policy choices are
  discarded. An option's
  `stat`/`skill`/`bonus` replace the defender's side of the opposed check
  (gate and handler-rolled alike, via `Checks.EvaluateOpposed`'s reaction
  parameter and `ActionContext.Reaction`); `noResist` accepts the action.
  An option's `report` is the actor-facing line for the choice ("{agent}
  attempts to dodge." — {agent} is the reacting defender, {target} the
  actor rendered "you"; sentence-capitalized): it's recorded to the
  actor's memory and shown ahead of the outcome message, since signals
  never reach the actor and the choice would otherwise be invisible to
  them. Unset = quiet.
  The `default` option applies at the deadline (`AdvanceTurn` →
  `ExpireDue`); resist-by-default for attacks, accept-by-default for
  positive actions (hug). A defender's last explicit choice per
  (verb, actor) is remembered (`EffectiveDefault`) and becomes the
  effective default while it's still available — pick parry once and it
  stays your default until it's impossible. NPC defenders choose through
  their policy
  (`IAgentPolicy.ChooseReactionAsync` — random picks uniformly, LLM picks
  in character; synchronous policies resolve at park time). Player UX in
  the CLI: real-time mode shows a status line above the input
  ("… — F2 to react (default: Dodge, 2s)") and F2 opens a modal popup;
  turn-based mode prompts inline after NPC turns. The rpg scenario wires
  reactions for attack (dodge/block/parry/accept), grapple and remove
  (resist/accept), and hug (push away/accept); a `wooden shield` on the
  `held_off_hand` body region enables block next to a `held` weapon.
- **Policies & NPC turns** — agents with `agent.policy != "player"` are
  autonomous. `IAgentPolicy.ChooseActionAsync` picks one of the resolved
  actions; policies resolve by string id through `PolicyRegistry` (replaceable
  at runtime — the LLM-policy seam, mirroring `HandlerRegistry`). The built-in
  `random` policy picks uniformly via `GameEngine.Random` (settable; seed it in
  tests) and supplies canned phrases for `say`. The built-in `auto` policy
  delegates to `llm` when that policy is registered (the CLI registers it when
  `--llm-endpoint` is set) and to `random` otherwise. `TurnManager.RunNpcTurns()`
  runs an async-ready pipeline per NPC: start the selection and skip the turn →
  skip while the task is in flight → when complete, re-resolve and execute only
  if the chosen `(verb, targetId)` is still available (stale choices are
  discarded). The CLI calls `RunNpcTurns()` after each player action and prints
  the player's drained signals as `You see: …`/`You hear: …` lines.
- **Agent memory** — `AgentMemory` (Core/Runtime) keeps a bounded per-agent
  log of recent events: signals the agent observed (recorded by `SignalBus`
  at delivery) and the results of its own actions (recorded by
  `TurnManager.PerformAction`; `look` is stored compactly as "You look
  around."). Capacity is data-driven via the `agent` module's
  `memoryLength` field (default 25). NPC LLM contexts render it as "Recent
  observations and actions (oldest first)" for continuity across plans and
  conversations.
- **LLM harness** (`src/AEngine.Llm`, no third-party deps) — one machinery
  serves both player free text and NPC decisions. `OpenAiCompatibleClient`
  POSTs `{BaseUrl}/v1/chat/completions` (OpenAI chat schema, optional Bearer
  key; works against KoboldCPP/llama.cpp, OpenRouter, Kimi, DeepSeek);
  `FakeLlmClient` queues canned responses for tests. `AgentContextBuilder`
  renders the **public** world view only (room, visible items with
  closed-container contents hidden, exits open/closed, inventory, action menu
  labels; NPC extras: `agent` module `character`/`goals` fields + the
  agent's memory of recent observations and actions). `LlmPlanner` builds the system/user prompts (output contract: one
  action per line, exactly as listed); `PlanParser` tolerantly strips
  numbering/bullets/prose (keeps lines starting with a known verb — the
  defaults union the scenario's currently available verbs, so RPG verbs
  like `attack` survive, and parameterized labels with filled-in arguments
  parse: "Attack the arena duelist in the head");
  `PlanExecutor` matches each line against **currently** available actions
  (case-insensitive label equality, then normalized containment) and stops on
  no-match or failure — conditional availability (unlock → open → go) resolves
  at execution time. `LlmPolicy` (id `llm`) asks for a full plan on first
  selection, caches steps, pops them matched against current availability; a
  stale step discards the plan remainder and re-plans next selection. New
  observed signals (anything pending in the agent's signal queue — it is
  drained into the context whenever a plan is made) interrupt the cached
  plan and trigger an immediate re-plan, so agents respond to being spoken
  to instead of carrying out a stale plan.
- **Runtime** — `GameEngine` ties everything together; `TurnManager` runs both
  time modes: turn-based (each action advances the turn) and real-time (the
  CLI's per-second timer calls `TurnManager.Tick()`, and NPC turns are driven
  by the timer instead of player input). Turn-consuming actions leave the
  actor **busy** for their affordance's data-driven `duration`
  (seconds/turns, default 1); busy NPCs skip their turns.
  `Scheduler` is a wake-up queue for long-running actions.
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
- Never commit without asking first. The user prefers to playtest changes
  before they are committed — present the change, wait for an explicit
  "commit" instruction. (Automated tests passing is not a substitute.)
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
  (menu numbers still work; input exactly matching an action label, e.g.
  "wait", runs directly without an LLM call). Live verification against a real server (e.g.
  KoboldCPP) is manual. Still planned: narration (LLM translating outcomes
  into prose — the CLI already has the /narrate all|room|actions|off
  setting, unwired), guided world expansion for *open* scenarios, speech variants
  (Shout/Whisper — need per-spec propagation overrides; say already carries
  free-text speech), provider config files, streaming, retries/backoff.
- **Autonomous agents** — partially implemented: NPCs with
  `agent.policy != "player"` act through the same affordances via
  `IAgentPolicy` (built-ins: `random` and `auto` — llm-if-available-else-random —
  in Core, `llm` in AEngine.Llm); see "Policies & NPC turns" above. Planned:
  perception-driven policies (the
  random policy ignores signals), agenda-driven NPCs, multi-player (multiple
  players controlling different agents).
- **Real-time mode** — implemented in the CLI: `--real-time` (or the
  `/realtime` slash command; `/turnbased` switches back) runs a per-second
  background timer that calls `TurnManager.Tick()` and `RunNpcTurns()` and
  prints the player's observed signals as they happen. Actions do not
  advance the turn themselves in this mode; the timer does. Observed
  events accumulate in the player's memory (capped at `memoryLength`), so
  plans made after watching events still know what happened. `/timescale N`
  scales the clock: each real second accumulates N game seconds (default
  1.0; action durations are unchanged — 0.5 makes a 2s action take 4s of
  real time). Planned:
  pacing the player's own multi-step plans by action duration (steps
  currently execute back-to-back), multi-player (several players,
  different agents).
- **Custom conflict/skill handlers** — partially implemented via the RPG
  stages (see the RPG systems bullet): lockpicking is check-gated now;
  pickpocket/combat adjudication land in stages 2–5. Custom handlers still
  register into `HandlerRegistry` and wire from data.
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
