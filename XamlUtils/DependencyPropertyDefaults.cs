using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using XamlRules;

namespace XamlUtils
{
    /// <summary>
    /// Best-effort, reflection-based lookup of a WPF <c>DependencyProperty</c>'s registered default
    /// value. Used to fill a <see cref="XamlRule.PropsToExtract"/> entry that isn't present in the
    /// markup: if the property is a dependency property we report its default, otherwise nothing.
    /// </summary>
    /// <remarks>
    /// A dependency property's default is supplied at runtime registration, so it is invisible to
    /// Roslyn symbols — the only way to read it is to reflect over the actual type. This therefore
    /// works only where the type's assembly can be loaded into the process (e.g. a Windows/WPF host);
    /// anywhere else it returns false and the property is simply omitted. We never reference WPF
    /// types directly (this library is netstandard2.0), so everything is accessed reflectively.
    /// </remarks>
    internal static class DependencyPropertyDefaults
    {
        // Keyed by assembly|namespace|name|property. A cached null means "no default / not a DP".
        private static readonly ConcurrentDictionary<string, string?> Cache =
            new ConcurrentDictionary<string, string?>();

        public static bool TryGetDefault(XamlElementType type, string propertyName, out string value)
        {
            var key = type.Assembly + "|" + type.Namespace + "|" + type.Name + "|" + propertyName;
            var result = Cache.GetOrAdd(key, _ => Resolve(type, propertyName));
            value = result ?? string.Empty;
            return result is not null;
        }

        private static string? Resolve(XamlElementType type, string propertyName)
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

                // Dependency properties follow the "<Name>Property" static field convention;
                // FlattenHierarchy picks up ones declared on base types (e.g. RangeBase.Minimum).
                var field = clrType.GetField(
                    propertyName + "Property",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                if (field is null || field.FieldType.Name != "DependencyProperty")
                    return null;

                var dependencyProperty = field.GetValue(null);
                if (dependencyProperty is null)
                    return null;

                var metadata = dependencyProperty.GetType()
                    .GetProperty("DefaultMetadata")?.GetValue(dependencyProperty);
                var defaultValue = metadata?.GetType()
                    .GetProperty("DefaultValue")?.GetValue(metadata);

                return defaultValue is null
                    ? null
                    : Convert.ToString(defaultValue, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                // Assembly/type not loadable here (e.g. non-Windows host): treat as "no default".
                return null;
            }
        }
    }
}
