using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using WpfCustomAnalyzer;
using Xunit;

namespace WpfCustomAnalyzer.Tests;

public class XamlMinAttributeAnalyzerTests
{
    // Pass: Min is non-zero, so no diagnostic should fire
    [Fact]
    public async Task NoDiagnostic_WhenMinAttributeIsNotZero()
    {
        var test = new CSharpAnalyzerTest<XamlMinAttributeAnalyzer, DefaultVerifier>
        {
            TestState =
            {
                Sources = { "class C { }" },
                AdditionalFiles =
                {
                    ("Test.xaml", "<Slider Min=\"5\" Max=\"10\" />")
                }
            }
        };

        await test.RunAsync();
    }

    // Pass: no Min attribute at all, so no diagnostic should fire
    [Fact]
    public async Task NoDiagnostic_WhenNoMinAttribute()
    {
        var test = new CSharpAnalyzerTest<XamlMinAttributeAnalyzer, DefaultVerifier>
        {
            TestState =
            {
                Sources = { "class C { }" },
                AdditionalFiles =
                {
                    ("Test.xaml", "<Slider Max=\"10\" />")
                }
            }
        };

        await test.RunAsync();
    }

    // Fail: Min="0" must produce XAML001 build error
    // XAML: <Slider Min="0" />
    //        12345678901234567
    // "Min="0"" starts at column 9, length 7, ends at column 16
    [Fact]
    public async Task Diagnostic_WhenMinAttributeIsZero()
    {
        var test = new CSharpAnalyzerTest<XamlMinAttributeAnalyzer, DefaultVerifier>
        {
            TestState =
            {
                Sources = { "class C { }" },
                AdditionalFiles =
                {
                    ("Test.xaml", "<Slider Min=\"0\" />")
                },
                ExpectedDiagnostics =
                {
                    new DiagnosticResult(XamlMinAttributeAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
                        .WithSpan("Test.xaml", 1, 9, 1, 16)
                        .WithArguments("Slider")
                }
            }
        };

        await test.RunAsync();
    }
}
