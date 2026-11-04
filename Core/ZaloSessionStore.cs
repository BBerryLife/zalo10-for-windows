using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Security.Cryptography.DataProtection;
using Windows.Storage;

namespace zalo10_for_windows.Core
{
    public sealed record SavedSession(string UserAgent, string Imei, string Uid, string DisplayName,
                                      List<CookieData> Cookies);

    /// <summary>
    /// Keeps the login between runs so the QR code is only needed once.
    ///
    /// What is stored: cookies (these ARE the credentials), IMEI, user agent, uid, display name.
    /// What is NOT stored: the secretKey. It is renewed on every start through getLoginInfo,
    /// exactly like Zalo10's refreshSessionKey.
    ///
    /// The file is encrypted with the Windows DataProtection API bound to the current Windows
    /// user, so it is useless if copied to another account or machine. It lives in the app's
    /// private LocalState folder, never in the repository.
    ///
    /// JSON is written by hand (no reflection) so it keeps working when the app is trimmed in Release.
    /// </summary>
    public static class ZaloSessionStore
    {
        private const string FileName = "session.bin";

        public static async Task SaveAsync(ZaloHttp http, ZaloSession s)
        {
            try
            {
                var ms = new MemoryStream();
                using (var w = new Utf8JsonWriter(ms))
                {
                    w.WriteStartObject();
                    w.WriteString("ua", s.UserAgent);
                    w.WriteString("imei", s.Imei);
                    w.WriteString("uid", s.Uid);
                    w.WriteString("name", s.DisplayName);
                    w.WriteStartArray("cookies");
                    foreach (var c in http.ExportCookies())
                    {
                        w.WriteStartObject();
                        w.WriteString("n", c.Name);
                        w.WriteString("v", c.Value);
                        w.WriteString("d", c.Domain);
                        w.WriteString("p", c.Path);
                        w.WriteNumber("e", c.ExpiresUnix);
                        w.WriteBoolean("s", c.Secure);
                        w.WriteBoolean("h", c.HttpOnly);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }

                var provider = new DataProtectionProvider("LOCAL=user");
                var protectedBuf = await provider.ProtectAsync(ms.ToArray().AsBuffer());
                var file = await ApplicationData.Current.LocalFolder
                    .CreateFileAsync(FileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBufferAsync(file, protectedBuf);
                Log("Da luu phien (ma hoa theo tai khoan Windows)");
            }
            catch (Exception ex) { Log("Luu phien that bai: " + ex.Message); }
        }

        public static async Task<SavedSession?> LoadAsync()
        {
            try
            {
                if (await ApplicationData.Current.LocalFolder.TryGetItemAsync(FileName) is not StorageFile file)
                {
                    Log("Chua co phien luu");
                    return null;
                }

                var buf = await FileIO.ReadBufferAsync(file);
                var plain = await new DataProtectionProvider().UnprotectAsync(buf);

                using var doc = JsonDocument.Parse(plain.ToArray());
                var r = doc.RootElement;
                string ua = Str(r, "ua"), imei = Str(r, "imei");
                if (ua.Length == 0 || imei.Length == 0) { Log("Phien luu khong day du"); return null; }

                var cookies = new List<CookieData>();
                if (r.TryGetProperty("cookies", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in arr.EnumerateArray())
                        cookies.Add(new CookieData(Str(c, "n"), Str(c, "v"), Str(c, "d"), Str(c, "p"),
                            c.TryGetProperty("e", out var e) && e.TryGetInt64(out var ex) ? ex : 0,
                            Bool(c, "s"), Bool(c, "h")));
                }

                Log($"Da doc phien luu: {cookies.Count} cookie");
                return new SavedSession(ua, imei, Str(r, "uid"), Str(r, "name"), cookies);
            }
            catch (Exception ex)
            {
                // Corrupt file, or it was written by another Windows user: treat as "no session".
                Log("Doc phien luu that bai: " + ex.Message);
                return null;
            }
        }

        public static async Task DeleteAsync()
        {
            try
            {
                if (await ApplicationData.Current.LocalFolder.TryGetItemAsync(FileName) is StorageFile file)
                    await file.DeleteAsync();
                Log("Da xoa phien luu");
            }
            catch (Exception ex) { Log("Xoa phien luu that bai: " + ex.Message); }
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        private static bool Bool(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        private static void Log(string msg) => Debug.WriteLine("[Zalo10][Store] " + msg);
    }
}
