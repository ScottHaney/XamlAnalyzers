#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using XamlUtils;

namespace WpfCustomAnalyzer
{
    /// <summary>
    /// An <see cref="IXamlTypeResolver"/> that maps XAML elements to CLR type identities using a
    /// Roslyn <see cref="Compilation"/> rather than loaded assemblies. It reads the
    /// <c>XmlnsDefinition</c> attributes off the compilation's referenced assemblies (as symbols /
    /// metadata) to map XAML namespace URIs to CLR namespaces, and resolves the
    /// <c>clr-namespace:</c> form directly. This is the resolution strategy appropriate inside an
    /// analyzer, which sees other projects' references as metadata, not as runtime assemblies.
    /// </summary>
    public sealed class CompilationXamlTypeResolver : IXamlTypeResolver
    {
        private readonly IAssemblySymbol _self;
        private readonly Dictionary<string, IAssemblySymbol> _assembliesByName =
            new(StringComparer.OrdinalIgnoreCase);

        // XAML namespace URI -> the (CLR namespace, declaring assembly) pairs it maps to.
        private readonly Dictionary<string, List<(string clrNamespace, IAssemblySymbol assembly)>> _xmlnsMap =
            new(StringComparer.Ordinal);

        public CompilationXamlTypeResolver(Compilation compilation)
        {
            _self = compilation.Assembly;

            var assemblies = new List<IAssemblySymbol> { _self };
            foreach (var reference in compilation.References)
            {
                if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly)
                    assemblies.Add(assembly);
            }

            foreach (var assembly in assemblies)
                _assembliesByName[assembly.Identity.Name] = assembly;

            foreach (var assembly in assemblies)
                IndexXmlnsDefinitions(assembly);
        }

        private void IndexXmlnsDefinitions(IAssemblySymbol assembly)
        {
            foreach (var attribute in assembly.GetAttributes())
            {
                if (attribute.AttributeClass?.Name != "XmlnsDefinitionAttribute")
                    continue;
                if (attribute.ConstructorArguments.Length < 2)
                    continue;

                // [XmlnsDefinition(xmlNamespace, clrNamespace, AssemblyName = "...")]
                if (attribute.ConstructorArguments[0].Value is not string xmlNamespace ||
                    attribute.ConstructorArguments[1].Value is not string clrNamespace)
                    continue;

                // By default the types live in the declaring assembly; AssemblyName redirects them.
                var target = assembly;
                foreach (var named in attribute.NamedArguments)
                {
                    if (named.Key == "AssemblyName"
                        && named.Value.Value is string assemblyName
                        && _assembliesByName.TryGetValue(assemblyName, out var redirected))
                    {
                        target = redirected;
                    }
                }

                if (!_xmlnsMap.TryGetValue(xmlNamespace, out var list))
                    _xmlnsMap[xmlNamespace] = list = new List<(string, IAssemblySymbol)>();
                list.Add((clrNamespace, target));
            }
        }

        public XamlElementType? Resolve(string xamlNamespace, string localName)
        {
            if (xamlNamespace.StartsWith("clr-namespace:", StringComparison.Ordinal))
            {
                var (clrNamespace, assemblyName) = ParseClrNamespace(xamlNamespace);
                var assembly = assemblyName is null
                    ? _self
                    : (_assembliesByName.TryGetValue(assemblyName, out var a) ? a : null);
                return assembly is null ? null : Find(assembly, clrNamespace, localName);
            }

            if (_xmlnsMap.TryGetValue(xamlNamespace, out var candidates))
            {
                foreach (var (clrNamespace, assembly) in candidates)
                {
                    var hit = Find(assembly, clrNamespace, localName);
                    if (hit is not null)
                        return hit;
                }
            }

            return null;
        }

        private static XamlElementType? Find(IAssemblySymbol assembly, string clrNamespace, string localName)
        {
            var metadataName = string.IsNullOrEmpty(clrNamespace) ? localName : clrNamespace + "." + localName;
            var type = assembly.GetTypeByMetadataName(metadataName);
            return type is null
                ? null
                : new XamlElementType(clrNamespace, localName, assembly.Identity.Name);
        }

        private static (string clrNamespace, string? assemblyName) ParseClrNamespace(string xamlNamespace)
        {
            // clr-namespace:Some.Namespace;assembly=Some.Assembly  (assembly part optional)
            var body = xamlNamespace.Substring("clr-namespace:".Length);
            var parts = body.Split(';');
            var clrNamespace = parts[0];
            string? assemblyName = null;
            for (var i = 1; i < parts.Length; i++)
            {
                if (parts[i].StartsWith("assembly=", StringComparison.Ordinal))
                    assemblyName = parts[i].Substring("assembly=".Length);
            }
            return (clrNamespace, assemblyName);
        }
    }
}
