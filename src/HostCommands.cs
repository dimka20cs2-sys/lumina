// Команды интерфейса (cmd из app.js). Выполняются в фоновом потоке; диалоги — через MainWindow.OnUi.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace AkeeLauncher
{
    static partial class HostCommands
    {
        public const string AppVersion = "1.0.0";

        public static object Run(MainWindow w, string cmd, Dictionary<string, object> m)
        {
            switch (cmd)
            {
                case "init": return Init();
                case "getVersions": return Versions.List();
                case "getLoaderVersions": return Loaders.List(Req(m, "loader"), Req(m, "mc"));
                case "getLoaderSupport": return Loaders.Support(Req(m, "mc"));
                case "getFabricLoaders": return Versions.FabricLoaders(Req(m, "mc"));
                case "saveSettings":
                    var s = J.Convert<Settings>(J.O(m, "settings"));
                    s.lastInstance = Config.Current.lastInstance;
                    s.selectedAccount = Config.Current.selectedAccount;
                    Config.Current = s;
                    Config.Save();
                    GameMode.OnSettingsSaved();
                    return Init();
                case "saveInstance": return Instances.Upsert(J.Convert<Instance>(J.O(m, "instance")));
                case "deleteInstance":
                    if (GameRunner.RunningInstance == Req(m, "id")) throw new Exception("Сначала закройте игру");
                    Instances.Delete(Req(m, "id"));
                    return Instances.All();
                case "selectInstance": Config.Current.lastInstance = Req(m, "id"); Config.Save(); return null;
                case "installOptimization": return Modrinth.Install(Instances.Get(Req(m, "id")));
                case "modsStatus": return Modrinth.Status(Req(m, "id"));
                case "toggleMod": return ToggleMod(Req(m, "id"), Req(m, "file"));
                // «Моды» (ModNest): каталог Modrinth для инсталляции
                case "mnCategories": return ModNest.Categories(Req(m, "kind"));
                case "mnSearch": return ModNest.Search(J.S(m, "id"), Req(m, "kind"), J.S(m, "query"), J.S(m, "index"), J.S(m, "category"), (int)J.L(m, "offset"), (int)J.L(m, "limit"));
                case "mnInstall": return ModNest.Install(J.S(m, "id"), Req(m, "project"), Req(m, "kind"), J.S(m, "world"));
                case "mnInstalled": return ModNest.Installed(Req(m, "id"));
                case "mnToggle": return ModNest.Toggle(Req(m, "id"), Req(m, "path"));
                case "mnRemove": return ModNest.Remove(Req(m, "id"), Req(m, "path"));
                case "mnWorlds": return ModNest.Worlds(Req(m, "id"));
                case "mnCheckUpdates": return ModNest.CheckUpdates(Req(m, "id"));
                case "mnUpdateAll": return ModNest.UpdateAll(Req(m, "id"));
                case "mnShaderLoader": return ModNest.InstallShaderLoader(Req(m, "id"));
                case "mnImport":
                    {
                        var files = new List<string>();
                        foreach (var f in J.A(m, "files")) if (f != null) files.Add(f.ToString());
                        if (files.Count == 0) throw new Exception("Нет файлов для импорта");
                        return ModNest.ImportFiles(J.S(m, "id"), files, J.S(m, "world"));
                    }
                case "mnPickFiles":
                    return w.OnUi(() =>
                    {
                        using (var d = new OpenFileDialog { Title = "Импорт модов, паков и сборок", Multiselect = true,
                            Filter = "Моды, паки и сборки (*.jar;*.zip;*.mrpack)|*.jar;*.zip;*.mrpack" })
                            return d.ShowDialog(w) == DialogResult.OK ? d.FileNames : new string[0];
                    });
                case "setInstanceIcon": return Instances.SetPresetIcon(Req(m, "id"), J.S(m, "icon"));
                case "pickInstanceIcon":
                    {
                        string file = w.OnUi(() =>
                        {
                            using (var d = new OpenFileDialog { Title = "Своя иконка сборки", Filter = "Картинки (*.png;*.jpg;*.jpeg;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp" })
                                return d.ShowDialog(w) == DialogResult.OK ? d.FileName : "";
                        });
                        if (file.Length == 0) return null;
                        return Instances.SetCustomIcon(Req(m, "id"), file);
                    }
                case "play": return Launcher.Play(Req(m, "id"), w.CloseLauncher, J.S(m, "server"));
                case "cancel": Launcher.Cancel(); return null;
                case "killGame": GameRunner.Kill(); return null;
                case "loginStart": return MicrosoftAuth.StartDeviceLogin();
                case "loginCancel": MicrosoftAuth.CancelLogin(); return null;
                case "logout":
                    AccountStore.Remove(Req(m, "uuid"));
                    if (Config.Current.selectedAccount == Req(m, "uuid")) { Config.Current.selectedAccount = ""; Config.Save(); }
                    return AccountStore.Public();
                case "selectAccount": Config.Current.selectedAccount = Req(m, "uuid"); Config.Save(); return AccountStore.Public();
                case "openFolder": return OpenFolder(J.S(m, "what"), J.S(m, "id"));
                case "openUrl": OpenUrl(J.S(m, "url")); return null;
                case "copyText": string t = J.S(m, "text") ?? ""; w.OnUi(() => { Clipboard.SetText(t.Length > 0 ? t : " "); return 0; }); return null;
                case "getGuide": return ReadGuide();
                case "pickFolder":
                    return w.OnUi(() =>
                    {
                        using (var d = new FolderBrowserDialog { Description = "Папка для файлов игры Lumina", ShowNewFolderButton = true })
                            return d.ShowDialog(w) == DialogResult.OK ? d.SelectedPath : null;
                    });
                case "pickJava":
                    return w.OnUi(() =>
                    {
                        using (var d = new OpenFileDialog { Title = "Выберите javaw.exe", Filter = "Java (javaw.exe;java.exe)|javaw.exe;java.exe" })
                            return d.ShowDialog(w) == DialogResult.OK ? d.FileName : null;
                    });
                default:
                    object r;
                    if (Extra(w, cmd, m, out r)) return r;
                    throw new Exception("неизвестная команда " + cmd);
            }
        }

        static string Req(Dictionary<string, object> m, string k)
        {
            string v = J.S(m, k);
            if (string.IsNullOrEmpty(v)) throw new Exception("не передан параметр " + k);
            return v;
        }

        static object Init()
        {
            var acc = AccountStore.Selected();
            if (acc != null && Config.Current.selectedAccount != acc.uuid) { Config.Current.selectedAccount = acc.uuid; Config.Save(); }
            return J.D(
                "appVersion", AppVersion,
                "settings", Config.Current,
                "instances", Instances.All(),
                "accounts", AccountStore.Public(),
                "azureConfigured", Config.AzureConfigured,
                "system", J.D("totalRamMb", LaunchBuilder.TotalRamMb(), "autoRamMb", LaunchBuilder.AutoRamMb()),
                "paths", J.D("data", Paths.DataRoot, "config", Paths.ConfigRoot),
                "running", GameRunner.RunningInstance,
                "busy", Launcher.Busy,
                "optimizationPack", Modrinth.Pack,
                "optimizationPacks", Modrinth.Packs(),
                "loaders", Loaders.All);
        }

        static object ToggleMod(string id, string file)
        {
            string mods = Path.Combine(Instances.Dir(id), "mods");
            string name = Path.GetFileName(file);
            string src = Path.Combine(mods, name);
            if (!File.Exists(src)) throw new Exception("Файл не найден: " + name);
            if (GameRunner.RunningInstance == id) throw new Exception("Сначала закройте игру");
            string dst = name.EndsWith(".disabled") ? src.Substring(0, src.Length - 9) : src + ".disabled";
            File.Move(src, dst);
            return Modrinth.Status(id);
        }

        static string OpenFolder(string what, string id)
        {
            string dir;
            switch (what)
            {
                case "instance": dir = Instances.Dir(id); break;
                case "mods": case "saves": case "resourcepacks": case "screenshots": case "logs": case "shaderpacks":
                    dir = Path.Combine(Instances.Dir(id), what); break;
                case "config": dir = Paths.ConfigRoot; break;
                default: dir = Paths.DataRoot; break;
            }
            Directory.CreateDirectory(dir);
            Process.Start("explorer.exe", "\"" + dir + "\"");
            return dir;
        }

        static readonly string[] AllowedHosts = {
            "microsoft.com", "live.com", "azure.com", "aka.ms", "minecraft.net", "xbox.com", "modrinth.com", "fabricmc.net", "quiltmc.org", "minecraftforge.net", "neoforged.net", "cisoft.vercel.app",
        };

        public static void OpenUrl(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out u) || u.Scheme != "https") return;
            foreach (var h in AllowedHosts)
                if (u.Host == h || u.Host.EndsWith("." + h)) { Process.Start(u.AbsoluteUri); return; }
        }

        static string ReadGuide()
        {
            string f = Path.Combine(MainWindow.AppDir, "AZURE-SETUP.md");
            return File.Exists(f) ? File.ReadAllText(f, Encoding.UTF8) : "Файл AZURE-SETUP.md не найден рядом с программой.";
        }
    }
}
