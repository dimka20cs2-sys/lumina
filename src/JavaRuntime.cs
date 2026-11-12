// Java от Mojang: нужный компонент (javaVersion.component) для windows-x64, проверка хешей, javaw.exe.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace AkeeLauncher
{
    static class JavaRuntime
    {
        public const string AllUrl = "https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";
        const string Platform = "windows-x64";

        public class Result { public string Javaw; public int Major; public bool Custom; }

        public static Result Ensure(VersionInfo v)
        {
            string custom = Config.Current.javaPath;
            if (!string.IsNullOrEmpty(custom))
            {
                string exe = custom;
                if (Directory.Exists(exe)) exe = Path.Combine(exe, "bin", "javaw.exe");
                else if (Path.GetFileName(exe).Equals("java.exe", StringComparison.OrdinalIgnoreCase))
                    exe = Path.Combine(Path.GetDirectoryName(exe), "javaw.exe");
                if (!File.Exists(exe)) throw new Exception("Java из настроек не найдена: " + custom);
                return new Result { Javaw = exe, Major = DetectMajor(exe, v.JavaMajor), Custom = true };
            }
            return new Result { Javaw = Install(v.JavaComponent), Major = v.JavaMajor };
        }

        public static string Install(string component)
        {
            Events.Stage("java", "Проверка Java (" + component + ")…");
            string root = Path.Combine(Paths.Runtimes, component);
            string javaw = Path.Combine(root, "bin", "javaw.exe");
            string stamp = Path.Combine(root, ".lumina-manifest");

            Dictionary<string, object> entry = null;
            try
            {
                var all = J.ParseObj(Http.GetRetry(AllUrl));
                var list = J.A(J.O(all, Platform), component);
                if (list.Count == 0) throw new Exception("Mojang не публикует Java «" + component + "» для Windows x64");
                entry = J.O(list[0]);
            }
            catch (Exception)
            {
                if (File.Exists(javaw)) return javaw;   // офлайн, но Java уже установлена
                throw;
            }

            var man = J.O(entry, "manifest");
            string manSha = J.S(man, "sha1");
            // Если этот же манифест уже полностью установлен и проверен — быстрый выход.
            if (File.Exists(javaw) && File.Exists(stamp) && File.ReadAllText(stamp).Trim() == manSha) return javaw;

            string manFile = Path.Combine(Paths.Runtimes, component + ".manifest.json");
            Downloader.Run(new List<DownloadJob> { new DownloadJob(J.S(man, "url"), manFile, manSha, J.L(man, "size")) }, "Java: манифест");
            var files = J.O(J.ParseObj(File.ReadAllText(manFile, Encoding.UTF8)), "files");
            if (files == null) throw new Exception("Пустой манифест Java");

            var jobs = new List<DownloadJob>();
            foreach (var kv in files)
            {
                var f = J.O(kv.Value);
                string type = J.S(f, "type");
                string path = Path.Combine(root, kv.Key.Replace('/', '\\'));
                if (type == "directory") { Directory.CreateDirectory(path); continue; }
                if (type != "file") continue;   // ссылки (link) на Windows не используются
                var raw = J.O(J.O(f, "downloads"), "raw");
                if (raw == null) continue;
                jobs.Add(new DownloadJob(J.S(raw, "url"), path, J.S(raw, "sha1"), J.L(raw, "size")));
            }
            Downloader.Run(jobs, "Java " + J.S(J.O(entry, "version"), "name"));
            if (!File.Exists(javaw)) throw new Exception("После установки не найден javaw.exe");
            File.WriteAllText(stamp, manSha);
            return javaw;
        }

        // Для Java из настроек: читаем «release» рядом с bin, иначе считаем требуемой версией.
        static int DetectMajor(string javaw, int fallback)
        {
            try
            {
                string rel = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(javaw)), "release");
                if (File.Exists(rel))
                    foreach (var line in File.ReadAllLines(rel))
                        if (line.StartsWith("JAVA_VERSION="))
                        {
                            string v = line.Substring(13).Trim('"');
                            if (v.StartsWith("1.")) v = v.Substring(2);
                            int dot = v.IndexOfAny(new[] { '.', '_', '+', '-' });
                            int m;
                            if (int.TryParse(dot > 0 ? v.Substring(0, dot) : v, out m)) return m;
                        }
            }
            catch { }
            return fallback;
        }
    }
}
