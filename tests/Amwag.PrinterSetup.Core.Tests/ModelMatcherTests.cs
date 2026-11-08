using System.Globalization;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Detection;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.Core.Tests;

public sealed class ModelMatcherTests
{
    private readonly ModelMatcher _matcher = new(SampleConfig.Create().Models);

    // ---- Plug and Play IDs ---------------------------------------------------------------------------------------

    [Fact]
    public void Approved_hardware_id_is_an_exact_match_ignoring_case()
    {
        var match = _matcher.MatchDevice(Device([@"usbprint\utax_tap-4030dn1652"]));

        Assert.Equal(SampleConfig.P4030, match.Model?.Id);
        Assert.Equal(MatchConfidence.Exact, match.Confidence);
        Assert.Equal(@"usbprint\utax_tap-4030dn1652", match.MatchedId);
    }

    [Fact]
    public void Compatible_id_matches_when_no_hardware_id_does()
    {
        var match = _matcher.MatchDevice(Device([@"USBPRINT\UNKNOWN0000"], compatibleIds: [SampleConfig.UsbHardwareId(SampleConfig.P4531)]));

        Assert.Equal(SampleConfig.P4531, match.Model?.Id);
    }

    [Fact]
    public void Hardware_ids_rank_above_compatible_ids()
    {
        var match = _matcher.MatchDevice(Device(
            [SampleConfig.UsbHardwareId(SampleConfig.P4030)],
            compatibleIds: [SampleConfig.UsbHardwareId(SampleConfig.P5032)]));

        Assert.Equal(SampleConfig.P4030, match.Model?.Id);
        Assert.False(match.IsAmbiguous);
    }

    [Theory]
    [InlineData(@"LPTENUM\UTAX_TAP-4030DN1652")]
    [InlineData(@"WSDPRINT\UTAX_TAP-4030DN1652")]
    [InlineData(@"USBPRINT\utax_tap-4030dn1652")]
    public void Ieee1284_derived_id_is_exact_for_an_untruncated_name(string hardwareId)
    {
        var matcher = new ModelMatcher([OnlyIeee1284("UTAX_TA", "P-4030DN")]);

        var match = matcher.MatchDevice(Device([hardwareId]));

        Assert.Equal(MatchConfidence.Exact, match.Confidence);
        Assert.Equal(hardwareId, match.MatchedId);
    }

    [Fact]
    public void Ieee1284_derived_id_needs_the_windows_checksum()
    {
        var matcher = new ModelMatcher([OnlyIeee1284("UTAX_TA", "P-4030DN")]);

        Assert.Equal(MatchConfidence.None, matcher.MatchDevice(Device([@"USBPRINT\UTAX_TAP-4030DNABCD"])).Confidence);
        // 03EB is the checksum of "UTAX TAP-4030DN" (with a space), not of "UTAX_TAP-4030DN".
        Assert.Equal(MatchConfidence.None, matcher.MatchDevice(Device([@"USBPRINT\UTAX_TAP-4030DN03EB"])).Confidence);
    }

    [Fact]
    public void Ieee1284_derived_id_replaces_spaces_but_checksums_the_raw_name()
    {
        var matcher = new ModelMatcher([OnlyIeee1284("UTAX TA", "P-4030DN")]);

        Assert.Equal(MatchConfidence.Exact, matcher.MatchDevice(Device([@"USBPRINT\UTAX_TAP-4030DN03EB"])).Confidence);
        Assert.Equal(MatchConfidence.None, matcher.MatchDevice(Device([@"USBPRINT\UTAX TAP-4030DN03EB"])).Confidence);
        Assert.Equal(MatchConfidence.None, matcher.MatchDevice(Device([@"USBPRINT\UTAX_TAP-4030DN1652"])).Confidence);
    }

