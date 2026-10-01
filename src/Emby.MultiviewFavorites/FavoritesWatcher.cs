using System;
using Emby.MultiviewFavorites.Sync;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.MultiviewFavorites
{
    /// <summary>
    /// Watches for the configured user favoriting/unfavoriting a Live TV channel and
    /// schedules a (debounced) sync to Dispatcharr.
    /// </summary>
    public class FavoritesWatcher : IServerEntryPoint
    {
        private static readonly TimeSpan FavoriteDebounce = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(60);

        private readonly IUserDataManager _userDataManager;
        private readonly ILogger _logger;
        private readonly SyncEngine _engine;

        public FavoritesWatcher(
            ILibraryManager libraryManager,
            IUserManager userManager,
            IUserDataManager userDataManager,
            ILiveTvManager liveTvManager,
            ITaskManager taskManager,
            ILogManager logManager)
        {
            _userDataManager = userDataManager;
            _logger = logManager.GetLogger("MultiviewFavorites");
            _engine = new SyncEngine(libraryManager, userManager, userDataManager, liveTvManager, taskManager, _logger);
        }

        public void Run()
        {
            try
            {
                var migrated = Plugin.Instance?.EnsureMigrated();
                if (migrated != null) _logger.Info("moved the 1.x settings into multiview \"{0}\"", migrated);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("could not migrate 1.x settings (they'll be migrated on the next save)", ex);
            }

            SyncCoordinator.Initialize(_engine);
            _userDataManager.UserDataSaved += OnUserDataSaved;

            if (Plugin.Instance?.Configuration?.Enabled == true)
                SyncCoordinator.RequestSync("server startup", StartupDelay);
        }

        private void OnUserDataSaved(object sender, UserDataSaveEventArgs e)
        {
            try
            {
                if (e == null || e.SaveReason != UserDataSaveReason.UpdateUserRating) return;
                if (!(e.Item is LiveTvChannel channel)) return;
                if (Plugin.Instance?.Configuration?.Enabled != true) return;
                if (!_engine.IsConfiguredUser(e.User)) return;

                var state = e.UserData != null && e.UserData.IsFavorite ? "favorited" : "unfavorited";
                _logger.Debug("{0} {1} {2}", e.User?.Name, state, channel.Name);
                SyncCoordinator.RequestSync($"{state} {channel.Name}", FavoriteDebounce);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("error handling user data change", ex);
            }
        }

        public void Dispose()
        {
            _userDataManager.UserDataSaved -= OnUserDataSaved;
            SyncCoordinator.Shutdown();
        }
    }
}
