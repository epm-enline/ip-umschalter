// IP-Umschalter — Selbsttest der Kernlogik (ohne Adminrechte, ändert nichts am System)
// Aufruf: IP-Umschalter.exe --selftest <ausgabe.txt>   → Exitcode 0 = alles grün
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace IpUmschalter
{
    static class SelfTest
    {
        static int pass, fail;
        static StringBuilder sb = new StringBuilder();

        static void Eq(string name, object expected, object actual)
        {
            bool ok = Equals(expected, actual) || (expected != null && actual != null && expected.ToString() == actual.ToString());
            if (ok) pass++; else fail++;
            sb.Append(ok ? "PASS " : "FAIL ").Append(name);
            if (!ok) sb.Append("  erwartet: [").Append(expected).Append("]  ist: [").Append(actual).Append("]");
            sb.Append("\r\n");
        }

        static Profile Static(string name, string adapter, string gw, string dns, params string[] ipmask)
        {
            var p = new Profile { Name = name, Adapter = adapter, Gateway = gw };
            for (int i = 0; i < ipmask.Length; i += 2) p.Addresses.Add(new IpEntry(ipmask[i], ipmask[i + 1]));
            p.Dns = Net.SplitList(dns);
            return p;
        }

        static bool IsXml(string s)
        {
            try { var d = new System.Xml.XmlDocument(); d.LoadXml(s.Substring(s.IndexOf("<Task"))); return true; } catch { return false; }
        }

        static string Errs(Profile p, params Profile[] others)
        {
            return string.Join(" | ", Net.Validate(p, others));
        }

        public static int Run(string outFile)
        {
            // Masken
            Eq("Maske 24", "255.255.255.0", Net.NormalizeMask("24"));
            Eq("Maske /16", "255.255.0.0", Net.NormalizeMask("/16"));
            Eq("Maske 32", "255.255.255.255", Net.NormalizeMask("32"));
            Eq("Maske 1", "128.0.0.0", Net.NormalizeMask("1"));
            Eq("Maske gepunktet", "255.255.255.128", Net.NormalizeMask(" 255.255.255.128 "));
            Eq("Maske 0 ungültig", null, Net.NormalizeMask("0"));
            Eq("Maske 33 ungültig", null, Net.NormalizeMask("33"));
            Eq("Maske lückenhaft ungültig", null, Net.NormalizeMask("255.0.255.0"));
            Eq("Maske 0.0.0.0 ungültig", null, Net.NormalizeMask("0.0.0.0"));
            Eq("Präfix von 255.255.252.0", 22, Net.PrefixOf("255.255.252.0"));

            // IP-Format
            Eq("IP gültig", true, Net.IsIPv4("192.168.1.10"));
            Eq("IP 256 ungültig", false, Net.IsIPv4("192.168.1.256"));
            Eq("IP 3 Teile ungültig", false, Net.IsIPv4("192.168.1"));
            Eq("IP Text ungültig", false, Net.IsIPv4("abc.d.e.f"));
            Eq("IP leer ungültig", false, Net.IsIPv4(""));

            // Eingabe "ip/prefix"
            var e1 = Net.ParseEntry("10.0.0.5/8", "255.255.255.0");
            Eq("ParseEntry ip/8 → IP", "10.0.0.5", e1.Ip);
            Eq("ParseEntry ip/8 → Maske hat Vorrang", "255.0.0.0", e1.Mask);
            Eq("ParseEntry ohne Maske → Standard", "255.255.255.0", Net.ParseEntry("10.0.0.5", "").Mask);
            Eq("ParseEntry Präfix in Maskenspalte", "255.255.0.0", Net.ParseEntry("10.0.0.5", "16").Mask);
            Eq("SplitList DNS", "1.1.1.1|8.8.8.8|9.9.9.9", string.Join("|", Net.SplitList("1.1.1.1, 8.8.8.8;9.9.9.9")));

            // Prüfung
            var ok = Static("Anlage", "Ethernet 2", "192.168.1.1", "1.1.1.1", "192.168.1.10", "255.255.255.0", "10.0.0.10", "255.0.0.0");
            Eq("Gültiges Profil ohne Fehler", "", Errs(ok));
            Eq("Name doppelt (Groß/Klein)", "Es gibt schon ein Profil „Anlage“.", Errs(ok, Static("ANLAGE", "x", "", "", "1.2.3.4", "24")));
            Eq("Statisch ohne IP", "Mindestens eine IP-Adresse eintragen (oder DHCP wählen).", Errs(Static("A", "E", "", "")));
            Eq("Ungültige IP", "Ungültige IP-Adresse: 192.168.1.300", Errs(Static("A", "E", "", "", "192.168.1.300", "24")));
            Eq("Ungültige Maske", "Ungültige Subnetzmaske bei 192.168.1.3: 255.0.255.0", Errs(Static("A", "E", "", "", "192.168.1.3", "255.0.255.0")));
            Eq("Netzadresse", "192.168.1.0/24 ist Netz- oder Broadcast-Adresse.", Errs(Static("A", "E", "", "", "192.168.1.0", "24")));
            Eq("Broadcast", "192.168.1.255/24 ist Netz- oder Broadcast-Adresse.", Errs(Static("A", "E", "", "", "192.168.1.255", "24")));
            Eq("/31 erlaubt Randadressen", "", Errs(Static("A", "E", "", "", "10.0.0.0", "31")));
            Eq("Loopback abgelehnt", "IP-Adresse nicht als Host-Adresse nutzbar: 127.0.0.2", Errs(Static("A", "E", "", "", "127.0.0.2", "8")));
            Eq("IP doppelt", "IP-Adresse doppelt: 10.1.1.1", Errs(Static("A", "E", "", "", "10.1.1.1", "24", "10.1.1.1", "16")));
            Eq("Gateway ungültig", "Ungültiges Gateway: 10.1.1", Errs(Static("A", "E", "10.1.1", "", "10.1.1.5", "24")));
            Eq("Gateway außerhalb", "Gateway 10.2.0.1 liegt in keinem Subnetz der IP-Adressen.", Errs(Static("A", "E", "10.2.0.1", "", "10.1.1.5", "24")));
            Eq("Gateway im 2. Subnetz ok", "", Errs(Static("A", "E", "10.2.0.1", "", "10.1.1.5", "24", "10.2.0.5", "16")));
            Eq("DNS ungültig", "Ungültiger DNS-Server: 8.8.8", Errs(Static("A", "E", "", "8.8.8", "10.1.1.5", "24")));
            Eq("Adapter fehlt", "Kein Netzwerkadapter gewählt.", Errs(Static("A", " ", "", "", "10.1.1.5", "24")));
            Eq("DHCP braucht keine IP", "", Errs(new Profile { Name = "D", Adapter = "E", Dhcp = true }));

            // netsh-Befehle
            var c1 = Net.BuildCommands(ok);
            Eq("Befehle statisch: Anzahl", 3, c1.Count);
            Eq("Befehl 1 set address mit Gateway", "interface ipv4 set address name=\"Ethernet 2\" source=static address=192.168.1.10 mask=255.255.255.0 gateway=192.168.1.1", c1[0]);
            Eq("Befehl 2 add address", "interface ipv4 add address name=\"Ethernet 2\" address=10.0.0.10 mask=255.0.0.0", c1[1]);
            Eq("Befehl 3 DNS primär", "interface ipv4 set dnsservers name=\"Ethernet 2\" source=static address=1.1.1.1 register=primary validate=no", c1[2]);
            var c2 = Net.BuildCommands(Static("A", "LAN", "", "", "192.168.0.2", "/24"));
            Eq("Ohne Gateway → gateway=none", "interface ipv4 set address name=\"LAN\" source=static address=192.168.0.2 mask=255.255.255.0 gateway=none", c2[0]);
            Eq("Ohne DNS → DNS leeren", "interface ipv4 set dnsservers name=\"LAN\" source=static address=none validate=no", c2[1]);
            var c3 = Net.BuildCommands(Static("A", "LAN", "", "1.1.1.1,8.8.8.8,9.9.9.9", "192.168.0.2", "24"));
            Eq("DNS 2 → index=2", "interface ipv4 add dnsservers name=\"LAN\" address=8.8.8.8 index=2 validate=no", c3[2]);
            Eq("DNS 3 → index=3", "interface ipv4 add dnsservers name=\"LAN\" address=9.9.9.9 index=3 validate=no", c3[3]);
            var c4 = Net.BuildCommands(new Profile { Name = "D", Adapter = "WLAN", Dhcp = true });
            Eq("DHCP Befehle", "interface ipv4 set address name=\"WLAN\" source=dhcp || interface ipv4 set dnsservers name=\"WLAN\" source=dhcp", string.Join(" || ", c4));

            // Abgleich Profil ↔ Adapter-Stand
            var cfg = new AdapterConfig { Found = true, Dhcp = false, Gateway = "192.168.1.1" };
            cfg.Addresses.Add(new IpEntry("10.0.0.10", "255.0.0.0"));
            cfg.Addresses.Add(new IpEntry("192.168.1.10", "255.255.255.0"));
            cfg.StaticDns.Add("1.1.1.1");
            Eq("Matches: gleiche Adressen, andere Reihenfolge", true, Net.Matches(ok, cfg));
            var okNoDns = ok.Clone(); okNoDns.Dns.Clear();
            Eq("Matches: DNS weicht ab", false, Net.Matches(okNoDns, cfg));
            var okOther = ok.Clone(); okOther.Addresses[1].Mask = "255.255.0.0";
            Eq("Matches: Maske weicht ab", false, Net.Matches(okOther, cfg));
            var okLess = ok.Clone(); okLess.Addresses.RemoveAt(1);
            Eq("Matches: zusätzliche Adresse am Adapter", false, Net.Matches(okLess, cfg));
            Eq("Matches: DHCP-Profil gegen statisch", false, Net.Matches(new Profile { Name = "D", Adapter = "E", Dhcp = true }, cfg));
            var dcfg = new AdapterConfig { Found = true, Dhcp = true };
            Eq("Matches: DHCP-Profil gegen DHCP", true, Net.Matches(new Profile { Name = "D", Adapter = "E", Dhcp = true }, dcfg));
            dcfg.StaticDns.Add("1.1.1.1");
            Eq("Matches: DHCP mit fest eingetragenem DNS ≠ DHCP-Profil", false, Net.Matches(new Profile { Name = "D", Adapter = "E", Dhcp = true }, dcfg));
            Eq("Matches: Adapter fehlt", false, Net.Matches(ok, new AdapterConfig()));

            // Speichern/Laden
            string tmp = Path.Combine(Path.GetTempPath(), "ipumschalter-selftest-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var list = new List<Profile> { ok, new Profile { Name = "Büro „DHCP“ \\ Ä", Adapter = "LAN-Verbindung", Dhcp = true } };
                Store.Save(tmp, list);
                Store.Save(tmp, list); // zweites Speichern über vorhandene Datei (File.Replace)
                var back = Store.Load(tmp);
                Eq("JSON: Anzahl", 2, back.Count);
                Eq("JSON: Sonderzeichen im Namen", "Büro „DHCP“ \\ Ä", back[1].Name);
                Eq("JSON: Adressen", "192.168.1.10/255.255.255.0,10.0.0.10/255.0.0.0", string.Join(",", back[0].Addresses.Select(a => a.Ip + "/" + a.Mask)));
                Eq("JSON: DNS", "1.1.1.1", string.Join(",", back[0].Dns));
                Eq("JSON: DHCP", true, back[1].Dhcp);
                Eq("JSON: Datei lesbar eingerückt", true, File.ReadAllText(tmp).Contains("\r\n    \"Name\": \"Anlage\""));
                File.WriteAllText(tmp, "[{\"Name\":\"alt\",\"Adapter\":\"E\"}]");
                var partial = Store.Load(tmp);
                Eq("JSON: fehlende Felder → leer statt null", "0|0|", partial[0].Addresses.Count + "|" + partial[0].Dns.Count + "|" + partial[0].Gateway);
            }
            finally { try { File.Delete(tmp); } catch { } }

            // Ist-Stand lesen (nur lesend)
            var real = Net.Adapters();
            Eq("Adapterliste nicht leer", true, real.Count > 0);
            if (real.Count > 0)
            {
                var rc = Net.ReadConfig(real[0].Name);
                Eq("ReadConfig findet " + real[0].Name, true, rc.Found);
                sb.Append("INFO ").Append(real[0].Name).Append(": ").Append(rc.Describe().Replace("\r\n", " / ")).Append("\r\n");
                var fromCfg = Net.FromConfig(rc, real[0].Name); fromCfg.Name = "x";
                Eq("FromConfig(Ist) passt zu Ist", true, Net.Matches(fromCfg, rc));
            }
            Eq("ReadConfig unbekannter Adapter", false, Net.ReadConfig("gibt-es-nicht-4711").Found);

            // Sperre vor dem Umschalten
            Eq("Blocker: verbunden → frei", null, Net.Blocker(ok, new AdapterConfig { Found = true, Connected = true }));
            Eq("Blocker: getrennt", true, (Net.Blocker(ok, new AdapterConfig { Found = true, Connected = false }) ?? "").Contains("ist getrennt"));
            Eq("Blocker: fehlt", true, (Net.Blocker(ok, new AdapterConfig()) ?? "").Contains("nicht vorhanden"));
            var down = real.FirstOrDefault(n => n.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up);
            if (down != null)
            {
                // Echter getrennter Adapter: Apply muss vor jedem netsh-Aufruf abbrechen (läuft ohne Adminrechte).
                var pd = Static("x", down.Name, "", "", "10.253.1.10", "24");
                var before = Net.ReadConfig(down.Name).Describe();
                var rd = Net.Apply(pd);
                Eq("Apply an getrenntem „" + down.Name + "“ abgelehnt", "False|", rd.Ok + "|" + rd.Log);
                Eq("Apply an getrenntem Adapter ändert nichts", before, Net.ReadConfig(down.Name).Describe());
            }

            // Update
            Eq("IsNewer 1.2.0 > 1.1.0", true, Updater.IsNewer("v1.2.0", "1.1.0"));
            Eq("IsNewer 1.10.0 > 1.9.0 (numerisch)", true, Updater.IsNewer("1.10.0", "1.9.0"));
            Eq("IsNewer gleich", false, Updater.IsNewer("v1.1.0", "1.1.0"));
            Eq("IsNewer älter", false, Updater.IsNewer("1.0.9", "1.1.0"));
            Eq("IsNewer 1.1 = 1.1.0", false, Updater.IsNewer("1.1", "1.1.0"));
            Eq("IsNewer Unsinn", false, Updater.IsNewer("latest", "1.1.0"));
            var rel = Updater.Parse("{\"tag_name\":\"v1.2.3\",\"body\":\"Neu\",\"assets\":[" +
                "{\"name\":\"IP-Umschalter.exe.sha256\",\"browser_download_url\":\"https://x/sha\"}," +
                "{\"name\":\"IP-Umschalter.exe\",\"browser_download_url\":\"https://x/exe\"}," +
                "{\"name\":\"anderes.zip\",\"browser_download_url\":\"https://x/zip\"}]}");
            Eq("Parse Release", "1.2.3|https://x/exe|https://x/sha|Neu", rel.Version + "|" + rel.ExeUrl + "|" + rel.ShaUrl + "|" + rel.Notes);
            Eq("Parse Release ohne Assets", "", Updater.Parse("{\"tag_name\":\"v2.0.0\",\"assets\":[]}").ExeUrl);
            Eq("SHA-256 von \"abc\"", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Updater.Sha256Hex(Encoding.ASCII.GetBytes("abc")));
            Eq("Assembly-Version = Program.Version", Program.Version, typeof(SelfTest).Assembly.GetName().Version.ToString(3));

            // Aufgabenplanung
            string xAuto = TaskSetup.BuildXml(@"C:\A & B\IP-Umschalter.exe", true, "S-1-5-21-1", @"PC\nutzer");
            string xNo = TaskSetup.BuildXml(@"C:\A\IP-Umschalter.exe", false, "S-1-5-21-1", @"PC\nutzer");
            Eq("Task-XML: Autostart hat LogonTrigger", true, xAuto.Contains("<LogonTrigger>") && xAuto.Contains(@"<UserId>PC\nutzer</UserId>"));
            Eq("Task-XML: ohne Autostart kein Trigger", false, xNo.Contains("LogonTrigger"));
            Eq("Task-XML: höchste Rechte", true, xNo.Contains("<RunLevel>HighestAvailable</RunLevel>"));
            Eq("Task-XML: Pfad mit & maskiert", true, xAuto.Contains(@"<Command>C:\A &amp; B\IP-Umschalter.exe</Command>"));
            Eq("Task-XML: gültiges XML", true, IsXml(xAuto) && IsXml(xNo));

            sb.Append("\r\n").Append(pass).Append(" bestanden, ").Append(fail).Append(" fehlgeschlagen\r\n");
            File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false));
            return fail == 0 ? 0 : 1;
        }
    }
}
