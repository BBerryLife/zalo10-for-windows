using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace zalo10_for_windows.Core
{
    public enum FinishStatus
    {
        Success,
        /// <summary>Zalo says the cookies are no longer valid (error 102): a new QR login is required.</summary>
        SessionInvalid,
        /// <summary>Network trouble or server hiccup: keep the saved session and try again.</summary>
        Transient,
        Failed,
        Cancelled
    }

    public sealed record FinishResult(FinishStatus Status, string? Message = null);

    /// <summary>
    /// Steps 8 and 9 of the login (also used to renew the session on startup, exactly like
    /// Zalo10's refreshSessionKey). Port of core/auth/ZaloService_Auth_QRFinish.cpp.
    /// Turns the web session cookies into a real API session: uid, secretKey, service URLs.
    /// </summary>
    public static class ZaloLoginFinisher
    {
        private const string Referer = "https://chat.zalo.me/";
        private const string LoginInfoUrl = "https://wpa.chat.zalo.me/api/login/getLoginInfo";
        private const string ServerInfoUrl = "https://wpa.chat.zalo.me/api/login/getServerInfo";
        private const string SessionInvalidCode = "102";

        private static readonly string[] ServiceNames =
        {
            "chat", "group", "profile", "group_poll", "group_board",
            "friend", "file", "quick_message", "reaction", "conversation"
        };

        public static async Task<FinishResult> FinishAsync(ZaloHttp http, ZaloSession session, CancellationToken ct)
        {
            try
            {
                var r = await GetLoginInfoAsync(http, session, ct);
                if (r.Status != FinishStatus.Success) return r;

                await GetServerInfoAsync(http, session, ct);
                Log($"Dang nhap hoan tat. uid=...{Tail(session.Uid)}, {session.ServiceUrls.Count} service, {session.WebSocketUrls.Count} websocket");
                return new FinishResult(FinishStatus.Success);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new FinishResult(FinishStatus.Cancelled); }
            catch (OperationCanceledException) { return new FinishResult(FinishStatus.Transient, "Hết thời gian chờ phản hồi từ Zalo"); }
            catch (HttpRequestException ex) { Log("Loi mang: " + ex.Message); return new FinishResult(FinishStatus.Transient, "Lỗi mạng: " + ex.Message); }
            catch (Exception ex) { Log("Loi: " + ex); return new FinishResult(FinishStatus.Failed, ex.Message); }
        }

        // ---- Step 8 -------------------------------------------------------------------------------
        private static async Task<FinishResult> GetLoginInfoAsync(ZaloHttp http, ZaloSession s, CancellationToken ct)
        {
            var data = new Dictionary<string, string>
            {
                ["computer_name"] = "Web",
                ["imei"] = s.Imei,
                ["language"] = s.Language,
                ["ts"] = ZaloCrypto.NowMs().ToString(),
            };

            var ep = ZaloCrypto.BuildEncryptedParams(s.Imei, data);

            var forSign = new Dictionary<string, string>
            {
                ["zcid"] = ep.Zcid,
                ["zcid_ext"] = ep.ZcidExt,
                ["enc_ver"] = ep.EncVer,
                ["params"] = ep.EncryptedData,
                ["type"] = ZaloCrypto.ApiType.ToString(),
                ["client_version"] = ZaloCrypto.ApiVersion.ToString(),
                ["nretry"] = "0",
            };

            var query = new Dictionary<string, string>(forSign)
            {
                ["signkey"] = ZaloCrypto.BuildSignKey("getlogininfo", forSign),
                ["imei"] = s.Imei,
            };

            using var resp = await http.GetAsync(ZaloCrypto.BuildUrl(LoginInfoUrl, query), Referer, ct, req =>
            {
                req.Headers.TryAddWithoutValidation("zpw_ver", ZaloCrypto.ApiVersion.ToString());
                req.Headers.TryAddWithoutValidation("zpw_type", ZaloCrypto.ApiType.ToString());
            });
            int status = (int)resp.StatusCode;
            Log($"Step8 HTTP {status}, cookie: {http.CookieSummary()}");
            if (!resp.IsSuccessStatusCode)
                return new FinishResult(status >= 500 || status == 429 ? FinishStatus.Transient : FinishStatus.Failed,
                                        $"getLoginInfo lỗi HTTP {status}");

            var raw = await resp.Content.ReadAsStringAsync(ct);

            JsonDocument outer;
            try { outer = JsonDocument.Parse(raw); }
            catch (JsonException) { Log("Step8: phan hoi khong phai JSON"); return Fail("Phản hồi đăng nhập không hợp lệ"); }

            using (outer)
            {
                var root = outer.RootElement;
                string outerCode = Str(root, "error_code");
                string errMsg = Str(root, "error_message");
                Log($"Step8 error_code={outerCode} message=\"{errMsg}\"");

                string enc = Str(root, "data");
                if (enc.Length == 0)
                {
                    if (outerCode == SessionInvalidCode) return Invalid();
                    return Fail(outerCode.Length > 0 && outerCode != "0"
                        ? $"Zalo từ chối đăng nhập (mã {outerCode}: {errMsg})"
                        : "Phản hồi đăng nhập trống");
                }

                string? decrypted = ZaloCrypto.AesDecryptBase64Key256(ep.EncryptKey, enc);
                if (string.IsNullOrEmpty(decrypted))
                {
                    Log($"Step8 GIAI MA THAT BAI: do dai data={enc.Length}");
                    return Fail("Không giải mã được phản hồi đăng nhập");
                }

                JsonDocument inner;
                try { inner = JsonDocument.Parse(decrypted); }
                catch (JsonException) { Log("Step8: du lieu giai ma khong phai JSON"); return Fail("Dữ liệu đăng nhập không hợp lệ"); }

                using (inner)
                {
                    var info = inner.RootElement;
                    if (info.ValueKind != JsonValueKind.Object) return Fail("Dữ liệu đăng nhập không hợp lệ");

                    string innerCode = Str(info, "error_code");
                    string innerMsg = Str(info, "error_message");
                    Log($"Step8 phong bi trong: error_code={innerCode}, error_message=\"{innerMsg}\"");
                    if (innerCode.Length > 0 && innerCode != "0")
                        return innerCode == SessionInvalidCode ? Invalid() : Fail($"Zalo trả lỗi {innerCode}: {innerMsg}");

                    if (info.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object) info = d;

                    s.SecretKey = Str(info, "zpw_enk");
                    s.Uid = Str(info, "uid");
                    s.DisplayName = Str(info, "display_name");
                    if (!s.IsLoggedIn)
                    {
                        Log($"Step8 THIEU: zpw_enk={(s.SecretKey.Length > 0 ? "co" : "khong")}, uid={(s.Uid.Length > 0 ? "co" : "khong")}");
                        return Fail("Thiếu khóa phiên hoặc uid trong phản hồi");
                    }

                    s.ServiceUrls.Clear();
                    if (info.TryGetProperty("zpw_service_map_v3", out var map) && map.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var name in ServiceNames)
                            if (map.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
                                s.ServiceUrls[name] = Str(arr[0]);
                    }

                    s.WebSocketUrls.Clear();
                    if (info.TryGetProperty("zpw_ws", out var ws) && ws.ValueKind == JsonValueKind.Array)
                        foreach (var u in ws.EnumerateArray().Take(10))
                        {
                            var url = Str(u);
                            if (url.Length > 0) s.WebSocketUrls.Add(url);
                        }
                }
            }
            return new FinishResult(FinishStatus.Success);
        }

        // ---- Step 9 -------------------------------------------------------------------------------
        private static async Task GetServerInfoAsync(ZaloHttp http, ZaloSession s, CancellationToken ct)
        {
            var p = new Dictionary<string, string>
            {
                ["imei"] = s.Imei,
                ["type"] = ZaloCrypto.ApiType.ToString(),
                ["client_version"] = ZaloCrypto.ApiVersion.ToString(),
                ["computer_name"] = "Web",
            };
            p["signkey"] = ZaloCrypto.BuildSignKey("getserverinfo", new Dictionary<string, string>(p));

            // Zalo10 ignores the body here; only the cookies it sets matter.
            using var resp = await http.GetAsync(ZaloCrypto.BuildUrl(ServerInfoUrl, p), Referer, ct);
            Log($"Step9 HTTP {(int)resp.StatusCode}");
        }

        // ---- helpers ------------------------------------------------------------------------------
        private static FinishResult Fail(string msg) => new(FinishStatus.Failed, msg);
        private static FinishResult Invalid() => new(FinishStatus.SessionInvalid, "Phiên đăng nhập đã hết hạn");

        /// <summary>Reads a JSON value as text whether it is a string or a number (uid can be either).</summary>
        private static string Str(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.String => e.GetString() ?? "",
            JsonValueKind.Number => e.GetRawText(),
            _ => ""
        };

        private static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? Str(v) : "";

        private static string Tail(string s) => s.Length <= 4 ? s : s[^4..];

        // Never log secretKey, cookie values or the encrypted payloads.
        private static void Log(string msg) => Debug.WriteLine("[Zalo10][Login] " + msg);
    }
}
