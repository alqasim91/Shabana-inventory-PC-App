using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Shabana.LabelsGen
{
    /// <summary>
    /// labels*.ar.json (AdditionalFiles) -> <c>Shabana.Core.Resources.Labels</c> (Labels.g.cs).
    /// Every JSON entry becomes a <c>const string</c>, so a reference to a key that is not in the JSON is a
    /// compile error (CS0117/CS0103). The 16 <c>_templates</c> become methods that format numeric arguments
    /// with fixed ar-EG tables (Arabic-Indic digits, U+066C grouping) and never trust the machine culture.
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class LabelsGenerator : IIncrementalGenerator
    {
        public const string HintName = "Labels.g.cs";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var files = context.AdditionalTextsProvider
                .Where(static t => IsLabelsFile(t.Path))
                .Select(static (t, ct) => new LabelsFile(t.Path, t.GetText(ct)?.ToString() ?? string.Empty))
                .Collect();

            context.RegisterSourceOutput(files, static (spc, all) =>
            {
                if (all.IsDefaultOrEmpty)
                {
                    return;
                }

                var ordered = all.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
                var source = LabelsEmitter.Generate(ordered, spc.ReportDiagnostic);
                if (source != null)
                {
                    spc.AddSource(HintName, SourceText.From(source, Encoding.UTF8));
                }
            });
        }

        internal static bool IsLabelsFile(string path)
        {
            var name = Path.GetFileName(path);
            return name.StartsWith("labels", StringComparison.OrdinalIgnoreCase)
                   && name.EndsWith(".ar.json", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class LabelsFile
    {
        public LabelsFile(string path, string text)
        {
            Path = path;
            Text = text;
        }

        public string Path { get; }

        public string Text { get; }

        public override bool Equals(object? obj) =>
            obj is LabelsFile o && o.Path == Path && o.Text == Text;

        public override int GetHashCode() => (Path.GetHashCode() * 397) ^ Text.GetHashCode();
    }

    internal static class Descriptors
    {
        private const string Category = "Shabana.Labels";

        public static readonly DiagnosticDescriptor MalformedJson = new DiagnosticDescriptor(
            "SHL0001", "Labels JSON is malformed", "{0}", Category, DiagnosticSeverity.Error, true);

        public static readonly DiagnosticDescriptor Collision = new DiagnosticDescriptor(
            "SHL0002", "Duplicate or colliding label name", "{0}", Category, DiagnosticSeverity.Error, true);

        public static readonly DiagnosticDescriptor BadTemplate = new DiagnosticDescriptor(
            "SHL0003", "Invalid label template", "{0}", Category, DiagnosticSeverity.Error, true);

        public static readonly DiagnosticDescriptor BadStructure = new DiagnosticDescriptor(
            "SHL0004", "Unexpected labels JSON structure", "{0}", Category, DiagnosticSeverity.Error, true);

        public static readonly DiagnosticDescriptor StrayPlaceholder = new DiagnosticDescriptor(
            "SHL0005", "Placeholder in a label that is not a registered template", "{0}", Category, DiagnosticSeverity.Error, true);
    }

    internal sealed class LabelEntry
    {
        public string Group = string.Empty;       // JSON group ("TABLE"), or "" for a top-level string
        public string Key = string.Empty;         // JSON key
        public string Value = string.Empty;
        public string Path = string.Empty;
        public int Line;
        public string Ident = string.Empty;       // C# member name
        public TemplateInfo? Template;
    }

    internal sealed class TemplateInfo
    {
        public List<string> ParamNames = new List<string>();
        public List<bool> ParamIsNumber = new List<bool>();
        public List<KeyValuePair<KeyValuePair<string, long>, string>> Variants = new List<KeyValuePair<KeyValuePair<string, long>, string>>();
    }

    internal static class LabelsEmitter
    {
        private const string Namespace = "Shabana.Core.Resources";
        private const string ClassName = "Labels";
        private static readonly Regex PlaceholderRx = new Regex(@"\{(\d+)\}", RegexOptions.Compiled);
        private static readonly Regex SignatureRx = new Regex(@"\(([^)]*)\)\s*=>", RegexOptions.Compiled);
        private static readonly Regex WhenRx = new Regex(@"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*==\s*(-?\d+)\s*$", RegexOptions.Compiled);

        // Members the generator itself declares inside every group class / the root class.
        private static readonly HashSet<string> ReservedMembers = new HashSet<string>(StringComparer.Ordinal)
        {
            "ByKey", "Num", "Equals", "ReferenceEquals", "GetHashCode", "GetType", "ToString", "MemberwiseClone", "Finalize",
        };

        public static string? Generate(IReadOnlyList<LabelsFile> files, Action<Diagnostic> report)
        {
            var errors = false;
            void Fail(DiagnosticDescriptor d, string path, int line, string msg)
            {
                errors = true;
                var pos = new LinePosition(Math.Max(0, line - 1), 0);
                report(Diagnostic.Create(d, Location.Create(path, default, new LinePositionSpan(pos, pos)), msg));
            }

            var entries = new List<LabelEntry>();
            var templateDefs = new List<(JsonObject def, string path)>();

            foreach (var file in files)
            {
                JsonNode root;
                try
                {
                    root = MiniJson.Parse(file.Text);
                }
                catch (JsonParseException ex)
                {
                    Fail(Descriptors.MalformedJson, file.Path, ex.Line, "Malformed JSON at line " + ex.Line + ", column " + ex.Column + ": " + ex.Message);
                    continue;
                }

                if (!(root is JsonObject rootObj))
                {
                    Fail(Descriptors.BadStructure, file.Path, root.Line, "The root of a labels file must be a JSON object.");
                    continue;
                }

                var seenGroups = new HashSet<string>(StringComparer.Ordinal);
                foreach (var member in rootObj.Members)
                {
                    if (!seenGroups.Add(member.Key))
                    {
                        Fail(Descriptors.Collision, file.Path, member.Value.Line, "Duplicate key '" + member.Key + "' at the top level.");
                        continue;
                    }

                    if (member.Key == "_templates")
                    {
                        if (member.Value is JsonArray arr)
                        {
                            foreach (var item in arr.Items)
                            {
                                if (item is JsonObject o)
                                {
                                    templateDefs.Add((o, file.Path));
                                }
                                else
                                {
                                    Fail(Descriptors.BadStructure, file.Path, item.Line, "Each '_templates' item must be an object.");
                                }
                            }
                        }
                        else
                        {
                            Fail(Descriptors.BadStructure, file.Path, member.Value.Line, "'_templates' must be an array.");
                        }

                        continue;
                    }

                    if (member.Key.StartsWith("_", StringComparison.Ordinal))
                    {
                        continue; // reserved for metadata
                    }

                    if (member.Value is JsonString topString)
                    {
                        entries.Add(new LabelEntry { Group = string.Empty, Key = member.Key, Value = topString.Value, Path = file.Path, Line = topString.Line });
                    }
                    else if (member.Value is JsonObject group)
                    {
                        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var kv in group.Members)
                        {
                            if (!seenKeys.Add(kv.Key))
                            {
                                Fail(Descriptors.Collision, file.Path, kv.Value.Line, "Duplicate key '" + member.Key + "." + kv.Key + "'.");
                                continue;
                            }

                            if (kv.Value is JsonString s)
                            {
                                entries.Add(new LabelEntry { Group = member.Key, Key = kv.Key, Value = s.Value, Path = file.Path, Line = s.Line });
                            }
                            else
                            {
                                Fail(Descriptors.BadStructure, file.Path, kv.Value.Line, "'" + member.Key + "." + kv.Key + "' must be a string.");
                            }
                        }
                    }
                    else
                    {
                        Fail(Descriptors.BadStructure, file.Path, member.Value.Line, "Top-level '" + member.Key + "' must be a string or an object of strings.");
                    }
                }
            }

            // Same JSON key in two files.
            foreach (var dup in entries.GroupBy(e => e.Group + "\u0000" + e.Key, StringComparer.Ordinal).Where(g => g.Count() > 1))
            {
                var first = dup.First();
                Fail(Descriptors.Collision, first.Path, first.Line, "Label '" + Display(first) + "' is defined in more than one labels file.");
            }

            // Identifiers.
            var groupIdents = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var g in entries.Where(e => e.Group.Length > 0).Select(e => e.Group).Distinct(StringComparer.Ordinal))
            {
                var ident = ToIdent(g);
                if (ident.Length == 0)
                {
                    var any = entries.First(e => e.Group == g);
                    Fail(Descriptors.Collision, any.Path, any.Line, "Group '" + g + "' has no usable C# name.");
                    continue;
                }

                groupIdents[g] = ident;
            }

            foreach (var clash in groupIdents.GroupBy(kv => kv.Value, StringComparer.Ordinal).Where(x => x.Count() > 1))
            {
                var any = entries.First(e => e.Group == clash.First().Key);
                Fail(Descriptors.Collision, any.Path, any.Line, "Groups " + string.Join(", ", clash.Select(c => "'" + c.Key + "'")) + " all map to C# name '" + clash.Key + "'.");
            }

            foreach (var e in entries)
            {
                e.Ident = ToIdent(e.Key);
                if (e.Ident.Length == 0)
                {
                    Fail(Descriptors.Collision, e.Path, e.Line, "Label '" + Display(e) + "' has no usable C# name.");
                }
            }

            // Templates.
            var byName = new Dictionary<string, LabelEntry>(StringComparer.Ordinal);
            foreach (var e in entries.Where(e => e.Ident.Length > 0))
            {
                byName[e.Group + "." + e.Key] = e; // a duplicate was already reported above
            }

            foreach (var (def, path) in templateDefs)
            {
                var keyNode = def.Get("key") as JsonString;
                if (keyNode == null)
                {
                    Fail(Descriptors.BadTemplate, path, def.Line, "A template needs a string 'key' (\"GROUP.name\").");
                    continue;
                }

                if (!byName.TryGetValue(keyNode.Value, out var entry))
                {
                    Fail(Descriptors.BadTemplate, path, keyNode.Line, "Template '" + keyNode.Value + "' does not match any label key.");
                    continue;
                }

                var info = BuildTemplate(def, keyNode.Value, entry, Fail);
                if (info != null)
                {
                    entry.Template = info;
                }
            }

            foreach (var e in entries.Where(e => e.Template == null))
            {
                var m = PlaceholderRx.Match(e.Value);
                if (m.Success)
                {
                    Fail(Descriptors.StrayPlaceholder, e.Path, e.Line,
                        "Label '" + Display(e) + "' contains placeholder '" + m.Value + "' but is not listed in '_templates'.");
                }
            }

            // Member-name collisions inside one class (and with generator-declared members).
            foreach (var grp in entries.Where(e => e.Ident.Length > 0).GroupBy(e => e.Group, StringComparer.Ordinal))
            {
                var used = new Dictionary<string, LabelEntry>(StringComparer.Ordinal);
                var className = grp.Key.Length == 0 ? ClassName : (groupIdents.TryGetValue(grp.Key, out var gi) ? gi : string.Empty);
                foreach (var e in grp)
                {
                    var names = new List<string> { e.Ident };
                    if (e.Template != null)
                    {
                        names.Add(e.Ident + "Template");
                    }

                    foreach (var n in names)
                    {
                        if (ReservedMembers.Contains(n) || n == className)
                        {
                            Fail(Descriptors.Collision, e.Path, e.Line, "Label '" + Display(e) + "' maps to C# member '" + n + "', which is reserved or equals its class name.");
                        }
                        else if (used.TryGetValue(n, out var other))
                        {
                            Fail(Descriptors.Collision, e.Path, e.Line, "Labels '" + Display(other) + "' and '" + Display(e) + "' both map to C# member '" + n + "'.");
                        }
                        else
                        {
                            used[n] = e;
                        }
                    }
                }
            }

            // A group class must not collide with a top-level member of Labels.
            var topMembers = new HashSet<string>(entries.Where(e => e.Group.Length == 0).Select(e => e.Ident), StringComparer.Ordinal);
            foreach (var g in groupIdents.Where(kv => topMembers.Contains(kv.Value)))
            {
                var any = entries.First(e => e.Group == g.Key);
                Fail(Descriptors.Collision, any.Path, any.Line, "Group '" + g.Key + "' collides with the top-level label of the same C# name '" + g.Value + "'.");
            }

            return errors ? null : Emit(entries, groupIdents);
        }

        private static TemplateInfo? BuildTemplate(JsonObject def, string key, LabelEntry entry, Action<DiagnosticDescriptor, string, int, string> fail)
        {
            var path = entry.Path;
            var line = def.Line;
            var ok = true;
            var info = new TemplateInfo();

            if (def.Get("params") is JsonArray pa)
            {
                foreach (var p in pa.Items)
                {
                    if (p is JsonString ps && Regex.IsMatch(ps.Value, "^[A-Za-z_][A-Za-z0-9_]*$"))
                    {
                        info.ParamNames.Add(ps.Value);
                    }
                    else
                    {
                        fail(Descriptors.BadTemplate, path, line, "Template '" + key + "': every 'params' item must be an identifier string.");
                        ok = false;
                    }
                }
            }
            else
            {
                fail(Descriptors.BadTemplate, path, line, "Template '" + key + "' needs a 'params' array.");
                return null;
            }

            if (info.ParamNames.Count == 0 || info.ParamNames.Distinct(StringComparer.Ordinal).Count() != info.ParamNames.Count)
            {
                fail(Descriptors.BadTemplate, path, line, "Template '" + key + "' needs at least one distinct parameter.");
                return null;
            }

            // Parameter types come from the original TypeScript signature kept in 'source'.
            var types = new Dictionary<string, string>(StringComparer.Ordinal);
            var source = (def.Get("source") as JsonString)?.Value ?? string.Empty;
            var sig = SignatureRx.Match(source);
            if (sig.Success)
            {
                foreach (var part in sig.Groups[1].Value.Split(','))
                {
                    var pieces = part.Split(new[] { ':' }, 2);
                    if (pieces.Length == 2)
                    {
                        types[pieces[0].Trim()] = pieces[1].Trim();
                    }
                }
            }

            foreach (var name in info.ParamNames)
            {
                if (!types.TryGetValue(name, out var t) || (t != "number" && t != "string"))
                {
                    fail(Descriptors.BadTemplate, path, line,
                        "Template '" + key + "': cannot determine the type of parameter '" + name + "' (expected 'number' or 'string' in its 'source' signature).");
                    ok = false;
                    info.ParamIsNumber.Add(false);
                }
                else
                {
                    info.ParamIsNumber.Add(t == "number");
                }
            }

            ok &= CheckFormat(entry.Value, info.ParamNames.Count, key, path, line, fail);

            if (def.Get("variants") is JsonArray va)
            {
                foreach (var v in va.Items)
                {
                    var vo = v as JsonObject;
                    var when = (vo?.Get("when") as JsonString)?.Value;
                    var text = (vo?.Get("text") as JsonString)?.Value;
                    var m = when == null ? Match.Empty : WhenRx.Match(when);
                    if (vo == null || text == null || !m.Success || !info.ParamNames.Contains(m.Groups[1].Value) || !long.TryParse(m.Groups[2].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
                    {
                        fail(Descriptors.BadTemplate, path, v.Line,
                            "Template '" + key + "': a variant must be {\"when\": \"<param> == <integer>\", \"text\": \"...\"} using one of the template's parameters.");
                        ok = false;
                        continue;
                    }

                    var idx = info.ParamNames.IndexOf(m.Groups[1].Value);
                    if (!info.ParamIsNumber[idx])
                    {
                        fail(Descriptors.BadTemplate, path, v.Line, "Template '" + key + "': a variant condition needs a numeric parameter.");
                        ok = false;
                        continue;
                    }

                    ok &= CheckFormat(text, info.ParamNames.Count, key, path, v.Line, fail);
                    info.Variants.Add(new KeyValuePair<KeyValuePair<string, long>, string>(new KeyValuePair<string, long>(m.Groups[1].Value, number), text));
                }
            }

            return ok ? info : null;
        }

        /// <summary>Only {0}..{n-1} placeholders are allowed; any other brace would throw at run time in string.Format.</summary>
        private static bool CheckFormat(string text, int paramCount, string key, string path, int line, Action<DiagnosticDescriptor, string, int, string> fail)
        {
            var stripped = PlaceholderRx.Replace(text, string.Empty);
            if (stripped.IndexOf('{') >= 0 || stripped.IndexOf('}') >= 0)
            {
                fail(Descriptors.BadTemplate, path, line, "Template '" + key + "': text contains a brace that is not a {N} placeholder.");
                return false;
            }

            foreach (Match m in PlaceholderRx.Matches(text))
            {
                if (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) >= paramCount)
                {
                    fail(Descriptors.BadTemplate, path, line, "Template '" + key + "': placeholder " + m.Value + " has no matching parameter (" + paramCount + " declared).");
                    return false;
                }
            }

            return true;
        }

        private static string Display(LabelEntry e) => e.Group.Length == 0 ? e.Key : e.Group + "." + e.Key;

        /// <summary>"PERM_LABEL" -> PermLabel, "showingCount" -> ShowingCount, "sales.view" -> SalesView, "1" -> _1.</summary>
        internal static string ToIdent(string key)
        {
            var sb = new StringBuilder();
            var part = new StringBuilder();

            void Flush()
            {
                if (part.Length == 0)
                {
                    return;
                }

                var p = part.ToString();
                var hasLower = p.Any(char.IsLower);
                if (!hasLower && p.Length > 1)
                {
                    sb.Append(char.ToUpperInvariant(p[0])).Append(p.Substring(1).ToLowerInvariant());
                }
                else
                {
                    sb.Append(char.ToUpperInvariant(p[0])).Append(p, 1, p.Length - 1);
                }

                part.Clear();
            }

            foreach (var c in key)
            {
                if (c < 128 && char.IsLetterOrDigit(c))
                {
                    part.Append(c);
                }
                else
                {
                    Flush();
                }
            }

            Flush();
            if (sb.Length == 0)
            {
                return string.Empty;
            }

            return char.IsDigit(sb[0]) ? "_" + sb : sb.ToString();
        }

        private static string Literal(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        var cat = char.GetUnicodeCategory(c);
                        var invisible = c < ' '
                            || cat == UnicodeCategory.Control
                            || cat == UnicodeCategory.Format
                            || cat == UnicodeCategory.LineSeparator
                            || cat == UnicodeCategory.ParagraphSeparator
                            || (cat == UnicodeCategory.SpaceSeparator && c != ' ')
                            || cat == UnicodeCategory.OtherNotAssigned
                            || cat == UnicodeCategory.PrivateUse;
                        if (invisible)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            return sb.Append('"').ToString();
        }

        private static string ParamName(string name) =>
            Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(name) != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None
            || Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetContextualKeywordKind(name) != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None
                ? "@" + name
                : name;

        private static string Emit(List<LabelEntry> entries, Dictionary<string, string> groupIdents)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("//   Generated by Shabana.LabelsGen from labels*.ar.json. Do not edit; edit the JSON.");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine("#nullable enable");
            sb.AppendLine("#pragma warning disable");
            sb.AppendLine();
            sb.AppendLine("namespace " + Namespace);
            sb.AppendLine("{");
            sb.AppendLine("    public static partial class " + ClassName);
            sb.AppendLine("    {");

            void EmitEntry(LabelEntry e, string indent)
            {
                if (e.Template == null)
                {
                    sb.AppendLine(indent + "public const string " + e.Ident + " = " + Literal(e.Value) + ";");
                    return;
                }

                var t = e.Template;
                sb.AppendLine(indent + "/// <summary>Format string of <see cref=\"" + e.Ident + "\"/>; {N} placeholders.</summary>");
                sb.AppendLine(indent + "public const string " + e.Ident + "Template = " + Literal(e.Value) + ";");
                var sig = string.Join(", ", t.ParamNames.Select((n, i) => (t.ParamIsNumber[i] ? "long " : "string ") + ParamName(n)));
                sb.AppendLine(indent + "public static string " + e.Ident + "(" + sig + ")");
                sb.AppendLine(indent + "{");
                string Args() => string.Join(", ", t.ParamNames.Select((n, i) => t.ParamIsNumber[i] ? ClassName + ".Num(" + ParamName(n) + ")" : ParamName(n)));
                foreach (var v in t.Variants)
                {
                    sb.AppendLine(indent + "    if (" + ParamName(v.Key.Key) + " == " + v.Key.Value.ToString(CultureInfo.InvariantCulture) + "L)");
                    sb.AppendLine(indent + "    {");
                    sb.AppendLine(indent + "        return string.Format(global::System.Globalization.CultureInfo.InvariantCulture, " + Literal(v.Value) + ", " + Args() + ");");
                    sb.AppendLine(indent + "    }");
                    sb.AppendLine();
                }

                sb.AppendLine(indent + "    return string.Format(global::System.Globalization.CultureInfo.InvariantCulture, " + e.Ident + "Template, " + Args() + ");");
                sb.AppendLine(indent + "}");
            }

            void EmitByKey(IEnumerable<LabelEntry> list, string indent, string qualifier)
            {
                sb.AppendLine();
                sb.AppendLine(indent + "/// <summary>Original JSON key -> text (templates map to their format string).</summary>");
                sb.AppendLine(indent + "public static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, string> ByKey =");
                sb.AppendLine(indent + "    new global::System.Collections.Generic.Dictionary<string, string>");
                sb.AppendLine(indent + "    {");
                foreach (var e in list)
                {
                    sb.AppendLine(indent + "        [" + Literal(e.Key) + "] = " + qualifier + e.Ident + (e.Template != null ? "Template" : string.Empty) + ",");
                }

                sb.AppendLine(indent + "    };");
            }

            foreach (var e in entries.Where(e => e.Group.Length == 0))
            {
                EmitEntry(e, "        ");
            }

            foreach (var g in entries.Where(e => e.Group.Length > 0).GroupBy(e => e.Group, StringComparer.Ordinal))
            {
                sb.AppendLine();
                sb.AppendLine("        public static class " + groupIdents[g.Key]);
                sb.AppendLine("        {");
                foreach (var e in g)
                {
                    EmitEntry(e, "            ");
                }

                EmitByKey(g, "            ", string.Empty);
                sb.AppendLine("        }");
            }

            sb.AppendLine();
            sb.AppendLine("        /// <summary>Integer -> ar-EG text with fixed tables (04 section 4.6): digits U+0660.., group U+066C, minus U+2212.</summary>");
            sb.AppendLine("        private static string Num(long value)");
            sb.AppendLine("        {");
            sb.AppendLine("            ulong magnitude = value < 0 ? (ulong)(-(value + 1)) + 1UL : (ulong)value;");
            sb.AppendLine("            var buffer = new char[48];");
            sb.AppendLine("            var pos = buffer.Length;");
            sb.AppendLine("            var digits = 0;");
            sb.AppendLine("            do");
            sb.AppendLine("            {");
            sb.AppendLine("                if (digits > 0 && digits % 3 == 0)");
            sb.AppendLine("                {");
            sb.AppendLine("                    buffer[--pos] = '\\u066C';");
            sb.AppendLine("                }");
            sb.AppendLine();
            sb.AppendLine("                buffer[--pos] = (char)('\\u0660' + (int)(magnitude % 10UL));");
            sb.AppendLine("                magnitude /= 10UL;");
            sb.AppendLine("                digits++;");
            sb.AppendLine("            }");
            sb.AppendLine("            while (magnitude != 0);");
            sb.AppendLine();
            sb.AppendLine("            if (value < 0)");
            sb.AppendLine("            {");
            sb.AppendLine("                buffer[--pos] = '\\u2212';");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine("            return new string(buffer, pos, buffer.Length - pos);");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }
    }
}
