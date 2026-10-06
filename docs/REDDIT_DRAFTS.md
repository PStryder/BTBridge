# Reddit posts and drafts

## Posted

| Date | Subreddit | Post | Draft |
|---|---|---|---|
| 2026-10-06 | r/Battletechgame | [Alpha testers wanted: BattleTech mod that lets an AI play…](https://www.reddit.com/r/Battletechgame/comments/1wzez3y/alpha_testers_wanted_battletech_mod_that_lets_an/) | Draft 3 |

Tester reports from that thread (platform, outcome, logs) go in GitHub issues so they're tracked.

## Drafts

Drafts 1 and 2 are not posted yet. Fill the `[brackets]` and run the checklist first.

### Before posting Drafts 1 and 2

- [x] The GitHub repo is public (confirmed 2026-10-06; not yet promoted anywhere).
- [ ] Play at least one skirmish against the agent-commanded OpFor, and replace the `[OPFOR TEST]` paragraph with what actually happened (good or bad).
- [x] Prebuilt zips published as the v0.1-alpha GitHub pre-release (Windows + macOS).
- [ ] Screenshots or a short clip: the chat overlay over a combat turn, the agent's decision feed, the Begin-Mission-to-salvage run.
- [ ] Re-read the "what doesn't work" list against the current README status table.
- [ ] r/Battletechgame rules on self-promotion and mod posts: check the sidebar before posting.

---

## Draft 1: r/Battletechgame

**Title:** I hooked Claude into BattleTech (vanilla + ModTek) so it can run a merc company, fight missions, or command the OpFor against me

**Body:**

I've been building a mod that lets an AI agent play HBS BattleTech through the game's own logic rather than by clicking the screen. It's called BTBridge, and it's at the "works on my test career" stage, so treat this as a show-and-tell, not a release.

What it can do right now, verified in-game:

- **Read your company:** mechbay, storage, and the mechlab *while you're building*, including unsaved edits, so you can talk builds with it as you go.
- **Refit and repair:** it plans a refit as a preview (steps, C-bills, days, the game's own validation), and only commits when told to. Repairs work like the mech bay's Repair button.
- **Run the campaign loop:** take a contract, negotiate pay/salvage, pick the lance, hit Begin Mission, click through the mission dialogue, fight, go through the after-action report and pick salvage. On my last run, three priority picks on the same partial mech assembled a whole Crab.
- **Handle the boring bits:** events (it reads the full text and options), notifications, the quarterly report, travel, pilot training, hiring, the store, Argo upgrades.
- **Talk to you in-game:** a small chat overlay on the right edge, in the objective yellow, plus an input bar (Ctrl+Shift+T) so you can talk back mid-mission.
- **Read the story:** mission chatter (including the voiced radio lines), campaign conversations with speakers, cinematic subtitles. All spoiler-gated: it only reads what you've already seen unless you tell it otherwise.

[OPFOR TEST: one paragraph on the first skirmish with the agent commanding the enemy. What it did well, what it did badly. The camera follows its units only when they're visible to you, and its orders show on screen only after each unit has acted.]

Things I learned the hard way that might interest modders:
- `LazySingletonBehavior<UIManager>.Instance` *creates* the UIManager if you ask too early. I did, from an overlay's first frame, and the game hung on a black screen at launch.
- `Contract.FinalizeSalvage` empties the list you hand it. My code read that list afterwards and reported "0 picks" for two missions while the salvage was arriving fine.
- The encounter's objective list includes the AI's hidden objectives ("Hidden KILL OBJECTIVE for OpFor1"). The player-facing list is a different one.
- Repairing a mech scraps its destroyed components; it doesn't replace them. The game's own popup says so, which I'd skimmed past.

What it doesn't do (yet):
- **Vanilla 1.9.1 + ModTek only.** RogueTech, BTA and anything using MechEngineer/CleverGirl change the same systems; not supported.
- It's an alpha: prebuilt Windows and macOS zips are on the [v0.1-alpha release](https://github.com/PStryder/BTBridge/releases/tag/v0.1-alpha), and macOS is untested.
- The scripted driver I used for testing won every mission but **got two of my pilots killed** holding a base against two assault 'Mechs. The real thing is the agent thinking through each turn, which is slower (10–30 s per unit).
- Flashpoints are wired up but I haven't played one through.

Repo: https://github.com/PStryder/BTBridge. Happy to answer questions about the hooks; the docs folder has notes on how the game's campaign and combat code fit together.

---

## Draft 2: r/ClaudeAI (or r/mcp)

**Title:** An MCP server that lets Claude play BattleTech: run the campaign, fight missions, and chat with you in-game

**Body:**

I've been working with Claude Code on BTBridge: a C# mod for HBS BattleTech (2018) plus a Python MCP server (76 tools), so an agent can play the game through its real logic instead of screenshots and mouse clicks.

The shape of it:
- **The mod** (Harmony patches, an HTTP bridge on localhost) reads game state and runs actions on the main thread.
- **Combat** hooks the game's own AI decision point. For each unit, the stock AI's choice is captured as a *suggestion*; the agent can accept it or give its own move/attack order. The agent can command your lance, or the enemy's against you.
- **The campaign** layer is a typed interrupt queue (events, popups, reports) plus actions for contracts, travel, pilots, the store, repairs and refits. Changes that cost money preview first and commit on confirm.
- **An in-game chat overlay** for the agent to talk to you, and an input box you can type into. Messages you type can only come from that box, so the agent can't fake operator input.

Design choices that mattered:
- **Preview, then confirm**, for anything that spends C-bills or is permanent. Refit plans are single-use and refuse if the mech, storage or funds changed since the preview.
- **Fog of war is respected.** When the agent commands the enemy, its briefing only shows what its side can see, its on-screen orders are held until each unit acts, and the camera never pans to a unit you can't see.
- **Cheats are a separate MCP server**, only there if you register it, gated by a launch flag *and* a human-only in-game arming hotkey, with an audit log. The point is that ordinary tools never quietly gain cheat behavior.
- **Tests that fail when the guarantee is removed.** Every Harmony hook is checked against the installed game's assemblies (one bad hook stops the whole mod from loading), and the rule tests are mutation-checked.

What worked live: three campaign missions start to finish without a single click (Begin Mission, dialogue, combat, after-action, salvage), repairs and refits, two jumps of travel with events along the way.

What surprised me: most of the bugs weren't in the hooks but in *reporting*. The salvage picks always worked; the code that described them read a list the game had already emptied. The agent was told "0 picks" while the parts arrived.

[OPFOR TEST: one or two sentences.]

Repo: https://github.com/PStryder/BTBridge. Vanilla BattleTech 1.9.1 + ModTek. Prebuilt v0.1 alpha zips for Windows and macOS are on the releases page.

---

## Draft 3: r/Battletechgame, alpha testers wanted (Windows and macOS)

**Posted 2026-10-06:** https://www.reddit.com/r/Battletechgame/comments/1wzez3y/alpha_testers_wanted_battletech_mod_that_lets_an/ (both download links were checksum-verified before posting).

**Title:** Alpha testers wanted: a BattleTech mod that lets an AI play the campaign (or fight you as the OpFor). Windows and macOS

**Body:**

I've been building **BTBridge**, a mod that lets an AI agent play HBS BattleTech through the game's own logic rather than clicking the screen. It can read your company and the mechlab while you build, refit and repair mechs, run the campaign loop (contracts, missions, salvage, travel, events, pilots, the store), command a lance in combat on your side or **as the enemy against you**, and chat with you through a small in-game overlay.

It works on my Windows machine. Now I need other people's setups, and **especially macOS players, because I don't have a Mac**: the Mac build has never been run.

**Downloads (v0.1 alpha):**
- Windows: https://github.com/PStryder/BTBridge/releases/download/v0.1-alpha/BTBridge-v0.1-alpha-windows.zip
- macOS: https://github.com/PStryder/BTBridge/releases/download/v0.1-alpha/BTBridge-v0.1-alpha-macos.zip
- Release page (notes and checksums): https://github.com/PStryder/BTBridge/releases/tag/v0.1-alpha
- The project: https://github.com/PStryder/BTBridge

**You need:** BattleTech **1.9.1** on Steam, **vanilla** (it won't work alongside RogueTech, BTA or other overhaul packs, so use a clean install), and **ModTek v4.5.1**: https://github.com/BattletechModders/ModTek/releases. Back up your saves folder first, as with any mod.

**Windows install:**
1. From the ModTek zip, copy `winhttp.dll`, `doorstop_config.ini` and the `Mods` folder into your BattleTech folder (the one with `BattleTech.exe`; on Steam usually `C:\Program Files (x86)\Steam\steamapps\common\BATTLETECH`).
2. From my zip, copy the `BTBridge` folder into `BATTLETECH\Mods\`.
3. Launch to the main menu. The version text should end in `/W MODTEK`, and `Mods\BTBridge\BTBridge.log` should say `BTBridge 0.1.0 initialized`.

**macOS install:**
1. Install ModTek following its macOS instructions (the game is launched through ModTek's script, set as the Steam launch option). The game folder is usually `~/Library/Application Support/Steam/steamapps/common/BATTLETECH`.
2. From my zip, copy the `BTBridge` folder into ModTek's `Mods` folder (normally `BATTLETECH/Mods`, next to `BattleTech.app`). If macOS quarantines it: `xattr -dr com.apple.quarantine BATTLETECH/Mods/BTBridge`
3. Launch to the main menu and check for `Mods/BTBridge/BTBridge.log`.

Each zip includes a fuller INSTALL guide.

**What to send back** (a comment, a DM, or a GitHub issue), whether it worked or not:
- Windows or Mac (and for Macs: Intel or Apple Silicon, macOS version)
- `Mods/BTBridge/BTBridge.log` (if it's missing, the mod never loaded, which is useful to know too)
- `Mods/.modtek/ModTek.log` and `Mods/.modtek/battletech_log.txt`

The zips are the game mod only; hooking up an AI agent (Claude or any MCP client) uses the MCP server in the repo, described in the README. Getting the mod to load cleanly on more machines is the first step.

It's alpha, so things may break, and when they do the logs are exactly what I need. Thanks!

