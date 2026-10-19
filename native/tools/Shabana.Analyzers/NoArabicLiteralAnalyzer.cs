using System;
using System.Collections.Immutable;
using System.IO;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Shabana.Analyzers
{
    /// <summary>
    /// SHB0001 (error): a string/char literal in C# or an attribute/text value in XAML contains Arabic
    /// (U+0600-06FF, U+0750-077F, U+FB50-FDFF, U+FE70-FEFF). All user-visible Arabic belongs in labels*.ar.json and
    /// is reached through the generated <c>Labels</c> class. Generated code is exempt (Roslyn skips it);
    /// comments are not literals and are ignored. Escapes ("ا") are caught because the decoded value is tested.
    /// XAML files are analysed when they are passed as AdditionalFiles (see Directory.Build.targets).
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class NoArabicLiteralAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "SHB0001";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId,
            "Arabic literal outside the labels JSON",
            "{0} contains Arabic text. Add it to labels.ar.json (or labels.native.ar.json) and reference it through Labels.",
            "Shabana.Localization",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "CLAUDE.md / 04 section 4.7: no Arabic string literal may appear outside the two JSON files.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public static bool ContainsArabic(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            foreach (var c in text!)
            {
                if ((c >= '؀' && c <= 'ۿ')
                    || (c >= 'ݐ' && c <= 'ݿ')
                    || (c >= 'ﭐ' && c <= '﷿')
                    || (c >= 'ﹰ' && c <= '﻿'))
                {
                    return true;
                }
            }

            return false;
        }

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxTreeAction(AnalyzeTree);
            context.RegisterAdditionalFileAction(AnalyzeAdditionalFile);
        }

        private static void AnalyzeTree(SyntaxTreeAnalysisContext context)
        {
            var root = context.Tree.GetRoot(context.CancellationToken);
            foreach (var token in root.DescendantTokens())
            {
                string what;
                switch (token.Kind())
                {
                    case SyntaxKind.StringLiteralToken:
                    case SyntaxKind.SingleLineRawStringLiteralToken:
                    case SyntaxKind.MultiLineRawStringLiteralToken:
                    case SyntaxKind.Utf8StringLiteralToken:
                    case SyntaxKind.Utf8SingleLineRawStringLiteralToken:
                    case SyntaxKind.Utf8MultiLineRawStringLiteralToken:
                        what = "String literal";
                        break;
                    case SyntaxKind.InterpolatedStringTextToken:
                        what = "Interpolated string text";
                        break;
                    case SyntaxKind.CharacterLiteralToken:
                        what = "Character literal";
                        break;
                    default:
                        continue;
                }

                if (ContainsArabic(token.ValueText))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Rule, token.GetLocation(), what));
                }
            }
        }

        private static void AnalyzeAdditionalFile(AdditionalFileAnalysisContext context)
        {
            var file = context.AdditionalFile;
            if (!file.Path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var text = file.GetText(context.CancellationToken);
            if (text == null)
            {
                return;
            }

            void Report(int line, int column, string what)
            {
                // XmlReader lines/columns are 1-based.
                var lineIndex = Math.Max(0, Math.Min(line - 1, text.Lines.Count - 1));
                var textLine = text.Lines[lineIndex];
                var col = Math.Max(0, Math.Min(column - 1, Math.Max(0, textLine.Span.Length - 1)));
                var start = textLine.Start + col;
                var span = new TextSpan(start, Math.Min(1, Math.Max(0, text.Length - start)));
                context.ReportDiagnostic(Diagnostic.Create(Rule, Location.Create(file.Path, span, text.Lines.GetLinePositionSpan(span)), what));
            }

            var content = text.ToString();
            if (!ContainsArabic(content))
            {
                return;
            }

            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    IgnoreComments = true,
                };
                using (var sr = new StringReader(content))
                using (var reader = XmlReader.Create(sr, settings))
                {
                    var info = (IXmlLineInfo)reader;
                    while (reader.Read())
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();
                        if (reader.NodeType == XmlNodeType.Element && reader.HasAttributes)
                        {
                            for (var ok = reader.MoveToFirstAttribute(); ok; ok = reader.MoveToNextAttribute())
                            {
                                if (ContainsArabic(reader.Value))
                                {
                                    Report(info.LineNumber, info.LinePosition, "XAML attribute '" + reader.Name + "'");
                                }
                            }

                            reader.MoveToElement();
                        }
                        else if ((reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.CDATA) && ContainsArabic(reader.Value))
                        {
                            Report(info.LineNumber, info.LinePosition, "XAML text");
                        }
                    }
                }
            }
            catch (XmlException)
            {
                // Not well-formed: be strict rather than silently skipping the file (the XAML compiler will also fail).
                for (var i = 0; i < text.Lines.Count; i++)
                {
                    if (ContainsArabic(text.Lines[i].ToString()))
                    {
                        Report(i + 1, 1, "XAML line");
                    }
                }
            }
        }
    }
}
