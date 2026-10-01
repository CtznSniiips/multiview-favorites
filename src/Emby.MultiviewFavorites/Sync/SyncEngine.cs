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
    /// <summary>Outcome for one multiview channel.</summary>
    public class ProfileResult
    {
        public string ProfileId { get; set; }
        public string Name { get; set; }
        public string UserName { get; set; }
        public bool Success { get; set; }
        public bool Changed { get; set; }
        public bool CreatedLayout { get; set; }
        public string LayoutId { get; set; }
        public string Message { get; set; }
        public List<FavoriteRow> Rows { get; set; } = new List<FavoriteRow>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class SyncResult
    {
        public bool Success { get; set; }
        public bool DryRun { get; set; }
        public bool Changed { get; set; }
        public string Message { get; set; }
        public string TimestampUtc { get; set; }
        public List<ProfileResult> Profiles { get; set; } = new List<ProfileResult>();
        public List<string> DeletedLayouts { get; set; } = new List<string>();
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

        private static string Now() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        /// <summary>
        /// Syncs every enabled multiview channel: reads each profile's user's Live TV favorites,
        /// matches them to Dispatcharr channels by number, and writes the first N (in the profile's
        /// tile order) into that profile's layout. Layouts of removed profiles are deleted.
        /// All changes go to Dispatcharr as one settings patch.
        /// </summary>
        public async Task<SyncResult> RunAsync(string reason, CancellationToken ct)
        {
            var cfg = Config;
            var result = new SyncResult { TimestampUtc = Now() };

            if (!cfg.Enabled)
            {
                result.Message = "Sync is disabled.";
                return result;
            }

            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var enabled = (cfg.Profiles ?? new MultiviewProfile[0]).Where(p => p != null && p.Enabled).ToList();
                var pendingDeletes = (cfg.LayoutsToDelete ?? new string[0]).ToList();
                var inputs = Prepare(enabled, cfg.Profiles, result, ct);

                if (inputs.Count == 0 && pendingDeletes.Count == 0)
                {
                    result.Success = result.Profiles.Count == 0;
                    result.Message = result.Profiles.Count == 0
                        ? "No multiview channels are enabled."
                        : "Nothing could be synced. See each multiview's status.";
                    SaveState(result, new List<string>());
                    return result;
                }

                var client = NewClient(cfg);
                await client.LoginAsync(ct).ConfigureAwait(false);
                var channels = await client.GetChannelsAsync(ct).ConfigureAwait(false);
                var settings = await client.GetSettingsAsync(ct).ConfigureAwait(false);

                var multi = SyncPlanner.BuildAll(inputs.Select(i => i.Input), channels, settings, pendingDeletes);
                Collect(multi, inputs, result, preview: false);

                if (multi.HasChanges)
                {
                    await client.PatchSettingsAsync(multi.Updates, ct).ConfigureAwait(false);
                    foreach (var kv in multi.Plans.Where(p => p.Value.HasChanges))
                    {
                        _logger.Info("updated layout {0}; tiles = [{1}]", kv.Value.LayoutId, string.Join(", ", kv.Value.TileIds));
                    }
                    foreach (var id in multi.DeletedLayoutIds) _logger.Info("deleted layout {0} (its multiview was removed)", id);

                    if (multi.NeedsM3uRefresh)
                    {
                        // Created/renamed/deleted layouts change Multiview's M3U.
                        await client.RefreshM3uAsync(ct).ConfigureAwait(false);
                        _logger.Info("asked Dispatcharr to regenerate the multiview M3U/EPG");
                        if (cfg.RefreshEmbyGuideOnCreate) QueueGuideRefresh();
                    }

                    if (cfg.RestartActiveStream)
                    {
                        foreach (var kv in multi.Plans.Where(p => p.Value.TilesChanged && !p.Value.IsNewLayout))
                        {
                            var killed = await client.RestartStreamAsync(kv.Value.LayoutId, ct).ConfigureAwait(false);
                            if (killed > 0) _logger.Info("restarted {0} running stream(s) of layout {1}", killed, kv.Value.LayoutId);
                        }
                    }
                }

                result.DeletedLayouts = multi.DeletedLayoutIds;
                result.Changed = multi.HasChanges;
                result.Success = result.Profiles.All(p => p.Success);
                result.Message = Summarize(result, preview: false);
                SaveState(result, multi.DeletedLayoutIds.Concat(multi.AlreadyGoneLayoutIds).ToList());
                _logger.Info("sync ({0}): {1}", reason, result.Message);
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
                foreach (var p in result.Profiles.Where(p => p.Success))
                {
                    p.Success = false;
                    p.Message = ex.Message;
                }
                SaveState(result, new List<string>(), "Failed: " + ex.Message);
                return result;
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>
        /// Shows what syncing one (possibly unsaved) multiview would do, without changing anything.
        /// </summary>
        public async Task<SyncResult> PreviewAsync(MultiviewProfile profile, CancellationToken ct)
        {
            var cfg = Config;
            var result = new SyncResult { DryRun = true, TimestampUtc = Now() };
            try
            {
                var others = (cfg.Profiles ?? new MultiviewProfile[0]).Where(p => p != null && p.Id != profile.Id).ToList();
                var inputs = Prepare(new List<MultiviewProfile> { profile }, others.Concat(new[] { profile }), result, ct);
                if (inputs.Count == 0)
                {
                    result.Message = result.Profiles.FirstOrDefault()?.Message;
                    return result;
                }

                var client = NewClient(cfg);
                await client.LoginAsync(ct).ConfigureAwait(false);
                var channels = await client.GetChannelsAsync(ct).ConfigureAwait(false);
                var settings = await client.GetSettingsAsync(ct).ConfigureAwait(false);

                var multi = SyncPlanner.BuildAll(inputs.Select(i => i.Input), channels, settings);
                Collect(multi, inputs, result, preview: true);
                result.Changed = multi.HasChanges;
                result.Success = result.Profiles.All(p => p.Success);
                result.Message = result.Profiles.FirstOrDefault()?.Message;
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = ex.Message;
                return result;
            }
        }

        private sealed class Prepared
        {
            public MultiviewProfile Profile;
            public ProfileResult Result;
            public ProfilePlanInput Input;
        }

        /// <summary>
        /// Resolves users and favorites for each profile. Profiles that can't be synced
        /// (no user, duplicate name) get a failed ProfileResult and are left out of the plan.
        /// </summary>
        private List<Prepared> Prepare(List<MultiviewProfile> profiles, IEnumerable<MultiviewProfile> allProfiles, SyncResult result, CancellationToken ct)
        {
            var all = (allProfiles ?? Enumerable.Empty<MultiviewProfile>()).Where(p => p != null).ToList();
            var duplicates = ConfigNormalizer.DuplicateNames(all.Where(p => p.Enabled || profiles.Contains(p)));
            var list = new List<Prepared>();

            foreach (var profile in profiles)
            {
                var name = string.IsNullOrWhiteSpace(profile.MultiviewName) ? ConfigNormalizer.DefaultName : profile.MultiviewName.Trim();
                var pr = new ProfileResult { ProfileId = profile.Id, Name = name, LayoutId = profile.LayoutId };
                result.Profiles.Add(pr);

                var user = ResolveUser(profile.EmbyUserId);
                pr.UserName = user?.Name;
                if (user == null)
                {
                    pr.Message = "Choose an Emby user for this multiview.";
                    continue;
                }
                if (duplicates.Contains(name))
                {
                    pr.Message = $"Another multiview is also named \"{name}\". Each multiview needs its own name.";
                    continue;
                }

                var favorites = GetFavoriteChannels(user, ct);
                _logger.Debug("{0}: {1} favorite Live TV channel(s) for {2}", name, favorites.Count, user.Name);

                list.Add(new Prepared
                {
                    Profile = profile,
                    Result = pr,
                    Input = new ProfilePlanInput
                    {
                        ProfileId = profile.Id,
                        Favorites = favorites,
                        Options = new SyncOptions
                        {
                            MultiviewName = name,
                            MaxStreams = profile.MaxStreams,
                            LayoutStyle = profile.LayoutStyle,
                            AudioSource = profile.AudioSource,
                            KnownLayoutId = profile.LayoutId,
                            TileOrder = profile.TileOrder,
                            ManualOrder = profile.ManualOrder ?? new string[0],
                            OtherMultiviewNames = all.Where(p => p.Id != profile.Id).Select(p => p.MultiviewName).ToList(),
                        },
                    },
                });
            }
            return list;
        }

        private void Collect(MultiPlan multi, List<Prepared> inputs, SyncResult result, bool preview)
        {
            foreach (var prep in inputs)
            {
                var plan = multi.Plans.First(p => p.Key == prep.Profile.Id).Value;
                var pr = prep.Result;
                pr.LayoutId = plan.LayoutId;
                pr.Rows = plan.Rows;
                pr.Warnings = plan.Warnings;
                pr.CreatedLayout = plan.IsNewLayout;
                pr.Changed = plan.HasChanges;
                pr.Success = true;
                pr.Message = Describe(plan, preview, prep.Profile.TileOrder);
                foreach (var w in plan.Warnings) _logger.Warn("{0}: {1}", pr.Name, w);
            }
        }

        private static DispatcharrClient NewClient(PluginConfiguration cfg)
        {
            return new DispatcharrClient(cfg.DispatcharrUrl, cfg.DashPath, cfg.DispatcharrUsername, cfg.DispatcharrPassword);
        }

        private static string Summarize(SyncResult result, bool preview)
        {
            var ok = result.Profiles.Count(p => p.Success);
            var failed = result.Profiles.Count - ok;
            var changed = result.Profiles.Count(p => p.Changed);
            var parts = new List<string> { $"{ok} multiview(s) synced" };
            if (changed > 0) parts.Add($"{changed} updated");
            if (failed > 0) parts.Add($"{failed} need attention");
            if (result.DeletedLayouts.Count > 0) parts.Add($"{result.DeletedLayouts.Count} removed layout(s) deleted");
            return string.Join(", ", parts) + ".";
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

        /// <summary>True if any enabled multiview uses this user's favorites.</summary>
        public bool IsConfiguredUser(User user)
        {
            if (user == null) return false;
            return (Config.Profiles ?? new MultiviewProfile[0])
                .Any(p => p != null && p.Enabled && UserMatches(user, p.EmbyUserId));
        }

        private static bool UserMatches(User user, string configured)
        {
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
            if (plan.IsNewLayout) what = preview ? "Will create the layout" : "Created the layout";
            else if (plan.HasChanges) what = preview ? "Will update the layout" : "Updated the layout";
            else what = "Up to date";
            return $"{what} with {tiles} channel(s), {order}.";
        }

        /// <summary>
        /// Writes per-profile layout ids and statuses back to the live config (matched by profile id,
        /// since the settings page may have saved a new config object during the sync) and drops
        /// processed layout deletions from the queue.
        /// </summary>
        private void SaveState(SyncResult result, List<string> processedDeletes, string failure = null)
        {
            var plugin = Plugin.Instance;
            if (plugin == null) return;
            try
            {
                var cfg = plugin.Configuration;
                var now = Now();
                foreach (var pr in result.Profiles)
                {
                    var profile = (cfg.Profiles ?? new MultiviewProfile[0]).FirstOrDefault(p => p != null && p.Id == pr.ProfileId);
                    if (profile == null) continue;
                    if (pr.Success && !string.IsNullOrEmpty(pr.LayoutId)) profile.LayoutId = pr.LayoutId;
                    profile.LastSyncUtc = now;
                    profile.LastSyncStatus = pr.Success ? pr.Message : "Failed: " + pr.Message;
                }
                if (processedDeletes.Count > 0)
                {
                    cfg.LayoutsToDelete = (cfg.LayoutsToDelete ?? new string[0])
                        .Where(id => !processedDeletes.Contains(id, StringComparer.OrdinalIgnoreCase))
                        .ToArray();
                }
                cfg.LastSyncUtc = now;
                cfg.LastSyncStatus = failure ?? result.Message ?? "";
                plugin.SaveConfiguration();
            }
            catch (Exception ex)
            {
                _logger.Debug("could not persist sync state: {0}", ex.Message);
            }
        }
    }
}
