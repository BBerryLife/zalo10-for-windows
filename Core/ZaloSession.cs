using System.Collections.Generic;

namespace zalo10_for_windows.Core
{
    /// <summary>
    /// Everything we learn from logging in. Cookies live in ZaloHttp.Cookies.
    /// SecretKey is the AES key for every API call afterwards: treat it like a password
    /// (never log it, and encrypt it with DPAPI when we persist the session).
    /// </summary>
    public sealed class ZaloSession
    {
        public string UserAgent { get; }
        public string Imei { get; }
        public string Language { get; } = "vi";

        public string Uid { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string SecretKey { get; set; } = "";

        /// <summary>chat, group, profile, group_poll, group_board, friend, file, quick_message, reaction, conversation.</summary>
        public Dictionary<string, string> ServiceUrls { get; } = new();

        /// <summary>WebSocket endpoints (zpw_ws), at most 10.</summary>
        public List<string> WebSocketUrls { get; } = new();

        public bool IsLoggedIn => SecretKey.Length > 0 && Uid.Length > 0;

        /// <param name="imei">Pass the saved IMEI when restoring a session; omit for a brand-new login.</param>
        public ZaloSession(string userAgent, string? imei = null)
        {
            UserAgent = userAgent;
            Imei = string.IsNullOrEmpty(imei) ? ZaloCrypto.GenerateImei(userAgent) : imei;
        }
    }
}
