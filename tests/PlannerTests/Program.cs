using System.Diagnostics;
using System.Net.Http;
using Emby.MultiviewFavorites.Sync;

// Mirrors SyncEngine.RunAsync's Dispatcharr round-trip (the Emby-only parts are the favorites lookup and logging).
static async Task<SyncPlan> RunSync(string baseUrl, List<FavoriteChannel> favs, SyncOptions opts, bool restart = false, string dash = "/dash", string password = "secret")
{
    var client = new DispatcharrClient(baseUrl, dash, "admin", password);
    await client.LoginAsync(CancellationToken.None);
    var channels = await client.GetChannelsAsync(CancellationToken.None);
    var settings = await client.GetSettingsAsync(CancellationToken.None);
    var plan = SyncPlanner.Build(favs, channels, settings, opts);
    if (plan.HasChanges)
    {
        await client.PatchSettingsAsync(plan.Updates, CancellationToken.None);
        if (plan.IsNewLayout || plan.Renamed) await client.RefreshM3uAsync(CancellationToken.None);
        if (plan.TilesChanged && !plan.IsNewLayout && restart) await client.RestartStreamAsync(plan.LayoutId, CancellationToken.None);
    }
    opts.KnownLayoutId = plan.LayoutId;
    return plan;
}

var failures = 0;
void Check(bool cond, string what)
{
    Console.WriteLine((cond ? "  PASS " : "  FAIL ") + what);
    if (!cond) failures++;
}

FavoriteChannel F(string name, string num) => new FavoriteChannel { Name = name, NumberText = num, EmbyId = Guid.NewGuid().ToString("N") };

// ------------------------------------------------------------------ MiniJson
Console.WriteLine("MiniJson");
{
    var src = "{\"a\":[1,2.5,-3e2,true,false,null],\"s\":\"q\\\"\\\\\\n\\u00e9\\u2603/\",\"o\":{\"x\":{}},\"e\":[]}";
    var obj = MiniJson.AsObject(MiniJson.Parse(src));
    var arr = MiniJson.AsArray(obj["a"]);
    Check((double)arr[1] == 2.5 && (double)arr[2] == -300 && (bool)arr[3] && arr[5] == null, "parses numbers/bools/null");
    Check((string)obj["s"] == "q\"\\\né☃/", "parses escapes and \\u sequences");
    var again = MiniJson.AsObject(MiniJson.Parse(MiniJson.Serialize(obj)));
    Check((string)again["s"] == (string)obj["s"] && MiniJson.AsArray(again["a"]).Count == 6, "round-trips");
    Check(MiniJson.ToCanonical(4.0) == "4" && MiniJson.ToCanonical(4) == "4" && MiniJson.ToCanonical("4") == "4", "canonical 4 == 4.0 == \"4\"");
    Check(MiniJson.Serialize(new Dictionary<string, object> { ["k"] = null }) == "{\"k\":null}", "serializes null (delete-key) values");
    var threw = false; try { MiniJson.Parse("{\"a\":1,}"); } catch (FormatException) { threw = true; }
    Check(threw, "rejects malformed JSON");
}

// ------------------------------------------------------------------ channel numbers
Console.WriteLine("ChannelNumbers");
Check(ChannelNumbers.Parse("5") == 5m && ChannelNumbers.Parse("5.0") == 5m, "5 == 5.0");
Check(ChannelNumbers.Parse("5-1") == 5.1m && ChannelNumbers.Parse("007") == 7m, "5-1 -> 5.1, 007 -> 7");
Check(ChannelNumbers.Parse("") == null && ChannelNumbers.Parse("abc") == null, "blank / text -> null");
Check(ChannelNumbers.Format(5.0m) == "5" && ChannelNumbers.Format(5.10m) == "5.1", "formats without trailing zeros");

