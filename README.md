# Multiview Favorites for Emby 

An Emby server plugin that turns your users' **favorite Live TV channels** into [Dispatcharr Multiview](https://github.com/swvn-dispatch/dispatcharr-multiview) layouts, one multiview channel per user (or as many as you like).

Favorite a channel in any Emby app (Samsung, Android TV, Roku, web) and a few seconds later it's a tile in your multiview. Unfavorite it and it drops out. Each multiview is an ordinary channel in Emby, so it plays on every client.

```
Emby favorites (Steve) ──► match by channel number ──► first N ──► "Steve's Favorites" layout ──┐
Emby favorites (Kids)  ──► match by channel number ──► first N ──► "Kids' Favorites" layout  ──┴─► M3U ──► Emby Live TV
```

<p align="center">
  <img src="https://github.com/CtznSniiips/multiview-favorites/blob/main/src/Emby.MultiviewFavorites/thumb.png?raw=true" width="256">
</p>

## How it works

For each multiview channel you set up:

1. The plugin reads that multiview's Emby user's favorite Live TV channels.
2. It matches each one to a Dispatcharr channel by **channel number** (`5`, `5.0`, and `5-1`/`5.1` style subchannels all compare correctly).
3. It orders the matches by the **Tile order** setting and keeps the first **N**, where N is the *Maximum streams* dropdown (2–9, and Multiview recommends at most 4). The first one is tile 1.
4. It writes those channels into the Multiview layout named by *Multiview channel name*, in Classic mode. If the layout doesn't exist it's created, and Multiview's M3U/EPG is regenerated so it shows up.

All multiviews are synced together, and every change goes to Dispatcharr as a single settings update.

A sync runs when:
- a user with a multiview favorites or unfavorites a Live TV channel (debounced 5 s)
- the plugin settings are saved
- **Sync all now** is pressed on the settings page
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
2. Under **Multiview channels**, click **Add one for each user** to get a "*Name*'s Favorites" multiview for every Emby user, or **Add multiview** to add them one at a time.
3. Select a multiview to edit its **Emby user**, **Multiview channel name**, **Maximum streams**, **Tile order**, **Layout style**, and **Audio**.
4. Tick **Enable sync** and click **Save**. The layouts are created in Dispatcharr right away.
5. **One-time step in Dispatcharr (per multiview):** each new layout appears as a stream under the *Dispatcharr Multiview* M3U account. Add or assign it to a channel (give it a number that won't clash, e.g. 9001, 9002, …), the same way you would any other stream.
6. Refresh the guide in Emby (the plugin queues **Refresh Guide** automatically when it creates, renames or removes a layout) and the multiview channels appear in Live TV.

**Preview** (in each multiview's editor) shows each favorite and whether it became a tile, went over the limit, had no Dispatcharr match, and so on, using the settings on screen, saved or not. Nothing is changed.

## Settings

Shared by all multiviews:

| Setting | Notes |
|---|---|
| Enable sync | Master switch. Preview still works when it's off. |
| Multiview server URL | Multiview plugin port, default 9292. |
| Dashboard mount path | Must match Multiview's *Dashboard Mount Path*. |
| Restart a multiview if it's playing… | Off by default. When off, changes apply the next time the channel is tuned. When on, viewers are disconnected so the player reconnects with the new tiles. |
| Refresh the Emby guide… | Queues Emby's Refresh Guide task after a layout is created, renamed or removed. |

Per multiview:

| Setting | Notes |
|---|---|
| Sync this multiview | Untick to pause it. Its Dispatcharr layout is left as it is. |
| Emby user | Whose favorites are used. Favorites are per user in Emby. |
| Multiview channel name | Layout name in Dispatcharr. Must be different for each multiview. Renaming here renames the same layout (it's tracked by id). |
| Maximum streams | 2–9. The first N in tile order win. Every tile is a separate upstream stream through Dispatcharr's proxy. |
| Tile order | *Lowest channel number first*, *Oldest favorite first*, or *Manual* (see below). |
| Layout style | Auto grid / Featured / Top featured, or *Don't change* to keep a custom style set in Dispatcharr. Tile 1 is the featured tile. |
| Audio | First tile, All tiles (one audio track per tile, switchable in the player), or *Don't change*. |

## Tile order

The tile order decides both **which** favorites make the cut when you have more than *Maximum streams*, and **where** they sit. Tile 1 is the large tile in the Featured layouts, and *Audio → First tile* follows it.

- **Lowest channel number first** (default).
- **Oldest favorite first:** the channel you've had favorited longest is tile 1. This uses the timestamp Emby records when an item's favorite flag changes, so it works for channels favorited before the plugin was installed. Unfavoriting and re-favoriting a channel moves it to the back. The Preview table shows each favorite's date.
- **Manual:** a list of the user's favorites appears on the settings page. Use the arrows to arrange it, then **Save**. Tile labels update live as you move rows and change the stream limit. Channels favorited later go to the end (in channel-number order) until you move them.
  **Reset to channel order** starts over.

Changing only the order counts as a tile change, so it follows the *Restart the multiview if it's playing* setting like any other change.

## Multiple multiview channels

- **One per user, or more:** each multiview is independent. The usual setup is one per user, but two multiviews can follow the same user (with different stream limits or tile orders, say).
- **Names must be unique:** each multiview gets its own Dispatcharr layout, and layouts are matched by name. The settings page won't save two with the same name.
- **Pausing vs removing:** untick *Sync this multiview* to stop updating one but keep its layout. **Remove multiview** deletes its layout from Dispatcharr on the next save (you're asked to confirm). The Dispatcharr channel you mapped to it is left in place with no stream.
- **Upgrading from 1.1:** your existing multiview is moved into the list automatically on first start, keeping its layout, user, order and all other settings.

## Behaviour notes

- **Fewer than 2 matches:** the layout is still saved, but Multiview won't play a layout with fewer than 2 channels. The settings page and log show a warning.
- **No match:** a favorite whose number doesn't exist in Dispatcharr is skipped and listed in Preview.
- **Duplicate numbers:** if several Dispatcharr channels share a number, the first is used and a warning is logged.
- **Multiviews are never tiled,** even if favorited. Any channel named like one of your multiviews or any other Multiview layout is skipped.
- **Regex layouts:** if the target layout was in Regex mode, it's switched to Classic so favorites control it.
- The Dispatcharr password is stored in the plugin's XML config on the Emby server (admin access only), like other Emby plugin credentials.
