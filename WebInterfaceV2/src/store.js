import { reactive } from 'vue'
import { api } from './api.js'

export const store = reactive({
  bots: [],
  activeBot: localStorage.getItem('ab_active_bot') || '',  // template name (stable key)
  botState: {},           // per running-bot-id live player state from SSE
  tab: localStorage.getItem('tab') || 'player',
  toast: null,
})

export function selectBot(name) {
  store.activeBot = name
  localStorage.setItem('ab_active_bot', name)
}

let toastTimer = null
export function showToast(msg, kind = 'info') {
  store.toast = { msg, kind }
  clearTimeout(toastTimer)
  toastTimer = setTimeout(() => { store.toast = null }, 3200)
}

/// <summary>Numeric id of the RUNNING instance for the selected template, or -1.</summary>
export function activeBotId() {
  const sel = store.bots.find(b => b.name === store.activeBot)
  if (sel) return sel.running ? sel.runId : -1
  // nothing selected (or selection vanished): prefer a running instance
  const first = store.bots.find(b => b.running)
  return first ? first.runId : -1
}

export function activeBotState() {
  const id = activeBotId()
  return id >= 0 ? store.botState[id] : null
}

export async function refreshStatus() {
  try {
    // merge the on-disk bot templates with the running instances so the
    // selector lists every instance, not just the connected ones
    const [botList, status] = await Promise.all([api.bots(), api.status()])
    const runningByName = {}
    for (const b of status.bots || []) {
      runningByName[(b.Name ?? b.name ?? '').toLowerCase()] = b
    }
    store.bots = (botList.bots || []).map((t, i) => {
      const run = runningByName[(t.name || '').toLowerCase()]
      return {
        name: t.name,
        tsName: t.tsName,
        running: !!t.running,
        runId: run ? (run.Id ?? run.id ?? i) : -1,
      }
    })
    for (const b of status.bots || []) {
      const id = b.Id ?? b.id
      store.botState[id] = { ...store.botState[id], ...b, ...(b.state || {}) }
    }
    // keep the saved selection if it still exists, otherwise pick a running bot
    if (!store.bots.some(b => b.name === store.activeBot)) {
      const first = store.bots.find(b => b.running) || store.bots[0]
      selectBot(first ? first.name : '')
    }
  } catch (e) {
    if (!(e instanceof api.AuthError)) showToast('无法连接 bot: ' + e.message, 'error')
  }
}

export function fmtTime(sec) {
  if (sec == null || isNaN(sec)) return '--:--'
  const s = Math.floor(sec % 60), m = Math.floor(sec / 60) % 60, h = Math.floor(sec / 3600)
  const mm = h ? String(m).padStart(2, '0') : m
  return (h ? h + ':' : '') + mm + ':' + String(s).padStart(2, '0')
}

export function fmtSize(mb) {
  if (mb >= 1024) return (mb / 1024).toFixed(1) + ' GB'
  return (mb || 0) + ' MB'
}

export function coverOrPlaceholder(cover, title) {
  return cover || null
}
