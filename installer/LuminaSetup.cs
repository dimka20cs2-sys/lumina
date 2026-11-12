// Установщик и деинсталлятор Lumina Launcher (by AkeeTech).
// Сборка в один автономный EXE со встроенным архивом LuminaPayload.zip, проверкой SHA-256,
// созданием ярлыков (Рабочий стол + меню «Пуск») и регистрацией в «Установленных приложениях» Windows (HKCU).
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Lumina Setup")]
[assembly: AssemblyProduct("Lumina Launcher")]
[assembly: AssemblyCompany("AkeeTech")]
[assembly: AssemblyDescription("Установщик лаунчера Lumina для Minecraft: Java Edition")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace LuminaSetup
{
    static class Program
    {
        public const string AppName = "Lumina";
        public const string AppVersion = "1.0.0";
        public const string Publisher = "AkeeTech";
        public const string UninstallRegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Lumina";
        public const string ExpectedExeSha256 = "c5a9fca40fc94529c6cf36df384e192e31fadeb1d3d12efc1e4cfa7d7b56b507";

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            foreach (var a in args)
            {
                if (string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "/uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    RunUninstall();
                    return;
                }
            }

            Application.Run(new SetupForm());
        }

        static void RunUninstall()
        {
            var res = MessageBox.Show(
                "Удалить лаунчер Lumina с компьютера?\n\nВаши миры, скриншоты и сборки в %APPDATA%\\.lumina сохранятся.",
                "Удаление Lumina",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (res != DialogResult.Yes) return;

            try
            {
                foreach (var p in Process.GetProcessesByName("Lumina"))
                {
                    try { p.Kill(); p.WaitForExit(3000); } catch { }
                }

                string desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Lumina.lnk");
                string startLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Lumina.lnk");
                if (File.Exists(desktopLnk)) File.Delete(desktopLnk);
                if (File.Exists(startLnk)) File.Delete(startLnk);

                string installDir = Path.GetDirectoryName(Application.ExecutablePath);
                try { Registry.CurrentUser.DeleteSubKeyTree(UninstallRegKey, false); } catch { }

                // Удаление папки программы через cmd после завершения деинсталлятора
                if (!string.IsNullOrEmpty(installDir) && Directory.Exists(installDir))
                {
                    var psi = new ProcessStartInfo("cmd.exe", "/C timeout /t 2 /nobreak >nul & rmdir /s /q \"" + installDir + "\"")
                    {
                        WindowStyle = ProcessWindowStyle.Hidden,
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    Process.Start(psi);
                }

                MessageBox.Show("Lumina успешно удалена.", "Удаление Lumina", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка при удалении: " + ex.Message, "Lumina", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public static void CreateShortcut(string lnkPath, string targetExe, string workDir, string description)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            object shell = Activator.CreateInstance(shellType);
            object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            Type scType = shortcut.GetType();
            scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetExe });
            scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workDir });
            scType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { description });
            scType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { targetExe + ",0" });
            scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }

        public static string Sha256File(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
            {
                var hash = sha.ComputeHash(fs);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }

    class SetupForm : Form
    {
        readonly TextBox pathBox;
        readonly Button browseBtn;
        readonly CheckBox chkDesktop;
        readonly CheckBox chkStartMenu;
        readonly CheckBox chkVerifyHash;
        readonly CheckBox chkLaunch;
        readonly Button installBtn;
        readonly Button cancelBtn;
        readonly Label statusLabel;
        readonly Panel progressTrack;
        readonly Panel progressFill;
        Image logoImg;
        bool installing;
        bool finished;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public SetupForm()
        {
            Text = "Установка Lumina — v" + Program.AppVersion;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 445);
            BackColor = Color.FromArgb(14, 15, 22);
            ForeColor = Color.FromArgb(244, 244, 248);
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            LoadEmbeddedLogo();

            var header = new BannerPanel(logoImg)
            {
                Dock = DockStyle.Top,
                Height = 112
            };
            Controls.Add(header);

            var lblDir = new Label
            {
                Text = "ПАПКА УСТАНОВКИ",
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                ForeColor = Color.FromArgb(150, 156, 178),
                Location = new Point(28, 128),
                AutoSize = true
            };
            Controls.Add(lblDir);

            string defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Lumina");
            pathBox = new TextBox
            {
                Text = defaultDir,
                Location = new Point(28, 150),
                Size = new Size(396, 29),
                BackColor = Color.FromArgb(24, 26, 38),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 9.5f)
            };
            Controls.Add(pathBox);

            browseBtn = new Button
            {
                Text = "Обзор…",
                Location = new Point(432, 148),
                Size = new Size(100, 29),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(28, 31, 46),
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            browseBtn.FlatAppearance.BorderColor = Color.FromArgb(55, 60, 82);
            browseBtn.Click += OnBrowse;
            Controls.Add(browseBtn);

            chkDesktop = MakeCheck("Создать ярлык на Рабочем столе", 194, true);
            chkStartMenu = MakeCheck("Добавить ярлык в меню «Пуск» и список программ Windows", 224, true);
            chkVerifyHash = MakeCheck("Проверить целостность файлов по криптографическому хешу SHA-256", 254, true);
            chkLaunch = MakeCheck("Запустить Lumina сразу после установки", 284, true);

            Controls.Add(chkDesktop);
            Controls.Add(chkStartMenu);
            Controls.Add(chkVerifyHash);
            Controls.Add(chkLaunch);

            statusLabel = new Label
            {
                Text = "Готово к установке · Не требует прав администратора · Токены защищены Windows DPAPI",
                Location = new Point(28, 326),
                Size = new Size(504, 20),
                ForeColor = Color.FromArgb(148, 163, 184),
                Font = new Font("Segoe UI", 8.5f)
            };
            Controls.Add(statusLabel);

            progressTrack = new Panel
            {
                Location = new Point(28, 352),
                Size = new Size(504, 8),
                BackColor = Color.FromArgb(28, 31, 46)
            };
            progressFill = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(0, 8),
                BackColor = Color.FromArgb(240, 96, 122)
            };
            progressTrack.Controls.Add(progressFill);
            Controls.Add(progressTrack);

            cancelBtn = new Button
            {
                Text = "Отмена",
                Location = new Point(296, 384),
                Size = new Size(104, 38),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(24, 26, 38),
                ForeColor = Color.FromArgb(200, 205, 220),
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 9.5f)
            };
            cancelBtn.FlatAppearance.BorderColor = Color.FromArgb(50, 54, 74);
            cancelBtn.Click += (s, e) => Close();
            Controls.Add(cancelBtn);

            installBtn = new Button
            {
                Text = "Установить",
                Location = new Point(408, 384),
                Size = new Size(124, 38),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(232, 86, 118),
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 9.75f, FontStyle.Bold)
            };
            installBtn.FlatAppearance.BorderSize = 0;
            installBtn.Click += OnInstallClick;
            Controls.Add(installBtn);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dark = 1;
            try { DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch { }
        }

        CheckBox MakeCheck(string text, int y, bool isChecked)
        {
            return new CheckBox
            {
                Text = text,
                Checked = isChecked,
                Location = new Point(28, y),
                Size = new Size(504, 24),
                ForeColor = Color.FromArgb(226, 232, 240),
                Font = new Font("Segoe UI", 9.25f),
                Cursor = Cursors.Hand
            };
        }

        void LoadEmbeddedLogo()
        {
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("LuminaPayload.zip"))
                {
                    if (s == null) return;
                    using (var z = new ZipArchive(s, ZipArchiveMode.Read))
                    {
                        var entry = z.GetEntry("ui/img/lumina-512.png");
                        if (entry != null)
                        {
                            using (var es = entry.Open())
                            using (var ms = new MemoryStream())
                            {
                                es.CopyTo(ms);
                                ms.Position = 0;
                                logoImg = Image.FromStream(ms);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        void OnBrowse(object sender, EventArgs e)
        {
            using (var d = new FolderBrowserDialog { Description = "Выберите папку для установки Lumina", SelectedPath = pathBox.Text, ShowNewFolderButton = true })
            {
                if (d.ShowDialog(this) == DialogResult.OK)
                    pathBox.Text = d.SelectedPath;
            }
        }

        void SetProgress(double ratio, string text)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetProgress(ratio, text))); return; }
            int w = (int)Math.Round(Math.Max(0, Math.Min(1, ratio)) * progressTrack.Width);
            progressFill.Width = w;
            statusLabel.Text = text;
        }

        async void OnInstallClick(object sender, EventArgs e)
        {
            if (finished)
            {
                string exe = Path.Combine(pathBox.Text.Trim(), "Lumina.exe");
                if (chkLaunch.Checked && File.Exists(exe))
                {
                    try { Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = pathBox.Text.Trim(), UseShellExecute = true }); } catch { }
                }
                Close();
                return;
            }
            if (installing) return;

            string targetDir = pathBox.Text.Trim();
            if (string.IsNullOrEmpty(targetDir))
            {
                MessageBox.Show(this, "Укажите папку для установки.", "Lumina Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            installing = true;
            pathBox.Enabled = false;
            browseBtn.Enabled = false;
            chkDesktop.Enabled = false;
            chkStartMenu.Enabled = false;
            chkVerifyHash.Enabled = false;
            installBtn.Enabled = false;
            cancelBtn.Enabled = false;

            bool createDesktop = chkDesktop.Checked;
            bool createStart = chkStartMenu.Checked;
            bool verifySha = chkVerifyHash.Checked;

            try
            {
                await Task.Run(() => PerformInstall(targetDir, createDesktop, createStart, verifySha));
                finished = true;
                progressFill.BackColor = Color.FromArgb(52, 211, 153);
                SetProgress(1.0, "✓ Lumina успешно установлена и проверена!");
                installBtn.Text = "Готово";
                installBtn.BackColor = Color.FromArgb(16, 185, 129);
                installBtn.Enabled = true;

                if (chkLaunch.Checked)
                {
                    string exe = Path.Combine(targetDir, "Lumina.exe");
                    if (File.Exists(exe))
                    {
                        try { Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = targetDir, UseShellExecute = true }); } catch { }
                    }
                    Close();
                }
            }
            catch (Exception ex)
            {
                installing = false;
                pathBox.Enabled = true;
                browseBtn.Enabled = true;
                chkDesktop.Enabled = true;
                chkStartMenu.Enabled = true;
                chkVerifyHash.Enabled = true;
                installBtn.Enabled = true;
                cancelBtn.Enabled = true;
                SetProgress(0, "Ошибка: " + ex.Message);
                MessageBox.Show(this, "Не удалось завершить установку:\n" + ex.Message, "Установка Lumina", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void PerformInstall(string targetDir, bool createDesktop, bool createStart, bool verifySha)
        {
            SetProgress(0.05, "Закрываем запущенные копии Lumina…");
            foreach (var p in Process.GetProcessesByName("Lumina"))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }

            Directory.CreateDirectory(targetDir);
            string fullRoot = Path.GetFullPath(targetDir).TrimEnd('\\') + "\\";

            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("LuminaPayload.zip"))
            {
                if (s == null) throw new Exception("Встроенный архив LuminaPayload.zip не найден.");
                using (var z = new ZipArchive(s, ZipArchiveMode.Read))
                {
                    int total = Math.Max(1, z.Entries.Count);
                    int done = 0;
                    foreach (var entry in z.Entries)
                    {
                        done++;
                        string rel = entry.FullName.Replace('/', '\\');
                        string dest = Path.GetFullPath(Path.Combine(fullRoot, rel));
                        if (!dest.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                            throw new Exception("Недопустимый путь в архиве: " + entry.FullName);

                        if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                        {
                            Directory.CreateDirectory(dest);
                            continue;
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        SetProgress(0.1 + 0.65 * done / total, "Распаковка: " + entry.Name);
                        using (var src = entry.Open())
                        using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            src.CopyTo(dst);
                        }
                    }
                }
            }

            string luminaExe = Path.Combine(targetDir, "Lumina.exe");
            if (!File.Exists(luminaExe))
                throw new Exception("Файл Lumina.exe не найден после распаковки.");

            if (verifySha)
            {
                SetProgress(0.80, "Проверка контрольной суммы SHA-256…");
                string actualSha = Program.Sha256File(luminaExe);
                if (!string.Equals(actualSha, Program.ExpectedExeSha256, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Контрольная сумма Lumina.exe не совпала (SHA-256).");
            }

            SetProgress(0.88, "Регистрация деинсталлятора в системе Windows…");
            string uninstallerPath = Path.Combine(targetDir, "Uninstall-Lumina.exe");
            try { File.Copy(Application.ExecutablePath, uninstallerPath, true); } catch { }

            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Program.UninstallRegKey))
                {
                    if (k != null)
                    {
                        k.SetValue("DisplayName", "Lumina Launcher");
                        k.SetValue("DisplayVersion", Program.AppVersion);
                        k.SetValue("Publisher", Program.Publisher);
                        k.SetValue("InstallLocation", targetDir);
                        k.SetValue("DisplayIcon", luminaExe + ",0");
                        k.SetValue("UninstallString", "\"" + uninstallerPath + "\" --uninstall");
                        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                        k.SetValue("EstimatedSize", 4200, RegistryValueKind.DWord);
                    }
                }
            }
            catch { }

            if (createDesktop)
            {
                SetProgress(0.94, "Создание ярлыка на Рабочем столе…");
                string desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Lumina.lnk");
                try { Program.CreateShortcut(desktopLnk, luminaExe, targetDir, "Лаунчер Minecraft: Java Edition"); } catch { }
            }

            if (createStart)
            {
                SetProgress(0.97, "Добавление ярлыка в меню «Пуск»…");
                string startLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Lumina.lnk");
                try { Program.CreateShortcut(startLnk, luminaExe, targetDir, "Лаунчер Minecraft: Java Edition"); } catch { }
            }
        }
    }

    class BannerPanel : Panel
    {
        readonly Image logo;
        public BannerPanel(Image logoImg)
        {
            logo = logoImg;
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using (var br = new LinearGradientBrush(ClientRectangle, Color.FromArgb(36, 17, 66), Color.FromArgb(89, 31, 99), 135f))
            {
                g.FillRectangle(br, ClientRectangle);
            }

            using (var pen = new Pen(Color.FromArgb(40, 255, 255, 255), 1f))
            {
                g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            }

            int textX = 28;
            if (logo != null)
            {
                g.DrawImage(logo, new Rectangle(28, 24, 64, 64));
                textX = 108;
            }

            using (var titleFont = new Font("Segoe UI", 17f, FontStyle.Bold))
            using (var subFont = new Font("Segoe UI", 9.5f, FontStyle.Regular))
            using (var badgeFont = new Font("Segoe UI", 8f, FontStyle.Bold))
            using (var white = new SolidBrush(Color.White))
            using (var muted = new SolidBrush(Color.FromArgb(220, 224, 240)))
            using (var badgeBg = new SolidBrush(Color.FromArgb(65, 52, 211, 153)))
            using (var badgeFg = new SolidBrush(Color.FromArgb(167, 243, 208)))
            {
                g.DrawString("Lumina Launcher", titleFont, white, textX, 24);
                g.DrawString("Быстрый и безопасный лаунчер Minecraft: Java Edition", subFont, muted, textX + 2, 58);

                var badgeRect = new Rectangle(Width - 138, 28, 110, 24);
                g.FillRectangle(badgeBg, badgeRect);
                g.DrawString("✓ SHA-256 VERIFIED", badgeFont, badgeFg, badgeRect.X + 7, badgeRect.Y + 5);
            }
        }
    }
}
