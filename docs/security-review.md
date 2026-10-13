# Web and media security review

Reviewed the modernization code initially at `e24f45d1` and integrated the newer `dad4c468` karaoke changes before final validation on 2026-10-13. This is a focused source review and regression fix, not a guarantee that the bot or its host is free of vulnerabilities. No production configuration, playback state or database was changed.

## Changes in this pull request

| Finding | Change | Verification |
|---|---|---|
| Logout only changed the UI; 30-day signed tokens could not be revoked individually and logins within a second shared a token | Fresh 256-bit opaque sessions, eight-hour absolute lifetime, bounded in-memory session registry and authenticated logout endpoint | Independent revocation, exact expiry, restart invalidation and logout endpoint tests |
| Open event streams outlived session validity | Recheck authentication before each event batch | Revoke an active stream and verify it ends |
| Concurrent failed logins could lose counter updates; tracking had no bound or expiry for unlocked addresses | Atomic updates, expiration and bounded tracking | Concurrent failures and lockout recovery tests |
| Generated API keys were printed to application logs; query-string credentials could enter history and access logs | Log only the secret file location; require credentials in headers | Query credentials rejected, Bearer and X-API-Key still supported |
| Frontend retained login tokens and personal music cookies in localStorage; stale responses could clear a newer session | Tab-scoped token storage, cookie in page memory, removal of legacy values, explicit logout cleanup and stale-response guards | Frontend credential, logout, failure and race regression tests |
| The shared music-provider HTTP handler could retain response cookies and reuse them across users; unvalidated cookie values entered request headers | Disable automatic cookie handling and validate personal cookie values without including them in errors | Local HTTP server proves response cookies do not cross requests; cookie-value and malformed-cookie endpoint regression tests |
| TLS certificate errors silently fell back to HTTP | Load the certificate before application startup and fail closed | Missing and invalid PFX startup tests |
| CORS allowed every origin, including hardcoded headers in v1/v2 | Same-origin by default; explicit `web.cors_origins` list with no wildcard origins | Origin validation tests |
| Authentication responses could be cached; missing frame/content/referrer defenses | `no-store` on API responses, frame restrictions, nosniff, no-referrer and HSTS on HTTPS | Source review; production frontend build |
| Malformed JSON was treated as an empty request and could trigger default mutation behavior | Require JSON, reject invalid/null bodies, cap depth at 32 and requests at 1 MiB; public and protected routes share error handling | Malformed login and media-type tests |
| Relative media path checks compared an unnormalized path prefix | Canonicalize the full path and check its relative directory components | Existing-file traversal and sibling-prefix tests |
| ffmpeg/yt-dlp arguments were assembled as strings | Use `ProcessStartInfo.ArgumentList`, retain argument boundaries and yt-dlp's option terminator | Quote/space/option-shaped input regression tests |
| Cache eviction ran after downloads, with no size limit or cancellation during body streaming | Limit each song to the smaller of 256 MiB and cache capacity, bound queued downloads to 128, apply a ten-minute deadline to streaming and remove partial files | Known/unknown length oversized downloads and successful cache publication tests |
| TSLib referenced a vulnerable Newtonsoft.Json version | Align it with the application's existing 13.0.3 reference | Dependency restore, compilation and full test suite |
| Thumbnail decoding used ImageSharp 1.0.0 with multiple published advisories; the dependency graph also contained System.Text.RegularExpressions 4.3.0 | Upgrade ImageSharp to 4.1.3 and pin the patched regex package 4.3.1 | Static PNG-to-JPEG and animated GIF round trips, full test suite and transitive vulnerability scan |

The process argument change prevents argument splitting. The review did not demonstrate a remote code execution exploit, and it does not claim that shell metacharacters were previously executed by a shell: both subprocesses already had `UseShellExecute=false`.

## Deployment and compatibility

1. Back up configuration and the history database. Deploy the backend and rebuild/deploy `WebInterfaceV2` together.
2. Rotate any shared API key previously used over HTTP or exposed in logs. The key is in `websecrets.ini`, not `websecrets.toml`. Restart after rotation. This pull request does not rotate production secrets.
3. All old signed sessions are rejected after upgrading. New sessions are kept in process memory and expire after eight hours. A restart, including certificate reload, invalidates sessions and requires browser login again.
4. Browser tokens survive a page reload in the same tab, but logout removes them. Personal `MUSIC_U` cookies are cleared on refresh, close or logout. sessionStorage is still readable by same-origin JavaScript; it is not an XSS defense.
5. Scripts should send a session in `Authorization: Bearer <session>` or the shared administrative key in `X-API-Key`. `?key=` is intentionally no longer accepted. Logging out a browser session does not revoke the shared scripting key.
6. Same-origin requests need no CORS configuration. If another trusted website must call the API, set exact origins under `[web]`, for example `cors_origins = ["https://music.example.com"]`. Do not add wildcards. The authentication key remains required.
7. With `use_ssl = true`, a missing or invalid certificate stops startup. An intentionally configured HTTP listener remains available for trusted local/reverse-proxy deployments; it must not expose credentials over a public plaintext connection.
8. The rate limiter uses the direct network peer address. Reverse proxies need an explicitly trusted forwarded-header setup; never trust arbitrary X-Forwarded-For values from the Internet.

