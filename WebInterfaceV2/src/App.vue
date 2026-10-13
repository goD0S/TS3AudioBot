<script setup>
import { onMounted, onUnmounted, ref, computed } from 'vue'
import { store, selectBot, activeBotId, activeBotState, refreshStatus, showToast } from './store.js'
import { api, connectEvents, getToken } from './api.js'
import PlayerBar from './components/PlayerBar.vue'
import LoginView from './components/LoginView.vue'
import PlayerTab from './components/PlayerTab.vue'
import PlaylistsTab from './components/PlaylistsTab.vue'
import NeteaseTab from './components/NeteaseTab.vue'
import BotsTab from './components/BotsTab.vue'
import CacheTab from './components/CacheTab.vue'

const tabs = [
  { id: 'player', label: '播放', glyph: '▶' },
  { id: 'playlists', label: '歌单', glyph: '♪' },
  { id: 'netease', label: '网易云', glyph: '☁' },
  { id: 'bots', label: '实例', glyph: '⚙' },
  { id: 'cache', label: '缓存', glyph: '▣' },
]

const loggedIn = ref(!!getToken())
let closeEvents = null

function onEvent(data) {
  for (const b of data.bots || []) {
    store.botState[b.id] = { ...(store.botState[b.id] || {}), ...b.state }
  }
}

function onAuthFail() {
  loggedIn.value = false
  closeEvents?.()
  closeEvents = null
}

function switchTab(id) {
  store.tab = id
  localStorage.setItem('tab', id)
}

function onLogin() {
  loggedIn.value = true
  start()
}

async function start() {
  await refreshStatus()
  closeEvents?.()
  closeEvents = connectEvents(onEvent, onAuthFail)
}

onMounted(() => { if (loggedIn.value) start() })
onUnmounted(() => closeEvents?.())

const state = computed(() => activeBotState())
</script>

<template>
  <LoginView v-if="!loggedIn" @ok="onLogin" />

  <div v-else class="app-layout">
    <aside class="sidebar">
      <div class="logo">
        <div class="logo-dot">♪</div>
        <span class="logo-text">TS3AudioBot</span>
      </div>
      <button
        v-for="t in tabs"
        :key="t.id"
        class="nav-item"
        :class="{ active: store.tab === t.id }"
        @click="switchTab(t.id)"
      >
        <span class="nav-glyph">{{ t.glyph }}</span>
        <span class="nav-label">{{ t.label }}</span>
      </button>
      <div class="bot-switch" v-if="store.bots.length > 0">
        <label>Bot 实例</label>
        <select :value="store.activeBot" @change="selectBot($event.target.value)">
          <option v-for="b in store.bots" :key="b.name" :value="b.name">
            {{ b.tsName || b.name }}{{ b.running ? '' : '（离线）' }}
          </option>
        </select>
      </div>
      <div class="bot-switch">
        <label style="cursor: pointer" @click="onAuthFail(); showToast('已退出登录')">退出登录</label>
      </div>
    </aside>

    <div class="main">
      <div class="content">
        <PlayerTab v-show="store.tab === 'player'" />
        <PlaylistsTab v-show="store.tab === 'playlists'" />
        <NeteaseTab v-show="store.tab === 'netease'" />
        <BotsTab v-show="store.tab === 'bots'" />
        <CacheTab v-show="store.tab === 'cache'" />
      </div>

      <PlayerBar v-if="state" />
    </div>

    <div v-if="store.toast" class="toast" :class="store.toast.kind">
      {{ store.toast.msg }}
    </div>
  </div>
</template>
