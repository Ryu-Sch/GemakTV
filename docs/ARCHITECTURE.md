# GemakTV architecture

## The pieces

```
                    ┌──────────────────────── GitHub: Ryu-Sch/GemakTV ────────────────────────┐
                    │  main       source code (owner only)                                     │
                    │  published  config.gtv         channel list + latest app release (enc.)  │
                    │             apk/GemakTV-N.apk.gtv   encrypted TV app builds              │
                    └───────▲───────────────────────────▲───────────────────────────▲──────────┘
                            │ PUT config.gtv            │ one commit: APK + config   │ GET (anonymous)
                   GemakTV Manager (phone)      Publisher (owner's PC)        GemakTV (TV box)
                   bot account + token          gh login / GITHUB_TOKEN       kiosk, plays IPTV
                            │                                                       │
                            └──── searches the iptv-org catalogue ───┐   plays HLS streams from the
                                  (iptv-org.github.io/api)           │   channels' own servers
```

| Project | Kind | Role |
|---|---|---|
| `src/GemakTV.Domain` | net10.0 library | Channels, streams, lineups and their diffs, app releases, stream failover, channel navigation. No I/O. |
| `src/GemakTV.Application` | net10.0 library | Use cases and the ports they need: channel-list sync and self-update (TV), lineup editing, catalogue search and conflict-safe publishing (manager). |
| `src/GemakTV.Infrastructure` | net10.0 library | Adapters: the `.gtv` encryption, config-key derivation, JSON, the GitHub client, the iptv-org catalogue, the stream health probe, local files. |
| `src/GemakTV.Platform` | Android library | Shared by both apps: extra trusted root certificates for old Android, the channel-logo cache. |
| `src/GemakTV.Tv` | Android app | Presentation and Android adapters for the TV: player, remote handling, kiosk, installer. |
| `src/GemakTV.Manager` | Android app | Presentation for the phone: sign-in, channel list, search, stream editing. |
| `tools/GemakTV.Publisher` | console | Owner's tool: signing key, first publish, releases, import/export. |
| `tools/GemakTV.FakeGitHub` | web app | Local stand-in for the GitHub API, to test the manager without real credentials. |
| `tests/GemakTV.Tests` | xUnit | Domain, use cases, encryption, GitHub client (against a faithful fake), catalogue, probe. |

Dependencies point inwards only: Domain ← Application ← Infrastructure ← apps and tools. The apps contain no
business rules; for example the TV's "which stream next?" is `StreamFailover` in Domain, and "is there an update?"
is `AppUpdatePolicy`, both unit-tested on the desktop.

## The published config

`config.gtv` on the `published` branch is the single source of truth: the channel lineup and the latest app
release, as JSON, encrypted.

```json
{
  "schema": 1, "revision": 7, "updatedUtc": "2026-11-19T07:05:00Z", "updatedBy": "gemaktv-bot",
  "channels": [
    { "id": "WildEarth.za", "name": "WildEarth", "logo": "https://…png",
      "streams": [ { "url": "https://…/playlist.m3u8", "userAgent": null, "referrer": null } ] }
  ],
  "app": { "versionCode": 6, "versionName": "1.0.5", "path": "apk/GemakTV-6.apk.gtv", "sha256": "…", "size": 10485760 }
}
```

Compatibility rule: only ever add fields. Old TVs ignore fields they do not know, and the `app` section must stay
readable by every version in the field, because it is how they update themselves.

Writers use optimistic concurrency. The manager saves with the file's git blob SHA (GitHub refuses a stale one),
and the publisher commits APK + config with the branch head as parent (GitHub refuses a non-fast-forward). A
concurrent edit is reported, never overwritten.

## Encryption

**Key.** `config.key` = HKDF-SHA256(private exponent D ‖ P ‖ Q of the app signing key, salt
`GemakTV/config-key/v1`, info `config-encryption:<SHA-256 of the modulus>`). So:
- the signing keystore is the only secret to keep;
- both apps get the key at build time (embedded resource) from the same keystore that signs them;
- someone who extracts the key from an APK learns nothing about the signing key (HKDF is one-way).

**Format (`.gtv`).** AES-256-GCM in 64 KiB chunks (the STREAM construction), so a 10 MB APK is decrypted while it
is read, without holding it in memory:

```
header (20 bytes, the associated data of every chunk):
  "GMTV" | version 1 | kind (1 config, 2 package) | 0x0000 | chunk size u32 | nonce prefix (7 random bytes) | 0x00
chunk i: AES-GCM(key, nonce = prefix | i (u32 BE) | last ? 1 : 0, plaintext[i]) || 16-byte tag
```

Wrong key, flipped bits, swapped chunks, truncation at a chunk boundary, and a package passed off as a config
are all detected (`GtvCipherTests`).

## Security model, honestly

| Threat | Protection |
|---|---|
| Someone changes the channel list or pushes an APK | Only the owner and the bot can write (GitHub permissions); rulesets confine the bot to normal commits on `published`. |
| A tampered or foreign APK reaches the TV | The config holds the APK's SHA-256 and size; the `.gtv` envelope is authenticated; the TV checks package name, version and signing certificate before installing; and Android itself refuses an update signed with a different key. |
| Reading the channel list from the public repository | It is encrypted. The key is inside every APK, though, so someone with an APK can decrypt it. Keep the APKs out of public places (they are not attached to GitHub releases on purpose). |
| The bot token leaks | It can only add commits to `published` in this repository. Revoke it on GitHub and sign in again with a new one. |
| A bad TV build | Crash-loop guard: three crashes in three minutes turn the kiosk off for 30 minutes and hand the box to its normal launcher. |

## The TV app

- **Playback.** Media3/ExoPlayer with small buffers (15–30 s, ~24 MB) for 1 GB boxes. Each channel's streams are
  tried in order, starting with the one that worked last time. A stream counts as failed if it errors, does not
  start within 25 s, or stalls for 20 s; after all fail, retries back off (10 s, 30 s, 1 min, then every 2 min).
  It waits for the network when there is none, and jumps back to the live edge when it falls behind.
- **Remote.** ▲ ▼ / CH+ CH−: change channel (skipping channels without a stream). OK: menu. BACK: close or show
  what is on. Digits: go to channel. Volume, mute and power go to Android. Every other key is swallowed.
- **Android 6 specifics.** Root certificates that Android 6 lacks (Let's Encrypt and others) are bundled and
  trusted alongside the system ones. All 36 `Player.Listener` callbacks are implemented explicitly, because Java
  default interface methods are desugared below API 24. "Lean back" full screen avoids the immersive-mode prompt.
- **Self-update.** Download (with progress), decrypt, verify, then `PackageInstaller`: silent as device owner,
  otherwise Android's install screen. After `MY_PACKAGE_REPLACED` the app starts itself again.

## Testing

```bash
dotnet test tests/GemakTV.Tests
```

103 tests covering the rules, the use cases, the encryption's failure modes, the GitHub client against an
in-memory GitHub that honours blob SHAs and fast-forward rules, catalogue parsing, and the stream probe.

End-to-end runs on emulators (see the README) covered: the Android 6 TV image in home-screen mode, a plain
Android 6 box as device owner, and an Android 6 phone for the manager against `tools/GemakTV.FakeGitHub`.
