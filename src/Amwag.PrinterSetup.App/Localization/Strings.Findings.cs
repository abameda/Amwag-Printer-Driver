namespace Amwag.PrinterSetup.App.Localization;

/// <summary>Finding texts for employees: a short title plus one or two plain, actionable sentences per FindingCode.</summary>
public sealed partial class Strings
{
    private static void Finding(CatalogBuilder c, string code, string englishTitle, string englishText, string arabicTitle, string arabicText)
    {
        c.Add($"finding.{code}.title", englishTitle, arabicTitle);
        c.Add($"finding.{code}.text", englishText, arabicText);
    }

    private static void AddEnvironmentAndDetectionFindings(CatalogBuilder c)
    {
        Finding(c, "SpoolerNotRunning",
            "Print service is not running", "The Windows Print Spooler service is stopped. The setup tries to start it; if that fails, restart the computer.",
            "خدمة الطباعة لا تعمل", "خدمة التخزين المؤقت للطباعة في Windows متوقفة. يحاول الإعداد تشغيلها، وإذا لم ينجح فأعد تشغيل الجهاز.");
        Finding(c, "SpoolerDisabled",
            "Print service is disabled", "The Windows Print Spooler service is disabled on this computer. Ask IT to enable it.",
            "خدمة الطباعة معطّلة", "خدمة التخزين المؤقت للطباعة معطّلة على هذا الجهاز. اطلب من فريق تقنية المعلومات تفعيلها.");
        Finding(c, "SpoolerUnavailable",
            "Print service is not responding", "Windows printing is not responding. Restart the computer and try again.",
            "خدمة الطباعة لا تستجيب", "خدمة الطباعة في Windows لا تستجيب. أعد تشغيل الجهاز وحاول مرة أخرى.");
        Finding(c, "ProtectedPrintModeEnabled",
            "Windows protected print mode is on", "This mode blocks drivers from printer manufacturers. Ask IT to turn it off for this computer.",
            "وضع الطباعة المحمي في Windows مفعّل", "يمنع هذا الوضع برامج التشغيل الصادرة عن الشركات المصنّعة للطابعات. اطلب من فريق تقنية المعلومات إيقافه على هذا الجهاز.");
        Finding(c, "UnsupportedOperatingSystem",
            "This Windows version is not supported", "The approved driver needs Windows 10 or Windows 11. Ask IT for help.",
            "إصدار Windows هذا غير مدعوم", "يتطلب برنامج التشغيل المعتمد نظام Windows 10 أو Windows 11. اطلب المساعدة من فريق تقنية المعلومات.");
        Finding(c, "UnsupportedArchitecture",
            "This computer type is not supported", "The approved driver is for 64-bit (x64) Windows only. Ask IT for help.",
            "نوع هذا الجهاز غير مدعوم", "برنامج التشغيل المعتمد مخصّص لنظام Windows بإصدار 64 بت (x64) فقط. اطلب المساعدة من فريق تقنية المعلومات.");
        Finding(c, "DimensionsUnconfirmed",
            "Ticket size not approved yet", "IT has not yet confirmed the ticket dimensions, so nothing was changed. Contact IT.",
            "حجم التذكرة لم يُعتمد بعد", "لم يؤكد فريق تقنية المعلومات أبعاد التذكرة بعد، لذلك لم يتم تغيير أي شيء. تواصل مع فريق تقنية المعلومات.");
        Finding(c, "ConfigurationInvalid",
            "Configuration problem", "The setup's configuration file is not valid. Contact IT.",
            "مشكلة في ملف الإعدادات", "ملف إعدادات أداة الإعداد غير صالح. تواصل مع فريق تقنية المعلومات.");
        Finding(c, "ServerConnectionNotManaged",
            "Printer is managed by a print server", "This printer is shared from a print server and must be configured there by IT. It was not changed.",
            "الطابعة تُدار عبر خادم طباعة", "هذه الطابعة مشتركة من خادم طباعة ويجب أن يضبطها فريق تقنية المعلومات هناك. لم يتم تغييرها.");

        Finding(c, "NoSupportedPrinterFound",
            "No ticket printer found", "Check that the printer is switched on and connected with its USB cable or to the network.",
            "لم يتم العثور على طابعة تذاكر", "تأكّد من أن الطابعة قيد التشغيل وموصولة بكابل USB أو بالشبكة.");
        // Repair's end for its printer without a queue: the printer is known, only Setup adds it to Windows.
        Finding(c, "NoSupportedPrinterFound.needsSetup",
            "Printer not set up yet", "This printer has not been added to Windows yet, so Repair cannot change it. Choose Run Setup to set it up.",
            "الطابعة غير مُعدّة بعد", "لم تُضَف هذه الطابعة إلى Windows بعد، لذلك لا يمكن تغييرها باستخدام «إصلاح». اختر «تشغيل الإعداد» لإعدادها.");
        // Repair's end for supported printers none of which has a queue: they are connected, only Setup adds one to Windows.
        Finding(c, "NoSupportedPrinterFound.printersWithoutQueue",
            "Printers not set up yet", "Supported printers were found, but none of them has been added to Windows yet, so Repair cannot change them. Choose Run Setup to set up the ticket printer.",
            "الطابعات غير مُعدّة بعد", "تم العثور على طابعات مدعومة، لكن لم تتم إضافة أيٍّ منها إلى Windows بعد، لذلك لا يمكن تغييرها باستخدام «إصلاح». اختر «تشغيل الإعداد» لإعداد طابعة التذاكر.");
        // The printer was found, but nobody confirmed its model; trying again asks again.
        Finding(c, "NoSupportedPrinterFound.modelNotConfirmed",
            "Printer model not confirmed", "The printer was found, but the setup changes it only after its model is confirmed. Check the model on the printer's label, then choose Try again and confirm it.",
            "لم يتم تأكيد طراز الطابعة", "تم العثور على الطابعة، لكن الإعداد لا يغيّرها إلا بعد تأكيد طرازها. تحقّق من الطراز المدوَّن على ملصق الطابعة، ثم اختر «حاول مرة أخرى» وأكّده.");
        // Nothing usable at the configured printer address: {0} the address. A network printer needs no cable advice.
        Finding(c, "NoSupportedPrinterFound.notAtAddress",
            "No ticket printer at this address", "No supported ticket printer was found at {0}, the address set for the ticket printer: nothing answers there, or the device there is not a supported ticket printer. Check the address, and that the printer is switched on and connected to the network.",
            "لا توجد طابعة تذاكر على هذا العنوان", "لم يتم العثور على طابعة تذاكر مدعومة على العنوان {0}، وهو العنوان المحدَّد لطابعة التذاكر: لا يستجيب أي جهاز عليه، أو أن الجهاز الموجود عليه ليس طابعة تذاكر مدعومة. تحقّق من العنوان، ومن أن الطابعة قيد التشغيل ومتصلة بالشبكة.");
        // Under the QueueLeftUnchanged title: {0} the queue left unchanged, {1} the configured address of the printer it prints to.
        // The technical details below give the reason (pooled ports, an LPR or IPP port).
        c.Add("finding.NoSupportedPrinterFound.leftUnchanged.text",
            "The printer entry {0} prints to the printer at {1} in a way the setup cannot configure (see the technical details below), so that printer is not set up automatically. Ask IT to change that printer entry or delete it, then run the setup again.",
            "يطبع إدخال الطابعة {0} إلى الطابعة الموجودة على العنوان {1} بطريقة لا يستطيع الإعداد ضبطها (راجع التفاصيل التقنية أدناه)، لذلك لا يتم إعداد تلك الطابعة تلقائيًا. اطلب من فريق تقنية المعلومات تغيير إدخال الطابعة هذا أو حذفه، ثم شغّل الإعداد مرة أخرى.");
        // Shown by Diagnostics, which has nothing to choose: Setup and Repair ask instead.
        Finding(c, "MultipleSupportedPrinters",
            "Several supported printers found", "It is not known which of them prints tickets, so each one was checked. See the details below.",
            "تم العثور على عدة طابعات مدعومة", "لا يُعرف أيٌّ منها يطبع التذاكر، لذلك تم فحص كلٍّ منها. راجع التفاصيل أدناه.");
        Finding(c, "ProbableMatchRequiresConfirmation",
            "Please confirm the printer model", "The printer's model could not be confirmed automatically.",
            "يُرجى تأكيد طراز الطابعة", "تعذّر تأكيد طراز الطابعة تلقائيًا.");
        Finding(c, "UnsupportedPrinterDetected",
            "Another printer model was found", "A printer was found, but it is not one of the supported TA Triumph-Adler models. It was not changed.",
            "تم العثور على طراز طابعة آخر", "تم العثور على طابعة، لكنها ليست من طرازات TA Triumph-Adler المدعومة. لم يتم تغييرها.");
        Finding(c, "DeviceNotConnected",
            "Printer not connected", "Windows knows this printer but cannot see it now. Switch it on and check the USB cable.",
            "الطابعة غير موصولة", "يعرف Windows هذه الطابعة لكنه لا يراها الآن. شغّل الطابعة وتحقّق من كابل USB.");
        Finding(c, "NetworkPrinterUnreachable",
            "Printer does not answer on the network", "Check that the printer is on and connected to the network, and that the address is correct.",
            "الطابعة لا تستجيب على الشبكة", "تأكّد من أن الطابعة قيد التشغيل ومتصلة بالشبكة، ومن صحة العنوان.");
        Finding(c, "NetworkIdentityUnavailable",
            "Printer model could not be read", "The printer answers but does not report its model over the network.",
            "تعذّرت قراءة طراز الطابعة", "تستجيب الطابعة لكنها لا تُبلغ عن طرازها عبر الشبكة.");
        // A printer address or printer entry the limit of network probes left unchecked (Data reason "probeLimit").
        Finding(c, "NetworkIdentityUnavailable.notChecked",
            "Printer not checked", "A printer was not checked because the limit of network checks was reached, and it may be the ticket printer. Ask IT to raise the limit or to name the ticket printer's address.",
            "لم يتم فحص طابعة", "لم يتم فحص طابعة لأنه تم بلوغ الحد الأقصى لعمليات الفحص عبر الشبكة، وقد تكون هي طابعة التذاكر. اطلب من فريق تقنية المعلومات رفع هذا الحد أو تحديد عنوان طابعة التذاكر.");
        Finding(c, "NetworkIdentityMismatch",
            "Different printer at this address", "The printer at this address is not a supported ticket printer. Check the address.",
            "طابعة مختلفة على هذا العنوان", "الطابعة الموجودة على هذا العنوان ليست طابعة تذاكر مدعومة. تحقّق من العنوان.");
        // IT's model (--model with --confirm-model) that the printer contradicts: {0} the model given, {1} the address, {2} the
        // model the printer reports. The printer may well be supported; the address or the model given is wrong.
        Finding(c, "NetworkIdentityMismatch.modelRefused",
            "Model does not match this printer", "The model {0} was given for the printer at {1}, but the printer reports {2}. Check the address and the model on the printer's label, then start the program again with the correct ones, or contact IT.",
            "الطراز لا يطابق هذه الطابعة", "تم تحديد الطراز {0} للطابعة الموجودة على العنوان {1}، لكن الطابعة تُبلغ عن الطراز {2}. تحقّق من العنوان ومن الطراز المدوَّن على ملصق الطابعة، ثم شغّل البرنامج مرة أخرى بالقيم الصحيحة، أو تواصل مع فريق تقنية المعلومات.");
        c.Add("finding.NetworkIdentityMismatch.modelRefused.otherModel",
            "The model {0} was given for the printer at {1}, but the printer reports another model. Check the address and the model on the printer's label, then start the program again with the correct ones, or contact IT.",
            "تم تحديد الطراز {0} للطابعة الموجودة على العنوان {1}، لكن الطابعة تُبلغ عن طراز آخر. تحقّق من العنوان ومن الطراز المدوَّن على ملصق الطابعة، ثم شغّل البرنامج مرة أخرى بالقيم الصحيحة، أو تواصل مع فريق تقنية المعلومات.");
        Finding(c, "IdentityConflict",
            "Printer and driver do not match", "The connected printer reports a different model than its driver. The setup follows the printer's own report.",
            "الطابعة وبرنامج التشغيل غير متطابقين", "تُبلغ الطابعة الموصولة عن طراز مختلف عن برنامج تشغيلها. يعتمد الإعداد على ما تُبلغ عنه الطابعة نفسها.");
        Finding(c, "QueueLeftUnchanged",
            "Printer entry left unchanged", "Windows has a printer entry that the setup cannot configure or could not match to its printer (another printer is connected there, its drivers disagree, it prints through several ports or an LPR or IPP port, or its port cannot be checked), so it was not changed, and a printer it prints to is not set up automatically; no second entry is added on a port it already prints to. If it is the ticket printer's entry, switch the ticket printer on, or have IT move the entry to a single Standard TCP/IP port, then run the setup again.",
            "إدخال طابعة لم يتغير", "يحتوي Windows على إدخال طابعة لا يستطيع الإعداد ضبطه أو تعذّر ربطه بطابعته (طابعة أخرى موصولة به، أو برامج تشغيله غير متطابقة، أو أنه يطبع عبر عدة منافذ (تجميع الطابعات) أو عبر منفذ LPR أو IPP، أو تعذّر التحقق من منفذه)، لذلك لم يتم تغييره، ولا تُضبط تلقائيًا الطابعة التي يطبع إليها، ولا يُضاف إدخال ثانٍ على منفذ يطبع إليه بالفعل. إذا كان هذا إدخال طابعة التذاكر، فشغّل طابعة التذاكر، أو اطلب من فريق تقنية المعلومات نقل الإدخال إلى منفذ TCP/IP قياسي واحد، ثم شغّل الإعداد مرة أخرى.");
        Finding(c, "DuplicateQueues",
            "Printer added more than once", "Windows has several entries for this printer. Only one was configured; the others were left unchanged.",
            "الطابعة مضافة أكثر من مرة", "يحتوي Windows على عدة إدخالات لهذه الطابعة. تم ضبط إدخال واحد فقط وتُركت الإدخالات الأخرى دون تغيير.");
        // The setup added a printer entry next to {0}, another entry that may print to the same printer (Data reason "queueAdded").
        // It may as well be another printer (a WSD entry's address is not known), so the text does not say that it is.
        Finding(c, "DuplicateQueues.queueAdded",
            "Another printer entry may print to this printer", "Windows also has the printer entry {0}, which may print to the ticket printer. If it does, make sure the ticket software prints to the entry the setup added, and delete {0}. If {0} is another printer, keep it.",
            "قد يطبع إدخال طابعة آخر إلى هذه الطابعة", "يحتوي Windows أيضًا على إدخال الطابعة {0}، وقد يطبع إلى طابعة التذاكر. إذا كان كذلك، فتأكّد من أن برنامج التذاكر يطبع إلى الإدخال الذي أضافه الإعداد، واحذف {0}. وإذا كان {0} طابعة أخرى، فاحتفظ به.");
        // The same for several such entries: {0} lists them (each isolated, with the language's list separator).
        c.Add("finding.DuplicateQueues.queueAdded.many",
            "Windows also has the printer entries {0}, any of which may print to the ticket printer. Make sure the ticket software prints to the entry the setup added, and delete an entry that prints to the ticket printer. Keep the entries of other printers.",
            "يحتوي Windows أيضًا على إدخالات الطابعات {0}، وقد يطبع أيٌّ منها إلى طابعة التذاكر. تأكّد من أن برنامج التذاكر يطبع إلى الإدخال الذي أضافه الإعداد، واحذف أي إدخال يطبع إلى طابعة التذاكر. واحتفظ بإدخالات الطابعات الأخرى.");
    }
}
