using System.Runtime.CompilerServices;
using System.Text;
using LaserficheReports.Application.Interfaces;
using LaserficheReports.Domain.Entities;
using LaserficheReports.Infrastructure.Services;
namespace LaserficheReports.Web;

internal sealed class LiveQueryService(ILaserficheEntryService entries, ILaserficheTemplateService templates,
    ILaserficheFieldDefinitionService definitions, ILaserficheDocumentService documents,
    LiveReportFiles? files = null, IHttpContextAccessor? accessor = null, IConfiguration? configuration = null)
{
    internal async Task<ChatResult> AnswerAsync(QueryIntent intent,string repository,CancellationToken ct)
    {
        if(intent.Type==QueryType.Clarification)return new ChatResult("حدد اسم الحقل أو القالب كما يظهر في Laserfiche، واكتب الشرط بصيغة «اسم الحقل يساوي القيمة». لا أستطيع تأكيد حصر لهذا الطلب قبل تحديد الشرط.",[]);
        if(intent.Type==QueryType.TemplateQuery&&intent.Template is null)
        {
            var values=await templates.GetTemplateDefinitionsAsync(ct);
            return new ChatResult("القوالب الحالية من Laserfiche:\n\n| الرقم | القالب |\n| --- | --- |\n"+string.Join("\n",values.Select(x=>$"| {x.Id} | {ReportSupport.Cell(x.Name)} |")),[],Scope(values.Count));
        }
        if(intent.Type==QueryType.FieldQuery)
        {
            var values=await definitions.GetFieldDefinitionsAsync(ct);
            return new ChatResult("الحقول الحالية من Laserfiche:\n\n| الرقم | الحقل | النوع |\n| --- | --- | --- |\n"+string.Join("\n",values.Select(x=>$"| {x.Key} | {ReportSupport.Cell(x.Value.Name)} | {ReportSupport.Cell(x.Value.FieldType)} |")),[],Scope(values.Count));
        }
        var artifact=files is not null && accessor?.HttpContext is not null ? files.Create(repository,accessor.HttpContext.Session.Id) : ((string Token,string Path)?)null;
        await using var writer=artifact is not null ? new StreamWriter(artifact.Value.Path,false,new UTF8Encoding(false)) : null;
        if(writer is not null)await writer.WriteLineAsync("# نتائج Laserfiche الحالية\n\n| الرقم | الاسم | المسار |\n| --- | --- | --- |");
        var evidence=new List<Evidence>();var rows=new StringBuilder();var total=0;long imageCount=0;var unknownImages=0;
        await foreach(var entry in MatchingAsync(intent,ct))
        {
            total++;
            if(intent.Kind=="images")
            {
                var pages=await documents.GetDocumentPagesAsync(entry.Id,ct);
                // Page count is not image count. Count only explicitly identified image pages.
                imageCount+=pages.Count(x=>x.MimeType?.StartsWith("image/",StringComparison.OrdinalIgnoreCase)==true||x.Width is >0&&x.Height is >0);
                unknownImages+=pages.Count(x=>string.IsNullOrEmpty(x.MimeType)&&!(x.Width is >0&&x.Height is >0));
            }
            if(writer is not null)await writer.WriteLineAsync($"| {entry.Id} | {ReportSupport.Cell(entry.Name)} | {ReportSupport.Cell(entry.FullPath)} |");
            if(evidence.Count>=500)continue; // display bound only; enumeration and counts continue to completion.
            var fields=intent.Type==QueryType.DocumentLookup?await entries.GetEntryFieldsAsync(entry.Id,ct):[];
            var text=fields.Count>0?string.Join("; ",fields.Select(x=>$"{x.FieldName}: {x.Value}")):entry.FullPath;
            evidence.Add(new(entry.Id,entry.Name,entry.FullPath,null,1,text,"laserfiche-metadata-live"));
            rows.AppendLine($"| {entry.Id} | {ReportSupport.Cell(entry.Name)} | {ReportSupport.Cell(entry.FullPath)} | {ReportSupport.Cell(text)} |");
        }
        var detail=$"تم الحصر مباشرة من Laserfiche وفق صلاحيات حسابك، دون Top-K. وقت انتهاء الفحص: {DateTimeOffset.UtcNow:O}. التغييرات أثناء الفحص قد تؤثر على النتائج؛ هذا ليس لقطة معاملات للمستودع.";
        if(total>500)detail+=" يعرض الرد أول 500 نتيجة فقط؛ العدد يشمل جميع النتائج المفحوصة.";
        var count=intent.Kind=="images"?$"الصور المؤكدة: **{imageCount}**."+(unknownImages>0?$" تعذر تحديد نوع {unknownImages} صفحة؛ عدد الصور جزئي.":""):$"عدد النتائج: **{total}**.";
        if(writer is not null){await writer.WriteLineAsync("\n"+count+"\n\n"+detail);await writer.FlushAsync(ct);}
        return new ChatResult(count+"\n\n"+detail+"\n\n| الرقم | الاسم | المسار | البيانات |\n| --- | --- | --- | --- |\n"+rows,evidence,Scope(total) with {Detail=detail,Exhaustive=unknownImages==0}) { DownloadUrl=artifact is not null?$"/api/reports/files/{artifact.Value.Token}":null };
        AnswerScope Scope(int count)=>new("repository",repository,count,0,true,"المصدر: Laserfiche API الحالي؛ النطاق يتبع صلاحيات حسابك.",intent.EntryIds);
    }
    internal async IAsyncEnumerable<LFEntry> MatchingAsync(QueryIntent intent,[EnumeratorCancellation] CancellationToken ct)
    {
        string? fieldName=null;
        if(intent.Condition is {} condition)
        {
            var schema=await definitions.GetFieldDefinitionsAsync(ct);
            var names=schema.Values.Select(x=>x.Name).Where(x=>ReportSupport.MatchesField(condition,x)).OrderByDescending(x=>ReportSupport.MatchKey(x).Length).ToArray();
            if(names.Length==0)throw new ArgumentException("لم أجد اسم الحقل المطلوب. اكتب اسمه الكامل كما يظهر في Laserfiche.");
            var longest=ReportSupport.MatchKey(names[0]).Length;
            var choices=names.Where(x=>ReportSupport.MatchKey(x).Length==longest).Distinct().ToArray();
            if(choices.Length!=1)throw new ArgumentException("اسم الحقل يحتمل أكثر من تعريف. حدد اسم الحقل الكامل.");
            fieldName=choices[0];
        }
        int? templateId=null;
        if(intent.Template is {} template)
        {
            var matches=(await templates.GetTemplateDefinitionsAsync(ct)).Where(x=>ReportSupport.MatchKey(x.Name)==ReportSupport.MatchKey(template)).ToArray();
            if(matches.Length!=1)throw new ArgumentException("حدد اسم قالب موجود في Laserfiche دون اختصار.");
            templateId=matches[0].Id;
        }
        var zone=TimeZoneInfo.FindSystemTimeZoneById(configuration?["Reports:TimeZone"]??"Asia/Riyadh");
        var start=TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,zone).Date;
        await foreach(var entry in Candidates(ct))
        {
            if(intent.Kind=="folders") {if(entry.EntryType is LFEntryType.Folder or LFEntryType.RecordSeries)yield return entry;continue;}
            if(entry.EntryType!=LFEntryType.Document)continue;
            if(templateId.HasValue&&entry.TemplateId!=templateId)continue;
            if(intent.DateMode=="created-today"&&(entry.CreationTime is null || TimeZoneInfo.ConvertTime(entry.CreationTime.Value,zone).Date!=start))continue;
            if(intent.DateMode=="modified-recent"&&!(entry.LastModifiedTime>=DateTimeOffset.UtcNow.AddDays(-7)))continue;
            var current=entry;
            if(intent.Kind=="electronic")
            {
                if(current.IsElectronicDocument is null)current=await entries.GetEntryAsync(entry.Id,ct);
                if(current.IsElectronicDocument is null)throw new ArgumentException("Laserfiche لم يرجع خاصية الملف الإلكتروني؛ لا يمكن إعطاء عدد مؤكد.");
                if(current.IsElectronicDocument!=true)continue;
            }
            if(fieldName is not null)
            {
                var fields=await entries.GetEntryFieldsAsync(entry.Id,ct);
                if(!fields.Any(x=>string.Equals(x.FieldName,fieldName,StringComparison.OrdinalIgnoreCase)&&ReportSupport.MatchesValue(x.Value,intent.Condition!.ExpectedValue,x.IsMultiValue)))continue;
            }
            yield return current;
        }
        async IAsyncEnumerable<LFEntry> Candidates([EnumeratorCancellation] CancellationToken token)
        {
            if(intent.EntryIds.Length>0){foreach(var id in intent.EntryIds)yield return await entries.GetEntryAsync(id,token);}
            else await foreach(var entry in LiveRepositoryTraversal.EnumerateAsync(entries,intent.FolderId,token))yield return entry;
        }
    }
    internal async Task<IndexedDocumentPage> ListAsync(int page,string? search,CancellationToken ct)
    {
        if(page<1||page>1000000||search?.Length>200)throw new ArgumentException("رقم الصفحة أو البحث غير صالح.");
        var rows=new List<IndexedDocument>();var offset=(page-1)*50;var found=0;
        await foreach(var entry in LiveRepositoryTraversal.EnumerateAsync(entries,cancellationToken:ct))
        {
            if(entry.EntryType!=LFEntryType.Document||!string.IsNullOrWhiteSpace(search)&&!(entry.Name.Contains(search,StringComparison.OrdinalIgnoreCase)||entry.FullPath.Contains(search,StringComparison.OrdinalIgnoreCase)))continue;
            if(found++<offset)continue;
            rows.Add(new(entry.Id,entry.Name,entry.FullPath,"live",0,null));if(rows.Count==51)break;
        }
        return new(rows.Take(50).ToArray(),page,rows.Count>50);
    }
}
