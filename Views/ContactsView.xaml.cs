using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using zalo10_for_windows.Core;

namespace zalo10_for_windows.Views
{
    /// <summary>
    /// The Danh bạ tab: friends, groups and communities, friend requests.
    /// It is a pure view: the host feeds it data and handles the events.
    /// </summary>
    public sealed partial class ContactsView : UserControl
    {
        private static readonly StringComparer Vi = StringComparer.Create(CultureInfo.GetCultureInfo("vi-VN"), true);

        private List<ContactInfo> _friends = new();
        private List<ContactInfo> _groups = new();
        private bool _ready;

        private readonly ObservableCollection<RequestRow> _received = new();
        private readonly ObservableCollection<RequestRow> _sent = new();
        private readonly ObservableCollection<RequestRow> _suggestions = new();

        /// <summary>Open the chat with this friend or group (thread id, is group).</summary>
        public event Action<string, bool>? OpenChatRequested;

        /// <summary>The friend-request page was shown (false) or its refresh button pressed (true).</summary>
        public event Action<bool>? InvitesRequested;

        public event Action<RequestRow>? AcceptRequested;
        public event Action<RequestRow>? RejectRequested;
        public event Action<RequestRow>? UndoRequested;

        public ContactsView()
        {
            InitializeComponent();
            ReceivedList.ItemsSource = _received;
            SentList.ItemsSource = _sent;
            SuggestionList.ItemsSource = _suggestions;
            _received.CollectionChanged += (_, _) => UpdateInviteHeaders();
            _sent.CollectionChanged += (_, _) => UpdateInviteHeaders();
            _suggestions.CollectionChanged += (_, _) => UpdateInviteHeaders();

            FriendsEmpty.Text = "Đang tải danh sách...";
            GroupsEmpty.Text = "Đang tải danh sách...";
            UpdateInviteHeaders();

            _ready = true;
            MenuList.SelectedIndex = 0;
        }

        // ---- sections ------------------------------------------------------------------------------
        private void MenuList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            int i = MenuList.SelectedIndex;
            if (i < 0) return;

            FriendsPage.Visibility = i == 0 ? Visibility.Visible : Visibility.Collapsed;
            GroupsPage.Visibility = i == 1 ? Visibility.Visible : Visibility.Collapsed;
            InvitesPage.Visibility = i == 2 ? Visibility.Visible : Visibility.Collapsed;
            GroupInvitesPage.Visibility = i == 3 ? Visibility.Visible : Visibility.Collapsed;
            RefreshButton.Visibility = i == 2 ? Visibility.Visible : Visibility.Collapsed;

            string[] titles = { "Danh sách bạn bè", "Danh sách nhóm và cộng đồng", "Lời mời kết bạn", "Lời mời vào nhóm và cộng đồng" };
            string[] icons = { "ic_contact", "ic_groups", "ic_add_contact", "ic_create_group" };
            HeaderTitle.Text = titles[i];
            HeaderIcon.UriSource = new Uri($"ms-appx:///Assets/Icons/{icons[i]}.png");

            if (i == 2) InvitesRequested?.Invoke(false);
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => InvitesRequested?.Invoke(true);

        // ---- friends and groups --------------------------------------------------------------------
        /// <param name="list">Null when loading failed; <paramref name="error"/> then says why.</param>
        public void SetFriends(List<ContactInfo>? list, string? error)
        {
            _friends = list ?? new List<ContactInfo>();
            FriendsEmpty.Text = error ?? "";
            RebuildFriends();
        }

        public void SetGroups(List<ContactInfo>? list, string? error)
        {
            _groups = list ?? new List<ContactInfo>();
            GroupsEmpty.Text = error ?? "";
            RebuildGroups();
        }

