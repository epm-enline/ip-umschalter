// IP-Umschalter — Programmstart, Tray-Icon, Profil-Fenster
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("IP-Umschalter")]
[assembly: AssemblyProduct("IP-Umschalter")]
[assembly: AssemblyVersion(IpUmschalter.Program.Version + ".0")]
[assembly: AssemblyFileVersion(IpUmschalter.Program.Version + ".0")]

namespace IpUmschalter
{
    static class Program
    {
        public const string Version = "1.1.1";   // einzige Versionsquelle (Assembly-Version, Release-Tag, Update-Vergleich)
        const string MutexName = "Local\\IpUmschalter.Instanz";

        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        // Kommandozeile → Befehl: (leer) = Fenster zeigen, --tray = still in den Infobereich, --apply "Name" = Profil aktivieren
        static string CommandOf(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--apply") return "apply\t" + args[1];
            if (args.Length >= 1 && args[0] == "--tray") return "tray";
            return "show";
        }

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length >= 1 && args[0] == "--selftest")
                return SelfTest.Run(args.Length >= 2 ? args[1] : "selftest.txt");

            // Nach einem Update: auf das Ende der alten Instanz warten, dann normal weiter.
            bool justUpdated = false;
            if (args.Length >= 3 && args[0] == "--after-update")
            {
                int pid;
                if (int.TryParse(args[1], out pid))
                    try { Process.GetProcessById(pid).WaitForExit(20000); } catch { }
                justUpdated = true;
                args = new[] { args[2] == "tray" ? "--tray" : "--show" };
            }

            string cmd = CommandOf(args);

            // 1. Läuft schon eine Instanz? → Befehl übergeben, fertig (ohne Adminrechte möglich).
            if (Ipc.TrySend(cmd, 300)) return 0;

