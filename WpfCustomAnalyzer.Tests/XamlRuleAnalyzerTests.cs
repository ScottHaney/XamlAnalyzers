using System;
using System.Collections.Generic;
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
using XamlRules;
using XamlUtils;
using Xunit;

namespace WpfCustomAnalyzer.Tests;

public class XamlRuleAnalyzerTests
{
    private const string PresentationXmlns =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    // ---- XamlParser extraction behaviour (tested directly, the level it belongs at) ----

    // Minimum (inline attribute) and Maximum (property element) extract identically.
    [Fact]
    public void Extracts_Props_From_Both_Attribute_Syntaxes()
    {
        var props = ExtractSlider(
            $"<Window xmlns=\"{PresentationXmlns}\">\n" +
            "  <Slider Minimum=\"3\">\n" +
            "    <Slider.Maximum>10</Slider.Maximum>\n" +
            "  </Slider>\n" +
            "</Window>");

        Assert.Equal("3", props["Minimum"].Value);
        Assert.Equal("10", props["Maximum"].Value);
    }

    // Missing properties fall back to the dependency-property defaults (Minimum=0, Maximum=1).
    [Fact]
    public void Fills_Missing_DependencyProperties_With_Their_Defaults()
    {
        var props = ExtractSlider($"<Window xmlns=\"{PresentationXmlns}\"><Slider /></Window>");

        Assert.Equal("0", props["Minimum"].Value);
        Assert.Equal("1", props["Maximum"].Value);
    }

    // {x:Static} is evaluated by reflection (StaticValues.SliderMinimum == 42).
    [Fact]
    public void Resolves_xStatic_Value_Via_Reflection()
    {
        var props = ExtractSlider(
            $"<Window xmlns=\"{PresentationXmlns}\"\n" +
            "        xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n" +
            "        xmlns:local=\"clr-namespace:WpfCustomAnalyzer.Tests;assembly=WpfCustomAnalyzer.Tests\">\n" +
            "  <Slider Minimum=\"{x:Static local:StaticValues.SliderMinimum}\" Maximum=\"10\" />\n" +
            "</Window>");

        Assert.Equal("42", props["Minimum"].Value);
        Assert.Equal("10", props["Maximum"].Value);
    }

    // {StaticResource} resolves a primitive int declared in an ancestor's resources.
    [Fact]
    public void Resolves_StaticResource_From_Ancestor_Resources()
    {
        var props = ExtractSlider(
            $"<StackPanel xmlns=\"{PresentationXmlns}\"\n" +
            "            xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n" +
            "            xmlns:sys=\"clr-namespace:System;assembly=System.Runtime\">\n" +
            "  <StackPanel.Resources>\n" +
            "    <sys:Int32 x:Key=\"myMin\">1</sys:Int32>\n" +
            "  </StackPanel.Resources>\n" +
            "  <Slider Minimum=\"{StaticResource myMin}\" Maximum=\"2\" />\n" +
            "</StackPanel>");

        Assert.Equal("1", props["Minimum"].Value);
        Assert.Equal("2", props["Maximum"].Value);
    }

    // A resource in a sibling (not an ancestor) is out of scope -> invalid marker.
    [Fact]
    public void StaticResource_From_NonAncestor_Sibling_Is_Invalid()
    {
        var props = ExtractSlider(
            $"<StackPanel xmlns=\"{PresentationXmlns}\"\n" +
            "            xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n" +
            "            xmlns:sys=\"clr-namespace:System;assembly=System.Runtime\">\n" +
            "  <Button><Button.Resources><sys:Int32 x:Key=\"myMin\">1</sys:Int32></Button.Resources></Button>\n" +
            "  <Slider Minimum=\"{StaticResource myMin}\" Maximum=\"2\" />\n" +
            "</StackPanel>");

        Assert.Equal(XamlParser.InvalidResourceValue, props["Minimum"].Value);
        Assert.Equal("2", props["Maximum"].Value);
    }

