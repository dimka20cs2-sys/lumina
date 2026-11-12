// Загрузчики модов: Fabric, Quilt (профиль JSON поверх ванили), Forge и NeoForge (официальный установщик, запуск без окна).
// Списки версий кэшируются в памяти (10 мин) и на диске (на случай отсутствия сети).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AkeeLauncher
{
    static class Loaders
    {
        public static readonly string[] All = { "vanilla", "fabric", "quilt", "forge", "neoforge" };
        public const string FabricMeta = "https://meta.fabricmc.net/v2/versions/loader/";
        public const string QuiltMeta = "https://meta.quiltmc.org/v3/versions/loader/";
        const string ForgeMaven = "https://maven.minecraftforge.net/net/minecraftforge/forge/";
        const string ForgePromos = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
        const string NeoMaven = "https://maven.neoforged.net/releases/net/neoforged/neoforge/";
        const string NeoLegacyMaven = "https://maven.neoforged.net/releases/net/neoforged/forge/";   // NeoForge для 1.20.1

        public static bool IsKnown(string loader) { return Array.IndexOf(All, loader) >= 0; }
        public static bool UsesInstaller(string loader) { return loader == "forge" || loader == "neoforge"; }

        public static string Title(string loader)
        {
            switch (loader)
            {
                case "fabric": return "Fabric";
                case "quilt": return "Quilt";
                case "forge": return "Forge";
                case "neoforge": return "NeoForge";
                default: return "Vanilla";
            }
        }

        // ---------- кэш метаданных ----------
        static readonly Dictionary<string, KeyValuePair<DateTime, string>> mem = new Dictionary<string, KeyValuePair<DateTime, string>>();

        static string Meta(string url, bool notFoundIsEmpty)
        {
            lock (mem)
            {
                KeyValuePair<DateTime, string> c;
                if (mem.TryGetValue(url, out c) && (DateTime.UtcNow - c.Key).TotalMinutes < 10) return c.Value;
            }
            string file = Path.Combine(Paths.Cache, "meta", Http.Hex(System.Security.Cryptography.SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(url))) + ".txt");
            string text;
            try
            {
                text = Http.GetRetry(url);
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, text, Encoding.UTF8);
            }
            catch (HttpError e)
            {
                if (e.Status == 404 && notFoundIsEmpty) text = "";
                else if (File.Exists(file)) text = File.ReadAllText(file, Encoding.UTF8);
                else throw;
            }
            catch
            {
                if (!File.Exists(file)) throw;
                text = File.ReadAllText(file, Encoding.UTF8);   // офлайн: последний сохранённый ответ
            }
            lock (mem) mem[url] = new KeyValuePair<DateTime, string>(DateTime.UtcNow, text);
            return text;
        }

        // ---------- списки версий ----------
        // [{version, stable, recommended}] — от новых к старым; recommended ставится у версии по умолчанию.
        public static List<object> List(string loader, string mc)
        {
            var r = new List<object>();
            switch (loader)
            {
                case "fabric":
                    foreach (var o in J.A(J.Parse(NonEmpty(Meta(FabricMeta + Http.Q(mc), true)))))
                    {
                        var l = J.O(J.O(o), "loader");
                        r.Add(Item(J.S(l, "version"), J.B(l, "stable")));
                    }
                    break;
                case "quilt":
                    foreach (var o in J.A(J.Parse(NonEmpty(Meta(QuiltMeta + Http.Q(mc), true)))))
                    {
                        string v = J.S(J.O(J.O(o), "loader"), "version");
                        if (v != null) r.Add(Item(v, v.IndexOf('-') < 0));
                    }
                    break;
                case "forge":
                    {
                        var promos = J.O(J.ParseObj(Meta(ForgePromos, false)), "promos");
                        string rec = J.S(promos, mc + "-recommended"), lat = J.S(promos, mc + "-latest");
                        var vers = new List<string>();
                        foreach (var full in MavenVersions(ForgeMaven))
                            if (full.StartsWith(mc + "-")) vers.Add(full.Substring(mc.Length + 1));
                        vers.Sort(CompareVersions); vers.Reverse();
                        foreach (var v in vers) r.Add(Item(v, v == rec || (rec == null && v == lat)));
                    }
                    break;
                case "neoforge":
                    {
                        var vers = new List<string>();
                        if (mc == "1.20.1")
                        {
                            foreach (var full in MavenVersions(NeoLegacyMaven))
                                if (full.StartsWith("1.20.1-")) vers.Add(full.Substring(7));
                        }
                        else
                            foreach (var v in MavenVersions(NeoMaven)) if (NeoMc(v) == mc) vers.Add(v);
                        vers.Sort(CompareVersions); vers.Reverse();
                        foreach (var v in vers) r.Add(Item(v, v.IndexOf("-", StringComparison.Ordinal) < 0));
                    }
                    break;
            }
            // Рекомендуемая = первая стабильная (для Forge — помеченная recommended/latest), иначе самая новая.
            Dictionary<string, object> pick = null;
            foreach (var o in r) if (J.B(J.O(o), "stable")) { pick = J.O(o); break; }
            if (pick == null && r.Count > 0) pick = J.O(r[0]);
            if (pick != null) pick["recommended"] = true;
            return r;
        }

        static string NonEmpty(string s) { return string.IsNullOrWhiteSpace(s) ? "[]" : s; }

        static Dictionary<string, object> Item(string v, bool stable) { return J.D("version", v, "stable", stable, "recommended", false); }

        public static string DefaultVersion(string loader, string mc)
        {
            foreach (var o in List(loader, mc)) if (J.B(J.O(o), "recommended")) return J.S(J.O(o), "version");
            return null;
        }

        // Какие загрузчики есть для версии игры: {vanilla:true, fabric:bool, ...}
        public static Dictionary<string, object> Support(string mc)
        {
            var r = J.D("vanilla", true);
            foreach (var l in All)
            {
                if (l == "vanilla") continue;
                try { r[l] = List(l, mc).Count > 0; } catch { r[l] = false; }
            }
            return r;
        }

        static List<string> MavenVersions(string baseUrl)
        {
            var r = new List<string>();
            foreach (Match m in Regex.Matches(Meta(baseUrl + "maven-metadata.xml", false), "<version>([^<]+)</version>")) r.Add(m.Groups[1].Value.Trim());
            return r;
        }

        // NeoForge: 21.1.77 → 1.21.1, 21.0.x → 1.21, 20.4.x → 1.20.4; с 2026 года: 26.1.2.5 → 26.1.2, 26.3.0.1 → 26.3.
        // Сборки для снапшотов (…+snapshot-N, +pre-N) пропускаются.
        public static string NeoMc(string v)
        {
            if (v.IndexOf('+') >= 0) return null;
            var p = v.Split('-')[0].Split('.');
            int major;
            if (p.Length < 3 || !int.TryParse(p[0], out major) || major < 20) return null;
            if (major < 26) return "1." + p[0] + (p[1] != "0" ? "." + p[1] : "");
            if (p.Length < 4) return null;
            return p[0] + "." + p[1] + (p[2] != "0" ? "." + p[2] : "");
        }

        public static int CompareVersions(string a, string b)
        {
            var pa = Regex.Split(a, "[.\\-+]");
            var pb = Regex.Split(b, "[.\\-+]");
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                string x = i < pa.Length ? pa[i] : "", y = i < pb.Length ? pb[i] : "";
                long nx, ny;
                bool ix = long.TryParse(x, out nx), iy = long.TryParse(y, out ny);
                int c;
                if (ix && iy) c = nx.CompareTo(ny);
                else if (ix != iy) c = ix ? 1 : -1;   // 1.0.0 новее 1.0.0-beta
                else c = string.CompareOrdinal(x, y);
                if (c != 0) return c;
            }
            return 0;
        }

        // ---------- профили ----------
        // Возвращает дочерний JSON профиля (с inheritsFrom) и его id.
        public static Dictionary<string, object> Profile(string loader, string mc, string ver, Dictionary<string, object> vanilla, out string id)
        {
            if (loader == "fabric" || loader == "quilt")
            {
                string url = (loader == "fabric" ? FabricMeta : QuiltMeta) + Http.Q(mc) + "/" + Http.Q(ver) + "/profile/json";
                string text = Meta(url, false);
                var json = J.ParseObj(text);
                id = J.S(json, "id") ?? (loader + "-loader-" + ver + "-" + mc);
                string file = Path.Combine(Paths.Versions, id, id + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, text, Encoding.UTF8);
                return json;
            }
            if (UsesInstaller(loader)) return Installed(loader, mc, ver, vanilla, out id);
            throw new Exception("Неизвестный загрузчик: " + loader);
        }

        // ---------- Forge / NeoForge ----------
        static string InstallerUrl(string loader, string mc, string ver)
        {
            if (loader == "forge") return ForgeMaven + mc + "-" + ver + "/forge-" + mc + "-" + ver + "-installer.jar";
            if (mc == "1.20.1") return NeoLegacyMaven + "1.20.1-" + ver + "/forge-1.20.1-" + ver + "-installer.jar";
            return NeoMaven + ver + "/neoforge-" + ver + "-installer.jar";
        }

        static string MarkerFile(string loader, string mc, string ver)
        {
            return Path.Combine(Paths.Cache, "loaders", loader + "-" + mc + "-" + Regex.Replace(ver, "[^A-Za-z0-9._-]", "_") + ".id");
        }

        static readonly object installGate = new object();

        static Dictionary<string, object> Installed(string loader, string mc, string ver, Dictionary<string, object> vanilla, out string id)
        {
            lock (installGate)
            {
                string marker = MarkerFile(loader, mc, ver);
                id = File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
                Events.Stage("loader", "Проверка " + Title(loader) + " " + ver + "…");
                string problem = id == null ? "не установлен" : CheckInstalled(id);
                if (problem != null)
                {
                    if (id != null) Events.Log("[Lumina] " + Title(loader) + " " + ver + ": " + problem + " — переустановка");
                    id = RunInstaller(loader, mc, ver, vanilla);
                    problem = CheckInstalled(id);
                    if (problem != null) throw new Exception("Установщик " + Title(loader) + " завершился, но установка неполная: " + problem);
                    Directory.CreateDirectory(Path.GetDirectoryName(marker));
                    File.WriteAllText(marker, id);
                }
                return J.ParseObj(File.ReadAllText(Path.Combine(Paths.Versions, id, id + ".json"), Encoding.UTF8));
            }
        }

        // null = всё на месте. Проверяет JSON профиля, библиотеки без ссылки (их создаёт установщик)
        // и выходные файлы процессоров установщика (по SHA-1 из install_profile.json).
        public static string CheckInstalled(string id)
        {
            string file = Path.Combine(Paths.Versions, id, id + ".json");
            if (!File.Exists(file)) return "нет " + id + ".json";
            Dictionary<string, object> json;
            try { json = J.ParseObj(File.ReadAllText(file, Encoding.UTF8)); } catch { return "повреждён " + id + ".json"; }
            foreach (var o in J.A(json, "libraries"))
            {
                var art = J.O(J.O(J.O(o), "downloads"), "artifact");
                if (art == null || !string.IsNullOrEmpty(J.S(art, "url"))) continue;
                string path = Path.Combine(Paths.Libraries, (J.S(art, "path") ?? Versions.MavenPath(J.S(J.O(o), "name"))).Replace('/', '\\'));
                if (!VerifyCache.IsValid(path, J.S(art, "sha1"), "sha1", J.L(art, "size"))) return "нет или повреждён " + Path.GetFileName(path);
            }
            string prof = Path.Combine(Paths.Versions, id, "lumina-install_profile.json");
            if (File.Exists(prof))
            {
                try
                {
                    var ip = J.ParseObj(File.ReadAllText(prof, Encoding.UTF8));
                    var data = J.O(ip, "data");
                    foreach (var p in J.A(ip, "processors"))
                    {
                        var pd = J.O(p);
                        var sides = J.A(pd, "sides");
                        if (sides.Count > 0 && !sides.Contains("client")) continue;
                        var outs = J.O(pd, "outputs");
                        if (outs == null) continue;
                        foreach (var kv in outs)
                        {
                            string path = DataValue(kv.Key, data, true), sha = DataValue(Convert.ToString(kv.Value), data, false);
                            if (path == null) continue;
                            if (!VerifyCache.IsValid(path, sha, "sha1", 0)) return "нет или повреждён " + Path.GetFileName(path);
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        // {KEY} → data[KEY].client; [g:a:v:c@ext] → путь в libraries; 'literal' → literal.
        static string DataValue(string s, Dictionary<string, object> data, bool isPath)
        {
            if (string.IsNullOrEmpty(s)) return null;
            if (s.StartsWith("{") && s.EndsWith("}"))
            {
                s = J.S(J.O(data, s.Substring(1, s.Length - 2)), "client");
                if (s == null) return null;
            }
            if (s.StartsWith("'") && s.EndsWith("'")) return s.Substring(1, s.Length - 2);
            if (s.StartsWith("[") && s.EndsWith("]"))
                return Path.Combine(Paths.Libraries, Versions.MavenPath(s.Substring(1, s.Length - 2)).Replace('/', '\\'));
            return isPath ? null : s;
        }

        static string RunInstaller(string loader, string mc, string ver, Dictionary<string, object> vanilla)
        {
            string title = Title(loader) + " " + ver;
            Events.Stage("loader", "Установка " + title + "…");

            // 1) Ванильный клиент на месте — установщик берёт его отсюда, а не качает заново.
            var client = J.O(J.O(vanilla, "downloads"), "client");
            if (client == null) throw new Exception("В описании версии " + mc + " нет клиента");
            Downloader.Run(new List<DownloadJob> { new DownloadJob(J.S(client, "url"), Path.Combine(Paths.Versions, mc, mc + ".jar"), J.S(client, "sha1"), J.L(client, "size")) }, "Клиент " + mc);

            // 2) Установщик требует launcher_profiles.json в целевой папке.
            string lp = Path.Combine(Paths.DataRoot, "launcher_profiles.json");
            if (!File.Exists(lp)) File.WriteAllText(lp, "{\"profiles\":{},\"settings\":{},\"version\":3}", new UTF8Encoding(false));

            // 3) Установщик с проверкой SHA-1 (файл .sha1 лежит рядом на maven).
            string url = InstallerUrl(loader, mc, ver);
            string dir = Path.Combine(Paths.Cache, "installers");
            string jar = Path.Combine(dir, Path.GetFileName(url));
            string sha1 = (Http.GetRetry(url + ".sha1") ?? "").Trim();
            if (sha1.Length >= 40) sha1 = sha1.Substring(0, 40); else throw new Exception("Нет контрольной суммы установщика " + title);
            Downloader.Run(new List<DownloadJob> { new DownloadJob(url, jar, sha1, 0) }, "Установщик " + title);

            string id;
            using (var z = ZipFile.OpenRead(jar))
            {
                var ve = z.GetEntry("version.json");
                if (ve == null) throw new Exception("В установщике " + title + " нет version.json");
                using (var rd = new StreamReader(ve.Open(), Encoding.UTF8)) id = J.S(J.ParseObj(rd.ReadToEnd()), "id");
                if (string.IsNullOrEmpty(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new Exception("Неверный id профиля в установщике");
                Directory.CreateDirectory(Path.Combine(Paths.Versions, id));
                var ip = z.GetEntry("install_profile.json");
                if (ip != null) ip.ExtractToFile(Path.Combine(Paths.Versions, id, "lumina-install_profile.json"), true);
            }

            // 4) Java, нужная этой версии игры (консольный java.exe — без окна, вывод перехватывается).
            var vi = new VersionInfo { Json = vanilla, Id = mc, BaseId = mc };
            string java = Path.Combine(Path.GetDirectoryName(JavaRuntime.Ensure(vi).Javaw), "java.exe");
            if (!File.Exists(java)) throw new Exception("Не найден java.exe рядом с " + java);

            Events.Stage("loader", "Установка " + title + " (это может занять пару минут)…");
            var tail = new Queue<string>();
            // Установщик пишет десятки тысяч строк (каждый файл): полный журнал — в файл, в интерфейс — только строки верхнего уровня.
            string logPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(jar) + ".lumina.log");
            var logFile = new StreamWriter(logPath, false, new UTF8Encoding(false));
            int forwarded = 0;
            var psi = new ProcessStartInfo(java, LaunchBuilder.JoinArgs(new List<string> { "-Djava.awt.headless=true", "-jar", jar, "--installClient", Paths.DataRoot }))
            {
                WorkingDirectory = dir,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.EnvironmentVariables.Remove("_JAVA_OPTIONS");
            psi.EnvironmentVariables.Remove("JAVA_TOOL_OPTIONS");
            DataReceivedEventHandler onLine = (s, e) =>
            {
                if (e.Data == null) return;
                bool show;
                lock (tail)
                {
                    tail.Enqueue(e.Data); while (tail.Count > 40) tail.Dequeue();
                    try { logFile.WriteLine(e.Data); } catch { }
                    show = e.Data.Length > 0 && !char.IsWhiteSpace(e.Data[0]) && forwarded++ < 300;
                }
                if (show) Events.Log("[" + Title(loader) + "] " + e.Data);
            };
            using (logFile)
            using (var p = new Process { StartInfo = psi })
            {
                p.OutputDataReceived += onLine;
                p.ErrorDataReceived += onLine;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                var sw = Stopwatch.StartNew();
                while (!p.WaitForExit(500))
                {
                    if (Downloader.Cancel || sw.Elapsed.TotalMinutes > 20)
                    {
                        try { p.Kill(); } catch { }
                        throw new OperationCanceledException(Downloader.Cancel ? "Отменено" : "Установщик " + title + " не уложился в 20 минут");
                    }
                }
                p.WaitForExit();
                if (p.ExitCode != 0)
                {
                    string last;
                    lock (tail) last = string.Join("\n", tail.ToArray());
                    throw new Exception("Установщик " + title + " завершился с кодом " + p.ExitCode + " (журнал: " + logPath + "):\n" + last);
                }
            }
            if (!File.Exists(Path.Combine(Paths.Versions, id, id + ".json"))) throw new Exception("Установщик " + title + " не создал профиль " + id);
            return id;
        }
    }
}
