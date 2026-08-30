# RPG systems

Staged, opt-in RPG mechanics, developed in `scenarios/rpg/` (dueling
arena); simple scenarios never reference these modules. All data-driven —
the engine never hardcodes the set of stats, skills, or body regions.

## Stage 1: stats, skills, checks

Stats/skills are map fields (`FieldType.Map`, string→int; `stats`/`skills`
modules with a `values` map; undeclared names read as 0; `Stats.Get/Set`
helpers). Affordances can declare a `check: { stat?, skill?, difficulty,
failText? }` — `TurnManager.PerformAction` evaluates it before running the
handler, so player plans, NPCs, and the debug API all respect it; a failed
check consumes the turn, runs no handler, emits no signals. The dice
formula is scenario data: a `rules` module (on the world root or a
top-level object) sets `diceCount`/`diceSides` (default 1d20; 0d0 is
diceless — used for deterministic tests); `Checks.Evaluate` returns the
margin. The `pick` handler unlocks without a key once its check passes.

## Stage 2: opposed checks, prone, stealing

Opposed checks (`check.opposed`: the defender — the target agent, or the
agent holding the target item — rolls their own dice + stat/skill, actor
must beat them; `check.failSignals` are emitted on failure so a botched
pickpocketing rattles the victim). `prone` is a posture (stored on the
agent even in a room; `Postures.Of` reads it) — `shove` (opposed,
`shoveable` module) knocks a victim prone, and a self-targeted `stand`
gated to `postures: ["prone"]` costs an action to get up (`go` already
requires standing; prone agents show "(prone)" in room listings). `steal`
(on the rpg scenario's portable module) takes an item from another agent's
inventory, opposed by the holder's perception; the resolver scans other
agents' pockets and restricts items held by another agent to steal-only —
worn garments offer `remove` instead, an opposed pull rolled in the handler
(combatant stats: strength/brawling vs agility) that lands the garment in
the puller's inventory.

## Stage 3: health

`health` module (`hp`/`maxHp`/`incapacitatedAt`, default threshold 0);
`Damage.Apply` clamps hp at 0 and reports incapacitation once — a standing
agent is knocked prone at the same moment (they crumple; seated/lying/
carried agents stay where they are). An incapacitated agent can only `look`
(resolver), gets no NPC turns, shows "(incapacitated)" in listings/examine,
and offers no resistance to opposed checks (`Checks.EvaluateOpposed` treats
their defense as 0 — robbing or stripping a downed foe auto-succeeds unless
the check has a difficulty).

## Stage 4: combat

`attackable` exposes `attack` (postures `["standing"]`); the attack handler
rolls opposed in-code (the attacker's bonus depends on the wielded weapon —
any worn `weapon`-module item; scenario data puts weapons on the `held`
region so they stack with a glove on `hand` — else the `combatant` module's
unarmed defaults); the defender's guard is their combatant `defenseStat`/
`defenseSkill` (default agility). Damage is N + n d m (weapon
`damageBonus`/`damageDice`/`damageSides`, e.g. greatsword 2d6) minus the
defender's worn `armor.protection` total (region-scoped once the defender
has body parts — stage 6), floored at 0; non-agent targets (training
dummies) are auto-hit. Handlers roll on `ActionContext.Random` (the
engine's seedable source). `failSignals` live at the **affordance** level:
any Failure result (gate or handler) emits them — a missed attack is
observable ("{agent} swings at the {target} and misses."). The arena armory
has a dagger (1d4), an arming sword (1d8), padded armor (protection 2), and
the strongbox rapier (1d8+1).

## Stage 5: grappling

`grappleable` exposes `grapple` (opposed, gated on the affordance): success
hauls the victim into **forced carrying** — the carried-posture rules
restrict them (own verbs only) plus `escape` from the agent-side `grappler`
module, an opposed break-out rolled in the handler (its defender is the
carrier, not the self-target; an incapacitated carrier can't hold anyone).
The grappler gets `release` (set the victim down, standing) and `choke` (on
`chokeable`) — a no-roll unarmed attack, combatant damage, armor ignored;
victims choked unconscious stay in the grappler's grasp.

## Stage 6: granular body parts and configurable crunch

A body part is a **child object** of the agent with a `bodypart` module
(`region` — the wear region that armor must cover to protect it; `vital`;
`crippleEffects`; `aimedPenalty` — the to-hit penalty for aiming at this
part, default 4) plus its own `health` pool. Parts are anatomy, not items:
the resolver and the inventory/examine/context listings skip them. Overall
state derives from the parts (no global pool): a crippled vital part
incapacitates, and the rules module's optional `shockThreshold` (percent of
total part damage) incapacitates by accumulated trauma. Cripple effects are
a small engine-known vocabulary fired on the >0 → 0 transition:
`disarm`/`disarm_offhand` (the worn held-region item unwears and clatters
to the floor), `prone` (they topple), `no_stand` (passive — the stand
handler refuses). Attacks land on a part: aimed via the say-style free-text
argument (label "Attack the arena duelist [in the {part}]" — parsed
generously, verbatim `{part}` = unaimed, unknown part fails, an ambiguous
side name ("arm") picks randomly among the matches, the part's own
`bodypart.aimedPenalty` applies — arms 3, legs 4, head 6, torso 0 in the
arena) or a uniform random part; armor soaks only when a worn garment
covers the part's region. `choke` crushes the `chokeable` module's `part`.
Agents without parts (the training dummy, future slimes) keep the
monolithic health pool, flat armor sum, and threshold incapacitation. The
`rules` module's `crunch` field ("numeric" default | "descriptive") plus
the `blowBands`/`conditionBands` map fields (label → percent, engine
defaults glancing/solid/severe and slightly wounded/wounded/severely
wounded) drive ALL damage and status reporting through one helper
(`Condition` in Core/Actions): numeric attacks report "in the left arm …
for 7 damage", and examine and inventory show the per-part fractions (pool
fraction for part-less agents); descriptive categorizes blows by
damage-vs-part-pool percent and shows condition words in room listings,
examine, and inventory (nothing while unhurt).
