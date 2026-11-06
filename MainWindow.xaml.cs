using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using zalo10_for_windows.Core;
using zalo10_for_windows.Views;

namespace zalo10_for_windows
{
    public sealed partial class MainWindow : Window
    {
        private readonly ZaloHttp _http = new();
        private ZaloSession _session;
        private readonly CancellationTokenSource _cts = new();

        // Messages already shown per conversation, so sent 1-1 messages survive switching threads.
        private readonly Dictionary<string, List<ChatMessage>> _chatCache = new();

        public MainWindow()
        {
            InitializeComponent();
            _session = new ZaloSession(_http.UserAgent);
            Title = "Zalo10";
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 720));

            // Title-bar / Alt+Tab icon. The Assets\*.png files only cover Start menu and taskbar tiles.
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Zalo10.ico");
            if (File.Exists(icon)) AppWindow.SetIcon(icon);

            Closed += (_, _) => { _cts.Cancel(); StopRealtime(); };

            Shell.ConversationSelected += c => _ = OpenConversationAsync(c);
            Shell.SendRequested += (c, text) => _ = SendMessageAsync(c, text);

            var contacts = Shell.ContactsPane;
            contacts.InvitesRequested += force => _ = LoadInvitesAsync(force);
            contacts.AcceptRequested += r => _ = RespondToRequestAsync(r, RequestAction.Accept);
            contacts.RejectRequested += r => _ = RespondToRequestAsync(r, RequestAction.Reject);
            contacts.UndoRequested += r => _ = RespondToRequestAsync(r, RequestAction.Undo);
        }

        private void Root_Loaded(object sender, RoutedEventArgs e) => _ = StartAsync();

        /// <summary>Saved session first; QR login only when there is none or Zalo no longer accepts it.</summary>
        private async Task StartAsync()
        {
            if (await TryRestoreSessionAsync(_cts.Token) || _cts.IsCancellationRequested) return;
            await RunLoginLoopAsync();
        }

        // ---- restore a saved session ---------------------------------------------------------------
        private async Task<bool> TryRestoreSessionAsync(CancellationToken ct)
        {
            var saved = await ZaloSessionStore.LoadAsync();
            if (saved == null) return false;

            ShowBusy("Đang khôi phục phiên đăng nhập...");
            _http.UserAgent = saved.UserAgent;
            _http.ResetCookies();
            _http.ImportCookies(saved.Cookies);
            _session = new ZaloSession(saved.UserAgent, saved.Imei);

            const int maxAttempts = 6;
            for (int attempt = 1; attempt <= maxAttempts && !ct.IsCancellationRequested; attempt++)
            {
                var r = await ZaloLoginFinisher.FinishAsync(_http, _session, ct);

                switch (r.Status)
                {
                    case FinishStatus.Success:
                        await ZaloSessionStore.SaveAsync(_http, _session); // cookies may have been renewed
                        ShowLoggedIn();
                        return true;

                    case FinishStatus.Cancelled:
                        return true; // window is closing

                    case FinishStatus.SessionInvalid:
                        await ZaloSessionStore.DeleteAsync();
                        StartFresh();
                        return false;

                    case FinishStatus.Transient:
                        // Keep the saved session: it is probably fine, the network just is not.
                        ShowBusy($"{r.Message}\nThử lại ({attempt}/{maxAttempts})...");
                        await DelayAsync(TimeSpan.FromSeconds(5), ct);
                        break;

                    default: // Failed: unknown problem, fall back to QR but leave the file alone
                        StartFresh();
                        return false;
                }
            }

            if (!ct.IsCancellationRequested) StartFresh();
            return ct.IsCancellationRequested;
        }

        private void StartFresh()
        {
            _http.UserAgent = ZaloHttp.DefaultUserAgent;
            _http.ResetCookies();
            _session = new ZaloSession(_http.UserAgent);
        }

        // ---- QR login ------------------------------------------------------------------------------
        /// <summary>Fetch a QR, wait for scan; when it expires or fails, fetch a new one automatically.</summary>
        private async Task RunLoginLoopAsync()
        {
            var ct = _cts.Token;
            int failures = 0;

            while (!ct.IsCancellationRequested)
            {
                ShowBusy("Đang lấy mã QR...");

                string? lastError = null;
                var login = new ZaloQrLogin(_http);
                login.QrReady += (png, _) => UI(async () => await ShowQrAsync(png));
                login.Scanned += name => UI(() => StatusText.Text = $"{name} đã quét mã. Hãy xác nhận trên điện thoại.");
                login.Failed += msg => lastError = msg;

                var result = await login.RunAsync(ct);

                if (result == QrLoginResult.Success)
                {
                    UI(() => ShowBusy("Đang lấy thông tin tài khoản..."));
                    var fin = await ZaloLoginFinisher.FinishAsync(_http, _session, ct);

                    if (fin.Status == FinishStatus.Success)
                    {
                        await ZaloSessionStore.SaveAsync(_http, _session);
                        ShowLoggedIn();
                        return;
                    }
                    if (fin.Status == FinishStatus.Cancelled) return;

                    lastError = fin.Message;
                    result = QrLoginResult.Failed;
                }

                switch (result)
                {
                    case QrLoginResult.Cancelled:
                        return;

                    case QrLoginResult.Expired:
                        failures = 0;
                        UI(() => ShowBusy("Mã QR đã hết hạn, đang lấy mã mới..."));
                        await DelayAsync(TimeSpan.FromMilliseconds(500), ct);
                        break;

                    default: // Failed: back off so we never hammer Zalo's servers
                        failures++;
                        int wait = Math.Min(30, 3 * failures);
                        for (int s = wait; s > 0 && !ct.IsCancellationRequested; s--)
                        {
                            int left = s;
                            UI(() =>
                            {
                                QrImage.Source = null;
                                Busy.IsActive = false;
                                StatusText.Text = $"{lastError ?? "Có lỗi xảy ra"}\nThử lại sau {left} giây...";
                            });
                            await DelayAsync(TimeSpan.FromSeconds(1), ct);
                        }
                        break;
                }
            }
        }

        // ---- UI helpers ----------------------------------------------------------------------------
        private void ShowLoggedIn()
        {
            QrImage.Source = null;
            Busy.IsActive = false;
            LoginPanel.Visibility = Visibility.Collapsed;
            Shell.Visibility = Visibility.Visible;
            Shell.SetUser(_session.DisplayName);
            _ = LoadConversationsAsync();
            StartRealtime();
        }

        // ---- real data ---------------------------------------------------------------------------
        /// <summary>Friends + groups, fetched in parallel and merged into one alphabetical list.</summary>
        private async Task LoadConversationsAsync()
        {
            var ct = _cts.Token;
            Shell.SetMessage("Đang tải danh sách...");

            var api = new ZaloApi(_http, _session);
            var friendsTask = api.GetFriendsAsync(ct);
            var groupsTask = api.GetGroupsAsync(ct);
            await Task.WhenAll(friendsTask, groupsTask);
            if (ct.IsCancellationRequested) return;

            var friends = friendsTask.Result;
            var groups = groupsTask.Result;

            if (friends.Status == ApiStatus.SessionExpired || groups.Status == ApiStatus.SessionExpired)
            {
                await ReturnToLoginAsync();
                return;
            }

            // Danh bạ tab: friends and groups each get their own list.
            Shell.ContactsPane.SetFriends(
                friends.Status == ApiStatus.Ok ? friends.Value : null,
                friends.Status == ApiStatus.Ok ? null : $"Không tải được danh sách bạn bè.\n{friends.Message}");
            Shell.ContactsPane.SetGroups(
                groups.Status == ApiStatus.Ok ? groups.Value : null,
                groups.Status == ApiStatus.Ok ? null : $"Không tải được danh sách nhóm.\n{groups.Message}");

            var all = new List<ContactInfo>();
            if (friends.Status == ApiStatus.Ok && friends.Value != null) all.AddRange(friends.Value);
            if (groups.Status == ApiStatus.Ok && groups.Value != null) all.AddRange(groups.Value);

            if (all.Count == 0)
            {
                bool anyOk = friends.Status == ApiStatus.Ok || groups.Status == ApiStatus.Ok;
                Shell.SetMessage(anyOk
                    ? "Chưa có hội thoại nào"
                    : $"Không tải được danh sách.\n{friends.Message ?? groups.Message}");
                return;
            }

            // Vietnamese collation. Recency ordering needs message timestamps, which come with the message API.
            var vi = StringComparer.Create(CultureInfo.GetCultureInfo("vi-VN"), true);
            Shell.SetConversations(all
                .OrderBy(c => c.Name, vi)
                .Select(c => new ConversationItem(c.Name, c.IsGroup ? "Nhóm" : "", "")
                {
                    ThreadId = c.Id,
                    IsGroup = c.IsGroup,
                    AvatarUrl = c.AvatarUrl,
                }));
        }


        // ---- friend requests (Danh bạ tab) -------------------------------------------------------
        private enum RequestAction { Accept, Reject, Undo }

        private DateTime _invitesLoadedAt = DateTime.MinValue;
        private bool _invitesLoading;

        /// <summary>Received, sent and suggested requests. Without force, a load within the last 15 s is reused.</summary>
        private async Task LoadInvitesAsync(bool force)
        {
            if (_invitesLoading) return;
            if (!force && DateTime.UtcNow - _invitesLoadedAt < TimeSpan.FromSeconds(15)) return;
            _invitesLoading = true;
            try
            {
                var ct = _cts.Token;
                var contacts = Shell.ContactsPane;
                contacts.SetInvitesLoading();

                var api = new ZaloApi(_http, _session);
                var recvTask = api.GetFriendRequestsAsync(ct);
                var sentTask = api.GetSentFriendRequestsAsync(ct);
                await Task.WhenAll(recvTask, sentTask);
                if (ct.IsCancellationRequested) return;

                var recv = recvTask.Result;
                var sent = sentTask.Result;
                if (recv.Status == ApiStatus.SessionExpired || sent.Status == ApiStatus.SessionExpired)
                {
                    await ReturnToLoginAsync();
                    return;
                }

                bool recvOk = recv.Status == ApiStatus.Ok && recv.Value != null;
                bool sentOk = sent.Status == ApiStatus.Ok && sent.Value != null;
                contacts.SetInvites(
                    recvOk ? recv.Value : null, recvOk ? null : $"Không tải được lời mời đã nhận. {recv.Message}",
                    sentOk ? sent.Value : null, sentOk ? null : $"Không tải được lời mời đã gửi. {sent.Message}");
                contacts.MarkInvitesLoaded();
                if (recvOk) _invitesLoadedAt = DateTime.UtcNow;
            }
            finally { _invitesLoading = false; }
        }

        private async Task RespondToRequestAsync(RequestRow row, RequestAction action)
        {
            var ct = _cts.Token;
            row.SetBusy(true, null);
            var api = new ZaloApi(_http, _session);
            var r = action switch
            {
                RequestAction.Accept => await api.AcceptFriendRequestAsync(row.Id, ct),
                RequestAction.Reject => await api.RejectFriendRequestAsync(row.Id, ct),
                _ => await api.UndoFriendRequestAsync(row.Id, ct),
            };
            if (ct.IsCancellationRequested) return;

            switch (r.Status)
            {
                case ApiStatus.Ok:
                    Shell.ContactsPane.RemoveRequest(row);
                    if (action == RequestAction.Accept) _ = RefreshFriendsAsync(row);
                    break;

                case ApiStatus.SessionExpired:
                    await ReturnToLoginAsync();
                    break;

                default:
                    row.SetBusy(false, $"Không thực hiện được: {r.Message ?? "lỗi không rõ"}");
                    break;
            }
        }

        /// <summary>After accepting a request: reload friends and make the new friend available in the chat list.</summary>
        private async Task RefreshFriendsAsync(RequestRow accepted)
        {
            var ct = _cts.Token;
            var friends = await new ZaloApi(_http, _session).GetFriendsAsync(ct);
            if (ct.IsCancellationRequested || friends.Status != ApiStatus.Ok || friends.Value == null) return;

            Shell.ContactsPane.SetFriends(friends.Value, null);
            var f = friends.Value.FirstOrDefault(x => x.Id == accepted.Id);
            if (f != null)
                Shell.AddConversationIfMissing(new ConversationItem(f.Name, "", "")
                {
                    ThreadId = f.Id, IsGroup = false, AvatarUrl = f.AvatarUrl,
                });
        }

        // ---- realtime (WebSocket) ----------------------------------------------------------------
        private ZaloWebSocket? _ws;
        private readonly HashSet<string> _historyLoaded = new();

        private void StartRealtime()
        {
            StopRealtime();
            var ws = new ZaloWebSocket(_http, _session);
            // Events come from a background thread: hop to the UI thread before touching anything visible.
            ws.MessagesReceived += msgs => UI(() => OnIncoming(msgs));
            ws.MessageRecalled += r => UI(() => OnRecalled(r));
            ws.ConnectionChanged += connected => UI(() => Shell.SetConnectionState(connected));
            _ws = ws;
            ws.Start();
        }

        private void StopRealtime()
        {
            _ws?.Dispose();
            _ws = null;
        }

        /// <summary>Adds to the per-conversation cache; false if that message id is already there.</summary>
        private bool AddToCache(string threadId, ChatMessage m)
        {
            if (!_chatCache.TryGetValue(threadId, out var list))
                _chatCache[threadId] = list = new List<ChatMessage>();
            if (m.MsgId.Length > 0 && list.Any(x => x.MsgId == m.MsgId)) return false;
            list.Add(m);
            return true;
        }

        private static List<ChatMessage> Sorted(IEnumerable<ChatMessage> msgs) =>
            msgs.OrderBy(m => m.TimeMs).ThenBy(m => m.MsgId, StringComparer.Ordinal).ToList();

        private static string OneLine(string text) => text.Replace("\r", "").Replace('\n', ' ');

        private void OnIncoming(IReadOnlyList<WsMessage> msgs)
        {
            foreach (var w in msgs)
            {
                if (!AddToCache(w.ThreadId, w.Message)) continue; // already shown (for example the echo of a message we just sent)

                Shell.AddChatMessage(w.ThreadId, ToItem(w.Message, w.IsGroup));

                string text = OneLine(w.Message.Text);
                string preview = w.Message.IsMine ? "Bạn: " + text
                    : w.IsGroup && w.Message.SenderName.Length > 0 ? $"{w.Message.SenderName}: {text}"
                    : text;
                Shell.Bump(w.ThreadId, preview, MessageItem.FormatTime(w.Message.TimeMs));
            }
        }

        private void OnRecalled(WsRecall r)
        {
            if (!_chatCache.TryGetValue(r.ThreadId, out var list)) return;
            int i = list.FindIndex(x => x.MsgId == r.MsgId);
            if (i < 0) return;

            var updated = list[i] with { Text = "Tin nhắn đã được thu hồi" };
            list[i] = updated;
            bool isGroup = Shell.Conversations.FirstOrDefault(c => c.ThreadId == r.ThreadId)?.IsGroup ?? false;
            Shell.ReplaceChatMessage(r.ThreadId, r.MsgId, ToItem(updated, isGroup));
        }

        // ---- chat ----------------------------------------------------------------------------------
        private MessageItem ToItem(ChatMessage m, bool isGroup) => new()
        {
            MsgId = m.MsgId,
            Text = m.Text,
            Sender = m.SenderName,
            TimeText = MessageItem.FormatTime(m.TimeMs),
            IsMine = m.IsMine,
            ShowSender = isGroup,
        };

        private async Task OpenConversationAsync(ConversationItem c)
        {
            var ct = _cts.Token;
            string id = c.ThreadId;

            // Show what we already have straight away (messages pushed or sent in this session).
            _chatCache.TryGetValue(id, out var cached);
            bool hadCache = cached is { Count: > 0 };
            if (hadCache) Shell.SetChatMessages(id, Sorted(cached!).Select(m => ToItem(m, c.IsGroup)), null, null);

            if (_historyLoaded.Contains(id))
            {
                if (!hadCache) Shell.SetChatMessages(id, Array.Empty<MessageItem>(), "Chưa có tin nhắn", null);
                return;
            }

            // History only exists on the WebSocket (the HTTP history endpoint now returns a web page).
            var ws = _ws;
            List<WsMessage>? history = ws == null ? null : await ws.RequestHistoryAsync(id, c.IsGroup, ct);
            if (ct.IsCancellationRequested) return;

            if (history == null)
            {
                if (!hadCache)
                    Shell.SetChatMessages(id, Array.Empty<MessageItem>(),
                        "Không tải được lịch sử tin nhắn.\nKết nối tin nhắn chưa sẵn sàng, hãy mở lại hội thoại sau vài giây.", null);
                return;
            }

            foreach (var h in history) AddToCache(id, h.Message);
            _historyLoaded.Add(id);

            _chatCache.TryGetValue(id, out var all);
            Shell.SetChatMessages(id, Sorted(all ?? new List<ChatMessage>()).Select(m => ToItem(m, c.IsGroup)),
                "Chưa có tin nhắn", null);
        }

        private async Task SendMessageAsync(ConversationItem c, string text)
        {
            var ct = _cts.Token;
            var r = await new ZaloApi(_http, _session).SendTextAsync(c.ThreadId, c.IsGroup, text, ct);
            if (ct.IsCancellationRequested) return;

            switch (r.Status)
            {
                case ApiStatus.Ok when r.Value != null:
                    // The WebSocket may already have delivered the echo of this message: AddToCache then says "not new".
                    if (AddToCache(c.ThreadId, r.Value))
                        Shell.AddChatMessage(c.ThreadId, ToItem(r.Value, c.IsGroup));
                    Shell.Bump(c.ThreadId, "Bạn: " + OneLine(text), MessageItem.FormatTime(r.Value.TimeMs));
                    break;

                case ApiStatus.SessionExpired:
                    await ReturnToLoginAsync();
                    break;

                default:
                    Shell.SetChatStatus(c.ThreadId, $"Gửi thất bại: {r.Message ?? "lỗi không rõ"}");
                    Shell.RestoreChatInput(c.ThreadId, text);
                    break;
            }
        }

        /// <summary>Zalo rejected the session mid-use (error 600): forget it and show the QR again.</summary>
        private async Task ReturnToLoginAsync()
        {
            StopRealtime();
            _chatCache.Clear();
            _historyLoaded.Clear();
            _invitesLoadedAt = DateTime.MinValue;
            await ZaloSessionStore.DeleteAsync();
            StartFresh();
            Shell.Visibility = Visibility.Collapsed;
            LoginPanel.Visibility = Visibility.Visible;
            await RunLoginLoopAsync();
        }

        private void ShowBusy(string text)
        {
            QrImage.Source = null;
            Busy.IsActive = true;
            StatusText.Text = text;
        }

        private async Task ShowQrAsync(byte[] png)
        {
            var bmp = new BitmapImage();
            using var ms = new InMemoryRandomAccessStream();
            await ms.WriteAsync(png.AsBuffer());
            ms.Seek(0);
            await bmp.SetSourceAsync(ms);
            QrImage.Source = bmp;
            Busy.IsActive = false;
            StatusText.Text = "Mở Zalo trên điện thoại và quét mã QR này";
        }

        private void UI(Action a) => DispatcherQueue.TryEnqueue(() => a());
        private void UI(Func<Task> a) => DispatcherQueue.TryEnqueue(async () => await a());

        private static async Task DelayAsync(TimeSpan t, CancellationToken ct)
        {
            try { await Task.Delay(t, ct); } catch (OperationCanceledException) { }
        }
    }
}
