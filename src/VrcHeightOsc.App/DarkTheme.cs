using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace VrcHeightOsc.App;

internal static class DarkTheme
{
    public static readonly Color Window = Color.FromArgb(8, 12, 23);
    public static readonly Color Surface = Color.FromArgb(16, 23, 39);
    public static readonly Color SurfaceRaised = Color.FromArgb(22, 31, 51);
    public static readonly Color SurfaceHover = Color.FromArgb(29, 41, 65);
    public static readonly Color Border = Color.FromArgb(43, 56, 82);
    public static readonly Color Text = Color.FromArgb(242, 247, 255);
    public static readonly Color Muted = Color.FromArgb(148, 163, 190);
    public static readonly Color Accent = Color.FromArgb(52, 211, 235);
    public static readonly Color AccentDeep = Color.FromArgb(23, 116, 142);
    public static readonly Color Purple = Color.FromArgb(124, 112, 255);
    public static readonly Color Success = Color.FromArgb(77, 221, 139);
    public static readonly Color Warning = Color.FromArgb(255, 190, 92);
    public static readonly Color Danger = Color.FromArgb(255, 102, 124);

    public static Label Label(string text, float size = 9.5F, FontStyle style = FontStyle.Regular, Color? color = null)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font("Segoe UI", size, style),
            ForeColor = color ?? Text,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
        };
    }

    public static TextBox TextBox(string text = "")
    {
        return new TextBox
        {
            Text = text,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = SurfaceRaised,
            ForeColor = Text,
            Font = new Font("Segoe UI", 10F),
            Height = 34,
            Margin = new Padding(0),
        };
    }

    public static ComboBox ComboBox(params string[] values)
    {
        var combo = new DarkComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 25,
            BackColor = SurfaceRaised,
            ForeColor = Text,
            Font = new Font("Segoe UI", 9.5F),
            Margin = Padding.Empty,
        };
        combo.Items.AddRange(values);
        combo.DrawItem += (_, e) =>
        {
            if (e.Index >= 0)
            {
                var selected = (e.State & DrawItemState.Selected) != 0;
                using var background = new SolidBrush(selected ? SurfaceHover : SurfaceRaised);
                e.Graphics.FillRectangle(background, e.Bounds);
                TextRenderer.DrawText(
                    e.Graphics,
                    combo.Items[e.Index]?.ToString(),
                    combo.Font,
                    Rectangle.Inflate(e.Bounds, -7, 0),
                    Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        };
        return combo;
    }

    public static Button Button(string text, EventHandler click, ButtonTone tone = ButtonTone.Secondary)
    {
        var colors = tone switch
        {
            ButtonTone.Primary => (AccentDeep, Color.FromArgb(29, 145, 174), Color.White),
            ButtonTone.Danger => (Color.FromArgb(112, 39, 57), Color.FromArgb(143, 47, 68), Color.White),
            _ => (SurfaceRaised, SurfaceHover, Text),
        };
        var button = new Button
        {
            Text = text,
            AutoSize = false,
            Height = 36,
            Width = Math.Max(92, TextRenderer.MeasureText(text, new Font("Segoe UI Semibold", 9F)).Width + 26),
            FlatStyle = FlatStyle.Flat,
            BackColor = colors.Item1,
            ForeColor = colors.Item3,
            Font = new Font("Segoe UI Semibold", 9F),
            Cursor = Cursors.Hand,
            Margin = new Padding(6, 0, 0, 0),
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = tone == ButtonTone.Secondary ? Border : colors.Item1;
        button.FlatAppearance.MouseOverBackColor = colors.Item2;
        button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(colors.Item2, 0.08F);
        button.Click += click;
        return button;
    }

    public static void ApplyDarkTitleBar(Form form)
    {
        var enabled = 1;
        if (DwmSetWindowAttribute(form.Handle, 20, ref enabled, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(form.Handle, 19, ref enabled, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    internal static extern int SetWindowTheme(IntPtr window, string? appName, string? idList);
}

internal enum ButtonTone
{
    Secondary,
    Primary,
    Danger,
}

internal sealed class DarkComboBox : ComboBox
{
    private const int WmPaint = 0x000F;

    protected override void OnHandleCreated(EventArgs eventargs)
    {
        base.OnHandleCreated(eventargs);
        DarkTheme.SetWindowTheme(Handle, "DarkMode_Explorer", null);
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg == WmPaint && IsHandleCreated && Width > 0 && Height > 0)
        {
            using var graphics = CreateGraphics();
            DrawDarkChrome(graphics);
        }
    }

    private void DrawDarkChrome(Graphics graphics)
    {
        var border = Focused ? DarkTheme.AccentDeep : DarkTheme.Border;
        var buttonWidth = Math.Min(29, Math.Max(22, Height));
        var button = new Rectangle(Width - buttonWidth - 1, 1, buttonWidth, Height - 2);

        using var buttonBrush = new SolidBrush(DarkTheme.SurfaceRaised);
        using var borderPen = new Pen(border);
        using var arrowBrush = new SolidBrush(DarkTheme.Muted);
        graphics.FillRectangle(buttonBrush, button);
        graphics.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);

        var centerX = button.Left + button.Width / 2;
        var centerY = button.Top + button.Height / 2 + 1;
        var arrow = new[]
        {
            new Point(centerX - 5, centerY - 2),
            new Point(centerX + 5, centerY - 2),
            new Point(centerX, centerY + 4),
        };
        graphics.FillPolygon(arrowBrush, arrow);
    }
}

internal sealed class SurfacePanel : Panel
{
    private int _cornerRadius = 14;
    private Color _borderColor = DarkTheme.Border;

    public int CornerRadius
    {
        get => _cornerRadius;
        set { _cornerRadius = Math.Max(0, value); UpdateRegion(); Invalidate(); }
    }

    public Color BorderColor
    {
        get => _borderColor;
        set { _borderColor = value; Invalidate(); }
    }

    public SurfacePanel()
    {
        BackColor = DarkTheme.Surface;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        UpdateRegion();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRectangle(ClientRectangle, CornerRadius);
        using var pen = new Pen(BorderColor);
        e.Graphics.DrawPath(pen, path);
    }

    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        using var path = RoundedRectangle(ClientRectangle, CornerRadius);
        Region?.Dispose();
        Region = new Region(path);
    }

    private static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
    {
        var path = new GraphicsPath();
        rectangle.Width = Math.Max(1, rectangle.Width - 1);
        rectangle.Height = Math.Max(1, rectangle.Height - 1);
        var diameter = Math.Min(Math.Min(radius * 2, rectangle.Width), rectangle.Height);
        if (diameter <= 2)
        {
            path.AddRectangle(rectangle);
            return path;
        }
        var arc = new Rectangle(rectangle.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = rectangle.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = rectangle.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = rectangle.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class AccentSlider : Control
{
    private double _value = 1.6;
    private bool _dragging;

    public double Minimum { get; set; } = 0.1;
    public double Maximum { get; set; } = 5.0;

    public double Value
    {
        get => _value;
        set
        {
            var next = Math.Clamp(value, Minimum, Maximum);
            if (Math.Abs(_value - next) < 0.00001) return;
            _value = next;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? ValueChanged;

    public AccentSlider()
    {
        Height = 45;
        BackColor = DarkTheme.Surface;
        Cursor = Cursors.Hand;
        TabStop = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var left = 12F;
        var right = Width - 12F;
        var center = Height / 2F;
        var fraction = Maximum <= Minimum ? 0 : (Value - Minimum) / (Maximum - Minimum);
        var thumbX = left + (float)fraction * (right - left);
        using var track = new Pen(DarkTheme.Border, 7) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var fill = new Pen(DarkTheme.Accent, 7) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        e.Graphics.DrawLine(track, left, center, right, center);
        e.Graphics.DrawLine(fill, left, center, thumbX, center);
        using var halo = new SolidBrush(Color.FromArgb(50, DarkTheme.Accent));
        using var knob = new SolidBrush(DarkTheme.Text);
        e.Graphics.FillEllipse(halo, thumbX - 13, center - 13, 26, 26);
        e.Graphics.FillEllipse(knob, thumbX - 7, center - 7, 14, 14);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        _dragging = true;
        SetFromX(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) SetFromX(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Left) Value -= 0.01;
        if (e.KeyCode == Keys.Right) Value += 0.01;
    }

    private void SetFromX(int x)
    {
        var fraction = Math.Clamp((x - 12D) / Math.Max(1D, Width - 24D), 0D, 1D);
        Value = Minimum + fraction * (Maximum - Minimum);
    }
}

internal sealed class ToggleSwitch : CheckBox
{
    public ToggleSwitch()
    {
        AutoSize = false;
        Height = 28;
        Width = 160;
        BackColor = DarkTheme.Surface;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        pevent.Graphics.Clear(BackColor);
        var track = new Rectangle(0, 4, 38, 20);
        using var trackBrush = new SolidBrush(Checked ? DarkTheme.AccentDeep : DarkTheme.Border);
        pevent.Graphics.FillRoundedRectangle(trackBrush, track, 10);
        using var knob = new SolidBrush(DarkTheme.Text);
        pevent.Graphics.FillEllipse(knob, Checked ? 20 : 3, 7, 14, 14);
        TextRenderer.DrawText(pevent.Graphics, Text, Font, new Point(48, 5), DarkTheme.Text, TextFormatFlags.NoPadding);
    }
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle rectangle, int radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        var arc = new Rectangle(rectangle.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = rectangle.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = rectangle.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = rectangle.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}
