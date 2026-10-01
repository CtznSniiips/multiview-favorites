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

        /// <summary>
        /// Moves 1.x single-channel settings into the profile list (once). Called from the entry
        /// point: the plugin's configuration path isn't set up yet while the constructor runs.
        /// Returns the migrated multiview's name, or null if there was nothing to migrate.
        /// </summary>
        public string EnsureMigrated()
        {
            var cfg = Configuration;
            if (!ConfigNormalizer.MigrateLegacy(cfg)) return null;
            SaveConfiguration();
            return cfg.Profiles.Length > 0 ? cfg.Profiles[0].MultiviewName : "";
        }

        public override string Name => "Multiview Favorites";

        public override string Description =>
            "Turns Emby users' favorite Live TV channels into Dispatcharr Multiview channels.";

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
        /// Called when the dashboard config page saves. Cleans up the posted profiles, keeps
        /// plugin-owned state the page may have posted back stale, queues the layouts of removed
        /// profiles for deletion, then kicks off a sync.
        /// </summary>
        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            ConfigNormalizer.NormalizeIncoming(configuration as PluginConfiguration, Configuration);

            base.UpdateConfiguration(configuration);
            SyncCoordinator.RequestSync("configuration saved", TimeSpan.FromSeconds(1));
        }
    }
}
