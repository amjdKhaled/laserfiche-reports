using LaserficheReports.Domain.Exceptions;
namespace LaserficheReports.Web;

internal static class RepositoryReadError
{
    internal static string Message(LaserficheException error) => error.StatusCode switch
    {
        429 when error.LFErrorCode == "9030" => "رفض Laserfiche إنشاء جلسة (9030): وصل الخادم أو الحساب إلى حد الجلسات، أو لم يُخصص ترخيص Named User للحساب. أغلق الجلسات غير المستخدمة وتحقق من الترخيص في Administration Console، ثم أعد المحاولة بعد دقيقة.",
        429 => "رفض Laserfiche الطلب مؤقتًا بسبب كثرة الطلبات. انتظر قليلًا ثم أعد المحاولة.",
        401 => "انتهت مصادقة Laserfiche أو رفض بيانات الدخول. أعد تسجيل الدخول إلى المستودع.",
        403 => "ليس لديك صلاحية تنفيذ البحث أو الوصول إلى الوثيقة أو المجلد المطلوب بهذا الحساب (403).",
        404 => "لم يتم العثور على المستودع أو الوثيقة المطلوبة، أو أنها غير ظاهرة لهذا الحساب (404). لا يمكن الجزم بأنها محذوفة.",
        502 or 503 or 504 => "تعذرت قراءة البيانات الحية لأن خادم Laserfiche أو بوابته لم يستجب بنجاح. لم تُعرض نتائج غير مؤكدة؛ أعد المحاولة.",
        _ => "أرجع Laserfiche خطأ أثناء قراءة البيانات الحية. لم تُعرض نتائج غير مؤكدة. راجع سجل الطلب لتحديد السبب."
    };
}
