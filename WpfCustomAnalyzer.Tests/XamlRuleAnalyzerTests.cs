using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using WpfCustomAnalyzer;
using XamlUtils;
using Xunit;

namespace WpfCustomAnalyzer.Tests;

public class XamlRuleAnalyzerTests
{
    private const string PresentationXmlns =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    // Slider is a configured rule; it should be flagged and its Minimum/Maximum extracted.
    // Both attribute syntaxes are used to prove they are handled identically.
    [Fact]
    public async Task Flags_Slider_And_Extracts_Props_From_Both_Syntaxes()
    {
        var xaml =
            $"<Window xmlns=\"{PresentationXmlns}\">\n" +
            "  <Slider Minimum=\"3\">\n" +
            "    <Slider.Maximum>10</Slider.Maximum>\n" +
            "  </Slider>\n" +
            "</Window>";

        var diagnostics = await RunAnalyzerAsync(xaml);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(XamlRuleAnalyzer.DiagnosticId, diagnostic.Id);

        var message = diagnostic.GetMessage();
        Assert.Contains("Slider", message);
        Assert.Contains("Minimum=3", message);
        Assert.Contains("Maximum=10", message);

        // Squiggle should sit on the element name: line 2, column 4 (1-based) == (1, 3) 0-based.
        var lineSpan = diagnostic.Location.GetLineSpan();
        Assert.Equal(new LinePosition(1, 3), lineSpan.StartLinePosition);
        Assert.Equal(new LinePosition(1, 3 + "Slider".Length), lineSpan.EndLinePosition);
    }

    // Neither Minimum nor Maximum is set in the markup. Both are Slider dependency properties, so
    // their extracted values should fall back to the registered DP defaults: Minimum=0, Maximum=1.
    [Fact]
    public async Task Fills_Missing_DependencyProperties_With_Their_Defaults()
    {
        var xaml =
            $"<Window xmlns=\"{PresentationXmlns}\">\n" +
            "  <Slider />\n" +
            "</Window>";

        var diagnostics = await RunAnalyzerAsync(xaml);

        var diagnostic = Assert.Single(diagnostics);
        var message = diagnostic.GetMessage();
        Assert.Contains("Minimum=0", message);   // dependency-property default
        Assert.Contains("Maximum=1", message);   // dependency-property default
    }

    // Minimum is set via {x:Static}. The analyzer should evaluate the referenced static member
    // (StaticValues.SliderMinimum == 42) the way x:Static does and use that as the value.
    [Fact]
    public async Task Resolves_xStatic_Value_Via_Reflection()
    {
        var xaml =
            $"<Window xmlns=\"{PresentationXmlns}\"\n" +
            "        xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n" +
            "        xmlns:local=\"clr-namespace:WpfCustomAnalyzer.Tests;assembly=WpfCustomAnalyzer.Tests\">\n" +
            "  <Slider Minimum=\"{x:Static local:StaticValues.SliderMinimum}\" Maximum=\"10\" />\n" +
            "</Window>";

        var diagnostics = await RunAnalyzerAsync(xaml);

        var diagnostic = Assert.Single(diagnostics);
        var message = diagnostic.GetMessage();
        Assert.Contains("Minimum=42", message);   // evaluated from x:Static
        Assert.Contains("Maximum=10", message);
    }

    // {StaticResource} pointing at an int resource (=1) declared in the Slider's parent's
    // resources should resolve to that primitive value.
    [Fact]
    public async Task Resolves_StaticResource_From_Ancestor_Resources()
    {
        var xaml =
            $"<StackPanel xmlns=\"{PresentationXmlns}\"\n" +
            "            xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n" +
            "            xmlns:sys=\"clr-namespace:System;assembly=System.Runtime\">\n" +
            "  <StackPanel.Resources>\n" +
            "    <sys:Int32 x:Key=\"myMin\">1</sys:Int32>\n" +
            "  </StackPanel.Resources>\n" +
            "  <Slider Minimum=\"{StaticResource myMin}\" Maximum=\"2\" />\n" +
            "</StackPanel>";

        var diagnostics = await RunAnalyzerAsync(xaml);

        var diagnostic = Assert.Single(diagnostics);
        var message = diagnostic.GetMessage();
        Assert.Contains("Minimum=1", message);
        Assert.Contains("Maximum=2", message);
    }

    // The resource lives in a *sibling* (Button) of the Slider, not an ancestor, so by the time the
    // Slider is read the Button scope is closed and the lookup must fail to the invalid marker.
    [Fact]
    public async Task StaticResource_From_NonAncestor_Sibling_Is_Invalid()
    {
        var xaml =
            $"<StackPanel xmlns=\"{PresentationXmlns}\"\n" +
            "            xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n" +
            "            xmlns:sys=\"clr-namespace:System;assembly=System.Runtime\">\n" +
            "  <Button>\n" +
            "    <Button.Resources>\n" +
            "      <sys:Int32 x:Key=\"myMin\">1</sys:Int32>\n" +
            "    </Button.Resources>\n" +
            "  </Button>\n" +
            "  <Slider Minimum=\"{StaticResource myMin}\" Maximum=\"2\" />\n" +
            "</StackPanel>";

        var diagnostics = await RunAnalyzerAsync(xaml);

        var diagnostic = Assert.Single(diagnostics);
        var message = diagnostic.GetMessage();
        Assert.Contains($"Minimum={XamlParser.InvalidResourceValue}", message);
        Assert.Contains("Maximum=2", message);
    }

