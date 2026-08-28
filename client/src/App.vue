<script setup lang="ts">
import { onMounted, ref } from 'vue'
import WorldTree from './components/WorldTree.vue'
import ObjectDetail from './components/ObjectDetail.vue'
import EnginePanel from './components/EnginePanel.vue'
import ActionsPanel from './components/ActionsPanel.vue'
import { refresh, selectObject, setAutoPoll, setBaseUrl, store } from './store'

const baseUrlInput = ref(store.baseUrl)

function applyBaseUrl(): void {
  setBaseUrl(baseUrlInput.value)
}

onMounted(() => {
  void refresh().then(() => {
    // select the player by default so the detail pane has content
    if (!store.selectedId && store.objects.some((o) => o.id === 'player'))
      void selectObject('player')
  })
})
</script>

<template>
  <header class="app-header">
    <h1>a-engine debug client</h1>
    <span class="conn-dot" :class="{ on: store.connected }" :title="store.connected ? 'connected' : 'disconnected'" />
    <input
      v-model="baseUrlInput"
      class="mono base-url"
      placeholder="http://127.0.0.1:5050"
      @keyup.enter="applyBaseUrl"
    />
    <button @click="applyBaseUrl">apply</button>
    <button :disabled="store.loading" @click="refresh">
      {{ store.loading ? 'refreshing…' : 'refresh' }}
    </button>
    <label class="checkbox">
      <input type="checkbox" :checked="store.autoPoll" @change="setAutoPoll(($event.target as HTMLInputElement).checked)" />
      auto-poll (2s)
    </label>
  </header>

  <div v-if="store.error" class="error-banner">
    <span>{{ store.error }}</span>
    <button @click="store.error = null">dismiss</button>
  </div>

  <main class="app-main">
    <div class="pane">
      <h2>World</h2>
      <WorldTree
        v-if="store.tree"
        :node="store.tree"
        :selected-id="store.selectedId"
        @select="selectObject"
      />
      <p v-else class="dim">No world loaded.</p>
    </div>

    <ObjectDetail />

    <div class="right-col">
      <EnginePanel />
      <ActionsPanel />
    </div>
  </main>
</template>
