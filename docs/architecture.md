# Architecture deep dive

The engine's core systems in detail. For a high-level map see the
repository-root `AGENTS.md`; RPG systems have their own doc
(`docs/rpg-systems.md`), as do the LLM harness (`docs/llm.md`), the CLI
(`docs/cli.md`), and the debug tooling (`docs/debug-api.md`).

## World model

`WorldObject`s form a single tree rooted at `world`; a flat id→object index
lives in `World`. Objects exist exactly once. Cross-cutting state (e.g. a
door's open/locked flags) lives in its own object and is referenced **by
id** from other objects (e.g. two `portal` door-sides share one `doorstate`
object via their `stateRef` field). Attribute values are `JsonElement`; a
string holding another object's id is a reference by convention (`ref`-typed
module fields).

**Runtime mutation** — `World` exposes `CreateObject`, `DestroyObject`
(recursive), `MoveObject` (cycle-checked), `AddModule`, `RemoveModule`,
`SetAttribute`, `SetFieldOverride`. All are safe to call mid-game.

## Modules

Composable, data-driven types (`scenarios/mvp/modules.json`):
`{ id, name, fields: [{name, type, default}], affordances: [{verb, handler,
prompt?, signals?, duration?, repeatBackoff?, repeatBackoffCap?, postures?,
sameSupport?}] }`. Field types: `string | int | bool | ref | list` (list =
string array; tolerates a comma-separated string). Field resolution:
per-object override → module default. `ModuleRegistry` supports
register/update/unregister at runtime. An affordance's optional `prompt`
marks the verb as taking a free-text argument (surfaced to the handler as
`ActionContext.Args["text"]`); its optional `signals` list declares the
sensory signals emitted on success; its optional `duration` (seconds/turns,
default 1) is how long the action takes — the actor is busy for that many
turns. Handlers may override the duration dynamically via
`ActionResult.Duration` — `say` scales with the length of the speech
(rules module `sayBaseSeconds` + `sayMillisPerChar`, defaults 2s +
100ms/char, so a 60-char sentence takes ~8s — slow enough that listeners
have time to respond before the speaker acts again). Busy time is tracked
on two independent tracks: the **action track** (everything; gates NPC
policy selection) and the **speech track** (affordances with
`speech: true`, e.g. say) — talking paces itself without blocking
movement or attacks, so an agent can travel or fight mid-monologue.
Idle verbs (look, wait)
carry `repeatBackoff`: each consecutive repeat doubles the duration up to
`repeatBackoffCap` (default 30), so a bored agent idles instead of
thrashing its policy with LLM calls; the backoff is interruptible — a
busy-but-idle agent wakes early when new signals arrive.

## Actions

