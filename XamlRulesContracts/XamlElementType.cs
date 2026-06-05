namespace XamlRules
{
    /// <summary>
    /// Identifies a CLR type by namespace, name and (simple) assembly name — the criteria used to
    /// match a XAML element to a <see cref="XamlRule"/>.
    /// </summary>
    public record XamlElementType(string Namespace, string Name, string Assembly);
}
