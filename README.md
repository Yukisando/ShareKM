<img src="Assets/icon.png" width="96" alt="" align="right">

# ShareKM

A tiny tray app that shares one keyboard and mouse between two Windows PCs on the same network,
plus clipboard text in both directions. Press one hotkey to switch which PC you control. No
install, no account, no PowerToys: one `.exe`.

## Download

Get `ShareKM.exe` from the [latest release](https://github.com/Yukisando/ShareKM/releases/latest)
and copy it to both PCs. It is a single self-contained file (no .NET install needed).

The exe is not code-signed, so Windows SmartScreen may say "Windows protected your PC" the first
time: click **More info**, then **Run anyway**. The source is all in this repo if you prefer to
build it yourself (see below).

## Setup

1. Copy `ShareKM.exe` to both PCs and run it on both. It lives in the tray.
2. The first time, Windows Firewall asks about network access: allow it on **private** networks.
   ShareKM uses TCP port 24800 (the link) and UDP port 24801 (finding the other PC). If you
   dismissed the prompt, allow `ShareKM.exe` inbound on private networks in Windows Defender Firewall.
3. **Pair the two PCs.** Settings opens by itself the first time. Under **Security**, press
   **Generate** on one PC (or type a passphrase of your own, at least 8 characters), press
   **Apply**, then enter the same passphrase on the other PC and press **Apply** there too.
   Without a passphrase ShareKM does not connect at all.
4. On either PC, double-click the tray icon to open **Settings**. The other PC shows up under
   **Other computers** within a few seconds: pick it and press **Connect**. You can
   also type its IP address or computer name, or use the tray menu: **Connect to >**.
   Connecting from one side is enough, and the choice is remembered for next time.
5. When the icon turns green the two are connected, and Security shows **Encrypted (AES-256-GCM)**.

Tray icon: grey = not connected, green = connected and controlling this PC,
orange = this PC's keyboard and mouse go to the other PC, blue = the other PC is controlling this one.

## Use

- **Scroll Lock** (default) is the hotkey, and it does the same thing on either keyboard:
  - on the PC that is forwarding: come back to this PC;
  - on the PC being controlled: give control back to the other PC;
  - otherwise: start controlling the other PC.

  The hotkey key is always taken by ShareKM (so the Scroll Lock light no longer toggles).
- **Failsafes**, in case you are ever stuck controlling the other PC:
  - hold **Esc for 3 seconds**: you are back on this PC (the other PC also gets that Esc press);
  - if Windows has dropped ShareKM's keyboard hook, pressing the hotkey is noticed anyway: ShareKM
    reinstalls the hook and gives you this PC back. A dropped mouse hook is reinstalled too;
  - if the connection drops, forwarding stops at once.
- Shortcuts with the Windows key go to the other PC too. ShareKM re-installs its keyboard hook each
  time you switch, so apps started after it (PowerToys, Logi Options, ...) cannot catch them first.
  Win+L and Ctrl+Alt+Del always stay on the PC they are pressed on (Windows does not let any app take them).
- To change the hotkey, open Settings, press **Record** and then the key you want (Pause, F-keys,
  Insert, ...). Esc cancels. It applies at once. Ctrl, Shift, Alt and Windows keys can't be used.
- Copy text on either PC and paste it on the other. This works whichever PC you are controlling.
- **Mouse update rate** (Settings > Performance) sets how often pointer movement is sent to the
  other PC while you control it: 60 to 1000 Hz, default 200 Hz. Movement in between is added up
  and sent in one message; clicks, wheel and keys always go out after the movement before them.
  Higher is smoother but uses more Wi-Fi and CPU. It only matters on the PC you are controlling
  from, and it applies at once.
- **Disconnect** in Settings drops the link and stops this PC accepting it until you press
  **Connect** or restart ShareKM.
- **Start with Windows** (tray menu, or Settings > Controls) starts ShareKM when you sign in.

### Run as administrator

Windows does not let a normal program see or type into windows that run as administrator (admin
terminals, Task Manager, installers): while one of them is in front, ShareKM can neither read your
keyboard and mouse there (on the PC you control from) nor type into it (on the other PC). Turn on
**Run as administrator** in Settings > Controls **on both PCs** to fix this.

- Turning it on shows a warning first (see [Security](#security)): an elevated ShareKM lets anyone
  with your passphrase, or malware on a paired PC, type into admin windows. Use it only on a network
  you trust, and only when you need it.
- Windows then asks for permission once (UAC), and ShareKM restarts as administrator. If you say
  No, nothing changes. Turning it off restarts ShareKM normally.
- With **Start with Windows** on, administrator mode uses a scheduled task named "ShareKM"
  (at sign-in, highest privileges, allowed on battery, no time limit) instead of the normal startup
  entry, so it starts elevated without a prompt each time. Turning administrator mode off goes back
  to the normal startup entry and removes the task.
- If administrator mode is on but ShareKM was started normally, it does not ask again by itself:
  Settings shows "Not running as administrator yet" with a **Restart as administrator** button.
- Even as administrator it cannot control the UAC prompt itself, the lock or sign-in screen, or
  Ctrl+Alt+Del. That would need a signed system service, which ShareKM is not.

The Settings window follows the Windows light or dark theme (and switches live when you change
it), uses your Windows accent colour, and can be used with the keyboard (Tab, Enter, Esc, and the
underlined Alt shortcuts).

### Updates

Settings > Updates shows your version. ShareKM checks GitHub for a newer release a few seconds after
it starts and once a day, and shows a notification when there is one. **Check for updates** asks
right away; **Update to x.y.z** downloads `ShareKM.exe` from the
[latest release](https://github.com/Yukisando/ShareKM/releases/latest), checks its SHA-256 against
the digest GitHub publishes, replaces the exe in place and restarts ShareKM (as administrator if
it was). The previous exe is kept as `ShareKM.exe.old` until the next start. ShareKM needs write
access to the folder the exe is in. Update both PCs.

The only thing ShareKM sends to GitHub is the request for the latest release (with a `ShareKM/x.y.z`
user agent).

## Config

`%APPDATA%\ShareKM\config.json`, created on first run:

```json
{ "Peer": "", "Port": 24800, "Hotkey": "Scroll", "MouseRateHz": 200, "PassphraseProtected": "AQAAANCM...", "RunAsAdmin": false }
```

`Peer` is the computer this PC dials (set by Connect). `Hotkey` is a .NET `Keys` name (for
example `Scroll`, `Pause`, `F12`). Both PCs must use the same `Port`. `MouseRateHz` is the mouse
update rate (60 to 1000; a missing or invalid value means 200). `PassphraseProtected` is the
shared passphrase encrypted with Windows DPAPI for your Windows user, so it can only be read by your
account on this PC. Set it in Settings. If you write a plain `"Passphrase": "..."` into the file
instead, ShareKM reads it once and replaces it with the protected form. `RunAsAdmin` is the
administrator mode (`true` or `false`; anything else means `false`); change it in Settings so the
restart and the startup task are handled. Restart ShareKM after editing the file by hand.

## Security

- **Pairing:** only a PC that knows the same passphrase can connect. On every connection both PCs
  prove they know it without sending it: the passphrase is stretched with PBKDF2-HMAC-SHA256
  (600,000 rounds), fresh session keys come from HKDF-SHA256 and random values from both sides, and
  each side checks the other's HMAC-SHA256 proof.
- **Encryption:** after that, everything on the link (keystrokes, mouse, clipboard text, control
  messages) is encrypted and checked with AES-256-GCM. A frame that was changed, replayed, dropped
  or reordered ends the connection.
- **Not protected:** the discovery broadcasts on UDP 24801 are plain text and contain the computer
  name and port, so others on the network can see that ShareKM runs. Which PCs talk to each other,
  and when, is also visible.
- **Choose a strong passphrase:** someone who records a handshake on your network, or tricks your
  PC into connecting to theirs, can try to guess the passphrase offline. Generate makes an 80-bit
  random one, which cannot be guessed; short or common passphrases can.
- Failed attempts are slowed down: a PC that keeps failing is refused for a while, and ShareKM
  only retries every 10 seconds after a mismatch.
- **1.1 cannot connect to 1.0.** Update both PCs. ShareKM tells you when the other PC runs an older
  version.
- **Administrator mode raises the stakes:** with [Run as administrator](#run-as-administrator) on,
  whoever has your passphrase (or controls a paired PC) can also type into admin windows on this
  PC. Turn it on only when you need it.

## Limitations

- **Security depends on the passphrase.** Anyone who has it, or controls one of the paired PCs, can
  type and click on the other one and read what you copy. Keep the firewall rules limited to
  private networks. See [Security](#security) above.
- Windows that run as administrator need [Run as administrator](#run-as-administrator) on
  both PCs. The UAC prompt itself and the lock or sign-in screen cannot be controlled at all.
- Ctrl+Alt+Del cannot be forwarded. It always goes to the PC the keyboard is plugged into.
- Clipboard sharing covers text only (up to about 1 million characters), not files or images.
- Mouse movement is sent as relative motion, so pointer speed and acceleration depend on both PCs'
  mouse settings.
- Discovery only works on the same subnet. Networks that block broadcasts (some guest Wi-Fi and
  corporate networks) need the address typed in.
- Exactly two PCs: connecting to a third replaces the current link.
- Windows only.

## Build from source

Needs the [.NET SDK](https://dotnet.microsoft.com/download) (10.0 or newer).

```
dotnet publish -c Release -r win-x64 -o publish
```

The result is `publish\ShareKM.exe`.

## License

[PolyForm Noncommercial 1.0.0](LICENSE): you may use, copy and modify ShareKM for any
non-commercial purpose, but you may not sell it or use it commercially. This is a
source-available license, not an OSI-approved open-source one.

Copyright Nathan de Castro.
