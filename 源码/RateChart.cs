using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace NetSpeedTest;

/// <summary>
/// 实时速率折线，GDI+ 自绘 + 双缓冲。一块网卡一条线。
///
/// 配色是跑过校验的（深色画布 #1a1a19）：数据线按固定顺序取分类色槽
/// （深色档 #3987e5 / #d95926 / #199e70 / #c98500 …，这个顺序的相邻两色
/// 色盲分离度和对比度都过闸）。套餐参考虚线是**图表装饰**不是序列，
/// 所以走中性灰 #898781 并直接贴标签。文字一律中性墨色，不穿数据线的颜色。
/// </summary>
sealed class RateChart : Control
{
    private static readonly Color Surface = Color.FromArgb(0x1a, 0x1a, 0x19);
    private static readonly Color GridColor = Color.FromArgb(0x2c, 0x2c, 0x2a);
    private static readonly Color AxisColor = Color.FromArgb(0x38, 0x38, 0x35);
    private static readonly Color Muted = Color.FromArgb(0x89, 0x87, 0x81);
    private static readonly Color Ink = Color.FromArgb(0xf0, 0xef, 0xec);

    /// <summary>分类色槽（深色档），按顺序取，不循环。</summary>
    private static readonly Color[] SeriesColors =
    {
        Color.FromArgb(0x39, 0x87, 0xe5),   // 蓝
        Color.FromArgb(0xd9, 0x59, 0x26),   // 橙
        Color.FromArgb(0x19, 0x9e, 0x70),   // 青
        Color.FromArgb(0xc9, 0x85, 0x00),   // 黄
        Color.FromArgb(0xd5, 0x51, 0x81),   // 品红
        Color.FromArgb(0x90, 0x85, 0xe9),   // 紫
    };

    private readonly List<string> _names = new();
    private readonly List<List<double>> _series = new();
    private readonly Font _heroFont, _unitFont, _tickFont, _smallFont;
    private readonly Pen _gridPen, _axisPen, _refPen;
    private readonly SolidBrush _surfaceBrush, _inkBrush;
    private readonly Pen[] _linePens;
    private readonly SolidBrush[] _lineBrushes;

    private string _phase = "";
    private string _sub = "";
    private long _payload;
    private double _yMax = 1000;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double PlanMbps { get; set; } = 1000;

    public RateChart()
    {
        // ★ OptimizedDoubleBuffer 必须和 UserPaint + AllPaintingInWmPaint 一起设才生效；
        //   AllPaintingInWmPaint 抑制 WM_ERASEBKGND，是消闪的关键。
        //   ResizeRedraw 不加的话拖窗口时折线会撕裂。
        SetStyle(ControlStyles.UserPaint
               | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Surface;

        // 画笔刷子做成字段 —— 在 OnPaint 里 new 的话 resize 时高频重绘会把 GDI 句柄耗光
        _heroFont = new Font("Microsoft YaHei UI", 26f, FontStyle.Bold);
        _unitFont = new Font("Microsoft YaHei UI", 10f);
        _tickFont = new Font("Consolas", 8.5f);
        _smallFont = new Font("Microsoft YaHei UI", 8.5f);

        _gridPen = new Pen(GridColor, 1f);
        _axisPen = new Pen(AxisColor, 1f);
        _refPen = new Pen(Muted, 1f) { DashStyle = DashStyle.Dash };
        _surfaceBrush = new SolidBrush(Surface);
        _inkBrush = new SolidBrush(Ink);

        _linePens = SeriesColors.Select(c => new Pen(c, 2f) { LineJoin = LineJoin.Round }).ToArray();
        _lineBrushes = SeriesColors.Select(c => new SolidBrush(c)).ToArray();
    }

    // ------------------------------------------------------------------ 外部接口

    public void BeginRun(IReadOnlyList<string> names, string phase)
    {
        _names.Clear();
        _names.AddRange(names);
        _series.Clear();
        for (int i = 0; i < names.Count; i++) _series.Add(new List<double>());
        _phase = phase;
        _sub = "";
        _payload = 0;
        Invalidate();
    }

    public void Clear()
    {
        _names.Clear();
        _series.Clear();
        _phase = "";
        _sub = "";
        _payload = 0;
        Invalidate();
    }

    public void AddPoint(int series, double mbps)
    {
        if (series < 0 || series >= _series.Count) return;
        _series[series].Add(mbps);
        Invalidate();
    }

    public void SetSubText(string text) { _sub = text; Invalidate(); }
    public void SetPayload(long bytes) { _payload = bytes; Invalidate(); }

    /// <summary>当前合计（各序列最新值之和）。</summary>
    public double TotalMbps
    {
        get
        {
            double t = 0;
            foreach (var s in _series) if (s.Count > 0) t += s[^1];
            return t;
        }
    }

    // ------------------------------------------------------------------ 绘制

    protected override void OnPaintBackground(PaintEventArgs pevent) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.FillRectangle(_surfaceBrush, ClientRectangle);

        float scale = DeviceDpi / 96f;
        float padLeft = 62f * scale, padRight = 24f * scale, padBottom = 24f * scale;
        // 顶部要留出：大数字（+ 多序列时的图例）+ 副标题；不留的话 Y 轴顶格刻度会和大数字叠上
        float padTop = (_series.Count >= 2 ? 92f : 76f) * scale;

        var plot = new RectangleF(
            padLeft, padTop,
            Math.Max(10f, Width - padLeft - padRight),
            Math.Max(10f, Height - padTop - padBottom));

        RecomputeYMax();
        DrawGrid(g, plot, scale);
        DrawReferenceLine(g, plot, scale);
        for (int i = 0; i < _series.Count; i++) DrawSeries(g, plot, i);
        DrawHeader(g, scale);
    }

