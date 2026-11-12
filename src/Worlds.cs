// «Миры и скриншоты»: миры всех инсталляций (saves\*, имя и время из level.dat), резервные копии в zip
// (<dataRoot>\backups\<инсталляция>\<мир>-<дата>.zip, ZIP64 при необходимости — его пишет ZipArchive), восстановление
// только как копия (никогда не перезаписывает), удаление в Корзину; галерея скриншотов с миниатюрами (кэш в <config>\thumbs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AkeeLauncher
{
    static class Worlds
    {
        public static bool UseRecycleBin = true;   // самотест удаляет сразу
        public const string ThumbHost = "lumina-thumbs.local";

        static string SafeName(string n)
        {
            if (string.IsNullOrEmpty(n) || n == "." || n == ".." || n.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
                throw new Exception("Неверное имя: " + n);
            return n;
        }

        public static string WorldDir(string instId, string folder)
        {
            string d = Path.Combine(Instances.Dir(instId), "saves", SafeName(folder));
            if (!Directory.Exists(d)) throw new Exception("Мир не найден: " + folder);
            return d;
        }

        static List<Instance> Scope(string instId)
        {
            if (string.IsNullOrEmpty(instId)) return Instances.All();
            return new List<Instance> { Instances.Get(instId) };
        }

        static long DirSize(string d)
        {
            long n = 0;
            try { foreach (var f in new DirectoryInfo(d).EnumerateFiles("*", SearchOption.AllDirectories)) n += f.Length; } catch { }
            return n;
        }

        static void Trash(string path, bool dir)
        {
            if (!UseRecycleBin) { if (dir) Directory.Delete(path, true); else File.Delete(path); return; }
            if (dir) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }

        // ---------- миры ----------
        public static Dictionary<string, object> WorldInfo(Instance inst, string dir)
        {
            var di = new DirectoryInfo(dir);
            string name = di.Name, version = null;
            int mode = -1; bool hardcore = false;
            DateTime last = di.LastWriteTime;
            string level = Path.Combine(dir, "level.dat");
            try
            {
                var root = Nbt.ReadFile(level);
                var n = Nbt.Get(root, "Data", "LevelName") as string;
                if (!string.IsNullOrWhiteSpace(n)) name = n;
                object lp = Nbt.Get(root, "Data", "LastPlayed");
                if (lp is long && (long)lp > 0) last = DateTimeOffset.FromUnixTimeMilliseconds((long)lp).LocalDateTime;
                version = Nbt.Get(root, "Data", "Version", "Name") as string;
                object gt = Nbt.Get(root, "Data", "GameType");
                if (gt is int) mode = (int)gt;
                object hc = Nbt.Get(root, "Data", "hardcore");
                if (hc is int) hardcore = (int)hc != 0;
            }
            catch { if (File.Exists(level)) last = File.GetLastWriteTime(level); }
            string icon = null;
            string ip = Path.Combine(dir, "icon.png");
            try { if (File.Exists(ip) && new FileInfo(ip).Length < 512 * 1024) icon = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(ip)); } catch { }
            return J.D("instance", inst.id, "instanceName", inst.name, "folder", di.Name, "name", name,
                "size", DirSize(dir), "lastPlayed", last.ToString("o"), "version", version,
                "mode", hardcore ? "Хардкор" : mode == 0 ? "Выживание" : mode == 1 ? "Творческий" : mode == 2 ? "Приключение" : mode == 3 ? "Наблюдатель" : "",
                "icon", icon, "running", GameRunner.RunningInstance == inst.id);
        }

        public static List<object> List(string instId)
        {
            var r = new List<Dictionary<string, object>>();
            foreach (var inst in Scope(instId))
            {
                string saves = Path.Combine(Instances.Dir(inst.id), "saves");
                if (!Directory.Exists(saves)) continue;
                foreach (var d in Directory.GetDirectories(saves))
                    if (File.Exists(Path.Combine(d, "level.dat"))) r.Add(WorldInfo(inst, d));
            }
            r.Sort((a, b) => string.Compare(J.S(b, "lastPlayed"), J.S(a, "lastPlayed"), StringComparison.Ordinal));
            return r.Cast<object>().ToList();
        }

        public static void Delete(string instId, string folder)
        {
            if (GameRunner.RunningInstance == instId) throw new Exception("Сначала закройте игру");
            Trash(WorldDir(instId, folder), true);
        }

        // ---------- резервные копии ----------
        static string BackupRoot(string instId) { return Path.Combine(Paths.Backups, SafeName(instId)); }

        public static Dictionary<string, object> Backup(string instId, string folder)
        {
            string src = WorldDir(instId, folder);
            string root = BackupRoot(instId);
            Directory.CreateDirectory(root);
            string baseName = folder + "-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string zip = Path.Combine(root, baseName + ".zip");
            for (int n = 2; File.Exists(zip); n++) zip = Path.Combine(root, baseName + "-" + n + ".zip");
            string tmp = zip + ".tmp";
            int files = 0; long bytes = 0;
            var skipped = new List<object>();
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite))
                using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    string prefix = Path.GetFullPath(src).TrimEnd('\\') + "\\";
                    foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                    {
                        string rel = Path.GetFullPath(f).Substring(prefix.Length).Replace('\\', '/');
                        if (rel == "session.lock") continue;   // замок запущенной игры, не нужен
                        try
                        {
                            using (var input = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                            {
                                var e = z.CreateEntry(folder + "/" + rel, CompressionLevel.Fastest);
                                e.LastWriteTime = File.GetLastWriteTime(f);
                                using (var o = e.Open()) input.CopyTo(o, 1 << 16);
                                bytes += input.Length;
                            }
                            files++;
                        }
                        catch (IOException ex) { skipped.Add(rel + ": " + ex.Message); }
                    }
                }
                Verify(tmp, files, bytes);
                File.Move(tmp, zip);
            }
            catch { try { File.Delete(tmp); } catch { } throw; }
            return J.D("file", Path.GetFileName(zip), "path", zip, "files", files, "bytes", bytes, "zipSize", new FileInfo(zip).Length,
                "skipped", skipped, "warning", GameRunner.RunningInstance == instId ? "Игра запущена — копия может быть несогласованной; лучше делать её из меню паузы после сохранения или после выхода." : null);
        }

        // Проверка архива: число файлов и суммарный размер совпадают; level.dat читается целиком.
        static void Verify(string zip, int files, long bytes)
        {
            using (var z = ZipFile.OpenRead(zip))
            {
                int n = 0; long sum = 0;
                foreach (var e in z.Entries)
                {
                    if (e.FullName.EndsWith("/")) continue;
                    n++; sum += e.Length;
                    if (e.Name == "level.dat" && e.FullName.Count(c => c == '/') == 1)
                        using (var s = e.Open()) { var buf = new byte[65536]; long got = 0; int r; while ((r = s.Read(buf, 0, buf.Length)) > 0) got += r; if (got != e.Length) throw new Exception("level.dat в архиве повреждён"); }
                }
                if (n != files || sum != bytes) throw new Exception("Архив не прошёл проверку: файлов " + n + "/" + files + ", байт " + sum + "/" + bytes);
            }
        }

        public static List<object> Backups(string instId)
        {
            var r = new List<Dictionary<string, object>>();
            if (!Directory.Exists(Paths.Backups)) return new List<object>();
            var names = new Dictionary<string, string>();
            foreach (var i in Instances.All()) names[i.id] = i.name;
            foreach (var d in Directory.GetDirectories(Paths.Backups))
            {
                string inst = Path.GetFileName(d);
                if (!string.IsNullOrEmpty(instId) && inst != instId) continue;
                foreach (var f in new DirectoryInfo(d).GetFiles("*.zip"))
                {
                    string world = f.Name.Substring(0, f.Name.Length - 4);
                    var m = System.Text.RegularExpressions.Regex.Match(world, @"^(.*)-\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}(-\d+)?$");
                    if (m.Success) world = m.Groups[1].Value;
                    string iname;
                    r.Add(J.D("instance", inst, "instanceName", names.TryGetValue(inst, out iname) ? iname : inst + " (удалена)",
                        "instanceExists", names.ContainsKey(inst), "file", f.Name, "world", world, "size", f.Length, "created", f.LastWriteTime.ToString("o")));
                }
            }
            r.Sort((a, b) => string.Compare(J.S(b, "created"), J.S(a, "created"), StringComparison.Ordinal));
            return r.Cast<object>().ToList();
        }

        static string BackupFile(string instId, string file)
        {
            if (!SafeName(file).EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new Exception("Неверный файл копии");
            string p = Path.Combine(BackupRoot(instId), file);
            if (!File.Exists(p)) throw new Exception("Копия не найдена: " + file);
            return p;
        }

        // Восстановление — всегда в новую папку «<мир> (копия …)», существующие миры не трогаются.
        public static Dictionary<string, object> Restore(string instId, string file, string targetInst)
        {
            string zip = BackupFile(instId, file);
            if (string.IsNullOrEmpty(targetInst)) targetInst = instId;
            Instances.Get(targetInst);   // проверка, что инсталляция есть
            string saves = Path.Combine(Instances.Dir(targetInst), "saves");
            Directory.CreateDirectory(saves);
            using (var z = ZipFile.OpenRead(zip))
            {
                // Верхняя папка архива (имя мира).
                string top = null;
                foreach (var e in z.Entries)
                {
                    int slash = e.FullName.IndexOf('/');
                    string t = slash > 0 ? e.FullName.Substring(0, slash) : "";
                    if (top == null) top = t; else if (top != t) { top = ""; break; }
                }
                if (top == null) throw new Exception("Архив пуст");
                string baseName = (top.Length > 0 ? top : Path.GetFileNameWithoutExtension(file)) + " (копия " + DateTime.Now.ToString("yyyy-MM-dd HH-mm") + ")";
                string dest = Path.Combine(saves, baseName);
                for (int n = 2; Directory.Exists(dest) || File.Exists(dest); n++) dest = Path.Combine(saves, baseName + " " + n);
                string destFull = Path.GetFullPath(dest).TrimEnd('\\') + "\\";
                string tmpDir = dest + ".restoring";
                if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
                string tmpFull = Path.GetFullPath(tmpDir).TrimEnd('\\') + "\\";
                bool hasLevel = false;
                try
                {
                    foreach (var e in z.Entries)
                    {
                        string rel = top.Length > 0 ? e.FullName.Substring(top.Length + 1) : e.FullName;
                        if (rel.Length == 0) continue;
                        string outPath = Path.GetFullPath(Path.Combine(tmpDir, rel.Replace('/', '\\')));
                        if (!outPath.StartsWith(tmpFull, StringComparison.OrdinalIgnoreCase)) throw new Exception("Архив содержит недопустимый путь: " + e.FullName);
                        if (e.FullName.EndsWith("/")) { Directory.CreateDirectory(outPath); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                        e.ExtractToFile(outPath, false);
                        if (rel == "level.dat") hasLevel = true;
                    }
                    if (!hasLevel) throw new Exception("В архиве нет level.dat — это не копия мира");
                    Directory.Move(tmpDir, dest);
                }
                catch { try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { } throw; }
                return J.D("instance", targetInst, "folder", Path.GetFileName(dest.TrimEnd('\\')), "path", destFull.TrimEnd('\\'));
            }
        }

        public static void DeleteBackup(string instId, string file) { Trash(BackupFile(instId, file), false); }

        public static string BackupFolder(string instId)
        {
            string d = string.IsNullOrEmpty(instId) ? Paths.Backups : BackupRoot(instId);
            Directory.CreateDirectory(d);
            return d;
        }

        // ---------- скриншоты ----------
        static string ShotPath(string instId, string file)
        {
            string p = Path.Combine(Instances.Dir(instId), "screenshots", SafeName(file));
            string ext = Path.GetExtension(p).ToLowerInvariant();
            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg") throw new Exception("Это не скриншот");
            if (!File.Exists(p)) throw new Exception("Скриншот не найден: " + file);
            return p;
        }

        static string ThumbName(FileInfo f)
        {
            using (var sha = SHA1.Create())
                return Http.Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(f.FullName.ToLowerInvariant() + "|" + f.Length + "|" + f.LastWriteTimeUtc.Ticks))).Substring(0, 20) + ".jpg";
        }

        static string ThumbUrl(string name) { return "https://" + ThumbHost + "/" + name; }

        public static List<object> Screenshots(string instId, int limit)
        {
            var all = new List<Tuple<Instance, FileInfo>>();
            foreach (var inst in Scope(instId))
            {
                string d = Path.Combine(Instances.Dir(inst.id), "screenshots");
                if (!Directory.Exists(d)) continue;
                foreach (var f in new DirectoryInfo(d).GetFiles())
                {
                    string ext = f.Extension.ToLowerInvariant();
                    if (ext == ".png" || ext == ".jpg" || ext == ".jpeg") all.Add(Tuple.Create(inst, f));
                }
            }
            all.Sort((a, b) => b.Item2.LastWriteTimeUtc.CompareTo(a.Item2.LastWriteTimeUtc));
            var r = new List<object>();
            foreach (var t in all.Take(limit > 0 ? limit : 300))
            {
                string tn = ThumbName(t.Item2);
                r.Add(J.D("instance", t.Item1.id, "instanceName", t.Item1.name, "file", t.Item2.Name, "size", t.Item2.Length,
                    "date", t.Item2.LastWriteTime.ToString("o"), "thumb", File.Exists(Path.Combine(Paths.Thumbs, tn)) ? ThumbUrl(tn) : null));
            }
            return r;
        }

        // Миниатюры по запросу (пачками): [{instance, file}] → {"instance/file": url}
        public static Dictionary<string, object> MakeThumbs(List<object> items)
        {
            Directory.CreateDirectory(Paths.Thumbs);
            var r = new Dictionary<string, object>();
            foreach (var o in items)
            {
                var d = J.O(o);
                string inst = J.S(d, "instance"), file = J.S(d, "file");
                try
                {
                    var fi = new FileInfo(ShotPath(inst, file));
                    string tn = ThumbName(fi), tp = Path.Combine(Paths.Thumbs, tn);
                    if (!File.Exists(tp)) MakeThumb(fi.FullName, tp, 360);
                    r[inst + "/" + file] = ThumbUrl(tn);
                }
                catch { r[inst + "/" + file] = null; }
            }
            return r;
        }

        public static void MakeThumb(string src, string dst, int width)
        {
            using (var fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var img = Image.FromStream(fs, false, false))
            {
                int w = Math.Min(width, img.Width), h = Math.Max(1, (int)((long)img.Height * w / img.Width));
                using (var bmp = new Bitmap(w, h))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(img, 0, 0, w, h);
                    }
                    var enc = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                    using (var ep = new EncoderParameters(1))
                    {
                        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);
                        string tmp = dst + ".tmp";
                        bmp.Save(tmp, enc, ep);
                        if (File.Exists(dst)) File.Delete(dst);
                        File.Move(tmp, dst);
                    }
                }
            }
        }

        public static void OpenShot(string instId, string file) { Process.Start(new ProcessStartInfo(ShotPath(instId, file)) { UseShellExecute = true }); }

        public static Bitmap LoadShot(string instId, string file)
        {
            using (var fs = new FileStream(ShotPath(instId, file), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var img = Image.FromStream(fs))
                return new Bitmap(img);
        }

        public static void DeleteShot(string instId, string file) { Trash(ShotPath(instId, file), false); }

        public static string OpenTarget(string what, string instId, string folder)
        {
            switch (what)
            {
                case "world": return WorldDir(instId, folder);
                case "backups": return BackupFolder(instId);
                case "screenshots":
                    { string d = Path.Combine(Instances.Dir(instId), "screenshots"); Directory.CreateDirectory(d); return d; }
                default: throw new Exception("Неизвестная папка");
            }
        }
    }
}