    [Theory]
    [InlineData("UTAX_TA", "P-4030DN", 0x1652)]
    [InlineData("UTAX_TA", "P-4531DN", 0xD6CF)]
    [InlineData("TA_UTAX", "P-5032DN", 0xEED0)]
    [InlineData("TA UTAX", "P-5032DN", 0x11DA)]
    [InlineData("UTAX TA", "P-4030DN", 0x03EB)]
    [InlineData("MS3D", "Compat", 0xE2D2)]
    [InlineData("Hewlett-Packard", "HP LaserJet 4P", 0x7EE2)]
    public void Checksum_reproduces_the_documented_test_vectors(string manufacturer, string model, int expected)
    {
        Assert.Equal((ushort)expected, Ieee1284.Checksum(manufacturer + model));
    }

    [Fact]
    public void Checksum_is_not_standard_crc16_arc()
    {
        // Standard CRC-16/ARC of "UTAX_TAP-5032DN" is 0x07F2; the Windows table (last high entry 0x4600) gives 0xC673.
        Assert.Equal((ushort)0xC673, Ieee1284.Checksum("UTAX_TAP-5032DN"));
    }

    [Fact]
    public void Ids_of_the_ta_infs_reproduce_from_mfg_and_mdl()
    {
        foreach (string modelId in SampleConfig.ModelIds)
        {
            string manufacturer = SampleConfig.Ieee1284Manufacturer(modelId);
            string expected = Ieee1284.HardwareIdName(manufacturer, modelId) + Ieee1284.Checksum(manufacturer + modelId).ToString("X4", CultureInfo.InvariantCulture);
            Assert.Equal(SampleConfig.HardwareIdName(modelId), expected);
        }
    }

    [Theory]
    [InlineData(@"USBPRINT\UTAX_TAP-4030DN165")]
    [InlineData(@"USBPRINT\UTAX_TAP-4030DN16520")]
    [InlineData(@"USBPRINT\UTAX_TAP-4030DN16G2")]
    [InlineData(@"USBPRINT\UTAX_TAP-4030DNX1652")]
    [InlineData(@"UTAX_TAP-4030DN1652")]
    [InlineData(@"USBPRINT\UTAX_TAP-4031DN1652")]
    public void Ieee1284_rule_needs_the_name_plus_exactly_four_hex_digits(string hardwareId)
    {
        var matcher = new ModelMatcher([OnlyIeee1284("UTAX_TA", "P-4030DN")]);

        Assert.Equal(MatchConfidence.None, matcher.MatchDevice(Device([hardwareId])).Confidence);
    }

    [Fact]
    public void Ieee1284_name_of_exactly_20_characters_still_matches()
    {
        var matcher = new ModelMatcher([OnlyIeee1284("ABCDEFGHIJKL", "P-4030DN")]);

        Assert.Equal(20, Ieee1284.HardwareIdName("ABCDEFGHIJKL", "P-4030DN").Length);
        Assert.Equal(MatchConfidence.Exact, matcher.MatchDevice(Device([@"USBPRINT\ABCDEFGHIJKLP-4030DN2AA4"])).Confidence);
    }

    [Fact]
    public void Truncated_ieee1284_name_never_matches_exactly()
    {
        // "TA_Triumph-AdlerP-4030DN" has 24 characters; Windows keeps the first 20 (C168 is even the right checksum).
        var matcher = new ModelMatcher([OnlyIeee1284("TA Triumph-Adler", "P-4030DN")]);

        var match = matcher.MatchDevice(Device([@"USBPRINT\TA_Triumph-AdlerP-40C168"]));

        Assert.Equal(MatchConfidence.None, match.Confidence);
        Assert.Null(match.Model);
    }

    [Fact]
    public void Same_level_matches_of_two_models_choose_none()
    {
        var match = _matcher.MatchDevice(Device([SampleConfig.UsbHardwareId(SampleConfig.P4030), SampleConfig.UsbHardwareId(SampleConfig.P5032)]));

        Assert.Null(match.Model);
        Assert.True(match.IsAmbiguous);
        Assert.Equal(2, match.AmbiguousModels.Count);
    }

