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
