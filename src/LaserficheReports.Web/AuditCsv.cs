using System.Globalization;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace LaserficheReports.Web;

internal static class AuditCsv
{
    internal static async Task<IReadOnlyList<AuditEvent>> ReadAsync(string path, AuditTrailReportOptions report,
        AuditTrailOptions options, CancellationToken ct)
    {
        // Read only a fresh, randomly named export. Never enumerate or remove LF data.
        for (var current = new FileInfo(path).Directory; current != null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new AuditTrailException("مسار تصدير التدقيق لا يقبل الروابط أو نقاط إعادة التوجيه.");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new AuditTrailException("ملف التصدير لا يقبل الروابط.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        if (stream.Length > options.MaxBytes) throw new AuditTrailException("تقرير التدقيق تجاوز حد الحجم؛ ضيّق نطاق التقرير المحفوظ.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
        var text = await reader.ReadToEndAsync(ct);
        return Parse(text, report, options.MaxRows);
    }

    internal static IReadOnlyList<AuditEvent> Parse(string text, AuditTrailReportOptions report, int maxRows)
    {
        var first = text.Split('\n', 2)[0];
        var delimiter = first.Contains('\t') ? "\t" : ",";
        using var parser = new TextFieldParser(new StringReader(text)) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(delimiter);
        try
        {
            var headers = parser.ReadFields() ?? throw new AuditTrailException("ملف التدقيق بلا أعمدة؛ لا يمكن اعتبار عدد الأحداث صفرًا.");
            if (headers.Distinct(StringComparer.Ordinal).Count() != headers.Length)
                throw new AuditTrailException("تقرير التدقيق يحتوي أسماء أعمدة مكررة.");
            var allowed = new[] { "eventTime", "eventType", "userName", "entryId", "entryName", "outcome" };
            if (report.Columns.Keys.Except(allowed).Any() || new[] { "eventTime", "eventType", "userName" }.Except(report.Columns.Keys).Any() ||
                report.Columns.Values.Any(v => !headers.Contains(v, StringComparer.Ordinal)))
                throw new AuditTrailException("اضبط AuditTrail:Repositories:Columns لتطابق أسماء أعمدة التصدير الفعلية؛ eventTime وeventType وuserName إلزامية.");
            var columns = report.Columns.ToDictionary(p => p.Key, p => Array.IndexOf(headers, p.Value));
            var events = new List<AuditEvent>();
            while (!parser.EndOfData)
            {
                var row = parser.ReadFields();
                if (row is null || row.Length != headers.Length || events.Count >= maxRows)
                    throw new AuditTrailException("تقرير التدقيق غير مكتمل أو تجاوز حد الصفوف؛ لم تُحسب نتائج جزئية.");
                string Get(string field) => columns.TryGetValue(field, out var index) ? row[index] : "";
                var timestamp = Get("eventTime");
                DateTimeOffset time;
                var style = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
                var valid = report.DateFormat != null
                    ? DateTimeOffset.TryParseExact(timestamp, report.DateFormat, CultureInfo.InvariantCulture, style, out time)
                    : DateTimeOffset.TryParseExact(timestamp, new[] { "O", "yyyy-MM-ddTHH:mm:ssK", "yyyy-MM-ddTHH:mm:ss.FFFFFFFK", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF" }, CultureInfo.InvariantCulture, style, out time);
                if (!valid || string.IsNullOrWhiteSpace(Get("eventType")))
                    throw new AuditTrailException("تاريخ أو نوع حدث غير صالح في تصدير التدقيق؛ اضبط DateFormat أو التقرير.");
                var idText = Get("entryId");
                int? id = null;
                if (!string.IsNullOrWhiteSpace(idText))
                {
                    if (!int.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 0)
                        throw new AuditTrailException("رقم إدخال غير صالح في تقرير التدقيق.");
                    if (number > 0) id = number;
                }
                events.Add(new AuditEvent(time, Get("eventType"), Get("userName"), id, Get("entryName"), Get("outcome")));
            }
            return events;
        }
        catch (FormatException) { throw new AuditTrailException("صيغة DateFormat غير صالحة في إعدادات التدقيق."); }
        catch (MalformedLineException) { throw new AuditTrailException("صيغة CSV لتقرير التدقيق غير صالحة؛ لم تُعرض نتائج جزئية."); }
    }
}
