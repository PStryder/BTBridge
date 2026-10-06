# BTBridge v0.1 (alpha): Windows install

BTBridge lets an AI agent read and play HBS BattleTech through the game's own logic. This zip is the game mod only. To connect an agent you also need the MCP server from the repository: https://github.com/PStryder/battletech-ai

**Alpha software.** Built for **vanilla BattleTech 1.9.1 + ModTek**. It is not compatible with RogueTech, BTA or other overhaul packs: use a clean install. Back up your saves folder before trying any mod.

## 1. Install ModTek (once)

1. Download ModTek v4.5.1 from https://github.com/BattletechModders/ModTek/releases
2. From the zip, copy `winhttp.dll`, `doorstop_config.ini` and the `Mods` folder (with `ModTek` inside) into your BattleTech folder, the one with `BattleTech.exe`.
   For Steam that's usually `C:\Program Files (x86)\Steam\steamapps\common\BATTLETECH`.
3. Launch the game once. The main menu's version text should end in `/W MODTEK`.

## 2. Install BTBridge

Copy the `BTBridge` folder from this zip into `BATTLETECH\Mods\`, so you have:

```
BATTLETECH\Mods\ModTek\...
BATTLETECH\Mods\BTBridge\BTBridge.dll
BATTLETECH\Mods\BTBridge\mod.json
```

## 3. Check it loaded

Launch to the main menu, then either:
- open `Mods\BTBridge\BTBridge.log` and look for `BTBridge 0.1.0 initialized; listening on 127.0.0.1:8787`, or
- run `curl http://127.0.0.1:8787/health` in a terminal (web browsers are deliberately refused).

In-game: **Ctrl+Shift+T** opens the chat input, **Ctrl+Shift+O** the overlay settings, **Ctrl+Shift+H** the history.

## If something goes wrong

Send these files (they contain no personal data beyond your Windows user name in paths):
- `Mods\BTBridge\BTBridge.log`
- `Mods\.modtek\ModTek.log`
- `Mods\.modtek\battletech_log.txt`

To uninstall, delete `Mods\BTBridge`. To remove ModTek too, delete `winhttp.dll`, `doorstop_config.ini` and `Mods\ModTek`.

Issues: https://github.com/PStryder/battletech-ai/issues
