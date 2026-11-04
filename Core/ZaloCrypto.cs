using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace zalo10_for_windows.Core
{
    public sealed record EncryptedParams(string Zcid, string ZcidExt, string EncVer,
                                         string EncryptedData, string EncryptKey);

    /// <summary>
    /// Port of core/ZaloService_Crypto.cpp (Zalo10).
    /// All AES here is CBC with an all-zero IV and PKCS#7 padding, exactly like the original,
    /// because that is what the Zalo web protocol expects.
    /// </summary>
    public static class ZaloCrypto
    {
        public const int ApiType = 30;
        public const int ApiVersion = 671;
        public const string AesFixedKey = "3FC4F0D2AB50057BCE0D90D9187A22B1";

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>Base64 decoder that tolerates missing padding, whitespace and url-safe characters.</summary>
        private static byte[] FromBase64Lenient(string s)
        {
            var t = new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray())
                        .Replace('-', '+').Replace('_', '/').TrimEnd('=');
            switch (t.Length % 4)
            {
                case 2: t += "=="; break;
                case 3: t += "="; break;
            }
            return Convert.FromBase64String(t);
        }

        // ---- key resolution -----------------------------------------------------------------------

        /// <summary>UTF-8 bytes of the key string, zero-padded / truncated to 32 bytes.</summary>
        private static byte[] KeyUtf8(string key)
        {
            var k = new byte[32];
            var src = Encoding.UTF8.GetBytes(key);
            Array.Copy(src, k, Math.Min(src.Length, 32));
            return k;
        }

        /// <summary>Base64-decoded key, zero-padded to 16, 24 or 32 bytes (used with the session secretKey).</summary>
        private static byte[] KeyBase64(string key)
        {
            var d = FromBase64Lenient(key);
            int size = d.Length <= 16 ? 16 : d.Length <= 24 ? 24 : 32;
            var k = new byte[size];
            Array.Copy(d, k, Math.Min(d.Length, size));
            return k;
        }

        // ---- AES primitives -----------------------------------------------------------------------

        private static byte[] Encrypt(byte[] key, string plain)
        {
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = key;
            aes.IV = new byte[16];
            var data = Encoding.UTF8.GetBytes(plain);
            return aes.CreateEncryptor().TransformFinalBlock(data, 0, data.Length);
        }

        private static string? Decrypt(byte[] key, string cipherB64)
        {
            try
            {
                // Zalo sometimes percent-encodes the base64 payload.
                var cleaned = Uri.UnescapeDataString(cipherB64).Replace("\r", "").Replace("\n", "").Trim();
                var cipher = FromBase64Lenient(cleaned);
                if (cipher.Length == 0 || cipher.Length % 16 != 0) return null;

                using var aes = Aes.Create();
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None; // strip padding by hand, same lenient rule as Zalo10
                aes.Key = key;
                aes.IV = new byte[16];
                var plain = aes.CreateDecryptor().TransformFinalBlock(cipher, 0, cipher.Length);

                int pad = plain[^1];
                int len = pad > 0 && pad <= 16 ? plain.Length - pad : plain.Length;
                return Encoding.UTF8.GetString(plain, 0, len);
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
            {
                return null;
            }
        }

        public static string AesEncryptHex(string keyStr, string plain) =>
            Convert.ToHexString(Encrypt(KeyUtf8(keyStr), plain)); // uppercase hex

        public static string AesEncryptBase64Key256(string keyStr, string plain) =>
            Convert.ToBase64String(Encrypt(KeyUtf8(keyStr), plain));

        public static string? AesDecryptBase64Key256(string keyStr, string cipherB64) =>
            Decrypt(KeyUtf8(keyStr), cipherB64);

        /// <summary>For API calls after login: key = base64-decoded session secretKey.</summary>
        public static string AesEncryptBase64(string secretKeyB64, string plain) =>
            Convert.ToBase64String(Encrypt(KeyBase64(secretKeyB64), plain));

        public static string? AesDecryptBase64(string secretKeyB64, string cipherB64)
        {
            try { return Decrypt(KeyBase64(secretKeyB64), cipherB64); }
            catch (FormatException) { return null; }
        }

        // ---- hashing / helpers ---------------------------------------------------------------------

        public static string Md5Hex(string input) =>
            Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

        public static string RandomHex(int len) =>
            Convert.ToHexString(RandomNumberGenerator.GetBytes((len + 1) / 2)).ToLowerInvariant()[..len];

        public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /// <summary>Port of generateIMEI(): uuid-v4 + "-" + md5(userAgent).</summary>
        public static string GenerateImei(string userAgent) =>
            Guid.NewGuid().ToString().ToLowerInvariant() + "-" + Md5Hex(userAgent);

        public static string ToJson(IDictionary<string, string> map) =>
            JsonSerializer.Serialize(new SortedDictionary<string, string>(map, StringComparer.Ordinal), JsonOpts);

        /// <summary>
        /// JSON object with keys sorted, values written as their real type (string / int / long / bool).
        /// Zalo10's QVariantMap serialisation produced numbers for numeric params; mirror that.
        /// </summary>
        public static string ToJsonTyped(IDictionary<string, object> map)
        {
            using var ms = new System.IO.MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartObject();
                foreach (var kv in map.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    switch (kv.Value)
                    {
                        case int i: w.WriteNumber(kv.Key, i); break;
                        case long l: w.WriteNumber(kv.Key, l); break;
                        case bool b: w.WriteBoolean(kv.Key, b); break;
                        default: w.WriteString(kv.Key, kv.Value?.ToString() ?? ""); break;
                    }
                }
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        // ---- request encryption + signing ----------------------------------------------------------

        /// <summary>
        /// Port of buildEncryptedParams: derives a one-off AES key from zcid_ext and zcid,
        /// then encrypts the JSON payload with it. The returned EncryptKey is also needed
        /// to decrypt the server's reply.
        /// </summary>
        public static EncryptedParams BuildEncryptedParams(string imei, IDictionary<string, string> data)
        {
            const string encVer = "v2";

            string firstLaunch = NowMs().ToString();
            string zcid = AesEncryptHex(AesFixedKey, $"{ApiType},{imei},{firstLaunch}");
            string zcidExt = RandomHex(8);

            string n = Md5Hex(zcidExt).ToUpperInvariant();
            var nEven = n.Where((_, i) => i % 2 == 0).Take(8);
            var zEven = zcid.Where((_, i) => i % 2 == 0).Take(12);
            var zOdd = zcid.Where((_, i) => i % 2 == 1).Reverse().Take(12);

            string encryptKey = new string(nEven.ToArray()) + new string(zEven.ToArray()) + new string(zOdd.ToArray());
            string encrypted = AesEncryptBase64Key256(encryptKey, ToJson(data));

            return new EncryptedParams(zcid, zcidExt, encVer, encrypted, encryptKey);
        }

        /// <summary>Port of buildSignKey: md5("zsecure" + type + all values ordered by key).</summary>
        public static string BuildSignKey(string type, IDictionary<string, string> p)
        {
            var sb = new StringBuilder("zsecure").Append(type);
            foreach (var key in p.Keys.OrderBy(k => k, StringComparer.Ordinal)) sb.Append(p[key]);
            return Md5Hex(sb.ToString());
        }

        /// <summary>Port of buildRawUrl: percent-encode every key and value.</summary>
        public static string BuildUrl(string baseUrl, IDictionary<string, string> p)
        {
            var parts = p.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                         .Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value));
            return baseUrl + "?" + string.Join("&", parts);
        }
    }
}
