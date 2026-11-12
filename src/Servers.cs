// «Серверы»: избранные серверы (servers.json в папке настроек), опрос по протоколу Server List Ping
// (рукопожатие + статус JSON + ping/pong), SRV-записи _minecraft._tcp (DnsQuery_W, запасной путь — dns.google),
// импорт списка из servers.dat инсталляции (только чтение).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace AkeeLauncher
{
    static class Servers
    {
        public class Fav { public string id = "", name = "", address = ""; }
        static readonly object gate = new object();

        // ---------- избранное ----------
        static List<Fav> Load()
        {
            try { if (File.Exists(Paths.ServersFile)) return J.Ser.Deserialize<List<Fav>>(File.ReadAllText(Paths.ServersFile, Encoding.UTF8)) ?? new List<Fav>(); }
            catch { }
            return new List<Fav>();
        }

        static void Save(List<Fav> l)
        {
            Directory.CreateDirectory(Paths.ConfigRoot);
            string tmp = Paths.ServersFile + ".tmp";
            File.WriteAllText(tmp, J.Write(l), Encoding.UTF8);
            FileUtil.ReplaceWith(tmp, Paths.ServersFile);
        }

        public static List<Fav> List() { lock (gate) return Load(); }

        public static string NormalizeAddress(string a)
        {
            a = (a ?? "").Trim();
            if (a.StartsWith("minecraft://")) a = a.Substring(12);
            a = a.TrimEnd('/');
            string host; int port;
            Split(a, out host, out port);
            if (host.Length == 0 || host.Length > 253 || !Regex.IsMatch(host, @"^[A-Za-z0-9.\-_:\[\]]+$"))
                throw new Exception("Неверный адрес сервера: «" + a + "». Пример: mc.example.com или 1.2.3.4:25565");
            if (port != 0 && (port < 1 || port > 65535)) throw new Exception("Неверный порт: " + a);
            return a;
        }

        // host[:port]; IPv6 — в квадратных скобках. port = 0, если не указан.
        public static void Split(string a, out string host, out int port)
        {
            port = 0; host = a ?? "";
            if (host.StartsWith("["))
            {
                int e = host.IndexOf(']');
                if (e > 0)
                {
                    string rest = host.Substring(e + 1);
                    host = host.Substring(1, e - 1);
                    if (rest.StartsWith(":")) int.TryParse(rest.Substring(1), out port);
                    if (rest.StartsWith(":") && port == 0) port = -1;
                }
                return;
            }
            int c = host.LastIndexOf(':');
            if (c > 0 && host.IndexOf(':') == c)
            {
                if (!int.TryParse(host.Substring(c + 1), out port)) port = -1;
                host = host.Substring(0, c);
            }
        }

        public static List<Fav> Add(string name, string address)
        {
            address = NormalizeAddress(address);
            name = (name ?? "").Trim();
            if (name.Length == 0) name = address;
            if (name.Length > 60) name = name.Substring(0, 60);
            lock (gate)
            {
                var l = Load();
                if (l.Exists(x => string.Equals(x.address, address, StringComparison.OrdinalIgnoreCase))) throw new Exception("Этот сервер уже в списке");
                l.Add(new Fav { id = Guid.NewGuid().ToString("N").Substring(0, 10), name = name, address = address });
                Save(l);
                return l;
            }
        }

        public static List<Fav> Update(string id, string name, string address)
        {
            lock (gate)
            {
                var l = Load();
                var f = l.Find(x => x.id == id);
                if (f == null) throw new Exception("Сервер не найден");
                if (!string.IsNullOrWhiteSpace(name)) f.name = name.Trim().Length > 60 ? name.Trim().Substring(0, 60) : name.Trim();
                if (!string.IsNullOrWhiteSpace(address)) f.address = NormalizeAddress(address);
                Save(l);
                return l;
            }
        }

        public static List<Fav> Remove(string id)
        {
            lock (gate) { var l = Load(); l.RemoveAll(x => x.id == id); Save(l); return l; }
        }

        public static List<Fav> Move(string id, int delta)
        {
            lock (gate)
            {
                var l = Load();
                int i = l.FindIndex(x => x.id == id), j = i + delta;
                if (i < 0 || j < 0 || j >= l.Count) return l;
                var t = l[i]; l[i] = l[j]; l[j] = t;
                Save(l);
                return l;
            }
        }

        // ---------- servers.dat (NBT без сжатия): servers[] {name, ip} ----------
        public static List<Fav> ReadServersDat(string path)
        {
            var r = new List<Fav>();
            if (!File.Exists(path)) return r;
            var root = Nbt.ReadFile(path);
            foreach (var o in J.A(Nbt.Get(root, "servers")))
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                string ip = Nbt.Get(d, "ip") as string, name = Nbt.Get(d, "name") as string;
                if (string.IsNullOrWhiteSpace(ip)) continue;
                r.Add(new Fav { name = string.IsNullOrWhiteSpace(name) ? ip : name, address = ip.Trim() });
            }
            return r;
        }

        public static Dictionary<string, object> ImportFromInstance(string instId)
        {
            string dat = Path.Combine(Instances.Dir(instId), "servers.dat");
            if (!File.Exists(dat)) throw new Exception("В этой инсталляции ещё нет servers.dat — добавьте сервер в игре или здесь вручную");
            int added = 0, skipped = 0;
            foreach (var s in ReadServersDat(dat))
            {
                bool dup;
                lock (gate) dup = Load().Exists(x => string.Equals(x.address, s.address.Trim(), StringComparison.OrdinalIgnoreCase));
                if (dup) { skipped++; continue; }
                try { Add(s.name, s.address); added++; }
                catch (Exception e) { if (e is IOException || e is UnauthorizedAccessException) throw; skipped++; }   // неверный адрес — пропускаем
            }
            return J.D("added", added, "skipped", skipped, "servers", List());
        }

        // ---------- SRV ----------
        [DllImport("dnsapi.dll", EntryPoint = "DnsQuery_W", CharSet = CharSet.Unicode)]
        static extern int DnsQuery(string name, ushort type, uint options, IntPtr extra, out IntPtr results, IntPtr reserved);
        [DllImport("dnsapi.dll")] static extern void DnsRecordListFree(IntPtr list, int freeType);

        // _minecraft._tcp.<host> → (цель, порт) или null.
        public static string[] ResolveSrv(string host, out string via)
        {
            via = "";
            if (Regex.IsMatch(host, @"^[\d.]+$") || host.Contains(":")) return null;   // IP-адрес — SRV не нужен
            string q = "_minecraft._tcp." + host;
            try
            {
                IntPtr res;
                int rc = DnsQuery(q, 33, 0, IntPtr.Zero, out res, IntPtr.Zero);
                if (rc == 0 && res != IntPtr.Zero)
                {
                    try
                    {
                        for (IntPtr p = res; p != IntPtr.Zero; p = Marshal.ReadIntPtr(p))
                        {
                            int off = IntPtr.Size * 2;
                            ushort type = (ushort)Marshal.ReadInt16(p, off);
                            if (type != 33) continue;
                            int data = IntPtr.Size == 8 ? 32 : 24;   // DNS_RECORD: pNext, pName, wType, wDataLength, Flags, dwTtl, dwReserved, Data
                            string target = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, data));
                            int port = (ushort)Marshal.ReadInt16(p, data + IntPtr.Size + 4);
                            via = "DNS Windows";
                            if (!string.IsNullOrEmpty(target) && port > 0) return new[] { target.TrimEnd('.'), port.ToString() };
                        }
                    }
                    finally { DnsRecordListFree(res, 1); }
                }
                if (rc == 9003 || rc == 9501) return null;   // нет такого имени / нет записей
            }
            catch { }
            // Запасной путь: DNS-over-HTTPS Google.
            try
            {
                var j = J.ParseObj(Http.Get("https://dns.google/resolve?name=" + Uri.EscapeDataString(q) + "&type=SRV"));
                foreach (var a in J.A(j, "Answer"))
                {
                    var d = J.O(a);
                    if (J.L(d, "type") != 33) continue;
                    var parts = (J.S(d, "data") ?? "").Split(' ');   // приоритет вес порт цель
                    if (parts.Length == 4) { via = "dns.google"; return new[] { parts[3].TrimEnd('.'), parts[2] }; }
                }
            }
            catch { }
            return null;
        }

        // ---------- Server List Ping ----------
        public static Dictionary<string, object> Ping(string address)
        {
            address = NormalizeAddress(address);
            string host; int port;
            Split(address, out host, out port);
            string connectHost = host, srvVia = null;
            int connectPort = port > 0 ? port : 25565;
            if (port <= 0)
            {
                string via;
                var srv = ResolveSrv(host, out via);
                if (srv != null) { connectHost = srv[0]; connectPort = int.Parse(srv[1]); srvVia = via; }
            }
            var sw = Stopwatch.StartNew();
            try
            {
                using (var tcp = new TcpClient())
                {
                    var ar = tcp.BeginConnect(connectHost, connectPort, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(5000)) throw new Exception("сервер не отвечает (таймаут подключения)");
                    tcp.EndConnect(ar);
                    tcp.NoDelay = true;
                    long connectMs = sw.ElapsedMilliseconds;
                    var s = tcp.GetStream();
                    s.ReadTimeout = 6000; s.WriteTimeout = 6000;

                    // Handshake: id 0, протокол -1 (статус), адрес, порт, следующее состояние 1.
                    var hs = new MemoryStream();
                    WriteVarInt(hs, 0); WriteVarInt(hs, -1); WriteString(hs, host);
                    hs.WriteByte((byte)(connectPort >> 8)); hs.WriteByte((byte)connectPort); WriteVarInt(hs, 1);
                    SendPacket(s, hs.ToArray());
                    SendPacket(s, new byte[] { 0 });   // Status Request

                    int len = ReadVarInt(s);
                    if (len <= 0 || len > 4 * 1024 * 1024) throw new Exception("неверный ответ сервера");
                    var pkt = ReadExact(s, len);
                    var ps = new MemoryStream(pkt);
                    if (ReadVarInt(ps) != 0) throw new Exception("неожиданный пакет");
                    int slen = ReadVarInt(ps);
                    string json = Encoding.UTF8.GetString(pkt, (int)ps.Position, Math.Min(slen, pkt.Length - (int)ps.Position));

                    // Ping/Pong: замер задержки.
                    long ping = connectMs;
                    try
                    {
                        var pp = new MemoryStream();
                        WriteVarInt(pp, 1);
                        long payload = DateTime.UtcNow.Ticks;
                        for (int i = 7; i >= 0; i--) pp.WriteByte((byte)(payload >> (8 * i)));
                        var t0 = Stopwatch.StartNew();
                        SendPacket(s, pp.ToArray());
                        int plen = ReadVarInt(s);
                        ReadExact(s, plen);
                        ping = t0.ElapsedMilliseconds;
                    }
                    catch { }
                    var r = ParseStatus(json);
                    r["online"] = true; r["ping"] = ping; r["address"] = address;
                    r["resolved"] = connectHost + ":" + connectPort; r["srv"] = srvVia;
                    return r;
                }
            }
            catch (Exception e)
            {
                string msg = e is SocketException ? SockMsg((SocketException)e) : e.Message;
                return J.D("online", false, "address", address, "error", msg, "resolved", connectHost + ":" + connectPort, "srv", srvVia);
            }
        }

        static string SockMsg(SocketException e)
        {
            switch (e.SocketErrorCode)
            {
                case SocketError.HostNotFound: case SocketError.NoData: return "адрес не найден (DNS)";
                case SocketError.ConnectionRefused: return "сервер выключен (соединение отклонено)";
                case SocketError.TimedOut: return "сервер не отвечает";
                default: return "нет соединения: " + e.SocketErrorCode;
            }
        }

        public static Dictionary<string, object> ParseStatus(string json)
        {
            var j = J.ParseObj(json);
            var ver = J.O(j, "version");
            var pl = J.O(j, "players");
            var sample = new List<object>();
            foreach (var o in J.A(pl, "sample")) { string n = J.S(J.O(o), "name"); if (!string.IsNullOrEmpty(n)) sample.Add(n); }
            string fav = J.S(j, "favicon");
            if (fav != null && !fav.StartsWith("data:image/png;base64,")) fav = null;
            object desc;
            j.TryGetValue("description", out desc);
            var segs = new List<object>();
            Motd(desc, new Style(), segs, 0);
            var plain = new StringBuilder();
            foreach (var sg in segs) plain.Append(J.S(J.O(sg), "t"));
            return J.D("version", Regex.Replace(J.S(ver, "name") ?? "", "§.?", "").Trim(), "protocol", J.L(ver, "protocol"),
                "players", J.L(pl, "online"), "max", J.L(pl, "max"), "sample", sample,
                "favicon", fav, "motd", segs, "motdText", plain.ToString());
        }

        // ---------- MOTD: компоненты чата и коды § → сегменты {t, c, b, i, u, s} ----------
        class Style
        {
            public string c; public bool b, i, u, s;
            public Style Copy() { return (Style)MemberwiseClone(); }
        }

        static readonly Dictionary<string, string> Named = new Dictionary<string, string> {
            { "black", "#000000" }, { "dark_blue", "#0000AA" }, { "dark_green", "#00AA00" }, { "dark_aqua", "#00AAAA" },
            { "dark_red", "#AA0000" }, { "dark_purple", "#AA00AA" }, { "gold", "#FFAA00" }, { "gray", "#AAAAAA" },
            { "dark_gray", "#555555" }, { "blue", "#5555FF" }, { "green", "#55FF55" }, { "aqua", "#55FFFF" },
            { "red", "#FF5555" }, { "light_purple", "#FF55FF" }, { "yellow", "#FFFF55" }, { "white", "#FFFFFF" },
        };
        const string Codes = "0123456789abcdef";
        static readonly string[] CodeColors = { "black", "dark_blue", "dark_green", "dark_aqua", "dark_red", "dark_purple", "gold", "gray",
            "dark_gray", "blue", "green", "aqua", "red", "light_purple", "yellow", "white" };

        static void Motd(object node, Style st, List<object> outSegs, int depth)
        {
            if (node == null || depth > 20 || outSegs.Count > 400) return;
            if (node is string) { Legacy((string)node, st, outSegs); return; }
            var d = node as Dictionary<string, object>;
            if (d == null)
            {
                foreach (var x in J.A(node)) Motd(x, st, outSegs, depth + 1);
                return;
            }
            var s = st.Copy();
            string color = J.S(d, "color");
            if (color != null) { string hex; s.c = Named.TryGetValue(color, out hex) ? hex : Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$") ? color : s.c; }
            if (J.Has(d, "bold")) s.b = J.B(d, "bold");
            if (J.Has(d, "italic")) s.i = J.B(d, "italic");
            if (J.Has(d, "underlined")) s.u = J.B(d, "underlined");
            if (J.Has(d, "strikethrough")) s.s = J.B(d, "strikethrough");
            string text = J.S(d, "text");
            if (!string.IsNullOrEmpty(text)) Legacy(text, s, outSegs);
            foreach (var x in J.A(d, "extra")) Motd(x, s, outSegs, depth + 1);
        }

        static void Legacy(string text, Style st, List<object> outSegs)
        {
            var s = st.Copy();
            var cur = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '§' && i + 1 < text.Length)
                {
                    char k = char.ToLowerInvariant(text[++i]);
                    Flush(cur, s, outSegs);
                    int ci = Codes.IndexOf(k);
                    if (ci >= 0) { s = new Style { c = Named[CodeColors[ci]] }; continue; }
                    switch (k)
                    {
                        case 'l': s.b = true; break;
                        case 'o': s.i = true; break;
                        case 'n': s.u = true; break;
                        case 'm': s.s = true; break;
                        case 'r': s = st.Copy(); break;
                    }
                    continue;
                }
                cur.Append(text[i]);
            }
            Flush(cur, s, outSegs);
        }

        static void Flush(StringBuilder cur, Style s, List<object> outSegs)
        {
            if (cur.Length == 0) return;
            outSegs.Add(J.D("t", cur.ToString(), "c", s.c, "b", s.b, "i", s.i, "u", s.u, "s", s.s));
            cur.Clear();
        }

        // ---------- протокол ----------
        static void WriteVarInt(Stream s, int v)
        {
            uint u = (uint)v;
            do
            {
                byte b = (byte)(u & 0x7F);
                u >>= 7;
                if (u != 0) b |= 0x80;
                s.WriteByte(b);
            } while (u != 0);
        }

        static int ReadVarInt(Stream s)
        {
            int v = 0, shift = 0;
            while (true)
            {
                int b = s.ReadByte();
                if (b < 0) throw new Exception("сервер закрыл соединение");
                v |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) return v;
                shift += 7;
                if (shift > 35) throw new Exception("неверный VarInt");
            }
        }

        static void WriteString(Stream s, string str)
        {
            var b = Encoding.UTF8.GetBytes(str);
            WriteVarInt(s, b.Length);
            s.Write(b, 0, b.Length);
        }

        static void SendPacket(Stream s, byte[] body)
        {
            var ms = new MemoryStream();
            WriteVarInt(ms, body.Length);
            ms.Write(body, 0, body.Length);
            var all = ms.ToArray();
            s.Write(all, 0, all.Length);
        }

        static byte[] ReadExact(Stream s, int n)
        {
            var b = new byte[n];
            int off = 0;
            while (off < n)
            {
                int r = s.Read(b, off, n - off);
                if (r <= 0) throw new Exception("ответ сервера оборвался");
                off += r;
            }
            return b;
        }
    }
}
