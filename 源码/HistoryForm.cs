using System.Text;

namespace NetSpeedTest;

/// <summary>历史记录查看器：一条一次测试，选中看完整报告。</summary>
sealed class HistoryForm : Form
{
    private static readonly Color PagePlane = Color.FromArgb(0xf9, 0xf9, 0xf7);
    private static readonly Color InkColor = Color.FromArgb(0x0b, 0x0b, 0x0b);
    private static readonly Color SecondaryInk = Color.FromArgb(0x52, 0x51, 0x4e);

    private readonly ListView _list = new();
    private readonly TextBox _detail = new();
    private readonly Label _count = new();
    private readonly SplitContainer _split = new();
    private List<HistoryEntry> _entries = new();

    public HistoryForm()
    {
        Text = "历史记录";
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = PagePlane;
        ClientSize = new Size(1020, 640);
        MinimumSize = new Size(760, 480);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;

        BuildLayout();
        Reload();
    }

    private void BuildLayout()
    {
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.Dock = DockStyle.Fill;
        _list.Font = new Font("Microsoft YaHei UI", 9F);
        _list.Columns.Add("时间", 150);
        _list.Columns.Add("测了", 130);
        _list.Columns.Add("网卡", 170);
        _list.Columns.Add("下载 Mbps", 90, HorizontalAlignment.Right);
        _list.Columns.Add("上传 Mbps", 90, HorizontalAlignment.Right);
        _list.Columns.Add("空闲延迟 ms", 95, HorizontalAlignment.Right);
        _list.Columns.Add("满载延迟 ms", 95, HorizontalAlignment.Right);
        _list.SelectedIndexChanged += (_, _) => ShowDetail();

        _detail.Multiline = true;
        _detail.ReadOnly = true;
        _detail.WordWrap = false;
        _detail.ScrollBars = ScrollBars.Both;
        _detail.Dock = DockStyle.Fill;
        _detail.BackColor = Color.White;
        _detail.ForeColor = InkColor;
        _detail.Font = new Font("Consolas", 9F);

        _split.Dock = DockStyle.Fill;
        _split.Orientation = Orientation.Horizontal;
        _split.SplitterWidth = 6;
        _split.Panel1MinSize = 120;
        _split.Panel2MinSize = 120;
        _split.Panel1.Controls.Add(_list);
        _split.Panel2.Controls.Add(_detail);

        _count.ForeColor = SecondaryInk;
        _count.AutoSize = true;
        _count.Padding = new Padding(2, 6, 0, 0);

        var refresh = new Button { Text = "刷新", AutoSize = true, Padding = new Padding(10, 2, 10, 2) };
        refresh.Click += (_, _) => Reload();

        var export = new Button { Text = "导出 CSV", AutoSize = true, Padding = new Padding(10, 2, 10, 2) };
        export.Click += (_, _) => ExportCsv();

        var openFolder = new Button { Text = "打开所在文件夹", AutoSize = true, Padding = new Padding(10, 2, 10, 2) };
        openFolder.Click += (_, _) => OpenFolder();

        var clear = new Button { Text = "清空历史", AutoSize = true, Padding = new Padding(10, 2, 10, 2) };
        clear.Click += (_, _) => ClearAll();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(6),
        };
        buttons.Controls.Add(_count);
        buttons.Controls.Add(refresh);
        buttons.Controls.Add(export);
        buttons.Controls.Add(openFolder);
        buttons.Controls.Add(clear);

        Controls.Add(_split);
        Controls.Add(buttons);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // SplitterDistance 必须等布局完再设 —— 在构造函数里设，控件还没定尺寸，会抛异常
        try { _split.SplitterDistance = Math.Max(_split.Panel1MinSize, (int)(_split.Height * 0.42)); } catch { }
    }

    private void Reload()
    {
        _entries = HistoryStore.LoadAll();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var e in _entries)
        {
            var item = new ListViewItem(e.TimeText);
            item.SubItems.Add(e.Tests);
            item.SubItems.Add(e.Nics);
            item.SubItems.Add(e.DownText);
            item.SubItems.Add(e.UpText);
            item.SubItems.Add(e.IdlePingText);
            item.SubItems.Add(e.LoadedPingText);
            _list.Items.Add(item);
        }
        _list.EndUpdate();

        _count.Text = _entries.Count == 0
            ? "还没有记录 —— 跑一次测试就会自动记在这里"
            : $"共 {_entries.Count} 条（最新在上面）";

        if (_list.Items.Count > 0) _list.Items[0].Selected = true;
        else _detail.Text = "";
    }

    private void ShowDetail()
    {
        int i = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : -1;
        _detail.Text = i >= 0 && i < _entries.Count ? _entries[i].Report : "";
        _detail.Select(0, 0);
        _detail.ScrollToCaret();
    }

    private void ExportCsv()
    {
        if (_entries.Count == 0) return;
        using var dlg = new SaveFileDialog
        {
            Filter = "CSV|*.csv",
            FileName = $"网速测试历史-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            InitialDirectory = AppContext.BaseDirectory,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var sb = new StringBuilder();
        sb.AppendLine("时间,测了,网卡,下载Mbps,上传Mbps,空闲延迟ms,满载延迟ms");
        foreach (var e in _entries)
            sb.AppendLine(string.Join(",",
                Csv(e.TimeText), Csv(e.Tests), Csv(e.Nics),
                e.DownAvg.ToString("0"), e.UpAvg.ToString("0"),
                e.IdlePingMs.ToString("0.0"), e.LoadedPingMs.ToString("0.0")));

        try
        {
            // Excel 认 BOM，不加的话中文列会乱码
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            _count.Text = "已导出：" + dlg.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string Csv(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private void OpenFolder()
    {
        try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + HistoryStore.FilePath + "\""); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "打不开", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void ClearAll()
    {
        if (_entries.Count == 0) return;
        var ok = MessageBox.Show(this, $"确定清空 {_entries.Count} 条历史记录？没法撤销。",
            "清空历史", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (ok != DialogResult.Yes) return;
        HistoryStore.Clear();
        Reload();
    }
}
