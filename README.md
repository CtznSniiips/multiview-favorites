# Multiview Favorites for Emby

An Emby server plugin that turns a user's **favorite Live TV channels** into a [Dispatcharr Multiview](https://github.com/swvn-dispatch/dispatcharr-multiview) layout.

Favorite a channel in any Emby app (Samsung, Android TV, Roku, web) and a few seconds later it's a tile in the multiview. Unfavorite it and it drops out. The multiview itself is an ordinary channel in Emby, so it plays on every client.

```
Emby favorites (user X) ──► match by channel number ──► lowest N ──► Dispatcharr Multiview layout ──► M3U ──► Emby Live TV
```

## How it works

1. The plugin reads the selected Emby user's favorite Live TV channels.
2. It matches each one to a Dispatcharr channel by **channel number** (`5`, `5.0`, and `5-1`/`5.1` style subchannels all compare correctly).
3. It orders the matches by the **Tile order** setting and keeps the first **N**, where N is the *Maximum streams* dropdown (2–9, and Multiview recommends at most 4). The first one is tile 1.
4. It writes those channels into a Multiview layout named by *Multiview channel name*, in Classic mode. If the layout doesn't exist it's created, and Multiview's M3U/EPG is regenerated so it shows up.

A sync runs when:
- the chosen user favorites or unfavorites a Live TV channel (debounced 5 s)
- the plugin settings are saved
- **Sync now** is pressed on the settings page
- the *Sync favorites to Dispatcharr Multiview* scheduled task runs (hourly by default, under Scheduled Tasks → Live TV)
- Emby starts (after a 60 s delay)

Only keys the plugin owns (the layout's name, style, audio, and channel slots) are changed, and only when they differ. Other layouts and Multiview's global settings are never touched.

## Requirements

- Emby Server 4.8 or newer. Built and tested against 4.9.5.0.
- Dispatcharr with the **Multiview** plugin installed and its PyAV engine installed.
- In the Multiview plugin settings, **Web Dashboard = Enabled**, then restart Dispatcharr. This plugin talks to Multiview's dashboard REST API on port **9292**, so that port has to be reachable from Emby (`9292:9292` in Dispatcharr's compose file if Emby runs elsewhere).
- A Dispatcharr user login (the same one you'd use for the Multiview dashboard).
- Emby Live TV set up from Dispatcharr's M3U/HDHR output, so that Emby's channel numbers **are** Dispatcharr's channel numbers.

## Install

1. Download the prebuilt `Emby.MultiviewFavorites.dll`.
2. Copy it into Emby's `plugins` folder:
   - Docker / Linux: `/config/plugins` or `/var/lib/emby/plugins`
   - Windows: `%AppData%\Emby-Server\programdata\plugins`
3. Restart Emby, then open **Settings → Plugins → Multiview Favorites**.

## Setup

1. Enter the **Multiview server URL** (e.g. `http://192.168.1.10:9292`, *not* the main Dispatcharr port), the dashboard path (`/dash` unless you changed it), and your Dispatcharr username and password. Click **Test connection**.
2. Pick the **Emby user** whose favorites drive the multiview.
3. Set the **Multiview channel name**, **Maximum streams**, **Layout style**, and **Audio**.
4. Tick **Enable sync** and click **Save**. The layout is created in Dispatcharr right away.
5. **One-time step in Dispatcharr:** the new layout appears as a stream under the *Dispatcharr Multiview* M3U account. Add or assign it to a channel (give it a number that won't clash, e.g. 9999), the same way you would any other stream.
6. Refresh the guide in Emby (the plugin queues **Refresh Guide** automatically when it creates or renames the layout) and the multiview channel appears in Live TV.

**Preview** on the settings page shows each favorite and whether it became a tile, went over the limit, had no Dispatcharr match, and so on, without changing anything.

## Settings

| Setting | Notes |
|---|---|
| Enable sync | Master switch. Preview still works when it's off. |
| Multiview server URL | Multiview plugin port, default 9292. |
| Dashboard mount path | Must match Multiview's *Dashboard Mount Path*. |
| Emby user | Whose favorites are used. Favorites are per user in Emby. |
| Multiview channel name | Layout name in Dispatcharr. Renaming here renames the same layout (it's tracked by id). |
| Maximum streams | 2–9. The first N in tile order win. Every tile is a separate upstream stream through Dispatcharr's proxy. |
| Tile order | *Lowest channel number first*, *Oldest favorite first*, or *Manual* (see below). |
| Layout style | Auto grid / Featured / Top featured, or *Don't change* to keep a custom style set in Dispatcharr. Tile 1 is the featured tile. |
| Audio | First tile, All tiles (one audio track per tile, switchable in the player), or *Don't change*. |
| Restart the multiview if it's playing… | Off by default. When off, changes apply the next time the channel is tuned. When on, viewers are disconnected so the player reconnects with the new tiles. |
| Refresh the Emby guide… | Queues Emby's Refresh Guide task after the layout is created or renamed. |

## Tile order

The tile order decides both **which** favorites make the cut when you have more than *Maximum streams*, and **where** they sit. Tile 1 is the large tile in the Featured layouts, and *Audio → First tile* follows it.

- **Lowest channel number first** (default).
- **Oldest favorite first:** the channel you've had favorited longest is tile 1. This uses the timestamp Emby records when an item's favorite flag changes, so it works for channels favorited before the plugin was installed. Unfavoriting and re-favoriting a channel moves it to the back. The Preview table shows each favorite's date.
- **Manual:** a list of the user's favorites appears on the settings page. Use the arrows to arrange it, then **Save**. Tile labels update live as you move rows and change the stream limit. Channels favorited later go to the end (in channel-number order) until you move them.
  **Reset to channel order** starts over.

Changing only the order counts as a tile change, so it follows the *Restart the multiview if it's playing* setting like any other change.

## Behaviour notes

- **Fewer than 2 matches:** the layout is still saved, but Multiview won't play a layout with fewer than 2 channels. The settings page and log show a warning.
- **No match:** a favorite whose number doesn't exist in Dispatcharr is skipped and listed in Preview.
- **Duplicate numbers:** if several Dispatcharr channels share a number, the first is used and a warning is logged.
- **The multiview itself is never tiled,** even if it's favorited. Any channel named like a Multiview layout is skipped.
- **Regex layouts:** if the target layout was in Regex mode, it's switched to Classic so favorites control it.
- The Dispatcharr password is stored in the plugin's XML config on the Emby server (admin access only), like other Emby plugin credentials.