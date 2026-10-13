<script setup>
import { onMounted, ref } from 'vue'
import { activeBotId, fmtTime, showToast } from '../store.js'
import { api, getNeteaseCookie, setNeteaseCookie } from '../api.js'

const url = ref('')
const listId = ref('')
const previewing = ref(false)
const preview = ref(null)
const importing = ref(false)
const useCookie = ref(getNeteaseCookie().length > 0)

const cookieInput = ref('')
const cookieChecking = ref(false)
const cookieStatus = ref(null) // {ok, nickname?, vip?, message?}
const showGuide = ref(false)

const keyword = ref('')
const searching = ref(false)
const songs = ref([])

const migrating = ref(false)
const migrateResult = ref(null)

function saveCookie() {
  const c = cookieInput.value.trim()
  if (c) setNeteaseCookie(c)
  useCookie.value = c.length > 0
}

async function checkCookie() {
  const c = cookieInput.value.trim()
  if (!c) { showToast('请先粘贴 cookie', 'error'); return }
  saveCookie()
  cookieChecking.value = true
  cookieStatus.value = null
  try {
    cookieStatus.value = await api.neteaseCookieCheck(c)
  } catch (e) { showToast(e.message, 'error') }
  cookieChecking.value = false
}

function clearCookie() {
  setNeteaseCookie('')
  cookieInput.value = ''
  cookieStatus.value = null
  useCookie.value = false
  showToast('已清除本地 cookie', 'success')
}

async function doPreview() {
  if (!url.value.trim()) return
  saveCookie()
  previewing.value = true
  preview.value = null
  try {
    preview.value = await api.neteasePreview(url.value.trim())
    if (!listId.value.trim() && preview.value.title) {
      listId.value = 'wy_' + preview.value.title.replace(/[^\w\u4e00-\u9fa5-]+/g, '_').slice(0, 40)
    }
  } catch (e) {
    showToast(e.message, 'error')
  }
  previewing.value = false
}

async function doImport() {
  if (!url.value.trim()) return
  saveCookie()
  importing.value = true
  try {
    const c = useCookie.value ? getNeteaseCookie() : undefined
    const r = await api.neteaseImport(activeBotId(), url.value.trim(), listId.value.trim() || undefined, true, c)
    showToast(`已导入「${r.title}」：${r.trackCount} 首（VIP ${r.vipCount} 首已用你的 cookie 预热缓存）`, 'success')
    preview.value = null
    url.value = ''
    listId.value = ''
  } catch (e) {
    showToast(e.message, 'error')
  }
  importing.value = false
}

async function doSearch() {
  if (!keyword.value.trim()) return
  searching.value = true
  try {
    const r = await api.neteaseSearch(activeBotId(), keyword.value.trim())
    songs.value = r.songs || []
  } catch (e) {
    showToast(e.message, 'error')
  }
  searching.value = false
}

async function playSong(s) {
  try {
    await api.play(activeBotId(), s.link)
    showToast(`播放「${s.name}」`, 'success')
  } catch (e) { showToast(e.message, 'error') }
}

async function enqueueSong(s) {
  try {
    await api.enqueue(activeBotId(), { url: s.link })
    showToast('已加入队列', 'success')
  } catch (e) { showToast(e.message, 'error') }
}

async function doMigrate() {
  if (!confirm('扫描所有歌单，把旧的网易云外链(outer-url)条目转换为正规条目？')) return
  migrating.value = true
  try {
    migrateResult.value = await api.neteaseMigrate(activeBotId())
    showToast(`迁移完成：转换 ${migrateResult.value.itemsConverted} 首`, 'success')
  } catch (e) {
    showToast(e.message, 'error')
  }
  migrating.value = false
}
</script>

