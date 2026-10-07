"""Held-out Arabic planner acceptance cases; never imported by the runtime planner."""
import copy

CATALOG = {"fields": [{"name": name, "fieldType": kind} for name, kind in [
    ("أجل الإنجاز", "Date"), ("القسم", "String"), ("حالة المعاملة", "String"),
    ("المسؤول", "String"), ("قيمة الطلب", "Number")]], "templates": ["مكاتبات", "طلبات"],
    "entryProperties": ["entryId", "name", "created", "modified", "template", "creator", "pageCount"]}


def leaf(field, operator, value=None, relative=None):
    return {"field": field, "operator": operator, **({"value": value} if value is not None else {}),
            **({"relative": relative} if relative else {})}


def date(unit="day", offset=0, boundary="start"):
    return {"unit": unit, "offset": offset, "boundary": boundary}


OVERDUE = leaf("أجل الإنجاز", "less_than", relative=date())
CASES = []

def add(questions, expect, history=None, catalog=None):
    for question in questions:
        CASES.append({"question": question, "catalog": copy.deepcopy(catalog or CATALOG), "today": "2026-10-07",
                      "history": history or [], "expect": copy.deepcopy(expect)})

add(["كم ملف عندنا الحين؟", "عطني إجمالي المستندات بدون سردها", "أحتاج تعداد الوثائق الحالية"], {"operation": "search", "countOnly": True})
add(["اعطيني تقرير عن الوثائق المنتهي موعد تسليمها", "أحتاج كشف بأسماء المستندات التي تجاوزت المهلة، كلها", "جهز لي تقرير بالملفات المتأخرة واحد واحد"],
    {"operation": "search", "filters": OVERDUE, "allResults": True, "countOnly": False})
add(["ورني أول خمس وثائق تجاوزت أجلها"], {"operation": "search", "filters": OVERDUE, "allResults": False, "limit": 5})
add(["وش اللي تعدى وقت إنجازه؟", "هل فيه مستندات فاتت المهلة المحددة لها؟", "أطلع العناصر اللي كان مفروض تنتهي قبل اليوم", "أود حصر الوثائق ذات المواعيد المنقضية"], {"operation": "search", "filters": OVERDUE})
for field in ["آخر موعد", "تاريخ الاستحقاق", "موعد التسليم"]:
    schema = copy.deepcopy(CATALOG); schema["fields"][0]["name"] = field
    add(["أبي الملفات اللي تجاوزت أجلها"], {"filters": leaf(field, "less_than", relative=date())}, catalog=schema)
