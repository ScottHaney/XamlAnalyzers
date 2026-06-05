using System;
using System.Collections.Generic;

namespace XamlRules
{
    /// <summary>
    /// A rule the analyzer enforces against XAML elements of a given type.
    /// </summary>
    /// <param name="TypeInfo">The element type the rule applies to.</param>
    /// <param name="PropsToExtract">
    /// The member names whose resolved values are gathered and passed to <paramref name="Validate"/>.
    /// </param>
    /// <param name="Validate">
    /// Given the extracted property values (member name → resolved value string; members that could
    /// not be resolved are omitted), returns <c>true</c> when the element is valid and <c>false</c>
    /// when it violates the rule (which the analyzer then reports).
    /// </param>
    public record XamlRule(
        XamlElementType TypeInfo,
        string[] PropsToExtract,
        Func<Dictionary<string, string>, bool> Validate);
}
