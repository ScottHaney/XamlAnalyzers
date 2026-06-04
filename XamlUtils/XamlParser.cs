using System;
using System.Collections.Generic;
using System.IO;
using Portable.Xaml;

namespace XamlUtils
{
    public class XamlParser
    {
        // The XAML language namespace (the "x:" prefix), where StaticExtension (x:Static) lives.
        private const string XamlLanguageNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

        /// <summary>
        /// Value placed in <see cref="RuleViolationResult.ExtractedProperties"/> for a
        /// {StaticResource} whose key can't be found in an ancestor's resources, or whose resource is
        /// not a primitive int/double/string.
        /// </summary>
        public const string InvalidResourceValue = "_INVALID_VALUE_";


        /// <summary>
        /// Convenience overload that resolves XAML elements to CLR types by reflecting over the
        /// assemblies named by the rules. Suitable for standalone/desktop usage where those
        /// assemblies can be loaded into the process. Inside a Roslyn analyzer use the overload
        /// that takes an <see cref="IXamlTypeResolver"/> backed by the compilation's symbols.
        /// </summary>
        public IEnumerable<RuleViolationResult> ParseXaml(string xaml, params XamlRule[] rulesToCheck)
            => ParseXaml(xaml, ReflectionXamlTypeResolver.ForRules(rulesToCheck), rulesToCheck);

