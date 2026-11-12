// «Скины»: локальная библиотека (<dataRoot>\skins: library.json + <id>.png) и смена скина/плаща аккаунта
// через Minecraft Services API (POST/DELETE /minecraft/profile/skins, PUT/DELETE /minecraft/profile/capes/active).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;

namespace AkeeLauncher
{
    static class Skins
    {
        const string Api = "https://api.minecraftservices.com/minecraft/profile";
        const int MaxPng = 256 * 1024;
        static readonly object gate = new object();

        static string LibFile { get { return Path.Combine(Paths.Skins, "library.json"); } }

        // ---------- проверка PNG ----------
        // Скин Minecraft: PNG 64×64 (современный) или 64×32 (старый формат). Возвращает размеры или бросает понятную ошибку.
        public static void ValidatePng(byte[] data, out int w, out int h)
        {
            w = h = 0;
            if (data == null || data.Length < 33) throw new Exception("Файл пустой или повреждён");
            if (data.Length > MaxPng) throw new Exception("Файл слишком большой для скина (больше 256 КБ)");
            byte[] sig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            for (int i = 0; i < 8; i++) if (data[i] != sig[i]) throw new Exception("Это не PNG-файл");
            if (Encoding.ASCII.GetString(data, 12, 4) != "IHDR") throw new Exception("PNG повреждён (нет IHDR)");
            w = (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19];
            h = (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23];
            if (w != 64 || (h != 64 && h != 32))
                throw new Exception("Скин должен быть 64×64 или 64×32 пикселя, а этот " + w + "×" + h);
            try { using (var ms = new MemoryStream(data)) using (var img = Image.FromStream(ms, false, true)) { } }
            catch { throw new Exception("PNG не читается (файл повреждён)"); }
        }

        // Тонкие руки (Alex): в 64×64 столбцы 54–55 в строках 20–31 (зад правой руки) прозрачные.
        public static string DetectVariant(byte[] png)
        {
            try
            {
                using (var ms = new MemoryStream(png))
                using (var bmp = new Bitmap(ms))
                {
                    if (bmp.Height != 64) return "classic";
                    for (int y = 20; y < 32; y++)
                        if (bmp.GetPixel(54, y).A != 0 || bmp.GetPixel(55, y).A != 0) return "classic";
                    return "slim";
                }
            }
            catch { return "classic"; }
        }

        static string NormVariant(string v)
        {
            v = (v ?? "").Trim().ToLowerInvariant();
            return v == "slim" || v == "alex" ? "slim" : "classic";
        }

        // ---------- библиотека ----------
        public class Item { public string id = "", name = "", variant = "classic", added = ""; public int width = 64, height = 64; }

        static List<Item> Load()
        {
            try
            {
                if (File.Exists(LibFile))
                {
                    var l = J.Ser.Deserialize<List<Item>>(File.ReadAllText(LibFile, Encoding.UTF8));
                    if (l != null) return l;
                }
            }
            catch { }
            return new List<Item>();
        }

        static void Save(List<Item> list)
        {
            Directory.CreateDirectory(Paths.Skins);
            string tmp = LibFile + ".tmp";
            File.WriteAllText(tmp, J.Write(list), Encoding.UTF8);
            FileUtil.ReplaceWith(tmp, LibFile);
        }

        static string PngPath(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 40 || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains(".."))
                throw new Exception("Неверный id скина");
            return Path.Combine(Paths.Skins, id + ".png");
        }

        public static string DataUrl(byte[] png) { return "data:image/png;base64," + Convert.ToBase64String(png); }

        public static List<object> List()
        {
            lock (gate)
            {
                var r = new List<object>();
                var list = Load();
                bool dirty = false;
                foreach (var it in list.ToArray())
                {
                    string p = PngPath(it.id);
                    if (!File.Exists(p)) { list.Remove(it); dirty = true; continue; }
                    r.Add(J.D("id", it.id, "name", it.name, "variant", it.variant, "width", it.width, "height", it.height,
                        "added", it.added, "dataUrl", DataUrl(File.ReadAllBytes(p))));
                }
                if (dirty) Save(list);
                return r;
            }
        }

        public static Item AddBytes(byte[] png, string name, string variant)
        {
            int w, h;
            ValidatePng(png, out w, out h);
            lock (gate)
            {
                var list = Load();
                var it = new Item
                {
                    id = Guid.NewGuid().ToString("N").Substring(0, 12),
                    name = CleanName(name),
                    variant = string.IsNullOrEmpty(variant) ? DetectVariant(png) : NormVariant(variant),
                    added = DateTime.UtcNow.ToString("o"), width = w, height = h,
                };
                Directory.CreateDirectory(Paths.Skins);
                File.WriteAllBytes(PngPath(it.id), png);
                list.Insert(0, it);
                Save(list);
                return it;
            }
        }

