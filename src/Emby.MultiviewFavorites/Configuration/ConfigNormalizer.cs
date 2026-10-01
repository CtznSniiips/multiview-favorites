using System;
using System.Collections.Generic;
using System.Linq;
using Emby.MultiviewFavorites.Sync;

namespace Emby.MultiviewFavorites.Configuration
{
    /// <summary>
    /// Pure config housekeeping (no Emby services), so it can be unit-tested:
    /// moving 1.x single-channel settings into a profile, and cleaning up what the
    /// settings page posts back.
    /// </summary>
    public static class ConfigNormalizer
    {
        public const string DefaultName = "Emby Favorites";

        /// <summary>
        /// Versions 1.0/1.1 stored one multiview in top-level fields. Move it into Profiles[0]
        /// (keeping its Dispatcharr layout id so the same layout keeps being used).
        /// Returns true if anything changed.
        /// </summary>
        public static bool MigrateLegacy(PluginConfiguration cfg)
        {
            if (cfg == null) return false;
            var hasLegacy = !string.IsNullOrWhiteSpace(cfg.EmbyUserId) || !string.IsNullOrWhiteSpace(cfg.LayoutId);
            if (!hasLegacy) return false;

            var profiles = (cfg.Profiles ?? new MultiviewProfile[0]).ToList();
            var alreadyThere = profiles.Any(p =>
                (!string.IsNullOrEmpty(cfg.LayoutId) && string.Equals(p.LayoutId, cfg.LayoutId, StringComparison.OrdinalIgnoreCase)) ||
                string.Equals((p.MultiviewName ?? "").Trim(), (cfg.MultiviewName ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

            if (!alreadyThere)
            {
                profiles.Insert(0, new MultiviewProfile
                {
                    Enabled = true,
                    EmbyUserId = cfg.EmbyUserId ?? "",
                    MultiviewName = string.IsNullOrWhiteSpace(cfg.MultiviewName) ? DefaultName : cfg.MultiviewName.Trim(),
                    MaxStreams = cfg.MaxStreams > 0 ? cfg.MaxStreams : 4,
                    TileOrder = TileOrders.Normalize(cfg.TileOrder),
                    ManualOrder = cfg.ManualOrder ?? new string[0],
                    LayoutStyle = cfg.LayoutStyle ?? "auto",
                    AudioSource = cfg.AudioSource ?? "0",
                    LayoutId = cfg.LayoutId ?? "",
                    LastSyncUtc = cfg.LastSyncUtc ?? "",
                    LastSyncStatus = cfg.LastSyncStatus ?? "",
                });
            }

            cfg.Profiles = profiles.ToArray();
            ClearLegacy(cfg);
            return true;
        }

        public static void ClearLegacy(PluginConfiguration cfg)
        {
            cfg.EmbyUserId = "";
            cfg.MultiviewName = "";
            cfg.MaxStreams = 0;
            cfg.TileOrder = "";
            cfg.ManualOrder = new string[0];
            cfg.LayoutStyle = null;
            cfg.AudioSource = null;
            cfg.LayoutId = "";
        }

        /// <summary>
        /// Cleans up a configuration posted by the settings page:
        /// fills in missing ids and defaults, clamps values, keeps plugin-owned sync state
        /// (layout ids, last-sync status) the page may have posted back stale, and queues the
        /// Dispatcharr layouts of removed profiles for deletion.
        /// </summary>
        public static void NormalizeIncoming(PluginConfiguration incoming, PluginConfiguration current)
        {
            if (incoming == null) return;
            current = current ?? new PluginConfiguration();

            // An older settings page (cached) might still post the 1.x shape.
            MigrateLegacy(incoming);

            var currentById = (current.Profiles ?? new MultiviewProfile[0])
                .Where(p => !string.IsNullOrEmpty(p?.Id))
                .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var profiles = new List<MultiviewProfile>();
            foreach (var p in incoming.Profiles ?? new MultiviewProfile[0])
            {
                if (p == null) continue;
                if (string.IsNullOrWhiteSpace(p.Id) || !seenIds.Add(p.Id))
                {
                    p.Id = Guid.NewGuid().ToString("N");
                    seenIds.Add(p.Id);
                }

                p.EmbyUserId = (p.EmbyUserId ?? "").Trim();
                p.MultiviewName = string.IsNullOrWhiteSpace(p.MultiviewName) ? DefaultName : p.MultiviewName.Trim();
                p.MaxStreams = Math.Max(2, Math.Min(SyncPlanner.MaxSlots, p.MaxStreams <= 0 ? 4 : p.MaxStreams));
                p.TileOrder = TileOrders.Normalize(p.TileOrder);
                p.ManualOrder = (p.ManualOrder ?? new string[0]).Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();
                if (p.LayoutStyle == null) p.LayoutStyle = "auto";
                if (p.AudioSource == null) p.AudioSource = "0";

                if (currentById.TryGetValue(p.Id, out var cur))
                {
                    if (string.IsNullOrEmpty(p.LayoutId)) p.LayoutId = cur.LayoutId;
                    p.LastSyncUtc = cur.LastSyncUtc;
                    p.LastSyncStatus = cur.LastSyncStatus;
                }
                profiles.Add(p);
            }
            incoming.Profiles = profiles.ToArray();

            // Layouts of removed profiles get deleted from Dispatcharr on the next sync,
            // unless another profile still points at the same layout.
            var stillUsed = new HashSet<string>(
                profiles.Select(p => p.LayoutId).Where(id => !string.IsNullOrEmpty(id)), StringComparer.OrdinalIgnoreCase);
            var toDelete = new List<string>(
                (current.LayoutsToDelete ?? new string[0]).Concat(incoming.LayoutsToDelete ?? new string[0]));
            foreach (var removed in currentById.Values.Where(c => !seenIds.Contains(c.Id)))
            {
                if (!string.IsNullOrEmpty(removed.LayoutId)) toDelete.Add(removed.LayoutId);
            }
            incoming.LayoutsToDelete = toDelete
                .Where(id => !string.IsNullOrWhiteSpace(id) && !stillUsed.Contains(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            incoming.LastSyncUtc = current.LastSyncUtc;
            incoming.LastSyncStatus = current.LastSyncStatus;
            ClearLegacy(incoming);
        }

        /// <summary>
        /// Names used by more than one profile (case-insensitive). Each profile needs its own
        /// Dispatcharr layout, and layouts are matched by name, so duplicates can't be synced.
        /// </summary>
        public static HashSet<string> DuplicateNames(IEnumerable<MultiviewProfile> profiles)
        {
            return new HashSet<string>(
                (profiles ?? Enumerable.Empty<MultiviewProfile>())
                    .Where(p => p != null)
                    .GroupBy(p => (p.MultiviewName ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key),
                StringComparer.OrdinalIgnoreCase);
        }
    }
}