    // A found-but-non-primitive resource resolves to the invalid marker.
    [Fact]
    public void StaticResource_Of_Complex_Type_Is_Invalid()
    {
        var props = ExtractSlider(
            $"<StackPanel xmlns=\"{PresentationXmlns}\"\n" +
            "            xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" +
            "  <StackPanel.Resources><Button x:Key=\"complexRes\" /></StackPanel.Resources>\n" +
            "  <Slider Minimum=\"{StaticResource complexRes}\" Maximum=\"2\" />\n" +
            "</StackPanel>");

        Assert.Equal(XamlParser.InvalidResourceValue, props["Minimum"].Value);
    }

    // An inline <Slider.Style> setter supplies Minimum; Maximum falls back to its DP default.
    [Fact]
    public void Resolves_Property_From_Inline_Style_Setter()
    {
        var props = ExtractSlider(
            $"<Slider xmlns=\"{PresentationXmlns}\"\n" +
            "        xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" +
            "  <Slider.Style>\n" +
            "    <Style TargetType=\"Slider\"><Setter Property=\"Minimum\" Value=\"3\"/></Style>\n" +
            "  </Slider.Style>\n" +
            "</Slider>");

        Assert.Equal("3", props["Minimum"].Value);
        Assert.Equal("1", props["Maximum"].Value);
    }

    // Style="{StaticResource}" + BasedOn: Minimum from derived, Maximum inherited from base.
    [Fact]
    public void Resolves_Style_Via_StaticResource_With_BasedOn_Inheritance()
    {
        var props = ExtractSlider(
            $"<StackPanel xmlns=\"{PresentationXmlns}\"\n" +
            "            xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" +
            "  <StackPanel.Resources>\n" +
            "    <Style x:Key=\"baseStyle\" TargetType=\"Slider\"><Setter Property=\"Maximum\" Value=\"50\"/></Style>\n" +
            "    <Style x:Key=\"derived\" TargetType=\"Slider\" BasedOn=\"{StaticResource baseStyle}\">\n" +
            "      <Setter Property=\"Minimum\" Value=\"5\"/>\n" +
            "    </Style>\n" +
            "  </StackPanel.Resources>\n" +
            "  <Slider Style=\"{StaticResource derived}\" />\n" +
            "</StackPanel>");

        Assert.Equal("5", props["Minimum"].Value);
        Assert.Equal("50", props["Maximum"].Value);
    }

    // A derived style's setter overrides the same property set by its base.
    [Fact]
    public void Derived_Style_Setter_Overrides_Base()
    {
        var props = ExtractSlider(
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
            "</StackPanel>");

        Assert.Equal("9", props["Minimum"].Value); // derived overrides base's 1
        Assert.Equal("2", props["Maximum"].Value); // from base
    }

    // A keyless implicit style applies by TargetType when no Style is set on the element.
    [Fact]
    public void Implicit_Keyless_Style_Applies_By_TargetType()
    {
        var props = ExtractSlider(
            $"<StackPanel xmlns=\"{PresentationXmlns}\">\n" +
            "  <StackPanel.Resources><Style TargetType=\"Slider\"><Setter Property=\"Minimum\" Value=\"4\"/></Style></StackPanel.Resources>\n" +
            "  <Slider />\n" +
            "</StackPanel>");

        Assert.Equal("4", props["Minimum"].Value);
        Assert.Equal("1", props["Maximum"].Value);
    }

    // TargetType="{x:Type Slider}" matches the same as the bare "Slider" form.
    [Fact]
    public void Implicit_Style_Matches_TargetType_Via_xType()
    {
        var props = ExtractSlider(
            $"<StackPanel xmlns=\"{PresentationXmlns}\"\n" +
            "            xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" +
            "  <StackPanel.Resources><Style TargetType=\"{x:Type Slider}\"><Setter Property=\"Minimum\" Value=\"6\"/></Style></StackPanel.Resources>\n" +
            "  <Slider />\n" +
            "</StackPanel>");

        Assert.Equal("6", props["Minimum"].Value);
    }

