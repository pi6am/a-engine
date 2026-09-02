<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import {
  attachModule,
  createObject,
  deleteAttribute,
  deleteObject,
  detachModule,
  moveObject,
  selectObject,
  setAttribute,
  setFieldOverride,
  store,
} from '../store'

const detail = computed(() => store.selected)
const memory = computed(() => store.memory)

// --- knowledge panel --------------------------------------------------

/** resolved knowledge-module fields, when the object tracks knowledge at all */
const knowledgeFields = computed<Record<string, unknown> | null>(() => {
  const m = detail.value?.modules.find((m) => m.moduleId === 'knowledge')
  return (m?.fields as Record<string, unknown> | null) ?? null
})

const knownNames = computed<string[]>(() => {
  const v = knowledgeFields.value?.knowsNames
  return Array.isArray(v) ? v.filter((x): x is string => typeof x === 'string') : []
})

const lastSeenEntries = computed<Record<string, { holder?: string; room?: string }>>(() => {
  const v = knowledgeFields.value?.lastSeen
  if (v === null || typeof v !== 'object' || Array.isArray(v)) return {}
  const out: Record<string, { holder?: string; room?: string }> = {}
  for (const [item, entry] of Object.entries(v as Record<string, unknown>)) {
    if (entry === null || typeof entry !== 'object') continue
    const holder = (entry as Record<string, unknown>).holder
    const room = (entry as Record<string, unknown>).room
    out[item] = {
      holder: typeof holder === 'string' ? holder : undefined,
      room: typeof room === 'string' ? room : undefined,
    }
  }
  return out
})

function objectName(id: string): string {
  return store.objects.find((o) => o.id === id)?.name ?? '(unknown object)'
}

/** agents whose names this object could still learn */
const learnableAgents = computed(() =>
  store.objects.filter(
    (o) => o.id !== detail.value?.id && o.modules.includes('agent') && !knownNames.value.includes(o.id),
  ),
)

const notableItems = computed(() => store.objects.filter((o) => o.modules.includes('notable')))
const trackedItems = computed(() => Object.keys(lastSeenEntries.value))
const holderOptions = computed(() => store.objects)
const roomOptions = computed(() => store.objects.filter((o) => o.modules.includes('room')))

const newNameId = ref('')

function learnName(): void {
  if (!detail.value || !newNameId.value) return
  const next = [...knownNames.value, newNameId.value]
  void setFieldOverride(detail.value.id, 'knowledge', 'knowsNames', next).then((ok) => {
    if (ok) newNameId.value = ''
  })
}

function forgetName(id: string): void {
  if (!detail.value) return
  void setFieldOverride(detail.value.id, 'knowledge', 'knowsNames', knownNames.value.filter((n) => n !== id))
}

/** rewrite the lastSeen map, dropping empty holder/room values */
function saveSightings(entries: Record<string, { holder?: string; room?: string }>): void {
  if (!detail.value) return
  const clean: Record<string, { holder?: string; room?: string }> = {}
  for (const [item, entry] of Object.entries(entries)) {
    clean[item] = {
      holder: entry.holder || undefined,
      room: entry.room || undefined,
    }
  }
  void setFieldOverride(detail.value.id, 'knowledge', 'lastSeen', clean)
}

function forgetItem(itemId: string): void {
  const next = { ...lastSeenEntries.value }
  delete next[itemId]
  saveSightings(next)
}

function updateSighting(itemId: string, patch: { holder?: string; room?: string }): void {
  saveSightings({ ...lastSeenEntries.value, [itemId]: { ...lastSeenEntries.value[itemId], ...patch } })
}

const newItemId = ref('')

function trackItem(): void {
  if (!newItemId.value) return
  updateSighting(newItemId.value, {})
  newItemId.value = ''
}

// attribute add/edit inputs, keyed by attribute name (function refs in v-for)
const attrInputs = reactive<Record<string, HTMLInputElement | null>>({})
const newAttrName = ref('')
const newAttrValue = ref('')

// field-override inputs, keyed by `${moduleId}.${field}`
const fieldInputs = reactive<Record<string, HTMLInputElement | null>>({})

const moveTarget = ref('')
const attachTarget = ref('')
const newChildId = ref('')
const newChildName = ref('')

/** Parse as JSON if possible, otherwise keep the raw string. */
function parseValue(text: string): unknown {
  try {
    return JSON.parse(text)
  } catch {
    return text
  }
}

const unattachedModules = computed(() =>
  store.modules.filter((m) => !detail.value?.modules.some((a) => a.moduleId === m.id)),
)

