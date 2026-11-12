// Инсталляции: своя папка игры (saves, mods, resourcepacks, options.txt) + общие библиотеки/ассеты/версии.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AkeeLauncher
{
    public class Instance
    {
        public string id = "";
        public string name = "";
        public string mc = "";
        public string loader = "vanilla";      // vanilla | fabric | quilt | forge | neoforge
        public string loaderVersion = "";      // пусто = последняя стабильная
        public int ramMb = 0;                   // 0 = из общих настроек
        public string jvmArgs = "";
        public string created = "";
        public string lastPlayed = "";
        public bool optimized = false;          // ставился пакет «Оптимизация»
        public string icon = "";                // ключ встроенной иконки (ui/img/instance-icons/<key>.svg) или "custom"
        public string iconData = null;          // только для интерфейса: data:-URL своей иконки (icon.png), в файл не пишется
    }

    static class Instances
    {
        public static string Dir(string id) { return Path.Combine(Paths.Instances, Safe(id)); }
        static string File_(string id) { return Path.Combine(Dir(id), "instance.json"); }

        static string Safe(string id)
        {
            if (string.IsNullOrEmpty(id) || !Regex.IsMatch(id, "^[A-Za-z0-9._-]+$") || id.Contains(".."))
                throw new Exception("Неверный id инсталляции");
            return id;
        }

        public static List<Instance> All()
        {
            var r = new List<Instance>();
            if (!Directory.Exists(Paths.Instances)) return r;
            foreach (var d in Directory.GetDirectories(Paths.Instances))
            {
                string f = Path.Combine(d, "instance.json");
                if (!File.Exists(f)) continue;
                try
                {
                    var i = J.Ser.Deserialize<Instance>(File.ReadAllText(f, Encoding.UTF8));
                    i.id = Path.GetFileName(d);
                    FillIcon(i);
                    r.Add(i);
                }
                catch { }
            }
            r.Sort((a, b) => string.Compare(b.lastPlayed ?? "", a.lastPlayed ?? "", StringComparison.Ordinal));
            return r;
        }

        public static Instance Get(string id)
        {
            string f = File_(id);
            if (!File.Exists(f)) throw new Exception("Инсталляция не найдена: " + id);
            var i = J.Ser.Deserialize<Instance>(File.ReadAllText(f, Encoding.UTF8));
            i.id = id;
            return i;
        }

        public static void Save(Instance i)
        {
            Directory.CreateDirectory(Dir(i.id));
            string data = i.iconData;
            i.iconData = null;   // картинка живёт в icon.png, в instance.json её не дублируем
            try { File.WriteAllText(File_(i.id), J.Write(i), Encoding.UTF8); }
            finally { i.iconData = data; }
        }

        // ---------- иконки ----------
        static string IconFile(string id) { return Path.Combine(Dir(id), "icon.png"); }

        static void FillIcon(Instance i)
        {
            i.iconData = null;
            if (i.icon != "custom") return;
            string f = IconFile(i.id);
            if (!File.Exists(f)) { i.icon = ""; return; }
            try { i.iconData = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(f)); }
            catch { i.icon = ""; }
        }

        // Своя картинка: вписать в квадрат 128×128 (мелкий пиксель-арт — без размытия) и сохранить icon.png.
        public static Instance SetCustomIcon(string id, string file)
        {
            var inst = Get(id);
            using (var src = System.Drawing.Image.FromFile(file))
            using (var bmp = new System.Drawing.Bitmap(128, 128, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                bool pixel = src.Width <= 64 && src.Height <= 64;
                g.InterpolationMode = pixel ? System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor : System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.Clear(System.Drawing.Color.Transparent);
                float k = Math.Min(128f / src.Width, 128f / src.Height);
                float w = src.Width * k, h = src.Height * k;
                g.DrawImage(src, (128 - w) / 2, (128 - h) / 2, w, h);
                bmp.Save(IconFile(id), System.Drawing.Imaging.ImageFormat.Png);
            }
            inst.icon = "custom";
            Save(inst);
            FillIcon(inst);
            return inst;
        }

        public static Instance SetPresetIcon(string id, string key)
        {
            if (!Regex.IsMatch(key ?? "", "^[a-z0-9-]{0,32}$")) throw new Exception("Неизвестная иконка");
            var inst = Get(id);
            inst.icon = key;
            Save(inst);
            try { if (File.Exists(IconFile(id))) File.Delete(IconFile(id)); } catch { }
            FillIcon(inst);
            return inst;
        }

        // Создать или изменить. Пустой id = новая инсталляция.
        public static Instance Upsert(Instance src)
        {
            if (string.IsNullOrWhiteSpace(src.mc)) throw new Exception("Не выбрана версия Minecraft");
            if (!Loaders.IsKnown(src.loader) || src.loader == "vanilla") { src.loader = "vanilla"; src.loaderVersion = ""; }
            src.loaderVersion = (src.loaderVersion ?? "").Trim();
            src.name = (src.name ?? "").Trim();
            if (src.name.Length == 0) src.name = src.mc + (src.loader != "vanilla" ? " " + Loaders.Title(src.loader) : "");
            if (src.name.Length > 60) src.name = src.name.Substring(0, 60);
            if (src.ramMb < 0) src.ramMb = 0;
            if (src.jvmArgs == null) src.jvmArgs = "";
            src.icon = (src.icon ?? "").Trim();
            if (src.icon != "custom" && !Regex.IsMatch(src.icon, "^[a-z0-9-]{0,32}$")) src.icon = "";

            if (string.IsNullOrEmpty(src.id))
            {
                src.id = NewId(src.name);
                src.created = DateTime.UtcNow.ToString("o");
            }
            else
            {
                var old = Get(src.id);
                src.created = old.created;
                src.lastPlayed = old.lastPlayed;
                bool same = old.mc == src.mc && old.loader == src.loader;
                src.optimized = old.optimized && same;
                // Сменилась версия игры или загрузчик — моды пакета от старой сборки убираем (чужие моды не трогаем).
                if (!same) Modrinth.RemovePackFiles(src.id);
            }
            if (src.icon == "custom" && !File.Exists(IconFile(src.id))) src.icon = "";
            Save(src);
            foreach (var sub in new[] { "saves", "mods", "resourcepacks", "screenshots", "logs" })
                Directory.CreateDirectory(Path.Combine(Dir(src.id), sub));
            FillIcon(src);
            return src;
        }

        static string NewId(string name)
        {
            string b = Regex.Replace(Translit(name.ToLowerInvariant()), "[^a-z0-9._-]+", "-").Trim('-', '.');
            if (b.Length == 0) b = "instance";
            if (b.Length > 32) b = b.Substring(0, 32);
            string id = b;
            for (int n = 2; Directory.Exists(Path.Combine(Paths.Instances, id)); n++) id = b + "-" + n;
            return id;
        }

        static string Translit(string s)
        {
            const string ru = "абвгдеёжзийклмнопрстуфхцчшщъыьэюя";
            string[] en = { "a", "b", "v", "g", "d", "e", "e", "zh", "z", "i", "y", "k", "l", "m", "n", "o", "p", "r", "s", "t", "u", "f", "h", "c", "ch", "sh", "sch", "", "y", "", "e", "yu", "ya" };
            var sb = new StringBuilder();
            foreach (char c in s) { int i = ru.IndexOf(c); sb.Append(i >= 0 ? en[i] : c.ToString()); }
            return sb.ToString();
        }

        // Удаление — в корзину Windows (миры не теряются безвозвратно).
        public static void Delete(string id)
        {
            string d = Dir(id);
            if (!Directory.Exists(d)) return;
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(d,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }

        public static void Touch(string id)
        {
            var i = Get(id);
            i.lastPlayed = DateTime.UtcNow.ToString("o");
            Save(i);
            Config.Current.lastInstance = id;
            Config.Save();
        }
    }
}