    // ---- Network identity ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("MFG:UTAX_TA;MDL:P-4030DN;CMD:PCLXL;")]
    [InlineData("manufacturer: UTAX_TA ; model: P-4030DN ;")]
    [InlineData("MFG:UTAX TA;MDL:P-4030DN;")]
    [InlineData("\u0000\u001AMFG:UTAX_TA;MDL:P-4030DN;")]
    public void Ieee1284_device_id_over_snmp_is_exact(string deviceId)
    {
        var match = _matcher.MatchNetwork(Identity(deviceId: deviceId));

        Assert.Equal(SampleConfig.P4030, match.Model?.Id);
        Assert.Equal(MatchConfidence.Exact, match.Confidence);
    }

    [Fact]
    public void Approved_network_model_name_is_exact_after_whitespace_normalization()
    {
        // hrDeviceDescr proves the model on a device under the Kyocera enterprise OID, which every TA/UTAX device reports.
        var match = _matcher.MatchNetwork(Identity(description: "  ta   triumph-adler   P-4531DN ") with { SysObjectId = "1.3.6.1.4.1.1347.41" });

        Assert.Equal(SampleConfig.P4531, match.Model?.Id);
        Assert.Equal(MatchConfidence.Exact, match.Confidence);
    }

    [Fact]
    public void Bare_model_name_from_the_kyocera_private_mib_is_exact()
    {
        var matcher = new ModelMatcher([SampleConfig.Model(SampleConfig.P4030) with { NetworkModelNames = ["P-4030DN"] }]);

        var match = matcher.MatchNetwork(Identity() with { VendorModelName = "P-4030DN", SysObjectId = "1.3.6.1.4.1.1347.41" });

        Assert.Equal(SampleConfig.P4030, match.Model?.Id);
        Assert.Equal(MatchConfidence.Exact, match.Confidence);
    }

    [Fact]
    public void Bare_model_name_without_any_vendor_evidence_is_only_probable()
    {
        var matcher = new ModelMatcher([SampleConfig.Model(SampleConfig.P4030) with { NetworkModelNames = ["P-4030DN"] }]);

        var match = matcher.MatchNetwork(Identity(description: "P-4030DN") with { SysObjectId = "1.3.6.1.4.1.9999.1" });

        Assert.Equal(SampleConfig.P4030, match.Model?.Id);
        Assert.Equal(MatchConfidence.Probable, match.Confidence);
    }

    [Theory]
    [InlineData("TA P-4030DN")]
    [InlineData("Kyocera P-4030DN printer")]
    [InlineData("UTAX_TA P-4030DN (KX)")]
    [InlineData("Triumph-Adler/P-4030DN")]
    public void Model_token_with_a_manufacturer_token_is_probable(string sysDescr)
    {
        var match = _matcher.MatchNetwork(Identity(sysDescr: sysDescr));

        Assert.Equal(SampleConfig.P4030, match.Model?.Id);
        Assert.Equal(MatchConfidence.Probable, match.Confidence);
    }

    [Theory]
    [InlineData("TA P-4030DNX")]
    [InlineData("TA P-4030D")]
    [InlineData("TA-P-4030DN")]
    [InlineData("P-4030DN")]
    [InlineData("Triumph-Adler P-4030")]
    [InlineData("TAP-4030DN")]
    public void Partial_names_never_identify_a_model(string sysDescr)
    {
        var match = _matcher.MatchNetwork(Identity(sysDescr: sysDescr));

        Assert.Null(match.Model);
        Assert.Equal(MatchConfidence.None, match.Confidence);
    }

    [Fact]
    public void Two_models_named_at_the_probable_level_choose_none()
    {
        var match = _matcher.MatchNetwork(Identity(sysDescr: "TA P-4030DN / P-5032DN"));

        Assert.Null(match.Model);
        Assert.True(match.IsAmbiguous);
    }

