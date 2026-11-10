using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GameImporter
{
    // Kleiner JSON-Leser fuer die Spielpakete: Objekte -> Dictionary, Listen -> List<object>, Zahlen -> double
    public static class Json
    {
        public static object Parse(string s) { int i = 0; var v = Val(s, ref i); Ws(s, ref i); if (i < s.Length) throw new FormatException("JSON: Text nach dem Ende bei " + i); return v; }

        static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        static object Val(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("JSON: unerwartetes Ende");
            char c = s[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>(); i++; Ws(s, ref i);
                if (s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(s, ref i); var k = Str(s, ref i); Ws(s, ref i);
                    if (s[i] != ':') throw new FormatException("JSON: ':' erwartet bei " + i);
                    i++; d[k] = Val(s, ref i); Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return d; }
                    throw new FormatException("JSON: ',' oder '}' erwartet bei " + i);
                }
            }
            if (c == '[')
            {
                var l = new List<object>(); i++; Ws(s, ref i);
                if (s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Val(s, ref i)); Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return l; }
                    throw new FormatException("JSON: ',' oder ']' erwartet bei " + i);
                }
            }
            if (c == '"') return Str(s, ref i);
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (s.Length - i >= 5 && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (st == i) throw new FormatException("JSON: unerwartetes Zeichen '" + c + "' bei " + i);
            return double.Parse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static string Str(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("JSON: '\"' erwartet bei " + i);
            i++; var sb = new StringBuilder();
            while (s[i] != '"')
            {
                char c = s[i++];
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
            i++;
            return sb.ToString();
        }

        public static string Write(object v)
        {
            var sb = new StringBuilder(); W(sb, v); return sb.ToString();
        }

        static void W(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }
            var s = v as string;
            if (s != null)
            {
                sb.Append('"');
                foreach (var c in s)
                {
                    if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                    else if (c == '\n') sb.Append("\\n");
                    else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                }
                sb.Append('"'); return;
            }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is int || v is long || v is float || v is double) { sb.Append(Convert.ToDouble(v).ToString("R", CultureInfo.InvariantCulture)); return; }
            var d = v as IDictionary<string, object>;
            if (d != null)
            {
                sb.Append('{'); bool first = true;
                foreach (var kv in d) { if (!first) sb.Append(','); first = false; W(sb, kv.Key); sb.Append(':'); W(sb, kv.Value); }
                sb.Append('}'); return;
            }
            var l = v as System.Collections.IEnumerable;
            if (l != null)
            {
                sb.Append('['); bool first = true;
                foreach (var x in l) { if (!first) sb.Append(','); first = false; W(sb, x); }
                sb.Append(']'); return;
            }
            W(sb, v.ToString());
        }

        // ---- bequemer Zugriff ----
        public static Dictionary<string, object> O(object o, string k) { var d = o as Dictionary<string, object>; object v; return d != null && d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null; }
        public static List<object> L(object o, string k) { var d = o as Dictionary<string, object>; object v; return d != null && d.TryGetValue(k, out v) ? v as List<object> : null; }
        public static object V(object o, string k) { var d = o as Dictionary<string, object>; object v; return d != null && d.TryGetValue(k, out v) ? v : null; }
        public static string S(object o, string k, string def = null) { var v = V(o, k); return v == null ? def : (v is double ? ((double)v).ToString(CultureInfo.InvariantCulture) : v.ToString()); }
        public static float F(object o, string k, float def = 0f) { var v = V(o, k); return v is double ? (float)(double)v : def; }
        public static bool B(object o, string k) { var v = V(o, k); return v is bool && (bool)v; }
        public static bool Has(object o, string k) { var d = o as Dictionary<string, object>; return d != null && d.ContainsKey(k); }
    }
}
