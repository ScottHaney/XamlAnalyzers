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

        // XAML member / type names we special-case while streaming.
        private const string ResourcesMember = "Resources";
        private const string StyleMember = "Style";
        private const string StyleObjectName = "Style";
        private const string SetterObjectName = "Setter";
        private const string TypeExtensionName = "TypeExtension";
        private const string TargetTypeMember = "TargetType";
        private const string BasedOnMember = "BasedOn";
        private const string KeyMember = "Key";
        private const string PropertyMember = "Property";
        private const string ValueMember = "Value";
        private const string PositionalParametersMember = "_PositionalParameters";
        private const string InitializationMember = "_Initialization";

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
        /// properties). No schema context is used, so elements need not be resolvable for reading;
        /// mapping (namespace, name) to a CLR identity is delegated to <paramref name="typeResolver"/>.
        /// For each matched element a property is resolved in this order:
        ///   1. a value set directly on the element (literal, {x:Static}, or {StaticResource});
        ///   2. a Style setter (the element's inline style, Style="{StaticResource}", or — when no
        ///      Style is set directly — an implicit keyless style matched by TargetType), following
        ///      the BasedOn chain with nearer styles overriding their bases;
        ///   3. the dependency-property default (via reflection), if available.
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

            // One frame per open object. The frame on top of the stack owns the member/value being
            // read; ancestor frames (still on the stack) hold their in-scope resources and styles.
            var objectStack = new Stack<ElementFrame>();
            var memberStack = new Stack<string>();

            // Accumulated XAML-namespace-prefix -> namespace declarations, used to resolve the
            // "prefix:" of {x:Static prefix:Type.Member} and a style's TargetType. Declarations
            // always precede their use, so a flat map (latest wins) is sufficient.
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
                        var typeName = xamlType?.Name ?? string.Empty;
                        var parentFrame = objectStack.Count > 0 ? objectStack.Peek() : null;
                        var parentMember = memberStack.Count > 0 ? memberStack.Peek() : null;

                        var clrType = xamlType is null
                            ? null
                            : typeResolver.Resolve(xamlType.PreferredXamlNamespace ?? string.Empty, xamlType.Name);

                        List<XamlRule>? matched = null;
                        HashSet<string>? wanted = null;
                        if (clrType is not null)
                        {
                            foreach (var rule in rulesToCheck)
                            {
                                // XamlElementType is a record: compares Namespace/Name/Assembly by value.
                                if (rule.TypeInfo != clrType)
                                    continue;

                                (matched ??= new List<XamlRule>()).Add(rule);
                                foreach (var prop in rule.PropsToExtract)
                                    (wanted ??= new HashSet<string>(StringComparer.Ordinal)).Add(prop);
                            }
                        }

                        // IXamlLineInfo, on a StartObject, points at the first character of the
                        // element's name (the char right after '<') — where the squiggle starts.
                        var frame = new ElementFrame(
                            matched,
                            wanted,
                            lineInfo.LineNumber,
                            lineInfo.LinePosition,
                            typeName)
                        {
                            ResolvedType = clrType,
                        };

                        // {x:Static}/{StaticResource} supplying a wanted member's value.
                        var markupKind = MarkupKindOf(xamlType);
                        frame.MarkupKind = markupKind;
                        if (markupKind != MarkupKind.None && parentFrame is not null && parentMember is not null
                            && parentFrame.WantedProps is not null && parentFrame.WantedProps.Contains(parentMember))
                        {
                            frame.MarkupTargetFrame = parentFrame;
                            frame.MarkupTargetMember = parentMember;
                        }

                        var isStyle = typeName == StyleObjectName;

                        // An explicit <ResourceDictionary> written directly inside <Element.Resources>.
                        if (parentMember == ResourcesMember && parentFrame is not null)
                            frame.ResourceDictOwner = parentFrame;

                        // An object directly inside a resource dictionary is a resource entry — unless
                        // it is a Style, which is handled as a style (below) rather than a primitive.
                        if (!isStyle && parentFrame?.ResourceDictOwner is not null)
                        {
                            frame.ResourceEntryOwner = parentFrame.ResourceDictOwner;
                            frame.ResourceEntryIsPrimitive = IsPrimitiveResourceType(xamlType);
                        }

                        if (isStyle)
                        {
                            // A Style is either declared in <Element.Resources> (keyed or implicit) or
                            // inline as the value of an element's Style member.
                            if (parentFrame?.ResourceDictOwner is not null)
                                frame.StyleBuilding = new StyleInfo(parentFrame.ResourceDictOwner, isInline: false);
                            else if (parentFrame?.StyleMemberOwner is not null)
                                frame.StyleBuilding = new StyleInfo(parentFrame.StyleMemberOwner, isInline: true);
                            else if (parentMember == StyleMember && parentFrame is not null)
                                frame.StyleBuilding = new StyleInfo(parentFrame, isInline: true);
                        }
                        else if (typeName == SetterObjectName && parentFrame?.StyleBuilding is not null)
                        {
                            frame.SetterTargetStyle = parentFrame.StyleBuilding;
                        }
                        else if (typeName == TypeExtensionName && parentMember == TargetTypeMember
                                 && parentFrame?.StyleBuilding is not null)
                        {
                            // TargetType="{x:Type ...}" — its positional parameter is the type name.
                            frame.StyleTargetTypeSink = parentFrame.StyleBuilding;
                        }

                        if (markupKind == MarkupKind.StaticResource && parentFrame is not null)
                        {
                            if (parentFrame.StyleBuilding is not null && parentMember == BasedOnMember)
                                frame.StyleBasedOnSink = parentFrame.StyleBuilding;       // BasedOn="{StaticResource ...}"
                            else if (parentMember == StyleMember && parentFrame.MatchedRules is not null)
                                frame.StyleKeySinkFrame = parentFrame;                    // Style="{StaticResource ...}"
                        }

                        objectStack.Push(frame);
                        break;
                    }

                    case XamlNodeType.GetObject:
                    {
                        // A "get" object — e.g. the implicit collection behind <Element.Resources> or
                        // the wrapper around an inline <Element.Style>. Tag it with the owning element
                        // so its contents can be attributed correctly.
                        var getFrame = new ElementFrame(null, null, 0, 0, string.Empty);
                        var parentFrame = objectStack.Count > 0 ? objectStack.Peek() : null;
                        var parentMember = memberStack.Count > 0 ? memberStack.Peek() : null;
                        if (parentFrame is not null && parentMember == ResourcesMember)
                            getFrame.ResourceDictOwner = parentFrame;
                        else if (parentFrame is not null && parentMember == StyleMember && parentFrame.MatchedRules is not null)
                            getFrame.StyleMemberOwner = parentFrame;
                        objectStack.Push(getFrame);
                        break;
                    }

                    case XamlNodeType.StartMember:
                        memberStack.Push(reader.Member?.Name ?? string.Empty);
                        break;

                    case XamlNodeType.Value:
                    {
                        if (objectStack.Count > 0 && memberStack.Count > 0)
                        {
                            var frame = objectStack.Peek();
                            var member = memberStack.Peek();
                            var isPositional = member == PositionalParametersMember || member == "Member";
                            var stringValue = reader.Value as string;

                            // Argument of a markup extension (the frame is that markup extension).
                            if (isPositional && stringValue is not null)
                            {
                                if (frame.MarkupKind == MarkupKind.XStatic
                                    && frame.MarkupTargetFrame is not null && frame.MarkupTargetMember is not null)
                                {
                                    // {x:Static}: evaluate the static member; on failure fall through.
                                    if (StaticMemberValues.TryResolve(typeResolver, namespaces, stringValue, out var staticValue))
                                        frame.MarkupTargetFrame.Captured[frame.MarkupTargetMember] = staticValue;
                                }
                                else if (frame.MarkupKind == MarkupKind.StaticResource
                                    && frame.MarkupTargetFrame is not null && frame.MarkupTargetMember is not null)
                                {
                                    // {StaticResource} for a member value.
                                    frame.MarkupTargetFrame.Captured[frame.MarkupTargetMember] =
                                        ResolveStaticResource(objectStack, stringValue);
                                }
                                else if (frame.StyleKeySinkFrame is not null)
                                {
                                    frame.StyleKeySinkFrame.StyleStaticResourceKey = stringValue;
                                }
                                else if (frame.StyleBasedOnSink is not null)
                                {
                                    frame.StyleBasedOnSink.BasedOnKey = stringValue;
                                }
                                else if (frame.StyleTargetTypeSink is not null)
                                {
                                    frame.StyleTargetTypeSink.TargetTypeName = stringValue;
                                }
                            }
                            else if (frame.StyleBuilding is not null && reader.Value is not null)
                            {
                                if (member == KeyMember)
                                    frame.StyleBuilding.Key = reader.Value.ToString();
                                else if (member == TargetTypeMember)
                                    frame.StyleBuilding.TargetTypeName = reader.Value.ToString();
                            }
                            else if (frame.SetterTargetStyle is not null && reader.Value is not null)
                            {
                                if (member == PropertyMember)
                                    frame.SetterProperty = reader.Value.ToString();
                                else if (member == ValueMember)
                                    frame.SetterValue = reader.Value.ToString();
                            }
                            else if (frame.ResourceEntryOwner is not null && reader.Value is not null)
                            {
                                if (member == KeyMember)
                                    frame.ResourceEntryKey = reader.Value.ToString();
                                else if (member == InitializationMember)
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

                        // Commit a primitive resource entry to its owning element's resources.
                        if (frame.ResourceEntryOwner is not null && frame.ResourceEntryKey is not null)
                        {
                            var owner = frame.ResourceEntryOwner;
                            owner.Resources ??= new Dictionary<string, ResourceEntry>(StringComparer.Ordinal);
                            owner.Resources[frame.ResourceEntryKey] =
                                new ResourceEntry(frame.ResourceEntryIsPrimitive, frame.ResourceEntryValue);
                        }

                        // Commit a finished setter to the style being built.
                        if (frame.SetterTargetStyle is not null && frame.SetterProperty is not null)
                        {
                            frame.SetterTargetStyle.Setters.Add(
                                new KeyValuePair<string, string>(frame.SetterProperty, frame.SetterValue ?? string.Empty));
                        }

                        // Commit a finished style to its owning element.
                        if (frame.StyleBuilding is not null)
                        {
                            var style = frame.StyleBuilding;
                            var owner = style.Owner;
                            if (style.IsInline)
                            {
                                owner.InlineStyle = style;
                            }
                            else if (style.Key is not null)
                            {
                                owner.KeyedStyles ??= new Dictionary<string, StyleInfo>(StringComparer.Ordinal);
                                owner.KeyedStyles[style.Key] = style;
                            }
                            else if (style.TargetTypeName is not null)
                            {
                                owner.ImplicitStyles ??= new List<StyleInfo>();
                                owner.ImplicitStyles.Add(style);
                            }
                        }

                        if (frame.MatchedRules is null)
                            break;

                        // The element plus its still-open ancestors form the resource/style scope
                        // (nearest first). Anything not an ancestor has already been popped.
                        var scope = new List<ElementFrame>(objectStack.Count + 1) { frame };
                        scope.AddRange(objectStack);
                        var styleSetters = ResolveStyleSetters(frame, scope, typeResolver, namespaces);

                        var location = new XamlRuleFailureLocation(
                            frame.LineNumber, frame.LinePosition, frame.ElementName);

                        foreach (var rule in frame.MatchedRules)
                        {
                            // Resolution order per property: direct value, then style setter, then
                            // the dependency-property default. Every requested property gets an
                            // entry recording where its value came from (or that none was found).
                            var props = new Dictionary<string, PropertyValue>(StringComparer.Ordinal);
                            foreach (var name in rule.PropsToExtract)
                            {
                                PropertyValue resolved;
                                if (frame.Captured.TryGetValue(name, out var value))
                                    resolved = new PropertyValue(value, PropertyValueSource.LocalValue);
                                else if (styleSetters.TryGetValue(name, out var setterValue))
                                    resolved = new PropertyValue(setterValue, PropertyValueSource.StyleSetterValue);
                                else if (DependencyPropertyDefaults.TryGetDefault(rule.TypeInfo, name, out var defaultValue))
                                    resolved = new PropertyValue(defaultValue, PropertyValueSource.DefaultValue);
                                else
                                    resolved = new PropertyValue(null, PropertyValueSource.NotValueFound);

                                props[name] = resolved;
                            }

                            results.Add(new RuleViolationResult(rule, location, props));
                        }
                        break;
                    }
                }
            }

            return results;
        }

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
        /// Resolves a {StaticResource} primitive by walking the open scopes from nearest ancestor to
        /// the root. Returns the primitive's string value, or <see cref="InvalidResourceValue"/> when
        /// the key is missing or the resource isn't a primitive int/double/string.
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

        /// <summary>
        /// Determines the element's effective style and returns the merged Property→Value map from its
        /// BasedOn chain (base-most applied first so nearer styles override their bases).
        /// </summary>
        private static Dictionary<string, string> ResolveStyleSetters(
            ElementFrame element,
            List<ElementFrame> scope,
            IXamlTypeResolver typeResolver,
            IReadOnlyDictionary<string, string> namespaces)
        {
            var setters = new Dictionary<string, string>(StringComparer.Ordinal);

            // Direct styles win: inline, then Style="{StaticResource}". Only when neither is set do
            // we consider an implicit keyless style matched by TargetType.
            var hasDirectStyle = element.InlineStyle is not null || element.StyleStaticResourceKey is not null;
            var style = element.InlineStyle;
            if (style is null && element.StyleStaticResourceKey is not null)
                style = FindKeyedStyle(scope, 0, element.StyleStaticResourceKey);
            if (style is null && !hasDirectStyle && element.ResolvedType is not null)
                style = FindImplicitStyle(scope, 0, element.ResolvedType, typeResolver, namespaces);
            if (style is null)
                return setters;

            // Walk the BasedOn chain from the chosen (most-derived) style to its bases.
            var chain = new List<StyleInfo>();
            var current = style;
            var guard = 0;
            while (current is not null && guard++ < 64)
            {
                chain.Add(current);
                if (current.BasedOnKey is null)
                    break;
                var startIndex = scope.IndexOf(current.Owner);
                if (startIndex < 0)
                    startIndex = 0;
                current = FindKeyedStyle(scope, startIndex, current.BasedOnKey);
            }

            // Apply base-most first so a derived style's setters override its base's, and within a
            // style later setters override earlier ones.
            for (var i = chain.Count - 1; i >= 0; i--)
                foreach (var setter in chain[i].Setters)
                    setters[setter.Key] = setter.Value;

            return setters;
        }

        private static StyleInfo? FindKeyedStyle(List<ElementFrame> scope, int startIndex, string key)
        {
            for (var i = startIndex; i < scope.Count; i++)
            {
                var keyed = scope[i].KeyedStyles;
                if (keyed is not null && keyed.TryGetValue(key, out var style))
                    return style;
            }
            return null;
        }

        private static StyleInfo? FindImplicitStyle(
            List<ElementFrame> scope,
            int startIndex,
            XamlElementType elementType,
            IXamlTypeResolver typeResolver,
            IReadOnlyDictionary<string, string> namespaces)
        {
            for (var i = startIndex; i < scope.Count; i++)
            {
                var implicitStyles = scope[i].ImplicitStyles;
                if (implicitStyles is null)
                    continue;
                foreach (var style in implicitStyles)
                {
                    var targetType = ResolveQualifiedType(style.TargetTypeName, namespaces, typeResolver);
                    if (targetType is not null && targetType == elementType)
                        return style;
                }
            }
            return null;
        }

        /// <summary>Resolves a possibly prefixed XAML type name (e.g. "Slider" or "ctl:MyControl").</summary>
        private static XamlElementType? ResolveQualifiedType(
            string? qualifiedName,
            IReadOnlyDictionary<string, string> namespaces,
            IXamlTypeResolver typeResolver)
        {
            if (string.IsNullOrEmpty(qualifiedName))
                return null;

            var qualified = qualifiedName!;
            var prefix = string.Empty;
            var name = qualified;
            var colon = qualified.IndexOf(':');
            if (colon >= 0)
            {
                prefix = qualified.Substring(0, colon);
                name = qualified.Substring(colon + 1);
            }

            return namespaces.TryGetValue(prefix, out var xmlns)
                ? typeResolver.Resolve(xmlns, name)
                : null;
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

        /// <summary>A WPF Style collected while parsing: its key/target/BasedOn and its setters.</summary>
        private sealed class StyleInfo
        {
            public StyleInfo(ElementFrame owner, bool isInline)
            {
                Owner = owner;
                IsInline = isInline;
            }

            /// <summary>The element whose resources or Style member defines this style (BasedOn search origin).</summary>
            public ElementFrame Owner { get; }

            /// <summary>True for an inline <c>&lt;Element.Style&gt;</c> (vs. a resource style).</summary>
            public bool IsInline { get; }

            public string? Key { get; set; }
            public string? TargetTypeName { get; set; }
            public string? BasedOnKey { get; set; }
            public List<KeyValuePair<string, string>> Setters { get; } = new List<KeyValuePair<string, string>>();
        }

        /// <summary>
        /// Tracks the state of one open XAML object while streaming: rule matches, captured member
        /// values, resource/style scope, and markup-extension / resource-entry / style bookkeeping.
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

            /// <summary>The element's resolved CLR type (used for implicit-style TargetType matching).</summary>
            public XamlElementType? ResolvedType { get; set; }

            // {x:Static}/{StaticResource} supplying the value of a wanted member: where to populate.
            public MarkupKind MarkupKind { get; set; }
            public ElementFrame? MarkupTargetFrame { get; set; }
            public string? MarkupTargetMember { get; set; }

            // Resources / styles declared in this element's <Element.Resources>, in scope until popped.
            public Dictionary<string, ResourceEntry>? Resources { get; set; }
            public Dictionary<string, StyleInfo>? KeyedStyles { get; set; }
            public List<StyleInfo>? ImplicitStyles { get; set; }

            // The element's own style (inline) or the key of its Style="{StaticResource}".
            public StyleInfo? InlineStyle { get; set; }
            public string? StyleStaticResourceKey { get; set; }

            // Set on a (implicit/explicit) resource-dictionary scope: the element that owns it.
            public ElementFrame? ResourceDictOwner { get; set; }

            // Set on the scope backing an element's Style member (inline style): the owning element.
            public ElementFrame? StyleMemberOwner { get; set; }

            // Set on a resource-entry object while it is being read, then committed to the owner.
            public ElementFrame? ResourceEntryOwner { get; set; }
            public bool ResourceEntryIsPrimitive { get; set; }
            public string? ResourceEntryKey { get; set; }
            public string? ResourceEntryValue { get; set; }

            // Set on a Style object while it is being built; committed at its EndObject.
            public StyleInfo? StyleBuilding { get; set; }

            // Set on a Setter object while it is being read; committed to its style.
            public StyleInfo? SetterTargetStyle { get; set; }
            public string? SetterProperty { get; set; }
            public string? SetterValue { get; set; }

            // Markup-extension frames that feed a style's fields rather than a member value.
            public ElementFrame? StyleKeySinkFrame { get; set; }   // Style="{StaticResource key}"
            public StyleInfo? StyleBasedOnSink { get; set; }       // BasedOn="{StaticResource key}"
            public StyleInfo? StyleTargetTypeSink { get; set; }    // TargetType="{x:Type name}"
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
    /// <param name="ExtractedProperties">The resolved value of each member named in <see cref="XamlRule.PropsToExtract"/>, keyed by member name. Every requested member has an entry; see <see cref="PropertyValue.Source"/> for where the value came from (or <see cref="PropertyValueSource.NotValueFound"/> when none could be determined).</param>
    public record RuleViolationResult(
        XamlRule FailedRule,
        IXamlRuleFailureLocation Location,
        IReadOnlyDictionary<string, PropertyValue> ExtractedProperties);

    /// <summary>
    /// A resolved property value together with where it came from.
    /// </summary>
    /// <param name="Value">The value as a string, or <c>null</c> when <paramref name="Source"/> is <see cref="PropertyValueSource.NotValueFound"/>.</param>
    /// <param name="Source">Where the value was resolved from.</param>
    public record PropertyValue(string? Value, PropertyValueSource Source);

    /// <summary>Where a <see cref="PropertyValue"/> was resolved from.</summary>
    public enum PropertyValueSource
    {
        /// <summary>No value could be determined for the property.</summary>
        NotValueFound,

        /// <summary>Set directly on the XAML element (e.g. <c>Minimum="0"</c>).</summary>
        LocalValue,

        /// <summary>Supplied by a Style setter (keyed, keyless, inline, or referenced — it doesn't matter which).</summary>
        StyleSetterValue,

        /// <summary>The dependency property's registered default value.</summary>
        DefaultValue,
    }

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