add(["المتأخر حق قسم المحاسبة بس", "هات ما تجاوز الأجل ويخص المحاسبة"], {"filters": {"logic": "and", "conditions": [OVERDUE, leaf("القسم", "equals", "المحاسبة")]}})
add(["طلبات المحاسبة أو المشتريات، أي وحدة منهم", "اجمع ملفات قسمي المشتريات والمحاسبة في قائمة وحدة"], {"filters": {"logic": "or", "conditions": [leaf("القسم", "equals", "المحاسبة"), leaf("القسم", "equals", "المشتريات")]}})
add(["استبعد الحالة مغلق من القائمة", "وش باقي إذا شلنا كل شيء حالته مغلق؟"], {"filters": leaf("حالة المعاملة", "not_equals", "مغلق")})
add(["طلع اللي خانة المسؤول عندهم فاضية", "أين الوثائق التي لم تعبأ قيمة المسؤول فيها؟"], {"filters": leaf("المسؤول", "is_empty")})
add(["قيمة الطلب تتراوح بين 50 و 900", "أحتاج الطلبات بقيمة لا تقل عن 50 ولا تزيد عن 900"], {"filters": {**leaf("قيمة الطلب", "between", "50"), "upper": "900"}})
add(["مين أكثر قسم عنده أوراق؟", "رتب الأقسام بحسب كمية المستندات"], {"operation": "group", "groupFields": [{"field": "القسم"}], "metrics": [{"function": "count"}]})
add(["أي قالب طاغي على المستودع؟", "ورني توزيع استخدام القوالب"], {"operation": "group", "groupFields": [{"field": "template"}]})
add(["أبي عدد المعاملات لكل مسؤول ولكل حالة", "قارن حالات المعاملات حسب المسؤولين"], {"operation": "group", "groupFields": [{"field": "المسؤول"}, {"field": "حالة المعاملة"}]})
add(["متوسط قيمة الطلب لكل قسم", "احسب المعدل المالي للطلبات بحسب أقسامها"], {"operation": "group", "groupFields": [{"field": "القسم"}], "metrics": [{"function": "average", "field": "قيمة الطلب"}]})
add(["كيف تغير عدد الوثائق المنشأة من شهر لشهر؟"], {"operation": "group", "groupFields": [{"field": "created", "bucket": "month"}]})
add(["ورني أقدم الأوراق أول شيء", "خل ترتيب المستندات حسب دخولها من زمان إلى الآن"], {"sort": "creationTime asc"})
add(["وش أحدث إضافة عندنا؟"], {"operation": "latest_created", "limit": 1})
add(["رتب الطلبات حسب قيمتها من الكبير للصغير"], {"sortField": "قيمة الطلب", "sortDirection": "desc"})
history = [{"role": "user", "text": "اعرض الملفات التي قسمها المحاسبة وحالة المعاملة فيها مفتوح"}]
add(["رتبها بالأقدم"], {"filters": {"logic": "and", "conditions": [leaf("القسم", "equals", "المحاسبة"), leaf("حالة المعاملة", "equals", "مفتوح")]}, "sort": "creationTime asc"}, history)
add(["طيب كم عددها؟"], {"countOnly": True, "filters": {"logic": "and", "conditions": [leaf("القسم", "equals", "المحاسبة"), leaf("حالة المعاملة", "equals", "مفتوح")]}}, history)
add(["ابي معلومات الإدخال 618", "وش بيانات 618 المسجلة الآن؟"], {"operation": "metadata", "entryIds": [618]})
add(["متى انضافت الوثيقة اللي اسمها Email؟"], {"operation": "metadata", "name": "Email", "requireUnique": True})
add(["عطني محتويات المجلد رقم 42"], {"folderId": 42, "entryType": "all"})
add(["وش مسار المجلد 42؟"], {"operation": "folder_information", "folderId": 42})
add(["أي قوالب متاحة عندنا؟"], {"operation": "templates"})
add(["أبي الأوراق اللي قالبها طلبات"], {"template": "طلبات"})
add(["اشرح لي وش مكتوب في المستند 618", "هل نص الوثيقة 618 يذكر غرامة؟"], {"content": True, "entryIds": [618]})
add(["لخص محتوى الملفات التي تأخر إنجازها", "وش تقول الأوراق المتجاوزة لأجلها عن الغرامات؟"], {"operation": "search", "content": True, "filters": OVERDUE})
add(["ملفات قسم المشتريات التي تتكلم عن مخاطر مالية"], {"operation": "search", "content": True, "filters": leaf("القسم", "equals", "المشتريات")})
add(["كم أنشأنا مستند أمس؟"], {"countOnly": True, "filters": {"logic": "and", "conditions": [leaf("created", "greater_or_equal", relative=date(offset=-1)), leaf("created", "less_than", relative=date())]}})
add(["أبي الملفات اللي محد عدلها من أكثر من شهر"], {"filters": leaf("modified", "less_than", relative=date("month", -1, "rolling"))})
add(["عد لي الوثائق اللي دخلت الأسبوع الماضي"], {"countOnly": True, "filters": {"logic": "and", "conditions": [leaf("created", "greater_or_equal", relative=date("week", -1)), leaf("created", "less_than", relative=date("week", 0))]}})

add(["وش متوسط عدد الملفات المنشأة في الشهر؟"], {"operation": "group", "groupFields": [{"field": "created", "bucket": "month"}], "metrics": [{"function": "count"}], "rollup": "average"})
add(["أي أسماء وثائق تتكرر أكثر من مرة؟"], {"operation": "group", "groupFields": [{"field": "name"}], "metrics": [{"function": "count"}], "having": {"metric": 0, "operator": "greater_than", "value": 1}})
