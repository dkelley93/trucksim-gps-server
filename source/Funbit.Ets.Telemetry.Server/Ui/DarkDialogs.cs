using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Media;
using System.Windows.Forms;
using Funbit.Ets.Telemetry.Server.Helpers;

namespace Funbit.Ets.Telemetry.Server.Ui
{
    public class DarkDialog : Form
    {
        public DarkDialog()
        {
            BackColor = DarkTheme.Window;
            ForeColor = DarkTheme.Text;
            Font = DarkTheme.Body;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            DarkTheme.UseDarkTitleBar(this);
        }
    }

    public sealed class DialogIcon : Control
    {
        MessageBoxIcon _kind = MessageBoxIcon.Information;

        public DialogIcon()
        {
            DoubleBuffered = true;
            Size = new Size(32, 32);
            BackColor = DarkTheme.Window;
        }

        public MessageBoxIcon Kind
        {
            get { return _kind; }
            set { _kind = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            Color color, background;
            switch (_kind)
            {
                case MessageBoxIcon.Error:
                    color = DarkTheme.Red;
                    background = DarkTheme.RedBg;
                    break;
                case MessageBoxIcon.Warning:
                    color = DarkTheme.Amber;
                    background = DarkTheme.AmberBg;
                    break;
                default:
                    color = DarkTheme.Link;
                    background = DarkTheme.MenuHover;
                    break;
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(background))
                g.FillEllipse(brush, 0, 0, Width - 1, Height - 1);
            float cx = Width / 2f, cy = Height / 2f, s = Width / 32f;
            using (var pen = new Pen(color, 2.6f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                if (_kind == MessageBoxIcon.Error)
                {
                    g.DrawLine(pen, cx - 5 * s, cy - 5 * s, cx + 5 * s, cy + 5 * s);
                    g.DrawLine(pen, cx + 5 * s, cy - 5 * s, cx - 5 * s, cy + 5 * s);
                }
                else if (_kind == MessageBoxIcon.Question)
                {
                    using (var font = new Font(DarkTheme.Brand.FontFamily, 18f * s, FontStyle.Bold, GraphicsUnit.Pixel))
                        TextRenderer.DrawText(g, "?", font, new Rectangle(0, 0, Width, Height), color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
                else
                {
                    bool warning = _kind == MessageBoxIcon.Warning;
                    float top = warning ? cy - 7 * s : cy - 2 * s;
                    float bottom = warning ? cy + 2 * s : cy + 7 * s;
                    float dot = warning ? cy + 7 * s : cy - 7 * s;
                    g.DrawLine(pen, cx, top, cx, bottom);
                    g.DrawLine(pen, cx, dot, cx, dot + 0.1f);
                }
            }
        }
    }

    public static class DarkMessageBox
    {
        const int MaxTextWidth = 380;

        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            return Show(owner, text, caption, buttons, icon, MessageBoxDefaultButton.Button1);
        }

        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon,
                                        MessageBoxDefaultButton defaultButton)
        {
            var results = Results(buttons);
            return Show(owner, text, caption, icon, results, Array.ConvertAll(results, Label), defaultButton);
        }

        public static DialogResult Ask(IWin32Window owner, string text, string caption, string yes, string no)
        {
            return Show(owner, text, caption, MessageBoxIcon.Question, new[] { DialogResult.Yes, DialogResult.No }, new[] { yes, no },
                MessageBoxDefaultButton.Button1);
        }

        static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxIcon icon, DialogResult[] results, string[] labels,
                                 MessageBoxDefaultButton defaultButton)
        {
            using (var dialog = new DarkDialog { Text = caption })
            {
                var iconBox = new DialogIcon { Kind = icon, Location = new Point(24, 24) };
                var label = new Label { Text = text, UseMnemonic = false, ForeColor = DarkTheme.Text, BackColor = DarkTheme.Window, Location = new Point(iconBox.Right + 16, 24) };
                label.Size = TextRenderer.MeasureText(text, DarkTheme.Body, new Size(MaxTextWidth, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                if (label.Height < iconBox.Height)
                    label.Top += (iconBox.Height - label.Height) / 2;

                var footer = new Panel { BackColor = DarkTheme.Header, Height = 60, Dock = DockStyle.Bottom };
                int width = Math.Max(label.Right + 24, 300);
                int x = width - 20;
                int defaultIndex = defaultButton == MessageBoxDefaultButton.Button3 ? 2 : defaultButton == MessageBoxDefaultButton.Button2 ? 1 : 0;
                var created = new List<DarkButton>();
                for (int i = results.Length - 1; i >= 0; i--)
                {
                    var button = new DarkButton
                    {
                        Text = labels[i],
                        DialogResult = results[i],
                        Primary = i == defaultIndex,
                        Font = DarkTheme.Body,
                        BackColor = DarkTheme.Header,
                        Height = 32
                    };
                    button.Width = Math.Max(88, DarkTheme.TextWidth(button, button.Text, button.Font) + 32);
                    x -= button.Width;
                    button.Location = new Point(x, 14);
                    x -= 8;
                    footer.Controls.Add(button);
                    created.Insert(0, button);
                }

                dialog.ClientSize = new Size(width, Math.Max(label.Bottom, iconBox.Bottom) + 24 + footer.Height);
                dialog.Controls.AddRange(new Control[] { iconBox, label, footer });
                dialog.AcceptButton = created[Math.Min(defaultIndex, created.Count - 1)];
                dialog.CancelButton = created.Find(b => b.DialogResult == DialogResult.Cancel || b.DialogResult == DialogResult.No) ?? created[created.Count - 1];
                dialog.ActiveControl = (Control)dialog.AcceptButton;
                if (owner == null)
                    dialog.StartPosition = FormStartPosition.CenterScreen;
                Sound(icon);
                var result = dialog.ShowDialog(owner);
                return result == DialogResult.Cancel && Array.IndexOf(results, DialogResult.Cancel) < 0 ? dialog.CancelButton.DialogResult : result;
            }
        }

        static DialogResult[] Results(MessageBoxButtons buttons)
        {
            switch (buttons)
            {
                case MessageBoxButtons.YesNo: return new[] { DialogResult.Yes, DialogResult.No };
                case MessageBoxButtons.YesNoCancel: return new[] { DialogResult.Yes, DialogResult.No, DialogResult.Cancel };
                case MessageBoxButtons.OKCancel: return new[] { DialogResult.OK, DialogResult.Cancel };
                case MessageBoxButtons.OK: return new[] { DialogResult.OK };
                default: throw new NotSupportedException(buttons.ToString());
            }
        }

        static string Label(DialogResult result)
        {
            switch (result)
            {
                case DialogResult.Yes: return L.Get("dialog.yes");
                case DialogResult.No: return L.Get("dialog.no");
                case DialogResult.Cancel: return L.Get("dialog.cancel");
                default: return L.Get("dialog.ok");
            }
        }

        static void Sound(MessageBoxIcon icon)
        {
            switch (icon)
            {
                case MessageBoxIcon.Error: SystemSounds.Hand.Play(); break;
                case MessageBoxIcon.Warning: SystemSounds.Exclamation.Play(); break;
                case MessageBoxIcon.Question: SystemSounds.Question.Play(); break;
                case MessageBoxIcon.Information: SystemSounds.Asterisk.Play(); break;
            }
        }
    }
}
