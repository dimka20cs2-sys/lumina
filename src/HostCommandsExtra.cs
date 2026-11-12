// Команды интерфейса для экранов «Скины», «Серверы», «Миры», автонастройки графики и игрового режима.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace AkeeLauncher
{
    static partial class HostCommands
    {
        static List<string> Files(Dictionary<string, object> m)
        {
            var files = new List<string>();
            foreach (var f in J.A(m, "files")) if (f != null) files.Add(f.ToString());
            return files;
        }

        static bool Extra(MainWindow w, string cmd, Dictionary<string, object> m, out object result)
        {
            result = null;
            switch (cmd)
            {
                // ---------- скины ----------
                case "skinsList": result = Skins.List(); return true;
                case "skinsImport":
                    {
                        var files = Files(m);
                        if (files.Count == 0) throw new Exception("Нет файлов для импорта");
                        result = Skins.ImportFiles(files); return true;
                    }
                case "skinsPick":
                    {
                        var picked = w.OnUi(() =>
                        {
                            using (var d = new OpenFileDialog { Title = "Добавить скин (PNG 64×64 или 64×32)", Multiselect = true, Filter = "Скины Minecraft (*.png)|*.png" })
                                return d.ShowDialog(w) == DialogResult.OK ? d.FileNames : new string[0];
                        });
                        result = picked.Length == 0 ? null : (object)Skins.ImportFiles(new List<string>(picked));
                        return true;
                    }
                case "skinsUpdate": result = Skins.Update(Req(m, "skin"), J.S(m, "name"), J.S(m, "variant")); return true;
                case "skinsDelete": result = Skins.Delete(Req(m, "skin")); return true;
                case "skinsDuplicate": result = Skins.Duplicate(Req(m, "skin")); return true;
                case "skinsAccount": result = Skins.AccountProfile(); return true;
                case "skinsApply": result = Skins.Apply(Req(m, "skin")); return true;
                case "skinsReset": result = Skins.Reset(); return true;
                case "skinsCape": result = Skins.SetCape(J.S(m, "cape")); return true;
                case "skinsImportAccount": result = Skins.ImportFromAccount(); return true;

                // ---------- графика ----------
                case "gfxStatus": result = GraphicsTuner.Status(Req(m, "id")); return true;
                case "gfxApply": result = GraphicsTuner.Apply(Req(m, "id"), Req(m, "profile")); return true;
                case "gfxRestore": result = GraphicsTuner.Restore(Req(m, "id")); return true;
                case "hardware": result = GraphicsTuner.HardwareDict(GraphicsTuner.Detect(J.B(m, "fresh"))); return true;

                // ---------- серверы ----------
                case "srvList": result = Servers.List(); return true;
                case "srvAdd": result = Servers.Add(J.S(m, "name"), Req(m, "address")); return true;
                case "srvUpdate": result = Servers.Update(Req(m, "sid"), J.S(m, "name"), J.S(m, "address")); return true;
                case "srvRemove": result = Servers.Remove(Req(m, "sid")); return true;
                case "srvMove": result = Servers.Move(Req(m, "sid"), (int)J.L(m, "delta")); return true;
                case "srvPing": result = Servers.Ping(Req(m, "address")); return true;
                case "srvImport": result = Servers.ImportFromInstance(Req(m, "id")); return true;

                // ---------- миры, копии, скриншоты ----------
                case "worldsList": result = Worlds.List(J.S(m, "id")); return true;
                case "worldBackup": result = Worlds.Backup(Req(m, "id"), Req(m, "folder")); return true;
                case "worldDelete": Worlds.Delete(Req(m, "id"), Req(m, "folder")); return true;
                case "backupsList": result = Worlds.Backups(J.S(m, "id")); return true;
                case "backupRestore": result = Worlds.Restore(Req(m, "id"), Req(m, "file"), J.S(m, "target")); return true;
                case "backupDelete": Worlds.DeleteBackup(Req(m, "id"), Req(m, "file")); return true;
                case "worldsOpen":
                    {
                        string dir = Worlds.OpenTarget(Req(m, "what"), J.S(m, "id"), J.S(m, "folder"));
                        Process.Start("explorer.exe", "\"" + dir + "\"");
                        result = dir; return true;
                    }
                case "shotsList": result = Worlds.Screenshots(J.S(m, "id"), (int)J.L(m, "limit")); return true;
                case "shotsThumbs": result = Worlds.MakeThumbs(J.A(m, "items")); return true;
                case "shotOpen": Worlds.OpenShot(Req(m, "id"), Req(m, "file")); return true;
                case "shotCopy":
                    {
                        var bmp = Worlds.LoadShot(Req(m, "id"), Req(m, "file"));
                        w.OnUi(() => { using (bmp) Clipboard.SetImage(bmp); return 0; });
                        return true;
                    }
                case "shotDelete": Worlds.DeleteShot(Req(m, "id"), Req(m, "file")); return true;

                // ---------- игровой режим ----------
                case "gameModeInfo":
                    {
                        string javaw = null;
                        string rt = Paths.Runtimes;
                        if (Directory.Exists(rt))
                            foreach (var d in Directory.GetDirectories(rt))
                            {
                                string j = Path.Combine(d, "bin", "javaw.exe");
                                if (File.Exists(j)) { javaw = j; break; }
                            }
                        result = GameMode.DryRun(javaw); return true;
                    }
            }
            return false;
        }
    }
}