const moveCandidates = computed(() =>
  store.objects.filter((o) => o.id !== detail.value?.id),
)

function format(value: unknown): string {
  return typeof value === 'string' ? value : JSON.stringify(value)
}

function saveAttr(name: string): void {
  const input = attrInputs[name]
  if (!input || !detail.value) return
  void setAttribute(detail.value.id, name, parseValue(input.value))
}

function addAttr(): void {
  if (!detail.value || !newAttrName.value.trim()) return
  void setAttribute(detail.value.id, newAttrName.value.trim(), parseValue(newAttrValue.value)).then((ok) => {
    if (ok) {
      newAttrName.value = ''
      newAttrValue.value = ''
    }
  })
}

function removeAttr(name: string): void {
  if (!detail.value) return
  void deleteAttribute(detail.value.id, name)
}

function saveField(moduleId: string, field: string): void {
  const input = fieldInputs[`${moduleId}.${field}`]
  if (!input || !detail.value) return
  void setFieldOverride(detail.value.id, moduleId, field, parseValue(input.value))
}

function attach(): void {
  if (!detail.value || !attachTarget.value) return
  void attachModule(detail.value.id, attachTarget.value).then((ok) => {
    if (ok) attachTarget.value = ''
  })
}

function detach(moduleId: string): void {
  if (!detail.value) return
  void detachModule(detail.value.id, moduleId)
}

function move(): void {
  if (!detail.value || !moveTarget.value) return
  void moveObject(detail.value.id, moveTarget.value).then((ok) => {
    if (ok) moveTarget.value = ''
  })
}

function destroy(): void {
  if (!detail.value) return
  if (window.confirm(`Delete '${detail.value.id}' and all its children?`))
    void deleteObject(detail.value.id)
}

function createChild(): void {
  if (!detail.value || !newChildId.value.trim()) return
  void createObject(newChildId.value.trim(), detail.value.id, newChildName.value || undefined)
    .then((ok) => {
      if (ok) {
        newChildId.value = ''
        newChildName.value = ''
      }
    })
}
</script>