<template>
  <div>
    <div class="page-title">网易云歌单</div>
    <div class="page-sub">粘贴歌单链接或 ID 一键导入；VIP 歌曲需要你自己的 MUSIC_U cookie（用完即焚，不保存在服务器）</div>

    <div class="card" style="padding: 20px; margin-bottom: 18px">
      <div style="display: flex; align-items: center; justify-content: space-between; margin-bottom: 12px">
        <div style="font-weight: 600">
          <span v-if="useCookie" style="color: var(--ok)">✓ 已启用个人 cookie（仅保存在此浏览器）</span>
          <span v-else style="color: var(--text-dim)">未启用 cookie（VIP 歌曲将无法完整播放）</span>
        </div>
        <button class="btn small" @click="showGuide = !showGuide">{{ showGuide ? '收起教程 ▲' : '如何获取 MUSIC_U？▼' }}</button>
      </div>

      <div v-if="showGuide" class="guide">
        <div class="guide-step"><span class="guide-num">1</span><div>电脑浏览器登录 <b>music.163.com</b>（手机网页版不行，需要电脑版）</div></div>
        <div class="guide-step"><span class="guide-num">2</span><div>按 <b>F12</b> 打开开发者工具，选择 <b>Application</b>（应用程序）标签页 <span class="guide-alt">（Safari 在「开发」菜单开启；Edge 同 Chrome）</span></div></div>
        <div class="guide-step"><span class="guide-num">3</span><div>左侧找到 <b>Cookies → https://music.163.com</b>，在列表里找到名为 <b>MUSIC_U</b> 的条目</div></div>
        <div class="guide-step"><span class="guide-num">4</span><div>双击它的值，完整复制（一长串字母数字），粘贴到下方输入框</div></div>
        <div class="guide-privacy">
          🔒 隐私说明：cookie 只保存在<b>你自己的浏览器</b>里，导入请求时随请求使用一次后立即丢弃（用完即焚），服务器不做任何持久化。请不要把 cookie 发给别人——它等同你的登录凭证。
        </div>
      </div>

      <div style="display: flex; gap: 10px; margin-top: 6px">
        <input v-model="cookieInput" placeholder="粘贴你的 MUSIC_U cookie（可选，VIP 歌曲需要）" style="flex: 1" />
        <button class="btn" :disabled="cookieChecking || !cookieInput.trim()" @click="checkCookie">
          <span v-if="cookieChecking" class="spin"></span>
          <template v-else>验证</template>
        </button>
        <button v-if="getNeteaseCookie() || cookieInput" class="btn small danger" @click="clearCookie">清除</button>
      </div>
      <div v-if="cookieStatus" style="margin-top: 10px; font-size: 13px"
        :style="cookieStatus.ok ? 'color: var(--ok)' : 'color: var(--accent2)'">
        {{ cookieStatus.ok
          ? `✓ 有效：${cookieStatus.nickname}${cookieStatus.vip ? '（VIP 账号，可完整播放 VIP 歌曲）' : '（非 VIP，仅能播放免费歌曲）'}`
          : '✗ ' + cookieStatus.message }}
      </div>
    </div>

    <div class="card" style="padding: 22px; margin-bottom: 28px">
      <div class="import-row">
        <input
          v-model="url"
          placeholder="例如 https://music.163.com/#/playlist?id=17883877526 或直接输入歌单 ID"
          @keydown.enter="doPreview"
        />
        <button class="btn" :disabled="previewing || !url.trim()" @click="doPreview">
          <span v-if="previewing" class="spin"></span>
          <template v-else>预览</template>
        </button>
      </div>

      <div v-if="preview" class="card preview-card" style="background: var(--bg-soft)">
        <img v-if="preview.coverUrl" :src="preview.coverUrl" />
        <div v-else class="pv-ph">♪</div>
        <div style="flex: 1; min-width: 0">
          <div class="pv-title">{{ preview.title }}</div>
          <div class="pv-sub">{{ preview.trackCount }} 首 · 其中 VIP {{ preview.vipCount }} 首{{ useCookie ? '（将用你的 cookie 预热缓存）' : '（无 cookie：VIP 首次播放会被跳过）' }}</div>
          <div class="import-row" style="margin-bottom: 10px">
            <input v-model="listId" placeholder="保存为歌单 ID（留空自动生成）" />
            <button class="btn primary" :disabled="importing" @click="doImport">
              <span v-if="importing" class="spin"></span>
              <template v-else>导入</template>
            </button>
          </div>
        </div>
      </div>
    </div>

    <div class="section-title">搜索网易云歌曲</div>
    <div class="import-row">
      <input v-model="keyword" placeholder="歌名 / 歌手" @keydown.enter="doSearch" />
      <button class="btn" :disabled="searching || !keyword.trim()" @click="doSearch">
        <span v-if="searching" class="spin"></span>
        <template v-else>搜索</template>
      </button>
    </div>
    <div v-if="songs.length" class="card" style="padding: 6px">
      <div v-for="s in songs" :key="s.id" class="list-row">
        <div class="row-main">
          <div class="row-title">
            <span v-if="s.vip" class="badge vip" style="margin-right: 6px">VIP</span>
            {{ s.name }}
          </div>
          <div class="row-sub">{{ [s.artists, s.album, s.duration ? fmtTime(s.duration) : ''].filter(Boolean).join(' · ') }}</div>
        </div>
        <div class="row-actions" style="opacity: 1">
          <button class="icon-btn" title="播放" @click="playSong(s)">▶</button>
          <button class="icon-btn" title="加入队列" @click="enqueueSong(s)">＋</button>
        </div>
      </div>
    </div>
    <div v-else-if="!searching" class="empty" style="padding: 24px 0">输入关键词搜索</div>

    <div class="section-title">旧歌单迁移</div>
    <div class="card" style="padding: 18px; display: flex; align-items: center; gap: 16px">
      <div style="flex: 1">
        <div style="font-weight: 600">修复旧版 outer-url 歌单条目</div>
        <div style="color: var(--text-dim); font-size: 13px; margin-top: 2px">
          旧版通过临时外链添加的网易云歌曲没有歌名信息且链接会失效，一键转换为带完整元数据的正规条目
        </div>
        <div v-if="migrateResult" style="font-size: 13px; margin-top: 8px; color: var(--ok)">
          扫描 {{ migrateResult.playlistsScanned }} 个歌单，转换 {{ migrateResult.itemsConverted }} 首，失败 {{ migrateResult.itemsFailed }} 首
        </div>
      </div>
      <button class="btn primary" :disabled="migrating" @click="doMigrate">
        <span v-if="migrating" class="spin"></span>
        <template v-else>开始迁移</template>
      </button>
    </div>
  </div>
</template>

<style scoped>
.guide {
  background: var(--bg-soft);
  border: 1px dashed var(--border);
  border-radius: 10px;
  padding: 14px 16px;
  margin-bottom: 14px;
  font-size: 13px;
  display: flex; flex-direction: column; gap: 9px;
}
.guide-step { display: flex; gap: 10px; align-items: baseline; }
.guide-num {
  width: 20px; height: 20px; flex-shrink: 0;
  border-radius: 50%;
  background: var(--accent-soft);
  color: var(--accent);
  font-weight: 700; font-size: 12px;
  display: inline-flex; align-items: center; justify-content: center;
}
.guide-alt { color: var(--text-faint); font-size: 12px; }
.guide-privacy {
  border-top: 1px solid var(--border);
  padding-top: 10px;
  color: var(--text-dim);
  font-size: 12px;
  line-height: 1.7;
}
</style>
