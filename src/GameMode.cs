// «Игровой режим»: на время игры — дискретная видеокарта для Java лаунчера и схема питания «Высокая производительность».
// Меняется только то, что относится к Lumina:
//  • HKCU\Software\Microsoft\DirectX\UserGpuPreferences: значение "<путь к javaw.exe из папки runtimes лаунчера>" = "GpuPreference=2;"
//    (настройка пользователя, как в «Параметры → Дисплей → Графика»); при выключении восстанавливается прежнее значение.
//  • powercfg /setactive: схема «Высокая производительность» на время игры, после выхода — прежняя активная схема.
//    Если лаунчер упал во время игры, восстановление выполняется при следующем запуске (состояние в gamemode-state.json).
// Автозапуск не добавляется, настройки других программ не трогаются.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace AkeeLauncher
{
    static class GameMode
    {
        public const string GpuKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
        public const string GpuValue = "GpuPreference=2;";
        public const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
        public const string UltimatePerformance = "e9a42b02-d5df-448d-aa00-03f14749eb61";
        static readonly object gate = new object();

        public class State
        {
            public string powerRestore = "";     // схема, которую вернуть после игры
            public string powerSet = "";         // схема, которую включили мы
            public int pid;
            public long pidStart;                // время старта процесса игры (UTC ticks) — защита от повторного pid
            public Dictionary<string, string> gpuOriginal = new Dictionary<string, string>();   // путь javaw → прежнее значение ("" = не было)
        }

        static State Load()
        {
            try { if (File.Exists(Paths.GameModeState)) return J.Ser.Deserialize<State>(File.ReadAllText(Paths.GameModeState, Encoding.UTF8)) ?? new State(); }
            catch { }
            return new State();
        }

        static void Save(State s)
        {
            Directory.CreateDirectory(Paths.ConfigRoot);
            if (string.IsNullOrEmpty(s.powerRestore) && s.gpuOriginal.Count == 0) { try { File.Delete(Paths.GameModeState); } catch { } return; }
            File.WriteAllText(Paths.GameModeState, J.Write(s), Encoding.UTF8);
        }

        // ---------- видеокарта ----------
        // Только javaw.exe из папки runtimes лаунчера — чужие программы не трогаем.
        public static bool IsLauncherJava(string javaw)
        {
            if (string.IsNullOrEmpty(javaw)) return false;
            try
            {
                string full = Path.GetFullPath(javaw);
                string root = Path.GetFullPath(Paths.Runtimes).TrimEnd('\\') + "\\";
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(full).Equals("javaw.exe", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // Новое значение: GpuPreference=2; поверх прежних параметров (например, SwapEffectUpgradeEnable=1;).
        public static string MergeGpuValue(string old)
        {
            string rest = Regex.Replace(old ?? "", @"GpuPreference=\d+;?", "");
            if (rest.Length > 0 && !rest.EndsWith(";")) rest += ";";
            return rest + GpuValue;
        }

        static string ReadGpu(string path)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(GpuKey))
                return k == null ? null : k.GetValue(path) as string;
        }

        static void ApplyGpu(string javaw)
        {
            if (!IsLauncherJava(javaw)) { Events.Log("[Lumina] Игровой режим: своя Java из настроек — предпочтение видеокарты не меняем"); return; }
            string path = Path.GetFullPath(javaw);
            lock (gate)
            {
                var st = Load();
                string cur = ReadGpu(path);
                string want = MergeGpuValue(cur);
                if (cur == want) return;
                if (!st.gpuOriginal.ContainsKey(path)) st.gpuOriginal[path] = cur ?? "";
                using (var k = Registry.CurrentUser.CreateSubKey(GpuKey)) k.SetValue(path, want, RegistryValueKind.String);
                Save(st);
                Events.Log("[Lumina] Игровой режим: Java → высокопроизводительная видеокарта (" + want + ")");
            }
        }

        // Выключили переключатель — возвращаем прежние значения (только для путей, которые меняли мы).
        public static int RemoveGpuPrefs()
        {
            int n = 0;
            lock (gate)
            {
                var st = Load();
                if (st.gpuOriginal.Count == 0) return 0;
                using (var k = Registry.CurrentUser.OpenSubKey(GpuKey, true))
                {
                    foreach (var kv in st.gpuOriginal)
                    {
                        if (k == null) break;
                        string cur = k.GetValue(kv.Key) as string;
                        if (cur == null || cur != MergeGpuValue(kv.Value)) continue;   // пользователь уже поменял сам — не трогаем
                        if (kv.Value.Length == 0) k.DeleteValue(kv.Key, false); else k.SetValue(kv.Key, kv.Value, RegistryValueKind.String);
                        n++;
                    }
                }
                st.gpuOriginal.Clear();
                Save(st);
            }
            return n;
        }

        // ---------- схема питания ----------
        public class PowerScheme { public string Guid, Name; public bool Active; }

        static string PowerCfg(string args)
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
            };
            using (var p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(10000);
                if (p.ExitCode != 0) throw new Exception("powercfg " + args + " → код " + p.ExitCode);
                return o;
            }
        }

        static readonly Regex GuidRx = new Regex(@"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*(?:\((.*?)\))?\s*(\*)?");

        public static List<PowerScheme> Schemes()
        {
            var r = new List<PowerScheme>();
            foreach (var line in PowerCfg("/list").Split('\n'))
            {
                var m = GuidRx.Match(line);
                if (m.Success) r.Add(new PowerScheme { Guid = m.Groups[1].Value.ToLowerInvariant(), Name = m.Groups[2].Value.Trim(), Active = m.Groups[3].Success });
            }
            return r;
        }

        public static string ActiveScheme()
        {
            var m = GuidRx.Match(PowerCfg("/getactivescheme"));
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
        }

        // Какую схему включать: «Высокая производительность», иначе «Максимальная» (если пользователь её добавил). Сами схемы не создаём.
        public static string TargetScheme(List<PowerScheme> list)
        {
            foreach (var s in list) if (s.Guid == HighPerformance) return s.Guid;
            foreach (var s in list) if (s.Guid == UltimatePerformance) return s.Guid;
            return null;
        }

        static void SwitchPower()
        {
            lock (gate)
            {
                var st = Load();
                string active = ActiveScheme();
                string target = TargetScheme(Schemes());
                if (target == null) { Events.Log("[Lumina] Игровой режим: схема «Высокая производительность» на этом ПК скрыта — питание не меняем"); return; }
                if (active == null || active == target) return;
                if (string.IsNullOrEmpty(st.powerRestore)) st.powerRestore = active;   // при повторном запуске не теряем исходную
                st.powerSet = target;
                Save(st);   // сначала запись — чтобы восстановить даже после сбоя
                PowerCfg("/setactive " + target);
                Events.Log("[Lumina] Игровой режим: схема питания «Высокая производительность» (после игры вернётся " + active + ")");
            }
        }

        public static void RestorePower()
        {
            lock (gate)
            {
                var st = Load();
                if (string.IsNullOrEmpty(st.powerRestore)) return;
                try
                {
                    string active = ActiveScheme();
                    // Если пользователь сам сменил схему во время игры — его выбор оставляем.
                    if (active == st.powerSet) { PowerCfg("/setactive " + st.powerRestore); Events.Log("[Lumina] Игровой режим: схема питания восстановлена"); }
                }
                catch (Exception e) { Events.Log("[Lumina] Игровой режим: не удалось вернуть схему питания: " + e.Message); }
                st.powerRestore = ""; st.powerSet = ""; st.pid = 0; st.pidStart = 0;
                Save(st);
            }
        }

        // ---------- вызовы из GameRunner ----------
        public static void BeforeLaunch(string javaw)
        {
            var s = Config.Current;
            try { if (s.gameModeGpu) ApplyGpu(javaw); } catch (Exception e) { Events.Log("[Lumina] Игровой режим (видеокарта): " + e.Message); }
            try { if (s.gameModePower) SwitchPower(); } catch (Exception e) { Events.Log("[Lumina] Игровой режим (питание): " + e.Message); }
        }

        public static void OnStarted(Process p)
        {
            lock (gate)
            {
                var st = Load();
                if (string.IsNullOrEmpty(st.powerRestore)) return;
                try { st.pid = p.Id; st.pidStart = p.StartTime.ToUniversalTime().Ticks; } catch { }
                Save(st);
            }
        }

        public static void AfterExit() { RestorePower(); }

        // При запуске лаунчера: схема осталась включённой после сбоя. Если та игра ещё идёт — ждём её выхода в фоне.
        public static void RecoverOnStartup()
        {
            State st;
            lock (gate) st = Load();
            if (string.IsNullOrEmpty(st.powerRestore)) return;
            Process p = null;
            try
            {
                if (st.pid > 0)
                {
                    p = Process.GetProcessById(st.pid);
                    if (p.StartTime.ToUniversalTime().Ticks != st.pidStart) p = null;
                }
            }
            catch { p = null; }
            if (p == null) { RestorePower(); return; }
            var t = new Thread(() => { try { p.WaitForExit(); } catch { } RestorePower(); }) { IsBackground = true };
            t.Start();
        }

        public static void OnSettingsSaved()
        {
            if (!Config.Current.gameModeGpu) { try { RemoveGpuPrefs(); } catch { } }
        }

        // Самотест: что было бы сделано, без изменений в реестре и схемах питания.
        public static Dictionary<string, object> DryRun(string javaw)
        {
            string path = string.IsNullOrEmpty(javaw) ? "" : Path.GetFullPath(javaw);
            string cur = null;
            try { cur = ReadGpu(path); } catch { }
            var list = new List<object>();
            List<PowerScheme> schemes = new List<PowerScheme>();
            string active = null, err = null;
            try { schemes = Schemes(); active = ActiveScheme(); } catch (Exception e) { err = e.Message; }
            foreach (var s in schemes) list.Add(J.D("guid", s.Guid, "name", s.Name, "active", s.Active));
            return J.D(
                "gpu", J.D("key", @"HKCU\" + GpuKey, "valueName", path, "eligible", IsLauncherJava(path), "current", cur, "wouldWrite", MergeGpuValue(cur)),
                "power", J.D("active", active, "target", TargetScheme(schemes), "schemes", list, "error", err));
        }
    }
}
