namespace Amwag.PrinterSetup.App.Localization;

public sealed partial class Strings
{
    private static void AddSteps(CatalogBuilder c)
    {
        c.Add("step.pending", "Waiting", "في الانتظار");
        // A step the run ended before: on the final list, never "Waiting".
        c.Add("step.notReached", "Not reached", "لم يتم الوصول إليها");
        c.Add("step.notStarted", "Not started; nothing was changed", "لم تبدأ؛ لم يتم تغيير أي شيء");
        c.Add("step.stopped", "Not finished: the setup was stopped", "لم تكتمل: تم إيقاف الإعداد");
        // An apply step that began but could not finish because an action of the other apply step failed.
        c.Add("step.afterFailure", "Not finished: another step failed", "لم تكتمل: فشلت خطوة أخرى");
        c.Add("step.notChanged", "Not changed: Diagnostics only checks", "لم يتم التغيير: التشخيص يفحص فقط");
        // Diagnostics' driver check and verification at the end when no single ticket printer is known: the printers it may be
        // were checked (one or several), or the ticket printer could not be checked (its model must be confirmed, or nothing
        // usable is at its address).
        c.Add("step.ticketUnknown", "Checked; ticket printer not known", "تم الفحص؛ طابعة التذاكر غير معروفة");
        c.Add("step.notChecked", "Not checked", "لم يتم الفحص");

        c.Add("step.Detect.title", "Detecting printer", "البحث عن الطابعة");
        c.Add("step.Detect.Running", "Looking for your printer…", "جارٍ البحث عن الطابعة…");
        c.Add("step.Detect.Succeeded", "Printer found", "تم العثور على الطابعة");
        c.Add("step.Detect.Skipped", "Skipped", "تم التخطي");
        c.Add("step.Detect.Warning", "Found, with warnings", "تم العثور عليها مع تحذيرات");
        c.Add("step.Detect.Failed", "No printer found", "لم يتم العثور على طابعة");
        // Repair's printer has no queue yet: not "No printer found".
        c.Add("step.Detect.notSetUp", "Printer not set up yet", "الطابعة غير مُعدّة بعد");
        // Repair found supported printers, none of which has a queue yet.
        c.Add("step.Detect.noneSetUp", "Printers found, none set up yet", "تم العثور على طابعات، لكن لم يتم إعداد أيٍّ منها بعد");
        // Repair's printer whose model nobody confirmed.
        c.Add("step.Detect.modelNotConfirmed", "Printer found; model not confirmed", "تم العثور على الطابعة؛ لم يتم تأكيد طرازها");
        // A queue the setup leaves unchanged keeps the printer at the configured address from being set up.
        c.Add("step.Detect.leftUnchanged", "Printer not set up automatically", "لا يمكن إعداد الطابعة تلقائيًا");
        // An environment blocker (the Print Spooler, protected print mode, the platform, the configuration, the ticket dimensions).
        c.Add("step.Detect.blocked", "This computer cannot be set up as it is", "لا يمكن إعداد هذا الجهاز بوضعه الحالي");
        // IT's model (--model with --confirm-model) that the printer contradicts: the printer was found.
        c.Add("step.Detect.modelRefused", "Printer found; the model given does not match", "تم العثور على الطابعة؛ الطراز المحدَّد لا يطابقها");

        c.Add("step.CheckDriver.title", "Checking driver", "فحص برنامج التشغيل");
        c.Add("step.CheckDriver.Running", "Checking the printer driver…", "جارٍ فحص برنامج تشغيل الطابعة…");
        c.Add("step.CheckDriver.Succeeded", "Driver is ready", "برنامج التشغيل جاهز");
        c.Add("step.CheckDriver.Skipped", "Not needed", "غير مطلوب");
        c.Add("step.CheckDriver.Warning", "Driver needs attention", "برنامج التشغيل يحتاج إلى انتباه");
        c.Add("step.CheckDriver.Failed", "Driver check failed", "فشل فحص برنامج التشغيل");
        // Setup and Repair check the driver before they install (or update) it; at the end the read-back says whether they did.
        c.Add("step.CheckDriver.willInstall", "Driver will be installed", "سيتم تثبيت برنامج التشغيل");
        c.Add("step.CheckDriver.willUpdate", "Driver will be updated", "سيتم تحديث برنامج التشغيل");
        c.Add("step.CheckDriver.notInstalled", "Driver not installed", "برنامج التشغيل غير مثبّت");
        c.Add("step.CheckDriver.notUpdated", "Driver not updated", "لم يتم تحديث برنامج التشغيل");
        // The read-back did not check the driver (it did not find the printer again), and no install action says what happened.
        c.Add("step.CheckDriver.notConfirmed", "Driver state not confirmed", "لم يتم التأكد من حالة برنامج التشغيل");

        c.Add("step.InstallDriver.title", "Installing driver", "تثبيت برنامج التشغيل");
        c.Add("step.InstallDriver.Running", "Installing the printer driver…", "جارٍ تثبيت برنامج تشغيل الطابعة…");
        c.Add("step.InstallDriver.Succeeded", "Driver installed", "تم تثبيت برنامج التشغيل");
        c.Add("step.InstallDriver.Skipped", "Already installed", "مثبّت بالفعل");
        c.Add("step.InstallDriver.Warning", "Installed, with warnings", "تم التثبيت مع تحذيرات");
        c.Add("step.InstallDriver.Failed", "Installation failed", "فشل التثبيت");

        c.Add("step.ConfigureTicket.title", "Configuring ticket size", "ضبط حجم التذكرة");
        c.Add("step.ConfigureTicket.Running", "Setting up the ticket paper size…", "جارٍ ضبط حجم ورق التذكرة…");
        c.Add("step.ConfigureTicket.Succeeded", "Ticket size set", "تم ضبط حجم التذكرة");
        c.Add("step.ConfigureTicket.Skipped", "Already correct", "مضبوط بالفعل");
        c.Add("step.ConfigureTicket.Warning", "Set, with warnings", "تم الضبط مع تحذيرات");
        c.Add("step.ConfigureTicket.Failed", "Could not set the ticket size", "تعذّر ضبط حجم التذكرة");

        c.Add("step.Verify.title", "Verification", "التحقق");
        c.Add("step.Verify.Running", "Checking that the settings were saved…", "جارٍ التحقق من حفظ الإعدادات…");
        c.Add("step.Verify.Succeeded", "All settings verified", "تم التحقق من جميع الإعدادات");
        c.Add("step.Verify.Skipped", "Skipped", "تم التخطي");
        c.Add("step.Verify.Warning", "Verified, with warnings", "تم التحقق مع تحذيرات");
        c.Add("step.Verify.Failed", "Some settings are wrong", "بعض الإعدادات غير صحيحة");

        c.Add("step.Ready.title", "Ready", "جاهزة للطباعة");
        c.Add("step.Ready.Running", "Finishing…", "جارٍ الإنهاء…");
        c.Add("step.Ready.Succeeded", "Ready to print tickets", "جاهزة لطباعة التذاكر");
        c.Add("step.Ready.Skipped", "Not reached", "لم يتم الوصول إليها");
        c.Add("step.Ready.Warning", "Ready, with warnings", "جاهزة مع تحذيرات");
        c.Add("step.Ready.Failed", "Not ready", "غير جاهزة");

        c.Add("phase.Started", "Starting…", "جارٍ البدء…");
        c.Add("phase.Downloading", "Downloading the approved printer driver…", "جارٍ تنزيل برنامج تشغيل الطابعة المعتمد…");
        c.Add("phase.Verifying", "Checking file integrity and digital signature…", "جارٍ التحقق من سلامة الملفات والتوقيع الرقمي…");
        c.Add("phase.Extracting", "Unpacking the driver package…", "جارٍ فك حزمة برنامج التشغيل…");
        c.Add("phase.Installing", "Installing…", "جارٍ التثبيت…");
        c.Add("phase.Waiting", "Waiting for Windows to finish setting up the printer…", "في انتظار أن يُكمل Windows إعداد الطابعة…");
        c.Add("phase.Configuring", "Applying settings…", "جارٍ تطبيق الإعدادات…");
        c.Add("phase.Completed", "Done", "تم");
        c.Add("phase.Skipped", "Nothing to change", "لا يلزم أي تغيير");
        c.Add("phase.Failed", "This step failed", "فشلت هذه الخطوة");
    }

