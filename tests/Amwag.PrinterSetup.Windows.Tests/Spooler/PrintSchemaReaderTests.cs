using System.Text;
using System.Xml;
using Amwag.PrinterSetup.Windows.Spooler;

namespace Amwag.PrinterSetup.Windows.Tests.Spooler;

/// <summary>Print Schema documents shaped like the ones prntvpt.dll returns (see the Print Schema framework specification).</summary>
public sealed class PrintSchemaReaderTests
{
    private const string Namespaces =
        "xmlns:psf=\"http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework\" " +
        "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
        "xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" " +
        "xmlns:ns0000=\"http://schemas.microsoft.com/windows/printing/oemdriverpt/Test/1.0/\"";

    private const string Keywords = "http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords";

    private static readonly string Capabilities = $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <psf:PrintCapabilities {Namespaces} xmlns:psk="{Keywords}" version="1">
          <psf:Feature name="psk:PageMediaSize">
            <psf:Property name="psf:SelectionType"><psf:Value xsi:type="xsd:QName">psk:PickOne</psf:Value></psf:Property>
            <psf:Option name="psk:ISOA4" constrained="psk:None">
              <psf:ScoredProperty name="psk:MediaSizeWidth"><psf:Value xsi:type="xsd:integer">210000</psf:Value></psf:ScoredProperty>
              <psf:ScoredProperty name="psk:MediaSizeHeight"><psf:Value xsi:type="xsd:integer">297000</psf:Value></psf:ScoredProperty>
              <psf:Property name="psk:DisplayName"><psf:Value xsi:type="xsd:string">A4</psf:Value></psf:Property>
            </psf:Option>
            <psf:Option name="ns0000:User0000000257" constrained="psk:None">
              <psf:ScoredProperty name="psk:MediaSizeWidth"><psf:Value xsi:type="xsd:integer">82296</psf:Value></psf:ScoredProperty>
              <psf:ScoredProperty name="psk:MediaSizeHeight"><psf:Value xsi:type="xsd:integer">170180</psf:Value></psf:ScoredProperty>
              <psf:Property name="psk:DisplayName"><psf:Value xsi:type="xsd:string">AMWAG Ticket</psf:Value></psf:Property>
            </psf:Option>
            <psf:Option name="psk:CustomMediaSize" constrained="psk:None">
              <psf:ScoredProperty name="psk:MediaSizeWidth"><psf:ParameterRef name="psk:PageMediaSizeMediaSizeWidth" /></psf:ScoredProperty>
              <psf:ScoredProperty name="psk:MediaSizeHeight"><psf:ParameterRef name="psk:PageMediaSizeMediaSizeHeight" /></psf:ScoredProperty>
            </psf:Option>
          </psf:Feature>
          <psf:Feature name="psk:JobInputBin">
            <psf:Option name="psk:AutoSelect" />
            <psf:Option name="psk:Manual"><psf:Property name="psk:DisplayName"><psf:Value xsi:type="xsd:string">MP Tray</psf:Value></psf:Property></psf:Option>
            <psf:Option name="ns0000:Cassette1" />
          </psf:Feature>
          <psf:Feature name="psk:PageInputBin">
            <psf:Option name="psk:Manual" />
          </psf:Feature>
          <psf:ParameterDef name="psk:PageMediaSizeMediaSizeWidth">
            <psf:Property name="psf:DataType"><psf:Value xsi:type="xsd:QName">xsd:integer</psf:Value></psf:Property>
            <psf:Property name="psf:DefaultValue"><psf:Value xsi:type="xsd:integer">215900</psf:Value></psf:Property>
            <psf:Property name="psf:MinValue"><psf:Value xsi:type="xsd:integer">70000</psf:Value></psf:Property>
            <psf:Property name="psf:MaxValue"><psf:Value xsi:type="xsd:integer">216000</psf:Value></psf:Property>
            <psf:Property name="psk:UnitType"><psf:Value xsi:type="xsd:string">microns</psf:Value></psf:Property>
          </psf:ParameterDef>
          <psf:ParameterDef name="psk:PageMediaSizeMediaSizeHeight">
            <psf:Property name="psf:MinValue"><psf:Value xsi:type="xsd:integer">148000</psf:Value></psf:Property>
            <psf:Property name="psf:MaxValue"><psf:Value xsi:type="xsd:integer">356000</psf:Value></psf:Property>
          </psf:ParameterDef>
        </psf:PrintCapabilities>
        """;

    [Fact]
    public void Capabilities_list_media_sizes_with_names_display_names_and_micrometers()
    {
        var summary = PrintSchemaReader.ReadCapabilities(Bytes(Capabilities));

        Assert.True(summary.Available);
        Assert.Null(summary.Error);
        Assert.Collection(
            summary.MediaSizes,
            a4 => Assert.Equal(new("psk:ISOA4", "A4", 210000, 297000), a4),
            ticket => Assert.Equal(new("ns0000:User0000000257", "AMWAG Ticket", 82296, 170180), ticket),
            custom => Assert.Equal(new("psk:CustomMediaSize", null, null, null), custom));
    }

    [Fact]
    public void Capabilities_report_the_custom_size_range_from_the_parameter_definitions()
    {
        var summary = PrintSchemaReader.ReadCapabilities(Bytes(Capabilities));

        Assert.Equal(new(70000, 216000, 148000, 356000), summary.CustomSizeRange);
    }

    [Fact]
    public void Capabilities_list_input_bin_options_once_across_job_document_and_page_features()
    {
        var summary = PrintSchemaReader.ReadCapabilities(Bytes(Capabilities));

        Assert.Equal(new[] { "psk:AutoSelect", "psk:Manual", "ns0000:Cassette1" }, summary.InputBins);
    }

    [Fact]
    public void Keyword_names_are_normalized_to_the_psk_prefix_whatever_prefix_the_driver_declares()
    {
        string document = Capabilities.Replace("psk:", "k:").Replace("xmlns:psk=", "xmlns:k=");

        var summary = PrintSchemaReader.ReadCapabilities(Bytes(document));

        Assert.Equal("psk:ISOA4", summary.MediaSizes[0].Name);
        Assert.Equal("A4", summary.MediaSizes[0].DisplayName);
        Assert.Equal(210000, summary.MediaSizes[0].WidthMicrometers);
        Assert.Equal(new(70000, 216000, 148000, 356000), summary.CustomSizeRange);
        Assert.Contains("psk:Manual", summary.InputBins);
    }

    [Fact]
    public void Postscript_custom_page_size_parameters_are_used_when_the_generic_ones_are_absent()
    {
        string document = $"""
            <psf:PrintCapabilities {Namespaces} xmlns:psk="{Keywords}" version="1">
              <psf:ParameterDef name="psk:PageMediaSizePSWidth">
                <psf:Property name="psf:MinValue"><psf:Value>76200</psf:Value></psf:Property>
                <psf:Property name="psf:MaxValue"><psf:Value>215900</psf:Value></psf:Property>
              </psf:ParameterDef>
              <psf:ParameterDef name="psk:PageMediaSizePSHeight">
                <psf:Property name="psf:MinValue"><psf:Value>127000</psf:Value></psf:Property>
                <psf:Property name="psf:MaxValue"><psf:Value>355600</psf:Value></psf:Property>
              </psf:ParameterDef>
            </psf:PrintCapabilities>
            """;

        var summary = PrintSchemaReader.ReadCapabilities(Bytes(document));

        Assert.Empty(summary.MediaSizes);
        Assert.Empty(summary.InputBins);
        Assert.Equal(new(76200, 215900, 127000, 355600), summary.CustomSizeRange);
    }

    [Fact]
    public void An_incomplete_custom_size_range_is_not_reported()
    {
        string document = $"""
            <psf:PrintCapabilities {Namespaces} xmlns:psk="{Keywords}" version="1">
              <psf:ParameterDef name="psk:PageMediaSizeMediaSizeWidth">
                <psf:Property name="psf:MinValue"><psf:Value>70000</psf:Value></psf:Property>
              </psf:ParameterDef>
              <psf:ParameterDef name="psk:PageMediaSizeMediaSizeHeight">
                <psf:Property name="psf:MinValue"><psf:Value>148000</psf:Value></psf:Property>
                <psf:Property name="psf:MaxValue"><psf:Value>356000</psf:Value></psf:Property>
              </psf:ParameterDef>
            </psf:PrintCapabilities>
            """;

        Assert.Null(PrintSchemaReader.ReadCapabilities(Bytes(document)).CustomSizeRange);
    }

    [Fact]
    public void Ticket_reports_the_selected_size_bin_orientation_and_duplex()
    {
        string document = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <psf:PrintTicket {Namespaces} xmlns:psk="{Keywords}" version="1">
              <psf:Feature name="psk:JobDuplexAllDocumentsContiguously"><psf:Option name="psk:OneSided" /></psf:Feature>
              <psf:Feature name="psk:JobInputBin"><psf:Option name="psk:Manual" /></psf:Feature>
              <psf:Feature name="psk:PageMediaSize">
                <psf:Option name="ns0000:User0000000257">
                  <psf:ScoredProperty name="psk:MediaSizeWidth"><psf:Value xsi:type="xsd:integer">82296</psf:Value></psf:ScoredProperty>
                  <psf:ScoredProperty name="psk:MediaSizeHeight"><psf:Value xsi:type="xsd:integer">170180</psf:Value></psf:ScoredProperty>
                </psf:Option>
              </psf:Feature>
              <psf:Feature name="psk:PageOrientation"><psf:Option name="psk:Portrait" /></psf:Feature>
            </psf:PrintTicket>
            """;

