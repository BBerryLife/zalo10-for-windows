using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace zalo10_for_windows.Core
{
    public enum QrLoginResult { Success, Expired, Failed, Cancelled }

    /// <summary>
    /// QR login, steps 1-7. Port of core/auth/ZaloService_Auth_QRSteps.cpp (Zalo10).
    /// Steps 8+ (encrypted getLoginInfo -> secretKey, uid, service URLs) come next.
    /// </summary>
    public sealed class ZaloQrLogin
    {
        private const string IdPcReferer = "https://id.zalo.me/account?continue=https%3A%2F%2Fzalo.me%2Fpc";
        private const string IdChatReferer = "https://id.zalo.me/account?continue=https%3A%2F%2Fchat.zalo.me%2F";
        private static readonly TimeSpan QrLifetime = TimeSpan.FromSeconds(100);

        private readonly ZaloHttp _http;
        private string _loginVersion = "5.6.1";
        private string _qrCode = "";

        /// <summary>PNG bytes of the QR image, plus the raw QR code string.</summary>
        public event Action<byte[], string>? QrReady;
        /// <summary>User scanned the QR; carries the account display name.</summary>
        public event Action<string>? Scanned;
        public event Action<string>? Failed;
        /// <summary>QR confirmed and session cookies obtained (before getLoginInfo).</summary>
        public event Action? SessionCookiesReady;
        /// <summary>QR was not scanned in time; call RunAsync again for a fresh code.</summary>
        public event Action? QrExpired;

        public string DisplayName { get; private set; } = "";

        public ZaloQrLogin(ZaloHttp http) => _http = http;

        /// <summary>
        /// Runs one full QR attempt. Returns Expired when the QR timed out unscanned,
        /// so the caller can simply call it again to get a fresh code.
        /// </summary>
        public async Task<QrLoginResult> RunAsync(CancellationToken ct)
        {
            try
            {
                Log("Bat dau phien dang nhap QR moi");
                _http.ResetCookies();

                await Step1LoadLoginPage(ct);
                await Step2GetLoginInfo(ct);
                await Step3VerifyClient(ct);
                if (!await Step4GenerateQr(ct)) return QrLoginResult.Failed;

                using var expire = CancellationTokenSource.CreateLinkedTokenSource(ct);
                expire.CancelAfter(QrLifetime);

                try
                {
                    if (!await Step5WaitingScan(expire.Token)) return QrLoginResult.Failed;
                    if (!await Step6WaitingConfirm(expire.Token)) return QrLoginResult.Failed;
                }
                catch (OperationCanceledException) when (expire.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    Log("QR het han (khong ai quet)");
                    QrExpired?.Invoke();
                    return QrLoginResult.Expired;
                }

                await Step7CheckSession(ct);
                Log($"Hoan tat buoc 7, co {_http.CookieCount} cookie: {_http.CookieSummary()}");
                SessionCookiesReady?.Invoke();
                return QrLoginResult.Success;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Log("Da huy");
                return QrLoginResult.Cancelled;
            }
            catch (OperationCanceledException)
            {
                Log("Qua thoi gian cho (timeout)");
                Failed?.Invoke("Hết thời gian chờ phản hồi từ Zalo");
                return QrLoginResult.Failed;
            }
            catch (HttpRequestException ex)
            {
                Log("Loi mang: " + ex);
                Failed?.Invoke("Lỗi mạng: " + ex.Message);
                return QrLoginResult.Failed;
            }
            catch (Exception ex)
            {
                Log("Loi: " + ex);
                Failed?.Invoke(ex.Message);
                return QrLoginResult.Failed;
            }
        }

        /// <summary>Shows in Visual Studio: View > Output > "Show output from: Debug".</summary>
        private static void Log(string msg) => Debug.WriteLine("[Zalo10][QR] " + msg);

        // ---- Step 1: load login page, sniff the web client version -------------------------------
        private async Task Step1LoadLoginPage(CancellationToken ct)
        {
            using var resp = await _http.GetAsync(
                "https://id.zalo.me/account?continue=https%3A%2F%2Fchat.zalo.me%2F",
                "https://chat.zalo.me/", ct);
            resp.EnsureSuccessStatusCode();
            var html = await resp.Content.ReadAsStringAsync(ct);

            string[] patterns =
            {
                @"stc-zlogin\.zdn\.vn/main-([\d.]+)\.js",
                @"main[.-]([\w.-]+)\.js",
                @"chunkMain[.-]([\w.-]+)\.js",
            };
            foreach (var p in patterns)
            {
                var m = Regex.Match(html, p);
                if (m.Success) { _loginVersion = m.Groups[1].Value; Log("Step1 OK, login version = " + _loginVersion); return; }
            }
            _loginVersion = "5.6.1"; // same fallback as Zalo10
            Log("Khong tim thay version, dung mac dinh " + _loginVersion);
        }

        // ---- Step 2/3: logininfo + verify-client (responses only matter for their cookies) -------
        private async Task Step2GetLoginInfo(CancellationToken ct)
        {
            using var r = await _http.PostFormAsync("https://id.zalo.me/account/logininfo", IdPcReferer,
                Form(("continue", "https://zalo.me/pc"), ("v", _loginVersion)), ct);
        }

        private async Task Step3VerifyClient(CancellationToken ct)
        {
            using var r = await _http.PostFormAsync("https://id.zalo.me/account/verify-client", IdPcReferer,
                Form(("type", "device"), ("continue", "https://zalo.me/pc"), ("v", _loginVersion)), ct);
        }

        // ---- Step 4: generate QR -----------------------------------------------------------------
        private async Task<bool> Step4GenerateQr(CancellationToken ct)
        {
            using var resp = await _http.PostFormAsync("https://id.zalo.me/account/authen/qr/generate", IdPcReferer,
                Form(("continue", "https://zalo.me/pc"), ("v", _loginVersion)), ct);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("data", out var data)) { Failed?.Invoke("Không nhận được mã QR từ Zalo."); return false; }

            _qrCode = Str(data, "code");
            if (_qrCode.Length == 0) { Failed?.Invoke("Không nhận được mã QR từ Zalo."); return false; }

            var image = Str(data, "image");
            int comma = image.IndexOf(',');
            if (comma >= 0) image = image[(comma + 1)..];

            byte[] png;
            if (image.Length > 0)
            {
                png = Convert.FromBase64String(image);
            }
            else
            {
                // Fallback used by Zalo10: render the code through quickchart.io
                var url = "https://quickchart.io/qr?text=" + Uri.EscapeDataString(_qrCode) + "&size=300&margin=2";
                using var qr = await _http.GetAsync(url, "", ct);
                png = await qr.Content.ReadAsByteArrayAsync(ct);
            }

            Log("Step4 OK, da tao QR (" + png.Length + " bytes)");
            QrReady?.Invoke(png, _qrCode);
            return true;
        }

        // ---- Step 5: long-poll until the QR is scanned (error_code 8 = keep waiting) -------------
        private async Task<bool> Step5WaitingScan(CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                using var resp = await _http.PostFormAsync("https://id.zalo.me/account/authen/qr/waiting-scan", IdChatReferer,
                    Form(("code", _qrCode), ("continue", "https://chat.zalo.me/"), ("v", _loginVersion)), ct);

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                int code = ErrorCode(doc.RootElement);

                if (code == 8) continue;
                if (code == 0)
                {
                    if (doc.RootElement.TryGetProperty("data", out var d)) DisplayName = Str(d, "display_name");
                    Log("Step5 OK, da quet boi: " + DisplayName);
                    Scanned?.Invoke(DisplayName);
                    return true;
                }
                Log("Step5 error_code = " + code);
                Failed?.Invoke($"Lỗi quét QR: {code}");
                return false;
            }
        }

        // ---- Step 6: long-poll until the user taps "confirm" on the phone ------------------------
        private async Task<bool> Step6WaitingConfirm(CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                using var resp = await _http.PostFormAsync("https://id.zalo.me/account/authen/qr/waiting-confirm", IdChatReferer,
                    Form(("code", _qrCode), ("gToken", ""), ("gAction", "CONFIRM_QR"),
                         ("continue", "https://chat.zalo.me/"), ("v", _loginVersion)), ct);

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                int code = ErrorCode(doc.RootElement);

                if (code == 8) continue;
                if (code == 0) { Log("Step6 OK, da xac nhan"); return true; }
                Log("Step6 error_code = " + code);
                Failed?.Invoke(code == -13 ? "Bạn đã từ chối xác nhận trên điện thoại" : $"Lỗi xác nhận: {code}");
                return false;
            }
        }

        // ---- Step 7: checksession, following the redirect chain by hand --------------------------
        private async Task Step7CheckSession(CancellationToken ct)
        {
            string url = "https://id.zalo.me/account/checksession?continue=https%3A%2F%2Fchat.zalo.me%2Findex.html";
            string referer = IdChatReferer;
            bool first = true;

            for (int hop = 0; hop < 10; hop++)
            {
                bool crossSite = !first && !url.Contains("id.zalo.me") && !url.Contains("chat.zalo.me");
                using var resp = await _http.GetAsync(url, referer, ct, req =>
                {
                    if (first || crossSite)
                    {
                        req.Headers.Remove("sec-fetch-dest"); req.Headers.TryAddWithoutValidation("sec-fetch-dest", "document");
                        req.Headers.Remove("sec-fetch-mode"); req.Headers.TryAddWithoutValidation("sec-fetch-mode", "navigate");
                        req.Headers.Remove("sec-fetch-site"); req.Headers.TryAddWithoutValidation("sec-fetch-site", crossSite ? "cross-site" : "same-origin");
                        req.Headers.TryAddWithoutValidation("upgrade-insecure-requests", "1");
                    }
                });
                first = false;

                int status = (int)resp.StatusCode;
                Log($"Step7 hop {hop}: {status} {Safe(url)} -> {(resp.Headers.Location is null ? "(het chuoi)" : Safe(resp.Headers.Location.ToString()))}");
                Log($"Step7 hop {hop}: cookie hien co: {_http.CookieSummary()}");
                if (status < 300 || status >= 400 || resp.Headers.Location is null) return; // chain finished

                var next = resp.Headers.Location.IsAbsoluteUri
                    ? resp.Headers.Location
                    : new Uri(new Uri(url), resp.Headers.Location);
                var nextStr = next.ToString();

                referer = nextStr.Contains("jr.zaloapp.com") ? "https://id.zalo.me/"
                        : nextStr.Contains("jr.chat.zalo.me") ? "https://jr.zaloapp.com/"
                        : "https://id.zalo.me/";
                url = nextStr;
            }
        }

        /// <summary>Host + path only: the query string can carry tokens, so it never goes in a log.</summary>
        private static string Safe(string u)
        {
            try { return new Uri(u, UriKind.RelativeOrAbsolute).IsAbsoluteUri ? new Uri(u).GetLeftPart(UriPartial.Path) : u.Split('?')[0]; }
            catch { return "?"; }
        }

        // ---- helpers ------------------------------------------------------------------------------
        private static List<KeyValuePair<string, string>> Form(params (string k, string v)[] f)
        {
            var l = new List<KeyValuePair<string, string>>(f.Length);
            foreach (var (k, v) in f) l.Add(new(k, v));
            return l;
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        private static int ErrorCode(JsonElement root)
        {
            if (!root.TryGetProperty("error_code", out var v)) return 0;
            return v.ValueKind switch
            {
                JsonValueKind.Number => v.GetInt32(),
                JsonValueKind.String when int.TryParse(v.GetString(), out var n) => n,
                _ => 0
            };
        }
    }
}
