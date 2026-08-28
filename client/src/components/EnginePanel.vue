<script setup lang="ts">
import { store } from '../store'
</script>

<template>
  <div class="pane">
    <h2>Engine</h2>
    <dl v-if="store.engine" class="kv">
      <dt>time mode</dt>
      <dd>{{ store.engine.timeMode }}</dd>
      <dt>turn</dt>
      <dd>{{ store.engine.currentTurn }}</dd>
    </dl>
    <p v-else class="dim">No engine data yet.</p>

    <h2>Pending scheduled actions</h2>
    <table v-if="store.engine?.pendingActions.length" class="simple">
      <thead>
        <tr>
          <th>wake</th>
          <th>agent</th>
          <th>handler</th>
          <th>target</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="(p, i) in store.engine.pendingActions" :key="i">
          <td>{{ p.wakeTurn }}</td>
          <td>{{ p.agentId }}</td>
          <td>{{ p.handlerId }}</td>
          <td>{{ p.targetId ?? '—' }}</td>
        </tr>
      </tbody>
    </table>
    <p v-else class="dim">None.</p>
  </div>
</template>
