using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Funbit.Ets.Telemetry.Server.Ui
{
    public class Card : Panel
    {
        public Color? BorderColor { get; set; }
        public bool Highlight { get; set; }
        public float Radius { get; set; } = 12f;

        public Card()
        {
            DoubleBuffered = true;
            BackColor = DarkTheme.Window;
            ResizeRedraw = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(DarkTheme.Window);
            var rect = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            if (Highlight)
            {
                DarkTheme.FillRounded(e.Graphics, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Radius + 3, Color.FromArgb(30, DarkTheme.Blue));
                rect = new RectangleF(3.5f, 3.5f, Width - 7, Height - 7);
            }
            DarkTheme.FillRounded(e.Graphics, rect, Radius, DarkTheme.Card, Highlight ? DarkTheme.Blue : BorderColor);
            base.OnPaint(e);
        }
    }

    public sealed class Pill : Control
    {
        Color _dot = DarkTheme.Amber;
        Color _background = DarkTheme.AmberBg;

        public Pill()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            Font = DarkTheme.SmallBold;
            BackColor = DarkTheme.Card;
            Height = 24;
        }

        public void Set(string text, Color color, Color background)
        {
            if (text == Text && color == _dot && background == _background)
                return;
            Text = text;
            _dot = color;
            ForeColor = color;
            _background = background;
            Width = DarkTheme.TextWidth(this, text, Font) + 36;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            DarkTheme.FillRounded(e.Graphics, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Height / 2f, _background);
            DarkTheme.DrawDot(e.Graphics, 10, Height / 2f - 4, 8, _dot);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(24, 0, Width - 30, Height), ForeColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
        }
    }

    public sealed class DarkButton : Control
    {
        bool _hover;
        int _spinnerAngle;
        Timer _spinner;

        public bool Primary { get; set; } = true;
        public bool Busy { get; set; }
        public bool AutoWidth { get; set; }
        public float Radius { get; set; } = 8f;

        public DarkButton()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            Font = DarkTheme.BodyBold;
            BackColor = DarkTheme.Card;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.Selectable, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            if (AutoWidth)
                Width = DarkTheme.TextWidth(this, Text, Font) + 20;
            Invalidate();
            base.OnTextChanged(e);
        }

        protected override void OnFontChanged(EventArgs e)
        {
            OnTextChanged(e);
            base.OnFontChanged(e);
        }

        public void SetBusy(bool busy)
        {
            Busy = busy;
            Enabled = !busy;
            Cursor = busy ? Cursors.Default : Cursors.Hand;
            if (busy && _spinner == null)
            {
                _spinner = new Timer { Interval = 40 };
                _spinner.Tick += (s, e) => { _spinnerAngle = (_spinnerAngle + 20) % 360; Invalidate(); };
                _spinner.Start();
            }
            else if (!busy && _spinner != null)
            {
                _spinner.Dispose();
                _spinner = null;
            }
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color fill, text;
            if (!Enabled)
            {
                fill = DarkTheme.Secondary;
                text = DarkTheme.TextMuted;
            }
            else if (Primary)
            {
                fill = _hover ? DarkTheme.BlueHover : DarkTheme.Blue;
                text = Color.White;
            }
            else
            {
                fill = _hover ? DarkTheme.SecondaryHover : DarkTheme.Secondary;
                text = DarkTheme.Text;
            }
            DarkTheme.FillRounded(e.Graphics, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Radius, fill);
            var textRect = new Rectangle(0, 0, Width, Height);
            if (Busy)
            {
                int size = 14;
                int textWidth = TextRenderer.MeasureText(e.Graphics, Text, Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
                int x = (Width - textWidth - size - 8) / 2;
                using (var pen = new Pen(text, 2.2f))
                {
                    pen.StartCap = LineCap.Round;
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    e.Graphics.DrawArc(pen, x, (Height - size) / 2, size, size, _spinnerAngle, 270);
                }
                textRect = new Rectangle(x + size + 8, 0, textWidth + 4, Height);
                TextRenderer.DrawText(e.Graphics, Text, Font, textRect, text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
                return;
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, textRect, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
                OnClick(EventArgs.Empty);
            base.OnKeyDown(e);
        }
    }

    public sealed class InfoIcon : Control
    {
        public InfoIcon()
        {
            Size = new Size(16, 16);
            BackColor = DarkTheme.Card;
            ForeColor = DarkTheme.TextMuted;
            Cursor = Cursors.Help;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            DarkTheme.DrawInfoIcon(e.Graphics, new RectangleF(0, 0, Width, Height), ForeColor);
        }
    }

    public sealed class ChevronButton : Control
    {
        public bool Expanded { get; set; }

        public ChevronButton()
        {
            Size = new Size(28, 28);
            BackColor = DarkTheme.Card;
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            DarkTheme.FillRounded(e.Graphics, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 6, DarkTheme.CardBorder);
            using (var pen = new Pen(DarkTheme.Text, 2.2f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                float cx = Width / 2f, cy = Height / 2f;
                float dy = Expanded ? -2f : 2f;
                e.Graphics.DrawLines(pen, new[] { new PointF(cx - 4, cy - dy), new PointF(cx, cy + dy), new PointF(cx + 4, cy - dy) });
            }
        }
    }

    public sealed class LinkText : LinkLabel
    {
        public LinkText()
        {
            LinkColor = DarkTheme.Link;
            ActiveLinkColor = DarkTheme.LinkHover;
            VisitedLinkColor = DarkTheme.Link;
            LinkBehavior = LinkBehavior.HoverUnderline;
            ForeColor = DarkTheme.TextMuted;
            Font = DarkTheme.Small;
            AutoSize = true;
            BackColor = Color.Transparent;
        }

        public void SetWithLink(string template, string linkText)
        {
            string text = string.Format(template, linkText);
            Text = text;
            int start = string.IsNullOrEmpty(linkText) ? -1 : text.IndexOf(linkText, StringComparison.Ordinal);
            LinkArea = start < 0 ? new LinkArea(0, 0) : new LinkArea(start, linkText.Length);
        }
    }

    public sealed class RichLabel : Control
    {
        public bool Centered { get; set; }
        public Font BoldFont { get; set; } = DarkTheme.BodyBold;
        public Color BoldColor { get; set; } = DarkTheme.Text;

        public RichLabel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            Font = DarkTheme.Note;
            BackColor = DarkTheme.Card;
            ForeColor = DarkTheme.TextSecondary;
        }

        struct Run
        {
            public string Word;
            public bool Bold;
            public bool SpaceBefore;
            public int Width;
        }

        List<Run> Runs(Graphics g)
        {
            var runs = new List<Run>();
            bool bold = false;
            bool trailingSpace = false;
            foreach (var part in (Text ?? "").Split(new[] { "<b>", "</b>" }, StringSplitOptions.None))
            {
                bool spaceBefore = trailingSpace || part.StartsWith(" ");
                trailingSpace = part.EndsWith(" ");
                foreach (var word in part.Split(' '))
                {
                    if (word.Length > 0)
                    {
                        var font = bold ? BoldFont : Font;
                        int width = TextRenderer.MeasureText(g, word, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
                        runs.Add(new Run { Word = word, Bold = bold, SpaceBefore = spaceBefore && runs.Count > 0, Width = width });
                    }
                    spaceBefore = true;
                }
                bold = !bold;
            }
            return runs;
        }

        public int MeasureHeight(int width)
        {
            using (var g = CreateGraphics())
                return LayoutRuns(g, width, false);
        }

        int LayoutRuns(Graphics g, int width, bool draw)
        {
            int fontHeight = (int)Font.GetHeight(g);
            int lineHeight = (int)(fontHeight * 1.35f);
            var line = new List<Run>();
            int y = 0, lineWidth = 0;
            int space = TextRenderer.MeasureText(g, " ", Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
            Action flush = () =>
            {
                if (draw)
                {
                    int x = Centered ? (width - lineWidth) / 2 : 0;
                    foreach (var item in line)
                    {
                        if (item.SpaceBefore)
                            x += space;
                        TextRenderer.DrawText(g, item.Word, item.Bold ? BoldFont : Font, new Point(x, y), item.Bold ? BoldColor : ForeColor, TextFormatFlags.NoPadding);
                        x += item.Width;
                    }
                }
                y += lineHeight;
                line.Clear();
                lineWidth = 0;
            };
            foreach (var run in Runs(g))
            {
                int extra = (line.Count > 0 && run.SpaceBefore ? space : 0) + run.Width;
                if (line.Count > 0 && lineWidth + extra > width)
                    flush();
                var placed = run;
                placed.SpaceBefore = run.SpaceBefore && line.Count > 0;
                line.Add(placed);
                lineWidth += (placed.SpaceBefore ? space : 0) + run.Width;
            }
            if (line.Count > 0)
                flush();
            return y - (lineHeight - fontHeight);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            LayoutRuns(e.Graphics, Width, true);
        }

        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    }

    public sealed class CodeLabel : Control
    {
        public CodeLabel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            Font = DarkTheme.Code;
            BackColor = DarkTheme.Card;
            ForeColor = Color.White;
            Height = 32;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            string text = Text ?? "";
            var sizes = new int[text.Length];
            int total = 0;
            for (int i = 0; i < text.Length; i++)
            {
                sizes[i] = TextRenderer.MeasureText(e.Graphics, text[i].ToString(), Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
                total += sizes[i] + (i < text.Length - 1 ? 4 : 0);
            }
            int x = (Width - total) / 2;
            for (int i = 0; i < text.Length; i++)
            {
                TextRenderer.DrawText(e.Graphics, text[i].ToString(), Font, new Rectangle(x, 0, sizes[i] + 2, Height), ForeColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                x += sizes[i] + 4;
            }
        }

        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    }

    // Sits inside the form over whatever card the anchor lives on. The drop shadow needs the pixels
    // under the panel, so it renders the form once when shown and paints on top of that snapshot.
    public sealed class TooltipPanel : Panel
    {
        const int BubbleWidth = 300;
        const int Shadow = 10;

        readonly LinkText _text = new LinkText();
        Control _anchor;
        Bitmap _backdrop;

        public event Action<string> LinkClicked;

        public TooltipPanel()
        {
            Visible = false;
            Width = BubbleWidth + Shadow * 2;
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = DarkTheme.Tooltip;
            _text.Font = DarkTheme.Small;
            _text.ForeColor = DarkTheme.Text;
            _text.AutoSize = false;
            _text.Location = new Point(Shadow + 12, Shadow + 10);
            _text.Width = BubbleWidth - 24;
            _text.LinkClicked += (s, e) => LinkClicked?.Invoke((string)e.Link.LinkData);
            _text.MouseLeave += (s, e) => HideIfAway();
            Controls.Add(_text);
            MouseLeave += (s, e) => HideIfAway();
        }

        public void Show(Control anchor, string text)
        {
            _text.Text = text;
            _text.LinkArea = new LinkArea(0, 0);
            Place(anchor);
        }

        public void Show(Control anchor, string template, string linkText, string url)
        {
            _text.SetWithLink(template, linkText);
            foreach (LinkLabel.Link link in _text.Links)
                link.LinkData = url;
            Place(anchor);
        }

        void Place(Control anchor)
        {
            _anchor = anchor;
            Visible = false;
            using (var g = CreateGraphics())
                _text.Height = TextRenderer.MeasureText(g, _text.Text, _text.Font, new Size(_text.Width, 0), TextFormatFlags.WordBreak).Height + 4;
            int bubbleHeight = _text.Height + 20;
            Height = bubbleHeight + Shadow * 2;
            var form = anchor.FindForm();
            var point = form.PointToClient(anchor.PointToScreen(new Point(0, anchor.Height + 6)));
            int x = Math.Max(8, Math.Min(point.X - BubbleWidth / 2, form.ClientSize.Width - BubbleWidth - 8));
            int y = point.Y + bubbleHeight > form.ClientSize.Height ? point.Y - anchor.Height - bubbleHeight - 12 : point.Y;
            Location = new Point(x - Shadow, Math.Max(0, y - Shadow));
            CaptureBackdrop(form);
            BringToFront();
            Visible = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _backdrop?.Dispose();
            base.Dispose(disposing);
        }

        void CaptureBackdrop(Form form)
        {
            _backdrop?.Dispose();
            var clientOrigin = form.PointToScreen(Point.Empty);
            using (var window = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(window, new Rectangle(Point.Empty, form.Size));
                var area = new Rectangle(Left + clientOrigin.X - form.Left, Top + clientOrigin.Y - form.Top, Width, Height);
                _backdrop = window.Clone(Rectangle.Intersect(area, new Rectangle(Point.Empty, window.Size)), window.PixelFormat);
            }
        }

        void HideIfAway()
        {
            var timer = new Timer { Interval = 150 };
            timer.Tick += (s, e) =>
            {
                timer.Dispose();
                if (!Visible)
                    return;
                var cursor = PointToClient(Cursor.Position);
                bool overTooltip = ClientRectangle.Contains(cursor);
                bool anchorAlive = _anchor != null && !_anchor.IsDisposed;
                bool overAnchor = anchorAlive && _anchor.ClientRectangle.Contains(_anchor.PointToClient(Cursor.Position));
                if (!overTooltip && !overAnchor)
                {
                    Visible = false;
                    _anchor = null;
                }
            };
            timer.Start();
        }

        public void HideFrom(Control anchor)
        {
            if (anchor == null)
            {
                Visible = false;
                _anchor = null;
            }
            else if (_anchor == anchor)
                HideIfAway();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(DarkTheme.Window);
            if (_backdrop != null)
                g.DrawImageUnscaled(_backdrop, 0, 0);
            var bubble = new RectangleF(Shadow + 0.5f, Shadow + 0.5f, Width - Shadow * 2 - 1, Height - Shadow * 2 - 1);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var shade = new SolidBrush(Color.FromArgb(7, Color.Black)))
                for (int i = Shadow; i > 0; i--)
                {
                    var ring = RectangleF.Inflate(bubble, i, i);
                    ring.Offset(0, 2);
                    using (var path = DarkTheme.RoundedRect(ring, 8 + i))
                        g.FillPath(shade, path);
                }
            DarkTheme.FillRounded(g, bubble, 8, DarkTheme.Tooltip, DarkTheme.TooltipBorder);
        }
    }
}
