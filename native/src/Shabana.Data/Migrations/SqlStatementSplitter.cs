using System.Text;

namespace Shabana.Data.Migrations;

/// <summary>
/// Splits a SQL script into statements the way <c>sqlite3_complete()</c> does: string literals, quoted identifiers and comments are
/// opaque, and a <c>CREATE TRIGGER</c> ends at the <c>;</c> after the <c>END</c> that itself follows a <c>;</c> (so a
/// <c>CASE ... END</c> inside a trigger body does not end it). Line endings are irrelevant (CRLF-safe).
/// </summary>
public static class SqlStatementSplitter
{
    public static IReadOnlyList<string> Split(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var statements = new List<string>();
        var current = new StringBuilder();
        var significant = new List<string>(8);      // the first words of the statement, to recognise CREATE TRIGGER
        var isTrigger = false;
        var sawSemicolonInBody = false;             // trigger: the previous significant token was ';'
        var endAfterSemicolon = false;              // trigger: the previous significant token was END that followed ';'
        var i = 0;

        void Finish()
        {
            var text = current.ToString().Trim();
            if (text.Length > 0 && HasContent(text))
            {
                statements.Add(text);
            }

            current.Clear();
            significant.Clear();
            isTrigger = false;
            sawSemicolonInBody = false;
            endAfterSemicolon = false;
        }

        while (i < script.Length)
        {
            var c = script[i];
            if (c == '-' && i + 1 < script.Length && script[i + 1] == '-')
            {
                var end = script.IndexOf('\n', i);
                var stop = end < 0 ? script.Length : end;
                current.Append(script, i, stop - i);
                i = stop;
                continue;
            }

            if (c == '/' && i + 1 < script.Length && script[i + 1] == '*')
            {
                var end = script.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = end < 0 ? script.Length : end + 2;
                current.Append(script, i, stop - i);
                i = stop;
                continue;
            }

            if (c is '\'' or '"' or '`' || c == '[')
            {
                var close = c == '[' ? ']' : c;
                var start = i;
                i++;
                while (i < script.Length)
                {
                    if (script[i] == close)
                    {
                        if (close != ']' && i + 1 < script.Length && script[i + 1] == close)
                        {
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    i++;
                }

                current.Append(script, start, i - start);
                if (significant.Count < 8)
                {
                    significant.Add("\"");
                }

                sawSemicolonInBody = false;
                endAfterSemicolon = false;
                continue;
            }

            if (c == ';')
            {
                current.Append(c);
                i++;
                if (isTrigger && !endAfterSemicolon)
                {
                    sawSemicolonInBody = true;   // inside the body: the statement is not over yet
                    continue;
                }

                Finish();
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < script.Length && (char.IsLetterOrDigit(script[i]) || script[i] == '_' || script[i] == '$'))
                {
                    i++;
                }

                var word = script.Substring(start, i - start);
                current.Append(word);
                if (significant.Count < 8)
                {
                    significant.Add(word.ToUpperInvariant());
                    if (!isTrigger)
                    {
                        isTrigger = IsCreateTrigger(significant);
                    }
                }

                endAfterSemicolon = isTrigger && sawSemicolonInBody && word.Equals("END", StringComparison.OrdinalIgnoreCase);
                sawSemicolonInBody = false;
                continue;
            }

            current.Append(c);
            i++;
            if (!char.IsWhiteSpace(c))
            {
                sawSemicolonInBody = false;
                endAfterSemicolon = false;
            }
        }

        Finish();
        return statements;
    }

    private static bool IsCreateTrigger(List<string> words)
    {
        // CREATE [TEMP|TEMPORARY] TRIGGER
        if (words.Count < 2 || words[0] != "CREATE")
        {
            return false;
        }

        return words[1] == "TRIGGER" || (words.Count >= 3 && words[1] is "TEMP" or "TEMPORARY" && words[2] == "TRIGGER");
    }

    /// <summary>True when the text holds anything but comments and whitespace.</summary>
    private static bool HasContent(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
            }
            else if (text[i] == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                var end = text.IndexOf('\n', i);
                i = end < 0 ? text.Length : end;
            }
            else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
            }
            else
            {
                return true;
            }
        }

        return false;
    }
}
