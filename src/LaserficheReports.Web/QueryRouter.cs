using System.Text.RegularExpressions;
namespace LaserficheReports.Web;
internal enum QueryType { MetadataQuery, ExactRepositorySearch, RepositoryStatistics, FolderQuery,
    TemplateQuery, FieldQuery, DocumentLookup, ContentSemanticSearch, ContentSummary, ReportGeneration, HybridQuery, Clarification }
internal sealed record QueryIntent(QueryType Type, FieldCondition? Condition, int[] EntryIds,
    string? Template=null, int? FolderId=null, string? DateMode=null, string? Kind=null);
internal static class QueryRouter
{
    internal static QueryIntent Route(string question)
    {
        var ids=ReportSupport.RequestedEntries(question);
        if(ReportSupport.NeedsFilterClarification(question))return new(QueryType.Clarification,null,ids);
        var content=Regex.IsMatch(question,@"لخص|تلخيص|ملخص|محتوى|المشاكل|المخاطر|حلل|تحليل|summary|summari[sz]e|content|analy[sz]e",RegexOptions.IgnoreCase);
        var condition=ReportSupport.ParseCondition(question);
        // In Arabic «... إجراء الوثيقة فيها تحت الإجراء» is an implicit equality.
        if(condition is null)
        {
            var implicitEquals=Regex.Match(question,@"(?:الوثائق|المستندات|وثائق|مستندات)\s+(?:التي\s+)?(?<field>.+?)\s+فيها\s+(?<value>.+?)[؟?]?$",RegexOptions.IgnoreCase);
            if(implicitEquals.Success)condition=new(implicitEquals.Groups["field"].Value.Trim(),implicitEquals.Groups["value"].Value.Trim());
        }
        var template=Regex.Match(question,@"(?:قالب|template)\s+[«""'](?<name>[^»""']+)[»""']",RegexOptions.IgnoreCase);
        var folder=Regex.Match(question,@"(?:مجلد|folder)\s*(?:رقم\s*)?(?<id>[0-9]+)",RegexOptions.IgnoreCase);
        int? folderId=folder.Success&&int.TryParse(folder.Groups["id"].Value,out var fid)?fid:null;
        var date=Regex.IsMatch(question,@"المنش[اأ]ة?\s+اليوم|[اأ]نشئت\s+اليوم|created today",RegexOptions.IgnoreCase)?"created-today":
            Regex.IsMatch(question,@"المعدلة?\s+(?:اليوم|م[ؤو]خرا)|modified (?:today|recently)",RegexOptions.IgnoreCase)?"modified-recent":null;
        var kind=Regex.IsMatch(question,@"(?:ملفات|الملفات)\s+[إا]لكترونية|electronic",RegexOptions.IgnoreCase)?"electronic":
            Regex.IsMatch(question,@"عدد\s+(?:الصور|صور)|count.*images",RegexOptions.IgnoreCase)?"images":
            ReportSupport.IsFolderCountQuestion(question)?"folders":null;
        if(content&&(condition is not null||template.Success||folderId.HasValue||date is not null||ids.Length>0||Regex.IsMatch(question,@"جميع|كل|all",RegexOptions.IgnoreCase)))
            return new(QueryType.HybridQuery,condition,ids,template.Success?template.Groups["name"].Value:null,folderId,date,kind);
        if(condition is not null)return new(QueryType.ExactRepositorySearch,condition,ids);
        if(template.Success)return new(QueryType.TemplateQuery,null,ids,template.Groups["name"].Value);
        if(!content&&Regex.IsMatch(question,@"القوالب|templates",RegexOptions.IgnoreCase))return new(QueryType.TemplateQuery,null,ids);
        if(!content&&ids.Length==0&&Regex.IsMatch(question,@"الحقول|fields",RegexOptions.IgnoreCase))return new(QueryType.FieldQuery,null,ids);
        if(folderId.HasValue)return new(QueryType.FolderQuery,null,ids,FolderId:folderId);
        if(date is not null||kind is not null||ReportSupport.IsInventoryQuestion(question)||Regex.IsMatch(question,@"^(?:كم|ما|ما هو|ماهو)?\s*(?:عدد|إحصائيات|احصائيات)\s+(?:الوثائق|المستندات|الملفات)",RegexOptions.IgnoreCase))return new(QueryType.RepositoryStatistics,null,ids,DateMode:date,Kind:kind);
        if(ids.Length>0&&ReportSupport.IsDocumentMetadataQuestion(question))return new(QueryType.DocumentLookup,null,ids);
        if(!content&&Regex.IsMatch(question,@"حقل|قالب|إدارة|الادارة|الإدارة|department|template|metadata",RegexOptions.IgnoreCase))return new(QueryType.Clarification,null,ids);
        return new(content?QueryType.ContentSemanticSearch:QueryType.ContentSemanticSearch,null,ids);
    }
}
