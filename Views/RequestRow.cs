using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace zalo10_for_windows.Views
{
    public enum RequestKind { Received, Sent, Suggestion }

    /// <summary>One card on the friend-request page.</summary>
    public sealed class RequestRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public RequestKind Kind { get; init; }
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string AvatarUrl { get; init; } = "";
        public string Subtitle { get; init; } = "";
        public string Message { get; init; } = "";

        public Visibility MessageVisibility => Message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility SubtitleVisibility => Subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        private bool _busy;
        private string _error = "";

        public bool CanAct => !_busy;
        public string Error => _error;
        public Visibility ErrorVisibility => _error.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        public void SetBusy(bool busy, string? error)
        {
            _busy = busy;
            _error = error ?? "";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanAct)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Error)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ErrorVisibility)));
        }

        private ImageSource? _picture;
        private bool _tried;

        public ImageSource? Picture
        {
            get
            {
                if (_tried) return _picture;
                _tried = true;
                if (Uri.TryCreate(AvatarUrl, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                    _picture = new BitmapImage(uri) { DecodePixelWidth = 96 };
                return _picture;
            }
        }
    }
}
