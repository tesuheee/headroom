using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace Headroom
{
    static class Json
    {
        static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        public static Dictionary<string, object> ParseObject(string json)
        {
            try
            {
                return Serializer.DeserializeObject(json) as Dictionary<string, object>;
            }
            catch
            {
                return null;
            }
        }

        public static Dictionary<string, object> Object(Dictionary<string, object> obj, string key)
        {
            if (obj == null) return null;
            object value;
            if (!obj.TryGetValue(key, out value)) return null;
            return value as Dictionary<string, object>;
        }

        public static Dictionary<string, object> ObjectOrNew(Dictionary<string, object> obj, string key)
        {
            var nested = Object(obj, key);
            if (nested != null) return nested;
            nested = new Dictionary<string, object>();
            if (obj != null) obj[key] = nested;
            return nested;
        }

        public static string Serialize(object value)
        {
            return Serializer.Serialize(value);
        }

        public static string String(Dictionary<string, object> obj, string key)
        {
            if (obj == null) return null;
            object value;
            if (!obj.TryGetValue(key, out value) || value == null) return null;
            return value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public static double? Double(Dictionary<string, object> obj, string key)
        {
            if (obj == null) return null;
            object value;
            if (!obj.TryGetValue(key, out value) || value == null) return null;
            try
            {
                if (value is double) return (double)value;
                if (value is float) return (float)value;
                if (value is decimal) return (double)(decimal)value;
                if (value is int) return (int)value;
                if (value is long) return (long)value;
                string s = value as string;
                if (s != null)
                {
                    double parsed;
                    if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                        return parsed;
                }
            }
            catch
            {
            }
            return null;
        }

        public static long? Long(Dictionary<string, object> obj, string key)
        {
            if (obj == null) return null;
            object value;
            if (!obj.TryGetValue(key, out value) || value == null) return null;
            try
            {
                if (value is long) return (long)value;
                if (value is int) return (int)value;
                if (value is double) return Convert.ToInt64((double)value);
                if (value is decimal) return Convert.ToInt64((decimal)value);
                string s = value as string;
                if (s != null)
                {
                    long parsed;
                    if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                        return parsed;
                }
            }
            catch
            {
            }
            return null;
        }

        public static bool? Bool(Dictionary<string, object> obj, string key)
        {
            if (obj == null) return null;
            object value;
            if (!obj.TryGetValue(key, out value) || value == null) return null;
            if (value is bool) return (bool)value;
            string s = value as string;
            if (s != null)
            {
                bool parsed;
                if (bool.TryParse(s, out parsed)) return parsed;
            }
            return null;
        }
    }
}
