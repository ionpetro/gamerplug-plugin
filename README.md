<img src="assets/logo.png" width="72" alt="GamerPlug">

# GamerPlug

**Looking for group, from your agent.** Ask for someone to play with and get real players from [GamerPlug](https://gamerplug.app), the teammate-finding app where every profile leads with gameplay clips.

> "Find me a chill duo for Apex on PS5. I'm Plat and I want someone with a mic."

## Tools

| Tool | What it does |
| --- | --- |
| `find_lfg_players` | Find players for a game. Optional filters: `platform` (PC, PS5, Xbox, Nintendo Switch), `mic` (`required` or `preferred`), `play_style` (Aggressive, Balanced, Conservative), `vibe` (Chill, Competitive, Funny, Encouraging, Serious), `apex_rank` (Apex Legends only, matches within one tier), `limit` (1–10, default 5). |
| `view_player` | A player's public profile by gamertag: games, platforms, play style, mic usage and a link to their clips. |
| `list_games` | The games you can find players for (Apex Legends, Fortnite, Call of Duty, Valorant, League of Legends, Rocket League, Overwatch 2, Marvel Rivals and more). |

All tools are read-only. No API key or account is needed to use the plugin.

## Teaming up

Results link to each player's GamerPlug profile. To play together, get the free app at [gamerplug.app/download](https://gamerplug.app/download) and swipe right on them; when they swipe right too, you get a chat to set up a game. The plugin can't message players for you.

## Setup

Install from the marketplace, or add the remote MCP server yourself:

```json
{
  "mcpServers": {
    "gamerplug": { "url": "https://gamerplug-mcp.vercel.app/mcp" }
  }
}
```

Grok CLI: `grok mcp add --transport http gamerplug https://gamerplug-mcp.vercel.app/mcp`

## Privacy

- The plugin only returns what a player's public GamerPlug profile already shows. It never returns age, gender, date of birth or contact handles, and it strips emails and phone numbers from bios.
- It only returns players aged 18 or over.
- It stores nothing about you: your search filters are used to answer the request and are not saved or used for training.

[Privacy Policy](https://gamerplug.app/privacy) · [Terms](https://gamerplug.app/tac)

## Support

support@gamerplug.app · [gamerplug.app/contact](https://gamerplug.app/contact)

## GamerPlug Clipper

This repo also hosts **GamerPlug Clipper**, a Windows desktop game-clipping app (replay buffer + hotkey save + upload to a GamerPlug profile). See [`clipper/README.md`](clipper/README.md). The looking-for-group MCP plugin above is unchanged.
