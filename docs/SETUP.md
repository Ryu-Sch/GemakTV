# Setting up GemakTV

This guide covers everything from a fresh checkout to a TV that turns on straight into live channels.
Steps marked **once** are done a single time.

1. [The signing key (once)](#1-the-signing-key-once)
2. [The GitHub bot account (once)](#2-the-github-bot-account-once)
3. [Preparing the MyGica box (once per box)](#3-preparing-the-mygica-box-once-per-box)
4. [Managing channels](#4-managing-channels)
5. [Releasing a new version of the TV app](#5-releasing-a-new-version-of-the-tv-app)
6. [When you visit: the maintenance panel](#6-when-you-visit-the-maintenance-panel)
7. [Troubleshooting](#7-troubleshooting)

Requirements on the build PC: .NET 10 SDK with the `android` workload, the Android SDK (Visual Studio's is fine),
a JDK (the one Visual Studio installs is fine) and, for publishing, the GitHub CLI signed in as the repository owner.
All desktop tools run through the `dotnet` host (`dotnet <tool>.dll`).

---

## 1. The signing key (once)

Both apps are signed with one key, and the key that encrypts everything published to GitHub is **derived from
that signing key**. Nothing secret is in the repository; it lives in `%USERPROFILE%\.gemaktv`:

| File | What it is |
|---|---|
| `gemaktv.keystore` | The app signing key (RSA 4096, PKCS#12, alias `gemaktv`) |
| `keystore.pass` | Its password (two identical lines: apksigner reads one per use) |
| `config.key` | 32-byte AES key derived from the signing key (HKDF-SHA256) |

```bash
dotnet build tools/GemakTV.Publisher -c Release
```

```bash
dotnet tools/GemakTV.Publisher/bin/Release/net10.0/GemakTV.Publisher.dll keys create
```

> **Back up `%USERPROFILE%\.gemaktv` somewhere safe (a password manager or an encrypted USB stick).**
> Android only installs an update signed with the same key. If the key is lost, the TV can never be updated
> again without a visit to reinstall it by hand.

`keys show` prints the key fingerprints; the TV shows the same config-key fingerprint in its maintenance panel.
Building on another PC: copy the folder there (or point the `GEMAKTV_SECRETS` environment variable at it).

## 2. The GitHub bot account (once)

The manager app signs in to GitHub as a dedicated account that may change the channel list and nothing else.
Creating a GitHub account needs an e-mail address, a CAPTCHA and a password, so this part is yours to do.

1. **Create the account** at <https://github.com/signup>, for example `gemaktv-bot`, with an e-mail address you
   control (Gmail's `you+gemaktv@gmail.com` form works). Turn on two-factor authentication for it.
2. **Invite it to the repository** (as `Ryu-Sch`, from this PC):

   ```bash
   gh api -X PUT repos/Ryu-Sch/GemakTV/collaborators/gemaktv-bot -f permission=push
   ```

   Then sign in as the bot and accept the invitation at <https://github.com/Ryu-Sch/GemakTV/invitations>.
3. **Create its access token.** Signed in as the bot: *Settings → Developer settings → Personal access tokens →
   Tokens (classic) → Generate new token (classic)*. Name it "GemakTV Manager", tick only **`public_repo`**, and
   pick an expiry (or none). Copy the token (`ghp_…`).

   *Why classic:* GitHub's fine-grained tokens cannot reach a personal repository where the account is only a
   collaborator. `public_repo` would let the token write to other public repositories the bot can write to, and
   it can write to this one only.
4. **Sign in to GemakTV Manager** with the bot's username and that token as the password. GitHub has not
   accepted real passwords from apps since 2021, so the token *is* the password for the app. The phone remembers
   it, encrypted with a key in the Android Keystore.

### What the bot can and cannot do

Repository rulesets (Settings → Rules) limit the bot to exactly what the manager needs. As the owner you
bypass them all.

| Ruleset | Effect |
|---|---|
| `main: owner only` | Only the owner can push to, force-push or delete `main` (the source code) |
| `published: history is kept` | `published` can only receive normal commits, so nobody can rewrite or delete its history |
| `no other branches` | Only the owner can create branches other than `main` and `published` |
| `tags: owner only` | Only the owner can create, move or delete tags |

Anyone can read the repository (it is public), but the channel list and app builds in it are encrypted.

## 3. Preparing the MyGica box (once per box)

You need the box on the same network as a PC with `adb` (Android SDK platform-tools) and about 15 minutes.

1. **Developer options:** *Settings → About → Build number*, press OK seven times. Then *Settings → Developer
   options*: turn on **USB debugging** and, if offered, **ADB over network**. Note the box's IP address
   (*Settings → Network*).
2. **Connect:**

   ```bash
   adb connect 192.168.1.50:5555
   ```

   Accept the "Allow USB debugging?" prompt on the TV (tick *Always allow*).
3. **Install GemakTV** (the build from `artifacts/`, or from the latest release script run):

   ```bash
   adb install -r artifacts/GemakTV-1.0.7-8.apk
   ```

4. **Make GemakTV the device owner.** This is the strongest kiosk. The screen is pinned, so no other app can
   be opened. Other apps are hidden, so they cannot run in the background. Updates install without a prompt.
   Android only allows this while **no accounts** are signed in on the box, so first remove any Google account
   (*Settings → Accounts*). Then:

   ```bash
   adb shell dpm set-device-owner io.github.ryusch.gemaktv/.AdminReceiver
   ```

   Expected: `Success: Device owner set to package io.github.ryusch.gemaktv`.
   If it says *"Not allowed… accounts"*, an account is still signed in. If it says *"Can't set package … as
   device owner"*, the box's firmware lacks the device-admin feature (the Android TV emulator image is like
   that). Use **home-screen mode** in that case:
   - start GemakTV once (`adb shell am start -n io.github.ryusch.gemaktv/.MainActivity`), press HOME on the
     remote and choose **GemakTV → Always**;
   - allow self-updates once: *Settings → Security → Unknown sources* on.
5. **Reboot** (`adb reboot`). The box should come up straight into GemakTV playing the first channel.
6. Turn **USB debugging** off again if you like; the kiosk does not need it.

To give the box back to normal use for a while, use *Kiosk off for 30 min* in the maintenance panel (section 6).
Android lets only the device-owner app itself, or a factory reset, remove device-owner status for good, so to
return the box to normal use permanently, factory-reset it.

### What the kiosk does

| | Device owner | Home-screen mode |
|---|---|---|
| Starts on boot, HOME returns to GemakTV | yes | yes |
| Other apps cannot be opened (screen pinning) | yes | partly (only HOME is caught) |
| Other launcher apps hidden, so they cannot run in the background | yes | no |
| Background apps asked to stop when GemakTV starts | yes | yes |
| Lock screen and status bar off | yes | no |
| Updates install silently | yes | Android asks "Install?" once per update |

Settings, keyboards and the package installer are never hidden, so a helper can always fix Wi-Fi. If GemakTV
crashes three times within three minutes, it switches the kiosk off for 30 minutes and hands the box back to
its normal launcher, so a bad build can never trap the box.

## 4. Managing channels

Install `artifacts/GemakTV-Manager-1.0.0.apk` on your Android phone (allow installing from your browser or file
manager when asked) and sign in as the bot (section 2).

- **Channels on the TV** is the list as it is saved on GitHub. Reorder with ▲ ▼, remove with the bin, tap a
  channel for its streams. Changes stay on the phone until you press **Save to TV**.
- **Add channels** searches the whole iptv-org database (about 40 000 channels). Accents and punctuation do not
  matter. *Only channels with a free stream* hides channels nobody has published a free stream for.
- **A channel's page** lists its stream addresses in the order the TV tries them, with a health check
  (working / blocked / not responding). Paste your own address under *Stream address* to add one. A pasted
  address goes first, because you trust it most.
- **Save to TV** writes the encrypted list to the `published` branch. If someone else saved meanwhile, the app
  says so instead of overwriting their change.
- On the TV: press **OK**, then **Check For Updated Channel List**.

### About kykNET

kykNET and kykNET & kie are MultiChoice (DStv) subscription channels. iptv-org lists them but has **no stream**
for them: iptv-org only collects streams that are free to watch, and no legitimate free stream exists. Both are
in the list and show *"not set up yet"* on the TV. If you have a stream address you are entitled to use (for
example from an IPTV subscription that includes kykNET), paste it on the channel's page in the manager and save.
The TV picks it up with *Check For Updated Channel List*. While a channel has no stream, ▲ ▼ skip over it.

## 5. Releasing a new version of the TV app

```bash
powershell -File scripts/release.ps1 -VersionName 1.0.6
```

The script raises the version code by one, builds a signed APK for 32-bit ARM (runs on every ARM box), copies
it to `artifacts/`, encrypts it, and commits it together with the updated config to `published` in one commit.
Commit the version bump it makes in `src/GemakTV.Tv/GemakTV.Tv.csproj`. Add `-DryRun` to build without
publishing, and `-Emulator` to include x86 for testing on the Android 6 emulator.

On the TV: **OK → Check For Updates**. The TV downloads, decrypts and checks the update (size, SHA-256,
package name, version and signing certificate), installs it and restarts on the channel it was showing.
The publish branch keeps the two newest builds; older ones are deleted from the branch tip (git history keeps them).

The manager app has no self-update; install new builds on the phone by hand (`ApplicationVersion` in
`src/GemakTV.Manager/GemakTV.Manager.csproj`).

## 6. When you visit: the maintenance panel

With the menu open (press **OK**), press **▲ ▲ ▼ ▼ ◀ ▶ ◀ ▶**. The panel shows the version, kiosk state and key
fingerprint, and offers:

- **Wi-Fi settings** / **All Android settings**: the kiosk steps aside while you are in Settings. Press HOME to
  come back, and the kiosk locks again.
- **Kiosk off for 30 min**: for 30 minutes other apps reappear and the normal launcher is reachable.
- **Close**.

## 7. Troubleshooting

| Symptom | What to do |
|---|---|
| "*X* is not available right now" | Every stream of that channel failed. The TV keeps retrying (after 10 s, 30 s, 1 min, then every 2 min). Check the channel in the manager; the health check shows which addresses still work. |
| "No internet connection" | The TV resumes by itself when the network is back. |
| "Could not check right now" | GitHub could not be reached. The TV falls back to GitHub's CDN when the API's hourly limit is reached. |
| "Updates are not switched on yet" | Home-screen mode without *Unknown sources*: turn it on (section 3). |
| "The update did not work" | Nothing was changed. Check the build was published with the same signing key (`keys show`). |
| Manager: "may not change Ryu-Sch/GemakTV" | The bot has not accepted the collaborator invitation, or the token lacks `public_repo`. |
| Manager: "did not accept this username and password" | The token expired or was revoked; create a new one (section 2.3). |
| Box crashes in a loop | After three crashes the kiosk turns itself off for 30 minutes; `files/crash.log` in the app's storage has details (`adb shell run-as` on debug builds, or `adb logcat -s GemakTV`). |
| Logs | `adb logcat -s GemakTV` shows tuning, failover, updates and kiosk actions. |
