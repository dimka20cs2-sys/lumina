// Минимальное чтение NBT (Named Binary Tag) Minecraft: level.dat (gzip) и servers.dat (без сжатия). Только чтение.
// Compound → Dictionary<string,object>, List → List<object>, числа → int/long/short/double/float, массивы → int[]/long[]/byte[].
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AkeeLauncher
{
    static class Nbt
    {
        public static Dictionary<string, object> ReadFile(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            return Read(data);
        }

        public static Dictionary<string, object> Read(byte[] data)
        {
            Stream s = new MemoryStream(data);
            if (data.Length > 2 && data[0] == 0x1f && data[1] == 0x8b) s = new GZipStream(s, CompressionMode.Decompress);
            else if (data.Length > 2 && data[0] == 0x78) { s.ReadByte(); s.ReadByte(); s = new DeflateStream(s, CompressionMode.Decompress); }
            using (s)
            {
                var r = new BinaryReader(s);
                int type = r.ReadByte();
                if (type != 10) throw new Exception("NBT: корень не compound");
                Str(r);   // имя корня
                return (Dictionary<string, object>)Payload(r, 10, 0);
            }
        }

        static object Payload(BinaryReader r, int type, int depth)
        {
            if (depth > 64) throw new Exception("NBT: слишком глубокая вложенность");
            switch (type)
            {
                case 1: return (int)(sbyte)r.ReadByte();
                case 2: return (short)BE(r, 2);
                case 3: return (int)BE(r, 4);
                case 4: return BE(r, 8);
                case 5: return BitConverter.ToSingle(BitConverter.GetBytes((int)BE(r, 4)), 0);
                case 6: return BitConverter.Int64BitsToDouble(BE(r, 8));
                case 7: { int n = Len(r); return r.ReadBytes(n); }
                case 8: return Str(r);
                case 9:
                    {
                        int et = r.ReadByte();
                        int n = Len(r);
                        var list = new List<object>(Math.Min(n, 4096));
                        for (int i = 0; i < n; i++) list.Add(Payload(r, et, depth + 1));
                        return list;
                    }
                case 10:
                    {
                        var d = new Dictionary<string, object>();
                        while (true)
                        {
                            int t = r.ReadByte();
                            if (t == 0) break;
                            string name = Str(r);
                            d[name] = Payload(r, t, depth + 1);
                        }
                        return d;
                    }
                case 11: { int n = Len(r); var a = new int[n]; for (int i = 0; i < n; i++) a[i] = (int)BE(r, 4); return a; }
                case 12: { int n = Len(r); var a = new long[n]; for (int i = 0; i < n; i++) a[i] = BE(r, 8); return a; }
                default: throw new Exception("NBT: неизвестный тег " + type);
            }
        }

        static int Len(BinaryReader r)
        {
            int n = (int)BE(r, 4);
            if (n < 0 || n > 64 * 1024 * 1024) throw new Exception("NBT: неверная длина");
            return n;
        }

        static long BE(BinaryReader r, int bytes)
        {
            byte[] b = r.ReadBytes(bytes);
            if (b.Length != bytes) throw new EndOfStreamException();
            long v = 0;
            foreach (byte x in b) v = (v << 8) | x;
            if (bytes == 4) return (int)v;
            if (bytes == 2) return (short)v;
            return v;
        }

        static string Str(BinaryReader r)
        {
            int n = (int)(BE(r, 2) & 0xFFFF);
            return Encoding.UTF8.GetString(r.ReadBytes(n));   // «модифицированный UTF-8» Java совпадает для обычных символов
        }

        // ---------- запись (только для самотеста: фальшивые level.dat / servers.dat) ----------
        public static byte[] Write(Dictionary<string, object> root, bool gzip)
        {
            var ms = new MemoryStream();
            using (var outS = gzip ? (Stream)new GZipStream(ms, CompressionMode.Compress, true) : new NonClosing(ms))
            {
                var w = new BinaryWriter(outS);
                w.Write((byte)10); WStr(w, "");
                WPayload(w, root);
                w.Flush();
            }
            return ms.ToArray();
        }

        static int TypeOf(object v)
        {
            if (v is Dictionary<string, object>) return 10;
            if (v is List<object>) return 9;
            if (v is string) return 8;
            if (v is long) return 4;
            if (v is int) return 3;
            if (v is short) return 2;
            if (v is double) return 6;
            if (v is byte) return 1;
            throw new Exception("NBT: тип не поддерживается при записи");
        }

        static void WPayload(BinaryWriter w, object v)
        {
            switch (TypeOf(v))
            {
                case 10:
                    foreach (var kv in (Dictionary<string, object>)v) { w.Write((byte)TypeOf(kv.Value)); WStr(w, kv.Key); WPayload(w, kv.Value); }
                    w.Write((byte)0); break;
                case 9:
                    {
                        var l = (List<object>)v;
                        w.Write((byte)(l.Count > 0 ? TypeOf(l[0]) : 0)); WBE(w, l.Count, 4);
                        foreach (var x in l) WPayload(w, x);
                        break;
                    }
                case 8: WStr(w, (string)v); break;
                case 4: WBE(w, (long)v, 8); break;
                case 3: WBE(w, (int)v, 4); break;
                case 2: WBE(w, (short)v, 2); break;
                case 6: WBE(w, BitConverter.DoubleToInt64Bits((double)v), 8); break;
                case 1: w.Write((byte)v); break;
            }
        }

        static void WBE(BinaryWriter w, long v, int bytes) { for (int i = bytes - 1; i >= 0; i--) w.Write((byte)(v >> (8 * i))); }
        static void WStr(BinaryWriter w, string s) { var b = Encoding.UTF8.GetBytes(s); WBE(w, b.Length, 2); w.Write(b); }

        class NonClosing : Stream
        {
            readonly Stream s;
            public NonClosing(Stream s) { this.s = s; }
            public override bool CanRead { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { return s.Length; } }
            public override long Position { get { return s.Position; } set { s.Position = value; } }
            public override void Flush() { s.Flush(); }
            public override int Read(byte[] b, int o, int c) { throw new NotSupportedException(); }
            public override long Seek(long o, SeekOrigin so) { throw new NotSupportedException(); }
            public override void SetLength(long v) { throw new NotSupportedException(); }
            public override void Write(byte[] b, int o, int c) { s.Write(b, o, c); }
        }

        // Удобный доступ по пути: Get(root, "Data", "LevelName")
        public static object Get(Dictionary<string, object> d, params string[] path)
        {
            object cur = d;
            foreach (var k in path)
            {
                var c = cur as Dictionary<string, object>;
                if (c == null || !c.TryGetValue(k, out cur)) return null;
            }
            return cur;
        }
    }
}