    // A directly-set Style suppresses any matching implicit (keyless) style.
    [Fact]
    public void Direct_Style_Suppresses_Implicit_Style()
    {
        var props = ExtractSlider(
            $"<StackPanel xmlns=\"{PresentationXmlns}\"\n" +
            "            xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" +
            "  <StackPanel.Resources>\n" +
            "    <Style TargetType=\"Slider\"><Setter Property=\"Minimum\" Value=\"4\"/></Style>\n" +
            "    <Style x:Key=\"explicitStyle\" TargetType=\"Slider\"><Setter Property=\"Maximum\" Value=\"7\"/></Style>\n" +
            "  </StackPanel.Resources>\n" +
            "  <Slider Style=\"{StaticResource explicitStyle}\" />\n" +
            "</StackPanel>");

        Assert.Equal("7", props["Maximum"].Value);                            // from the explicit style
        Assert.Equal("0", props["Minimum"].Value);                            // DP default; implicit ignored
        Assert.Equal(PropertyValueSource.DefaultValue, props["Minimum"].Source);
    }

    // A value set directly on the element wins over the same property from its style.
    [Fact]
    public void Direct_Value_Overrides_Style_Setter()
    {
        var props = ExtractSlider(
            $"<StackPanel xmlns=\"{PresentationXmlns}\"\n" +
            "            xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" +
            "  <StackPanel.Resources><Style x:Key=\"s\" TargetType=\"Slider\"><Setter Property=\"Minimum\" Value=\"5\"/></Style></StackPanel.Resources>\n" +
            "  <Slider Minimum=\"8\" Style=\"{StaticResource s}\" />\n" +
            "</StackPanel>");

        Assert.Equal("8", props["Minimum"].Value);
        Assert.Equal(PropertyValueSource.LocalValue, props["Minimum"].Source);
    }

    // Each resolved property reports where its value came from (and NotValueFound -> null value).
    [Fact]
    public void PropertyValue_Reports_Its_Source()
    {
        var xaml =
            $"<StackPanel xmlns=\"{PresentationXmlns}\"\n" +
            "            xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" +
            "  <StackPanel.Resources>\n" +
            "    <Style x:Key=\"s\" TargetType=\"Slider\"><Setter Property=\"Maximum\" Value=\"50\"/></Style>\n" +
            "  </StackPanel.Resources>\n" +
            "  <Slider Minimum=\"3\" Style=\"{StaticResource s}\" />\n" +
            "  <Slider />\n" +
            "</StackPanel>";

        var rule = new XamlRule(
            new XamlElementType("System.Windows.Controls", "Slider", "PresentationFramework"),
            new[] { "Minimum", "Maximum", "NotAProp" },
            _ => true);
        var results = new XamlParser().ParseXaml(xaml, NewResolver(), rule).ToList();

        Assert.Equal(2, results.Count);

        var first = results[0].ExtractedProperties;
        Assert.Equal(new PropertyValue("3", PropertyValueSource.LocalValue), first["Minimum"]);
        Assert.Equal(new PropertyValue("50", PropertyValueSource.StyleSetterValue), first["Maximum"]);
        Assert.Equal(new PropertyValue(null, PropertyValueSource.NotValueFound), first["NotAProp"]);

        var second = results[1].ExtractedProperties;
        Assert.Equal(new PropertyValue("0", PropertyValueSource.DefaultValue), second["Minimum"]);
        Assert.Equal(new PropertyValue("1", PropertyValueSource.DefaultValue), second["Maximum"]);
        Assert.Equal(new PropertyValue(null, PropertyValueSource.NotValueFound), second["NotAProp"]);
    }

    // ---- XamlRuleAnalyzer (Minimum > Maximum) behaviour ----

    // A Slider whose Minimum exceeds its Maximum produces an XAML100 error on the element name.
    [Fact]
    public async Task Reports_Error_When_Minimum_Greater_Than_Maximum()
    {
        var diagnostics = await RunAnalyzerAsync($"<Slider xmlns=\"{PresentationXmlns}\" Minimum=\"5\" Maximum=\"2\" />");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(XamlRuleAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);

        // Squiggle sits on the element name: "<Slider" -> name starts at column 2 (1-based) == 1.
        var lineSpan = diagnostic.Location.GetLineSpan();
        Assert.Equal(new LinePosition(0, 1), lineSpan.StartLinePosition);
        Assert.Equal(new LinePosition(0, 1 + "Slider".Length), lineSpan.EndLinePosition);
    }

