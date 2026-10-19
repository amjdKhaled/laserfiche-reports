"""Trusted local planning guidance derived from the Laserfiche 12 search reference.

Source: https://doc.laserfiche.com/laserfiche.documentation/12/userguide/en-us/content/search-syntax.htm
The model returns typed plans. Only the backend compiles and executes syntax.
"""

SEARCH_REFERENCE = """
Laserfiche 12 search semantics (trusted reference; not repository data):
Metadata searches span templates: {[]:[FieldName]="Value"}; a template alone is
{[TemplateName]}. A field value condition is different from a template condition.
Blank fields use an empty value, while wildcard matching excludes blanks.
Map requests for documents containing/populated with a field to is_not_empty;
no exact value is needed. Empty/unfilled fields use is_empty.
Entry names use LF:Name, IDs LF:ID, page counts LF:PageCount, creation LF:Created,
last modification LF:Modified. Metadata dates remain separate from these properties.
AND means every condition; OR means any condition, with explicit grouping.
Numeric/date comparisons preserve <, <=, > and >=. A full calendar year is a
period, never a date value equal to the four-digit year. Dates in our JSON are ISO;
the backend handles syntax formatting. Counts, sorting and aggregation are backend
operations, not raw search syntax. Do not emit raw syntax or unsupported operators.
Only names and types in the LIVE catalog may be used, regardless of examples here.
"""

SEMANTIC_FIELDS = """
اختَر الحقل بحسب المعنى الكامل؛ المستخدم لا يحتاج كتابة الاسم حرفيًا.
طبّق هذا على كل الحقول دون قائمة أسماء ثابتة: تجاهل التشكيل والهمزة والمسافات
والشرطة/الشرطة السفلية عند فهم الاسم، وافهم تصريف الكلمة والمرادفات من السياق.
مثلًا الاسم الذي يصف نشاطًا ليس اسمًا يصف حفظًا، ولو تشاركا المكان والتاريخ.
المكان داخل اسم الحقل لا يطلب فلتر مجلد. الحقل الذي يطابق المعنى والنوع والتقويم
يكفي ولا تسأل لمجرد وجود حقل قريب أقل ملاءمة. عند وجود نسخ تختلف فقط في تهجئة
الاسم أو مسافاته، وسؤال المستخدم عام عن هذه الخاصية دون تحديد نسخة، يمكن استخدام
OR لفحص النسخ المتكافئة الموجودة فعلًا؛ لا تختَر نسخة اعتباطيًا ولا تخلط النشاط
بالحفظ أو الميلادي بالهجري أو حقولًا تصف إجراءات مختلفة.
«تحتوي على حقل» تعني أن له قيمة غير فارغة، وليست طلبًا لقيمة مفقودة.
طلب العدد فقط => search مع countOnly=true، دون groupFields أو metrics.
طلب تعريف الحقول والقوالب => schema، لا بحث وثائق ولا طلب اسم حقل من المستخدم.
«هذا الشهر» فترة نسبية محسوبة من today، دون سؤال عن التاريخ أو اسم الحقل.
"""
