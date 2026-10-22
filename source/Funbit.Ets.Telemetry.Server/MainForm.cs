using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Funbit.Ets.Telemetry.Server.Controllers;
using Funbit.Ets.Telemetry.Server.Data;
using Funbit.Ets.Telemetry.Server.Helpers;
using Funbit.Ets.Telemetry.Server.Services;
using Funbit.Ets.Telemetry.Server.Setup;
using Funbit.Ets.Telemetry.Server.Ui;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Funbit.Ets.Telemetry.Server
{
    public partial class MainForm : Form
    {
        [DllImport("user32.dll")]
        static extern int RegisterWindowMessage(string message);
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        const int SW_SHOWNORMAL = 1;
        const int LeftColumnWidth = 556;
        const int Gap = 12;
        const int Edge = 14;
        const string LoopbackInterfaceId = "loopback";

        int _showExistingInstanceMessage;

        Settings _settings;
        LinkedDeviceStore _devices;
        PairingSessionService _pairing;
        PresenceClient _presence;
        MinimalHttpServer _server;
        readonly WaveOutVoiceAudio _voiceAudio = new WaveOutVoiceAudio();
        VoiceEndpoints _voice;
        string _serverError;
        bool _hasCheckedForUpdates;
        bool _startedMinimized;
        bool _active;
        static readonly log4net.ILog Log = log4net.LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        readonly HttpClient _broadcastHttpClient = new HttpClient();
        static readonly Encoding Utf8 = new UTF8Encoding(false);
        static readonly string BroadcastUrl = ConfigurationManager.AppSettings["BroadcastUrl"];
        static readonly string BroadcastUserId = Convert.ToBase64String(
            Utf8.GetBytes(ConfigurationManager.AppSettings["BroadcastUserId"] ?? ""));
        static readonly string BroadcastUserPassword = Convert.ToBase64String(
            Utf8.GetBytes(ConfigurationManager.AppSettings["BroadcastUserPassword"] ?? ""));
        static readonly int BroadcastRateInSeconds = Math.Min(Math.Max(1,
            Convert.ToInt32(ConfigurationManager.AppSettings["BroadcastRate"])), 86400);
        static readonly bool UseTestTelemetryData = Convert.ToBoolean(
            ConfigurationManager.AppSettings["UseEts2TestTelemetryData"]);

        MenuStrip _menu;
        ToolStripMenuItem _serverMenu, _rerunSetupItem, _startWithWindowsItem, _languageMenu, _helpMenu, _websiteItem,
            _connectionHelpItem, _githubItem, _discordItem, _logFolderItem, _checkUpdatesItem, _connectionHelpLink;
#if DEBUG
        ToolStripMenuItem _productionItem, _stagingItem;
#endif
        Panel _brand;
        Image _brandIcon;
        Card _serverCard;
        Label _serverTitle;
        Pill _serverPill;
        DarkButton _retry;
        LinkedDevicesCard _devicesCard;
        Card _pluginCard;
        Label _pluginTitle;
        PluginRow _ets2Row, _atsRow;
        Card _manualCard;
        Label _manualTitle, _interfaceLabel, _ipLabel, _reachLabel, _ipValue;
        ChevronButton _chevron;
        Panel _manualContent;
        DarkDropDown _interfaces;
        LinkText _reachUrl, _helpLine;
        InfoIcon _reachInfo;
        LinkDevicePanel _linkPanel;
        TooltipPanel _tooltip;

        sealed class PluginRow
        {
            public string Game;
            public Label Name, Status, Path;
            public DarkButton Install;
        }

        public MainForm()
        {
            _settings = Settings.Instance;
            L.Apply(_settings.Language);
            _pairing = new PairingSessionService(CloudConfig.ApiBaseUrl, _settings.PcId, CloudConfig.Port, ConnectCandidates, TimeSpan.FromMinutes(2));
            InitializeComponent();
            BuildUi();
            _showExistingInstanceMessage = RegisterWindowMessage(Program.ShowExistingInstanceMessage);
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            _active = true;
            UpdateStandby();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            _active = false;
            UpdateStandby();
        }

        void UpdateStandby()
        {
            var window = WindowState == FormWindowState.Minimized || !Visible ? WindowPresence.Hidden
                : _active ? WindowPresence.Front : WindowPresence.Background;
            _presence?.UpdateStandby(_server != null, Ets2ProcessHelper.IsEts2Running && ScsTelemetryDataReader.Instance.IsConnected, window);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            DarkTheme.UseDarkTitleBar(this);
        }

        // WS_EX_COMPOSITED: the window and all its children are painted into one buffer per pass, so no partial repaint can flash.
        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= 0x02000000;
                return parameters;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == _showExistingInstanceMessage)
                RestoreWindow();
            base.WndProc(ref m);
        }

        void BuildUi()
        {
            BackColor = DarkTheme.Window;
            ForeColor = DarkTheme.Text;
            Font = DarkTheme.Body;
            DarkTheme.StyleDropDown(contextMenuStrip);

            _menu = new MenuStrip();
            _serverMenu = new ToolStripMenuItem();
            _rerunSetupItem = new ToolStripMenuItem();
            _rerunSetupItem.Click += rerunSetupToolStripMenuItem_Click;
            _startWithWindowsItem = new ToolStripMenuItem { CheckOnClick = true };
            _startWithWindowsItem.Click += startWithWindowsToolStripMenuItem_Click;
            _languageMenu = new ToolStripMenuItem();
            _serverMenu.DropDownItems.AddRange(new ToolStripItem[] { _rerunSetupItem, _startWithWindowsItem, new ToolStripSeparator(), _languageMenu });
#if DEBUG
            _productionItem = new ToolStripMenuItem("Production") { Checked = _settings.ApiEnvironment != "staging" };
            _productionItem.Click += (s, e) => SetEnvironment(null);
            _stagingItem = new ToolStripMenuItem("Staging") { Checked = _settings.ApiEnvironment == "staging" };
            _stagingItem.Click += (s, e) => SetEnvironment("staging");
            var environmentMenu = new ToolStripMenuItem("Environment");
            environmentMenu.DropDownItems.AddRange(new ToolStripItem[] { _productionItem, _stagingItem });
            _serverMenu.DropDownItems.Add(environmentMenu);
#endif
            _helpMenu = new ToolStripMenuItem();
            _websiteItem = new ToolStripMenuItem();
            _websiteItem.Click += (s, e) => ProcessHelper.OpenUrl(CloudConfig.WebsiteUrl);
            _connectionHelpItem = new ToolStripMenuItem();
            _connectionHelpItem.Click += (s, e) => ProcessHelper.OpenUrl(CloudConfig.ConnectHelpUrl);
            _githubItem = new ToolStripMenuItem();
            _githubItem.Click += (s, e) => ProcessHelper.OpenUrl("https://github.com/TruckSim-GPS/trucksim-gps-server");
            _discordItem = new ToolStripMenuItem();
            _discordItem.Click += (s, e) => ProcessHelper.OpenUrl("https://discord.gg/RdC99Er37U");
            _logFolderItem = new ToolStripMenuItem();
            _logFolderItem.Click += openLogFolderToolStripMenuItem_Click;
            _checkUpdatesItem = new ToolStripMenuItem();
            _checkUpdatesItem.Click += (s, e) => CheckForUpdates(reportErrors: true);
            _helpMenu.DropDownItems.AddRange(new ToolStripItem[] { _websiteItem, _connectionHelpItem, _githubItem, _discordItem, _logFolderItem, new ToolStripSeparator(), _checkUpdatesItem });
            _connectionHelpLink = new ToolStripMenuItem { Alignment = ToolStripItemAlignment.Right, ForeColor = DarkTheme.Link, Tag = "link" };
            _connectionHelpLink.Click += (s, e) => ProcessHelper.OpenUrl(CloudConfig.ConnectHelpUrl);
            _menu.Items.AddRange(new ToolStripItem[] { _serverMenu, _helpMenu, _connectionHelpLink });
            DarkTheme.StyleMenu(_menu);
            MainMenuStrip = _menu;
            Controls.Add(_menu);
            BuildLanguageMenu();

            _brandIcon = Image.FromStream(Assembly.GetExecutingAssembly().GetManifestResourceStream("app_icon.png"));
            _brand = new Panel { Cursor = Cursors.Hand, BackColor = DarkTheme.Window, Size = new Size(240, 36) };
            _brand.Paint += PaintBrand;
            _brand.Click += (s, e) => ProcessHelper.OpenUrl(CloudConfig.WebsiteUrl);
            Controls.Add(_brand);

            _tooltip = new TooltipPanel();
            _tooltip.LinkClicked += ProcessHelper.OpenUrl;
            Controls.Add(_tooltip);

            _serverCard = new Card { Width = LeftColumnWidth, Height = 46 };
            _serverTitle = new Label { AutoSize = true, Font = DarkTheme.Title, ForeColor = Color.White, BackColor = Color.Transparent, Location = new Point(16, 12) };
            _serverPill = new Pill();
            _retry = new DarkButton { Primary = false, Font = DarkTheme.Body, Radius = 6, AutoWidth = true, Height = 22, Visible = false };
            _retry.Click += (s, e) => StartServer();
            _serverCard.Controls.AddRange(new Control[] { _serverTitle, _serverPill, _retry });
            Controls.Add(_serverCard);

            _devicesCard = new LinkedDevicesCard(_tooltip) { Width = LeftColumnWidth, Height = 176 };
            _devicesCard.RemoveRequested += id => { _devices.Remove(id); RefreshDevices(); };
            Controls.Add(_devicesCard);

            _pluginCard = new Card { Width = LeftColumnWidth, Height = 100 };
            _pluginTitle = new Label { AutoSize = true, Font = DarkTheme.Title, ForeColor = Color.White, BackColor = Color.Transparent, Location = new Point(16, 14) };
            _pluginCard.Controls.Add(_pluginTitle);
            _ets2Row = BuildPluginRow("ETS2", 44);
            _atsRow = BuildPluginRow("ATS", 70);
            Controls.Add(_pluginCard);

            _manualCard = new Card { Width = LeftColumnWidth, Height = 48, BorderColor = DarkTheme.CardBorder };
            _manualTitle = new Label { AutoSize = true, Font = DarkTheme.Title, ForeColor = Color.White, BackColor = Color.Transparent, Location = new Point(16, 13) };
            _chevron = new ChevronButton { Location = new Point(LeftColumnWidth - 16 - 28, 10) };
            MouseEventHandler toggle = (s, e) => { if (e.Button == MouseButtons.Left) ToggleManual(); };
            _chevron.MouseDown += toggle;
            _manualCard.MouseDown += toggle;
            _manualTitle.MouseDown += toggle;
            _manualCard.Cursor = Cursors.Hand;
            _manualContent = new Panel { Location = new Point(16, 48), Size = new Size(LeftColumnWidth - 32, 116), BackColor = DarkTheme.Card, Visible = false };
            _interfaceLabel = MutedLabel(0, 8);
            _ipLabel = MutedLabel(0, 46);
            _reachLabel = MutedLabel(0, 74);
            _interfaces = new DarkDropDown { Location = new Point(130, 2), Width = 380 };
            _interfaces.SelectedIndexChanged += interfaceDropDown_SelectedIndexChanged;
            _ipValue = new Label { AutoSize = true, Font = DarkTheme.Mono, ForeColor = Color.White, BackColor = Color.Transparent, Location = new Point(130, 44) };
            _reachUrl = new LinkText { Font = DarkTheme.Body, Location = new Point(130, 72) };
            _reachUrl.LinkClicked += (s, e) => ProcessHelper.OpenUrl(_reachUrl.Text);
            _reachInfo = new InfoIcon { Size = new Size(14, 14) };
            _reachInfo.MouseEnter += (s, e) => _tooltip.Show(_reachInfo, L.Get("manual.reachability_tooltip"));
            _reachInfo.MouseLeave += (s, e) => _tooltip.HideFrom(_reachInfo);
            _helpLine = new LinkText { Location = new Point(0, 98) };
            _helpLine.LinkClicked += (s, e) => ProcessHelper.OpenUrl(CloudConfig.ConnectHelpUrl);
            _manualContent.Controls.AddRange(new Control[] { _interfaceLabel, _ipLabel, _reachLabel, _interfaces, _ipValue, _reachUrl, _reachInfo, _helpLine });
            _manualCard.Controls.AddRange(new Control[] { _manualTitle, _chevron, _manualContent });
            Controls.Add(_manualCard);

            LayoutCards();
            ApplyTexts();
            L.LanguageChanged += ApplyTexts;
        }

        Label MutedLabel(int x, int y)
        {
            return new Label { AutoSize = true, Font = DarkTheme.Body, ForeColor = DarkTheme.TextMuted, BackColor = Color.Transparent, Location = new Point(x, y) };
        }

        PluginRow BuildPluginRow(string game, int y)
        {
            var row = new PluginRow
            {
                Game = game,
                Name = new Label { AutoSize = true, Font = DarkTheme.Body, ForeColor = DarkTheme.TextMuted, BackColor = Color.Transparent, Location = new Point(16, y), Text = game },
                Status = new Label { AutoSize = true, Font = DarkTheme.Body, BackColor = Color.Transparent, Location = new Point(70, y) },
                Path = new Label { AutoSize = false, AutoEllipsis = true, Font = DarkTheme.Body, ForeColor = DarkTheme.TextMuted, BackColor = Color.Transparent, Height = 20, Location = new Point(70, y) },
                Install = new DarkButton { Font = DarkTheme.Body, Radius = 6, AutoWidth = true, Height = 22, Visible = false }
            };
            row.Install.Click += (s, e) => CopyPluginsForGame(game);
            _pluginCard.Controls.AddRange(new Control[] { row.Name, row.Status, row.Path, row.Install });
            return row;
        }

        void LayoutCards()
        {
            int top = _menu.Height + 10;
            _brand.Location = new Point(16, top);
            top += 36 + Gap;
            _serverCard.Location = new Point(Edge, top);
            _devicesCard.Location = new Point(Edge, _serverCard.Bottom + Gap);
            _pluginCard.Location = new Point(Edge, _devicesCard.Bottom + Gap);
            _manualCard.Location = new Point(Edge, _pluginCard.Bottom + Gap);
            int right = Edge + LeftColumnWidth + Gap;
            if (_linkPanel == null)
            {
                _linkPanel = new LinkDevicePanel(_pairing);
                Controls.Add(_linkPanel);
            }
            _linkPanel.Bounds = new Rectangle(right, top, ClientSize.Width - right - Edge, ClientSize.Height - top - Edge);
        }

        IReadOnlyList<string> ConnectCandidates()
        {
            var candidates = NetworkHelper.GetConnectCandidates(_settings.DefaultNetworkInterfaceId);
            candidates.Add("127.0.0.1/8");
            return candidates;
        }

        void BuildLanguageMenu()
        {
            _languageMenu.DropDownItems.Clear();
            var system = new ToolStripMenuItem { Tag = null, Checked = _settings.Language == null };
            system.Click += (s, e) => SetLanguage(null);
            _languageMenu.DropDownItems.Add(system);
            _languageMenu.DropDownItems.Add(new ToolStripSeparator());
            foreach (var code in L.Available)
            {
                string name;
                if (!L.NativeNames.TryGetValue(code, out name))
                    name = code;
                var item = new ToolStripMenuItem(name) { Tag = code, Checked = _settings.Language == code };
                item.Click += (s, e) => SetLanguage((string)((ToolStripItem)s).Tag);
                _languageMenu.DropDownItems.Add(item);
            }
        }

        void SetLanguage(string code)
        {
            _settings.Language = code;
            _settings.Save();
            L.Apply(code);
            foreach (var item in _languageMenu.DropDownItems.OfType<ToolStripMenuItem>())
                item.Checked = (string)item.Tag == code;
        }

