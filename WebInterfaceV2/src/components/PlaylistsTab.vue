<script setup>
import { onMounted, ref, watch } from 'vue'
import { activeBotId, showToast } from '../store.js'
import { api } from '../api.js'
import PlaylistDetail from './PlaylistDetail.vue'

const playlists = ref([])
const loading = ref(true)
const detail = ref(null)   // { id } -> modal fetches content

async function load() {
  const id = activeBotId()
  if (id < 0) return
  loading.value = true
  try {
    const res = await api.playlists(id)
    playlists.value = res.playlists || []
  } catch (e) {
    showToast(e.message, 'error')
  }
  loading.value = false
}

function coverSrc(p) {
  return p.cover || null
}

async function quickPlay(p, shuffle) {
  try {
    const r = await api.playPlaylist(activeBotId(), p.id, { shuffle })
    showToast(`开始播放「${p.title}」(${r.count} 首)`, 'success')
  } catch (e) { showToast(e.message, 'error') }
}

async function remove(p) {
  if (!confirm(`确定删除歌单「${p.title}」？此操作不可恢复。`)) return
  try {
    await api.deletePlaylist(activeBotId(), p.id)
    showToast('已删除', 'success')
    await load()
  } catch (e) { showToast(e.message, 'error') }
}

function sourceHost(src) {
  if (!src) return null
  try { return new URL(src).host } catch { return '外部' }
}

watch(activeBotId, () => load())
onMounted(load)
</script>

<template>
  <div>
    <div style="display: flex; align-items: center; justify-content: space-between">
      <div>
        <div class="page-title">歌单库</div>
        <div class="page-sub">每个 bot 实例维护独立的歌单，支持网易云批量导入</div>
      </div>
      <button class="btn" @click="load">⟳ 刷新</button>
    </div>

    <div v-if="loading" style="padding: 60px; text-align: center"><span class="spin"></span></div>

    <div v-else-if="playlists.length === 0" class="empty">
      <div class="glyph">♪</div>
      还没有歌单，去「网易云」页导入一个吧
    </div>

    <div v-else class="grid">
      <div v-for="p in playlists" :key="p.id" class="pl-card" @click="detail = p">
        <img v-if="coverSrc(p)" :src="coverSrc(p)" class="pl-cover" />
        <div v-else class="pl-cover-ph">♪</div>
        <div class="pl-info">
          <div class="pl-name">{{ p.title || p.id }}</div>
          <div class="pl-sub">
            <span>{{ p.count }} 首</span>
            <span v-if="sourceHost(p.source)" class="badge">{{ sourceHost(p.source) }}</span>
          </div>
        </div>
        <div style="display: flex; gap: 8px; padding: 0 14px 14px" @click.stop>
          <button class="btn small primary" @click="quickPlay(p, false)">▶ 播放</button>
          <button class="btn small" @click="quickPlay(p, true)">🔀 随机</button>
          <button class="btn small danger" style="margin-left: auto" @click="remove(p)">删除</button>
        </div>
      </div>
    </div>

    <PlaylistDetail v-if="detail" :playlist="detail" @close="detail = null" />
  </div>
</template>