            if (!IsAdmin())
            {
                // 2. Über die Aufgabenplanung erhöht starten (keine UAC-Abfrage), dann Befehl übergeben.
                if (TaskSetup.Run())
                {
                    if (cmd == "tray") return 0;
                    for (int i = 0; i < 30; i++) if (Ipc.TrySend(cmd, 500)) return 0;
                    return 1;
                }
                // 3. Keine Aufgabe → einmal per UAC.
                return Relaunch(args);
            }

            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    // Instanz startet gerade noch → kurz warten und Befehl übergeben.
                    for (int i = 0; i < 10; i++) if (Ipc.TrySend(cmd, 500)) break;
                    return 0;
                }
                try { TaskSetup.Ensure(Settings.Load()); } catch { }
                Updater.CleanupOld(Application.ExecutablePath);
                Application.Run(new TrayApp(cmd, justUpdated));
            }
            return 0;
        }

        static bool IsAdmin()
        {
            return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        }

        static int Relaunch(string[] args)
        {
            var psi = new ProcessStartInfo(Application.ExecutablePath,
                string.Join(" ", args.Select(a => "\"" + a.Replace("\"", "") + "\"")));
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            try { Process.Start(psi); }
            catch (Win32Exception) { } // UAC abgelehnt
            return 0;
        }
    }

    static class AppIcon
    {
        static Icon small, large;
        static Icon Load(Size size)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("IpUmschalter.app.ico"))
                return s != null ? new Icon(s, size) : SystemIcons.Application;
        }
        public static Icon Small() { return small ?? (small = Load(SystemInformation.SmallIconSize)); }
        public static Icon Large() { return large ?? (large = Load(SystemInformation.IconSize)); }
    }

    /// <summary>Gemeinsamer Profilbestand für Tray und Fenster.</summary>
    class Model
    {
        public List<Profile> Profiles = new List<Profile>();
        public event EventHandler Changed;

        public void Load()
        {
            try { Profiles = Store.Load(Store.DefaultPath); }
            catch (Exception ex)
            {
                string bak = Store.DefaultPath + ".defekt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                try { File.Copy(Store.DefaultPath, bak); } catch { }
                MessageBox.Show("profiles.json ist nicht lesbar und wurde gesichert als\r\n" + bak + "\r\n\r\n" + ex.Message,
                    "IP-Umschalter", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Profiles = new List<Profile>();
            }
        }

        public void Save()
        {
            Store.Save(Store.DefaultPath, Profiles);
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        public Profile ActiveFor(string adapter, AdapterConfig cfg)
        {
            return Profiles.FirstOrDefault(p => string.Equals(p.Adapter, adapter, StringComparison.OrdinalIgnoreCase) && Net.Matches(p, cfg));
        }

        public HashSet<Profile> ActiveProfiles()
        {
            var res = new HashSet<Profile>();
            foreach (var g in Profiles.GroupBy(p => p.Adapter.ToLowerInvariant()))
            {
                var cfg = Net.ReadConfig(g.First().Adapter);
                foreach (var p in g) if (Net.Matches(p, cfg)) res.Add(p);
            }
            return res;
        }
    }

    class TrayApp : ApplicationContext
    {
        readonly Model model = new Model();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly Control marshal = new Control();
        readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer();
        MainForm form;
        bool busy;

        bool trayHintShown;

        readonly System.Windows.Forms.Timer updTimer = new System.Windows.Forms.Timer();
        bool updating;
        string pendingUpdate; // geladene, noch nicht eingesetzte exe

        public TrayApp(string startCmd, bool justUpdated)
        {
            marshal.CreateControl();
            model.Load();
            model.Changed += delegate { UpdateTooltip(); };

            menu.Opening += delegate { BuildMenu(); };
            tray.Icon = AppIcon.Small();
            tray.ContextMenuStrip = menu;          // Rechtsklick → Profile
            tray.Visible = true;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowForm(); }; // Linksklick → Fenster
            tray.BalloonTipClicked += delegate { ShowForm(); };
            BuildMenu();
            UpdateTooltip();

            poll.Interval = 5000;
            poll.Tick += delegate { if (!busy) UpdateTooltip(); };
            poll.Start();

            Ipc.Serve(c => marshal.BeginInvoke(new Action<string>(HandleCommand), c));
            HandleCommand(startCmd);

            if (justUpdated) tray.ShowBalloonTip(4000, "IP-Umschalter aktualisiert", "Jetzt Version " + Program.Version + ".", ToolTipIcon.Info);

            // Update-Prüfung: 1 min nach Start, dann alle 6 h; eine bereitliegende exe wird jede Minute erneut versucht.
            updTimer.Interval = 60 * 1000;
            updTimer.Tick += delegate
            {
                updTimer.Interval = pendingUpdate != null ? 60 * 1000 : 6 * 3600 * 1000;
                if (pendingUpdate != null) TryInstall(false);
                else if (Settings.Load().AutoUpdate) CheckUpdate(false);
            };
            updTimer.Start();
        }

        /// <summary>Sucht nach einer neuen Version; manual = vom Anwender angestoßen (meldet auch „aktuell“ und Fehler).</summary>
        public void CheckUpdate(bool manual)
        {
            if (updating) return;
            updating = true;
            string exe = Application.ExecutablePath;
            Task.Factory.StartNew(() =>
            {
                var rel = Updater.Latest();
                if (!Updater.IsNewer(rel.Version, Program.Version)) return new string[] { null, rel.Version };
                return new[] { Updater.Download(rel, exe), rel.Version };
            }).ContinueWith(t =>
            {
                updating = false;
                if (t.IsFaulted)
                {
                    if (manual) MessageBox.Show("Update-Prüfung fehlgeschlagen:\r\n" + t.Exception.GetBaseException().Message, "IP-Umschalter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (t.Result[0] == null)
                {
                    if (manual) MessageBox.Show("Version " + Program.Version + " ist aktuell (neueste auf GitHub: " + t.Result[1] + ").", "IP-Umschalter", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                pendingUpdate = t.Result[0];
                if (manual) tray.ShowBalloonTip(3000, "IP-Umschalter", "Version " + t.Result[1] + " wird installiert …", ToolTipIcon.Info);
                TryInstall(manual);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Setzt die geladene exe ein und startet neu – nie während einer Umschaltung.
        /// Ungespeicherte Eingaben: automatisch → später; manual („Jetzt suchen“) → Speichern-Rückfrage.</summary>
        void TryInstall(bool manual)
        {
            if (pendingUpdate == null || !File.Exists(pendingUpdate)) { pendingUpdate = null; return; }
            if (busy) { RetrySoon(); return; } // Activate ruft nach der Umschaltung erneut auf
            if (form != null && !form.IsDisposed && form.IsDirty)
            {
                if (!manual || !form.ConfirmLeave()) { RetrySoon(); return; }
            }
            if (openSettings != null)
            {
                // Einstellungsfenster (modal) erst schließen, dann neu ansetzen
                openSettings.Close();
                marshal.BeginInvoke(new Action(() => TryInstall(manual)));
                return;
            }
            string exe = Application.ExecutablePath;
            bool shown = form != null && !form.IsDisposed && form.Visible;
            try { Updater.Swap(exe, pendingUpdate); }
            catch (Exception ex)
            {
                pendingUpdate = null;
                MessageBox.Show("Update konnte nicht eingesetzt werden:\r\n" + ex.Message, "IP-Umschalter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            pendingUpdate = null;
            var psi = new ProcessStartInfo(exe, "--after-update " + Process.GetCurrentProcess().Id + " " + (shown ? "show" : "tray"));
            psi.UseShellExecute = false;
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            Process.Start(psi);
            ExitApp(true);
        }

        void HandleCommand(string cmd)
        {
            if (cmd == "ping") return;
            if (cmd == "exit") { ExitApp(); return; }
            if (cmd == "update") { CheckUpdate(false); return; }
            if (cmd == "update-now") { CheckUpdate(true); return; }          // wie „Jetzt suchen“
            if (cmd == "settings") { ShowSettings(null); return; }
            if (cmd == "tray") { if (model.Profiles.Count == 0) ShowForm(); return; }
            if (cmd.StartsWith("apply\t"))
            {
                string name = cmd.Substring(6);
                var p = model.Profiles.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (p == null) tray.ShowBalloonTip(4000, "IP-Umschalter", "Profil „" + name + "“ gibt es nicht (mehr).", ToolTipIcon.Warning);
                else if (busy) tray.ShowBalloonTip(3000, "IP-Umschalter", "Es wird gerade umgeschaltet – bitte kurz warten.", ToolTipIcon.Info);
                else Activate(p);
                return;
            }
            ShowForm();
        }

        /// <summary>Fenster wurde geschlossen → einmal pro Sitzung zeigen, wo das Programm weiterläuft.</summary>
        public void HiddenToTray()
        {
            if (trayHintShown) return;
            trayHintShown = true;
            tray.ShowBalloonTip(3000, "IP-Umschalter läuft weiter", "Rechtsklick auf das IP-Symbol: Profil wählen. Linksklick: Fenster.", ToolTipIcon.Info);
        }

        public void ShowSettings(IWin32Window owner)
        {
            if (openSettings != null) { openSettings.Activate(); return; }
            using (var f = new SettingsForm(() => CheckUpdate(true)))
            {
                openSettings = f;
                try { f.ShowDialog(owner); } finally { openSettings = null; }
            }
        }

        SettingsForm openSettings;

        void RetrySoon()
        {
            updTimer.Stop(); updTimer.Interval = 60 * 1000; updTimer.Start();
        }

        void BuildMenu()
        {
            menu.Items.Clear();
            var active = model.ActiveProfiles();
            var groups = model.Profiles.GroupBy(p => p.Adapter).ToList();
            foreach (var g in groups)
            {
                var hdr = new ToolStripMenuItem(g.Key); hdr.Enabled = false;
                menu.Items.Add(hdr);
                foreach (var p in g)
                {
                    var prof = p;
                    var it = new ToolStripMenuItem(p.Name);
                    it.Checked = active.Contains(p);
                    it.ToolTipText = Summary(p);
                    it.Enabled = !busy;
                    it.Click += delegate { Activate(prof); };
                    menu.Items.Add(it);
                }
                menu.Items.Add(new ToolStripSeparator());
            }
            if (model.Profiles.Count == 0) { var e = new ToolStripMenuItem("(noch keine Profile)"); e.Enabled = false; menu.Items.Add(e); menu.Items.Add(new ToolStripSeparator()); }
            var edit = new ToolStripMenuItem("Profile bearbeiten …"); edit.Font = new Font(edit.Font, FontStyle.Bold);
            edit.Click += delegate { ShowForm(); };
            menu.Items.Add(edit);
            var settings = new ToolStripMenuItem("Einstellungen …");
            settings.Click += delegate { ShowSettings(form != null && !form.IsDisposed && form.Visible ? form : null); };
            menu.Items.Add(settings);
            var quit = new ToolStripMenuItem("Beenden");
            quit.Click += delegate { ExitApp(); };
            menu.Items.Add(quit);
        }

        public static string Summary(Profile p)
        {
            if (p.Dhcp) return "DHCP";
            string s = string.Join(", ", p.Addresses.Select(a => a.Ip + "/" + Net.PrefixOf(a.Mask)));
            if (p.Gateway != "") s += "  GW " + p.Gateway;
            return s;
        }

        void UpdateTooltip()
        {
            var act = model.ActiveProfiles().Select(p => p.Name).ToList();
            string t = "IP-Umschalter" + (act.Count > 0 ? ": " + string.Join(", ", act) : "");
            tray.Text = t.Length > 63 ? t.Substring(0, 60) + "…" : t;
        }

        public void Activate(Profile p)
        {
            if (busy) return;
            busy = true;
            if (form != null && !form.IsDisposed) form.SetBusy(true, "Aktiviere „" + p.Name + "“ …");
            var copy = p.Clone();
            Task.Factory.StartNew(() => Net.Apply(copy)).ContinueWith(t =>
            {
                busy = false;
                ApplyResult r = t.IsFaulted
                    ? new ApplyResult { Ok = false, Message = t.Exception.GetBaseException().Message, Log = "" }
                    : t.Result;
                UpdateTooltip();
                if (form != null && !form.IsDisposed) form.AfterApply(r);
                if (r.Ok) tray.ShowBalloonTip(2500, "IP-Umschalter", r.Message, ToolTipIcon.Info);
                else MessageBox.Show(r.Message + "\r\n\r\nProtokoll:\r\n" + r.Log, "IP-Umschalter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void ShowForm()
        {
            if (form == null || form.IsDisposed) form = new MainForm(model, this);
            form.Show();
            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
            form.Activate();
            form.BringToFront();
        }

        public bool Busy { get { return busy; } }

        public void ExitApp() { ExitApp(false); }

        void ExitApp(bool force)
        {
            if (!force && form != null && !form.IsDisposed && !form.ConfirmLeave()) return;
            updTimer.Stop();
            if (form != null && !form.IsDisposed) { form.AllowClose = true; form.Close(); }
            poll.Stop();
            tray.Visible = false;
            tray.Dispose();
            ExitThread();
        }
    }

    class AdapterItem
    {
        public string Name, Text;
        public override string ToString() { return Text; }
    }

    class MainForm : Form
    {
        readonly Model model;
        readonly TrayApp app;
        public bool AllowClose;

        readonly ListBox list = new ListBox();
        readonly Button btnNew = new Button(), btnCopy = new Button(), btnDel = new Button(), btnUp = new Button(), btnDown = new Button();
        readonly TextBox txtName = new TextBox();
        readonly ComboBox cmbAdapter = new ComboBox();
        readonly Button btnRefresh = new Button();
        readonly RadioButton rbDhcp = new RadioButton(), rbStatic = new RadioButton();
        readonly Button btnTake = new Button();
        readonly DataGridView grid = new DataGridView();
        readonly TextBox txtGw = new TextBox(), txtDns = new TextBox(), txtState = new TextBox();
        readonly Label lblIps = new Label(), lblGw = new Label(), lblDns = new Label(), lblDnsHint = new Label();
        readonly Button btnSave = new Button(), btnApply = new Button(), btnLink = new Button(), btnSettings = new Button();
        readonly Label lblStatus = new Label();

        int current = -1;
        bool dirty, loading, suppress;
        public bool IsDirty { get { return dirty; } }

        public MainForm(Model model, TrayApp app)
        {
            this.model = model; this.app = app;
            Text = "IP-Umschalter " + Program.Version;
            Icon = AppIcon.Large();
            Font = new Font("Segoe UI", 9f);
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(820, 534);

            SuspendLayout();
            Add(new Label { Text = "Profile", Location = new Point(12, 12), AutoSize = true });
            list.SetBounds(12, 32, 210, 382);
            list.IntegralHeight = false;
            Controls.Add(list);
            list.Height = 348;
            Btn(btnNew, "Neu", 12, 388, 102); Btn(btnCopy, "Kopieren", 120, 388, 102);
            Btn(btnDel, "Löschen", 12, 422, 102); Btn(btnUp, "▲", 120, 422, 49); Btn(btnDown, "▼", 173, 422, 49);
            Btn(btnSettings, "⚙  Einstellungen …", 12, 456, 210); btnSettings.Height = 28;

            int lx = 240, x = 345, w = 460;
            Add(new Label { Text = "Name", Location = new Point(lx, 35), AutoSize = true });
            txtName.SetBounds(x, 32, w, 25); Controls.Add(txtName);

            Add(new Label { Text = "Adapter", Location = new Point(lx, 68), AutoSize = true });
            cmbAdapter.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbAdapter.SetBounds(x, 65, w - 40, 25); Controls.Add(cmbAdapter);
            Btn(btnRefresh, "↻", x + w - 34, 64, 34); btnRefresh.Height = 26;

            Add(new Label { Text = "Adresse", Location = new Point(lx, 102), AutoSize = true });
            rbDhcp.Text = "DHCP"; rbDhcp.SetBounds(x, 99, 80, 24); Controls.Add(rbDhcp);
            rbStatic.Text = "Statisch"; rbStatic.SetBounds(x + 85, 99, 90, 24); Controls.Add(rbStatic);
            Btn(btnTake, "Aktuelle Einstellung übernehmen", x + w - 220, 97, 220);

            lblIps.Text = "IP-Adressen"; lblIps.Location = new Point(lx, 135); lblIps.AutoSize = true; Controls.Add(lblIps);
            grid.SetBounds(x, 132, w, 150);
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersWidth = 28;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.BackgroundColor = SystemColors.Window;
            grid.Columns.Add("ip", "IP-Adresse (auch 192.168.1.10/24)");
            grid.Columns.Add("mask", "Subnetzmaske");
            grid.Columns[0].FillWeight = 60; grid.Columns[1].FillWeight = 40;
            grid.DefaultValuesNeeded += (s, e) => { e.Row.Cells[1].Value = Net.DefaultMask; };
            Controls.Add(grid);

            lblGw.Text = "Gateway"; lblGw.Location = new Point(lx, 295); lblGw.AutoSize = true; Controls.Add(lblGw);
            txtGw.SetBounds(x, 292, 200, 25); Controls.Add(txtGw);
            Add(new Label { Text = "leer = kein Gateway", Location = new Point(x + 210, 295), AutoSize = true, ForeColor = SystemColors.GrayText });

            lblDns.Text = "DNS-Server"; lblDns.Location = new Point(lx, 328); lblDns.AutoSize = true; Controls.Add(lblDns);
            txtDns.SetBounds(x, 325, w, 25); Controls.Add(txtDns);
            lblDnsHint.Text = "mehrere mit Komma trennen, Reihenfolge = Priorität";
            lblDnsHint.Location = new Point(x, 352); lblDnsHint.AutoSize = true; lblDnsHint.ForeColor = SystemColors.GrayText; Controls.Add(lblDnsHint);

            Add(new Label { Text = "Adapter jetzt", Location = new Point(lx, 380), AutoSize = true });
            txtState.SetBounds(x, 377, w, 60);
            txtState.Multiline = true; txtState.ReadOnly = true; txtState.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(txtState);

            Btn(btnSave, "Speichern", x, 450, 110); btnSave.Height = 34;
            Btn(btnApply, "Speichern + Aktivieren", x + 118, 450, 170); btnApply.Height = 34;
            btnApply.Font = new Font(Font, FontStyle.Bold);
            Btn(btnLink, "Desktop-Verknüpfung", x + w - 164, 450, 164); btnLink.Height = 34;

            lblStatus.SetBounds(12, 500, 796, 24);
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(lblStatus);
            ResumeLayout(false);
            PerformLayout();

            // Ereignisse
            list.SelectedIndexChanged += delegate { OnSelect(); };
            btnNew.Click += delegate { NewProfile(null); };
            btnCopy.Click += delegate { if (current >= 0) NewProfile(model.Profiles[current]); };
            btnDel.Click += delegate { DeleteProfile(); };
            btnUp.Click += delegate { MoveProfile(-1); };
            btnDown.Click += delegate { MoveProfile(1); };
            btnRefresh.Click += delegate { FillAdapters(SelectedAdapter()); ShowState(); };
            btnTake.Click += delegate { TakeCurrent(); };
            btnSave.Click += delegate { SaveCurrent(); };
            btnApply.Click += delegate { if (SaveCurrent()) app.Activate(model.Profiles[current]); };
            btnLink.Click += delegate { CreateShortcut(); };
            btnSettings.Click += delegate { app.ShowSettings(this); };
            rbDhcp.CheckedChanged += delegate { UpdateEnabled(); MarkDirty(); };
            txtName.TextChanged += delegate { MarkDirty(); };
            txtGw.TextChanged += delegate { MarkDirty(); };
            txtDns.TextChanged += delegate { MarkDirty(); };
            cmbAdapter.SelectedIndexChanged += delegate { MarkDirty(); ShowState(); };
            grid.CellValueChanged += delegate { MarkDirty(); };
            grid.RowsRemoved += delegate { MarkDirty(); };
            model.Changed += delegate { if (!IsDisposed) RefreshList(); };

            FillAdapters(null);
            RefreshList();
            if (model.Profiles.Count > 0) list.SelectedIndex = 0; else LoadEditor(-1);
        }

        void Add(Control c) { Controls.Add(c); }
        void Btn(Button b, string text, int x, int y, int w) { b.Text = text; b.SetBounds(x, y, w, 28); Controls.Add(b); }

        void MarkDirty()
        {
            if (loading || current < 0) return;
            if (!dirty) { dirty = true; Text = "IP-Umschalter " + Program.Version + " – ungespeichert"; }
        }

        void ClearDirty() { dirty = false; Text = "IP-Umschalter " + Program.Version; }

        void RefreshList()
        {
            suppress = true;
            var active = model.ActiveProfiles();
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var p in model.Profiles) list.Items.Add((active.Contains(p) ? "● " : "   ") + p.Name);
            list.EndUpdate();
            if (current >= model.Profiles.Count) current = model.Profiles.Count - 1;
            list.SelectedIndex = current;
            suppress = false;
        }

        void FillAdapters(string select)
        {
            bool wasLoading = loading; loading = true;
            cmbAdapter.Items.Clear();
            foreach (var n in Net.Adapters())
                cmbAdapter.Items.Add(new AdapterItem
                {
                    Name = n.Name,
                    Text = n.Name + "  –  " + n.Description + (n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up ? "  (verbunden)" : "  (getrennt)")
                });
            SelectAdapter(select);
            loading = wasLoading;
        }

        void SelectAdapter(string name)
        {
            if (string.IsNullOrEmpty(name)) { cmbAdapter.SelectedIndex = -1; return; }
            foreach (AdapterItem it in cmbAdapter.Items)
                if (string.Equals(it.Name, name, StringComparison.OrdinalIgnoreCase)) { cmbAdapter.SelectedItem = it; return; }
            var missing = new AdapterItem { Name = name, Text = name + "  (nicht vorhanden)" };
            cmbAdapter.Items.Add(missing);
            cmbAdapter.SelectedItem = missing;
        }

        string SelectedAdapter()
        {
            var it = cmbAdapter.SelectedItem as AdapterItem;
            return it == null ? "" : it.Name;
        }

        void UpdateEnabled()
        {
            bool st = rbStatic.Checked && current >= 0;
            grid.Enabled = st; txtGw.Enabled = st; txtDns.Enabled = st;
            grid.DefaultCellStyle.ForeColor = st ? SystemColors.WindowText : SystemColors.GrayText;
        }

        void LoadEditor(int idx)
        {
            loading = true;
            current = idx;
            bool has = idx >= 0;
            foreach (Control c in new Control[] { txtName, cmbAdapter, btnRefresh, rbDhcp, rbStatic, btnTake, btnSave, btnApply, btnLink, btnCopy, btnDel, btnUp, btnDown })
                c.Enabled = has && !app.Busy;
            grid.Rows.Clear();
            if (!has)
            {
                txtName.Text = ""; txtGw.Text = ""; txtDns.Text = ""; rbStatic.Checked = true; cmbAdapter.SelectedIndex = -1;
                txtState.Text = "";
            }
            else
            {
                var p = model.Profiles[idx];
                txtName.Text = p.Name;
                FillAdapters(p.Adapter);
                rbDhcp.Checked = p.Dhcp; rbStatic.Checked = !p.Dhcp;
                foreach (var a in p.Addresses) grid.Rows.Add(a.Ip, a.Mask);
                txtGw.Text = p.Gateway;
                txtDns.Text = string.Join(", ", p.Dns);
            }
            loading = false;
            ClearDirty();
            UpdateEnabled();
            ShowState();
        }

        Profile ReadEditor()
        {
            var p = new Profile();
            p.Name = txtName.Text.Trim();
            p.Adapter = SelectedAdapter();
            p.Dhcp = rbDhcp.Checked;
            grid.EndEdit();
            foreach (DataGridViewRow r in grid.Rows)
            {
                if (r.IsNewRow) continue;
                string ip = Convert.ToString(r.Cells[0].Value), mask = Convert.ToString(r.Cells[1].Value);
                if (ip.Trim() == "" && (mask.Trim() == "" || mask.Trim() == Net.DefaultMask)) continue;
                p.Addresses.Add(Net.ParseEntry(ip, mask));
            }
            p.Gateway = txtGw.Text.Trim();
            p.Dns = Net.SplitList(txtDns.Text);
            if (p.Dhcp) { p.Addresses.Clear(); p.Gateway = ""; p.Dns.Clear(); }
            return p;
        }

        // Liefert false, wenn ungültig (Meldung wurde gezeigt).
        bool SaveCurrent()
        {
            if (current < 0) return false;
            var p = ReadEditor();
            var others = model.Profiles.Where((x, i) => i != current).ToList();
            var err = Net.Validate(p, others);
            if (err.Count > 0)
            {
                MessageBox.Show(this, string.Join("\r\n", err), "Profil nicht gespeichert", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            model.Profiles[current] = p;
            model.Save();
            LoadEditor(current);
            Status("Gespeichert: „" + p.Name + "“");
            return true;
        }

        public bool ConfirmLeave()
        {
            if (!dirty || current < 0) return true;
            var r = MessageBox.Show(this, "Änderungen an „" + model.Profiles[current].Name + "“ speichern?", "IP-Umschalter",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.Yes) return SaveCurrent();
            ClearDirty();
            return true;
        }

        void OnSelect()
        {
            if (suppress) return;
            int idx = list.SelectedIndex;
            if (idx == current) return;
            if (!ConfirmLeave())
            {
                suppress = true; list.SelectedIndex = current; suppress = false;
                return;
            }
            LoadEditor(idx);
        }

        void NewProfile(Profile template)
        {
            if (!ConfirmLeave()) return;
            Profile p;
            if (template != null) { p = template.Clone(); p.Name = Unique(template.Name + " (Kopie)"); }
            else
            {
                p = new Profile();
                p.Name = Unique("Neues Profil");
                var ad = Net.Adapters().FirstOrDefault(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                    && n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet);
                p.Adapter = ad != null ? ad.Name : "";
                p.Dhcp = true;
            }
            model.Profiles.Add(p);
            current = model.Profiles.Count - 1;
            model.Save();
            LoadEditor(current);
            txtName.Focus(); txtName.SelectAll();
            Status(template != null ? "Kopie angelegt." : "Neues Profil angelegt – Adapter und Adressen eintragen, dann speichern.");
        }

        string Unique(string baseName)
        {
            string n = baseName; int i = 2;
            while (model.Profiles.Any(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))) n = baseName + " " + i++;
            return n;
        }

        void DeleteProfile()
        {
            if (current < 0) return;
            var p = model.Profiles[current];
            if (MessageBox.Show(this, "Profil „" + p.Name + "“ löschen?", "IP-Umschalter", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            model.Profiles.RemoveAt(current);
            ClearDirty();
            current = Math.Min(current, model.Profiles.Count - 1);
            model.Save();
            LoadEditor(current);
            Status("Gelöscht: „" + p.Name + "“");
        }

        void MoveProfile(int dir)
        {
            int to = current + dir;
            if (current < 0 || to < 0 || to >= model.Profiles.Count) return;
            if (!ConfirmLeave()) return;
            var p = model.Profiles[current];
            model.Profiles.RemoveAt(current);
            model.Profiles.Insert(to, p);
            current = to;
            model.Save();
            LoadEditor(current);
        }

        void TakeCurrent()
        {
            string ad = SelectedAdapter();
            if (ad == "") return;
            var cfg = Net.ReadConfig(ad);
            if (!cfg.Found) { Status("Adapter „" + ad + "“ nicht vorhanden."); return; }
            var p = Net.FromConfig(cfg, ad);
            rbDhcp.Checked = p.Dhcp; rbStatic.Checked = !p.Dhcp;
            grid.Rows.Clear();
            foreach (var a in p.Addresses) grid.Rows.Add(a.Ip, a.Mask);
            txtGw.Text = p.Gateway;
            txtDns.Text = string.Join(", ", p.Dns);
            MarkDirty();
            Status("Aktuelle Einstellung von „" + ad + "“ übernommen – noch nicht gespeichert.");
        }

        void ShowState()
        {
            string ad = SelectedAdapter();
            txtState.Text = ad == "" ? "" : Net.ReadConfig(ad).Describe();
        }

        void CreateShortcut()
        {
            if (current < 0) return;
            if (dirty && !SaveCurrent()) return;
            var p = model.Profiles[current];
            string safe = string.Concat(p.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "IP " + safe + ".lnk");
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic sc = shell.CreateShortcut(lnk);
                sc.TargetPath = Application.ExecutablePath;
                sc.Arguments = "--apply \"" + p.Name + "\"";
                sc.WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath);
                sc.IconLocation = Application.ExecutablePath + ",0";
                sc.Description = "IP-Profil „" + p.Name + "“ aktivieren";
                sc.Save();
                Status("Verknüpfung erstellt: " + lnk + "  (nach Umbenennen des Profils neu erstellen)");
            }
            catch (Exception ex) { MessageBox.Show(this, "Verknüpfung nicht erstellt:\r\n" + ex.Message, "IP-Umschalter", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        void Status(string s) { lblStatus.Text = s; }

        public void SetBusy(bool b, string msg)
        {
            foreach (Control c in new Control[] { btnApply, btnSave, list, btnNew, btnCopy, btnDel }) c.Enabled = !b;
            Cursor = b ? Cursors.WaitCursor : Cursors.Default;
            Status(msg);
        }

        public void AfterApply(ApplyResult r)
        {
            SetBusy(false, r.Ok ? r.Message : "Nicht vollständig übernommen – siehe Meldung.");
            RefreshList();
            if (dirty) ShowState(); else LoadEditor(current); // ungespeicherte Eingaben nicht verwerfen
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
            {
                if (!ConfirmLeave()) { e.Cancel = true; return; }
                e.Cancel = true;
                Hide();
                LoadEditor(current);
                app.HiddenToTray();
                return;
            }
            base.OnFormClosing(e);
        }

        // Minimieren legt das Fenster ebenfalls in den Infobereich (kein Taskleisten-Eintrag).
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized && Visible)
            {
                Hide();
                WindowState = FormWindowState.Normal;
                app.HiddenToTray();
            }
        }
    }
}
