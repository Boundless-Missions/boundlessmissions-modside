/*
 * MiniJSON.cs – Minimal JSON parser for Unity/KSP mods.
 *
 * Based on the widely-used MiniJSON by Calvin Rien (MIT License).
 * Handles Dictionary<string, object>, List<object>, string, double, long, bool, null.
 * Unity's JsonUtility cannot parse arbitrary JSON — this fills that gap.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace GeneKerman
{
    public static class MiniJSON
    {
        public static object Deserialize(string json)
        {
            if (json == null) return null;
            return Parser.Parse(json);
        }

        public static Dictionary<string, object> DeserializeDict(string json)
        {
            return Deserialize(json) as Dictionary<string, object>;
        }

        public static List<object> DeserializeList(string json)
        {
            return Deserialize(json) as List<object>;
        }

        public static string Serialize(object obj)
        {
            return Serializer.Serialize(obj);
        }

        // Helper accessors
        public static string GetString(Dictionary<string, object> dict, string key, string def = "")
        {
            if (dict != null && dict.TryGetValue(key, out var v) && v != null)
                return v.ToString();
            return def;
        }

        public static int GetInt(Dictionary<string, object> dict, string key, int def = 0)
        {
            if (dict != null && dict.TryGetValue(key, out var v) && v != null)
            {
                if (v is long l) return (int)l;
                if (v is double d) return (int)d;
                if (int.TryParse(v.ToString(), out int i)) return i;
            }
            return def;
        }

        public static double GetDouble(Dictionary<string, object> dict, string key, double def = 0)
        {
            if (dict != null && dict.TryGetValue(key, out var v) && v != null)
            {
                if (v is double d) return d;
                if (v is long l) return l;
                if (double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double r)) return r;
            }
            return def;
        }

        public static bool GetBool(Dictionary<string, object> dict, string key, bool def = false)
        {
            if (dict != null && dict.TryGetValue(key, out var v) && v != null)
            {
                if (v is bool b) return b;
                return v.ToString().ToLower() == "true";
            }
            return def;
        }

        public static List<object> GetList(Dictionary<string, object> dict, string key)
        {
            if (dict != null && dict.TryGetValue(key, out var v) && v is List<object> l)
                return l;
            return new List<object>();
        }

        public static Dictionary<string, object> GetDict(Dictionary<string, object> dict, string key)
        {
            if (dict != null && dict.TryGetValue(key, out var v) && v is Dictionary<string, object> d)
                return d;
            return null;
        }

        sealed class Parser : IDisposable
        {
            const string WORD_BREAK = "{}[],:\"";
            StringReader json;

            Parser(string jsonString) { json = new StringReader(jsonString); }

            public static object Parse(string jsonString)
            {
                using (var p = new Parser(jsonString))
                    return p.ParseValue();
            }

            public void Dispose() { json.Dispose(); }

            char PeekChar { get { return Convert.ToChar(json.Peek()); } }
            char NextChar { get { return Convert.ToChar(json.Read()); } }
            string NextWord
            {
                get
                {
                    var sb = new StringBuilder();
                    while (!IsWordBreak(PeekChar))
                    {
                        sb.Append(NextChar);
                        if (json.Peek() == -1) break;
                    }
                    return sb.ToString();
                }
            }

            bool IsWordBreak(char c) { return Char.IsWhiteSpace(c) || WORD_BREAK.IndexOf(c) != -1; }

            enum TOKEN { NONE, CURLY_OPEN, CURLY_CLOSE, SQUARED_OPEN, SQUARED_CLOSE, COLON, COMMA, STRING, NUMBER, TRUE, FALSE, NULL }

            TOKEN NextToken
            {
                get
                {
                    EatWhitespace();
                    if (json.Peek() == -1) return TOKEN.NONE;
                    switch (PeekChar)
                    {
                        case '{': json.Read(); return TOKEN.CURLY_OPEN;
                        case '}': json.Read(); return TOKEN.CURLY_CLOSE;
                        case '[': json.Read(); return TOKEN.SQUARED_OPEN;
                        case ']': json.Read(); return TOKEN.SQUARED_CLOSE;
                        case ',': json.Read(); return TOKEN.COMMA;
                        case '"': return TOKEN.STRING;
                        case ':': json.Read(); return TOKEN.COLON;
                        case '-': case '0': case '1': case '2': case '3': case '4':
                        case '5': case '6': case '7': case '8': case '9':
                            return TOKEN.NUMBER;
                    }
                    switch (NextWord)
                    {
                        case "false": return TOKEN.FALSE;
                        case "true": return TOKEN.TRUE;
                        case "null": return TOKEN.NULL;
                    }
                    return TOKEN.NONE;
                }
            }

            void EatWhitespace() { while (json.Peek() != -1 && Char.IsWhiteSpace(PeekChar)) json.Read(); }

            object ParseValue()
            {
                switch (NextToken)
                {
                    case TOKEN.STRING: return ParseString();
                    case TOKEN.NUMBER: return ParseNumber();
                    case TOKEN.CURLY_OPEN: return ParseObject();
                    case TOKEN.SQUARED_OPEN: return ParseArray();
                    case TOKEN.TRUE: return true;
                    case TOKEN.FALSE: return false;
                    case TOKEN.NULL: return null;
                    default: return null;
                }
            }

            Dictionary<string, object> ParseObject()
            {
                var table = new Dictionary<string, object>();
                while (true)
                {
                    switch (NextToken)
                    {
                        case TOKEN.NONE: return null;
                        case TOKEN.CURLY_CLOSE: return table;
                        case TOKEN.COMMA: continue;
                        default:
                            string name = ParseString();
                            if (name == null) return null;
                            if (NextToken != TOKEN.COLON) return null;
                            table[name] = ParseValue();
                            break;
                    }
                }
            }

            List<object> ParseArray()
            {
                var array = new List<object>();
                while (true)
                {
                    var token = NextToken;
                    switch (token)
                    {
                        case TOKEN.NONE: return null;
                        case TOKEN.SQUARED_CLOSE: return array;
                        case TOKEN.COMMA: continue;
                        default:
                            // Re-read the value since NextToken consumed characters for non-string/non-number
                            if (token == TOKEN.STRING) array.Add(ParseString());
                            else if (token == TOKEN.NUMBER) array.Add(ParseNumber());
                            else if (token == TOKEN.CURLY_OPEN) array.Add(ParseObject());
                            else if (token == TOKEN.SQUARED_OPEN) array.Add(ParseArray());
                            else if (token == TOKEN.TRUE) array.Add(true);
                            else if (token == TOKEN.FALSE) array.Add(false);
                            else if (token == TOKEN.NULL) array.Add(null);
                            break;
                    }
                }
            }

            string ParseString()
            {
                var sb = new StringBuilder();
                json.Read(); // opening "
                bool parsing = true;
                while (parsing)
                {
                    if (json.Peek() == -1) break;
                    char c = NextChar;
                    switch (c)
                    {
                        case '"': parsing = false; break;
                        case '\\':
                            if (json.Peek() == -1) { parsing = false; break; }
                            c = NextChar;
                            switch (c)
                            {
                                case '"': case '\\': case '/': sb.Append(c); break;
                                case 'b': sb.Append('\b'); break;
                                case 'f': sb.Append('\f'); break;
                                case 'n': sb.Append('\n'); break;
                                case 'r': sb.Append('\r'); break;
                                case 't': sb.Append('\t'); break;
                                case 'u':
                                    var hex = new char[4];
                                    for (int i = 0; i < 4; i++) hex[i] = NextChar;
                                    sb.Append((char)Convert.ToInt32(new string(hex), 16));
                                    break;
                            }
                            break;
                        default: sb.Append(c); break;
                    }
                }
                return sb.ToString();
            }

            object ParseNumber()
            {
                string number = NextWord;
                if (number.IndexOf('.') == -1 && number.IndexOf('E') == -1 && number.IndexOf('e') == -1)
                {
                    if (long.TryParse(number, NumberStyles.Any, CultureInfo.InvariantCulture, out long l))
                        return l;
                }
                if (double.TryParse(number, NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                    return d;
                return 0;
            }
        }

        sealed class Serializer
        {
            StringBuilder sb;
            Serializer() { sb = new StringBuilder(); }

            public static string Serialize(object obj)
            {
                var s = new Serializer();
                s.SerializeValue(obj);
                return s.sb.ToString();
            }

            void SerializeValue(object value)
            {
                if (value == null) { sb.Append("null"); return; }
                if (value is string s) { SerializeString(s); return; }
                if (value is bool b) { sb.Append(b ? "true" : "false"); return; }
                if (value is IDictionary dict) { SerializeObject(dict); return; }
                if (value is IList list) { SerializeArray(list); return; }
                if (value is char c) { SerializeString(c.ToString()); return; }
                SerializeOther(value);
            }

            void SerializeObject(IDictionary obj)
            {
                bool first = true;
                sb.Append('{');
                foreach (object e in obj.Keys)
                {
                    if (!first) sb.Append(',');
                    SerializeString(e.ToString());
                    sb.Append(':');
                    SerializeValue(obj[e]);
                    first = false;
                }
                sb.Append('}');
            }

            void SerializeArray(IList array)
            {
                sb.Append('[');
                bool first = true;
                for (int i = 0; i < array.Count; i++)
                {
                    if (!first) sb.Append(',');
                    SerializeValue(array[i]);
                    first = false;
                }
                sb.Append(']');
            }

            void SerializeString(string str)
            {
                sb.Append('"');
                foreach (char c in str)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < ' ') { sb.AppendFormat("\\u{0:X4}", (int)c); }
                            else { sb.Append(c); }
                            break;
                    }
                }
                sb.Append('"');
            }

            void SerializeOther(object value)
            {
                if (value is float f) { sb.Append(f.ToString("R", CultureInfo.InvariantCulture)); }
                else if (value is double d) { sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); }
                else if (value is int || value is long || value is short || value is byte ||
                         value is uint || value is ulong || value is ushort || value is sbyte)
                { sb.Append(value); }
                else { SerializeString(value.ToString()); }
            }
        }
    }
}
