// Консольный самотест (Lumina.SelfTest.exe <папка> [--skip-modnest]): список релизов, загрузка с проверкой хешей,
// Fabric/Quilt/Forge/NeoForge (установщики Forge/NeoForge запускаются без окна), командная строка запуска,
// пакет «Оптимизация» по загрузчикам, «Моды» (ModNest): поиск, зависимости, обновления, .mrpack.
// Эндпоинты входа проверяются без учётных данных. Сама игра НЕ запускается.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace AkeeLauncher
{
    static partial class SelfTestProgram
    {
        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Length < 1) { Console.WriteLine("Использование: Lumina.SelfTest.exe <пустая папка> [--skip-modnest] [--only-features]"); return 2; }
            bool skipModNest = args.Contains("--skip-modnest");
            Paths.ConfigRoot = Path.GetFullPath(args[0]);
            Paths.DataRoot = Paths.ConfigRoot;
            Directory.CreateDirectory(Paths.DataRoot);
            Http.Init();
            Config.Current = new Settings { concurrency = 16 };

            var lastPrint = new Dictionary<string, DateTime>();
            Events.Sink = (name, d) =>
            {
                if (name == "stage") Console.WriteLine("  · " + J.S(d, "text"));
                else if (name == "log")
                {
                    string line = J.S(d, "line") ?? "";
                    Console.WriteLine("    | " + (line.Length > 200 ? line.Substring(0, 200) + "…" : line));
                }
                else if (name == "progress")
                {
                    string label = J.S(d, "label");
                    bool final = J.L(d, "files") == J.L(d, "filesTotal");
                    DateTime t;
                    if (!final && lastPrint.TryGetValue(label, out t) && (DateTime.Now - t).TotalSeconds < 3) return;
                    lastPrint[label] = DateTime.Now;
                    Console.WriteLine(string.Format("    [{0}] файлы {1}/{2}, {3:0.0}/{4:0.0} МБ, скорость {5:0.0} МБ/с, скачано {6}{7}",
                        label, J.L(d, "files"), J.L(d, "filesTotal"), J.L(d, "bytes") / 1048576.0, J.L(d, "bytesTotal") / 1048576.0,
                        J.L(d, "speed") / 1048576.0, J.L(d, "downloaded"), final ? " ✓" : ""));
                }
            };

            int fails = 0;
            if (args.Contains("--only-features"))
            {
                fails = FeatureTests();
                Console.WriteLine();
                Console.WriteLine(fails == 0 ? "ИТОГ: все проверки пройдены" : "ИТОГ: ошибок — " + fails);
                return fails == 0 ? 0 : 1;
            }
            var fake = new AuthSession { Name = "SelfTest", Uuid = "00000000000000000000000000000000", AccessToken = "SELFTEST-NO-TOKEN" };
            const string Mc = "1.21.1";

            fails += Step("Старые настройки (поле showSnapshots) читаются", () =>
            {
                var s = J.Ser.Deserialize<Settings>("{\"showSnapshots\":true,\"ramMb\":4096}");
                if (s.ramMb != 4096) throw new Exception("настройки не прочитаны");
            });

            fails += Step("Список версий: только релизы, от новой до 1.20", () =>
            {
                var list = Versions.List();
                string first = J.S(J.O(list[0]), "id"), last = J.S(J.O(list[list.Count - 1]), "id");
                Console.WriteLine("  релизов: " + list.Count + ", первая: " + first + ", последняя: " + last + "; latest.release = " + Versions.LatestRelease());
                foreach (var o in list) if (J.S(J.O(o), "type") != "release") throw new Exception("в списке не релиз: " + J.S(J.O(o), "id"));
                if (first != Versions.LatestRelease()) throw new Exception("первая в списке не самый новый релиз");
                if (last != Versions.OldestVersion) throw new Exception("последняя в списке не " + Versions.OldestVersion);
            });
            fails += Step("RAM", () => Console.WriteLine("  ОЗУ ПК " + LaunchBuilder.TotalRamMb() + " МБ → авто " + LaunchBuilder.AutoRamMb() + " МБ; Windows " + Rules.OsVersion));

            fails += Step("Поддержка загрузчиков по версиям", () =>
            {
                foreach (var mc in new[] { Mc, "1.20.1", "1.20", Versions.LatestRelease() })
                {
                    var sup = Loaders.Support(mc);
                    var parts = new List<string>();
                    foreach (var l in Loaders.All)
                        if (l != "vanilla") parts.Add(l + (J.B(sup, l) ? "=" + (Loaders.DefaultVersion(l, mc) ?? "?") + " (" + Loaders.List(l, mc).Count + " шт.)" : "=нет"));
                    Console.WriteLine("  " + mc + ": " + string.Join(", ", parts));
                }
            });

            Instance van = null;
            fails += Step(Mc + " vanilla: загрузка и командная строка", () =>
            {
                van = Instances.Upsert(new Instance { name = "Тест Vanilla", mc = Mc });
                var p = Timed(() => Launcher.Prepare(van, fake), "первая подготовка");
                Console.WriteLine("  скачано файлов: " + p.Game.Downloaded + ", classpath: " + p.Game.Classpath.Count + " шт.");
                Check(p);
                Print(p.Command);
                var p2 = Timed(() => Launcher.Prepare(van, fake), "повторная подготовка (всё уже проверено)");
                Console.WriteLine("  скачано повторно: " + p2.Game.Downloaded + " (ожидается 0)");
                if (p2.Game.Downloaded != 0) throw new Exception("повторная подготовка что-то скачала");

                // Порча файла: проверка хеша должна его перекачать.
                string victim = p.Game.Classpath.First(c => c.Contains(@"\libraries\"));
                var bytes = File.ReadAllBytes(victim); bytes[bytes.Length / 2] ^= 0xFF; File.WriteAllBytes(victim, bytes);
                File.SetLastWriteTimeUtc(victim, DateTime.UtcNow.AddMinutes(-1));
                var p3 = Launcher.Prepare(van, fake);
                Console.WriteLine("  испорчен " + Path.GetFileName(victim) + " → перекачано: " + p3.Game.Downloaded + " (ожидается 1)");
                if (p3.Game.Downloaded != 1) throw new Exception("испорченный файл не перекачан");
            });

            var byLoader = new Dictionary<string, Instance>();
            foreach (var loader in new[] { "fabric", "quilt", "forge", "neoforge" })
            {
                string l = loader;
                fails += Step(Mc + " + " + Loaders.Title(l) + ": установка и командная строка", () =>
                {
                    var inst = Instances.Upsert(new Instance { name = "Тест " + Loaders.Title(l), mc = Mc, loader = l });
                    byLoader[l] = inst;
                    var p = Timed(() => Launcher.Prepare(inst, fake), "установка и подготовка");
                    Console.WriteLine("  профиль: " + p.Game.Version.Id + " (" + l + " " + p.Game.Version.LoaderVersion + "), mainClass: " + p.Game.Version.MainClass);
                    Check(p);
                    Print(p.Command);
                    var p2 = Timed(() => Launcher.Prepare(inst, fake), "повторная подготовка (без переустановки)");
                    if (p2.Game.Downloaded != 0) throw new Exception("повторная подготовка что-то скачала: " + p2.Game.Downloaded);
                });
                fails += Step("Пакет «Оптимизация» для " + Loaders.Title(l) + " " + Mc + " (подбор)", () =>
                {
                    var plan = Modrinth.Resolve(Mc, l);
                    Console.WriteLine("  моды: " + string.Join(", ", plan.Mods.Select(x => x.Slug + " " + x.Version + (x.Dependency ? " (зависимость)" : ""))));
                    Console.WriteLine("  пропущено: " + (plan.Skipped.Count == 0 ? "нет" : string.Join(", ", plan.Skipped)));
                    if (plan.Mods.Count == 0) throw new Exception("ни одного мода");
                });
            }

            fails += Step("Forge: повреждённая установка переустанавливается", () =>
            {
                Instance fi;
                if (!byLoader.TryGetValue("forge", out fi)) throw new Exception("нет Forge-инсталляции");
                var v = Versions.Resolve(fi.mc, fi.loader, fi.loaderVersion);
                string json = Path.Combine(Paths.Versions, v.Id, v.Id + ".json");
                Console.WriteLine("  до порчи: " + (Loaders.CheckInstalled(v.Id) ?? "всё на месте"));
                File.WriteAllText(json, "{ испорчено");
                Console.WriteLine("  после порчи: " + (Loaders.CheckInstalled(v.Id) ?? "всё на месте"));
                var p = Timed(() => Launcher.Prepare(fi, fake), "подготовка с переустановкой");
                Check(p);
                if (Loaders.CheckInstalled(v.Id) != null) throw new Exception("не переустановлен");
            });

            fails += Step("Пакет «Оптимизация»: установка в Fabric, затем смена на NeoForge (Fabric-моды убираются)", () =>
            {
                Instance fab;
                if (!byLoader.TryGetValue("fabric", out fab)) throw new Exception("нет Fabric-инсталляции");
                var r = Modrinth.Install(fab);
                Console.WriteLine("  Fabric: установлено " + J.A(r["installed"]).Count + ", пропущено: " + J.A(r["skipped"]).Count);
                int before = Directory.GetFiles(Path.Combine(Instances.Dir(fab.id), "mods")).Length;
                fab.loader = "neoforge"; fab.loaderVersion = "";
                fab = Instances.Upsert(fab);
                int after = Directory.GetFiles(Path.Combine(Instances.Dir(fab.id), "mods")).Length;
                Console.WriteLine("  файлов в mods: " + before + " → после смены загрузчика " + after);
                if (after != 0) throw new Exception("Fabric-моды остались");
                var r2 = Modrinth.Install(fab);
                Console.WriteLine("  NeoForge: установлено " + J.A(r2["installed"]).Count + " (" + string.Join(", ", J.A(r2["installed"]).Select(o => J.S(J.O(o), "slug"))) + ")");
            });

            if (!skipModNest) fails += ModNestTests();

            fails += Step("Эндпоинты входа Microsoft/Minecraft (без учётных данных)", () => Console.Write(MicrosoftAuth.ProbeEndpoints()));

            fails += FeatureTests();

            Console.WriteLine();
            Console.WriteLine("Размер папки: " + (DirSize(Paths.DataRoot) / 1048576) + " МБ");
            Console.WriteLine(fails == 0 ? "ИТОГ: все проверки пройдены" : "ИТОГ: ошибок — " + fails);
            return fails == 0 ? 0 : 1;
        }

        static int Step(string title, Action a)
        {
            Console.WriteLine();
            Console.WriteLine("== " + title);
            try { a(); Console.WriteLine("  OK"); return 0; }
            catch (Exception e) { Console.WriteLine("  ОШИБКА: " + e); return 1; }
        }

        // Всё для запуска на месте: файлы classpath существуют, нет неподставленных ${…}, есть mainClass.
        static void Check(Launcher.Prepared p)
        {
            if (string.IsNullOrEmpty(p.Game.Version.MainClass)) throw new Exception("нет mainClass");
            foreach (var c in p.Game.Classpath) if (!File.Exists(c)) throw new Exception("нет файла classpath: " + c);
            foreach (var a in p.Command.Args) if (a.Contains("${")) throw new Exception("неподставленная переменная: " + a);
            if (!File.Exists(p.Command.Javaw)) throw new Exception("нет Java: " + p.Command.Javaw);
            // Пути из модульного пути (-p) Forge/NeoForge тоже должны существовать.
            int i = p.Command.Args.IndexOf("-p");
            if (i >= 0)
            {
                foreach (var m in p.Command.Args[i + 1].Split(';')) if (!File.Exists(m)) throw new Exception("нет файла module path: " + m);
                Console.WriteLine("  module path (-p): " + p.Command.Args[i + 1].Split(';').Length + " jar, все на месте");
            }
            Console.WriteLine("  classpath: " + p.Game.Classpath.Count + " файлов, все на месте; клиент: " + p.Game.ClientJar);
        }

        // «Моды» (ModNest): поиск, установка с зависимостями, ресурс-пак, шейдер + Iris, датапак, обновления, .mrpack.
        static int ModNestTests()
        {
            int fails = 0;
            ModNest.UseRecycleBin = false;   // самотест не трогает Корзину
            Instance inst = null;
            fails += Step("ModNest: поиск по типам", () =>
            {
                inst = Instances.Upsert(new Instance { name = "Тест ModNest", mc = "1.21.1", loader = "fabric" });
                foreach (var k in new[] { new[] { "mod", "sodium", "relevance", "" }, new[] { "mod", "", "downloads", "optimization" },
                                          new[] { "resourcepack", "", "downloads", "" }, new[] { "shader", "", "downloads", "" },
                                          new[] { "datapack", "terralith", "relevance", "" }, new[] { "modpack", "optimized", "downloads", "" } })
                {
                    var r = ModNest.Search(inst.id, k[0], k[1], k[2], k[3], 0, 5);
                    var hits = J.A(r["hits"]);
                    Console.WriteLine("  " + k[0] + " «" + k[1] + "» " + k[2] + (k[3] != "" ? " [" + k[3] + "]" : "") + ": всего " + r["total"] + "; " +
                        string.Join(", ", hits.Take(3).Select(h => J.S(J.O(h), "title") + " (" + J.S(J.O(h), "author") + ", " + J.L(J.O(h), "downloads") + ")")));
                    if (hits.Count == 0) throw new Exception("пустой результат: " + k[0]);
                }
                Console.WriteLine("  категорий: моды " + ModNest.Categories("mod").Count + ", шейдеры " + ModNest.Categories("shader").Count);
            });

            Func<Dictionary<string, object>, string> fmt = r =>
                string.Join(", ", J.A(r["installed"]).Select(o => J.S(J.O(o), "title") + " " + J.S(J.O(o), "version") + " [" + J.S(J.O(o), "kind") + (J.B(J.O(o), "dependency") ? ", зависимость" : "") + "]"))
                + (J.A(r["missing"]).Count > 0 ? "; не найдено: " + string.Join(", ", J.A(r["missing"])) : "");

            fails += Step("ModNest: установка модов и ресурс-пака с зависимостями", () =>
            {
                Console.WriteLine("  modmenu → " + fmt(ModNest.Install(inst.id, "modmenu", "mod", null)));
                Console.WriteLine("  appleskin → " + fmt(ModNest.Install(inst.id, "appleskin", "mod", null)));
                Console.WriteLine("  fresh-animations → " + fmt(ModNest.Install(inst.id, "fresh-animations", "resourcepack", null)));
                var sh = ModNest.Install(inst.id, "complementary-reimagined", "shader", null);
                Console.WriteLine("  complementary-reimagined → " + fmt(sh) + "; нужен загрузчик шейдеров: " + J.S(sh, "needsShaderLoader"));
                if (J.S(sh, "needsShaderLoader") != "iris") throw new Exception("не предложен Iris");
                Console.WriteLine("  загрузчик шейдеров → " + fmt(ModNest.InstallShaderLoader(inst.id)));
                if (!ModNest.HasShaderLoader(inst)) throw new Exception("Iris не установлен");
                var items = ModNest.Installed(inst.id);
                foreach (var k in new[] { "mod", "resourcepack", "shader" })
                    Console.WriteLine("  установлено [" + k + "]: " + string.Join(", ", items.Where(o => J.S(J.O(o), "kind") == k).Select(o => J.S(J.O(o), "file"))));
                if (!items.Any(o => J.S(J.O(o), "file").StartsWith("fabric-api"))) throw new Exception("зависимость fabric-api не поставлена");
                if (!items.Any(o => J.S(J.O(o), "kind") == "resourcepack")) throw new Exception("нет ресурс-пака");
            });

            fails += Step("ModNest: датапак в мир, выключение и удаление", () =>
            {
                string world = Path.Combine(Instances.Dir(inst.id), "saves", "Тестовый мир");
                Directory.CreateDirectory(world);
                File.WriteAllBytes(Path.Combine(world, "level.dat"), new byte[] { 0 });
                Console.WriteLine("  миры: " + string.Join(", ", ModNest.Worlds(inst.id)));
                Console.WriteLine("  terralith → " + fmt(ModNest.Install(inst.id, "terralith", "datapack", "Тестовый мир")));
                var dp = ModNest.Installed(inst.id).Select(J.O).First(o => J.S(o, "kind") == "datapack");
                Console.WriteLine("  датапак: " + J.S(dp, "path"));
                var after = ModNest.Toggle(inst.id, J.S(dp, "path")).Select(J.O).First(o => J.S(o, "kind") == "datapack");
                if (J.B(after, "enabled")) throw new Exception("не выключился");
                ModNest.Toggle(inst.id, J.S(after, "path"));
                var left = ModNest.Remove(inst.id, J.S(dp, "path"));
                if (left.Any(o => J.S(J.O(o), "kind") == "datapack")) throw new Exception("датапак не удалён");
                Console.WriteLine("  выключен → включён → удалён: OK");
            });

            fails += Step("ModNest: проверка и установка обновлений (по SHA-1, /version_files/update)", () =>
            {
                // Подкладываем старую версию AppleSkin вместо свежей.
                string mods = Path.Combine(Instances.Dir(inst.id), "mods");
                foreach (var f in Directory.GetFiles(mods, "appleskin*")) File.Delete(f);
                var vers = J.A(J.Parse(Http.GetRetry("https://api.modrinth.com/v2/project/appleskin/version?loaders=" + Http.Q("[\"fabric\"]") + "&game_versions=" + Http.Q("[\"1.21.1\"]"))));
                var oldest = J.O(vers[vers.Count - 1]);
                var file = J.O(J.A(oldest, "files")[0]);
                Downloader.Run(new List<DownloadJob> { new DownloadJob { Url = J.S(file, "url"), Path = Path.Combine(mods, J.S(file, "filename")), Hash = J.S(J.O(file, "hashes"), "sha512"), Algo = "sha512" } }, "старый AppleSkin");
                Console.WriteLine("  подложен " + J.S(file, "filename") + " (" + vers.Count + " версий для 1.21.1)");
                var chk = ModNest.CheckUpdates(inst.id);
                var ups = J.A(chk["updates"]);
                Console.WriteLine("  проверено файлов: " + chk["checked"] + ", не с Modrinth: " + chk["unknown"] + ", обновлений: " + ups.Count);
                foreach (var u in ups) Console.WriteLine("    " + J.S(J.O(u), "file") + ": " + J.S(J.O(u), "from") + " → " + J.S(J.O(u), "to"));
                if (!ups.Any(u => J.S(J.O(u), "file").StartsWith("appleskin"))) throw new Exception("обновление AppleSkin не найдено");
                var r = ModNest.UpdateAll(inst.id);
                Console.WriteLine("  обновлено: " + J.A(r["updated"]).Count);
                if (File.Exists(Path.Combine(mods, J.S(file, "filename")))) throw new Exception("старый файл остался");
                var again = J.A(ModNest.CheckUpdates(inst.id)["updates"]);
                Console.WriteLine("  повторная проверка: обновлений " + again.Count + " (ожидается 0)");
                if (again.Count != 0) throw new Exception("после обновления остались обновления");
            });

            fails += Step("ModNest: импорт .mrpack (свой маленький пакет) и локального ресурс-пака", () =>
            {
                string dir = Path.Combine(Paths.DataRoot, "mrpack-test");
                Directory.CreateDirectory(dir);
                var mv = J.A(J.Parse(Http.GetRetry("https://api.modrinth.com/v2/project/modmenu/version?loaders=" + Http.Q("[\"fabric\"]") + "&game_versions=" + Http.Q("[\"1.21.1\"]"))));
                var mf = J.O(J.A(J.O(mv[0]), "files")[0]);
                var index = J.D("formatVersion", 1, "game", "minecraft", "versionId", "1.0", "name", "Мини-сборка Lumina",
                    "files", new object[] { J.D("path", "mods/" + J.S(mf, "filename"), "hashes", J.O(mf, "hashes"), "downloads", new[] { J.S(mf, "url") }, "fileSize", J.L(mf, "size"), "env", J.D("client", "required", "server", "optional")) },
                    "dependencies", J.D("minecraft", "1.21.1", "fabric-loader", "0.16.10"));
                string pack = Path.Combine(dir, "mini.mrpack");
                if (File.Exists(pack)) File.Delete(pack);
                using (var z = System.IO.Compression.ZipFile.Open(pack, System.IO.Compression.ZipArchiveMode.Create))
                {
                    using (var w = new StreamWriter(z.CreateEntry("modrinth.index.json").Open(), new UTF8Encoding(false))) w.Write(J.Write(index));
                    using (var w = new StreamWriter(z.CreateEntry("overrides/config/lumina-test.txt").Open())) w.Write("ok");
                    using (var w = new StreamWriter(z.CreateEntry("overrides/../escape.txt").Open())) w.Write("must not escape");
                }
                string rp = Path.Combine(dir, "my-pack.zip");
                if (File.Exists(rp)) File.Delete(rp);
                using (var z = System.IO.Compression.ZipFile.Open(rp, System.IO.Compression.ZipArchiveMode.Create))
                {
                    using (var w = new StreamWriter(z.CreateEntry("pack.mcmeta").Open())) w.Write("{\"pack\":{\"pack_format\":34,\"description\":\"test\"}}");
                    z.CreateEntry("assets/minecraft/textures/.keep");
                }
                var r = ModNest.ImportFiles(inst.id, new List<string> { pack, rp }, null);
                Console.WriteLine("  импорт: " + string.Join(", ", J.A(r["imported"]).Select(o => J.S(J.O(o), "file") + "→" + J.S(J.O(o), "kind"))) + "; ошибки: " + string.Join("; ", J.A(r["errors"])));
                var ni = (Instance)r["created"];
                Console.WriteLine("  новая инсталляция: " + ni.name + " (" + ni.mc + ", " + ni.loader + " " + ni.loaderVersion + ")");
                string nd = Instances.Dir(ni.id);
                if (!File.Exists(Path.Combine(nd, "mods", J.S(mf, "filename")))) throw new Exception("мод из сборки не скачан");
                if (!File.Exists(Path.Combine(nd, "config", "lumina-test.txt"))) throw new Exception("overrides не распакованы");
                if (File.Exists(Path.Combine(Paths.Instances, "escape.txt"))) throw new Exception("выход за папку инсталляции!");
                if (!File.Exists(Path.Combine(Instances.Dir(inst.id), "resourcepacks", "my-pack.zip"))) throw new Exception("ресурс-пак не импортирован");
            });

            fails += Step("ModNest: сборка с Modrinth (.mrpack → новая инсталляция)", () =>
            {
                var r = Timed(() => ModNest.InstallModpack("fabulously-optimized"), "установка сборки");
                var ni = (Instance)r["instance"];
                Console.WriteLine("  " + ni.name + ": Minecraft " + r["mc"] + ", " + r["loader"] + " " + r["loaderVersion"] + "; файлов " + r["files"] + ", overrides " + r["overrides"] + ", пропущено " + r["skipped"]);
                string md = Path.Combine(Instances.Dir(ni.id), "mods");
                int mods = Directory.Exists(md) ? Directory.GetFiles(md).Length : 0;
                Console.WriteLine("  модов в папке: " + mods);
                if (mods == 0) throw new Exception("моды сборки не скачаны");
            });
            return fails;
        }

        static T Timed<T>(Func<T> f, string what)
        {
            var sw = Stopwatch.StartNew();
            var r = f();
            Console.WriteLine("  " + what + ": " + sw.Elapsed.TotalSeconds.ToString("0.0") + " с");
            return r;
        }

        static void Print(LaunchCommand c)
        {
            Console.WriteLine("  Java " + c.JavaMajor + ": " + c.Javaw + (File.Exists(c.Javaw) ? " (есть)" : " (НЕТ!)"));
            string s = c.Printable(null);
            // classpath длинный — сокращаем для вывода
            int cp = s.IndexOf("-cp ");
            if (cp >= 0)
            {
                int end = s.IndexOf(' ', cp + 4);
                if (end > cp + 200) s = s.Substring(0, cp + 4) + s.Substring(cp + 4, 120) + "…[" + (end - cp) + " символов]" + s.Substring(end);
            }
            Console.WriteLine("  КОМАНДА (" + c.ArgString().Length + " символов): " + s);
        }

        static long DirSize(string d)
        {
            long n = 0;
            foreach (var f in new DirectoryInfo(d).EnumerateFiles("*", SearchOption.AllDirectories)) n += f.Length;
            return n;
        }
    }
}
