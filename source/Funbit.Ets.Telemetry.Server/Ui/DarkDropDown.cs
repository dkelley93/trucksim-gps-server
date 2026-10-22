using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Funbit.Ets.Telemetry.Server.Ui
{
    public sealed class DarkDropDown : Control
    {
        public sealed class Item
        {
            public string Text;
            public string Detail;
            public object Value;
        }

        const int RowHeight = 22;
        const int ArrowWidth = 20;
        const int MaxVisibleRows = 8;

        public readonly List<Item> Items = new List<Item>();
        int _selectedIndex = -1;
        bool _hover;
        bool _closedByClick;
        ListPopup _popup;
        ListSurface _list;

        public event EventHandler SelectedIndexChanged;

        public DarkDropDown()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            Font = DarkTheme.Body;
            BackColor = DarkTheme.Control;
            ForeColor = DarkTheme.Text;
            Height = 24;
            SetStyle(ControlStyles.Selectable, true);
        }

        public int SelectedIndex
        {
            get { return _selectedIndex; }
            set
            {
                if (value == _selectedIndex)
                    return;
                _selectedIndex = value;
                Invalidate();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public Item SelectedItem
        {
            get { return _selectedIndex < 0 ? null : Items[_selectedIndex]; }
            set { SelectedIndex = Items.IndexOf(value); }
        }

        public bool DroppedDown
        {
            get { return _popup != null && _popup.Visible; }
            set
            {
                if (value)
                    Open();
                else
                    _popup?.Close();
            }
        }

        void Open()
        {
            if (Items.Count == 0 || DroppedDown)
                return;
            if (_popup == null)
            {
                _list = new ListSurface(this);
                _popup = new ListPopup(this);
                DarkTheme.StyleDropDown(_popup);
                _popup.Items.Add(new ToolStripControlHost(_list) { AutoSize = false, Margin = Padding.Empty, Padding = Padding.Empty });
                _popup.Closed += (s, e) => OnPopupClosed(e.CloseReason);
            }
            _list.Size = new Size(Width - 2, Math.Min(Items.Count, MaxVisibleRows) * RowHeight);
            _popup.Size = new Size(Width, _list.Height + 2);
            _list.Hot = _selectedIndex;
            _list.ScrollTo(_selectedIndex);
            _popup.Show(this, new Point(0, Height + 2));
        }

        // A click that closed the list is still on its way to this control, so it must not reopen it.
        void OnPopupClosed(ToolStripDropDownCloseReason reason)
        {
            Invalidate();
            if (!IsHandleCreated)
                return;
            _closedByClick = reason == ToolStripDropDownCloseReason.AppClicked;
            BeginInvoke(new Action(() =>
            {
                _closedByClick = false;
                var form = FindForm();
                if (form.ActiveControl == this)
                    form.ActiveControl = null;
            }));
        }

        void Choose(int index)
        {
            _popup.Close();
            SelectedIndex = index;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && !_closedByClick)
                Open();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (DroppedDown)
                _list.Scroll(e.Delta > 0 ? -1 : 1);
            base.OnMouseWheel(e);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Up || keyData == Keys.Down || keyData == Keys.Enter || keyData == Keys.Escape || base.IsInputKey(keyData);
        }

        // While the list is open the ToolStrip menu filter owns the keyboard, so the popup forwards keys here.
        bool HandleListKey(Keys keyData)
        {
            if (keyData == Keys.Down)
                _list.MoveHot(1);
            else if (keyData == Keys.Up)
                _list.MoveHot(-1);
            else if (keyData == Keys.Enter || keyData == Keys.Space)
                Choose(_list.Hot < 0 ? _selectedIndex : _list.Hot);
            else if (keyData == Keys.Escape || keyData == Keys.F4)
                _popup.Close();
            else
                return false;
            return true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (DroppedDown)
            {
                if (!HandleListKey(e.KeyData))
                {
                    base.OnKeyDown(e);
                    return;
                }
            }
            else if (e.KeyData == (Keys.Alt | Keys.Down) || e.KeyData == Keys.F4 || e.KeyData == Keys.Space || e.KeyData == Keys.Enter)
                Open();
            else if (e.KeyCode == Keys.Down && _selectedIndex < Items.Count - 1)
                SelectedIndex = _selectedIndex + 1;
            else if (e.KeyCode == Keys.Up && _selectedIndex > 0)
                SelectedIndex = _selectedIndex - 1;
            else
            {
                base.OnKeyDown(e);
                return;
            }
            e.Handled = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(_hover || DroppedDown ? DarkTheme.ControlHover : DarkTheme.Control);
            using (var pen = new Pen(Focused && ShowFocusCues || DroppedDown ? DarkTheme.Blue : DarkTheme.ControlBorder))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            if (_selectedIndex >= 0)
                DrawRow(g, Items[_selectedIndex], new Rectangle(1, 0, Width - ArrowWidth - 1, Height), false);
            DrawChevron(g, Width - ArrowWidth / 2f - 1, Height / 2f, DarkTheme.TextSecondary);
        }

        static void DrawRow(Graphics g, Item item, Rectangle bounds, bool hot)
        {
            int detailWidth = TextRenderer.MeasureText(g, item.Detail, DarkTheme.Mono, Size.Empty, TextFormatFlags.NoPadding).Width;
            var detailRect = new Rectangle(bounds.Right - 8 - detailWidth, bounds.Y, detailWidth, bounds.Height);
            var textRect = new Rectangle(bounds.X + 6, bounds.Y, detailRect.Left - 12 - bounds.X, bounds.Height);
            TextRenderer.DrawText(g, item.Text, DarkTheme.Body, textRect, hot ? Color.White : DarkTheme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, item.Detail, DarkTheme.Mono, detailRect, hot ? Color.White : DarkTheme.TextSecondary,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        static void DrawChevron(Graphics g, float cx, float cy, Color color)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(color, 1.6f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, new[] { new PointF(cx - 3.5f, cy - 1.5f), new PointF(cx, cy + 2f), new PointF(cx + 3.5f, cy - 1.5f) });
            }
        }

        sealed class ListPopup : ToolStripDropDown
        {
            readonly DarkDropDown _owner;

            public ListPopup(DarkDropDown owner)
            {
                _owner = owner;
                AutoSize = false;
                Padding = Padding.Empty;
                Margin = Padding.Empty;
                BackColor = DarkTheme.Menu;
            }

            protected override bool ProcessDialogKey(Keys keyData)
            {
                return _owner.HandleListKey(keyData) || base.ProcessDialogKey(keyData);
            }
        }

        sealed class ListSurface : Control
        {
            const int ThumbWidth = 4;

            readonly DarkDropDown _owner;
            int _first;
            public int Hot = -1;

            public ListSurface(DarkDropDown owner)
            {
                _owner = owner;
                DoubleBuffered = true;
                BackColor = DarkTheme.Menu;
            }

            int Count => _owner.Items.Count;
            int Shown => Math.Min(Count, MaxVisibleRows);

            public void ScrollTo(int index)
            {
                _first = Math.Max(0, Math.Min(index - Shown / 2, Count - Shown));
                Invalidate();
            }

            public void Scroll(int rows)
            {
                int first = Math.Max(0, Math.Min(_first + rows, Count - Shown));
                if (first == _first)
                    return;
                _first = first;
                var cursor = PointToClient(Cursor.Position);
                if (ClientRectangle.Contains(cursor))
                    Hot = RowAt(cursor.Y);
                Invalidate();
            }

            public void MoveHot(int step)
            {
                Hot = Math.Max(0, Math.Min((Hot < 0 ? _owner._selectedIndex : Hot) + step, Count - 1));
                if (Hot < _first)
                    _first = Hot;
                else if (Hot >= _first + Shown)
                    _first = Hot - Shown + 1;
                Invalidate();
            }

            int RowAt(int y)
            {
                int index = _first + y / RowHeight;
                return y >= 0 && y < Shown * RowHeight && index < Count ? index : -1;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                int index = RowAt(e.Y);
                if (index != Hot)
                {
                    Hot = index;
                    Invalidate();
                }
                base.OnMouseMove(e);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                Hot = -1;
                Invalidate();
                base.OnMouseLeave(e);
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                Scroll(e.Delta > 0 ? -1 : 1);
                base.OnMouseWheel(e);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                int index = RowAt(e.Y);
                if (e.Button == MouseButtons.Left && index >= 0)
                    _owner.Choose(index);
                base.OnMouseUp(e);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(DarkTheme.Menu);
                bool scrolls = Count > Shown;
                int rowWidth = scrolls ? Width - ThumbWidth - 2 : Width;
                for (int i = _first; i < Math.Min(Count, _first + Shown); i++)
                {
                    var bounds = new Rectangle(0, (i - _first) * RowHeight, rowWidth, RowHeight);
                    bool hot = i == Hot;
                    if (hot)
                        using (var brush = new SolidBrush(DarkTheme.Blue))
                            g.FillRectangle(brush, bounds);
                    DrawRow(g, _owner.Items[i], bounds, hot);
                }
                if (!scrolls)
                    return;
                float thumbHeight = Height * (float)Shown / Count;
                float thumbTop = Height * (float)_first / Count;
                DarkTheme.FillRounded(g, new RectangleF(Width - ThumbWidth - 1, thumbTop + 1, ThumbWidth, thumbHeight - 2), ThumbWidth / 2f, DarkTheme.ControlBorder);
            }
        }
    }
}
