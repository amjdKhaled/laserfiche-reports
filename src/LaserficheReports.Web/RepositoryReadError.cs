using LaserficheReports.Domain.Exceptions;
namespace LaserficheReports.Web;

internal static class RepositoryReadError
{
    internal static string Message(LaserficheException error) => error.StatusCode switch
    {
        401 => "انتهت مصادقة Laserfiche أو رفض بيانات الدخول. أعد تسجيل الدخول إلى المستودع.",
        403 => "رفض Laserfiche صلاحية قراءة البيانات المطلوبة لهذا الحساب.",
        404 => "لم يجد Laserfiche المستودع أو الوثيقة المطلوبة؛ قد تكون حُذفت أو نُقلت.",
        502 or 503 or 504 => "تعذرت قراءة البيانات الحية لأن خادم Laserfiche أو بوابته لم يستجب بنجاح. لم تُعرض نتائج غير مؤكدة؛ أعد المحاولة.",
        _ => "أرجع Laserfiche خطأ أثناء قراءة البيانات الحية. لم تُعرض نتائج غير مؤكدة. راجع سجل الطلب لتحديد السبب."
    };
}
