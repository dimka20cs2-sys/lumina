// Запуск процесса игры: приоритет, захват вывода в интерфейс и в файл, разбор XML-логов log4j, определение вылета.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AkeeLauncher
{
    static class GameRunner
    {
        static Process game;
        static StreamWriter logFile;
        static readonly Queue<string> tail = new Queue<string>();
        static readonly object gate = new object();
        static string curInstance;

        // Для окна (игровой режим: свернуть/усыпить интерфейс): true — игра запущена, false — завершилась.
        public static event Action<bool> StateChanged;

        public static bool Running { get { var p = game; try { return p != null && !p.HasExited; } catch { return false; } } }
        public static string RunningInstance { get { return Running ? curInstance : null; } }

        // capture=false — лаунчер закрывается после запуска, вывод не перехватываем.
        public static void Start(LaunchCommand cmd, string instanceId, bool capture)
        {
            lock (gate)
            {
                if (Running) throw new Exception("Игра уже запущена");
                tail.Clear();
                evt = null; evtMsg = null;
                curInstance = instanceId;
                var psi = new ProcessStartInfo(cmd.Javaw, cmd.ArgString())
                {
                    WorkingDirectory = cmd.WorkDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = capture,
                    RedirectStandardError = capture,
                };
                if (capture) { psi.StandardOutputEncoding = Encoding.UTF8; psi.StandardErrorEncoding = Encoding.UTF8; }
                psi.EnvironmentVariables.Remove("_JAVA_OPTIONS");
                psi.EnvironmentVariables.Remove("JAVA_TOOL_OPTIONS");

                string logPath = Path.Combine(cmd.WorkDir, "logs", "lumina-launcher.log");
                Directory.CreateDirectory(Path.GetDirectoryName(logPath));
                if (logFile != null) { try { logFile.Dispose(); } catch { } }
                logFile = new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true };

                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                if (capture)
                {
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) OnLine(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) OnLine(e.Data); };
                }
                p.Exited += (s, e) => OnExit(p, instanceId);
                GameMode.BeforeLaunch(cmd.Javaw);
                try { p.Start(); }
                catch { GameMode.AfterExit(); throw; }
                game = p;
                GameMode.OnStarted(p);
                try
                {
                    var pr = Config.Current.priority;
                    p.PriorityClass = pr == "high" ? ProcessPriorityClass.High : pr == "above" ? ProcessPriorityClass.AboveNormal : ProcessPriorityClass.Normal;
                }
                catch { }
                if (capture) { p.BeginOutputReadLine(); p.BeginErrorReadLine(); }
                Events.Emit("game", J.D("state", "running", "instance", instanceId, "pid", p.Id));
                var h = StateChanged; if (h != null) try { h(true); } catch { }
            }
        }

        public static void Kill()
        {
            var p = game;
            try { if (p != null && !p.HasExited) p.Kill(); } catch { }
        }

        // log4j в старых конфигурациях пишет события XML-ом: <log4j:Event … level="INFO" thread="…"><log4j:Message><![CDATA[…]]></log4j:Message></log4j:Event>
        static readonly Regex EvStart = new Regex("<log4j:Event[^>]*timestamp=\"(\\d+)\"[^>]*level=\"(\\w+)\"[^>]*thread=\"([^\"]*)\"");
        static string evt, evtMsg;

        static void OnLine(string line)
        {
            lock (tail)
            {
                if (line.StartsWith("<log4j:Event"))
                {
                    var m = EvStart.Match(line);
                    if (m.Success)
                    {
                        var t = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(m.Groups[1].Value)).ToLocalTime();
                        evt = "[" + t.ToString("HH:mm:ss") + "] [" + m.Groups[3].Value + "/" + m.Groups[2].Value + "]: ";
                        evtMsg = "";
                        return;
                    }
                }
                if (evt != null)
                {
                    if (line.Contains("</log4j:Event>")) { Emit(evt + evtMsg.Trim()); evt = null; return; }
                    string l = line.Replace("<log4j:Message><![CDATA[", "").Replace("]]></log4j:Message>", "")
                                   .Replace("<log4j:Throwable><![CDATA[", "").Replace("]]></log4j:Throwable>", "");
                    evtMsg += (evtMsg.Length > 0 ? "\n" : "") + l;
                    return;
                }
                Emit(line);
            }
        }

        static void Emit(string text)
        {
            foreach (var l in text.Split('\n'))
            {
                string line = l.TrimEnd('\r');
                tail.Enqueue(line);
                while (tail.Count > 60) tail.Dequeue();
                try { if (logFile != null) logFile.WriteLine(line); } catch { }
                Events.Log(line);
            }
        }

        static void OnExit(Process p, string instanceId)
        {
            int code = 0;
            try { code = p.ExitCode; } catch { }
            try { p.WaitForExit(); } catch { }   // дочитать вывод
            string[] last;
            lock (tail) last = tail.ToArray();
            bool crashed = code != 0;
            if (crashed)
            {
                var crashDir = Path.Combine(Instances.Dir(instanceId), "crash-reports");
                if (Directory.Exists(crashDir))
                {
                    var files = new DirectoryInfo(crashDir).GetFiles("crash-*.txt");
                    Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                    if (files.Length > 0 && (DateTime.UtcNow - files[0].LastWriteTimeUtc).TotalMinutes < 5)
                        Emit("Отчёт о сбое: " + files[0].FullName);
                }
            }
            try { if (logFile != null) { logFile.WriteLine("[Lumina] Игра завершилась, код " + code); logFile.Dispose(); logFile = null; } } catch { }
            GameMode.AfterExit();
            var sc = StateChanged; if (sc != null) try { sc(false); } catch { }
            Events.Emit("game", J.D("state", "exited", "instance", instanceId, "code", code, "crashed", crashed, "tail", last));
        }
    }
}
