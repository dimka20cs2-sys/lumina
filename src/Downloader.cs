// Параллельная загрузка с проверкой SHA-1/SHA-512, повторами, докачкой (.part + Range) и прогрессом.
// Уже проверенные файлы запоминаются (размер + время изменения + хеш), чтобы не хешировать их при каждом запуске.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace AkeeLauncher
{
    public class DownloadJob
    {
        public string Url, Path, Hash, Algo = "sha1";
        public long Size;
        public DownloadJob() { }
        public DownloadJob(string url, string path, string sha1, long size) { Url = url; Path = path; Hash = sha1; Size = size; }
    }

    static class Downloader
    {
        public static volatile bool Cancel;

        // Возвращает число реально скачанных файлов.
        public static int Run(List<DownloadJob> input, string label)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var jobs = new List<DownloadJob>();
            long totalBytes = 0;
            foreach (var j in input) if (j != null && j.Path != null && seen.Add(j.Path)) { jobs.Add(j); totalBytes += Math.Max(0, j.Size); }
            if (jobs.Count == 0) return 0;

            var queue = new ConcurrentQueue<DownloadJob>(jobs);
            var errors = new ConcurrentQueue<string>();
            long doneBytes = 0, netBytes = 0;
            int doneFiles = 0, downloaded = 0;
            int threads = Math.Min(Config.Current.concurrency, jobs.Count);
            var workers = new List<Thread>();
            for (int i = 0; i < threads; i++)
            {
                var t = new Thread(() =>
                {
                    DownloadJob j;
                    while (!Cancel && queue.TryDequeue(out j))
                    {
                        try
                        {
                            if (!VerifyCache.IsValid(j.Path, j.Hash, j.Algo, j.Size))
                            {
                                Fetch(j, ref netBytes);
                                Interlocked.Increment(ref downloaded);
                            }
                        }
                        catch (Exception e) { errors.Enqueue(System.IO.Path.GetFileName(j.Path) + ": " + e.Message); }
                        Interlocked.Add(ref doneBytes, Math.Max(0, j.Size));
                        Interlocked.Increment(ref doneFiles);
                    }
                });
                t.IsBackground = true;
                t.Start();
                workers.Add(t);
            }

            // Отчёт о прогрессе каждые 250 мс; скорость — по сетевым байтам за последнюю секунду.
            var sw = Stopwatch.StartNew();
            long lastNet = 0; double lastT = 0, speed = 0;
            while (true)
            {
                bool alive = false;
                foreach (var w in workers) if (w.IsAlive) { alive = true; break; }
                double now = sw.Elapsed.TotalSeconds;
                long net = Interlocked.Read(ref netBytes);
                if (now - lastT >= 0.75) { speed = (net - lastNet) / (now - lastT); lastNet = net; lastT = now; }
                Events.Emit("progress", J.D("label", label, "files", doneFiles, "filesTotal", jobs.Count,
                    "bytes", Interlocked.Read(ref doneBytes), "bytesTotal", totalBytes, "speed", (long)speed, "downloaded", downloaded));
                if (!alive) break;
                Thread.Sleep(250);
            }
            VerifyCache.Save();
            if (Cancel) throw new OperationCanceledException("Отменено");
            if (!errors.IsEmpty)
            {
                string first; errors.TryPeek(out first);
                throw new Exception("Не удалось скачать " + errors.Count + " файл(ов). Первая ошибка: " + first);
            }
            return downloaded;
        }

        static void Fetch(DownloadJob j, ref long netBytes)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(j.Path));
            string tmp = j.Path + ".part";
            Exception last = null;
            for (int attempt = 0; attempt < 5 && !Cancel; attempt++)
            {
                if (attempt > 0) Thread.Sleep(400 * attempt * attempt);
                try
                {
                    long have = File.Exists(tmp) ? new FileInfo(tmp).Length : 0;
                    if (j.Size > 0 && have >= j.Size) { File.Delete(tmp); have = 0; }
                    var r = Http.Request(j.Url, "GET");
                    r.AutomaticDecompression = DecompressionMethods.None;
                    if (have > 0) r.AddRange(have);
                    using (var resp = (HttpWebResponse)r.GetResponse())
                    {
                        bool append = have > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
                        using (var src = resp.GetResponseStream())
                        using (var dst = new FileStream(tmp, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                        {
                            var buf = new byte[1 << 16];
                            int n;
                            while ((n = src.Read(buf, 0, buf.Length)) > 0)
                            {
                                if (Cancel) throw new OperationCanceledException();
                                dst.Write(buf, 0, n);
                                Interlocked.Add(ref netBytes, n);
                            }
                        }
                    }
                    if (!string.IsNullOrEmpty(j.Hash))
                    {
                        string got = Http.FileHash(tmp, j.Algo);
                        if (!string.Equals(got, j.Hash, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(tmp);
                            throw new Exception("хеш не совпал");
                        }
                    }
                    if (File.Exists(j.Path)) File.Delete(j.Path);
                    File.Move(tmp, j.Path);
                    VerifyCache.Remember(j.Path, j.Hash);
                    return;
                }
                catch (WebException e)
                {
                    last = e;
                    var resp = e.Response as HttpWebResponse;
                    if (resp != null && (int)resp.StatusCode == 416) { try { File.Delete(tmp); } catch { } }
                    else if (resp != null && (int)resp.StatusCode == 404) throw new Exception("нет на сервере (404)");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { last = e; }
            }
            throw last ?? new OperationCanceledException();
        }
    }

    // Кэш проверенных файлов: путь → размер|время|хеш.
    static class VerifyCache
    {
        static readonly ConcurrentDictionary<string, string> map = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static string loadedFrom;
        static volatile bool dirty;

        static string FileName { get { return System.IO.Path.Combine(Paths.Cache, "verified.tsv"); } }

        static void EnsureLoaded()
        {
            if (loadedFrom == FileName) return;
            lock (map)
            {
                if (loadedFrom == FileName) return;
                map.Clear();
                try
                {
                    if (File.Exists(FileName))
                        foreach (var line in File.ReadAllLines(FileName, Encoding.UTF8))
                        {
                            int t = line.IndexOf('\t');
                            if (t > 0) map[line.Substring(0, t)] = line.Substring(t + 1);
                        }
                }
                catch { }
                loadedFrom = FileName;
            }
        }

        static string Stamp(FileInfo f, string hash) { return f.Length + "|" + f.LastWriteTimeUtc.Ticks + "|" + (hash ?? "").ToLowerInvariant(); }

        public static bool IsValid(string path, string hash, string algo, long size)
        {
            EnsureLoaded();
            var f = new FileInfo(path);
            if (!f.Exists) return false;
            if (size > 0 && f.Length != size) return false;
            if (string.IsNullOrEmpty(hash)) return size <= 0 || f.Length == size;
            string s;
            if (map.TryGetValue(path, out s) && s == Stamp(f, hash)) return true;
            if (!string.Equals(Http.FileHash(path, algo), hash, StringComparison.OrdinalIgnoreCase)) return false;
            map[path] = Stamp(f, hash);
            dirty = true;
            return true;
        }

        public static void Remember(string path, string hash)
        {
            if (string.IsNullOrEmpty(hash)) return;
            EnsureLoaded();
            map[path] = Stamp(new FileInfo(path), hash);
            dirty = true;
        }

        public static void Save()
        {
            if (!dirty) return;
            lock (map)
            {
                try
                {
                    Directory.CreateDirectory(Paths.Cache);
                    var sb = new StringBuilder();
                    foreach (var kv in map) sb.Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
                    File.WriteAllText(FileName + ".tmp", sb.ToString(), Encoding.UTF8);
                    if (File.Exists(FileName)) File.Delete(FileName);
                    File.Move(FileName + ".tmp", FileName);
                    dirty = false;
                }
                catch { }
            }
        }
    }
}
