using System;
using System.Collections.Generic;
using System.Configuration;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Funbit.Ets.Telemetry.Server.Helpers;
using Funbit.Ets.Telemetry.Server.Properties;
using Funbit.Ets.Telemetry.Server.Setup;

namespace Funbit.Ets.Telemetry.Server
{
    public partial class SetupForm : Form
    {
        static readonly log4net.ILog Log = log4net.LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        bool _setupFinished;

        readonly Dictionary<ISetup, PictureBox> _setupStatusImages = new Dictionary<ISetup, PictureBox>();

        public SetupForm()
        {
            InitializeComponent();

            DialogResult = DialogResult.OK;

            string port = ConfigurationManager.AppSettings["Port"];
            string mode = Program.UninstallMode ? "uninstall" : "install";
            Text = L.Get("setup.title");
            groupBox.Text = L.Get("setup.status_group");
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
            // convert status enumeration to images
            Bitmap statusImage = inverseStatus == SetupStatus.Uninstalled
                                     ? Resources.StatusIcon
                                     : (inverseStatus == SetupStatus.Installed
                                            ? Resources.SuccessStatusIcon
                                            : Resources.FailureStatusIcon);
            if (_setupStatusImages.ContainsKey(step))
                _setupStatusImages[step].Image = statusImage;
        }
        
        private void SetupForm_Load(object sender, EventArgs e)
        {
            // show application version 
            Text += @" " + AssemblyHelper.Version;

            // make sure that game is not running
            if (Ets2ProcessHelper.IsEts2Running)
            {
                MessageBox.Show(this, L.Get("setup.game_running"), L.Get("setup.warning_title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                    MessageBox.Show(this, L.Get("setup.elevation_required"), L.Get("setup.elevation_title"), MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
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

            MessageBox.Show(this, message, L.Get("setup.done_title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
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
    }
}