        static string CleanName(string n)
        {
            n = (n ?? "").Trim();
            if (n.Length == 0) n = "Скин";
            return n.Length > 40 ? n.Substring(0, 40) : n;
        }

        // Импорт файлов (диалог или перетаскивание): каждый проверяется отдельно.
        public static Dictionary<string, object> ImportFiles(List<string> files)
        {
            var added = new List<object>();
            var errors = new List<object>();
            foreach (var f in files)
            {
                try
                {
                    if (!File.Exists(f)) throw new Exception("файл не найден");
                    if (new FileInfo(f).Length > MaxPng) throw new Exception("слишком большой для скина");
                    var it = AddBytes(File.ReadAllBytes(f), Path.GetFileNameWithoutExtension(f), null);
                    added.Add(it.id);
                }
                catch (Exception e) { errors.Add(Path.GetFileName(f) + ": " + e.Message); }
            }
            return J.D("added", added, "errors", errors, "skins", List());
        }

        public static List<object> Update(string id, string name, string variant)
        {
            lock (gate)
            {
                var list = Load();
                var it = list.Find(x => x.id == id);
                if (it == null) throw new Exception("Скин не найден");
                if (name != null) it.name = CleanName(name);
                if (variant != null) it.variant = NormVariant(variant);
                Save(list);
            }
            return List();
        }

        public static List<object> Delete(string id)
        {
            lock (gate)
            {
                var list = Load();
                list.RemoveAll(x => x.id == id);
                string p = PngPath(id);
                if (File.Exists(p)) File.Delete(p);
                Save(list);
            }
            return List();
        }

        public static List<object> Duplicate(string id)
        {
            Item src;
            lock (gate) src = Load().Find(x => x.id == id);
            if (src == null) throw new Exception("Скин не найден");
            string name = src.name + " (копия)";
            AddBytes(File.ReadAllBytes(PngPath(id)), name, src.variant);
            return List();
        }

        static Item Get(string id)
        {
            lock (gate)
            {
                var it = Load().Find(x => x.id == id);
                if (it == null) throw new Exception("Скин не найден в библиотеке");
                return it;
            }
        }

        // ---------- Minecraft Services API ----------
        public static byte[] BuildMultipart(string variant, byte[] png, string boundary)
        {
            var ms = new MemoryStream();
            Action<string> w = s => { var b = Encoding.UTF8.GetBytes(s); ms.Write(b, 0, b.Length); };
            w("--" + boundary + "\r\nContent-Disposition: form-data; name=\"variant\"\r\n\r\n" + NormVariant(variant) + "\r\n");
            w("--" + boundary + "\r\nContent-Disposition: form-data; name=\"file\"; filename=\"skin.png\"\r\nContent-Type: image/png\r\n\r\n");
            ms.Write(png, 0, png.Length);
            w("\r\n--" + boundary + "--\r\n");
            return ms.ToArray();
        }

        // Профиль: {id, name, skins:[{id,state,url,variant}], capes:[{id,state,url,alias}]} → удобный вид для интерфейса (без картинок).
        public static Dictionary<string, object> ParseProfile(string json)
        {
            var p = J.ParseObj(json);
            var skins = new List<object>();
            var capes = new List<object>();
            Dictionary<string, object> active = null;
            foreach (var o in J.A(p, "skins"))
            {
                var s = J.O(o);
                var d = J.D("id", J.S(s, "id"), "state", J.S(s, "state"), "url", J.S(s, "url"),
                    "variant", NormVariant(J.S(s, "variant")), "alias", J.S(s, "alias"));
                skins.Add(d);
                if (J.S(s, "state") == "ACTIVE" && active == null) active = d;
            }
            string activeCape = null;
            foreach (var o in J.A(p, "capes"))
            {
                var c = J.O(o);
                capes.Add(J.D("id", J.S(c, "id"), "state", J.S(c, "state"), "url", J.S(c, "url"), "alias", J.S(c, "alias") ?? "Плащ"));
                if (J.S(c, "state") == "ACTIVE") activeCape = J.S(c, "id");
            }
            return J.D("uuid", J.S(p, "id"), "name", J.S(p, "name"), "skins", skins, "capes", capes,
                "activeSkin", active, "activeCape", activeCape);
        }

        static string FriendlyApiError(HttpError e)
        {
            string msg = "";
            try { var b = J.ParseObj(e.Body); msg = J.S(b, "errorMessage") ?? J.S(b, "error") ?? ""; } catch { }
            switch (e.Status)
            {
                case 400: return "Mojang отклонил запрос" + (msg.Length > 0 ? ": " + msg : " (проверьте файл скина)");
                case 401: return "Вход устарел — Mojang не принял токен. Войдите в аккаунт заново на вкладке «Аккаунт».";
                case 403: return "Mojang запретил доступ (403). Скорее всего, приложение Lumina ещё не одобрено Mojang для API Minecraft (заявка aka.ms/mce-reviewappid), либо на аккаунте нет Java Edition.";
                case 404: return "Профиль Minecraft не найден" + (msg.Length > 0 ? ": " + msg : ". Создайте ник на minecraft.net.");
                case 429: return "Слишком много запросов к Mojang. Подождите минуту и попробуйте снова.";
                default: return "Сервис Minecraft ответил ошибкой HTTP " + e.Status + (msg.Length > 0 ? ": " + msg : "");
            }
        }

