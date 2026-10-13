# مثبّت تقارير ليزرفيش الذكية

## المخرجات

البناء يستخدم WiX 4.0.5 مثل مثبّت Dashboard، مع MSI ومعالج EXE مستقلين بـUpgradeCodes خاصة بـReports. لا تستخدم هذه الحزمة أسماء IIS أو GUIDs أو ملفات Dashboard.

المخرجات في `artifacts/LaserficheReports-<version>/`:

- `LaserficheReports-Setup.exe`: المعالج ذو خطوات الترحيب، اتصال Laserfiche، إعدادات الموديل والمنافذ، المراجعة، التثبيت، والتحقق.
- `LaserficheReports.msi`: ملكية الملفات، الخدمة، الاختصار، الترقية والإزالة.
- `build-info.json` و`SHA256SUMS.txt` و`python-dependencies.txt`: الإصدار والمصدر وبصمات الحزمة واعتمادات Python الفعلية.

ملفات البرنامج في `%ProgramFiles%\LaserficheReports`. الإعدادات والسجلات في `%ProgramData%\LaserficheReports`. المثبّت يستخدم خدمة Windows باسم `LaserficheReports` بهوية LocalService لتشغيل تطبيق الويب والـplanner، مع إعادة تشغيل عملياتهما عند تعطلها. الخدمة تربط التطبيق والـplanner بـ127.0.0.1 فقط؛ لا يضيف المثبّت IIS ولا ANCM ولا جدارًا ناريًا عامًا.

## البناء

على جهاز بناء Windows x64: .NET 8 SDK، Python 3.11 x64 في PATH، Git، واتصال بتنزيل NuGet وPyPI والمتطلب .NET Framework. لا تُطلب هذه أدوات البناء من الجهاز الثاني.

```powershell
git pull --ff-only origin fix/typed-semantic-repository-plans
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build\publish.ps1 -Version 1.0.1
```

استخدم رقم إصدار جديدًا من ثلاثة أجزاء لكل إصدار موزع. البناء يرفض مجلد إصدار موجودًا أو سورس غير ملتزم به، وينظف bin/obj الخاصة بالـBA وأفعال MSI قبل البناء. تُقارن بصمات DLLs المنسوخة إلى staging بمخرجات البناء. يُجمد الـplanner مع Python واعتماداته باستخدام PyInstaller؛ لا يُنسخ venv من جهاز التطوير. فحص `--help` يشغّل الملف المجمد نفسه قبل إنتاج MSI. لا يُعلن نجاح عند فشل أي مرحلة.

إعدادات جهاز المطور وكلمات المرور وملفات `appsettings.Local.json` وconfig لا تدخل الحمولة. ملف `other` لم يكن موجودًا في فرع Reports أو main وقت إنشاء هذا التغيير؛ لا توجد إعدادات جهاز ثانٍ مختلقة أو مستوردة منه. أرفق الملف أو التزم به قبل طلب تطبيق محتواه.

## الجهاز الثاني

1. إذا سبق استخدام المثبّت القديم ذي مهمة تسجيل الدخول، شغّل `deployment\windows\unregister.ps1` تحت نفس المستخدم. هذا يوقف Reports فقط ويحفظ ملفاته وإعداداته. المثبّت الجديد يرفض بقاء المهمة القديمة لتجنب نسختين على نفس المنافذ. احتفظ بنسخة من الإعدادات القديمة.
2. ثبّت Ollama ووفر النموذج محليًا. مثال عند توفر الإنترنت: `ollama pull qwen2.5:14b`. النموذج ليس داخل Git أو EXE. تشغيل Ollama وإتاحة الموديل مسؤولية البيئة؛ المثبّت لا يزيل Ollama ولا يستولي على عملياته.
3. شغّل `LaserficheReports-Setup.exe`. أدخل عنوان خادم API الحقيقي وApiBasePath وإصدار API والمستودع الافتراضي وعنوان Web Client. عنوان localhost هو الجهاز الحالي؛ استخدم اسم الخادم حين يوجد Laserfiche على جهاز آخر.
4. اختر موديل Ollama الموجود ومنافذ Reports والـplanner. الإعدادات الحالية تُقرأ عند التحديث ولا تُستبدل افتراضيًا بـlocalhost أو testemployee. لا تُجمع كلمة مرور في معالج التثبيت؛ سجّل الدخول داخل التطبيق.
5. المعالج يفرق بين نجاح معاملة MSI وجاهزية تطبيق الويب والـplanner. إذا لم يكن Ollama أو الموديل جاهزًا يعرض ذلك بوضوح ولا يدعي نجاح تقرير حقيقي. افتح Reports من Start Menu واختبر سؤالًا على مستودعك.

