using System;
using System.Text;
using System.Text.Json;

namespace zalo10_for_windows.Core
{
    /// <summary>A decoded payload. Dispose it to release the underlying JSON document.</summary>
    public sealed class WsPayload : IDisposable
    {
        private readonly JsonDocument _doc;
        public JsonElement Data { get; }

        public WsPayload(JsonDocument doc, JsonElement data)
        {
            _doc = doc;
            Data = data;
        }

        public void Dispose() => _doc.Dispose();
    }

    /// <summary>
    /// Wire format of Zalo's WebSocket. Port of handleWsMessage / sendWsRequest / decodeWsEnvelope (Zalo10).
    ///
    /// Every message is a binary frame:  [version=1][cmd lo][cmd hi][subCmd][UTF-8 JSON]
    /// Typical JSON: {"encrypt":N,"data":"..."} where N says how "data" is protected:
    ///   0  plain JSON
    ///   1  AES-CBC with the session secretKey (same as the HTTP API)
    ///   2  percent-encoded base64, AES-GCM with the handshake cipher key, then gzip
    ///   3  base64, AES-GCM with the handshake cipher key
    /// </summary>
    public static class ZaloWsCodec
    {
        public static byte[] BuildFrame(int cmd, int subCmd, string json)
        {
            var body = Encoding.UTF8.GetBytes(json);
            var frame = new byte[4 + body.Length];
            frame[0] = 1;
            frame[1] = (byte)(cmd & 0xFF);
            frame[2] = (byte)((cmd >> 8) & 0xFF);
            frame[3] = (byte)(subCmd & 0xFF);
            Buffer.BlockCopy(body, 0, frame, 4, body.Length);
            return frame;
        }

        public static bool TryParseFrame(byte[] payload, out int cmd, out int subCmd, out ReadOnlyMemory<byte> json)
        {
            cmd = subCmd = 0;
            json = default;
            if (payload.Length < 4) return false;
            cmd = payload[1] | (payload[2] << 8);
            subCmd = payload[3];
            json = new ReadOnlyMemory<byte>(payload, 4, payload.Length - 4);
            return true;
        }

        /// <summary>
        /// Opens the envelope and returns the inner "data" object (or the whole decoded JSON when it has no
        /// "data" object). Returns null when the payload cannot be decoded.
        /// </summary>
        public static WsPayload? Decode(ReadOnlyMemory<byte> json, byte[]? cipherKey, string secretKey)
        {
            JsonDocument outer;
            try { outer = JsonDocument.Parse(json); }
            catch (JsonException) { return null; }

            var root = outer.RootElement;
            int encType = 1;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("encrypt", out var e))
            {
                if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n)) encType = n;
                else if (e.ValueKind == JsonValueKind.String && int.TryParse(e.GetString(), out var m)) encType = m;
            }

            // 0: not encrypted
            if (encType == 0)
            {
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
                    return new WsPayload(outer, d);
                return new WsPayload(outer, root);
            }

            string dataStr = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var ds) && ds.ValueKind == JsonValueKind.String
                ? ds.GetString() ?? "" : "";

            // 2 and 3: AES-GCM with the cipher key from the handshake (2 also gzip)
            if ((encType == 2 || encType == 3) && cipherKey != null && cipherKey.Length > 0)
            {
                outer.Dispose();
                byte[] cipher;
                try
                {
                    string b64 = encType == 2 ? Uri.UnescapeDataString(dataStr) : dataStr;
                    cipher = FromBase64(b64);
                }
                catch (FormatException) { return null; }

                var plain = ZaloCrypto.AesGcmDecrypt(cipherKey, cipher);
                if (plain == null || plain.Length == 0) return null;

                // For type 2 the plaintext is gzip; if it is not, fall back to treating it as JSON already.
                var text = encType == 2 ? (ZaloCrypto.Gunzip(plain) ?? plain) : plain;
                return ParseUnwrapped(text);
            }

            // 1 (and anything else): AES-CBC with the session secretKey, then with the cipher key as a fallback
            string? dec = ZaloCrypto.AesDecryptBase64(secretKey, dataStr);
            if ((string.IsNullOrEmpty(dec) || dec.Trim() == "{}") && cipherKey != null && cipherKey.Length > 0)
                dec = ZaloCrypto.AesDecryptBase64(Convert.ToBase64String(cipherKey), dataStr);
            outer.Dispose();
            if (string.IsNullOrEmpty(dec)) return null;
            return ParseUnwrapped(Encoding.UTF8.GetBytes(dec));
        }

        private static WsPayload? ParseUnwrapped(byte[] text)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(text); }
            catch (JsonException) { return null; }

            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
                return new WsPayload(doc, d);
            return new WsPayload(doc, r);
        }

        private static byte[] FromBase64(string s)
        {
            var t = s.Trim().Replace('-', '+').Replace('_', '/').TrimEnd('=');
            switch (t.Length % 4)
            {
                case 2: t += "=="; break;
                case 3: t += "="; break;
            }
            return Convert.FromBase64String(t);
        }
    }
}