<template>
  <div class="pane">
    <h2>Object</h2>
    <p v-if="!detail" class="dim">Select an object in the tree.</p>

    <template v-else>
      <dl class="kv">
        <dt>id</dt>
        <dd>{{ detail.id }}</dd>
        <dt>name</dt>
        <dd>{{ detail.name }}</dd>
        <dt>description</dt>
        <dd>{{ detail.description }}</dd>
        <dt>parent</dt>
        <dd>
          <button v-if="detail.parent" class="link" @click="selectObject(detail.parent!)">
            {{ detail.parent }}
          </button>
          <span v-else class="dim">—</span>
        </dd>
      </dl>

      <div class="card">
        <h3>Attributes</h3>
        <div v-for="(value, name) in detail.attributes" :key="name" class="row field-row">
          <span class="dim">{{ name }}</span>
          <input
            :ref="(el) => { attrInputs[name as string] = el as HTMLInputElement | null }"
            class="mono grow"
            :value="format(value)"
            @keyup.enter="saveAttr(name as string)"
          />
          <button @click="saveAttr(name as string)">set</button>
          <button class="danger" @click="removeAttr(name as string)">×</button>
        </div>
        <p v-if="!Object.keys(detail.attributes).length" class="dim">No attributes.</p>
        <div class="row">
          <input v-model="newAttrName" placeholder="name" style="width: 110px" />
          <input v-model="newAttrValue" class="mono grow" placeholder='value (JSON, e.g. 42, true, "text")' />
          <button @click="addAttr">add</button>
        </div>
      </div>

      <div class="card">
        <h3>Modules</h3>
        <div v-for="m in detail.modules" :key="m.moduleId" class="card">
          <div class="row">
            <strong class="mono">{{ m.moduleId }}</strong>
            <span class="grow" />
            <button class="danger" @click="detach(m.moduleId)">detach</button>
          </div>
          <template v-if="m.fields">
            <div v-for="(value, field) in m.fields" :key="field" class="row field-row">
              <span :class="field in m.overrides ? 'overridden' : 'dim'">{{ field }}</span>
              <input
                :ref="(el) => { fieldInputs[`${m.moduleId}.${field as string}`] = el as HTMLInputElement | null }"
                class="mono grow"
                :value="format(value)"
                @keyup.enter="saveField(m.moduleId, field as string)"
              />
              <button @click="saveField(m.moduleId, field as string)">set</button>
            </div>
          </template>
          <p v-else class="dim">Module is not registered — no fields to show.</p>
        </div>
        <div class="row">
          <select v-model="attachTarget" class="grow">
            <option value="" disabled>attach module…</option>
            <option v-for="m in unattachedModules" :key="m.id" :value="m.id">
              {{ m.id }} ({{ m.name }})
            </option>
          </select>
          <button :disabled="!attachTarget" @click="attach">attach</button>
        </div>
      </div>

      <div class="card">
        <h3>Children</h3>
        <div v-for="child in detail.children" :key="child" class="row">
          <button class="link" @click="selectObject(child)">{{ child }}</button>
        </div>
        <p v-if="!detail.children.length" class="dim">No children.</p>
        <div class="row">
          <input v-model="newChildId" class="mono" placeholder="new id" style="width: 120px" />
          <input v-model="newChildName" class="grow" placeholder="name (optional)" />
          <button :disabled="!newChildId.trim()" @click="createChild">create child</button>
        </div>
      </div>

      <div v-if="memory !== null" class="card">
        <h3>Memory</h3>
        <ol class="memory">
          <li v-for="entry in memory" :key="entry.seq">
            <span class="score" :title="`salience ${entry.salience}`">{{ entry.score }}</span>
            {{ entry.text }}
          </li>
        </ol>
        <p v-if="!memory.length" class="dim">No memories yet.</p>
      </div>

      <div v-if="knowledgeFields" class="card">
        <h3>Knowledge</h3>

        <h4>Known names</h4>
        <div v-for="id in knownNames" :key="id" class="row">
          <span class="mono">{{ id }}</span>
          <span class="grow">{{ objectName(id) }}</span>
          <button class="danger" @click="forgetName(id)">×</button>
        </div>
        <p v-if="!knownNames.length" class="dim">Knows nobody by name.</p>
        <div class="row">
          <select v-model="newNameId" class="grow">
            <option value="" disabled>learn a name…</option>
            <option v-for="a in learnableAgents" :key="a.id" :value="a.id">
              {{ a.id }} ({{ a.name }})
            </option>
          </select>
          <button :disabled="!newNameId" @click="learnName">learn</button>
        </div>

        <h4>Notable items</h4>
        <div v-for="(entry, itemId) in lastSeenEntries" :key="itemId" class="card">
          <div class="row">
            <strong>{{ objectName(itemId) }}</strong>
            <span class="grow" />
            <button class="danger" @click="forgetItem(itemId)">forget</button>
          </div>
          <div class="row">
            <span class="dim">holder</span>
            <select
              class="grow"
              :value="entry.holder ?? ''"
              @change="updateSighting(itemId, { holder: ($event.target as HTMLSelectElement).value })"
            >
              <option value="">— unknown —</option>
              <option v-for="o in holderOptions" :key="o.id" :value="o.id">{{ o.name }} ({{ o.id }})</option>
            </select>
          </div>
          <div class="row">
            <span class="dim">room</span>
            <select
              class="grow"
              :value="entry.room ?? ''"
              @change="updateSighting(itemId, { room: ($event.target as HTMLSelectElement).value })"
            >
              <option value="">— unknown —</option>
              <option v-for="o in roomOptions" :key="o.id" :value="o.id">{{ o.name }} ({{ o.id }})</option>
            </select>
          </div>
        </div>
        <p v-if="!trackedItems.length" class="dim">No notable items tracked.</p>
        <div class="row">
          <select v-model="newItemId" class="grow">
            <option value="" disabled>track a notable item…</option>
            <option
              v-for="o in notableItems.filter((n) => !trackedItems.includes(n.id))"
              :key="o.id"
              :value="o.id"
            >
              {{ o.name }} ({{ o.id }})
            </option>
          </select>
          <button :disabled="!newItemId" @click="trackItem">track</button>
        </div>
      </div>

      <div class="card">
        <h3>Move / delete</h3>
        <div class="row">
          <select v-model="moveTarget" class="grow">
            <option value="" disabled>move to parent…</option>
            <option v-for="o in moveCandidates" :key="o.id" :value="o.id">
              {{ o.id }} ({{ o.name }})
            </option>
          </select>
          <button :disabled="!moveTarget" @click="move">move</button>
        </div>
        <div class="row">
          <button class="danger" :disabled="detail.id === 'world'" @click="destroy">
            delete object (recursive)
          </button>
        </div>
      </div>
    </template>
  </div>
</template>
