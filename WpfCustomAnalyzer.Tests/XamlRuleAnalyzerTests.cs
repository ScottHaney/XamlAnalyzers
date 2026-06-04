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
