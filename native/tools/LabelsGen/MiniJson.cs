using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Shabana.LabelsGen
{
    /// <summary>
    /// A tiny strict JSON reader. A source generator cannot depend on System.Text.Json, and we need exact
    /// line numbers plus duplicate-key detection (which System.Text.Json silently drops), so this is cheaper
    /// than shipping a dependency.
    /// </summary>
    internal abstract class JsonNode
    {
        public int Line { get; set; }
    }

    internal sealed class JsonString : JsonNode
    {
        public string Value { get; set; } = string.Empty;
    }

    internal sealed class JsonScalar : JsonNode
    {
    }

    internal sealed class JsonArray : JsonNode
    {
        public List<JsonNode> Items { get; } = new List<JsonNode>();
    }

    internal sealed class JsonObject : JsonNode
    {
        /// <summary>Members in file order; duplicates are kept so the caller can report them.</summary>
        public List<KeyValuePair<string, JsonNode>> Members { get; } = new List<KeyValuePair<string, JsonNode>>();

        public JsonNode? Get(string key)
        {
            foreach (var m in Members)
            {
                if (m.Key == key)
                {
                    return m.Value;
                }
            }

            return null;
        }
    }

    internal sealed class JsonParseException : Exception
    {
        public JsonParseException(string message, int line, int column)
            : base(message)
        {
            Line = line;
            Column = column;
        }

        public int Line { get; }

        public int Column { get; }
    }

    internal sealed class MiniJson
    {
        private readonly string _s;
        private int _i;
        private int _line = 1;
        private int _lineStart;

        private MiniJson(string text)
        {
            _s = text;
        }

        public static JsonNode Parse(string text)
        {
            var p = new MiniJson(text);
            p.SkipWs();
            var v = p.ParseValue(0);
            p.SkipWs();
            if (p._i != p._s.Length)
            {
                throw p.Error("Unexpected content after the end of the JSON document.");
            }

            return v;
        }

        private JsonParseException Error(string message) =>
            new JsonParseException(message, _line, _i - _lineStart + 1);

        private void SkipWs()
        {
            while (_i < _s.Length)
            {
                var c = _s[_i];
                if (c == '\n')
                {
                    _i++;
                    _line++;
                    _lineStart = _i;
                }
                else if (c == ' ' || c == '\t' || c == '\r' || c == '﻿')
                {
                    _i++;
                }
                else
                {
                    break;
                }
            }
        }

        private JsonNode ParseValue(int depth)
        {
            if (depth > 64)
            {
                throw Error("JSON is nested too deeply.");
            }

            if (_i >= _s.Length)
            {
                throw Error("Unexpected end of JSON.");
            }

            var line = _line;
            JsonNode node;
            var c = _s[_i];
            if (c == '{')
            {
                node = ParseObject(depth);
            }
            else if (c == '[')
            {
                node = ParseArray(depth);
            }
            else if (c == '"')
            {
                node = new JsonString { Value = ParseString() };
            }
            else if (c == '-' || (c >= '0' && c <= '9'))
            {
                ParseNumber();
                node = new JsonScalar();
            }
            else if (Literal("true") || Literal("false") || Literal("null"))
            {
                node = new JsonScalar();
            }
            else
            {
                throw Error("Unexpected character '" + c + "'.");
            }

            node.Line = line;
            return node;
        }

        private bool Literal(string word)
        {
            if (string.CompareOrdinal(_s, _i, word, 0, word.Length) == 0)
            {
                _i += word.Length;
                return true;
            }

            return false;
        }

        private void ParseNumber()
        {
            var start = _i;
            if (_s[_i] == '-')
            {
                _i++;
            }

            var digits = 0;
            while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9')
            {
                _i++;
                digits++;
            }

            if (digits == 0)
            {
                _i = start;
                throw Error("Invalid number.");
            }

            if (_i < _s.Length && _s[_i] == '.')
            {
                _i++;
                var frac = 0;
                while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9')
                {
                    _i++;
                    frac++;
                }

                if (frac == 0)
                {
                    throw Error("Invalid number.");
                }
            }

            if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
            {
                _i++;
                if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-'))
                {
                    _i++;
                }

                var exp = 0;
                while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9')
                {
                    _i++;
                    exp++;
                }

                if (exp == 0)
                {
                    throw Error("Invalid number.");
                }
            }
        }

        private JsonObject ParseObject(int depth)
        {
            var obj = new JsonObject();
            _i++; // {
            SkipWs();
            if (_i < _s.Length && _s[_i] == '}')
            {
                _i++;
                return obj;
            }

            while (true)
            {
                SkipWs();
                if (_i >= _s.Length || _s[_i] != '"')
                {
                    throw Error("Expected a quoted property name.");
                }

                var key = ParseString();
                SkipWs();
                if (_i >= _s.Length || _s[_i] != ':')
                {
                    throw Error("Expected ':' after property name.");
                }

                _i++;
                SkipWs();
                var value = ParseValue(depth + 1);
                obj.Members.Add(new KeyValuePair<string, JsonNode>(key, value));
                SkipWs();
                if (_i >= _s.Length)
                {
                    throw Error("Unexpected end of JSON inside an object.");
                }

                if (_s[_i] == ',')
                {
                    _i++;
                    continue;
                }

                if (_s[_i] == '}')
                {
                    _i++;
                    return obj;
                }

                throw Error("Expected ',' or '}' in object.");
            }
        }

        private JsonArray ParseArray(int depth)
        {
            var arr = new JsonArray();
            _i++; // [
            SkipWs();
            if (_i < _s.Length && _s[_i] == ']')
            {
                _i++;
                return arr;
            }

            while (true)
            {
                SkipWs();
                arr.Items.Add(ParseValue(depth + 1));
                SkipWs();
                if (_i >= _s.Length)
                {
                    throw Error("Unexpected end of JSON inside an array.");
                }

                if (_s[_i] == ',')
                {
                    _i++;
                    continue;
                }

                if (_s[_i] == ']')
                {
                    _i++;
                    return arr;
                }

                throw Error("Expected ',' or ']' in array.");
            }
        }

        private string ParseString()
        {
            _i++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (_i >= _s.Length)
                {
                    throw Error("Unterminated string.");
                }

                var c = _s[_i++];
                if (c == '"')
                {
                    return sb.ToString();
                }

                if (c < ' ')
                {
                    _i--;
                    throw Error("Control character in string (escape it).");
                }

                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (_i >= _s.Length)
                {
                    throw Error("Unterminated escape sequence.");
                }

                var e = _s[_i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (_i + 4 > _s.Length
                            || !int.TryParse(_s.Substring(_i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                        {
                            throw Error("Invalid \\u escape.");
                        }

                        sb.Append((char)code);
                        _i += 4;
                        break;
                    default:
                        _i--;
                        throw Error("Invalid escape '\\" + e + "'.");
                }
            }
        }
    }
}
