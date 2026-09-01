# CLI (AEngine.Cli)

Text-first console REPL. `AEngine.Cli [scenarioDirOrZip] [--debug-api[=PORT]]
[--debug-port N] [--llm-endpoint URL] [--llm-model NAME] [--llm-api-key
KEY] [--real-time]`. The scenario argument is a directory holding
`modules.json`/`world.json`, a zip archive containing them, or an image
card (PNG/JPEG with the documents in its metadata) — any extension, all
recognized by content.

## Rendering model

The full room description prints **only on arrival** — the player's room
(`World.RoomOf`) is tracked across the main loop and the real-time timer,
and the look render runs when it changes (including being carried elsewhere
while idle). While you stay put, only action results and observations
print; an explicit `look` reprints on demand.

Observed signals render via one helper: same-room events print bare and
capitalized ("The old cook opens the cupboard."); the "You see: …"/
"You hear: …" framing is reserved for signals that crossed a portal
(`Signal.ThroughPortal`).

Every player action outcome ends with a blank line, separating the
outcome log from the sensory log (or room arrival description) that
follows; the arrival print itself carries no leading blank.

Narrated prose (LLM narration, when enabled) is word-wrapped to
`min(Console.WindowWidth, 80)` columns at print time; raw template
output is left alone.

## ConsolePrompt

Per-key line editing — cursor movement, mid-line insert, up/down history,
slash-command completion popup with tab (filters as you type; ESC or
deleting the leading `/` closes it) — and redraws the input line around
background output. F2 opens the quick-time reaction popup in real-time
mode (announced on a status line).

## Slash commands

Slash commands are meta actions: they never consume game time or turns.
Registered in `SlashCommandRegistry` (extensible, with aliases and help
text): `/actions` (numbered action list), `/showplan on|off` (log the
extracted LLM plan, default off), `/narrate all|room|actions|off` (LLM
narration scope, see `docs/llm.md`), `/realtime` (`/rt`), `/turnbased`
(`/tb`), `/timescale N` (`/ts`), `/quit` (`/exit`), `/help`. Output
toggles live in `OutputSettings`.

## Auto mode (AI self-play)

`/auto` (or `/auto on`) hands the player character to the AI — the
`agent.policy` field flips between `player` and `auto` (llm with an
endpoint, random otherwise), so the character plans and acts exactly like
an NPC; ESC (or `/auto off`) restores control. A testing harness for
"can the LLM beat this scenario?"; the
scenario should give the player object diegetic `character`/`goals`
fields to play from (the nail scenario does — deliberately without a
walkthrough). The time mode is untouched: in real-time mode the world
clock drives the character like any NPC; in turn-based mode the prompt's
idle callback steps `RunNpcTurns` while the prompt shows "Auto mode:
press ESC to cancel" (input disabled; ESC or `/auto off` restores
control). Input-disabled also means headless runs work: with piped input,
auto mode just steps until the game ends. Spectator output prints live:
observed signals plus the character's own action results, which signals
never deliver to the actor — those ride a bounded per-agent queue in
`TurnManager` (`DrainOutcomes`; parked-action resolutions already flow
through `Reactions.DrainResolved`). The player's quick-time reactions go
through their policy like any NPC's (no inline prompt, no F2 banner).

## Real-time mode

`--real-time` (or `/realtime`; `/turnbased` switches back) runs a
per-second background timer that calls `TurnManager.Tick()` and
`RunNpcTurns()` and prints the player's observed signals as they happen
(above the input line). Actions do not advance the turn themselves in this
mode; the timer does. Observed events accumulate in the player's memory
(capped at `memoryLength`), so plans made after watching events still know
what happened. `/timescale N` scales the clock: each real second
accumulates N game seconds (default 1; action durations are unchanged —
0.5 makes a 2s action take 4s of real time; 0 pauses the world entirely —
no game time passes and NPCs take no turns, so it works as a read-the-
room button). If the world ends while the
player is idle (an NPC's rite, a defeat — anything that sets
`engine.GameOver` or incapacitates the player), the timer wakes the
blocked prompt (`ConsolePrompt.Wake()` — ReadLine polls `KeyAvailable`
rather than blocking on `ReadKey`), and the main loop prints the ending
through the same top-of-loop path as turn-based mode.

Planned: pacing the player's own multi-step plans by action duration (steps
currently execute back-to-back), multi-player.
