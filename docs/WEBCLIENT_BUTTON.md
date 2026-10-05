# زر التقارير الذكية داخل ليزرفيش

يضيف التكامل زر **التقارير الذكية** بجانب Dashboard في الشريط العلوي لـ Laserfiche Web Client، باستخدام `rightNavbar` الموجود في التخصيص الحالي. يفتح تطبيق التقارير في تبويب جديد ويمرر اسم المستودع من `WebAccessRepositoryName`. لا يبدأ خدمة التقارير؛ يجب تشغيل المشروع أولًا.

من مجلد المشروع على جهاز Web Client، افتح PowerShell بصلاحية المسؤول وشغّل:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\deploy-webclient-button.ps1 -ReportsUrl "http://localhost:5187/"
```

ثم حدّث صفحة ليزرفيش باستخدام **Ctrl+F5**. السكربت يحتفظ بنسخ احتياطية مؤرخة من الملفات التي يعدّلها، ولا يغير زر Dashboard. إعادة تشغيله تحدّث رابط الزر ولا تضيف زرًا مكررًا.

إذا كان مسار Web Client مختلفًا، استخدم Physical Path الخاص بتطبيق Laserfiche في IIS:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\deploy-webclient-button.ps1 -ReportsUrl "http://REPORTS-SERVER:5187/" -WebClientPath "D:\Laserfiche\Web Files"
```

`localhost` مناسب عند فتح المتصفح على جهاز التقارير نفسه. للمستخدمين على أجهزة أخرى، ضع عنوان خدمة التقارير الداخلي الذي يستطيعون الوصول إليه، وتأكد أن الخدمة تستمع على هذا العنوان. هذا التكامل محلي ولا يحتاج خدمة سحابية.

اختيار المستودع من الرابط لا يمنح صلاحية الدخول. إذا لم توجد جلسة تقارير صالحة للمستودع نفسه، تظهر شاشة تسجيل الدخول مع اختيار المستودع القادم من ليزرفيش. لا ينقل الزر كلمة المرور أو رمز جلسة ليزرفيش.

للإزالة:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\deploy-webclient-button.ps1 -Remove
```

بعد تحديث أو إعادة تثبيت Web Client، قد تحتاج لإعادة تشغيل سكربت الإضافة. يعتمد هذا التخصيص على `Browse.aspx` و`rightNavbar` في تثبيتك الحالي؛ اختبر ظهور الزر بجانب Dashboard وفتح المستودع الصحيح على جهازك بعد النشر.
