// Файлы игры: клиент .jar, библиотеки (с фильтром по ОС), natives старых версий, ассеты, конфиг log4j.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AkeeLauncher
{
    public class PreparedGame
    {
        public VersionInfo Version;
        public List<string> Classpath = new List<string>();
        public string ClientJar, NativesDir, AssetsRoot, GameAssets, AssetIndexId, LogArg;
        public int Downloaded;
    }

    static class GameFiles
    {
        const string DefaultMaven = "https://libraries.minecraft.net/";
        const string ResourcesUrl = "https://resources.download.minecraft.net/";

        public static PreparedGame Prepare(VersionInfo v, string gameDir)
        {
            var g = new PreparedGame { Version = v, AssetsRoot = Paths.Assets };
            var jobs = new List<DownloadJob>();
            var nativeJars = new List<KeyValuePair<string, List<string>>>();

            Events.Stage("libraries", "Проверка библиотек…");
            foreach (var o in J.A(v.Json, "libraries")) AddLibrary(J.O(o), g, jobs, nativeJars);

            var client = J.O(J.O(v.Json, "downloads"), "client");
            if (client == null) throw new Exception("В описании версии нет клиента");
            g.ClientJar = Path.Combine(Paths.Versions, v.BaseId, v.BaseId + ".jar");
            jobs.Add(new DownloadJob(J.S(client, "url"), g.ClientJar, J.S(client, "sha1"), J.L(client, "size")));
            g.Classpath.Add(g.ClientJar);

            var log = J.O(J.O(J.O(v.Json, "logging"), "client"), "file");
            if (log != null)
            {
                string lp = Path.Combine(Paths.Assets, "log_configs", J.S(log, "id"));
                jobs.Add(new DownloadJob(J.S(log, "url"), lp, J.S(log, "sha1"), J.L(log, "size")));
                g.LogArg = (J.S(J.O(J.O(v.Json, "logging"), "client"), "argument") ?? "").Replace("${path}", lp);
            }
            g.Downloaded += Downloader.Run(jobs, "Библиотеки и клиент");

            // Forge/NeoForge: как официальный лаунчер — клиент лежит под именем профиля (versions\<id>\<id>.jar),
            // на это имя ссылается их -DignoreList=…,${version_name}.jar.
            if (Loaders.UsesInstaller(v.Loader) && v.Id != v.BaseId)
            {
                string own = Path.Combine(Paths.Versions, v.Id, v.Id + ".jar");
                var src = new FileInfo(g.ClientJar);
                var dst = new FileInfo(own);
                if (!dst.Exists || dst.Length != src.Length) { Directory.CreateDirectory(dst.DirectoryName); File.Copy(g.ClientJar, own, true); }
                g.Classpath[g.Classpath.IndexOf(g.ClientJar)] = own;
                g.ClientJar = own;
            }

            g.NativesDir = Path.Combine(Paths.Natives, v.BaseId);
            Directory.CreateDirectory(g.NativesDir);
            foreach (var n in nativeJars) Extract(n.Key, g.NativesDir, n.Value);

            PrepareAssets(v, g, gameDir);
            return g;
        }

        static void AddLibrary(Dictionary<string, object> lib, PreparedGame g, List<DownloadJob> jobs, List<KeyValuePair<string, List<string>>> nativeJars)
        {
            if (lib == null || !Rules.Allowed(J.A(lib, "rules"), null)) return;
            string name = J.S(lib, "name") ?? "";
            // Новые версии кладут natives в отдельные библиотеки с классификатором — берём только x64.
            if (name.EndsWith(":natives-windows-arm64") || name.EndsWith(":natives-windows-x86")) return;

            var dl = J.O(lib, "downloads");
            var art = J.O(dl, "artifact");
            if (art != null)
            {
                string path = Path.Combine(Paths.Libraries, (J.S(art, "path") ?? Versions.MavenPath(name)).Replace('/', '\\'));
                if (!string.IsNullOrEmpty(J.S(art, "url"))) jobs.Add(new DownloadJob(J.S(art, "url"), path, J.S(art, "sha1"), J.L(art, "size")));
                else if (!File.Exists(path))   // Forge/NeoForge: файл создаёт установщик, ссылки на скачивание нет
                    throw new Exception("Нет файла " + Path.GetFileName(path) + " — загрузчик установлен не полностью, запустите ещё раз");
                AddCp(g, path);
            }
            else if (dl == null && name.Length > 0)
            {
                // Формат Fabric: name + url репозитория (+ sha1/size)
                string rel = Versions.MavenPath(name);
                string baseUrl = J.S(lib, "url", DefaultMaven);
                if (!baseUrl.EndsWith("/")) baseUrl += "/";
                string path = Path.Combine(Paths.Libraries, rel.Replace('/', '\\'));
                jobs.Add(new DownloadJob(baseUrl + rel, path, J.S(lib, "sha1"), J.L(lib, "size")));
                AddCp(g, path);
            }

            // Старый формат natives: natives.windows = "natives-windows[-${arch}]" → classifiers
            string cls = J.S(J.O(lib, "natives"), "windows");
            if (cls != null)
            {
                cls = cls.Replace("${arch}", "64");
                var nat = J.O(J.O(dl, "classifiers"), cls);
                if (nat != null)
                {
                    string path = Path.Combine(Paths.Libraries, (J.S(nat, "path") ?? Versions.MavenPath(name + ":" + cls)).Replace('/', '\\'));
                    jobs.Add(new DownloadJob(J.S(nat, "url"), path, J.S(nat, "sha1"), J.L(nat, "size")));
                    var excl = new List<string>();
                    foreach (var e in J.A(J.O(lib, "extract"), "exclude")) excl.Add(e.ToString());
                    nativeJars.Add(new KeyValuePair<string, List<string>>(path, excl));
                }
            }
        }

        static void AddCp(PreparedGame g, string path)
        {
            foreach (var p in g.Classpath) if (string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) return;
            g.Classpath.Add(path);
        }

        static void Extract(string jar, string dir, List<string> exclude)
        {
            exclude.Add("META-INF/");
            using (var z = ZipFile.OpenRead(jar))
                foreach (var e in z.Entries)
                {
                    if (e.FullName.EndsWith("/")) continue;
                    bool skip = false;
                    foreach (var x in exclude) if (e.FullName.StartsWith(x, StringComparison.OrdinalIgnoreCase)) { skip = true; break; }
                    if (skip) continue;
                    string target = Path.GetFullPath(Path.Combine(dir, e.FullName));
                    if (!target.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) continue;   // защита от ../
                    var fi = new FileInfo(target);
                    if (fi.Exists && fi.Length == e.Length) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    try { e.ExtractToFile(target, true); } catch (IOException) { /* занято запущенной игрой */ }
                }
        }

        static void PrepareAssets(VersionInfo v, PreparedGame g, string gameDir)
        {
            var ai = J.O(v.Json, "assetIndex");
            if (ai == null) throw new Exception("В описании версии нет assetIndex");
            g.AssetIndexId = J.S(ai, "id");
            string idxFile = Path.Combine(Paths.Assets, "indexes", g.AssetIndexId + ".json");
            Events.Stage("assets", "Проверка ресурсов игры…");
            Downloader.Run(new List<DownloadJob> { new DownloadJob(J.S(ai, "url"), idxFile, J.S(ai, "sha1"), J.L(ai, "size")) }, "Индекс ресурсов");
            var idx = J.ParseObj(File.ReadAllText(idxFile, Encoding.UTF8));
            bool isVirtual = J.B(idx, "virtual"), toResources = J.B(idx, "map_to_resources");

            var objs = J.O(idx, "objects") ?? new Dictionary<string, object>();
            var jobs = new List<DownloadJob>(objs.Count);
            foreach (var kv in objs)
            {
                var o = J.O(kv.Value);
                string h = J.S(o, "hash");
                if (h == null || h.Length < 2) continue;
                string sub = h.Substring(0, 2);
                jobs.Add(new DownloadJob(ResourcesUrl + sub + "/" + h, Path.Combine(Paths.Assets, "objects", sub, h), h, J.L(o, "size")));
            }
            g.Downloaded += Downloader.Run(jobs, "Ресурсы игры");

            g.GameAssets = Paths.Assets;
            if (isVirtual || toResources)
            {
                // Очень старые версии читают ресурсы по именам, а не по хешам.
                string dir = toResources ? Path.Combine(gameDir, "resources") : Path.Combine(Paths.Assets, "virtual", g.AssetIndexId);
                foreach (var kv in objs)
                {
                    string h = J.S(J.O(kv.Value), "hash");
                    string dst = Path.Combine(dir, kv.Key.Replace('/', '\\'));
                    if (File.Exists(dst)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(Path.Combine(Paths.Assets, "objects", h.Substring(0, 2), h), dst, true);
                }
                g.GameAssets = dir;
            }
        }
    }
}
