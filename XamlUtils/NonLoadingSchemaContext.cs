using System.Reflection;
using Portable.Xaml;

namespace XamlUtils
{
    /// <summary>
    /// A <see cref="XamlSchemaContext"/> that never loads assemblies. Portable.Xaml otherwise calls
    /// <c>Assembly.Load</c> while reading <c>clr-namespace:...;assembly=...</c> elements and throws
    /// if the assembly isn't present in the process. Since <see cref="XamlParser"/> only reads
    /// structure (and resolves types separately via an <see cref="IXamlTypeResolver"/>), we suppress
    /// that load so unknown/unavailable elements degrade to "unknown" instead of crashing the read.
    /// </summary>
    internal sealed class NonLoadingSchemaContext : XamlSchemaContext
    {
        protected override Assembly OnAssemblyResolve(string assemblyName) => null!;
    }
}
