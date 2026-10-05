using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using LaserficheReports.Infrastructure.Realtime;
namespace LaserficheReports.Web;
internal sealed class LiveReportFiles(IOptions<RealtimeOptions> options)
{
    private sealed record Artifact(string Path,string Repository,string Session,DateTimeOffset Expires);
    private readonly ConcurrentDictionary<string,Artifact> _files=new();
    public (string Token,string Path) Create(string repository,string session)
    {
        foreach(var pair in _files.Where(x=>x.Value.Expires<DateTimeOffset.UtcNow))
            if(_files.TryRemove(pair.Key,out var old))File.Delete(old.Path);
        var directory=Path.Combine(options.Value.StateDirectory,"reports");Directory.CreateDirectory(directory);
        var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(24));var path=Path.Combine(directory,token+".md");
        _files[token]=new(path,repository,session,DateTimeOffset.UtcNow.AddHours(1));return(token,path);
    }
    public string? Resolve(string token,string repository,string session)=>
        _files.TryGetValue(token,out var file)&&file.Repository==repository&&file.Session==session&&file.Expires>DateTimeOffset.UtcNow?file.Path:null;
}
