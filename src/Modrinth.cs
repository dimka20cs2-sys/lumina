// Пакет «Оптимизация» с Modrinth под загрузчик инсталляции (Fabric/Quilt/Forge/NeoForge):
// подбор сборок под версию игры и загрузчик, обязательные зависимости, проверка SHA-512. Fabric-моды в Forge не попадают.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AkeeLauncher
{
    static class Modrinth
    {
        const string Api = "https://api.modrinth.com/v2/";
        public static readonly string[] Pack = { "sodium", "lithium", "ferrite-core", "immediatelyfast", "entityculling", "modernfix", "krypton", "dynamic-fps" };

        // Группы: внутри группы — взаимозаменяемые моды, ставится первый доступный (Sodium или Embeddium, не оба).
        static string[][] Groups(string loader)
        {
            if (loader == "forge" || loader == "neoforge")
                return new[] {
                    new[] { "sodium", "embeddium" }, new[] { "lithium" }, new[] { "ferrite-core" }, new[] { "modernfix" },
                    new[] { "immediatelyfast" }, new[] { "entityculling" }, new[] { "dynamic-fps" },
                };
            var g = new List<string[]>();
            foreach (var s in Pack) g.Add(new[] { s });
            return g.ToArray();
        }

        public static string[] PackFor(string loader)
        {
            if (loader == "vanilla" || !Loaders.IsKnown(loader)) return new string[0];
            var r = new List<string>();
            foreach (var g in Groups(loader)) r.AddRange(g);
            return r.ToArray();
        }

        public static Dictionary<string, object> Packs()
        {
            var r = new Dictionary<string, object>();
            foreach (var l in Loaders.All) r[l] = PackFor(l);
            return r;
        }

        // Какие сборки Modrinth подходят: Quilt запускает и Fabric-моды; NeoForge 1.20.1 — форк Forge 47 и берёт Forge-моды.
        public static string[] ModLoaders(string loader, string mc)
        {
            switch (loader)
            {
                case "quilt": return new[] { "quilt", "fabric" };
                case "neoforge": return mc == "1.20.1" ? new[] { "neoforge", "forge" } : new[] { "neoforge" };
                default: return new[] { loader };
            }
        }

        public class ModFile
        {
            public string ProjectId, Slug, Title, Version, FileName, Url, Sha512;
            public long Size;
            public bool Dependency;
        }

        public class Plan
        {
            public List<ModFile> Mods = new List<ModFile>();
            public List<string> Skipped = new List<string>();
        }

        // Порядок попыток подбора сборки. Quilt: сначала Fabric-сборки (они идут на Quilt и не требуют QSL,
        // которой для новых версий игры нет), затем родные Quilt-сборки.
        public static string[][] LoaderTries(string loader, string mc)
        {
            if (loader == "quilt") return new[] { new[] { "fabric" }, new[] { "quilt" } };
            return new[] { ModLoaders(loader, mc) };
        }

        static Dictionary<string, object> BestVersion(string project, string mc, string[] loaders)
        {
            if (loaders.Length == 1 && loaders[0] == "quilt-auto")
            {
                foreach (var t in LoaderTries("quilt", mc)) { var v = BestVersion(project, mc, t); if (v != null) return v; }
                return null;
            }
            string url = Api + "project/" + Http.Q(project) + "/version?loaders=" + Http.Q(J.Write(loaders)) + "&game_versions=" + Http.Q("[\"" + mc + "\"]");
            List<object> list;
            try { list = J.A(J.Parse(Http.GetRetry(url))); }
            catch (HttpError e) { if (e.Status == 404) return null; throw; }
            Dictionary<string, object> best = null;
            foreach (var o in list)   // Modrinth отдаёт от новых к старым; предпочитаем release
            {
                var v = J.O(o);
                if (J.S(v, "version_type") == "release") return v;
                if (best == null) best = v;
            }
            return best;
        }

        static ModFile ToFile(Dictionary<string, object> v, string slug)
        {
            Dictionary<string, object> file = null;
            foreach (var f in J.A(v, "files")) { if (file == null || J.B(J.O(f), "primary")) file = J.O(f); if (J.B(J.O(f), "primary")) break; }
            if (file == null) return null;
            return new ModFile
            {
                ProjectId = J.S(v, "project_id"), Slug = slug, Title = J.S(v, "name"), Version = J.S(v, "version_number"),
                FileName = Path.GetFileName(J.S(file, "filename")), Url = J.S(file, "url"),
                Sha512 = J.S(J.O(file, "hashes"), "sha512"), Size = J.L(file, "size"),
            };
        }

        // Подбор пакета под версию и загрузчик (без скачивания) — используется и самотестом.
        public static Plan Resolve(string mc) { return Resolve(mc, "fabric"); }

        public static Plan Resolve(string mc, string loader)
        {
            var plan = new Plan();
            var have = new HashSet<string>();
            var deps = new Queue<string>();
            var loaders = loader == "quilt" ? new[] { "quilt-auto" } : ModLoaders(loader, mc);
            foreach (var group in Groups(loader))
            {
                Dictionary<string, object> v = null;
                ModFile mf = null;
                foreach (var slug in group)
                {
                    Events.Stage("mods", "Modrinth: " + slug);
                    v = BestVersion(slug, mc, loaders);
                    mf = v == null ? null : ToFile(v, slug);
                    if (mf != null) break;
                }
                if (mf == null) { plan.Skipped.AddRange(group); continue; }
                if (!have.Add(mf.ProjectId)) continue;
                plan.Mods.Add(mf);
                QueueDeps(v, deps);
            }
            while (deps.Count > 0)
            {
                string pid = deps.Dequeue();
                if (have.Contains(pid)) continue;
                var v = BestVersion(pid, mc, loaders);
                var mf = v == null ? null : ToFile(v, pid);
                if (mf == null) { plan.Skipped.Add("зависимость " + ProjectSlug(pid)); have.Add(pid); continue; }
                have.Add(mf.ProjectId);
                mf.Dependency = true;
                mf.Slug = ProjectSlug(pid);
                plan.Mods.Add(mf);
                QueueDeps(v, deps);
            }
            return plan;
        }

        static void QueueDeps(Dictionary<string, object> v, Queue<string> q)
        {
            foreach (var d in J.A(v, "dependencies"))
            {
                var dd = J.O(d);
                if (J.S(dd, "dependency_type") == "required" && J.S(dd, "project_id") != null) q.Enqueue(J.S(dd, "project_id"));
            }
        }

        static string ProjectSlug(string pid)
        {
            try { return J.S(J.ParseObj(Http.GetRetry(Api + "project/" + Http.Q(pid))), "slug") ?? pid; } catch { return pid; }
        }

        // Установка в папку mods инсталляции. Файлы прежнего пакета, которых нет в новом плане (другая версия мода,
        // другая версия игры или загрузчик), удаляются; чужие моды не трогаются.
        public static Dictionary<string, object> Install(Instance inst)
        {
            if (inst.loader == "vanilla" || !Loaders.IsKnown(inst.loader))
                throw new Exception("Пакету «Оптимизация» нужен загрузчик модов. Выберите Fabric, Quilt, Forge или NeoForge в настройках инсталляции.");
            var plan = Resolve(inst.mc, inst.loader);
            string mods = Path.Combine(Instances.Dir(inst.id), "mods");
            Directory.CreateDirectory(mods);
            var jobs = new List<DownloadJob>();
            foreach (var m in plan.Mods)
                jobs.Add(new DownloadJob { Url = m.Url, Path = Path.Combine(mods, m.FileName), Hash = m.Sha512, Algo = "sha512", Size = m.Size });
            Downloader.Run(jobs, "Моды оптимизации");

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in plan.Mods) keep.Add(m.FileName);
            RemovePackFiles(inst.id, keep);

            var state = new Dictionary<string, object>();
            var installed = new List<object>();
            foreach (var m in plan.Mods)
            {
                state[m.ProjectId] = J.D("slug", m.Slug, "file", m.FileName, "version", m.Version, "dependency", m.Dependency);
                installed.Add(J.D("slug", m.Slug, "title", m.Title, "version", m.Version, "file", m.FileName, "dependency", m.Dependency));
            }
            File.WriteAllText(StateFile(inst.id), J.Write(J.D("mc", inst.mc, "loader", inst.loader, "mods", state, "skipped", plan.Skipped)), Encoding.UTF8);
            inst.optimized = true;
            Instances.Save(inst);
            return J.D("installed", installed, "skipped", plan.Skipped);
        }

        public static void RemovePackFiles(string id) { RemovePackFiles(id, null); }

        static void RemovePackFiles(string id, HashSet<string> keep)
        {
            string mods = Path.Combine(Instances.Dir(id), "mods");
            foreach (var kv in ReadState(id))
            {
                string f = J.S(J.O(kv.Value), "file");
                if (f == null || (keep != null && keep.Contains(f))) continue;
                f = Path.GetFileName(f);
                foreach (var p in new[] { Path.Combine(mods, f), Path.Combine(mods, f + ".disabled") })
                    try { if (File.Exists(p)) File.Delete(p); } catch { }
            }
            if (keep == null) try { File.Delete(StateFile(id)); } catch { }
        }

        static string StateFile(string id) { return Path.Combine(Instances.Dir(id), "lumina-optimization.json"); }

        static Dictionary<string, object> ReadState(string id)
        {
            try { return J.O(J.ParseObj(File.ReadAllText(StateFile(id), Encoding.UTF8)), "mods") ?? new Dictionary<string, object>(); }
            catch { return new Dictionary<string, object>(); }
        }

        // Для вкладки «Моды»: все .jar в папке mods + отметка, какие из пакета.
        public static Dictionary<string, object> Status(string id)
        {
            var inst = Instances.Get(id);
            string mods = Path.Combine(Instances.Dir(id), "mods");
            var pack = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in ReadState(id)) { string f = J.S(J.O(kv.Value), "file"); if (f != null) pack[f] = J.S(J.O(kv.Value), "slug"); }
            var files = new List<object>();
            if (Directory.Exists(mods))
                foreach (var f in new DirectoryInfo(mods).GetFiles("*.jar*"))
                {
                    string slug;
                    pack.TryGetValue(f.Name, out slug);
                    files.Add(J.D("file", f.Name, "size", f.Length, "fromPack", slug != null, "slug", slug ?? "", "enabled", !f.Name.EndsWith(".disabled")));
                }
            List<object> skipped = new List<object>();
            try { skipped = J.A(J.ParseObj(File.ReadAllText(StateFile(id), Encoding.UTF8)), "skipped"); } catch { }
            return J.D("instance", id, "loader", inst.loader, "mc", inst.mc, "optimized", inst.optimized, "pack", PackFor(inst.loader), "mods", files, "skipped", skipped);
        }
    }
}
