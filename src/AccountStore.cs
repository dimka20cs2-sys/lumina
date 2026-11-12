// Аккаунты Microsoft: хранятся в accounts.dat, зашифрованные DPAPI (ProtectedData, CurrentUser) — расшифровать может только этот пользователь Windows.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AkeeLauncher
{
    public class Account
    {
        public string uuid = "";
        public string name = "";
        public string refreshToken = "";
        public string mcToken = "";
        public long mcTokenExpires = 0;    // UTC ticks
        public bool ownsGame = false;
        public string clientId = "";       // каким приложением Azure получен refresh token
    }

    static class AccountStore
    {
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Lumina/accounts/v1");
        static readonly object gate = new object();

        public static List<Account> Load()
        {
            lock (gate)
            {
                try
                {
                    if (!File.Exists(Paths.AccountsFile)) return new List<Account>();
                    byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(Paths.AccountsFile), Entropy, DataProtectionScope.CurrentUser);
                    return J.Ser.Deserialize<List<Account>>(Encoding.UTF8.GetString(plain)) ?? new List<Account>();
                }
                catch { return new List<Account>(); }
            }
        }

        static void Save(List<Account> list)
        {
            lock (gate)
            {
                Directory.CreateDirectory(Paths.ConfigRoot);
                byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(J.Write(list)), Entropy, DataProtectionScope.CurrentUser);
                string tmp = Paths.AccountsFile + ".tmp";
                File.WriteAllBytes(tmp, enc);
                if (File.Exists(Paths.AccountsFile)) File.Delete(Paths.AccountsFile);
                File.Move(tmp, Paths.AccountsFile);
            }
        }

        public static Account Find(string uuid)
        {
            foreach (var a in Load()) if (a.uuid == uuid) return a;
            return null;
        }

        public static void Upsert(Account acc)
        {
            lock (gate)
            {
                var list = Load();
                list.RemoveAll(a => a.uuid == acc.uuid);
                list.Insert(0, acc);
                Save(list);
            }
        }

        public static void Remove(string uuid)
        {
            lock (gate)
            {
                var list = Load();
                list.RemoveAll(a => a.uuid == uuid);
                Save(list);
            }
        }

        // Для интерфейса — без токенов.
        public static List<object> Public()
        {
            var r = new List<object>();
            foreach (var a in Load())
                r.Add(J.D("uuid", a.uuid, "name", a.name, "ownsGame", a.ownsGame,
                    "selected", a.uuid == Config.Current.selectedAccount));
            return r;
        }

        public static Account Selected()
        {
            var list = Load();
            foreach (var a in list) if (a.uuid == Config.Current.selectedAccount) return a;
            return list.Count > 0 ? list[0] : null;
        }
    }
}
