// Окно WinForms + WebView2: интерфейс из папки ui\ (https://app.local/), обмен сообщениями с app.js.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AkeeLauncher
{
    class MainWindow : Form
    {
        public static readonly string AppDir = Path.GetDirectoryName(Application.ExecutablePath);
        readonly WebView2 web = new WebView2();
        bool ready;
        readonly Queue<string> pending = new Queue<string>();
        readonly Queue<string> pendingLog = new Queue<string>();   // строки лога, пока интерфейс усыплён (ограничено)
        bool suspended, hiddenForGame;

        public MainWindow()
        {
            Text = "Lumina";
            BackColor = Color.FromArgb(8, 10, 22);
            StartPosition = FormStartPosition.CenterScreen;
            var wa = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(1280, wa.Width - 80), Math.Min(800, wa.Height - 60));
            MinimumSize = new Size(960, 600);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            web.Dock = DockStyle.Fill;
            web.DefaultBackgroundColor = BackColor;
            Controls.Add(web);
            AkeeLauncher.Events.Sink = Post;
            GameRunner.StateChanged += OnGameState;
            Load += async (a, b) => await Init();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dark = 1;
            try { DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch { }
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

        async Task Init()
        {
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Paths.ConfigRoot, "webview"));
                await web.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось запустить WebView2 Runtime: " + ex.Message +
                    "\n\nУстановите Microsoft Edge WebView2 Runtime с сайта Microsoft.", "Lumina", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }
            var core = web.CoreWebView2;
#if DEBUG
            core.Settings.AreDevToolsEnabled = true;
#else
            core.Settings.AreDevToolsEnabled = false;
#endif
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.SetVirtualHostNameToFolderMapping("app.local", Path.Combine(AppDir, "ui"), CoreWebView2HostResourceAccessKind.Allow);
            // Миниатюры скриншотов (только папка кэша миниатюр, не данные игры).
            try { Directory.CreateDirectory(Paths.Thumbs); core.SetVirtualHostNameToFolderMapping(Worlds.ThumbHost, Paths.Thumbs, CoreWebView2HostResourceAccessKind.Allow); } catch { }
            core.WebMessageReceived += OnMessage;
            core.NewWindowRequested += (s, e) => { e.Handled = true; HostCommands.OpenUrl(e.Uri); };
            core.NavigationCompleted += (s, e) => { ready = true; Flush(); };
            core.Navigate("https://app.local/index.html");
        }

        // Сообщение из интерфейса: {callId, cmd, ...}. Ответ: {id: callId, ok, data | error}.
        void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Dictionary<string, object> msg;
            try { msg = J.ParseObj(e.WebMessageAsJson); }
            catch { return; }
            if (msg == null) return;
            object id = msg.ContainsKey("callId") ? msg["callId"] : null;
            // Перетащенные файлы (postMessageWithAdditionalObjects): настоящие пути на диске.
            try
            {
                var extra = e.AdditionalObjects;
                if (extra != null && extra.Count > 0)
                {
                    var files = new List<object>();
                    foreach (var o in extra) { var f = o as CoreWebView2File; if (f != null) files.Add(f.Path); }
                    msg["files"] = files.ToArray();
                }
            }
            catch { }
            string cmd = J.S(msg, "cmd") ?? "";
            Task.Run(() => HostCommands.Run(this, cmd, msg)).ContinueWith(t =>
            {
                var reply = new Dictionary<string, object> { { "id", id }, { "ok", !t.IsFaulted } };
                if (t.IsFaulted) reply["error"] = t.Exception.GetBaseException().Message;
                else reply["data"] = t.Result;
                Send(J.Write(reply));
            });
        }

        // События из фоновых потоков: {event, ...}
        void Post(string name, Dictionary<string, object> data)
        {
            var d = new Dictionary<string, object>(data);
            d["event"] = name;
            Send(J.Write(d), name == "log");
        }

        void Send(string json) { Send(json, false); }

        void Send(string json, bool isLog)
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (suspended && ready)
                    {
                        // Интерфейс спит (игровой режим): события копим, лог — только последние строки (полный лог пишется в файл).
                        if (isLog) { pendingLog.Enqueue(json); while (pendingLog.Count > 1000) pendingLog.Dequeue(); }
                        else pending.Enqueue(json);
                        return;
                    }
                    if (!ready || web.CoreWebView2 == null) { pending.Enqueue(json); while (pending.Count > 500) pending.Dequeue(); return; }
                    web.CoreWebView2.PostWebMessageAsJson(json);
                }));
            }
            catch { }
        }

        void Flush()
        {
            if (web.CoreWebView2 == null) return;
            while (pendingLog.Count > 0) web.CoreWebView2.PostWebMessageAsJson(pendingLog.Dequeue());
            while (pending.Count > 0) web.CoreWebView2.PostWebMessageAsJson(pending.Dequeue());
        }

        // ---------- игровой режим: свернуть окно и усыпить WebView на время игры ----------
        void OnGameState(bool running)
        {
            if (IsDisposed) return;
            try { BeginInvoke(new Action(() => { if (running) EnterGameMode(); else ExitGameMode(); })); } catch { }
        }

        async void EnterGameMode()
        {
            if (!Config.Current.gameModeHide || !ready || web.CoreWebView2 == null || IsDisposed) return;
            hiddenForGame = true;
            WindowState = FormWindowState.Minimized;
            try
            {
                web.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                web.Visible = false;   // TrySuspendAsync работает только для невидимого WebView
                suspended = true;
                bool ok = await web.CoreWebView2.TrySuspendAsync();
                if (!ok && !hiddenForGame) ResumeUi();
            }
            catch { }
            TrimMemory();
        }

        void ExitGameMode()
        {
            if (!hiddenForGame) return;
            hiddenForGame = false;
            ResumeUi();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            try { Activate(); } catch { }
        }

        void ResumeUi()
        {
            if (web.CoreWebView2 == null) return;
            try { if (web.CoreWebView2.IsSuspended) web.CoreWebView2.Resume(); } catch { }
            try { web.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal; } catch { }
            web.Visible = true;
            suspended = false;
            Flush();
        }

        // Пользователь сам развернул окно во время игры — будим интерфейс.
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (suspended && WindowState != FormWindowState.Minimized) ResumeUi();
        }

        static void TrimMemory()
        {
            try { GC.Collect(); GC.WaitForPendingFinalizers(); SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1)); } catch { }
        }

        [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

        // Выполнить на UI-потоке (диалоги, буфер обмена).
        public T OnUi<T>(Func<T> f) { return (T)Invoke(f); }

        public void CloseLauncher() { BeginInvoke(new Action(Close)); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            AkeeLauncher.Events.Sink = null;
            GameRunner.StateChanged -= OnGameState;
            Launcher.Cancel();
            MicrosoftAuth.CancelLogin();
            base.OnFormClosing(e);
        }
    }
}
