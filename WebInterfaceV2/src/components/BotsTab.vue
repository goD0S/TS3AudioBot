<script setup>
import { onMounted, ref } from 'vue'
import { activeBotId, showToast } from '../store.js'
import { api } from '../api.js'

const bots = ref([])
const loading = ref(true)
const showCreate = ref(false)

const form = ref({ name: '', server: '127.0.0.1:9987', tsName: '', channel: '', autoRun: true, startNow: true })
const renaming = ref(null)   // {name, value}
const channelling = ref(null) // {name, value}

async function load() {
  loading.value = true
  try {
    const r = await api.bots()
    bots.value = r.bots || []
  } catch (e) { showToast(e.message, 'error') }
  loading.value = false
}

async function createBot() {
  if (!form.value.name.trim()) { showToast('请填写实例名', 'error'); return }
  try {
    const r = await api.createBot({ ...form.value, name: form.value.name.trim() })
    showToast(r.created ? `已创建 ${r.name}` : `${r.name} 已存在，配置已更新`, 'success')
    showCreate.value = false
    await load()
  } catch (e) { showToast(e.message, 'error') }
}

async function act(fn, okMsg) {
  try { await fn(); if (okMsg) showToast(okMsg, 'success'); await load() }
  catch (e) { showToast(e.message, 'error') }
}

onMounted(load)
</script>

<template>
  <div>
    <div style="display: flex; align-items: center; justify-content: space-between; margin-bottom: 4px">
      <div>
        <div class="page-title">Bot 实例管理</div>
        <div class="page-sub">创建 / 连接 TS3 服务器 / 设置昵称与目标频道</div>
      </div>
      <div style="display: flex; gap: 10px">
        <button class="btn" @click="load">⟳ 刷新</button>
        <button class="btn primary" @click="showCreate = !showCreate">＋ 新建实例</button>
      </div>
    </div>

    <div v-if="showCreate" class="card" style="padding: 20px; margin: 14px 0">
      <div style="display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: 12px">
        <label style="display: flex; flex-direction: column; gap: 5px; font-size: 12px; color: var(--text-dim)">
          实例名（字母数字-_）
          <input v-model="form.name" placeholder="例如 music2" />
        </label>
        <label style="display: flex; flex-direction: column; gap: 5px; font-size: 12px; color: var(--text-dim)">
          TS3 服务器地址
          <input v-model="form.server" placeholder="127.0.0.1:9987" />
        </label>
        <label style="display: flex; flex-direction: column; gap: 5px; font-size: 12px; color: var(--text-dim)">
          TS 内昵称
          <input v-model="form.tsName" placeholder="例如 🎵 音乐 bot 2" />
        </label>
        <label style="display: flex; flex-direction: column; gap: 5px; font-size: 12px; color: var(--text-dim)">
          目标频道（路径或 /id）
          <input v-model="form.channel" placeholder="例如 大厅/音乐 或 /12" />
        </label>
      </div>
      <div style="display: flex; align-items: center; gap: 18px; margin-top: 14px">
        <label style="display: flex; align-items: center; gap: 7px; font-size: 13px">
          <input type="checkbox" v-model="form.autoRun" style="width: auto" /> 重启后自动连接
        </label>
        <label style="display: flex; align-items: center; gap: 7px; font-size: 13px">
          <input type="checkbox" v-model="form.startNow" style="width: auto" /> 立即连接
        </label>
        <button class="btn primary" style="margin-left: auto" @click="createBot">创建</button>
      </div>
    </div>

    <div v-if="loading" style="padding: 50px; text-align: center"><span class="spin"></span></div>

    <div v-else style="display: grid; grid-template-columns: repeat(auto-fill, minmax(330px, 1fr)); gap: 16px; margin-top: 16px">
      <div v-for="b in bots" :key="b.name" class="card" style="padding: 18px">
        <div style="display: flex; align-items: center; gap: 12px; margin-bottom: 12px">
          <div class="logo-dot" style="background: var(--card-hover); color: var(--text)">♪</div>
          <div style="flex: 1; min-width: 0">
            <div style="font-weight: 700; white-space: nowrap; overflow: hidden; text-overflow: ellipsis">
              {{ b.tsName || b.name }}
            </div>
            <div style="font-size: 12px; color: var(--text-dim)">{{ b.name }} · {{ b.server || '未设置服务器' }}</div>
          </div>
          <span class="badge" :style="b.running ? 'background: rgba(62,207,142,.14); color: var(--ok)' : ''">
            {{ b.running ? '已连接' : '离线' }}
          </span>
        </div>

        <div style="font-size: 12px; color: var(--text-dim); margin-bottom: 12px">
          <div v-if="b.running">当前频道：<span style="color: var(--text)">{{ b.channelName || b.defaultChannel || '默认' }}</span></div>
          <div v-else>默认频道：{{ b.defaultChannel || '未设置' }}</div>
          <div style="margin-top: 3px">
            开机自启：
            <button class="badge" style="cursor: pointer" @click="act(() => api.setAutoRun(b.name, !b.autoRun))">
              {{ b.autoRun ? '开' : '关' }}
            </button>
          </div>
        </div>

        <div style="display: flex; flex-wrap: wrap; gap: 8px">
          <button v-if="!b.running" class="btn small primary" @click="act(() => api.connectBot(b.name), '已连接')">▶ 连接</button>
          <button v-else class="btn small danger" @click="act(() => api.disconnectBot(b.name), '已断开')">■ 断开</button>
          <button class="btn small" @click="renaming = { name: b.name, value: b.tsName }">✏ 改昵称</button>
          <button class="btn small" @click="channelling = { name: b.name, value: b.defaultChannel }">⇢ 进频道</button>
          <button class="btn small danger" style="margin-left: auto"
            @click="confirm(`确定删除实例 ${b.name}？`) && act(() => api.deleteBot(b.name), '已删除')"
          >删除</button>
        </div>

        <div v-if="renaming?.name === b.name" style="display: flex; gap: 8px; margin-top: 12px">
          <input v-model="renaming.value" placeholder="新的 TS 昵称" style="flex: 1" />
          <button class="btn small primary" @click="act(() => api.setTsName(renaming.name, renaming.value), '已改名'); renaming = null">保存</button>
          <button class="btn small" @click="renaming = null">取消</button>
        </div>
        <div v-if="channelling?.name === b.name" style="display: flex; gap: 8px; margin-top: 12px">
          <input v-model="channelling.value" placeholder="频道路径（大厅/音乐）或 /频道id" style="flex: 1" />
          <button class="btn small primary" @click="act(() => api.setChannel(channelling.name, channelling.value), '已切换'); channelling = null">进入</button>
          <button class="btn small" @click="channelling = null">取消</button>
        </div>
      </div>
    </div>

    <div v-if="!loading && bots.length === 0" class="empty">
      <div class="glyph">♪</div>
      还没有实例，点「新建实例」创建一个
    </div>
  </div>
</template>
