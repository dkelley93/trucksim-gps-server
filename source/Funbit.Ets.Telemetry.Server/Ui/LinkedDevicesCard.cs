using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Funbit.Ets.Telemetry.Server.Helpers;
using Funbit.Ets.Telemetry.Server.Services;

namespace Funbit.Ets.Telemetry.Server.Ui
{
    public sealed class LinkedDevicesCard : Card
    {
        const int RowHeight = 33;
        const int RowGap = 6;

        readonly Label _title = new Label { AutoSize = true, Font = DarkTheme.Title, ForeColor = Color.White, BackColor = Color.Transparent, Location = new Point(16, 14) };
        readonly Label _count = new Label { AutoSize = true, Font = DarkTheme.Body, ForeColor = DarkTheme.TextMuted, BackColor = Color.Transparent };
        readonly Label _empty = new Label { AutoSize = true, Font = DarkTheme.Body, ForeColor = DarkTheme.TextMuted, BackColor = Color.Transparent, Location = new Point(16, 50) };
        readonly ListPanel _list = new ListPanel { BackColor = DarkTheme.Card, Location = new Point(16, 44), Visible = false };
        readonly List<DeviceRow> _rows = new List<DeviceRow>();
        readonly TooltipPanel _tooltip;

        public event Action<string> RemoveRequested;

        public LinkedDevicesCard(TooltipPanel tooltip)
        {
            _tooltip = tooltip;
            Controls.AddRange(new Control[] { _title, _count, _empty, _list });
            _list.Scrolled += () => _tooltip.HideFrom(null);
            ApplyTexts();
        }

        public void ApplyTexts()
        {
            _title.Text = L.Get("devices.title");
            _empty.Text = L.Get("devices.empty");
            foreach (var row in _rows)
                row.ApplyTexts();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            _list.Size = new Size(Width - 32, Height - _list.Top - 14);
            foreach (var row in _rows)
                row.Width = _list.Content.Width;
            _count.Location = new Point(Width - 16 - _count.Width, 16);
        }

        public void Update(List<LinkedDevice> devices, IReadOnlyDictionary<string, ThroughputMeter> throughput)
        {
            _count.Text = L.Get("devices.count", devices.Count);
            _count.Location = new Point(Width - 16 - _count.Width, 16);
            _empty.Visible = devices.Count == 0;
            _list.Visible = devices.Count > 0;

            bool sameRows = devices.Count == _rows.Count;
            for (int i = 0; sameRows && i < devices.Count; i++)
                sameRows = _rows[i].DeviceId == devices[i].DeviceId;
            if (sameRows)
            {
                for (int i = 0; i < devices.Count; i++)
                    _rows[i].Update(devices[i], Meter(throughput, devices[i]));
                return;
            }

            _list.Content.SuspendLayout();
            _tooltip.HideFrom(null);
            foreach (var row in _rows)
                row.Dispose();
            _rows.Clear();
            _list.Content.Controls.Clear();
            _list.SetContentHeight(Math.Max(0, devices.Count * (RowHeight + RowGap) - RowGap));
            int y = 0;
            foreach (var device in devices)
            {
                var row = new DeviceRow(device.DeviceId, _list.Content.Width, _tooltip, () => RemoveRequested?.Invoke(device.DeviceId));
                row.Update(device, Meter(throughput, device));
                row.Location = new Point(0, y);
                _rows.Add(row);
                _list.Content.Controls.Add(row);
                y += RowHeight + RowGap;
            }
            _list.Content.ResumeLayout();
        }

        static ThroughputMeter Meter(IReadOnlyDictionary<string, ThroughputMeter> throughput, LinkedDevice device)
        {
            ThroughputMeter meter;
            return throughput.TryGetValue(device.DeviceId, out meter) ? meter : null;
        }

        static string Rate(double bytesPerSecond)
        {
            return (bytesPerSecond / 1024).ToString("0.0");
        }

