// Список версий Mojang (только релизы), загрузка JSON версии, профили загрузчиков и слияние (inheritsFrom), правила (rules) для Windows x64.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AkeeLauncher
{
    public class VersionInfo
    {
        public Dictionary<string, object> Json;   // итоговый (слитый) JSON
        public string Id;                         // id профиля (для Fabric — fabric-loader-…)
        public string BaseId;                     // ванильная версия (её .jar и natives)
        public string Loader = "vanilla";
        public string LoaderVersion = "";
        public string MainClass { get { return J.S(Json, "mainClass"); } }
        public string Type { get { return J.S(Json, "type", "release"); } }
        public string JavaComponent { get { return J.S(J.O(Json, "javaVersion"), "component", "jre-legacy"); } }
        public int JavaMajor { get { long v = J.L(J.O(Json, "javaVersion"), "majorVersion"); return v > 0 ? (int)v : 8; } }
    }

    static class Versions
    {
        public const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
        

        static Dictionary<string, object> manifest;
        static DateTime manifestAt;
        static readonly object gate = new object();

        public static Dictionary<string, object> Manifest()
        {
            lock (gate)
            {
                if (manifest != null && (DateTime.UtcNow - manifestAt).TotalMinutes < 10) return manifest;
                string cache = Path.Combine(Paths.Versions, "version_manifest_v2.json");
                try
                {
                    string text = Http.GetRetry(ManifestUrl);
                    manifest = J.ParseObj(text);
                    Directory.CreateDirectory(Paths.Versions);
                    File.WriteAllText(cache, text, Encoding.UTF8);
                }
                catch
                {
                    if (!File.Exists(cache)) throw;
                    manifest = J.ParseObj(File.ReadAllText(cache, Encoding.UTF8));   // офлайн: последний сохранённый список
                }
                manifestAt = DateTime.UtcNow;
                return manifest;
            }
        }

        // Для интерфейса: [{id, type, releaseTime}] — только релизы, от новых к старым, до OldestVersion включительно.
        // Самая старая версия в списке: лаунчер поддерживает 1.20 и новее.
        public const string OldestVersion = "1.20";

        public static List<object> List()
        {
            var r = new List<object>();
            foreach (var o in J.A(Manifest(), "versions"))   // в манифесте от новых к старым
            {
                var v = J.O(o);
                string type = J.S(v, "type"), id = J.S(v, "id");
                if (type == "release") r.Add(J.D("id", id, "type", type, "releaseTime", J.S(v, "releaseTime")));
                if (id == OldestVersion) break;
            }
            return r;
        }

        public static string LatestRelease() { return J.S(J.O(Manifest(), "latest"), "release"); }

        public static List<object> FabricLoaders(string mc) { return Loaders.List("fabric", mc); }

        public static Dictionary<string, object> LoadVanilla(string id)
        {
            string file = Path.Combine(Paths.Versions, id, id + ".json");
            Dictionary<string, object> entry = null;
            try
            {
                foreach (var o in J.A(Manifest(), "versions"))
                    if (J.S(J.O(o), "id") == id) { entry = J.O(o); break; }
            }
            catch { if (!File.Exists(file)) throw; }
            if (entry != null)
                Downloader.Run(new List<DownloadJob> { new DownloadJob(J.S(entry, "url"), file, J.S(entry, "sha1"), 0) }, "Версия " + id);
            else if (!File.Exists(file)) throw new Exception("Версия " + id + " не найдена в списке Mojang");
            return J.ParseObj(File.ReadAllText(file, Encoding.UTF8));
        }

        public static VersionInfo Resolve(string mc, string loader, string loaderVersion)
        {
            var vi = new VersionInfo { BaseId = mc };
            var vanilla = LoadVanilla(mc);
            if (loader == null || loader == "vanilla" || !Loaders.IsKnown(loader)) { vi.Json = vanilla; vi.Id = mc; return vi; }

            if (string.IsNullOrEmpty(loaderVersion))
            {
                loaderVersion = Loaders.DefaultVersion(loader, mc);
                if (string.IsNullOrEmpty(loaderVersion)) throw new Exception(Loaders.Title(loader) + " не поддерживает версию " + mc);
            }
            string pid;
            var child = Loaders.Profile(loader, mc, loaderVersion, vanilla, out pid);
            vi.Json = Merge(vanilla, child);
            vi.Id = pid;
            vi.Loader = loader;
            vi.LoaderVersion = loaderVersion;
            return vi;
        }

        // Наследование: дочерний профиль поверх родителя. Библиотеки дочернего — первыми, дубликаты по group:artifact:classifier убираются.
        public static Dictionary<string, object> Merge(Dictionary<string, object> parent, Dictionary<string, object> child)
        {
            var r = new Dictionary<string, object>(parent);
            foreach (var kv in child)
            {
                if (kv.Key == "libraries" || kv.Key == "arguments" || kv.Key == "inheritsFrom") continue;
                var d = kv.Value as Dictionary<string, object>;
                if (d != null && d.Count == 0) continue;   // Forge пишет "logging": {} — оставляем ванильный конфиг
                r[kv.Key] = kv.Value;
            }

            var libs = new List<object>();
            var keys = new HashSet<string>();
            foreach (var l in J.A(child, "libraries")) if (keys.Add(LibKey(J.S(J.O(l), "name")))) libs.Add(l);
            foreach (var l in J.A(parent, "libraries")) if (keys.Add(LibKey(J.S(J.O(l), "name")))) libs.Add(l);
            r["libraries"] = libs.ToArray();

            var pa = J.O(parent, "arguments");
            var ca = J.O(child, "arguments");
            if (pa != null || ca != null)
            {
                var game = new List<object>(J.A(pa, "game")); game.AddRange(J.A(ca, "game"));
                var jvm = new List<object>(J.A(pa, "jvm")); jvm.AddRange(J.A(ca, "jvm"));
                r["arguments"] = J.D("game", game.ToArray(), "jvm", jvm.ToArray());
            }
            return r;
        }

        static string LibKey(string name)
        {
            var p = (name ?? "").Split('@')[0].Split(':');
            if (p.Length < 3) return name ?? "";
            return p[0] + ":" + p[1] + (p.Length > 3 ? ":" + p[3] : "");
        }

        // Путь в репозитории Maven: group:artifact:version[:classifier][@ext]
        public static string MavenPath(string name)
        {
            string ext = "jar";
            int at = name.IndexOf('@');
            if (at >= 0) { ext = name.Substring(at + 1); name = name.Substring(0, at); }
            var p = name.Split(':');
            string file = p[1] + "-" + p[2] + (p.Length > 3 ? "-" + p[3] : "") + "." + ext;
            return p[0].Replace('.', '/') + "/" + p[1] + "/" + p[2] + "/" + file;
        }
    }

    static class Rules
    {
        // Настоящая версия Windows (Environment.OSVersion без манифеста возвращает 6.2).
        public static readonly string OsVersion = ReadOsVersion();

        static string ReadOsVersion()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    object maj = k.GetValue("CurrentMajorVersionNumber"), min = k.GetValue("CurrentMinorVersionNumber");
                    if (maj != null) return maj + "." + (min ?? 0);
                }
            }
            catch { }
            return Environment.OSVersion.Version.Major + "." + Environment.OSVersion.Version.Minor;
        }

        // Без правил — разрешено. Иначе по умолчанию запрещено, применяется последнее подходящее правило.
        public static bool Allowed(List<object> rules, Dictionary<string, bool> features)
        {
            if (rules == null || rules.Count == 0) return true;
            bool allow = false;
            foreach (var o in rules)
            {
                var rule = J.O(o);
                if (rule == null) continue;
                if (Matches(rule, features)) allow = J.S(rule, "action") == "allow";
            }
            return allow;
        }

        static bool Matches(Dictionary<string, object> rule, Dictionary<string, bool> features)
        {
            var os = J.O(rule, "os");
            if (os != null)
            {
                string name = J.S(os, "name");
                if (name != null && name != "windows") return false;
                string arch = J.S(os, "arch");
                if (arch != null && arch != "x86_64" && arch != "amd64" && arch != "x64") return false;
                string ver = J.S(os, "version");
                if (ver != null)
                {
                    try { if (!Regex.IsMatch(OsVersion, ver)) return false; } catch { return false; }
                }
            }
            var feats = J.O(rule, "features");
            if (feats != null)
                foreach (var kv in feats)
                {
                    bool have;
                    if (features == null || !features.TryGetValue(kv.Key, out have)) have = false;
                    if (have != (kv.Value is bool && (bool)kv.Value)) return false;
                }
            return true;
        }
    }
}
