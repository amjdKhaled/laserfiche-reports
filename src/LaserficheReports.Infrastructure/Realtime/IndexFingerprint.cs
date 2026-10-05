using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LaserficheReports.Domain.Entities;
namespace LaserficheReports.Infrastructure.Realtime;
public static class IndexFingerprint
{
    public static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Metadata(LFEntry entry,IReadOnlyList<LFFieldValue> fields)=>Hash(JsonSerializer.Serialize(new
    {entry.Id,entry.Name,entry.FullPath,entry.FolderPath,entry.TemplateId,entry.TemplateName,entry.Creator,entry.CreationTime,
      Fields=fields.OrderBy(x=>x.FieldDefinitionId).ThenBy(x=>x.FieldName,StringComparer.Ordinal).ThenBy(x=>x.Value,StringComparer.Ordinal)
        .Select(x=>new {x.FieldDefinitionId,x.FieldName,x.Value,x.FieldType,x.IsMultiValue})}));
}