    // A valid range produces no diagnostic.
    [Fact]
    public async Task No_Diagnostic_When_Minimum_Not_Greater_Than_Maximum()
    {
        var diagnostics = await RunAnalyzerAsync($"<Slider xmlns=\"{PresentationXmlns}\" Minimum=\"2\" Maximum=\"5\" />");

        Assert.Empty(diagnostics);
    }

    // The check uses resolved values: Minimum from a style (5) vs. Maximum's DP default (1) -> error.
    [Fact]
    public async Task Error_Uses_Resolved_Style_And_Default_Values()
    {
        var diagnostics = await RunAnalyzerAsync(
            $"<StackPanel xmlns=\"{PresentationXmlns}\">\n" +
            "  <StackPanel.Resources><Style TargetType=\"Slider\"><Setter Property=\"Minimum\" Value=\"5\"/></Style></StackPanel.Resources>\n" +
            "  <Slider />\n" +
            "</StackPanel>");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(XamlRuleAnalyzer.DiagnosticId, diagnostic.Id);
    }

    // Non-Slider elements are ignored.
    [Fact]
    public async Task Ignores_NonSlider_Elements()
    {
        var diagnostics = await RunAnalyzerAsync(
            $"<Window xmlns=\"{PresentationXmlns}\"><Button Content=\"Hi\" /></Window>");

        Assert.Empty(diagnostics);
    }

    // ---- helpers ----

    private static IReadOnlyDictionary<string, PropertyValue> ExtractSlider(string xaml)
    {
        var rule = new XamlRule(
            new XamlElementType("System.Windows.Controls", "Slider", "PresentationFramework"),
            new[] { "Minimum", "Maximum" },
            _ => true);
        var results = new XamlParser().ParseXaml(xaml, NewResolver(), rule).ToList();
        return Assert.Single(results).ExtractedProperties;
    }

    private static CompilationXamlTypeResolver NewResolver() =>
        new CompilationXamlTypeResolver(CreateWpfCompilation());

    private static async Task<ImmutableArray<Diagnostic>> RunAnalyzerAsync(string xaml)
    {
        var compilation = CreateWpfCompilation();
        var files = ImmutableArray.Create<AdditionalText>(new InMemoryAdditionalText("Test.xaml", xaml));
        var options = new AnalyzerOptions(files);

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

        // Reference this test assembly so the resolver can find StaticValues ({x:Static} test) and
        // the analyzer can discover TestSliderRulesLoader; reference contracts so its attribute /
        // IXamlRulesLoader symbols resolve.
        references.Add(MetadataReference.CreateFromFile(typeof(XamlRuleAnalyzerTests).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(XamlRule).Assembly.Location));

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

/// <summary>
/// Discovered by the analyzer (it's in this test assembly, which the test compilation references and
/// which references the contracts assembly). Supplies the Slider Minimum &lt;= Maximum rule.
/// </summary>
[XamlRulesLoader]
public sealed class TestSliderRulesLoader : IXamlRulesLoader
{
    public IReadOnlyList<XamlRule> CreateRules() => new[]
    {
        new XamlRule(
            new XamlElementType("System.Windows.Controls", "Slider", "PresentationFramework"),
            new[] { "Minimum", "Maximum" },
            MinimumNotGreaterThanMaximum),
    };

    private static bool MinimumNotGreaterThanMaximum(Dictionary<string, string> props)
    {
        if (TryGetDouble(props, "Minimum", out var min) && TryGetDouble(props, "Maximum", out var max))
            return min <= max;
        return true; // can't determine -> don't flag
    }

    private static bool TryGetDouble(Dictionary<string, string> props, string key, out double value)
    {
        value = 0;
        return props.TryGetValue(key, out var text)
            && double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}
