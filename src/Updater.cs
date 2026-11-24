// IP-Umschalter — Update über GitHub-Releases (öffentliches Repo, keine Anmeldung)
// Release = Tag "vX.Y.Z" mit den Assets IP-Umschalter.exe und IP-Umschalter.exe.sha256 (siehe release.ps1).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace IpUmschalter
{
    public static class Updater
    {
        public const string Repo = "epm-enline/ip-umschalter";
        public const string AssetName = "IP-Umschalter.exe";

        public class Release
        {
            public string Version = "";   // ohne "v"
            public string ExeUrl = "";
            public string ShaUrl = "";
            public string Notes = "";
        }

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
            var wc = new WebClient();
            wc.Headers["User-Agent"] = "IP-Umschalter-Updater";
            wc.Encoding = Encoding.UTF8;
            return wc;
        }

        /// <summary>Neueste Release-Angaben aus der GitHub-API-Antwort (JSON).</summary>
        public static Release Parse(string json)
        {
            var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            var r = new Release();
            r.Version = Convert.ToString(d["tag_name"]).Trim().TrimStart('v', 'V');
            object body;
            if (d.TryGetValue("body", out body) && body != null) r.Notes = Convert.ToString(body);
            object assets;
            if (d.TryGetValue("assets", out assets) && assets is IEnumerable)
                foreach (var o in (IEnumerable)assets)
                {
                    var a = o as Dictionary<string, object>;
                    if (a == null) continue;
                    string name = Convert.ToString(a["name"]), url = Convert.ToString(a["browser_download_url"]);
                    if (name == AssetName) r.ExeUrl = url;
                    else if (name == AssetName + ".sha256") r.ShaUrl = url;
                }
            return r;
        }

        public static Release Latest()
        {
            using (var wc = Client())
            {
                wc.Headers["Accept"] = "application/vnd.github+json";
                return Parse(wc.DownloadString("https://api.github.com/repos/" + Repo + "/releases/latest"));
            }
        }

        public static bool IsNewer(string remote, string local)
        {
            Version a, b;
            if (!System.Version.TryParse(Norm(remote), out a) || !System.Version.TryParse(Norm(local), out b)) return false;
            return a > b;
        }

        static string Norm(string v)
        {
            v = (v ?? "").Trim().TrimStart('v', 'V');
            int dash = v.IndexOf('-'); if (dash >= 0) v = v.Substring(0, dash);
            var parts = v.Split('.').ToList();
            while (parts.Count < 3) parts.Add("0");
            return string.Join(".", parts.Take(4));
        }

        public static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>Lädt die exe nach &lt;exe&gt;.new und prüft Prüfsumme und Assembly-Version. Liefert den Pfad.</summary>
        public static string Download(Release r, string exePath)
        {
            if (r.ExeUrl == "" || r.ShaUrl == "") throw new InvalidOperationException("Release v" + r.Version + " hat keine exe/Prüfsumme.");
            byte[] data; string shaText;
            using (var wc = Client())
            {
                data = wc.DownloadData(r.ExeUrl);
                shaText = wc.DownloadString(r.ShaUrl);
            }
            string expected = shaText.Trim().Split(new[] { ' ', '\t', '\r', '\n', '*' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            string actual = Sha256Hex(data);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Prüfsumme stimmt nicht (erwartet " + expected + ", geladen " + actual + ").");
            string target = exePath + ".new";
            File.WriteAllBytes(target, data);
            Version v = AssemblyName.GetAssemblyName(target).Version;
            if (Norm(v.ToString(3)) != Norm(r.Version))
            {
                File.Delete(target);
                throw new InvalidOperationException("Geladene exe meldet Version " + v.ToString(3) + " statt " + r.Version + ".");
            }
            return target;
        }

        /// <summary>Tauscht die laufende exe aus (Umbenennen geht auch bei laufender exe).</summary>
        public static void Swap(string exePath, string newFile)
        {
            string old = exePath + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(exePath, old);
            try { File.Move(newFile, exePath); }
            catch { File.Move(old, exePath); throw; }
        }

        public static void CleanupOld(string exePath)
        {
            try { if (File.Exists(exePath + ".old")) File.Delete(exePath + ".old"); } catch { }
            try { if (File.Exists(exePath + ".new")) File.Delete(exePath + ".new"); } catch { }
        }
    }
}
