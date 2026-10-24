using System;
using System.Collections.Generic;
using System.Configuration;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows.Forms;
using Funbit.Ets.Telemetry.Server.Helpers;
using Funbit.Ets.Telemetry.Server.Setup;
using Funbit.Ets.Telemetry.Server.Ui;

namespace Funbit.Ets.Telemetry.Server
{
    public partial class SetupForm : DarkDialog
    {
        static readonly log4net.ILog Log = log4net.LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        bool _setupFinished;

        readonly Dictionary<ISetup, StepIcon> _setupStatusImages = new Dictionary<ISetup, StepIcon>();

        public SetupForm()
        {
            InitializeComponent();

            DialogResult = DialogResult.OK;

            string port = ConfigurationManager.AppSettings["Port"];
            string mode = Program.UninstallMode ? "uninstall" : "install";
            Text = L.Get("setup.title");
            statusTitle.Text = L.Get("setup.status_group");
            vcredistStatusLabel.Text = L.Get("setup.step_vcredist");
            pluginStatusLabel.Text = L.Get("setup.step_plugin_" + mode);
            firewallStatusLabel.Text = L.Get("setup.step_firewall_" + mode, port);
            urlReservationStatusLabel.Text = L.Get("setup.step_acl_" + mode, $"http://+:{port}/");
            okButton.Text = L.Get("setup." + mode);
            helpLabel.Text = L.Get("setup.help");
        }

        void SetStepStatus(ISetup step, SetupStatus status)
        {
            SetupStatus inverseStatus = status;
            if (Program.UninstallMode)
            {
                switch (status)
                {
                    case SetupStatus.Installed:
                        inverseStatus = SetupStatus.Uninstalled;
                        break;
                    case SetupStatus.Uninstalled:
                        inverseStatus = SetupStatus.Installed;
                        break;
                }
            }
            if (_setupStatusImages.ContainsKey(step))
                _setupStatusImages[step].Status = inverseStatus;
        }
        
        private void SetupForm_Load(object sender, EventArgs e)
        {
            // show application version 
            Text += @" " + AssemblyHelper.Version;

            // make sure that game is not running
            if (Ets2ProcessHelper.IsEts2Running)
            {
                DarkMessageBox.Show(this, L.Get("setup.game_running"), L.Get("setup.warning_title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.Abort;
                return;
            }

            // make sure that we have Administrator rights
            if (!Uac.IsProcessElevated())
            {
                try
                {
                    // we have to restart the setup with Administrator privileges
                    // Pass -rerunsetup flag if this was triggered manually
                    if (Program.ForceSetupMode)
                        Uac.RestartElevated("-rerunsetup");
                    else
                        Uac.RestartElevated();
                    DialogResult = DialogResult.Abort;
                    // exit current process once elevated instance is launched
                    Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    Log.Error(ex);
                    DarkMessageBox.Show(this, L.Get("setup.elevation_required"), L.Get("setup.elevation_title"), MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                    DialogResult = DialogResult.Abort;
                    return;
                }
            }
            
            // update UI
            foreach (var step in SetupManager.Steps)
            {
                if (step is VCRedistSetup)
                    _setupStatusImages.Add(step, vcredistStatusImage);
                else if (step is PluginSetup)
                    _setupStatusImages.Add(step, pluginStatusImage);
                else if (step is FirewallSetup)
                    _setupStatusImages.Add(step, firewallStatusImage);
                else if (step is UrlReservationSetup)
                    _setupStatusImages.Add(step, urlReservationStatusImage);
                SetStepStatus(step, step.Status);
            }
        }

        private void okButton_Click(object sender, EventArgs e)
        {
            okButton.Enabled = false;
            _setupFinished = true;

            foreach (var step in SetupManager.Steps)
            {
                try
                {
                    SetStepStatus(step, Program.UninstallMode ? step.Uninstall(this) : step.Install(this));
                }
                catch (Exception ex)
                {
                    _setupFinished = false;
                    Log.Error(ex);
                    ex.ShowAsMessageBox(this, L.Get("setup.error_title"));
                }
            }

            string message = L.Get("setup.done_" + (Program.UninstallMode ? "uninstalled" : "installed") + (_setupFinished ? "" : "_errors"));

            if (Program.UninstallMode)
                Helpers.Settings.Clear();

            DarkMessageBox.Show(this, message, L.Get("setup.done_title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }

        private void SetupForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason != CloseReason.UserClosing || Program.UninstallMode)
                DialogResult = DialogResult.Abort;
        }

        private void helpLabel_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            ProcessHelper.OpenUrl("https://github.com/trucksim-gps/trucksim-gps-server/");
        }

        sealed class StepIcon : Control
        {
            SetupStatus _status;

            public StepIcon()
            {
                DoubleBuffered = true;
                Size = new Size(24, 24);
                BackColor = DarkTheme.Card;
            }

            public SetupStatus Status
            {
                get { return _status; }
                set { _status = value; Invalidate(); }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(BackColor);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var circle = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
                if (_status == SetupStatus.Uninstalled)
                {
                    using (var pen = new Pen(DarkTheme.ControlBorder, 1.5f))
                        g.DrawEllipse(pen, RectangleF.Inflate(circle, -1, -1));
                    return;
                }
                bool done = _status == SetupStatus.Installed;
                using (var brush = new SolidBrush(done ? DarkTheme.GreenBg : DarkTheme.RedBg))
                    g.FillEllipse(brush, circle);
                float c = Width / 2f;
                using (var pen = new Pen(done ? DarkTheme.Green : DarkTheme.Red, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    if (done)
                        g.DrawLines(pen, new[] { new PointF(c - 5, c), new PointF(c - 1.5f, c + 3.5f), new PointF(c + 5, c - 3.5f) });
                    else
                    {
                        g.DrawLine(pen, c - 4, c - 4, c + 4, c + 4);
                        g.DrawLine(pen, c + 4, c - 4, c - 4, c + 4);
                    }
                }
            }
        }
    }
}