## Remaining risks requiring follow-up

### Outbound requests and local resources

Authenticated web playback reaches the generic resource resolvers. `MediaResolver.ValidateWeb` can request arbitrary HTTP(S) destinations through `WebWrapper`; automatic redirects are not restricted to public addresses. ffmpeg and yt-dlp also perform their own network requests. Initial URL checks alone would not fix redirect or DNS-rebinding attacks across all these paths.

Therefore **SSRF remains a risk for a compromised or insufficiently trusted API operator**. Do not share the administrative key with untrusted listeners. Use a restricted runtime account/container and an egress policy that denies loopback, private networks and cloud metadata from media fetchers, with carefully scoped exceptions for any intentional internal streams. The bot's own TeamSpeak connection to localhost is a separate connection that may need to remain permitted.

Absolute local-file playback and local directory playlists are still supported administrative features. The relative-path repair does not turn the media directory into a filesystem sandbox and does not prevent symlink traversal. Host filesystem permissions remain necessary.

Thumbnail inputs have a compressed-byte limit, but dimension checks currently occur after image decoding and animated images have no total decoded-frame budget. The decoder upgrade addresses reported dependency advisories; it does not establish a strict memory ceiling for hostile images. Treat untrusted media/image processing as a separate resource-limited worker when exposing playback to untrusted listeners.

### Administrative authority

The shared key grants access to bot management as well as playback. The v2 API calls those modules directly and does not apply per-listener TeamSpeak rights. A public listening service needs separate operator/listener credentials and server-side authorization rules. The legacy command bridge also requires careful rights and plugin review. This PR does not introduce a new role model.

### LiteDB dependency

NuGet's vulnerability feed reports **LiteDB 4.1.4 / CVE-2022-23535**, with the advisory describing unsafe polymorphic deserialization when attacker-controlled documents reach object-valued models. Affected versions are below 5.0.13. The examined database paths use typed entities rather than inserting arbitrary API JSON; this review did not establish a remotely reachable exploit in this application.

Upgrading LiteDB requires a separate migration and compatibility change: the code uses v4 `LiteCollection<T>` and `Shrink`, and persistent history/token/statistics data must be preserved. It is intentionally recorded as unresolved instead of silently applying a major database upgrade. See the [maintainer advisory](https://github.com/litedb-org/LiteDB/security/advisories/GHSA-3x49-g6rc-c284).

Newtonsoft.Json's [nesting-related advisory](https://github.com/advisories/GHSA-5crp-9r3c-p9vr) was addressed by aligning TSLib to 13.0.3 and bounding modern API JSON depth. A current dependency scan should remain part of release checks; a clean scan does not replace code review.

ImageSharp is updated to [4.1.3](https://github.com/SixLabors/ImageSharp/releases/tag/v4.1.3), which targets .NET 8. Its [license](https://github.com/SixLabors/ImageSharp/blob/v4.1.3/LICENSE) grants Apache 2.0 for use in open-source software such as this OSL-3.0 project; downstream deployments under different licensing must review their own eligibility. The regex package is pinned to the advisory's [patched 4.3.1 version](https://github.com/advisories/GHSA-cmhx-cq75-c4mj). The final transitive dependency scan still reports LiteDB; these updates are not a claim of a clean dependency graph.

### Coverage limits

Local validation passed 73 backend tests on .NET SDK 8.0.425 and seven frontend tests on Node 24, and produced the Vite production bundle. The local sandbox required Node tests with `--test-isolation=none` and a Vite API runner that skipped optional Windows mapped-drive enumeration while retaining the Vue plugin and production settings. The committed CI uses ordinary `dotnet test`, `npm test` and `npm run build` on Linux. Existing compiler warnings and the LiteDB advisory remain.

No authenticated destructive operation or exploit payload was sent to the production bot. There was no host/port sweep, load test, dependency exploitation, or review of operator-installed plugins and downloader binaries. Regression tests use local fixtures and mocked download responses; music provider availability and real audio playback require an operator's staging test.
