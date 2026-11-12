// Самотест новых функций (без окна и без запуска игры): «Скины», «Автонастройка графики», «Игровой режим» (только dry-run —
// реестр и схема питания НЕ меняются), «Серверы» (пинг публичных серверов, SRV, servers.dat), «Миры» (копия → проверка → восстановление).
// Всё пишется во временную папку <папка самотеста>\features-test, которая удаляется в конце.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace AkeeLauncher
{
    static partial class SelfTestProgram
    {
        static void Assert(bool ok, string what) { if (!ok) throw new Exception("не выполнено: " + what); }

        static byte[] MakePng(int w, int h, bool slim)
        {
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        bmp.SetPixel(x, y, Color.FromArgb(255, (x * 4) & 255, (y * 4) & 255, 128));
                if (slim && h == 64) for (int y = 16; y < 32; y++) { bmp.SetPixel(54, y, Color.Transparent); bmp.SetPixel(55, y, Color.Transparent); }
                using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); return ms.ToArray(); }
            }
        }

        static int FeatureTests()
        {
            int fails = 0;
            string oldConfig = Paths.ConfigRoot, oldData = Paths.DataRoot;
            string root = Path.Combine(oldData, "features-test");
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(root);
            Paths.ConfigRoot = root; Paths.DataRoot = root;
            Worlds.UseRecycleBin = false;
            try
            {
                fails += Step("Скины: проверка PNG (64×64, 64×32, неверные)", () =>
                {
                    int w, h;
                    var classic = MakePng(64, 64, false); var slim = MakePng(64, 64, true); var legacy = MakePng(64, 32, false);
                    Skins.ValidatePng(classic, out w, out h); Console.WriteLine("  64×64 classic: OK " + w + "×" + h + ", модель " + Skins.DetectVariant(classic));
                    Skins.ValidatePng(slim, out w, out h); Console.WriteLine("  64×64 slim: OK, модель " + Skins.DetectVariant(slim));
                    Skins.ValidatePng(legacy, out w, out h); Console.WriteLine("  64×32: OK " + w + "×" + h + ", модель " + Skins.DetectVariant(legacy));
                    Assert(Skins.DetectVariant(slim) == "slim" && Skins.DetectVariant(classic) == "classic", "определение модели рук");
                    var bad = new Dictionary<string, byte[]> {
                        { "32×32 PNG", MakePng(32, 32, false) }, { "128×128 PNG", MakePng(128, 128, false) },
                        { "мусор", Encoding.ASCII.GetBytes("это вообще не картинка, просто текст для проверки длины 33+") },
                        { "PNG с битой картинкой", classic.Take(60).ToArray() },
                    };
                    foreach (var kv in bad)
                    {
                        try { Skins.ValidatePng(kv.Value, out w, out h); throw new Exception("принят неверный файл: " + kv.Key); }
                        catch (Exception e) { if (e.Message.StartsWith("принят")) throw; Console.WriteLine("  " + kv.Key + " → отклонён: " + e.Message); }
                    }
                });

                fails += Step("Скины: библиотека (добавить, переименовать, дубль, удалить)", () =>
                {
                    string src = Path.Combine(root, "png-src");
                    Directory.CreateDirectory(src);
                    File.WriteAllBytes(Path.Combine(src, "Мой скин.png"), MakePng(64, 64, true));
                    File.WriteAllBytes(Path.Combine(src, "old.png"), MakePng(64, 32, false));
                    File.WriteAllBytes(Path.Combine(src, "big.png"), MakePng(128, 64, false));
                    var r = Skins.ImportFiles(new List<string> { Path.Combine(src, "Мой скин.png"), Path.Combine(src, "old.png"), Path.Combine(src, "big.png") });
                    var added = J.A(r["added"]); var errors = J.A(r["errors"]);
                    Console.WriteLine("  импорт: добавлено " + added.Count + ", ошибок " + errors.Count + " (" + string.Join("; ", errors) + ")");
                    Assert(added.Count == 2 && errors.Count == 1, "2 добавлено, 1 отклонён");
                    var list = Skins.List();
                    var first = J.O(list.First(o => J.S(J.O(o), "name") == "Мой скин"));
                    Assert(J.S(first, "variant") == "slim", "автоопределение slim при импорте");
                    Assert(J.S(first, "dataUrl").StartsWith("data:image/png;base64,"), "dataUrl");
                    string id = J.S(first, "id");
                    Skins.Update(id, "Переименован", "classic");
                    Skins.Duplicate(id);
                    list = Skins.List();
                    Console.WriteLine("  после правки и дубля: " + string.Join(", ", list.Select(o => J.S(J.O(o), "name") + " [" + J.S(J.O(o), "variant") + "]")));
                    Assert(list.Count == 3 && list.Any(o => J.S(J.O(o), "name") == "Переименован (копия)"), "дубль");
                    Skins.Delete(id);
                    Assert(Skins.List().Count == 2 && !File.Exists(Path.Combine(Paths.Skins, id + ".png")), "удаление");
                    Console.WriteLine("  файлов в папке skins: " + Directory.GetFiles(Paths.Skins).Length + " (library.json + 2 PNG)");
                });

                fails += Step("Скины: multipart-запрос на смену скина (собирается, НЕ отправляется)", () =>
                {
                    var png = MakePng(64, 64, false);
                    string b = "----LuminaTest123";
                    var body = Skins.BuildMultipart("slim", png, b);
                    string head = Encoding.UTF8.GetString(body, 0, 260);
                    Console.WriteLine("  размер тела: " + body.Length + " байт (PNG " + png.Length + ")");
                    Console.WriteLine("  начало: " + head.Substring(0, head.IndexOf("Content-Type")).Replace("\r\n", "⏎"));
                    Assert(head.StartsWith("--" + b + "\r\nContent-Disposition: form-data; name=\"variant\"\r\n\r\nslim\r\n"), "поле variant");
                    Assert(head.Contains("name=\"file\"; filename=\"skin.png\"\r\nContent-Type: image/png\r\n\r\n"), "поле file");
                    string all = Encoding.GetEncoding(28591).GetString(body);
                    int pngAt = all.IndexOf("\x89PNG");
                    Assert(pngAt > 0 && body.Skip(pngAt).Take(png.Length).SequenceEqual(png), "PNG внутри тела без изменений");
                    Assert(all.EndsWith("\r\n--" + b + "--\r\n"), "закрывающая граница");
                });

                fails += Step("Скины: разбор профиля Minecraft (скины и плащи) и ошибка без входа", () =>
                {
                    const string sample = "{\"id\":\"069a79f444e94726a5befca90e38aaf5\",\"name\":\"Notch\"," +
                        "\"skins\":[{\"id\":\"6a6e65e5-76dd-4c3c-a625-162924514568\",\"state\":\"ACTIVE\",\"url\":\"http://textures.minecraft.net/texture/292009a4925b58f02c77dadc3ecef07ea4c7472f64e0fdc32ce5522489362680\",\"textureKey\":\"292009a4\",\"variant\":\"SLIM\"}]," +
                        "\"capes\":[{\"id\":\"1981aad373fa9754\",\"state\":\"INACTIVE\",\"url\":\"http://textures.minecraft.net/texture/2340c0e03dd24a11b15a8b33c2a7e9e32abb2051b2481d0ba7defd635ca7a933\",\"alias\":\"Migrator\"}," +
                        "{\"id\":\"c0a5b8d7-0000\",\"state\":\"ACTIVE\",\"url\":\"http://textures.minecraft.net/texture/abc\",\"alias\":\"Vanilla\"}]}";
                    var p = Skins.ParseProfile(sample);
                    var a = J.O(p, "activeSkin");
                    Console.WriteLine("  " + J.S(p, "name") + ": скин " + J.S(a, "variant") + ", плащей " + J.A(p, "capes").Count + ", надет плащ " + J.S(p, "activeCape"));
                    Assert(J.S(a, "variant") == "slim" && J.A(p, "capes").Count == 2 && J.S(p, "activeCape") == "c0a5b8d7-0000", "разбор профиля");
                    try { Skins.AccountProfile(); throw new Exception("без аккаунта запрос прошёл"); }
                    catch (Exception e) { if (e.Message.Contains("прошёл")) throw; Console.WriteLine("  без входа: «" + e.Message + "»"); Assert(e.Message.Contains("войдите"), "понятная ошибка без входа"); }
                });

                HardwareInfo hw = null;
                fails += Step("Графика: железо этого ПК", () =>
                {
                    hw = GraphicsTuner.Detect(true);
                    Console.WriteLine("  видеокарты: " + string.Join(" | ", hw.AllGpus));
                    Console.WriteLine("  выбрана: " + hw.Gpu + " (" + hw.GpuVendor + ", " + hw.VramMb + " МБ, " + (hw.Discrete ? "дискретная" : "встроенная") + ")");
                    Console.WriteLine("  CPU: " + hw.Cpu + " — " + hw.Cores + " ядер / " + hw.Threads + " потоков; ОЗУ " + hw.RamMb + " МБ");
                    Console.WriteLine("  экран: " + hw.ScreenW + "×" + hw.ScreenH + " @ " + hw.RefreshHz + " Гц; уровень ПК: " + hw.Tier + " (" + hw.TierName + ")");
                    Assert(hw.RamMb > 0 && hw.Threads > 0 && hw.RefreshHz >= 30, "базовые параметры");
                });

                fails += Step("Графика: профили в options.txt (+Sodium), бинды сохраняются, резервная копия и откат", () =>
                {
                    string gd = Path.Combine(root, "gfx-instance");
                    Directory.CreateDirectory(Path.Combine(gd, "mods"));
                    Directory.CreateDirectory(Path.Combine(gd, "config"));
                    File.WriteAllBytes(Path.Combine(gd, "mods", "sodium-fabric-0.6.13+mc1.21.1.jar"), new byte[] { 0x50, 0x4b });
                    string origOptions = "version:3955\nlang:ru_ru\nrenderDistance:32\nkey_key.jump:key.keyboard.space\nkey_key.attack:key.mouse.left\nsoundCategory_master:0.5\ngraphicsMode:2\nfov:0.25\n";
                    string origSodium = "{\"quality\":{\"weather_quality\":\"FANCY\",\"enable_vignette\":true},\"notifications\":{\"has_seen_donation_prompt\":true}}";
                    File.WriteAllText(Path.Combine(gd, "options.txt"), origOptions, new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(gd, "config", "sodium-options.json"), origSodium, new UTF8Encoding(false));
                    foreach (var prof in GraphicsTuner.Profiles)
                    {
                        var r = GraphicsTuner.ApplyToDir(gd, "1.21.1", prof, hw, false);
                        var text = File.ReadAllText(Path.Combine(gd, "options.txt"));
                        Func<string, string> val = k => text.Split('\n').Where(l => l.StartsWith(k + ":")).Select(l => l.Substring(k.Length + 1)).FirstOrDefault();
                        Console.WriteLine("  " + prof + ": изменений " + J.A(r["changes"]).Count + " → renderDistance " + val("renderDistance") + ", simulation " + val("simulationDistance") +
                            ", graphicsMode " + val("graphicsMode") + ", maxFps " + val("maxFps") + ", vsync " + val("enableVsync") + ", particles " + val("particles") +
                            ", clouds " + val("renderClouds") + ", biomeBlend " + val("biomeBlendRadius"));
                        Assert(val("key_key.jump") == "key.keyboard.space" && val("lang") == "ru_ru" && val("fov") == "0.25" && val("soundCategory_master") == "0.5", "бинды/язык/прочее сохранены");
                        Assert(text.Split('\n').Count(l => l.StartsWith("renderDistance:")) == 1, "ключ не задублирован");
                        Assert(val("enableVsync") == "false", "vsync выкл");
                        Assert(prof != "fps" || val("maxFps") == "260", "Макс. FPS без лимита");
                        Assert(prof == "fps" || val("maxFps") == Math.Max(30, Math.Min(260, hw.RefreshHz)).ToString(), "лимит = частота экрана");
                        var sod = J.ParseObj(File.ReadAllText(Path.Combine(gd, "config", "sodium-options.json")));
                        Assert(J.B(J.O(sod, "notifications"), "has_seen_donation_prompt"), "чужие ключи Sodium сохранены");
                        Assert(J.S(J.O(sod, "quality"), "weather_quality") == (prof == "fps" ? "FAST" : prof == "quality" ? "FANCY" : "DEFAULT"), "Sodium weather_quality");
                    }
                    var back = GraphicsTuner.RestoreDir(gd);
                    Console.WriteLine("  откат: " + string.Join(", ", J.A(back["restored"])));
                    Assert(File.ReadAllText(Path.Combine(gd, "options.txt")) == origOptions, "options.txt восстановлен байт в байт");
                    Assert(File.ReadAllText(Path.Combine(gd, "config", "sodium-options.json")) == origSodium, "sodium-options.json восстановлен");
                    Assert(!Directory.Exists(Path.Combine(gd, ".lumina-backup", "graphics")), "резервная копия убрана после отката");

                    // Без options.txt: файл создаётся, откат его удаляет.
                    string gd2 = Path.Combine(root, "gfx-empty");
                    Directory.CreateDirectory(gd2);
                    var r2 = GraphicsTuner.ApplyToDir(gd2, "1.21.1", "balance", hw, true);
                    Console.WriteLine("  пустая инсталляция: создан options.txt (" + J.A(r2["changes"]).Count + " ключей), Sodium: " + r2["sodium"]);
                    Assert(File.ReadAllText(Path.Combine(gd2, "options.txt")).Contains("fullscreen:true"), "полный экран из настроек");
                    GraphicsTuner.RestoreDir(gd2);
                    Assert(!File.Exists(Path.Combine(gd2, "options.txt")), "созданный options.txt удалён откатом");
                });

                fails += Step("Игровой режим: dry-run (реестр и схема питания НЕ меняются)", () =>
                {
                    string javaw = Path.Combine(Paths.Runtimes, "java-runtime-delta", "bin", "javaw.exe");
                    string before = null;
                    using (var k = Registry.CurrentUser.OpenSubKey(GameMode.GpuKey)) if (k != null) before = string.Join("|", k.GetValueNames().Select(n => n + "=" + k.GetValue(n)));
                    var d = GameMode.DryRun(javaw);
                    var g = J.O(d, "gpu"); var p = J.O(d, "power");
                    Console.WriteLine("  реестр: " + J.S(g, "key") + " → \"" + J.S(g, "valueName") + "\" = \"" + J.S(g, "wouldWrite") + "\" (сейчас: " + (J.S(g, "current") ?? "нет") + ", подходит: " + J.B(g, "eligible") + ")");
                    Console.WriteLine("  питание: активная " + J.S(p, "active") + ", включили бы " + (J.S(p, "target") ?? "нет (схема скрыта)") + (J.S(p, "error") != null ? ", ошибка: " + J.S(p, "error") : ""));
                    foreach (var o in J.A(p, "schemes")) Console.WriteLine("    " + J.S(J.O(o), "guid") + " " + J.S(J.O(o), "name") + (J.B(J.O(o), "active") ? " *" : ""));
                    if (J.S(p, "target") == null) Console.WriteLine("  схемы «Высокая производительность» нет в списке — во время игры питание не меняется");
                    else if (J.S(p, "target") == J.S(p, "active")) Console.WriteLine("  уже активна нужная схема — ничего не меняется");
                    else Console.WriteLine("  команды: powercfg /setactive " + J.S(p, "target") + " → после игры powercfg /setactive " + J.S(p, "active"));
                    Assert(J.B(g, "eligible"), "javaw из runtimes подходит");
                    Assert(!GameMode.IsLauncherJava(@"C:\Program Files\Java\jdk-21\bin\javaw.exe"), "чужая Java не подходит");
                    Assert(!GameMode.IsLauncherJava(Path.Combine(Paths.Runtimes, "x", "bin", "java.exe")), "только javaw.exe");
                    Assert(GameMode.MergeGpuValue(null) == "GpuPreference=2;", "новое значение");
                    Assert(GameMode.MergeGpuValue("SwapEffectUpgradeEnable=1;") == "SwapEffectUpgradeEnable=1;GpuPreference=2;", "слияние с другими параметрами");
                    Assert(GameMode.MergeGpuValue("GpuPreference=1;SwapEffectUpgradeEnable=1;") == "SwapEffectUpgradeEnable=1;GpuPreference=2;", "замена GpuPreference");
                    Assert(J.S(p, "error") != null || J.S(p, "active") != null, "активная схема прочитана");
                    string after = null;
                    using (var k = Registry.CurrentUser.OpenSubKey(GameMode.GpuKey)) if (k != null) after = string.Join("|", k.GetValueNames().Select(n => n + "=" + k.GetValue(n)));
                    Assert(before == after, "реестр не изменился");
                    Assert(!File.Exists(Paths.GameModeState), "состояние игрового режима не создано");
                    Console.WriteLine("  реестр до/после совпадает, состояние не создано");
                });

                fails += Step("Серверы: адреса, MOTD, SRV", () =>
                {
                    string h; int port;
                    Servers.Split("mc.example.com:25570", out h, out port); Assert(h == "mc.example.com" && port == 25570, "host:port");
                    Servers.Split("[2001:db8::1]:25565", out h, out port); Assert(h == "2001:db8::1" && port == 25565, "IPv6");
                    Servers.Split("play.example.net", out h, out port); Assert(port == 0, "без порта");
                    foreach (var badAddr in new[] { "", "bad host!", "a.b:99999", "a.b:xx" })
                    {
                        try { Servers.NormalizeAddress(badAddr); throw new Exception("принят неверный адрес «" + badAddr + "»"); }
                        catch (Exception e) { if (e.Message.StartsWith("принят")) throw; }
                    }
                    var st = Servers.ParseStatus("{\"version\":{\"name\":\"Paper 1.21.1\",\"protocol\":767},\"players\":{\"max\":100,\"online\":5,\"sample\":[{\"name\":\"Steve\",\"id\":\"x\"}]}," +
                        "\"description\":{\"text\":\"\",\"extra\":[{\"text\":\"Lumina \",\"color\":\"gold\",\"bold\":true},{\"text\":\"§aзелёный §lжирный§r обычный\"},{\"text\":\" hex\",\"color\":\"#12abef\"}]}}");
                    Assert((string)Servers.ParseStatus("{\"version\":{\"name\":\"§f§fWe support: 1.20\"},\"description\":\"x\"}")["version"] == "We support: 1.20", "коды § в версии");
                    Console.WriteLine("  MOTD: «" + st["motdText"] + "», сегментов " + J.A(st["motd"]).Count + ", " + st["players"] + "/" + st["max"] + ", " + st["version"]);
                    var segs = J.A(st["motd"]).Select(J.O).ToList();
                    Assert((string)st["motdText"] == "Lumina зелёный жирный обычный hex", "текст MOTD без кодов §");
                    Assert(J.S(segs[0], "c") == "#FFAA00" && J.B(segs[0], "b"), "цвет и жирность компонента");
                    Assert(segs.Any(s => J.S(s, "t") == "жирный" && J.B(s, "b") && J.S(s, "c") == "#55FF55"), "§a§l");
                    Assert(segs.Any(s => J.S(s, "t") == " hex" && J.S(s, "c") == "#12abef"), "hex-цвет");
                    foreach (var host in new[] { "hypixel.net", "2b2t.org", "mc.hypixel.net" })
                    {
                        string via;
                        var srv = Servers.ResolveSrv(host, out via);
                        Console.WriteLine("  SRV _minecraft._tcp." + host + " → " + (srv == null ? "нет записи" : srv[0] + ":" + srv[1] + " (" + via + ")"));
                        if (host == "hypixel.net") Assert(srv != null && srv[0] == "mc.hypixel.net" && srv[1] == "25565", "SRV hypixel.net");
                    }
                });

                fails += Step("Серверы: опрос публичных серверов (Server List Ping)", () =>
                {
                    int online = 0;
                    foreach (var a in new[] { "mc.hypixel.net", "play.cubecraft.net", "hypixel.net", "2b2t.org", "127.0.0.1:1" })
                    {
                        var r = Servers.Ping(a);
                        if (J.B(r, "online"))
                        {
                            online++;
                            string motd = ((string)r["motdText"]).Replace("\n", " ⏎ ");
                            Console.WriteLine("  " + a + " [" + r["resolved"] + (r["srv"] != null ? ", SRV" : "") + "]: " + r["players"] + "/" + r["max"] + ", " + r["version"] + ", пинг " + r["ping"] + " мс, иконка " + (r["favicon"] != null ? "есть" : "нет"));
                            Console.WriteLine("    MOTD: " + (motd.Length > 110 ? motd.Substring(0, 110) + "…" : motd));
                        }
                        else Console.WriteLine("  " + a + ": недоступен — " + r["error"]);
                        if (a == "127.0.0.1:1") Assert(!J.B(r, "online"), "закрытый порт = недоступен");
                    }
                    Assert(online >= 2, "ответили хотя бы два публичных сервера");
                });

                Instance inst = null;
                fails += Step("Серверы: избранное и импорт из servers.dat (только чтение)", () =>
                {
                    inst = Instances.Upsert(new Instance { name = "Тест функций", mc = "1.21.1" });
                    var dat = Nbt.Write(J.D("servers", new List<object> {
                        J.D("name", "Мой сервер", "ip", "mc.hypixel.net", "acceptTextures", (byte)1),
                        J.D("name", "Локальный", "ip", "localhost:25566"),
                    }), false);
                    string datPath = Path.Combine(Instances.Dir(inst.id), "servers.dat");
                    File.WriteAllBytes(datPath, dat);
                    var read = Servers.ReadServersDat(datPath);
                    Console.WriteLine("  servers.dat: " + string.Join(", ", read.Select(s => s.name + " = " + s.address)));
                    Assert(read.Count == 2 && read[1].address == "localhost:25566", "чтение servers.dat");
                    Servers.Add("Hypixel", "mc.hypixel.net");
                    var imp = Servers.ImportFromInstance(inst.id);
                    Console.WriteLine("  импорт: добавлено " + imp["added"] + ", уже было " + imp["skipped"]);
                    Assert((int)imp["added"] == 1 && (int)imp["skipped"] == 1, "дубликаты пропускаются");
                    Assert(File.ReadAllBytes(datPath).SequenceEqual(dat), "servers.dat не изменён");
                    var l = Servers.List();
                    Servers.Move(l[1].id, -1);
                    Assert(Servers.List()[0].id == l[1].id, "перестановка");
                    Servers.Remove(l[0].id);
                    Assert(Servers.List().Count == 1, "удаление");
                });

                fails += Step("Серверы: быстрый вход (--quickPlayMultiplayer) в командной строке", () =>
                {
                    Func<bool, string, LaunchCommand> build = (withRule, server) =>
                    {
                        var game = new List<object> { "--username", "${auth_player_name}" };
                        if (withRule)
                            game.Add(J.D("rules", new object[] { J.D("action", "allow", "features", J.D("is_quick_play_multiplayer", true)) },
                                "value", new object[] { "--quickPlayMultiplayer", "${quickPlayMultiplayer}" }));
                        var v = new VersionInfo { Id = "test", BaseId = "test", Json = J.D("mainClass", "net.minecraft.client.main.Main", "type", "release", "arguments", J.D("game", game, "jvm", new List<object>())) };
                        var g = new PreparedGame { Version = v, NativesDir = "n", AssetsRoot = "a", GameAssets = "a", AssetIndexId = "17" };
                        return LaunchBuilder.Build(g, new Instance { id = "x", mc = "1.21.1" }, "C:\\game", new AuthSession { Name = "Test", Uuid = "0", AccessToken = "t" }, "javaw.exe", 21, server);
                    };
                    foreach (var withRule in new[] { true, false })
                    {
                        var c = build(withRule, "mc.hypixel.net:25565");
                        int i = c.Args.IndexOf("--quickPlayMultiplayer");
                        Console.WriteLine("  " + (withRule ? "правило в JSON версии" : "без правила (загрузчик)") + ": …" + string.Join(" ", c.Args.Skip(c.Args.IndexOf("net.minecraft.client.main.Main"))));
                        Assert(i > 0 && c.Args[i + 1] == "mc.hypixel.net:25565" && c.Args.Count(a => a == "--quickPlayMultiplayer") == 1, "аргумент ровно один раз");
                        Assert(!build(withRule, null).Args.Contains("--quickPlayMultiplayer"), "без сервера аргумента нет");
                    }
                });

                fails += Step("Миры: список (level.dat), копия в zip, проверка, восстановление копией, скриншоты", () =>
                {
                    string world = Path.Combine(Instances.Dir(inst.id), "saves", "Тест мир");
                    Directory.CreateDirectory(Path.Combine(world, "region"));
                    long lastPlayed = DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeMilliseconds();
                    File.WriteAllBytes(Path.Combine(world, "level.dat"), Nbt.Write(J.D("Data", J.D("LevelName", "Мой мир", "LastPlayed", lastPlayed, "GameType", 1,
                        "hardcore", (byte)0, "Version", J.D("Name", "1.21.1", "Id", 3955)), "DataVersion", 3955), true));
                    var rnd = new Random(42);
                    var region = new byte[300000]; rnd.NextBytes(region);
                    File.WriteAllBytes(Path.Combine(world, "region", "r.0.0.mca"), region);
                    File.WriteAllBytes(Path.Combine(world, "icon.png"), MakePng(64, 64, false));
                    File.WriteAllText(Path.Combine(world, "session.lock"), "☃");
                    var worlds = Worlds.List(null);
                    var w = J.O(worlds.First(o => J.S(J.O(o), "folder") == "Тест мир"));
                    Console.WriteLine("  мир: «" + J.S(w, "name") + "» (" + J.S(w, "mode") + ", " + J.S(w, "version") + "), " + J.L(w, "size") + " байт, играли " + J.S(w, "lastPlayed") + ", иконка " + (J.S(w, "icon") != null));
                    Assert(J.S(w, "name") == "Мой мир" && J.S(w, "mode") == "Творческий" && J.S(w, "version") == "1.21.1", "разбор level.dat");

                    var b = Worlds.Backup(inst.id, "Тест мир");
                    Console.WriteLine("  копия: " + b["file"] + ", файлов " + b["files"] + ", " + b["bytes"] + " байт → zip " + b["zipSize"] + " байт");
                    Assert((int)b["files"] == 3, "3 файла (без session.lock)");
                    using (var z = ZipFile.OpenRead((string)b["path"]))
                        Assert(z.Entries.All(e => e.FullName.StartsWith("Тест мир/")) && !z.Entries.Any(e => e.Name == "session.lock"), "структура архива");
                    var backups = Worlds.Backups(inst.id);
                    Assert(backups.Count == 1 && J.S(J.O(backups[0]), "world") == "Тест мир", "список копий");

                    var r1 = Worlds.Restore(inst.id, (string)b["file"], null);
                    var r2 = Worlds.Restore(inst.id, (string)b["file"], null);
                    Console.WriteLine("  восстановлено: «" + r1["folder"] + "», повторно: «" + r2["folder"] + "»");
                    Assert((string)r1["folder"] != (string)r2["folder"] && (string)r1["folder"] != "Тест мир", "никогда не перезаписывает");
                    string rest = (string)r1["path"];
                    Assert(File.ReadAllBytes(Path.Combine(rest, "region", "r.0.0.mca")).SequenceEqual(region), "регион совпадает байт в байт");
                    Assert(File.Exists(Path.Combine(world, "session.lock")), "исходный мир не тронут");
                    Assert(Worlds.List(inst.id).Count == 3, "в списке 3 мира");

                    // Архив с выходом за пределы папки — отклоняется.
                    string evil = Path.Combine(Paths.Backups, inst.id, "evil.zip");
                    using (var z = ZipFile.Open(evil, ZipArchiveMode.Create))
                    {
                        using (var s = new StreamWriter(z.CreateEntry("w/level.dat").Open())) s.Write("x");
                        using (var s = new StreamWriter(z.CreateEntry("w/../../../escape.txt").Open())) s.Write("x");
                    }
                    try { Worlds.Restore(inst.id, "evil.zip", null); throw new Exception("опасный архив распакован"); }
                    catch (Exception e) { if (e.Message.Contains("распакован")) throw; Console.WriteLine("  опасный архив отклонён: " + e.Message); }
                    Assert(!File.Exists(Path.Combine(Instances.Dir(inst.id), "escape.txt")), "ничего не вышло за папку");
                    Worlds.DeleteBackup(inst.id, "evil.zip");

                    // Скриншоты и миниатюры.
                    string shots = Path.Combine(Instances.Dir(inst.id), "screenshots");
                    using (var bmp = new Bitmap(1920, 1080))
                    {
                        using (var gr = System.Drawing.Graphics.FromImage(bmp)) gr.Clear(Color.SteelBlue);
                        bmp.Save(Path.Combine(shots, "2026-11-07_12.00.00.png"), ImageFormat.Png);
                    }
                    var list = Worlds.Screenshots(null, 100);
                    Assert(list.Count == 1 && J.S(J.O(list[0]), "thumb") == null, "скриншот найден, миниатюры ещё нет");
                    var th = Worlds.MakeThumbs(new List<object> { J.D("instance", inst.id, "file", "2026-11-07_12.00.00.png") });
                    string url = (string)th[inst.id + "/2026-11-07_12.00.00.png"];
                    string thumbFile = Path.Combine(Paths.Thumbs, url.Substring(url.LastIndexOf('/') + 1));
                    using (var img = Image.FromFile(thumbFile)) { Console.WriteLine("  миниатюра: " + url + " (" + img.Width + "×" + img.Height + ", " + new FileInfo(thumbFile).Length + " байт)"); Assert(img.Width == 360, "ширина миниатюры"); }
                    Assert(J.S(J.O(Worlds.Screenshots(inst.id, 10)[0]), "thumb") == url, "миниатюра из кэша");
                    Worlds.DeleteShot(inst.id, "2026-11-07_12.00.00.png");
                    Worlds.Delete(inst.id, (string)r2["folder"]);
                    Worlds.DeleteBackup(inst.id, (string)b["file"]);
                    Assert(Worlds.List(inst.id).Count == 2 && Worlds.Backups(inst.id).Count == 0 && Worlds.Screenshots(inst.id, 10).Count == 0, "удаление");
                });
            }
            finally
            {
                Paths.ConfigRoot = oldConfig; Paths.DataRoot = oldData;
                Worlds.UseRecycleBin = true;
                try { Directory.Delete(root, true); Console.WriteLine(); Console.WriteLine("Временная папка новых тестов удалена: " + root); }
                catch (Exception e) { Console.WriteLine("Не удалось удалить " + root + ": " + e.Message); }
            }
            return fails;
        }
    }
}