// ------------------------------------------------------------------ planner (pure)
Console.WriteLine("Planner");
{
    var dch = new List<DispatcharrChannel>
    {
        new() { Id = "12", Name = "CBS", Number = 2 }, new() { Id = "13", Name = "NBC", Number = 4 },
        new() { Id = "14", Name = "FOX", Number = 5 }, new() { Id = "17", Name = "PBS Kids", Number = 5.1m },
        new() { Id = "15", Name = "ESPN", Number = 206 },
    };
    var favs = new List<FavoriteChannel>
    {
        F("ESPN", "206"), F("NBC", "4"), F("Local", ""), F("CBS", "2"), F("Unknown", "300"),
        F("PBS Kids", "5.1"), F("FOX", "5"), F("Emby Favorites", "9001"),
    };
    var plan = SyncPlanner.Build(favs, dch, new Dictionary<string, object>(), new SyncOptions { MaxStreams = 4 }, () => "abcd1234");
    Check(plan.IsNewLayout && plan.LayoutId == "abcd1234", "creates a new layout when none exists");
    Check(string.Join(",", plan.TileIds) == "12,13,14,17", "picks the 4 lowest matched channel numbers (2,4,5,5.1)");
    Check(plan.Rows.First(r => r.EmbyName == "ESPN").Status.Contains("limit"), "206 is over the limit");
    Check(plan.Rows.First(r => r.EmbyName == "Unknown").Status.Contains("no Dispatcharr channel"), "300 has no Dispatcharr match");
    Check(plan.Rows.First(r => r.EmbyName == "Local").Status.Contains("no channel number"), "blank number skipped");
    Check(plan.Rows.First(r => r.EmbyName == "Emby Favorites").Status.Contains("multiview"), "the multiview channel itself is skipped");
    Check(MiniJson.ToCanonical(plan.Updates["multiview_abcd1234_channel_count"]) == "4", "channel_count = 4");
    Check((string)plan.Updates["multiview_abcd1234_selector_type"] == "classic" && (string)plan.Updates["multiview_abcd1234_epg_source_mode"] == "dummy", "classic selector + dummy EPG on create");
    Check(MiniJson.AsArray(plan.Updates["multiview_order"]).Count == 1, "appends to multiview_order");

    var dup = SyncPlanner.Build(new[] { F("A", "2"), F("B", "2.0") }, dch, new Dictionary<string, object>(), new SyncOptions(), () => "x0000001");
    Check(dup.TileIds.Count == 1 && dup.Rows[1].Status.Contains("duplicate"), "two Emby channels on the same number tile once");
}

// ------------------------------------------------------------------ tile order modes
Console.WriteLine("Tile order");
{
    var dch = new List<DispatcharrChannel>
    {
        new() { Id = "12", Name = "CBS", Number = 2 }, new() { Id = "13", Name = "NBC", Number = 4 },
        new() { Id = "14", Name = "FOX", Number = 5 }, new() { Id = "15", Name = "ESPN", Number = 206 },
    };
    var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    FavoriteChannel G(string id, string name, string num, int? daysAgo) => new FavoriteChannel
        { EmbyId = id, Name = name, NumberText = num, FavoritedUtc = daysAgo == null ? null : t0.AddDays(-daysAgo.Value) };
    var favs = new List<FavoriteChannel>
    {
        G("e-cbs", "CBS", "2", 1), G("e-nbc", "NBC", "4", null), G("e-fox", "FOX", "5", 30), G("e-espn", "ESPN", "206", 90),
    };
    string Run(string order, IList<string> manual = null, int max = 3) =>
        string.Join(",", SyncPlanner.Build(favs, dch, new Dictionary<string, object>(),
            new SyncOptions { TileOrder = order, ManualOrder = manual ?? new List<string>(), MaxStreams = max }, () => "t0000001").TileIds);

    Check(Run("channel") == "12,13,14", "channel: lowest numbers 2,4,5");
    Check(Run("favorited") == "15,14,12", "favorited: oldest first (ESPN 90d, FOX 30d, CBS 1d); unknown date goes last");
    Check(Run("manual", new[] { "e-fox", "e-espn" }) == "14,15,12", "manual: FOX, ESPN, then unlisted by channel number");
    Check(Run("manual", new[] { "e-espn", "e-cbs", "e-nbc", "e-fox" }) == "15,12,13", "manual: full custom order, limited to max");
    Check(Run("manual", new[] { "gone", "e-nbc" }) == "13,12,14", "manual: ids no longer favorited are ignored");
    Check(Run("bogus") == Run("channel"), "unknown mode falls back to channel order");
    var plan = SyncPlanner.Build(favs, dch, new Dictionary<string, object>(), new SyncOptions { TileOrder = "favorited", MaxStreams = 2 }, () => "t0000002");
    Check(plan.Rows[0].EmbyId == "e-espn" && plan.Rows[0].FavoritedUtc != null && plan.Rows.Count(r => r.Eligible) == 4 && plan.Rows.Count(r => r.Included) == 2,
        "rows carry id, favorited time and eligibility (over-limit rows stay eligible)");
}

