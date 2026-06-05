using System.Collections.Generic;

namespace XamlRules
{
    /// <summary>
    /// Supplies the set of <see cref="XamlRule"/>s the analyzer should enforce. Implement this on a
    /// class marked with <see cref="XamlRulesLoaderAttribute"/> in a project referenced (build-time)
    /// by the project being analyzed; the analyzer discovers it, instantiates it, and calls
    /// <see cref="CreateRules"/>.
    /// </summary>
    public interface IXamlRulesLoader
    {
        IReadOnlyList<XamlRule> CreateRules();
    }
}
