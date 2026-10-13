<script setup>
import { onMounted, onUnmounted, ref, watch } from 'vue'
import { activeBotId, activeBotState, fmtTime, showToast } from '../store.js'
import { api } from '../api.js'
import QueuePanel from './QueuePanel.vue'

const state = ref(null)
let timer = null

async function load() {
  const id = activeBotId()
  if (id < 0) { state.value = null; return }
  try {
    state.value = await api.player(id)
  } catch { state.value = null }
}

watch(activeBotId, () => load())

async function act(fn, okMsg) {
  try {
    await fn(activeBotId())
    if (okMsg) showToast(okMsg, 'success')
    await load()
  } catch (e) {
    showToast(e.message, 'error')
  }
}

function seekByClick(e) {
  const r = e.currentTarget.getBoundingClientRect()
  const ratio = (e.clientX - r.left) / r.width
  const len = state.value?.length || 0
  if (len) act(b => api.seek(b, Math.floor(ratio * len)))
}

onMounted(() => { load(); timer = setInterval(load, 4000) })
onUnmounted(() => clearInterval(timer))
</script>

<template>
  <div v-if="state">
    <div style="display: flex; align-items: flex-start; gap: 30px; flex-wrap: wrap">
      <img
        v-if="state.current?.cover"
        :src="state.current.cover"
        style="width: 180px; height: 180px; border-radius: 18px; object-fit: cover; background: var(--card)"
      />
      <div v-else class="pl-cover-ph" style="width: 180px; border-radius: 18px; font-size: 54px">♫</div>
      <div style="flex: 1; min-width: 260px">
        <div class="page-title" style="font-size: 26px">{{ state.current?.title || '没有在播放' }}</div>
        <div class="page-sub" style="font-size: 15px; margin-bottom: 18px">
          {{ state.current?.artist || (state.playing ? '' : '粘贴链接播放，或去「网易云」导入歌单') }}
        </div>
        <div style="display: flex; gap: 10px; margin-bottom: 22px; flex-wrap: wrap">
          <button class="btn primary" @click="act(b => api.previous(b))">⏮ 上一首</button>
          <button v-if="state.paused" class="btn primary" @click="act(b => api.play(b))">▶ 播放</button>
          <button v-else class="btn primary" @click="act(b => api.pause(b, 1))">⏸ 暂停</button>
          <button class="btn primary" @click="act(b => api.next(b))">下一首 ⏭</button>
          <button class="btn danger" @click="act(b => api.stop(b))">■ 停止</button>
        </div>

        <div style="display: flex; gap: 12px; margin-bottom: 20px; max-width: 540px">
          <input
            placeholder="粘贴链接，插入为下一首播放（网易云歌曲 / 直播流 / 本地路径）"
            style="flex: 1"
            @keydown.enter="e => { const v = e.target.value.trim(); if (v) { act(b => api.playNext(b, v), '已插入为下一首'); e.target.value = '' } }"
          />
        </div>

        <div style="max-width: 540px">
          <div style="display: flex; justify-content: space-between; font-size: 12px; color: var(--text-dim); margin-bottom: 6px">
            <span>{{ fmtTime(state.position) }}</span>
            <span>{{ fmtTime(state.length) }}</span>
          </div>
          <div class="progress-track" @click="seekByClick">
            <div class="progress-fill" :style="{ width: ((state.position / state.length) * 100 || 0) + '%', transition: 'none' }"></div>
          </div>
        </div>
      </div>
    </div>

    <QueuePanel style="margin-top: 26px" />
  </div>
  <div v-else class="empty">
    <div class="glyph">⚬</div>
    没有正在运行的 bot 实例
  </div>
</template>