        static string RelativeTime(DateTime utc)
        {
            var age = DateTime.UtcNow - utc;
            if (age < TimeSpan.FromMinutes(1)) return L.Get("time.just_now");
            if (age < TimeSpan.FromHours(1)) return L.Get("time.minutes_ago", (int)age.TotalMinutes);
            if (age < TimeSpan.FromDays(1)) return L.Get("time.hours_ago", (int)age.TotalHours);
            if (age < TimeSpan.FromDays(2)) return L.Get("time.yesterday");
            if (age < TimeSpan.FromDays(14)) return L.Get("time.days_ago", (int)age.TotalDays);
            if (age < TimeSpan.FromDays(60)) return L.Get("time.weeks_ago", (int)(age.TotalDays / 7));
            return L.Get("time.months_ago", (int)(age.TotalDays / 30));
        }

        sealed class ListPanel : Panel
        {
            const int ThumbWidth = 4;
            const int Gutter = ThumbWidth + 8;
            const int MinThumbHeight = 20;

            public readonly Panel Content = new Panel { BackColor = DarkTheme.Card };
            int _contentHeight, _offset;
            float _grab = -1;
            bool _hot;

            public event Action Scrolled;

            public ListPanel()
            {
                DoubleBuffered = true;
                ResizeRedraw = true;
                Controls.Add(Content);
            }

            bool Scrolls => _contentHeight > Height;
            int MaxOffset => Math.Max(0, _contentHeight - Height);

            public void SetContentHeight(int height)
            {
                _contentHeight = height;
                Content.Size = new Size(Width - (Scrolls ? Gutter : 0), height);
                ScrollTo(_offset);
            }

            void ScrollTo(int offset)
            {
                offset = Math.Max(0, Math.Min(offset, MaxOffset));
                bool moved = offset != _offset;
                _offset = offset;
                Content.Top = -offset;
                Invalidate();
                if (moved)
                    Scrolled?.Invoke();
            }

            RectangleF Thumb()
            {
                float height = Math.Max(MinThumbHeight, (float)Height * Height / _contentHeight);
                float top = MaxOffset == 0 ? 0 : (Height - height) * _offset / MaxOffset;
                return new RectangleF(Width - ThumbWidth - 1, top, ThumbWidth, height);
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                SetContentHeight(_contentHeight);
            }

            void SetHot(Point cursor)
            {
                bool hot = Scrolls && cursor.X >= Width - Gutter && cursor.X < Width && cursor.Y >= Thumb().Top && cursor.Y <= Thumb().Bottom;
                if (hot == _hot)
                    return;
                _hot = hot;
                Invalidate();
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                ScrollTo(_offset - e.Delta * (RowHeight + RowGap) / 120);
                SetHot(e.Location);
                base.OnMouseWheel(e);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left || !Scrolls)
                    return;
                var thumb = Thumb();
                if (e.Y < thumb.Top)
                    ScrollTo(_offset - Height);
                else if (e.Y > thumb.Bottom)
                    ScrollTo(_offset + Height);
                else
                    _grab = e.Y - thumb.Top;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (_grab >= 0 && Scrolls)
                    ScrollTo((int)Math.Round((e.Y - _grab) / (Height - Thumb().Height) * MaxOffset));
                SetHot(e.Location);
            }

            protected override void OnMouseCaptureChanged(EventArgs e)
            {
                base.OnMouseCaptureChanged(e);
                _grab = -1;
                SetHot(PointToClient(Cursor.Position));
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                _hot = false;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (!Scrolls)
                    return;
                var thumb = Thumb();
                thumb.Inflate(0, -1);
                DarkTheme.FillRounded(e.Graphics, thumb, ThumbWidth / 2f, _hot || _grab >= 0 ? DarkTheme.TextMuted : DarkTheme.ControlBorder);
            }
        }