    [Fact]
    public void Two_models_with_the_same_network_name_choose_none()
    {
        var models = SampleConfig.Create().Models.Select(m => m with { NetworkModelNames = ["TA Printer"] }).ToList();

        var match = new ModelMatcher(models).MatchNetwork(Identity(description: "TA Printer"));

        Assert.Null(match.Model);
        Assert.Equal(3, match.AmbiguousModels.Count);
    }

    [Fact]
    public void Device_id_match_is_stronger_than_a_name_naming_another_model()
    {
        var match = _matcher.MatchNetwork(Identity(deviceId: SampleConfig.DeviceId1284(SampleConfig.P5032), sysDescr: "TA P-4030DN"));

        Assert.Equal(SampleConfig.P5032, match.Model?.Id);
        Assert.Equal(MatchConfidence.Exact, match.Confidence);
    }

    [Fact]
    public void Unexpected_manufacturer_with_a_supported_mdl_is_only_probable()
    {
        var match = _matcher.MatchNetwork(Identity(deviceId: "MFG:Kyocera;MDL:P-4030DN;"));

        Assert.Equal(SampleConfig.P4030, match.Model?.Id);
        Assert.Equal(MatchConfidence.Probable, match.Confidence);
    }

    [Fact]
    public void Mdl_naming_no_supported_model_is_reported()
    {
        Assert.True(_matcher.ReportsUnsupportedModel(Identity(deviceId: "ID:ECOSYS P2040dw;MFG:Kyocera;MDL:ECOSYS P2040dw;"), out string reported));
        Assert.Equal("Kyocera ECOSYS P2040dw", reported);
        Assert.False(_matcher.ReportsUnsupportedModel(Identity(deviceId: "MFG:UTAX_TA;MDL:P-4030DN;"), out _));
        Assert.False(_matcher.ReportsUnsupportedModel(Identity(deviceId: "MFG:Kyocera;MDL:p-5032dn;"), out _));
        Assert.False(_matcher.ReportsUnsupportedModel(Identity(sysDescr: "KYOCERA Document Solutions Printing System"), out _));
    }

    [Fact]
    public void Suggested_models_include_a_bare_model_token_and_the_1284_model()
    {
        var suggested = _matcher.ModelsSuggestedBy(Identity(deviceId: "MFG:Kyocera;MDL:P-5032DN;", sysDescr: "Printer P-4531DN"));

        Assert.Equal(new[] { SampleConfig.P5032, SampleConfig.P4531 }, suggested.Select(m => m.Id));
    }

    // ---- Model evidence without an IEEE 1284 ID (shipped-style bare network model names) -----------------------------

    [Fact]
    public void Kyocera_model_name_of_an_unsupported_model_is_reported_although_the_printer_name_names_a_supported_one()
    {
        // A P-4532DN that replaced a P-4030DN and kept its administrator-assigned name (prtGeneralPrinterName).
        var identity = KyoceraIdentity("P-4532DN") with { PrinterName = "P-4030DN" };

        var match = BareNames.MatchNetwork(identity);

        Assert.Null(match.Model);
        Assert.Equal(MatchConfidence.None, match.Confidence);
        Assert.True(BareNames.ReportsUnsupportedModel(identity, out string reported));
        Assert.Equal("P-4532DN", reported);
        Assert.Empty(BareNames.ModelsSuggestedBy(identity));
    }

    [Fact]
    public void Printer_name_of_another_model_neither_identifies_nor_makes_a_device_ambiguous()
    {
        var identity = KyoceraIdentity("P-4531DN") with { PrinterName = "P-4030DN" };

        var match = BareNames.MatchNetwork(identity);

        Assert.Equal((SampleConfig.P4531, MatchConfidence.Exact), (match.Model?.Id, match.Confidence));
        Assert.False(match.IsAmbiguous);
        Assert.Equal(new[] { SampleConfig.P4531 }, BareNames.ModelsSuggestedBy(identity).Select(m => m.Id));
    }