        private void FriendSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) { if (_ready) RebuildFriends(); }
        private void Friend_OptionChanged(object sender, SelectionChangedEventArgs e) { if (_ready) RebuildFriends(); }
        private void GroupSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) { if (_ready) RebuildGroups(); }
        private void Group_OptionChanged(object sender, SelectionChangedEventArgs e) { if (_ready) RebuildGroups(); }

        private static IEnumerable<ContactInfo> Filter(List<ContactInfo> all, string query, bool descending)
        {
            string q = TextFold.Fold(query.Trim());
            var src = q.Length == 0 ? all : all.Where(c => TextFold.Fold(c.Name).Contains(q));
            return descending ? src.OrderByDescending(c => c.Name, Vi) : src.OrderBy(c => c.Name, Vi);
        }

        private void RebuildFriends()
        {
            FriendsCount.Text = $"Bạn bè ({_friends.Count})";
            var rows = new List<ContactRow>();
            string last = "";
            foreach (var c in Filter(_friends, FriendSearch.Text ?? "", FriendSort.SelectedIndex == 1))
            {
                string letter = TextFold.Letter(c.Name);
                if (letter != last)
                {
                    rows.Add(new ContactRow { IsHeader = true, Letter = letter });
                    last = letter;
                }
                rows.Add(new ContactRow { Id = c.Id, Title = c.Name, AvatarUrl = c.AvatarUrl });
            }
            FriendList.ItemsSource = rows;

            if (rows.Count == 0 && FriendsEmpty.Text.Length == 0)
                FriendsEmpty.Text = _friends.Count == 0 ? "Chưa có bạn bè nào" : "Không tìm thấy bạn nào";
            else if (rows.Count > 0) FriendsEmpty.Text = "";
            FriendsEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RebuildGroups()
        {
            GroupsCount.Text = $"Nhóm và cộng đồng ({_groups.Count})";
            var rows = Filter(_groups, GroupSearch.Text ?? "", GroupSort.SelectedIndex == 1)
                .Select(c => new ContactRow
                {
                    Id = c.Id, Title = c.Name, IsGroup = true, AvatarUrl = c.AvatarUrl,
                    Subtitle = c.MemberCount > 0 ? $"{c.MemberCount} thành viên" : "",
                }).ToList();
            GroupList.ItemsSource = rows;

            if (rows.Count == 0 && GroupsEmpty.Text.Length == 0)
                GroupsEmpty.Text = _groups.Count == 0 ? "Chưa tham gia nhóm nào" : "Không tìm thấy nhóm nào";
            else if (rows.Count > 0) GroupsEmpty.Text = "";
            GroupsEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ContactList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ContactRow { IsHeader: false } r) OpenChatRequested?.Invoke(r.Id, r.IsGroup);
        }

        private void MenuChat_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: ContactRow r }) OpenChatRequested?.Invoke(r.Id, r.IsGroup);
        }

        // ---- friend requests -----------------------------------------------------------------------
        public void SetInvitesLoading()
        {
            if (_received.Count + _sent.Count + _suggestions.Count > 0) return; // keep what is shown while refreshing
            InvitesStatus.Text = "Đang tải lời mời...";
            InvitesStatus.Visibility = Visibility.Visible;
        }

        /// <summary>A null list means that part failed to load; the matching error text says why.</summary>
        public void SetInvites(FriendRequestSet? received, string? receivedError, List<FriendRequestInfo>? sent, string? sentError)
        {
            if (received != null)
            {
                Fill(_received, received.Received.Select(r => new RequestRow
                {
                    Kind = RequestKind.Received, Id = r.Id, Name = r.Name, AvatarUrl = r.AvatarUrl,
                    Subtitle = FormatDate(r.TimeMs), Message = r.Message,
                }));
                Fill(_suggestions, received.Suggestions.Select(r => new RequestRow
                {
                    Kind = RequestKind.Suggestion, Id = r.Id, Name = r.Name, AvatarUrl = r.AvatarUrl,
                    Subtitle = r.Message.Length > 0 ? r.Message : "Gợi ý kết bạn",
                }));
            }
            if (sent != null)
            {
                Fill(_sent, sent.Select(r => new RequestRow
                {
                    Kind = RequestKind.Sent, Id = r.Id, Name = r.Name, AvatarUrl = r.AvatarUrl,
                    Subtitle = "Bạn đã gửi lời mời",
                }));
            }

            ReceivedError.Text = receivedError ?? "";
            ReceivedError.Visibility = receivedError != null ? Visibility.Visible : Visibility.Collapsed;
            SentError.Text = sentError ?? "";
            SentError.Visibility = sentError != null ? Visibility.Visible : Visibility.Collapsed;
            _failedReceived = received == null;
            _failedSent = sent == null;
            UpdateInviteHeaders();
        }

        private bool _failedReceived, _failedSent;

        private static void Fill(ObservableCollection<RequestRow> target, IEnumerable<RequestRow> rows)
        {
            var list = rows.ToList();
            target.Clear();
            foreach (var r in list) target.Add(r);
        }

        public void RemoveRequest(RequestRow row)
        {
            _received.Remove(row);
            _sent.Remove(row);
        }

        private void UpdateInviteHeaders()
        {
            ReceivedHeader.Text = $"Lời mời đã nhận ({_received.Count})";
            SentHeader.Text = $"Lời mời đã gửi ({_sent.Count})";
            SuggestionsHeader.Text = $"Gợi ý kết bạn ({_suggestions.Count})";

            ReceivedSection.Visibility = _received.Count > 0 || _failedReceived ? Visibility.Visible : Visibility.Collapsed;
            SentSection.Visibility = _sent.Count > 0 || _failedSent ? Visibility.Visible : Visibility.Collapsed;
            SuggestionsSection.Visibility = _suggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            bool any = _received.Count + _sent.Count + _suggestions.Count > 0 || _failedReceived || _failedSent;
            if (any) InvitesStatus.Visibility = Visibility.Collapsed;
            else if (_loaded)
            {
                InvitesStatus.Text = "Bạn chưa có lời mời kết bạn nào";
                InvitesStatus.Visibility = Visibility.Visible;
            }
        }

        private bool _loaded;

        /// <summary>Call after SetInvites so an empty result reads "no requests" rather than "loading".</summary>
        public void MarkInvitesLoaded()
        {
            _loaded = true;
            UpdateInviteHeaders();
        }

        private static string FormatDate(long ms)
        {
            if (ms <= 0) return "";
            var t = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
            return t.Year == DateTime.Now.Year ? t.ToString("dd/MM") : t.ToString("dd/MM/yyyy");
        }

        private void Accept_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: RequestRow r }) AcceptRequested?.Invoke(r);
        }

        private void Reject_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: RequestRow r }) RejectRequested?.Invoke(r);
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: RequestRow r }) UndoRequested?.Invoke(r);
        }
    }
}