    private static void AddActions(CatalogBuilder c)
    {
        c.Add("action.StartSpooler.running", "Starting the Print Spooler service…", "جارٍ تشغيل خدمة التخزين المؤقت للطباعة…");
        c.Add("action.StartSpooler.done", "Print Spooler service started", "تم تشغيل خدمة التخزين المؤقت للطباعة");
        c.Add("action.StartSpooler.skipped", "Print Spooler service already running", "خدمة التخزين المؤقت للطباعة تعمل بالفعل");
        c.Add("action.StartSpooler.failed", "Could not start the Print Spooler service", "تعذّر تشغيل خدمة التخزين المؤقت للطباعة");

        c.Add("action.PrepareDriverPackage.running", "Preparing the approved printer driver…", "جارٍ تجهيز برنامج تشغيل الطابعة المعتمد…");
        c.Add("action.PrepareDriverPackage.done", "Driver package ready", "حزمة برنامج التشغيل جاهزة");
        c.Add("action.PrepareDriverPackage.skipped", "Driver package not needed", "حزمة برنامج التشغيل غير مطلوبة");
        c.Add("action.PrepareDriverPackage.failed", "Could not prepare the driver package", "تعذّر تجهيز حزمة برنامج التشغيل");

        c.Add("action.InstallDriver.running", "Installing the printer driver…", "جارٍ تثبيت برنامج تشغيل الطابعة…");
        c.Add("action.InstallDriver.done", "Printer driver installed", "تم تثبيت برنامج تشغيل الطابعة");
        c.Add("action.InstallDriver.skipped", "Printer driver already installed", "برنامج تشغيل الطابعة مثبّت بالفعل");
        c.Add("action.InstallDriver.failed", "Could not install the printer driver", "تعذّر تثبيت برنامج تشغيل الطابعة");

        c.Add("action.BindUsbDevice.running", "Connecting the driver to the USB printer…", "جارٍ ربط برنامج التشغيل بطابعة USB…");
        c.Add("action.BindUsbDevice.done", "USB printer linked to the driver", "تم ربط طابعة USB ببرنامج التشغيل");
        c.Add("action.BindUsbDevice.skipped", "USB printer already uses the driver", "طابعة USB تستخدم برنامج التشغيل بالفعل");
        c.Add("action.BindUsbDevice.failed", "Could not link the driver to the USB printer", "تعذّر ربط برنامج التشغيل بطابعة USB");

        c.Add("action.CreateTcpIpPort.running", "Creating the network printer port…", "جارٍ إنشاء منفذ طابعة الشبكة…");
        c.Add("action.CreateTcpIpPort.done", "Network port created", "تم إنشاء منفذ الشبكة");
        c.Add("action.CreateTcpIpPort.skipped", "Network port already exists", "منفذ الشبكة موجود بالفعل");
        c.Add("action.CreateTcpIpPort.failed", "Could not create the network port", "تعذّر إنشاء منفذ الشبكة");

        c.Add("action.CreateQueue.running", "Adding the printer to Windows…", "جارٍ إضافة الطابعة إلى Windows…");
        c.Add("action.CreateQueue.done", "Printer added", "تمت إضافة الطابعة");
        c.Add("action.CreateQueue.skipped", "Printer already added", "الطابعة مضافة بالفعل");
        c.Add("action.CreateQueue.failed", "Could not add the printer", "تعذّرت إضافة الطابعة");

        c.Add("action.ChangeQueueDriver.running", "Switching the printer to the approved driver…", "جارٍ تحويل الطابعة إلى برنامج التشغيل المعتمد…");
        c.Add("action.ChangeQueueDriver.done", "Printer now uses the approved driver", "تستخدم الطابعة الآن برنامج التشغيل المعتمد");
        c.Add("action.ChangeQueueDriver.skipped", "Printer already uses the approved driver", "تستخدم الطابعة برنامج التشغيل المعتمد بالفعل");
        c.Add("action.ChangeQueueDriver.failed", "Could not change the printer's driver", "تعذّر تغيير برنامج تشغيل الطابعة");

        c.Add("action.ChangeQueuePort.running", "Updating the printer's port…", "جارٍ تحديث منفذ الطابعة…");
        c.Add("action.ChangeQueuePort.done", "Printer port updated", "تم تحديث منفذ الطابعة");
        c.Add("action.ChangeQueuePort.skipped", "Printer port already correct", "منفذ الطابعة صحيح بالفعل");
        c.Add("action.ChangeQueuePort.failed", "Could not update the printer's port", "تعذّر تحديث منفذ الطابعة");

        c.Add("action.CreateForm.running", "Creating the ticket paper size…", "جارٍ إنشاء حجم ورق التذكرة…");
        c.Add("action.CreateForm.done", "Ticket paper size created", "تم إنشاء حجم ورق التذكرة");
        c.Add("action.CreateForm.skipped", "Ticket paper size already exists", "حجم ورق التذكرة موجود بالفعل");
        c.Add("action.CreateForm.failed", "Could not create the ticket paper size", "تعذّر إنشاء حجم ورق التذكرة");

        c.Add("action.UpdateForm.running", "Correcting the ticket paper size…", "جارٍ تصحيح حجم ورق التذكرة…");
        c.Add("action.UpdateForm.done", "Ticket paper size corrected", "تم تصحيح حجم ورق التذكرة");
        c.Add("action.UpdateForm.skipped", "Ticket paper size already correct", "حجم ورق التذكرة صحيح بالفعل");
        c.Add("action.UpdateForm.failed", "Could not correct the ticket paper size", "تعذّر تصحيح حجم ورق التذكرة");

        c.Add("action.ConfigureGlobalDefaults.running", "Setting the printer's default preferences…", "جارٍ ضبط تفضيلات الطباعة الافتراضية للطابعة…");
        c.Add("action.ConfigureGlobalDefaults.done", "Default preferences saved", "تم حفظ التفضيلات الافتراضية");
        c.Add("action.ConfigureGlobalDefaults.skipped", "Default preferences already correct", "التفضيلات الافتراضية صحيحة بالفعل");
        c.Add("action.ConfigureGlobalDefaults.failed", "Could not save the default preferences", "تعذّر حفظ التفضيلات الافتراضية");

        c.Add("action.ConfigureUserDefaults.running", "Setting your printing preferences…", "جارٍ ضبط تفضيلات الطباعة الخاصة بك…");
        c.Add("action.ConfigureUserDefaults.done", "Your printing preferences saved", "تم حفظ تفضيلات الطباعة الخاصة بك");
        c.Add("action.ConfigureUserDefaults.skipped", "Your printing preferences are already correct", "تفضيلات الطباعة الخاصة بك صحيحة بالفعل");
        c.Add("action.ConfigureUserDefaults.failed", "Could not save your printing preferences", "تعذّر حفظ تفضيلات الطباعة الخاصة بك");
    }
}
