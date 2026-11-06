using System;
using System.Globalization;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace zalo10_for_windows.Views
{
    /// <summary>Lower-case, accent-free text for searching and for the A-Z section letters.</summary>
    public static class TextFold
    {
        public static string Fold(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s.Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                char c = char.ToLowerInvariant(ch);
                sb.Append(c == 'đ' ? 'd' : c);
            }
            return sb.ToString();
        }

        /// <summary>"Bảo Đỗ" gives "B", "Đức" gives "D", anything not a plain letter gives "#".</summary>
        public static string Letter(string name)
        {
            string f = Fold(name.TrimStart());
            return f.Length > 0 && f[0] is >= 'a' and <= 'z' ? char.ToUpperInvariant(f[0]).ToString() : "#";
        }
    }

    /// <summary>One line of the friends or groups list: a letter heading or a contact.</summary>
    public sealed class ContactRow
    {
        public bool IsHeader { get; init; }
        public string Letter { get; init; } = "";
        public string Id { get; init; } = "";
        public string Title { get; init; } = "";
        public string Subtitle { get; init; } = "";
        public bool IsGroup { get; init; }
        public string AvatarUrl { get; init; } = "";

        public Visibility HeaderVisibility => IsHeader ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RowVisibility => IsHeader ? Visibility.Collapsed : Visibility.Visible;
        public Visibility SubtitleVisibility => Subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        private ImageSource? _picture;
        private bool _tried;

        /// <summary>Created when the list first shows this row. Groups without an avatar get Zalo10's default group picture.</summary>
        public ImageSource? Picture
        {
            get
            {
                if (_tried) return _picture;
                _tried = true;
                if (Uri.TryCreate(AvatarUrl, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                    _picture = new BitmapImage(uri) { DecodePixelWidth = 112 };
                else if (IsGroup)
                    _picture = new BitmapImage(new Uri("ms-appx:///Assets/Icons/default_group.png")) { DecodePixelWidth = 112 };
                return _picture;
            }
        }
    }
}
