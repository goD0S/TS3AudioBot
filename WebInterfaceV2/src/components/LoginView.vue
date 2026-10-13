<script setup>
import { ref } from 'vue'
import { api } from '../api.js'

const emit = defineEmits(['ok'])
const key = ref('')
const busy = ref(false)
const error = ref('')

async function doLogin() {
  if (!key.value.trim() || busy.value) return
  busy.value = true
  error.value = ''
  try {
    await api.login(key.value.trim())
    key.value = ''
    emit('ok')
  } catch (e) {
    error.value = e.message === '需要密钥登录' ? '密钥错误' : e.message
  }
  busy.value = false
}
</script>

<template>
  <div class="login-mask">
    <div class="login-card">
      <div class="logo" style="justify-content: center; margin-bottom: 6px">
        <div class="logo-dot">♪</div>
      </div>
      <div class="login-title">TS3AudioBot</div>
      <div class="login-sub">请输入访问密钥（向管理员获取）</div>
      <input
        v-model="key"
        type="password"
        autocomplete="off"
        placeholder="访问密钥"
        autofocus
        @keydown.enter="doLogin"
      />
      <button class="btn primary" style="justify-content: center" :disabled="busy || !key.trim()" @click="doLogin">
        <span v-if="busy" class="spin"></span>
        <template v-else>登 录</template>
      </button>
      <div v-if="error" class="login-error">{{ error }}</div>
    </div>
  </div>
</template>

<style scoped>
.login-mask {
  position: fixed; inset: 0;
  background: var(--bg);
  z-index: 200;
  display: flex; align-items: center; justify-content: center;
}
.login-card {
  width: min(360px, 90vw);
  background: var(--card);
  border: 1px solid var(--border);
  border-radius: 18px;
  padding: 34px 30px;
  display: flex; flex-direction: column; gap: 13px;
  box-shadow: var(--shadow);
}
.login-title { text-align: center; font-size: 19px; font-weight: 700; }
.login-sub { text-align: center; color: var(--text-dim); font-size: 13px; margin-bottom: 8px; }
.login-error { color: var(--accent2); font-size: 13px; text-align: center; }
</style>
