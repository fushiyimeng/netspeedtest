using System.Text;

namespace NetSpeedTest;

sealed class MainForm : Form
{
    private static readonly Color PagePlane = Color.FromArgb(0xf9, 0xf9, 0xf7);
    private static readonly Color InkColor = Color.FromArgb(0x0b, 0x0b, 0x0b);
    private static readonly Color SecondaryInk = Color.FromArgb(0x52, 0x51, 0x4e);
    private static readonly Color MutedInk = Color.FromArgb(0x89, 0x87, 0x81);

    private readonly AppSettings _settings = AppSettings.Load();

    private readonly RateChart _chart = new();
    private readonly Label _nicLine = new();
    private readonly Label _warnLine = new();
    private readonly Label _latencyLine = new();
    private readonly ToolStripStatusLabel _statusText = new("就绪");

    private readonly CheckBox _cbDown = new() { Text = "下载", Checked = true };
    private readonly CheckBox _cbUp = new() { Text = "上传" };
    private readonly CheckBox _cbPing = new() { Text = "延迟 (ping)", Checked = true };

    private readonly NumericUpDown _seconds = new();
    private readonly NumericUpDown _threads = new();
    private readonly NumericUpDown _warmup = new();
    private readonly NumericUpDown _plan = new();
    private readonly TextBox _uploadUrl = new();

    private readonly CheckedListBox _nicList = new();
    private readonly List<NicCandidate> _nicItems = new();

    private readonly TextBox _report = new();
    private readonly Label _verdict = new();

    private readonly Button _start = new();
    private readonly Button _stop = new();
    private readonly Button _history = new();
    private readonly Button _copy = new();

    private readonly Dictionary<string, PingStat> _idlePings = new();
    private readonly Dictionary<string, PingStat> _loadedPings = new();

    private CancellationTokenSource? _cts;
    private PingStat? _livePing;     // 正在测的那条，还没落进字典
    private bool _running;

    public MainForm()
    {
        Text = "网速测试";
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = PagePlane;
        ClientSize = new Size(1220, 820);
        MinimumSize = new Size(1020, 680);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;

        BuildLayout();
        LoadSettingsIntoControls();

        _stop.Enabled = false;
        AcceptButton = _start;

        _start.Click += async (_, _) =>
        {
            try { await RunTestAsync(); }
            catch (Exception ex) { Program.LogCrash("界面", ex); _statusText.Text = "出错了：" + ex.Message; SetRunning(false); }
        };
        _stop.Click += (_, _) => _cts?.Cancel();
        _history.Click += (_, _) => { using var f = new HistoryForm(); f.ShowDialog(this); };
        _copy.Click += (_, _) => CopyReport();

        _cbDown.CheckedChanged += (_, _) => UpdateStartEnabled();
        _cbUp.CheckedChanged += (_, _) => UpdateStartEnabled();
        _cbPing.CheckedChanged += (_, _) => UpdateStartEnabled();
        // CheckedListBox 只有 ItemCheck（在状态提交**之前**触发），所以推迟一拍再算
        _nicList.ItemCheck += (_, _) =>
        {
            if (IsHandleCreated) BeginInvoke(UpdateStartEnabled);
        };

        UpdateStartEnabled();
    }

