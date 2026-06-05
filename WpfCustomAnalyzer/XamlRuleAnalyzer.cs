#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using XamlRules;
using XamlUtils;

namespace WpfCustomAnalyzer
{
    /// <summary>
    /// Enforces XAML rules supplied by the project under analysis. The rules come from classes marked
    /// with <see cref="XamlRulesLoaderAttribute"/> (implementing <see cref="IXamlRulesLoader"/>) in a
    /// referenced (build-time) assembly: the analyzer discovers them via the compilation's symbols,
    /// loads the assembly, calls <see cref="IXamlRulesLoader.CreateRules"/>, then for each matched
    /// element resolves the rule's <see cref="XamlRule.PropsToExtract"/> with the full
    /// <see cref="XamlParser"/> pipeline and reports when the rule's <see cref="XamlRule.Validate"/>
    /// returns <c>false</c>.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class XamlRuleAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "XAML100";

        private const string ContractsAssemblyName = "XamlRulesContracts";
        private const string LoaderAttributeMetadataName = "XamlRules.XamlRulesLoaderAttribute";

        internal static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId,
            title: "XAML element violates a configured rule",
            messageFormat: "'{0}' violates a XAML rule ({1})",
            category: "XAML",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "Reports XAML elements that fail a rule supplied by an IXamlRulesLoader.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(Rule);