        // Выполнить запрос с токеном Minecraft выбранного аккаунта; при 401 — один раз обновить токен.
        static T WithToken<T>(Func<string, T> f)
        {
            var acc = AccountStore.Selected();
            if (acc == null) throw new Exception("Сначала войдите в аккаунт Microsoft (вкладка «Аккаунт») — скин меняется у аккаунта Minecraft.");
            var s = MicrosoftAuth.Session(acc);
            try { return f(s.AccessToken); }
            catch (HttpError e)
            {
                if (e.Status != 401) throw new Exception(FriendlyApiError(e));
                acc.mcTokenExpires = 0;
                s = MicrosoftAuth.Session(acc);
                try { return f(s.AccessToken); }
                catch (HttpError e2) { throw new Exception(FriendlyApiError(e2)); }
            }
        }

        // Картинки текстур — только с textures.minecraft.net.
        static string TextureDataUrl(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out u) || u.Host != "textures.minecraft.net") return null;
            string https = "https://" + u.Host + u.PathAndQuery;
            try { return DataUrl(Http.GetBytes(https, MaxPng)); }
            catch { try { return DataUrl(Http.GetBytes("http://" + u.Host + u.PathAndQuery, MaxPng)); } catch { return null; } }
        }

        static Dictionary<string, object> WithImages(Dictionary<string, object> prof)
        {
            var active = J.O(prof, "activeSkin");
            if (active != null) active["dataUrl"] = TextureDataUrl(J.S(active, "url"));
            foreach (var o in J.A(prof, "capes"))
            {
                var c = J.O(o);
                c["dataUrl"] = TextureDataUrl(J.S(c, "url"));
            }
            return prof;
        }

        public static Dictionary<string, object> AccountProfile()
        {
            return WithImages(ParseProfile(WithToken(t => Http.Get(Api, t))));
        }

        public static Dictionary<string, object> Apply(string id)
        {
            var it = Get(id);
            byte[] png = File.ReadAllBytes(PngPath(id));
            int w, h;
            ValidatePng(png, out w, out h);
            string boundary = "----Lumina" + Guid.NewGuid().ToString("N");
            byte[] body = BuildMultipart(it.variant, png, boundary);
            string json = WithToken(t => Http.SendRaw("POST", Api + "/skins", t, "multipart/form-data; boundary=" + boundary, body));
            // Ответ — обновлённый профиль; если пусто, перечитываем.
            return WithImages(ParseProfile(string.IsNullOrWhiteSpace(json) ? WithToken(t => Http.Get(Api, t)) : json));
        }

        public static Dictionary<string, object> Reset()
        {
            string json = WithToken(t => Http.SendRaw("DELETE", Api + "/skins/active", t, null, null));
            return WithImages(ParseProfile(string.IsNullOrWhiteSpace(json) ? WithToken(t => Http.Get(Api, t)) : json));
        }

        public static Dictionary<string, object> SetCape(string capeId)
        {
            string json;
            if (string.IsNullOrEmpty(capeId))
                json = WithToken(t => Http.SendRaw("DELETE", Api + "/capes/active", t, null, null));
            else
                json = WithToken(t => Http.SendRaw("PUT", Api + "/capes/active", t, "application/json", Encoding.UTF8.GetBytes(J.Write(J.D("capeId", capeId)))));
            return WithImages(ParseProfile(string.IsNullOrWhiteSpace(json) ? WithToken(t => Http.Get(Api, t)) : json));
        }

        // Текущий скин аккаунта → в библиотеку.
        public static List<object> ImportFromAccount()
        {
            var prof = ParseProfile(WithToken(t => Http.Get(Api, t)));
            var active = J.O(prof, "activeSkin");
            if (active == null) throw new Exception("У аккаунта стандартный скин — импортировать нечего");
            Uri u;
            if (!Uri.TryCreate(J.S(active, "url") ?? "", UriKind.Absolute, out u) || u.Host != "textures.minecraft.net")
                throw new Exception("Неожиданный адрес текстуры скина");
            byte[] png = Http.GetBytes("https://" + u.Host + u.PathAndQuery, MaxPng);
            AddBytes(png, (J.S(prof, "name") ?? "Аккаунт") + " — текущий", J.S(active, "variant"));
            return List();
        }
    }
}
