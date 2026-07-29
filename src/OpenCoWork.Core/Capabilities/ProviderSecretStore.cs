using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Agents;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.Workspaces;

namespace OpenCoWork.Core.Capabilities;

internal interface IProviderOsSecretStore
{
    bool IsAvailable { get; }

    string? Read(string account);

    void Set(string account, string secret);

    void Clear(string account);
}

internal sealed class InMemoryOsSecretStore : IProviderOsSecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public bool IsAvailable => true;

    public string? Read(string account) => _values.GetValueOrDefault(account);

    public void Set(string account, string secret) => _values[account] = secret;

    public void Clear(string account) => _values.Remove(account);
}

internal sealed class UnavailableOsSecretStore : IProviderOsSecretStore
{
    public bool IsAvailable => false;

    public string? Read(string account) => throw Unavailable();

    public void Set(string account, string secret) => throw Unavailable();

    public void Clear(string account) => throw Unavailable();

    private static PlatformNotSupportedException Unavailable() =>
        new("The operating-system secret store is unavailable.");
}

internal static class ProviderOsSecretStore
{
    public static IProviderOsSecretStore Create() =>
        OperatingSystem.IsMacOS()
            ? new MacOsProviderSecretStore()
            : OperatingSystem.IsWindows()
                ? new WindowsProviderSecretStore()
                : new UnavailableOsSecretStore();
}

internal sealed class ProviderSecretLease : IDisposable
{
    private bool _disposed;
    private IDisposable? _redaction;
    private string? _secret;

    internal ProviderSecretLease(string? secret, IDisposable? redaction = null)
    {
        _secret = secret;
        _redaction = redaction;
    }

    public string? Secret =>
        !_disposed
            ? _secret
            : throw new ObjectDisposedException(nameof(ProviderSecretLease));

    public void Dispose()
    {
        _disposed = true;
        _secret = null;
        Interlocked.Exchange(ref _redaction, null)?.Dispose();
    }
}

internal sealed class ProviderAuthService
{
    private readonly ModelsConfig _models;
    private readonly ProviderDeclarationCatalog _declarations;
    private readonly IProviderOsSecretStore _store;
    private readonly SecretRedactor _redactor;
    private readonly Func<string, string?> _readEnvironmentVariable;
    private readonly string _workspaceHash;

    public ProviderAuthService(
        ModelsConfig models,
        ProviderDeclarationCatalog declarations,
        IProviderOsSecretStore store,
        SecretRedactor redactor,
        Func<string, string?>? readEnvironmentVariable = null,
        OpenCoWorkPaths? paths = null)
    {
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _declarations = declarations ??
                        throw new ArgumentNullException(nameof(declarations));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _readEnvironmentVariable =
            readEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        _workspaceHash = Hash(
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(paths?.WorkspaceRoot ?? Environment.CurrentDirectory)));
    }

    public ProviderSecretLease Acquire(string? profileId)
    {
        if (profileId is null)
        {
            return new ProviderSecretLease(secret: null);
        }

        var profile = GetProfile(profileId);
        if (profile.Kind == ProviderAuthKind.None)
        {
            return new ProviderSecretLease(secret: null);
        }

        if (profile.Kind != ProviderAuthKind.ApiKey)
        {
            throw AuthenticationFailed();
        }

        string? secret;
        try
        {
            secret = profile.SourceKind switch
            {
                ProviderAuthSourceKind.Environment =>
                    _readEnvironmentVariable(profile.SourceName!),
                ProviderAuthSourceKind.OsSecretStore when _store.IsAvailable =>
                    _store.Read(Account(profile.Id)),
                _ => null,
            };
        }
        catch (Exception exception) when (
            exception is Win32Exception or ExternalException or
                PlatformNotSupportedException)
        {
            throw AuthenticationFailed();
        }

        if (string.IsNullOrWhiteSpace(secret))
        {
            throw AuthenticationFailed();
        }

        return new ProviderSecretLease(secret, _redactor.RegisterSecret(secret));
    }

    public void Set(string profileId, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        var profile = GetProfile(profileId);
        if (profile.SourceKind != ProviderAuthSourceKind.OsSecretStore ||
            !_store.IsAvailable)
        {
            throw AuthenticationFailed();
        }

        _store.Set(Account(profile.Id), secret);
    }

    public void Clear(string profileId)
    {
        var profile = GetProfile(profileId);
        if (profile.SourceKind != ProviderAuthSourceKind.OsSecretStore ||
            !_store.IsAvailable)
        {
            throw AuthenticationFailed();
        }

        _store.Clear(Account(profile.Id));
    }

    internal ProviderSecretLease AcquireStored(string profileId)
    {
        var profile = GetProfile(profileId);
        if (profile.SourceKind != ProviderAuthSourceKind.OsSecretStore ||
            !_store.IsAvailable)
        {
            throw AuthenticationFailed();
        }

        try
        {
            var secret = _store.Read(Account(profile.Id));
            return new ProviderSecretLease(
                secret,
                string.IsNullOrWhiteSpace(secret)
                    ? null
                    : _redactor.RegisterSecret(secret));
        }
        catch (Exception exception) when (
            exception is Win32Exception or ExternalException or
                PlatformNotSupportedException)
        {
            throw AuthenticationFailed();
        }
    }

    internal ProviderAuthProfile GetProfile(string profileId)
    {
        if (profileId.StartsWith("core/", StringComparison.Ordinal))
        {
            var providerId = profileId["core/".Length..];
            if (_models.Providers.TryGetValue(providerId, out var provider))
            {
                return new ProviderAuthProfile(
                    profileId,
                    ProviderAuthKind.ApiKey,
                    ProviderAuthSourceKind.Environment,
                    provider.ApiKey.Environment,
                    ProviderAuthPlacement.Bearer,
                    Available: true);
            }
        }

        return _declarations.AuthProfiles.TryGetValue(profileId, out var profile)
            ? profile
            : throw AuthenticationFailed();
    }

    private string Account(string profileId) => $"{_workspaceHash}:{profileId}";

    private static AgentPreparationException AuthenticationFailed() =>
        new(
            AgentErrorCodes.ProviderAuthenticationFailed,
            "Provider authentication is unavailable.");

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}

