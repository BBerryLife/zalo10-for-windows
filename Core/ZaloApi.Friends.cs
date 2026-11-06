using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace zalo10_for_windows.Core
{
    /// <summary>A friend request (received or sent) or a friend suggestion.</summary>
    public sealed record FriendRequestInfo(string Id, string Name, string AvatarUrl, string Message, long TimeMs);

    public sealed record FriendRequestSet(List<FriendRequestInfo> Received, List<FriendRequestInfo> Suggestions);

    /// <summary>
    /// Friend requests. Received requests, suggestions, accept and reject follow Zalo10
    /// (fetchInvites / acceptFriendRequest / rejectFriendRequest). The list of sent requests and
    /// "undo" are NOT in Zalo10: they follow the Zalo web endpoints as far as known and are untested.
    /// </summary>
    public sealed partial class ZaloApi
    {
        private bool TryFriendBase(out string baseUrl) =>
            _s.ServiceUrls.TryGetValue("friend", out baseUrl!) || _s.ServiceUrls.TryGetValue("profile", out baseUrl!);

        // ---- received requests + suggestions (recommendsv2/list) -----------------------------------
        public async Task<ApiResult<FriendRequestSet>> GetFriendRequestsAsync(CancellationToken ct)
        {
            if (!TryFriendBase(out var baseUrl))
                return new(ApiStatus.Failed, null, "Thiếu địa chỉ dịch vụ kết bạn");
            try
            {
                var inner = new Dictionary<string, object> { ["imei"] = _s.Imei };
                var q = Versioned();
                q["params"] = ZaloCrypto.AesEncryptBase64(_s.SecretKey, ZaloCrypto.ToJsonTyped(inner));
                using var resp = await _http.GetAsync(ZaloCrypto.BuildUrl(baseUrl + "/api/friend/recommendsv2/list", q), Referer, ct);
                using var reply = await ReadAsync("recommends", resp, ct);
                if (reply.Status != ApiStatus.Ok) return new(reply.Status, null, reply.Message);

                var items = FindArray(reply.Data, "recommItems").ToList();
                if (items.Count == 0 && reply.Data.ValueKind == JsonValueKind.Object &&
                    reply.Data.TryGetProperty("data", out var d))
                    items = FindArray(d, "recommItems").ToList();

                var received = new List<FriendRequestInfo>();
                var suggestions = new List<FriendRequestInfo>();
                foreach (var item in items)
                {
                    if (!item.TryGetProperty("dataInfo", out var info) || info.ValueKind != JsonValueKind.Object) continue;
                    int type = int.TryParse(Str(info, "recommType"), out var t) ? t : 0; // 1 = suggestion, 2 = received request
                    if (type != 1 && type != 2) continue;

                    string id = FirstNonEmpty(info, "userId", "uid", "fid");
                    if (id.Length == 0) continue;
                    string name = FirstNonEmpty(info, "zaloName", "displayName", "fullName");

                    string msg = "";
                    long time = ParseTime(FirstNonEmpty(info, "recommTime", "time"));
                    if (time == 0) time = ParseTime(Str(item, "recommTime"));
                    if (info.TryGetProperty("recommInfo", out var ri) && ri.ValueKind == JsonValueKind.Object)
                    {
                        msg = FirstNonEmpty(ri, "message", "customText");
                        if (time == 0) time = ParseTime(Str(ri, "time"));
                    }
                    if (msg.Length == 0) msg = Str(info, "msg");
                    msg = msg.Trim();
                    if (msg.StartsWith("{") || msg.StartsWith("[")) msg = "";

                    var r = new FriendRequestInfo(id, name.Length > 0 ? name : id, Str(info, "avatar"), msg, time);
                    (type == 2 ? received : suggestions).Add(r);
                }
                Log($"recommends: {received.Count} loi moi, {suggestions.Count} goi y");
                return new(ApiStatus.Ok, new FriendRequestSet(received, suggestions));
            }
            catch (Exception ex) { return Fail<FriendRequestSet>("recommends", ex, ct); }
        }

        // ---- sent requests (not in Zalo10; endpoint from Zalo web, untested) -----------------------
        public async Task<ApiResult<List<FriendRequestInfo>>> GetSentFriendRequestsAsync(CancellationToken ct)
        {
            if (!TryFriendBase(out var baseUrl))
                return new(ApiStatus.Failed, null, "Thiếu địa chỉ dịch vụ kết bạn");
            try
            {
                var inner = new Dictionary<string, object>
                {
                    ["page"] = 1, ["count"] = 100, ["last_id"] = 0, ["avatar_size"] = 120, ["imei"] = _s.Imei,
                };
                var q = Versioned();
                q["params"] = ZaloCrypto.AesEncryptBase64(_s.SecretKey, ZaloCrypto.ToJsonTyped(inner));
                using var resp = await _http.GetAsync(ZaloCrypto.BuildUrl(baseUrl + "/api/friend/requested/list", q), Referer, ct);
                using var reply = await ReadAsync("requested", resp, ct);
                if (reply.Status != ApiStatus.Ok) return new(reply.Status, null, reply.Message);

                // The reply is either a list or a map keyed by user id.
                var entries = new List<JsonElement>();
                var data = reply.Data;
                if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("data", out var inn) &&
                    inn.ValueKind is JsonValueKind.Object or JsonValueKind.Array) data = inn;
                if (data.ValueKind == JsonValueKind.Array) entries.AddRange(data.EnumerateArray());
                else if (data.ValueKind == JsonValueKind.Object)
                    entries.AddRange(data.EnumerateObject().Select(p => p.Value).Where(v => v.ValueKind == JsonValueKind.Object));

                var result = new List<FriendRequestInfo>();
                foreach (var e in entries)
                {
                    string id = FirstNonEmpty(e, "userId", "uid", "fid");
                    if (id.Length == 0) continue;
                    string name = FirstNonEmpty(e, "zaloName", "displayName", "fullName");
                    long time = 0; string msg = "";
                    if (e.TryGetProperty("fReqInfo", out var fi) && fi.ValueKind == JsonValueKind.Object)
                    {
                        time = ParseTime(Str(fi, "time"));
                        msg = Str(fi, "message");
                    }
                    result.Add(new FriendRequestInfo(id, name.Length > 0 ? name : id, Str(e, "avatar"), msg, time));
                }
                Log($"requested: {result.Count} loi moi da gui");
                return new(ApiStatus.Ok, result);
            }
            catch (Exception ex) { return Fail<List<FriendRequestInfo>>("requested", ex, ct); }
        }

        // ---- accept / reject / undo -----------------------------------------------------------------
        public Task<ApiResult<bool>> AcceptFriendRequestAsync(string userId, CancellationToken ct) =>
            FriendActionAsync("accept", "/api/friend/accept",
                new Dictionary<string, object> { ["fid"] = userId, ["language"] = _s.Language }, ct);

        public Task<ApiResult<bool>> RejectFriendRequestAsync(string userId, CancellationToken ct) =>
            FriendActionAsync("reject", "/api/friend/reject", new Dictionary<string, object> { ["fid"] = userId }, ct);

        /// <summary>Withdraw a request we sent. Endpoint not in Zalo10: untested.</summary>
        public Task<ApiResult<bool>> UndoFriendRequestAsync(string userId, CancellationToken ct) =>
            FriendActionAsync("undo", "/api/friend/undo", new Dictionary<string, object> { ["fid"] = userId }, ct);

        private async Task<ApiResult<bool>> FriendActionAsync(string name, string path, Dictionary<string, object> inner, CancellationToken ct)
        {
            if (!TryFriendBase(out var baseUrl))
                return new(ApiStatus.Failed, false, "Thiếu địa chỉ dịch vụ kết bạn");
            try
            {
                string enc = ZaloCrypto.AesEncryptBase64(_s.SecretKey, ZaloCrypto.ToJsonTyped(inner));
                string url = ZaloCrypto.BuildUrl(baseUrl + path, Versioned());
                using var resp = await _http.PostFormAsync(url, Referer,
                    new[] { new KeyValuePair<string, string>("params", enc) }, ct);
                using var reply = await ReadAsync(name, resp, ct);
                Log($"{name}: {reply.Status}");
                return reply.Status == ApiStatus.Ok ? new(ApiStatus.Ok, true) : new(reply.Status, false, reply.Message);
            }
            catch (Exception ex) { return Fail<bool>(name, ex, ct); }
        }

        /// <summary>Unix time in seconds or milliseconds (as text) to milliseconds; 0 when unknown.</summary>
        private static long ParseTime(string s)
        {
            if (!long.TryParse(s, out long v) || v <= 0) return 0;
            return v < 100_000_000_000L ? v * 1000 : v;
        }
    }
}
