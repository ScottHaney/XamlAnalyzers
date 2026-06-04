using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace XamlUtils
{
    /// <summary>
    /// Best-effort, reflection-based evaluation of an <c>{x:Static prefix:Type.Member}</c> markup
    /// extension: it resolves the referenced static field or property and returns its value as a
    /// string, the same way the x:Static markup extension would at runtime.
    /// </summary>
    /// <remarks>
    /// The type is resolved through the supplied <see cref="IXamlTypeResolver"/> (so it works for
    /// both URI and <c>clr-namespace:</c> namespaces, via symbols inside an analyzer), then the
    /// member's value is read reflectively — which requires the type's assembly to be loadable in
    /// the process. Where it isn't (e.g. a non-Windows host, or the user's own not-yet-built
    /// assembly) this returns false and the property is left to its other fallbacks.
    /// </remarks>
    internal static class StaticMemberValues
    {
        // Keyed by assembly|namespace|type|member. A cached null means "couldn't evaluate".
        private static readonly ConcurrentDictionary<string, string?> Cache =
            new ConcurrentDictionary<string, string?>();

        /// <param name="prefixToXmlns">Current XAML-namespace-prefix → namespace declarations.</param>
        /// <param name="memberExpression">The x:Static argument, e.g. <c>local:MyClass.Value</c>.</param>
        public static bool TryResolve(
            IXamlTypeResolver typeResolver,
            IReadOnlyDictionary<string, string> prefixToXmlns,
            string memberExpression,
            out string value)
        {
            value = string.Empty;
            if (string.IsNullOrEmpty(memberExpression))
                return false;

            // Optional "prefix:" in front of "Type.Member".
            var prefix = string.Empty;
            var rest = memberExpression;
            var colon = memberExpression.IndexOf(':');
            if (colon >= 0)
            {
                prefix = memberExpression.Substring(0, colon);
                rest = memberExpression.Substring(colon + 1);
            }

            // "Type.Member" splits on the last dot.
            var dot = rest.LastIndexOf('.');
            if (dot <= 0 || dot >= rest.Length - 1)
                return false;
            var typeName = rest.Substring(0, dot);
            var memberName = rest.Substring(dot + 1);

            if (!prefixToXmlns.TryGetValue(prefix, out var xmlns))
                return false;

            var resolvedType = typeResolver.Resolve(xmlns, typeName);
            if (resolvedType is null)
                return false;

            var key = resolvedType.Assembly + "|" + resolvedType.Namespace + "|" + resolvedType.Name + "|" + memberName;
            var result = Cache.GetOrAdd(key, _ => ResolveStaticValue(resolvedType, memberName));
            value = result ?? string.Empty;
            return result is not null;
        }

        private static string? ResolveStaticValue(XamlElementType type, string memberName)
        {
            try
            {
                var assembly = Assembly.Load(new AssemblyName(type.Assembly));
                var fullName = string.IsNullOrEmpty(type.Namespace)
                    ? type.Name
                    : type.Namespace + "." + type.Name;
                var clrType = assembly.GetType(fullName);
                if (clrType is null)
                    return null;

                const BindingFlags flags =
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;

                // x:Static targets a static/const field or a static property (incl. enum members).
                object? raw;
                var field = clrType.GetField(memberName, flags);
                if (field is not null)
                {
                    raw = field.GetValue(null);
                }
                else
                {
                    var property = clrType.GetProperty(memberName, flags);
                    if (property is null || !property.CanRead)
                        return null;
                    raw = property.GetValue(null);
                }

                return raw is null ? null : Convert.ToString(raw, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
