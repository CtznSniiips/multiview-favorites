using MediaBrowser.Model.Plugins;

namespace Emby.MultiviewFavorites.Configuration
{
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

        // ---- What to sync -----------------------------------------------

        /// <summary>Emby user id whose Live TV favorites drive the multiview.</summary>
        public string EmbyUserId { get; set; } = "";

        /// <summary>Name of the multiview layout in Dispatcharr (created if missing).</summary>
        public string MultiviewName { get; set; } = "Emby Favorites";

        /// <summary>Max tiles. Only the N favorites with the lowest channel numbers are used.</summary>
        public int MaxStreams { get; set; } = 4;

        /// <summary>
        /// Which favorites win the tiles, and in what order:
        /// "channel" (lowest channel number first), "favorited" (oldest favorite first),
        /// or "manual" (ManualOrder).
        /// </summary>
        public string TileOrder { get; set; } = "channel";

        /// <summary>Emby Live TV channel ids, tile 1 first. Used when TileOrder = manual.</summary>
        public string[] ManualOrder { get; set; } = new string[0];

        /// <summary>auto | featured | top_featured | "" (leave Dispatcharr's setting alone)</summary>
        public string LayoutStyle { get; set; } = "auto";

        /// <summary>"0" (first tile) | "all" (one track per tile) | "" (leave alone)</summary>
        public string AudioSource { get; set; } = "0";

        /// <summary>Kill a running multiview stream after its tiles change so the next tune picks up the new set.</summary>
        public bool RestartActiveStream { get; set; } = false;

        /// <summary>Queue Emby's "Refresh Guide" task after the multiview channel is created or renamed.</summary>
        public bool RefreshEmbyGuideOnCreate { get; set; } = true;

        // ---- State written by the plugin (not user-editable) -------------

        /// <summary>Dispatcharr layout id (8 hex chars) this plugin owns.</summary>
        public string LayoutId { get; set; } = "";

        public string LastSyncUtc { get; set; } = "";

        public string LastSyncStatus { get; set; } = "";
    }
}