التوزيع ليس مثبتًا offline مضمونًا: .NET Framework 4.8 قد يُنزّل إذا كان مفقودًا، وOllama والموديل يجب توفيرهما مسبقًا. تشغيل الخدمة قبل تسجيل الدخول لا يكفي وحده لعمل AI؛ يجب أن يكون Ollama متاحًا لتلك البيئة أيضًا. لا تتجاوز أخطاء TLS بتعطيل التحقق من الشهادات. لا يثبّت Reports شهادة ولا يغير إعدادات API Server أو حساباته.

## الإعدادات والترقية والإزالة

`laserfiche.config.json` يحمل إعدادات الاتصال. `hosting.config.json` يحمل Port وGraphPort وModel وOllamaUrl. ملفات runtime وبيانات الاعتماد والإعدادات غير المعروفة تُحفظ. إصلاح MSI بلا profile يحافظ على الاتصال والمنافذ كما هي. تغييرات إعدادات الخدمة تتطلب إعادة تشغيل خدمة Reports.

كتابة الإعدادات ذرية، ومعها journal للمعاملة. rollback يعيد الملف فقط إذا ظل مطابقًا لكتابة هذه المعاملة، ولا يطمس تعديلًا متزامنًا. MSI يزيل الملفات المملوكة له فقط والخدمة الخاصة به، ويحفظ إعدادات Reports والملفات غير المعروفة. لا توجد إزالة تكرارية لجذر التثبيت أو بيانات Laserfiche، ولا حذف لنماذج أو قواعد بيانات أو شهادات أو IIS أو .NET مشتركة. حارس فوري يمنع المسارات البديلة والروابط وhard links واسم خدمة مستخدمًا من برنامج آخر قبل التعديل.

تكامل زر Laserfiche الحالي يبقى إعدادًا مستقلًا؛ الحزمة لا تعدّل Browse.aspx أو Desktop Client. لا تعتبر ترقية المثبّت القديم تلقائية؛ إيقاف المهمة القديمة خطوة انتقال واضحة.

## التحقق

`GET /api/app/status` يعرض build.Version وbuild.SourceCommit ووقت تشغيل العملية. استجابة static files تطلب revalidation كي تظهر الهوية الجديدة بعد التحديث.

```powershell
Invoke-RestMethod http://127.0.0.1:5187/api/app/status | ConvertTo-Json -Depth 8
```

اختبارات Windows على آلة قابلة للتخلص منها:

```powershell
.\installer\Test-ReportsInstallation.ps1 -MsiPath .\artifacts\LaserficheReports-1.0.1\LaserficheReports.msi -DisposableMachine
```

هذه الدورة تختبر install/repair/rollback/major-upgrade/uninstall، واستجابة HTTP الحقيقية من التطبيق المثبت، تشغيل الـplanner المجمد، نسخة المصدر والخطوط، وحفظ الملفات غير المعروفة والإعدادات. ترقية fixture تستخدم نفس الحمولة برقم MSI أعلى؛ لا تثبت الترقية من مثبت Dashboard أو من جميع مثبتات Reports السابقة. اختبارات الجلسات تستخدم API محاكى وتغطي 30 دورة تجديد وطلبات متزامنة و401 قديمًا ورفض9030. اختبار تقرير حقيقي وصحة نتائج Laserfiche، إعادة إقلاع الجهاز دون login، وتجربة المعالج تفاعليًا على الجهاز الثاني يجب إتمامها في بيئة المستخدم؛ لا تُختلق نتائج هذه الفحوص.

السجلات: `%ProgramData%\LaserficheReports\logs\web.log` و`planner.log` و`planner-trace.jsonl`. لا تشارك كلمات المرور أو التوكنات. تشخيص توقف النتائج بعد فترة يحتاج HTTP status ووقت حدوثه وإصدار API Server، لأن idle-session في Laserfiche قد ينتهي قبل expires_in.
