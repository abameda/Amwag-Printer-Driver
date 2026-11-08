using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Units;
using Amwag.PrinterSetup.Core.Printing;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.Core.Tests;

public sealed class DevModeTests
{
    [Fact]
    public void Fake_devmode_has_the_realistic_layout()
    {
        var devMode = FakeDevModes.Create("TA P-4030DN KX", DmPaper.A4, "A4", privateSize: 512);

        Assert.Equal(DevMode.CurrentPublicSize, devMode.Size);
        Assert.Equal(FakeDevModes.SpecVersion, devMode.SpecVersion);
        Assert.Equal(512, devMode.DriverExtra);
        Assert.Equal(220 + 512, devMode.TotalSize);
        Assert.Equal("TA P-4030DN KX", devMode.DeviceName);
        Assert.Equal((uint)1, devMode.Nup);
        Assert.Equal((uint)1, devMode.MediaType);
    }

    [Fact]
    public void Round_trip_through_bytes_preserves_every_byte()
    {
        var original = FakeDevModes.Create("Queue", DmPaper.Letter, "Letter", DmOrientation.Landscape, DmDuplex.Vertical, 90, DmBin.Manual);

        var copy = DevMode.FromBytes(original.ToArray());

        Assert.Equal(original.ToArray(), copy.ToArray());
        Assert.Equal(DmOrientation.Landscape, copy.Orientation);
        Assert.Equal(DmPaper.Letter, copy.PaperSize);
        Assert.Equal("Letter", copy.FormName);
        Assert.Equal(DmDuplex.Vertical, copy.Duplex);
        Assert.Equal(90, copy.Scale);
        Assert.Equal(DmBin.Manual, copy.DefaultSource);
    }

    [Fact]
    public void Editing_public_fields_keeps_driver_private_bytes()
    {
        var devMode = FakeDevModes.Create("Queue", DmPaper.A4, "A4", privateSize: 300);
        byte[] privateBefore = devMode.AsSpan()[DevMode.CurrentPublicSize..].ToArray();

        devMode.FormName = "AMWAG Ticket";
        devMode.PaperSize = 257;
        devMode.Orientation = DmOrientation.Portrait;
        devMode.Scale = 100;
        devMode.Duplex = DmDuplex.Simplex;
        devMode.DefaultSource = DmBin.Manual;
        devMode.Fields |= DevModeFields.FormName;

        Assert.Equal(privateBefore, devMode.AsSpan()[DevMode.CurrentPublicSize..].ToArray());
        Assert.Equal("AMWAG Ticket", devMode.FormName);
    }

    [Fact]
    public void Desired_defaults_are_applied_to_a_copy_without_touching_private_bytes()
    {
        var original = FakeDevModes.Create("Queue", DmPaper.A4, "A4", DmOrientation.Landscape, DmDuplex.Vertical, 80, DmBin.Auto);
        original.Fields |= DevModeFields.PaperLength | DevModeFields.PaperWidth;
        var desired = new DesiredDefaults("AMWAG Ticket", 257, 823, 1702, DmOrientation.Portrait, DmDuplex.Simplex, 100, DmBin.Manual);

        var edited = desired.ApplyTo(original);

        Assert.Empty(desired.Differences(edited));
        Assert.False(edited.Has(DevModeFields.PaperLength));
        Assert.False(edited.Has(DevModeFields.PaperWidth));
        Assert.True(edited.Has(DevModeFields.FormName | DevModeFields.PaperSize | DevModeFields.Orientation | DevModeFields.Scale | DevModeFields.Duplex | DevModeFields.DefaultSource));
        Assert.Equal(original.AsSpan()[DevMode.CurrentPublicSize..].ToArray(), edited.AsSpan()[DevMode.CurrentPublicSize..].ToArray());
        Assert.Equal("A4", original.FormName);
    }

    [Fact]
    public void Unmanaged_duplex_and_source_are_left_alone()
    {
        var original = FakeDevModes.Create("Queue", DmPaper.A4, "A4", duplex: DmDuplex.Vertical, defaultSource: DmBin.Auto);
        var desired = new DesiredDefaults("AMWAG Ticket", 257, 823, 1702, DmOrientation.Portrait, null, 100, null);

        var edited = desired.ApplyTo(original);

        Assert.Equal(DmDuplex.Vertical, edited.Duplex);
        Assert.Equal(DmBin.Auto, edited.DefaultSource);
        Assert.Empty(desired.Differences(edited));
    }

