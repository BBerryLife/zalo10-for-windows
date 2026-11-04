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

            Closed += (_, _) => _cts.Cancel();

            Shell.ConversationSelected += c => _ = OpenConversationAsync(c);
            Shell.SendRequested += (c, text) => _ = SendMessageAsync(c, text);
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


        // ---- chat ----------------------------------------------------------------------------------
        private MessageItem ToItem(ChatMessage m, bool isGroup) => new()
        {
            Text = m.Text,
            Sender = m.SenderName,
            TimeText = MessageItem.FormatTime(m.TimeMs),
            IsMine = m.IsMine,
            ShowSender = isGroup,
        };

        private async Task OpenConversationAsync(ConversationItem c)
        {
            var ct = _cts.Token;

            if (c.IsGroup)
            {
                var r = await new ZaloApi(_http, _session).GetGroupHistoryAsync(c.ThreadId, ct);
                if (ct.IsCancellationRequested) return;

                if (r.Status == ApiStatus.SessionExpired) { await ReturnToLoginAsync(); return; }
                if (r.Status != ApiStatus.Ok)
                {
                    Shell.SetChatMessages(c.ThreadId, Array.Empty<MessageItem>(),
                        $"Không tải được tin nhắn.\n{r.Message}", null);
                    return;
                }
                _chatCache[c.ThreadId] = r.Value ?? new List<ChatMessage>();
            }

            _chatCache.TryGetValue(c.ThreadId, out var msgs);
            string? note = c.IsGroup
                ? null
                : "Lịch sử tin nhắn riêng chỉ có qua kết nối WebSocket (chưa làm), nên chưa hiển thị. Bạn vẫn gửi được tin nhắn.";

            Shell.SetChatMessages(c.ThreadId,
                (msgs ?? new List<ChatMessage>()).Select(m => ToItem(m, c.IsGroup)),
                c.IsGroup ? "Chưa có tin nhắn" : "Chưa có tin nhắn trong phiên này", note);
        }

        private async Task SendMessageAsync(ConversationItem c, string text)
        {
            var ct = _cts.Token;
            var r = await new ZaloApi(_http, _session).SendTextAsync(c.ThreadId, c.IsGroup, text, ct);
            if (ct.IsCancellationRequested) return;

            switch (r.Status)
            {
                case ApiStatus.Ok when r.Value != null:
                    if (!_chatCache.TryGetValue(c.ThreadId, out var list))
                        _chatCache[c.ThreadId] = list = new List<ChatMessage>();
                    list.Add(r.Value);
                    Shell.AddChatMessage(c.ThreadId, ToItem(r.Value, c.IsGroup));
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
