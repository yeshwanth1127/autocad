using System;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace autocad_final.Licensing
{
    /// <summary>
    /// 7-day evaluation: first-run start time is stored (encoded) in HKCU, with a hidden-file fallback.
    /// </summary>
    internal static class TrialExpiry
    {
        private const int TrialDays = 7;
        private const string RegistryPath = @"Software\autocad-final\Runtime";
        private const string RegistryValueName = "d";

        private static readonly byte[] _xorKey = BuildXorKey();
        private static bool? _cachedExpired;

        internal static string ExpiredUserMessage =>
            "[autocad-final] This trial has expired (7 days from first use). Remove and reinstall does not reset the period.";

        internal static bool IsExpired()
        {
            if (_cachedExpired.HasValue)
                return _cachedExpired.Value;

            try
            {
                DateTime startUtc = GetOrCreateStartUtc();
                var end = startUtc.AddDays(TrialDays);
                bool expired = DateTime.UtcNow > end;
                _cachedExpired = expired;
                return expired;
            }
            catch
            {
                _cachedExpired = true;
                return true;
            }
        }

        private static DateTime GetOrCreateStartUtc()
        {
            DateTime? fromReg = TryReadRegistryStart();
            DateTime? fromFile = TryReadFileStart();

            if (!fromReg.HasValue && !fromFile.HasValue)
            {
                var start = DateTime.UtcNow;
                string encoded = TrialPayloadCodec.Encode(start);
                TryWriteRegistry(encoded);
                TryWriteHiddenFile(encoded);
                return start;
            }

            if (!fromReg.HasValue)
                return fromFile.Value;
            if (!fromFile.HasValue)
                return fromReg.Value;

            return fromReg.Value < fromFile.Value ? fromReg.Value : fromFile.Value;
        }

        private static DateTime? TryReadRegistryStart()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false))
                {
                    var s = key?.GetValue(RegistryValueName) as string;
                    if (string.IsNullOrEmpty(s))
                        return null;
                    return TrialPayloadCodec.TryDecode(s, out var utc) ? utc : (DateTime?)null;
                }
            }
            catch
            {
                return null;
            }
        }

        private static void TryWriteRegistry(string encoded)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true))
                {
                    key?.SetValue(RegistryValueName, encoded, RegistryValueKind.String);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (SecurityException) { }
            catch (IOException) { }
        }

        /// <summary>Primary: %LocalAppData%\.ac_plg\s.dat (hidden dir).</summary>
        private static string GetHiddenStoreFilePathPrimary()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(EnsureHiddenDir(Path.Combine(local, ".ac_plg")), "s.dat");
        }

        /// <summary>Secondary fallback (e.g. if LocalAppData is blocked): %AppData%\.ac_plg\s.dat.</summary>
        private static string GetHiddenStoreFilePathSecondary()
        {
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(EnsureHiddenDir(Path.Combine(roaming, ".ac_plg")), "s.dat");
        }

        private static string EnsureHiddenDir(string dir)
        {
            try
            {
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    new DirectoryInfo(dir).Attributes |= FileAttributes.Hidden;
                }
            }
            catch
            {
                // still use path for write attempt
            }

            return dir;
        }

        private static DateTime? TryReadFileStart()
        {
            DateTime? a = TryReadOneFile(GetHiddenStoreFilePathPrimary());
            DateTime? b = TryReadOneFile(GetHiddenStoreFilePathSecondary());
            if (!a.HasValue)
                return b;
            if (!b.HasValue)
                return a;
            return a.Value < b.Value ? a : b;
        }

        private static DateTime? TryReadOneFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return null;
                var text = File.ReadAllText(path, Encoding.UTF8).Trim();
                if (string.IsNullOrEmpty(text))
                    return null;
                return TrialPayloadCodec.TryDecode(text, out var utc) ? utc : (DateTime?)null;
            }
            catch
            {
                return null;
            }
        }

        private static void TryWriteHiddenFile(string encoded)
        {
            TryWriteOneHiddenFile(GetHiddenStoreFilePathPrimary(), encoded);
            TryWriteOneHiddenFile(GetHiddenStoreFilePathSecondary(), encoded);
        }

        private static void TryWriteOneHiddenFile(string path, string encoded)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(path, encoded, Encoding.UTF8);
                try
                {
                    new FileInfo(path).Attributes |= FileAttributes.Hidden;
                    if (!string.IsNullOrEmpty(dir))
                        new DirectoryInfo(dir).Attributes |= FileAttributes.Hidden;
                }
                catch
                {
                    // ignore attribute failures
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (SecurityException) { }
            catch (IOException) { }
        }

        private static byte[] BuildXorKey()
        {
            const string salt = "autocad-final|trial|EA04F805-C87E-4C73-AFFF-9B94F1230C30";
            using (var sha = SHA256.Create())
                return sha.ComputeHash(Encoding.UTF8.GetBytes(salt));
        }

        private static class TrialPayloadCodec
        {
            internal static string Encode(DateTime startUtc)
            {
                long ticks = startUtc.Ticks;
                var buf = BitConverter.GetBytes(ticks);
                XorBuffer(buf);
                return Convert.ToBase64String(buf);
            }

            internal static bool TryDecode(string encoded, out DateTime startUtc)
            {
                startUtc = default;
                try
                {
                    var buf = Convert.FromBase64String(encoded.Trim());
                    if (buf.Length != 8)
                        return false;
                    XorBuffer(buf);
                    long ticks = BitConverter.ToInt64(buf, 0);
                    if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                        return false;
                    startUtc = new DateTime(ticks, DateTimeKind.Utc);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            private static void XorBuffer(byte[] buf)
            {
                for (int i = 0; i < buf.Length; i++)
                    buf[i] ^= _xorKey[i % _xorKey.Length];
            }
        }
    }
}
