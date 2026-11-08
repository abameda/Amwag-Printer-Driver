using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Amwag.PrinterSetup.Core.Printing;

namespace Amwag.PrinterSetup.Windows.Spooler;

/// <summary>
/// Reads the parts of Print Schema documents the setup checks (PrintCapabilities and PrintTicket). Names in the documents
/// are QNames: they are resolved against the document's namespace declarations, and keywords of the public keyword
/// namespace are reported with the conventional "psk:" prefix whatever prefix the driver chose. Sizes are micrometers.
/// </summary>
internal static class PrintSchemaReader
{
    private static readonly XNamespace Psf = "http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework";
    private static readonly XNamespace Psk = "http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords";

    private static readonly XName FeatureElement = Psf + "Feature";
    private static readonly XName OptionElement = Psf + "Option";
    private static readonly XName PropertyElement = Psf + "Property";
    private static readonly XName ScoredPropertyElement = Psf + "ScoredProperty";
    private static readonly XName ValueElement = Psf + "Value";
    private static readonly XName ParameterRefElement = Psf + "ParameterRef";
    private static readonly XName ParameterDefElement = Psf + "ParameterDef";
    private static readonly XName ParameterInitElement = Psf + "ParameterInit";

    private static readonly XName PageMediaSize = Psk + "PageMediaSize";
    private static readonly XName MediaSizeWidth = Psk + "MediaSizeWidth";
    private static readonly XName MediaSizeHeight = Psk + "MediaSizeHeight";
    private static readonly XName DisplayName = Psk + "DisplayName";
    private static readonly XName MinValue = Psf + "MinValue";
    private static readonly XName MaxValue = Psf + "MaxValue";

    private static readonly XName[] InputBinFeatures = [Psk + "JobInputBin", Psk + "DocumentInputBin", Psk + "PageInputBin"];
    private static readonly XName[] OrientationFeatures = [Psk + "PageOrientation"];
    private static readonly XName[] DuplexFeatures = [Psk + "JobDuplexAllDocumentsContiguously", Psk + "DocumentDuplex"];

    // Custom page size parameters: generic custom size first, then the PostScript custom page size.
    private static readonly (XName Width, XName Height)[] CustomSizeParameters =
    [
        (Psk + "PageMediaSizeMediaSizeWidth", Psk + "PageMediaSizeMediaSizeHeight"),
        (Psk + "PageMediaSizePSWidth", Psk + "PageMediaSizePSHeight"),
    ];

    /// <summary>Throws <see cref="XmlException"/> or <see cref="InvalidDataException"/> for a malformed document.</summary>
    public static PrintCapabilitiesSummary ReadCapabilities(byte[] document)
    {
        XElement root = Load(document, "PrintCapabilities");
        XElement? mediaSize = FindFeature(root, PageMediaSize);

        return new PrintCapabilitiesSummary
        {
            Available = true,
            MediaSizes = mediaSize?.Elements(OptionElement).Select(ReadMediaSizeOption).ToList() ?? [],
            CustomSizeRange = ReadCustomSizeRange(root),
            InputBins = InputBinFeatures
                .Select(name => FindFeature(root, name))
                .OfType<XElement>()
                .SelectMany(feature => feature.Elements(OptionElement))
                .Select(OptionName)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList(),
        };
    }

    /// <summary>Throws <see cref="XmlException"/> or <see cref="InvalidDataException"/> for a malformed document.</summary>
    public static PrintTicketMediaInfo ReadTicket(byte[] document)
    {
        XElement root = Load(document, "PrintTicket");
        XElement? mediaSize = FindFeature(root, PageMediaSize)?.Element(OptionElement);

        return new PrintTicketMediaInfo
        {
            Available = true,
            MediaSizeName = mediaSize is null ? null : OptionName(mediaSize),
            WidthMicrometers = mediaSize is null ? null : ScoredInteger(root, mediaSize, MediaSizeWidth),
            HeightMicrometers = mediaSize is null ? null : ScoredInteger(root, mediaSize, MediaSizeHeight),
            InputBin = SelectedOption(root, InputBinFeatures),
            Orientation = SelectedOption(root, OrientationFeatures),
            Duplex = SelectedOption(root, DuplexFeatures),
        };
    }

