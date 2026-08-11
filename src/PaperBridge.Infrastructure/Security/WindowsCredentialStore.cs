using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using PaperBridge.Application.Abstractions;

namespace PaperBridge.Infrastructure.Security;

public sealed class WindowsCredentialStore : ICredentialStore
{
    private const int ErrorNotFound = 1168;
    private const int CredentialTypeGeneric = 1;
    private const int CredentialPersistLocalMachine = 2;
    private const int MaximumCredentialBlobBytes = 2560;
    private const string TargetPrefix = "PaperBridge/";

    public Task SaveAsync(string account, string secret, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var secretBytes = Encoding.Unicode.GetByteCount(secret);
        if (secretBytes > MaximumCredentialBlobBytes)
        {
            throw new ArgumentException(
                $"Credential secret exceeds the Windows limit of {MaximumCredentialBlobBytes} bytes.",
                nameof(secret));
        }

        var secretPointer = Marshal.StringToCoTaskMemUni(secret);

        try
        {
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = TargetPrefix + account.Trim(),
                CredentialBlobSize = secretBytes,
                CredentialBlob = secretPointer,
                Persist = CredentialPersistLocalMachine,
                UserName = account.Trim()
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not save the credential.");
            }
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(secretPointer);
        }

        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string account, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(account);

        if (!CredRead(TargetPrefix + account.Trim(), CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return Task.FromResult<string?>(null);
            }

            throw new Win32Exception(error, "Could not read the credential.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return Task.FromResult<string?>(string.Empty);
            }

            var secretBytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, secretBytes, 0, secretBytes.Length);
            return Task.FromResult<string?>(Encoding.Unicode.GetString(secretBytes));
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public Task DeleteAsync(string account, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(account);

        if (!CredDelete(TargetPrefix + account.Trim(), CredentialTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(error, "Could not delete the credential.");
            }
        }

        return Task.CompletedTask;
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

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
