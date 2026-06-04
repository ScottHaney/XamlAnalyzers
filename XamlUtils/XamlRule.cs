namespace XamlUtils
{
    public record XamlRule(XamlElementType TypeInfo, string[] PropsToExtract);

    public record XamlElementType(string Namespace, string Name, string Assembly);
}
