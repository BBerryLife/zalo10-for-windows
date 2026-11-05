using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace zalo10_for_windows.Views
{
    /// <summary>One chat bubble. Plain class with normal setters, as XAML code generation needs.</summary>
    public sealed class MessageItem
    {
        // Created lazily on the UI thread (first MessageItem is built there).
        private static Brush? _mine, _other;
        private static Brush MineBrush => _mine ??= new SolidColorBrush(ColorHelper.FromArgb(255, 213, 232, 255));
        private static Brush OtherBrush => _other ??= new SolidColorBrush(ColorHelper.FromArgb(255, 255, 255, 255));

        public string MsgId { get; set; } = "";
        public string Text { get; set; } = "";
        public string Sender { get; set; } = "";
        public string TimeText { get; set; } = "";
        public bool IsMine { get; set; }

        /// <summary>Group chats show who wrote each incoming bubble.</summary>
        public bool ShowSender { get; set; }

        public HorizontalAlignment Align => IsMine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        public Brush BubbleBrush => IsMine ? MineBrush : OtherBrush;
        public Visibility SenderVisibility =>
            !IsMine && ShowSender && Sender.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>"14:05" for today, "14:05 03/11" for earlier days.</summary>
        public static string FormatTime(long unixMs)
        {
            if (unixMs <= 0) return "";
            var t = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime();
            return t.Date == DateTime.Today ? t.ToString("HH:mm") : t.ToString("HH:mm dd/MM");
        }
    }
}
