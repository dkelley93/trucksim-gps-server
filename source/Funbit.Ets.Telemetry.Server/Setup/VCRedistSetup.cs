using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Funbit.Ets.Telemetry.Server.Helpers;
using Funbit.Ets.Telemetry.Server.Ui;
using Microsoft.Win32;

namespace Funbit.Ets.Telemetry.Server.Setup
{
    public class VCRedistSetup : ISetup
    {
        static readonly log4net.ILog Log = log4net.LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        const string RegistryKeyPath = @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64";
        const string VCRedistDownloadUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";
        const string VCRedistInfoUrl = "https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist";

        SetupStatus _status;

        public VCRedistSetup()
        {
            _status = IsVC2022Installed() ? SetupStatus.Installed : SetupStatus.Uninstalled;
        }

        public SetupStatus Status => _status;

        public SetupStatus Install(IWin32Window owner)
        {
            if (IsVC2022Installed())
            {
                _status = SetupStatus.Installed;
                return _status;
            }

            ShowInstallPrompt(owner);

            // Re-check after user action
            _status = IsVC2022Installed() ? SetupStatus.Installed : SetupStatus.Uninstalled;
            return _status;
        }

        public SetupStatus Uninstall(IWin32Window owner)
        {
            // VC++ Redistributable uninstall is not handled by this app
            // It's a system-wide component that should be managed by Windows
            return SetupStatus.Installed;
        }

        static bool IsVC2022Installed()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(RegistryKeyPath))
                {
                    if (key == null)
                        return false;

                    object bldValue = key.GetValue("Bld");
                    object installedValue = key.GetValue("Installed");

                    if (bldValue != null && installedValue != null)
                    {
                        int buildNumber = (int)bldValue;
                        int installed = (int)installedValue;
                        
                        // VC++ 2019+ is compatible with 2022
                        return installed == 1 && buildNumber >= 27000;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to check VC++ Redistributable: {ex.Message}");
            }

            return false;
        }

        static void ShowInstallPrompt(IWin32Window owner)
        {
            using (var dialog = new DarkDialog { Text = L.Get("vcredist.title"), ShowInTaskbar = true, TopMost = true })
            {
                var icon = new DialogIcon { Kind = MessageBoxIcon.Warning, Location = new Point(24, 24) };

                var messageLabel = new Label { Text = L.Get("vcredist.message"), Location = new Point(icon.Right + 16, 24) };
                messageLabel.Size = TextRenderer.MeasureText(messageLabel.Text, DarkTheme.Body, new Size(380, 0), TextFormatFlags.WordBreak);

                var footer = new Panel { BackColor = DarkTheme.Header, Height = 64, Dock = DockStyle.Bottom };
                dialog.ClientSize = new Size(messageLabel.Right + 24, messageLabel.Bottom + 24 + footer.Height);

                var infoLink = new LinkText { Text = L.Get("vcredist.more_info"), Location = new Point(22, 24) };

                var downloadButton = new DarkButton { Text = L.Get("vcredist.download"), BackColor = DarkTheme.Header, Height = 34 };
                downloadButton.Width = Math.Max(110, DarkTheme.TextWidth(downloadButton, downloadButton.Text, downloadButton.Font) + 32);
                downloadButton.Location = new Point(dialog.ClientSize.Width - 16 - downloadButton.Width, 15);

                var cancelButton = new DarkButton { Text = L.Get("vcredist.skip"), Primary = false, Font = DarkTheme.Body, BackColor = DarkTheme.Header, Height = 34, DialogResult = DialogResult.Cancel };
                cancelButton.Width = Math.Max(96, DarkTheme.TextWidth(cancelButton, cancelButton.Text, cancelButton.Font) + 32);
                cancelButton.Location = new Point(downloadButton.Left - 8 - cancelButton.Width, 15);

                // Download button - opens direct download link
                downloadButton.Click += (s, e) =>
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = VCRedistDownloadUrl,
                            UseShellExecute = true
                        });

                        DarkMessageBox.Show(dialog, L.Get("vcredist.download_started"), L.Get("vcredist.download_started_title"),
                            MessageBoxButtons.OK, MessageBoxIcon.Information);

                        dialog.DialogResult = DialogResult.OK;
                        dialog.Close();

                        // Always exit the application so user must restart after installing
                        Environment.Exit(0);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Failed to open download link: {ex.Message}");
                        DarkMessageBox.Show(dialog, L.Get("vcredist.link_failed", VCRedistDownloadUrl), L.Get("setup.error_title"),
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                };

                // Info link - opens Microsoft documentation
                infoLink.LinkClicked += (s, e) =>
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = VCRedistInfoUrl,
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Failed to open info link: {ex.Message}");
                        DarkMessageBox.Show(dialog, L.Get("vcredist.link_failed", VCRedistInfoUrl), L.Get("setup.error_title"),
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                };

                footer.Controls.AddRange(new Control[] { infoLink, cancelButton, downloadButton });
                dialog.Controls.AddRange(new Control[] { icon, messageLabel, footer });
                dialog.AcceptButton = downloadButton;
                dialog.CancelButton = cancelButton;
                dialog.ActiveControl = downloadButton;

                dialog.ShowDialog(owner);
            }
        }

        public static bool CheckAndPromptOnStartup(IWin32Window owner)
        {
            if (!IsVC2022Installed())
            {
                ShowInstallPrompt(owner);
                // If we're still here, user clicked "Skip for Now"
                return true; // Continue running
            }
            return true; // VC++ is installed, continue running
        }
    }
}
