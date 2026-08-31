// Typed client for the a-engine debug REST API (src/AEngine.DebugServer).
// All shapes match the server's camelCase JSON payloads.

export interface TreeNode {
  id: string
  name: string
  children: TreeNode[]
}

export interface ObjectSummary {
  id: string
  name: string
  parent: string | null
  modules: string[]
}

export interface ModuleAttachment {
  moduleId: string
  overrides: Record<string, unknown>
  /** resolved field values (override -> module default); null if module unregistered */
  fields: Record<string, unknown> | null
}

export interface ObjectDetail {
  id: string
  name: string
  description: string
  parent: string | null
  children: string[]
  attributes: Record<string, unknown>
  modules: ModuleAttachment[]
}

export interface AgentMemory {
  agentId: string
  entries: string[]
}

export interface ModuleFieldDef {
  name: string
  type: string
  default: unknown
}

export interface ModuleAffordance {
  verb: string
  handler: string
  requires: string | null
}

export interface ModuleDef {
  id: string
  name: string
  fields: ModuleFieldDef[]
  affordances: ModuleAffordance[]
}

export interface PendingAction {
  wakeTurn: number
  agentId: string
  handlerId: string
  targetId: string | null
}

export interface EngineState {
  timeMode: string
  currentTurn: number
  pendingActions: PendingAction[]
}

export interface AvailableAction {
  verb: string
  targetId: string | null
  label: string
  handlerId: string
}

export interface ExecuteResult {
  success: boolean
  outcome?: 'success' | 'noop' | 'failure'
  message: string
  turn: number
}

/** Non-2xx response from the debug server (body shape: { error }). */
export class ApiError extends Error {
  readonly status: number
  constructor(status: number, message: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
  }
}

export class DebugApi {
  constructor(public baseUrl: string) {}

  private url(path: string): string {
    return `${this.baseUrl.replace(/\/+$/, '')}${path}`
  }

  private async request<T>(method: string, path: string, body?: unknown): Promise<T> {
    const res = await fetch(this.url(path), {
      method,
      headers: body !== undefined ? { 'Content-Type': 'application/json' } : undefined,
      body: body !== undefined ? JSON.stringify(body) : undefined,
    })
    if (!res.ok) {
      let message = `${method} ${path} failed: HTTP ${res.status}`
      try {
        const data = (await res.json()) as { error?: string }
        if (data.error) message = data.error
      } catch {
        // non-JSON error body — keep the status-line message
      }
      throw new ApiError(res.status, message)
    }
    if (res.status === 204) return undefined as T
    return (await res.json()) as T
  }

  getHealth(): Promise<{ status: string }> {
    return this.request('GET', '/api/health')
  }

  getEngine(): Promise<EngineState> {
    return this.request('GET', '/api/engine')
  }

  getTree(): Promise<TreeNode> {
    return this.request('GET', '/api/world/tree')
  }

  listObjects(): Promise<ObjectSummary[]> {
    return this.request('GET', '/api/objects')
  }

  getObject(id: string): Promise<ObjectDetail> {
    return this.request('GET', `/api/objects/${encodeURIComponent(id)}`)
  }

  /** An agent's remembered observations and actions (oldest first). */
  getMemory(id: string): Promise<AgentMemory> {
    return this.request('GET', `/api/objects/${encodeURIComponent(id)}/memory`)
  }

  createObject(id: string, parentId: string, name?: string, description?: string): Promise<ObjectDetail> {
    return this.request('POST', '/api/objects', { id, parentId, name, description })
  }

  deleteObject(id: string): Promise<void> {
    return this.request('DELETE', `/api/objects/${encodeURIComponent(id)}`)
  }

  moveObject(id: string, parentId: string): Promise<ObjectDetail> {
    return this.request('POST', `/api/objects/${encodeURIComponent(id)}/move`, { parentId })
  }

  setAttribute(id: string, name: string, value: unknown): Promise<ObjectDetail> {
    return this.request('PUT', `/api/objects/${encodeURIComponent(id)}/attributes/${encodeURIComponent(name)}`, value)
  }

  deleteAttribute(id: string, name: string): Promise<void> {
    return this.request('DELETE', `/api/objects/${encodeURIComponent(id)}/attributes/${encodeURIComponent(name)}`)
  }

  attachModule(id: string, moduleId: string, overrides?: Record<string, unknown>): Promise<ObjectDetail> {
    return this.request('PUT', `/api/objects/${encodeURIComponent(id)}/modules/${encodeURIComponent(moduleId)}`, { overrides })
  }

  detachModule(id: string, moduleId: string): Promise<void> {
    return this.request('DELETE', `/api/objects/${encodeURIComponent(id)}/modules/${encodeURIComponent(moduleId)}`)
  }

  setFieldOverride(id: string, moduleId: string, field: string, value: unknown): Promise<ObjectDetail> {
    return this.request(
      'PUT',
      `/api/objects/${encodeURIComponent(id)}/modules/${encodeURIComponent(moduleId)}/fields/${encodeURIComponent(field)}`,
      value,
    )
  }

  listModules(): Promise<ModuleDef[]> {
    return this.request('GET', '/api/modules')
  }

  getActions(agentId: string): Promise<AvailableAction[]> {
    return this.request('GET', `/api/actions?agentId=${encodeURIComponent(agentId)}`)
  }

  executeAction(agentId: string, verb: string, targetId: string | null): Promise<ExecuteResult> {
    return this.request('POST', '/api/actions/execute', { agentId, verb, targetId })
  }
}
