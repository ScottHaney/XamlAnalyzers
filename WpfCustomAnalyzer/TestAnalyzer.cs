using System;
using System.Collections.Immutable;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace WpfCustomAnalyzer
{
    [DiagnosticAnalyzer(LanguageNames.CSharp, LanguageNames.VisualBasic)]
    public class XamlMinAttributeAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "XAML001";

        internal static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId,
            title: "XAML element has Min=\"0\"",
            messageFormat: "Element '{0}' has Min=\"0\" which is not allowed",
            category: "XAML",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "XAML elements must not have a Min attribute set to 0.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationAction(compilationContext =>
            {
                foreach (var additionalFile in compilationContext.Options.AdditionalFiles)
                {
                    if (!additionalFile.Path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var sourceText = additionalFile.GetText(compilationContext.CancellationToken);
                    if (sourceText == null)
                        continue;

                    AnalyzeXamlFile(compilationContext, additionalFile.Path, sourceText);
                }
            });
        }

        private static void AnalyzeXamlFile(
            CompilationAnalysisContext context,
            string filePath,
            SourceText sourceText)
        {
            XDocument doc;
            try
            {
                doc = XDocument.Parse(sourceText.ToString(), LoadOptions.SetLineInfo);
            }
            catch (XmlException)
            {
                return;
            }

            foreach (var element in doc.Descendants())
            {
                var minAttr = element.Attribute("Min");
                if (minAttr == null || minAttr.Value != "0")
                    continue;

                var lineInfo = (IXmlLineInfo)minAttr;
                if (!lineInfo.HasLineInfo())
                    continue;

                // IXmlLineInfo is 1-based; LinePosition uses 0-based
                var line = lineInfo.LineNumber - 1;
                var col = lineInfo.LinePosition - 1;
                var attrText = minAttr.ToString(); // e.g. Min="0"

                var textLine = sourceText.Lines[line];
                var spanStart = textLine.Start + col;
                var textSpan = TextSpan.FromBounds(spanStart, spanStart + attrText.Length);

                var linePositionSpan = new LinePositionSpan(
                    new LinePosition(line, col),
                    new LinePosition(line, col + attrText.Length));

                var location = Location.Create(filePath, textSpan, linePositionSpan);
                context.ReportDiagnostic(Diagnostic.Create(Rule, location, element.Name.LocalName));
            }
        }
    }
}
