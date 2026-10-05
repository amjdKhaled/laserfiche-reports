using System.Security.Cryptography;
using System.Text.Json;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Common;
using LaserficheReports.Infrastructure.Repository;
using Microsoft.Extensions.Options;
namespace LaserficheReports.Infrastructure.Realtime;

/// <summary>Background-only machine DPAPI credentials, protected by installer ACLs.
/// Browser login never writes this store and browser credentials never become a service account.</summary>
internal sealed class IndexerCredentialProvider(ICredentialProvider inner,RepositoryExecutionContext execution,
    IOptions<RealtimeOptions> options) : ICredentialProvider
{
    public async Task<LaserficheCredential> GetCredentialsAsync(string repositoryKey,CancellationToken cancellationToken=default)
    {
        if(execution.Current is not null&&OperatingSystem.IsWindows())
        {
            var path=Path.Combine(options.Value.StateDirectory,"credentials",IndexFingerprint.Hash(repositoryKey.ToLowerInvariant())+".bin");
            if(File.Exists(path))
            {
                var ciphertext=await File.ReadAllBytesAsync(path,cancellationToken);
                var bytes=ProtectedData.Unprotect(ciphertext,null,DataProtectionScope.LocalMachine);
                try {return JsonSerializer.Deserialize<LaserficheCredential>(bytes,new JsonSerializerOptions {PropertyNameCaseInsensitive=true})??throw new InvalidOperationException("Invalid service credential store.");}
                finally {CryptographicOperations.ZeroMemory(bytes);}
            }
        }
        return await inner.GetCredentialsAsync(repositoryKey,cancellationToken);
    }
    public Task StoreCredentialsAsync(string key,string username,string password,CancellationToken ct=default)=>inner.StoreCredentialsAsync(key,username,password,ct);
}
