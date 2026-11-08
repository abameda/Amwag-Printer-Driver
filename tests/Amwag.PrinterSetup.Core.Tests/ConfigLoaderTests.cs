using System.Text;
using System.Text.Json;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Units;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.Core.Tests;

public sealed class ConfigLoaderTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // ---- Validation rules ----------------------------------------------------------------------------------------

    [Fact]
    public void Sample_configuration_is_valid()
    {
        Assert.Empty(ConfigLoader.Validate(SampleConfig.Create()));
    }

    public static TheoryData<string, Func<DeploymentConfig, DeploymentConfig>, string> InvalidConfigurations => new()
    {
        { "schema version", c => c with { SchemaVersion = 2 }, "schemaVersion must be 1" },
        { "empty form name", c => c with { Ticket = c.Ticket with { FormName = " " } }, "ticket.formName must not be empty" },
        { "long form name", c => c with { Ticket = c.Ticket with { FormName = new string('F', 32) } }, "at most 31 fit" },
        { "control character", c => c with { Ticket = c.Ticket with { FormName = "AMWAG\tTicket" } }, "control characters" },
        { "zero width", c => c with { Ticket = c.Ticket with { Width = 0 } }, "ticket.width must be greater than zero" },
        { "too small", c => c with { Ticket = c.Ticket with { Height = 0.3m } }, "between 10 and 1000 mm" },
        { "too large", c => c with { Ticket = c.Ticket with { Width = 1001, Unit = LengthUnit.Millimeter } }, "between 10 and 1000 mm" },
        { "confirmed without confirmer", c => c with { Ticket = c.Ticket with { ConfirmedBy = null } }, "ticket.confirmedBy is required" },
        { "confirmed with bad date", c => c with { Ticket = c.Ticket with { ConfirmedOn = "30.10.2026" } }, "yyyy-MM-dd" },
        { "scale 0", c => c with { PrintPreferences = c.PrintPreferences with { ScalePercent = 0 } }, "scalePercent must be between 1 and 400" },
        { "scale 401", c => c with { PrintPreferences = c.PrintPreferences with { ScalePercent = 401 } }, "scalePercent must be between 1 and 400" },
        { "raw port 0", c => c with { Connection = c.Connection with { RawPortNumber = 0 } }, "rawPortNumber must be between 1 and 65535" },
        { "raw port 65536", c => c with { Connection = c.Connection with { RawPortNumber = 65536 } }, "rawPortNumber must be between 1 and 65535" },
        { "empty queue name", c => c with { Connection = c.Connection with { NewQueueName = "" } }, "connection.newQueueName must not be empty" },
        { "backslash queue name", c => c with { Connection = c.Connection with { NewQueueName = @"AMWAG\Ticket" } }, "must not contain" },
        { "comma queue name", c => c with { Connection = c.Connection with { NewQueueName = "AMWAG, Ticket" } }, "must not contain" },
        { "long queue name", c => c with { Connection = c.Connection with { NewQueueName = new string('Q', 221) } }, "at most 220 characters" },
        { "network timeout", c => c with { Connection = c.Connection with { NetworkTimeoutMilliseconds = 0 } }, "networkTimeoutMilliseconds" },
        { "usb wait", c => c with { Connection = c.Connection with { UsbQueueWaitSeconds = 0 } }, "usbQueueWaitSeconds" },
        { "download timeout", c => c with { DriverSource = c.DriverSource with { DownloadTimeoutSeconds = 0 } }, "downloadTimeoutSeconds" },
        { "no models", c => c with { Models = [] }, "models must contain at least one" },
        { "no packages", c => c with { DriverPackages = [] }, "driverPackages must contain at least one" },
        { "empty model id", c => EditModel(c, m => m with { Id = "" }), "id must not be empty" },
        { "duplicate model id", c => c with { Models = [.. c.Models, c.Models[0] with { HardwareIds = [@"USBPRINT\OTHER1234"] }] }, "id is not unique" },
        { "duplicate model id case", c => c with { Models = [.. c.Models, c.Models[0] with { Id = "p-4030dn", HardwareIds = [@"USBPRINT\OTHER1234"] }] }, "id is not unique" },
        { "empty display name", c => EditModel(c, m => m with { DisplayName = "" }), "displayName must not be empty" },
        { "no identity", c => EditModel(c, m => m with { HardwareIds = [], Ieee1284Ids = [] }), "at least one hardwareId or ieee1284Id" },
        { "no accepted drivers", c => EditModel(c, m => m with { AcceptedDrivers = [] }), "acceptedDrivers must not be empty" },
        { "preferred not accepted", c => EditModel(c, m => m with { PreferredDriverName = "Other Driver" }), "must be one of acceptedDrivers" },
        { "bad min version", c => EditModel(c, m => m with { AcceptedDrivers = [new AcceptedDriver { Name = m.PreferredDriverName, MinVersion = "eight" }] }), "is not a version" },
        { "unknown package", c => EditModel(c, m => m with { DriverPackageId = "missing" }), "does not name a driver package" },
        { "hardware id in two models", c => EditModel(c, m => m with { HardwareIds = [.. m.HardwareIds, SampleConfig.UsbHardwareId(SampleConfig.P5032)] }), "appears in models" },
        { "duplicate package", c => c with { DriverPackages = [.. c.DriverPackages, c.DriverPackages[0]] }, "id is not unique" },
        { "short sha", c => EditPackage(c, p => p with { Sha256 = "abc" }), "sha256 must be 64 hexadecimal" },
        { "non-hex sha", c => EditPackage(c, p => p with { Sha256 = new string('g', 64) }), "sha256 must be 64 hexadecimal" },
        { "zero size", c => EditPackage(c, p => p with { SizeBytes = 0 }), "sizeBytes must be greater than zero" },
        { "http url", c => EditPackage(c, p => p with { DownloadUrl = "http://dl.triumph-adler.com/x.zip" }), "must use https" },
        { "foreign host", c => EditPackage(c, p => p with { DownloadUrl = "https://drivers.example.com/x.zip" }), "not an official" },
        { "lookalike host", c => EditPackage(c, p => p with { DownloadUrl = "https://triumph-adler.com.example.net/x.zip" }), "not an official" },
        { "suffix host", c => EditPackage(c, p => p with { DownloadUrl = "https://eviltriumph-adler.com/x.zip" }), "not an official" },
        { "relative url", c => EditPackage(c, p => p with { DownloadUrl = "/x.zip" }), "absolute URL" },
        { "file name with path", c => EditPackage(c, p => p with { FileName = @"..\x.zip" }), "without path separators" },
        { "inf with parent", c => EditPackage(c, p => p with { InfPath = "64bit/../../x.inf" }), "without '..'" },
        { "absolute inf", c => EditPackage(c, p => p with { InfPath = @"C:\x.inf" }), "without '..'" },
        { "no signers", c => EditPackage(c, p => p with { TrustedSigners = [] }), "trustedSigners" },
        { "wrong environment", c => EditPackage(c, p => p with { Environment = "Windows ARM64" }), "environment must be" },
    };

    [Theory]
    [MemberData(nameof(InvalidConfigurations))]
    public void Each_rule_rejects_its_violation(string rule, Func<DeploymentConfig, DeploymentConfig> edit, string expectedError)
    {
        var errors = ConfigLoader.Validate(edit(SampleConfig.Create()));

        Assert.True(errors.Any(e => e.Contains(expectedError, StringComparison.Ordinal)),
            $"Rule '{rule}': expected an error containing \"{expectedError}\", got: {string.Join(" | ", errors)}");
    }

    [Theory]
    [InlineData("https://www.triumph-adler.com/caas/v1/ta/media/102720/data/x/Kx87_UPD_8.7.0917.zip")]
    [InlineData("https://www.utax.com/x.zip")]
    [InlineData("https://utax.it/x.zip")]
    [InlineData("https://download.kyoceradocumentsolutions.com/x.zip")]
    [InlineData("https://triumph-adler.de/x.zip")]
    public void Official_download_hosts_are_accepted(string url)
    {
        Assert.Empty(ConfigLoader.Validate(EditPackage(SampleConfig.Create(), p => p with { DownloadUrl = url })));
    }

    [Fact]
    public void Unconfirmed_dimensions_are_a_warning_not_an_error()
    {
        var config = SampleConfig.Create() with { Ticket = SampleConfig.Create().Ticket with { DimensionsConfirmed = false, ConfirmedBy = null, ConfirmedOn = null } };

        var (errors, warnings) = ConfigLoader.Check(config);

        Assert.Empty(errors);
        Assert.Contains(warnings, w => w.Contains("not confirmed", StringComparison.Ordinal));
    }

    [Fact]
    public void Scale_other_than_100_rounding_residual_and_unapproved_package_are_warnings()
    {
        var config = SampleConfig.Create();
        config = config with
        {
            PrintPreferences = config.PrintPreferences with { ScalePercent = 98 },
            Ticket = config.Ticket with { Width = 3.24001m },
            DriverPackages = [config.DriverPackages[0] with { Approved = false }],
        };

        var (errors, warnings) = ConfigLoader.Check(config);

        Assert.Empty(errors);
        Assert.Contains(warnings, w => w.Contains("scalePercent is 98", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("not a whole number of micrometers", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("is not approved", StringComparison.Ordinal));
    }

    [Fact]
    public void Explicit_null_sections_are_errors_not_crashes()
    {
        string json = """{ "schemaVersion": 1, "ticket": null, "models": null, "driverPackages": null, "connection": null }""";
        string path = _temp.Write("config.json", json);

        var ex = Assert.Throws<ConfigurationException>(() => Load(explicitPath: path));

        Assert.Contains(ex.Errors, e => e.Contains("ticket is required", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.Contains("models must contain", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.Contains("connection must not be null", StringComparison.Ordinal));
    }

    // ---- Loading -------------------------------------------------------------------------------------------------

    [Fact]
    public void Missing_explicit_path_is_an_error_even_when_other_sources_exist()
    {
        _temp.Write(ConfigLoader.ConfigFileName, SampleJson());
        string missing = System.IO.Path.Combine(_temp.Path, "nope.json");

        var ex = Assert.Throws<ConfigurationException>(() => Load(explicitPath: missing, embedded: SampleJson()));

        Assert.Equal(missing, ex.Source);
        Assert.Contains(ex.Errors, e => e.Contains("does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public void Explicit_path_wins_over_the_file_next_to_the_executable()
    {
        _temp.Write(ConfigLoader.ConfigFileName, SampleJson(c => c with { ConfigVersion = "beside-exe" }));
        string explicitPath = _temp.Write("it/custom.json", SampleJson(c => c with { ConfigVersion = "explicit" }));

        var loaded = Load(explicitPath: explicitPath);

        Assert.Equal("explicit", loaded.Config.ConfigVersion);
        Assert.Equal(explicitPath, loaded.Source);
    }

    [Fact]
    public void File_next_to_the_executable_wins_over_the_embedded_copy()
    {
        string path = _temp.Write(ConfigLoader.ConfigFileName, SampleJson(c => c with { ConfigVersion = "beside-exe" }));

        var loaded = Load(embedded: SampleJson(c => c with { ConfigVersion = "embedded" }));

        Assert.Equal("beside-exe", loaded.Config.ConfigVersion);
        Assert.Equal(path, loaded.Source);
    }

    [Fact]
    public void Embedded_copy_is_the_fallback()
    {
        var loaded = Load(embedded: SampleJson());

        Assert.Equal(ConfigLoader.EmbeddedSource, loaded.Source);
        Assert.Equal(SampleConfig.Ticket, loaded.Config.Ticket.Dimensions);
        Assert.Equal(LengthUnit.Inch, loaded.Config.Ticket.Unit);
        Assert.Equal(3, loaded.Config.Models.Count);
    }

    [Fact]
    public void No_configuration_anywhere_is_an_error()
    {
        var ex = Assert.Throws<ConfigurationException>(() => Load());

        Assert.Equal(ConfigLoader.EmbeddedSource, ex.Source);
    }

    [Fact]
    public void Malformed_json_is_an_error_naming_the_source()
    {
        string path = _temp.Write(ConfigLoader.ConfigFileName, "{ \"schemaVersion\": 1, ");

        var ex = Assert.Throws<ConfigurationException>(() => Load());

        Assert.Equal(path, ex.Source);
        Assert.Contains(ex.Errors, e => e.StartsWith("Malformed JSON", StringComparison.Ordinal));
    }

    [Fact]
    public void Invalid_configuration_throws_with_every_error()
    {
        _temp.Write(ConfigLoader.ConfigFileName, SampleJson(c => c with { SchemaVersion = 3, Connection = c.Connection with { RawPortNumber = 0 } }));

        var ex = Assert.Throws<ConfigurationException>(() => Load());

        Assert.True(ex.Errors.Count >= 2);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_accepted()
    {
        string json = SampleJson().Replace("\"schemaVersion\": 1,", "// approved by IT\n  \"schemaVersion\": 1,", StringComparison.Ordinal);
        _temp.Write(ConfigLoader.ConfigFileName, json);

        Assert.Empty(ConfigLoader.Validate(Load().Config));
    }

    [Fact]
    public void Warnings_are_returned_by_load()
    {
        _temp.Write(ConfigLoader.ConfigFileName, SampleJson(c => c with { Ticket = c.Ticket with { DimensionsConfirmed = false } }));

        var loaded = Load();

        Assert.Contains(loaded.Warnings, w => w.Contains("not confirmed", StringComparison.Ordinal));
    }

    // ---- Branch file ---------------------------------------------------------------------------------------------

    [Fact]
    public void Branch_file_next_to_the_executable_is_loaded_and_trimmed()
    {
        _temp.Write(ConfigLoader.ConfigFileName, SampleJson());
        string path = _temp.Write(ConfigLoader.BranchFileName,
            """{ "branchId": "ALX-01", "branchName": " Alexandria Station ", "printerAddress": " 10.20.1.50 ", "modelId": "P-5032DN", "queueName": "" }""");

        var loaded = Load();

        Assert.Equal(path, loaded.BranchSource);
        Assert.Equal("10.20.1.50", loaded.Branch!.PrinterAddress);
        Assert.Equal("Alexandria Station", loaded.Branch.BranchName);
        Assert.Equal("P-5032DN", loaded.Branch.ModelId);
        Assert.Null(loaded.Branch.QueueName);
    }

    [Fact]
    public void Branch_file_in_program_data_is_the_fallback_and_the_executable_folder_wins()
    {
        _temp.Write(ConfigLoader.ConfigFileName, SampleJson());
        string programData = _temp.Subdirectory("ProgramData");
        string fallback = _temp.Write("ProgramData/" + ConfigLoader.BranchFileName, """{ "branchId": "program-data" }""");

        var fromProgramData = Load(programData: programData);
        Assert.Equal(fallback, fromProgramData.BranchSource);
        Assert.Equal("program-data", fromProgramData.Branch!.BranchId);

        _temp.Write(ConfigLoader.BranchFileName, """{ "branchId": "beside-exe" }""");
        Assert.Equal("beside-exe", Load(programData: programData).Branch!.BranchId);
    }

    [Fact]
    public void No_branch_file_means_no_branch()
    {
        _temp.Write(ConfigLoader.ConfigFileName, SampleJson());

        var loaded = Load(programData: _temp.Subdirectory("empty"));

        Assert.Null(loaded.Branch);
        Assert.Null(loaded.BranchSource);
    }

    [Theory]
    [InlineData("""{ "ticket": { "width": 3, "height": 6 } }""")]
    [InlineData("""{ "branchId": "x", "Ticket": null }""")]
    public void Branch_file_cannot_carry_ticket_dimensions(string json)
    {
        _temp.Write(ConfigLoader.ConfigFileName, SampleJson());
        _temp.Write(ConfigLoader.BranchFileName, json);

        var ex = Assert.Throws<ConfigurationException>(() => Load());

        Assert.Contains("Ticket dimensions cannot be overridden per branch", ex.Errors[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("10.0.0.256")]
    [InlineData("10.1")]
    [InlineData("0x0A.0.0.1")]
    [InlineData("printer one")]
    [InlineData("-printer.branch.local")]
    [InlineData("printer_01")]
    [InlineData("http://10.0.0.5")]
    [InlineData("192.168.001.020")]
    [InlineData("[::1]:9100")]
    public void Branch_printer_address_must_be_an_ip_or_host_name(string address)
    {
        _temp.Write(ConfigLoader.ConfigFileName, SampleJson());
        _temp.Write(ConfigLoader.BranchFileName, JsonSerializer.Serialize(new { printerAddress = address }));

        var ex = Assert.Throws<ConfigurationException>(() => Load());

        Assert.Contains(ex.Errors, e => e.Contains("printerAddress", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("10.20.1.50")]
    [InlineData("fe80::1")]
    [InlineData("2001:db8::25")]
    [InlineData("prn-alx01")]
    [InlineData("prn-alx01.branch.amwag.local")]
    public void Valid_branch_printer_addresses_are_accepted(string address)
    {
        _temp.Write(ConfigLoader.ConfigFileName, SampleJson());
        _temp.Write(ConfigLoader.BranchFileName, JsonSerializer.Serialize(new { printerAddress = address }));

        Assert.Equal(address, Load().Branch!.PrinterAddress);
    }

    [Theory]
    [InlineData("[2001:db8::25]", "2001:db8::25")]
    [InlineData("::ffff:10.20.1.50", "10.20.1.50")]
    [InlineData("prn-alx01.branch.amwag.local.", "prn-alx01.branch.amwag.local")]
    public void Branch_printer_address_is_stored_in_its_canonical_spelling(string address, string expected)
    {
        _temp.Write(ConfigLoader.ConfigFileName, SampleJson());
        _temp.Write(ConfigLoader.BranchFileName, JsonSerializer.Serialize(new { printerAddress = address }));

        Assert.Equal(expected, Load().Branch!.PrinterAddress);
    }

    [Fact]
    public void Branch_model_must_exist()
    {
        _temp.Write(ConfigLoader.ConfigFileName, SampleJson());
        _temp.Write(ConfigLoader.BranchFileName, """{ "modelId": "P-9999DN" }""");

        var ex = Assert.Throws<ConfigurationException>(() => Load());

        Assert.Contains(ex.Errors, e => e.Contains("P-9999DN", StringComparison.Ordinal));
    }

    // ---- The shipped configuration -------------------------------------------------------------------------------

    private static string ShippedConfigPath => System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", ConfigLoader.ConfigFileName);

    [Fact]
    public void Shipped_configuration_parses()
    {
        var config = JsonSerializer.Deserialize<DeploymentConfig>(File.ReadAllText(ShippedConfigPath), ConfigJson.Options);

        Assert.NotNull(config);
        Assert.Equal(1, config.SchemaVersion);
        Assert.Equal("AMWAG Ticket", config.Ticket.FormName);
        Assert.Equal(new PaperDimensions(83_058, 170_180), config.Ticket.Dimensions);
    }

    [Fact]
    public void Shipped_configuration_is_valid()
    {
        var config = JsonSerializer.Deserialize<DeploymentConfig>(File.ReadAllText(ShippedConfigPath), ConfigJson.Options)!;

        Assert.Empty(ConfigLoader.Validate(config));
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------

    private LoadedConfiguration Load(string? explicitPath = null, string? embedded = null, string? programData = null) =>
        ConfigLoader.Load(explicitPath, _temp.Path, () => embedded is null ? null : new MemoryStream(Encoding.UTF8.GetBytes(embedded)), programData);

    private static string SampleJson(Func<DeploymentConfig, DeploymentConfig>? edit = null)
    {
        var config = SampleConfig.Create();
        return JsonSerializer.Serialize(edit is null ? config : edit(config), ConfigJson.Options);
    }

    private static DeploymentConfig EditModel(DeploymentConfig config, Func<ModelDefinition, ModelDefinition> edit) =>
        config with { Models = [edit(config.Models[0]), .. config.Models.Skip(1)] };

    private static DeploymentConfig EditPackage(DeploymentConfig config, Func<DriverPackageDefinition, DriverPackageDefinition> edit) =>
        config with { DriverPackages = [edit(config.DriverPackages[0]), .. config.DriverPackages.Skip(1)] };
}
