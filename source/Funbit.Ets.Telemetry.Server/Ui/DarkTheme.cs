using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Funbit.Ets.Telemetry.Server.Ui
{
    public static class DarkTheme
    {
        public static readonly Color Window = Hex("#1B202A");
        public static readonly Color Header = Hex("#171C25");
        public static readonly Color HeaderLine = Hex("#232938");
        public static readonly Color Card = Hex("#232938");
        public static readonly Color CardBorder = Hex("#2E3648");
        public static readonly Color Row = Hex("#1B202A");
        public static readonly Color Text = Hex("#DEDEDE");
        public static readonly Color TextSecondary = Hex("#A0A6B2");
        public static readonly Color TextMuted = Hex("#7E828C");
        public static readonly Color MenuText = Hex("#A0A6B2");
        public static readonly Color Blue = Hex("#2190FD");
        public static readonly Color BlueHover = Hex("#3B9DFF");
        public static readonly Color Link = Hex("#42AEFA");
        public static readonly Color LinkHover = Hex("#6CC1FF");
        public static readonly Color Teal = Hex("#50CBDA");
        public static readonly Color TealBg = Hex("#183A40");
        public static readonly Color Green = Hex("#6DD58C");
        public static readonly Color GreenBg = Hex("#1E3A2B");
        public static readonly Color Amber = Hex("#FFD230");
        public static readonly Color AmberBg = Hex("#2A2718");
        public static readonly Color Red = Hex("#D56D6D");
        public static readonly Color RedBg = Hex("#3A2222");
        public static readonly Color Orange = Hex("#D5886D");
        public static readonly Color Secondary = Hex("#27303C");
        public static readonly Color SecondaryHover = Hex("#313B49");
        public static readonly Color Control = Hex("#404F60");
        public static readonly Color ControlHover = Hex("#47586B");
        public static readonly Color ControlBorder = Hex("#55647A");
        public static readonly Color DotIdle = Hex("#404F60");
        public static readonly Color Tooltip = Hex("#323B48");
        public static readonly Color TooltipBorder = Hex("#4A5568");
        public static readonly Color Menu = Hex("#151A22");
        public static readonly Color MenuBorder = Hex("#3A4457");
        public static readonly Color MenuHover = Hex("#1E3D63");

        const string Family = "Segoe UI";
        public static readonly Font Body = new Font(Family, 9.75f);
        public static readonly Font BodyBold = new Font(Family, 9.75f, FontStyle.Bold);
        public static readonly Font Small = new Font(Family, 9f);
        public static readonly Font SmallBold = new Font(Family, 9f, FontStyle.Bold);
        public static readonly Font Note = new Font(Family, 9.5f);
        public static readonly Font Title = new Font(Family, 10.5f, FontStyle.Bold);
        public static readonly Font PanelTitle = new Font(Family, 11.25f, FontStyle.Bold);
        public static readonly Font Brand = new Font(Family, 12.75f, FontStyle.Bold);
        public static readonly Font BrandSub = new Font(Family, 8.75f);
        public static readonly Font Mono = new Font("Consolas", 10.5f);
        public static readonly Font Code = new Font("Consolas", 16.5f, FontStyle.Bold);

        public static Color Hex(string hex)
        {
            return ColorTranslator.FromHtml(hex);
        }

        public static int TextWidth(Control control, string text, Font font)
        {
            using (var g = control.CreateGraphics())
                return TextRenderer.MeasureText(g, text, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
        }

        public static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            float d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRounded(Graphics g, RectangleF rect, float radius, Color fill, Color? border = null, float borderWidth = 1f)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = RoundedRect(rect, radius))
            using (var brush = new SolidBrush(fill))
            {
                g.FillPath(brush, path);
                if (border.HasValue)
                    using (var pen = new Pen(border.Value, borderWidth))
                        g.DrawPath(pen, path);
            }
        }

        public static void DrawDot(Graphics g, float x, float y, float size, Color color)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(color))
                g.FillEllipse(brush, x, y, size, size);
        }

        public static void DrawInfoIcon(Graphics g, RectangleF rect, Color color)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(color, 1.6f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawEllipse(pen, rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2);
                float cx = rect.X + rect.Width / 2;
                g.DrawLine(pen, cx, rect.Y + rect.Height * 0.45f, cx, rect.Y + rect.Height * 0.72f);
                g.DrawLine(pen, cx, rect.Y + rect.Height * 0.28f, cx, rect.Y + rect.Height * 0.29f);
            }
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public static void UseDarkTitleBar(Form form)
        {
            int enabled = 1;
            if (DwmSetWindowAttribute(form.Handle, 20, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(form.Handle, 19, ref enabled, sizeof(int));
        }

        public static void StyleMenu(MenuStrip menu)
        {
            menu.Renderer = new DarkMenuRenderer();
            menu.BackColor = Header;
            menu.ForeColor = MenuText;
            menu.Font = Note;
            menu.Padding = new Padding(8, 2, 8, 2);
        }

        public static void StyleDropDown(ToolStripDropDown menu)
        {
            menu.Renderer = new DarkMenuRenderer();
            menu.BackColor = Menu;
            menu.ForeColor = Text;
            menu.Font = Body;
        }

        sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
        {
            public DarkMenuRenderer() : base(new DarkColorTable()) { }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                bool topLevel = e.Item.OwnerItem == null && e.Item.Owner is MenuStrip;
                bool hot = e.Item.Selected || e.Item.Pressed;
                if ((string)e.Item.Tag == "link")
                    e.TextColor = hot ? LinkHover : Link;
                else
                    e.TextColor = !e.Item.Enabled ? TextMuted : hot ? Color.White : topLevel ? MenuText : Text;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                bool hot = (e.Item.Selected || e.Item.Pressed) && e.Item.Enabled && (string)e.Item.Tag != "link";
                if (!hot)
                    return;
                var rect = new RectangleF(2.5f, 1.5f, e.Item.Width - 5, e.Item.Height - 3);
                FillRounded(e.Graphics, rect, 4, MenuHover);
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                var rect = new Rectangle(e.ImageRectangle.X + 4, e.ImageRectangle.Y + 4, e.ImageRectangle.Width - 8, e.ImageRectangle.Height - 8);
                using (var pen = new Pen(Green, 2f))
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    e.Graphics.DrawLines(pen, new[]
                    {
                        new Point(rect.Left, rect.Top + rect.Height / 2),
                        new Point(rect.Left + rect.Width / 3, rect.Bottom),
                        new Point(rect.Right, rect.Top)
                    });
                }
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = TextMuted;
                base.OnRenderArrow(e);
            }
        }

        sealed class DarkColorTable : ProfessionalColorTable
        {
            public override Color MenuStripGradientBegin => Header;
            public override Color MenuStripGradientEnd => Header;
            public override Color MenuBorder => DarkTheme.MenuBorder;
            public override Color ToolStripDropDownBackground => Menu;
            public override Color ImageMarginGradientBegin => Menu;
            public override Color ImageMarginGradientMiddle => Menu;
            public override Color ImageMarginGradientEnd => Menu;
            public override Color SeparatorDark => DarkTheme.MenuBorder;
            public override Color SeparatorLight => DarkTheme.MenuBorder;
            public override Color CheckBackground => Menu;
            public override Color CheckSelectedBackground => MenuHover;
            public override Color CheckPressedBackground => MenuHover;
        }
    }
}
