<script setup>
import { onMounted, onUnmounted, ref } from 'vue'
import { activeBotId, fmtTime, showToast } from '../store.js'
import { api } from '../api.js'

const items = ref([])
const title = ref('')
let timer = null

async function load() {
  const id = activeBotId()
  if (id < 0) return
  try {
    const q = await api.queue(id)
    items.value = q.items || []
    title.value = q.title || ''
  } catch { /* bot offline */ }
}

async function playItem(it) {
  try {
    // keep the queue intact and start playing from this song onwards
    await api.playQueueIndex(activeBotId(), it.index)
    showToast(`从「${it.title}」开始播放`, 'success')
  } catch (e) { showToast(e.message, 'error') }
}

async function removeItem(i) {
  try {
    await api.removeQueue(activeBotId(), i)
    await load()
  } catch (e) { showToast(e.message, 'error') }
}

onMounted(() => { load(); timer = setInterval(load, 5000) })
onUnmounted(() => clearInterval(timer))
defineExpose({ load })
</script>

<template>
  <div>
    <div class="section-title">当前队列 ({{ items.length }})</div>
    <div v-if="items.length === 0" class="empty" style="padding: 30px 0">队列为空</div>
    <div v-else class="card" style="padding: 6px">
      <div v-for="(it, i) in items" :key="i" class="list-row">
        <div class="idx">{{ i + 1 }}</div>
        <img v-if="it.cover" :src="it.cover" class="row-cover" />
        <div v-else class="row-cover"></div>
        <div class="row-main">
          <div class="row-title">
            <span v-if="it.vip" class="badge vip" style="margin-right: 6px">VIP</span>
            {{ it.title || it.resId }}
          </div>
          <div class="row-sub">
            {{ [it.artist, it.duration ? fmtTime(+it.duration) : ''].filter(Boolean).join(' · ') }}
          </div>
        </div>
        <div class="row-actions">
          <button class="icon-btn" title="立即播放" @click="playItem(it)">▶</button>
          <button class="icon-btn" title="移出队列" @click="removeItem(i)">✕</button>
        </div>
      </div>
    </div>
  </div>
</template>
