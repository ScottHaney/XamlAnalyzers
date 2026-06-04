using System;
using System.Collections.Generic;
using System.IO;
using Portable.Xaml;

namespace XamlUtils
{
    public class XamlParser
    {
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

            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XamlNodeType.StartObject:
                    {
                        var xamlType = reader.Type;
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
                            xamlType?.Name ?? string.Empty));
                        break;
                    }

                    case XamlNodeType.GetObject:
                        // An implicit object (e.g. a pre-existing collection being appended to)
                        // still opens a scope we must balance, but is never a rule target.
                        objectStack.Push(ElementFrame.Ignored);
                        break;

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
                            if (frame.WantedProps is not null
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

        /// <summary>
        /// Tracks the state of one open XAML element while streaming: which rules it matched,
        /// which member values to capture, where its name sits, and the captured values.
        /// </summary>
        private sealed class ElementFrame
        {
            /// <summary>Shared frame for object scopes that can never match a rule.</summary>
            public static readonly ElementFrame Ignored = new(null, null, 0, 0, string.Empty);

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
