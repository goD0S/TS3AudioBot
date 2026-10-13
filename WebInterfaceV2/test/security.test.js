import test, { beforeEach } from 'node:test'
import assert from 'node:assert/strict'

class MemoryStorage {
  values = new Map()
  getItem(key) { return this.values.get(key) ?? null }
  setItem(key, value) { this.values.set(key, String(value)) }
  removeItem(key) { this.values.delete(key) }
}

let apiModule
let caseNumber = 0
beforeEach(async () => {
  globalThis.localStorage = new MemoryStorage()
  globalThis.sessionStorage = new MemoryStorage()
  localStorage.setItem('ab_token', 'old-persistent-token')
  localStorage.setItem('ab_netease_cookie', 'old-personal-cookie')
  apiModule = await import(`../src/api.js?test=${++caseNumber}`)
})

test('old persistent credentials are removed without being reused', () => {
  assert.equal(apiModule.getToken(), '')
  assert.equal(apiModule.getNeteaseCookie(), '')
  assert.equal(localStorage.getItem('ab_token'), null)
  assert.equal(localStorage.getItem('ab_netease_cookie'), null)
})

test('login stores its token in this tab and the personal cookie stays in memory', async () => {
  globalThis.fetch = async () => Response.json({ token: 'new-session' })
  await apiModule.api.login('test-only-key')
  apiModule.setNeteaseCookie('personal-cookie')
  assert.equal(sessionStorage.getItem('ab_token'), 'new-session')
  assert.equal(localStorage.getItem('ab_token'), null)
  assert.equal(sessionStorage.getItem('ab_netease_cookie'), null)
  assert.equal(localStorage.getItem('ab_netease_cookie'), null)
  assert.equal(apiModule.getNeteaseCookie(), 'personal-cookie')
})

test('logout presents its token for revocation and clears local credentials', async () => {
  apiModule.setToken('session-to-revoke')
  apiModule.setNeteaseCookie('personal-cookie')
  let call
  globalThis.fetch = async (url, options) => { call = { url, options }; return Response.json({ ok: true }) }
  await apiModule.api.logout()
  assert.equal(call.url, '/api/v2/auth/logout')
  assert.equal(call.options.method, 'POST')
  assert.equal(call.options.headers.Authorization, 'Bearer session-to-revoke')
  assert.equal(apiModule.getToken(), '')
  assert.equal(apiModule.getNeteaseCookie(), '')
})

test('failed remote logout still clears this tab', async () => {
  apiModule.setToken('old-session')
  apiModule.setNeteaseCookie('personal-cookie')
  globalThis.fetch = async () => { throw new Error('offline') }
  await assert.rejects(apiModule.api.logout())
  assert.equal(apiModule.getToken(), '')
  assert.equal(apiModule.getNeteaseCookie(), '')
})

test('an old pending logout does not clear a newer login', async () => {
  apiModule.setToken('old-session')
  let finish
  globalThis.fetch = () => new Promise(resolve => { finish = resolve })
  const pending = apiModule.api.logout()
  apiModule.clearCredentials()
  apiModule.setToken('new-session')
  apiModule.setNeteaseCookie('new-personal-cookie')
  finish(Response.json({ ok: true }))
  await pending
  assert.equal(apiModule.getToken(), 'new-session')
  assert.equal(apiModule.getNeteaseCookie(), 'new-personal-cookie')
})

test('an old rejected request does not clear a newer login', async () => {
  apiModule.setToken('old-session')
  let finish
  globalThis.fetch = () => new Promise(resolve => { finish = resolve })
  const pending = apiModule.api.status()
  apiModule.setToken('new-session')
  finish(Response.json({ error: 'expired' }, { status: 401 }))
  await assert.rejects(pending, apiModule.api.AuthError)
  assert.equal(apiModule.getToken(), 'new-session')
})

test('event-stream authentication failure clears credentials and reports it', async () => {
  apiModule.setToken('expired-session')
  apiModule.setNeteaseCookie('personal-cookie')
  globalThis.fetch = async () => Response.json({ error: 'expired' }, { status: 401 })
  let notified
  const ended = new Promise(resolve => { notified = resolve })
  const close = apiModule.connectEvents(() => assert.fail('No state should arrive'), notified)
  await ended
  close()
  assert.equal(apiModule.getToken(), '')
  assert.equal(apiModule.getNeteaseCookie(), '')
})
