using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.MultiviewFavorites.Sync
{
    public class DispatcharrChannel
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public decimal? Number { get; set; }
    }

    public class DispatcharrException : Exception
    {
        public DispatcharrException(string message) : base(message) { }
        public DispatcharrException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Talks to the Dispatcharr Multiview plugin's dashboard REST API
    /// (http://host:9292{dash_path}/api/*). The dashboard must be enabled in
    /// the Multiview plugin settings ("Web Dashboard" = Enabled).
    /// </summary>
    public class DispatcharrClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private const int MaxAttempts = 2;
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

        private readonly string _apiBase;
        private readonly string _username;
        private readonly string _password;
        private string _accessToken;

        public DispatcharrClient(string baseUrl, string dashPath, string username, string password)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new DispatcharrException("Dispatcharr Multiview URL is not configured.");

            var root = baseUrl.Trim().TrimEnd('/');
            if (!root.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !root.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                root = "http://" + root;
            }

            var path = (dashPath ?? "").Trim().Trim('/');
            _apiBase = path.Length == 0 ? root + "/api" : root + "/" + path + "/api";
            _username = username ?? "";
            _password = password ?? "";
        }

        public string ApiBase => _apiBase;

        public async Task LoginAsync(CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_username) || string.IsNullOrEmpty(_password))
                throw new DispatcharrException("Dispatcharr username and password are required.");

            var body = MiniJson.Serialize(new Dictionary<string, object>
            {
                ["username"] = _username,
                ["password"] = _password,
            });

            var json = await SendAsync(HttpMethod.Post, "/auth/token", body, authenticated: false, ct).ConfigureAwait(false);
            var token = MiniJson.GetString(MiniJson.AsObject(json), "access");
            if (string.IsNullOrEmpty(token))
                throw new DispatcharrException("Dispatcharr login succeeded but returned no access token.");
            _accessToken = token;
        }

        public async Task<List<DispatcharrChannel>> GetChannelsAsync(CancellationToken ct)
        {
            var json = await SendAsync(HttpMethod.Get, "/channels", null, true, ct).ConfigureAwait(false);
            var arr = MiniJson.AsArray(json) ?? throw new DispatcharrException("Unexpected /api/channels response.");

            var result = new List<DispatcharrChannel>();
            foreach (var item in arr)
            {
                var o = MiniJson.AsObject(item);
                if (o == null) continue;
                result.Add(new DispatcharrChannel
                {
                    Id = MiniJson.GetString(o, "id"),
                    Name = MiniJson.GetString(o, "name"),
                    Number = ChannelNumbers.Parse(MiniJson.GetString(o, "channel_number")),
                });
            }
            return result;
        }

        /// <summary>Returns the Multiview plugin's full settings dictionary.</summary>
        public async Task<Dictionary<string, object>> GetSettingsAsync(CancellationToken ct)
        {
            var json = await SendAsync(HttpMethod.Get, "/config", null, true, ct).ConfigureAwait(false);
            var settings = MiniJson.AsObject(MiniJson.Get(MiniJson.AsObject(json), "settings"));
            if (settings == null) throw new DispatcharrException("Unexpected /api/config response (no settings).");
            return settings;
        }

        /// <summary>Partial settings update. A null value deletes that key.</summary>
        public Task PatchSettingsAsync(Dictionary<string, object> updates, CancellationToken ct)
        {
            return SendAsync(new HttpMethod("PATCH"), "/config", MiniJson.Serialize(updates), true, ct);
        }

        /// <summary>Regenerates multiview.m3u + EPG and triggers Dispatcharr's M3U refresh.</summary>
        public Task RefreshM3uAsync(CancellationToken ct)
        {
            return SendAsync(HttpMethod.Post, "/refresh", "{}", true, ct);
        }

        /// <summary>Kills a running multiview stream for the layout (no-op if not running). Returns killed count.</summary>
        public async Task<int> RestartStreamAsync(string layoutId, CancellationToken ct)
        {
            var body = MiniJson.Serialize(new Dictionary<string, object> { ["n"] = layoutId });
            var json = await SendAsync(HttpMethod.Post, "/streams/restart", body, true, ct).ConfigureAwait(false);
            var killed = MiniJson.GetString(MiniJson.AsObject(json), "killed");
            int.TryParse(killed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n);
            return n;
        }

        private async Task<object> SendAsync(HttpMethod method, string path, string jsonBody, bool authenticated, CancellationToken ct)
        {
            var url = _apiBase + path;
            if (authenticated && _accessToken == null) await LoginAsync(ct).ConfigureAwait(false);

            HttpResponseMessage resp = null;
            for (var attempt = 1; resp == null; attempt++)
            {
                // A request message can only be sent once, so build a fresh one per attempt.
                using (var req = new HttpRequestMessage(method, url))
                {
                    if (jsonBody != null)
                        req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                    if (authenticated)
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

                    try
                    {
                        resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
                    }
                    catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
                    {
                        throw new DispatcharrException($"Timed out contacting {url}.", ex);
                    }
                    catch (HttpRequestException) when (attempt < MaxAttempts && !ct.IsCancellationRequested)
                    {
                        // The connection dropped before any response (reset, closed early, stale
                        // keep-alive). Every call here is safe to repeat, so try once more.
                        await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
                    }
                    catch (HttpRequestException ex)
                    {
                        throw new DispatcharrException($"Could not reach {url}: {ex.Message}", ex);
                    }
                }
            }

            using (resp)
            {
                var text = resp.Content == null ? "" : await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    string detail = null;
                    try { detail = MiniJson.GetString(MiniJson.AsObject(MiniJson.Parse(text)), "error"); } catch { /* not JSON */ }

                    if (resp.StatusCode == HttpStatusCode.NotFound)
                    {
                        throw new DispatcharrException(
                            $"{url} returned 404. Make sure the Multiview plugin's \"Web Dashboard\" setting is Enabled " +
                            "(then restart Dispatcharr) and that the Dashboard Mount Path matches." +
                            (detail != null ? " (" + detail + ")" : ""));
                    }
                    if (resp.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        throw new DispatcharrException("Dispatcharr rejected the credentials" + (detail != null ? ": " + detail : "."));
                    }
                    throw new DispatcharrException($"{method} {url} failed: {(int)resp.StatusCode} {detail ?? resp.ReasonPhrase}");
                }

                if (string.IsNullOrWhiteSpace(text)) return null;
                try
                {
                    return MiniJson.Parse(text);
                }
                catch (FormatException ex)
                {
                    throw new DispatcharrException($"{url} returned something that isn't JSON. Is the URL pointing at the Multiview plugin (port 9292)?", ex);
                }
            }
        }
    }

    public static class ChannelNumbers
    {
        /// <summary>Parses "5", "5.0", "5.1", "005" into a comparable decimal. Returns null for blanks/non-numbers.</summary>
        public static decimal? Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim().Replace(',', '.');
            // OTA-style "5-1" subchannels
            if (s.IndexOf('-') > 0) s = s.Replace('-', '.');
            if (decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return d; // decimal equality/hashing ignore scale, so 5.0m == 5m
            return null;
        }

        public static string Format(decimal? d)
        {
            if (d == null) return "";
            return d.Value.ToString("0.#########", CultureInfo.InvariantCulture);
        }
    }
}