    [Theory]
    [InlineData(2100, 2970, true)]  // A4 overrides the ticket form
    [InlineData(823, 1702, false)]  // the ticket paper's own size
    [InlineData(833, 1692, false)]  // within 1 mm
    [InlineData(834, 1702, true)]   // 1.1 mm wider
    [InlineData(0, 0, false)]       // flags without a size select nothing
    public void Size_fields_are_a_paper_difference_only_when_they_give_another_size(int width, int length, bool differs)
    {
        var desired = new DesiredDefaults("AMWAG Ticket", 257, 823, 1702, DmOrientation.Portrait, null, 100, null);
        var devMode = desired.ApplyTo(FakeDevModes.Create("Queue", DmPaper.A4, "A4"));
        devMode.Fields |= DevModeFields.PaperWidth | DevModeFields.PaperLength;
        devMode.PaperWidth = (short)width;
        devMode.PaperLength = (short)length;

        var differences = desired.Differences(devMode);
        Assert.Equal(differs, differences.Contains("paper"));
        Assert.Equal(differs ? 1 : 0, differences.Count);
        Assert.Equal(!differs, desired.PaperMatches(devMode));
    }

    [Theory]
    [InlineData("AMWAG Ticket", (short)257, true)]
    [InlineData("", (short)257, true)]   // the KX v3 driver leaves dmFormName empty
    [InlineData("A4", (short)257, false)]
    [InlineData("", DmPaper.A4, false)]
    public void Paper_is_selected_by_its_id_with_its_name_or_an_empty_name(string formName, short paperSize, bool selects)
    {
        var desired = new DesiredDefaults("AMWAG Ticket", 257, 823, 1702, DmOrientation.Portrait, null, 100, null);
        var devMode = desired.ApplyTo(FakeDevModes.Create("Queue", DmPaper.A4, "A4"));
        devMode.FormName = formName;
        devMode.PaperSize = paperSize;

        Assert.Equal(selects, desired.PaperMatches(devMode));
    }

    [Fact]
    public void Ticket_paper_is_found_by_name_else_by_size_but_never_as_dmpaper_user()
    {
        var size = new PaperDimensions(82_296, 170_180);
        var byName = new DriverCapabilities
        {
            QueueName = "Queue",
            Papers = [new(DmPaper.User, "Custom", 823, 1702), new(541, "Ticket", 824, 1701), new(257, "AMWAG Ticket", 823, 1702)],
        };
        Assert.Equal(257, byName.FindTicketPaper("AMWAG Ticket", size)!.Id);
        Assert.Equal(257, byName.FindTicketPaper("Other", size)!.Id); // the closest size
        var bySize = byName with { Papers = [new(DmPaper.User, "Custom", 823, 1702), new(541, "Ticket", 824, 1701)] };
        Assert.Equal(541, bySize.FindTicketPaper("AMWAG Ticket", size)!.Id);

        var none = byName with { Papers = [new(DmPaper.User, "Custom", 823, 1702), new(542, "Wide", 835, 1702)] };
        Assert.Null(none.FindTicketPaper("Other", size));
    }

    [Fact]
    public void One_size_field_of_another_size_is_a_paper_difference_and_values_without_flags_are_not()
    {
        var desired = new DesiredDefaults("AMWAG Ticket", 257, 823, 1702, DmOrientation.Portrait, null, 100, null);
        var devMode = desired.ApplyTo(FakeDevModes.Create("Queue", DmPaper.A4, "A4"));
        // Drivers fill in the values of the paper they resolved and clear the flags: that selects nothing.
        devMode.PaperWidth = 2100;
        devMode.PaperLength = 2970;
        Assert.Empty(desired.Differences(devMode));

        devMode.Fields |= DevModeFields.PaperLength;

        Assert.Equal(["paper"], desired.Differences(devMode));
    }

    [Theory]
    [InlineData(DmBin.Manual, false)] // the multipurpose tray itself
    [InlineData(DmBin.Auto, false)]   // what the driver keeps instead of a tray that is only preferred
    [InlineData((short)257, true)]    // any other source
    public void Source_the_driver_keeps_instead_of_a_preferred_tray_is_no_difference(short source, bool differs)
    {
        var desired = new DesiredDefaults("AMWAG Ticket", 257, 823, 1702, DmOrientation.Portrait, null, 100, DmBin.Manual, SourceRequired: false) { SourceSubstitute = DmBin.Auto };
        var devMode = desired.ApplyTo(FakeDevModes.Create("Queue", DmPaper.A4, "A4"));
        devMode.DefaultSource = source;

        Assert.Equal(differs ? new[] { "paperSource" } : [], desired.Differences(devMode));
        Assert.Equal(!differs, desired.SourceMatches(devMode));
    }

