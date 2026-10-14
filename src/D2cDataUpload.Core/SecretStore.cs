using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace D2cDataUpload.Core;

/// <summary>
/// Passwörter nie im Klartext speichern.
/// Windows: DPAPI (CryptProtectData, Bereich "aktueller Benutzer") – nur derselbe Windows-Benutzer
/// auf demselben PC kann den Wert wieder lesen. Kein NuGet-Paket nötig (direkter Aufruf crypt32.dll).
/// Andere Systeme (nur Entwicklung/Tests): AES-GCM mit zufälligem Schlüssel in einer Datei mit Rechten 600.
/// Gespeichertes Format: "dpapi:BASE64" bzw. "aes:BASE64". Leerer Wert = kein Passwort.
/// </summary>
public static class SecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(AppSettings.AppFolderName + "|v1");

    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var data = Encoding.UTF8.GetBytes(plain);
        try
        {
            return OperatingSystem.IsWindows()
                ? "dpapi:" + Convert.ToBase64String(Dpapi(data, protect: true))
                : "aes:" + Convert.ToBase64String(AesProtect(data));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    /// <summary>Entschlüsselt; bei fremdem Benutzer/PC oder defektem Wert: "" (Passwort neu eingeben).</summary>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try
        {
            if (stored.StartsWith("dpapi:", StringComparison.Ordinal) && OperatingSystem.IsWindows())
                return Encoding.UTF8.GetString(Dpapi(Convert.FromBase64String(stored[6..]), protect: false));
            if (stored.StartsWith("aes:", StringComparison.Ordinal))
                return Encoding.UTF8.GetString(AesUnprotect(Convert.FromBase64String(stored[4..])));
        }
        catch (Exception)
        {
            // nicht entschlüsselbar
        }
        return "";
    }

    public static bool IsProtected(string? stored) =>
        string.IsNullOrEmpty(stored) || stored.StartsWith("dpapi:", StringComparison.Ordinal) || stored.StartsWith("aes:", StringComparison.Ordinal);

    // --- DPAPI -----------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    private const int CryptprotectUiForbidden = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private static byte[] Dpapi(byte[] input, bool protect)
    {
        var inBlob = new DataBlob();
        var entBlob = new DataBlob();
        var outBlob = new DataBlob();
        try
        {
            inBlob.pbData = Marshal.AllocHGlobal(input.Length);
            inBlob.cbData = input.Length;
            Marshal.Copy(input, 0, inBlob.pbData, input.Length);
            entBlob.pbData = Marshal.AllocHGlobal(Entropy.Length);
            entBlob.cbData = Entropy.Length;
            Marshal.Copy(Entropy, 0, entBlob.pbData, Entropy.Length);

            var ok = protect
                ? CryptProtectData(ref inBlob, AppSettings.AppFolderName, ref entBlob, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entBlob, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, ref outBlob);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());

            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            if (inBlob.pbData != IntPtr.Zero)
            {
                for (int i = 0; i < inBlob.cbData; i++) Marshal.WriteByte(inBlob.pbData, i, 0);
                Marshal.FreeHGlobal(inBlob.pbData);
            }
            if (entBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(entBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    // --- Fallback ausserhalb Windows (Entwicklung/Tests) ----------------
    private static byte[] Key()
    {
        var path = Path.Combine(AppSettings.Folder, "secret.key");
        if (!File.Exists(path))
        {
            File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(32));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return File.ReadAllBytes(path);
    }

    private static byte[] AesProtect(byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Key(), 16);
        aes.Encrypt(nonce, plain, cipher, tag, Entropy);
        return [.. nonce, .. tag, .. cipher];
    }

    private static byte[] AesUnprotect(byte[] blob)
    {
        var nonce = blob[..12];
        var tag = blob[12..28];
        var cipher = blob[28..];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(Key(), 16);
        aes.Decrypt(nonce, cipher, tag, plain, Entropy);
        return plain;
    }
}