    private static XElement Load(byte[] document, string rootName)
    {
        using var stream = new MemoryStream(document, writable: false);
        XElement root = XDocument.Load(stream).Root ?? throw new InvalidDataException("The Print Schema document is empty.");
        if (root.Name != Psf + rootName)
            throw new InvalidDataException($"Expected a psf:{rootName} document but the root element is {root.Name}.");
        return root;
    }

    private static MediaSizeCapability ReadMediaSizeOption(XElement option) =>
        new(
            OptionName(option) ?? "",
            PropertyValue(option, DisplayName),
            ScoredInteger(root: null, option, MediaSizeWidth),
            ScoredInteger(root: null, option, MediaSizeHeight));

    private static CustomMediaSizeRange? ReadCustomSizeRange(XElement root)
    {
        foreach (var (widthName, heightName) in CustomSizeParameters)
        {
            XElement? width = FindNamed(root.Elements(ParameterDefElement), widthName);
            XElement? height = FindNamed(root.Elements(ParameterDefElement), heightName);
            if (width is null || height is null)
                continue;

            int? minWidth = ParseInteger(PropertyValue(width, MinValue));
            int? maxWidth = ParseInteger(PropertyValue(width, MaxValue));
            int? minHeight = ParseInteger(PropertyValue(height, MinValue));
            int? maxHeight = ParseInteger(PropertyValue(height, MaxValue));
            if (minWidth is int a && maxWidth is int b && minHeight is int c && maxHeight is int d)
                return new CustomMediaSizeRange(a, b, c, d);
        }
        return null;
    }

    private static string? SelectedOption(XElement root, XName[] featureNames)
    {
        foreach (XName name in featureNames)
        {
            XElement? option = FindFeature(root, name)?.Element(OptionElement);
            if (option is not null)
                return OptionName(option);
        }
        return null;
    }

    /// <summary>
    /// A scored property's integer value. In a ticket a custom size refers to a ParameterInit by name; capabilities
    /// only define the parameter's range, so a reference without <paramref name="root"/> yields null.
    /// </summary>
    private static int? ScoredInteger(XElement? root, XElement option, XName propertyName)
    {
        XElement? property = FindNamed(option.Elements(ScoredPropertyElement), propertyName);
        if (property is null)
            return null;

        XElement? value = property.Element(ValueElement);
        if (value is not null)
            return ParseInteger(value.Value);

        XElement? reference = property.Element(ParameterRefElement);
        if (root is null || reference is null)
            return null;

        XElement? init = FindNamed(root.Elements(ParameterInitElement), ResolveName(reference));
        return ParseInteger(init?.Element(ValueElement)?.Value);
    }

    private static string? PropertyValue(XElement element, XName propertyName) =>
        FindNamed(element.Elements(PropertyElement), propertyName)?.Element(ValueElement)?.Value;

    private static XElement? FindFeature(XElement root, XName featureName) =>
        FindNamed(root.Elements(FeatureElement), featureName);

    private static XElement? FindNamed(IEnumerable<XElement> elements, XName? name) =>
        name is null ? null : elements.FirstOrDefault(element => ResolveName(element) == name);

    /// <summary>The element's "name" attribute as an expanded name, or null when absent or its prefix is undeclared.</summary>
    private static XName? ResolveName(XElement element)
    {
        string? qualified = ((string?)element.Attribute("name"))?.Trim();
        if (string.IsNullOrEmpty(qualified))
            return null;

        int colon = qualified.IndexOf(':');
        if (colon < 0)
            return element.GetDefaultNamespace() + qualified;

        XNamespace? ns = element.GetNamespaceOfPrefix(qualified[..colon]);
        string local = qualified[(colon + 1)..];
        return ns is null || local.Length == 0 ? null : ns + local;
    }

    private static string? OptionName(XElement option)
    {
        string? qualified = ((string?)option.Attribute("name"))?.Trim();
        if (string.IsNullOrEmpty(qualified))
            return null;

        XName? name = ResolveName(option);
        return name is not null && name.Namespace == Psk ? "psk:" + name.LocalName : qualified;
    }

    private static int? ParseInteger(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;
}
