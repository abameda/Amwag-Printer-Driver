namespace Amwag.PrinterSetup.App.Localization;

public sealed partial class Strings
{
    private static void AddTicketCheck(CatalogBuilder c)
    {
        c.Add("result.checkSoftware", "Check ticket software", "فحص برنامج التذاكر");

        c.Add("ticketcheck.title", "Check the ticket software", "فحص برنامج التذاكر");
        c.Add("ticketcheck.instruction", "Print one ticket from your ticket software now.", "اطبع الآن تذكرة واحدة من برنامج التذاكر.");
        c.Add("ticketcheck.explanation", "This check only reads the settings Windows receives with the print job; it changes nothing. The ticket software must print to \"{0}\".", "يقرأ هذا الفحص فقط الإعدادات التي يستلمها Windows مع مهمة الطباعة، ولا يغيّر أي شيء. يجب أن يطبع برنامج التذاكر على «{0}».");
        c.Add("ticketcheck.waiting", "Waiting for a print job… {0} left", "في انتظار مهمة طباعة… الوقت المتبقي {0}");
        c.Add("ticketcheck.reading", "Print job found. Reading its settings…", "تم العثور على مهمة طباعة. جارٍ قراءة إعداداتها…");
        c.Add("ticketcheck.stop", "Stop", "إيقاف");
        c.Add("ticketcheck.again", "Check again", "فحص مرة أخرى");
        c.Add("ticketcheck.back", "Back", "رجوع");
        c.Add("ticketcheck.job", "Print job: {0}", "مهمة الطباعة: {0}");

        c.Add("ticketcheck.column.setting", "Setting", "الإعداد");
        c.Add("ticketcheck.column.requested", "Ticket software asked for", "ما طلبه برنامج التذاكر");
        c.Add("ticketcheck.column.expected", "AMWAG Ticket default", "إعداد تذكرة AMWAG");

        c.Add("ticketcheck.setting.Paper", "Paper size", "حجم الورق");
        c.Add("ticketcheck.setting.Orientation", "Orientation", "الاتجاه");
        c.Add("ticketcheck.setting.Duplex", "Two-sided printing", "الطباعة على الوجهين");
        c.Add("ticketcheck.setting.Scale", "Scale", "مقياس الطباعة");
        c.Add("ticketcheck.setting.PaperSource", "Paper source", "مصدر الورق");

        c.Add("ticketcheck.status.Matches", "Matches", "مطابق");
        c.Add("ticketcheck.status.Differs", "Differs", "مختلف");
        c.Add("ticketcheck.status.NotSpecified", "Not set by the program", "لم يحدده البرنامج");
        c.Add("ticketcheck.status.Unknown", "Cannot be checked", "تعذّر الفحص");

        c.Add("ticketcheck.value.notSpecified", "Not specified", "غير محدد");
        c.Add("ticketcheck.value.portrait", "Portrait", "عمودي");
        c.Add("ticketcheck.value.landscape", "Landscape", "أفقي");
        c.Add("ticketcheck.value.simplex", "Off (single-sided)", "متوقفة (وجه واحد)");
        c.Add("ticketcheck.value.duplexLong", "On, long edge", "مفعّلة، الحافة الطويلة");
        c.Add("ticketcheck.value.duplexShort", "On, short edge", "مفعّلة، الحافة القصيرة");
        c.Add("ticketcheck.value.number", "Value {0}", "القيمة {0}");
        c.Add("ticketcheck.value.paperNumber", "Paper number {0}", "الورق رقم {0}");
        c.Add("ticketcheck.value.trayNumber", "Tray number {0}", "الدرج رقم {0}");
        c.Add("ticketcheck.value.multipurposeTray", "Multipurpose tray", "الدرج متعدد الأغراض");

        c.Add("ticketcheck.ok.title", "The ticket software uses the ticket settings", "يستخدم برنامج التذاكر إعدادات التذكرة");
        c.Add("ticketcheck.ok.text", "The print job asked for the AMWAG Ticket settings. Nothing needs to change.", "طلبت مهمة الطباعة إعدادات تذكرة AMWAG. لا يلزم تغيير أي شيء.");
        c.Add("ticketcheck.ok.notSpecified", "Settings the program did not set come from the printer defaults that the setup configured.", "الإعدادات التي لم يحددها البرنامج تؤخذ من الإعدادات الافتراضية للطابعة التي ضبطها الإعداد.");
        c.Add("ticketcheck.differs.title", "The ticket software overrides printer settings", "برنامج التذاكر يتجاوز إعدادات الطابعة");
        c.Add("ticketcheck.differs.text", "The print job asked for settings that differ from the AMWAG Ticket defaults, so tickets may not print correctly.", "طلبت مهمة الطباعة إعدادات تختلف عن الإعدادات الافتراضية لتذكرة AMWAG، لذلك قد لا تُطبع التذاكر بشكل صحيح.");

        c.Add("ticketcheck.guidance.title", "In the ticket software's page setup or print settings:", "في إعداد الصفحة أو إعدادات الطباعة في برنامج التذاكر:");
        c.Add("ticketcheck.guidance.paper", "Paper size: choose \"{0}\", or the printer's default paper size.", "حجم الورق: اختر «{0}» أو حجم الورق الافتراضي للطابعة.");
        c.Add("ticketcheck.guidance.orientation", "Orientation: {0}.", "الاتجاه: {0}.");
        c.Add("ticketcheck.guidance.duplex", "Two-sided printing: {0}.", "الطباعة على الوجهين: {0}.");
        c.Add("ticketcheck.guidance.source", "Paper source: {0}, or the printer default.", "مصدر الورق: {0} أو الإعداد الافتراضي للطابعة.");
        c.Add("ticketcheck.guidance.scale", "Margins: none. Scale: {0}, with \"fit to page\" turned off.", "الهوامش: بدون هوامش. مقياس الطباعة: {0} مع إيقاف خيار «ملاءمة الصفحة».");
        c.Add("ticketcheck.guidance.docs", "Then print one ticket and check again. IT finds the settings for browsers and report designers in:", "ثم اطبع تذكرة واحدة وافحص مرة أخرى. يجد فريق تقنية المعلومات إعدادات المتصفحات ومصممي التقارير في:");

        c.Add("ticketcheck.gone.title", "The print job left the queue too quickly", "غادرت مهمة الطباعة قائمة الانتظار بسرعة كبيرة");
        c.Add("ticketcheck.gone.text", "Windows passed the ticket to the printer before its settings could be read. Pause the printer in Windows during the check (open its print queue, then Printer > Pause Printing; this may need an IT account), check again and print one ticket, then resume printing.", "أرسل Windows التذكرة إلى الطابعة قبل أن تتسنى قراءة إعداداتها. أوقف الطابعة مؤقتًا في Windows أثناء الفحص (افتح قائمة انتظار الطباعة، ثم الطابعة > إيقاف الطباعة مؤقتًا؛ قد يلزم لذلك حساب لفريق تقنية المعلومات)، ثم افحص مرة أخرى واطبع تذكرة واحدة، وبعدها استأنف الطباعة.");
        c.Add("ticketcheck.noSettings.title", "Windows did not report the job's settings", "لم يُبلغ Windows عن إعدادات المهمة");
        c.Add("ticketcheck.noSettings.text", "A print job arrived, but Windows did not report the settings it was printed with. Compare a real ticket with the test ticket instead.", "وصلت مهمة طباعة، لكن Windows لم يُبلغ عن الإعدادات التي طُبعت بها. قارن تذكرة حقيقية بتذكرة الاختبار بدلًا من ذلك.");
        c.Add("ticketcheck.noJob.title", "No print job arrived", "لم تصل أي مهمة طباعة");
        c.Add("ticketcheck.noJob.text", "Nothing was printed to \"{0}\" within 2 minutes. Check that the ticket software prints to this printer, then check again. If you did print, the job may have left the queue too quickly: pause the printer in Windows during the check.", "لم يُطبع أي شيء على «{0}» خلال دقيقتين. تأكّد من أن برنامج التذاكر يطبع على هذه الطابعة، ثم افحص مرة أخرى. إذا كنت قد طبعت بالفعل، فربما غادرت المهمة قائمة الانتظار بسرعة: أوقف الطابعة مؤقتًا في Windows أثناء الفحص.");
        c.Add("ticketcheck.cancelled.title", "The check was stopped", "تم إيقاف الفحص");
        c.Add("ticketcheck.cancelled.text", "Nothing was changed.", "لم يتم تغيير أي شيء.");
        c.Add("ticketcheck.unreadable.title", "The print queue could not be read", "تعذّرت قراءة قائمة انتظار الطباعة");
        c.Add("ticketcheck.unreadable.text", "Check that the printer is still installed and that the Print Spooler service is running, then check again.", "تأكّد من أن الطابعة لا تزال مثبّتة وأن خدمة التخزين المؤقت للطباعة قيد التشغيل، ثم افحص مرة أخرى.");
        c.Add("ticketcheck.failed.title", "The check could not run", "تعذّر تشغيل الفحص");
        c.Add("ticketcheck.failed.text", "Something went wrong while reading the print queue. The log has the details.", "حدث خطأ أثناء قراءة قائمة انتظار الطباعة. يحتوي السجل على التفاصيل.");
    }
}