// ------------------------------------------------------------------ config migration / normalization
Console.WriteLine("Config");
{
    var legacy = new Emby.MultiviewFavorites.Configuration.PluginConfiguration
    {
        EmbyUserId = "user-a", MultiviewName = "Steve Favorites", MaxStreams = 3, TileOrder = "manual",
        ManualOrder = new[] { "x", "y" }, LayoutStyle = "", AudioSource = "all", LayoutId = "aaaa1111",
        LastSyncStatus = "ok",
    };
    Check(Emby.MultiviewFavorites.Configuration.ConfigNormalizer.MigrateLegacy(legacy), "1.x settings are detected");
    var mp = legacy.Profiles.Single();
    Check(mp.EmbyUserId == "user-a" && mp.MultiviewName == "Steve Favorites" && mp.MaxStreams == 3 && mp.TileOrder == "manual"
          && mp.ManualOrder.SequenceEqual(new[] { "x", "y" }) && mp.LayoutStyle == "" && mp.AudioSource == "all" && mp.LayoutId == "aaaa1111",
        "migrated into one profile, keeping its layout id and every setting (including \"don't change\" style)");
    Check(legacy.EmbyUserId == "" && legacy.LayoutId == "" && !Emby.MultiviewFavorites.Configuration.ConfigNormalizer.MigrateLegacy(legacy),
        "legacy fields cleared; migration runs once");
    Check(!Emby.MultiviewFavorites.Configuration.ConfigNormalizer.MigrateLegacy(new Emby.MultiviewFavorites.Configuration.PluginConfiguration()),
        "fresh install: nothing to migrate");

    var current = new Emby.MultiviewFavorites.Configuration.PluginConfiguration
    {
        Profiles = new[]
        {
            new Emby.MultiviewFavorites.Configuration.MultiviewProfile { Id = "p1", MultiviewName = "A", LayoutId = "lay00001", LastSyncStatus = "synced" },
            new Emby.MultiviewFavorites.Configuration.MultiviewProfile { Id = "p2", MultiviewName = "B", LayoutId = "lay00002" },
            new Emby.MultiviewFavorites.Configuration.MultiviewProfile { Id = "p3", MultiviewName = "C", LayoutId = "" },
        },
    };
    var posted = new Emby.MultiviewFavorites.Configuration.PluginConfiguration
    {
        Profiles = new[]
        {
            // page posted p1 back with stale/empty state and silly values
            new Emby.MultiviewFavorites.Configuration.MultiviewProfile { Id = "p1", MultiviewName = "  A  ", LayoutId = "", LastSyncStatus = "", MaxStreams = 50, TileOrder = "weird" },
            new Emby.MultiviewFavorites.Configuration.MultiviewProfile { Id = "", MultiviewName = "", MaxStreams = 0 },
        },
    };
    Emby.MultiviewFavorites.Configuration.ConfigNormalizer.NormalizeIncoming(posted, current);
    var q1 = posted.Profiles[0];
    Check(q1.LayoutId == "lay00001" && q1.LastSyncStatus == "synced" && q1.MultiviewName == "A" && q1.MaxStreams == 9 && q1.TileOrder == "channel",
        "keeps plugin-owned state, trims names, clamps values");
    Check(!string.IsNullOrEmpty(posted.Profiles[1].Id) && posted.Profiles[1].MultiviewName == "Emby Favorites" && posted.Profiles[1].MaxStreams == 4,
        "new profile gets an id and defaults");
    Check(posted.LayoutsToDelete.SequenceEqual(new[] { "lay00002" }), "removed profile with a layout -> queued for deletion (one without a layout isn't)");

    var dupNames = Emby.MultiviewFavorites.Configuration.ConfigNormalizer.DuplicateNames(new[]
    {
        new Emby.MultiviewFavorites.Configuration.MultiviewProfile { MultiviewName = "Kids" },
        new Emby.MultiviewFavorites.Configuration.MultiviewProfile { MultiviewName = "kids " },
        new Emby.MultiviewFavorites.Configuration.MultiviewProfile { MultiviewName = "Steve" },
    });
    Check(dupNames.Count == 1 && dupNames.Contains("Kids"), "duplicate names detected case-insensitively");
}

