using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace zalo10_for_windows.Views
{
    /// <summary>Header + message bubbles + input box for one conversation at a time.</summary>
    public sealed partial class ChatView : UserControl
    {
        public ObservableCollection<MessageItem> Messages { get; } = new();

        /// <summary>The user pressed Enter / Send with this text.</summary>
        public event Action<string>? SendRequested;

        public ChatView()
        {
            InitializeComponent();
        }

        /// <summary>Switch to a conversation: new header, empty list, "loading" text.</summary>
        public void Begin(ConversationItem c)
        {
            TitleText.Text = c.Title;
            SubtitleText.Text = c.IsGroup ? "Nhóm" : "Bạn bè";
            HeaderAvatar.DisplayName = c.Title;
            HeaderAvatar.ProfilePicture = c.Picture;
            InputBox.PlaceholderText = $"Nhập tin nhắn tới {c.Title}";
            InputBox.Text = "";

            Messages.Clear();
            SetNote(null);
            SetStatus(null);
            InfoText.Text = "Đang tải tin nhắn...";
            InfoText.Visibility = Visibility.Visible;
        }

        /// <param name="emptyText">Shown in the middle when there is nothing to display (empty or error).</param>
        /// <param name="note">Optional yellow banner under the header.</param>
        public void SetMessages(IEnumerable<MessageItem> items, string? emptyText, string? note)
        {
            Messages.Clear();
            foreach (var m in items) Messages.Add(m);

            SetNote(note);
            InfoText.Text = emptyText ?? "Chưa có tin nhắn";
            InfoText.Visibility = Messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ScrollToEnd();
        }

        public void Add(MessageItem item)
        {
            Messages.Add(item);
            InfoText.Visibility = Visibility.Collapsed;
            ScrollToEnd();
        }

        /// <summary>Swap a bubble in place (a recalled message changes its text).</summary>
        public void Replace(string msgId, MessageItem item)
        {
            for (int i = 0; i < Messages.Count; i++)
                if (Messages[i].MsgId == msgId) { Messages[i] = item; return; }
        }

        public void SetStatus(string? text)
        {
            StatusBar.Text = text ?? "";
            StatusBar.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>Put the text back after a failed send, unless the user already typed something new.</summary>
        public void RestoreInput(string text)
        {
            if (InputBox.Text.Length == 0) InputBox.Text = text;
        }

        private void SetNote(string? note)
        {
            NoteText.Text = note ?? "";
            NoteBar.Visibility = string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ScrollToEnd()
        {
            if (Messages.Count == 0) return;
            var last = Messages[Messages.Count - 1];
            // Wait one layout pass so the new rows exist before scrolling to them.
            DispatcherQueue.TryEnqueue(() =>
            {
                MessageList.UpdateLayout();
                MessageList.ScrollIntoView(last);
            });
        }

        // ---- input ----------------------------------------------------------------------------------
        private void Send_Click(object sender, RoutedEventArgs e) => Send();

        /// <summary>Enter sends, Shift+Enter inserts a new line.</summary>
        private void InputBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter) return;
            bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                             .HasFlag(CoreVirtualKeyStates.Down);
            if (shift) return;
            e.Handled = true;
            Send();
        }

        private void Send()
        {
            string text = InputBox.Text.Trim();
            if (text.Length == 0) return;
            InputBox.Text = "";
            SetStatus(null);
            SendRequested?.Invoke(text);
        }
    }
}