    // The resource is found but isn't a primitive int/double/string, so it resolves to the marker.
    [Fact]
    public async Task StaticResource_Of_Complex_Type_Is_Invalid()
    {
        var xaml =
            $"<StackPanel xmlns=\"{PresentationXmlns}\"\n" +
            "            xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" +
            "  <StackPanel.Resources>\n" +
            "    <Button x:Key=\"complexRes\" />\n" +
            "  </StackPanel.Resources>\n" +
            "  <Slider Minimum=\"{StaticResource complexRes}\" Maximum=\"2\" />\n" +
            "</StackPanel>";

        var diagnostics = await RunAnalyzerAsync(xaml);

        var diagnostic = Assert.Single(diagnostics);
        var message = diagnostic.GetMessage();
        Assert.Contains($"Minimum={XamlParser.InvalidResourceValue}", message);
        Assert.Contains("Maximum=2", message);
    }

    // An inline <Slider.Style> setter supplies Minimum; Maximum falls back to its DP default.
    [Fact]
    public async Task Resolves_Property_From_Inline_Style_Setter()
    {
        var xaml =
            $"<Slider xmlns=\"{PresentationXmlns}\"\n" +
            "        xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" +
            "  <Slider.Style>\n" +
            "    <Style TargetType=\"Slider\"><Setter Property=\"Minimum\" Value=\"3\"/></Style>\n" +
            "  </Slider.Style>\n" +
            "</Slider>";

        var diagnostics = await RunAnalyzerAsync(xaml);

        var message = Assert.Single(diagnostics).GetMessage();
        Assert.Contains("Minimum=3", message);
        Assert.Contains("Maximum=1", message); // DP default
    }

    // Style="{StaticResource}" plus a BasedOn chain: Minimum from the derived style, Maximum
    // inherited from the base style it is BasedOn.
    [Fact]
    public async Task Resolves_Style_Via_StaticResource_With_BasedOn_Inheritance()
    {
        var xaml =
            $"<StackPanel xmlns=\"{PresentationXmlns}\"\n" +
            "            xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" +
            "  <StackPanel.Resources>\n" +
            "    <Style x:Key=\"baseStyle\" TargetType=\"Slider\"><Setter Property=\"Maximum\" Value=\"50\"/></Style>\n" +
            "    <Style x:Key=\"derived\" TargetType=\"Slider\" BasedOn=\"{StaticResource baseStyle}\">\n" +
            "      <Setter Property=\"Minimum\" Value=\"5\"/>\n" +
            "    </Style>\n" +
            "  </StackPanel.Resources>\n" +
            "  <Slider Style=\"{StaticResource derived}\" />\n" +
            "</StackPanel>";

        var diagnostics = await RunAnalyzerAsync(xaml);

        var message = Assert.Single(diagnostics).GetMessage();
        Assert.Contains("Minimum=5", message);
        Assert.Contains("Maximum=50", message);
    }

    // A derived style's setter overrides the same property set by the style it is BasedOn.
    [Fact]
    public async Task Derived_Style_Setter_Overrides_Base()
    {
        var xaml =
            $"<StackPanel xmlns=\"{PresentationXmlns}\"\n" +
            "            xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" +
            "  <StackPanel.Resources>\n" +
            "    <Style x:Key=\"baseStyle\" TargetType=\"Slider\">\n" +
            "      <Setter Property=\"Minimum\" Value=\"1\"/><Setter Property=\"Maximum\" Value=\"2\"/>\n" +
            "    </Style>\n" +
            "    <Style x:Key=\"derived\" TargetType=\"Slider\" BasedOn=\"{StaticResource baseStyle}\">\n" +
            "      <Setter Property=\"Minimum\" Value=\"9\"/>\n" +
            "    </Style>\n" +
            "  </StackPanel.Resources>\n" +
            "  <Slider Style=\"{StaticResource derived}\" />\n" +
            "</StackPanel>";

        var diagnostics = await RunAnalyzerAsync(xaml);

        var message = Assert.Single(diagnostics).GetMessage();
        Assert.Contains("Minimum=9", message); // derived overrides base's 1
        Assert.Contains("Maximum=2", message); // from base
    }

    // A keyless implicit style whose TargetType matches applies when no Style is set on the element.
    [Fact]
    public async Task Implicit_Keyless_Style_Applies_By_TargetType()
    {
        var xaml =
            $"<StackPanel xmlns=\"{PresentationXmlns}\">\n" +
            "  <StackPanel.Resources>\n" +
            "    <Style TargetType=\"Slider\"><Setter Property=\"Minimum\" Value=\"4\"/></Style>\n" +
            "  </StackPanel.Resources>\n" +
            "  <Slider />\n" +
            "</StackPanel>";

        var diagnostics = await RunAnalyzerAsync(xaml);

        var message = Assert.Single(diagnostics).GetMessage();
        Assert.Contains("Minimum=4", message);
        Assert.Contains("Maximum=1", message); // DP default
    }

