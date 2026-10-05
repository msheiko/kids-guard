using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using KidGuard.Core;
using static KidGuard.Win32.NativeMethods;

namespace KidGuard.Win32;

/// <summary>Локальные учётные записи Windows (NetUserGetInfo / NetUserSetInfo).</summary>
public static class LocalAccounts
{
    public static bool Exists(string user)
    {
        var code = NetUserGetInfo(null, user, 1, out var buffer);
        if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
        if (code == NerrUserNotFound) return false;
        if (code != NerrSuccess) throw new Win32Exception(code);
        return true;
    }

    public static bool IsDisabled(string user) => (GetInfo(user).usri1_flags & UfAccountDisable) != 0;

    /// <summary>Эквивалент <c>net user &lt;name&gt; /active:yes|no</c>.</summary>
    public static void SetDisabled(string user, bool disabled)
    {
        var flags = GetInfo(user).usri1_flags;
        var info = new USER_INFO_1008
        {
            usri1008_flags = disabled ? flags | UfAccountDisable : flags & ~UfAccountDisable,
        };
        var code = NetUserSetInfo(null, user, 1008, ref info, out _);
        if (code != NerrSuccess) throw new Win32Exception(code);
    }

    /// <summary>Входит ли пользователь в группу «Администраторы» (в том числе косвенно).</summary>
    public static bool IsAdministrator(string user)
    {
        if (GetInfo(user).usri1_priv == UserPrivAdmin) return true;

        var adminGroup = LocalName(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        var code = NetUserGetLocalGroups(null, user, 0, LgIncludeIndirect, out var buffer, MaxPreferredLength, out var read, out _);
        try
        {
            if (code != NerrSuccess) throw new Win32Exception(code);
            var size = Marshal.SizeOf<LOCALGROUP_USERS_INFO_0>();
            for (var i = 0; i < read; i++)
            {
                var entry = Marshal.PtrToStructure<LOCALGROUP_USERS_INFO_0>(buffer + i * size);
                var name = Marshal.PtrToStringUni(entry.lgrui0_name);
                if (string.Equals(name, adminGroup, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
        }
    }

    /// <summary>Локализованное имя группы или учётной записи без префикса домена.</summary>
    public static string LocalName(SecurityIdentifier sid)
    {
        var account = sid.Translate(typeof(NTAccount)).Value;
        var slash = account.IndexOf('\\');
        return slash >= 0 ? account[(slash + 1)..] : account;
    }

    static USER_INFO_1 GetInfo(string user)
    {
        var code = NetUserGetInfo(null, user, 1, out var buffer);
        try
        {
            if (code != NerrSuccess) throw new Win32Exception(code);
            return Marshal.PtrToStructure<USER_INFO_1>(buffer);
        }
        finally
        {
            if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
        }
    }
}

/// <summary>Включение и отключение учётной записи ребёнка (раздел 5.1).</summary>
public sealed class LocalAccountController : IAccountController
{
    readonly string _user;

    public LocalAccountController(string user) => _user = user;

    public bool IsEnabled() => !LocalAccounts.IsDisabled(_user);

    public void SetEnabled(bool enabled)
    {
        // Защита от самоблокировки: администраторов не трогаем никогда (раздел 10.8).
        if (!enabled && LocalAccounts.IsAdministrator(_user))
            throw new InvalidOperationException($"Refusing to disable administrator account '{_user}'");
        LocalAccounts.SetDisabled(_user, !enabled);
    }
}
