using System;
using MediaBrowser.Model.Plugins;

namespace Emby.MultiviewFavorites.Configuration
{
    /// <summary>One multiview channel: whose favorites, which Dispatcharr layout, and how it's tiled.</summary>
    public class MultiviewProfile
    {
        /// <summary>Stable id for this profile (not shown to users).</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public bool Enabled { get; set; } = true;

        /// <summary>Emby user id whose Live TV favorites drive this multiview.</summary>
        public string EmbyUserId { get; set; } = "";

        /// <summary>Name of the multiview layout in Dispatcharr (created if missing). Must be unique.</summary>
        public string MultiviewName { get; set; } = "Emby Favorites";

        /// <summary>Max tiles (2-9). The first N favorites in tile order are used.</summary>
        public int MaxStreams { get; set; } = 4;

        /// <summary>"channel" (lowest channel number first), "favorited" (oldest favorite first), or "manual".</summary>
        public string TileOrder { get; set; } = "channel";

        /// <summary>Emby Live TV channel ids, tile 1 first. Used when TileOrder = manual.</summary>
        public string[] ManualOrder { get; set; } = new string[0];

        /// <summary>auto | featured | top_featured | "" (leave Dispatcharr's setting alone)</summary>
        public string LayoutStyle { get; set; } = "auto";

        /// <summary>"0" (first tile) | "all" (one track per tile) | "" (leave alone)</summary>
        public string AudioSource { get; set; } = "0";

        // ---- State written by the plugin --------------------------------

        /// <summary>Dispatcharr layout id (8 hex chars) this profile owns.</summary>
        public string LayoutId { get; set; } = "";

        public string LastSyncUtc { get; set; } = "";

        public string LastSyncStatus { get; set; } = "";
    }

    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>Master switch. When off, nothing is pushed to Dispatcharr.</summary>
        public bool Enabled { get; set; } = false;

        // ---- Dispatcharr connection -------------------------------------

        /// <summary>Base URL of the Multiview plugin's server, e.g. http://192.168.1.10:9292</summary>
        public string DispatcharrUrl { get; set; } = "http://dispatcharr:9292";

        /// <summary>Multiview dashboard mount path (Multiview "Dashboard Mount Path" setting).</summary>
        public string DashPath { get; set; } = "/dash";

        public string DispatcharrUsername { get; set; } = "";

        public string DispatcharrPassword { get; set; } = "";

        // ---- Behaviour shared by all multiviews ---------------------------

        /// <summary>Kill a running multiview stream after its tiles change so the next tune picks up the new set.</summary>
        public bool RestartActiveStream { get; set; } = false;

        /// <summary>Queue Emby's "Refresh Guide" task after a multiview channel is created, renamed or deleted.</summary>
        public bool RefreshEmbyGuideOnCreate { get; set; } = true;

        // ---- Multiview channels -------------------------------------------

        public MultiviewProfile[] Profiles { get; set; } = new MultiviewProfile[0];

        /// <summary>
        /// Dispatcharr layout ids of removed profiles, deleted from Dispatcharr on the next sync.
        /// Only ids this plugin created are ever put here.
        /// </summary>
        public string[] LayoutsToDelete { get; set; } = new string[0];

        public string LastSyncUtc { get; set; } = "";

        public string LastSyncStatus { get; set; } = "";

        // ---- Version 1.0/1.1 single-channel settings ----------------------
        // Still read from existing config files so they can be moved into Profiles[0]
        // (see Plugin.MigrateLegacySettings). Cleared after migration.

        public string EmbyUserId { get; set; } = "";
        public string MultiviewName { get; set; } = "";
        public int MaxStreams { get; set; } = 0;
        public string TileOrder { get; set; } = "";
        public string[] ManualOrder { get; set; } = new string[0];
        public string LayoutStyle { get; set; } = null;
        public string AudioSource { get; set; } = null;
        public string LayoutId { get; set; } = "";
    }
}