    // ------------------------------------------------------------------ 布局

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(BuildTopLine(), 0, 0);
        root.Controls.Add(BuildMiddle(), 0, 1);
        root.Controls.Add(BuildResults(), 0, 2);
        root.Controls.Add(BuildStatusBar(), 0, 3);
        Controls.Add(root);
    }

    private Control BuildTopLine()
    {
        _nicLine.AutoSize = true;
        _nicLine.Font = new Font("Microsoft YaHei UI", 9.5F);
        _nicLine.ForeColor = InkColor;
        _nicLine.Margin = new Padding(0);

        _warnLine.AutoSize = true;
        _warnLine.ForeColor = Verdict.LevelColor(VerdictLevel.Warning);
        _warnLine.Visible = false;
        _warnLine.Margin = new Padding(0, 4, 0, 0);

        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14, 12, 14, 0),
        };
        top.Controls.Add(_nicLine);
        top.Controls.Add(_warnLine);
        return top;
    }

    private Control BuildMiddle()
    {
        var chartHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 8, 10, 6) };
        _chart.Dock = DockStyle.Fill;
        chartHost.Controls.Add(_chart);

        _latencyLine.AutoSize = true;
        _latencyLine.ForeColor = SecondaryInk;
        _latencyLine.Padding = new Padding(2, 2, 0, 0);
        _latencyLine.Margin = new Padding(0);

        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.Controls.Add(chartHost, 0, 0);
        left.Controls.Add(_latencyLine, 0, 1);

        var middle = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(14, 0, 14, 6),
        };
        middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        middle.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 386));
        middle.Controls.Add(left, 0, 0);
        middle.Controls.Add(BuildRightColumn(), 1, 0);
        return middle;
    }

    private Control BuildRightColumn()
    {
        // 第三行是占位用的：TableLayoutPanel 会把行高总和之外的多余空间全塞给最后一行
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.Controls.Add(Group("测什么", BuildWhatBox()), 0, 0);
        right.Controls.Add(Group("设置", BuildSettingsBox()), 0, 1);
        right.Controls.Add(Group("网卡（可多选）", BuildNicBox()), 0, 2);
        right.Controls.Add(BuildButtons(), 0, 3);
        return right;
    }

    private Control BuildWhatBox()
    {
        foreach (var cb in new[] { _cbDown, _cbUp, _cbPing })
        {
            cb.AutoSize = true;
            cb.Margin = new Padding(0, 0, 18, 0);
        }

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Padding = new Padding(4, 6, 4, 4),
        };
        flow.Controls.Add(_cbDown);
        flow.Controls.Add(_cbUp);
        flow.Controls.Add(_cbPing);
        return flow;
    }

    private Control BuildSettingsBox()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 6,
            Padding = new Padding(4),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));

        AddSettingRow(table, 0, "时长（秒）", _seconds, 5, 300);
        AddSettingRow(table, 1, "线程数（每块网卡）", _threads, 4, 64);
        AddSettingRow(table, 2, "预热（秒）", _warmup, 0, 15);
        AddSettingRow(table, 3, "套餐（Mbps）", _plan, 0, 100000, 100);

        var urlLabel = new Label
        {
            Text = "上传落点（改成你国内能收 POST 的地址）",
            AutoSize = true,
            ForeColor = SecondaryInk,
            Margin = new Padding(0, 8, 0, 0),
        };
        _uploadUrl.Dock = DockStyle.Fill;
        _uploadUrl.Margin = new Padding(0, 2, 0, 0);

        table.Controls.Add(urlLabel, 0, 4);
        table.SetColumnSpan(urlLabel, 2);
        table.Controls.Add(_uploadUrl, 0, 5);
        table.SetColumnSpan(_uploadUrl, 2);
        return table;
    }

    private Control BuildNicBox()
    {
        var refresh = new LinkLabel { Text = "重新扫描网卡", AutoSize = true, Margin = new Padding(2, 0, 0, 2) };
        refresh.LinkClicked += (_, _) => PopulateNicList();

        _nicList.CheckOnClick = true;
        _nicList.Dock = DockStyle.Fill;
        _nicList.IntegralHeight = false;
        _nicList.Margin = new Padding(2, 0, 2, 2);

        var table = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        table.Controls.Add(refresh, 0, 0);
        table.Controls.Add(_nicList, 0, 1);
        return table;
    }

    private Control BuildButtons()
    {
        _start.Text = "开始测试";
        _start.Dock = DockStyle.Fill;
        _start.AutoSize = false;
        _start.Margin = new Padding(0, 6, 4, 6);

        _stop.Text = "停止";
        _stop.Dock = DockStyle.Fill;
        _stop.AutoSize = false;
        _stop.Margin = new Padding(4, 6, 4, 6);

        _history.Text = "历史记录";
        _history.Dock = DockStyle.Fill;
        _history.AutoSize = false;
        _history.Margin = new Padding(4, 6, 0, 6);

        // 行高必须显式写成 Percent 100%，否则表格的隐式 AutoSize 行会把按钮撑成一大坨
        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        buttons.Controls.Add(_start, 0, 0);
        buttons.Controls.Add(_stop, 1, 0);
        buttons.Controls.Add(_history, 2, 0);
        return buttons;
    }

    private Control BuildResults()
    {
        _report.Dock = DockStyle.Fill;
        _report.Multiline = true;
        _report.ReadOnly = true;
        _report.WordWrap = false;
        _report.ScrollBars = ScrollBars.Vertical;
        _report.BorderStyle = BorderStyle.None;
        _report.BackColor = Color.White;
        _report.ForeColor = InkColor;
        _report.Font = new Font("Consolas", 9F);
        _report.Margin = new Padding(4, 4, 12, 4);

        // AutoSize=false + Dock=Fill + TopLeft：这样才会按列宽自动换行（AutoSize=true 的
        // Label 不换行，长句会被直接切掉）
        _verdict.Dock = DockStyle.Fill;
        _verdict.AutoSize = false;
        _verdict.TextAlign = ContentAlignment.TopLeft;
        _verdict.Padding = new Padding(6, 4, 6, 4);
        _verdict.ForeColor = MutedInk;

        _copy.Text = "复制结果";
        _copy.Dock = DockStyle.Top;
        _copy.Height = 28;

        var verdictCol = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        verdictCol.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        verdictCol.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        verdictCol.Controls.Add(_verdict, 0, 0);
        verdictCol.Controls.Add(_copy, 0, 1);

        var results = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(4) };
        results.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        results.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
        results.Controls.Add(_report, 0, 0);
        results.Controls.Add(verdictCol, 1, 0);

        var box = new GroupBox
        {
            Text = "结果",
            Dock = DockStyle.Fill,
            ForeColor = SecondaryInk,
            Padding = new Padding(8, 4, 8, 8),
            Margin = new Padding(14, 0, 14, 6),
        };
        box.Controls.Add(results);
        return box;
    }

    private Control BuildStatusBar()
    {
        _statusText.ForeColor = SecondaryInk;
        var strip = new StatusStrip { Dock = DockStyle.Fill, SizingGrip = false, BackColor = PagePlane };
        strip.Items.Add(_statusText);
        return strip;
    }

    private static GroupBox Group(string title, Control content) => new()
    {
        Text = title,
        Dock = DockStyle.Fill,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ForeColor = SecondaryInk,
        Padding = new Padding(8, 4, 8, 6),
        Margin = new Padding(0, 0, 0, 6),
        Controls = { content },
    };

    private static void AddSettingRow(TableLayoutPanel table, int row, string label, NumericUpDown box, decimal min, decimal max, decimal step = 1)
    {
        box.Minimum = min;
        box.Maximum = max;
        box.Increment = step;
        box.Dock = DockStyle.Fill;
        box.TextAlign = HorizontalAlignment.Right;

        var l = new Label { Text = label, AutoSize = true, ForeColor = SecondaryInk, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 0) };
        box.Margin = new Padding(4, 2, 0, 2);

        table.Controls.Add(l, 0, row);
        table.Controls.Add(box, 1, row);
    }

    // ------------------------------------------------------------------ 设置读写

    private void LoadSettingsIntoControls()
    {
        _cbDown.Checked = _settings.TestDownload;
        _cbUp.Checked = _settings.TestUpload;
        _cbPing.Checked = _settings.TestPing;

        _seconds.Value = Math.Clamp(_settings.Seconds, (int)_seconds.Minimum, (int)_seconds.Maximum);
        _threads.Value = Math.Clamp(_settings.Threads, (int)_threads.Minimum, (int)_threads.Maximum);
        _warmup.Value = Math.Clamp(_settings.WarmupSeconds, (int)_warmup.Minimum, (int)_warmup.Maximum);
        _plan.Value = Math.Clamp((decimal)_settings.PlanMbps, _plan.Minimum, _plan.Maximum);
        _uploadUrl.Text = string.IsNullOrWhiteSpace(_settings.UploadUrl) ? TrafficEngine.DefaultUploadUrl : _settings.UploadUrl;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        PopulateNicList();
        UpdateStartEnabled();
    }

    private void PopulateNicList()
    {
        // 记下当前勾选的，重扫后尽量恢复
        var keep = new HashSet<string>(CheckedNicNames(), StringComparer.OrdinalIgnoreCase);
        if (keep.Count == 0)
            foreach (var n in _settings.SelectedNics) keep.Add(n);

        _nicItems.Clear();
        _nicItems.AddRange(NetInfo.ListCandidates());

        _nicList.BeginUpdate();
        _nicList.Items.Clear();
        foreach (var c in _nicItems) _nicList.Items.Add(c.Display);
        _nicList.EndUpdate();

        // 默认勾上承载默认路由的那块；它不在列表里就退回第一块可用的
        var defName = NetInfo.FindDefaultNic()?.Name ?? "";
        int firstUsable = _nicItems.FindIndex(c => c.Usable);

        for (int i = 0; i < _nicItems.Count; i++)
        {
            var c = _nicItems[i];
            bool check = keep.Count > 0
                ? keep.Contains(c.Name)
                : c.Usable && (c.Name == defName || i == firstUsable);
            if (check && c.Usable) _nicList.SetItemChecked(i, true);
        }

        RefreshNicLine();
        UpdateStartEnabled();
    }

    private List<string> CheckedNicNames()
    {
        var names = new List<string>();
        foreach (int i in _nicList.CheckedIndices)
            if (i >= 0 && i < _nicItems.Count) names.Add(_nicItems[i].Name);
        return names;
    }

    private void RefreshNicLine()
    {
        var checkedNames = CheckedNicNames();
        if (checkedNames.Count == 0)
        {
            _nicLine.Text = "没勾选任何网卡 —— 在上面的列表里挑一块。";
            _warnLine.Visible = false;
            return;
        }

        var parts = new List<string>();
        foreach (var name in checkedNames)
        {
            var nic = NetInfo.Resolve(new[] { name }).FirstOrDefault();
            if (nic is null) continue;
            var s = NetInfo.Snapshot(nic);
            parts.Add($"{s.Name} {s.LocalIp}（链路 {s.SpeedText}，网关 {s.Gateway ?? "无"}）");
        }
        _nicLine.Text = (checkedNames.Count > 1 ? $"已选 {checkedNames.Count} 块网卡：" : "网卡 ") + string.Join("    ", parts);

        var warns = new List<string>();
        foreach (var name in checkedNames)
        {
            var item = _nicItems.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (item is null) continue;
            var nic = NetInfo.Resolve(new[] { name }).FirstOrDefault();
            if (nic is null) continue;
            warns.AddRange(NetInfo.Snapshot(nic).Warnings);
        }
        _warnLine.Text = string.Join("\n", warns);
        _warnLine.Visible = warns.Count > 0;
    }

    private void UpdateStartEnabled()
    {
        bool anyTest = _cbDown.Checked || _cbUp.Checked || _cbPing.Checked;
        bool anyNic = _nicList.CheckedIndices.Count > 0;
        _start.Enabled = !_running && anyTest && anyNic;
    }

    // ------------------------------------------------------------------ 跑测试

    private async Task RunTestAsync()
    {
        if (_cts is not null) return;

        var names = CheckedNicNames();
        var options = new TestOptions
        {
            TestDownload = _cbDown.Checked,
            TestUpload = _cbUp.Checked,
            TestPing = _cbPing.Checked,
            Seconds = (int)_seconds.Value,
            Threads = (int)_threads.Value,
            WarmupSeconds = (int)_warmup.Value,
            PlanMbps = (double)_plan.Value,
            UploadUrl = string.IsNullOrWhiteSpace(_uploadUrl.Text) ? TrafficEngine.DefaultUploadUrl : _uploadUrl.Text.Trim(),
            NicNames = names,
        };

        _settings.TestDownload = options.TestDownload;
        _settings.TestUpload = options.TestUpload;
        _settings.TestPing = options.TestPing;
        _settings.Seconds = options.Seconds;
        _settings.Threads = options.Threads;
        _settings.WarmupSeconds = options.WarmupSeconds;
        _settings.PlanMbps = options.PlanMbps;
        _settings.UploadUrl = options.UploadUrl;
        _settings.SelectedNics = names;
        _settings.Save();

        _chart.PlanMbps = options.PlanMbps;
        _chart.Clear();
        _chart.SetSubText(options.AnyBandwidth ? "" : "只测延迟 —— 没有吞吐曲线");
        _idlePings.Clear();
        _loadedPings.Clear();
        _livePing = null;
        _report.Text = "";
        _verdict.Text = "测速中…";
        _verdict.ForeColor = MutedInk;
        _latencyLine.Text = "延迟  —— 还没测";
        SetRunning(true);

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var live = new List<double>();

        var runner = new TestRunner();

        runner.Status += s => Ui(() => _statusText.Text = s);

        runner.Phase += phase => Ui(() =>
        {
            if (phase is "下载" or "上传")
            {
                _chart.BeginRun(names, phase);
                live.Clear();
            }
        });

        runner.SampleTick += (second, perNic, payload) => Ui(() =>
        {
            double total = 0;
            for (int i = 0; i < perNic.Length; i++)
            {
                _chart.AddPoint(i, perNic[i]);
                total += perNic[i];
            }
            live.Add(total);
            _chart.SetPayload(payload);
            _chart.SetSubText(live.Count > 0 ? $"稳态均 {live.Average():N0} Mbps" : "");
            _statusText.Text = $"第 {second} 秒 · 合计 {total:N0} Mbps";
        });

        runner.PingUpdated += p => Ui(() => { _livePing = p; _latencyLine.Text = LatencyText(); });

        runner.PingDone += (p, loaded) => Ui(() =>
        {
            _livePing = null;
            (loaded ? _loadedPings : _idlePings)[p.Label] = p;
            _latencyLine.Text = LatencyText();
        });

        TestReport report;
        try
        {
            report = await runner.RunAsync(options, token);
        }
        catch (Exception ex)
        {
            Program.LogCrash("测速过程", ex);
            report = new TestReport { Error = "意外错误：" + ex.Message, PlanMbps = options.PlanMbps, TestsText = options.TestsText };
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetRunning(false);
        }

        // 判读在右边那块面板上已经有了，报告里就不再重复
        _report.Text = Verdict.BuildText(report, includeVerdict: false);
        _report.Select(0, 0);
        _report.ScrollToCaret();

        var (verdictText, level) = Verdict.Judge(report);
        _verdict.Text = verdictText;
        _verdict.ForeColor = Verdict.LevelColor(level);

        if (report.Ok) HistoryStore.Append(ToHistory(report));

        _statusText.Text = report.Stopped ? "已停止" : "完成";
    }

    private static HistoryEntry ToHistory(TestReport r) => new()
    {
        Time = DateTime.Now,
        Tests = r.TestsText,
        Nics = string.Join(" + ", r.Nics.Select(n => n.Snapshot.Name)),
        DownAvg = r.TotalDownAvg,
        UpAvg = r.TotalUpAvg,
        // 公网那个比网关更有参考价值（网关就亚毫秒）
        IdlePingMs = r.IdlePings.FirstOrDefault(p => p.Label == "公网")?.AvgMs ?? 0,
        LoadedPingMs = r.LoadedPings.FirstOrDefault(p => p.Label == "公网")?.AvgMs ?? 0,
        Report = Verdict.BuildText(r),      // 存历史的那份要带判读，自包含
    };

    private string LatencyText()
    {
        var lines = new List<string>();
        if (_idlePings.Count > 0)
            lines.Add("延迟  空闲  " + string.Join("      ", _idlePings.Values.Select(p => p.Short)));
        if (_loadedPings.Count > 0)
            lines.Add((lines.Count == 0 ? "延迟  " : "      ") + "满载  " + string.Join("      ", _loadedPings.Values.Select(p => p.Short)));
        if (_livePing is not null)
            lines.Add((lines.Count == 0 ? "延迟  " : "      ") + "测中  " + _livePing.Short);

        return lines.Count == 0 ? "延迟  —— 还没测" : string.Join("\n", lines);
    }

    private void SetRunning(bool running)
    {
        _running = running;
        _stop.Enabled = running;
        _seconds.Enabled = !running;
        _threads.Enabled = !running;
        _warmup.Enabled = !running;
        _plan.Enabled = !running;
        _uploadUrl.Enabled = !running;
        _cbDown.Enabled = !running;
        _cbUp.Enabled = !running;
        _cbPing.Enabled = !running;
        _nicList.Enabled = !running;
        UpdateStartEnabled();
        if (running) _statusText.Text = "启动中…";
    }

    private void Ui(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(action); } catch (ObjectDisposedException) { }
        }
        else
        {
            action();
        }
    }

    private void CopyReport()
    {
        if (_report.TextLength == 0) return;
        try
        {
            Clipboard.SetText(_report.Text);
            _statusText.Text = "结果已复制到剪贴板";
        }
        catch (Exception ex)
        {
            _statusText.Text = "复制失败：" + ex.Message;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 关窗口时把还在飞的连接掐断，别让带宽一直被占着
        try { _cts?.Cancel(); } catch { }
        base.OnFormClosing(e);
    }
}
