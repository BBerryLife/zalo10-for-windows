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
    public enum ApiStatus { Ok, SessionExpired, RateLimited, Failed, Cancelled }

    /// <summary>Outcome of an API call. Dispose it to release the parsed JSON.</summary>
    public sealed class ApiReply : IDisposable
    {
        public ApiStatus Status { get; init; }
        public string? Message { get; init; }
        /// <summary>The decrypted payload with the {error_code, data} envelope already removed.</summary>
        public JsonElement Data { get; init; }
        private readonly JsonDocument? _doc;

        public ApiReply(ApiStatus status, string? message = null, JsonDocument? doc = null, JsonElement data = default)
        {
            Status = status; Message = message; _doc = doc; Data = data;
        }

        public void Dispose() => _doc?.Dispose();
    }

    public sealed record ApiResult<T>(ApiStatus Status, T? Value, string? Message = null);

    /// <summary>One chat message reduced to what a basic chat view shows. Text is already human readable.</summary>
    public sealed record ChatMessage(string MsgId, string SenderId, string SenderName, long TimeMs, bool IsMine, string Text);

    /// <summary>A friend or a group, as needed by the conversation list.</summary>
    public sealed record ContactInfo(string Id, string Name, string AvatarUrl, bool IsGroup);

    /// <summary>
    /// Calls made after login. Every request carries its parameters AES-encrypted with the
    /// session secretKey, and every reply comes back encrypted the same way.
    /// Port of Zalo10's fetchFriends / fetchConversations / fetchGroupDetails.
    /// </summary>
    public sealed class ZaloApi
    {
        private const string Referer = "https://chat.zalo.me/";
        private readonly ZaloHttp _http;
        private readonly ZaloSession _s;

        public ZaloApi(ZaloHttp http, ZaloSession session)
        {
            _http = http;
            _s = session;
        }

        // ---- friends -------------------------------------------------------------------------------
        public async Task<ApiResult<List<ContactInfo>>> GetFriendsAsync(CancellationToken ct)
        {
            if (!_s.ServiceUrls.TryGetValue("profile", out var baseUrl))
                return new(ApiStatus.Failed, null, "Thiếu địa chỉ dịch vụ danh bạ");

            try
            {
                var inner = new Dictionary<string, object>
                {
                    ["incInvalid"] = 1,
                    ["page"] = 1,
                    ["count"] = 20000,
                    ["avatar_size"] = 120,
                    ["actiontime"] = 0,
                    ["imei"] = _s.Imei,
                };
                string enc = ZaloCrypto.AesEncryptBase64(_s.SecretKey, ZaloCrypto.ToJsonTyped(inner));

                var q = Versioned();
                q["params"] = enc;
                using var resp = await _http.GetAsync(ZaloCrypto.BuildUrl(baseUrl + "/api/social/friend/getfriends", q), Referer, ct);
                using var reply = await ReadAsync("getfriends", resp, ct);
                if (reply.Status != ApiStatus.Ok) return new(reply.Status, null, reply.Message);

                var result = new List<ContactInfo>();
                foreach (var f in FindArray(reply.Data, "data", "friends"))
                {
                    string id = FirstNonEmpty(f, "userId", "uid");
                    string name = FirstNonEmpty(f, "zaloName", "displayName", "username");
                    if (id.Length == 0 || name.Length == 0) continue;
                    result.Add(new ContactInfo(id, name, Str(f, "avatar"), false));
                }
                Log($"getfriends: {result.Count} ban be");
                return new(ApiStatus.Ok, result);
            }
            catch (Exception ex) { return Fail<List<ContactInfo>>("getfriends", ex, ct); }
        }

        // ---- groups (two steps: list of ids, then details) -----------------------------------------
        public async Task<ApiResult<List<ContactInfo>>> GetGroupsAsync(CancellationToken ct)
        {
            if (!_s.ServiceUrls.TryGetValue("group", out var baseUrl))
                return new(ApiStatus.Failed, null, "Thiếu địa chỉ dịch vụ nhóm");

            try
            {
                // Step 1: ids of every group the account belongs to.
                List<string> ids;
                using (var resp = await _http.GetAsync(ZaloCrypto.BuildUrl(baseUrl + "/api/group/getlg/v4", Versioned()), Referer, ct))
                using (var reply = await ReadAsync("getlg", resp, ct))
                {
                    if (reply.Status != ApiStatus.Ok) return new(reply.Status, null, reply.Message);
                    ids = reply.Data.ValueKind == JsonValueKind.Object &&
                          reply.Data.TryGetProperty("gridVerMap", out var gv) && gv.ValueKind == JsonValueKind.Object
                        ? gv.EnumerateObject().Select(p => p.Name).ToList()
                        : new List<string>();
                }
                Log($"getlg: {ids.Count} nhom");
                if (ids.Count == 0) return new(ApiStatus.Ok, new List<ContactInfo>());

                // Step 2: details (name, avatar) for those ids.
                var verMap = ids.ToDictionary(id => id, _ => (object)0);
                var inner = new Dictionary<string, object> { ["gridVerMap"] = ZaloCrypto.ToJsonTyped(verMap) };
                string enc = ZaloCrypto.AesEncryptBase64(_s.SecretKey, ZaloCrypto.ToJsonTyped(inner));

                string url = ZaloCrypto.BuildUrl(baseUrl + "/api/group/getmg-v2", Versioned());
                using var resp2 = await _http.PostFormAsync(url, Referer,
                    new[] { new KeyValuePair<string, string>("params", enc) }, ct);
                using var reply2 = await ReadAsync("getmg", resp2, ct);
                if (reply2.Status != ApiStatus.Ok) return new(reply2.Status, null, reply2.Message);

                var groups = new List<JsonElement>();
                if (reply2.Data.ValueKind == JsonValueKind.Object)
                {
                    if (reply2.Data.TryGetProperty("gridInfoMap", out var map) && map.ValueKind == JsonValueKind.Object)
                        groups.AddRange(map.EnumerateObject().Select(p => p.Value));
                    else if (reply2.Data.TryGetProperty("gridInfos", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        groups.AddRange(arr.EnumerateArray());
                }

                var result = new List<ContactInfo>();
                foreach (var g in groups)
                {
                    string id = Str(g, "groupId");
                    if (id.Length == 0) continue;
                    string name = Str(g, "name");
                    if (name.Length == 0) name = "Nhóm " + (id.Length > 6 ? id[^6..] : id); // same fallback as Zalo10
                    result.Add(new ContactInfo(id, name, Str(g, "avt"), true));
                }
                Log($"getmg: {result.Count} chi tiet nhom");
                return new(ApiStatus.Ok, result);
            }
            catch (Exception ex) { return Fail<List<ContactInfo>>("groups", ex, ct); }
        }

        // ---- group history -------------------------------------------------------------------------
        /// <summary>Last 50 messages of a group, oldest first. (1-1 history only exists on the WebSocket.)</summary>
        public async Task<ApiResult<List<ChatMessage>>> GetGroupHistoryAsync(string groupId, CancellationToken ct)
        {
            if (!_s.ServiceUrls.TryGetValue("group", out var baseUrl))
                return new(ApiStatus.Failed, null, "Thiếu địa chỉ dịch vụ nhóm");

            try
            {
                var inner = new Dictionary<string, object> { ["grid"] = groupId, ["count"] = 50 };
                var q = Versioned();
                q["params"] = ZaloCrypto.AesEncryptBase64(_s.SecretKey, ZaloCrypto.ToJsonTyped(inner));

                using var resp = await _http.GetAsync(ZaloCrypto.BuildUrl(baseUrl + "/api/group/history", q), Referer, ct);
                using var reply = await ReadAsync("history", resp, ct);
                if (reply.Status != ApiStatus.Ok) return new(reply.Status, null, reply.Message);

                var raw = FindArray(reply.Data, "groupMsgs", "data", "msgList").ToList();

                // Pass 1: which messages were recalled or deleted by me.
                var recalled = new HashSet<string>();
                var deleted = new HashSet<string>();
                foreach (var m in raw)
                {
                    string type = Str(m, "msgType");
                    if (type.Equals("chat.undo", StringComparison.OrdinalIgnoreCase))
                    {
                        using var c = ContentObject(m);
                        string id = FirstNonEmpty(c.RootElement, "globalMsgId", "cliMsgId");
                        if (id.Length > 0) recalled.Add(id);
                    }
                    else if (type.Equals("chat.delete", StringComparison.OrdinalIgnoreCase) &&
                             Str(m, "uidFrom") == _s.Uid &&
                             m.TryGetProperty("content", out var dc))
                    {
                        // content is a list like [{"globalDelMsgId":"..","uidFrom":".."}]
                        JsonDocument? doc = null;
                        try
                        {
                            var list = dc;
                            if (dc.ValueKind == JsonValueKind.String) { doc = JsonDocument.Parse(dc.GetString() ?? "[]"); list = doc.RootElement; }
                            if (list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
                            {
                                string id = FirstNonEmpty(list[0], "globalDelMsgId", "clientDelMsgId");
                                if (id.Length > 0) deleted.Add(id);
                            }
                        }
                        catch (JsonException) { }
                        finally { doc?.Dispose(); }
                    }
                }

                // Pass 2: the messages to show.
                var result = new List<ChatMessage>();
                foreach (var m in raw)
                {
                    string type = Str(m, "msgType");
                    if (type is "group.poll" or "chat.undo" or "chat.delete" or "chat.reaction") continue;

                    string msgId = Str(m, "msgId");
                    string cliId = Str(m, "cliMsgId");
                    if (deleted.Contains(msgId) || (cliId.Length > 0 && deleted.Contains(cliId))) continue;

                    string text = recalled.Contains(msgId) || (cliId.Length > 0 && recalled.Contains(cliId))
                        ? "Tin nhắn đã được thu hồi"
                        : Describe(type, m);
                    if (text.Length == 0) continue;

                    string from = Str(m, "uidFrom");
                    long.TryParse(Str(m, "ts"), out long ts);
                    result.Add(new ChatMessage(msgId, from, Str(m, "dName"), ts, from == _s.Uid, text));
                }

                result.Sort((a, b) => a.TimeMs != b.TimeMs
                    ? a.TimeMs.CompareTo(b.TimeMs)
                    : string.CompareOrdinal(a.MsgId, b.MsgId));
                Log($"history: {raw.Count} muc, hien {result.Count} tin");
                return new(ApiStatus.Ok, result);
            }
            catch (Exception ex) { return Fail<List<ChatMessage>>("history", ex, ct); }
        }

        // ---- send a text message -------------------------------------------------------------------
        public async Task<ApiResult<ChatMessage>> SendTextAsync(string threadId, bool isGroup, string text, CancellationToken ct)
        {
            if (!_s.ServiceUrls.TryGetValue(isGroup ? "group" : "chat", out var baseUrl))
                return new(ApiStatus.Failed, null, "Thiếu địa chỉ dịch vụ nhắn tin");

            try
            {
                long now = ZaloCrypto.NowMs();
                var inner = new Dictionary<string, object>
                {
                    ["message"] = text,
                    ["clientId"] = now.ToString(),
                    ["ttl"] = 0,
                };
                if (isGroup) { inner["visibility"] = 0; inner["grid"] = threadId; }
                else { inner["toid"] = threadId; inner["imei"] = _s.Imei; }

                string enc = ZaloCrypto.AesEncryptBase64(_s.SecretKey, ZaloCrypto.ToJsonTyped(inner));
                string url = ZaloCrypto.BuildUrl(baseUrl + (isGroup ? "/api/group/sendmsg" : "/api/message/sms"), Versioned());

                using var resp = await _http.PostFormAsync(url, Referer,
                    new[] { new KeyValuePair<string, string>("params", enc) }, ct);
                using var reply = await ReadAsync("send", resp, ct);
                if (reply.Status != ApiStatus.Ok) return new(reply.Status, null, reply.Message);

                string msgId = Str(reply.Data, "msgId");
                if (msgId.Length == 0 || msgId == "0") msgId = now.ToString();
                Log("send: ok");
                return new(ApiStatus.Ok, new ChatMessage(msgId, _s.Uid, _s.DisplayName, now, true, text));
            }
            catch (Exception ex) { return Fail<ChatMessage>("send", ex, ct); }
        }

        // ---- message content -> readable text ------------------------------------------------------
        internal static JsonDocument ContentObject(JsonElement m)
        {
            // content may be an object, or a JSON object serialised into a string.
            if (m.TryGetProperty("content", out var c))
            {
                if (c.ValueKind == JsonValueKind.Object) return JsonDocument.Parse(c.GetRawText());
                if (c.ValueKind == JsonValueKind.String)
                {
                    try
                    {
                        string s = c.GetString() ?? "";
                        if (s.TrimStart().StartsWith("{")) return JsonDocument.Parse(s);
                    }
                    catch (JsonException) { }
                }
            }
            return JsonDocument.Parse("{}");
        }

        /// <summary>Plain text stays text; photos, stickers, files, calls and links become a short label.</summary>
        internal static string Describe(string type, JsonElement m)
        {
            if (!m.TryGetProperty("content", out var c)) return "";

            bool plainType = type.Length == 0 || type.Equals("webchat", StringComparison.OrdinalIgnoreCase);
            if (c.ValueKind == JsonValueKind.String)
            {
                string s = c.GetString() ?? "";
                if (plainType || !s.TrimStart().StartsWith("{")) return s;
            }
            else if (c.ValueKind != JsonValueKind.Object) return "";

            using var doc = ContentObject(m);
            var o = doc.RootElement;
            string t = type.ToLowerInvariant();

            if (t.Contains("photo")) { string cap = Str(o, "title"); return cap.Length > 0 ? "[Hình ảnh] " + cap : "[Hình ảnh]"; }
            if (t.Contains("sticker")) return "[Nhãn dán]";
            if (t.Contains("video")) return "[Video]";
            if (t.Contains("voice")) return "[Tin nhắn thoại]";
            if (t.Contains("file")) { string n = Str(o, "title"); return n.Length > 0 ? "[Tệp] " + n : "[Tệp]"; }
            if (t.Contains("recommended") || t.Contains("link"))
            {
                string action = Str(o, "action");
                if (action.Contains("call", StringComparison.OrdinalIgnoreCase))
                    return action.Contains("misscall", StringComparison.OrdinalIgnoreCase) ? "[Cuộc gọi nhỡ]" : "[Cuộc gọi]";
                string title = Str(o, "title"), href = Str(o, "href");
                return title.Length > 0 ? "[Liên kết] " + title : href.Length > 0 ? href : "[Liên kết]";
            }
            return "[Tin nhắn chưa được hỗ trợ]";
        }

        // ---- shared plumbing -----------------------------------------------------------------------
        private static Dictionary<string, string> Versioned() => new()
        {
            ["zpw_ver"] = ZaloCrypto.ApiVersion.ToString(),
            ["zpw_type"] = ZaloCrypto.ApiType.ToString(),
        };

        /// <summary>
        /// Opens the {error_code, data} envelope and decrypts data with the session secretKey.
        /// error 600 means the session cookies are dead (log in again); an HTML body means HTTP 429.
        /// </summary>
        private async Task<ApiReply> ReadAsync(string name, HttpResponseMessage resp, CancellationToken ct)
        {
            string raw = await resp.Content.ReadAsStringAsync(ct);

            if ((int)resp.StatusCode == 429 || raw.Contains("429 Too Many Requests"))
            {
                Log($"{name}: bi gioi han toc do (429)");
                return new ApiReply(ApiStatus.RateLimited, "Zalo đang giới hạn tốc độ, hãy thử lại sau");
            }
            if (raw.TrimStart().StartsWith("<", StringComparison.Ordinal))
            {
                // An HTML page instead of JSON: the endpoint is gone or blocked (not the same as being rate limited).
                Log($"{name}: nhan trang HTML thay vi JSON (HTTP {(int)resp.StatusCode})");
                return new ApiReply(ApiStatus.Failed, $"Máy chủ trả về trang web thay vì dữ liệu (HTTP {(int)resp.StatusCode})");
            }

            JsonDocument outer;
            try { outer = JsonDocument.Parse(raw); }
            catch (JsonException) { Log($"{name}: phan hoi khong phai JSON (HTTP {(int)resp.StatusCode})"); return new ApiReply(ApiStatus.Failed, "Phản hồi không hợp lệ"); }

            var root = outer.RootElement;
            int ec = ErrorCode(root);
            if (ec != 0)
            {
                string msg = Str(root, "error_message");
                outer.Dispose();
                Log($"{name}: error_code={ec} \"{msg}\"");
                return ec == 600
                    ? new ApiReply(ApiStatus.SessionExpired, "Phiên đăng nhập đã hết hạn")
                    : new ApiReply(ApiStatus.Failed, $"Zalo trả lỗi {ec}: {msg}");
            }

            string enc = Str(root, "data");
            outer.Dispose();
            if (enc.Length == 0) return new ApiReply(ApiStatus.Ok);

            string? plain = ZaloCrypto.AesDecryptBase64(_s.SecretKey, enc);
            if (string.IsNullOrEmpty(plain))
            {
                Log($"{name}: giai ma that bai (data dai {enc.Length})");
                return new ApiReply(ApiStatus.Failed, "Không giải mã được phản hồi");
            }

            JsonDocument inner;
            try { inner = JsonDocument.Parse(plain); }
            catch (JsonException) { Log($"{name}: du lieu giai ma khong phai JSON"); return new ApiReply(ApiStatus.Failed, "Dữ liệu không hợp lệ"); }

            var el = inner.RootElement;
            if (el.ValueKind == JsonValueKind.Object)
            {
                int iec = ErrorCode(el);
                if (iec != 0)
                {
                    string msg = Str(el, "error_message");
                    inner.Dispose();
                    Log($"{name}: loi ben trong error_code={iec} \"{msg}\"");
                    return iec == 600
                        ? new ApiReply(ApiStatus.SessionExpired, "Phiên đăng nhập đã hết hạn")
                        : new ApiReply(ApiStatus.Failed, $"Zalo trả lỗi {iec}: {msg}");
                }
                // Unwrap {"error_code":0,"data":{...}} / {"data":[...]}
                if (el.TryGetProperty("data", out var d) && (d.ValueKind == JsonValueKind.Object || d.ValueKind == JsonValueKind.Array))
                    el = d;
            }
            return new ApiReply(ApiStatus.Ok, null, inner, el);
        }

        private ApiResult<T> Fail<T>(string name, Exception ex, CancellationToken ct)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested) return new(ApiStatus.Cancelled, default);
            Log($"{name}: loi {ex.GetType().Name}: {ex.Message}");
            return new(ApiStatus.Failed, default, ex is HttpRequestException or OperationCanceledException
                ? "Lỗi mạng, không kết nối được tới Zalo" : ex.Message);
        }

        /// <summary>Finds the list of items in a reply shaped as [..], {"data":[..]} or {"friends":[..]}.</summary>
        private static IEnumerable<JsonElement> FindArray(JsonElement e, params string[] names)
        {
            if (e.ValueKind == JsonValueKind.Array) return e.EnumerateArray();
            if (e.ValueKind == JsonValueKind.Object)
                foreach (var n in names)
                    if (e.TryGetProperty(n, out var a) && a.ValueKind == JsonValueKind.Array) return a.EnumerateArray();
            return Enumerable.Empty<JsonElement>();
        }

        internal static string FirstNonEmpty(JsonElement e, params string[] names)
        {
            foreach (var n in names)
            {
                var v = Str(e, n);
                if (v.Length > 0) return v;
            }
            return "";
        }

        private static int ErrorCode(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("error_code", out var v)) return 0;
            return v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetInt32(out var n) => n,
                JsonValueKind.String when int.TryParse(v.GetString(), out var n) => n,
                _ => 0
            };
        }

        internal static string Str(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return "";
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.GetRawText(),
                _ => ""
            };
        }

        // Never log names, ids or message text: only endpoint names, counts and error codes.
        private static void Log(string msg) => Debug.WriteLine("[Zalo10][API] " + msg);
    }
}
