using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using Funbit.Ets.Telemetry.Server.Helpers;
using Funbit.Ets.Telemetry.Server.Services;

namespace Funbit.Ets.Telemetry.Server.Ui
{
    public sealed class LinkDevicePanel : Card
    {
        const int Pad = 16;
        const int QrInset = 12;
        const int QrMax = 230;
        const int QrMinScale = 2;

        readonly PairingSessionService _pairing;
        readonly Label _title = new Label { AutoSize = true, Font = DarkTheme.PanelTitle, ForeColor = Color.White, BackColor = Color.Transparent };
        readonly Label _countdown = new Label { AutoSize = true, Font = DarkTheme.Body, ForeColor = DarkTheme.TextMuted, BackColor = Color.Transparent };
        readonly RichLabel _description = new RichLabel { Centered = true };
        readonly RichLabel[] _steps = { new RichLabel(), new RichLabel(), new RichLabel() };
        readonly RichLabel _errorText = new RichLabel { Font = DarkTheme.Small, ForeColor = DarkTheme.Text, BoldFont = DarkTheme.SmallBold, BackColor = DarkTheme.RedBg };
        readonly DarkButton _generate = new DarkButton { Height = 40, Radius = 10 };
        readonly DarkButton _cancel = new DarkButton { Height = 32, Primary = false, Font = DarkTheme.SmallBold };
        readonly DarkButton _renew = new DarkButton { Height = 34, Width = 150, Visible = false, BackColor = Color.Transparent };
        readonly Label _expiredLabel = new Label { AutoSize = true, Font = DarkTheme.BodyBold, ForeColor = Color.White, BackColor = Color.Transparent, Visible = false };
        readonly Label _codeHint = new Label { Font = DarkTheme.Small, ForeColor = DarkTheme.TextMuted, BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleCenter, Height = 18 };
        readonly CodeLabel _code = new CodeLabel();
        Bitmap _qr;
        Bitmap _qrBlurred;
        int QrBox => (_qr?.Width ?? 166) + QrInset * 2;
        PairingState _shown = (PairingState)(-1);

        public LinkDevicePanel(PairingSessionService pairing)
        {
            _pairing = pairing;
            Padding = new Padding(Pad);
            _generate.Click += (s, e) => Generate();
            _renew.Click += (s, e) => Generate();
            _cancel.Click += (s, e) => _pairing.Cancel();
            Controls.AddRange(new Control[] { _title, _countdown, _description, _errorText, _generate, _cancel, _renew, _expiredLabel, _codeHint, _code });
            foreach (var step in _steps)
                Controls.Add(step);
            _pairing.StateChanged += () =>
            {
                if (IsHandleCreated)
                    BeginInvoke(new Action(Render));
            };
            ApplyTexts();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Render();
        }

        async void Generate()
        {
            await _pairing.GenerateAsync();
        }

        public void ApplyTexts()
        {
            _title.Text = L.Get("link.title");
            _description.Text = L.Get("link.description");
            for (int i = 0; i < _steps.Length; i++)
                _steps[i].Text = (i + 1) + ". " + L.Get("link.step" + (i + 1));
            _errorText.Text = L.Get("link.error_cloud");
            _cancel.Text = L.Get("link.cancel");
            _renew.Text = L.Get("link.show_new_code");
            _expiredLabel.Text = L.Get("link.code_expired");
            _codeHint.Text = L.Get("link.no_camera");
            _shown = (PairingState)(-1);
            Render();
        }

        public void Tick()
        {
            if (_pairing.State == PairingState.Active)
            {
                var left = _pairing.Remaining;
                _countdown.Text = $"{(int)left.TotalMinutes}:{left.Seconds:00}";
                _countdown.Location = new Point(Width - Pad - _countdown.Width, _title.Top + 1);
            }
        }

