using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace zalo10_for_windows.Core
{
    /// <summary>A chat message together with the conversation it belongs to.</summary>
    public sealed record WsMessage(string ThreadId, bool IsGroup, ChatMessage Message);

    /// <summary>The sender took a message back.</summary>
    public sealed record WsRecall(string ThreadId, string MsgId);

    /// <summary>
    /// Realtime connection: receives new messages and loads message history.
    /// Port of Zalo10's core/websocket (Transport, Frames, Dispatch, Push, History). ClientWebSocket
    /// already does TLS, framing and masking, so only the Zalo-specific parts are written here:
    /// the upgrade headers, the handshake that delivers the cipher key, the 25 s ping, and decoding.
    /// Events are raised on a background thread.
    /// </summary>
    public sealed class ZaloWebSocket : IDisposable
    {
        private const string Origin = "https://chat.zalo.me";
        private static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(25);

        private readonly ZaloHttp _http;
        private readonly ZaloSession _s;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly Dictionary<string, TaskCompletionSource<List<WsMessage>?>> _pending = new();
        private readonly HashSet<string> _loggedCmds = new();

        private ClientWebSocket? _ws;
        private byte[]? _cipherKey;
        private volatile bool _gotHandshake;
        private bool _connected;
        private int _historyLogs;
        private TaskCompletionSource<bool> _ready = NewReady();

        public event Action<IReadOnlyList<WsMessage>>? MessagesReceived;
        public event Action<WsRecall>? MessageRecalled;
        /// <summary>true once the handshake is done, false when the link drops.</summary>
        public event Action<bool>? ConnectionChanged;

        public bool IsConnected => _connected;

        public ZaloWebSocket(ZaloHttp http, ZaloSession session)
        {
            _http = http;
            _s = session;
        }

        public void Start() => _ = RunAsync(_cts.Token);

        // ---- connection loop -----------------------------------------------------------------------
        private async Task RunAsync(CancellationToken ct)
        {
            if (_s.WebSocketUrls.Count == 0) { Log("khong co dia chi websocket"); return; }

            int urlIndex = 0, fails = 0;
            while (!ct.IsCancellationRequested)
            {
                _gotHandshake = false;
                string baseUrl = _s.WebSocketUrls[urlIndex % _s.WebSocketUrls.Count];
                try { await ConnectOnceAsync(baseUrl, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { Log($"loi ket noi: {ex.GetType().Name}: {ex.Message}"); }

                SetConnected(false);

                // A host that never completed the handshake is skipped next time, like Zalo10 does.
                if (_gotHandshake) fails = 0; else urlIndex++;

                int delay = 5000 << Math.Min(fails, 3); // 5s, 10s, 20s, 40s
                fails++;
                Log($"mat ket noi, thu lai sau {delay / 1000}s");
                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task ConnectOnceAsync(string baseUrl, CancellationToken ct)
        {
            string sep = baseUrl.Contains('?') ? "&" : "?";
            string url = $"{baseUrl}{sep}t={ZaloCrypto.NowMs()}&zpw_ver={ZaloCrypto.ApiVersion}&zpw_type={ZaloCrypto.ApiType}";

            using var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.Zero; // Zalo has its own application-level ping
            ws.Options.SetRequestHeader("Origin", Origin);
            ws.Options.SetRequestHeader("User-Agent", _s.UserAgent);
            string cookies = _http.CookieHeaderForZaloMe();
            if (cookies.Length > 0) ws.Options.SetRequestHeader("Cookie", cookies);

            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(TimeSpan.FromSeconds(15));
                await ws.ConnectAsync(new Uri(url), connectCts.Token);
            }
            Log("da ket noi, dang cho bat tay");

            _ws = ws;
            _cipherKey = null;
            using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                var buffer = new byte[16 * 1024];
                using var message = new MemoryStream();
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    message.SetLength(0);
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (r.MessageType == WebSocketMessageType.Close)
                        {
                            Log($"may chu dong ket noi: {ws.CloseStatus} {ws.CloseStatusDescription}");
                            return;
                        }
                        message.Write(buffer, 0, r.Count);
                        if (message.Length > 16 * 1024 * 1024) throw new InvalidDataException("goi tin qua lon");
                    }
                    while (!r.EndOfMessage);

                    bool wasHandshake = HandleMessage(message.ToArray());
                    if (wasHandshake) _ = PingLoopAsync(ws, loopCts.Token);
                }
            }
            finally
            {
                loopCts.Cancel();
                _ws = null;
                try { ws.Abort(); } catch { /* already closed */ }
            }
        }

        private async Task PingLoopAsync(ClientWebSocket ws, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    await SendFrameAsync(ZaloWsCodec.BuildFrame(2, 1, $"{{\"eventId\":{ZaloCrypto.NowMs()}}}"), ct);
                    await Task.Delay(PingEvery, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log("ping loi: " + ex.Message); }
        }

        private async Task SendFrameAsync(byte[] frame, CancellationToken ct)
        {
            var ws = _ws;
            if (ws == null || ws.State != WebSocketState.Open) throw new InvalidOperationException("websocket chua ket noi");
            await _sendLock.WaitAsync(ct);
            try { await ws.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true, ct); }
            finally { _sendLock.Release(); }
        }

        // ---- incoming frames -----------------------------------------------------------------------
        /// <returns>true if this was the handshake reply.</returns>
        private bool HandleMessage(byte[] payload)
        {
            if (!ZaloWsCodec.TryParseFrame(payload, out int cmd, out int sub, out var json)) return false;

            if (cmd == 1 && sub == 1) { HandleHandshake(json); return true; }
            if ((cmd == 501 || cmd == 521) && sub == 0) { HandlePush(cmd == 521, json); return false; }
            if ((cmd == 510 || cmd == 511) && sub == 1) { HandleHistory(cmd == 511, json); return false; }

            // Receipts (502/522), group events (601), reactions (612)... are not used yet. Log each kind once.
            if (_loggedCmds.Add($"{cmd}/{sub}")) Log($"bo qua goi tin cmd={cmd} sub={sub}");
            return false;
        }

        private void HandleHandshake(ReadOnlyMemory<byte> json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                string key = ZaloApi.Str(doc.RootElement, "key");
                _cipherKey = Convert.FromBase64String(PadBase64(key));
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                Log("bat tay: khong doc duoc khoa");
                return;
            }
            _gotHandshake = true;
            Log($"bat tay OK (khoa {_cipherKey!.Length} byte)");
            SetConnected(true);
        }

        private void HandlePush(bool isGroup, ReadOnlyMemory<byte> json)
        {
            using var p = ZaloWsCodec.Decode(json, _cipherKey, _s.SecretKey);
            if (p == null) { Log($"khong giai ma duoc tin {(isGroup ? "nhom" : "rieng")}"); return; }

            var raw = FindMessages(p.Data, isGroup);
            var batch = ParseBatch(raw, isGroup, out var recalls);
            Log($"tin den {(isGroup ? "nhom" : "rieng")}: {raw.Count} muc, {batch.Count} tin");

            foreach (var r in recalls) MessageRecalled?.Invoke(r);
            if (batch.Count > 0) MessagesReceived?.Invoke(batch);
        }

        // ---- history -------------------------------------------------------------------------------
        /// <summary>
        /// Asks the server for the latest messages of one conversation (cmd 510 for 1-1, 511 for groups).
        /// Returns null when the link is down, the request times out or the reply cannot be read.
        /// </summary>
        public async Task<List<WsMessage>?> RequestHistoryAsync(string threadId, bool isGroup, CancellationToken ct)
        {
            using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            string key = PendingKey(isGroup, threadId);
            var tcs = new TaskCompletionSource<List<WsMessage>?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pending) _pending[key] = tcs;

            try
            {
                if (!await WaitReadyAsync(TimeSpan.FromSeconds(15), link.Token)) return null;

                string idField = isGroup ? "grid" : "toid";
                string body = $"{{\"first\":true,\"lastId\":null,\"{idField}\":\"{threadId}\",\"preIds\":[]}}";
                await SendFrameAsync(ZaloWsCodec.BuildFrame(isGroup ? 511 : 510, 1, body), link.Token);

                var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(12), link.Token));
                if (done != tcs.Task) { Log($"lich su {(isGroup ? "nhom" : "rieng")}: het thoi gian cho"); return null; }
                return tcs.Task.Result;
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex) when (ex is WebSocketException or InvalidOperationException)
            {
                Log("yeu cau lich su loi: " + ex.Message);
                return null;
            }
            finally
            {
                lock (_pending)
                    if (_pending.TryGetValue(key, out var cur) && cur == tcs) _pending.Remove(key);
            }
        }

        private void HandleHistory(bool isGroup, ReadOnlyMemory<byte> json)
        {
            using var p = ZaloWsCodec.Decode(json, _cipherKey, _s.SecretKey);
            if (p == null)
            {
                Log("lich su: khong giai ma duoc");
                CompletePending(isGroup, new Dictionary<string, List<WsMessage>>(), failed: true);
                return;
            }

            var raw = FindMessages(p.Data, isGroup);
            if (_historyLogs++ < 3)
                Log($"lich su: truong=[{string.Join(",", Names(p.Data))}], {raw.Count} muc");

            var batch = ParseBatch(raw, isGroup, out _);
            var byThread = batch.GroupBy(m => m.ThreadId).ToDictionary(g => g.Key, g => g.ToList());
            Log($"lich su {(isGroup ? "nhom" : "rieng")}: {batch.Count} tin, {byThread.Count} hoi thoai");
            CompletePending(isGroup, byThread, failed: false);
        }

        /// <summary>The reply does not say which conversation it is for, so answer every waiting request of that kind.</summary>
        private void CompletePending(bool isGroup, Dictionary<string, List<WsMessage>> byThread, bool failed)
        {
            string prefix = isGroup ? "g:" : "u:";
            List<(TaskCompletionSource<List<WsMessage>?> tcs, List<WsMessage>? result)> toComplete = new();
            lock (_pending)
            {
                foreach (var kv in _pending.Where(k => k.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                {
                    string thread = kv.Key.Substring(2);
                    List<WsMessage>? result = failed ? null
                        : byThread.TryGetValue(thread, out var l) ? l : new List<WsMessage>();
                    toComplete.Add((kv.Value, result));
                }
            }
            foreach (var (tcs, result) in toComplete) tcs.TrySetResult(result);
        }

        // ---- message parsing -----------------------------------------------------------------------
        private static List<JsonElement> FindMessages(JsonElement d, bool isGroup)
        {
            string[] order = isGroup ? new[] { "groupMsgs", "msgs", "ms" } : new[] { "msgs", "ms", "groupMsgs" };

            static List<JsonElement>? Probe(JsonElement e, string[] names)
            {
                if (e.ValueKind != JsonValueKind.Object) return null;
                foreach (var n in names)
                    if (e.TryGetProperty(n, out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > 0)
                        return a.EnumerateArray().ToList();
                return null;
            }

            var found = Probe(d, order);
            if (found != null) return found;
            if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("data", out var inner))
                found = Probe(inner, order);
            return found ?? new List<JsonElement>();
        }

        private List<WsMessage> ParseBatch(List<JsonElement> raw, bool isGroup, out List<WsRecall> recalls)
        {
            recalls = new List<WsRecall>();
            var recalledIds = new HashSet<string>();

            // Pass 1: ids of messages that were taken back (they arrive as separate "chat.undo" messages).
            foreach (var m in raw)
            {
                if (!ZaloApi.Str(m, "msgType").Equals("chat.undo", StringComparison.OrdinalIgnoreCase)) continue;
                using var c = ZaloApi.ContentObject(m);
                string id = ZaloApi.FirstNonEmpty(c.RootElement, "globalMsgId", "cliMsgId");
                if (id.Length == 0) continue;
                recalledIds.Add(id);
                string thread = ThreadOf(m, isGroup, out _, out _);
                if (thread.Length > 0) recalls.Add(new WsRecall(thread, id));
            }

            // Pass 2: the messages themselves.
            var result = new List<WsMessage>();
            foreach (var m in raw)
            {
                string type = ZaloApi.Str(m, "msgType");
                if (type is "group.poll" or "chat.undo" or "chat.delete" or "chat.reaction") continue;

                string thread = ThreadOf(m, isGroup, out bool self, out string from);
                if (thread.Length == 0) continue;

                string msgId = ZaloApi.Str(m, "msgId");
                string cliId = ZaloApi.Str(m, "cliMsgId");
                string text = recalledIds.Contains(msgId) || (cliId.Length > 0 && recalledIds.Contains(cliId))
                    ? "Tin nhắn đã được thu hồi"
                    : ZaloApi.Describe(type, m);
                if (text.Length == 0) continue;

                long.TryParse(ZaloApi.Str(m, "ts"), out long ts);
                if (ts <= 0) ts = ZaloCrypto.NowMs();

                result.Add(new WsMessage(thread, isGroup,
                    new ChatMessage(msgId, from, ZaloApi.Str(m, "dName"), ts, self, text)));
            }
            return result;
        }

        /// <summary>
        /// Which conversation a message belongs to. Zalo writes "0" for "me" in uidFrom / idTo.
        /// Group: the group id (idTo). 1-1: the other person.
        /// </summary>
        private string ThreadOf(JsonElement m, bool isGroup, out bool self, out string from)
        {
            string rawFrom = ZaloApi.Str(m, "uidFrom");
            string rawTo = ZaloApi.Str(m, "idTo");
            self = rawFrom == "0" || rawFrom == _s.Uid;
            from = self ? _s.Uid : rawFrom;
            string to = rawTo == "0" ? _s.Uid : rawTo;

            string thread = isGroup ? to : (self ? to : from);
            return thread.Length == 0 || thread == _s.Uid ? "" : thread;
        }

        // ---- helpers -------------------------------------------------------------------------------
        private static string PendingKey(bool isGroup, string threadId) => (isGroup ? "g:" : "u:") + threadId;

        private static TaskCompletionSource<bool> NewReady() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private void SetConnected(bool value)
        {
            if (_connected == value) return;
            _connected = value;
            if (value) _ready.TrySetResult(true);
            else if (_ready.Task.IsCompleted) _ready = NewReady();
            ConnectionChanged?.Invoke(value);
        }

        private async Task<bool> WaitReadyAsync(TimeSpan timeout, CancellationToken ct)
        {
            var task = _ready.Task;
            var done = await Task.WhenAny(task, Task.Delay(timeout, ct));
            return done == task && task.IsCompletedSuccessfully;
        }

        private static string PadBase64(string s)
        {
            var t = s.Trim().Replace('-', '+').Replace('_', '/').TrimEnd('=');
            return (t.Length % 4) switch { 2 => t + "==", 3 => t + "=", _ => t };
        }

        private static IEnumerable<string> Names(JsonElement e) =>
            e.ValueKind == JsonValueKind.Object ? e.EnumerateObject().Select(p => p.Name) : Enumerable.Empty<string>();

        private int _disposed;

        /// <summary>Safe to call more than once (re-login and window close can both reach it).</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _cts.Cancel();
            try { _ws?.Abort(); } catch { /* ignore */ }
            lock (_pending)
                foreach (var t in _pending.Values) t.TrySetResult(null);
            _cts.Dispose();
        }

        // Never log message text, names, ids or keys: only counts and protocol numbers.
        private static void Log(string msg) => Debug.WriteLine("[Zalo10][WS] " + msg);
    }
}
