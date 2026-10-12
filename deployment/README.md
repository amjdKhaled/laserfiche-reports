# تشغيل تقارير ليزرفيش محليًا ونقلها

**الموديل الافتراضي: `gpt-oss:20b`.** للنقل الأسهل إلى Windows دون SDK أو
Visual Studio على الجهاز الثاني، استخدم [حزمة النقل](TRANSFER.md):
`deployment/windows/build-package.ps1` ثم فك ZIP وافتح `Install.cmd`.
الحزمة تفحص تحميل النموذج الحقيقي قبل تسجيل التشغيل التلقائي.

## Windows بدون Visual Studio

التثبيت مرة واحدة يحتاج .NET 8 SDK وPython 3.11 x64 وOllama المثبت عندك.
بعده يعمل البرنامج المنشور بدون Visual Studio أو dotnet run، ويبدأ تلقائيًا
عند دخول **نفس مستخدم Windows**. هذه مهمة بدء دخول، وليست خدمة تعمل قبل تسجيل الدخول.

1. أوقف تشغيل الويب و`start.ps1` القديمين بـ Ctrl+C لتفريغ المنافذ 5187 و8766.
2. تأكد أن نماذج Ollama التي تستخدمها موجودة وأن Laserfiche وSupabase يعملان.
3. من مجلد المشروع:

```powershell
powershell -ExecutionPolicy Bypass -File .\deployment\windows\install.ps1 -Model gpt-oss:20b
```

التثبيت في `%LOCALAPPDATA%\LaserficheReports`. ينسخ إعدادات `appsettings.Local.json`
الموجودة إذا لم توجد إعدادات في التثبيت، ويقرأ إعدادات ProgramData المعتادة.
لا يحذف أو ينقل ملفات Laserfiche. لا تسجل كلمة مرور حسابك في السكربت.
افتح اختصار **Laserfiche Reports** على سطح المكتب أو `http://127.0.0.1:5187`.
السجل: `%LOCALAPPDATA%\LaserficheReports\logs\services.log`.
المشرف يعيد تشغيل العملية المتوقفة؛ يستخدم Ollama الجاري أو يبدأه إذا لم يكن يعمل.

للتحديث: شغّل unregister أدناه، ثم `git pull` وأعد install. الإعدادات محفوظة.
لإيقاف البدء التلقائي والعمليات الخاصة بالتثبيت فقط:

```powershell
powershell -ExecutionPolicy Bypass -File .\deployment\windows\unregister.ps1
```

السكربت يحفظ الملفات والبيانات؛ لا يزيل Laserfiche أو Supabase أو نماذج Ollama.
التثبيت يحتاج أن تسمح سياسة Windows بتسجيل Scheduled Task للمستخدم الحالي.

## Docker Desktop / Docker Engine

الحاوية تجمع تطبيق .NET وخدمة Python وOllama المحلي، وتعرض الواجهة فقط على
`127.0.0.1:5187`. لا تنشر منفذ النموذج أو المخطط. يعمل CPU افتراضيًا.
لا تحتوي الحاوية Laserfiche Server ولا قاعدة بياناته ولا Supabase.
تتصل **بقاعدة Supabase الموجودة** دون إنشاء جدول documents جديد أو تغيير بياناته.
OCR خدمة اختيارية منفصلة، ويمكن ضبط `Ocr:BaseUrl` بعنوانها القابل للوصول.
تحويل صور TIFF/JPEG/BMP/GIF داخل الحاوية يستخدم Pillow محليًا.

```powershell
Copy-Item .\deployment\docker\appsettings.example.json .\deployment\docker\appsettings.Local.json
notepad .\deployment\docker\appsettings.Local.json
```

ضع عنوان Laserfiche الفعلي وسلسلة اتصال PostgreSQL/Supabase الحالية.
`localhost` داخل الحاوية يعني الحاوية، وليس Windows؛ لقاعدة البيانات على نفس
Windows استخدم `host.docker.internal` مع المنفذ المنشور الصحيح. لا تستعمل
عنوان PostgreSQL الخاص بقاعدة مستودع Laserfiche؛ المطلوب قاعدة Supabase فقط.
لعنوان Laserfiche HTTPS استخدم اسم DNS يطابق الشهادة. ضع شهادة CA العامة `.crt`
في `deployment/docker/certs` قبل البناء إن كانت الشهادة داخلية.
الإعداد المحلي مستبعد من Git ومن سياق البناء، ويربط للقراءة وقت التشغيل فقط.

```powershell
docker compose up -d --build
docker compose exec reports ollama pull gpt-oss:20b
docker compose exec reports ollama pull nomic-embed-text-v2-moe
```

ثم افتح `http://127.0.0.1:5187`. البناء وتنزيل النماذج يحتاجان إنترنت مرة واحدة؛
التشغيل بعدها محلي. `restart: unless-stopped` يبدأ الحاوية مع Docker Engine؛
فعّل بدء Docker Desktop مع دخول Windows. النماذج والإعدادات القابلة للكتابة
ومفاتيح حماية البيانات والسجلات في volumes مستقلة.

```powershell
docker compose exec reports sh -c 'tail -n 100 /app/logs/services.log'
docker compose stop
```

لا تستخدم `docker compose down -v` للاحتفاظ بالنماذج والإعدادات.
يمكن تثبيت نسخة Ollama معينة عند البناء:
`docker compose build --build-arg OLLAMA_IMAGE=ollama/ollama:<version>`.

## النقل لجهاز آخر دون إعادة بناء

على الجهاز الأول بعد نجاح البناء وتنزيل النماذج:

```powershell
docker image save -o reports-image.tar laserfiche-reports:local
docker compose exec reports tar -cf /tmp/reports-models.tar -C /models .
docker compose cp reports:/tmp/reports-models.tar .\reports-models.tar
```

انقل الأرشيفين و`compose.yaml` ومجلد `deployment/docker` مع الإعداد المحلي إلى
الجهاز الآخر، وعدّل عنوان Laserfiche وقاعدة Supabase إن تغيرا. حافظ على خصوصية
ملف الإعدادات. لا تحتاج مصدر المشروع لتشغيل صورة محملة باستخدام `--no-build`.

```powershell
docker image load -i reports-image.tar
docker compose create --no-build
docker compose cp .\reports-models.tar reports:/tmp/reports-models.tar
docker compose start
docker compose exec reports tar -xf /tmp/reports-models.tar -C /models
```

التسجيل في التطبيق يكون من جديد على الجهاز الآخر. إذا كنت تنقل Supabase نفسه،
خذ backup/restore لقاعدة Supabase وأعد عنوان الاتصال؛ أرشيف التطبيق لا يحتويها
ولا يحتوي أرشيف Laserfiche. لا تنقل ملفات مستودع Laserfiche باعتبارها ملفات تطبيق.

## حدود التحقق

اختبارات Python والواجهة والتحقق من الشيفرة تُشغّل أثناء التطوير. اختبار تثبيت
Windows وإقلاع Docker وربطهما بخدمات جهازك يحتاج البيئة الفعلية؛ نجاح اختبار
المخطط لا يضمن صحة كل تفسير لغوي. الاقتراحات تبقى أسئلة تأكيد ولا تنفذ بدائل
تلقائيًا أو تقدم عددًا تقديريًا على أنه نتيجة.
