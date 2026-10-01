using System;
using System.Linq;
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

    [Route("/MultiviewFavorites/Preview", "POST", Summary = "Shows what syncing one (possibly unsaved) multiview would do, without changing anything")]
    [Authenticated(Roles = "Admin")]
    public class PreviewRequest : IReturn<SyncResult>
    {
        public string Id { get; set; }
        public string EmbyUserId { get; set; }
        public string MultiviewName { get; set; }
        public int MaxStreams { get; set; }
        public string TileOrder { get; set; }
        public string[] ManualOrder { get; set; }
        public string LayoutStyle { get; set; }
        public string AudioSource { get; set; }
        public string LayoutId { get; set; }
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
            return _engine.RunAsync("manual sync", CancellationToken.None).GetAwaiter().GetResult();
        }

        public object Post(PreviewRequest request)
        {
            var saved = (Plugin.Instance?.Configuration?.Profiles ?? new MultiviewProfile[0])
                .FirstOrDefault(p => p != null && !string.IsNullOrEmpty(request.Id) && p.Id == request.Id);
            var profile = new MultiviewProfile
            {
                Id = string.IsNullOrEmpty(request.Id) ? Guid.NewGuid().ToString("N") : request.Id,
                Enabled = true,
                EmbyUserId = request.EmbyUserId ?? "",
                MultiviewName = request.MultiviewName,
                MaxStreams = Math.Max(2, Math.Min(SyncPlanner.MaxSlots, request.MaxStreams <= 0 ? 4 : request.MaxStreams)),
                TileOrder = TileOrders.Normalize(request.TileOrder),
                ManualOrder = request.ManualOrder ?? new string[0],
                LayoutStyle = request.LayoutStyle ?? "auto",
                AudioSource = request.AudioSource ?? "0",
                LayoutId = !string.IsNullOrEmpty(request.LayoutId) ? request.LayoutId : saved?.LayoutId ?? "",
            };
            return _engine.PreviewAsync(profile, CancellationToken.None).GetAwaiter().GetResult();
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