    [Fact]
    public void Driver_source_is_accepted_only_for_a_tray_that_is_only_preferred()
    {
        var spooler = new FakePrintSpooler();
        spooler.SeedForm(FormInfo.UserForm(SampleConfig.FormName, SampleConfig.Ticket));
        spooler.SeedQueue(new PrintQueueInfo { Name = "Queue", DriverName = "TA P-4030DN KX", PortName = "USB001" });
        // The driver turns the multipurpose tray into Auto for papers as narrow as the ticket.
        spooler.SetProfile("TA P-4030DN KX", FakeDriverProfile.Default with { CassetteMinimumWidthTenthsOfMillimeter = 1050, SourceBelowCassetteMinimum = DmBin.Auto });
        var preferred = new DesiredDefaults("AMWAG Ticket", 257, 823, 1702, DmOrientation.Portrait, null, 100, DmBin.Manual, SourceRequired: false);
        var current = preferred.ApplyTo(FakeDevModes.Create("Queue", DmPaper.A4, "A4"));
        current.DefaultSource = DmBin.Auto;

        var accepted = TicketDefaults.AcceptDriverSource(preferred, spooler, "Queue", current);
        var required = TicketDefaults.AcceptDriverSource(preferred with { SourceRequired = true }, spooler, "Queue", current);

        Assert.Equal(DmBin.Auto, accepted.SourceSubstitute);
        Assert.Empty(accepted.Differences(current));
        Assert.Null(required.SourceSubstitute);
        Assert.Equal(["paperSource"], required.Differences(current));
        Assert.Empty(spooler.Writes);
    }

    [Fact]
    public void Read_back_that_keeps_an_overriding_size_is_a_difference()
    {
        var desired = new DesiredDefaults("AMWAG Ticket", 257, 823, 1702, DmOrientation.Portrait, DmDuplex.Simplex, 100, DmBin.Manual);
        var validated = desired.ApplyTo(FakeDevModes.Create("Queue", DmPaper.A4, "A4"));
        var expected = desired.ValuesOf(validated);
        var readBack = validated.Clone();
        readBack.Fields |= DevModeFields.PaperWidth | DevModeFields.PaperLength;
        readBack.PaperWidth = 2100;
        readBack.PaperLength = 2970;

        Assert.Empty(expected.Differences(validated));
        Assert.Equal(["paper"], expected.Differences(readBack));
    }

    [Fact]
    public void Paper_description_shows_the_size_only_when_a_size_flag_is_set()
    {
        var devMode = FakeDevModes.Create("Queue", 257, "AMWAG Ticket");
        devMode.PaperWidth = 2100;
        devMode.PaperLength = 2970;
        Assert.Equal("'AMWAG Ticket' (paper 257)", TicketDefaults.DescribePaper(devMode));

        devMode.Fields |= DevModeFields.PaperWidth;

        Assert.Equal("'AMWAG Ticket' (paper 257), size 2100 x 2970 tenths of a mm", TicketDefaults.DescribePaper(devMode));
    }

    [Fact]
    public void Form_name_of_31_characters_fits_and_32_is_rejected()
    {
        var devMode = FakeDevModes.Create("Queue", DmPaper.A4, "A4");

        devMode.FormName = new string('x', DevMode.MaxNameLength);
        Assert.Equal(31, devMode.FormName.Length);
        Assert.Throws<ArgumentException>(() => devMode.FormName = new string('x', DevMode.MaxNameLength + 1));
    }

    [Fact]
    public void Short_or_inconsistent_buffers_are_rejected()
    {
        var bytes = FakeDevModes.Create("Queue", DmPaper.A4, "A4", privateSize: 64).ToArray();

        Assert.Throws<ArgumentException>(() => DevMode.FromBytes(bytes.AsSpan(0, DevMode.MinimumPublicSize - 1)));
        Assert.Throws<ArgumentException>(() => DevMode.FromBytes(bytes.AsSpan(0, bytes.Length - 1)));
    }

    [Fact]
    public void Clone_is_independent()
    {
        var original = FakeDevModes.Create("Queue", DmPaper.A4, "A4");
        var clone = original.Clone();

        clone.PaperSize = DmPaper.Letter;

        Assert.Equal(DmPaper.A4, original.PaperSize);
    }
}