    private void RecomputeYMax()
    {
        double peak = PlanMbps;
        foreach (var s in _series) foreach (var v in s) if (v > peak) peak = v;
        double total = TotalMbps;
        if (total > peak) peak = total;
        _yMax = NiceMax(peak * 1.06);
    }

    /// <summary>把上限吸到几个好看的刻度上 —— 纯自适应上下抽动反而看不出抖动。</summary>
    private static double NiceMax(double v)
    {
        double[] steps = { 100, 200, 300, 500, 750, 1000, 1250, 1500, 2000, 2500, 3000, 4000, 5000, 7500, 10000 };
        foreach (var s in steps) if (v <= s) return s;
        return Math.Ceiling(v / 5000) * 5000;
    }

    private void DrawGrid(Graphics g, RectangleF plot, float scale)
    {
        for (int i = 0; i <= 4; i++)
        {
            double val = _yMax * i / 4.0;
            float y = plot.Bottom - (float)(val / _yMax) * plot.Height;
            // 注意：这两支笔是缓存的字段，绝不能用 using —— 会被 Dispose 掉，
            // 第二帧再画就是"Parameter is not valid"
            g.DrawLine(i == 0 ? _axisPen : _gridPen, plot.Left, y, plot.Right, y);
            if (i > 0)
            {
                var tickBox = new Rectangle((int)(2 * scale), (int)(y - 7 * scale), (int)(54 * scale), (int)(14 * scale));
                TextRenderer.DrawText(g, val.ToString("N0"), _tickFont, tickBox, Muted,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        // X 轴刻度：每 5 秒一根。位置必须和数据点用同一套映射（第 s 秒 = 下标 s-1），
        // 否则刻度会跟折线对不上。
        int cap = Capacity();
        for (int s = 5; s <= cap; s += 5)
        {
            float x = plot.Left + (float)(s - 1) / Math.Max(1, cap - 1) * plot.Width;
            if (x > plot.Right) break;
            g.DrawLine(_gridPen, x, plot.Top, x, plot.Bottom);
            TextRenderer.DrawText(g, s + "s", _tickFont,
                new Rectangle((int)(x - 16 * scale), (int)plot.Bottom + 4, (int)(32 * scale), (int)(14 * scale)),
                Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>
    /// 绘图区能放下多少个点：向上取到 5 的倍数，且不小于 10。
    /// 每档只挪 5 个，所以跑的过程中 X 轴基本不跳；跑完 15 秒的测试刚好铺满整个宽度。
    /// </summary>
    private int Capacity()
    {
        int most = 0;
        foreach (var s in _series) most = Math.Max(most, s.Count);
        int need = Math.Max(most, 10);
        return Math.Min(240, (need + 4) / 5 * 5);
    }

    private void DrawReferenceLine(Graphics g, RectangleF plot, float scale)
    {
        if (PlanMbps <= 0 || PlanMbps > _yMax) return;

        float y = plot.Bottom - (float)(PlanMbps / _yMax) * plot.Height;
        g.DrawLine(_refPen, plot.Left, y, plot.Right, y);

        // 用 Rectangle + Right 对齐来排版，别拿 MeasureText 的结果自己算坐标 ——
        // 带中文时会量窄，标签会被右边缘切掉。
        string label = $"{PlanMbps:N0} Mbps 套餐";
        var measured = TextRenderer.MeasureText(g, label, _smallFont, Size.Empty, TextFormatFlags.NoPadding);
        int boxW = measured.Width + 20;
        int boxH = measured.Height + 2;
        int boxX = Math.Max((int)plot.Left, (int)(plot.Right - boxW - 2 * scale));
        int boxY = (int)(y - boxH - 2 * scale);
        if (boxY < plot.Top) boxY = (int)(y + 2 * scale);

        var box = new Rectangle(boxX, boxY, boxW, boxH);
        g.FillRectangle(_surfaceBrush, box);
        TextRenderer.DrawText(g, label, _smallFont, box, Muted,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private void DrawSeries(Graphics g, RectangleF plot, int index)
    {
        var pts = _series[index];
        if (pts.Count == 0) return;

        int cap = Capacity();
        float step = plot.Width / Math.Max(1, cap - 1);
        var line = new PointF[pts.Count];
        for (int i = 0; i < pts.Count; i++)
        {
            float x = plot.Left + i * step;
            float y = plot.Bottom - (float)Math.Clamp(pts[i] / _yMax, 0, 1) * plot.Height;
            line[i] = new PointF(x, y);
        }

        var pen = _linePens[index % _linePens.Length];
        if (line.Length >= 2) g.DrawLines(pen, line);

        // 最新点：8px 实心点 + 2px 画布色描边（和线叠在一起时也看得清）
        var last = line[^1];
        g.FillEllipse(_surfaceBrush, last.X - 6f, last.Y - 6f, 12f, 12f);
        g.FillEllipse(_lineBrushes[index % _lineBrushes.Length], last.X - 4f, last.Y - 4f, 8f, 8f);
    }

    private void DrawHeader(Graphics g, float scale)
    {
        float x = 14f * scale, y = 8f * scale;

        string num = TotalMbps.ToString("N0");
        var numSize = TextRenderer.MeasureText(g, num, _heroFont, Size.Empty, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, num, _heroFont, new Point((int)x, (int)y), Ink, TextFormatFlags.NoPadding);

        float unitX = x + numSize.Width + 10 * scale;
        TextRenderer.DrawText(g, "Mbps", _unitFont,
            new Point((int)unitX, (int)(y + numSize.Height - 19 * scale)), Muted, TextFormatFlags.NoPadding);

        if (!string.IsNullOrEmpty(_phase))
        {
            var unitSize = TextRenderer.MeasureText(g, "Mbps", _unitFont, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, $"（{_phase}）", _unitFont,
                new Point((int)(unitX + unitSize.Width + 8 * scale), (int)(y + numSize.Height - 19 * scale)),
                Muted, TextFormatFlags.NoPadding);
        }

        float lineY = y + numSize.Height + 1 * scale;

        // 图例：>= 2 条线才需要（单条线用标题就说明了）。色块带身份，文字保持中性墨色。
        if (_series.Count >= 2)
        {
            float lx = x;
            for (int i = 0; i < _series.Count; i++)
            {
                var brush = _lineBrushes[i % _lineBrushes.Length];
                g.FillEllipse(brush, lx, lineY + 5 * scale, 7 * scale, 7 * scale);
                lx += 11 * scale;

                double cur = _series[i].Count > 0 ? _series[i][^1] : 0;
                string text = $"{Trim(_names[i], 12)} {cur:N0}";
                TextRenderer.DrawText(g, text, _smallFont, new Point((int)lx, (int)lineY), Muted, TextFormatFlags.NoPadding);
                lx += TextRenderer.MeasureText(g, text, _smallFont, Size.Empty, TextFormatFlags.NoPadding).Width + 18 * scale;
            }
            lineY += 16 * scale;
        }

        string sub = string.IsNullOrEmpty(_sub) ? "" : _sub;
        if (_payload > 0)
            sub = sub.Length > 0 ? $"{sub}    ·    已传 {_payload / 1e9:N2} GB" : $"已传 {_payload / 1e9:N2} GB";
        if (sub.Length > 0)
            TextRenderer.DrawText(g, sub, _smallFont, new Point((int)x, (int)lineY), Muted, TextFormatFlags.NoPadding);
    }

    private static string Trim(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _heroFont.Dispose(); _unitFont.Dispose(); _tickFont.Dispose(); _smallFont.Dispose();
            _gridPen.Dispose(); _axisPen.Dispose(); _refPen.Dispose();
            _surfaceBrush.Dispose(); _inkBrush.Dispose();
            foreach (var p in _linePens) p.Dispose();
            foreach (var b in _lineBrushes) b.Dispose();
        }
        base.Dispose(disposing);
    }
}
