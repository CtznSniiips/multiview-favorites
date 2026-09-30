using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Emby.MultiviewFavorites.Sync
{
    /// <summary>
    /// Tiny, dependency-free JSON reader/writer.
    ///
    /// Dispatcharr's settings blob is a free-form dictionary of mixed value types,
    /// which is awkward to model with typed DTOs. Emby's own serializer changes
    /// implementation between server versions, and referencing System.Text.Json
    /// directly from a netstandard plugin collides with the runtime copy Emby ships,
    /// so this keeps the plugin self-contained.
    ///
    /// Parse output: Dictionary&lt;string,object&gt;, List&lt;object&gt;, string, double, bool, null.
    /// </summary>
    public static class MiniJson
    {
        public static object Parse(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            var p = new Parser(json);
            p.SkipWs();
            var value = p.ReadValue();
            p.SkipWs();
            if (!p.AtEnd) throw new FormatException("Unexpected trailing characters in JSON at " + p.Pos);
            return value;
        }

        public static string Serialize(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value);
            return sb.ToString();
        }

        // ------------------------------------------------------------ writer

        private static void Write(StringBuilder sb, object v)
        {
            switch (v)
            {
                case null:
                    sb.Append("null");
                    return;
                case string s:
                    WriteString(sb, s);
                    return;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    return;
                case int _:
                case long _:
                case short _:
                case byte _:
                case uint _:
                case ulong _:
                case decimal _:
                    sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                    return;
                case double d:
                    sb.Append(FormatDouble(d));
                    return;
                case float f:
                    sb.Append(FormatDouble(f));
                    return;
                case IDictionary<string, object> dict:
                {
                    sb.Append('{');
                    var first = true;
                    foreach (var kv in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, kv.Key);
                        sb.Append(':');
                        Write(sb, kv.Value);
                    }
                    sb.Append('}');
                    return;
                }
                case IEnumerable seq:
                {
                    sb.Append('[');
                    var first = true;
                    foreach (var item in seq)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        Write(sb, item);
                    }
                    sb.Append(']');
                    return;
                }
                default:
                    WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
                    return;
            }
        }

        private static string FormatDouble(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "null";
            if (d == Math.Floor(d) && Math.Abs(d) < 1e15) return ((long)d).ToString(CultureInfo.InvariantCulture);
            return d.ToString("R", CultureInfo.InvariantCulture);
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ------------------------------------------------------------ reader

        private sealed class Parser
        {
            private readonly string _s;
            public int Pos;

            public Parser(string s) { _s = s; }

            public bool AtEnd => Pos >= _s.Length;

            public void SkipWs()
            {
                while (Pos < _s.Length && char.IsWhiteSpace(_s[Pos])) Pos++;
            }

            private char Peek()
            {
                if (Pos >= _s.Length) throw new FormatException("Unexpected end of JSON");
                return _s[Pos];
            }

            private void Expect(char c)
            {
                if (Peek() != c) throw new FormatException($"Expected '{c}' at {Pos}, found '{_s[Pos]}'");
                Pos++;
            }

            public object ReadValue()
            {
                SkipWs();
                var c = Peek();
                switch (c)
                {
                    case '{': return ReadObject();
                    case '[': return ReadArray();
                    case '"': return ReadString();
                    case 't': ReadLiteral("true"); return true;
                    case 'f': ReadLiteral("false"); return false;
                    case 'n': ReadLiteral("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                        throw new FormatException($"Unexpected character '{c}' at {Pos}");
                }
            }

            private void ReadLiteral(string lit)
            {
                if (string.CompareOrdinal(_s, Pos, lit, 0, lit.Length) != 0)
                    throw new FormatException($"Invalid literal at {Pos}");
                Pos += lit.Length;
            }

            private Dictionary<string, object> ReadObject()
            {
                var d = new Dictionary<string, object>(StringComparer.Ordinal);
                Expect('{');
                SkipWs();
                if (Peek() == '}') { Pos++; return d; }
                while (true)
                {
                    SkipWs();
                    var key = ReadString();
                    SkipWs();
                    Expect(':');
                    d[key] = ReadValue();
                    SkipWs();
                    if (Peek() == ',') { Pos++; continue; }
                    Expect('}');
                    return d;
                }
            }

            private List<object> ReadArray()
            {
                var list = new List<object>();
                Expect('[');
                SkipWs();
                if (Peek() == ']') { Pos++; return list; }
                while (true)
                {
                    list.Add(ReadValue());
                    SkipWs();
                    if (Peek() == ',') { Pos++; continue; }
                    Expect(']');
                    return list;
                }
            }

            private string ReadString()
            {
                Expect('"');
                var sb = new StringBuilder();
                while (true)
                {
                    var c = Peek();
                    Pos++;
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    var e = Peek();
                    Pos++;
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
                            if (Pos + 4 > _s.Length) throw new FormatException("Bad \\u escape");
                            sb.Append((char)int.Parse(_s.Substring(Pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            Pos += 4;
                            break;
                        default: throw new FormatException($"Bad escape '\\{e}' at {Pos}");
                    }
                }
            }

            private double ReadNumber()
            {
                var start = Pos;
                if (Peek() == '-') Pos++;
                while (Pos < _s.Length && "0123456789.eE+-".IndexOf(_s[Pos]) >= 0) Pos++;
                var text = _s.Substring(start, Pos - start);
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    throw new FormatException($"Invalid number '{text}' at {start}");
                return d;
            }
        }

        // ------------------------------------------------------------ helpers

        public static Dictionary<string, object> AsObject(object v) => v as Dictionary<string, object>;

        public static List<object> AsArray(object v) => v as List<object>;

        public static object Get(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            return d.TryGetValue(key, out var v) ? v : null;
        }

        public static string GetString(Dictionary<string, object> d, string key) => ToCanonical(Get(d, key));

        /// <summary>
        /// Canonical string form used for change detection, so 4, 4.0 and "4" all compare equal.
        /// null stays null.
        /// </summary>
        public static string ToCanonical(object v)
        {
            switch (v)
            {
                case null: return null;
                case string s: return s;
                case bool b: return b ? "true" : "false";
                case double d: return FormatDouble(d);
                case float f: return FormatDouble(f);
                case int _:
                case long _:
                case decimal _:
                    return Convert.ToString(v, CultureInfo.InvariantCulture);
                default:
                    return Serialize(v);
            }
        }
    }
}
