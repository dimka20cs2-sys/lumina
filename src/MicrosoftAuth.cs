// Вход ТОЛЬКО через Microsoft: device code → Xbox Live → XSTS → Minecraft Services → проверка покупки → профиль.
// Офлайн-режима нет: без купленной игры запуск невозможен.
using System;
using System.Collections.Generic;
using System.Threading;

namespace AkeeLauncher
{
    static class MicrosoftAuth
    {
        const string MsBase = "https://login.microsoftonline.com/consumers/oauth2/v2.0/";
        const string Scope = "XboxLive.signin offline_access";
        const string XblUrl = "https://user.auth.xboxlive.com/user/authenticate";
        const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
        public const string McLogin = "https://api.minecraftservices.com/authentication/login_with_xbox";
        const string McEntitlements = "https://api.minecraftservices.com/entitlements/mcstore";
        const string McProfile = "https://api.minecraftservices.com/minecraft/profile";

        static volatile int flow;   // номер текущего входа; новый вход/отмена делает старый недействительным

        static string ClientId()
        {
            if (!Config.AzureConfigured)
                throw new Exception("Не указан Application (client) ID приложения Azure. Откройте «Настройки» и следуйте инструкции AZURE-SETUP.md.");
            return Config.Current.azureClientId;
        }

        // Шаг 1: получить код. Опрос токена идёт в фоне, итог — событие "auth" {state: ok|error}.
        public static Dictionary<string, object> StartDeviceLogin()
        {
            string cid = ClientId();
            Dictionary<string, object> dc;
            try { dc = J.ParseObj(Http.PostForm(MsBase + "devicecode", new Dictionary<string, string> { { "client_id", cid }, { "scope", Scope } })); }
            catch (HttpError e) { throw new Exception(MsError(e)); }
            int my = Interlocked.Increment(ref flow);
            string device = J.S(dc, "device_code");
            int interval = Math.Max(1, (int)J.L(dc, "interval"));
            var until = DateTime.UtcNow.AddSeconds(Math.Max(60, J.L(dc, "expires_in")));
            var t = new Thread(() => Poll(my, cid, device, interval, until)) { IsBackground = true };
            t.Start();
            return J.D("userCode", J.S(dc, "user_code"), "verificationUri", J.S(dc, "verification_uri"),
                "expiresIn", J.L(dc, "expires_in"), "message", J.S(dc, "message"));
        }

        public static void CancelLogin() { Interlocked.Increment(ref flow); }

        static void Poll(int my, string cid, string device, int interval, DateTime until)
        {
            try
            {
                while (my == flow && DateTime.UtcNow < until)
                {
                    Thread.Sleep(interval * 1000);
                    if (my != flow) return;
                    try
                    {
                        var tok = J.ParseObj(Http.PostForm(MsBase + "token", new Dictionary<string, string> {
                            { "grant_type", "urn:ietf:params:oauth:grant-type:device_code" }, { "client_id", cid }, { "device_code", device } }));
                        Events.Emit("auth", J.D("state", "progress", "text", "Проверяем Xbox Live и покупку Minecraft…"));
                        var acc = Complete(J.S(tok, "access_token"), J.S(tok, "refresh_token"), cid);
                        if (my != flow) return;
                        Config.Current.selectedAccount = acc.uuid;
                        Config.Save();
                        Events.Emit("auth", J.D("state", "ok", "name", acc.name, "uuid", acc.uuid, "ownsGame", acc.ownsGame));
                        return;
                    }
                    catch (HttpError e)
                    {
                        string err = J.S(SafeObj(e.Body), "error");
                        if (err == "authorization_pending") continue;
                        if (err == "slow_down") { interval += 5; continue; }
                        if (e.Status == 400 || e.Status == 401) throw new Exception(MsError(e));
                        throw;
                    }
                }
                if (my == flow) throw new Exception("Время на ввод кода истекло. Начните вход заново.");
            }
            catch (Exception ex)
            {
                if (my == flow) Events.Emit("auth", J.D("state", "error", "text", ex.Message));
            }
        }

