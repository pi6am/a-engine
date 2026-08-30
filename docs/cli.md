# CLI (AEngine.Cli)

Text-first console REPL. `AEngine.Cli [scenarioDir] [--debug-api[=PORT]]
[--debug-port N] [--llm-endpoint URL] [--llm-model NAME] [--llm-api-key
KEY] [--real-time]`.

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

## Real-time mode

`--real-time` (or `/realtime`; `/turnbased` switches back) runs a
per-second background timer that calls `TurnManager.Tick()` and
`RunNpcTurns()` and prints the player's observed signals as they happen
(above the input line). Actions do not advance the turn themselves in this
mode; the timer does. Observed events accumulate in the player's memory
(capped at `memoryLength`), so plans made after watching events still know
what happened. `/timescale N` scales the clock: each real second
accumulates N game seconds (default 1.0; action durations are unchanged —
0.5 makes a 2s action take 4s of real time).

Planned: pacing the player's own multi-step plans by action duration (steps
currently execute back-to-back), multi-player.
