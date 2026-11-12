// HTTP-запросы: GET/POST с понятными ошибками (код + тело ответа), хеши файлов.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace AkeeLauncher
{
    public class HttpError : Exception
    {
        public readonly int Status;
        public readonly string Body;
        public HttpError(int status, string body, string url)
            : base("HTTP " + status + " — " + url) { Status = status; Body = body ?? ""; }
    }

    static class Http
    {
        public const string UserAgent = "Lumina-Launcher/1.0";

        public static void Init()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;   // TLS 1.2 / 1.3
            ServicePointManager.DefaultConnectionLimit = 128;
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.UseNagleAlgorithm = false;
        }

        public static HttpWebRequest Request(string url, string method)
        {
            var r = (HttpWebRequest)WebRequest.Create(url);
            r.Method = method;
            r.UserAgent = UserAgent;
            r.Timeout = 30000;
            r.ReadWriteTimeout = 60000;
            r.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            r.KeepAlive = true;
            return r;
        }

        public static string Get(string url) { return Get(url, null); }

        public static string Get(string url, string bearer)
        {
            var r = Request(url, "GET");
            r.Accept = "application/json";
            if (bearer != null) r.Headers[HttpRequestHeader.Authorization] = "Bearer " + bearer;
            return Send(r, null, null);
        }

        // Повтор при сетевых сбоях (не при 4xx).
        public static string GetRetry(string url)
        {
            for (int i = 0; ; i++)
            {
                try { return Get(url); }
                catch (HttpError e) { if (e.Status < 500 || i >= 3) throw; }
                catch (WebException) { if (i >= 3) throw; }
                System.Threading.Thread.Sleep(500 * (i + 1));
            }
        }

        public static string PostForm(string url, Dictionary<string, string> form)
        {
            var sb = new StringBuilder();
            foreach (var kv in form)
            {
                if (sb.Length > 0) sb.Append('&');
                sb.Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(kv.Value ?? ""));
            }
            var r = Request(url, "POST");
            r.Accept = "application/json";
            return Send(r, "application/x-www-form-urlencoded", Encoding.UTF8.GetBytes(sb.ToString()));
        }

        public static string PostJson(string url, object body)
        {
            var r = Request(url, "POST");
            r.Accept = "application/json";
            return Send(r, "application/json", Encoding.UTF8.GetBytes(J.Write(body)));
        }

        // Произвольный метод (PUT/DELETE/POST multipart) с токеном Bearer; тело может быть null.
        public static string SendRaw(string method, string url, string bearer, string contentType, byte[] body)
        {
            var r = Request(url, method);
            r.Accept = "application/json";
            if (bearer != null) r.Headers[HttpRequestHeader.Authorization] = "Bearer " + bearer;
            if (body == null && (method == "PUT" || method == "POST")) body = new byte[0];
            return Send(r, contentType ?? "application/json", body);
        }

        // Двоичный ответ (картинки скинов и т. п.), с ограничением размера.
        public static byte[] GetBytes(string url, int maxBytes)
        {
            var r = Request(url, "GET");
            try
            {
                using (var resp = (HttpWebResponse)r.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var ms = new MemoryStream())
                {
                    var buf = new byte[16384];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        ms.Write(buf, 0, n);
                        if (ms.Length > maxBytes) throw new Exception("Файл слишком большой: " + url);
                    }
                    return ms.ToArray();
                }
            }
            catch (WebException e)
            {
                var resp = e.Response as HttpWebResponse;
                if (resp == null) throw;
                int code = (int)resp.StatusCode;
                resp.Close();
                throw new HttpError(code, "", r.RequestUri.GetLeftPart(UriPartial.Path));
            }
        }

        static string Send(HttpWebRequest r, string contentType, byte[] body)
        {
            try
            {
                if (body != null)
                {
                    r.ContentType = contentType;
                    r.ContentLength = body.Length;
                    using (var s = r.GetRequestStream()) s.Write(body, 0, body.Length);
                }
                using (var resp = (HttpWebResponse)r.GetResponse())
                using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return rd.ReadToEnd();
            }
            catch (WebException e)
            {
                var resp = e.Response as HttpWebResponse;
                if (resp == null) throw;
                string text = "";
                try { using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) text = rd.ReadToEnd(); } catch { }
                int code = (int)resp.StatusCode;
                resp.Close();
                throw new HttpError(code, text, r.RequestUri.GetLeftPart(UriPartial.Path));
            }
        }

        // ----- хеши -----
        public static string FileHash(string path, string algo)
        {
            using (HashAlgorithm h = algo == "sha512" ? (HashAlgorithm)SHA512.Create() : SHA1.Create())
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
                return Hex(h.ComputeHash(f));
        }

        public static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        public static string Q(string s) { return Uri.EscapeDataString(s); }
    }
}