#if DEBUG
        void SetEnvironment(string environment)
        {
            _settings.ApiEnvironment = environment;
            _settings.PreferredRelayUrl = null;
            _settings.StandbyToken = null;
            _settings.Save();
            _productionItem.Checked = environment == null;
            _stagingItem.Checked = environment == "staging";
            _pairing.UseApi(CloudConfig.ApiBaseUrl);
            _presence.UseApi(CloudConfig.ApiBaseUrl);
        }
#endif

        void ApplyTexts()
        {
            Text = L.Get("window.title") + " " + AssemblyHelper.Version;
            trayIcon.Text = L.Get("tray.title");
            trayIcon.BalloonTipTitle = L.Get("tray.title");
            openToolStripMenuItem.Text = L.Get("menu.open");
            trayStartWithWindowsMenuItem.Text = L.Get("menu.start_with_windows");
            closeToolStripMenuItem.Text = L.Get("menu.close");
            _serverMenu.Text = L.Get("menu.server");
            _rerunSetupItem.Text = L.Get("menu.rerun_setup");
            _startWithWindowsItem.Text = L.Get("menu.start_with_windows");
            _languageMenu.Text = L.Get("menu.language");
            ((ToolStripMenuItem)_languageMenu.DropDownItems[0]).Text = L.Get("language.system");
            _helpMenu.Text = L.Get("menu.help");
            _websiteItem.Text = L.Get("menu.website");
            _connectionHelpItem.Text = L.Get("menu.connection_help");
            _githubItem.Text = L.Get("menu.github");
            _discordItem.Text = L.Get("menu.discord");
            _logFolderItem.Text = L.Get("menu.open_log_folder");
            _checkUpdatesItem.Text = L.Get("menu.check_updates");
            _connectionHelpLink.Text = L.Get("menu.connection_help");
            _serverTitle.Text = L.Get("server.title");
            _retry.Text = L.Get("server.retry");
            _pluginTitle.Text = L.Get("plugin.title");
            _manualTitle.Text = L.Get("manual.title");
            _interfaceLabel.Text = L.Get("manual.interface");
            _ipLabel.Text = L.Get("manual.ip");
            _reachLabel.Text = L.Get("manual.reachability");
            _helpLine.SetWithLink(L.Get("manual.connection_help_line"), L.Get("menu.connection_help"));
            _ets2Row.Install.Text = _atsRow.Install.Text = L.Get("plugin.install");
            foreach (var item in _interfaces.Items.Where(i => (string)i.Value == LoopbackInterfaceId))
                item.Text = L.Get("manual.localhost");
            _interfaces.Invalidate();
            _devicesCard.ApplyTexts();
            _linkPanel.ApplyTexts();
            Relayout();
        }

        void Relayout()
        {
            _reachInfo.Location = new Point(_reachUrl.Right + 6, _reachUrl.Top + 3);
            UpdateStatus();
            UpdatePluginRow(_ets2Row);
            UpdatePluginRow(_atsRow);
            if (_devices != null)
                RefreshDevices();
        }

        void PaintBrand(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(DarkTheme.Window);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using (var path = DarkTheme.RoundedRect(new RectangleF(0, 0, 36, 36), 8))
            {
                g.SetClip(path);
                g.DrawImage(_brandIcon, new Rectangle(0, 0, 36, 36));
                g.ResetClip();
            }
            int x = 48;
            TextRenderer.DrawText(g, "TruckSim ", DarkTheme.Brand, new Point(x, -1), Color.White, TextFormatFlags.NoPadding);
            x += TextRenderer.MeasureText(g, "TruckSim ", DarkTheme.Brand, Size.Empty, TextFormatFlags.NoPadding).Width;
            TextRenderer.DrawText(g, "GPS", DarkTheme.Brand, new Point(x, -1), DarkTheme.Teal, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, L.Get("brand.website"), DarkTheme.BrandSub, new Point(48, 22), DarkTheme.TextMuted, TextFormatFlags.NoPadding);
        }

        void ToggleManual()
        {
            _chevron.Expanded = !_chevron.Expanded;
            _manualContent.Visible = _chevron.Expanded;
            _manualCard.Height = _chevron.Expanded ? 48 + _manualContent.Height + 12 : 48;
            _chevron.Invalidate();
        }

        void Setup()
        {
            try
            {
                if (Program.UninstallMode && SetupManager.Steps.All(s => s.Status == SetupStatus.Uninstalled))
                {
                    DarkMessageBox.Show(this, L.Get("setup.nothing_to_uninstall"), L.Get("setup.done_title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                    Environment.Exit(0);
                }

                if (Program.UninstallMode || SetupManager.Steps.Any(s => s.Status != SetupStatus.Installed))
                {
                    var result = new SetupForm().ShowDialog(this);
                    if (result == DialogResult.Abort)
                        Environment.Exit(0);
                }

                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal;
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                ex.ShowAsMessageBox(this, L.Get("setup.error_title"));
            }
        }

        void Start()
        {
            try
            {
                Log.Info("=== Server Start Begin ===");
                Log.InfoFormat("Current Date/Time: {0}", DateTime.Now);
                Log.InfoFormat(".NET Framework Version: {0}", Environment.Version);
                Log.InfoFormat("OS Version: {0}", Environment.OSVersion);
                Log.InfoFormat("PC id: {0}...", _settings.PcId.Substring(0, 8));

                var networkInterfaces = NetworkHelper.GetAllActiveNetworkInterfaces();
                Log.InfoFormat("Found {0} active network interfaces", networkInterfaces.Count());

                _interfaces.Items.Clear();
                foreach (var networkInterface in networkInterfaces)
                {
                    Log.InfoFormat("Network Interface: {0} - {1}", networkInterface.Name, networkInterface.Ip);
                    _interfaces.Items.Add(new DarkDropDown.Item { Text = networkInterface.Name, Detail = networkInterface.Ip, Value = networkInterface.Id });
                }
                _interfaces.Items.Add(new DarkDropDown.Item { Text = L.Get("manual.localhost"), Detail = "127.0.0.1", Value = LoopbackInterfaceId });

                int remembered = _interfaces.Items.FindIndex(i => (string)i.Value == _settings.DefaultNetworkInterfaceId);
                _interfaces.SelectedIndex = Math.Max(0, remembered);

                _devices = new LinkedDeviceStore(_settings);
                _voice = new VoiceEndpoints(_devices, new VoiceCache(VoiceCache.DefaultRoot), _voiceAudio);
                _presence = new PresenceClient(CloudConfig.ApiBaseUrl, _settings, _devices, _pairing, ConnectCandidates, CloudConfig.Port);
                StartServer();

                statusUpdateTimer.Enabled = true;

                if (!string.IsNullOrEmpty(BroadcastUrl))
                {
                    _broadcastHttpClient.DefaultRequestHeaders.Add("X-UserId", BroadcastUserId);
                    _broadcastHttpClient.DefaultRequestHeaders.Add("X-UserPassword", BroadcastUserPassword);
                    broadcastTimer.Interval = BroadcastRateInSeconds * 1000;
                    broadcastTimer.Enabled = true;
                }

                trayIcon.Visible = true;
                Activate();
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                ex.ShowAsMessageBox(this, L.Get("setup.network_error_title"), MessageBoxIcon.Exclamation);
            }
        }

        void StartServer()
        {
            try
            {
                _server = new MinimalHttpServer(CloudConfig.Port, _pairing, _devices, _voice);
                _server.Start();
                _serverError = null;
            }
            catch (Exception ex)
            {
                var socketError = ex as SocketException;
                _serverError = socketError != null && socketError.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? L.Get("server.port_in_use", CloudConfig.Port)
                    : L.Get("server.not_running", ex.Message);
                _server?.Dispose();
                _server = null;
            }
            UpdateStatus();
        }

        void MainForm_Load(object sender, EventArgs e)
        {
            Log.InfoFormat("Running version {0} on {1} ({2}) {3}", AssemblyHelper.Version,
                Environment.OSVersion,
                Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit",
                Program.UninstallMode ? "[UNINSTALL MODE]" : "");

            var startWithWindows = IsStartWithWindowsEnabled();
            _startWithWindowsItem.Checked = startWithWindows;
            trayStartWithWindowsMenuItem.Checked = startWithWindows;

            Setup();
            Start();

            if (Program.StartMinimized)
            {
                _startedMinimized = true;
                trayIcon.Tag = "Already shown";
                WindowState = FormWindowState.Minimized;
                ShowInTaskbar = false;
                trayIcon.ShowBalloonTip(3000, L.Get("tray.title"), L.Get("tray.background"), ToolTipIcon.Info);
            }
            else
            {
                CheckForUpdates();
            }
        }

        void MainForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            try
            {
                Log.Info("Application closing, stopping server...");
                _devices?.Flush();
                _pairing?.Cancel();
                _presence?.Dispose();
                _server?.Dispose();
                _voiceAudio.Dispose();
                trayIcon.Visible = false;
                Log.Info("Server stopped successfully");
            }
            catch (Exception ex)
            {
                Log.Error("Error during application shutdown", ex);
            }
        }

        void openToolStripMenuItem_Click(object sender, EventArgs e)
        {
            RestoreWindow();
        }

        void closeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Close();
        }

        void trayIcon_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            RestoreWindow();
        }

        void trayIcon_BalloonTipClicked(object sender, EventArgs e)
        {
            RestoreWindow();
        }

        void RestoreWindow()
        {
            ShowWindow(Handle, SW_SHOWNORMAL);
            ShowInTaskbar = true;
            WindowState = FormWindowState.Normal;
            Activate();
            SetForegroundWindow(Handle);
            if (_startedMinimized && !_hasCheckedForUpdates)
                CheckForUpdates();
        }

        void statusUpdateTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                UpdateStatus();
                UpdatePluginRow(_ets2Row);
                UpdatePluginRow(_atsRow);
                TryAutoCorrectGamePath();
                RefreshDevices();
                _linkPanel.Tick();

                bool pairingActive = _pairing.State == PairingState.Generating || _pairing.State == PairingState.Active;
                _presence.SetWanted(_server != null && pairingActive);
                UpdateStandby();
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                ex.ShowAsMessageBox(this, L.Get("setup.process_error_title"));
                statusUpdateTimer.Enabled = false;
            }
        }

        void RefreshDevices()
        {
            _devicesCard.Update(_devices.Sorted(), _presence.SampleThroughput());
        }

        void UpdateStatus()
        {
            if (_devices == null)
                return;
            if (_serverError != null)
                _serverPill.Set(_serverError, DarkTheme.Red, DarkTheme.RedBg);
            else if (UseTestTelemetryData)
                _serverPill.Set(L.Get("server.running_test"), DarkTheme.Teal, DarkTheme.TealBg);
            else if (Ets2ProcessHelper.IsEts2Running && ScsTelemetryDataReader.Instance.IsConnected)
                _serverPill.Set(L.Get("server.running_game", GameDisplayName()), DarkTheme.Green, DarkTheme.GreenBg);
            else if (Ets2ProcessHelper.IsEts2Running)
                _serverPill.Set(L.Get("server.running_starting", GameDisplayName()), DarkTheme.Teal, DarkTheme.TealBg);
            else
                _serverPill.Set(L.Get("server.waiting"), DarkTheme.Amber, DarkTheme.AmberBg);

            _retry.Visible = _serverError != null;
            int right = _serverCard.Width - 16;
            if (_retry.Visible)
            {
                _retry.Location = new Point(right - _retry.Width, 12);
                right = _retry.Left - 8;
            }
            _serverPill.Location = new Point(right - _serverPill.Width, 11);
        }

        static string GameDisplayName()
        {
            return Ets2ProcessHelper.LastRunningGameName == "ATS" ? "American Truck Simulator" : "Euro Truck Simulator 2";
        }

        void MainForm_Resize(object sender, EventArgs e)
        {
            UpdateStandby();
            ShowInTaskbar = WindowState != FormWindowState.Minimized;
            if (!ShowInTaskbar && trayIcon.Tag == null)
            {
                trayIcon.ShowBalloonTip(1000, L.Get("tray.title"), L.Get("tray.restore"), ToolTipIcon.Info);
                trayIcon.Tag = "Already shown";
            }
        }

        void interfaceDropDown_SelectedIndexChanged(object sender, EventArgs e)
        {
            var selected = _interfaces.SelectedItem;
            _ipValue.Text = selected.Detail;
            _reachUrl.Text = $"http://{selected.Detail}:{CloudConfig.Port}/";
            _reachUrl.LinkArea = new LinkArea(0, _reachUrl.Text.Length);
            _reachInfo.Location = new Point(_reachUrl.Right + 6, _reachUrl.Top + 3);
            _settings.DefaultNetworkInterfaceId = (string)selected.Value;
            _settings.Save();
        }

        async void broadcastTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                broadcastTimer.Enabled = false;
                var data = ScsTelemetryDataReader.Instance.Read();
                var json = JsonConvert.SerializeObject(data);
                using (var content = new StringContent(json, Utf8, "application/json"))
                {
                    await _broadcastHttpClient.PostAsync(BroadcastUrl, content);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
            broadcastTimer.Enabled = true;
        }

        void openLogFolderToolStripMenuItem_Click(object sender, EventArgs e)
        {
            string logFile = log4net.LogManager.GetRepository().GetAppenders()
                .OfType<log4net.Appender.FileAppender>().First().File;
            Process.Start("explorer.exe", File.Exists(logFile)
                ? $"/select,\"{logFile}\""
                : $"\"{Path.GetDirectoryName(logFile)}\"");
        }

        void startWithWindowsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            trayStartWithWindowsMenuItem.Checked = _startWithWindowsItem.Checked;
            SetStartWithWindows(_startWithWindowsItem.Checked);
        }

        void trayStartWithWindowsMenuItem_Click(object sender, EventArgs e)
        {
            _startWithWindowsItem.Checked = trayStartWithWindowsMenuItem.Checked;
            SetStartWithWindows(trayStartWithWindowsMenuItem.Checked);
        }

        const string StartupRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string StartupValueName = "TruckSimGPS";

        bool IsStartWithWindowsEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, false))
            {
                return key?.GetValue(StartupValueName) != null;
            }
        }

        void SetStartWithWindows(bool enabled)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, true))
            {
                if (enabled)
                {
                    string exePath = Process.GetCurrentProcess().MainModule.FileName;
                    key.SetValue(StartupValueName, $"\"{exePath}\" -minimized");
                }
                else
                {
                    key.DeleteValue(StartupValueName, false);
                }
            }
        }

        void rerunSetupToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                if (Ets2ProcessHelper.IsEts2Running)
                {
                    DarkMessageBox.Show(this, L.Get("setup.game_running"), L.Get("setup.warning_title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                string originalEts2Path = _settings.Ets2GamePath;
                string originalAtsPath = _settings.AtsGamePath;
                bool clearedPaths = false;
                if (originalEts2Path == "N/A")
                {
                    _settings.Ets2GamePath = null;
                    clearedPaths = true;
                }
                if (originalAtsPath == "N/A")
                {
                    _settings.AtsGamePath = null;
                    clearedPaths = true;
                }
                if (clearedPaths)
                    _settings.Save();

                statusUpdateTimer.Enabled = false;
                try
                {
                    Program.ForceSetupMode = true;
                    var result = new SetupForm().ShowDialog(this);
                    if (result == DialogResult.OK)
                    {
                        statusUpdateTimer_Tick(this, EventArgs.Empty);
                        TryAutoCorrectGamePath();
                    }
                    else if (originalAtsPath == "N/A")
                    {
                        _settings.AtsGamePath = originalAtsPath;
                        _settings.Save();
                    }
                }
                finally
                {
                    statusUpdateTimer.Enabled = true;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                ex.ShowAsMessageBox(this, L.Get("setup.error_title"));
                statusUpdateTimer.Enabled = true;
            }
        }

        sealed class UpdateInfo
        {
            public string CurrentVersion;
            public string ChangelogUrl;
            public string DownloadUrl;
            public string InstallerArgs;
            public Version InstalledVersion;
            public bool IsUpdateAvailable;
        }

        void CheckForUpdates(bool reportErrors = false)
        {
            _hasCheckedForUpdates = true;

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using (var client = new WebClient())
                    {
                        client.Encoding = Encoding.UTF8;
                        client.Headers[HttpRequestHeader.UserAgent] = "TruckSimGPS-Server";
                        string json = client.DownloadString(
                            "https://api.github.com/repos/TruckSim-GPS/trucksim-gps-server/releases/latest");

                        var release = JObject.Parse(json);
                        string tagName = release.Value<string>("tag_name") ?? "";
                        string version = tagName.TrimStart('v');
                        string changelogUrl = release.Value<string>("html_url");
                        string downloadUrl = changelogUrl;
                        string releaseBody = release.Value<string>("body");

                        var assets = release["assets"] as JArray;
                        if (assets != null)
                        {
                            foreach (var asset in assets)
                            {
                                string name = asset.Value<string>("name") ?? "";
                                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                {
                                    downloadUrl = asset.Value<string>("browser_download_url");
                                    break;
                                }
                            }
                        }

                        bool hasInstaller = downloadUrl != changelogUrl;
                        var installedVersion = Assembly.GetEntryAssembly().GetName().Version;

                        var info = new UpdateInfo
                        {
                            CurrentVersion = version,
                            ChangelogUrl = changelogUrl,
                            DownloadUrl = downloadUrl,
                            InstallerArgs = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
                            InstalledVersion = installedVersion,
                            IsUpdateAvailable = hasInstaller && new Version(version) > installedVersion
                        };

                        BeginInvoke(new Action(() => ShowUpdateResult(info, releaseBody, reportErrors)));
                    }
                }
                catch (Exception)
                {
                    if (reportErrors)
                        BeginInvoke(new Action(() =>
                            DarkMessageBox.Show(this, L.Get("update.unreachable"), L.Get("update.unreachable_title"),
                                MessageBoxButtons.OK, MessageBoxIcon.Warning)));
                }
            });
        }

        void ShowUpdateResult(UpdateInfo info, string releaseBody, bool reportErrors)
        {
            if (!info.IsUpdateAvailable)
            {
                if (reportErrors)
                    DarkMessageBox.Show(this, L.Get("update.latest", info.InstalledVersion.ToString(3)), L.Get("update.latest_title"),
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var form = new UpdateForm(info.CurrentVersion, info.InstalledVersion.ToString(), releaseBody, info.ChangelogUrl))
            {
                if (form.ShowDialog(this) == DialogResult.OK)
                {
                    if (DownloadVerifyAndLaunchUpdate(info))
                        Application.Exit();
                }
            }
        }

        // True when the verified installer was launched and the caller should exit. The file stays
        // locked (FileShare.Read) from download through Process.Start so it cannot be swapped in between.
        bool DownloadVerifyAndLaunchUpdate(UpdateInfo info)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), $"TruckSimGPS_Update_{Guid.NewGuid():N}.exe");

            // The download starts from the dialog's Shown event so DownloadFileCompleted cannot
            // fire before ShowDialog has a running message pump.
            Exception downloadError = null;
            using (var dialog = new UpdateDownloadDialog())
            using (var client = new WebClient())
            {
                client.Headers[HttpRequestHeader.UserAgent] = "TruckSimGPS-Server";
                client.DownloadProgressChanged += (s, e) => dialog.SetProgress(e.ProgressPercentage);
                client.DownloadFileCompleted += (s, e) =>
                {
                    downloadError = e.Error;
                    dialog.MarkDone();
                };
                dialog.Shown += (s, e) => client.DownloadFileAsync(new Uri(info.DownloadUrl), tempPath);
                dialog.ShowDialog(this);
            }

            if (downloadError != null)
            {
                Log.Error("Update download failed", downloadError);
                SafeDelete(tempPath);
                DarkMessageBox.Show(this, L.Get("update.download_failed"), L.Get("update.failed_title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            FileStream lockHandle;
            try
            {
                lockHandle = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (Exception ex)
            {
                Log.Error("Could not open downloaded update for verification", ex);
                SafeDelete(tempPath);
                DarkMessageBox.Show(this, L.Get("update.open_failed"), L.Get("update.failed_title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            using (lockHandle)
            {
                var verification = SignatureVerifier.Verify(tempPath);
                if (!verification.IsValid)
                {
                    Log.ErrorFormat("Update rejected, signature verification failed. Reason: {0}. Signer: {1}",
                        verification.FailureReason, verification.SignerSubject ?? "<unknown>");
                    lockHandle.Dispose();
                    SafeDelete(tempPath);
                    DarkMessageBox.Show(this, L.Get("update.rejected"), L.Get("update.rejected_title"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }

                // Downgrade guard: a substituted release asset must not point users at an older signed installer.
                string downgradeReason;
                if (!CheckInstallerIsNotADowngrade(tempPath, info.InstalledVersion, out downgradeReason))
                {
                    Log.ErrorFormat("Update rejected, downgrade guard failed: {0}", downgradeReason);
                    lockHandle.Dispose();
                    SafeDelete(tempPath);
                    DarkMessageBox.Show(this, L.Get("update.rejected"), L.Get("update.rejected_title"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }

                Log.InfoFormat("Update signature verified. Signer: {0}", verification.SignerSubject);

                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = tempPath,
                        Arguments = info.InstallerArgs ?? string.Empty,
                        UseShellExecute = true
                    });
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error("Failed to launch verified update installer", ex);
                    lockHandle.Dispose();
                    SafeDelete(tempPath);
                    DarkMessageBox.Show(this, L.Get("update.launch_failed", ex.Message), L.Get("update.failed_title"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }
            }
        }

        static bool CheckInstallerIsNotADowngrade(string installerPath, Version currentVersion, out string reason)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(installerPath);
                if (string.IsNullOrEmpty(info.ProductVersion))
                {
                    reason = "Installer is missing ProductVersion metadata";
                    return false;
                }

                Version downloadedVersion;
                if (!Version.TryParse(info.ProductVersion, out downloadedVersion))
                {
                    reason = $"Could not parse installer ProductVersion '{info.ProductVersion}'";
                    return false;
                }

                if (downloadedVersion < currentVersion)
                {
                    reason = $"Installer version {downloadedVersion} is older than installed version {currentVersion}";
                    return false;
                }

                reason = null;
                return true;
            }
            catch (Exception ex)
            {
                reason = $"Could not read installer version: {ex.Message}";
                return false;
            }
        }

        static void SafeDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                Log.WarnFormat("Could not delete temp file '{0}': {1}", path, ex.Message);
            }
        }

        sealed class UpdateDownloadDialog : DarkDialog
        {
            readonly DarkProgressBar _bar;

            public UpdateDownloadDialog()
            {
                Text = L.Get("update.downloading_title");
                ControlBox = false;
                ClientSize = new Size(400, 104);

                var label = new Label
                {
                    Text = L.Get("update.downloading"),
                    AutoSize = false,
                    Size = new Size(352, 34),
                    Location = new Point(24, 20)
                };

                _bar = new DarkProgressBar
                {
                    Location = new Point(24, 68),
                    Size = new Size(352, 8)
                };

                Controls.Add(label);
                Controls.Add(_bar);
            }

            public void SetProgress(int percentage)
            {
                _bar.Value = percentage;
            }

            public void MarkDone()
            {
                _bar.Value = 100;
                Close();
            }
        }

        void UpdatePluginRow(PluginRow row)
        {
            string gamePath = row.Game == "ETS2" ? _settings.Ets2GamePath : _settings.AtsGamePath;
            string statusKey;
            var result = CheckGamePluginStatus(row.Game, gamePath, out statusKey);
            row.Status.Text = L.Get(statusKey);
            row.Status.ForeColor = result == PluginValidationResult.Valid ? DarkTheme.Green
                : result == PluginValidationResult.PluginMissing ? DarkTheme.Orange
                : statusKey == "plugin.not_configured" ? DarkTheme.TextMuted
                : DarkTheme.Red;
            row.Install.Visible = result == PluginValidationResult.PluginMissing;
            row.Install.Location = new Point(_pluginCard.Width - 16 - row.Install.Width, row.Name.Top - 1);
            bool showPath = !string.IsNullOrEmpty(gamePath) && gamePath != "N/A";
            row.Path.Text = showPath ? " · " + gamePath : "";
            row.Path.Location = new Point(row.Status.Right, row.Status.Top);
            int right = row.Install.Visible ? row.Install.Left - 10 : _pluginCard.Width - 16;
            row.Path.Width = Math.Max(0, right - row.Path.Left);
        }

        void TryAutoCorrectGamePath()
        {
            try
            {
                if (UseTestTelemetryData || !Ets2ProcessHelper.IsEts2Running)
                    return;

                string runningGame = Ets2ProcessHelper.LastRunningGameName;
                string detectedPath = Ets2ProcessHelper.LastRunningGamePath;
                if (string.IsNullOrEmpty(runningGame) || string.IsNullOrEmpty(detectedPath))
                    return;

                string currentStoredPath = runningGame == "ETS2" ? _settings.Ets2GamePath : _settings.AtsGamePath;
                if (string.Equals(currentStoredPath, detectedPath, StringComparison.OrdinalIgnoreCase))
                    return;
                if (!IsValidGamePath(detectedPath, runningGame))
                    return;

                if (runningGame == "ETS2")
                    _settings.Ets2GamePath = detectedPath;
                else
                    _settings.AtsGamePath = detectedPath;
                _settings.Save();
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        bool IsValidGamePath(string gamePath, string gameName = null)
        {
            if (string.IsNullOrEmpty(gamePath))
                return false;

            try
            {
                if (!File.Exists(Path.Combine(gamePath, "base.scs")))
                    return false;
                if (!Directory.Exists(Path.Combine(gamePath, "bin")))
                    return false;
                if (!string.IsNullOrEmpty(gameName))
                {
                    string gameExeName = gameName == "ETS2" ? "eurotrucks2.exe" : "amtrucks.exe";
                    return File.Exists(Path.Combine(gamePath, "bin", "win_x64", gameExeName));
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        enum PluginValidationResult
        {
            Valid,
            InvalidPath,
            PluginMissing
        }

        PluginValidationResult CheckGamePluginStatus(string gameName, string gamePath, out string statusKey)
        {
            try
            {
                if (string.IsNullOrEmpty(gamePath) || gamePath == "N/A")
                {
                    statusKey = "plugin.not_configured";
                    return PluginValidationResult.InvalidPath;
                }

                if (!IsValidGamePath(gamePath, gameName))
                {
                    statusKey = "plugin.invalid_path";
                    return PluginValidationResult.InvalidPath;
                }

                switch (PluginSetup.GetPluginState(gamePath))
                {
                    case PluginSetup.PluginState.Valid:
                        statusKey = "plugin.installed";
                        return PluginValidationResult.Valid;
                    case PluginSetup.PluginState.NotInstalled:
                    case PluginSetup.PluginState.Outdated:
                        statusKey = "plugin.not_installed";
                        return PluginValidationResult.PluginMissing;
                    default:
                        statusKey = "plugin.install_corrupted";
                        return PluginValidationResult.InvalidPath;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                statusKey = "plugin.invalid_path";
                return PluginValidationResult.InvalidPath;
            }
        }

        void CopyPluginsForGame(string gameName)
        {
            try
            {
                string gamePath = gameName == "ETS2" ? _settings.Ets2GamePath : _settings.AtsGamePath;
                if (string.IsNullOrEmpty(gamePath) || gamePath == "N/A" || !IsValidGamePath(gamePath, gameName))
                {
                    DarkMessageBox.Show(this, L.Get("plugin.copy_invalid_path", gameName), L.Get("plugin.copy_title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                string destX86Path = Path.Combine(gamePath, @"bin\win_x86\plugins", PluginSetup.TelemetryDllName);
                string destX64Path = Path.Combine(gamePath, @"bin\win_x64\plugins", PluginSetup.TelemetryDllName);
                Directory.CreateDirectory(Path.GetDirectoryName(destX86Path));
                Directory.CreateDirectory(Path.GetDirectoryName(destX64Path));

                Log.InfoFormat("Copying {1} x86 plugin DLL file to: {0}", destX86Path, gameName);
                File.Copy(PluginSetup.LocalX86PluginPath, destX86Path, true);
                Log.InfoFormat("Copying {1} x64 plugin DLL file to: {0}", destX64Path, gameName);
                File.Copy(PluginSetup.LocalX64PluginPath, destX64Path, true);

                DarkMessageBox.Show(this, L.Get("plugin.copy_success", gameName, destX86Path, destX64Path), L.Get("plugin.copy_title"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                UpdatePluginRow(gameName == "ETS2" ? _ets2Row : _atsRow);
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                DarkMessageBox.Show(this, L.Get("plugin.copy_failed", gameName, ex.Message), L.Get("plugin.copy_title"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
