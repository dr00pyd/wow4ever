using System.Runtime.InteropServices;
using System.Text;
using ForeverScout.Core;

namespace ForeverScout.Mac;

/// <summary>
/// Nyckeln sparas i användarens nyckelring (login-nyckelringen) som ett generiskt lösenord: tjänst
/// <c>se.wow4ever.foreverscout</c>, konto <c>contributor-key</c>. Motsvarigheten till DPAPI på Windows.
/// <para>Security.framework anropas direkt (SecItemAdd/SecItemCopyMatching/SecItemUpdate/SecItemDelete via P/Invoke)
/// i stället för kommandot <c>security</c>: nyckeln syns då aldrig i en processlista, och posten skapas med en
/// åtkomstlista som bara litar på den här appens kodsignatur – andra program måste be användaren om lov. Efter en
/// uppdatering (samma Team ID och paket-id) godkänns den nya versionen automatiskt.</para>
/// Inget skrivs till disk utanför nyckelringen.
/// </summary>
internal static class KeychainStore
{
    private const string Service = SafeFs.MacBundleId;
    private const string Account = "contributor-key";
    private const string SecurityLib = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8 = 0x08000100; // kCFStringEncodingUTF8
    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;

    public static string? Load()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        try
        {
            using var cf = new CfScope();
            var query = cf.Dict(
                (K.SecClass, K.SecClassGenericPassword), (K.SecAttrService, cf.Str(Service)), (K.SecAttrAccount, cf.Str(Account)),
                (K.SecReturnData, K.True), (K.SecMatchLimit, K.SecMatchLimitOne));
            var status = SecItemCopyMatching(query, out var data);
            if (status != ErrSecSuccess || data == IntPtr.Zero) return null;
            try
            {
                var len = (int)CFDataGetLength(data);
                var bytes = new byte[len];
                Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, len);
                var key = Encoding.UTF8.GetString(bytes);
                Array.Clear(bytes);
                return key.Length > 0 ? key : null;
            }
            finally { CFRelease(data); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or TypeInitializationException) { return null; }
    }

    public static void Save(string key)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Keychain");
        using var cf = new CfScope();
        var bytes = Encoding.UTF8.GetBytes(key);
        var data = cf.Data(bytes);
        Array.Clear(bytes);
        var match = cf.Dict((K.SecClass, K.SecClassGenericPassword), (K.SecAttrService, cf.Str(Service)), (K.SecAttrAccount, cf.Str(Account)));
        var status = SecItemUpdate(match, cf.Dict((K.SecValueData, data)));
        if (status == ErrSecItemNotFound)
        {
            var add = cf.Dict(
                (K.SecClass, K.SecClassGenericPassword), (K.SecAttrService, cf.Str(Service)), (K.SecAttrAccount, cf.Str(Account)),
                (K.SecAttrLabel, cf.Str("Forever Scout")), (K.SecValueData, data));
            status = SecItemAdd(add, IntPtr.Zero);
        }
        if (status != ErrSecSuccess) throw new IOException("Keychain " + status);
    }

    public static void Delete()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            using var cf = new CfScope();
            SecItemDelete(cf.Dict((K.SecClass, K.SecClassGenericPassword), (K.SecAttrService, cf.Str(Service)), (K.SecAttrAccount, cf.Str(Account))));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or TypeInitializationException) { }
    }

    // ------------------------------------------------------------------ CoreFoundation/Security

    /// <summary>Konstanter som exporteras av ramverken (CFStringRef-värden resp. adresser till strukturer).</summary>
    private static class K
    {
        private static readonly IntPtr Sec = NativeLibrary.Load(SecurityLib);
        private static readonly IntPtr Cf = NativeLibrary.Load(CoreFoundationLib);
        private static IntPtr Value(IntPtr lib, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(lib, name));

        public static readonly IntPtr SecClass = Value(Sec, "kSecClass");
        public static readonly IntPtr SecClassGenericPassword = Value(Sec, "kSecClassGenericPassword");
        public static readonly IntPtr SecAttrService = Value(Sec, "kSecAttrService");
        public static readonly IntPtr SecAttrAccount = Value(Sec, "kSecAttrAccount");
        public static readonly IntPtr SecAttrLabel = Value(Sec, "kSecAttrLabel");
        public static readonly IntPtr SecValueData = Value(Sec, "kSecValueData");
        public static readonly IntPtr SecReturnData = Value(Sec, "kSecReturnData");
        public static readonly IntPtr SecMatchLimit = Value(Sec, "kSecMatchLimit");
        public static readonly IntPtr SecMatchLimitOne = Value(Sec, "kSecMatchLimitOne");
        public static readonly IntPtr True = Value(Cf, "kCFBooleanTrue");
        /// <summary>Adresserna till kCFTypeDictionaryKeyCallBacks/ValueCallBacks (strukturerna själva, inte pekare i dem).</summary>
        public static readonly IntPtr DictKeyCallBacks = NativeLibrary.GetExport(Cf, "kCFTypeDictionaryKeyCallBacks");
        public static readonly IntPtr DictValueCallBacks = NativeLibrary.GetExport(Cf, "kCFTypeDictionaryValueCallBacks");
    }

    /// <summary>Släpper alla CF-objekt som skapats i ett anrop.</summary>
    private sealed class CfScope : IDisposable
    {
        private readonly List<IntPtr> _owned = [];

        private IntPtr Own(IntPtr p)
        {
            if (p == IntPtr.Zero) throw new InvalidOperationException("CoreFoundation");
            _owned.Add(p);
            return p;
        }

        public IntPtr Str(string s) => Own(CFStringCreateWithCString(IntPtr.Zero, s, Utf8));
        public IntPtr Data(byte[] b) => Own(CFDataCreate(IntPtr.Zero, b, b.Length));

        public IntPtr Dict(params (IntPtr Key, IntPtr Value)[] pairs) =>
            Own(CFDictionaryCreate(IntPtr.Zero, pairs.Select(p => p.Key).ToArray(), pairs.Select(p => p.Value).ToArray(), pairs.Length,
                K.DictKeyCallBacks, K.DictValueCallBacks));

        public void Dispose()
        {
            for (int i = _owned.Count - 1; i >= 0; i--) CFRelease(_owned[i]);
            _owned.Clear();
        }
    }

    [DllImport(CoreFoundationLib)] private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, [MarshalAs(UnmanagedType.LPUTF8Str)] string cStr, uint encoding);
    [DllImport(CoreFoundationLib)] private static extern IntPtr CFDataCreate(IntPtr alloc, byte[] bytes, nint length);
    [DllImport(CoreFoundationLib)] private static extern IntPtr CFDictionaryCreate(IntPtr alloc, IntPtr[] keys, IntPtr[] values, nint numValues, IntPtr keyCallBacks, IntPtr valueCallBacks);
    [DllImport(CoreFoundationLib)] private static extern nint CFDataGetLength(IntPtr data);
    [DllImport(CoreFoundationLib)] private static extern IntPtr CFDataGetBytePtr(IntPtr data);
    [DllImport(CoreFoundationLib)] private static extern void CFRelease(IntPtr cf);
    [DllImport(SecurityLib)] private static extern int SecItemAdd(IntPtr attributes, IntPtr result);
    [DllImport(SecurityLib)] private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);
    [DllImport(SecurityLib)] private static extern int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);
    [DllImport(SecurityLib)] private static extern int SecItemDelete(IntPtr query);
}