Module affordances name a `handler` **string id**, resolved through
`HandlerRegistry` (handlers are replaceable at runtime — this is the
extension seam). Built-ins: basic (flavor verbs, interpolates the verb into
its message), look, go, open, close, take, drop, put, give, unlock, lock,
pick, inventory, say, wait (`wait` just passes the turn; it is quiet — no
signals), sit, lie, stand, wear, remove, shove, steal, examine, trade,
ritual. An affordance may declare a `label` to override the verb-generated
menu text (`{target}` substitutes the target's name verbatim) — for
phrasing the verb can't produce, like "Ask the sorcerer to remove the
dragon-mark". `trade` is the barter verb: the affordance lives on a `ware`
item another agent holds (the resolver's held-by-other allowlist covers
it), and the handler swaps it for the item id named by the ware's `wants`
field. When the actor lacks the wanted item the holder refuses: with a
`refusal` line on the ware this is real speech — the holder says it
aloud (audible to the room and remembered by the holder) while the actor
gets "You try to barter for..."; without one, a generic message names
the wanted item.
`ritual` is a requirements-gated service on the host's `ritual` module:
required item ids (held by host or supplicant), consumed items (destroyed),
modules removed from the supplicant, an `epilogue`, and `endsGame`. It runs
in both directions — the supplicant-facing "ask" (actor = supplicant,
target = host) and a performer-facing one (actor = host, target =
supplicant) — via two affordance targeting flags: `othersOnly` (never
offered against the module's own owner — the sorcerer does not "ask the
sorcerer") and `targetOthers` (emitted from the agent's own modules, one
entry per other agent present — "Perform the unbinding rite on {target}").
`inventory` splits belongings into wearing/carrying, plus
"You bear:" for inalienable (non-portable, non-bodypart) objects like a
curse-brand. `put` and
`give` are **two-object verbs**: the resolver emits one entry per
(item × open container) / (held item × other agent) — "Put the key into
the desk drawer", "Give the bread to the old cook" — with the item riding
as the action's `AuxTargetId` (the container/recipient stays `TargetId`,
so give's accept/decline reaction finds the recipient; `put` respects open
state and the container's `capacity`). `examine`
is universal: the resolver offers it for every visible object (room
contents, agents and their pockets, open containers' contents, inventory)
with no module of its own (moduleId "" — no signals, default duration, no
check); its handler shows the full description, an agent's worn/carried
items and posture, an openable's state and an open container's contents —
but never lock state. Carried agents can't examine. `ActionResolver`
enumerates the actions currently available to an agent as structured
`(verb, target, label, handlerId, moduleId, prompt?)` entries. Listings are
filtered by **observable** state: `open`/`close` follow the visible open
state, take/drop follow held — while `unlock`/`lock` are always listed
since lock state is not observable. `ResolvePotential` returns the same set
without open/close state filtering, so a generated-but-redundant plan line
still resolves (and noops at runtime). Results are three-valued
(`ActionOutcome.Success | Noop | Failure`): redundant attempts whose end
state already holds are **noops** (no turn consumed, no signals, not a
failure — plan executors skip over them), wrong-state/missing-key attempts
are failures (turn consumed). `look` exits show `open`/`closed` only
(never "locked"). `Perception` (Core/Actions) is the shared
observable-rendering helper used by `look`, `open`, and the LLM context
builder: openables get ` (closed)` / ` (open)` annotations and an open
container's contents list as separate entries ("You see: desk drawer
(open), brass key (in desk drawer)"); `open` reports contents ("… There is
a brass key inside.").

## Speech

`say` is an affordance of the `can_speak` module (agents without it can't
speak), only ever offered from the agent's own modules. Its label is
parameterized: `Say: {speech}`, or `Say [to <name>]: {speech}` per
addressee when several other agents are in the room. Plan parsing is
generous ("Say [to X]: \"...\"", quotes optional, colon optional, and the
speech-first paraphrase `Say: "..." to X` — the trailing addressee is only
recognized with quoted speech, where the closing quote disambiguates it
from the utterance); the
parsed speech rides `AvailableAction.Text` into `Args["text"]`. Naming is
**observer-relative**: every agent is the protagonist of their own
perception (`Perception.NameFor` renders self as "you"); scenario data
gives agents descriptive names (the player object is "the guest", not
"you") so other agents' contexts and signals read correctly.

## Signals

Ephemeral sensory observations (`SignalSense.Visual | Audible`) delivered
by `SignalBus` on `GameEngine` into per-agent in-memory queues
(`Emit`/`Drain`/`Peek`), plus private sensations via `SendTo`: an object
with the `ambient` module periodically sends one of its `texts` variants
to the agent holding it — a cursed mark burning. The delay comes from
the `interval` spec, either a fixed number of seconds or
`{ "min": n, "max": n }` (uniform random, re-rolled per emission), and
tracks time actually passing: real-time ticks advance all timers, while
in turn-based mode each action advances only the acting agent's own held
objects by the action's duration — so NPC count doesn't speed up the
player's emissions. The timer only runs while an agent holds the object.
Ambient texts are authored
second-person for the holder and do not propagate. Location is
room-granular: `World.RoomOf` walks
the parent chain to the nearest `room` module, so a carried agent (or one
inside a container) acts from and observes in the carrier's room — a
carried NPC's speech reaches the player holding it. After a successful
action, `TurnManager.PerformAction` looks up the affordance's signal specs
and each observing agent (any object with the `agent` module except the
actor) receives the single highest-priority receivable signal (ties: first
listed); texts format `{agent}`/`{target}`/`{arg}` placeholders plus
`{container}` (" from the cupboard" when the target was taken out of a
non-room, non-agent holder, empty otherwise) and `{item}` (a two-object
verb's aux target — the gift for give, the stowed item for put; empty for
one-object verbs), collapsing doubled articles when a template's own "the"
meets a name that already carries one ("opens the the strongbox" → "opens
the strongbox"). Formatting is observer-relative: when the observer IS the
target, `{target}` renders as "you" ("the old cook gives the bread to
you"), and at sentence start the verb after it drops its third-person -s
("{target} declines" → "you decline"). Propagation: same room → all
senses; one portal away → a sense passes only if the portal **side in the
origin room** transmits it (`portal` fields `transmitVisual`/
`transmitAudio`: `always | whenOpen | never`, defaults `whenOpen`/`always`;
`whenOpen` reads the shared doorstate via that side's own `stateRef`);
farther rooms get nothing. One-way propagation (e.g. a one-way mirror) is
pure data on the two sides. Signals delivered through a portal get a
directional suffix naming the side in the observer's room ("… through the
wooden door to the south." — vertical portals read "to the floor
above/below", not the ungrammatical "to the up/down"), suppressed when the signal's target is that
same door ("the cook opens the wooden door."), and are flagged
`ThroughPortal` (traversal reports too): the CLI keeps the
"You see: …"/"You hear: …" framing only for signals that crossed a portal —
same-room events print bare and capitalized ("The cook opens the
cupboard."). An action targeting a portal manifests on both sides of the
door: observers in the other side's room perceive it as a same-room event
(visual included), transmission rules notwithstanding. Signal specs have a
`scope`: unset = normal propagation; `departure`/`arrival` are delivered
only on portal traversal (a successful `go`) to observers in the room left
/ the room entered, with `{exitPortal}`/`{exitDirection}`/`{entryPortal}`/
`{entryDirection}` placeholders ("the cook exits through the wooden door to
the south." / "the cook enters from the wooden door to the north.").

## Posture

Sitting, lying, prone, and being carried: `Postures.Of` (Core/Actions)
derives it — parent is an agent → `carried`; parent is furniture → the
`agent` module's `posture` field (`sitting`/`lying`, set by the sit/lie
handlers, cleared by stand/take/drop) so a bed can offer both; in a room
the field also applies (`standing`, or `prone` after being shoved). Getting
on/off furniture is ordinary affordances on composable modules — `sittable`
(`sit`, and `stand` gated to `postures: ["sitting"]`) and `lyable` (`lie`,
and `stand` gated to `["lying"]`), each with a `capacity` field; both
modules carry their own `stand` so a lyable-only mat never traps anyone,
and the posture gates guarantee only one `stand` is ever listed. Action
compatibility is authored **on the affordance**: `postures` is an
allow-list (absent = any posture; `go` declares `["standing"]` — you must
stand up before leaving) and `sameSupport: true` requires the target to
share the agent's parent (cuddle a bed-mate, not someone on a chair).
Everything else keeps same-room reach while seated (open the drawer, read
the book) with zero extra authoring. `ActionResolver` enforces the rules in
both `Resolve` and `ResolvePotential`, so the CLI menu, LLM planner, NPC
validation, and debug API all inherit them; furniture occupants are scanned
as action targets and say-addressees (grandchildren of the room). While
`carried`, only the agent's own verbs (look/inventory/wait/say) are
offered — no escape until the carrier drops them. Perception renders
posture everywhere: look and the LLM context open with "You are sitting on
the chair." / "You are being carried by the guest.", and room listings show
occupants container-style: "the old cook (sitting on the chair)".

## Clothing

Garments have the `wearable` module (`regions`: the body regions they
occupy; `worn` flag) and are **worn as children of the agent** (same
containment as inventory) with `worn: true`. `wear`/`remove` handlers
(`Clothing` helper in Core/Actions): wearing requires holding the garment
and a `body` module whose `regions` list covers the garment's (no body, no
wearing — a horse has `back`, not `top`); at most one worn garment per
region (conflict = region-set intersection, so layering is the author's
choice of region names — shirt `["top"]`, coat `["outer"]`, armor
`["top","bottom"]`; sizes are expressible the same way, e.g. `giant_top`).
`drop` refuses worn items. `remove` on a garment worn by *another* agent is
an opposed pull (see `docs/rpg-systems.md` stage 2); labels name the holder
("Take off the blue jeans from the arena duelist", "Steal the dagger from
Bob") so the LLM planner and the player can tell whose item it is. Room
listings stay
compact; `look` (and the LLM context) adds a dressed line per *other*
agent ("the old cook is wearing an apron." — your own outfit stays off
the room description), and `inventory` splits "You are wearing: …" from
"You are carrying: …".

## Reactions (quick-time events)

An affordance can declare a `reaction` spec (`window` in game seconds,
`telegraph` signal text, `options`): when the action targets a
non-incapacitated agent and ≥2 options survive availability filtering
(`requiresWornModule` — block needs a worn `shield`, parry a worn
`weapon`), `PerformAction` parks it: the telegraph is observable, the actor
is committed (busy, turn spent), and the defender picks a response before
the check/handler resolve (`ReactionManager` on `GameEngine`). At
resolution the actor's eligibility is re-validated (`ResolvePotential`) —
an actor incapacitated, knocked prone, or grabbed during the window fizzles
("The moment passes."), mirroring how stale NPC policy choices are
discarded. An option's `stat`/`skill`/`bonus` replace the defender's side
of the opposed check (gate and handler-rolled alike, via
`Checks.EvaluateOpposed`'s reaction parameter and `ActionContext.Reaction`);
`noResist` accepts the action. An option's `report` is the actor-facing
line for the choice ("{agent} attempts to dodge." — {agent} is the
reacting defender, {target} the actor rendered "you";
sentence-capitalized): it's recorded to the actor's memory and shown ahead
of the outcome message, since signals never reach the actor and the choice
would otherwise be invisible to them. Unset = quiet. The `default` option
applies at the deadline (`AdvanceTurn` → `ExpireDue`); resist-by-default
for attacks, accept-by-default for positive actions (hug). A defender's
last explicit choice per (verb, actor) is remembered (`EffectiveDefault`)
and becomes the effective default while it's still available — pick parry
once and it stays your default until it's impossible. NPC defenders choose
through their policy (`IAgentPolicy.ChooseReactionAsync` — random picks
uniformly, LLM picks in character; synchronous policies resolve at park
time). Player UX in the CLI: real-time mode shows a status line above the
input ("… — F2 to react (default: Dodge, 2s)") and F2 opens a modal popup;
turn-based mode prompts inline after NPC turns. The rpg scenario wires
reactions for attack (dodge/block/parry/accept), grapple and remove
(resist/accept), and hug (push away/accept); a `wooden shield` on the
`held_off_hand` body region enables block next to a `held` weapon.

## Policies & NPC turns

Agents with `agent.policy != "player"` are autonomous.
`IAgentPolicy.ChooseActionAsync` picks one of the resolved actions;
policies resolve by string id through `PolicyRegistry` (replaceable at
runtime — the LLM-policy seam, mirroring `HandlerRegistry`). The built-in
`random` policy picks uniformly via `GameEngine.Random` (settable; seed it
in tests) and supplies canned phrases for `say`. The built-in `auto` policy
delegates to `llm` when that policy is registered (the CLI registers it
when `--llm-endpoint` is set) and to `random` otherwise.
`TurnManager.RunNpcTurns()` runs an async-ready pipeline per NPC: start the
selection and skip the turn → skip while the task is in flight → when
complete, re-resolve and execute only if the chosen `(verb, targetId)` is
still available (stale choices are discarded). The CLI calls
`RunNpcTurns()` after each player action and prints the player's drained
signals.

## Agent memory

`AgentMemory` (Core/Runtime) keeps a bounded per-agent log of recent
events: signals the agent observed (recorded by `SignalBus` at delivery)
and the results of its own actions (recorded by `TurnManager.PerformAction`;
`look` is stored compactly as "You look around."). Capacity is data-driven
via the `agent` module's `memoryLength` field (default 25). Two anti-bloat
rules keep the log informative under idling: consecutive duplicate entries
collapse to one ("You wait." × 17 is one fact), and state snapshots — look
and examine results — are recorded under a snapshot key that supersedes the
previous snapshot of the same subject, so only the freshest rendering
survives (an NPC who examines you three times remembers one block, not
three). NPC LLM contexts render it as "Recent observations and actions
(oldest first)" for continuity across plans and conversations.

## Runtime

`GameEngine` ties everything together; `TurnManager` runs both time modes:
turn-based (each action advances the turn) and real-time (the CLI's
per-second timer calls `TurnManager.Tick()`, and NPC turns are driven by
the timer instead of player input). Turn-consuming actions leave the actor
**busy** for their affordance's data-driven `duration` (seconds/turns,
default 1); busy NPCs skip their turns. `Scheduler` is a wake-up queue for
long-running actions (nothing schedules multi-turn actions yet).
**Game over**: a handler result flagged `EndsGame` records its message on
`engine.GameOver` (NPC turns stop once set); the CLI prints it as the
ending and exits. The CLI also ends the game with the scenario's root
`defeatText` when the player is incapacitated. **Level of detail**:
`RunNpcTurns` throttles agents no player can perceive — full rate in a
player's room and adjacent (portal-linked) rooms, otherwise new work
starts only every `npcLodFactor` turns (rules module, default 10, 1
disables), staggered per agent id; in-flight policy decisions always
finish.

## Scenarios

JSON files defining modules and an initial world tree; `ScenarioLoader`
composes multiple documents in order (later overrides by id). Packaging is
pluggable via `IScenarioSource` (registered in `ScenarioSources`): a source
turns a path into the raw JSON documents, and the loader merges them.
Built-in sources: a directory holding `modules.json`/`world.json`; a zip
archive (recognized by the "PK" magic bytes, so any extension — `.zip`,
`.scen` — works) holding those files at any depth; and an image card —
PNG (zlib-compressed zTXt chunk; the scenario title rides in a standard
tEXt "Title" chunk) or JPEG (COM segments, chunked under the 64KB limit)
carrying the documents in its metadata (`ImageCard`). Cards are packed and
unpacked with AEngine.Util (`card pack`/`card unpack`), which copies the
image bytes verbatim — no recompression — and writes the folder's
`card.png`/`card.jpeg` metadata-stripped so the JSON files stay the source
of truth (`ScenarioCard`).
