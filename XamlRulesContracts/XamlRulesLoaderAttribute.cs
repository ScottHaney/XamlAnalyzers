using System;

namespace XamlRules
{
    /// <summary>
    /// Marks a class (implementing <see cref="IXamlRulesLoader"/>) that the analyzer should discover
    /// and use to obtain its <see cref="XamlRule"/>s.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class XamlRulesLoaderAttribute : Attribute
    {
    }
}
