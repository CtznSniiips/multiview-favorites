using System.Threading;
using Emby.MultiviewFavorites.Configuration;
using Emby.MultiviewFavorites.Sync;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Tasks;

namespace Emby.MultiviewFavorites.Api
{
    [Route("/MultiviewFavorites/Sync", "POST", Summary = "Runs a favorites -> Dispatcharr multiview sync now")]
    [Authenticated(Roles = "Admin")]
    public class SyncNowRequest : IReturn<SyncResult>
    {
    }

    [Route("/MultiviewFavorites/Preview", "GET", Summary = "Shows what a sync would do, without changing anything")]
    [Authenticated(Roles = "Admin")]
    public class PreviewRequest : IReturn<SyncResult>
    {
        /// <summary>Optional: channel | favorited | manual, to preview an unsaved choice.</summary>
        public string TileOrder { get; set; }

        /// <summary>Optional: comma-separated Emby channel ids, to preview an unsaved manual order.</summary>
        public string ManualOrder { get; set; }
    }

    [Route("/MultiviewFavorites/Test", "POST", Summary = "Tests the Dispatcharr connection with the given (unsaved) settings")]
    [Authenticated(Roles = "Admin")]
    public class TestConnectionRequest : IReturn<SyncResult>
    {
        public string DispatcharrUrl { get; set; }
        public string DashPath { get; set; }
        public string DispatcharrUsername { get; set; }
        public string DispatcharrPassword { get; set; }
    }

    public class MultiviewFavoritesService : IService
    {
        private readonly SyncEngine _engine;

        public MultiviewFavoritesService(
            ILibraryManager libraryManager,
            IUserManager userManager,
            IUserDataManager userDataManager,
            ILiveTvManager liveTvManager,
            ITaskManager taskManager,
            ILogManager logManager)
        {
            _engine = SyncCoordinator.Engine ?? new SyncEngine(libraryManager, userManager, userDataManager, liveTvManager,
                taskManager, logManager.GetLogger("MultiviewFavorites"));
        }

        public object Post(SyncNowRequest request)
        {
            return _engine.RunAsync("manual sync", dryRun: false, CancellationToken.None).GetAwaiter().GetResult();
        }

        public object Get(PreviewRequest request)
        {
            var manual = request.ManualOrder == null
                ? null
                : request.ManualOrder.Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries);
            return _engine.RunAsync("preview", true, request.TileOrder, manual, CancellationToken.None).GetAwaiter().GetResult();
        }

        public object Post(TestConnectionRequest request)
        {
            var saved = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var cfg = new PluginConfiguration
            {
                DispatcharrUrl = string.IsNullOrWhiteSpace(request.DispatcharrUrl) ? saved.DispatcharrUrl : request.DispatcharrUrl,
                DashPath = request.DashPath ?? saved.DashPath,
                DispatcharrUsername = string.IsNullOrEmpty(request.DispatcharrUsername) ? saved.DispatcharrUsername : request.DispatcharrUsername,
                DispatcharrPassword = string.IsNullOrEmpty(request.DispatcharrPassword) ? saved.DispatcharrPassword : request.DispatcharrPassword,
            };
            return _engine.TestConnectionAsync(cfg, CancellationToken.None).GetAwaiter().GetResult();
        }
    }
}
