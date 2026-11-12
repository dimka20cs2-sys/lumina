// Настройки лаунчера, пути к папкам данных и канал событий в интерфейс.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AkeeLauncher
{
    public class Settings
    {
        public string azureClientId = "";      // Application (client) ID из portal.azure.com — вводит пользователь
        public string dataDir = "";            // пусто = %APPDATA%\.lumina
        public int ramMb = 0;                   // 0 = авто
        public string javaPath = "";           // пусто = Java от Mojang, скачивается сама
        public string jvmArgs = "";            // дополнительные аргументы JVM
        public int width = 1280;
        public int height = 720;
        public bool fullscreen = false;
        public bool customResolution = false;
        public int concurrency = 16;
        public string priority = "normal";     // normal | above | high
        public bool closeOnLaunch = false;     // закрыть лаунчер после запуска игры
        // Игровой режим (см. GameMode.cs): всё выключено по умолчанию.
        public bool gameModeGpu = false;       // javaw лаунчера → дискретная видеокарта (HKCU\...\UserGpuPreferences)
        public bool gameModePower = false;     // схема «Высокая производительность» на время игры
        public bool gameModeHide = false;      // свернуть лаунчер и усыпить WebView на время игры
        public string lastInstance = "";
        public string selectedAccount = "";    // uuid
    }

    static class Paths
    {
        // Корень настроек (settings.json, accounts.dat) — всегда тут; игровые файлы — в Settings.dataDir.
        public static string ConfigRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".lumina");
        public static string DataRoot = ConfigRoot;

        public static string SettingsFile { get { return Path.Combine(ConfigRoot, "settings.json"); } }
        public static string AccountsFile { get { return Path.Combine(ConfigRoot, "accounts.dat"); } }
        public static string Versions { get { return Path.Combine(DataRoot, "versions"); } }
        public static string Libraries { get { return Path.Combine(DataRoot, "libraries"); } }
        public static string Assets { get { return Path.Combine(DataRoot, "assets"); } }
        public static string Runtimes { get { return Path.Combine(DataRoot, "runtimes"); } }
        public static string Instances { get { return Path.Combine(DataRoot, "instances"); } }
        public static string Cache { get { return Path.Combine(DataRoot, "cache"); } }
        public static string Natives { get { return Path.Combine(DataRoot, "natives"); } }
        public static string Skins { get { return Path.Combine(DataRoot, "skins"); } }
        public static string Backups { get { return Path.Combine(DataRoot, "backups"); } }
        public static string Thumbs { get { return Path.Combine(ConfigRoot, "thumbs"); } }
        public static string ServersFile { get { return Path.Combine(ConfigRoot, "servers.json"); } }
        public static string GameModeState { get { return Path.Combine(ConfigRoot, "gamemode-state.json"); } }
    }

    static class Config
    {
        static readonly object gate = new object();
        public static Settings Current = new Settings();

        public static void Load()
        {
            lock (gate)
            {
                try
                {
                    if (File.Exists(Paths.SettingsFile))
                        Current = J.Ser.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile, Encoding.UTF8)) ?? new Settings();
                }
                catch { Current = new Settings(); }
                Normalize(Current);
                Apply();
            }
        }

        public static void Save()
        {
            lock (gate)
            {
                Normalize(Current);
                Directory.CreateDirectory(Paths.ConfigRoot);
                string tmp = Paths.SettingsFile + ".tmp";
                File.WriteAllText(tmp, J.Write(Current), Encoding.UTF8);
                if (File.Exists(Paths.SettingsFile)) File.Replace(tmp, Paths.SettingsFile, null);
                else File.Move(tmp, Paths.SettingsFile);
                Apply();
            }
        }

        static void Normalize(Settings s)
        {
            if (s.concurrency < 1) s.concurrency = 1;
            if (s.concurrency > 64) s.concurrency = 64;
            if (s.ramMb < 0) s.ramMb = 0;
            if (s.ramMb > 0 && s.ramMb < 1024) s.ramMb = 1024;
            if (s.width < 320) s.width = 320;
            if (s.height < 240) s.height = 240;
            if (s.priority != "above" && s.priority != "high") s.priority = "normal";
            s.azureClientId = (s.azureClientId ?? "").Trim();
            if (s.azureClientId.Length == 0) s.azureClientId = BuiltInClientId;
            s.dataDir = (s.dataDir ?? "").Trim();
            s.javaPath = (s.javaPath ?? "").Trim().Trim('"');
            if (s.jvmArgs == null) s.jvmArgs = "";
        }

        static void Apply()
        {
            Paths.DataRoot = string.IsNullOrEmpty(Current.dataDir) ? Paths.ConfigRoot : Current.dataDir;
            Directory.CreateDirectory(Paths.DataRoot);
        }

        // Приложение «Lumina» в Azure (Application (client) ID) — встроено, чтобы игрокам ничего не настраивать.
        // Это не секрет: такой ID публичен у любого лаунчера.
        public const string BuiltInClientId = "46e94c90-26b8-4c33-b3c9-115189d5944f";

        public static bool AzureConfigured
        {
            get { Guid g; return Guid.TryParse(Current.azureClientId, out g) && g != Guid.Empty; }
        }
    }

    // События в интерфейс: {event: "...", ...}. Окно направляет их в WebView2, самотест — в консоль.
    static class Events
    {
        public static Action<string, Dictionary<string, object>> Sink;

        public static void Emit(string name, Dictionary<string, object> data)
        {
            var s = Sink;
            if (s == null) return;
            try { s(name, data ?? new Dictionary<string, object>()); } catch { }
        }

        public static void Stage(string stage, string text) { Emit("stage", J.D("stage", stage, "text", text)); }
        public static void Log(string line) { Emit("log", J.D("line", line)); }
    }
}