    // {x:Type Slider} as a TargetType resolves the same as the bare "Slider" form.
    [Fact]
    public async Task Implicit_Style_Matches_TargetType_Via_xType()
    {
        var xaml =
            $"<StackPanel xmlns=\"{PresentationXmlns}\"\n" +
            "            xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" +
            "  <StackPanel.Resources>\n" +
            "    <Style TargetType=\"{x:Type Slider}\"><Setter Property=\"Minimum\" Value=\"6\"/></Style>\n" +
            "  </StackPanel.Resources>\n" +
            "  <Slider />\n" +
            "</StackPanel>";

        var diagnostics = await RunAnalyzerAsync(xaml);

        var message = Assert.Single(diagnostics).GetMessage();
        Assert.Contains("Minimum=6", message);
    }

    // A directly-set Style suppresses any matching implicit (keyless) style.
    [Fact]
    public async Task Direct_Style_Suppresses_Implicit_Style()
    {
        var xaml =
            $"<StackPanel xmlns=\"{PresentationXmlns}\"\n" +
            "            xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" +
            "  <StackPanel.Resources>\n" +
            "    <Style TargetType=\"Slider\"><Setter Property=\"Minimum\" Value=\"4\"/></Style>\n" +
            "    <Style x:Key=\"explicitStyle\" TargetType=\"Slider\"><Setter Property=\"Maximum\" Value=\"7\"/></Style>\n" +
            "  </StackPanel.Resources>\n" +
            "  <Slider Style=\"{StaticResource explicitStyle}\" />\n" +
            "</StackPanel>";

        var diagnostics = await RunAnalyzerAsync(xaml);

        var message = Assert.Single(diagnostics).GetMessage();
        Assert.Contains("Maximum=7", message);   // from the explicit style
        Assert.Contains("Minimum=0", message);   // DP default — the implicit style is ignored
    }

    // A value set directly on the element wins over the same property from its style.
    [Fact]
    public async Task Direct_Value_Overrides_Style_Setter()
    {
        var xaml =
            $"<StackPanel xmlns=\"{PresentationXmlns}\"\n" +
            "            xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" +
            "  <StackPanel.Resources>\n" +
            "    <Style x:Key=\"s\" TargetType=\"Slider\"><Setter Property=\"Minimum\" Value=\"5\"/></Style>\n" +
            "  </StackPanel.Resources>\n" +
            "  <Slider Minimum=\"8\" Style=\"{StaticResource s}\" />\n" +
            "</StackPanel>";

        var diagnostics = await RunAnalyzerAsync(xaml);

        var message = Assert.Single(diagnostics).GetMessage();
        Assert.Contains("Minimum=8", message);   // direct value wins over the setter's 5
    }

    // Button is not a configured rule, so nothing should be reported.
    [Fact]
    public async Task Ignores_Elements_That_Match_No_Rule()
    {
        var xaml =
            $"<Window xmlns=\"{PresentationXmlns}\">\n" +
            "  <Button Content=\"Hi\" />\n" +
            "</Window>";

        var diagnostics = await RunAnalyzerAsync(xaml);

        Assert.Empty(diagnostics);
    }

    private static async Task<ImmutableArray<Diagnostic>> RunAnalyzerAsync(string xaml)
    {
        var compilation = CreateWpfCompilation();
        var additionalFiles = ImmutableArray.Create<AdditionalText>(
            new InMemoryAdditionalText("Test.xaml", xaml));
        var options = new AnalyzerOptions(additionalFiles);

        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new XamlRuleAnalyzer()),
            options);

        return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
    }

    private static CSharpCompilation CreateWpfCompilation()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

        foreach (var name in new[] { "PresentationFramework", "PresentationCore", "WindowsBase" })
            references.Add(MetadataReference.CreateFromFile(Assembly.Load(name).Location));

        // Reference this test assembly so the resolver can find StaticValues (used by the
        // {x:Static} test) and reflection can load it at runtime.
        references.Add(MetadataReference.CreateFromFile(typeof(XamlRuleAnalyzerTests).Assembly.Location));

        return CSharpCompilation.Create(
            "TargetAssembly",
            new[] { CSharpSyntaxTree.ParseText("class C { }") },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private sealed class InMemoryAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        public InMemoryAdditionalText(string path, string content)
        {
            Path = path;
            _text = SourceText.From(content);
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }
}

/// <summary>
/// A loadable type with a static member, referenced via {x:Static} by
/// <see cref="WpfCustomAnalyzer.Tests.XamlRuleAnalyzerTests"/>. Must be top-level (not nested) so it
/// is addressable as <c>clr-namespace:WpfCustomAnalyzer.Tests</c>.
/// </summary>
public static class StaticValues
{
    public static double SliderMinimum => 42;
}
