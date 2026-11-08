namespace Amwag.PrinterSetup.App.Localization;

public sealed partial class Strings
{
    private static void AddResults(CatalogBuilder c)
    {
        c.Add("outcome.Ready.title", "Ready to print tickets", "جاهزة لطباعة التذاكر");
        c.Add("outcome.Ready.text", "The ticket printer is set up and every setting was checked.", "تم إعداد طابعة التذاكر والتحقق من جميع الإعدادات.");
        c.Add("outcome.ReadyWithWarnings.title", "Ready, with warnings", "جاهزة مع تحذيرات");
        c.Add("outcome.ReadyWithWarnings.text", "You can print tickets, but some items need attention. See the details below.", "يمكنك طباعة التذاكر، لكن بعض البنود تحتاج إلى انتباه. راجع التفاصيل أدناه.");
        c.Add("outcome.NotReady.title", "The printer is not ready", "الطابعة غير جاهزة");
        c.Add("outcome.NotReady.text", "Some settings could not be confirmed. Run Repair or try again.", "تعذّر تأكيد بعض الإعدادات. شغّل «إصلاح» أو حاول مرة أخرى.");
        // The printer has no queue (its install failed, or it was never set up): Repair, which never adds one, is not offered.
        c.Add("outcome.NotReady.noQueue", "The printer has not been added to Windows. Solve the problem shown below, then try again.", "لم تتم إضافة الطابعة إلى Windows. عالج المشكلة الموضّحة أدناه، ثم حاول مرة أخرى.");
        c.Add("outcome.NotReady.noQueue.Diagnostics", "The printer has not been added to Windows yet. Run Setup to set it up.", "لم تتم إضافة الطابعة إلى Windows بعد. شغّل الإعداد لإضافتها وضبطها.");
        // Diagnostics could not tell which of several printers is the ticket printer, and none of them has been set up.
        c.Add("outcome.NotReady.printersWithoutQueue", "The printers found have not been added to Windows yet. Run Setup to set up the ticket printer.", "لم تتم إضافة الطابعات التي تم العثور عليها إلى Windows بعد. شغّل الإعداد لإعداد طابعة التذاكر.");
        // Not ready, and Repair is not offered (it could change nothing): the banner does not send the employee to Repair.
        c.Add("outcome.NotReady.solve", "Some settings could not be confirmed. Solve the problem shown below, then try again.", "تعذّر تأكيد بعض الإعدادات. عالج المشكلة الموضّحة أدناه، ثم حاول مرة أخرى.");
        c.Add("outcome.NoPrinter.title", "No ticket printer found", "لم يتم العثور على طابعة تذاكر");
        c.Add("outcome.NoPrinter.text", "Connect the printer with its USB cable or make sure it is on the network, then try again.", "وصّل الطابعة بكابل USB أو تأكّد من اتصالها بالشبكة، ثم حاول مرة أخرى.");
        // Repair's ticket printer has no queue yet (or none of the printers found has one): only Setup adds one.
        c.Add("outcome.NoPrinter.needsSetup.title", "Nothing to repair yet", "لا يوجد ما يمكن إصلاحه بعد");
        c.Add("outcome.NoPrinter.needsSetup.text", "Repair changes only a printer that is already set up, so nothing was changed.", "لا يغيّر «إصلاح» إلا طابعة مُعدّة بالفعل، لذلك لم يتم تغيير أي شيء.");
        c.Add("outcome.NoPrinter.printersWithoutQueue.text", "None of the printers found has been added to Windows yet, and Repair changes only a printer that is already set up, so nothing was changed.", "لم تتم إضافة أيٍّ من الطابعات التي تم العثور عليها إلى Windows بعد، ولا يغيّر «إصلاح» إلا طابعة مُعدّة بالفعل، لذلك لم يتم تغيير أي شيء.");
        // The printer was found, but nobody confirmed its model: Try again asks again.
        c.Add("outcome.NoPrinter.modelNotConfirmed.title", "The printer was not changed", "لم يتم تغيير الطابعة");
        c.Add("outcome.NoPrinter.modelNotConfirmed.text", "Its model was not confirmed, so nothing was changed.", "لم يتم تأكيد طرازها، لذلك لم يتم تغيير أي شيء.");
        // Repair found no printer it could change: Setup looks for the printer again and adds it.
        c.Add("outcome.NoPrinter.runSetup", "Nothing was changed. Connect the printer with its USB cable or make sure it is on the network, then choose Run Setup.", "لم يتم تغيير أي شيء. وصّل الطابعة بكابل USB أو تأكّد من اتصالها بالشبكة، ثم اختر «تشغيل الإعداد».");
        // Nothing usable is at the configured printer address (branch.json, --address, or the address entered in this run); no
        // other printer stands in for the ticket printer.
        c.Add("outcome.notAtAddress.title", "Ticket printer not found at its address", "لم يتم العثور على طابعة التذاكر على عنوانها");
        c.Add("outcome.notAtAddress.text", "Nothing was changed, and no other printer was used in its place. Solve the problem shown below, then try again.", "لم يتم تغيير أي شيء، ولم تُستخدم طابعة أخرى بدلًا منها. عالج المشكلة الموضّحة أدناه، ثم حاول مرة أخرى.");
        c.Add("outcome.notAtAddress.text.Diagnostics", "No other printer was taken as the ticket printer in its place. Solve the problem shown below, then try again.", "لم تُعتبر أي طابعة أخرى طابعةَ التذاكر بدلًا منها. عالج المشكلة الموضّحة أدناه، ثم حاول مرة أخرى.");
        // A queue the setup cannot configure (pooled, on an LPR or IPP port) prints to the printer at the configured address, so
        // that printer is not set up automatically: IT changes or deletes the queue first.
        c.Add("outcome.leftUnchanged.title", "The ticket printer cannot be set up automatically", "لا يمكن إعداد طابعة التذاكر تلقائيًا");
        c.Add("outcome.leftUnchanged.text", "A printer entry in Windows prints to it in a way the setup cannot configure, so nothing was changed. IT must change or delete that printer entry first.", "يطبع إليها إدخال طابعة في Windows بطريقة لا يستطيع الإعداد ضبطها، لذلك لم يتم تغيير أي شيء. يجب أن يغيّر فريق تقنية المعلومات إدخال الطابعة هذا أو يحذفه أولًا.");
        c.Add("outcome.leftUnchanged.text.Diagnostics", "A printer entry in Windows prints to it in a way the setup cannot configure. IT must change or delete that printer entry before the ticket printer can be set up.", "يطبع إليها إدخال طابعة في Windows بطريقة لا يستطيع الإعداد ضبطها. يجب أن يغيّر فريق تقنية المعلومات إدخال الطابعة هذا أو يحذفه قبل أن يصبح بالإمكان إعداد طابعة التذاكر.");
        c.Add("outcome.Cancelled.title", "Setup was stopped", "تم إيقاف الإعداد");
        c.Add("outcome.Cancelled.title.Repair", "Repair was stopped", "تم إيقاف الإصلاح");
        c.Add("outcome.Cancelled.text", "Nothing else was changed. You can run the setup again at any time.", "لم يتم تغيير أي شيء آخر. يمكنك تشغيل الإعداد مرة أخرى في أي وقت.");
        c.Add("outcome.Cancelled.text.Repair", "Nothing else was changed. You can run Repair again at any time.", "لم يتم تغيير أي شيء آخر. يمكنك تشغيل «إصلاح» مرة أخرى في أي وقت.");
        // IT's model (--model with --confirm-model) is not what the printer reports: the run stopped itself, before any change.
        c.Add("outcome.Cancelled.modelRefused.title", "The printer was not changed", "لم يتم تغيير الطابعة");
        c.Add("outcome.Cancelled.modelRefused.text", "The model given for this printer does not match the model it reports, so nothing was changed.", "الطراز المحدَّد لهذه الطابعة لا يطابق الطراز الذي تُبلغ عنه، لذلك لم يتم تغيير أي شيء.");
        c.Add("outcome.ElevationDeclined.title", "Permission was not given", "لم يتم منح الإذن");
        c.Add("outcome.ElevationDeclined.text", "Windows did not get permission to install the driver, so nothing was changed. Choose Try again, then choose Yes when Windows asks.", "لم يحصل Windows على إذن تثبيت برنامج التشغيل، لذلك لم يتم تغيير أي شيء. اختر «حاول مرة أخرى»، ثم اختر «نعم» عندما يطلب Windows ذلك.");
        c.Add("outcome.Blocked.title", "Setup cannot continue", "لا يمكن متابعة الإعداد");
        c.Add("outcome.Blocked.title.Repair", "Repair cannot continue", "لا يمكن متابعة الإصلاح");
        c.Add("outcome.Blocked.text", "A required condition is not met. Nothing was changed.", "يوجد شرط مطلوب غير متحقق. لم يتم تغيير أي شيء.");
        // A blocker found once the administrator steps had changed something (the installed driver's paper range, for example).
        c.Add("outcome.Blocked.afterChanges", "Some changes were made, but the setup cannot finish: a required condition is not met.", "تم إجراء بعض التغييرات، لكن لا يمكن إكمال الإعداد: يوجد شرط مطلوب غير متحقق.");
        c.Add("outcome.Blocked.afterChanges.Repair", "Some changes were made, but Repair cannot finish: a required condition is not met.", "تم إجراء بعض التغييرات، لكن لا يمكن إكمال الإصلاح: يوجد شرط مطلوب غير متحقق.");
        c.Add("outcome.Failed.title", "Setup did not finish", "لم يكتمل الإعداد");
        c.Add("outcome.Failed.title.Repair", "Repair did not finish", "لم يكتمل الإصلاح");
        c.Add("outcome.Failed.text", "Something went wrong. Your IT team can use the report to help.", "حدث خطأ. يمكن لفريق تقنية المعلومات استخدام التقرير للمساعدة.");

        c.Add("check.SpoolerRunning", "Print Spooler service", "خدمة التخزين المؤقت للطباعة");
        c.Add("check.DriverInstalled", "Printer driver installed", "تثبيت برنامج تشغيل الطابعة");
        c.Add("check.QueueExists", "Printer added to Windows", "إضافة الطابعة إلى Windows");
        c.Add("check.QueueUsesAcceptedDriver", "Printer uses the approved driver", "استخدام برنامج التشغيل المعتمد");
        c.Add("check.QueuePortCorrect", "Printer port", "منفذ الطابعة");
        c.Add("check.FormExists", "Ticket paper size exists", "وجود حجم ورق التذكرة");
        c.Add("check.FormSizeExact", "Ticket paper size is exact", "دقة أبعاد ورق التذكرة");
        c.Add("check.DriverListsForm", "Driver offers the ticket size", "دعم برنامج التشغيل لحجم التذكرة");
        c.Add("check.DefaultPaper", "Default paper size", "حجم الورق الافتراضي");
        c.Add("check.DefaultOrientation", "Default orientation", "الاتجاه الافتراضي");
        c.Add("check.DefaultDuplex", "Single-sided printing", "الطباعة على وجه واحد");
        c.Add("check.DefaultScale", "Scale", "مقياس الطباعة");
        c.Add("check.DefaultPaperSource", "Paper source", "مصدر الورق");
        c.Add("check.UserDefaultsConsistent", "Your personal preferences", "تفضيلاتك الشخصية");
        c.Add("check.EffectiveDefaults", "Settings that programs receive", "الإعدادات التي تستلمها البرامج");
        c.Add("check.PrintTicketMediaSize", "Paper size for modern apps", "حجم الورق في التطبيقات الحديثة");
        c.Add("check.PrinterOnline", "Printer online", "اتصال الطابعة");
        c.Add("check.DeviceConnected", "Printer connected and linked to the driver", "توصيل الطابعة وربطها ببرنامج التشغيل");

        c.Add("status.Pass", "Passed", "ناجح");
        c.Add("status.Warning", "Warning", "تحذير");
        c.Add("status.Fail", "Failed", "فشل");
        c.Add("status.NotApplicable", "Not applicable", "غير منطبق");
        c.Add("status.Unknown", "Unknown", "غير معروف");

        c.Add("result.summary", "Summary", "الملخص");
        c.Add("result.details.show", "Show details", "عرض التفاصيل");
        c.Add("result.details.hide", "Hide details", "إخفاء التفاصيل");
        c.Add("result.expectedActual", "Expected: {0}   Found: {1}", "المتوقع: {0}   الموجود: {1}");
        c.Add("result.printTest", "Print Test Ticket", "طباعة تذكرة اختبار");
        c.Add("result.repair", "Repair", "إصلاح");
        c.Add("result.saveReport", "Save report…", "حفظ التقرير…");
        c.Add("result.copyReport", "Copy report", "نسخ التقرير");
        c.Add("result.copied", "Report copied", "تم نسخ التقرير");
        c.Add("result.openLog", "Open log folder", "فتح مجلد السجلات");
        c.Add("result.close", "Close", "إغلاق");
        c.Add("result.tryAgain", "Try again", "حاول مرة أخرى");
        c.Add("result.runSetup", "Run Setup", "تشغيل الإعداد");
        c.Add("result.whatHappened", "What happened", "ما الذي حدث");
        c.Add("result.technical", "Technical details", "تفاصيل تقنية");
        c.Add("result.win32", "Windows error {0}: {1}", "خطأ Windows رقم {0}: {1}");
        c.Add("result.reboot", "Restart this computer to finish installing the driver.", "أعد تشغيل هذا الجهاز لإكمال تثبيت برنامج التشغيل.");
        c.Add("result.printersChecked", "Printers checked", "الطابعات التي تم فحصها");
        c.Add("result.noPrinterChecked", "No printer was checked.", "لم يتم فحص أي طابعة.");
        c.Add("result.reportUnavailable", "The report is not available for this run.", "التقرير غير متوفر لهذا التشغيل.");
        c.Add("result.saveTitle", "Save the diagnostic report", "حفظ تقرير التشخيص");
        c.Add("result.textFiles", "Text files", "ملفات نصية");
        c.Add("result.saveFailed", "The report could not be saved: {0}", "تعذّر حفظ التقرير: {0}");
        c.Add("result.saved", "Report saved", "تم حفظ التقرير");
        c.Add("result.logUnavailable", "No log folder is available.", "لا يتوفر مجلد سجلات.");
    }
}
