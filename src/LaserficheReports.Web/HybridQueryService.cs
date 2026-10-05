using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LaserficheReports.Domain.Entities;
using Npgsql;
namespace LaserficheReports.Web;

/// <summary>Filter every matching entry via live API, then stream ALL content chunks for each authorized entry.
/// No semantic top-k is used to select this population. Large reports are streamed to a session-owned file.</summary>
internal sealed class HybridQueryService(LiveQueryService live,IConfiguration config,IHttpClientFactory clients,
    LiveReportFiles files,IHttpContextAccessor accessor)
{
    public async Task<ChatResult> AnswerAsync(string question,QueryIntent intent,string repository,CancellationToken ct)
    {
        await using var db=new NpgsqlConnection(config["Supabase:PostgresConnectionString"]);
        await db.OpenAsync(ct);
        var artifact=files.Create(repository,accessor.HttpContext!.Session.Id);
        await using var writer=new StreamWriter(artifact.Path,false,new UTF8Encoding(false));
        await writer.WriteLineAsync("# تحليل الوثائق المطابقة من Laserfiche\n\n"+question+"\n");
        var preview=new StringBuilder();var sources=new List<Evidence>();var matching=0;var indexed=0;var stale=0;var calls=0;
        await foreach(var entry in live.MatchingAsync(intent,ct))
        {
            matching++;
            // The parent timestamp is checked against the current API row; old index data is never silently analyzed.
            const string sql="""
              select c.content,c.metadata::text,p.metadata->>'last_modified_time'
              from public.documents c join lateral (
                select metadata from public.documents p where p.metadata->>'source'='laserfiche-reports'
                and p.metadata->>'record_type'='document-metadata'
                and lower(p.metadata->>'repository_id')=lower(@repository)
                and p.metadata->>'entry_id'=@entry order by id desc limit 1
              ) p on true
              where c.metadata->>'source'='laserfiche-reports' and c.metadata->>'record_type'='document-chunk'
                and lower(c.metadata->>'repository_id')=lower(@repository) and c.metadata->>'entry_id'=@entry
                and coalesce(c.metadata->>'text_source','')<>'laserfiche-metadata'
              order by (c.metadata->>'chunk_index')::int,c.id;
              """;
            await using var command=new NpgsqlCommand(sql,db);command.Parameters.AddWithValue("repository",repository);command.Parameters.AddWithValue("entry",entry.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await using var reader=await command.ExecuteReaderAsync(ct);
            var batch=new List<Evidence>();var chars=0;var chunks=0;var outdated=false;
            await writer.WriteLineAsync($"\n## {entry.Id} — {entry.Name}\n\nالمسار: {entry.FullPath}\n");
            while(await reader.ReadAsync(ct))
            {
                if(reader.IsDBNull(2)||!DateTimeOffset.TryParse(reader.GetString(2),out var modified)||entry.LastModifiedTime!=modified){outdated=true;break;}
                using var metadata=JsonDocument.Parse(reader.GetString(1));var m=metadata.RootElement;
                var page=m.TryGetProperty("page_number",out var p)&&p.TryGetInt32(out var n)?(int?)n:null;
                var evidence=new Evidence(entry.Id,entry.Name,entry.FullPath,page,1,reader.GetString(0),m.TryGetProperty("text_source",out var ts)?ts.GetString()??"":"");
                batch.Add(evidence);chars+=evidence.Text.Length;chunks++;
                if(sources.Count<100)sources.Add(evidence);
                if(chars>=16000||batch.Count>=24){await Summarize();batch.Clear();chars=0;}
            }
            if(outdated){stale++;await writer.WriteLineAsync("المحتوى في انتظار التحديث؛ لم يُستخدم الفهرس القديم.");continue;}
            if(batch.Count>0)await Summarize();
            if(chunks>0)indexed++;else await writer.WriteLineAsync("لا يتوفر محتوى مفهرس صالح لهذه الوثيقة بعد.");
            async Task Summarize()
            {
                var scope=new AnswerScope("selected-documents",repository,1,batch.Count,false,"جزء من محتوى الوثيقة؛ لا تستنتج حصرًا من هذه الدفعة.",[entry.Id]);
                using var body=new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new {question,evidence=batch,scope},new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                body.Headers.ContentType=new("application/json"){CharSet="utf-8"};
                using var response=await clients.CreateClient("ReportsGraph").PostAsync("answer",body,ct);response.EnsureSuccessStatusCode();
                var result=await response.Content.ReadFromJsonAsync<GraphResponse>(ct);
                if(string.IsNullOrWhiteSpace(result?.Answer))throw new InvalidOperationException("Empty content-analysis answer.");calls++;
                var text=result.Answer+ReportSupport.SourceTable(batch);
                await writer.WriteLineAsync(text);
                if(preview.Length<18000)preview.AppendLine($"\n### {entry.Id} — {ReportSupport.Cell(entry.Name)}\n"+text);
            }
        }
        var detail=$"مطابق من Laserfiche: {matching}. وثائق بمحتوى محلل: {indexed}. غير متاحة أو في انتظار الفهرسة: {matching-indexed} (منها {stale} بفهرس قديم). التحليل على دفعات من جميع المقاطع المتاحة، دون اختيار عينة Top-K.";
        await writer.WriteLineAsync("\n"+detail);await writer.FlushAsync(ct);files.Complete(artifact.Token);
        var link=$"/api/reports/files/{artifact.Token}";
        var complete=matching==indexed;
        return new ChatResult(detail+$"\n\n[تحميل التقرير الكامل لجميع الوثائق المطابقة]({link})\n\n"+preview.ToString(),sources,
            new("live-filtered-content",repository,matching,sources.Count,complete,detail,intent.EntryIds))
        {DownloadUrl=link,Quality=new(complete?"complete":"partial",true,"per-batch","live-hybrid-v1",calls)};
    }
    private sealed record GraphResponse(string Answer);
}