// ------------------------------------------------------------------ multi-profile planning
Console.WriteLine("Multi-profile planner");
{
    var dch = new List<DispatcharrChannel>
    {
        new() { Id = "12", Name = "CBS", Number = 2 }, new() { Id = "13", Name = "NBC", Number = 4 },
        new() { Id = "14", Name = "FOX", Number = 5 }, new() { Id = "15", Name = "ESPN", Number = 206 },
        new() { Id = "16", Name = "Steve's Favorites", Number = 9001 },
    };
    var settings = new Dictionary<string, object>
    {
        ["multiview_order"] = new List<object> { "old00001" },
        ["multiview_old00001_name"] = "Sports Wall",
        ["multiview_old00001_channel_1"] = "15",
    };
    var ids = new Queue<string>(new[] { "new00001", "new00002" });
    var multi = SyncPlanner.BuildAll(new[]
    {
        new ProfilePlanInput { ProfileId = "steve", Favorites = new List<FavoriteChannel> { F("CBS", "2"), F("NBC", "4"), F("Kids' Favorites", "9002") },
            Options = new SyncOptions { MultiviewName = "Steve's Favorites", OtherMultiviewNames = new[] { "Kids' Favorites" } } },
        new ProfilePlanInput { ProfileId = "kids", Favorites = new List<FavoriteChannel> { F("FOX", "5"), F("ESPN", "206"), F("Steve's Favorites", "9001") },
            Options = new SyncOptions { MultiviewName = "Kids' Favorites", OtherMultiviewNames = new[] { "Steve's Favorites" } } },
    }, dch, settings, null, () => ids.Dequeue());
    var order = MiniJson.AsArray(multi.Updates["multiview_order"]).Select(x => (string)x).ToList();
    Check(string.Join(",", order) == "old00001,new00001,new00002", "two new layouts in one sync both land in multiview_order");
    Check((string)multi.Updates["multiview_new00001_channel_1"] == "12" && (string)multi.Updates["multiview_new00002_channel_1"] == "14", "each layout gets its own user's tiles");
    Check(multi.Plans[1].Value.Rows.First(r => r.EmbyName == "Steve's Favorites").Status.Contains("multiview"), "another profile's multiview channel is never tiled");
    Check(multi.NeedsM3uRefresh, "new layouts -> M3U refresh");

    var del = SyncPlanner.BuildAll(new ProfilePlanInput[0], dch, settings, new[] { "old00001", "missing1" });
    Check(del.DeletedLayoutIds.SequenceEqual(new[] { "old00001" }) && del.AlreadyGoneLayoutIds.SequenceEqual(new[] { "missing1" }),
        "deletes queued layouts; already-gone ones are just dropped");
    Check(del.Updates.ContainsKey("multiview_old00001_name") && del.Updates["multiview_old00001_name"] == null
          && MiniJson.AsArray(del.Updates["multiview_order"]).Count == 0, "deletion nulls the layout's keys and removes it from the order");
}

