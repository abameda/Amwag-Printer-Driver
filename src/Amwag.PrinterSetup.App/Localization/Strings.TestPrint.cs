namespace Amwag.PrinterSetup.App.Localization;

public sealed partial class Strings
{
    private static void AddTestPrint(CatalogBuilder c)
    {
        c.Add("testprint.title", "Print a test ticket", "طباعة تذكرة اختبار");
        c.Add("testprint.load", "Load ticket paper into the multipurpose tray, then click Print.", "ضع ورق التذاكر في الدرج متعدد الأغراض، ثم انقر «طباعة».");
        c.Add("testprint.loadHint", "The test ticket has corner marks, rulers and two 50 mm bars, so you can check its size and position.", "تحتوي تذكرة الاختبار على علامات زوايا ومساطر وشريطين بطول 50 مم، لتتحقق من حجمها وموضعها.");
        c.Add("testprint.print", "Print", "طباعة");
        c.Add("testprint.printAnyway", "Print anyway", "طباعة على أي حال");
        c.Add("testprint.precheck.title", "The ticket settings are not active", "إعدادات التذكرة غير مفعّلة");
        c.Add("testprint.precheck.mismatch", "Programs currently get the paper size \"{0}\" instead of \"{1}\", so the test ticket may not match real tickets. Run Repair to restore the ticket settings.", "تحصل البرامج حاليًا على حجم الورق «{0}» بدلًا من «{1}»، لذلك قد لا تطابق تذكرة الاختبار التذاكر الفعلية. شغّل «إصلاح» لاستعادة إعدادات التذكرة.");
        c.Add("testprint.precheck.unreadable", "The printer's current paper settings could not be read, so the test ticket may not match real tickets.", "تعذّرت قراءة إعدادات الورق الحالية للطابعة، لذلك قد لا تطابق تذكرة الاختبار التذاكر الفعلية.");
        c.Add("testprint.sending", "Sending the test ticket to the printer…", "جارٍ إرسال تذكرة الاختبار إلى الطابعة…");

        c.Add("testprint.stage.NotSeen", "Waiting for the print queue…", "في انتظار قائمة الطباعة…");
        c.Add("testprint.stage.Spooling", "Preparing the ticket…", "جارٍ تجهيز التذكرة…");
        c.Add("testprint.stage.Queued", "Waiting in the print queue…", "في قائمة انتظار الطباعة…");
        c.Add("testprint.stage.Printing", "Printing…", "جارٍ الطباعة…");
        c.Add("testprint.stage.Printed", "The printer reports the ticket as printed", "أبلغت الطابعة بأن التذكرة طُبعت");
        c.Add("testprint.stage.LeftQueue", "Sent to the printer", "تم الإرسال إلى الطابعة");

        c.Add("testprint.problem.Offline", "The printer is offline. Check that it is switched on and connected.", "الطابعة غير متصلة. تأكّد من تشغيلها وتوصيلها.");
        c.Add("testprint.problem.PaperOut", "The printer is out of paper. Load ticket paper into the multipurpose tray.", "نفد الورق من الطابعة. ضع ورق التذاكر في الدرج متعدد الأغراض.");
        c.Add("testprint.problem.DoorOpen", "A printer cover is open. Close it.", "أحد أغطية الطابعة مفتوح. أغلقه.");
        c.Add("testprint.problem.PaperJam", "Paper is jammed. Follow the instructions on the printer panel.", "يوجد انحشار في الورق. اتبع التعليمات الظاهرة على لوحة الطابعة.");
        c.Add("testprint.problem.NoToner", "The printer is out of toner.", "نفد مسحوق الحبر (التونر) من الطابعة.");
        c.Add("testprint.problem.NeedsAttention", "The printer needs attention. Check the printer panel.", "الطابعة تحتاج إلى تدخّل. راجع لوحة الطابعة.");
        c.Add("testprint.problem.JobError", "Windows reports an error for the test ticket.", "يُبلغ Windows عن خطأ في تذكرة الاختبار.");
        c.Add("testprint.problem.PrinterError", "The printer reports an error. Check the printer panel.", "تُبلغ الطابعة عن خطأ. راجع لوحة الطابعة.");
        c.Add("testprint.problem.Blocked", "The print queue is blocked.", "قائمة الطباعة متوقفة.");
        c.Add("testprint.problem.Deleted", "The test ticket was removed from the print queue.", "تم حذف تذكرة الاختبار من قائمة الطباعة.");

        c.Add("testprint.sent.title", "Windows sent the test ticket to the printer", "أرسل Windows تذكرة الاختبار إلى الطابعة");
        c.Add("testprint.sent.text", "Please check the printed ticket:", "يُرجى فحص التذكرة المطبوعة:");
        c.Add("testprint.check.corners", "All four corner marks are visible", "علامات الزوايا الأربع ظاهرة");
        c.Add("testprint.check.bars", "Both bars measure exactly 50 mm", "طول كل من الشريطين 50 مم بالضبط");
        c.Add("testprint.check.text", "The text is upright and nothing is cut off", "النص في الاتجاه الصحيح ولا يوجد شيء مقطوع");
        c.Add("testprint.check.tray", "The ticket came from the multipurpose tray", "سُحبت التذكرة من الدرج متعدد الأغراض");
        c.Add("testprint.paperUsed", "Paper size used by Windows: {0}", "حجم الورق الذي استخدمه Windows: {0}");
        c.Add("testprint.ok", "Printed correctly", "طُبعت بشكل صحيح");
        c.Add("testprint.notOk", "There is a problem", "توجد مشكلة");
        c.Add("testprint.stillQueued.title", "The test ticket is still waiting", "تذكرة الاختبار لا تزال في الانتظار");
        c.Add("testprint.stillQueued.text", "The test ticket is still in the print queue. Check that the printer is on and has paper; it prints as soon as the printer is ready.", "لا تزال تذكرة الاختبار في قائمة انتظار الطباعة. تأكّد من أن الطابعة قيد التشغيل وبها ورق؛ ستُطبع فور جاهزية الطابعة.");
        c.Add("testprint.problem.title", "The printer reported a problem", "أبلغت الطابعة عن مشكلة");
        c.Add("testprint.failed.title", "The test ticket could not be sent", "تعذّر إرسال تذكرة الاختبار");
        c.Add("testprint.failed.text", "Windows did not accept the print job. Check that the printer is installed, then try again.", "لم يقبل Windows مهمة الطباعة. تأكّد من أن الطابعة مثبّتة، ثم حاول مرة أخرى.");
        c.Add("testprint.printAgain", "Print again", "طباعة مرة أخرى");
        c.Add("testprint.back", "Back", "رجوع");
        c.Add("testprint.done", "Done", "تم");

        c.Add("testprint.answer.ok", "Thank you. You confirmed that the test ticket printed correctly.", "شكرًا لك. لقد أكّدت أن تذكرة الاختبار طُبعت بشكل صحيح.");
        c.Add("testprint.answer.problem", "You reported a problem with the test ticket. Check the following:", "أبلغت عن مشكلة في تذكرة الاختبار. تحقّق مما يلي:");
        c.Add("testprint.guidance.misaligned", "Off-center or crooked: slide the guides of the multipurpose tray against the ticket paper.", "مائلة أو غير متمركزة: اضبط موجّهات الورق في الدرج متعدد الأغراض على عرض ورق التذكرة.");
        c.Add("testprint.guidance.size", "Wrong size or scaled: on the printer panel, set the paper size of the multipurpose tray to the ticket size (custom), then run Repair.", "حجم خاطئ أو مُصغّر: من لوحة الطابعة اضبط حجم الورق للدرج متعدد الأغراض على حجم التذكرة (مخصّص)، ثم شغّل «إصلاح».");
        c.Add("testprint.guidance.cutoff", "Cut off at an edge: printers cannot print the outer 4 mm of the sheet. Keep ticket content inside the dashed safe area.", "مقطوعة عند الحافة: لا تطبع الطابعات على مسافة 4 مم من حافة الورقة. أبقِ محتوى التذكرة داخل منطقة الأمان المتقطعة.");
        c.Add("testprint.guidance.tray", "Fed from the wrong tray: on the printer panel, check the multipurpose tray settings (paper size and type).", "سُحبت من درج خاطئ: من لوحة الطابعة راجع إعدادات الدرج متعدد الأغراض (حجم الورق ونوعه).");
    }
}
