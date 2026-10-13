<script setup>
import { onMounted, ref } from 'vue'
import { activeBotId, fmtTime, showToast } from '../store.js'
import { api } from '../api.js'

const props = defineProps({ playlist: { type: Object, required: true } })
const emit = defineEmits(['close'])

const meta = ref(null)
const items = ref([])
const loading = ref(true)

async function load() {
  try {
    const res = await api.playlist(activeBotId(), props.playlist.id)
    meta.value = res
    items.value = res.items || []
  } catch (e) {
    showToast(e.message, 'error')
  }
  loading.value = false
}

async function playFrom(index) {
  try {
    await api.playPlaylist(activeBotId(), props.playlist.id, { index })
    showToast('开始播放', 'success')
  } catch (e) { showToast(e.message, 'error') }
}

async function playItem(it) {
  const link = it.link || it.resId
  try {
    await api.play(activeBotId(), link)
    showToast('已播放', 'success')
  } catch (e) { showToast(e.message, 'error') }
}

onMounted(load)
</script>

<template>
  <div class="modal-mask" @click.self="emit('close')">
    <div class="modal">
      <div class="modal-head">
        <img v-if="meta?.cover" :src="meta.cover" />
        <div v-else class="pl-cover-ph" style="width: 54px; height: 54px; border-radius: 10px; font-size: 22px">♪</div>
        <div style="flex: 1; min-width: 0">
          <div class="mh-title">{{ meta?.title || playlist.title || playlist.id }}</div>
          <div class="mh-sub">{{ meta?.count ?? '…' }} 首<span v-if="meta?.creator"> · by {{ meta.creator }}</span></div>
        </div>
        <button class="btn primary" @click="playFrom(0)">▶ 播放全部</button>
        <button class="btn" @click="playFrom(0)">🔀 随机</button>
        <button class="icon-btn" @click="emit('close')">✕</button>
      </div>
      <div class="modal-body">
        <div v-if="loading" style="padding: 50px; text-align: center"><span class="spin"></span></div>
        <div v-else>
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
                {{ [it.artist, it.album, it.duration ? fmtTime(+it.duration) : ''].filter(Boolean).join(' · ') }}
              </div>
            </div>
            <div class="row-actions">
              <button class="icon-btn" title="立即播放" @click="playItem(it)">▶</button>
              <button class="icon-btn" title="从这里播放" @click="playFrom(i)">≡▶</button>
              <a v-if="it.link" class="icon-btn" :href="it.link" target="_blank" title="在网易云打开" style="display:inline-flex">↗</a>
            </div>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>
