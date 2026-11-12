// Lumina (by AkeeTech) — точка входа: одно окно на пользователя, DPI, TLS, загрузка настроек.
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Lumina")]
[assembly: AssemblyProduct("Lumina")]
[assembly: AssemblyCompany("AkeeTech")]
[assembly: AssemblyDescription("Лаунчер Minecraft: Java Edition")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace AkeeLauncher
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            var mx = new Mutex(true, "AkeeTech-Lumina-single-instance", out created);
            if (!created) return;
            try { SetProcessDPIAware(); } catch { }
            Http.Init();
            Config.Load();
            try { GameMode.RecoverOnStartup(); } catch { }   // схема питания после сбоя во время игры
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainWindow());
            VerifyCache.Save();
            GC.KeepAlive(mx);
        }

        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    }
}