        sealed class DeviceRow : Control
        {
            public readonly string DeviceId;
            readonly Label _name = new Label { AutoSize = true, Font = DarkTheme.BodyBold, ForeColor = Color.White, BackColor = Color.Transparent, Location = new Point(28, 0) };
            readonly Label _status = new Label { AutoSize = false, AutoEllipsis = true, Font = DarkTheme.Body, BackColor = Color.Transparent };
            readonly Label _rate = new Label { AutoSize = false, AutoEllipsis = true, Font = DarkTheme.Body, ForeColor = DarkTheme.Amber, BackColor = Color.Transparent };
            readonly DarkButton _remove = new DarkButton { Primary = false, Font = DarkTheme.Body, Radius = 6, AutoWidth = true, Height = 22, BackColor = DarkTheme.Row };
            readonly InfoIcon _info = new InfoIcon { ForeColor = DarkTheme.Amber, Size = new Size(14, 14), BackColor = DarkTheme.Row, Visible = false };
            readonly TooltipPanel _tooltip;
            LinkedDevice _device;
            ThroughputMeter _meter;
            bool _connected, _relay;

            public DeviceRow(string deviceId, int width, TooltipPanel tooltip, Action remove)
            {
                DeviceId = deviceId;
                _tooltip = tooltip;
                DoubleBuffered = true;
                Size = new Size(width, RowHeight);
                BackColor = DarkTheme.Card;
                _remove.Click += (s, e) => remove();
                _info.MouseEnter += (s, e) => _tooltip.Show(_info, L.Get("devices.relay_tooltip"), L.Get("menu.connection_help"), CloudConfig.ConnectHelpUrl);
                _info.MouseLeave += (s, e) => _tooltip.HideFrom(_info);
                Controls.AddRange(new Control[] { _name, _status, _rate, _remove, _info });
                ApplyTexts();
            }

            public void ApplyTexts()
            {
                _remove.Text = L.Get("devices.remove");
                LayoutRow();
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                LayoutRow();
            }

            void LayoutRow()
            {
                _remove.Location = new Point(Width - 10 - _remove.Width, (RowHeight - 22) / 2);
                if (_device != null)
                    Refresh(_device, _meter, true);
            }

            public void Update(LinkedDevice device, ThroughputMeter meter)
            {
                Refresh(device, meter, false);
            }

            void Refresh(LinkedDevice device, ThroughputMeter meter, bool force)
            {
                _device = device;
                _meter = meter;
                bool connected = LinkedDeviceStore.IsConnected(device);
                bool relay = connected && device.LastMethod == ConnectionMethod.Relay;
                string status = connected
                    ? L.Get(relay ? "devices.connected_relay" : "devices.connected_direct")
                    : L.Get("devices.last_seen", RelativeTime(device.LastSeenAt));
                string rate = relay && meter != null ? L.Get("devices.throughput", Rate(meter.UpBytesPerSecond), Rate(meter.DownBytesPerSecond)) : "";
                string name = device.Name ?? L.Get("devices.unnamed");
                if (!force && connected == _connected && relay == _relay && status == _status.Text && rate == _rate.Text && name == _name.Text)
                    return;
                bool dotChanged = connected != _connected || relay != _relay;
                _connected = connected;
                _relay = relay;
                _name.Text = name;
                _name.Top = (RowHeight - _name.PreferredHeight) / 2;
                _status.Text = status;
                _status.ForeColor = connected ? (relay ? DarkTheme.Amber : DarkTheme.Green) : DarkTheme.TextMuted;
                _status.Height = _status.PreferredHeight;
                _status.Location = new Point(_name.Right + 10, (RowHeight - _status.Height) / 2);
                int maxRight = _remove.Left - 10;
                _status.Width = Math.Max(20, Math.Min(_status.PreferredWidth, maxRight - (relay ? 20 : 0) - _status.Left));
                _info.Location = new Point(_status.Right + 6, (RowHeight - 14) / 2);
                _info.Visible = relay;
                _rate.Text = rate;
                _rate.Bounds = new Rectangle(_info.Right + 4, _status.Top, Math.Max(0, maxRight - _info.Right - 4), _status.Height);
                if (dotChanged || force)
                    Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(DarkTheme.Card);
                DarkTheme.FillRounded(e.Graphics, new RectangleF(0.5f, 0.5f, Width - 1, RowHeight - 1), 8, DarkTheme.Row);
                DarkTheme.DrawDot(e.Graphics, 10, RowHeight / 2f - 4, 8, _connected ? (_relay ? DarkTheme.Amber : DarkTheme.Green) : DarkTheme.DotIdle);
            }
        }
    }
}