    [Fact]
    public void Printer_name_alone_says_nothing_about_the_device()
    {
        var identity = new NetworkIdentity { Address = "10.0.0.5", RawPortReachable = true, SnmpResponded = true, PrinterName = "TA Triumph-Adler P-4030DN" };

        Assert.Equal(MatchConfidence.None, _matcher.MatchNetwork(identity).Confidence);
        Assert.False(ModelMatcher.IsInformative(identity));
        Assert.False(ModelMatcher.IsVendorIdentity(identity));
        Assert.False(_matcher.ReportsUnsupportedModel(identity, out _));
    }

    [Theory]
    [InlineData("Kyocera model name", "1.3.6.1.4.1.1347.41", MatchConfidence.Exact)]
    [InlineData("Kyocera model name", null, MatchConfidence.Exact)] // an answer from the Kyocera private MIB is enough
    [InlineData("device description", "1.3.6.1.4.1.1347.41", MatchConfidence.Exact)]
    [InlineData("device description", null, MatchConfidence.Probable)]
    [InlineData("device description", "1.3.6.1.4.1.11.2.3.9.1", MatchConfidence.Probable)]
    [InlineData("sysDescr", "1.3.6.1.4.1.1347.41", MatchConfidence.Probable)] // free text, a brand string on TA devices
    public void Bare_model_name_is_exact_only_in_a_model_name_field(string field, string? sysObjectId, MatchConfidence expected)
    {
        var identity = new NetworkIdentity
        {
            Address = "10.0.0.5",
            RawPortReachable = true,
            SnmpResponded = true,
            SysObjectId = sysObjectId,
            VendorModelName = field == "Kyocera model name" ? "P-4030DN" : null,
            DeviceDescription = field == "device description" ? "P-4030DN" : null,
            SysDescr = field == "sysDescr" ? "P-4030DN" : "KYOCERA Document Solutions Printing System",
        };

        var match = BareNames.MatchNetwork(identity);

        Assert.Equal((SampleConfig.P4030, expected), (match.Model?.Id, match.Confidence));
    }

    [Theory]
    [InlineData("ECOSYS M2540dn", null, "ECOSYS M2540dn")]
    [InlineData(null, "P-4532DN", "P-4532DN")]
    [InlineData("  P-4532DN  ", "P-4030DN", "P-4532DN")] // kcprtGeneralModelName ranks above hrDeviceDescr
    public void Kyocera_family_model_name_of_an_unsupported_model_is_reported(string? vendorModelName, string? deviceDescription, string expected)
    {
        var identity = KyoceraIdentity(null) with { VendorModelName = vendorModelName, DeviceDescription = deviceDescription };

        Assert.True(BareNames.ReportsUnsupportedModel(identity, out string reported));
        Assert.Equal(expected, reported);
    }

    [Theory]
    [InlineData("P-4531DN", null)] // a supported model
    [InlineData("TA P-4531DN", null)] // names a supported model Id as a token
    [InlineData(null, "TA_UTAX Printing System")] // a brand string: no model designation
    [InlineData(null, null)] // brand-only identity (sysDescr only)
    public void Kyocera_family_identity_naming_no_other_model_is_not_reported(string? vendorModelName, string? deviceDescription)
    {
        var identity = KyoceraIdentity(null) with { VendorModelName = vendorModelName, DeviceDescription = deviceDescription };

        Assert.False(BareNames.ReportsUnsupportedModel(identity, out _));
    }

    [Fact]
    public void Description_of_another_vendors_device_is_not_reported_as_an_unsupported_kyocera_model()
    {
        // Not a TA/UTAX/Kyocera device, so its description is not a Kyocera model name; detection rejects it as another vendor's.
        var identity = Identity(description: "HP LaserJet M404dn", sysDescr: "HP ETHERNET MULTI-ENVIRONMENT") with { SysObjectId = "1.3.6.1.4.1.11.2.3.9.1" };

        Assert.False(BareNames.ReportsUnsupportedModel(identity, out _));
        Assert.True(ModelMatcher.IsInformative(identity));
        Assert.False(ModelMatcher.IsKyoceraFamily(identity));
    }