        // Из токена Microsoft — аккаунт Minecraft. Бросает исключение, если игры нет.
        static Account Complete(string msAccess, string refresh, string cid)
        {
            string uhs;
            string xsts = Xsts(msAccess, out uhs);
            Dictionary<string, object> mc;
            try { mc = J.ParseObj(Http.PostJson(McLogin, J.D("identityToken", "XBL3.0 x=" + uhs + ";" + xsts))); }
            catch (HttpError e)
            {
                if (e.Status == 403 || e.Body.Contains("Invalid app registration"))
                    throw new Exception("Minecraft отклонил приложение Azure (403 «Invalid app registration»): ваш client ID ещё не одобрен Mojang. Подайте заявку по AZURE-SETUP.md (aka.ms/mce-reviewappid) и дождитесь одобрения.");
                throw new Exception("Ошибка входа в Minecraft: HTTP " + e.Status);
            }
            string token = J.S(mc, "access_token");
            long expires = DateTime.UtcNow.AddSeconds(Math.Max(300, J.L(mc, "expires_in"))).Ticks;

            bool owns = false;
            foreach (var it in J.A(J.ParseObj(Http.Get(McEntitlements, token)), "items"))
            {
                string n = J.S(J.O(it), "name");
                if (n == "product_minecraft" || n == "game_minecraft") { owns = true; break; }
            }
            if (!owns) throw new Exception("На этом аккаунте Microsoft нет Minecraft: Java Edition. Игру нужно купить на minecraft.net — без этого запуск невозможен.");

            Dictionary<string, object> prof;
            try { prof = J.ParseObj(Http.Get(McProfile, token)); }
            catch (HttpError e)
            {
                if (e.Status == 404) throw new Exception("Minecraft куплен, но профиль Java ещё не создан. Зайдите на minecraft.net, выберите ник и попробуйте снова.");
                throw;
            }
            var acc = new Account
            {
                uuid = J.S(prof, "id"), name = J.S(prof, "name"), refreshToken = refresh ?? "",
                mcToken = token, mcTokenExpires = expires, ownsGame = true, clientId = cid,
            };
            AccountStore.Upsert(acc);
            return acc;
        }

        static string Xsts(string msAccess, out string uhs)
        {
            Dictionary<string, object> xbl;
            try
            {
                xbl = J.ParseObj(Http.PostJson(XblUrl, J.D(
                    "Properties", J.D("AuthMethod", "RPS", "SiteName", "user.auth.xboxlive.com", "RpsTicket", "d=" + msAccess),
                    "RelyingParty", "http://auth.xboxlive.com", "TokenType", "JWT")));
            }
            catch (HttpError e) { throw new Exception("Xbox Live отклонил вход (HTTP " + e.Status + ")."); }
            try
            {
                var x = J.ParseObj(Http.PostJson(XstsUrl, J.D(
                    "Properties", J.D("SandboxId", "RETAIL", "UserTokens", new[] { J.S(xbl, "Token") }),
                    "RelyingParty", "rp://api.minecraftservices.com/", "TokenType", "JWT")));
                uhs = J.S(J.O(J.A(J.O(J.O(x, "DisplayClaims")), "xui")[0]), "uhs");
                return J.S(x, "Token");
            }
            catch (HttpError e) { throw new Exception(XErr(J.L(SafeObj(e.Body), "XErr"), e.Status)); }
        }

        static string XErr(long code, int status)
        {
            switch (code)
            {
                case 2148916233: return "У этого аккаунта Microsoft нет профиля Xbox. Создайте его на xbox.com (бесплатно) и войдите снова.";
                case 2148916235: return "Xbox Live недоступен в стране аккаунта.";
                case 2148916236: case 2148916237: return "Аккаунт требует подтверждения возраста на xbox.com.";
                case 2148916238: return "Детский аккаунт: взрослый должен добавить его в семью Microsoft (account.microsoft.com/family).";
                default: return "XSTS отклонил вход (HTTP " + status + (code != 0 ? ", XErr " + code : "") + ").";
            }
        }

