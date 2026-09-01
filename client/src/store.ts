import { computed, reactive } from 'vue'
import {
  DebugApi,
  type AvailableAction,
  type EngineState,
  type ExecuteResult,
  type MemoryEntry,
  type ModuleDef,
  type ObjectDetail,
  type ObjectSummary,
  type TreeNode,
} from './api'

const BASE_URL_KEY = 'a-engine.debugBaseUrl'
const DEFAULT_BASE_URL = 'http://127.0.0.1:5050'
const POLL_INTERVAL_MS = 2000

interface StoreState {
  baseUrl: string
  connected: boolean
  loading: boolean
  error: string | null
  tree: TreeNode | null
  engine: EngineState | null
  objects: ObjectSummary[]
  modules: ModuleDef[]
  selectedId: string | null
  selected: ObjectDetail | null
  /** remembered events of the selected object when it is an agent; null otherwise */
  memory: MemoryEntry[] | null
  agentId: string | null
  actions: AvailableAction[]
  lastResult: ExecuteResult | null
  autoPoll: boolean
}

export const store = reactive<StoreState>({
  baseUrl: localStorage.getItem(BASE_URL_KEY) ?? DEFAULT_BASE_URL,
  connected: false,
  loading: false,
  error: null,
  tree: null,
  engine: null,
  objects: [],
  modules: [],
  selectedId: null,
  selected: null,
  memory: null,
  agentId: null,
  actions: [],
  lastResult: null,
  autoPoll: false,
})

let api = new DebugApi(store.baseUrl)
let pollTimer: ReturnType<typeof setInterval> | null = null

/** Objects with the `agent` module — the choices for the actions panel. */
export const agents = computed(() => store.objects.filter((o) => o.modules.includes('agent')))

export function setBaseUrl(url: string): void {
  store.baseUrl = url.trim() || DEFAULT_BASE_URL
  localStorage.setItem(BASE_URL_KEY, store.baseUrl)
  api = new DebugApi(store.baseUrl)
  void refresh()
}

async function guarded(work: () => Promise<void>): Promise<void> {
  store.error = null
  try {
    await work()
    store.connected = true
  } catch (err) {
    store.connected = false
    store.error = err instanceof Error ? err.message : String(err)
  }
}

/** Refetch the shared world state; also refreshes open detail/action views. */
export async function refresh(): Promise<void> {
  store.loading = true
  try {
    await guarded(async () => {
      const [tree, engine, objects, modules] = await Promise.all([
        api.getTree(),
        api.getEngine(),
        api.listObjects(),
        api.listModules(),
      ])
      store.tree = tree
      store.engine = engine
      store.objects = objects
      store.modules = modules
    })
    if (store.selectedId) await selectObject(store.selectedId)
    if (store.agentId) await selectAgent(store.agentId)
  } finally {
    store.loading = false
  }
}

export async function selectObject(id: string): Promise<void> {
  await guarded(async () => {
    store.selectedId = id
    store.selected = await api.getObject(id)
    // agents get their memory alongside; other objects have none to show
    store.memory = store.selected.modules.some((m) => m.moduleId === 'agent')
      ? (await api.getMemory(id)).entries
      : null
  })
}

export async function selectAgent(id: string): Promise<void> {
  await guarded(async () => {
    store.agentId = id
    store.actions = await api.getActions(id)
  })
}

export async function executeAction(action: AvailableAction): Promise<void> {
  if (!store.agentId) return
  await guarded(async () => {
    store.lastResult = await api.executeAction(store.agentId!, action.verb, action.targetId)
  })
  await refresh()
}

/** Run a mutation, surface its error, then resync all views from the server. */
async function mutate(work: () => Promise<unknown>): Promise<boolean> {
  let ok = true
  await guarded(async () => {
    await work()
  })
  if (store.error) ok = false
  await refresh()
  return ok
}

export function createObject(id: string, parentId: string, name?: string, description?: string): Promise<boolean> {
  return mutate(() => api.createObject(id, parentId, name, description))
}

export function deleteObject(id: string): Promise<boolean> {
  return mutate(async () => {
    await api.deleteObject(id)
    if (store.selectedId === id) {
      store.selectedId = null
      store.selected = null
      store.memory = null
    }
  })
}

export function moveObject(id: string, parentId: string): Promise<boolean> {
  return mutate(() => api.moveObject(id, parentId))
}

export function setAttribute(id: string, name: string, value: unknown): Promise<boolean> {
  return mutate(() => api.setAttribute(id, name, value))
}

export function deleteAttribute(id: string, name: string): Promise<boolean> {
  return mutate(() => api.deleteAttribute(id, name))
}

export function attachModule(id: string, moduleId: string): Promise<boolean> {
  return mutate(() => api.attachModule(id, moduleId))
}

export function detachModule(id: string, moduleId: string): Promise<boolean> {
  return mutate(() => api.detachModule(id, moduleId))
}

export function setFieldOverride(id: string, moduleId: string, field: string, value: unknown): Promise<boolean> {
  return mutate(() => api.setFieldOverride(id, moduleId, field, value))
}

export function setAutoPoll(on: boolean): void {
  store.autoPoll = on
  if (pollTimer !== null) {
    clearInterval(pollTimer)
    pollTimer = null
  }
  if (on) pollTimer = setInterval(() => void refresh(), POLL_INTERVAL_MS)
}
