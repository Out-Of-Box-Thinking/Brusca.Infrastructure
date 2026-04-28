using Brusca.Core.Contracts.Services;
using Brusca.Core.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Brusca.Infrastructure.Encryption;

/// <summary>
/// Encryption service backed by ASP.NET Core Data Protection. Used to seal
/// the <c>EncryptedPiiJson</c> column. Keys are managed by the host:
/// DPAPI on Windows by default; a key-ring directory may be configured via
/// <see cref="PiiOptions.KeyRingDirectory"/>.
/// </summary>
public sealed class DataProtectionEncryptionService : IEncryptionService
{
    private readonly IDataProtector _protector;

    public DataProtectionEncryptionService(
        IDataProtectionProvider provider,
        IOptions<BruscaOptions> options)
    {
        var purpose = options.Value.Pii.DataProtectionApplicationName;
        _protector = provider.CreateProtector(purpose);
    }

    public string Encrypt(string plainText) =>
        string.IsNullOrEmpty(plainText) ? string.Empty : _protector.Protect(plainText);

    public string Decrypt(string cipherText) =>
        string.IsNullOrEmpty(cipherText) ? string.Empty : _protector.Unprotect(cipherText);
}
