using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace zalo10_for_windows.Views
{
    /// <summary>One row of the conversation list.</summary>
    public sealed class ConversationItem : INotifyPropertyChanged
    {
        private string _preview;
        private string _time;

        public ConversationItem(string title, string preview, string time)
        {
            Title = title;
            _preview = preview;
            _time = time;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Title { get; set; }

        /// <summary>Second line of the row: last message, or "Nhóm" for a group.</summary>
        public string Preview
        {
            get => _preview;
            set { if (_preview != value) { _preview = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preview))); } }
        }

        public string Time
        {
            get => _time;
            set { if (_time != value) { _time = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Time))); } }
        }

        /// <summary>Zalo user id (friend) or group id: what the chat view will need later.</summary>
        public string ThreadId { get; set; } = "";
        public bool IsGroup { get; set; }
        public string AvatarUrl { get; set; } = "";

        private ImageSource? _picture;
        private bool _pictureTried;

        /// <summary>
        /// Created on first read, which happens when the list realises this row on screen.
        /// That way a few hundred contacts do not trigger a few hundred image downloads at once.
        /// Null (initials are shown instead) when the contact has no usable avatar.
        /// </summary>
        public ImageSource? Picture
        {
            get
            {
                if (!_pictureTried)
                {
                    _pictureTried = true;
                    if (Uri.TryCreate(AvatarUrl, UriKind.Absolute, out var uri) &&
                        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                        _picture = new BitmapImage(uri) { DecodePixelWidth = 88 };
                }
                return _picture;
            }
        }
    }

    /// <summary>
    /// Main screen after login: blue sidebar, conversation list, content area.
    /// For now it is only the layout; real conversations are filled in through SetConversations().
    /// </summary>
    public sealed partial class MainShell : UserControl
    {
        public ObservableCollection<ConversationItem> Conversations { get; } = new();

        /// <summary>The user clicked a conversation in the list.</summary>
        public event Action<ConversationItem>? ConversationSelected;

        /// <summary>The user sent text from the chat view.</summary>
        public event Action<ConversationItem, string>? SendRequested;

        private ConversationItem? _current;
        private bool _suppressSelection;
        private string _user = "";

        public MainShell()
        {
            InitializeComponent();
            Conversations.CollectionChanged += (_, _) => UpdateEmpty();
            UpdateEmpty();

            Chat.SendRequested += text =>
            {
                if (_current != null) SendRequested?.Invoke(_current, text);
            };
        }

        // ---- chat -----------------------------------------------------------------------------------
        private void ConversationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelection) return;
            if (ConversationList.SelectedItem is not ConversationItem c) return;
            _current = c;
            WelcomeHost.Visibility = Visibility.Collapsed;
            Chat.Visibility = Visibility.Visible;
            Chat.Begin(c);
            ConversationSelected?.Invoke(c);
        }

        // The results below arrive asynchronously. If the user has already opened another
        // conversation by then, they are dropped (the threadId no longer matches).
        public void SetChatMessages(string threadId, IEnumerable<MessageItem> items, string? emptyText, string? note)
        {
            if (_current?.ThreadId == threadId) Chat.SetMessages(items, emptyText, note);
        }

        public void AddChatMessage(string threadId, MessageItem item)
        {
            if (_current?.ThreadId == threadId) Chat.Add(item);
        }

        public void ReplaceChatMessage(string threadId, string msgId, MessageItem item)
        {
            if (_current?.ThreadId == threadId) Chat.Replace(msgId, item);
        }

        public void SetChatStatus(string threadId, string? text)
        {
            if (_current?.ThreadId == threadId) Chat.SetStatus(text);
        }

        public void RestoreChatInput(string threadId, string text)
        {
            if (_current?.ThreadId == threadId) Chat.RestoreInput(text);
        }

        public void SetUser(string displayName)
        {
            _user = displayName;
            Avatar.DisplayName = displayName;
            StatusLine.Text = $"Đã đăng nhập: {displayName}";
        }

        public void SetConnectionState(bool connected) =>
            StatusLine.Text = $"Đã đăng nhập: {_user} · " + (connected ? "đã kết nối" : "đang kết nối lại...");

        /// <summary>A message arrived or was sent: refresh the row's preview and move it to the top.</summary>
        public void Bump(string threadId, string preview, string time)
        {
            var item = Conversations.FirstOrDefault(c => c.ThreadId == threadId);
            if (item == null) return;

            item.Preview = preview;
            item.Time = time;

            int index = Conversations.IndexOf(item);
            if (index <= 0) return;

            // Moving a row can drop the list selection; put it back without reopening the chat.
            bool wasOpen = ReferenceEquals(_current, item);
            _suppressSelection = true;
            try
            {
                Conversations.Move(index, 0);
                if (wasOpen) ConversationList.SelectedItem = item;
            }
            finally { _suppressSelection = false; }
        }

        public void SetConversations(IEnumerable<ConversationItem> items)
        {
            Conversations.Clear();
            foreach (var i in items) Conversations.Add(i);
        }

        /// <summary>Text shown in the list area while it is empty (loading, error, nothing yet).</summary>
        public void SetMessage(string text) => EmptyText.Text = text;

        private void UpdateEmpty() =>
            EmptyText.Visibility = Conversations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        private void Nav_Click(object sender, RoutedEventArgs e)
        {
            var selected = (Brush)Resources["RailSelectedBrush"];
            var none = new SolidColorBrush(Colors.Transparent);
            ChatNav.Background = ReferenceEquals(sender, ChatNav) ? selected : none;
            ContactsNav.Background = ReferenceEquals(sender, ContactsNav) ? selected : none;
        }

        private void Tab_Click(object sender, RoutedEventArgs e)
        {
            bool priority = ReferenceEquals(sender, TabPriority);
            TabPriority.FontWeight = priority ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            TabOther.FontWeight = priority ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold;
            TabPriority.Opacity = priority ? 1.0 : 0.6;
            TabOther.Opacity = priority ? 0.6 : 1.0;
        }
    }
}