        var ticket = PrintSchemaReader.ReadTicket(Bytes(document));

        Assert.True(ticket.Available);
        Assert.Equal("ns0000:User0000000257", ticket.MediaSizeName);
        Assert.Equal(82296, ticket.WidthMicrometers);
        Assert.Equal(170180, ticket.HeightMicrometers);
        Assert.Equal("psk:Manual", ticket.InputBin);
        Assert.Equal("psk:Portrait", ticket.Orientation);
        Assert.Equal("psk:OneSided", ticket.Duplex);
    }

    [Fact]
    public void Ticket_resolves_a_custom_size_through_its_parameter_initializers()
    {
        string document = $"""
            <psf:PrintTicket {Namespaces} xmlns:psk="{Keywords}" version="1">
              <psf:Feature name="psk:PageMediaSize">
                <psf:Option name="psk:CustomMediaSize">
                  <psf:ScoredProperty name="psk:MediaSizeWidth"><psf:ParameterRef name="psk:PageMediaSizeMediaSizeWidth" /></psf:ScoredProperty>
                  <psf:ScoredProperty name="psk:MediaSizeHeight"><psf:ParameterRef name="psk:PageMediaSizeMediaSizeHeight" /></psf:ScoredProperty>
                </psf:Option>
              </psf:Feature>
              <psf:Feature name="psk:DocumentDuplex"><psf:Option name="psk:TwoSidedLongEdge" /></psf:Feature>
              <psf:Feature name="psk:PageInputBin"><psf:Option name="ns0000:Cassette1" /></psf:Feature>
              <psf:ParameterInit name="psk:PageMediaSizeMediaSizeWidth"><psf:Value xsi:type="xsd:integer">82296</psf:Value></psf:ParameterInit>
              <psf:ParameterInit name="psk:PageMediaSizeMediaSizeHeight"><psf:Value xsi:type="xsd:integer">170180</psf:Value></psf:ParameterInit>
            </psf:PrintTicket>
            """;

        var ticket = PrintSchemaReader.ReadTicket(Bytes(document));

        Assert.Equal("psk:CustomMediaSize", ticket.MediaSizeName);
        Assert.Equal(82296, ticket.WidthMicrometers);
        Assert.Equal(170180, ticket.HeightMicrometers);
        Assert.Equal("psk:TwoSidedLongEdge", ticket.Duplex);
        Assert.Equal("ns0000:Cassette1", ticket.InputBin);
        Assert.Null(ticket.Orientation);
    }

    [Fact]
    public void Utf16_documents_are_read()
    {
        string document = Capabilities.Replace("encoding=\"UTF-8\"", "encoding=\"UTF-16\"");
        byte[] bytes = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(document.TrimStart())];

        Assert.Equal(3, PrintSchemaReader.ReadCapabilities(bytes).MediaSizes.Count);
    }

    [Fact]
    public void A_ticket_is_not_accepted_as_capabilities()
    {
        string document = $"""<psf:PrintTicket {Namespaces} xmlns:psk="{Keywords}" version="1" />""";

        Assert.Throws<InvalidDataException>(() => PrintSchemaReader.ReadCapabilities(Bytes(document)));
    }

    [Fact]
    public void Malformed_xml_is_reported_as_an_xml_error()
    {
        Assert.Throws<XmlException>(() => PrintSchemaReader.ReadTicket(Bytes("<psf:PrintTicket")));
    }

    private static byte[] Bytes(string document) => Encoding.UTF8.GetBytes(document.TrimStart());
}