        static string MsError(HttpError e)
        {
            var o = SafeObj(e.Body);
            string err = J.S(o, "error") ?? "", desc = J.S(o, "error_description") ?? "";
            if (desc.Contains("AADSTS700016") || desc.Contains("AADSTS700038")) return "Приложение с таким client ID не найдено в Azure. Проверьте ID в настройках.";
            if (desc.Contains("AADSTS7000218") || desc.Contains("AADSTS70002") || err == "unauthorized_client")
                return "В приложении Azure не включены «public client flows» (Allow public client flows = Yes). См. AZURE-SETUP.md.";
            if (desc.Contains("AADSTS9002346") || desc.Contains("AADSTS50020"))
                return "Приложение Azure должно поддерживать личные аккаунты Microsoft («Personal Microsoft accounts only»).";
            if (err == "invalid_grant") return "Вход устарел. Войдите в аккаунт заново.";
            if (err == "expired_token") return "Время на ввод кода истекло. Начните вход заново.";
            if (err == "authorization_declined") return "Вход отменён в браузере.";
            return "Microsoft: " + (err.Length > 0 ? err : "HTTP " + e.Status) + (desc.Length > 0 ? " — " + desc.Split('\r', '\n')[0] : "");
        }

        static Dictionary<string, object> SafeObj(string s) { try { return J.ParseObj(s); } catch { return null; } }

        // Перед запуском: свежий токен Minecraft (обновление через refresh token) и подтверждённая покупка.
        public static AuthSession Session(Account acc)
        {
            if (acc == null) throw new Exception("Войдите в аккаунт Microsoft (вкладка «Аккаунт»). Играть можно только с купленным Minecraft.");
            if (acc.ownsGame && !string.IsNullOrEmpty(acc.mcToken) && acc.mcTokenExpires > DateTime.UtcNow.AddMinutes(10).Ticks)
                return new AuthSession { Name = acc.name, Uuid = acc.uuid, AccessToken = acc.mcToken };
            if (string.IsNullOrEmpty(acc.refreshToken)) throw new Exception("Вход устарел. Войдите в аккаунт заново.");
            string cid = string.IsNullOrEmpty(acc.clientId) ? ClientId() : acc.clientId;
            Dictionary<string, object> tok;
            try
            {
                tok = J.ParseObj(Http.PostForm(MsBase + "token", new Dictionary<string, string> {
                    { "grant_type", "refresh_token" }, { "client_id", cid }, { "refresh_token", acc.refreshToken }, { "scope", Scope } }));
            }
            catch (HttpError e) { throw new Exception(MsError(e)); }
            var fresh = Complete(J.S(tok, "access_token"), J.S(tok, "refresh_token") ?? acc.refreshToken, cid);
            return new AuthSession { Name = fresh.name, Uuid = fresh.uuid, AccessToken = fresh.mcToken };
        }

        // Самотест: проверка формата запросов без учётных данных (ожидаются ошибки сервера, вход не выполняется).
        public static string ProbeEndpoints()
        {
            var sb = new System.Text.StringBuilder();
            try { Http.PostForm(MsBase + "devicecode", new Dictionary<string, string> { { "client_id", Guid.NewGuid().ToString() }, { "scope", Scope } }); sb.Append("devicecode: неожиданно OK\n"); }
            catch (HttpError e) { sb.Append("devicecode (случайный client ID) → HTTP " + e.Status + ": " + MsError(e) + "\n"); }
            try { Http.PostJson(McLogin, J.D("identityToken", "XBL3.0 x=0;invalid")); sb.Append("login_with_xbox: неожиданно OK\n"); }
            catch (HttpError e) { sb.Append("login_with_xbox (фиктивный токен) → HTTP " + e.Status + "\n"); }
            try { Http.Get(McEntitlements, "invalid"); sb.Append("entitlements: неожиданно OK\n"); }
            catch (HttpError e) { sb.Append("entitlements (без токена) → HTTP " + e.Status + "\n"); }
            return sb.ToString();
        }
    }
}
