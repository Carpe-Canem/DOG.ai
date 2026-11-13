using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using DogAI.Core.Privacy;
using Microsoft.Win32;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;

namespace DogAI.App;

/// <summary>
/// Windows Hello for the data lock. DOG.ai keeps one Hello key in the Windows account (made the first time Hello is turned
/// on for a lock). Windows asks for the face, fingerprint or PIN every time the key signs. The key never leaves Windows,
/// and DOG.ai stores no secret: what unlocks the data is the signature, which only comes out after that check.
/// </summary>
public sealed class WindowsHello : IHelloSigner
{
    /// <summary>
    /// The key's name in the Windows account. One key serves every DOG.ai data folder on this PC: each folder's lock file
    /// has its own challenge, so each gets its own signature. The key is never replaced or deleted here, because that
    /// would silently turn Hello off for the other folders.
    /// </summary>
    private const string KeyName = "DOG.ai data lock";

    private string? _pcId;

    public string PcId => _pcId ??= MakePcId();

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            return await KeyCredentialManager.IsSupportedAsync();
        }
        catch (Exception)
        {
            // An older Windows, or Hello switched off by policy: treated as not there.
            return false;
        }
    }

    public async Task<byte[]?> SignAsync(byte[] challenge, bool create)
    {
        try
        {
            var opened = await KeyCredentialManager.OpenAsync(KeyName);
            if (opened.Status == KeyCredentialStatus.NotFound && create)
                opened = await KeyCredentialManager.RequestCreateAsync(KeyName, KeyCredentialCreationOption.FailIfExists);
            if (opened.Status != KeyCredentialStatus.Success) return null;

            var signed = await opened.Credential.RequestSignAsync(CryptographicBuffer.CreateFromByteArray(challenge));
            return signed.Status == KeyCredentialStatus.Success ? signed.Result.ToArray() : null;
        }
        catch (Exception)
        {
            // Cancelled, Hello removed, or Windows refused: the lock screen falls back to the password.
            return null;
        }
    }

    /// <summary>
    /// Tells this PC and Windows account apart from others in the lock file, without writing either name there:
    /// a fingerprint of Windows' own machine id and the account's id.
    /// </summary>
    private static string MakePcId()
    {
        string machine;
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            machine = key?.GetValue("MachineGuid") as string ?? Environment.MachineName;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            machine = Environment.MachineName;
        }
        string account;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            account = identity.User?.Value ?? Environment.UserName;
        }
        catch (System.Security.SecurityException)
        {
            account = Environment.UserName;
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("DOG.ai pc id\n" + machine + "\n" + account));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }
}