        static XamlRuleAnalyzer()
        {
            // Redirect the contracts dependency of a byte-loaded rules assembly to the copy we
            // already have, so its IXamlRulesLoader/XamlRule are the same types we cast to.
            AppDomain.CurrentDomain.AssemblyResolve += ResolveContracts;
        }

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            // Per-file (live) analysis so diagnostics appear during editing, not just on full build.
            // CompilationStart gives us the Compilation (resolver + rule discovery) once.
            context.RegisterCompilationStartAction(startContext =>
            {
                var resolver = new CompilationXamlTypeResolver(startContext.Compilation);
                var rules = DiscoverRules(startContext.Compilation).ToArray();
                if (rules.Length == 0)
                    return;

                startContext.RegisterAdditionalFileAction(fileContext =>
                    AnalyzeAdditionalFile(fileContext, resolver, rules));
            });
        }

        private static void AnalyzeAdditionalFile(
            AdditionalFileAnalysisContext context,
            IXamlTypeResolver resolver,
            XamlRule[] rules)
        {
            var additionalFile = context.AdditionalFile;
            if (!additionalFile.Path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                return;

            var sourceText = additionalFile.GetText(context.CancellationToken);
            if (sourceText is null)
                return;

            List<RuleViolationResult> matches;
            try
            {
                matches = new XamlParser().ParseXaml(sourceText.ToString(), resolver, rules).ToList();
            }
            catch (Exception)
            {
                // Malformed XAML or a reader error — skip this file rather than crash analysis.
                return;
            }

            foreach (var match in matches)
            {
                // Pass the resolved values (member -> value; unresolved members omitted) to the rule.
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var pair in match.ExtractedProperties)
                {
                    if (pair.Value.Value is not null)
                        values[pair.Key] = pair.Value.Value;
                }

                bool isValid;
                try
                {
                    isValid = match.FailedRule.Validate(values);
                }
                catch (Exception)
                {
                    continue; // a faulty rule predicate shouldn't crash analysis
                }

                if (isValid)
                    continue;

                var location = ToLocation(additionalFile.Path, sourceText, match.Location);
                if (location is null)
                    continue;

                var summary = string.Join(", ", values.Select(v => $"{v.Key}={v.Value}"));
                context.ReportDiagnostic(Diagnostic.Create(Rule, location, match.Location.ElementName, summary));
            }
        }

        // ---- rule discovery / loading ----

        private static IReadOnlyList<XamlRule> DiscoverRules(Compilation compilation)
        {
            var attributeSymbol = compilation.GetTypeByMetadataName(LoaderAttributeMetadataName);
            if (attributeSymbol is null)
                return Array.Empty<XamlRule>();

            var rules = new List<XamlRule>();
            foreach (var reference in compilation.References)
            {
                if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assemblySymbol)
                    continue;
                if (!ReferencesContracts(assemblySymbol))
                    continue;

                var loaderTypes = new List<INamedTypeSymbol>();
                CollectLoaderTypes(assemblySymbol.GlobalNamespace, attributeSymbol, loaderTypes);
                if (loaderTypes.Count == 0)
                    continue;

                var path = (reference as PortableExecutableReference)?.FilePath;
                if (string.IsNullOrEmpty(path))
                    continue;

                var assembly = LoadAssembly(path!);
                if (assembly is null)
                    continue;

                foreach (var loaderType in loaderTypes)
                {
                    try
                    {
                        var type = assembly.GetType(GetMetadataName(loaderType));
                        if (type is null)
                            continue;
                        if (Activator.CreateInstance(type) is IXamlRulesLoader loader)
                        {
                            var created = loader.CreateRules();
                            if (created is not null)
                                rules.AddRange(created);
                        }
                    }
                    catch (Exception)
                    {
                        // Ignore a loader we can't construct/run rather than crash analysis.
                    }
                }
            }

            return rules;
        }

        private static bool ReferencesContracts(IAssemblySymbol assembly)
        {
            foreach (var module in assembly.Modules)
            {
                foreach (var id in module.ReferencedAssemblies)
                {
                    if (string.Equals(id.Name, ContractsAssemblyName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        private static void CollectLoaderTypes(INamespaceSymbol ns, INamedTypeSymbol attribute, List<INamedTypeSymbol> output)
        {
            foreach (var type in ns.GetTypeMembers())
                CollectLoaderTypes(type, attribute, output);
            foreach (var child in ns.GetNamespaceMembers())
                CollectLoaderTypes(child, attribute, output);
        }

        private static void CollectLoaderTypes(INamedTypeSymbol type, INamedTypeSymbol attribute, List<INamedTypeSymbol> output)
        {
            if (type.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attribute)))
                output.Add(type);
            foreach (var nested in type.GetTypeMembers())
                CollectLoaderTypes(nested, attribute, output);
        }

        private static string GetMetadataName(INamedTypeSymbol type)
        {
            var name = type.MetadataName;
            for (var containing = type.ContainingType; containing is not null; containing = containing.ContainingType)
                name = containing.MetadataName + "+" + name;

            var ns = type.ContainingNamespace;
            return ns is null || ns.IsGlobalNamespace ? name : ns.ToDisplayString() + "." + name;
        }

        // Caches the loaded rules assembly per path, keyed by the file's last-write time. This is the
        // crucial bit for correctness: the compiler can be a persistent server (VBCSCompiler), and
        // loading by path/identity would keep serving a STALE rules assembly after it's rebuilt. By
        // keying on the timestamp we reload a fresh copy whenever the rules assembly actually changes.
        private static readonly ConcurrentDictionary<string, (DateTime Stamp, Assembly Assembly)> LoadedAssemblies =
            new ConcurrentDictionary<string, (DateTime, Assembly)>();

        private static Assembly? LoadAssembly(string path)
        {
            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(path); }
            catch (Exception) { stamp = default; }

            if (LoadedAssemblies.TryGetValue(path, out var cached))
            {
                if (cached.Stamp == stamp)
                    return cached.Assembly; // unchanged since we last loaded it
            }
            else
            {
                // First time we see this path: adopt an already-loaded copy from the same file. This
                // is the in-process case (e.g. tests). Byte-loaded copies report no Location, so this
                // only matches genuinely path-loaded assemblies — never a previously byte-loaded one.
                var existing = FindLoadedByPath(path);
                if (existing is not null)
                {
                    LoadedAssemblies[path] = (stamp, existing);
                    return existing;
                }
            }

            // Load a FRESH copy from the current bytes so rule edits take effect even on a persistent
            // build server. The contracts dependency is redirected to our copy by ResolveContracts.
            Assembly? loaded;
            try { loaded = Assembly.Load(File.ReadAllBytes(path)); }
            catch (Exception) { return null; }

            LoadedAssemblies[path] = (stamp, loaded);
            return loaded;
        }

        private static Assembly? FindLoadedByPath(string path)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                    continue;

                string location;
                try { location = assembly.Location; }
                catch (Exception) { continue; }

                if (!string.IsNullOrEmpty(location)
                    && string.Equals(Path.GetFullPath(location), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                {
                    return assembly;
                }
            }
            return null;
        }

        private static Assembly? ResolveContracts(object? sender, ResolveEventArgs args)
        {
            var requested = new AssemblyName(args.Name).Name;
            var contracts = typeof(IXamlRulesLoader).Assembly;
            return string.Equals(requested, contracts.GetName().Name, StringComparison.OrdinalIgnoreCase)
                ? contracts
                : null;
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
