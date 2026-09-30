using System;
using System.Threading;

namespace Emby.MultiviewFavorites.Sync
{
    /// <summary>
    /// Debounces sync requests. Favoriting five channels in a row (or saving the
    /// config page) collapses into a single Dispatcharr update.
    /// </summary>
    public static class SyncCoordinator
    {
        private static readonly object Lock = new object();
        private static SyncEngine _engine;
        private static Timer _timer;
        private static string _pendingReason;

        public static SyncEngine Engine => _engine;

        public static void Initialize(SyncEngine engine)
        {
            lock (Lock)
            {
                _engine = engine;
                if (_timer == null) _timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
            }
        }

        public static void Shutdown()
        {
            lock (Lock)
            {
                _timer?.Dispose();
                _timer = null;
                _engine = null;
            }
        }

        public static void RequestSync(string reason, TimeSpan delay)
        {
            lock (Lock)
            {
                if (_engine == null || _timer == null) return;
                _pendingReason = _pendingReason == null ? reason : _pendingReason + "; " + reason;
                _timer.Change(delay, Timeout.InfiniteTimeSpan);
            }
        }

        private static async void Fire()
        {
            SyncEngine engine;
            string reason;
            lock (Lock)
            {
                engine = _engine;
                reason = _pendingReason ?? "requested";
                _pendingReason = null;
            }
            if (engine == null) return;

            try
            {
                await engine.RunAsync(reason, dryRun: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // RunAsync already logs; never let an async void crash the server.
            }
        }
    }
}
