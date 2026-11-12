// Небольшие помощники для разбора JSON (JavaScriptSerializer отдаёт Dictionary<string,object> / object[] / ArrayList).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace AkeeLauncher
{
    static class J
    {
        [ThreadStatic] static JavaScriptSerializer ser;

        public static JavaScriptSerializer Ser
        {
            get
            {
                if (ser == null) ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };
                return ser;
            }
        }

        public static object Parse(string s) { return Ser.DeserializeObject(s); }
        public static Dictionary<string, object> ParseObj(string s) { return O(Parse(s)); }
        public static string Write(object o) { return Ser.Serialize(o); }
        public static T Convert<T>(object o) { return Ser.ConvertToType<T>(o); }

        public static Dictionary<string, object> O(object o) { return o as Dictionary<string, object>; }

        public static Dictionary<string, object> O(Dictionary<string, object> d, string k)
        {
            if (d == null) return null;
            object v;
            return d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null;
        }

        public static List<object> A(object o)
        {
            var r = new List<object>();
            if (o == null || o is string || o is IDictionary) return r;
            var e = o as IEnumerable;
            if (e != null) foreach (var x in e) r.Add(x);
            return r;
        }

        public static List<object> A(Dictionary<string, object> d, string k)
        {
            if (d == null) return new List<object>();
            object v;
            return d.TryGetValue(k, out v) ? A(v) : new List<object>();
        }

        public static string S(Dictionary<string, object> d, string k)
        {
            if (d == null) return null;
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return null;
            return System.Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static string S(Dictionary<string, object> d, string k, string def)
        {
            string s = S(d, k);
            return string.IsNullOrEmpty(s) ? def : s;
        }

        public static long L(Dictionary<string, object> d, string k)
        {
            string s = S(d, k);
            long r;
            return s != null && long.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out r) ? r : 0;
        }

        public static bool B(Dictionary<string, object> d, string k)
        {
            if (d == null) return false;
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return false;
            if (v is bool) return (bool)v;
            return string.Equals(v.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        }

        public static bool Has(Dictionary<string, object> d, string k) { return d != null && d.ContainsKey(k) && d[k] != null; }

        // Сборка словаря: J.D("a", 1, "b", "x")
        public static Dictionary<string, object> D(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }
    }
}