        /// <summary>
        /// Parses the XAML content and reports a <see cref="RuleViolationResult"/> for every
        /// element whose resolved CLR type matches a rule's <see cref="XamlRule.TypeInfo"/>.
        /// </summary>
        /// <remarks>
        /// The XAML is read structurally with <see cref="XamlXmlReader"/> — a sequential node
        /// stream (StartObject/EndObject for elements, StartMember/EndMember/Value for
        /// properties). Because the reader works at the XAML object-model level it surfaces both
        /// ways of writing a property identically:
        ///   - inline:           &lt;Slider Minimum="0" /&gt;
        ///   - property element:  &lt;Slider&gt;&lt;Slider.Minimum&gt;0&lt;/Slider.Minimum&gt;&lt;/Slider&gt;
        /// No schema context is used, so elements need not be resolvable for reading — the
        /// reader still yields each element's XAML namespace, local name, members and line info.
        /// Mapping that (namespace, name) to a CLR identity is delegated to
        /// <paramref name="typeResolver"/>, which is what makes the matching environment-agnostic.
        /// </remarks>
        public IEnumerable<RuleViolationResult> ParseXaml(
            string xaml,
            IXamlTypeResolver typeResolver,
            params XamlRule[] rulesToCheck)
        {
            var results = new List<RuleViolationResult>();
            if (rulesToCheck is null || rulesToCheck.Length == 0)
                return results;

            // StringReader avoids the cost of encoding the text to bytes for a MemoryStream;
            // XamlXmlReader reads straight from the TextReader.
            using var textReader = new StringReader(xaml);
            using var reader = new XamlXmlReader(
                textReader,
                new NonLoadingSchemaContext(),
                new XamlXmlReaderSettings { ProvideLineInfo = true });
            var lineInfo = (IXamlLineInfo)reader;

            // One frame per open element. The frame on top of the stack always owns the
            // member/value currently being read, so we can capture an element's *direct*
            // members (the PropsToExtract values) without confusing them with nested children.
            var objectStack = new Stack<ElementFrame>();
            var memberStack = new Stack<string>();

            // Accumulated XAML-namespace-prefix -> namespace declarations, used to resolve the
            // "prefix:" of an {x:Static prefix:Type.Member} expression. Declarations always precede
            // their use, so a flat map (latest wins) is sufficient for this best-effort lookup.
            var namespaces = new Dictionary<string, string>(StringComparer.Ordinal);

            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XamlNodeType.NamespaceDeclaration:
                    {
                        var declaration = reader.Namespace;
                        if (declaration?.Prefix is not null && declaration.Namespace is not null)
                            namespaces[declaration.Prefix] = declaration.Namespace;
                        break;
                    }

                    case XamlNodeType.StartObject:
                    {
                        var xamlType = reader.Type;

                        // Markup extensions that supply a member value: {x:Static ...} and
                        // {StaticResource ...}. When one is the value of a member we want to extract,
                        // remember which element/member it should populate (resolved at its Value).
                        var markupKind = MarkupKindOf(xamlType);
                        ElementFrame? markupTargetFrame = null;
                        string? markupTargetMember = null;
                        if (markupKind != MarkupKind.None && objectStack.Count > 0 && memberStack.Count > 0)
                        {
                            var parentFrame = objectStack.Peek();
                            var parentMember = memberStack.Peek();
                            if (parentFrame.WantedProps is not null && parentFrame.WantedProps.Contains(parentMember))
                            {
                                markupTargetFrame = parentFrame;
                                markupTargetMember = parentMember;
                            }
                        }

                        // An explicit <ResourceDictionary> written directly inside an
                        // <Element.Resources> is the resource dictionary for that element.
                        ElementFrame? resourceDictOwner = null;
                        if (objectStack.Count > 0 && memberStack.Count > 0 && memberStack.Peek() == ResourcesMember)
                            resourceDictOwner = objectStack.Peek();

                        // An object sitting directly inside a resource dictionary is a resource entry
                        // belonging to that dictionary's owner element.
                        var resourceEntryOwner = objectStack.Count > 0 ? objectStack.Peek().ResourceDictOwner : null;

                        var clrType = xamlType is null
                            ? null
                            : typeResolver.Resolve(xamlType.PreferredXamlNamespace ?? string.Empty, xamlType.Name);

                        List<XamlRule>? matched = null;
                        HashSet<string>? wanted = null;
                        if (clrType is not null)
                        {
                            foreach (var rule in rulesToCheck)
                            {
                                // XamlElementType is a record: this compares Namespace, Name and
                                // Assembly by value, which is exactly the match criteria.
                                if (rule.TypeInfo != clrType)
                                    continue;

                                (matched ??= new List<XamlRule>()).Add(rule);
                                foreach (var prop in rule.PropsToExtract)
                                    (wanted ??= new HashSet<string>(StringComparer.Ordinal)).Add(prop);
                            }
                        }

                        // IXamlLineInfo, on a StartObject, points at the first character of the
                        // element's name (the char right after '<') — where the squiggle starts.
                        objectStack.Push(new ElementFrame(
                            matched,
                            wanted,
                            lineInfo.LineNumber,
                            lineInfo.LinePosition,
                            xamlType?.Name ?? string.Empty)
                        {
                            MarkupKind = markupKind,
                            MarkupTargetFrame = markupTargetFrame,
                            MarkupTargetMember = markupTargetMember,
                            ResourceDictOwner = resourceDictOwner,
                            ResourceEntryOwner = resourceEntryOwner,
                            ResourceEntryIsPrimitive = resourceEntryOwner is not null && IsPrimitiveResourceType(xamlType),
                        });
                        break;
                    }

                    case XamlNodeType.GetObject:
                    {
                        // A "get" object — e.g. the implicit ResourceDictionary behind
                        // <Element.Resources>. If it backs a Resources member, record the element
                        // that owns it so the entries inside can be attributed to that element.
                        var getFrame = new ElementFrame(null, null, 0, 0, string.Empty);
                        if (objectStack.Count > 0 && memberStack.Count > 0 && memberStack.Peek() == ResourcesMember)
                            getFrame.ResourceDictOwner = objectStack.Peek();
                        objectStack.Push(getFrame);
                        break;
                    }

                    case XamlNodeType.StartMember:
                        memberStack.Push(reader.Member?.Name ?? string.Empty);
                        break;

                    case XamlNodeType.Value:
                    {
                        // A value belongs to the current member of the current (top) element.
                        if (objectStack.Count > 0 && memberStack.Count > 0)
                        {
                            var frame = objectStack.Peek();
                            var member = memberStack.Peek();

                            if (frame.MarkupKind != MarkupKind.None
                                && frame.MarkupTargetFrame is not null
                                && frame.MarkupTargetMember is not null
                                && (member == "_PositionalParameters" || member == "Member")
                                && reader.Value is string markupArgument)
                            {
                                if (frame.MarkupKind == MarkupKind.XStatic)
                                {
                                    // {x:Static}: evaluate the static member. On failure leave the
                                    // member to its other fallbacks (DP default, etc.).
                                    if (StaticMemberValues.TryResolve(typeResolver, namespaces, markupArgument, out var staticValue))
                                        frame.MarkupTargetFrame.Captured[frame.MarkupTargetMember] = staticValue;
                                }
                                else // StaticResource
                                {
                                    // {StaticResource}: search the ancestor chain for the keyed
                                    // resource. A miss or a non-primitive value yields the marker.
                                    frame.MarkupTargetFrame.Captured[frame.MarkupTargetMember] =
                                        ResolveStaticResource(objectStack, markupArgument);
                                }
                            }
                            else if (frame.ResourceEntryOwner is not null && reader.Value is not null)
                            {
                                // Collecting a resource entry's x:Key and its primitive text value.
                                if (member == "Key")
                                    frame.ResourceEntryKey = reader.Value.ToString();
                                else if (member == "_Initialization")
                                    frame.ResourceEntryValue = reader.Value.ToString();
                            }
                            else if (frame.WantedProps is not null
                                && frame.WantedProps.Contains(member)
                                && reader.Value is not null)
                            {
                                frame.Captured[member] = reader.Value.ToString() ?? string.Empty;
                            }
                        }
                        break;
                    }

                    case XamlNodeType.EndMember:
                        if (memberStack.Count > 0)
                            memberStack.Pop();
                        break;

                    case XamlNodeType.EndObject:
                    {
                        if (objectStack.Count == 0)
                            break;

                        var frame = objectStack.Pop();

                        // Commit a finished resource entry to its owning element's resources, so it
                        // is visible to {StaticResource} lookups from that element and its descendants
                        // (and goes out of scope now that this owner-or-deeper scope is closing).
                        if (frame.ResourceEntryOwner is not null && frame.ResourceEntryKey is not null)
                        {
                            var owner = frame.ResourceEntryOwner;
                            owner.Resources ??= new Dictionary<string, ResourceEntry>(StringComparer.Ordinal);
                            owner.Resources[frame.ResourceEntryKey] =
                                new ResourceEntry(frame.ResourceEntryIsPrimitive, frame.ResourceEntryValue);
                        }

                        if (frame.MatchedRules is null)
                            break;

                        var location = new XamlRuleFailureLocation(
                            frame.LineNumber, frame.LinePosition, frame.ElementName);

                        foreach (var rule in frame.MatchedRules)
                        {
                            // Each result carries only the props that rule asked for. A prop that
                            // isn't present in the markup falls back to its dependency-property
                            // default (when the type is loadable and it is a DP); otherwise it's
                            // omitted.
                            var props = new Dictionary<string, string>(StringComparer.Ordinal);
                            foreach (var name in rule.PropsToExtract)
                            {
                                if (frame.Captured.TryGetValue(name, out var value))
                                    props[name] = value;
                                else if (DependencyPropertyDefaults.TryGetDefault(rule.TypeInfo, name, out var defaultValue))
                                    props[name] = defaultValue;
                            }

                            results.Add(new RuleViolationResult(rule, location, props));
                        }
                        break;
                    }
                }
            }

