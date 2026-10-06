# Reddit drafts (not posted)

Drafts for announcing battletech-ai. Nothing here has been posted. Fill the `[brackets]` and run the checklist first.

## Before posting

- [x] The GitHub repo is public (confirmed 2026-10-06; not yet promoted anywhere).
- [ ] Play at least one skirmish against the agent-commanded OpFor, and replace the `[OPFOR TEST]` paragraph with what actually happened (good or bad).
- [ ] Decide whether to ship a prebuilt `BTBridge.dll` as a GitHub release. Right now it's build-from-source only (needs the .NET SDK and the game installed), which will stop most r/Battletechgame readers.
- [ ] Screenshots or a short clip: the chat overlay over a combat turn, the agent's decision feed, the Begin-Mission-to-salvage run.
- [ ] Re-read the "what doesn't work" list against the current README status table.
- [ ] r/Battletechgame rules on self-promotion and mod posts: check the sidebar before posting.

---

## Draft 1: r/Battletechgame

**Title:** I hooked Claude into BattleTech (vanilla + ModTek) so it can run a merc company, fight missions, or command the OpFor against me

**Body:**

I've been building a mod that lets an AI agent play HBS BattleTech through the game's own logic rather than by clicking the screen. It's called battletech-ai, and it's at the "works on my test career" stage, so treat this as a show-and-tell, not a release.

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
- No prebuilt download [unless that changed]: you'd build it from source.
- The scripted driver I used for testing won every mission but **got two of my pilots killed** holding a base against two assault 'Mechs. The real thing is the agent thinking through each turn, which is slower (10–30 s per unit).
- Flashpoints are wired up but I haven't played one through.

Repo: https://github.com/PStryder/battletech-ai. Happy to answer questions about the hooks; the docs folder has notes on how the game's campaign and combat code fit together.

---

## Draft 2: r/ClaudeAI (or r/mcp)

**Title:** An MCP server that lets Claude play BattleTech: run the campaign, fight missions, and chat with you in-game

**Body:**

I've been working with Claude Code on battletech-ai: a C# mod for HBS BattleTech (2018) plus a Python MCP server (76 tools), so an agent can play the game through its real logic instead of screenshots and mouse clicks.

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

Repo: https://github.com/PStryder/battletech-ai. Vanilla BattleTech 1.9.1 + ModTek on Windows; build from source for now.

---

## Draft 3: r/Battletechgame, macOS testers wanted

Before posting: package a prebuilt release zip (`BTBridge.dll`, `mod.json`, `INSTALL-macOS.md`) and replace the bracketed line.

**Title:** Looking for macOS BattleTech players to help test a mod (ModTek, 15–30 minutes)

**Body:**

I've been building a mod that lets an AI agent play HBS BattleTech through the game's own logic: running the campaign, fighting missions, or commanding the OpFor against you, with a small in-game chat overlay. It works on Windows, and I've just added macOS support, but I don't have a Mac to test it on. That's where I need help.

**What I'm looking for:** a few people with BattleTech on a Mac (Steam, v1.9.1), ideally one Intel Mac and one Apple Silicon (the game runs under Rosetta there).

**What the test involves:**
1. Install ModTek using its macOS instructions, if you don't already have it.
2. Drop in the mod. [I'll provide a prebuilt zip, so no compiling.]
3. Launch to the main menu and check that the version string shows ModTek.
4. Send me two log files: `BTBridge.log` from the mod folder, and ModTek's log from `Mods/.modtek/`.

That's the core test: does it load. If you're up for more, I'll have a short checklist of 2–3 things to try in a skirmish.

**Things to know:**
- **Vanilla only.** It's built for vanilla BattleTech + ModTek. It won't work alongside RogueTech, BTA or other overhaul packs, so use a clean install or a separate copy.
- **Saves:** it doesn't touch them unless you use it in a campaign. Back up your saves folder anyway, as with any mod.
- **It's an early project,** so if something breaks, the logs are exactly what I need. A failure is a useful result.

Repo, with more on what it does: https://github.com/PStryder/battletech-ai

Comment here or DM me if you're willing. Thanks!
