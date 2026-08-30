# Debug tooling

## Debug REST API

`AEngine.DebugServer.DebugServer` serves the live world over HTTP for dev
tooling. Built on `System.Net.HttpListener` (base class library, zero extra
dependencies), bound to **loopback only**. Enable it in the CLI with
`--debug-api` (default port 5050), `--debug-api=PORT`, or `--debug-port N`.
**Off by default; unauthenticated — never expose it beyond localhost.**

Endpoints (JSON in/out, camelCase):

- `GET /api/health`
- `GET /api/engine` (time mode, current turn, pending scheduler entries)
- `GET /api/world/tree`
- `GET /api/objects`
- `GET /api/objects/{id}` (attributes + modules with resolved field values)
- `POST /api/objects` `{id, parentId, name?, description?}`
- `DELETE /api/objects/{id}` (recursive)
- `POST /api/objects/{id}/move` `{"parentId": "..."}`
- `PUT|DELETE /api/objects/{id}/attributes/{name}`
- `PUT|DELETE /api/objects/{id}/modules/{moduleId}`
- `PUT /api/objects/{id}/modules/{moduleId}/fields/{field}` (raw JSON value body)
- `GET /api/modules`
- `GET /api/actions?agentId=`
- `POST /api/actions/execute` `{agentId, verb, targetId}` → runs the resolved
  menu action through `TurnManager.PerformAction` (advances the turn), 200 →
  `{success, message, turn}`, unknown agent or unavailable action → 404

Errors: unknown id → 404, cycle/duplicate/root-guard → 409, bad JSON → 400,
wrong method → 405. Permissive CORS (any origin, OPTIONS preflight) for the
browser client. All world access (HTTP and REPL alike) is serialized on
`GameEngine.SyncRoot`.

## Debug web client

`client/` — Vue 3 + Vite + TypeScript, manually scaffolded (runtime dep:
`vue` only; dev: vite, @vitejs/plugin-vue, typescript, vue-tsc). Scripts:
`npm run dev` (proxies `/api` → `http://127.0.0.1:5050`), `npm run build`
(`vue-tsc --noEmit && vite build`), `npm run preview`. It expects the CLI
running with `--debug-api`; the API base URL is editable in the header
(default `http://127.0.0.1:5050`, persisted to localStorage). Views: world
tree, object editor (attributes, modules + field overrides,
move/delete/create child), engine panel, actions panel (execute via
`POST /api/actions/execute`). Manual refresh + optional ~2s auto-poll; no
server push.

Planned: a `GET /api/signals?agentId=` peek endpoint + signals panel
(`SignalBus.Peek` already exists).