            return results;
        }

        /// <summary>The XAML member exposing a WPF resource dictionary (<c>FrameworkElement.Resources</c>).</summary>
        private const string ResourcesMember = "Resources";

        private static MarkupKind MarkupKindOf(XamlType? type)
        {
            if (type is null)
                return MarkupKind.None;
            if (type.Name == "StaticExtension" && type.PreferredXamlNamespace == XamlLanguageNamespace)
                return MarkupKind.XStatic;
            if (type.Name == "StaticResource" || type.Name == "StaticResourceExtension")
                return MarkupKind.StaticResource;
            return MarkupKind.None;
        }

        /// <summary>True for the primitive resource types we know how to stringify: int, double, string.</summary>
        private static bool IsPrimitiveResourceType(XamlType? type)
        {
            switch (type?.Name)
            {
                case "Int32":
                case "Double":
                case "String":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Resolves a {StaticResource} by walking the open element scopes from nearest ancestor to
        /// the root (anything not an ancestor has already been popped, so isn't visible). Returns the
        /// primitive resource's string value, or <see cref="InvalidResourceValue"/> when the key is
        /// not found or the resource isn't a primitive int/double/string.
        /// </summary>
        private static string ResolveStaticResource(Stack<ElementFrame> objectStack, string key)
        {
            foreach (var frame in objectStack)
            {
                if (frame.Resources is not null && frame.Resources.TryGetValue(key, out var entry))
                    return entry.IsPrimitive && entry.Value is not null ? entry.Value : InvalidResourceValue;
            }
            return InvalidResourceValue;
        }

        private enum MarkupKind
        {
            None,
            XStatic,
            StaticResource,
        }

        /// <summary>A resource collected from an <c>&lt;Element.Resources&gt;</c> section.</summary>
        private readonly struct ResourceEntry
        {
            public ResourceEntry(bool isPrimitive, string? value)
            {
                IsPrimitive = isPrimitive;
                Value = value;
            }

            /// <summary>True when the resource is an int/double/string (so <see cref="Value"/> is usable).</summary>
            public bool IsPrimitive { get; }

            /// <summary>The primitive resource's text value, if any.</summary>
            public string? Value { get; }
        }

        /// <summary>
        /// Tracks the state of one open XAML element while streaming: which rules it matched, which
        /// member values to capture, where its name sits, captured values, resource scope, and any
        /// markup-extension/resource-entry bookkeeping.
        /// </summary>
        private sealed class ElementFrame
        {
            public ElementFrame(
                List<XamlRule>? matchedRules,
                HashSet<string>? wantedProps,
                int lineNumber,
                int linePosition,
                string elementName)
            {
                MatchedRules = matchedRules;
                WantedProps = wantedProps;
                LineNumber = lineNumber;
                LinePosition = linePosition;
                ElementName = elementName;
            }

            public List<XamlRule>? MatchedRules { get; }
            public HashSet<string>? WantedProps { get; }
            public Dictionary<string, string> Captured { get; } = new(StringComparer.Ordinal);
            public int LineNumber { get; }
            public int LinePosition { get; }
            public string ElementName { get; }

            // When this frame is an {x:Static}/{StaticResource} markup extension supplying the value
            // of a wanted member, these identify the element/member whose value it should populate.
            public MarkupKind MarkupKind { get; set; }
            public ElementFrame? MarkupTargetFrame { get; set; }
            public string? MarkupTargetMember { get; set; }

            // Resources declared in this element's <Element.Resources>, keyed by x:Key. Only set for
            // elements that actually have a resources section; in scope until this frame is popped.
            public Dictionary<string, ResourceEntry>? Resources { get; set; }

            // Set on a resource-dictionary scope: the element that owns it.
            public ElementFrame? ResourceDictOwner { get; set; }

            // Set on a resource-entry object while it is being read, then committed to the owner.
            public ElementFrame? ResourceEntryOwner { get; set; }
            public bool ResourceEntryIsPrimitive { get; set; }
            public string? ResourceEntryKey { get; set; }
            public string? ResourceEntryValue { get; set; }
        }
    }

    /// <summary>
    /// Maps a XAML element — identified by its XAML namespace and local name — to the CLR type
    /// identity it resolves to, or <c>null</c> when it cannot be resolved. Implementations decide
    /// the resolution strategy: reflection over loaded assemblies, Roslyn compilation symbols, etc.
    /// </summary>
    public interface IXamlTypeResolver
    {
        /// <param name="xamlNamespace">
        /// The element's XAML namespace, either a URI (e.g. the WPF presentation namespace) or the
        /// <c>clr-namespace:Some.Ns;assembly=Some.Assembly</c> form.
        /// </param>
        /// <param name="localName">The element's local name (e.g. <c>Slider</c>).</param>
        XamlElementType? Resolve(string xamlNamespace, string localName);
    }

    /// <summary>
    /// Represents the result of a rule violation found during XAML parsing.
    /// </summary>
    /// <param name="FailedRule"></param>
    /// <param name="Location">This is the file location information needed for the roslyn analyzer to add a red squiggle. The red squiggle should go underneath the name of the XamlElement matched by the FailedRule</param>
    /// <param name="ExtractedProperties">The values of the matched element's members named in <see cref="XamlRule.PropsToExtract"/>, keyed by member name. Members that were not present on the element are omitted.</param>
    public record RuleViolationResult(
        XamlRule FailedRule,
        IXamlRuleFailureLocation Location,
        IReadOnlyDictionary<string, string> ExtractedProperties);

    /// <summary>
    /// Location information for where to start and end the red squiggle in the XAML file for a rule violation. This is needed for the roslyn analyzer to add a red squiggle underneath the name of the XamlElement matched by the FailedRule
    /// </summary>
    public interface IXamlRuleFailureLocation
    {
        /// <summary>1-based line of the matched element's name.</summary>
        int LineNumber { get; }

        /// <summary>1-based column of the first character of the matched element's name.</summary>
        int LinePosition { get; }

        /// <summary>The element name to underline; its length is the span of the squiggle.</summary>
        string ElementName { get; }
    }

    /// <summary>
    /// Concrete <see cref="IXamlRuleFailureLocation"/> produced by <see cref="XamlParser"/>.
    /// </summary>
    public sealed record XamlRuleFailureLocation(int LineNumber, int LinePosition, string ElementName)
        : IXamlRuleFailureLocation;
}
