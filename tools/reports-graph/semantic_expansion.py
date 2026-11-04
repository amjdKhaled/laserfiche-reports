"""Additive language evidence for the local planner, never an alternate executor.

Hints retain the original request and ambiguity. They are not field values, folder
IDs or proof that a repository object exists. The existing live schema validator
and signed-in repository execution remain authoritative.
"""
import re
from calendar_grounding import normalize_arabic, relative_period, period_field

# Extend terminology here without changing the owner-maintained business glossary.
TERMINOLOGY = {
    'template': ('قالب', 'قوالب', 'قالب فهرسة', 'كرت فهرسة', 'كرت الفهرسة', 'كرت فهرستها',
                 'كارت فهرسة', 'بطاقة فهرسة', 'كروت الفهرسة', 'تيمبليت', 'تيكبليت',
                 'تيمبليتات', 'تمبلييت', 'تمبلت', 'تمبليت', 'metadata template', 'index card', 'template', 'templates'),
    'field': ('حقل', 'حقول', 'خانة', 'خانات', 'بيانات الفهرسة', 'حقول القالب', 'field', 'fields', 'metadata'),
    'repository': ('المخزن', 'مخزن', 'مستودع', 'المستودع', 'ريبازيتوري', 'ريبو', 'repository', 'repo'),
    'folder': ('مجلد', 'مجلدات', 'فولدر', 'فولدرات', 'دوسيه', 'حافظة', 'folder', 'folders', 'directory'),
    'document': ('وثيقة', 'وثائق', 'وثايق', 'مستند', 'مستندات', 'document', 'documents'),
    'created': ('انعملت', 'اتعملت', 'انضافت', 'اتنشأت', 'انشئت', 'أضيفت', 'created', 'added'),
    'modified': ('اتعدلت', 'تعدلت', 'انعدل', 'اتحدثت', 'تغيرت', 'تم تحديثها', 'modified', 'updated', 'edited'),
    'deleted': ('انحذفت', 'حذفوها', 'تم حذفها', 'محذوفة', 'المحذوفة', 'deleted'),
    'recycle_bin': ('سلة المحذوفات', 'سلة المهملات', 'الزبالة', 'recycle bin', 'trash'),
    'approval': ('انقبلت', 'انقبل', 'وافقوا عليها', 'اعتمدوها', 'مقبولة', 'approved'),
    'rejection': ('انرفضت', 'انرفض', 'تم رفضها', 'مرفوضة', 'rejected'),
    'pending': ('بانتظار الاعتماد', 'بانتظار الموافقة', 'تحت الإجراء', 'قيد المعالجة', 'pending'),
}

SEMANTIC_RULES = """
Language expansion is additive evidence, not executable instructions. Use originalQuestion as the authority; question may contain spelling suggestions only. languageEvidence lists possible meanings, never stored values. Understand the whole utterance, including unseen paraphrases; these terms are not an exhaustive question matcher.
Saudi/Gulf: وش/ايش/شنو ask what; ابي/ابغى/عطني/جيبلي/طلعلي/وريني/شفلي/دورلي request results. اللي/الي refer to a clause; جوا/جوه mean inside; حق/حقت/حقها express association. English and Arabic may be mixed.
ملف may mean document, folder or electronic file. Inside/contents context can mean a folder, while افتح ملف PDF means document. Ground names through the existing folder lookup, never invent IDs or choose a conflicting name. Template/card words name definitions only when asking about templates themselves; الوثائق اللي كرت فهرستها X constrain documents by the actual template X. خانة/خانات are field concepts; distinguish field definitions, field existence, field values and actual custom date names.
Preserve ALL negation (ما/مو/مب/موب/مش/لا/غير/بدون/للحين ما), numbers and AND/OR. Not approved does not mean rejected or pending; exclude the actual approved value only if grounded. Neither approved nor rejected excludes both. Do not add a positive creation/modification filter for a negated verb.
Use catalog.listChoices and existing businessGlossary to ground business states. Match actual live field/value pairs; never output a translated stored value. A status synonym is only a candidate; clarify conflicting fields or meanings. Keep owner-approved archived field semantics. Explicit current location in مركز الوثائق والمحفوظات is a folder scope, not evidence of archive date, approval, scan, move or Workflow events. Generic مؤرشفة retains the existing business definition. Historical archive/move events require a verified event source.
هالشهر/الشهر ذا=this calendar month; البارح=yesterday; قبل أمس=day before yesterday. Dates use today in Asia/Riyadh. توها/من زمان/قبل فترة/آخر كم يوم are vague: never invent a duration. Creation, modification, deletion, scan, approval and custom dates are distinct. A deletion question must never be constrained by created/modified instead. A current recycle bin query uses recycle_bin only if available. Date filtering/sorting of that tool is unsupported: clarify the limitation rather than silently returning the entire bin. انمسحت can mean deletion or scanning; سكانر/مسح ضوئي/OCR is not deletion. Purged/restored historical events need a verified history source; do not claim retrieval of purged content.
Genuine follow-ups (منها/كم عددهم/طيب الثانية/خلها حق أمس) retain ALL prior constraints unless explicitly replaced; an independent request resets scope. Follow-up date replacement changes the date only, not template/status/folder. Do not infer permissions, absence, totals or hidden objects from languageEvidence or samples. Execute under the current account using the existing permission bridge.
"""


