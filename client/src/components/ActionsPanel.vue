<script setup lang="ts">
import { agents, executeAction, selectAgent, store } from '../store'
import type { AvailableAction } from '../api'

function run(action: AvailableAction): void {
  void executeAction(action)
}
</script>

<template>
  <div class="pane">
    <h2>Actions</h2>

    <div class="row">
      <label for="agent-picker" class="dim">agent</label>
      <select
        id="agent-picker"
        class="grow"
        :value="store.agentId ?? ''"
        @change="selectAgent(($event.target as HTMLSelectElement).value)"
      >
        <option value="" disabled>pick an agent…</option>
        <option v-for="a in agents" :key="a.id" :value="a.id">
          {{ a.id }} ({{ a.name }})
        </option>
      </select>
    </div>

    <template v-if="store.agentId">
      <button
        v-for="(a, i) in store.actions"
        :key="i"
        class="action-btn"
        :title="`verb=${a.verb} target=${a.targetId ?? '—'} handler=${a.handlerId}`"
        @click="run(a)"
      >
        {{ a.label }}
      </button>
      <p v-if="!store.actions.length" class="dim">No actions available.</p>

      <div v-if="store.lastResult" class="result" :class="store.lastResult.success ? 'ok' : 'fail'">
        {{ store.lastResult.message }}
        <div class="dim">turn {{ store.lastResult.turn }}</div>
      </div>
    </template>
    <p v-else class="dim">Pick an agent to see its available actions.</p>
  </div>
</template>
