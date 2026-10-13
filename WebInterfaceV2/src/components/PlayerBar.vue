<script setup>
import { computed, ref, watch } from 'vue'
import { activeBotId, activeBotState, fmtTime, showToast } from '../store.js'
import { api } from '../api.js'

const state = computed(() => activeBotState())
const volume = ref(null)

watch(state, (s) => {
  if (s && volume.value === null) volume.value = s.volume
})
watch(volume, (v, old) => {
  if (old !== null && v !== null && state.value && v !== state.value.volume) {
    api.volume(activeBotId(), v).catch(e => showToast(e.message, 'error'))
  }
})

async function act(fn, okMsg) {
  try { await fn(activeBotId()) } catch (e) { showToast(e.message, 'error') }
}
</script>

<template>
  <div v-if="state" class="playerbar">
    <img v-if="state.current?.cover" :src="state.current.cover" class="pb-cover" />
    <div v-else class="pb-cover" style="display: flex; align-items: center; justify-content: center; color: var(--text-faint); font-size: 20px">♫</div>

    <div class="pb-meta">
      <div class="pb-title">{{ state.current?.title || '没有在播放' }}</div>
      <div class="pb-artist">{{ state.current?.artist || (state.paused ? '已暂停' : ' ') }}</div>
    </div>

    <div class="pb-center">
      <div class="pb-controls">
        <button class="icon-btn" title="上一首" @click="act(b => api.previous(b))">⏮</button>
        <button v-if="!state.paused" class="icon-btn big accent" title="暂停" @click="act(b => api.pause(b, 1))">❚❚</button>
        <button v-else class="icon-btn big accent" title="播放" @click="act(b => api.play(b))">▶</button>
        <button class="icon-btn" title="下一首" @click="act(b => api.next(b))">⏭</button>
      </div>
      <div class="pb-progress">
        <span class="pb-time">{{ fmtTime(state.position) }}</span>
        <div class="progress-track">
          <div class="progress-fill" :style="{ width: ((state.position / state.length) * 100 || 0) + '%' }"></div>
        </div>
        <span class="pb-time right">{{ fmtTime(state.length) }}</span>
      </div>
    </div>

    <div class="pb-volume">
      <span style="color: var(--text-dim)">🔉</span>
      <input type="range" min="0" max="100" v-model.number="volume" />
      <span style="font-size: 12px; color: var(--text-dim); width: 28px">{{ volume }}</span>
    </div>
  </div>
</template>
