using Npgsql;

namespace LaserficheReports.Web;

internal sealed record DatabaseProblem(string Error, string Message, string SqlState);
internal static class DatabaseDiagnostics
{
    internal static DatabaseProblem Describe(PostgresException error)
    {
        if (error.MessageText.Contains("ENOIDENTIFIER", StringComparison.OrdinalIgnoreCase) ||
            error.MessageText.Contains("no tenant identifier", StringComparison.OrdinalIgnoreCase))
            return new("supabase_tenant_identifier_missing",
                "تعذر اتصال Supabase: وسيط الاتصال لم يتعرف على المستأجر. في Supabase المحلي عبر Supavisor، " +
                "اضبط Username=postgres.<POOLER_TENANT_ID> بالقيمة الفعلية من ملف .env الخاص بـ Supabase، " +
                "داخل Supabase:PostgresConnectionString في appsettings.Local.json، ثم أعد تشغيل التطبيق. " +
                "لا تضع YOUR_POOLER_TENANT_ID حرفيًا. عند الاتصال المباشر بـ PostgreSQL استخدم اسم مستخدم الاتصال المباشر.", error.SqlState);
        if (error.SqlState == "28P01")
            return new("supabase_authentication_failed", "رفضت قاعدة البيانات بيانات الدخول. تحقق من اسم المستخدم وكلمة المرور في إعداد اتصال Supabase.", error.SqlState);
        return new("document_search_failed", "تعذر تنفيذ طلب قاعدة البيانات. راجع سجل التطبيق وإعداد اتصال Supabase/PostgreSQL.", error.SqlState);
    }
}
