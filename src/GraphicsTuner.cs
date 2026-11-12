// «Автонастройка графики»: определение железа (WMI + реестр видеоадаптеров + частота экрана) и запись options.txt
// (и config\sodium-options.json, если стоит Sodium) в папку инсталляции под профиль «Макс. FPS» / «Баланс» / «Красиво».
// Существующий options.txt не перезаписывается целиком: меняются только ключи графики, бинды/язык и прочее остаются.
// Перед первой правкой делается резервная копия — «Вернуть как было» восстанавливает её.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace AkeeLauncher
{
    public class HardwareInfo
    {
        public string Gpu = "", GpuVendor = "";
        public long VramMb;
        public bool Discrete;
        public List<string> AllGpus = new List<string>();
        public string Cpu = "";
        public int Cores, Threads;
        public long RamMb;
        public int RefreshHz, ScreenW, ScreenH;
        public int Tier;            // 0 слабый … 3 мощный
        public string TierName = "";
    }

    static class GraphicsTuner
    {
        public static readonly string[] Profiles = { "fps", "balance", "quality" };
        static HardwareInfo cached;

        // ---------- железо ----------
        public static HardwareInfo Detect(bool fresh)
        {
            if (cached != null && !fresh) return cached;
            var h = new HardwareInfo();
            var vram = RegistryVram();
            long bestScore = -1;
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name, AdapterRAM, CurrentRefreshRate FROM Win32_VideoController"))
                    foreach (ManagementObject o in s.Get())
                    {
                        string name = ((o["Name"] ?? "") + "").Trim();
                        if (name.Length == 0) continue;
                        h.AllGpus.Add(name);
                        if (name.Contains("Microsoft Basic") || name.Contains("Remote Display") || name.Contains("Virtual")) continue;
                        long mb = 0;
                        if (vram.ContainsKey(name)) mb = vram[name];
                        else { try { mb = Convert.ToInt64(o["AdapterRAM"] ?? 0) / 1048576; } catch { } }
                        string vendor = Vendor(name);
                        bool discrete = IsDiscrete(name, vendor, mb);
                        long score = (discrete ? 1000000 : 0) + mb;
                        if (score > bestScore) { bestScore = score; h.Gpu = name; h.GpuVendor = vendor; h.VramMb = mb; h.Discrete = discrete; }
                        try { int hz = Convert.ToInt32(o["CurrentRefreshRate"] ?? 0); if (hz > h.RefreshHz && hz < 1000) h.RefreshHz = hz; } catch { }
                    }
            }
            catch { }
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor"))
                    foreach (ManagementObject o in s.Get())
                    {
                        h.Cpu = ((o["Name"] ?? "") + "").Trim();
                        h.Cores += Convert.ToInt32(o["NumberOfCores"] ?? 0);
                        h.Threads += Convert.ToInt32(o["NumberOfLogicalProcessors"] ?? 0);
                    }
            }
            catch { }
            if (h.Threads == 0) h.Threads = Environment.ProcessorCount;
            if (h.Cores == 0) h.Cores = Math.Max(1, h.Threads / 2);
            h.RamMb = LaunchBuilder.TotalRamMb();
            // Частота основного монитора — точнее, чем у WMI (там частота любого активного выхода).
            try
            {
                var dm = new DEVMODE();
                dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
                if (EnumDisplaySettings(null, -1, ref dm))
                {
                    if (dm.dmDisplayFrequency > 1) h.RefreshHz = dm.dmDisplayFrequency;
                    h.ScreenW = dm.dmPelsWidth; h.ScreenH = dm.dmPelsHeight;
                }
            }
            catch { }
            if (h.RefreshHz < 30) h.RefreshHz = 60;
            ComputeTier(h);
            cached = h;
            return h;
        }

        static string Vendor(string name)
        {
            string n = name.ToLowerInvariant();
            if (n.Contains("nvidia") || n.Contains("geforce") || n.Contains("quadro") || n.Contains("rtx")) return "NVIDIA";
            if (n.Contains("amd") || n.Contains("radeon")) return "AMD";
            if (n.Contains("intel")) return "Intel";
            return "";
        }

        static bool IsDiscrete(string name, string vendor, long vramMb)
        {
            string n = name.ToLowerInvariant();
            if (vendor == "NVIDIA") return true;
            if (vendor == "AMD")
                return n.Contains("rx ") || n.Contains("radeon pro") || (vramMb >= 2048 && !n.Contains("radeon(tm) graphics") && !n.Contains("vega"));
            if (vendor == "Intel") return n.Contains("arc") && vramMb >= 2048;
            return vramMb >= 2048;
        }

        // Точный объём видеопамяти (WMI AdapterRAM ограничен 4 ГБ): HKLM\...\Class\{4d36e968…}\000N\HardwareInformation.qwMemorySize.
        static Dictionary<string, long> RegistryVram()
        {
            var r = new Dictionary<string, long>();
            try
            {
                using (var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (cls == null) return r;
                    foreach (var sub in cls.GetSubKeyNames())
                    {
                        if (sub.Length != 4) continue;
                        try
                        {
                            using (var k = cls.OpenSubKey(sub))
                            {
                                if (k == null) continue;
                                string desc = k.GetValue("DriverDesc") as string;
                                if (string.IsNullOrEmpty(desc)) continue;
                                long bytes = 0;
                                object q = k.GetValue("HardwareInformation.qwMemorySize");
                                if (q is long) bytes = (long)q;
                                else if (q is byte[] && ((byte[])q).Length >= 8) bytes = BitConverter.ToInt64((byte[])q, 0);
                                if (bytes <= 0)
                                {
                                    object m = k.GetValue("HardwareInformation.MemorySize");
                                    if (m is int) bytes = (uint)(int)m;
                                    else if (m is byte[] && ((byte[])m).Length >= 4) bytes = BitConverter.ToUInt32((byte[])m, 0);
                                }
                                long mb = bytes / 1048576;
                                if (mb > 0 && (!r.ContainsKey(desc) || r[desc] < mb)) r[desc] = mb;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return r;
        }

        static void ComputeTier(HardwareInfo h)
        {
            int gpu = !h.Discrete ? 0 : h.VramMb >= 8000 ? 3 : h.VramMb >= 5500 ? 2 : 1;
            int cpu = h.Cores >= 8 ? 2 : h.Cores >= 6 ? 1 : 0;
            int ram = h.RamMb >= 15000 ? 2 : h.RamMb >= 7500 ? 1 : 0;
            int t;
            if (gpu == 0 || ram == 0 || h.Cores < 4) t = 0;
            else if (gpu >= 3 && cpu >= 2 && ram >= 2) t = 3;
            else if (gpu >= 2 && cpu >= 1 && ram >= 1) t = 2;
            else t = 1;
            h.Tier = t;
            h.TierName = new[] { "начальный", "средний", "мощный", "топовый" }[t];
        }

        public static Dictionary<string, object> HardwareDict(HardwareInfo h)
        {
            return J.D("gpu", h.Gpu, "gpuVendor", h.GpuVendor, "vramMb", h.VramMb, "discrete", h.Discrete, "allGpus", h.AllGpus,
                "cpu", h.Cpu, "cores", h.Cores, "threads", h.Threads, "ramMb", h.RamMb,
                "refreshHz", h.RefreshHz, "screen", h.ScreenW > 0 ? h.ScreenW + "×" + h.ScreenH : "",
                "tier", h.Tier, "tierName", h.TierName);
        }

        // ---------- профиль → значения options.txt (ключи 1.20+) ----------
        public static List<KeyValuePair<string, string>> OptionsFor(string profile, HardwareInfo h, bool sodium, bool fullscreen)
        {
            int t = h.Tier;
            int hz = Math.Max(30, Math.Min(260, h.RefreshHz));
            int rd, sim, gfx, particles, biome, mip; double ent; bool ao, shadows; string clouds; int maxFps;
            switch (profile)
            {
                case "fps":
                    rd = new[] { 6, 8, 10, 12 }[t]; sim = new[] { 5, 6, 6, 8 }[t];
                    gfx = 0; particles = t == 0 ? 2 : 1; ent = t == 0 ? 0.5 : 0.75; biome = 0; mip = t == 0 ? 0 : 2;
                    ao = t > 0; shadows = false; clouds = "false"; maxFps = 260;
                    break;
                case "quality":
                    rd = new[] { 10, 16, 20, 24 }[t]; sim = new[] { 8, 10, 12, 12 }[t];
                    gfx = t == 3 && !sodium ? 2 : 1; particles = 0; ent = t >= 2 ? 1.5 : 1.25; biome = t == 0 ? 2 : 5; mip = 4;
                    ao = true; shadows = true; clouds = "true"; maxFps = hz;
                    break;
                default:   // balance
                    rd = new[] { 8, 12, 14, 16 }[t]; sim = new[] { 6, 8, 10, 10 }[t];
                    gfx = t == 0 ? 0 : 1; particles = t >= 2 ? 0 : 1; ent = 1.0; biome = t == 0 ? 1 : 2; mip = 4;
                    ao = true; shadows = t > 0; clouds = "fast"; maxFps = hz;
                    break;
            }
            if (h.RamMb < 12000) rd = Math.Min(rd, 16);
            sim = Math.Max(5, Math.Min(sim, rd));
            Func<bool, string> b = x => x ? "true" : "false";
            return new List<KeyValuePair<string, string>>
            {
                Kv("renderDistance", rd.ToString()),
                Kv("simulationDistance", sim.ToString()),
                Kv("graphicsMode", gfx.ToString()),
                Kv("maxFps", maxFps.ToString()),
                Kv("enableVsync", "false"),
                Kv("particles", particles.ToString()),
                Kv("entityDistanceScaling", ent.ToString("0.0#", CultureInfo.InvariantCulture)),
                Kv("biomeBlendRadius", biome.ToString()),
                Kv("mipmapLevels", mip.ToString()),
                Kv("ao", b(ao)),
                Kv("entityShadows", b(shadows)),
                Kv("renderClouds", "\"" + clouds + "\""),
                Kv("prioritizeChunkUpdates", "0"),
                Kv("fullscreen", b(fullscreen)),
            };
        }

        static KeyValuePair<string, string> Kv(string k, string v) { return new KeyValuePair<string, string>(k, v); }

        // Sodium: те же профили для его собственных настроек (раздел.ключ → значение JSON).
        public static List<KeyValuePair<string, object>> SodiumFor(string profile, HardwareInfo h)
        {
            string q = profile == "fps" ? "FAST" : profile == "quality" ? "FANCY" : "DEFAULT";
            return new List<KeyValuePair<string, object>>
            {
                new KeyValuePair<string, object>("quality.weather_quality", q),
                new KeyValuePair<string, object>("quality.leaves_quality", q),
                new KeyValuePair<string, object>("quality.enable_vignette", profile != "fps"),
                new KeyValuePair<string, object>("performance.chunk_builder_threads", 0),
                new KeyValuePair<string, object>("performance.always_defer_chunk_updates_v2", profile != "quality"),
                new KeyValuePair<string, object>("performance.animate_only_visible_textures", true),
                new KeyValuePair<string, object>("performance.use_entity_culling", true),
                new KeyValuePair<string, object>("performance.use_fog_occlusion", true),
                new KeyValuePair<string, object>("performance.use_block_face_culling", true),
                new KeyValuePair<string, object>("performance.use_no_error_g_l_context", true),
                new KeyValuePair<string, object>("advanced.cpu_render_ahead_limit", 3),
            };
        }

        // ---------- запись с сохранением остальных строк ----------
        // Возвращает новый текст и список изменений [{file, key, from, to}].
        public static string MergeOptions(string existing, List<KeyValuePair<string, string>> values, List<object> changes, string dataVersion)
        {
            var lines = new List<string>();
            if (!string.IsNullOrEmpty(existing)) lines.AddRange(existing.Replace("\r\n", "\n").Split('\n'));
            while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
            bool hasVersion = false;
            foreach (var l in lines) if (l.StartsWith("version:")) hasVersion = true;
            if (!hasVersion && !string.IsNullOrEmpty(dataVersion)) lines.Insert(0, "version:" + dataVersion);
            foreach (var kv in values)
            {
                int idx = -1;
                for (int i = 0; i < lines.Count; i++) if (lines[i].StartsWith(kv.Key + ":")) { idx = i; break; }
                string old = idx >= 0 ? lines[idx].Substring(kv.Key.Length + 1) : null;
                if (old == kv.Value) continue;
                if (idx >= 0) lines[idx] = kv.Key + ":" + kv.Value; else lines.Add(kv.Key + ":" + kv.Value);
                changes.Add(J.D("file", "options.txt", "key", kv.Key, "from", old, "to", kv.Value));
            }
            return string.Join("\n", lines) + "\n";
        }

        public static string MergeSodium(string existing, List<KeyValuePair<string, object>> values, List<object> changes)
        {
            Dictionary<string, object> root = null;
            try { if (!string.IsNullOrWhiteSpace(existing)) root = J.ParseObj(existing); } catch { }
            if (root == null) root = new Dictionary<string, object>();
            foreach (var kv in values)
            {
                var parts = kv.Key.Split('.');
                var sec = J.O(root, parts[0]);
                if (sec == null) { sec = new Dictionary<string, object>(); root[parts[0]] = sec; }
                object old;
                sec.TryGetValue(parts[1], out old);
                string oldS = old == null ? null : J.Write(old), newS = J.Write(kv.Value);
                if (oldS == newS) continue;
                sec[parts[1]] = kv.Value;
                changes.Add(J.D("file", "sodium-options.json", "key", kv.Key, "from", oldS, "to", newS));
            }
            return J.Write(root);
        }

        public static bool HasSodium(string gameDir)
        {
            string mods = Path.Combine(gameDir, "mods");
            if (!Directory.Exists(mods)) return false;
            foreach (var f in Directory.GetFiles(mods, "*.jar"))
            {
                string n = Path.GetFileName(f).ToLowerInvariant();
                if (n.StartsWith("sodium") && !n.StartsWith("sodium-extra") && !n.StartsWith("sodiumextra")) return true;
            }
            return false;
        }

        static string BackupDir(string gameDir) { return Path.Combine(gameDir, ".lumina-backup", "graphics"); }

        // Номер данных (DataVersion) версии игры — из version.json внутри client.jar; нужен, только если options.txt ещё нет.
        static string DataVersion(string mc)
        {
            try
            {
                string jar = Path.Combine(Paths.Versions, mc, mc + ".jar");
                if (!File.Exists(jar)) return null;
                using (var z = ZipFile.OpenRead(jar))
                {
                    var e = z.GetEntry("version.json");
                    if (e == null) return null;
                    using (var rd = new StreamReader(e.Open(), Encoding.UTF8))
                    {
                        long v = J.L(J.ParseObj(rd.ReadToEnd()), "world_version");
                        return v > 0 ? v.ToString() : null;
                    }
                }
            }
            catch { return null; }
        }

        public static Dictionary<string, object> Status(string instId)
        {
            string dir = Instances.Dir(instId);
            string man = Path.Combine(BackupDir(dir), "manifest.json");
            Dictionary<string, object> m = null;
            try { if (File.Exists(man)) m = J.ParseObj(File.ReadAllText(man, Encoding.UTF8)); } catch { }
            return J.D("hardware", HardwareDict(Detect(false)), "sodium", HasSodium(dir), "hasBackup", m != null,
                "backupDate", J.S(m, "created"), "profile", J.S(m, "profile"), "optionsExists", File.Exists(Path.Combine(dir, "options.txt")));
        }

        // Применить профиль к папке игры (самотест передаёт свою папку и железо).
        public static Dictionary<string, object> ApplyToDir(string gameDir, string mc, string profile, HardwareInfo h, bool fullscreen)
        {
            if (Array.IndexOf(Profiles, profile) < 0) throw new Exception("Неизвестный профиль графики: " + profile);
            string opt = Path.Combine(gameDir, "options.txt");
            string sodFile = Path.Combine(gameDir, "config", "sodium-options.json");
            bool sodium = HasSodium(gameDir);

            // Резервная копия — только перед первой автонастройкой (чтобы «Вернуть как было» вернуло исходное состояние).
            string bdir = BackupDir(gameDir);
            string man = Path.Combine(bdir, "manifest.json");
            bool backupCreated = false;
            if (!File.Exists(man))
            {
                Directory.CreateDirectory(bdir);
                if (File.Exists(opt)) File.Copy(opt, Path.Combine(bdir, "options.txt"), true);
                if (File.Exists(sodFile)) File.Copy(sodFile, Path.Combine(bdir, "sodium-options.json"), true);
                File.WriteAllText(man, J.Write(J.D("created", DateTime.Now.ToString("yyyy-MM-dd HH:mm"), "hadOptions", File.Exists(opt),
                    "hadSodium", File.Exists(sodFile), "profile", profile)), Encoding.UTF8);
                backupCreated = true;
            }
            else
            {
                try { var m = J.ParseObj(File.ReadAllText(man, Encoding.UTF8)); m["profile"] = profile; File.WriteAllText(man, J.Write(m), Encoding.UTF8); } catch { }
            }

            var changes = new List<object>();
            string existing = File.Exists(opt) ? File.ReadAllText(opt, new UTF8Encoding(false)) : null;
            string text = MergeOptions(existing, OptionsFor(profile, h, sodium, fullscreen), changes, existing == null ? DataVersion(mc) : null);
            WriteAtomic(opt, text);
            if (sodium)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(sodFile));
                string sj = File.Exists(sodFile) ? File.ReadAllText(sodFile, Encoding.UTF8) : null;
                WriteAtomic(sodFile, MergeSodium(sj, SodiumFor(profile, h), changes));
            }
            return J.D("profile", profile, "hardware", HardwareDict(h), "changes", changes, "sodium", sodium, "backupCreated", backupCreated, "hasBackup", true);
        }

        static void WriteAtomic(string path, string text)
        {
            FileUtil.WriteAtomic(path, text, new UTF8Encoding(false));
        }

        public static Dictionary<string, object> Apply(string instId, string profile)
        {
            if (GameRunner.RunningInstance == instId) throw new Exception("Сначала закройте игру: при выходе она перезапишет options.txt");
            var inst = Instances.Get(instId);
            return ApplyToDir(Instances.Dir(instId), inst.mc, profile, Detect(false), Config.Current.fullscreen);
        }

        public static Dictionary<string, object> Restore(string instId)
        {
            if (GameRunner.RunningInstance == instId) throw new Exception("Сначала закройте игру");
            return RestoreDir(Instances.Dir(instId));
        }

        public static Dictionary<string, object> RestoreDir(string gameDir)
        {
            string bdir = BackupDir(gameDir), man = Path.Combine(bdir, "manifest.json");
            if (!File.Exists(man)) throw new Exception("Резервной копии нет — автонастройка ещё не применялась");
            var m = J.ParseObj(File.ReadAllText(man, Encoding.UTF8));
            string opt = Path.Combine(gameDir, "options.txt");
            string sodFile = Path.Combine(gameDir, "config", "sodium-options.json");
            var restored = new List<object>();
            if (J.B(m, "hadOptions")) { File.Copy(Path.Combine(bdir, "options.txt"), opt, true); restored.Add("options.txt"); }
            else if (File.Exists(opt)) { File.Delete(opt); restored.Add("options.txt (удалён — его не было)"); }
            if (J.B(m, "hadSodium")) { File.Copy(Path.Combine(bdir, "sodium-options.json"), sodFile, true); restored.Add("sodium-options.json"); }
            else if (File.Exists(sodFile))
            {
                // Файл создала автонастройка — Sodium сам создаст настройки по умолчанию.
                File.Delete(sodFile); restored.Add("sodium-options.json (удалён — его не было)");
            }
            Directory.Delete(bdir, true);
            try { string parent = Path.GetDirectoryName(bdir); if (Directory.GetFileSystemEntries(parent).Length == 0) Directory.Delete(parent); } catch { }
            return J.D("restored", restored, "hasBackup", false);
        }

        // ---------- WinAPI ----------
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }
    }
}
