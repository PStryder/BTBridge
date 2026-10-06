# BTBridge v0.1 (alpha): macOS install

BTBridge lets an AI agent read and play HBS BattleTech through the game's own logic. This zip is the game mod only. To connect an agent you also need the MCP server from the repository: https://github.com/PStryder/BTBridge

**Alpha software, and macOS is untested so far.** That's why this build exists: if you can try it, the logs below are exactly what's needed, whether it works or not. Built for **vanilla BattleTech 1.9.1 + ModTek**; not compatible with RogueTech, BTA or other overhaul packs. Back up your saves before trying any mod. On Apple Silicon the game runs under Rosetta; please say which Mac you have when reporting.

## 1. Install ModTek (once)

1. Download ModTek v4.5.1 from https://github.com/BattletechModders/ModTek/releases
2. Follow ModTek's macOS instructions from its release / README. On macOS the game is started through ModTek's launch script (set as the Steam launch option) rather than a DLL.
   The Steam game folder is usually `~/Library/Application Support/Steam/steamapps/common/BATTLETECH` (it contains `BattleTech.app`).
3. Launch the game once. The main menu's version text should end in `/W MODTEK`. Note where ModTek's `Mods` folder is (normally `BATTLETECH/Mods`, next to `BattleTech.app`).

## 2. Install BTBridge

Copy the `BTBridge` folder from this zip into that `Mods` folder, so you have:

```
BATTLETECH/Mods/ModTek/...
BATTLETECH/Mods/BTBridge/BTBridge.dll
BATTLETECH/Mods/BTBridge/mod.json
```

If macOS quarantines the download, clear it on the folder: `xattr -dr com.apple.quarantine BATTLETECH/Mods/BTBridge`

## 3. Check it loaded

Launch to the main menu, then either:
- open `Mods/BTBridge/BTBridge.log` and look for `BTBridge 0.1.0 initialized; listening on 127.0.0.1:8787`, or
- in Terminal: `curl http://127.0.0.1:8787/health` (web browsers are deliberately refused).

In-game: **Ctrl+Shift+T** opens the chat input, **Ctrl+Shift+O** the overlay settings, **Ctrl+Shift+H** the history (Control, not Command).

## Please report back

Whether it worked or not, send:
- your Mac model / chip (Intel or Apple Silicon) and macOS version;
- `Mods/BTBridge/BTBridge.log` (missing = the mod never loaded, which is useful to know too);
- ModTek's logs from `Mods/.modtek/` (`ModTek.log` and `battletech_log.txt`).

To uninstall, delete `Mods/BTBridge`.

Issues: https://github.com/PStryder/BTBridge/issues