internal sealed class MacOsProviderSecretStore : IProviderOsSecretStore
{
    private const int Success = 0;
    private const int ItemNotFound = -25300;
    private static readonly byte[] Service = "OpenCoWork"u8.ToArray();

    public bool IsAvailable => OperatingSystem.IsMacOS();

    public string? Read(string account)
    {
        EnsurePlatform();
        var accountBytes = Encoding.UTF8.GetBytes(account);
        var status = SecKeychainFindGenericPassword(
            IntPtr.Zero,
            (uint)Service.Length,
            Service,
            (uint)accountBytes.Length,
            accountBytes,
            out var length,
            out var data,
            out var item);
        if (status == ItemNotFound)
        {
            return null;
        }

        ThrowIfFailed(status);
        try
        {
            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, checked((int)length));
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero)
            {
                CFRelease(item);
            }
        }
    }

    public void Set(string account, string secret)
    {
        EnsurePlatform();
        var accountBytes = Encoding.UTF8.GetBytes(account);
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var status = SecKeychainFindGenericPassword(
            IntPtr.Zero,
            (uint)Service.Length,
            Service,
            (uint)accountBytes.Length,
            accountBytes,
            out _,
            out var data,
            out var item);
        if (status == ItemNotFound)
        {
            ThrowIfFailed(SecKeychainAddGenericPassword(
                IntPtr.Zero,
                (uint)Service.Length,
                Service,
                (uint)accountBytes.Length,
                accountBytes,
                (uint)secretBytes.Length,
                secretBytes,
                out item));
            if (item != IntPtr.Zero)
            {
                CFRelease(item);
            }

            return;
        }

        ThrowIfFailed(status);
        try
        {
            ThrowIfFailed(SecKeychainItemModifyAttributesAndData(
                item,
                IntPtr.Zero,
                (uint)secretBytes.Length,
                secretBytes));
        }
        finally
        {
            _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero)
            {
                CFRelease(item);
            }
        }
    }

    public void Clear(string account)
    {
        EnsurePlatform();
        var accountBytes = Encoding.UTF8.GetBytes(account);
        var status = SecKeychainFindGenericPassword(
            IntPtr.Zero,
            (uint)Service.Length,
            Service,
            (uint)accountBytes.Length,
            accountBytes,
            out _,
            out var data,
            out var item);
        if (status == ItemNotFound)
        {
            return;
        }

        ThrowIfFailed(status);
        try
        {
            ThrowIfFailed(SecKeychainItemDelete(item));
        }
        finally
        {
            _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero)
            {
                CFRelease(item);
            }
        }
    }

    private static void EnsurePlatform()
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException();
        }
    }

    private static void ThrowIfFailed(int status)
    {
        if (status != Success)
        {
            throw new ExternalException("Keychain Services operation failed.", status);
        }
    }

    [DllImport(
        "/System/Library/Frameworks/Security.framework/Security",
        EntryPoint = "SecKeychainFindGenericPassword")]
    private static extern int SecKeychainFindGenericPassword(
        IntPtr keychainOrArray,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        out uint passwordLength,
        out IntPtr passwordData,
        out IntPtr itemRef);

    [DllImport(
        "/System/Library/Frameworks/Security.framework/Security",
        EntryPoint = "SecKeychainAddGenericPassword")]
    private static extern int SecKeychainAddGenericPassword(
        IntPtr keychain,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        uint passwordLength,
        byte[] passwordData,
        out IntPtr itemRef);

    [DllImport(
        "/System/Library/Frameworks/Security.framework/Security",
        EntryPoint = "SecKeychainItemModifyAttributesAndData")]
    private static extern int SecKeychainItemModifyAttributesAndData(
        IntPtr itemRef,
        IntPtr attributes,
        uint length,
        byte[] data);

    [DllImport(
        "/System/Library/Frameworks/Security.framework/Security",
        EntryPoint = "SecKeychainItemDelete")]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(
        "/System/Library/Frameworks/Security.framework/Security",
        EntryPoint = "SecKeychainItemFreeContent")]
    private static extern int SecKeychainItemFreeContent(
        IntPtr attributeList,
        IntPtr data);

    [DllImport(
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation",
        EntryPoint = "CFRelease")]
    private static extern void CFRelease(IntPtr value);
}

internal sealed class WindowsProviderSecretStore : IProviderOsSecretStore
{
    private const int CredentialTypeGeneric = 1;
    private const int CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public bool IsAvailable => OperatingSystem.IsWindows();

    public string? Read(string account)
    {
        EnsurePlatform();
        if (!CredRead(Target(account), CredentialTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            return error == ErrorNotFound
                ? null
                : throw new Win32Exception(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Set(string account, string secret)
    {
        EnsurePlatform();
        var bytes = Encoding.UTF8.GetBytes(secret);
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = Target(account),
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
                UserName = account,
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public void Clear(string account)
    {
        EnsurePlatform();
        if (!CredDelete(Target(account), CredentialTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(error);
            }
        }
    }

    private static string Target(string account) => $"OpenCoWork:{account}";

    private static void EnsurePlatform()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        int type,
        int flags,
        out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(
        ref NativeCredential credential,
        int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(
        string target,
        int type,
        int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
