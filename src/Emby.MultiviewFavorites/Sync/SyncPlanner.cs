using System;
using System.Collections.Generic;
using System.Linq;

namespace Emby.MultiviewFavorites.Sync
{
    public class FavoriteChannel
    {
        public string EmbyId { get; set; }
        public string Name { get; set; }
        public string NumberText { get; set; }

        /// <summary>When the user favorited the channel (Emby's UserItemData.RatingLastModified).</summary>
        public DateTimeOffset? FavoritedUtc { get; set; }
    }

    public static class TileOrders
    {
        /// <summary>Lowest channel number first.</summary>
        public const string ChannelNumber = "channel";

        /// <summary>Oldest favorite first.</summary>
        public const string Favorited = "favorited";

        /// <summary>User-arranged list (SyncOptions.ManualOrder); anything not in it goes last, by channel number.</summary>
        public const string Manual = "manual";

        public static string Normalize(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case Favorited: return Favorited;
                case Manual: return Manual;
                default: return ChannelNumber;
            }
        }
    }

    public class SyncOptions
    {
        public string MultiviewName { get; set; } = "Emby Favorites";
        public int MaxStreams { get; set; } = 4;
        public string LayoutStyle { get; set; } = "auto";
        public string AudioSource { get; set; } = "0";
        public string KnownLayoutId { get; set; } = "";
        public string TileOrder { get; set; } = TileOrders.ChannelNumber;

        /// <summary>Emby channel ids in the user's preferred order (manual mode).</summary>
        public IList<string> ManualOrder { get; set; } = new List<string>();
    }

    /// <summary>One row per Emby favorite, for logs and the config page table.</summary>
    public class FavoriteRow
    {
        public string EmbyId { get; set; }
        public string EmbyName { get; set; }
        public string Number { get; set; }
        public string DispatcharrName { get; set; }
        public string DispatcharrId { get; set; }
        public bool Included { get; set; }
        public int Tile { get; set; }

        /// <summary>Matched to a usable Dispatcharr channel (a tile, or would be one if the limit allowed).</summary>
        public bool Eligible { get; set; }
        public string FavoritedUtc { get; set; }
        public string Status { get; set; }
    }

    public class SyncPlan
    {
        public string LayoutId { get; set; }
        public bool IsNewLayout { get; set; }
        public bool Renamed { get; set; }
        public bool TilesChanged { get; set; }
        public List<string> TileIds { get; set; } = new List<string>();
        public List<FavoriteRow> Rows { get; set; } = new List<FavoriteRow>();
        public List<string> Warnings { get; set; } = new List<string>();

        /// <summary>Only the keys that actually differ from Dispatcharr's current settings. null value = delete key.</summary>
        public Dictionary<string, object> Updates { get; set; } = new Dictionary<string, object>();

        public bool HasChanges => Updates.Count > 0;
    }

    /// <summary>
    /// Pure logic: favorites + Dispatcharr state in, settings patch out. No I/O, so it
    /// can be unit-tested outside Emby.
    /// </summary>
    public static class SyncPlanner
    {
        // Multiview's channel_count field is capped at 9; clear any slots beyond what we use.
        public const int MaxSlots = 9;

        public static SyncPlan Build(
            IEnumerable<FavoriteChannel> favorites,
            IList<DispatcharrChannel> dispatcharrChannels,
            Dictionary<string, object> settings,
            SyncOptions options,
            Func<string> newIdFactory = null)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            settings = settings ?? new Dictionary<string, object>();
            var plan = new SyncPlan();

            var name = string.IsNullOrWhiteSpace(options.MultiviewName) ? "Emby Favorites" : options.MultiviewName.Trim();
            var max = Math.Max(2, Math.Min(MaxSlots, options.MaxStreams));

            // ---------------------------------------------------- layout id
            var order = (MiniJson.AsArray(MiniJson.Get(settings, "multiview_order")) ?? new List<object>())
                .Select(MiniJson.ToCanonical)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            string layoutId = null;
            if (!string.IsNullOrEmpty(options.KnownLayoutId) && order.Contains(options.KnownLayoutId))
            {
                layoutId = options.KnownLayoutId;
            }
            else
            {
                layoutId = order.FirstOrDefault(id =>
                    string.Equals(MiniJson.GetString(settings, $"multiview_{id}_name")?.Trim(), name, StringComparison.OrdinalIgnoreCase));
            }

            if (layoutId == null)
            {
                var factory = newIdFactory ?? NewLayoutId;
                do { layoutId = factory(); } while (order.Contains(layoutId));
                plan.IsNewLayout = true;
            }
            plan.LayoutId = layoutId;
            var p = $"multiview_{layoutId}_";

            // Names of every multiview output, so a favorited multiview channel never tiles itself.
            var multiviewNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
            foreach (var id in order)
            {
                var n = MiniJson.GetString(settings, $"multiview_{id}_name");
                if (!string.IsNullOrWhiteSpace(n)) multiviewNames.Add(n.Trim());
            }

            // ---------------------------------------------------- matching
            var byNumber = new Dictionary<decimal, List<DispatcharrChannel>>();
            foreach (var ch in dispatcharrChannels ?? new List<DispatcharrChannel>())
            {
                if (ch?.Number == null || string.IsNullOrEmpty(ch.Id)) continue;
                if (!byNumber.TryGetValue(ch.Number.Value, out var list)) byNumber[ch.Number.Value] = list = new List<DispatcharrChannel>();
                list.Add(ch);
            }

            var ordered = Order(favorites, options);

            var selected = new List<string>();
            foreach (var x in ordered)
            {
                var row = new FavoriteRow
                {
                    EmbyId = x.Fav.EmbyId,
                    FavoritedUtc = x.Fav.FavoritedUtc?.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                    EmbyName = x.Fav.Name,
                    Number = x.Num != null ? ChannelNumbers.Format(x.Num) : (x.Fav.NumberText ?? ""),
                };
                plan.Rows.Add(row);

                if (x.Num == null)
                {
                    row.Status = "Skipped: no channel number in Emby";
                    continue;
                }
                if (multiviewNames.Contains((x.Fav.Name ?? "").Trim()))
                {
                    row.Status = "Skipped: this is a multiview channel";
                    continue;
                }
                if (!byNumber.TryGetValue(x.Num.Value, out var matches))
                {
                    row.Status = $"Skipped: no Dispatcharr channel numbered {row.Number}";
                    continue;
                }

                var match = matches[0];
                row.DispatcharrName = match.Name;
                row.DispatcharrId = match.Id;

                if (multiviewNames.Contains((match.Name ?? "").Trim()))
                {
                    row.Status = "Skipped: Dispatcharr channel is a multiview output";
                    continue;
                }
                if (matches.Count > 1)
                {
                    plan.Warnings.Add($"Channel number {row.Number} is used by {matches.Count} Dispatcharr channels; using \"{match.Name}\".");
                }
                if (selected.Contains(match.Id))
                {
                    row.Status = "Skipped: duplicate of a channel already in the multiview";
                    continue;
                }
                row.Eligible = true;
                if (selected.Count >= max)
                {
                    row.Status = $"Not included: over the {max}-stream limit";
                    continue;
                }

                selected.Add(match.Id);
                row.Included = true;
                row.Tile = selected.Count;
                row.Status = $"Tile {selected.Count}";
            }
            plan.TileIds = selected;

            if (selected.Count < 2)
            {
                plan.Warnings.Add(
                    $"Only {selected.Count} favorite channel(s) matched. Multiview needs at least 2 channels before the stream will play.");
            }

            // ---------------------------------------------------- desired state
            var desired = new Dictionary<string, object>();
            var slotCount = Math.Max(2, selected.Count);

            desired[p + "name"] = name;
            desired[p + "selector_type"] = "classic";
            desired[p + "channel_count"] = slotCount;
            for (var i = 1; i <= MaxSlots; i++)
            {
                if (i <= slotCount)
                    desired[p + "channel_" + i] = i <= selected.Count ? selected[i - 1] : "_none";
                else
                    desired[p + "channel_" + i] = null; // delete stale slots
            }

            var style = (options.LayoutStyle ?? "").Trim();
            if (style.Length > 0) desired[p + "layout"] = style;
            else if (plan.IsNewLayout) desired[p + "layout"] = "auto";

            var audio = (options.AudioSource ?? "").Trim();
            if (audio.Length > 0) desired[p + "audio_source"] = audio;
            else if (plan.IsNewLayout) desired[p + "audio_source"] = "0";

            if (plan.IsNewLayout)
            {
                desired[p + "epg_source_mode"] = "dummy";
                desired["multiview_order"] = order.Concat(new[] { layoutId }).Cast<object>().ToList();
            }

            if (!plan.IsNewLayout)
            {
                var existingSelector = MiniJson.GetString(settings, p + "selector_type");
                if (existingSelector == "regex")
                    plan.Warnings.Add("The layout was in Regex mode; switched it to Classic so favorites control the channels.");
            }

            // ---------------------------------------------------- diff
            foreach (var kv in desired)
            {
                var exists = settings.ContainsKey(kv.Key);
                if (kv.Value == null)
                {
                    if (exists) plan.Updates[kv.Key] = null;
                    continue;
                }
                if (kv.Key == "multiview_order")
                {
                    plan.Updates[kv.Key] = kv.Value;
                    continue;
                }
                var cur = exists ? MiniJson.ToCanonical(settings[kv.Key]) : null;
                if (cur != MiniJson.ToCanonical(kv.Value)) plan.Updates[kv.Key] = kv.Value;
            }

            plan.Renamed = !plan.IsNewLayout && plan.Updates.ContainsKey(p + "name");
            plan.TilesChanged = plan.IsNewLayout || !CurrentTiles(settings, p).SequenceEqual(selected);
            return plan;
        }

        private sealed class Ranked
        {
            public FavoriteChannel Fav;
            public decimal? Num;
        }

        /// <summary>
        /// Priority order of the favorites. The first N eligible entries become tiles 1..N,
        /// so this decides both which channels make the cut and where they sit.
        /// Channel number (then name) is always the final tie-breaker so results are stable.
        /// </summary>
        private static List<Ranked> Order(IEnumerable<FavoriteChannel> favorites, SyncOptions options)
        {
            var items = (favorites ?? Enumerable.Empty<FavoriteChannel>())
                .Where(f => f != null)
                .Select(f => new Ranked { Fav = f, Num = ChannelNumbers.Parse(f.NumberText) })
                .ToList();

            IOrderedEnumerable<Ranked> sorted;
            switch (TileOrders.Normalize(options.TileOrder))
            {
                case TileOrders.Favorited:
                    // Unknown favorite time sorts after known ones.
                    sorted = items
                        .OrderBy(x => x.Fav.FavoritedUtc == null ? 1 : 0)
                        .ThenBy(x => x.Fav.FavoritedUtc ?? DateTimeOffset.MaxValue);
                    break;

                case TileOrders.Manual:
                    var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    var manual = options.ManualOrder ?? new List<string>();
                    for (var i = 0; i < manual.Count; i++)
                    {
                        var id = (manual[i] ?? "").Trim();
                        if (id.Length > 0 && !index.ContainsKey(id)) index[id] = i;
                    }
                    sorted = items.OrderBy(x =>
                        x.Fav.EmbyId != null && index.TryGetValue(x.Fav.EmbyId, out var pos) ? pos : int.MaxValue);
                    break;

                default:
                    sorted = items.OrderBy(x => 0);
                    break;
            }

            return sorted
                .ThenBy(x => x.Num == null ? 1 : 0)
                .ThenBy(x => x.Num ?? 0m)
                .ThenBy(x => x.Fav.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> CurrentTiles(Dictionary<string, object> settings, string prefix)
        {
            var list = new List<string>();
            int.TryParse(MiniJson.GetString(settings, prefix + "channel_count") ?? "4", out var count);
            count = Math.Max(2, count);
            for (var i = 1; i <= count; i++)
            {
                var v = MiniJson.GetString(settings, prefix + "channel_" + i);
                if (!string.IsNullOrEmpty(v) && v != "_none") list.Add(v);
            }
            return list;
        }

        public static string NewLayoutId()
        {
            // Same shape Multiview uses (secrets.token_hex(4)).
            return Guid.NewGuid().ToString("N").Substring(0, 8);
        }
    }
}
