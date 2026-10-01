# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/).

Each release needs a `## [x.y.z] - YYYY-MM-DD` section before running the Release
workflow. The workflow copies that section into the GitHub release notes and fails if it's missing.

## [Unreleased]

## [1.2.0] - 2026-10-01

### Added
- **Multiple multiview channels:** set up as many multiviews as you like, each following one Emby user's favorites
  with its own Dispatcharr layout, stream limit, tile order, layout style and audio.
- *Add one for each user* creates a "*Name*'s Favorites" multiview for every Emby user who doesn't have one.
- Pause a multiview (keeps its layout) or remove it (deletes its layout from Dispatcharr on save, after a confirmation).
- *Sync all now* shows the result for every multiview; each multiview shows its own last-sync status.
- Preview works on unsaved multiviews.

### Changed
- Existing single-multiview settings are moved into the new list automatically on first start, keeping the same layout.
- All multiviews are synced together and sent to Dispatcharr as a single settings update.
- A multiview is never tiled into another multiview, even if favorited.
- The Dispatcharr client retries once if a connection drops before Dispatcharr responds.

### Fixed
- Test harness no longer depends on a fixed port and can't stall on the mock server's output.

## [1.1.0] - 2026-09-30

### Added
- **Tile order** setting: *Lowest channel number first* (default), *Oldest favorite first*, or *Manual*.
  The order decides which favorites make the cut and which one is tile 1 (the large tile in the Featured layouts).
- *Oldest favorite first* uses the timestamp Emby records when a favorite is toggled, so it works for
  channels favorited before the plugin was installed.
- Manual ordering list on the settings page with up/down arrows, live tile labels, and *Reset to channel order*.
- The Preview table shows when each channel was favorited.
- Preview uses the tile order currently on screen, even before it's saved.
- GitHub Actions release workflow with version stamping, tests, checksums, and changelog-based release notes.

### Changed
- Changing only the tile order now counts as a tile change (it follows the *Restart the multiview if it's playing* setting).

## [1.0.0] - 2026-09-29

### Added
- Initial release: syncs a chosen Emby user's favorite Live TV channels into a Dispatcharr Multiview layout,
  matched by channel number, lowest N channels up to a configurable stream limit (2–9).
- Creates or renames the layout in Dispatcharr and regenerates the Multiview M3U/EPG.
- Syncs on favorite changes (debounced), on settings save, on server start, hourly, and on demand.
- Settings page with connection test, Preview, and Sync now.