    // ---- Drivers and vendors -------------------------------------------------------------------------------------

    [Fact]
    public void Model_specific_driver_indicates_its_model()
    {
        Assert.Equal(SampleConfig.P4531, _matcher.ModelForDriver("ta p-4531dn kx")?.Id);
        Assert.Equal(SampleConfig.P4531, _matcher.ModelForDriver(SampleConfig.XpsDriverName(SampleConfig.P4531))?.Id);
    }

    [Fact]
    public void Universal_driver_accepted_by_several_models_indicates_none()
    {
        var models = SampleConfig.Create().Models
            .Select(m => m with { AcceptedDrivers = [.. m.AcceptedDrivers, new AcceptedDriver { Name = "TA Universal KX" }] })
            .ToList();
        var matcher = new ModelMatcher(models);

        Assert.Null(matcher.ModelForDriver("TA Universal KX"));
        Assert.Equal(3, matcher.ModelsAcceptingDriver("TA Universal KX").Count);
        Assert.Null(matcher.ModelForDriver("HP Universal Printing PCL 6"));
    }

    [Fact]
    public void Tokenizer_keeps_hyphens_and_splits_at_other_punctuation()
    {
        Assert.Equal(new[] { "UTAX", "TA", "P-4030DN", "KX" }, ModelMatcher.Tokenize("UTAX_TA P-4030DN (KX)"));
        Assert.Equal(new[] { "Triumph-Adler", "P-4030DN" }, ModelMatcher.Tokenize("Triumph-Adler;P-4030DN"));
    }

    [Theory]
    [InlineData("TA Triumph-Adler", true)]
    [InlineData("KYOCERA Document Solutions", true)]
    [InlineData("UTAX_TA", true)]
    [InlineData("TAKEDA Printers", false)]
    [InlineData("HP", false)]
    public void Manufacturer_tokens_are_whole_tokens(string text, bool expected)
    {
        Assert.Equal(expected, ModelMatcher.HasManufacturerToken(text));
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------

    /// <summary>The sample models with the shipped configuration's bare network model names ("P-4030DN" ...).</summary>
    private static readonly ModelMatcher BareNames = new(SampleConfig.Create().Models.Select(m => m with { NetworkModelNames = [m.Id] }).ToList());

    /// <summary>A TA device without a 1284 ID that reports <paramref name="model"/> as kcprtGeneralModelName and hrDeviceDescr.</summary>
    private static NetworkIdentity KyoceraIdentity(string? model) => new()
    {
        Address = "10.1.2.50",
        RawPortReachable = true,
        SnmpResponded = true,
        SysDescr = "TA_UTAX Printing System",
        SysObjectId = "1.3.6.1.4.1.1347.41",
        DeviceDescription = model,
        VendorModelName = model,
    };

    private static PnpPrinterDevice Device(IReadOnlyList<string> hardwareIds, IReadOnlyList<string>? compatibleIds = null) => new()
    {
        InstanceId = @"USBPRINT\TEST\1",
        Bus = PnpBus.Usb,
        HardwareIds = hardwareIds,
        CompatibleIds = compatibleIds ?? [],
        IsPresent = true,
    };

    private static NetworkIdentity Identity(string? deviceId = null, string? description = null, string? sysDescr = null) => new()
    {
        Address = "10.0.0.5",
        RawPortReachable = true,
        SnmpResponded = true,
        Ieee1284DeviceId = deviceId,
        DeviceDescription = description,
        SysDescr = sysDescr,
    };

    private static ModelDefinition OnlyIeee1284(string manufacturer, string model) => SampleConfig.Model(SampleConfig.P4030) with
    {
        HardwareIds = [],
        Ieee1284Ids = [new Ieee1284Identity { Manufacturer = manufacturer, Model = model }],
    };
}
