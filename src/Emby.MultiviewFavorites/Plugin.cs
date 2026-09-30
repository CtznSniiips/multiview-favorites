using System;
using System.Collections.Generic;
using System.IO;
using Emby.MultiviewFavorites.Configuration;
using Emby.MultiviewFavorites.Sync;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Emby.MultiviewFavorites
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        public static readonly Guid PluginId = new Guid("6c1f7a52-3d0e-4b8a-9e51-2f6d4c8b7a19");

        public static Plugin Instance { get; private set; }

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public override string Name => "Multiview Favorites";

        public override string Description =>
            "Syncs a user's favorited Live TV channels into a Dispatcharr Multiview layout.";

        public override Guid Id => PluginId;

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public Stream GetThumbImage()
        {
            return GetType().Assembly.GetManifestResourceStream(GetType().Namespace + ".thumb.png");
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "multiviewfavorites",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                    IsMainConfigPage = true,
                    EnableInMainMenu = false,
                },
                new PluginPageInfo
                {
                    Name = "multiviewfavoritesjs",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.js",
                },
            };
        }

        /// <summary>
        /// Called when the dashboard config page saves. Keeps plugin-owned state the
        /// page may have posted back stale, then kicks off a sync.
        /// </summary>
        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            var incoming = configuration as PluginConfiguration;
            var current = Configuration;
            if (incoming != null && current != null)
            {
                if (string.IsNullOrEmpty(incoming.LayoutId)) incoming.LayoutId = current.LayoutId;
                incoming.LastSyncUtc = current.LastSyncUtc;
                incoming.LastSyncStatus = current.LastSyncStatus;
                if (incoming.MaxStreams < 2) incoming.MaxStreams = 2;
                if (incoming.MaxStreams > 9) incoming.MaxStreams = 9;
                if (incoming.ManualOrder == null) incoming.ManualOrder = new string[0];
                incoming.TileOrder = Sync.TileOrders.Normalize(incoming.TileOrder);
            }

            base.UpdateConfiguration(configuration);
            SyncCoordinator.RequestSync("configuration saved", TimeSpan.FromSeconds(1));
        }
    }
}
