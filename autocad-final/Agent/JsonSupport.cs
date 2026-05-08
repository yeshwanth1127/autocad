using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace autocad_final.Agent
{
    /// <summary>
    /// JSON helpers that do not depend on System.Web.Extensions (AutoCAD host may not load that assembly).
    /// </summary>
    public static class JsonSupport
    {
        private static readonly DataContractJsonSerializerSettings DcsSettings = new DataContractJsonSerializerSettings
        {
            UseSimpleDictionaryFormat = true
        };

        /// <summary>Deserializes JSON from tool arguments that include nested arrays/objects (e.g. cuts[]).</summary>
        public static T DeserializeDataContract<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return default;
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                var serializer = new DataContractJsonSerializer(typeof(T), DcsSettings);
                return (T)serializer.ReadObject(ms);
            }
        }

        public static string Serialize(object value)
        {
            if (value == null)
                return "null";

            var type = value.GetType();
            if (Attribute.IsDefined(type, typeof(DataContractAttribute), false))
                return SerializeDataContract(value, type);

            return SerializeReflection(value, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
        }

        public static Dictionary<string, object> DeserializeDictionary(string json)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(json))
                return result;

            // Flat tool-call arguments: "key": "value" | number | bool | null
            const string pattern = "\"(?<k>[a-zA-Z0-9_]+)\"\\s*:\\s*(\"(?<s>(?:\\\\.|[^\"\\\\])*)\"|(?<n>-?\\d+(?:\\.\\d+)?)|(?<b>true|false)|null)";
            foreach (Match m in Regex.Matches(json, pattern, RegexOptions.CultureInvariant))
            {
                var k = m.Groups["k"].Value;
                if (m.Groups["s"].Success)
                    result[k] = UnescapeJsonString(m.Groups["s"].Value);
                else if (m.Groups["n"].Success)
                    result[k] = double.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
                else if (m.Groups["b"].Success)
                    result[k] = m.Groups["b"].Value == "true";
                else
                    result[k] = null;
            }

            return result;
        }

        private static string UnescapeJsonString(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;

            var sb = new StringBuilder(s.Length);
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length)
                {
                    sb.Append(s[i]);
                    continue;
                }

                i++;
                switch (s[i])
                {
                    case '"':
                    case '\\':
                    case '/':
                        sb.Append(s[i]);
                        break;
                    case 'b':
                        sb.Append('\b');
                        break;
                    case 'f':
                        sb.Append('\f');
                        break;
                    case 'n':
                        sb.Append('\n');
                        break;
                    case 'r':
                        sb.Append('\r');
                        break;
                    case 't':
                        sb.Append('\t');
                        break;
                    case 'u' when i + 4 < s.Length:
                        var hex = s.Substring(i + 1, 4);
                        if (ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u))
                        {
                            sb.Append((char)u);
                            i += 4;
                        }
                        break;
                    default:
                        sb.Append(s[i]);
                        break;
                }
            }

            return sb.ToString();
        }

        private static string SerializeDataContract(object value, Type type)
        {
            using (var ms = new MemoryStream())
            {
                var serializer = new DataContractJsonSerializer(type, DcsSettings);
                serializer.WriteObject(ms, value);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        private const int MaxDepth = 48;

        private static string SerializeReflection(object value, int depth, HashSet<object> seen)
        {
            if (value == null)
                return "null";

            if (depth > MaxDepth)
                return "null";

            var type = value.GetType();
            if (Attribute.IsDefined(type, typeof(DataContractAttribute), false))
                return SerializeDataContract(value, type);

            if (value is string s)
                return EscapeJsonString(s);

            if (value is char c)
                return EscapeJsonString(c.ToString());

            if (value is bool b)
                return b ? "true" : "false";

            if (value is DateTime dt)
                return EscapeJsonString(dt.ToString("o", CultureInfo.InvariantCulture));

            if (value is Guid g)
                return EscapeJsonString(g.ToString());

            switch (Type.GetTypeCode(type))
            {
                case TypeCode.SByte:
                case TypeCode.Byte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Single:
                case TypeCode.Double:
                case TypeCode.Decimal:
                    return Convert.ToString(value, CultureInfo.InvariantCulture);
                case TypeCode.Empty:
                case TypeCode.DBNull:
                    return "null";
            }

            if (type.IsEnum)
                return Convert.ToString(Convert.ChangeType(value, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

            if (value is IDictionary dict && !(value is string))
            {
                var sb = new StringBuilder();
                sb.Append('{');
                var first = true;
                foreach (DictionaryEntry entry in dict)
                {
                    if (!first)
                        sb.Append(',');
                    first = false;
                    sb.Append(EscapeJsonString(Convert.ToString(entry.Key, CultureInfo.InvariantCulture)));
                    sb.Append(':');
                    sb.Append(SerializeReflection(entry.Value, depth + 1, seen));
                }
                sb.Append('}');
                return sb.ToString();
            }

            if (value is IEnumerable enumerable && !(value is string))
            {
                var sb = new StringBuilder();
                sb.Append('[');
                var first = true;
                foreach (var item in enumerable)
                {
                    if (!first)
                        sb.Append(',');
                    first = false;
                    sb.Append(SerializeReflection(item, depth + 1, seen));
                }
                sb.Append(']');
                return sb.ToString();
            }

            if (type.IsClass && !type.IsPrimitive && seen.Contains(value))
                return "null";

            if (type.IsClass && !type.IsPrimitive)
                seen.Add(value);

            try
            {
                var props = type.GetProperties(BindingFlags.Instance | BindingFlags.Public);

                // When ANY property on this type carries [DataMember], treat it as opt-in:
                // only serialize [DataMember] properties and skip the rest.  This lets
                // classes like OpenRouterMessage have private backing properties (Content,
                // ContentParts) alongside their public serialized surface (ContentForSerialization).
                bool hasAnyDataMember = false;
                foreach (var p2 in props)
                    if (p2.GetCustomAttributes(typeof(DataMemberAttribute), false).Length > 0)
                    { hasAnyDataMember = true; break; }

                var sb = new StringBuilder();
                sb.Append('{');
                var first = true;
                foreach (var p in props)
                {
                    if (!p.CanRead || p.GetIndexParameters().Length > 0)
                        continue;

                    // Use [DataMember(Name=...)] when present for correct snake_case JSON names.
                    var dmAttrs = p.GetCustomAttributes(typeof(DataMemberAttribute), false);
                    var dm = dmAttrs.Length > 0 ? (DataMemberAttribute)dmAttrs[0] : null;

                    // Skip properties without [DataMember] on types that use opt-in serialization.
                    if (hasAnyDataMember && dm == null)
                        continue;

                    var v = p.GetValue(value, null);

                    // Honor EmitDefaultValue=false: skip property when value is null or default.
                    if (dm != null && !dm.EmitDefaultValue)
                    {
                        if (v == null) continue;
                        if (p.PropertyType.IsValueType &&
                            v.Equals(Activator.CreateInstance(p.PropertyType))) continue;
                    }

                    var jsonName = dm?.Name ?? p.Name;

                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(EscapeJsonString(jsonName));
                    sb.Append(':');
                    sb.Append(SerializeReflection(v, depth + 1, seen));
                }
                sb.Append('}');
                return sb.ToString();
            }
            finally
            {
                if (type.IsClass && !type.IsPrimitive)
                    seen.Remove(value);
            }
        }

        private static string EscapeJsonString(string s)
        {
            if (s == null)
                return "\"\"";

            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (ch < ' ')
                            sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", (int)ch);
                        else
                            sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

            bool IEqualityComparer<object>.Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            int IEqualityComparer<object>.GetHashCode(object obj)
            {
                return RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
