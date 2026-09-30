using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.MultiviewFavorites.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.MultiviewFavorites.Sync
{
    public class SyncResult
    {
        public bool Success { get; set; }
        public bool DryRun { get; set; }
        public bool Changed { get; set; }
        public bool CreatedLayout { get; set; }
        public string LayoutId { get; set; }
        public string Message { get; set; }
        public string TimestampUtc { get; set; }
        public List<FavoriteRow> Rows { get; set; } = new List<FavoriteRow>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class SyncEngine
    {
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IUserDataManager _userDataManager;
        private readonly ILiveTvManager _liveTvManager;
        private readonly ITaskManager _taskManager;
        private readonly ILogger _logger;

        public SyncEngine(
            ILibraryManager libraryManager,
            IUserManager userManager,
            IUserDataManager userDataManager,
            ILiveTvManager liveTvManager,
            ITaskManager taskManager,
            ILogger logger)
        {
            _libraryManager = libraryManager;
            _userManager = userManager;
            _userDataManager = userDataManager;
            _liveTvManager = liveTvManager;
            _taskManager = taskManager;
            _logger = logger;
        }

        private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

        /// <summary>
        /// Reads the configured user's Live TV favorites, matches them to Dispatcharr channels by
        /// channel number, and writes the lowest-numbered N into the multiview layout.
        /// dryRun computes and returns the plan without writing anything.
        /// </summary>
        public Task<SyncResult> RunAsync(string reason, bool dryRun, CancellationToken ct)
        {
            return RunAsync(reason, dryRun, null, null, ct);
        }

        /// <param name="tileOrderOverride">Preview only: try a tile order without saving it.</param>
        /// <param name="manualOrderOverride">Preview only: try a manual order without saving it.</param>
        public async Task<SyncResult> RunAsync(string reason, bool dryRun, string tileOrderOverride, IList<string> manualOrderOverride, CancellationToken ct)
        {
            var cfg = Config;
            var result = new SyncResult
            {
                DryRun = dryRun,
                TimestampUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            };

            if (!dryRun && !cfg.Enabled)
            {
                result.Message = "Sync is disabled.";
                return result;
            }

            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var user = ResolveUser(cfg.EmbyUserId);
                if (user == null)
                    throw new DispatcharrException("Choose an Emby user whose favorites should drive the multiview.");

                var favorites = GetFavoriteChannels(user, ct);
                _logger.Info("sync ({0}) - {1} favorite Live TV channel(s) for {2}", reason, favorites.Count, user.Name);

                var client = new DispatcharrClient(cfg.DispatcharrUrl, cfg.DashPath, cfg.DispatcharrUsername, cfg.DispatcharrPassword);
                await client.LoginAsync(ct).ConfigureAwait(false);
                var channels = await client.GetChannelsAsync(ct).ConfigureAwait(false);
                var settings = await client.GetSettingsAsync(ct).ConfigureAwait(false);

                var effectiveOrder = dryRun && !string.IsNullOrEmpty(tileOrderOverride) ? tileOrderOverride : cfg.TileOrder;
                var plan = SyncPlanner.Build(favorites, channels, settings, new SyncOptions
                {
                    MultiviewName = cfg.MultiviewName,
                    MaxStreams = cfg.MaxStreams,
                    LayoutStyle = cfg.LayoutStyle,
                    AudioSource = cfg.AudioSource,
                    KnownLayoutId = cfg.LayoutId,
                    TileOrder = effectiveOrder,
                    ManualOrder = dryRun && manualOrderOverride != null
                        ? manualOrderOverride
                        : (IList<string>)(cfg.ManualOrder ?? new string[0]),
                });

                result.LayoutId = plan.LayoutId;
                result.Rows = plan.Rows;
                result.Warnings = plan.Warnings;
                result.CreatedLayout = plan.IsNewLayout;
                result.Changed = plan.HasChanges;

                foreach (var w in plan.Warnings) _logger.Warn("{0}", w);

                if (dryRun)
                {
                    result.Success = true;
                    result.Message = Describe(plan, true, effectiveOrder);
                    return result;
                }

                if (plan.HasChanges)
                {
                    await client.PatchSettingsAsync(plan.Updates, ct).ConfigureAwait(false);
                    _logger.Info("updated layout {0} ({1} key(s)); tiles = [{2}]",
                        plan.LayoutId, plan.Updates.Count, string.Join(", ", plan.TileIds));

                    if (plan.IsNewLayout || plan.Renamed)
                    {
                        // New/renamed layout needs a new M3U entry before it can show up anywhere.
                        await client.RefreshM3uAsync(ct).ConfigureAwait(false);
                        _logger.Info("asked Dispatcharr to regenerate the multiview M3U/EPG");
                        if (cfg.RefreshEmbyGuideOnCreate) QueueGuideRefresh();
                    }

                    if (plan.TilesChanged && !plan.IsNewLayout && cfg.RestartActiveStream)
                    {
                        var killed = await client.RestartStreamAsync(plan.LayoutId, ct).ConfigureAwait(false);
                        if (killed > 0) _logger.Info("restarted {0} running multiview stream(s)", killed);
                    }
                }

                result.Success = true;
                result.Message = Describe(plan, false, effectiveOrder);
                SaveState(plan.LayoutId, result.Message);
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (ex is DispatcharrException) _logger.Warn("{0}", ex.Message);
                else _logger.ErrorException("sync failed", ex);

                result.Success = false;
                result.Message = ex.Message;
                if (!dryRun) SaveState(null, "Failed: " + ex.Message);
                return result;
            }
            finally
            {
                Gate.Release();
            }
        }

        public async Task<SyncResult> TestConnectionAsync(PluginConfiguration cfg, CancellationToken ct)
        {
            var result = new SyncResult { DryRun = true, TimestampUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) };
            try
            {
                var client = new DispatcharrClient(cfg.DispatcharrUrl, cfg.DashPath, cfg.DispatcharrUsername, cfg.DispatcharrPassword);
                await client.LoginAsync(ct).ConfigureAwait(false);
                var channels = await client.GetChannelsAsync(ct).ConfigureAwait(false);
                var settings = await client.GetSettingsAsync(ct).ConfigureAwait(false);
                var layouts = (MiniJson.AsArray(MiniJson.Get(settings, "multiview_order")) ?? new List<object>()).Count;
                result.Success = true;
                result.Message = $"Connected to {client.ApiBase}: {channels.Count} channel(s), {layouts} multiview layout(s).";
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = ex.Message;
            }
            return result;
        }

        public bool IsConfiguredUser(User user)
        {
            if (user == null) return false;
            var configured = Config.EmbyUserId;
            if (string.IsNullOrWhiteSpace(configured)) return false;
            if (Guid.TryParse(configured, out var g) && g == user.Id) return true;
            return string.Equals(configured, user.Id.ToString("N"), StringComparison.OrdinalIgnoreCase)
                   || string.Equals(configured, user.InternalId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        private User ResolveUser(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            try { return _userManager.GetUserById(id); }
            catch { return null; }
        }

        private List<FavoriteChannel> GetFavoriteChannels(User user, CancellationToken ct)
        {
            BaseItem[] items = null;

            // Preferred: the Live TV manager's own channel query (respects Live TV access).
            try
            {
                items = _liveTvManager.GetInternalChannels(new InternalItemsQuery(user) { IsFavorite = true }, false, null, ct)?.Items;
            }
            catch (Exception ex)
            {
                _logger.Debug("Live TV channel query failed, falling back to library query: {0}", ex.Message);
            }

            var channels = Filter(user, items);
            if (channels.Count > 0) return channels;

            // Fallback: generic library query.
            try
            {
                items = _libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    IsFavorite = true,
                    Recursive = true,
                    IncludeItemTypes = new[] { "TvChannel" },
                });
            }
            catch (Exception ex)
            {
                _logger.Debug("library favorites query failed: {0}", ex.Message);
                items = null;
            }
            return Filter(user, items);
        }

        private List<FavoriteChannel> Filter(User user, BaseItem[] items)
        {
            var list = new List<FavoriteChannel>();
            if (items == null) return list;
            foreach (var ch in items.OfType<LiveTvChannel>())
            {
                // Belt and braces: confirm the favorite flag against user data.
                var data = _userDataManager.GetUserData(user, ch);
                if (data == null || !data.IsFavorite) continue;
                list.Add(new FavoriteChannel
                {
                    EmbyId = ch.Id.ToString("N"),
                    Name = ch.Name,
                    NumberText = ch.Number,
                    // Emby stamps RatingLastModified when the favorite flag is toggled, so for a
                    // currently-favorited channel it's the time it was favorited.
                    FavoritedUtc = data.RatingLastModified,
                });
            }
            return list;
        }

        private void QueueGuideRefresh()
        {
            try
            {
                var worker = _taskManager.ScheduledTasks.FirstOrDefault(t =>
                    string.Equals(t.ScheduledTask?.Key, "RefreshGuide", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.Name, "Refresh Guide", StringComparison.OrdinalIgnoreCase));
                if (worker == null)
                {
                    _logger.Info("couldn't find Emby's Refresh Guide task; refresh the guide manually to see the new channel.");
                    return;
                }
                // Give Dispatcharr's celery M3U refresh a head start before Emby re-reads the tuner.
                Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ =>
                {
                    try
                    {
                        _taskManager.QueueScheduledTask(worker.ScheduledTask, new TaskOptions());
                        _logger.Info("queued Emby guide refresh");
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn("could not queue guide refresh: {0}", ex.Message);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.Warn("could not queue guide refresh: {0}", ex.Message);
            }
        }

        private static string Describe(SyncPlan plan, bool preview, string tileOrder)
        {
            var tiles = plan.TileIds.Count;
            string order;
            switch (TileOrders.Normalize(tileOrder))
            {
                case TileOrders.Favorited: order = "oldest favorite first"; break;
                case TileOrders.Manual: order = "manual order"; break;
                default: order = "lowest channel number first"; break;
            }
            string what;
            if (plan.IsNewLayout) what = preview ? "Will create the multiview layout" : "Created the multiview layout";
            else if (plan.HasChanges) what = preview ? "Will update the multiview layout" : "Updated the multiview layout";
            else what = "Multiview already up to date";
            return $"{what} with {tiles} channel(s), {order}.";
        }

        private void SaveState(string layoutId, string status)
        {
            var plugin = Plugin.Instance;
            if (plugin == null) return;
            try
            {
                var cfg = plugin.Configuration;
                if (!string.IsNullOrEmpty(layoutId)) cfg.LayoutId = layoutId;
                cfg.LastSyncUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                cfg.LastSyncStatus = status ?? "";
                plugin.SaveConfiguration();
            }
            catch (Exception ex)
            {
                _logger.Debug("could not persist sync state: {0}", ex.Message);
            }
        }
    }
}
