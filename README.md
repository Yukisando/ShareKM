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
3. On either PC, double-click the tray icon to open **Settings**. The other PC shows up under
   "Other computers running ShareKM" within a few seconds: pick it and press **Connect**. You can
   also type its IP address or computer name, or use the tray menu: **Connect to >**.
   Connecting from one side is enough, and the choice is remembered for next time.
4. When the icon turns green the two are connected.

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
- To change the hotkey, open Settings, press **Record** and then the key you want (Pause, F-keys,
  Insert, ...). Esc cancels. It applies at once. Ctrl, Shift, Alt and Windows keys can't be used.
- Copy text on either PC and paste it on the other. This works whichever PC you are controlling.
- **Disconnect** in Settings drops the link and stops this PC accepting it until you press
  **Connect** or restart ShareKM.
- **Start with Windows** in the tray menu adds ShareKM to your user's startup programs.

## Config

`%APPDATA%\ShareKM\config.json`, created on first run:

```json
{ "Peer": "", "Port": 24800, "Hotkey": "Scroll" }
```

`Peer` is the computer this PC dials (set by Connect). `Hotkey` is a .NET `Keys` name (for
example `Scroll`, `Pause`, `F12`). Both PCs must use the same `Port`. Restart ShareKM after
editing the file by hand.

## Limitations

- **No encryption or pairing.** Anyone who can reach TCP port 24800 on either PC can type and
  click on it and read what you copy. Anyone on the network also sees each PC's name through the
  UDP beacons. Only use it on a network you trust, and keep the firewall rules limited to private
  networks.
- Windows does not let it control UAC prompts, the lock or sign-in screen, or windows of programs
  running as administrator, unless ShareKM itself runs as administrator.
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