// ------------------------------------------------------------------ end to end against real api.py
var srcDir = Environment.GetEnvironmentVariable("MV_SRC")!;
var state = Path.Combine(Path.GetTempPath(), $"mv_state_{Guid.NewGuid():N}.json");
// Free port, so a leftover mock (or anything else) on a fixed port can't hijack the run.
int port;
{
    var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    probe.Start();
    port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
}
var mockErr = new System.Text.StringBuilder();
var py = Process.Start(new ProcessStartInfo("python3", $"../mock_dispatcharr.py {srcDir} {port} {state}") { RedirectStandardOutput = true, RedirectStandardError = true })!;
// Drain stderr continuously so a chatty mock can never block on a full pipe.
py.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (mockErr) mockErr.AppendLine(e.Data); };
py.BeginErrorReadLine();
try
{
    var line = await py.StandardOutput.ReadLineAsync();
    if (line != "ready")
    {
        py.WaitForExit(2000);
        throw new Exception("mock Dispatcharr failed to start:\n" + mockErr);
    }
    var baseUrl = $"http://127.0.0.1:{port}";
    var http = new HttpClient();
    async Task<Dictionary<string, object>> Resolve(string id) => MiniJson.AsObject(MiniJson.Parse(await http.GetStringAsync($"{baseUrl}/__resolve?id={id}")));
    async Task<List<string>> Calls() => MiniJson.AsArray(MiniJson.Parse(await http.GetStringAsync($"{baseUrl}/__calls"))).Select(x => (string)x).ToList();
    string Tiles(Dictionary<string, object> r) => string.Join(",", MiniJson.AsArray(r["tiles"]).Select(x => (string)x));

    var opts = new SyncOptions { MultiviewName = "Emby Favorites", MaxStreams = 4, LayoutStyle = "auto", AudioSource = "0" };
    var favs = new List<FavoriteChannel> { F("NBC", "4"), F("CBS", "2"), F("ESPN", "206"), F("FOX", "5"), F("PBS Kids", "5-1"), F("ABC", "007"), F("Sports Wall", "9000") };

    Console.WriteLine("E2E: create");
    var p1 = await RunSync(baseUrl, favs, opts);
    var r1 = await Resolve(p1.LayoutId);
    Check(p1.IsNewLayout, "new layout created");
    Check(Tiles(r1) == "CBS,NBC,FOX,PBS Kids", "Dispatcharr resolves tiles CBS,NBC,FOX,PBS Kids (lowest 4 of 2,4,5,5.1,7,206)");
    Check((bool)r1["playable"], "layout is playable (>= 2 tiles)");
    Check(MiniJson.AsArray(r1["order"]).Count == 2 && MiniJson.ToCanonical(r1["count"]) == "2", "existing 'Sports Wall' kept; multiview_count synced to 2 (survives reconcile)");
    Check(MiniJson.AsArray(r1["fields"]).Select(x => (string)x).Contains($"multiview_{p1.LayoutId}_channel_4"), "Multiview's own settings page renders the new layout");
    Check((await Calls()).Contains("refresh"), "M3U refresh requested for the new layout");
    Check(p1.Rows.First(r => r.EmbyName == "Sports Wall").Status.Contains("multiview"), "favorited 'Sports Wall' multiview is not tiled");

    Console.WriteLine("E2E: idempotent");
    var p2 = await RunSync(baseUrl, favs, opts);
    Check(!p2.HasChanges && !p2.IsNewLayout && p2.LayoutId == p1.LayoutId, "second sync makes no changes");

    Console.WriteLine("E2E: unfavorite FOX with restart on");
    favs.RemoveAll(f => f.Name == "FOX");
    var before = (await Calls()).Count;
    var p3 = await RunSync(baseUrl, favs, opts, restart: true);
    var r3 = await Resolve(p1.LayoutId);
    var newCalls = (await Calls()).Skip(before).ToList();
    Check(Tiles(r3) == "CBS,NBC,PBS Kids,ABC", "tiles now CBS,NBC,PBS Kids,ABC");
    Check(newCalls.SequenceEqual(new[] { $"restart:{p1.LayoutId}" }), "running stream restarted, no M3U refresh");

    Console.WriteLine("E2E: manual reorder");
    var ids = favs.ToDictionary(f => f.Name, f => f.EmbyId);
    opts.TileOrder = "manual";
    opts.ManualOrder = new List<string> { ids["ABC"], ids["PBS Kids"], ids["CBS"] };
    before = (await Calls()).Count;
    var pm = await RunSync(baseUrl, favs, opts, restart: true);
    Check(Tiles(await Resolve(p1.LayoutId)) == "ABC,PBS Kids,CBS,NBC", "tiles follow the manual order, then channel number");
    Check(pm.TilesChanged && (await Calls()).Skip(before).Contains($"restart:{p1.LayoutId}"), "reorder alone counts as a tile change");
    opts.TileOrder = "channel";
    await RunSync(baseUrl, favs, opts);
    Check(Tiles(await Resolve(p1.LayoutId)) == "CBS,NBC,PBS Kids,ABC", "switching back to channel order restores it");

    Console.WriteLine("E2E: rename");
    before = (await Calls()).Count;
    opts.MultiviewName = "My Favorites";
    var p4 = await RunSync(baseUrl, favs, opts);
    Check(p4.Renamed && p4.LayoutId == p1.LayoutId && (await Calls()).Skip(before).Contains("refresh"), "same layout renamed and M3U refreshed");

    Console.WriteLine("E2E: lost layout id, found by name");
    opts.KnownLayoutId = "";
    var p5 = await RunSync(baseUrl, favs, opts);
    Check(!p5.IsNewLayout && p5.LayoutId == p1.LayoutId, "re-attaches to the existing layout by name");

    Console.WriteLine("E2E: max 4 -> 2");
    opts.MaxStreams = 2;
    var p6 = await RunSync(baseUrl, favs, opts);
    var st = MiniJson.AsObject(MiniJson.Parse(File.ReadAllText(state)));
    Check(Tiles(await Resolve(p1.LayoutId)) == "CBS,NBC", "only CBS,NBC tiled");
    Check(!st.ContainsKey($"multiview_{p1.LayoutId}_channel_3") && !st.ContainsKey($"multiview_{p1.LayoutId}_channel_4"), "stale slots 3-4 deleted");

    Console.WriteLine("E2E: single favorite");
    opts.MaxStreams = 4;
    var p7 = await RunSync(baseUrl, new List<FavoriteChannel> { F("CBS", "2") }, opts);
    var r7 = await Resolve(p1.LayoutId);
    Check(!(bool)r7["playable"] && p7.Warnings.Any(w => w.Contains("at least 2")), "warns that 1 channel isn't playable");

    Console.WriteLine("E2E: multiple multiviews");
    {
        var client = new DispatcharrClient(baseUrl, "/dash", "admin", "secret");
        async Task<MultiPlan> SyncAll(IEnumerable<ProfilePlanInput> ps, IEnumerable<string> deletes = null)
        {
            var mp = SyncPlanner.BuildAll(ps, await client.GetChannelsAsync(CancellationToken.None), await client.GetSettingsAsync(CancellationToken.None), deletes);
            if (mp.HasChanges) await client.PatchSettingsAsync(mp.Updates, CancellationToken.None);
            return mp;
        }
        var kidsFavs = new List<FavoriteChannel> { F("PBS Kids", "5.1"), F("ABC", "7"), F("FOX", "5") };
        var annaFavs = new List<FavoriteChannel> { F("ESPN", "206"), F("CBS", "2") };
        ProfilePlanInput P(string id, string name, List<FavoriteChannel> fv) => new ProfilePlanInput { ProfileId = id, Favorites = fv,
            Options = new SyncOptions { MultiviewName = name, OtherMultiviewNames = new[] { "Kids", "Anna", "My Favorites" }.Where(n => n != name).ToList() } };

        var mm = await SyncAll(new[] { P("kids", "Kids", kidsFavs), P("anna", "Anna", annaFavs) });
        var kidsId = mm.Plans[0].Value.LayoutId; var annaId = mm.Plans[1].Value.LayoutId;
        var rk = await Resolve(kidsId);
        Check(mm.Plans.All(p => p.Value.IsNewLayout) && Tiles(rk) == "FOX,PBS Kids,ABC" && Tiles(await Resolve(annaId)) == "CBS,ESPN",
            "two users' multiviews created in one patch, each with its own tiles");
        Check(MiniJson.AsArray(rk["order"]).Count == 4 && MiniJson.ToCanonical(rk["count"]) == "4",
            "Multiview sees all 4 layouts (Sports Wall, the earlier one, Kids, Anna); count in sync");
        var again = await SyncAll(new[] { P("kids", "Kids", kidsFavs), P("anna", "Anna", annaFavs) });
        Check(!again.HasChanges, "re-sync of both is a no-op");

        var gone = await SyncAll(new[] { P("kids", "Kids", kidsFavs) }, new[] { annaId });
        var st2 = MiniJson.AsObject(MiniJson.Parse(File.ReadAllText(state)));
        var order2 = MiniJson.AsArray(st2["multiview_order"]).Select(x => (string)x).ToList();
        Check(gone.DeletedLayoutIds.SequenceEqual(new[] { annaId }) && !order2.Contains(annaId) && order2.Contains(kidsId) && order2.Count == 3
              && !st2.Keys.Any(k => k.StartsWith($"multiview_{annaId}_")) && MiniJson.ToCanonical(st2["multiview_count"]) == "3",
            "removed profile's layout deleted (keys gone, order + count updated); others untouched");
        Check(Tiles(await Resolve(kidsId)) == "FOX,PBS Kids,ABC", "remaining multiview still resolves");
    }

    Console.WriteLine("E2E: errors");
    var msg = "";
    try { await RunSync(baseUrl, favs, opts, password: "wrong"); } catch (DispatcharrException ex) { msg = ex.Message; }
    Check(msg.Contains("rejected the credentials"), "bad password -> clear message: " + msg);
    msg = "";
    try { await RunSync(baseUrl, favs, opts, dash: "/nope"); } catch (DispatcharrException ex) { msg = ex.Message; }
    Check(msg.Contains("Web Dashboard"), "wrong path -> dashboard hint");
    msg = "";
    try { await RunSync("http://127.0.0.1:1", favs, opts); } catch (DispatcharrException ex) { msg = ex.Message; }
    Check(msg.StartsWith("Could not reach"), "unreachable -> clear message");
}
finally
{
    try { py.Kill(); } catch { }
}

Console.WriteLine(failures == 0 ? "\nALL PASSED" : $"\n{failures} FAILED");
return failures == 0 ? 0 : 1;
