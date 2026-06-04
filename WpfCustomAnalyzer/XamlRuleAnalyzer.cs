#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using XamlUtils;

namespace WpfCustomAnalyzer
{
    /// <summary>
    /// Checks <c>.xaml</c> AdditionalFiles against a set of <see cref="XamlRule"/>s using
    /// <see cref="XamlParser"/>. Element-to-type resolution is done through the compilation's symbols
    /// via <see cref="CompilationXamlTypeResolver"/>, so no assemblies are loaded into the analysis
    /// process. Because XamlUtils uses Portable.Xaml (not System.Xaml) this analyzer is netstandard2.0
    /// and runs live in the IDE.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class XamlRuleAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "XAML100";

        /// <summary>
        /// The rules to enforce. Hard-coded here for now; a real analyzer would likely load these
        /// from configuration or an AdditionalFile.
        /// </summary>
        internal static readonly XamlRule[] Rules =
        {
            new XamlRule(
                new XamlElementType("System.Windows.Controls", "Slider", "PresentationFramework"),
                new[] { "Minimum", "Maximum" })
        };

        internal static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId,
            title: "XAML element matches a restricted rule",
            messageFormat: "Element '{0}' matches a restricted XAML rule{1}",
            category: "XAML",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Reports XAML elements whose resolved CLR type matches a configured rule.",
            customTags: WellKnownDiagnosticTags.CompilationEnd);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationAction(AnalyzeCompilation);
        }

        private static void AnalyzeCompilation(CompilationAnalysisContext context)
        {
            var resolver = new CompilationXamlTypeResolver(context.Compilation);
            var parser = new XamlParser();

            foreach (var additionalFile in context.Options.AdditionalFiles)
            {
                if (!additionalFile.Path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                    continue;

                var sourceText = additionalFile.GetText(context.CancellationToken);
                if (sourceText is null)
                    continue;

                List<RuleViolationResult> violations;
                try
                {
                    violations = parser.ParseXaml(sourceText.ToString(), resolver, Rules).ToList();
                }
                catch (Exception)
                {
                    // Malformed XAML or a reader error — skip this file rather than crash analysis.
                    continue;
                }

                foreach (var violation in violations)
                {
                    var location = ToLocation(additionalFile.Path, sourceText, violation.Location);
                    if (location is null)
                        continue;

                    var props = violation.ExtractedProperties.Count == 0
                        ? string.Empty
                        : " (" + string.Join(", ", violation.ExtractedProperties.Select(kv => $"{kv.Key}={kv.Value.Value} [{kv.Value.Source}]")) + ")";

                    context.ReportDiagnostic(Diagnostic.Create(
                        Rule, location, violation.Location.ElementName, props));
                }
            }
        }

        /// <summary>
        /// Turns the parser's 1-based line/column + element name into a Roslyn <see cref="Location"/>
        /// spanning just the element's name (where the squiggle goes).
        /// </summary>
        private static Location? ToLocation(string path, SourceText text, IXamlRuleFailureLocation location)
        {
            var line = location.LineNumber - 1;   // IXamlLineInfo is 1-based; Roslyn is 0-based.
            var column = location.LinePosition - 1;
            if (line < 0 || line >= text.Lines.Count || column < 0)
                return null;

            var start = text.Lines[line].Start + column;
            var end = start + location.ElementName.Length;
            if (start < 0 || end > text.Length)
                return null;

            var span = TextSpan.FromBounds(start, end);
            var linePositionSpan = new LinePositionSpan(
                new LinePosition(line, column),
                new LinePosition(line, column + location.ElementName.Length));

            return Location.Create(path, span, linePositionSpan);
        }
    }
}
