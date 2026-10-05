using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using static KidGuard.Win32.NativeMethods;

namespace KidGuard.Win32;

/// <summary>Права учётных записей в локальной политике безопасности (LSA), раздел 5.12.</summary>
public static class LsaRights
{
    public const string TimeZone = "SeTimeZonePrivilege";
    public const string SystemTime = "SeSystemtimePrivilege";

    public static SecurityIdentifier BuiltinUsers { get; } = new(WellKnownSidType.BuiltinUsersSid, null);

    public static IReadOnlyList<string> Get(SecurityIdentifier sid)
    {
        using var policy = Open();
        var status = LsaEnumerateAccountRights(policy.Handle, ToBytes(sid), out var buffer, out var count);
        if (status == StatusObjectNameNotFound) return [];
        Check(status);
        try
        {
            var result = new List<string>((int)count);
            var size = Marshal.SizeOf<LSA_UNICODE_STRING>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<LSA_UNICODE_STRING>(buffer + i * size);
                result.Add(Marshal.PtrToStringUni(item.Buffer, item.Length / 2));
            }
            return result;
        }
        finally
        {
            LsaFreeMemory(buffer);
        }
    }

    public static bool Has(SecurityIdentifier sid, string right) =>
        Get(sid).Contains(right, StringComparer.OrdinalIgnoreCase);

    public static void Add(SecurityIdentifier sid, string right)
    {
        using var policy = Open();
        WithRight(right, rights => Check(LsaAddAccountRights(policy.Handle, ToBytes(sid), rights, 1)));
    }

    public static void Remove(SecurityIdentifier sid, string right)
    {
        using var policy = Open();
        WithRight(right, rights =>
        {
            var status = LsaRemoveAccountRights(policy.Handle, ToBytes(sid), false, rights, 1);
            if (status != StatusObjectNameNotFound) Check(status);
        });
    }

    static void WithRight(string right, Action<LSA_UNICODE_STRING[]> action)
    {
        var buffer = Marshal.StringToHGlobalUni(right);
        try
        {
            var value = new LSA_UNICODE_STRING
            {
                Buffer = buffer,
                Length = (ushort)(right.Length * 2),
                MaximumLength = (ushort)((right.Length + 1) * 2),
            };
            action([value]);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    static PolicyHandle Open()
    {
        var attributes = new LSA_OBJECT_ATTRIBUTES { Length = Marshal.SizeOf<LSA_OBJECT_ATTRIBUTES>() };
        Check(LsaOpenPolicy(IntPtr.Zero, ref attributes, PolicyAllAccess, out var handle));
        return new PolicyHandle(handle);
    }

    static byte[] ToBytes(SecurityIdentifier sid)
    {
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        return bytes;
    }

    static void Check(uint status)
    {
        if (status != 0) throw new Win32Exception(LsaNtStatusToWinError(status));
    }

    sealed class PolicyHandle(IntPtr handle) : IDisposable
    {
        public IntPtr Handle => handle;

        public void Dispose() => LsaClose(handle);
    }
}

/// <summary>Шифрование токена бота DPAPI в контексте машины (раздел 10.2).</summary>
public static class TokenProtector
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("KidGuard.BotToken.v1");

    public static string Protect(string token) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.LocalMachine));

    public static string Unprotect(string protectedToken) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedToken), Entropy, DataProtectionScope.LocalMachine));
}

/// <summary>Текущий часовой пояс Windows (служба его не использует, только отслеживает изменения).</summary>
public static class WindowsTimeZone
{
    const string KeyPath = @"SYSTEM\CurrentControlSet\Control\TimeZoneInformation";

    public static string? CurrentId()
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
        return key?.GetValue("TimeZoneKeyName") as string;
    }
}
