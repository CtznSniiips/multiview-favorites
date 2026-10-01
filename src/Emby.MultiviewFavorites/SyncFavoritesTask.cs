using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emby.MultiviewFavorites.Sync;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.MultiviewFavorites
{
    /// <summary>
    /// Safety net: favorites can change in ways that don't raise UserDataSaved
    /// (channel renumbered in Dispatcharr, channel removed, etc.), so re-sync periodically.
    /// Shows under Scheduled Tasks -> Live TV and can be run on demand.
    /// </summary>
    public class SyncFavoritesTask : IScheduledTask
    {
        private readonly SyncEngine _engine;

        public SyncFavoritesTask(
            ILibraryManager libraryManager,
            IUserManager userManager,
            IUserDataManager userDataManager,
            ILiveTvManager liveTvManager,
            ITaskManager taskManager,
            ILogManager logManager)
        {
            _engine = new SyncEngine(libraryManager, userManager, userDataManager, liveTvManager, taskManager,
                logManager.GetLogger("MultiviewFavorites"));
        }

        public string Name => "Sync favorites to Dispatcharr Multiview";

        public string Key => "MultiviewFavoritesSync";

        public string Description => "Pushes each multiview's user's favorite Live TV channels into its Dispatcharr Multiview layout.";

        public string Category => "Live TV";

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            progress?.Report(0);
            var result = await _engine.RunAsync("scheduled task", cancellationToken).ConfigureAwait(false);
            progress?.Report(100);
            if (!result.Success && Plugin.Instance?.Configuration?.Enabled == true)
                throw new Exception(result.Message);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = "IntervalTrigger",
                    IntervalTicks = TimeSpan.FromHours(1).Ticks,
                },
            };
        }
    }
}