        void Render()
        {
            var state = _pairing.State;
            bool relayout = state != _shown;
            _shown = state;
            SuspendLayout();
            int width = Width - Pad * 2;
            bool active = state == PairingState.Active || state == PairingState.Expired;
            Highlight = !active;

            _description.Visible = !active;
            _generate.Visible = !active;
            _errorText.Visible = state == PairingState.Error;
            _countdown.Visible = state == PairingState.Active;
            _cancel.Visible = active;
            _codeHint.Visible = state == PairingState.Active;
            _code.Visible = state == PairingState.Active;
            _renew.Visible = state == PairingState.Expired;
            _expiredLabel.Visible = state == PairingState.Expired;
            foreach (var step in _steps)
                step.Visible = active;

            if (active)
            {
                var session = _pairing.Current;
                if (relayout && session != null)
                {
                    _qr?.Dispose();
                    _qrBlurred?.Dispose();
                    _qr = RenderQrBitmap(session.QrPayload);
                    _qrBlurred = state == PairingState.Expired ? Blur(_qr) : null;
                    _code.Text = session.Code.Substring(0, 3) + "-" + session.Code.Substring(3);
                }
                else if (relayout && state == PairingState.Expired && _qr != null && _qrBlurred == null)
                {
                    _qrBlurred = Blur(_qr);
                }
                _title.Font = DarkTheme.Title;
                _title.Location = new Point(Pad, Pad - 2);
                int y = _title.Bottom + 10;
                foreach (var step in _steps)
                {
                    step.Font = DarkTheme.Note;
                    step.Centered = false;
                    step.Bounds = new Rectangle(Pad, y, width, step.MeasureHeight(width));
                    y = step.Bottom + 2;
                }
                _qrTop = y + 8;
                y = _qrTop + QrBox + 10;
                _codeHint.Bounds = new Rectangle(Pad, y, width, 18);
                _code.Bounds = new Rectangle(Pad, y + 20, width, 32);
                _cancel.Bounds = new Rectangle(Pad, Height - Pad - 32, width, 32);
                _expiredLabel.Location = new Point((Width - _expiredLabel.Width) / 2, _qrTop + QrBox / 2 - 30);
                _renew.Location = new Point((Width - _renew.Width) / 2, _qrTop + QrBox / 2 - 2);
                Tick();
            }
            else
            {
                _title.Font = DarkTheme.PanelTitle;
                _description.Font = DarkTheme.Note;
                int descHeight = _description.MeasureHeight(width);
                int errorTextWidth = width - 10 - 16 - 8 - 10;
                int errorHeight = state == PairingState.Error ? _errorText.MeasureHeight(errorTextWidth) + 16 : 0;
                int total = 54 + 14 + _title.Height + 14 + descHeight + 14 + _generate.Height + (errorHeight > 0 ? errorHeight + 14 : 0);
                int y = (Height - total) / 2;
                _iconTop = y;
                y += 54 + 14;
                _title.Location = new Point((Width - _title.Width) / 2, y);
                y += _title.Height + 14;
                _description.Bounds = new Rectangle(Pad, y, width, descHeight);
                y += descHeight + 14;
                if (errorHeight > 0)
                {
                    _errorBox = new Rectangle(Pad, y, width, errorHeight);
                    _errorText.Bounds = new Rectangle(Pad + 10 + 16 + 8, y + 8, errorTextWidth, errorHeight - 16);
                    y += errorHeight + 14;
                }
                _generate.Bounds = new Rectangle(Pad, y, width, 40);
                _generate.Text = L.Get(state == PairingState.Generating ? "link.generating" : state == PairingState.Error ? "link.try_again" : "link.generate");
                _generate.SetBusy(state == PairingState.Generating);
            }
            ResumeLayout();
            Invalidate();
        }

        int _iconTop;
        int _qrTop;
        Rectangle _errorBox;

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var state = _pairing.State;
            if (state == PairingState.Active || state == PairingState.Expired)
            {
                float x = (Width - QrBox) / 2f;
                var box = new RectangleF(x, _qrTop, QrBox, QrBox);
                if (state == PairingState.Expired)
                {
                    using (var attributes = new ImageAttributes())
                    {
                        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = 0.35f });
                        using (var path = DarkTheme.RoundedRect(box, 10))
                        {
                            g.SetClip(path);
                            using (var white = new SolidBrush(Color.FromArgb(90, Color.White)))
                                g.FillPath(white, path);
                            if (_qrBlurred != null)
                                g.DrawImage(_qrBlurred, new Rectangle((int)x + QrInset, _qrTop + QrInset, QrBox - QrInset * 2, QrBox - QrInset * 2),
                                    0, 0, _qrBlurred.Width, _qrBlurred.Height, GraphicsUnit.Pixel, attributes);
                            g.ResetClip();
                        }
                    }
                }
                else
                {
                    DarkTheme.FillRounded(g, box, 10, Color.White);
                    if (_qr != null)
                        g.DrawImage(_qr, (int)x + QrInset, _qrTop + QrInset);
                }
                return;
            }

            if (state == PairingState.Error)
                PaintErrorBox(g);
            float scale = 54f / 24f;
            float ox = (Width - 54) / 2f, oy = _iconTop;
            using (var pen = new Pen(DarkTheme.Teal, 1.6f * scale))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                using (var path = DarkTheme.RoundedRect(new RectangleF(ox + 7 * scale, oy + 2 * scale, 10 * scale, 20 * scale), 2 * scale))
                    g.DrawPath(pen, path);
                g.DrawLine(pen, ox + 11 * scale, oy + 18 * scale, ox + 13 * scale, oy + 18 * scale);
            }
        }

        void PaintErrorBox(Graphics g)
        {
            DarkTheme.FillRounded(g, new RectangleF(_errorBox.X + 0.5f, _errorBox.Y + 0.5f, _errorBox.Width - 1, _errorBox.Height - 1), 8, DarkTheme.RedBg, DarkTheme.Red);
            using (var pen = new Pen(DarkTheme.Red, 2f))
            {
                pen.LineJoin = LineJoin.Round;
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                float x = _errorBox.X + 10, y = _errorBox.Y + 9;
                g.DrawPolygon(pen, new[] { new PointF(x + 8, y + 2), new PointF(x + 15, y + 14), new PointF(x + 1, y + 14) });
                g.DrawLine(pen, x + 8, y + 7, x + 8, y + 10);
                g.DrawLine(pen, x + 8, y + 12.5f, x + 8, y + 12.6f);
            }
        }

        static Bitmap RenderQrBitmap(string payload)
        {
            using (var generator = new QRCoder.QRCodeGenerator())
            using (var data = generator.CreateQrCode(payload, QRCoder.QRCodeGenerator.ECCLevel.M))
            using (var png = new QRCoder.PngByteQRCode(data))
            using (var stream = new MemoryStream(png.GetGraphic(Math.Max(QrMinScale, QrMax / data.ModuleMatrix.Count))))
            {
                return new Bitmap(stream);
            }
        }

        static Bitmap Blur(Bitmap source)
        {
            int small = Math.Max(8, source.Width / 8);
            using (var tiny = new Bitmap(small, small))
            {
                using (var g = Graphics.FromImage(tiny))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(source, 0, 0, small, small);
                }
                var result = new Bitmap(source.Width, source.Height);
                using (var g = Graphics.FromImage(result))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(tiny, 0, 0, source.Width, source.Height);
                }
                return result;
            }
        }
    }
}
