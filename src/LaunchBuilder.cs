// Командная строка запуска: оптимизированные флаги JVM, подстановка ${...} для старого (minecraftArguments) и нового (arguments) формата.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AkeeLauncher
{
    // Данные входа, полученные ТОЛЬКО через Microsoft-аккаунт (см. MicrosoftAuth).
    public class AuthSession
    {
        public string Name, Uuid, AccessToken, Xuid = "0";
    }

    public class LaunchCommand
    {
        public string Javaw, WorkDir;
        public List<string> Args = new List<string>();
        public int RamMb, JavaMajor;

        public string ArgString() { return LaunchBuilder.JoinArgs(Args); }

        // Для журнала: токен скрыт.
        public string Printable(string token)
        {
            string s = "\"" + Javaw + "\" " + ArgString();
            return string.IsNullOrEmpty(token) || token.Length < 8 ? s : s.Replace(token, "<скрыто>");
        }
    }

    static class LaunchBuilder
    {
        public static long TotalRamMb()
        {
            try { return (long)(new Microsoft.VisualBasic.Devices.ComputerInfo().TotalPhysicalMemory / (1024 * 1024)); }
            catch { return 8192; }
        }

        // Авто: ~30% памяти ПК, от 2 до 8 ГБ, кратно 512 МБ.
        public static int AutoRamMb()
        {
            long r = TotalRamMb() * 30 / 100;
            r = Math.Max(2048, Math.Min(8192, r));
            return (int)(r / 512 * 512);
        }

        public static int EffectiveRam(Instance inst)
        {
            if (inst != null && inst.ramMb > 0) return inst.ramMb;
            if (Config.Current.ramMb > 0) return Config.Current.ramMb;
            return AutoRamMb();
        }

        // G1GC в духе флагов Aikar, адаптированных для клиента: короткие паузы, крупное молодое поколение, без AlwaysPreTouch.
        public static List<string> TunedJvm(int ramMb, int javaMajor)
        {
            var a = new List<string> {
                "-Xms" + ramMb + "M", "-Xmx" + ramMb + "M",
                "-XX:+UnlockExperimentalVMOptions", "-XX:+UseG1GC", "-XX:+ParallelRefProcEnabled",
                "-XX:MaxGCPauseMillis=50", "-XX:+DisableExplicitGC",
                "-XX:G1NewSizePercent=30", "-XX:G1MaxNewSizePercent=40",
                "-XX:G1HeapRegionSize=" + (ramMb >= 12288 ? "16M" : "8M"),
                "-XX:G1ReservePercent=20", "-XX:G1HeapWastePercent=5", "-XX:G1MixedGCCountTarget=4",
                "-XX:InitiatingHeapOccupancyPercent=15", "-XX:G1MixedGCLiveThresholdPercent=90",
                "-XX:SurvivorRatio=32", "-XX:MaxTenuringThreshold=1", "-XX:+PerfDisableSharedMem",
                "-Dfile.encoding=UTF-8", "-Dsun.stdout.encoding=UTF-8", "-Dsun.stderr.encoding=UTF-8",
            };
            if (javaMajor >= 17) a.Add("-XX:+UseStringDeduplication");
            if (javaMajor >= 18) { a.Add("-Dstdout.encoding=UTF-8"); a.Add("-Dstderr.encoding=UTF-8"); }
            return a;
        }

        public static LaunchCommand Build(PreparedGame g, Instance inst, string gameDir, AuthSession auth, string javaw, int javaMajor)
        {
            return Build(g, inst, gameDir, auth, javaw, javaMajor, null);
        }

        // quickPlayServer — "host:port" для быстрого входа на сервер (1.20+: --quickPlayMultiplayer), иначе null.
        public static LaunchCommand Build(PreparedGame g, Instance inst, string gameDir, AuthSession auth, string javaw, int javaMajor, string quickPlayServer)
        {
            var s = Config.Current;
            var v = g.Version;
            int ram = EffectiveRam(inst);
            bool customRes = s.customResolution && !s.fullscreen;
            bool quick = !string.IsNullOrEmpty(quickPlayServer);
            var features = new Dictionary<string, bool> { { "has_custom_resolution", customRes }, { "is_quick_play_multiplayer", quick } };

            var vars = new Dictionary<string, string> {
                { "auth_player_name", auth.Name }, { "version_name", v.Id }, { "game_directory", gameDir },
                { "assets_root", g.AssetsRoot }, { "game_assets", g.GameAssets }, { "assets_index_name", g.AssetIndexId },
                { "auth_uuid", auth.Uuid }, { "auth_access_token", auth.AccessToken },
                { "auth_session", "token:" + auth.AccessToken + ":" + auth.Uuid },
                { "auth_xuid", auth.Xuid ?? "0" }, { "clientid", Config.Current.azureClientId ?? "" },
                { "user_type", "msa" }, { "user_properties", "{}" }, { "version_type", v.Type },
                { "natives_directory", g.NativesDir }, { "library_directory", Paths.Libraries },
                { "classpath_separator", ";" }, { "classpath", string.Join(";", g.Classpath) },
                { "launcher_name", "Lumina" }, { "launcher_version", "1.0" },
                { "resolution_width", s.width.ToString() }, { "resolution_height", s.height.ToString() },
                { "quickPlayMultiplayer", quickPlayServer ?? "" },
            };

            var cmd = new LaunchCommand { Javaw = javaw, WorkDir = gameDir, RamMb = ram, JavaMajor = javaMajor };
            cmd.Args.AddRange(TunedJvm(ram, javaMajor));
            if (!string.IsNullOrEmpty(g.LogArg)) cmd.Args.Add(g.LogArg);

            var arguments = J.O(v.Json, "arguments");
            var jvm = J.A(arguments, "jvm");
            if (jvm.Count > 0) AddArgs(cmd.Args, jvm, vars, features);
            else cmd.Args.AddRange(new[] { "-Djava.library.path=" + g.NativesDir, "-cp", vars["classpath"] });   // старый формат

            cmd.Args.AddRange(SplitArgs(s.jvmArgs));
            if (inst != null) cmd.Args.AddRange(SplitArgs(inst.jvmArgs));
            cmd.Args.Add(v.MainClass);

            string legacy = J.S(v.Json, "minecraftArguments");
            if (J.A(arguments, "game").Count > 0) AddArgs(cmd.Args, J.A(arguments, "game"), vars, features);
            else if (legacy != null)
            {
                foreach (var t in legacy.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) cmd.Args.Add(Subst(t, vars));
                if (customRes) cmd.Args.AddRange(new[] { "--width", s.width.ToString(), "--height", s.height.ToString() });
            }
            if (s.fullscreen) cmd.Args.Add("--fullscreen");
            // Профили загрузчиков без правила is_quick_play_multiplayer — добавляем аргумент сами (поддерживается с 1.20).
            if (quick && !cmd.Args.Contains("--quickPlayMultiplayer")) { cmd.Args.Add("--quickPlayMultiplayer"); cmd.Args.Add(quickPlayServer); }
            return cmd;
        }

        static void AddArgs(List<string> outList, List<object> src, Dictionary<string, string> vars, Dictionary<string, bool> features)
        {
            foreach (var o in src)
            {
                if (o is string) { outList.Add(Subst((string)o, vars)); continue; }
                var d = J.O(o);
                if (d == null || !Rules.Allowed(J.A(d, "rules"), features)) continue;
                object val; d.TryGetValue("value", out val);
                if (val is string) outList.Add(Subst((string)val, vars));
                else foreach (var x in J.A(val)) outList.Add(Subst(x.ToString(), vars));
            }
        }

        static readonly Regex VarRx = new Regex(@"\$\{([a-zA-Z0-9_]+)\}");

        public static string Subst(string s, Dictionary<string, string> vars)
        {
            return VarRx.Replace(s, m => { string v; return vars.TryGetValue(m.Groups[1].Value, out v) ? (v ?? "") : m.Value; });
        }

        // Разбор строки аргументов пользователя с учётом кавычек.
        public static List<string> SplitArgs(string s)
        {
            var r = new List<string>();
            if (string.IsNullOrWhiteSpace(s)) return r;
            var cur = new StringBuilder();
            bool q = false, any = false;
            foreach (char c in s)
            {
                if (c == '"') { q = !q; any = true; }
                else if (char.IsWhiteSpace(c) && !q) { if (any) { r.Add(cur.ToString()); cur.Clear(); any = false; } }
                else { cur.Append(c); any = true; }
            }
            if (any) r.Add(cur.ToString());
            return r;
        }

        // Экранирование по правилам CommandLineToArgvW.
        public static string JoinArgs(List<string> args)
        {
            var sb = new StringBuilder();
            foreach (var a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) { sb.Append(a); continue; }
                sb.Append('"');
                int bs = 0;
                foreach (char c in a)
                {
                    if (c == '\\') { bs++; continue; }
                    if (c == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); bs = 0; continue; }
                    sb.Append('\\', bs); bs = 0; sb.Append(c);
                }
                sb.Append('\\', bs * 2);
                sb.Append('"');
            }
            return sb.ToString();
        }
    }
}
