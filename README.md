# a-engine

A data-driven text adventure engine: worlds, items, and NPCs are JSON, and
every verb resolves through an extensible handler library. Agents, the
player and NPCs alike, act through the same affordances and perceive the
world through sensory signals. LLM-driven NPCs (and an LLM-driven player)
plug in through any OpenAI-compatible endpoint.

Requires the **.NET 10 SDK**.

## Run it

You can run any adventure without an LLM, but the experience is
limited. NPCs will act randomly or not at all and commands must
be typed exactly. Run it with `dotnet` like this:

```bash
dotnet run --project src/AEngine.Cli -- scenarios/mvp
```

Type action numbers, menu labels (`wait`, `go north`), or `/help`.
`/actions` lists what you can do right now.

## With an LLM

With an endpoint, the LLM will interpret your free-text input plans and
generate multi-step actions for your character. NPCs will also act
autonomously. Try a demo adventure with this command:

```bash
dotnet run --project src/AEngine.Cli -- scenarios/nail --llm-endpoint http://127.0.0.1:5001
```

- `--real-time`: time passes automatically. Use /realtime and /turnbased
  toggle at runtime.
- `--llm-endpoint`: any OpenAI-compatible server (`/v1/chat/completions`):
  KoboldCPP, llama.cpp, OpenRouter, DeepSeek, Kimi, ... Local servers work
  fine; use a model that follows instructions well.
- `--llm-model`: the model name the server expects.
- `--llm-api-key`: when the endpoint needs one (or set
  `AENGINE_LLM_ENDPOINT` / `AENGINE_LLM_MODEL` / `AENGINE_LLM_API_KEY`).

Then type naturally — `ask the dockhand about the sorcerer` — and watch
NPCs answer, argue, and remember you.

### Slash commands

There are a variety of slash commands you can use to affect the game. Here
are some of the most useful:

- `/auto`: the LLM plays your character until you press ESC.
- `/realtime`: Switch to realtime mode.
- `/turnbased`: Switch to turn based mode.
- `/timescale {number}`: Change the rate of time passing in realtime mode.
- `/narrate {all|room|actions|off}`: Turn on LLM-augmented narration.
- `/showplan`: Log how the LLM interprets your free-text input.
- `/quit` or `/exit`: Exit the game.

## Scenario cards

Instead of specifying a scenario folder like `scenarios/nail`, you can
pack a scenario into a png or jpeg image using the `card pack` command.

```bash
dotnet run --project src/AEngine.Util -- card pack scenario scenario.png -i cover.png
```

## More

- `docs/`: architecture deep-dives (world model, signals, RPG systems,
  LLM harness, CLI, debug tooling).
- Web inspector: add `--debug-api` and run the client in `client/`
  (`npm install && npm run dev`) for a live world tree, memory, and
  knowledge editor.
- Tests: `dotnet test`.

This is an early-stage project; expect rough edges. `AGENTS.md` maps
the codebase for contributors.