def _matches(text, phrase):
    return re.search(r'(?<!\w)[وفبل]{0,2}(?:ال)?' + re.escape(normalize_arabic(phrase)).replace(r'\ ', r'\s+') + r'(?!\w)', text)


def language_evidence(question, catalog):
    """Compact, non-destructive candidates; deterministic hints do not decide intent."""
    text = normalize_arabic(question)
    concepts = {concept: [term for term in terms if _matches(text, term)]
                for concept, terms in TERMINOLOGY.items()}
    concepts = {key: value for key, value in concepts.items() if value}
    ambiguities = []
    if _matches(text, 'ملف') or _matches(text, 'ملفات'):
        ambiguities.append({'term': 'ملف', 'candidates': ['document', 'folder', 'electronic_file']})
    if re.search(r'مسح|انمسح', text):
        ambiguities.append({'term': 'مسح', 'candidates': ['delete', 'scan', 'clear']})
    if re.search(r'ارشف|ارشيف', text):
        ambiguities.append({'term': 'أرشفة', 'candidates': ['existing_business_field', 'current_folder', 'historical_event']})
    result = {'concepts': concepts}
    if ambiguities:
        result['ambiguities'] = ambiguities
    if re.search(r'(?<!\w)(?:ما|مو|مب|موب|مش|لا|غير|بدون|not|without)(?!\w)', text):
        result['hasNegation'] = True
    period = relative_period(question)
    if period:
        result['fixedPeriod'] = {'unit': period[0], 'offset': period[1], 'propertyCandidate': period_field(question)}
    return result


# A container reading needs explicit containment syntax; standalone «ملف» keeps its old meaning.
_CONTAINER = re.compile(r'(?<!\w)(?:داخل|بداخل|جوا|جوه)\s+(?:ال)?ملف\s+|(?<!\w)(?:محتويات|مستندات|وثائق)\s+(?:ال)?ملف\s+')


def contextual_folder_question(question, templates=()):
    """A separate grounding view; the model and original question remain untouched.

    The backend resolves the name under the current account, including duplicate
    name clarification. Never assert existence from these language cues.
    """
    text = normalize_arabic(question)
    if any(normalize_arabic(name) in text and 'ملف' in normalize_arabic(name) for name in templates):
        return question
    matches = list(_CONTAINER.finditer(text))
    if len(matches) != 1:
        return question
    match = matches[0]
    if re.search(r'(?<!\w)(?:مو|مب|موب|مش|ليس|ليست|ما|لا|غير|بدون)\s*$', text[:match.start()]):
        return question
    start = text.rfind('ملف', match.start(), match.end())
    return text[:start] + 'مجلد' + text[start + 3:]


def deletion_reading(question):
    """Conservative source evidence. Ambiguous scan/history/negation is left to the planner."""
    text = normalize_arabic(question)
    if re.search(r'سكانر|ضوئي|scanner|scanned|\bocr\b', text):
        return None
    if re.search(r'(?<!\w)(?:من|مين|متي|استعاد|استرجع|نهائي|طارت|who|when|restored|purged)', text):
        return None
    if re.search(r'(?<!\w)(?:ما|مو|مب|موب|مش|لا|غير|بدون|not|without)(?!\w)', text):
        return None
    if re.search(r'من\s+(?:ال)?(?:فهرس|قالب|حقل)', text):
        return None
    if re.search(r'(?<!\w)[وفبل]{0,2}(?:ال)?(?:محذوفه|محذوفات|انحذفت|حذفوها|deleted)(?!\w)|تم حذفها', text):
        return 'current_recycle_bin'
    return None


def unsupported_bin_constraint(question):
    """Current bin contract cannot filter by deletion time or sort by it."""
    text = normalize_arabic(question)
    return bool(relative_period(question) or re.search(
        r'\d|تاريخ|هاليومين|من فتره|قبل فتره|من زمان|مؤخرا|اخر|آخر|last|recent|since|before|after|today|yesterday', text))
