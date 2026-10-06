# btai

The MCP servers for BTBridge. The [repository README](../README.md) covers the mod, the bridge API, and what's verified in-game.

## Servers

| Script | What it is |
|---|---|
| `btai-mcp` | The main server: 76 tools. Offline tools work without the game; live tools talk to the BTBridge mod on `BTBRIDGE_URL` and say clearly when the game isn't reachable |
| `btai-cheats-mcp` | The operator cheat tier, kept separate so it's only there if you register it. Its routes don't exist unless the game was launched with cheats enabled |

```json
{
  "mcpServers": {
    "battletech": { "command": "uv", "args": ["run", "--directory", "server", "btai-mcp"] }
  }
}
```

## Tool families

| Prefix | Covers |
|---|---|
| `catalog_*`, `check_mech_build` | offline: components, chassis, stock mechs from the game's JSON; a build pre-check |
| `game_status`, `campaign_*`, `mechlab_current`, `validate_mech_build` | company, mechbay, storage, the live mechlab, refits and repairs |
| `skirmish_*` | saved custom skirmish mechs and lances |
| `sim_*`, `contracts_list`, `contract_*`, `mission_*`, `aar_*`, `starmap`, `travel*`, `pilots`, `pilot_train`, `hiring_hall`, `hire_pilot`, `dismiss_pilot`, `store*`, `argo_*`, `finances`, `flashpoint*` | the campaign loop |
| `combat_*` | control modes, briefing, decisions, standing orders, mission dialogue |
| `dialog_transcript`, `story_*`, `video_*` | dialogue history, spoiler-gated story text, cinematic status and skip |
| `overlay_*` | the in-game chat: say, history, the operator inbox |

Tools that change the campaign preview first where the game allows it (refit, repair, training, travel), and say so in their descriptions. Ask the player before confirming anything that spends money or is permanent.

## Tests

```bash
uv run pytest -q
```

`tests/test_tools.py` checks that every live tool sends the request the mod's route table expects, and that every route it calls is declared in `mod/BTBridge/Routes.cs`.
