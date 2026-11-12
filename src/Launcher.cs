// Сценарий «Играть»: аккаунт → версия (+Fabric) → файлы → Java → командная строка → запуск.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace AkeeLauncher
{
    static class Launcher
    {
        static int busy;

        public static bool Busy { get { return busy != 0; } }

        public class Prepared
        {
            public Instance Instance;
            public PreparedGame Game;
            public LaunchCommand Command;
            public string GameDir;
        }

        // Подготовка без запуска (её же использует самотест с тестовым AuthSession).
        public static Prepared Prepare(Instance inst, AuthSession auth) { return Prepare(inst, auth, null); }

        public static Prepared Prepare(Instance inst, AuthSession auth, string server)
        {
            string gameDir = Instances.Dir(inst.id);
            Events.Stage("version", "Версия " + inst.mc + (inst.loader != "vanilla" ? " + " + Loaders.Title(inst.loader) : "") + "…");
            var v = Versions.Resolve(inst.mc, inst.loader, inst.loaderVersion);
            var g = GameFiles.Prepare(v, gameDir);
            var java = JavaRuntime.Ensure(v);
            var cmd = LaunchBuilder.Build(g, inst, gameDir, auth, java.Javaw, java.Major, server);
            return new Prepared { Instance = inst, Game = g, Command = cmd, GameDir = gameDir };
        }

        // Возвращает true, если игра запущена. closeLauncher вызывается, если включено «закрыть лаунчер».
        public static Dictionary<string, object> Play(string instanceId, Action closeLauncher) { return Play(instanceId, closeLauncher, null); }

        // server — "host:port": сразу зайти на сервер (Quick Play, 1.20+).
        public static Dictionary<string, object> Play(string instanceId, Action closeLauncher, string server)
        {
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw new Exception("Подготовка уже идёт");
            Downloader.Cancel = false;
            var sw = Stopwatch.StartNew();
            try
            {
                if (GameRunner.Running) throw new Exception("Игра уже запущена");
                var inst = Instances.Get(instanceId);

                // Сначала аккаунт: без купленного Minecraft ничего не качаем и не запускаем.
                Events.Stage("auth", "Проверка аккаунта Microsoft…");
                var auth = MicrosoftAuth.Session(AccountStore.Selected());

                if (!string.IsNullOrEmpty(server)) server = Servers.NormalizeAddress(server);
                var p = Prepare(inst, auth, server);
                if (!string.IsNullOrEmpty(server)) Events.Log("[Lumina] Быстрый вход на сервер: " + server);
                Events.Log("[Lumina] Java: " + p.Command.Javaw + " (Java " + p.Command.JavaMajor + "), память " + p.Command.RamMb + " МБ");
                Events.Log("[Lumina] " + p.Command.Printable(auth.AccessToken));
                Events.Stage("start", "Запуск Minecraft…");
                bool close = Config.Current.closeOnLaunch && closeLauncher != null;
                GameRunner.Start(p.Command, inst.id, !close);
                Instances.Touch(inst.id);
                Events.Stage("done", "Игра запущена");
                if (close) closeLauncher();
                return J.D("started", true, "seconds", Math.Round(sw.Elapsed.TotalSeconds, 1), "ramMb", p.Command.RamMb, "java", p.Command.JavaMajor);
            }
            catch (Exception e)
            {
                Events.Stage("error", e.Message);
                throw;
            }
            finally { Interlocked.Exchange(ref busy, 0); }
        }

        public static void Cancel() { Downloader.Cancel = true; }
    }
}
