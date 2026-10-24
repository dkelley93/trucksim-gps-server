namespace Funbit.Ets.Telemetry.Server
{
    partial class SetupForm
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(SetupForm));
            this.okButton = new Funbit.Ets.Telemetry.Server.Ui.DarkButton();
            this.statusCard = new Funbit.Ets.Telemetry.Server.Ui.Card();
            this.statusTitle = new System.Windows.Forms.Label();
            this.vcredistStatusImage = new StepIcon();
            this.vcredistStatusLabel = new System.Windows.Forms.Label();
            this.pluginStatusImage = new StepIcon();
            this.pluginStatusLabel = new System.Windows.Forms.Label();
            this.firewallStatusImage = new StepIcon();
            this.firewallStatusLabel = new System.Windows.Forms.Label();
            this.urlReservationStatusImage = new StepIcon();
            this.urlReservationStatusLabel = new System.Windows.Forms.Label();
            this.footer = new System.Windows.Forms.Panel();
            this.helpLabel = new Funbit.Ets.Telemetry.Server.Ui.LinkText();
            this.statusCard.SuspendLayout();
            this.footer.SuspendLayout();
            this.SuspendLayout();
            //
            // statusCard
            //
            this.statusCard.Controls.Add(this.statusTitle);
            this.statusCard.Controls.Add(this.vcredistStatusImage);
            this.statusCard.Controls.Add(this.vcredistStatusLabel);
            this.statusCard.Controls.Add(this.pluginStatusImage);
            this.statusCard.Controls.Add(this.pluginStatusLabel);
            this.statusCard.Controls.Add(this.firewallStatusImage);
            this.statusCard.Controls.Add(this.firewallStatusLabel);
            this.statusCard.Controls.Add(this.urlReservationStatusImage);
            this.statusCard.Controls.Add(this.urlReservationStatusLabel);
            this.statusCard.Location = new System.Drawing.Point(16, 16);
            this.statusCard.Size = new System.Drawing.Size(360, 236);
            //
            // statusTitle
            //
            this.statusTitle.AutoSize = true;
            this.statusTitle.BackColor = Funbit.Ets.Telemetry.Server.Ui.DarkTheme.Card;
            this.statusTitle.Font = Funbit.Ets.Telemetry.Server.Ui.DarkTheme.Title;
            this.statusTitle.ForeColor = System.Drawing.Color.White;
            this.statusTitle.Location = new System.Drawing.Point(16, 14);
            //
            // step rows
            //
            this.vcredistStatusImage.Location = new System.Drawing.Point(16, 52);
            this.vcredistStatusLabel.Location = new System.Drawing.Point(52, 55);
            this.pluginStatusImage.Location = new System.Drawing.Point(16, 96);
            this.pluginStatusLabel.Location = new System.Drawing.Point(52, 99);
            this.firewallStatusImage.Location = new System.Drawing.Point(16, 140);
            this.firewallStatusLabel.Location = new System.Drawing.Point(52, 143);
            this.urlReservationStatusImage.Location = new System.Drawing.Point(16, 184);
            this.urlReservationStatusLabel.Location = new System.Drawing.Point(52, 187);
            foreach (var label in new[] { this.vcredistStatusLabel, this.pluginStatusLabel, this.firewallStatusLabel, this.urlReservationStatusLabel })
            {
                label.AutoSize = true;
                label.MaximumSize = new System.Drawing.Size(292, 0);
                label.BackColor = Funbit.Ets.Telemetry.Server.Ui.DarkTheme.Card;
                label.ForeColor = Funbit.Ets.Telemetry.Server.Ui.DarkTheme.Text;
            }
            //
            // footer
            //
            this.footer.BackColor = Funbit.Ets.Telemetry.Server.Ui.DarkTheme.Header;
            this.footer.Controls.Add(this.helpLabel);
            this.footer.Controls.Add(this.okButton);
            this.footer.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.footer.Size = new System.Drawing.Size(392, 64);
            //
            // helpLabel
            //
            this.helpLabel.Location = new System.Drawing.Point(22, 24);
            this.helpLabel.TabIndex = 17;
            this.helpLabel.TabStop = true;
            this.helpLabel.LinkClicked += new System.Windows.Forms.LinkLabelLinkClickedEventHandler(this.helpLabel_LinkClicked);
            //
            // okButton
            //
            this.okButton.BackColor = Funbit.Ets.Telemetry.Server.Ui.DarkTheme.Header;
            this.okButton.Location = new System.Drawing.Point(256, 15);
            this.okButton.Size = new System.Drawing.Size(120, 34);
            this.okButton.TabIndex = 2;
            this.okButton.Click += new System.EventHandler(this.okButton_Click);
            //
            // SetupForm
            //
            this.ClientSize = new System.Drawing.Size(392, 332);
            this.Controls.Add(this.statusCard);
            this.Controls.Add(this.footer);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.ShowIcon = true;
            this.ShowInTaskbar = true;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.TopMost = true;
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.SetupForm_FormClosing);
            this.Load += new System.EventHandler(this.SetupForm_Load);
            this.statusCard.ResumeLayout(false);
            this.statusCard.PerformLayout();
            this.footer.ResumeLayout(false);
            this.footer.PerformLayout();
            this.ResumeLayout(false);
        }

        #endregion

        private Funbit.Ets.Telemetry.Server.Ui.DarkButton okButton;
        private Funbit.Ets.Telemetry.Server.Ui.Card statusCard;
        private System.Windows.Forms.Label statusTitle;
        private StepIcon vcredistStatusImage;
        private System.Windows.Forms.Label vcredistStatusLabel;
        private StepIcon pluginStatusImage;
        private System.Windows.Forms.Label pluginStatusLabel;
        private StepIcon firewallStatusImage;
        private System.Windows.Forms.Label firewallStatusLabel;
        private StepIcon urlReservationStatusImage;
        private System.Windows.Forms.Label urlReservationStatusLabel;
        private System.Windows.Forms.Panel footer;
        private Funbit.Ets.Telemetry.Server.Ui.LinkText helpLabel;
    }
}