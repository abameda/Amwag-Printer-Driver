namespace Amwag.PrinterSetup.App.Localization;

public sealed partial class Strings
{
    private static void AddSettingsFindings(CatalogBuilder c)
    {
        Finding(c, "DriverListsForm",
            "Driver offers the ticket size", "The printer driver lists the ticket paper size.",
            "برنامج التشغيل يدعم حجم التذكرة", "يعرض برنامج تشغيل الطابعة حجم ورق التذكرة.");
        Finding(c, "DriverDoesNotListForm",
            "Driver does not offer the ticket size", "The printer driver does not list the ticket paper size. IT must add a custom page size of the ticket size in the printer's printing preferences, then run Repair.",
            "برنامج التشغيل لا يعرض حجم التذكرة", "لا يعرض برنامج تشغيل الطابعة حجم ورق التذكرة. يجب أن يضيف فريق تقنية المعلومات حجم صفحة مخصصًا بحجم التذكرة في تفضيلات الطباعة للطابعة، ثم شغّل «إصلاح».");
        Finding(c, "TicketSizeOutsideDriverRange",
            "Ticket size not supported by the driver", "The printer driver does not accept paper of this size. Contact IT.",
            "حجم التذكرة غير مدعوم من برنامج التشغيل", "لا يقبل برنامج تشغيل الطابعة ورقًا بهذا الحجم. تواصل مع فريق تقنية المعلومات.");
        Finding(c, "TicketSizeOutsideTrayRange",
            "Ticket paper needs the multipurpose tray", "Paper of this size can only be fed from the multipurpose tray.",
            "ورق التذكرة يحتاج إلى الدرج متعدد الأغراض", "لا يمكن سحب ورق بهذا الحجم إلا من الدرج متعدد الأغراض.");
        Finding(c, "DriverRejectedSetting",
            "Driver refused a setting", "The printer driver did not accept one of the ticket settings. Send the report to IT.",
            "رفض برنامج التشغيل أحد الإعدادات", "لم يقبل برنامج تشغيل الطابعة أحد إعدادات التذكرة. أرسل التقرير إلى فريق تقنية المعلومات.");
        Finding(c, "PaperSourceNotFound",
            "Multipurpose tray not found", "The driver does not offer the multipurpose tray, so the printer's default paper source is used.",
            "لم يتم العثور على الدرج متعدد الأغراض", "لا يعرض برنامج التشغيل الدرج متعدد الأغراض، لذلك يُستخدم مصدر الورق الافتراضي للطابعة.");
        Finding(c, "PaperSourceRejected",
            "Multipurpose tray could not be selected", "The driver did not keep the multipurpose tray as paper source. If tickets come from the wrong tray, select the tray on the printer panel.",
            "تعذّر اختيار الدرج متعدد الأغراض", "لم يحتفظ برنامج التشغيل بالدرج متعدد الأغراض كمصدر للورق. إذا خرجت التذاكر من درج خاطئ فاختر الدرج من لوحة الطابعة.");

        Finding(c, "GlobalDefaultsCorrect",
            "Printer defaults are correct", "Ticket size, portrait orientation, single-sided printing and 100% scale are the printer's defaults.",
            "الإعدادات الافتراضية للطابعة صحيحة", "حجم التذكرة والاتجاه الطولي والطباعة على وجه واحد والمقياس 100% هي الإعدادات الافتراضية للطابعة.");
        Finding(c, "GlobalDefaultsIncorrect",
            "Printer defaults need correcting", "The printer's default settings will be set for tickets.",
            "الإعدادات الافتراضية للطابعة تحتاج إلى تصحيح", "سيتم ضبط الإعدادات الافتراضية للطابعة لطباعة التذاكر.");
        Finding(c, "UserDefaultsCorrect",
            "Your preferences are correct", "Your personal printing preferences match the ticket settings.",
            "تفضيلاتك صحيحة", "تفضيلات الطباعة الشخصية لديك تطابق إعدادات التذكرة.");
        Finding(c, "UserDefaultsIncorrect",
            "Your preferences need correcting", "Your personal printing preferences differ from the ticket settings and will be corrected.",
            "تفضيلاتك تحتاج إلى تصحيح", "تختلف تفضيلات الطباعة الشخصية لديك عن إعدادات التذكرة وسيتم تصحيحها.");
        Finding(c, "UserDefaultsAbsent",
            "No personal preferences", "You have no personal preferences for this printer, so the printer defaults apply.",
            "لا توجد تفضيلات شخصية", "ليست لديك تفضيلات شخصية لهذه الطابعة، لذلك تُطبَّق الإعدادات الافتراضية للطابعة.");
        Finding(c, "DefaultsNotPersisted",
            "Settings did not stay saved", "Windows or the driver did not keep the ticket settings. Run Repair; if this persists, send the report to IT.",
            "لم يتم الاحتفاظ بالإعدادات", "لم يحتفظ Windows أو برنامج التشغيل بإعدادات التذكرة. شغّل «إصلاح»، وإذا استمرت المشكلة فأرسل التقرير إلى فريق تقنية المعلومات.");
        Finding(c, "PrintTicketMismatch",
            "Modern apps may use another size", "The driver reports a different paper size to modern apps. Print a test ticket to check.",
            "قد تستخدم التطبيقات الحديثة حجمًا آخر", "يُبلغ برنامج التشغيل التطبيقات الحديثة بحجم ورق مختلف. اطبع تذكرة اختبار للتحقق.");
        Finding(c, "PrintCapabilitiesUnavailable",
            "Extra driver check not available", "Additional information from the driver could not be read. This does not affect printing.",
            "الفحص الإضافي لبرنامج التشغيل غير متاح", "تعذّرت قراءة معلومات إضافية من برنامج التشغيل. لا يؤثر ذلك على الطباعة.");
        Finding(c, "ApplicationMayOverrideDefaults",
            "Programs can choose their own paper", "Some programs choose the paper size themselves. If tickets print wrongly, check the paper size in the ticket program.",
            "قد تختار البرامج الورق بنفسها", "بعض البرامج تختار حجم الورق بنفسها. إذا طُبعت التذاكر بشكل خاطئ فتحقّق من حجم الورق في برنامج التذاكر.");

        Finding(c, "ElevationRequired",
            "Permission needed", "Windows will ask for permission to install the driver and create the ticket paper size.",
            "الإذن مطلوب", "سيطلب Windows إذنًا لتثبيت برنامج التشغيل وإنشاء حجم ورق التذكرة.");
        Finding(c, "ElevationDeclined",
            "Permission was not given", "Nothing was changed. Choose Try again, then choose Yes when Windows asks.",
            "لم يتم منح الإذن", "لم يتم تغيير أي شيء. اختر «حاول مرة أخرى»، ثم اختر «نعم» عندما يطلب Windows ذلك.");
        Finding(c, "ElevationFailed",
            "Administrator step failed", "The part of the setup that needs administrator rights could not run. Try again or contact IT.",
            "فشلت خطوة المسؤول", "تعذّر تشغيل جزء الإعداد الذي يتطلب صلاحيات المسؤول. حاول مرة أخرى أو تواصل مع فريق تقنية المعلومات.");
        Finding(c, "OperationFailed",
            "Something went wrong", "An unexpected error occurred. Try again; if it keeps failing, send the report to IT.",
            "حدث خطأ", "حدث خطأ غير متوقع. حاول مرة أخرى، وإذا استمرت المشكلة فأرسل التقرير إلى فريق تقنية المعلومات.");
        Finding(c, "Cancelled",
            "Cancelled", "The setup was stopped before it finished.",
            "تم الإلغاء", "تم إيقاف الإعداد قبل اكتماله.");
        c.Add("finding.Cancelled.text.Repair", "Repair was stopped before it finished.", "تم إيقاف الإصلاح قبل اكتماله.");
        Finding(c, "ConfigurationChanged",
            "Configuration changed", "The setup's configuration changed while this window was open, so nothing was changed. Close the program and start it again.",
            "تغيّرت الإعدادات", "تغيّرت إعدادات أداة الإعداد أثناء فتح هذه النافذة، لذلك لم يتم تغيير أي شيء. أغلق البرنامج ثم شغّله مرة أخرى.");
        Finding(c, "ApplyLockBusy",
            "Another setup is running", "Another AMWAG Printer Setup run is changing this computer, so nothing was changed. Try again in a few minutes.",
            "عملية إعداد أخرى قيد التشغيل", "تقوم عملية أخرى لأداة AMWAG Printer Setup بتغيير إعدادات هذا الجهاز، لذلك لم يتم تغيير أي شيء. حاول مرة أخرى بعد بضع دقائق.");
    }
}
