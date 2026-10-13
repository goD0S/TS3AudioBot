<script setup>
import { onMounted, ref, watch } from 'vue'
import { fmtSize, showToast, activeBotId } from '../store.js'
import { api } from '../api.js'

const stats = ref(null)

async function load() {
  try {
    stats.value = await api.cache()
  } catch (e) {
    stats.value = null
  }
}

watch(activeBotId, () => load())

async function clear() {
  if (!confirm('清空全部缓存歌曲？之后播放 VIP 歌曲时会重新下载。')) return
  try {
    await api.clearCache()
    showToast('缓存已清空', 'success')
    await load()
  } catch (e) { showToast(e.message, 'error') }
}

onMounted(load)
</script>

<template>
  <div>
    <div class="page-title">本地缓存</div>
    <div class="page-sub">所有播放过的歌曲都会缓存（VIP 完整版优先保留、最晚清理；免费歌空间不足时先清理）</div>

    <div v-if="stats" class="stat-row">
      <div class="card stat-card">
        <div class="stat-num">{{ stats.files }}</div>
        <div class="stat-label">已缓存歌曲（VIP {{ stats.vipFiles }}）</div>
      </div>
      <div class="card stat-card">
        <div class="stat-num">{{ fmtSize(stats.usedMb) }}</div>
        <div class="stat-label">占用空间 / 上限 {{ fmtSize(stats.maxMb) }}</div>
      </div>
      <div class="card stat-card">
        <div class="stat-num">{{ stats.activeDownloads }}</div>
        <div class="stat-label">正在下载</div>
      </div>
      <div class="card stat-card" style="display: flex; align-items: center; justify-content: center">
        <button class="btn danger" @click="clear">清空缓存</button>
      </div>
    </div>
    <div v-else class="empty">
      <div class="glyph">▣</div>
      缓存不可用
    </div>

    <div class="section-title">说明</div>
    <div class="card" style="padding: 18px; color: var(--text-dim); font-size: 13px; line-height: 1.8">
      <div>· 缓存策略：播放（或导入）过的歌曲全部缓存；VIP 完整版带优先标记，空间满时最晚清理</div>
      <div>· 你的 MUSIC_U cookie 只用于获取 VIP 完整版，免费歌曲的解析和缓存完全不经过 cookie</div>
      <div>· 空间满时先按「最久未播放」清免费歌，VIP 歌最后清</div>
      <div>· 缓存目录为 bot 工作目录下的 <code>cache/netease/</code>，可通过 ts3audiobot.toml 的 [factories.netease] 配置上限</div>
    </div>
  </div>
</template>
