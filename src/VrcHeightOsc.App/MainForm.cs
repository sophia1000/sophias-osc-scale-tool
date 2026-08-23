using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using VrcHeightOsc.Core.Domain;

namespace VrcHeightOsc.App;

internal sealed class MainForm : Form
{
    private readonly IAppController _controller;
    private readonly Label _status = DarkTheme.Label("Starting network services…", 9F, color: DarkTheme.Muted);
    private readonly Label _networkDetails = DarkTheme.Label("OSC and OSCQuery are starting", 8.5F, color: DarkTheme.Muted);
    private readonly Label _connectionBadge = DarkTheme.Label("● STARTING", 9F, FontStyle.Bold, DarkTheme.Warning);
    private readonly Label _eyeHeight = MetricValue();
    private readonly Label _minimum = MetricValue();
    private readonly Label _maximum = MetricValue();
    private readonly Label _allowed = MetricValue();
    private readonly AccentSlider _heightSlider = new() { Dock = DockStyle.Fill, Minimum = 0.1, Maximum = 5.0, Value = 1.6 };
    private readonly TextBox _heightText = DarkTheme.TextBox("1.60");
    private readonly ToggleSwitch _smooth = new() { Text = "Smooth changes", Font = new Font("Segoe UI", 9.5F), BackColor = DarkTheme.Surface };
    private readonly TextBox _smoothTime = DarkTheme.TextBox("0.35");
    private readonly DataGridView _rulesGrid = new();
    private readonly BindingList<RuleDefinition> _ruleBindings = new();
    private readonly Label _ruleEditorTitle = DarkTheme.Label("Select a rule", 14F, FontStyle.Bold);
    private readonly Panel _ruleEditorContent = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = DarkTheme.Surface };

    private readonly ToggleSwitch _ruleEnabled = new() { Text = "Rule enabled", Font = new Font("Segoe UI", 9.5F), BackColor = DarkTheme.SurfaceRaised };
    private readonly TextBox _ruleParameter = DarkTheme.TextBox();
    private readonly ComboBox _ruleMode = DarkTheme.ComboBox("trigger", "follow");
    private readonly ComboBox _ruleCondition = DarkTheme.ComboBox("true", "false", "above", "below");
    private readonly TextBox _ruleThreshold = DarkTheme.TextBox();
    private readonly ComboBox _ruleAction = DarkTheme.ComboBox("set", "add");
    private readonly TextBox _ruleHeight = DarkTheme.TextBox();
    private readonly TextBox _ruleCooldown = DarkTheme.TextBox();
    private readonly ToggleSwitch _ruleEdgeOnly = new() { Text = "Rising edge only", Font = new Font("Segoe UI", 9.5F), BackColor = DarkTheme.SurfaceRaised };
    private readonly ToggleSwitch _ruleSmooth = new() { Text = "Smooth this rule", Font = new Font("Segoe UI", 9.5F), BackColor = DarkTheme.SurfaceRaised };
    private readonly TextBox _ruleSmoothTime = DarkTheme.TextBox();
    private readonly ToggleSwitch _ruleLimit = new() { Text = "Enable limits", Font = new Font("Segoe UI", 9.5F), BackColor = DarkTheme.SurfaceRaised };
    private readonly TextBox _ruleLimitMin = DarkTheme.TextBox();
    private readonly TextBox _ruleLimitMax = DarkTheme.TextBox();
    private readonly ComboBox _ruleLimitBehavior = DarkTheme.ComboBox("clamp", "block_outside", "toward_range");
    private readonly TextBox _followInputMin = DarkTheme.TextBox();
    private readonly TextBox _followInputMax = DarkTheme.TextBox();
    private readonly TextBox _followHeightMin = DarkTheme.TextBox();
    private readonly TextBox _followHeightMax = DarkTheme.TextBox();
    private readonly TextBox _followDeadband = DarkTheme.TextBox();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 250 };
    private SplitContainer? _rulesSplit;
    private bool _closing;
    private bool _loadingRules;
    private bool _loadingEditor;

    public MainForm(IAppController controller)
    {
        _controller = controller;
        Text = AppConstants.Name;
        MinimumSize = new Size(1120, 760);
        Size = new Size(1240, 860);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = DarkTheme.Window;
        ForeColor = DarkTheme.Text;
        Font = new Font("Segoe UI", 9.5F);
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        var taskbarIconPath = Path.Combine(AppContext.BaseDirectory, "vrc-height-osc-icon.ico");
        if (File.Exists(taskbarIconPath))
        {
            Icon = new Icon(taskbarIconPath);
        }

        Controls.Add(BuildLayout());
        ConfigureRuleGrid();
        WireEvents();

        _controller.StateChanged += ControllerStateChanged;
        _refreshTimer.Tick += (_, _) => RefreshSnapshot();
        Shown += OnShownAsync;
        FormClosing += OnFormClosingAsync;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkTheme.ApplyDarkTitleBar(this);
    }

    private Control BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(22, 18, 22, 16),
            RowCount = 4,
            ColumnCount = 1,
            BackColor = DarkTheme.Window,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 274));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildOverview(), 0, 1);
        root.Controls.Add(BuildRulesPanel(), 0, 2);
        root.Controls.Add(BuildFooter(), 0, 3);
        return root;
    }

    private Control BuildHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var titleStack = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, RowCount = 2, Margin = Padding.Empty };
        titleStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        titleStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        titleStack.Controls.Add(DarkTheme.Label("VRC HEIGHT OSC", 22F, FontStyle.Bold), 0, 0);
        titleStack.Controls.Add(DarkTheme.Label("Avatar scale control  •  OSCQuery auto-reconnect", 9.5F, color: DarkTheme.Muted), 0, 1);
        header.Controls.Add(titleStack, 0, 0);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 9, 0, 0),
        };
        actions.Controls.Add(DarkTheme.Button("Refresh values", async (_, _) => await _controller.RefreshValuesAsync()));
        actions.Controls.Add(DarkTheme.Button("Reconnect", (_, _) => _controller.RequestHardReconnect(), ButtonTone.Primary));
        header.Controls.Add(actions, 1, 0);
        return header;
    }

    private Control BuildOverview()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 14),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
        layout.Controls.Add(BuildLiveStatePanel(), 0, 0);
        layout.Controls.Add(BuildHeightPanel(), 1, 0);
        return layout;
    }

    private Control BuildLiveStatePanel()
    {
        var panel = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(18), Margin = new Padding(0, 0, 7, 0) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = Color.Transparent };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Color.Transparent, Margin = Padding.Empty };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.Controls.Add(DarkTheme.Label("Live avatar state", 13F, FontStyle.Bold), 0, 0);
        _connectionBadge.Anchor = AnchorStyles.Right;
        heading.Controls.Add(_connectionBadge, 1, 0);
        layout.Controls.Add(heading, 0, 0);

        var metrics = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 2, BackColor = Color.Transparent, Margin = Padding.Empty };
        metrics.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        metrics.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        metrics.Controls.Add(MetricCard("EYE HEIGHT", _eyeHeight, DarkTheme.Accent, new Padding(0, 0, 5, 5)), 0, 0);
        metrics.Controls.Add(MetricCard("SCALING", _allowed, DarkTheme.Purple, new Padding(5, 0, 0, 5)), 1, 0);
        metrics.Controls.Add(MetricCard("UDON MIN", _minimum, DarkTheme.Muted, new Padding(0, 5, 5, 0)), 0, 1);
        metrics.Controls.Add(MetricCard("UDON MAX", _maximum, DarkTheme.Muted, new Padding(5, 5, 0, 0)), 1, 1);
        layout.Controls.Add(metrics, 0, 1);
        panel.Controls.Add(layout);
        return panel;
    }

    private Control BuildHeightPanel()
    {
        var panel = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(20, 17, 20, 16), Margin = new Padding(7, 0, 0, 0) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, BackColor = Color.Transparent };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(DarkTheme.Label("Height control", 13F, FontStyle.Bold), 0, 0);

        var targetRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, BackColor = Color.Transparent, Margin = Padding.Empty };
        targetRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        targetRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        targetRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 27));
        targetRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        var targetLabel = DarkTheme.Label("Target height", 9F, FontStyle.Regular, DarkTheme.Muted);
        targetLabel.Anchor = AnchorStyles.Left;
        targetRow.Controls.Add(targetLabel, 0, 0);
        _heightText.Dock = DockStyle.Fill;
        _heightText.TextAlign = HorizontalAlignment.Center;
        targetRow.Controls.Add(_heightText, 1, 0);
        var metreLabel = DarkTheme.Label("m", 10F, FontStyle.Bold, DarkTheme.Muted);
        metreLabel.Anchor = AnchorStyles.Left;
        metreLabel.Margin = new Padding(7, 7, 0, 0);
        targetRow.Controls.Add(metreLabel, 2, 0);
        var setButton = DarkTheme.Button("Set height", async (_, _) => await SetHeightFromTextAsync(), ButtonTone.Primary);
        setButton.Dock = DockStyle.Fill;
        setButton.Margin = Padding.Empty;
        targetRow.Controls.Add(setButton, 3, 0);
        layout.Controls.Add(targetRow, 0, 1);

        var sliderBlock = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = Color.Transparent, Margin = Padding.Empty };
        sliderBlock.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sliderBlock.RowStyles.Add(new RowStyle(SizeType.Absolute, 15));
        sliderBlock.Controls.Add(_heightSlider, 0, 0);
        var range = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Color.Transparent, Margin = Padding.Empty };
        range.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        range.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        range.Controls.Add(DarkTheme.Label("0.10 m", 7.5F, color: DarkTheme.Muted), 0, 0);
        var maxLabel = DarkTheme.Label("5.00 m", 7.5F, color: DarkTheme.Muted);
        maxLabel.Anchor = AnchorStyles.Right;
        range.Controls.Add(maxLabel, 1, 0);
        sliderBlock.Controls.Add(range, 0, 1);
        layout.Controls.Add(sliderBlock, 0, 2);

        var quick = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, BackColor = Color.Transparent, Margin = new Padding(0, 3, 0, 2) };
        for (var i = 0; i < 6; i++) quick.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / 6));
        var deltas = new[] { -0.10, -0.05, -0.01, 0.01, 0.05, 0.10 };
        for (var i = 0; i < deltas.Length; i++)
        {
            var captured = deltas[i];
            var button = DarkTheme.Button(captured.ToString("+0.00;-0.00", CultureInfo.InvariantCulture), async (_, _) =>
                await _controller.AddHeightAsync(captured, _smooth.Checked, ReadSmoothTime()));
            button.Dock = DockStyle.Fill;
            button.Width = 60;
            button.Margin = new Padding(i == 0 ? 0 : 4, 0, 0, 0);
            quick.Controls.Add(button, i, 0);
        }
        layout.Controls.Add(quick, 0, 3);

        var smoothRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, BackColor = Color.Transparent, Margin = new Padding(0, 8, 0, 0) };
        smoothRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        smoothRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        smoothRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        smoothRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _smooth.Anchor = AnchorStyles.Left;
        smoothRow.Controls.Add(_smooth, 0, 0);
        var durationLabel = DarkTheme.Label("Duration", 8.5F, color: DarkTheme.Muted);
        durationLabel.Anchor = AnchorStyles.Right;
        durationLabel.Margin = new Padding(0, 7, 8, 0);
        smoothRow.Controls.Add(durationLabel, 1, 0);
        _smoothTime.Dock = DockStyle.Fill;
        _smoothTime.TextAlign = HorizontalAlignment.Center;
        smoothRow.Controls.Add(_smoothTime, 2, 0);
        var seconds = DarkTheme.Label("sec", 8.5F, color: DarkTheme.Muted);
        seconds.Anchor = AnchorStyles.Left;
        seconds.Margin = new Padding(7, 7, 0, 0);
        smoothRow.Controls.Add(seconds, 3, 0);
        layout.Controls.Add(smoothRow, 0, 4);

        panel.Controls.Add(layout);
        return panel;
    }

    private Control BuildRulesPanel()
    {
        var panel = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(18), Margin = new Padding(0, 0, 0, 12) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = Color.Transparent };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Color.Transparent, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var titles = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, BackColor = Color.Transparent, Margin = Padding.Empty };
        titles.Controls.Add(DarkTheme.Label("Parameter rules", 13F, FontStyle.Bold), 0, 0);
        titles.Controls.Add(DarkTheme.Label("Choose a rule on the left, then edit every setting on the right.", 8.5F, color: DarkTheme.Muted), 0, 1);
        header.Controls.Add(titles, 0, 0);

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 8), Margin = Padding.Empty };
        buttons.Controls.Add(DarkTheme.Button("+ Add rule", (_, _) => AddRule(), ButtonTone.Primary));
        buttons.Controls.Add(DarkTheme.Button("Test", async (_, _) => await TestSelectedRuleAsync()));
        buttons.Controls.Add(DarkTheme.Button("Remove", (_, _) => RemoveSelectedRule(), ButtonTone.Danger));
        buttons.Controls.Add(DarkTheme.Button("Save", async (_, _) => { SaveAllRules(); await _controller.SaveAsync(); }));
        header.Controls.Add(buttons, 1, 0);
        layout.Controls.Add(header, 0, 0);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            SplitterWidth = 12,
            BackColor = DarkTheme.Surface,
            BorderStyle = BorderStyle.None,
            Margin = Padding.Empty,
        };
        split.Panel1.BackColor = DarkTheme.SurfaceRaised;
        split.Panel1.Padding = new Padding(1);
        split.Panel2.BackColor = DarkTheme.Surface;
        split.Panel2.Padding = new Padding(12, 0, 0, 0);
        split.Panel1.Controls.Add(_rulesGrid);
        split.Panel2.Controls.Add(BuildRuleEditor());
        _rulesSplit = split;
        layout.Controls.Add(split, 0, 1);
        panel.Controls.Add(layout);
        return panel;
    }

    private Control BuildRuleEditor()
    {
        var host = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = DarkTheme.Surface, Margin = Padding.Empty };
        host.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        host.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        host.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _ruleEditorTitle.Dock = DockStyle.Fill;
        host.Controls.Add(_ruleEditorTitle, 0, 0);

        var behaviorPage = BuildEditorSection("BEHAVIOR & MOTION", new[]
        {
            Field("Parameter name", _ruleParameter), Field("Mode", _ruleMode), Field("Condition", _ruleCondition), Field("Threshold", _ruleThreshold),
            Field("Action", _ruleAction), Field("Height / delta", _ruleHeight), Field("Cooldown (sec)", _ruleCooldown), Field("Activation", _ruleEdgeOnly),
            Field("State", _ruleEnabled), Field("Transition", _ruleSmooth), Field("Smooth time (sec)", _ruleSmoothTime),
        });
        var limitsPage = BuildEditorSection("HEIGHT LIMITS", new[]
        {
            Field("State", _ruleLimit), Field("Minimum height", _ruleLimitMin), Field("Maximum height", _ruleLimitMax), Field("Outside range", _ruleLimitBehavior),
        });
        var followPage = BuildEditorSection("FLOAT FOLLOW MAPPING", new[]
        {
            Field("Input minimum", _followInputMin), Field("Input maximum", _followInputMax), Field("Height minimum", _followHeightMin),
            Field("Height maximum", _followHeightMax), Field("Deadband", _followDeadband),
        });
        foreach (var page in new[] { behaviorPage, limitsPage, followPage })
        {
            page.Dock = DockStyle.Fill;
            page.AutoSize = false;
            page.Margin = Padding.Empty;
            page.Visible = false;
            _ruleEditorContent.Controls.Add(page);
        }
        _ruleEditorContent.AutoScroll = false;

        var tabs = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty };
        var behaviorTab = DarkTheme.Button("Behavior", (_, _) => { }, ButtonTone.Primary);
        var limitsTab = DarkTheme.Button("Limits", (_, _) => { });
        var followTab = DarkTheme.Button("Float follow", (_, _) => { });
        var tabButtons = new[] { behaviorTab, limitsTab, followTab };
        foreach (var tab in tabButtons)
        {
            tab.Height = 30;
            tab.Margin = new Padding(0, 0, 6, 0);
            tabs.Controls.Add(tab);
        }
        behaviorTab.Click += (_, _) => ShowEditorPage(behaviorPage, behaviorTab, tabButtons);
        limitsTab.Click += (_, _) => ShowEditorPage(limitsPage, limitsTab, tabButtons);
        followTab.Click += (_, _) => ShowEditorPage(followPage, followTab, tabButtons);
        ShowEditorPage(behaviorPage, behaviorTab, tabButtons);

        host.Controls.Add(tabs, 0, 1);
        host.Controls.Add(_ruleEditorContent, 0, 2);
        return host;
    }

    private static void ShowEditorPage(Control page, Button activeButton, IEnumerable<Button> buttons)
    {
        foreach (var button in buttons)
        {
            var active = ReferenceEquals(button, activeButton);
            button.BackColor = active ? DarkTheme.AccentDeep : DarkTheme.SurfaceRaised;
            button.FlatAppearance.BorderColor = active ? DarkTheme.AccentDeep : DarkTheme.Border;
        }
        page.Visible = true;
        page.BringToFront();
    }

    private static SurfacePanel BuildEditorSection(string title, IReadOnlyList<(string Label, Control Control)> fields)
    {
        var section = new SurfacePanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = DarkTheme.SurfaceRaised,
            BorderColor = DarkTheme.Border,
            Padding = new Padding(13, 11, 13, 13),
            Margin = new Padding(0, 0, 0, 10),
        };
        var rows = (int)Math.Ceiling(fields.Count / 4D);
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, RowCount = rows + 1, BackColor = Color.Transparent, Margin = Padding.Empty };
        for (var i = 0; i < 4; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        var titleLabel = DarkTheme.Label(title, 8F, FontStyle.Bold, DarkTheme.Accent);
        grid.Controls.Add(titleLabel, 0, 0);
        grid.SetColumnSpan(titleLabel, 4);
        for (var i = 0; i < fields.Count; i++)
        {
            var block = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, RowCount = 2, BackColor = Color.Transparent, Margin = new Padding(i % 4 == 0 ? 0 : 5, 8, i % 4 == 3 ? 0 : 5, 0) };
            block.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            block.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            block.Controls.Add(DarkTheme.Label(fields[i].Label, 8.2F, color: DarkTheme.Muted), 0, 0);
            fields[i].Control.Dock = DockStyle.Fill;
            block.Controls.Add(fields[i].Control, 0, 1);
            grid.Controls.Add(block, i % 4, 1 + i / 4);
        }
        section.Controls.Add(grid);
        return section;
    }

    private Control BuildFooter()
    {
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent, Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        _status.AutoEllipsis = true;
        _status.Dock = DockStyle.Fill;
        _status.Anchor = AnchorStyles.Left;
        _networkDetails.AutoEllipsis = true;
        _networkDetails.Dock = DockStyle.Fill;
        _networkDetails.TextAlign = ContentAlignment.MiddleRight;
        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(_networkDetails, 1, 0);
        return footer;
    }

    private void ConfigureRuleGrid()
    {
        _rulesGrid.Dock = DockStyle.Fill;
        _rulesGrid.AutoGenerateColumns = false;
        _rulesGrid.AllowUserToAddRows = false;
        _rulesGrid.AllowUserToDeleteRows = false;
        _rulesGrid.AllowUserToResizeRows = false;
        _rulesGrid.MultiSelect = false;
        _rulesGrid.ReadOnly = true;
        _rulesGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _rulesGrid.RowHeadersVisible = false;
        _rulesGrid.BackgroundColor = DarkTheme.SurfaceRaised;
        _rulesGrid.BorderStyle = BorderStyle.None;
        _rulesGrid.GridColor = DarkTheme.Border;
        _rulesGrid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _rulesGrid.EnableHeadersVisualStyles = false;
        _rulesGrid.ColumnHeadersHeight = 38;
        _rulesGrid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        _rulesGrid.ColumnHeadersDefaultCellStyle.BackColor = DarkTheme.SurfaceRaised;
        _rulesGrid.ColumnHeadersDefaultCellStyle.ForeColor = DarkTheme.Muted;
        _rulesGrid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 8.5F);
        _rulesGrid.ColumnHeadersDefaultCellStyle.Padding = new Padding(7, 0, 0, 0);
        _rulesGrid.RowTemplate.Height = 43;
        _rulesGrid.DefaultCellStyle.BackColor = DarkTheme.SurfaceRaised;
        _rulesGrid.DefaultCellStyle.ForeColor = DarkTheme.Text;
        _rulesGrid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(33, 70, 91);
        _rulesGrid.DefaultCellStyle.SelectionForeColor = DarkTheme.Text;
        _rulesGrid.DefaultCellStyle.Font = new Font("Segoe UI", 9F);
        _rulesGrid.DefaultCellStyle.Padding = new Padding(7, 2, 4, 2);
        _rulesGrid.DataSource = _ruleBindings;
        _rulesGrid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "ON", DataPropertyName = nameof(RuleDefinition.Enabled), Width = 42 });
        _rulesGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "PARAMETER", DataPropertyName = nameof(RuleDefinition.Parameter), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 130 });
        _rulesGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "MODE", DataPropertyName = nameof(RuleDefinition.Mode), Width = 72 });
        _rulesGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "VALUE", DataPropertyName = nameof(RuleDefinition.HeightValue), Width = 68, DefaultCellStyle = new DataGridViewCellStyle { Format = "0.###" } });
    }

    private void WireEvents()
    {
        _heightSlider.ValueChanged += (_, _) =>
        {
            _heightText.Text = _heightSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
            SaveUiSettings();
        };
        _smooth.CheckedChanged += (_, _) => SaveUiSettings();
        _smoothTime.TextChanged += (_, _) => SaveUiSettings();
        _rulesGrid.SelectionChanged += (_, _) => LoadSelectedRuleIntoEditor();

        foreach (var box in new[] { _ruleParameter, _ruleThreshold, _ruleHeight, _ruleCooldown, _ruleSmoothTime, _ruleLimitMin, _ruleLimitMax, _followInputMin, _followInputMax, _followHeightMin, _followHeightMax, _followDeadband })
        {
            box.TextChanged += (_, _) => UpdateRuleFromEditor();
        }
        foreach (var combo in new[] { _ruleMode, _ruleCondition, _ruleAction, _ruleLimitBehavior })
        {
            combo.SelectedIndexChanged += (_, _) => UpdateRuleFromEditor();
        }
        foreach (var toggle in new[] { _ruleEnabled, _ruleEdgeOnly, _ruleSmooth, _ruleLimit })
        {
            toggle.CheckedChanged += (_, _) => UpdateRuleFromEditor();
        }
    }

    private async void OnShownAsync(object? sender, EventArgs e)
    {
        await _controller.StartAsync();
        ApplyUiSettings(_controller.Snapshot.Ui);
        if (_rulesSplit is { Width: > 700 })
        {
            _rulesSplit.SplitterDistance = Math.Min(355, _rulesSplit.Width - 520);
        }
        ReloadRules();
        RefreshSnapshot();
        _refreshTimer.Start();
    }

    private async void OnFormClosingAsync(object? sender, FormClosingEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true;
        _closing = true;
        _refreshTimer.Stop();
        Enabled = false;
        SaveAllRules();
        SaveUiSettings();
        await _controller.SaveAsync();
        await _controller.DisposeAsync();
        _controller.StateChanged -= ControllerStateChanged;
        BeginInvoke(Close);
    }

    private void ControllerStateChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(RefreshSnapshot); } catch (InvalidOperationException) { }
    }

    private void RefreshSnapshot()
    {
        if (IsDisposed) return;
        var state = _controller.Snapshot;
        var network = _controller.NetworkSnapshot;
        _eyeHeight.Text = FormatMetres(state.EyeHeight);
        _minimum.Text = FormatMetres(state.EyeHeightMin);
        _maximum.Text = FormatMetres(state.EyeHeightMax);
        _allowed.Text = state.ScalingAllowed switch { true => "Allowed", false => "Blocked", _ => "Waiting" };
        _allowed.ForeColor = state.ScalingAllowed switch { true => DarkTheme.Success, false => DarkTheme.Danger, _ => DarkTheme.Text };
        _connectionBadge.Text = network.Connected ? "●  CONNECTED" : "●  RECONNECTING";
        _connectionBadge.ForeColor = network.Connected ? DarkTheme.Success : DarkTheme.Warning;
        _status.Text = state.LastStatus;
        _networkDetails.Text = network.Connected
            ? $"OSC {network.OscHost}:{network.OscPort}   •   Query {network.QueryHost}:{network.QueryPort}   •   {state.Parameters.Count} params"
            : $"Local OSC {network.LocalOscPort}   •   OSCQuery {network.LocalQueryPort}   •   generation {network.Generation}";
    }

    private void ReloadRules(int preferredIndex = 0)
    {
        _loadingRules = true;
        try
        {
            _ruleBindings.RaiseListChangedEvents = false;
            _ruleBindings.Clear();
            foreach (var rule in _controller.Snapshot.Rules) _ruleBindings.Add(rule.Clone());
            _ruleBindings.RaiseListChangedEvents = true;
            _ruleBindings.ResetBindings();
            if (_ruleBindings.Count > 0)
            {
                var index = Math.Clamp(preferredIndex, 0, _ruleBindings.Count - 1);
                _rulesGrid.ClearSelection();
                _rulesGrid.Rows[index].Selected = true;
                _rulesGrid.CurrentCell = _rulesGrid.Rows[index].Cells[1];
            }
        }
        finally
        {
            _loadingRules = false;
        }
        LoadSelectedRuleIntoEditor();
    }

    private void LoadSelectedRuleIntoEditor()
    {
        if (_loadingRules) return;
        var rule = _rulesGrid.CurrentRow?.DataBoundItem as RuleDefinition;
        _ruleEditorContent.Enabled = rule is not null;
        _ruleEditorTitle.Text = rule is null ? "Select a rule" : string.IsNullOrWhiteSpace(rule.Parameter) ? "New parameter rule" : rule.Parameter;
        if (rule is null) return;

        _loadingEditor = true;
        try
        {
            _ruleEnabled.Checked = rule.Enabled;
            _ruleParameter.Text = rule.Parameter;
            Select(_ruleMode, rule.Mode);
            Select(_ruleCondition, rule.Condition);
            _ruleThreshold.Text = FormatNumber(rule.Threshold);
            Select(_ruleAction, rule.Action);
            _ruleHeight.Text = FormatNumber(rule.HeightValue);
            _ruleCooldown.Text = FormatNumber(rule.Cooldown);
            _ruleEdgeOnly.Checked = rule.RisingEdgeOnly;
            _ruleSmooth.Checked = rule.SmoothEnabled;
            _ruleSmoothTime.Text = FormatNumber(rule.SmoothTime);
            _ruleLimit.Checked = rule.LimitEnabled;
            _ruleLimitMin.Text = FormatNumber(rule.LimitMin);
            _ruleLimitMax.Text = FormatNumber(rule.LimitMax);
            Select(_ruleLimitBehavior, rule.LimitBehavior);
            _followInputMin.Text = FormatNumber(rule.FollowInputMin);
            _followInputMax.Text = FormatNumber(rule.FollowInputMax);
            _followHeightMin.Text = FormatNumber(rule.FollowHeightMin);
            _followHeightMax.Text = FormatNumber(rule.FollowHeightMax);
            _followDeadband.Text = FormatNumber(rule.FollowDeadband);
        }
        finally
        {
            _loadingEditor = false;
        }
    }

    private void UpdateRuleFromEditor()
    {
        if (_loadingEditor || _loadingRules || _rulesGrid.CurrentRow?.DataBoundItem is not RuleDefinition rule) return;
        rule.Enabled = _ruleEnabled.Checked;
        rule.Parameter = _ruleParameter.Text;
        rule.Mode = _ruleMode.SelectedItem?.ToString() ?? rule.Mode;
        rule.Condition = _ruleCondition.SelectedItem?.ToString() ?? rule.Condition;
        ReadDouble(_ruleThreshold, value => rule.Threshold = value);
        rule.Action = _ruleAction.SelectedItem?.ToString() ?? rule.Action;
        ReadDouble(_ruleHeight, value => rule.HeightValue = value);
        ReadDouble(_ruleCooldown, value => rule.Cooldown = value);
        rule.RisingEdgeOnly = _ruleEdgeOnly.Checked;
        rule.SmoothEnabled = _ruleSmooth.Checked;
        ReadDouble(_ruleSmoothTime, value => rule.SmoothTime = value);
        rule.LimitEnabled = _ruleLimit.Checked;
        ReadDouble(_ruleLimitMin, value => rule.LimitMin = value);
        ReadDouble(_ruleLimitMax, value => rule.LimitMax = value);
        rule.LimitBehavior = _ruleLimitBehavior.SelectedItem?.ToString() ?? rule.LimitBehavior;
        ReadDouble(_followInputMin, value => rule.FollowInputMin = value);
        ReadDouble(_followInputMax, value => rule.FollowInputMax = value);
        ReadDouble(_followHeightMin, value => rule.FollowHeightMin = value);
        ReadDouble(_followHeightMax, value => rule.FollowHeightMax = value);
        ReadDouble(_followDeadband, value => rule.FollowDeadband = value);
        _ruleEditorTitle.Text = string.IsNullOrWhiteSpace(rule.Parameter) ? "New parameter rule" : rule.Parameter;
        _controller.UpdateRule(_rulesGrid.CurrentRow.Index, rule);
        _rulesGrid.Refresh();
    }

    private void SaveAllRules()
    {
        UpdateRuleFromEditor();
        for (var i = 0; i < _ruleBindings.Count; i++) _controller.UpdateRule(i, _ruleBindings[i]);
    }

    private void AddRule()
    {
        _controller.AddRule();
        ReloadRules(_controller.Snapshot.Rules.Count - 1);
        _ruleParameter.Focus();
    }

    private void RemoveSelectedRule()
    {
        var index = _rulesGrid.CurrentRow?.Index ?? -1;
        if (index < 0) return;
        _controller.RemoveRule(index);
        ReloadRules(Math.Max(0, index - 1));
    }

    private async Task TestSelectedRuleAsync()
    {
        var index = _rulesGrid.CurrentRow?.Index ?? -1;
        if (index < 0) return;
        UpdateRuleFromEditor();
        await _controller.TestRuleAsync(index);
    }

    private async Task SetHeightFromTextAsync()
    {
        if (!double.TryParse(_heightText.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
        {
            MessageBox.Show(this, "Enter a valid height in metres.", AppConstants.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        height = Math.Clamp(height, _heightSlider.Minimum, _heightSlider.Maximum);
        _heightSlider.Value = height;
        await _controller.SetHeightAsync(height, _smooth.Checked, ReadSmoothTime());
    }

    private double ReadSmoothTime() => double.TryParse(_smoothTime.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        ? Math.Clamp(value, 0.02, 10.0)
        : 0.35;

    private void SaveUiSettings()
    {
        if (!IsHandleCreated || _closing && IsDisposed) return;
        _controller.UpdateUi(ui =>
        {
            ui.HeightValue = double.TryParse(_heightText.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : ui.HeightValue;
            ui.SmoothEnabled = _smooth.Checked;
            ui.SmoothTime = _smoothTime.Text;
            if (WindowState == FormWindowState.Normal) ui.Geometry = $"{Width}x{Height}+{Left}+{Top}";
        });
    }

    private void ApplyUiSettings(UiConfig ui)
    {
        _heightText.Text = ui.HeightValue.ToString("0.##", CultureInfo.InvariantCulture);
        _heightSlider.Value = Math.Clamp(ui.HeightValue, _heightSlider.Minimum, _heightSlider.Maximum);
        _smooth.Checked = ui.SmoothEnabled;
        _smoothTime.Text = ui.SmoothTime;
        var match = Regex.Match(ui.Geometry ?? "", @"^(\d+)x(\d+)(?:\+(-?\d+)\+(-?\d+))?");
        if (!match.Success) return;
        Size = new Size(Math.Max(MinimumSize.Width, int.Parse(match.Groups[1].Value)), Math.Max(MinimumSize.Height, int.Parse(match.Groups[2].Value)));
        if (match.Groups[3].Success && match.Groups[4].Success)
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(int.Parse(match.Groups[3].Value), int.Parse(match.Groups[4].Value));
        }
    }

    private static SurfacePanel MetricCard(string title, Label value, Color accent, Padding margin)
    {
        var card = new SurfacePanel { Dock = DockStyle.Fill, BackColor = DarkTheme.SurfaceRaised, BorderColor = DarkTheme.Border, Padding = new Padding(13, 10, 13, 9), Margin = margin, CornerRadius = 11 };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 2, BackColor = Color.Transparent, Margin = Padding.Empty };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 7));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var marker = new Panel { BackColor = accent, Dock = DockStyle.Fill, Margin = new Padding(0, 3, 4, 3) };
        layout.Controls.Add(marker, 0, 0);
        var titleLabel = DarkTheme.Label(title, 7.7F, FontStyle.Bold, DarkTheme.Muted);
        titleLabel.Anchor = AnchorStyles.Left;
        layout.Controls.Add(titleLabel, 1, 0);
        value.Dock = DockStyle.Fill;
        value.Anchor = AnchorStyles.Left;
        layout.Controls.Add(value, 0, 1);
        layout.SetColumnSpan(value, 2);
        card.Controls.Add(layout);
        return card;
    }

    private static Label MetricValue() => new()
    {
        Text = "--",
        AutoSize = false,
        Font = new Font("Segoe UI Semibold", 17F, FontStyle.Bold),
        ForeColor = DarkTheme.Text,
        BackColor = Color.Transparent,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = Padding.Empty,
    };

    private static (string Label, Control Control) Field(string label, Control control) => (label, control);
    private static string FormatMetres(double? value) => value.HasValue ? $"{value.Value:0.000} m" : "-- m";
    private static string FormatNumber(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void Select(ComboBox combo, string value)
    {
        var index = combo.FindStringExact(value);
        combo.SelectedIndex = index >= 0 ? index : 0;
    }

    private static void ReadDouble(TextBox box, Action<double> assign)
    {
        if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) assign(value);
    }
}
