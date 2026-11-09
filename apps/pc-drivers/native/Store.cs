using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace PCDrivers
{
    internal sealed class Store
    {
        internal readonly string Root, Data;
        internal static readonly Encoding Utf8 = new UTF8Encoding(false);
        internal Store(string root) { Root = root; Data = Path.Combine(root, "Data"); Directory.CreateDirectory(Data); Directory.CreateDirectory(Path.Combine(Data, "Backups")); }
        internal static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024, RecursionLimit = 64 }; }
        internal static string Json(object value) { return Serializer().Serialize(value); }
        internal static Dictionary<string, object> Map(params object[] pairs) { var d = new Dictionary<string, object>(); for (int i = 0; i < pairs.Length; i += 2) d.Add((string)pairs[i], pairs[i + 1]); return d; }
        internal static object Get(Dictionary<string, object> d, string key) { object v; return d.TryGetValue(key, out v) ? v : null; }
        internal static string Text(Dictionary<string, object> d, string key) { return Convert.ToString(Get(d, key)); }
        internal Dictionary<string, object> Read(string name) { string p = Path.Combine(Data, name); return File.Exists(p) ? Serializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(p)) : new Dictionary<string, object>(); }
        internal void Write(string name, object value) { WritePath(Path.Combine(Data, name), value); }
        internal static void WritePath(string path, object value) { string t = path + ".new"; using (var f = new FileStream(t, FileMode.Create, FileAccess.Write, FileShare.None)) { byte[] b = Utf8.GetBytes(Json(value)); f.Write(b, 0, b.Length); f.Flush(true); } if (File.Exists(path)) File.Replace(t, path, path + ".previous", true); else File.Move(t, path); }
        internal static string Hash(Stream stream) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(stream)).Replace("-", ""); }
    }
}
