namespace Amwag.PrinterSetup.App.Localization;

public sealed partial class Strings
{
    private static void AddInterface(CatalogBuilder c)
    {
        c.Add("header.subtitle.Setup", "Sets up the ticket printer for your branch", "إعداد طابعة التذاكر في فرعك");
        c.Add("header.subtitle.Repair", "Checks and repairs the ticket printer settings", "فحص إعدادات طابعة التذاكر وإصلاحها");
        c.Add("header.subtitle.Diagnostics", "Checks the ticket printer without changing anything", "فحص طابعة التذاكر دون تغيير أي شيء");
        c.Add("header.language.accessible", "Switch the language to Arabic", "تغيير اللغة إلى الإنجليزية");
        c.Add("header.contactIt", "Contact IT", "تواصل مع الدعم الفني");
        c.Add("header.contactIt.accessible", "Contact the IT Department on WhatsApp", "التواصل مع إدارة تكنولوجيا المعلومات عبر واتساب");
        c.Add("contact.title", "Contact the IT Department", "التواصل مع إدارة تكنولوجيا المعلومات");
        c.Add("contact.text", "A picture of this window and the setup log will be prepared for WhatsApp. Who should receive them?", "سيتم تجهيز صورة لهذه النافذة وسجل الإعداد لإرسالهما عبر واتساب. إلى من تريد إرسالهما؟");
        c.Add("contact.sent.title", "Finish in WhatsApp", "أكمل الإرسال في واتساب");
        c.Add("contact.sent.text", "WhatsApp is opening a chat with {0}, with the message already written. In the chat, press Ctrl+V to attach the picture and the log, then press Send.", "يجري فتح محادثة واتساب مع {0} والرسالة مكتوبة بالفعل. في المحادثة، اضغط Ctrl+V لإرفاق الصورة والسجل، ثم اضغط «إرسال».");
        c.Add("contact.sent.note", "If your browser opens first, choose to continue to the chat in WhatsApp. If the files are not attached, choose Show files and drag both files into the chat.", "إذا فُتح المتصفح أولًا، فاختر متابعة المحادثة في واتساب. وإذا لم تُرفق الملفات، فاختر «عرض الملفات» واسحب الملفين إلى المحادثة.");
        c.Add("contact.showFiles", "Show files", "عرض الملفات");
        c.Add("contact.done", "Done", "تم");
        c.Add("contact.prepareFailed", "The picture and the log could not be prepared: {0}", "تعذّر تجهيز الصورة والسجل: {0}");
        c.Add("contact.openFailed", "WhatsApp could not be opened. Choose Show files and send both files on WhatsApp to {0} at {1}.", "تعذّر فتح واتساب. اختر «عرض الملفات» وأرسل الملفين عبر واتساب إلى {0} على الرقم {1}.");
        c.Add("steps.accessible", "Setup steps", "خطوات الإعداد");
        // The company's own Arabic trade name replaces "AMWAG Travel" in its copyright line.
        c.Add("footer.copyright", "© 2026 AMWAG Travel · AMWAG IT Department", "© 2026 أمواج ترافيل · إدارة تكنولوجيا المعلومات");
        c.Add("footer.developedBy", "Developed by", "تطوير");
        c.Add("footer.developer", "Eng. Abdelhmeed Elshorbagy", "م. عبدالحميد الشوربجي");
        c.Add("footer.developer.accessible", "Eng. Abdelhmeed Elshorbagy, opens {0}", "م. عبدالحميد الشوربجي، يفتح {0}");
        c.Add("splash.title", "Printer Setup", "إعداد الطابعة");
        c.Add("splash.loadingSettings", "Loading settings…", "جارٍ تحميل الإعدادات…");
        c.Add("splash.openingWindow", "Opening the window…", "جارٍ فتح النافذة…");
        c.Add("splash.version", "Version {0}", "الإصدار {0}");

        c.Add("progress.title.Setup", "Setting up your ticket printer", "جارٍ إعداد طابعة التذاكر");
        c.Add("progress.title.Repair", "Checking and repairing your ticket printer", "جارٍ فحص طابعة التذاكر وإصلاحها");
        c.Add("progress.title.Diagnostics", "Checking your ticket printer", "جارٍ فحص طابعة التذاكر");
        c.Add("progress.explanation", "This usually takes less than a minute. You will only be asked something if it is really needed.", "يستغرق ذلك عادةً أقل من دقيقة، ولن يُطلب منك أي شيء إلا عند الضرورة.");
        c.Add("progress.elevation", "Windows will now ask for permission to install the printer driver and create the ticket paper size. Please choose Yes.", "سيطلب Windows الآن إذنًا لتثبيت برنامج تشغيل الطابعة وإنشاء حجم ورق التذكرة. يُرجى اختيار «نعم».");
        c.Add("progress.waitingForOtherRun", "Waiting for another AMWAG Printer Setup run to finish…", "في انتظار انتهاء عملية أخرى لأداة AMWAG Printer Setup…");
        c.Add("progress.cancelling", "Stopping after the current step…", "جارٍ الإيقاف بعد الخطوة الحالية…");
        c.Add("progress.cancelling.note", "A step that is already running with administrator rights finishes first, and later steps are not started. The window closes when the setup has stopped.", "تكتمل أولًا أي خطوة تعمل حاليًا بصلاحيات المسؤول، ولا تبدأ الخطوات التالية. تُغلق النافذة بعد توقف الإعداد.");
        c.Add("progress.accessible", "Progress", "مستوى التقدّم");

        c.Add("card.title", "Your printer", "الطابعة");
        c.Add("card.model", "Printer model", "طراز الطابعة");
        c.Add("card.connection", "Connection", "الاتصال");
        c.Add("card.driver", "Driver", "برنامج التشغيل");
        c.Add("card.queue", "Printer name", "اسم الطابعة");
        c.Add("card.ticketSize", "Ticket size", "حجم ورق التذكرة");
        c.Add("card.paperSource", "Paper source", "مصدر الورق");
        c.Add("card.identifiedBy", "Identified by", "طريقة التعرّف");
        c.Add("card.presence", "Status", "الحالة");
        c.Add("value.connected", "Connected", "موصولة");
        c.Add("value.notConnected", "Not connected", "غير موصولة");
        c.Add("value.earlierConnection", "Known from an earlier connection", "معروفة من اتصال سابق");
        c.Add("value.notAvailable", "Not available", "غير متوفر");
        c.Add("value.notYetInstalled", "Will be installed", "سيتم تثبيته");
        c.Add("value.newQueue", "Will be created", "سيتم إنشاؤها");
        c.Add("value.unknownModel", "Unknown model", "طراز غير معروف");
        c.Add("value.driverVersion", "{0} (version {1})", "{0} (الإصدار {1})");
        c.Add("value.paperSourceDefault", "Printer default", "الإعداد الافتراضي للطابعة");

        c.Add("connection.Usb", "USB cable", "كابل USB");
        c.Add("connection.TcpIp", "Network (TCP/IP)", "الشبكة (TCP/IP)");
        c.Add("connection.Wsd", "Network (WSD)", "الشبكة (WSD)");
        c.Add("connection.Other", "Other connection", "اتصال آخر");
        c.Add("connection.usbPort", "USB cable, port {0}", "كابل USB، المنفذ {0}");
        c.Add("connection.network", "Network, address {0}", "الشبكة، العنوان {0}");

        c.Add("confidence.Exact", "The printer reported its exact model", "أبلغت الطابعة عن طرازها بدقة");
        c.Add("confidence.DriverOnly", "Identified from the installed driver", "تم التعرّف عليها من برنامج التشغيل المثبّت");
        c.Add("confidence.Probable", "Probable match, needs your confirmation", "تطابق مرجّح يحتاج إلى تأكيدك");
        c.Add("confidence.UserAsserted", "Model chosen by you", "الطراز الذي اخترته");
        c.Add("confidence.None", "Not identified", "لم يتم التعرّف عليها");

        c.Add("ui.cancel", "Cancel", "إلغاء");
        c.Add("ui.yes", "Yes", "نعم");
        c.Add("ui.no", "No", "لا");

        c.Add("choose.title", "Choose the ticket printer", "اختر طابعة التذاكر");
        c.Add("choose.text", "More than one supported printer was found. Choose the one this computer prints tickets on.", "تم العثور على أكثر من طابعة مدعومة. اختر الطابعة التي يطبع عليها هذا الجهاز التذاكر.");
        c.Add("choose.text.single", "It is not certain that this printer is the ticket printer. Choose it only if this computer prints tickets on it.", "ليس من المؤكد أن هذه الطابعة هي طابعة التذاكر. اخترها فقط إذا كان هذا الجهاز يطبع التذاكر عليها.");
        c.Add("choose.continue", "Continue", "متابعة");
        c.Add("choose.noQueue", "Not added to Windows yet", "لم تُضَف إلى Windows بعد");

        c.Add("confirm.title", "Is this the right printer?", "هل هذه هي الطابعة الصحيحة؟");
        c.Add("confirm.text", "A printer at {0} reports \"{1}\". Is this a {2}?", "تُعرّف الطابعة الموجودة على العنوان {0} نفسها بالاسم «{1}». هل هي من طراز {2}؟");
        c.Add("confirm.hint", "Choose Yes only if the label on the printer shows this model.", "اختر «نعم» فقط إذا كان الملصق الموجود على الطابعة يُظهر هذا الطراز.");

        c.Add("network.title", "Enter the printer's network address", "أدخل عنوان الطابعة على الشبكة");
        c.Add("network.text.nothing", "No ticket printer was found automatically. A network printer that was never set up on this computer may not be found automatically.", "لم يتم العثور على طابعة تذاكر تلقائيًا. قد لا يُعثر تلقائيًا على طابعة شبكة لم يسبق إعدادها على هذا الجهاز.");
        c.Add("network.text.retry", "No ticket printer could be identified at that address. Check the address and try again.", "تعذّر التعرّف على طابعة تذاكر على هذا العنوان. تحقّق من العنوان وحاول مرة أخرى.");
        c.Add("network.text.declined", "Enter the ticket printer's address. A network printer that was never set up on this computer may not be found automatically.", "أدخل عنوان طابعة التذاكر. قد لا يُعثر تلقائيًا على طابعة شبكة لم يسبق إعدادها على هذا الجهاز.");
        c.Add("network.label", "IP address or host name", "عنوان IP أو اسم المضيف");
        c.Add("network.hint", "The address is shown on the printer's status page, for example {0}.", "يظهر العنوان في صفحة حالة الطابعة، مثل {0}.");
        c.Add("network.invalid", "Enter a valid IP address (for example {0}) or host name.", "أدخل عنوان IP صالحًا (مثل {0}) أو اسم مضيف صالحًا.");
        c.Add("network.previous", "Last address tried: {0}", "آخر عنوان تمت تجربته: {0}");
        c.Add("network.find", "Find printer", "البحث عن الطابعة");

        c.Add("model.title", "Which printer model is this?", "ما طراز هذه الطابعة؟");
        c.Add("model.text", "The printer at {0} answers on the network but does not report its model.", "الطابعة الموجودة على العنوان {0} تستجيب على الشبكة لكنها لا تُبلغ عن طرازها.");
        c.Add("model.warning", "Choose a model only if you are sure: check the label on the printer. A wrong choice installs the wrong driver.", "لا تختر طرازًا إلا إذا كنت متأكدًا: راجع الملصق الموجود على الطابعة. اختيار طراز خاطئ يؤدي إلى تثبيت برنامج تشغيل غير مناسب.");
        c.Add("model.continue", "Use this model", "استخدام هذا الطراز");

        c.Add("close.title", "Stop the setup?", "إيقاف الإعداد؟");
        c.Add("close.text", "The printer setup is still running. Stop it and close the window? A step that is already running with administrator rights finishes first and later steps are not started; the window closes when the setup has stopped. Changes already made stay in place, and you can run the setup again at any time.", "لا يزال إعداد الطابعة قيد التشغيل. هل تريد إيقافه وإغلاق النافذة؟ تكتمل أولًا أي خطوة تعمل حاليًا بصلاحيات المسؤول ولا تبدأ الخطوات التالية، وتُغلق النافذة بعد توقف الإعداد. تبقى التغييرات التي تمت كما هي، ويمكنك تشغيل الإعداد مرة أخرى في أي وقت.");
    }
}
