using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace zalo10_for_windows.Core
{
    /// <summary>One cookie, flattened for storage. ExpiresUnix = 0 means a session cookie.</summary>
    public sealed record CookieData(string Name, string Value, string Domain, string Path,
                                    long ExpiresUnix, bool Secure, bool HttpOnly);

    /// <summary>
    /// HTTP layer. Port of ZaloService::buildRequest from Zalo10
    /// (core/network/ZaloService_Network_Request.cpp).
    ///
    /// Unlike Zalo10, cookies are kept in a real CookieContainer, i.e. scoped per domain and path
    /// like a browser does. Zalo10 used one flat name->value map, which breaks as soon as two hosts
    /// in the redirect chain (id.zalo.me, jr.zaloapp.com, jr.chat.zalo.me, chat.zalo.me) set cookies
    /// with the same name: the last one wins and wpa.chat.zalo.me receives the wrong one.
    ///
    /// Redirects are still followed by hand (step 7 needs custom Referer headers).
    /// TLS certificates ARE verified (Zalo10 disabled that on BB10; do not do the same here).
    /// </summary>
    public sealed class ZaloHttp : IDisposable
    {
        public const string DefaultUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

        private HttpClient _client;
        private CookieContainer _jar;

        public string UserAgent { get; set; } = DefaultUserAgent;

        public ZaloHttp()
        {
            (_client, _jar) = Create();
        }

        private static (HttpClient, CookieContainer) Create()
        {
            var jar = new CookieContainer();
            var handler = new HttpClientHandler
            {
                UseCookies = true,
                CookieContainer = jar,
                AllowAutoRedirect = false
            };
            return (new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) }, jar);
        }

        /// <summary>Drop every cookie (start a clean login attempt).</summary>
        public void ResetCookies()
        {
            var old = _client;
            (_client, _jar) = Create();
            old.Dispose();
        }

        public int CookieCount => _jar.GetAllCookies().Count;

        /// <summary>Names and domains only, never values: safe for logs.</summary>
        public string CookieSummary() =>
            string.Join(", ", _jar.GetAllCookies().Cast<Cookie>().Select(c => $"{c.Name}[{c.Domain}]"));

        public List<CookieData> ExportCookies() =>
            _jar.GetAllCookies().Cast<Cookie>()
                .Where(c => !c.Expired)
                .Select(c => new CookieData(c.Name, c.Value, c.Domain, string.IsNullOrEmpty(c.Path) ? "/" : c.Path,
                    c.Expires == DateTime.MinValue ? 0 : new DateTimeOffset(c.Expires.ToUniversalTime()).ToUnixTimeSeconds(),
                    c.Secure, c.HttpOnly))
                .ToList();

        public void ImportCookies(IEnumerable<CookieData> cookies)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var d in cookies)
            {
                if (d.ExpiresUnix != 0 && d.ExpiresUnix <= now) continue; // already expired
                try
                {
                    var c = new Cookie(d.Name, d.Value, d.Path, d.Domain) { Secure = d.Secure, HttpOnly = d.HttpOnly };
                    if (d.ExpiresUnix != 0) c.Expires = DateTimeOffset.FromUnixTimeSeconds(d.ExpiresUnix).LocalDateTime;
                    _jar.Add(c);
                }
                catch (CookieException) { /* skip a cookie .NET refuses */ }
            }
        }

        public HttpRequestMessage BuildRequest(HttpMethod method, string url, string referer,
                                               bool jsonAccept = false)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.TryAddWithoutValidation("Accept", jsonAccept
                ? "application/json, text/plain, */*"
                : "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
            req.Headers.TryAddWithoutValidation("Accept-Language", "vi-VN,vi;q=0.9,en-US;q=0.8,en;q=0.7");

            if (!string.IsNullOrEmpty(referer))
            {
                req.Headers.TryAddWithoutValidation("Referer", referer);
                var u = new Uri(referer);
                req.Headers.TryAddWithoutValidation("Origin", $"{u.Scheme}://{u.Authority}");
            }

            req.Headers.TryAddWithoutValidation("sec-fetch-dest", "empty");
            req.Headers.TryAddWithoutValidation("sec-fetch-mode", "cors");
            req.Headers.TryAddWithoutValidation("sec-fetch-site", "same-origin");
            // No Cookie header here: the CookieContainer attaches the right cookies per host.
            return req;
        }

        public Task<HttpResponseMessage> GetAsync(string url, string referer, CancellationToken ct,
                                                  Action<HttpRequestMessage>? tweak = null)
        {
            var req = BuildRequest(HttpMethod.Get, url, referer);
            tweak?.Invoke(req);
            return _client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        }

        public Task<HttpResponseMessage> PostFormAsync(string url, string referer,
            IEnumerable<KeyValuePair<string, string>> fields, CancellationToken ct)
        {
            var req = BuildRequest(HttpMethod.Post, url, referer);
            req.Content = new FormUrlEncodedContent(fields);
            return _client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        }

        public void Dispose() => _client.Dispose();
    }
}
