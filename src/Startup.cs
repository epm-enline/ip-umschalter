// IP-Umschalter — Start ohne UAC (Aufgabenplanung), Befehle an die laufende Instanz (Named Pipe), Einstellungen
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace IpUmschalter
{
    /// <summary>Befehle an die laufende (erhöhte) Instanz: "show", "tray", "apply\tProfilname".</summary>
    public static class Ipc
    {
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);

        public static string PipeName
        {
            get { return "IpUmschalter-" + WindowsIdentity.GetCurrent().User.Value; }
        }

        public static bool TrySend(string cmd, int timeoutMs)
        {
            try
            {
                using (var c = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut))
                {
                    c.Connect(timeoutMs);
                    try { AllowSetForegroundWindow(-1); } catch { } // Fenster der Instanz darf nach vorn
                    var w = new StreamWriter(c, new UTF8Encoding(false));
                    w.WriteLine(cmd); w.Flush();
                    return new StreamReader(c, Encoding.UTF8).ReadLine() == "ok";
                }
            }
            catch { return false; }
        }

        /// <summary>Server-Schleife im Hintergrund; ruft onCommand im Hintergrundthread auf.</summary>
        public static void Serve(Action<string> onCommand)
        {
            var t = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        // Nur der eigene Benutzer (auch ohne Adminrechte) darf verbinden.
                        var sec = new PipeSecurity();
                        sec.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
                        using (var s = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None, 4096, 4096, sec))
                        {
                            s.WaitForConnection();
                            var r = new StreamReader(s, Encoding.UTF8);
                            string line = r.ReadLine();
                            var w = new StreamWriter(s, new UTF8Encoding(false));
                            w.WriteLine("ok"); w.Flush();
                            if (line != null) onCommand(line);
                        }
                    }
                    catch { Thread.Sleep(300); }
                }
            });
            t.IsBackground = true;
            t.Start();
        }
    }

    /// <summary>Aufgabe „IP-Umschalter“ in der Aufgabenplanung: startet die exe mit höchsten Rechten ohne UAC-Abfrage,
    /// optional zusätzlich bei der Anmeldung (Autostart).</summary>
    public static class TaskSetup
    {
        public const string TaskName = "IP-Umschalter";

        static int Schtasks(string args, out string output)
        {
            var psi = new ProcessStartInfo("schtasks.exe", args);
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            using (var p = Process.Start(psi))
            {
                output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        /// <summary>Startet die Aufgabe (geht ohne Adminrechte). false = Aufgabe fehlt oder Start verweigert.</summary>
        public static bool Run()
        {
            string o;
            try { return Schtasks("/run /tn \"" + TaskName + "\"", out o) == 0; }
            catch { return false; }
        }

        public class State { public bool Exists; public bool Autostart; public string Command = ""; }

        public static State Query()
        {
            var st = new State();
            string xml;
            if (Schtasks("/query /tn \"" + TaskName + "\" /xml ONE", out xml) != 0) return st;
            st.Exists = true;
            st.Autostart = xml.Contains("<LogonTrigger");
            int a = xml.IndexOf("<Command>"), b = xml.IndexOf("</Command>");
            if (a >= 0 && b > a) st.Command = xml.Substring(a + 9, b - a - 9).Trim().Replace("&amp;", "&");
            return st;
        }

        static string X(string s) { return SecurityElement.Escape(s); }

        public static string BuildXml(string exe, bool autostart, string userSid, string userName)
        {
            string trigger = autostart
                ? "<LogonTrigger><Enabled>true</Enabled><UserId>" + X(userName) + "</UserId><Delay>PT5S</Delay></LogonTrigger>"
                : "";
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                "<RegistrationInfo><Description>IP-Umschalter ohne UAC-Abfrage starten" + (autostart ? " und bei der Anmeldung" : "") + ".</Description></RegistrationInfo>" +
                "<Triggers>" + trigger + "</Triggers>" +
                "<Principals><Principal id=\"Author\"><UserId>" + X(userSid) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
                "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>false</StartWhenAvailable>" +
                "<RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable><IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>" +
                "<AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden><RunOnlyIfIdle>false</RunOnlyIfIdle>" +
                "<WakeToRun>false</WakeToRun><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>5</Priority></Settings>" +
                "<Actions Context=\"Author\"><Exec><Command>" + X(exe) + "</Command><Arguments>--tray</Arguments>" +
                "<WorkingDirectory>" + X(Path.GetDirectoryName(exe)) + "</WorkingDirectory></Exec></Actions></Task>";
        }

        /// <summary>Legt die Aufgabe an bzw. ersetzt sie (braucht Adminrechte). Liefert Fehlertext oder null.</summary>
        public static string Install(bool autostart) { return Install(Application.ExecutablePath, autostart); }

        public static string Install(string exe, bool autostart)
        {
            var id = WindowsIdentity.GetCurrent();
            string tmp = Path.Combine(Path.GetTempPath(), "ipumschalter-task-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(tmp, BuildXml(exe, autostart, id.User.Value, id.Name), Encoding.Unicode);
                string o;
                int code = Schtasks("/create /tn \"" + TaskName + "\" /xml \"" + tmp + "\" /f", out o);
                return code == 0 ? null : o.Trim();
            }
            catch (Exception ex) { return ex.Message; }
            finally { try { File.Delete(tmp); } catch { } }
        }

        public static string Remove()
        {
            string o;
            if (!Query().Exists) return null;
            return Schtasks("/delete /tn \"" + TaskName + "\" /f", out o) == 0 ? null : o.Trim();
        }

        /// <summary>Beim erhöhten Start: Aufgabe anlegen bzw. auf den aktuellen exe-Pfad nachziehen.</summary>
        public static void Ensure(Settings s)
        {
            if (!s.StartOhneUac) return;
            var st = Query();
            if (st.Exists && string.Equals(st.Command, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return;
            Install(st.Exists && st.Autostart);
        }
    }

    public class Settings
    {
        public bool StartOhneUac { get; set; }
        public bool AutoUpdate { get; set; }
        public Settings() { StartOhneUac = true; AutoUpdate = true; }

        static string FilePath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json"); } }

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath)) return new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(FilePath, Encoding.UTF8)) ?? new Settings();
            }
            catch { }
            return new Settings();
        }

        public void Save()
        {
            File.WriteAllText(FilePath, Store.Pretty(new JavaScriptSerializer().Serialize(this)), new UTF8Encoding(false));
        }
    }

    public class SettingsForm : Form
    {
        readonly CheckBox chkNoUac = new CheckBox(), chkAuto = new CheckBox(), chkUpdate = new CheckBox();

        /// <param name="checkNow">„Jetzt suchen“: sucht und installiert über die laufende Instanz.</param>
        public SettingsForm(Action checkNow)
        {
            Text = "IP-Umschalter – Einstellungen";
            Icon = AppIcon.Large();
            Font = new Font("Segoe UI", 9f);
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(470, 224);

            chkAuto.Text = "Mit Windows starten (im Infobereich neben der Uhr)";
            chkAuto.SetBounds(16, 16, 440, 24); Controls.Add(chkAuto);

            chkNoUac.Text = "Ohne Admin-Abfrage starten";
            chkNoUac.SetBounds(16, 48, 440, 24); Controls.Add(chkNoUac);

            chkUpdate.Text = "Automatisch aktualisieren";
            chkUpdate.SetBounds(16, 82, 330, 24); Controls.Add(chkUpdate);
            var btnCheck = new Button { Text = "Jetzt suchen" };
            btnCheck.SetBounds(356, 80, 100, 28); Controls.Add(btnCheck);
            btnCheck.Click += delegate { if (checkNow != null) checkNow(); };

            var folder = new Button { Text = "Programmordner öffnen" };
            folder.SetBounds(16, 128, 180, 28); Controls.Add(folder);
            folder.Click += delegate { Process.Start("explorer.exe", "\"" + AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\') + "\""); };
            var ver = new Label { Text = "Version " + Program.Version, ForeColor = SystemColors.GrayText, AutoSize = true };
            ver.Location = new Point(206, 134); Controls.Add(ver);

            var ok = new Button { Text = "OK" }; ok.SetBounds(278, 180, 85, 30); Controls.Add(ok);
            var cancel = new Button { Text = "Abbrechen", DialogResult = DialogResult.Cancel }; cancel.SetBounds(371, 180, 85, 30); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;

            var st = TaskSetup.Query();
            chkNoUac.Checked = st.Exists;
            chkAuto.Checked = st.Exists && st.Autostart;
            chkUpdate.Checked = Settings.Load().AutoUpdate;
            chkAuto.CheckedChanged += delegate { if (chkAuto.Checked) chkNoUac.Checked = true; };
            chkNoUac.CheckedChanged += delegate { if (!chkNoUac.Checked) chkAuto.Checked = false; };
            ok.Click += delegate { if (Apply()) { DialogResult = DialogResult.OK; Close(); } };
        }

        bool Apply()
        {
            var s = Settings.Load();
            s.StartOhneUac = chkNoUac.Checked;
            s.AutoUpdate = chkUpdate.Checked;
            string err = chkNoUac.Checked ? TaskSetup.Install(chkAuto.Checked) : TaskSetup.Remove();
            if (err != null)
            {
                MessageBox.Show(this, "Aufgabenplanung meldet:\r\n" + err, "IP-Umschalter", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            try { s.Save(); } catch (Exception ex) { MessageBox.Show(this, "settings.json nicht gespeichert:\r\n" + ex.Message); return false; }
            return true;
        }
    }
}
