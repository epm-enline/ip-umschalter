// IP-Umschalter — Kernlogik (Profile, Prüfung, netsh-Befehle, Ist-Stand aus der Registry)
// C# 5 (Compiler aus .NET Framework 4.x), daher keine String-Interpolation / ?.-Operatoren.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace IpUmschalter
{
    public class IpEntry
    {
        public string Ip { get; set; }
        public string Mask { get; set; }
        public IpEntry() { Ip = ""; Mask = ""; }
        public IpEntry(string ip, string mask) { Ip = ip; Mask = mask; }
    }

    public class Profile
    {
        public string Name { get; set; }
        public string Adapter { get; set; }
        public bool Dhcp { get; set; }
        public List<IpEntry> Addresses { get; set; }
        public string Gateway { get; set; }
        public List<string> Dns { get; set; }

        public Profile()
        {
            Name = ""; Adapter = ""; Gateway = "";
            Addresses = new List<IpEntry>(); Dns = new List<string>();
        }

        public Profile Clone()
        {
            var p = new Profile();
            p.Name = Name; p.Adapter = Adapter; p.Dhcp = Dhcp; p.Gateway = Gateway;
            p.Addresses = Addresses.Select(a => new IpEntry(a.Ip, a.Mask)).ToList();
            p.Dns = new List<string>(Dns);
            return p;
        }

        internal void FixNulls()
        {
            if (Name == null) Name = "";
            if (Adapter == null) Adapter = "";
            if (Gateway == null) Gateway = "";
            if (Addresses == null) Addresses = new List<IpEntry>();
            if (Dns == null) Dns = new List<string>();
            foreach (var a in Addresses) { if (a.Ip == null) a.Ip = ""; if (a.Mask == null) a.Mask = ""; }
        }
    }

    public static class Store
    {
        public static string DefaultPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profiles.json"); }
        }

        public static List<Profile> Load(string path)
        {
            if (!File.Exists(path)) return new List<Profile>();
            var ser = new JavaScriptSerializer();
            var list = ser.Deserialize<List<Profile>>(File.ReadAllText(path, Encoding.UTF8)) ?? new List<Profile>();
            foreach (var p in list) p.FixNulls();
            return list;
        }

        public static void Save(string path, List<Profile> list)
        {
            var ser = new JavaScriptSerializer();
            string json = Pretty(ser.Serialize(list));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        public static string Pretty(string json)
        {
            var sb = new StringBuilder();
            int ind = 0; bool inStr = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inStr)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < json.Length) sb.Append(json[++i]);
                    else if (c == '"') inStr = false;
                    continue;
                }
                switch (c)
                {
                    case '"': inStr = true; sb.Append(c); break;
                    case '{':
                    case '[':
                        sb.Append(c);
                        if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']')) { sb.Append(json[++i]); break; }
                        ind++; sb.Append("\r\n").Append(' ', ind * 2); break;
                    case '}':
                    case ']':
                        ind--; sb.Append("\r\n").Append(' ', ind * 2).Append(c); break;
                    case ',': sb.Append(",\r\n").Append(' ', ind * 2); break;
                    case ':': sb.Append(": "); break;
                    default: if (!char.IsWhiteSpace(c)) sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }

    public class AdapterConfig
    {
        public bool Found;
        public bool Connected;
        public string Status = "nicht vorhanden";
        public string Description = "";
        public bool Dhcp;
        public List<IpEntry> Addresses = new List<IpEntry>();
        public string Gateway = "";
        public List<string> Dns = new List<string>();        // wirksame DNS (statisch oder per DHCP)
        public List<string> StaticDns = new List<string>();  // fest eingetragene DNS

        public string Describe()
        {
            if (!Found) return "Adapter nicht vorhanden";
            var sb = new StringBuilder();
            sb.Append(Description).Append(" — ").Append(Status).Append("\r\n");
            sb.Append(Dhcp ? "DHCP" : "Statisch").Append(": ");
            sb.Append(Addresses.Count == 0 ? "keine Adresse" : string.Join(", ", Addresses.Select(a => a.Ip + "/" + Net.PrefixOf(a.Mask))));
            sb.Append("\r\nGateway: ").Append(Gateway == "" ? "–" : Gateway);
            sb.Append("   DNS: ").Append(Dns.Count == 0 ? "–" : string.Join(", ", Dns));
            return sb.ToString();
        }
    }

    public class ApplyResult
    {
        public bool Ok;
        public string Message;
        public string Log;
    }

    public static class Net
    {
        public const string DefaultMask = "255.255.255.0";

        public static bool IsIPv4(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            var parts = s.Trim().Split('.');
            if (parts.Length != 4) return false;
            foreach (var p in parts)
            {
                int v;
                if (p.Length == 0 || p.Length > 3 || !p.All(char.IsDigit) || !int.TryParse(p, out v) || v > 255) return false;
            }
            return true;
        }

        public static uint ToUInt(string ip)
        {
            var b = ip.Trim().Split('.').Select(x => uint.Parse(x)).ToArray();
            return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
        }

        public static string FromUInt(uint v)
        {
            return string.Format("{0}.{1}.{2}.{3}", (v >> 24) & 255, (v >> 16) & 255, (v >> 8) & 255, v & 255);
        }

        /// <summary>Maske als "255.255.255.0", "24" oder "/24" → gepunktete Maske, ungültig → null.</summary>
        public static string NormalizeMask(string m)
        {
            if (m == null) return null;
            m = m.Trim();
            if (m.StartsWith("/")) m = m.Substring(1);
            int prefix;
            if (m.Length > 0 && m.Length <= 2 && m.All(char.IsDigit) && int.TryParse(m, out prefix))
            {
                if (prefix < 1 || prefix > 32) return null;
                return FromUInt(prefix == 32 ? 0xFFFFFFFFu : ~(0xFFFFFFFFu >> prefix));
            }
            if (!IsIPv4(m)) return null;
            uint u = ToUInt(m);
            if (u == 0) return null;
            uint inv = ~u;
            if ((inv & (inv + 1)) != 0) return null; // nicht zusammenhängend
            return FromUInt(u);
        }

        public static int PrefixOf(string mask)
        {
            string n = NormalizeMask(mask);
            if (n == null) return 0;
            uint u = ToUInt(n); int c = 0;
            while ((u & 0x80000000u) != 0) { c++; u <<= 1; }
            return c;
        }

        public static bool SameSubnet(string ip, string mask, string other)
        {
            uint m = ToUInt(mask);
            return (ToUInt(ip) & m) == (ToUInt(other) & m);
        }

        /// <summary>Zerlegt "1.2.3.4/24" in IP und Maske; die Präfixangabe hat Vorrang.</summary>
        public static IpEntry ParseEntry(string ip, string mask)
        {
            ip = (ip ?? "").Trim(); mask = (mask ?? "").Trim();
            int slash = ip.IndexOf('/');
            if (slash >= 0) { mask = ip.Substring(slash + 1); ip = ip.Substring(0, slash).Trim(); }
            if (mask == "") mask = DefaultMask;
            string n = NormalizeMask(mask);
            return new IpEntry(ip, n ?? mask);
        }

        public static List<string> SplitList(string s)
        {
            return (s ?? "").Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x != "").ToList();
        }

        public static List<string> Validate(Profile p, IEnumerable<Profile> others)
        {
            var err = new List<string>();
            if (p.Name.Trim() == "") err.Add("Profilname fehlt.");
            else if (others.Any(o => !ReferenceEquals(o, p) && string.Equals(o.Name.Trim(), p.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
                err.Add("Es gibt schon ein Profil „" + p.Name.Trim() + "“.");
            if (p.Adapter.Trim() == "") err.Add("Kein Netzwerkadapter gewählt.");
            if (p.Dhcp) return err;

            if (p.Addresses.Count == 0) err.Add("Mindestens eine IP-Adresse eintragen (oder DHCP wählen).");
            var seen = new HashSet<string>();
            foreach (var a in p.Addresses)
            {
                string label = a.Ip == "" ? "(leer)" : a.Ip;
                if (!IsIPv4(a.Ip)) { err.Add("Ungültige IP-Adresse: " + label); continue; }
                string m = NormalizeMask(a.Mask);
                if (m == null) { err.Add("Ungültige Subnetzmaske bei " + label + ": " + a.Mask); continue; }
                uint ip = ToUInt(a.Ip), mu = ToUInt(m);
                uint first = ip >> 24;
                if (first == 0 || first == 127 || first >= 224) { err.Add("IP-Adresse nicht als Host-Adresse nutzbar: " + label); continue; }
                int prefix = PrefixOf(m);
                if (prefix <= 30 && ((ip & ~mu) == 0 || (ip & ~mu) == ~mu))
                    err.Add(label + "/" + prefix + " ist Netz- oder Broadcast-Adresse.");
                if (!seen.Add(a.Ip.Trim())) err.Add("IP-Adresse doppelt: " + label);
            }
            if (p.Gateway.Trim() != "")
            {
                if (!IsIPv4(p.Gateway)) err.Add("Ungültiges Gateway: " + p.Gateway);
                else if (err.Count == 0 && !p.Addresses.Any(a => SameSubnet(a.Ip, NormalizeMask(a.Mask), p.Gateway)))
                    err.Add("Gateway " + p.Gateway.Trim() + " liegt in keinem Subnetz der IP-Adressen.");
            }
            foreach (var d in p.Dns) if (!IsIPv4(d)) err.Add("Ungültiger DNS-Server: " + d);
            return err;
        }

        static string Q(string s) { return "\"" + s.Trim() + "\""; }

        /// <summary>netsh-Argumente in Ausführungsreihenfolge. Profil muss gültig sein.</summary>
        public static List<string> BuildCommands(Profile p)
        {
            var cmds = new List<string>();
            string n = "name=" + Q(p.Adapter);
            if (p.Dhcp)
            {
                cmds.Add("interface ipv4 set address " + n + " source=dhcp");
                cmds.Add("interface ipv4 set dnsservers " + n + " source=dhcp");
                return cmds;
            }
            for (int i = 0; i < p.Addresses.Count; i++)
            {
                var a = p.Addresses[i];
                string mask = NormalizeMask(a.Mask);
                if (i == 0)
                    cmds.Add("interface ipv4 set address " + n + " source=static address=" + a.Ip.Trim() + " mask=" + mask +
                             " gateway=" + (p.Gateway.Trim() == "" ? "none" : p.Gateway.Trim()));
                else
                    cmds.Add("interface ipv4 add address " + n + " address=" + a.Ip.Trim() + " mask=" + mask);
            }
            if (p.Dns.Count == 0)
                cmds.Add("interface ipv4 set dnsservers " + n + " source=static address=none validate=no");
            for (int i = 0; i < p.Dns.Count; i++)
            {
                if (i == 0) cmds.Add("interface ipv4 set dnsservers " + n + " source=static address=" + p.Dns[0].Trim() + " register=primary validate=no");
                else cmds.Add("interface ipv4 add dnsservers " + n + " address=" + p.Dns[i].Trim() + " index=" + (i + 1) + " validate=no");
            }
            return cmds;
        }

        // ---------- Ist-Stand ----------

        public static List<NetworkInterface> Adapters()
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.Supports(NetworkInterfaceComponent.IPv4))
                .OrderBy(n => n.OperationalStatus == OperationalStatus.Up ? 0 : 1).ThenBy(n => n.Name)
                .ToList();
        }

        static List<string> MultiOrSingle(object v)
        {
            var arr = v as string[];
            if (arr != null) return arr.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
            var s = v as string;
            return SplitList(s);
        }

        public static AdapterConfig ReadConfig(string adapterName)
        {
            var cfg = new AdapterConfig();
            var ni = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(x => string.Equals(x.Name, (adapterName ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            if (ni == null) return cfg;
            cfg.Found = true;
            cfg.Description = ni.Description;
            cfg.Connected = ni.OperationalStatus == OperationalStatus.Up;
            cfg.Status = cfg.Connected ? "verbunden" : "getrennt";
            using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + ni.Id))
            {
                if (k == null) return cfg;
                object d = k.GetValue("EnableDHCP");
                cfg.Dhcp = d is int && (int)d == 1;
                cfg.StaticDns = MultiOrSingle(k.GetValue("NameServer"));
                if (cfg.Dhcp)
                {
                    var ip = MultiOrSingle(k.GetValue("DhcpIPAddress"));
                    var mk = MultiOrSingle(k.GetValue("DhcpSubnetMask"));
                    for (int i = 0; i < ip.Count; i++)
                        if (ip[i] != "0.0.0.0") cfg.Addresses.Add(new IpEntry(ip[i], i < mk.Count ? mk[i] : ""));
                    cfg.Gateway = MultiOrSingle(k.GetValue("DhcpDefaultGateway")).FirstOrDefault() ?? "";
                    cfg.Dns = cfg.StaticDns.Count > 0 ? cfg.StaticDns : MultiOrSingle(k.GetValue("DhcpNameServer"));
                }
                else
                {
                    var ip = MultiOrSingle(k.GetValue("IPAddress"));
                    var mk = MultiOrSingle(k.GetValue("SubnetMask"));
                    for (int i = 0; i < ip.Count; i++)
                        if (ip[i] != "0.0.0.0") cfg.Addresses.Add(new IpEntry(ip[i], i < mk.Count ? mk[i] : ""));
                    cfg.Gateway = MultiOrSingle(k.GetValue("DefaultGateway")).FirstOrDefault() ?? "";
                    cfg.Dns = cfg.StaticDns;
                }
            }
            return cfg;
        }

        /// <summary>Entspricht die Adapter-Einstellung dem Profil?</summary>
        public static bool Matches(Profile p, AdapterConfig c)
        {
            if (!c.Found) return false;
            if (p.Dhcp) return c.Dhcp && c.StaticDns.Count == 0;
            if (c.Dhcp) return false;
            var want = new HashSet<string>(p.Addresses.Select(a => a.Ip.Trim() + "/" + NormalizeMask(a.Mask)));
            var have = new HashSet<string>(c.Addresses.Select(a => a.Ip.Trim() + "/" + NormalizeMask(a.Mask)));
            if (!want.SetEquals(have)) return false;
            if (p.Gateway.Trim() != c.Gateway.Trim()) return false;
            return p.Dns.Select(x => x.Trim()).SequenceEqual(c.StaticDns);
        }

        public static Profile FromConfig(AdapterConfig c, string adapter)
        {
            var p = new Profile();
            p.Adapter = adapter;
            p.Dhcp = c.Dhcp;
            if (!c.Dhcp)
            {
                p.Addresses = c.Addresses.Select(a => new IpEntry(a.Ip, NormalizeMask(a.Mask) ?? a.Mask)).ToList();
                p.Gateway = c.Gateway;
                p.Dns = new List<string>(c.StaticDns);
            }
            return p;
        }

        // ---------- Anwenden ----------

        /// <summary>Grund, warum nicht umgeschaltet werden kann, sonst null.</summary>
        public static string Blocker(Profile p, AdapterConfig c)
        {
            if (!c.Found) return "Adapter „" + p.Adapter + "“ ist nicht vorhanden (USB-Adapter abgesteckt oder umbenannt?).";
            // Gemessen 2026-11-24: an getrennten Adaptern schalten weder netsh noch Set-NetIPInterface noch WMI
            // DHCP ab — die Adressen landen neben aktivem DHCP, der Rückweg auf DHCP räumt sie nicht weg.
            if (!c.Connected) return "Adapter „" + p.Adapter + "“ ist getrennt. Windows übernimmt IP-Einstellungen nur bei " +
                                     "verbundenem Adapter – Kabel anstecken (Gegenseite eingeschaltet) und erneut aktivieren.";
            return null;
        }

        public static ApplyResult Apply(Profile p)
        {
            string block = Blocker(p, ReadConfig(p.Adapter));
            if (block != null) return new ApplyResult { Ok = false, Message = block, Log = "" };
            var log = new StringBuilder();
            bool anyError = false;
            foreach (var cmd in BuildCommands(p))
            {
                int code;
                string output = RunNetsh(cmd, out code);
                log.Append("netsh ").Append(cmd).Append("\r\n");
                if (output != "") log.Append("  → ").Append(output.Replace("\r\n", " ").Trim()).Append("\r\n");
                if (code != 0) { anyError = true; log.Append("  (Exitcode ").Append(code).Append(")\r\n"); }
            }
            // Ergebnis zählt, nicht die netsh-Meldungen (z. B. „Objekt bereits vorhanden“ beim gleichen Gateway).
            var cfg = ReadConfig(p.Adapter);
            var r = new ApplyResult();
            r.Log = log.ToString();
            r.Ok = Matches(p, cfg);
            if (r.Ok) r.Message = "„" + p.Name + "“ ist aktiv (" + p.Adapter + ").";
            else r.Message = "„" + p.Name + "“ wurde nicht vollständig übernommen" + (anyError ? " (netsh meldet Fehler)." : ".") +
                             "\r\n\r\nJetzt am Adapter:\r\n" + cfg.Describe();
            return r;
        }

        static string RunNetsh(string args, out int code)
        {
            var enc = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            var psi = new ProcessStartInfo("netsh.exe", args);
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = enc; psi.StandardErrorEncoding = enc;
            using (var pr = Process.Start(psi))
            {
                string o = pr.StandardOutput.ReadToEnd();
                string e = pr.StandardError.ReadToEnd();
                pr.WaitForExit();
                code = pr.ExitCode;
                return (o + " " + e).Trim();
            }
        }
    }
}
