using System;
using System.Collections.Generic;
using System.Reflection;
using Portable.Xaml;
using Portable.Xaml.Schema;

namespace XamlUtils
{
    /// <summary>
    /// An <see cref="IXamlTypeResolver"/> that resolves XAML elements by reflecting over a set of
    /// loaded CLR assemblies via a <see cref="XamlSchemaContext"/>. The schema context reads the
    /// assemblies' <c>XmlnsDefinition</c> attributes to map XAML namespaces (URIs and the
    /// <c>clr-namespace:</c> form) onto CLR types.
    /// </summary>
    /// <remarks>
    /// Use this for standalone/desktop scenarios where the relevant assemblies can be loaded into
    /// the process. It is not appropriate inside a Roslyn analyzer, which inspects another project's
    /// metadata references through symbols rather than loading them as runtime assemblies.
    /// </remarks>
    public sealed class ReflectionXamlTypeResolver : IXamlTypeResolver
    {
        private readonly XamlSchemaContext _schemaContext;

        public ReflectionXamlTypeResolver(IEnumerable<Assembly> assemblies)
        {
            _schemaContext = new XamlSchemaContext(assemblies);
        }

        /// <summary>
        /// Builds a resolver scoped to the assemblies named by the rules, loading each one.
        /// Assemblies that fail to load are skipped — rules pointing at them simply won't match.
        /// </summary>
        public static ReflectionXamlTypeResolver ForRules(XamlRule[] rulesToCheck)
        {
            var assemblies = new List<Assembly>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var rule in rulesToCheck ?? Array.Empty<XamlRule>())
            {
                var name = rule.TypeInfo.Assembly;
                if (string.IsNullOrEmpty(name) || !seen.Add(name))
                    continue;

                try
                {
                    assemblies.Add(Assembly.Load(new AssemblyName(name)));
                }
                catch (Exception)
                {
                    // Unresolvable assembly: ignore so the rest of the rules still work.
                }
            }

            return new ReflectionXamlTypeResolver(assemblies);
        }

        public XamlElementType? Resolve(string xamlNamespace, string localName)
        {
            var xamlType = _schemaContext.GetXamlType(new XamlTypeName(xamlNamespace, localName));
            var clrType = xamlType?.UnderlyingType;
            if (clrType is null)
                return null;

            return new XamlElementType(
                clrType.Namespace ?? string.Empty,
                clrType.Name,
                clrType.Assembly.GetName().Name ?? string.Empty);
        }
    }
}
