using Amwag.PrinterSetup.App.Localization;
using Amwag.PrinterSetup.App.Support;
using Amwag.PrinterSetup.App.UI;
using Amwag.PrinterSetup.Core.Configuration;

namespace Amwag.PrinterSetup.App.Tests.UI;

/// <summary>The Contact IT button and its dialog (never shown; nothing is written, copied or opened).</summary>
public sealed class ContactItTests
{
    private static readonly ItContact[] Contacts =
    [
        new() { Name = "Eng. Abdelhmeed Elshorbagy", NameArabic = "م. عبدالحميد الشوربجي", WhatsApp = "201061922226" },
        new() { Name = "Eng. Abdelrhman Elshorbagy", NameArabic = "م. عبدالرحمن الشوربجي", WhatsApp = "201011955580" },
    ];

    private static readonly SupportPackage Package = new(@"C:\Support\AMWAG-IT-1-screenshot.png", @"C:\Support\AMWAG-IT-1-logs.zip");

    [Fact]
    public void Header_offers_Contact_IT_only_when_IT_contacts_are_configured() => Sta.Run(() =>
    {
        using var without = new MainForm(TestRuntime.Create("en"));
        Assert.Null(without.ContactItButton);

        using var configured = new MainForm(TestRuntime.Create("en", config: TestRuntime.Config with { ItContacts = Contacts }));
        _ = configured.Handle;
        Assert.Equal("Contact IT", configured.ContactItButton!.Text);
        Assert.Equal("Contact the IT Department on WhatsApp", configured.ContactItButton.AccessibleName);

        configured.SwitchLanguage();
        Assert.Equal(Strings.Arabic.Text("header.contactIt"), configured.ContactItButton.Text);
    });

    [Fact]
    public void Window_picture_covers_the_whole_window_content() => Sta.Run(() =>
    {
        using var form = new MainForm(TestRuntime.Create("en", config: TestRuntime.Config with { ItContacts = Contacts }));
        _ = form.Handle;
        form.ShowResult(UiSamples.Failed());
        form.PerformLayout(); // a shown window lays out its docked content itself

        using var picture = form.CaptureWindow();

        Assert.Equal(form.ClientSize, picture.Size);
    });

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void Dialog_offers_each_contact_in_the_window_language_and_sends_to_the_chosen_one(string language) => Sta.Run(() =>
    {
        var t = language == "ar" ? Strings.Arabic : Strings.English;
        var sent = new List<ItContact>();
        using var dialog = new ContactItDialog(t, Contacts, (_, contact) => { sent.Add(contact); return Package; }, _ => { });

        string[] names = Contacts.Select(c => ContactItDialog.NameOf(c, t)).ToArray();
        Assert.Equal(names, WindowRun.All(dialog).OfType<ActionButton>().Select(b => b.Text).Where(names.Contains));
        Assert.Contains(t.Text("contact.text"), WindowRun.Labels(dialog));

        WindowRun.Click(dialog, names[1]);

        Assert.Equal([Contacts[1]], sent);
        Assert.Contains(t.Text("contact.sent.title"), WindowRun.Labels(dialog));
        Assert.Contains(t.Format("contact.sent.text", names[1]), WindowRun.Labels(dialog));
    });

    [Fact]
    public void Show_files_offers_the_prepared_files() => Sta.Run(() =>
    {
        var shown = new List<SupportPackage>();
        using var dialog = new ContactItDialog(Strings.English, Contacts, (_, _) => Package, shown.Add);

        WindowRun.Click(dialog, Contacts[0].Name);
        WindowRun.Click(dialog, Strings.English.Text("contact.showFiles"));

        Assert.Equal([Package], shown);
    });

    [Fact]
    public void Nothing_prepared_keeps_the_choice_open() => Sta.Run(() =>
    {
        using var dialog = new ContactItDialog(Strings.English, Contacts, (_, _) => null, _ => { });

        WindowRun.Click(dialog, Contacts[0].Name);

        Assert.DoesNotContain(Strings.English.Text("contact.sent.title"), WindowRun.Labels(dialog));
        Assert.Contains(Strings.English.Text("contact.text"), WindowRun.Labels(dialog));
    });
}
