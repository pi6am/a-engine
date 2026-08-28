<script setup lang="ts">
import { ref } from 'vue'
import type { TreeNode } from '../api'

const props = defineProps<{
  node: TreeNode
  selectedId: string | null
  depth?: number
}>()

const emit = defineEmits<{
  select: [id: string]
}>()

// root starts expanded so the tree is visible on load
const open = ref((props.depth ?? 0) < 1)
</script>

<template>
  <div class="tree-node">
    <div
      class="tree-row"
      :class="{ selected: node.id === selectedId }"
      @click="emit('select', node.id)"
    >
      <span
        class="tree-toggle"
        @click.stop="open = !open"
      >{{ node.children.length ? (open ? '▾' : '▸') : '·' }}</span>
      <span>{{ node.name }}</span>
      <span class="dim">{{ node.id }}</span>
    </div>
    <div v-if="open && node.children.length" class="tree-children">
      <WorldTree
        v-for="child in node.children"
        :key="child.id"
        :node="child"
        :selected-id="selectedId"
        :depth="(depth ?? 0) + 1"
        @select="emit('select', $event)"
      />
    </div>
  </div>
</template>
