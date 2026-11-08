namespace Amwag.PrinterSetup.App.Localization;

public sealed partial class Strings
{
    private static void AddDriverAndQueueFindings(CatalogBuilder c)
    {
        Finding(c, "DriverReady",
            "Driver is ready", "The approved printer driver is installed.",
            "برنامج التشغيل جاهز", "برنامج تشغيل الطابعة المعتمد مثبّت.");
        Finding(c, "DriverMissing",
            "Driver not installed", "The approved printer driver will be installed.",
            "برنامج التشغيل غير مثبّت", "سيتم تثبيت برنامج تشغيل الطابعة المعتمد.");
        Finding(c, "DriverOutdated",
            "Driver is out of date", "A newer approved version of the printer driver will be installed.",
            "برنامج التشغيل قديم", "سيتم تثبيت إصدار أحدث معتمد من برنامج تشغيل الطابعة.");
        Finding(c, "DriverPackageUnavailable",
            "Driver could not be obtained", "The approved driver was not found next to the setup and could not be downloaded. Check the internet connection or contact IT.",
            "تعذّر الحصول على برنامج التشغيل", "لم يتم العثور على برنامج التشغيل المعتمد بجانب أداة الإعداد وتعذّر تنزيله. تحقّق من الاتصال بالإنترنت أو تواصل مع فريق تقنية المعلومات.");
        Finding(c, "DriverPackageIntegrityFailed",
            "Driver file is damaged", "The driver file does not match the approved copy, so it was not used. Contact IT.",
            "ملف برنامج التشغيل تالف", "ملف برنامج التشغيل لا يطابق النسخة المعتمدة، لذلك لم يُستخدم. تواصل مع فريق تقنية المعلومات.");
        Finding(c, "DriverSignatureInvalid",
            "Driver signature is not trusted", "The driver's digital signature could not be verified, so it was not installed. Contact IT.",
            "توقيع برنامج التشغيل غير موثوق", "تعذّر التحقق من التوقيع الرقمي لبرنامج التشغيل، لذلك لم يُثبَّت. تواصل مع فريق تقنية المعلومات.");
        Finding(c, "DriverPackageNotApproved",
            "Driver not approved", "This driver has not been approved by IT, so it was not installed.",
            "برنامج التشغيل غير معتمد", "لم يعتمد فريق تقنية المعلومات برنامج التشغيل هذا، لذلك لم يُثبَّت.");
        Finding(c, "DriverPackageIncompatible",
            "Driver does not fit this computer", "The approved driver is not compatible with this version of Windows. Contact IT.",
            "برنامج التشغيل غير متوافق مع هذا الجهاز", "برنامج التشغيل المعتمد غير متوافق مع إصدار Windows هذا. تواصل مع فريق تقنية المعلومات.");
        Finding(c, "DriverInstallFailed",
            "Driver installation failed", "Windows could not install the printer driver. Restart the computer and try again.",
            "فشل تثبيت برنامج التشغيل", "تعذّر على Windows تثبيت برنامج تشغيل الطابعة. أعد تشغيل الجهاز وحاول مرة أخرى.");
        Finding(c, "DeviceDriverNotBound",
            "USB printer not linked to the driver", "Windows has not linked the USB printer itself to the approved driver. Unplug the USB cable, plug it back in, and run the setup again. If this stays, make the driver files or internet access available, or contact IT.",
            "طابعة USB غير مرتبطة ببرنامج التشغيل", "لم يربط Windows طابعة USB نفسها ببرنامج التشغيل المعتمد. افصل كابل USB ثم أعد توصيله وشغّل الإعداد مرة أخرى. إذا استمرت المشكلة، فوفّر ملفات برنامج التشغيل أو الاتصال بالإنترنت، أو تواصل مع فريق تقنية المعلومات.");
        Finding(c, "RebootRequired",
            "Restart required", "Restart the computer to finish installing the driver, then run the setup again.",
            "إعادة التشغيل مطلوبة", "أعد تشغيل الجهاز لإكمال تثبيت برنامج التشغيل، ثم شغّل الإعداد مرة أخرى.");

        Finding(c, "QueueReady",
            "Printer is set up", "The printer is added to Windows with the approved driver.",
            "الطابعة مُعدّة", "الطابعة مضافة إلى Windows ببرنامج التشغيل المعتمد.");
        Finding(c, "QueueMissing",
            "Printer not added yet", "The printer will be added to Windows.",
            "الطابعة غير مضافة بعد", "ستتم إضافة الطابعة إلى Windows.");
        Finding(c, "QueueDriverMismatch",
            "Printer uses another driver", "The printer will be switched to the approved driver.",
            "الطابعة تستخدم برنامج تشغيل آخر", "سيتم تحويل الطابعة إلى برنامج التشغيل المعتمد.");
        Finding(c, "DriverIgnoresTicketForm",
            "Printer driver does not offer the ticket paper", "The printer's current driver does not offer the ticket paper by name, so the printer will be switched to the approved driver that does.",
            "برنامج تشغيل الطابعة لا يعرض ورق التذكرة", "لا يعرض برنامج التشغيل الحالي للطابعة ورق التذكرة باسمه، لذلك سيتم تحويل الطابعة إلى برنامج التشغيل المعتمد الذي يعرضه.");
        Finding(c, "QueueDriverRestored",
            "Printer driver put back", "The ticket settings could not be applied with the new driver, so the printer's previous driver and settings were restored. Send the report to IT.",
            "تمت إعادة برنامج تشغيل الطابعة", "تعذّر تطبيق إعدادات التذكرة باستخدام برنامج التشغيل الجديد، لذلك أُعيد برنامج التشغيل والإعدادات السابقة للطابعة. أرسل التقرير إلى فريق تقنية المعلومات.");
        Finding(c, "QueueDriverRestoreFailed",
            "Printer driver could not be put back", "The ticket settings failed with the new driver, and the printer's previous driver could not be restored. Contact IT.",
            "تعذّرت إعادة برنامج تشغيل الطابعة", "فشلت إعدادات التذكرة باستخدام برنامج التشغيل الجديد، وتعذّرت إعادة برنامج التشغيل السابق للطابعة. تواصل مع فريق تقنية المعلومات.");
        Finding(c, "QueuePortMismatch",
            "Printer port is wrong", "The printer points to the wrong connection; this will be corrected.",
            "منفذ الطابعة غير صحيح", "تشير الطابعة إلى اتصال غير صحيح، وسيتم تصحيح ذلك.");
        Finding(c, "QueueCreateFailed",
            "Printer could not be added", "Windows could not add the printer. Try again; if it keeps failing, send the report to IT.",
            "تعذّرت إضافة الطابعة", "تعذّر على Windows إضافة الطابعة. حاول مرة أخرى، وإذا استمرت المشكلة فأرسل التقرير إلى فريق تقنية المعلومات.");
        Finding(c, "PortCreateFailed",
            "Network port could not be created", "Windows could not create the printer's network port. Try again or send the report to IT.",
            "تعذّر إنشاء منفذ الشبكة", "تعذّر على Windows إنشاء منفذ الشبكة للطابعة. حاول مرة أخرى أو أرسل التقرير إلى فريق تقنية المعلومات.");
        Finding(c, "PrinterOffline",
            "Printer is offline", "Switch the printer on and check its cable or network connection.",
            "الطابعة غير متصلة", "شغّل الطابعة وتحقّق من الكابل أو من اتصال الشبكة.");
        Finding(c, "PrinterError",
            "Printer reports an error", "Check the printer's display for a paper jam, an open cover or an empty tray.",
            "الطابعة تُبلغ عن خطأ", "راجع شاشة الطابعة بحثًا عن انحشار ورق أو غطاء مفتوح أو درج فارغ.");
        Finding(c, "PrinterPaused",
            "Printing is paused", "The printer is paused in Windows. Resume it in Settings, Printers & scanners.",
            "الطباعة متوقفة مؤقتًا", "الطابعة متوقفة مؤقتًا في Windows. استأنف الطباعة من الإعدادات، الطابعات والماسحات الضوئية.");

        Finding(c, "FormReady",
            "Ticket paper size is ready", "The ticket paper size exists in Windows with the exact dimensions.",
            "حجم ورق التذكرة جاهز", "حجم ورق التذكرة موجود في Windows بالأبعاد الدقيقة.");
        Finding(c, "FormMissing",
            "Ticket paper size missing", "The ticket paper size will be created in Windows.",
            "حجم ورق التذكرة غير موجود", "سيتم إنشاء حجم ورق التذكرة في Windows.");
        Finding(c, "FormSizeMismatch",
            "Ticket paper size is wrong", "The ticket paper size exists with different dimensions and will be corrected.",
            "حجم ورق التذكرة غير صحيح", "حجم ورق التذكرة موجود بأبعاد مختلفة وسيتم تصحيحه.");
        Finding(c, "FormSimilarExists",
            "A similar paper size exists", "Another paper size with a similar name exists. It was left unchanged.",
            "يوجد حجم ورق مشابه", "يوجد حجم ورق آخر باسم مشابه، وقد تُرك دون تغيير.");
        Finding(c, "FormCreateFailed",
            "Ticket paper size could not be created", "Windows could not create the ticket paper size. Try again or send the report to IT.",
            "تعذّر إنشاء حجم ورق التذكرة", "تعذّر على Windows إنشاء حجم ورق التذكرة. حاول مرة أخرى أو أرسل التقرير إلى فريق تقنية المعلومات.");
    }
}
