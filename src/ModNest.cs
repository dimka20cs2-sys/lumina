// «Моды» (ModNest внутри Lumina): каталог Modrinth для инсталляции — поиск с фильтрами, установка с зависимостями (SHA-512),
// ресурс-паки, шейдеры (+ Iris/Oculus), датапаки в мир, обновления по хешу файлов, сборки .mrpack как новая инсталляция,
// импорт локальных .jar/.zip/.mrpack. Логика перенесена из ModNest (Apps\ModManager) и переписана под Lumina.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace AkeeLauncher
{
    static class ModNest
    {
        const string Api = "https://api.modrinth.com/v2/";
        public static readonly string[] Kinds = { "mod", "resourcepack", "shader", "datapack", "modpack" };
        public static readonly string[] PackHosts = { "cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com" };
        public static bool UseRecycleBin = true;   // самотест удаляет файлы сразу

        static readonly object gate = new object();

        // ---------- категории ----------
        static readonly string[][] ModCats = {
            new[] { "", "Все" }, new[] { "optimization", "Оптимизация" }, new[] { "utility", "Утилиты" }, new[] { "technology", "Технологии" },
            new[] { "adventure", "Приключения" }, new[] { "magic", "Магия" }, new[] { "worldgen", "Генерация мира" }, new[] { "decoration", "Декор" },
            new[] { "storage", "Хранение" }, new[] { "mobs", "Мобы" }, new[] { "equipment", "Снаряжение" }, new[] { "food", "Еда" },
            new[] { "game-mechanics", "Механики" }, new[] { "transportation", "Транспорт" }, new[] { "management", "Управление" },
            new[] { "social", "Общение" }, new[] { "library", "Библиотеки" },
        };
        static readonly string[][] ResCats = {
            new[] { "", "Все" }, new[] { "16x", "16x" }, new[] { "32x", "32x" }, new[] { "64x", "64x" }, new[] { "realistic", "Реализм" },
            new[] { "vanilla-like", "Ванильный стиль" }, new[] { "combat", "PvP" }, new[] { "simplistic", "Простой" }, new[] { "decoration", "Декор" },
            new[] { "models", "Модели" }, new[] { "fonts", "Шрифты" }, new[] { "audio", "Звуки" }, new[] { "gui", "GUI" },
        };
        static readonly string[][] ShaderCats = {
            new[] { "", "Все" }, new[] { "realistic", "Реализм" }, new[] { "semi-realistic", "Полуреализм" }, new[] { "cartoon", "Мультяшный" },
            new[] { "vanilla-like", "Ванильный стиль" }, new[] { "fantasy", "Фэнтези" }, new[] { "shadows", "Тени" }, new[] { "reflections", "Отражения" },
            new[] { "path-tracing", "Трассировка лучей" }, new[] { "potato", "Для слабых ПК" }, new[] { "high", "Для мощных ПК" },
        };
        static readonly string[][] DataCats = {
            new[] { "", "Все" }, new[] { "worldgen", "Мир" }, new[] { "adventure", "Приключения" }, new[] { "game-mechanics", "Механики" },
            new[] { "utility", "Утилиты" }, new[] { "mobs", "Мобы" }, new[] { "equipment", "Снаряжение" }, new[] { "magic", "Магия" },
            new[] { "decoration", "Украшения" }, new[] { "food", "Еда" },
        };
        static readonly string[][] PackCats = {
            new[] { "", "Все" }, new[] { "optimization", "Оптимизация" }, new[] { "lightweight", "Лёгкие" }, new[] { "adventure", "Приключения" },
            new[] { "technology", "Технологии" }, new[] { "magic", "Магия" }, new[] { "quests", "Квесты" }, new[] { "multiplayer", "Мультиплеер" },
            new[] { "combat", "Бои" }, new[] { "challenging", "Хардкор" }, new[] { "kitchen-sink", "Всё сразу" },
        };

        public static List<object> Categories(string kind)
        {
            string[][] src;
            switch (kind) { case "resourcepack": src = ResCats; break; case "shader": src = ShaderCats; break; case "datapack": src = DataCats; break; case "modpack": src = PackCats; break; default: src = ModCats; break; }
            var r = new List<object>();
            foreach (var c in src) r.Add(J.D("id", c[0], "label", c[1]));
            return r;
        }

        static string CheckKind(string kind)
        {
            if (Array.IndexOf(Kinds, kind) < 0) throw new Exception("Неизвестный тип: " + kind);
            return kind;
        }

        // Какие загрузчики Modrinth подходят типу контента в этой инсталляции. null — без фильтра по загрузчику.
        static string[] LoaderFilter(Instance inst, string kind)
        {
            switch (kind)
            {
                case "mod":
                    if (inst.loader == "vanilla") throw new Exception("В инсталляции без загрузчика моды не работают. Выберите Fabric, Quilt, Forge или NeoForge в её настройках.");
                    return Modrinth.ModLoaders(inst.loader, inst.mc);
                case "datapack": return new[] { "datapack" };
                default: return null;   // ресурс-паки и шейдеры — только по версии игры
            }
        }

        // ---------- поиск ----------
        public static Dictionary<string, object> Search(string instId, string kind, string query, string index, string category, int offset, int limit)
        {
            CheckKind(kind);
            Instance inst = string.IsNullOrEmpty(instId) ? null : Instances.Get(instId);
            if (kind != "modpack" && inst == null) throw new Exception("Выберите инсталляцию");
            var facets = new List<object> { new[] { "project_type:" + kind } };
            if (kind == "modpack")
            {
                // Сборки ставятся новой инсталляцией: только поддерживаемые загрузчики.
                facets.Add(new[] { "categories:fabric", "categories:quilt", "categories:forge", "categories:neoforge" });
            }
            else
            {
                facets.Add(new[] { "versions:" + inst.mc });
                var lf = LoaderFilter(inst, kind);
                if (lf != null) facets.Add(lf.Select(l => "categories:" + l).ToArray());
            }
            if (!string.IsNullOrEmpty(category)) facets.Add(new[] { "categories:" + category });
            if (index != "downloads" && index != "updated" && index != "newest" && index != "follows") index = "relevance";
            if (limit <= 0 || limit > 50) limit = 20;
            string url = Api + "search?query=" + Http.Q(query ?? "") + "&facets=" + Http.Q(J.Write(facets)) + "&index=" + index
                + "&offset=" + Math.Max(0, offset) + "&limit=" + limit;
            var o = J.ParseObj(Http.GetRetry(url));
            var have = inst == null ? new Dictionary<string, object>() : State(inst.id);
            var hits = new List<object>();
            foreach (var h in J.A(o, "hits"))
            {
                var d = J.O(h);
                string pid = J.S(d, "project_id");
                hits.Add(J.D("id", pid, "slug", J.S(d, "slug"), "title", J.S(d, "title"), "author", J.S(d, "author"),
                    "downloads", J.L(d, "downloads"), "follows", J.L(d, "follows"), "description", J.S(d, "description"),
                    "icon", J.S(d, "icon_url") ?? "", "type", J.S(d, "project_type"), "updated", J.S(d, "date_modified"),
                    "categories", J.A(d, "display_categories"), "installed", pid != null && have.ContainsKey(pid)));
            }
            return J.D("total", J.L(o, "total_hits"), "offset", offset, "limit", limit, "hits", hits,
                "mc", inst == null ? "" : inst.mc, "loader", inst == null ? "" : inst.loader);
        }

        // ---------- версии ----------
        static Dictionary<string, object> Best(string project, string[] loaders, string mc)
        {
            if (loaders != null && loaders.Contains("quilt"))   // Quilt: сначала Fabric-сборки (без QSL), потом родные
            {
                foreach (var t in Modrinth.LoaderTries("quilt", mc)) { var v = Best(project, t, mc); if (v != null) return v; }
                return null;
            }
            var q = new List<string>();
            if (loaders != null && loaders.Length > 0) q.Add("loaders=" + Http.Q(J.Write(loaders)));
            if (!string.IsNullOrEmpty(mc)) q.Add("game_versions=" + Http.Q(J.Write(new[] { mc })));
            List<object> list;
            try { list = J.A(J.Parse(Http.GetRetry(Api + "project/" + Http.Q(project) + "/version" + (q.Count > 0 ? "?" + string.Join("&", q) : "")))); }
            catch (HttpError e) { if (e.Status == 404) return null; throw; }
            Dictionary<string, object> beta = null, alpha = null;
            foreach (var x in list)   // от новых к старым
            {
                var v = J.O(x);
                if (PrimaryFile(v) == null) continue;
                string t = J.S(v, "version_type");
                if (t == "release") return v;
                if (t == "beta" && beta == null) beta = v;
                if (alpha == null) alpha = v;
            }
            return beta ?? alpha;
        }

        static Dictionary<string, object> PrimaryFile(Dictionary<string, object> v)
        {
            Dictionary<string, object> file = null;
            foreach (var f in J.A(v, "files"))
            {
                var fd = J.O(f);
                if (J.B(fd, "primary")) return fd;
                if (file == null) file = fd;
            }
            return file;
        }

        static Dictionary<string, object> Project(string id)
        {
            try { return J.ParseObj(Http.GetRetry(Api + "project/" + Http.Q(id))); } catch (HttpError) { return null; }
        }

        // ---------- папки ----------
        static string KindDir(Instance inst, string kind, string world)
        {
            string root = Instances.Dir(inst.id);
            switch (kind)
            {
                case "mod": return Path.Combine(root, "mods");
                case "resourcepack": return Path.Combine(root, "resourcepacks");
                case "shader": return Path.Combine(root, "shaderpacks");
                case "datapack":
                    if (string.IsNullOrEmpty(world)) throw new Exception("Выберите мир для датапака");
                    string w = Path.GetFileName(world);
                    if (w != world || w == ".." || !Directory.Exists(Path.Combine(root, "saves", w))) throw new Exception("Мир не найден: " + world);
                    return Path.Combine(root, "saves", w, "datapacks");
                default: throw new Exception("Сюда нельзя установить: " + kind);
            }
        }

        public static List<object> Worlds(string instId)
        {
            var r = new List<object>();
            string saves = Path.Combine(Instances.Dir(instId), "saves");
            if (Directory.Exists(saves))
                foreach (var d in new DirectoryInfo(saves).GetDirectories().OrderByDescending(x => x.LastWriteTimeUtc))
                    if (File.Exists(Path.Combine(d.FullName, "level.dat"))) r.Add(d.Name);
            return r;
        }

        // ---------- состояние: что поставлено через каталог ----------
        static string StateFile(string id) { return Path.Combine(Instances.Dir(id), "lumina-modnest.json"); }

        static Dictionary<string, object> State(string id)
        {
            try { return J.O(J.ParseObj(File.ReadAllText(StateFile(id), Encoding.UTF8)), "projects") ?? new Dictionary<string, object>(); }
            catch { return new Dictionary<string, object>(); }
        }

        static void SaveState(string id, Dictionary<string, object> projects)
        {
            File.WriteAllText(StateFile(id), J.Write(J.D("projects", projects)), Encoding.UTF8);
        }

        static string Rel(Instance inst, string full)
        {
            string root = Path.GetFullPath(Instances.Dir(inst.id)).TrimEnd('\\') + "\\";
            return Path.GetFullPath(full).Substring(root.Length).Replace('\\', '/');
        }

        // Путь внутри инсталляции и только в папках контента (mods, resourcepacks, shaderpacks, saves/<мир>/datapacks).
        static string SafeContentPath(Instance inst, string rel)
        {
            if (string.IsNullOrEmpty(rel) || rel.Contains(":")) throw new Exception("Неверный путь");
            string root = Path.GetFullPath(Instances.Dir(inst.id)).TrimEnd('\\') + "\\";
            string full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', '\\')));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new Exception("Неверный путь");
            var parts = full.Substring(root.Length).Split('\\');
            bool ok = (parts.Length == 2 && (parts[0] == "mods" || parts[0] == "resourcepacks" || parts[0] == "shaderpacks"))
                   || (parts.Length == 4 && parts[0] == "saves" && parts[2] == "datapacks");
            if (!ok) throw new Exception("Неверный путь");
            return full;
        }

        // ---------- установка ----------
        class Planned { public Dictionary<string, object> Version; public string Kind, Dir, Title, Slug; public bool Dependency; }

        public static Dictionary<string, object> Install(string instId, string project, string kind, string world)
        {
            CheckKind(kind);
            if (kind == "modpack") return InstallModpack(project);
            lock (gate)
            {
                var inst = Instances.Get(instId);
                var state = State(inst.id);
                var root = Best(project, LoaderFilter(inst, kind), inst.mc);
                if (root == null) throw new Exception("Нет сборки для Minecraft " + inst.mc + (kind == "mod" ? " + " + Loaders.Title(inst.loader) : ""));
                var info = Project(J.S(root, "project_id"));
                var plan = new List<Planned> { new Planned { Version = root, Kind = kind, Dir = KindDir(inst, kind, world), Title = J.S(info, "title"), Slug = J.S(info, "slug") } };
                var missing = new List<string>();

                // Обязательные зависимости (рекурсивно). У ресурс-паков зависимостями бывают моды (например, Fresh Animations → EMF/ETF).
                var seen = new HashSet<string>(state.Keys) { J.S(root, "project_id") };
                var todo = new Queue<Dictionary<string, object>>();
                todo.Enqueue(root);
                int guard = 0;
                while (todo.Count > 0 && guard++ < 60)
                {
                    var v = todo.Dequeue();
                    foreach (var d in J.A(v, "dependencies"))
                    {
                        var dd = J.O(d);
                        if (J.S(dd, "dependency_type") != "required") continue;
                        string pid = J.S(dd, "project_id");
                        if (pid == null || !seen.Add(pid)) continue;
                        var p = Project(pid);
                        string dk = J.S(p, "project_type") ?? "mod";
                        string title = J.S(p, "title") ?? pid;
                        if (dk == "mod" && inst.loader == "vanilla") { missing.Add(title + " (нужен загрузчик модов)"); continue; }
                        if (Array.IndexOf(Kinds, dk) < 0 || dk == "modpack" || (dk == "datapack" && string.IsNullOrEmpty(world))) { missing.Add(title); continue; }
                        var dv = Best(pid, LoaderFilter(inst, dk), inst.mc);
                        if (dv == null) { missing.Add(title); continue; }
                        plan.Add(new Planned { Version = dv, Kind = dk, Dir = KindDir(inst, dk, world), Title = title, Slug = J.S(p, "slug"), Dependency = true });
                        todo.Enqueue(dv);
                    }
                }

                var jobs = new List<DownloadJob>();
                foreach (var p in plan)
                {
                    var f = PrimaryFile(p.Version);
                    jobs.Add(new DownloadJob { Url = J.S(f, "url"), Path = Path.Combine(p.Dir, Path.GetFileName(J.S(f, "filename"))), Hash = J.S(J.O(f, "hashes"), "sha512"), Algo = "sha512", Size = J.L(f, "size") });
                }
                Downloader.Run(jobs, "Modrinth: " + plan[0].Title);

                var installed = new List<object>();
                foreach (var p in plan)
                {
                    var f = PrimaryFile(p.Version);
                    string pid = J.S(p.Version, "project_id"), file = Path.GetFileName(J.S(f, "filename"));
                    string full = Path.Combine(p.Dir, file);
                    object prev;
                    if (state.TryGetValue(pid, out prev))   // переустановка/другая версия — старый файл убираем
                    {
                        string old = J.S(J.O(prev), "path");
                        if (old != null && !string.Equals(old, Rel(inst, full), StringComparison.OrdinalIgnoreCase))
                            try { Trash(SafeContentPath(inst, old)); Trash(SafeContentPath(inst, old + ".disabled")); } catch { }
                    }
                    state[pid] = J.D("kind", p.Kind, "path", Rel(inst, full), "title", p.Title, "slug", p.Slug,
                        "version", J.S(p.Version, "version_number"), "versionId", J.S(p.Version, "id"), "dependency", p.Dependency);
                    installed.Add(J.D("id", pid, "title", p.Title, "version", J.S(p.Version, "version_number"), "file", file, "kind", p.Kind, "dependency", p.Dependency));
                }
                SaveState(inst.id, state);
                string shaderLoader = kind == "shader" && !HasShaderLoader(inst) ? ShaderLoaderFor(inst) : null;
                return J.D("installed", installed, "missing", missing, "needsShaderLoader", shaderLoader ?? "",
                    "shaderNote", kind == "shader" && shaderLoader == null && !HasShaderLoader(inst) ? "Шейдерам нужен загрузчик модов (Fabric/Quilt + Iris, NeoForge + Iris, Forge + Oculus)" : "");
            }
        }

        // ---------- шейдеры: Iris / Oculus ----------
        public static bool HasShaderLoader(Instance inst)
        {
            string mods = Path.Combine(Instances.Dir(inst.id), "mods");
            if (!Directory.Exists(mods)) return false;
            foreach (var f in Directory.GetFiles(mods, "*.jar"))
            {
                string n = Path.GetFileName(f).ToLowerInvariant();
                if (n.StartsWith("iris") || n.StartsWith("oculus")) return true;
            }
            return false;
        }

        // Какой мод нужен для шейдеров: Fabric/Quilt — Iris; NeoForge — Iris (если есть сборка), иначе Oculus; Forge — Oculus.
        public static string ShaderLoaderFor(Instance inst)
        {
            switch (inst.loader)
            {
                case "fabric": case "quilt": return "iris";
                case "neoforge": return Best("iris", Modrinth.ModLoaders(inst.loader, inst.mc), inst.mc) != null ? "iris" : "oculus";
                case "forge": return "oculus";
                default: return null;
            }
        }

        public static Dictionary<string, object> InstallShaderLoader(string instId)
        {
            var inst = Instances.Get(instId);
            string slug = ShaderLoaderFor(inst);
            if (slug == null) throw new Exception("Для шейдеров нужен загрузчик модов: выберите Fabric, Quilt, Forge или NeoForge в настройках инсталляции");
            return Install(instId, slug, "mod", null);
        }

        // ---------- установленное ----------
        public static List<object> Installed(string instId)
        {
            var inst = Instances.Get(instId);
            var byPath = new Dictionary<string, KeyValuePair<string, Dictionary<string, object>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in State(inst.id)) { string p = J.S(J.O(kv.Value), "path"); if (p != null) byPath[p] = new KeyValuePair<string, Dictionary<string, object>>(kv.Key, J.O(kv.Value)); }
            var r = new List<object>();
            string root = Instances.Dir(inst.id);
            Action<string, string, string> scan = (dir, kind, world) =>
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in new DirectoryInfo(dir).GetFiles())
                {
                    string n = f.Name.ToLowerInvariant();
                    bool disabled = n.EndsWith(".disabled");
                    string bare = disabled ? n.Substring(0, n.Length - 9) : n;
                    if (!bare.EndsWith(".jar") && !bare.EndsWith(".zip")) continue;
                    string rel = Rel(inst, f.FullName), relOn = disabled ? rel.Substring(0, rel.Length - 9) : rel;
                    KeyValuePair<string, Dictionary<string, object>> st;
                    bool known = byPath.TryGetValue(relOn, out st);
                    r.Add(J.D("kind", kind, "file", f.Name, "path", rel, "size", f.Length, "enabled", !disabled, "world", world ?? "",
                        "projectId", known ? st.Key : "", "title", known ? J.S(st.Value, "title") : "", "version", known ? J.S(st.Value, "version") : "",
                        "dependency", known && J.B(st.Value, "dependency"), "fromPack", false));
                }
            };
            scan(Path.Combine(root, "mods"), "mod", null);
            scan(Path.Combine(root, "resourcepacks"), "resourcepack", null);
            scan(Path.Combine(root, "shaderpacks"), "shader", null);
            foreach (var w in Worlds(inst.id)) scan(Path.Combine(root, "saves", (string)w, "datapacks"), "datapack", (string)w);
            return r;
        }

        public static List<object> Toggle(string instId, string rel)
        {
            var inst = Instances.Get(instId);
            if (GameRunner.RunningInstance == instId) throw new Exception("Сначала закройте игру");
            string src = SafeContentPath(inst, rel);
            if (!File.Exists(src)) throw new Exception("Файл не найден: " + Path.GetFileName(src));
            string dst = src.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? src.Substring(0, src.Length - 9) : src + ".disabled";
            File.Move(src, dst);
            return Installed(instId);
        }

        public static List<object> Remove(string instId, string rel)
        {
            lock (gate)
            {
                var inst = Instances.Get(instId);
                if (GameRunner.RunningInstance == instId) throw new Exception("Сначала закройте игру");
                string full = SafeContentPath(inst, rel);
                if (!File.Exists(full)) throw new Exception("Файл не найден: " + Path.GetFileName(full));
                Trash(full);
                string relOn = rel.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? rel.Substring(0, rel.Length - 9) : rel;
                var state = State(inst.id);
                foreach (var k in state.Keys.ToList())
                    if (string.Equals(J.S(J.O(state[k]), "path"), relOn, StringComparison.OrdinalIgnoreCase)) state.Remove(k);
                SaveState(inst.id, state);
                return Installed(instId);
            }
        }

        static void Trash(string path)
        {
            if (!File.Exists(path)) return;
            if (UseRecycleBin)
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else File.Delete(path);
        }

        // ---------- обновления (POST /v2/version_files/update по SHA-1 файлов) ----------
        class Update { public Dictionary<string, object> Item, Version; }

        static List<Update> FindUpdates(Instance inst, out int checkedCount, out int unknown)
        {
            var items = Installed(inst.id);
            var byKind = new Dictionary<string, Dictionary<string, Dictionary<string, object>>>();   // kind → sha1 → item
            foreach (var o in items)
            {
                var it = J.O(o);
                string kind = J.S(it, "kind");
                string sha = Http.FileHash(Path.Combine(Instances.Dir(inst.id), J.S(it, "path").Replace('/', '\\')), "sha1");
                it["sha1"] = sha;
                if (!byKind.ContainsKey(kind)) byKind[kind] = new Dictionary<string, Dictionary<string, object>>();
                byKind[kind][sha] = it;
            }
            var r = new List<Update>();
            checkedCount = items.Count;
            int known = 0;
            foreach (var kv in byKind)
            {
                var hashes = kv.Value.Keys.ToList();
                // Какие файлы вообще есть на Modrinth.
                var ident = J.ParseObj(Http.PostJson(Api + "version_files", J.D("hashes", hashes, "algorithm", "sha1"))) ?? new Dictionary<string, object>();
                known += ident.Count;
                if (ident.Count == 0) continue;
                var body = J.D("hashes", ident.Keys.ToList(), "algorithm", "sha1", "game_versions", new[] { inst.mc });
                string[] lf = kv.Key == "mod" ? (inst.loader == "vanilla" ? null : Modrinth.ModLoaders(inst.loader, inst.mc)) : kv.Key == "datapack" ? new[] { "datapack" } : null;
                if (lf != null) body["loaders"] = lf;
                Dictionary<string, object> upd;
                try { upd = J.ParseObj(Http.PostJson(Api + "version_files/update", body)) ?? new Dictionary<string, object>(); }
                catch (HttpError e) { if (e.Status == 404) continue; throw; }
                foreach (var u in upd)
                {
                    var v = J.O(u.Value);
                    Dictionary<string, object> it;
                    if (v == null || !kv.Value.TryGetValue(u.Key.ToLowerInvariant(), out it)) continue;
                    bool same = false;
                    foreach (var f in J.A(v, "files"))
                        if (string.Equals(J.S(J.O(J.O(f), "hashes"), "sha1"), u.Key, StringComparison.OrdinalIgnoreCase)) same = true;
                    var cur = J.O(ident.ContainsKey(u.Key) ? ident[u.Key] : null);
                    if (!same && cur != null && J.S(cur, "id") == J.S(v, "id")) same = true;
                    if (same || PrimaryFile(v) == null) continue;
                    if (cur != null && J.S(cur, "date_published") != null && string.CompareOrdinal(J.S(v, "date_published") ?? "", J.S(cur, "date_published")) <= 0) continue;
                    it["current"] = cur == null ? "" : J.S(cur, "version_number");
                    it["projectId"] = J.S(v, "project_id");
                    r.Add(new Update { Item = it, Version = v });
                }
            }
            unknown = checkedCount - known;
            return r;
        }

        public static Dictionary<string, object> CheckUpdates(string instId)
        {
            var inst = Instances.Get(instId);
            int n, unknown;
            var ups = FindUpdates(inst, out n, out unknown);
            var list = new List<object>();
            foreach (var u in ups)
                list.Add(J.D("path", J.S(u.Item, "path"), "file", J.S(u.Item, "file"), "kind", J.S(u.Item, "kind"), "title", J.S(u.Item, "title"),
                    "from", J.S(u.Item, "current"), "to", J.S(u.Version, "version_number"), "newFile", J.S(PrimaryFile(u.Version), "filename")));
            return J.D("updates", list, "checked", n, "unknown", unknown);
        }

        public static Dictionary<string, object> UpdateAll(string instId)
        {
            lock (gate)
            {
                var inst = Instances.Get(instId);
                if (GameRunner.RunningInstance == instId) throw new Exception("Сначала закройте игру");
                int n, unknown;
                var ups = FindUpdates(inst, out n, out unknown);
                if (ups.Count == 0) return J.D("updated", new List<object>(), "checked", n);
                var jobs = new List<DownloadJob>();
                foreach (var u in ups)
                {
                    var f = PrimaryFile(u.Version);
                    string dir = Path.GetDirectoryName(SafeContentPath(inst, J.S(u.Item, "path")));
                    jobs.Add(new DownloadJob { Url = J.S(f, "url"), Path = Path.Combine(dir, Path.GetFileName(J.S(f, "filename"))), Hash = J.S(J.O(f, "hashes"), "sha512"), Algo = "sha512", Size = J.L(f, "size") });
                }
                Downloader.Run(jobs, "Обновления Modrinth");
                var state = State(inst.id);
                var done = new List<object>();
                for (int i = 0; i < ups.Count; i++)
                {
                    var u = ups[i];
                    string oldFull = SafeContentPath(inst, J.S(u.Item, "path")), newFull = jobs[i].Path;
                    if (!J.B(u.Item, "enabled")) { string dis = newFull + ".disabled"; if (File.Exists(dis)) File.Delete(dis); File.Move(newFull, dis); }
                    if (!string.Equals(oldFull, newFull, StringComparison.OrdinalIgnoreCase) && !string.Equals(oldFull, newFull + ".disabled", StringComparison.OrdinalIgnoreCase)) Trash(oldFull);
                    string pid = J.S(u.Version, "project_id");
                    object prev;
                    var entry = state.TryGetValue(pid, out prev) ? J.O(prev) : J.D("kind", J.S(u.Item, "kind"), "title", J.S(u.Item, "title"), "dependency", false);
                    entry["path"] = Rel(inst, newFull);
                    entry["version"] = J.S(u.Version, "version_number");
                    entry["versionId"] = J.S(u.Version, "id");
                    state[pid] = entry;
                    done.Add(J.D("file", Path.GetFileName(newFull), "from", J.S(u.Item, "current"), "to", J.S(u.Version, "version_number"), "kind", J.S(u.Item, "kind")));
                }
                SaveState(inst.id, state);
                return J.D("updated", done, "checked", n);
            }
        }

        // ---------- сборки .mrpack ----------
        static readonly Dictionary<string, string> PackLoaders = new Dictionary<string, string> {
            { "fabric-loader", "fabric" }, { "quilt-loader", "quilt" }, { "forge", "forge" }, { "neoforge", "neoforge" },
        };

        public static Dictionary<string, object> InstallModpack(string project)
        {
            Dictionary<string, object> best = null;
            List<object> list;
            try { list = J.A(J.Parse(Http.GetRetry(Api + "project/" + Http.Q(project) + "/version"))); }
            catch (HttpError e) { if (e.Status == 404) throw new Exception("Сборка не найдена"); throw; }
            foreach (var x in list)   // от новых к старым; только сборки под поддерживаемые загрузчики и Minecraft 1.20+
            {
                var v = J.O(x);
                if (PrimaryFile(v) == null) continue;
                bool okLoader = J.A(v, "loaders").Any(l => PackLoaders.ContainsValue(Convert.ToString(l)));
                if (!okLoader) continue;
                if (best == null) best = v;
                if (J.S(v, "version_type") == "release") { best = v; break; }
            }
            if (best == null) throw new Exception("У сборки нет версии для Fabric, Quilt, Forge или NeoForge");
            var f = PrimaryFile(best);
            string file = Path.Combine(Paths.Cache, "mrpack", Path.GetFileName(J.S(f, "filename")));
            Downloader.Run(new List<DownloadJob> { new DownloadJob { Url = J.S(f, "url"), Path = file, Hash = J.S(J.O(f, "hashes"), "sha512"), Algo = "sha512", Size = J.L(f, "size") } }, "Сборка " + J.S(best, "name"));
            var info = Project(J.S(best, "project_id"));
            return ImportMrpack(file, J.S(info, "title"));
        }

        static string SafeInside(string root, string rel)
        {
            if (string.IsNullOrEmpty(rel) || rel.Contains(":") || rel.StartsWith("/") || rel.StartsWith("\\")) return null;
            string r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            string full = Path.GetFullPath(Path.Combine(r, rel.Replace('/', '\\')));
            return full.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        static bool AllowedHost(string url)
        {
            Uri u;
            return Uri.TryCreate(url, UriKind.Absolute, out u) && u.Scheme == "https" && PackHosts.Contains(u.Host.ToLowerInvariant());
        }

        // Новая инсталляция из .mrpack: версия игры и загрузчик — из dependencies, файлы — с проверкой хешей, затем overrides/.
        public static Dictionary<string, object> ImportMrpack(string mrpack, string title)
        {
            using (var zip = ZipFile.OpenRead(mrpack))
            {
                var ie = zip.GetEntry("modrinth.index.json");
                if (ie == null) throw new Exception("Это не сборка Modrinth: нет modrinth.index.json");
                Dictionary<string, object> idx;
                using (var rd = new StreamReader(ie.Open(), Encoding.UTF8)) idx = J.ParseObj(rd.ReadToEnd());
                var deps = J.O(idx, "dependencies");
                string mc = J.S(deps, "minecraft"), loader = "vanilla", lver = "";
                if (string.IsNullOrEmpty(mc)) throw new Exception("В сборке не указана версия Minecraft");
                foreach (var kv in deps ?? new Dictionary<string, object>())
                    if (PackLoaders.ContainsKey(kv.Key)) { loader = PackLoaders[kv.Key]; lver = Convert.ToString(kv.Value); }
                if (deps != null && deps.Keys.Any(k => k != "minecraft" && !PackLoaders.ContainsKey(k)))
                    throw new Exception("Загрузчик сборки не поддерживается: " + string.Join(", ", deps.Keys.Where(k => k != "minecraft")));

                string name = J.S(idx, "name") ?? title ?? Path.GetFileNameWithoutExtension(mrpack);
                var inst = Instances.Upsert(new Instance { name = name, mc = mc, loader = loader, loaderVersion = lver });
                string root = Instances.Dir(inst.id);
                var jobs = new List<DownloadJob>();
                int skipped = 0;
                foreach (var o in J.A(idx, "files"))
                {
                    var f = J.O(o);
                    if (J.S(J.O(f, "env"), "client") == "unsupported") { skipped++; continue; }
                    string dest = SafeInside(root, J.S(f, "path"));
                    string url = J.A(f, "downloads").Select(x => Convert.ToString(x)).FirstOrDefault(AllowedHost);
                    if (dest == null || url == null) { skipped++; continue; }
                    var h = J.O(f, "hashes");
                    string sha512 = J.S(h, "sha512"), sha1 = J.S(h, "sha1");
                    if (string.IsNullOrEmpty(sha512) && string.IsNullOrEmpty(sha1)) { skipped++; continue; }   // без хеша не ставим
                    jobs.Add(new DownloadJob { Url = url, Path = dest, Hash = sha512 ?? sha1, Algo = sha512 != null ? "sha512" : "sha1", Size = J.L(f, "fileSize") });
                }
                Downloader.Run(jobs, "Сборка: " + name);
                int overrides = 0;
                foreach (var prefix in new[] { "overrides/", "client-overrides/" })   // client-overrides — поверх общих
                    foreach (var e in zip.Entries)
                    {
                        string n = e.FullName.Replace('\\', '/');
                        if (!n.StartsWith(prefix) || n.EndsWith("/")) continue;
                        string dest = SafeInside(root, n.Substring(prefix.Length));
                        if (dest == null) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        e.ExtractToFile(dest, true);
                        overrides++;
                    }
                return J.D("instance", inst, "files", jobs.Count, "overrides", overrides, "skipped", skipped, "mc", mc, "loader", loader, "loaderVersion", lver);
            }
        }

        // ---------- импорт локальных файлов ----------
        // .mrpack → новая инсталляция; .jar → mods; .zip → по содержимому: шейдер / датапак (нужен мир) / ресурс-пак.
        public static Dictionary<string, object> ImportFiles(string instId, List<string> files, string world)
        {
            var done = new List<object>();
            var needWorld = new List<object>();
            var errors = new List<object>();
            Dictionary<string, object> created = null;
            foreach (var path in files)
            {
                try
                {
                    if (!File.Exists(path)) throw new Exception("нет файла");
                    string ext = Path.GetExtension(path).ToLowerInvariant(), kind;
                    if (ext == ".mrpack") { created = ImportMrpack(path, null); done.Add(J.D("file", Path.GetFileName(path), "kind", "modpack")); continue; }
                    if (ext == ".jar") kind = "mod";
                    else if (ext == ".zip") kind = ClassifyZip(path);
                    else throw new Exception("поддерживаются .jar, .zip и .mrpack");
                    if (kind == "modpack") { created = ImportMrpack(path, null); done.Add(J.D("file", Path.GetFileName(path), "kind", "modpack")); continue; }
                    var inst = Instances.Get(instId);
                    if (kind == "mod" && inst.loader == "vanilla") throw new Exception("в инсталляции без загрузчика моды не работают");
                    if (kind == "datapack" && string.IsNullOrEmpty(world)) { needWorld.Add(path); continue; }
                    string dir = KindDir(inst, kind, world);
                    Directory.CreateDirectory(dir);
                    File.Copy(path, Path.Combine(dir, Path.GetFileName(path)), true);
                    done.Add(J.D("file", Path.GetFileName(path), "kind", kind));
                }
                catch (Exception e) { errors.Add(Path.GetFileName(path) + ": " + e.Message); }
            }
            return J.D("imported", done, "needWorld", needWorld, "errors", errors, "created", created == null ? null : created["instance"]);
        }

        static string ClassifyZip(string path)
        {
            using (var z = ZipFile.OpenRead(path))
            {
                bool mcmeta = false, assets = false, data = false, shaders = false;
                foreach (var e in z.Entries)
                {
                    string n = e.FullName.Replace('\\', '/');
                    if (n == "modrinth.index.json") return "modpack";
                    if (n == "pack.mcmeta") mcmeta = true;
                    if (n.StartsWith("assets/")) assets = true;
                    if (n.StartsWith("data/")) data = true;
                    if (n.StartsWith("shaders/")) shaders = true;
                }
                if (shaders) return "shader";
                if (mcmeta && data && !assets) return "datapack";
                if (mcmeta || assets) return "resourcepack";
            }
            throw new Exception("не похоже на ресурс-пак, шейдер или датапак");
        }
    }
}
