using Binance.Net.Enums;
using Common;
using Common.Helper;
using ScottPlot;
using ScottPlot.WinForms;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Test;
using Test.ChannelPlayback.WinForms.Engine;
using Test.ChannelPlayback.WinForms.Models;
using Color = System.Drawing.Color;
using Font = System.Drawing.Font;
using FontStyle = System.Drawing.FontStyle;
using Label = System.Windows.Forms.Label;
using Orientation = System.Windows.Forms.Orientation;

namespace Test.ChannelPlayback.WinForms.Forms
{
    /// <summary>
    /// 动态外包络通道与 K 线回放主界面 (WinForms)
    /// 特性：
    /// 1. 响应式布局优化：回放核心控制常驻置顶，参数与监控标签页化，彻底杜绝小屏幕或高 DPI 下控件看不全的问题
    /// 2. 界面参数与窗口状态全自动记忆与持久化恢复 (ui_settings)
    /// 3. 采用 Test 内高性能真实行情读取机制，100% 真实数据回放，支持任意指定周期
    /// 4. 默认 200 长度通道 (左 100 根基准分析 + 右 100 根延伸)，严格包含所有高低点，实时动态演变高度与角度
    /// 5. 点击任意 K 线即刻在日志右侧联动呈现微观逐笔 Tick 数据（走势图 + 成交明细表）
    /// </summary>
    public class MainChannelPlaybackForm : Form
    {
        private readonly KlinePlaybackEngine _engine;
        private DynamicChannelResult _currentChannel;
        private RawKline _currentKline;
        private int _currentBarIndex = -1;
        private volatile bool _isRendering = false;
        private bool _isInitializing = true;

        // UI 核心布局控件
        private SplitContainer splitMain = null!;
        private SplitContainer splitLeft = null!;
        private FormsPlot formsPlot = null!;
        private Panel panelRight = null!;

        // 图表顶部独立状态栏控件
        private Panel panelChartHeader = null!;
        private Label lblChartHeaderTitle = null!;
        private Label lblChartHeaderStats = null!;
        private CheckBox chkShowLegend = null!;
        private CheckBox chkHeaderShowKlineHighLow = null!;
        private ComboBox cboHeaderChartType = null!;

        // 右侧顶部常驻回放控制
        private GroupBox grpPlayback = null!;
        private Button btnPlay = null!;
        private Button btnPause = null!;
        private Button btnStep = null!;
        private Button btnReset = null!;
        private TrackBar tbSpeed = null!;
        private Label lblSpeedVal = null!;
        private TrackBar tbProgress = null!;
        private Label lblProgressVal = null!;

        // 右侧选项卡容器
        private TabControl tabRight = null!;
        private TabPage tabChannel = null!;
        private TabPage tabData = null!;
        private TabPage tabMetrics = null!;

        // Tab 1: 通道参数控件
        private NumericUpDown numLeftLen = null!;
        private NumericUpDown numRightLen = null!;
        private Label lblTotalLength = null!;
        private ComboBox cboCalcMode = null!;
        private ComboBox cboWindowMode = null!;
        private ComboBox cboChartType = null!;
        private CheckBox chkAutoScale = null!;
        private CheckBox chkFollowLatest = null!;
        private CheckBox chkShowTouchMarkers = null!;
        private CheckBox chkShowKlineHighLow = null!;
        private CheckBox chkEnableRetainChannel = null!;
        private NumericUpDown numConfirmBars = null!;
        private ComboBox cboBreakoutRule = null!;
        private CheckBox chkSpecialRetained = null!;
        private NumericUpDown numSpecialRetainedBars = null!;
        private NumericUpDown numSpecialRetainedAngle = null!;
        private CheckBox chkEnableVPattern = null!;
        private NumericUpDown numVPatternThreshold = null!;
        private CheckBox chkShowVPatternLines = null!;
        private CheckBox chkEnableConsecutiveTrend = null!;
        private NumericUpDown numConsecutiveBars = null!;
        private NumericUpDown numConsecutivePct = null!;
        private ComboBox cboConsecPriceMode = null!;
        private CheckBox chkShowConsecutiveChannel = null!;
        private CheckBox chkEnableChannelAutoUpdate = null!;
        private ComboBox cboChannelUpdateMode = null!;
        private CheckBox chkShowHistoricalChannels = null!;
        private CheckBox chkEnableReversalOrder = null!;
        private NumericUpDown numReversalMinutes = null!;
        private NumericUpDown numReversalPLong = null!;
        private NumericUpDown numReversalPMedium = null!;
        private NumericUpDown numReversalPShort = null!;
        private NumericUpDown numReversalTickPullback = null!;
        private NumericUpDown numReversalChannelZone = null!;
        private CheckBox chkShowReversalYellowLines = null!;
        private CheckBox chkShowObservationCycles = null!;

        // Tab 2: 数据源控件
        private ComboBox cboCoin = null!;
        private ComboBox cboInterval = null!;
        private DateTimePicker dtpStart = null!;
        private DateTimePicker dtpEnd = null!;
        private Button btnLoadData = null!;
        private Label lblDataHint = null!;

        // Tab 3: 实时监控看板控件
        private Label lblMetricPrice = null!;
        private Label lblMetricType = null!;
        private Label lblMetricP1P2 = null!;
        private Label lblMetricP3 = null!;
        private Label lblMetricHeight = null!;
        private Label lblMetricAngle = null!;
        private Label lblMetricSlope = null!;
        private Label lblMetricUpper = null!;
        private Label lblMetricLower = null!;
        private Label lblMetricTouch = null!;
        private Label lblMetricRetainedStatus = null!;
        private Label lblMetricRetainedExtreme = null!;
        private Label lblMetricRetainedBoundary = null!;
        private Label lblMetricRetainedBreakout = null!;
        private Label lblMetricVPatternSummary = null!;
        private Label lblMetricLatestVPattern = null!;
        private Label lblMetricConsecutiveTrendSummary = null!;
        private Label lblMetricLatestConsecutiveTrend = null!;
        private Label lblMetricReversalSummary = null!;
        private Label lblMetricLatestReversalSignal = null!;

        // 底部左右分栏 (左侧日志，右侧 Tick 详情)
        private SplitContainer splitBottom = null!;

        // 左侧日志面板控件
        private Panel panelLogContainer = null!;
        private Panel panelLogHeader = null!;
        private Label lblLogTitle = null!;
        private Button btnClearLog = null!;
        private RichTextBox txtLog = null!;

        // 右侧微观 Tick 详情控件
        private Panel panelTickDetail = null!;
        private Panel panelTickHeader = null!;
        private Label lblTickTitle = null!;
        private Label lblTickInfo = null!;
        private TabControl tabTickViews = null!;
        private TabPage tabTickTable = null!;
        private TabPage tabTickPlot = null!;
        private DataGridView dgvTicks = null!;
        private FormsPlot formsPlotTick = null!;
        private readonly List<TickViewModel> _displayedTicks = new();

        // Tick 查看周期切换工具栏与合成 K 线控件
        private FlowLayoutPanel pnlTickPeriodBar = null!;
        private Label lblTickPeriodTag = null!;
        private Button btnPeriodRaw = null!;
        private Button btnPeriod1m = null!;
        private Button btnPeriod5m = null!;
        private Button btnPeriod15m = null!;
        private Button btnPeriodCustom = null!;
        private NumericUpDown numCustomTickPeriod = null!;
        private Label lblCustomMinUnit = null!;
        private Label lblPeriodSummary = null!;

        private TabPage tabKlines = null!;
        private TabPage tabRawTicks = null!;
        private DataGridView dgvSynthesizedKlines = null!;
        private readonly List<SynthesizedKlineViewModel> _displayedKlines = new();

        // 缓存最近加载的原始 Tick 及对应 K 线区间
        private RawTick[]? _cachedRawTicks = null;
        private int _cachedTickStartIndex = -1;
        private int _cachedTickEndIndex = -1;
        private bool _isEvaluatingReversalFromTicks = false;

        // 当前选中的 Tick 周期模式 (0: 原始逐笔 Tick, 1: 1分钟, 2: 5分钟, 3: 15分钟, 4: 自定义分钟)
        private int _tickPeriodMode = 0;
        private int _customTickMinutes = 3;

        // K 线鼠标点击与高亮选中状态 (支持单击单选 / Shift 连续多选 / 绿色通道整体选中)
        private bool _isPlotMouseDown = false;
        private System.Drawing.Point _plotMouseDownPos;
        private int? _selectedBarIndex = null;
        private int? _selectedBarStartIndex = null;
        private int? _selectedBarEndIndex = null;
        private ConsecutiveTrendItem? _selectedConsecutiveTrend = null;

        /// <summary>
        /// 逐笔 Tick 虚拟列表视图模型
        /// </summary>
        private class TickViewModel
        {
            public int TickIndex { get; set; }
            public int BarIndex { get; set; }
            public string BarTimeStr { get; set; } = "";
            public bool IsFirstTickOfBar { get; set; }
            public long Time { get; set; }
            public decimal Price { get; set; }
            public decimal Qty { get; set; }
            public decimal QuoteQty { get; set; }
            public bool IsBuyer { get; set; }
            public decimal DiffPct { get; set; }
            public string ChannelRelation { get; set; } = "";
        }

        /// <summary>
        /// 由 Tick 数据流式聚合合成的指定分钟周期 K 线视图模型
        /// </summary>
        private class SynthesizedKlineViewModel
        {
            public int Index { get; set; }
            public long OpenTime { get; set; }
            public long CloseTime { get; set; }
            public string TimeRangeStr { get; set; } = "";
            public decimal Open { get; set; }
            public decimal High { get; set; }
            public decimal Low { get; set; }
            public decimal Close { get; set; }
            public decimal ChangePct { get; set; }
            public decimal AmplitudePct { get; set; }
            public decimal Volume { get; set; }
            public decimal QuoteVolume { get; set; }
            public long TradeCount { get; set; }
            public decimal TakerBuyVolume { get; set; }
            public decimal TakerBuyRatio { get; set; }
            public string ChannelRelation { get; set; } = "";
        }

        public MainChannelPlaybackForm()
        {
            _engine = new KlinePlaybackEngine();
            InitializeComponents();
            BindEngineEvents();
            ApplyDarkTheme();

            // 窗体加载时恢复持久化记忆参数并启动首次加载
            this.Load += (s, e) =>
            {
                LoadSettingsToUi();
                _isInitializing = false;
            };

            this.Shown += async (s, e) =>
            {
                await LoadSelectedDataAsync();
            };
        }

        #region 初始化组件与响应式布局

        private void InitializeComponents()
        {
            this.Text = "动态包络通道与真实 K 线流式回放系统 (Dynamic Envelope Channel Playback)";
            this.Size = new Size(1600, 950);
            this.MinimumSize = new Size(1180, 720);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular, GraphicsUnit.Point);

            // 1. 主分割容器 (左右分割，左侧图表+日志，右侧控制面板)
            splitMain = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 6,
                FixedPanel = FixedPanel.Panel2
            };

            // 2. 左侧分割容器 (上下分割，上方图表，下方日志)
            splitLeft = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = 6
            };

            // ① 图表顶部独立状态栏 (原生 WinForms 容器，放置标题与实时指标，彻底释放画布空间)
            panelChartHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 36,
                BackColor = Color.FromArgb(15, 23, 42), // Slate 900
                Padding = new Padding(10, 4, 10, 4)
            };

            lblChartHeaderTitle = new Label
            {
                Text = "⚡ BTCUSDT · 1m · 动态包络通道",
                Font = new Font("Microsoft YaHei", 9.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248), // Sky 400
                AutoSize = true,
                Location = new Point(10, 8)
            };

            lblChartHeaderStats = new Label
            {
                Text = "真实行情数据就绪中...",
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular),
                ForeColor = Color.FromArgb(226, 232, 240), // Slate 200
                AutoSize = true,
                Location = new Point(250, 9),
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };

            chkShowLegend = new CheckBox
            {
                Text = "图例",
                Font = new Font("Microsoft YaHei", 8.5F),
                ForeColor = Color.FromArgb(148, 163, 184),
                AutoSize = true,
                Dock = DockStyle.Right,
                Padding = new Padding(6, 0, 2, 0),
                Checked = false
            };
            chkShowLegend.CheckedChanged += (s, e) =>
            {
                SaveSettingsFromUi();
                formsPlot.Refresh();
            };

            chkHeaderShowKlineHighLow = new CheckBox
            {
                Text = "⚪高低点",
                Font = new Font("Microsoft YaHei", 8.5F),
                ForeColor = Color.FromArgb(74, 222, 128),
                AutoSize = true,
                Dock = DockStyle.Right,
                Padding = new Padding(6, 0, 4, 0),
                Checked = true
            };
            chkHeaderShowKlineHighLow.CheckedChanged += (s, e) =>
            {
                if (_isInitializing) return;
                if (chkShowKlineHighLow != null && chkShowKlineHighLow.Checked != chkHeaderShowKlineHighLow.Checked)
                {
                    chkShowKlineHighLow.Checked = chkHeaderShowKlineHighLow.Checked;
                }
                SaveSettingsFromUi();
                RenderCurrentState();
            };

            cboHeaderChartType = new ComboBox
            {
                Dock = DockStyle.Right,
                Width = 105,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Microsoft YaHei", 8.5F),
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.FromArgb(241, 245, 249),
                FlatStyle = FlatStyle.Flat
            };
            cboHeaderChartType.Items.AddRange(new object[] { "🕯️ 蜡烛图", "📈 折线图" });
            cboHeaderChartType.SelectedIndex = 0;
            cboHeaderChartType.SelectedIndexChanged += (s, e) =>
            {
                if (_isInitializing) return;
                if (cboChartType != null && cboChartType.SelectedIndex != cboHeaderChartType.SelectedIndex)
                {
                    cboChartType.SelectedIndex = cboHeaderChartType.SelectedIndex;
                }
                SaveSettingsFromUi();
                RenderCurrentState();
            };

            panelChartHeader.Controls.Add(lblChartHeaderTitle);
            panelChartHeader.Controls.Add(lblChartHeaderStats);
            panelChartHeader.Controls.Add(chkHeaderShowKlineHighLow);
            panelChartHeader.Controls.Add(chkShowLegend);
            panelChartHeader.Controls.Add(cboHeaderChartType);

            // ② ScottPlot 图表控件 (纯净全屏展示 K 线与动态通道)
            formsPlot = new FormsPlot
            {
                Dock = DockStyle.Fill
            };
            formsPlot.DoubleClick += (s, e) =>
            {
                formsPlot.Plot.Axes.Margins(0.02, 0.08);
                formsPlot.Plot.Axes.AutoScale();
                formsPlot.Refresh();
            };
            formsPlot.MouseDown += OnFormsPlotMouseDown;
            formsPlot.MouseUp += OnFormsPlotMouseUp;
            formsPlot.MouseMove += OnFormsPlotMouseMove;

            splitLeft.Panel1.Controls.Add(formsPlot);
            splitLeft.Panel1.Controls.Add(panelChartHeader); // Dock Top 居于上方，Fill 填满剩余区域

            // ③ 底部左右分割容器 (左侧系统运行日志，右侧微观逐笔 Tick 详情)
            splitBottom = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 6
            };

            // A. 左侧：系统运行日志容器 (带标题与一键清空按钮)
            panelLogHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 28,
                BackColor = Color.FromArgb(30, 41, 59) // Slate 800
            };

            lblLogTitle = new Label
            {
                Text = "⚡ 系统运行日志与事件监控",
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(203, 213, 225),
                AutoSize = true,
                Location = new Point(8, 6)
            };

            btnClearLog = new Button
            {
                Text = "清空日志",
                Size = new Size(65, 22),
                Location = new Point(190, 3),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei", 8F),
                BackColor = Color.FromArgb(51, 65, 85),
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            btnClearLog.FlatAppearance.BorderSize = 0;
            btnClearLog.Click += (s, e) => txtLog.Clear();

            panelLogHeader.Controls.Add(lblLogTitle);
            panelLogHeader.Controls.Add(btnClearLog);

            txtLog = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.FromArgb(15, 23, 42), // Slate 900
                ForeColor = Color.FromArgb(226, 232, 240),
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9F, FontStyle.Regular)
            };

            panelLogContainer = new Panel { Dock = DockStyle.Fill };
            panelLogContainer.Controls.Add(txtLog);
            panelLogContainer.Controls.Add(panelLogHeader);
            splitBottom.Panel1.Controls.Add(panelLogContainer);

            // B. 右侧：微观逐笔 Tick 详情面板 (标题 + TabControl 双视图)
            panelTickDetail = new Panel { Dock = DockStyle.Fill };

            panelTickHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 46,
                BackColor = Color.FromArgb(30, 41, 59) // Slate 800
            };

            lblTickTitle = new Label
            {
                Text = "🔍 K线微观逐笔 Tick 明细",
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248), // Sky 400
                AutoSize = true,
                Location = new Point(8, 4)
            };

            lblTickInfo = new Label
            {
                Text = "未选中 K 线 (在上方图表中点击任意 K 线或绿色通道查看对应详情及逐笔成交)",
                Font = new Font("Microsoft YaHei", 8.5F),
                ForeColor = Color.FromArgb(148, 163, 184), // Slate 400
                AutoSize = true,
                Location = new Point(8, 24)
            };

            panelTickHeader.Controls.Add(lblTickTitle);
            panelTickHeader.Controls.Add(lblTickInfo);

            tabTickViews = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Microsoft YaHei", 8.5F)
            };

            tabTickTable = new TabPage("📑 逐笔成交明细表");
            tabTickTable.BackColor = Color.FromArgb(15, 23, 42);

            dgvTicks = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(15, 23, 42),
                GridColor = Color.FromArgb(51, 65, 85),
                BorderStyle = BorderStyle.None,
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                ReadOnly = true,
                MultiSelect = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                EnableHeadersVisualStyles = false,
                Font = new Font("Consolas", 8.5F),
                VirtualMode = true
            };

            typeof(DataGridView).InvokeMember(
                "DoubleBuffered",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.SetProperty,
                null, dgvTicks, new object[] { true });

            dgvTicks.CellValueNeeded += OnDgvTicksCellValueNeeded;
            dgvTicks.CellFormatting += OnDgvTicksCellFormatting;
            dgvTicks.SelectionChanged += OnDgvTicksSelectionChanged;
            dgvTicks.CellPainting += OnDgvTicksCellPainting;

            dgvTicks.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(30, 41, 59);
            dgvTicks.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(56, 189, 248);
            dgvTicks.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei", 8F, FontStyle.Bold);
            dgvTicks.DefaultCellStyle.BackColor = Color.FromArgb(15, 23, 42);
            dgvTicks.DefaultCellStyle.ForeColor = Color.FromArgb(241, 245, 249);
            dgvTicks.DefaultCellStyle.SelectionBackColor = Color.FromArgb(30, 58, 138);

            dgvTicks.Columns.Add("ColIdx", "#");
            dgvTicks.Columns.Add("ColBar", "K线时段");
            dgvTicks.Columns.Add("ColTime", "时间 (HH:mm:ss.fff)");
            dgvTicks.Columns.Add("ColPrice", "价格 (USDT)");
            dgvTicks.Columns.Add("ColQty", "数量 (Qty)");
            dgvTicks.Columns.Add("ColQuote", "成交额 (USDT)");
            dgvTicks.Columns.Add("ColSide", "主动买卖");
            dgvTicks.Columns.Add("ColChange", "偏离开盘 (%)");
            dgvTicks.Columns.Add("ColChannel", "通道位置");

            dgvTicks.Columns[0].Width = 45;
            dgvTicks.Columns[1].Width = 110;
            dgvTicks.Columns[2].Width = 135;
            dgvTicks.Columns[3].Width = 90;
            dgvTicks.Columns[4].Width = 85;
            dgvTicks.Columns[5].Width = 95;
            dgvTicks.Columns[6].Width = 85;
            dgvTicks.Columns[7].Width = 85;
            dgvTicks.Columns[8].Width = 100;

            tabTickTable.Controls.Add(dgvTicks);

            // --- 周期切换工具栏 ---
            pnlTickPeriodBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.FromArgb(24, 34, 52), // Navy Dark 850
                Padding = new Padding(6, 4, 6, 4),
                WrapContents = false
            };

            lblTickPeriodTag = new Label
            {
                Text = "⏱️ 查看周期:",
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248),
                AutoSize = true,
                Margin = new Padding(2, 6, 4, 2)
            };

            btnPeriodRaw = CreatePeriodButton("⚡ 原始Tick", 0);
            btnPeriod1m = CreatePeriodButton("🕯️ 1分钟", 1);
            btnPeriod5m = CreatePeriodButton("🕯️ 5分钟", 2);
            btnPeriod15m = CreatePeriodButton("🕯️ 15分钟", 3);
            btnPeriodCustom = CreatePeriodButton("⚙️ 自定义", 4);

            numCustomTickPeriod = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 1440,
                Value = 3,
                Width = 52,
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.FromArgb(241, 245, 249),
                Font = new Font("Consolas", 8.5F, FontStyle.Bold),
                Margin = new Padding(2, 4, 2, 2)
            };
            numCustomTickPeriod.ValueChanged += (s, e) =>
            {
                _customTickMinutes = (int)numCustomTickPeriod.Value;
                if (_tickPeriodMode == 4)
                {
                    SetTickPeriodMode(4);
                }
            };

            lblCustomMinUnit = new Label
            {
                Text = "分钟",
                Font = new Font("Microsoft YaHei", 8F),
                ForeColor = Color.FromArgb(148, 163, 184),
                AutoSize = true,
                Margin = new Padding(0, 7, 8, 2)
            };

            lblPeriodSummary = new Label
            {
                Text = "",
                Font = new Font("Microsoft YaHei", 8F),
                ForeColor = Color.FromArgb(74, 222, 128),
                AutoSize = true,
                Margin = new Padding(4, 7, 2, 2)
            };

            pnlTickPeriodBar.Controls.Add(lblTickPeriodTag);
            pnlTickPeriodBar.Controls.Add(btnPeriodRaw);
            pnlTickPeriodBar.Controls.Add(btnPeriod1m);
            pnlTickPeriodBar.Controls.Add(btnPeriod5m);
            pnlTickPeriodBar.Controls.Add(btnPeriod15m);
            pnlTickPeriodBar.Controls.Add(btnPeriodCustom);
            pnlTickPeriodBar.Controls.Add(numCustomTickPeriod);
            pnlTickPeriodBar.Controls.Add(lblCustomMinUnit);
            pnlTickPeriodBar.Controls.Add(lblPeriodSummary);

            // --- 合成 K 线表格与选项卡 ---
            tabKlines = new TabPage("📊 合成 K 线明细");
            tabKlines.BackColor = Color.FromArgb(15, 23, 42);

            dgvSynthesizedKlines = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(15, 23, 42),
                GridColor = Color.FromArgb(51, 65, 85),
                BorderStyle = BorderStyle.None,
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                ReadOnly = true,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                EnableHeadersVisualStyles = false,
                Font = new Font("Consolas", 8.5F),
                VirtualMode = true
            };

            typeof(DataGridView).InvokeMember(
                "DoubleBuffered",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.SetProperty,
                null, dgvSynthesizedKlines, new object[] { true });

            dgvSynthesizedKlines.CellValueNeeded += OnDgvSynthesizedKlinesCellValueNeeded;
            dgvSynthesizedKlines.CellFormatting += OnDgvSynthesizedKlinesCellFormatting;
            dgvSynthesizedKlines.SelectionChanged += OnDgvSynthesizedKlinesSelectionChanged;

            dgvSynthesizedKlines.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(30, 41, 59);
            dgvSynthesizedKlines.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(56, 189, 248);
            dgvSynthesizedKlines.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei", 8F, FontStyle.Bold);
            dgvSynthesizedKlines.DefaultCellStyle.BackColor = Color.FromArgb(15, 23, 42);
            dgvSynthesizedKlines.DefaultCellStyle.ForeColor = Color.FromArgb(241, 245, 249);
            dgvSynthesizedKlines.DefaultCellStyle.SelectionBackColor = Color.FromArgb(30, 58, 138);

            dgvSynthesizedKlines.Columns.Add("ColKIdx", "#");
            dgvSynthesizedKlines.Columns.Add("ColKTime", "时段范围 (HH:mm)");
            dgvSynthesizedKlines.Columns.Add("ColKOpen", "开盘 (Open)");
            dgvSynthesizedKlines.Columns.Add("ColKHigh", "最高 (High)");
            dgvSynthesizedKlines.Columns.Add("ColKLow", "最低 (Low)");
            dgvSynthesizedKlines.Columns.Add("ColKClose", "收盘 (Close)");
            dgvSynthesizedKlines.Columns.Add("ColKChange", "涨跌 (%)");
            dgvSynthesizedKlines.Columns.Add("ColKVol", "成交量");
            dgvSynthesizedKlines.Columns.Add("ColKQuote", "成交额 (USDT)");
            dgvSynthesizedKlines.Columns.Add("ColKTrades", "Tick笔数");
            dgvSynthesizedKlines.Columns.Add("ColKBuyRatio", "主买占比");
            dgvSynthesizedKlines.Columns.Add("ColKChannel", "通道位置");

            dgvSynthesizedKlines.Columns[0].Width = 40;
            dgvSynthesizedKlines.Columns[1].Width = 115;
            dgvSynthesizedKlines.Columns[2].Width = 90;
            dgvSynthesizedKlines.Columns[3].Width = 90;
            dgvSynthesizedKlines.Columns[4].Width = 90;
            dgvSynthesizedKlines.Columns[5].Width = 90;
            dgvSynthesizedKlines.Columns[6].Width = 85;
            dgvSynthesizedKlines.Columns[7].Width = 90;
            dgvSynthesizedKlines.Columns[8].Width = 100;
            dgvSynthesizedKlines.Columns[9].Width = 75;
            dgvSynthesizedKlines.Columns[10].Width = 80;
            dgvSynthesizedKlines.Columns[11].Width = 100;

            tabKlines.Controls.Add(dgvSynthesizedKlines);

            tabRawTicks = tabTickTable;

            tabTickPlot = new TabPage("📈 内部 Tick 分时走势");
            tabTickPlot.BackColor = Color.FromArgb(15, 23, 42);

            formsPlotTick = new FormsPlot
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(15, 23, 42)
            };
            formsPlotTick.DoubleClick += (s, e) =>
            {
                formsPlotTick.Plot.Axes.Margins(0.03, 0.28);
                formsPlotTick.Plot.Axes.AutoScale();
                formsPlotTick.Refresh();
            };
            tabTickPlot.Controls.Add(formsPlotTick);

            tabTickViews.TabPages.Add(tabTickTable);
            tabTickViews.TabPages.Add(tabTickPlot);

            panelTickDetail.Controls.Add(tabTickViews);
            panelTickDetail.Controls.Add(pnlTickPeriodBar);
            panelTickDetail.Controls.Add(panelTickHeader);
            splitBottom.Panel2.Controls.Add(panelTickDetail);

            splitLeft.Panel2.Controls.Add(splitBottom);

            splitMain.Panel1.Controls.Add(splitLeft);

            // 3. 右侧控制面板 (采用置顶回放控制 + 选项卡布局，彻底避免空间不足控件被遮挡)
            panelRight = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.FromArgb(30, 41, 59), // Slate 800
                Padding = new Padding(8, 6, 8, 12)
            };
            splitMain.Panel2.Controls.Add(panelRight);

            // --- A. 置顶常驻：K 线回放控制面板 ---
            grpPlayback = CreateGroupBox("🎮 1. K 线回放核心控制", 0, 185);
            grpPlayback.Dock = DockStyle.Top;
            {
                btnPlay = new Button
                {
                    Text = "▶ 播放",
                    Location = new Point(12, 25),
                    Size = new Size(76, 36),
                    BackColor = Color.FromArgb(5, 150, 105), // Emerald 600
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Microsoft YaHei", 9.5F, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnPlay.FlatAppearance.BorderSize = 0;
                btnPlay.Click += (s, e) => _engine.Play();

                btnPause = new Button
                {
                    Text = "⏸ 暂停",
                    Location = new Point(94, 25),
                    Size = new Size(76, 36),
                    BackColor = Color.FromArgb(217, 119, 6), // Amber 600
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Microsoft YaHei", 9.5F, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnPause.FlatAppearance.BorderSize = 0;
                btnPause.Click += (s, e) => _engine.Pause();

                btnStep = new Button
                {
                    Text = "⏭ 单步+1",
                    Location = new Point(176, 25),
                    Size = new Size(78, 36),
                    BackColor = Color.FromArgb(79, 70, 229), // Indigo 600
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnStep.FlatAppearance.BorderSize = 0;
                btnStep.Click += (s, e) => _engine.StepForward(1);

                btnReset = new Button
                {
                    Text = "⏹ 重置",
                    Location = new Point(260, 25),
                    Size = new Size(74, 36),
                    BackColor = Color.FromArgb(71, 85, 105), // Slate 600
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnReset.FlatAppearance.BorderSize = 0;
                btnReset.Click += (s, e) => _engine.Reset();

                lblSpeedVal = CreateLabel("回放间隔: 50 ms (20 bar/s)", 12, 70);
                tbSpeed = new TrackBar
                {
                    Location = new Point(8, 88),
                    Width = 330,
                    Minimum = 5,
                    Maximum = 500,
                    Value = 50,
                    TickFrequency = 50
                };
                tbSpeed.Scroll += (s, e) =>
                {
                    _engine.SetSpeed(tbSpeed.Value);
                    double barsPerSec = 1000.0 / tbSpeed.Value;
                    lblSpeedVal.Text = $"回放间隔: {tbSpeed.Value} ms ({barsPerSec:F1} bar/s)";
                    SaveSettingsFromUi();
                };

                lblProgressVal = CreateLabel("回放进度: 0 / 0 根", 12, 126);
                tbProgress = new TrackBar
                {
                    Location = new Point(8, 144),
                    Width = 330,
                    Minimum = 0,
                    Maximum = 100,
                    Value = 0,
                    TickStyle = TickStyle.None
                };
                tbProgress.Scroll += (s, e) =>
                {
                    _engine.SeekTo(tbProgress.Value);
                };

                grpPlayback.Controls.AddRange(new Control[] {
                    btnPlay, btnPause, btnStep, btnReset,
                    lblSpeedVal, tbSpeed, lblProgressVal, tbProgress
                });
            }

            // --- B. 下方选项卡容器 (分类明确，小屏大屏均能 100% 完整展示全部控件) ---
            tabRight = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular),
                Padding = new Point(10, 6)
            };

            tabChannel = new TabPage("📐 通道参数");
            tabChannel.BackColor = Color.FromArgb(30, 41, 59);
            tabChannel.AutoScroll = true;

            tabData = new TabPage("📂 真实数据源");
            tabData.BackColor = Color.FromArgb(30, 41, 59);

            tabMetrics = new TabPage("📊 实时看板");
            tabMetrics.BackColor = Color.FromArgb(30, 41, 59);

            // Tab 1: 通道参数设置
            {
                var lblL = CreateLabel("左侧分析长度:", 15, 18);
                numLeftLen = new NumericUpDown { Location = new Point(125, 15), Width = 190, Minimum = 10, Maximum = 1000, Value = 100 };
                numLeftLen.ValueChanged += (s, e) =>
                {
                    _engine.LeftLength = (int)numLeftLen.Value;
                    UpdateTotalLengthLabel();
                    SaveSettingsFromUi();
                    _engine.TriggerCurrentFrame();
                };

                var lblR = CreateLabel("右侧延长跨度:", 15, 48);
                numRightLen = new NumericUpDown { Location = new Point(125, 45), Width = 190, Minimum = 0, Maximum = 1000, Value = 100 };
                numRightLen.ValueChanged += (s, e) =>
                {
                    _engine.RightExtendLength = (int)numRightLen.Value;
                    UpdateTotalLengthLabel();
                    SaveSettingsFromUi();
                    _engine.TriggerCurrentFrame();
                };

                lblTotalLength = new Label
                {
                    Text = "📐 通道总长度: 200 根 (左 100 + 右 100)",
                    Location = new Point(15, 78),
                    AutoSize = true,
                    ForeColor = Color.FromArgb(250, 204, 21), // Yellow 400
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold)
                };

                var lblMode = CreateLabel("通道确认算法:", 15, 108);
                cboCalcMode = new ComboBox { Location = new Point(125, 105), Width = 190, DropDownWidth = 275, DropDownStyle = ComboBoxStyle.DropDownList };
                cboCalcMode.Items.AddRange(new object[] {
                    "三点趋势定向 (向上2低1高/向下2高1低) [推荐]",
                    "三点智能自适应 (紧凑优先)",
                    "强制 2低点1高点 (支撑优先)",
                    "强制 2高点1低点 (阻力优先)",
                    "线性回归包络 (全量均值)",
                    "极小高度包络 (紧密契合)"
                });
                cboCalcMode.SelectedIndex = 0;
                cboCalcMode.SelectedIndexChanged += (s, e) =>
                {
                    _engine.CalculationMode = (ChannelCalculationMode)cboCalcMode.SelectedIndex;
                    SaveSettingsFromUi();
                    _engine.TriggerCurrentFrame();
                };

                var lblWin = CreateLabel("计算窗口模式:", 15, 138);
                cboWindowMode = new ComboBox { Location = new Point(125, 135), Width = 190, DropDownStyle = ComboBoxStyle.DropDownList };
                cboWindowMode.Items.AddRange(new object[] { "滑动窗口 (左侧固定跨度)", "全量累计 (从起点到当前)" });
                cboWindowMode.SelectedIndex = 0;
                cboWindowMode.SelectedIndexChanged += (s, e) =>
                {
                    _engine.CumulativeMode = cboWindowMode.SelectedIndex == 1;
                    SaveSettingsFromUi();
                    _engine.TriggerCurrentFrame();
                };

                var lblChartType = CreateLabel("K线呈现模式:", 15, 168);
                cboChartType = new ComboBox { Location = new Point(125, 165), Width = 190, DropDownWidth = 220, DropDownStyle = ComboBoxStyle.DropDownList };
                cboChartType.Items.AddRange(new object[] { "🕯️ 蜡烛图 (Candlestick)", "📈 折线图 (Line Chart)" });
                cboChartType.SelectedIndex = 0;
                cboChartType.SelectedIndexChanged += (s, e) =>
                {
                    if (_isInitializing) return;
                    if (cboHeaderChartType != null && cboHeaderChartType.SelectedIndex != cboChartType.SelectedIndex)
                    {
                        cboHeaderChartType.SelectedIndex = cboChartType.SelectedIndex;
                    }
                    SaveSettingsFromUi();
                    RenderCurrentState();
                };

                chkAutoScale = new CheckBox
                {
                    Text = "自动适配合理坐标轴 (Auto-Scale)",
                    Location = new Point(15, 200),
                    AutoSize = true,
                    Checked = true,
                    ForeColor = Color.FromArgb(226, 232, 240)
                };
                chkAutoScale.CheckedChanged += (s, e) => SaveSettingsFromUi();

                chkFollowLatest = new CheckBox
                {
                    Text = "视图自动跟随最新推进的 K 线",
                    Location = new Point(15, 228),
                    AutoSize = true,
                    Checked = true,
                    ForeColor = Color.FromArgb(226, 232, 240)
                };
                chkFollowLatest.CheckedChanged += (s, e) => SaveSettingsFromUi();

                chkShowTouchMarkers = new CheckBox
                {
                    Text = "三点锚定标记",
                    Location = new Point(15, 256),
                    AutoSize = true,
                    Checked = true,
                    ForeColor = Color.FromArgb(226, 232, 240)
                };
                chkShowTouchMarkers.CheckedChanged += (s, e) =>
                {
                    SaveSettingsFromUi();
                    _engine.TriggerCurrentFrame();
                };

                chkShowKlineHighLow = new CheckBox
                {
                    Text = "⚪ K线高低点",
                    Location = new Point(160, 256),
                    AutoSize = true,
                    Checked = true,
                    ForeColor = Color.FromArgb(74, 222, 128)
                };
                chkShowKlineHighLow.CheckedChanged += (s, e) =>
                {
                    if (_isInitializing) return;
                    if (chkHeaderShowKlineHighLow != null && chkHeaderShowKlineHighLow.Checked != chkShowKlineHighLow.Checked)
                    {
                        chkHeaderShowKlineHighLow.Checked = chkShowKlineHighLow.Checked;
                    }
                    SaveSettingsFromUi();
                    RenderCurrentState();
                };

                chkEnableRetainChannel = new CheckBox
                {
                    Text = "锁定保留极值通道并识别突破 (Breakout)",
                    Location = new Point(15, 284),
                    AutoSize = true,
                    Checked = true,
                    ForeColor = Color.FromArgb(250, 204, 21), // Yellow 400
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold)
                };
                chkEnableRetainChannel.CheckedChanged += (s, e) =>
                {
                    _engine.EnableRetainedChannel = chkEnableRetainChannel.Checked;
                    SaveSettingsFromUi();
                    _engine.TriggerCurrentFrame();
                };

                var lblConfirmBars = CreateLabel("极值确认无碰根数:", 15, 314);
                numConfirmBars = new NumericUpDown
                {
                    Location = new Point(135, 311),
                    Width = 180,
                    Minimum = 1,
                    Maximum = 50,
                    Value = 3
                };
                numConfirmBars.ValueChanged += (s, e) =>
                {
                    _engine.RetainedConfirmBars = (int)numConfirmBars.Value;
                    SaveSettingsFromUi();
                    _engine.TriggerCurrentFrame();
                };

                var lblBreakoutRule = CreateLabel("突破判定基准价格:", 15, 344);
                cboBreakoutRule = new ComboBox
                {
                    Location = new Point(135, 341),
                    Width = 180,
                    DropDownStyle = ComboBoxStyle.DropDownList
                };
                cboBreakoutRule.Items.AddRange(new object[] { "收盘价突破 (Close)", "极值价突破 (High/Low)" });
                cboBreakoutRule.SelectedIndex = 0;
                cboBreakoutRule.SelectedIndexChanged += (s, e) =>
                {
                    _engine.BreakoutRule = (BreakoutRule)cboBreakoutRule.SelectedIndex;
                    SaveSettingsFromUi();
                    _engine.TriggerCurrentFrame();
                };

                chkSpecialRetained = new CheckBox
                {
                    Text = "🟣 通道>100根且角度≥35°保留线宽0.8f紫色",
                    Location = new Point(15, 375),
                    AutoSize = true,
                    Checked = true,
                    ForeColor = Color.FromArgb(192, 132, 252) // Purple 400
                };
                chkSpecialRetained.CheckedChanged += (s, e) =>
                {
                    _engine.EnableSpecialRetainedStyle = chkSpecialRetained.Checked;
                    SaveSettingsFromUi();
                    _engine.TriggerCurrentFrame();
                };

                var lblSpecialBars = CreateLabel("强趋势门槛K线数:", 15, 404);
                numSpecialRetainedBars = new NumericUpDown
                {
                    Location = new Point(135, 401),
                    Width = 180,
                    Minimum = 10,
                    Maximum = 1000,
                    Increment = 10,
                    Value = 100
                };
                numSpecialRetainedBars.ValueChanged += (s, e) =>
                {
                    _engine.SpecialRetainedMinBars = (int)numSpecialRetainedBars.Value;
                    UpdateSpecialRetainedLabel();
                    SaveSettingsFromUi();
                    _engine.TriggerCurrentFrame();
                };

                var lblSpecialAngle = CreateLabel("强趋势门槛角度(°):", 15, 434);
                numSpecialRetainedAngle = new NumericUpDown
                {
                    Location = new Point(135, 431),
                    Width = 180,
                    Minimum = 1.0m,
                    Maximum = 89.0m,
                    DecimalPlaces = 1,
                    Increment = 1.0m,
                    Value = 35.0m
                };
                numSpecialRetainedAngle.ValueChanged += (s, e) =>
                {
                    _engine.SpecialRetainedMinAngle = (double)numSpecialRetainedAngle.Value;
                    UpdateSpecialRetainedLabel();
                    SaveSettingsFromUi();
                    _engine.TriggerCurrentFrame();
                };

                chkEnableVPattern = new CheckBox
                {
                    Text = "🎯 识别并标记 V / 倒V 形态 (价差≥5%)",
                    Location = new Point(15, 465),
                    AutoSize = true,
                    Checked = true,
                    ForeColor = Color.FromArgb(74, 222, 128) // Green 400
                };
                chkEnableVPattern.CheckedChanged += (s, e) =>
                {
                    _engine.EnableVPattern = chkEnableVPattern.Checked;
                    SaveSettingsFromUi();
                    _engine.TriggerCurrentFrame();
                };

                var lblVThreshold = CreateLabel("V形态价差门槛(%):", 15, 494);
                numVPatternThreshold = new NumericUpDown
                {
                    Location = new Point(135, 491),
                    Width = 180,
                    Minimum = 0.5m,
                    Maximum = 50.0m,
                    DecimalPlaces = 1,
                    Increment = 0.5m,
                    Value = 5.0m
                };
                numVPatternThreshold.ValueChanged += (s, e) =>
                {
                    _engine.VPatternMinPriceDiffPct = numVPatternThreshold.Value;
                    UpdateVPatternThresholdLabel();
                    SaveSettingsFromUi();
                    _engine.TriggerCurrentFrame();
                };

                chkShowVPatternLines = new CheckBox
                {
                    Text = "📐 绘制 V / 倒V 轮廓折线与注记",
                    Location = new Point(15, 522),
                    AutoSize = true,
                    Checked = true,
                    ForeColor = Color.FromArgb(241, 245, 249)
                };
                chkShowVPatternLines.CheckedChanged += (s, e) =>
                {
                    _engine.ShowVPatternLines = chkShowVPatternLines.Checked;
                    SaveSettingsFromUi();
                    _engine.TriggerCurrentFrame();
                };

                chkEnableConsecutiveTrend = new CheckBox
                {
                    Text = "🔥 识别连续上涨/下跌形态 (≥5根且≥2.5%)",
                    Location = new Point(15, 555),
                    AutoSize = true,
                    Checked = true,
                    ForeColor = Color.FromArgb(251, 146, 60) // Orange 400
                };
                chkEnableConsecutiveTrend.CheckedChanged += (s, e) =>
                {
                    _engine.EnableConsecutiveTrend = chkEnableConsecutiveTrend.Checked;
                    SaveSettingsFromUi();
                    _engine.TriggerCurrentFrame();
                };

                var lblConsecBars = CreateLabel("连续K线门槛(根):", 15, 584);
                numConsecutiveBars = new NumericUpDown
                {
                    Location = new Point(145, 581),
                    Width = 170,
                    Minimum = 2,
                    Maximum = 50,
                    Increment = 1,
                    Value = 5
                };
                numConsecutiveBars.ValueChanged += (s, e) =>
                {
                    _engine.ConsecutiveTrendMinBars = (int)numConsecutiveBars.Value;
                    UpdateConsecutiveTrendLabel();
                    SaveSettingsFromUi();
                    _engine.TriggerCurrentFrame();
                };

                var lblConsecPct = CreateLabel("涨跌幅门槛(%):", 15, 614);
                numConsecutivePct = new NumericUpDown
                {
                    Location = new Point(145, 611),
                    Width = 170,
                    Minimum = 0.0m,
                    Maximum = 50.0m,
                    DecimalPlaces = 1,
                    Increment = 0.5m,
                    Value = 0.0m
                };
                numConsecutivePct.ValueChanged += (s, e) =>
                {
                    _engine.ConsecutiveTrendMinPct = numConsecutivePct.Value;
                    UpdateConsecutiveTrendLabel();
                    SaveSettingsFromUi();
                    _engine.TriggerCurrentFrame();
                };

                var lblConsecPriceMode = CreateLabel("通道取值模式:", 15, 642);
                cboConsecPriceMode = new ComboBox
                {
                    Location = new Point(145, 639),
                    Width = 195,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = Color.FromArgb(30, 41, 59),
                    ForeColor = Color.FromArgb(226, 232, 240),
                    FlatStyle = FlatStyle.Flat
                };
                cboConsecPriceMode.Items.AddRange(new object[]
                {
                    "Close 收盘价 (窄通道)",
                    "High/Low 极值 (宽通道)"
                });
                cboConsecPriceMode.SelectedIndex = 0;
                cboConsecPriceMode.SelectedIndexChanged += (s, e) =>
                {
                    _engine.ConsecutiveChannelPriceMode = (ConsecutiveChannelPriceMode)cboConsecPriceMode.SelectedIndex;
                    SaveSettingsFromUi();
                    _engine.TriggerCurrentFrame();
                };

                chkShowConsecutiveChannel = new CheckBox
                {
                    Text = "🟩 绘制连续走势平行通道 (绿色 0.8f)",
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular),
                    ForeColor = Color.FromArgb(74, 222, 128),
                    Location = new Point(15, 670),
                    AutoSize = true,
                    Checked = true
                };
                chkShowConsecutiveChannel.CheckedChanged += (s, e) =>
                {
                    _engine.ShowConsecutiveChannel = chkShowConsecutiveChannel.Checked;
                    SaveSettingsFromUi();
                    RenderPlot();
                };

                chkEnableChannelAutoUpdate = new CheckBox
                {
                    Text = "🔄 超出通道且无信号自动更新绘制新通道",
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular),
                    ForeColor = Color.FromArgb(56, 189, 248), // Sky 400
                    Location = new Point(15, 696),
                    AutoSize = true,
                    Checked = true
                };
                chkEnableChannelAutoUpdate.CheckedChanged += (s, e) =>
                {
                    _engine.EnableChannelAutoUpdate = chkEnableChannelAutoUpdate.Checked;
                    SaveSettingsFromUi();
                    _engine.TriggerCurrentFrame();
                };

                var lblChannelUpdateMode = CreateLabel("新通道模式:", 15, 725);
                cboChannelUpdateMode = new ComboBox
                {
                    Location = new Point(145, 722),
                    Width = 195,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = Color.FromArgb(30, 41, 59),
                    ForeColor = Color.FromArgb(226, 232, 240),
                    FlatStyle = FlatStyle.Flat
                };
                cboChannelUpdateMode.Items.AddRange(new object[]
                {
                    "滚动最新 5 根 (Rolling)",
                    "扩展全波段 (Expanding)"
                });
                cboChannelUpdateMode.SelectedIndex = 0;
                cboChannelUpdateMode.SelectedIndexChanged += (s, e) =>
                {
                    _engine.ChannelUpdateMode = (ChannelUpdateMode)cboChannelUpdateMode.SelectedIndex;
                    SaveSettingsFromUi();
                    _engine.TriggerCurrentFrame();
                };

                chkShowHistoricalChannels = new CheckBox
                {
                    Text = "显示更新前历史旧通道 (淡灰虚线)",
                    Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Regular),
                    ForeColor = Color.FromArgb(148, 163, 184), // Slate 400
                    Location = new Point(15, 752),
                    AutoSize = true,
                    Checked = true
                };
                chkShowHistoricalChannels.CheckedChanged += (s, e) =>
                {
                    _engine.ShowHistoricalChannels = chkShowHistoricalChannels.Checked;
                    SaveSettingsFromUi();
                    RenderPlot();
                };

                chkEnableReversalOrder = new CheckBox
                {
                    Text = "⚡ 连涨/连跌第5根反转做单监控",
                    Location = new Point(15, 782),
                    AutoSize = true,
                    Checked = true,
                    ForeColor = Color.FromArgb(250, 204, 21), // Yellow 400
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold)
                };
                chkEnableReversalOrder.CheckedChanged += (s, e) =>
                {
                    _engine.EnableReversalOrder = chkEnableReversalOrder.Checked;
                    SaveSettingsFromUi();
                    _engine.TriggerCurrentFrame();
                };

                var lblRevMin = CreateLabel("观察期周期(分):", 15, 811);
                numReversalMinutes = new NumericUpDown
                {
                    Location = new Point(145, 808),
                    Width = 170,
                    Minimum = 1,
                    Maximum = 60,
                    Increment = 1,
                    Value = 3
                };
                numReversalMinutes.ValueChanged += (s, e) =>
                {
                    _engine.ReversalObservationMinutes = (int)numReversalMinutes.Value;
                    SaveSettingsFromUi();
                    _engine.TriggerCurrentFrame();
                };

                var lblRevPLong = CreateLabel("大实体阈值 P_L(%):", 15, 841);
                numReversalPLong = new NumericUpDown
                {
                    Location = new Point(145, 838),
                    Width = 170,
                    Minimum = 0.1m,
                    Maximum = 10.0m,
                    DecimalPlaces = 2,
                    Increment = 0.1m,
                    Value = 1.0m
                };
                numReversalPLong.ValueChanged += (s, e) =>
                {
                    _engine.ReversalPLong = numReversalPLong.Value;
                    SaveSettingsFromUi();
                    _engine.TriggerCurrentFrame();
                };

                var lblRevPMed = CreateLabel("中小实体阈值(%):", 15, 871);
                numReversalPMedium = new NumericUpDown
                {
                    Location = new Point(145, 868),
                    Width = 80,
                    Minimum = 0.05m,
                    Maximum = 5.0m,
                    DecimalPlaces = 2,
                    Increment = 0.05m,
                    Value = 0.35m
                };
                numReversalPMedium.ValueChanged += (s, e) =>
                {
                    _engine.ReversalPMedium = numReversalPMedium.Value;
                    _engine.ReversalPShort = numReversalPMedium.Value;
                    if (numReversalPShort != null) numReversalPShort.Value = numReversalPMedium.Value;
                    SaveSettingsFromUi();
                    _engine.TriggerCurrentFrame();
                };

                numReversalPShort = new NumericUpDown
                {
                    Location = new Point(235, 868),
                    Width = 80,
                    Minimum = 0.05m,
                    Maximum = 5.0m,
                    DecimalPlaces = 2,
                    Increment = 0.05m,
                    Value = 0.35m,
                    Visible = false
                };

                var lblRevPullback = CreateLabel("Tick回落/反弹(%):", 15, 899);
                numReversalTickPullback = new NumericUpDown
                {
                    Location = new Point(145, 896),
                    Width = 170,
                    Minimum = 0.01m,
                    Maximum = 1.0m,
                    DecimalPlaces = 3,
                    Increment = 0.01m,
                    Value = 0.06m
                };
                numReversalTickPullback.ValueChanged += (s, e) =>
                {
                    _engine.ReversalTickPullbackPct = numReversalTickPullback.Value;
                    SaveSettingsFromUi();
                    _engine.TriggerCurrentFrame();
                };

                var lblRevChannelDivision = CreateLabel("📐 四等分观察线: 50%/75%/100%(涨) | 50%/25%/0%(跌)", 15, 929);
                lblRevChannelDivision.Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Regular);
                lblRevChannelDivision.ForeColor = Color.FromArgb(52, 211, 153);
                lblRevChannelDivision.AutoSize = true;
                numReversalChannelZone = new NumericUpDown { Visible = false };

                chkShowReversalYellowLines = new CheckBox
                {
                    Text = "🟡 在K线上绘制做单短黄线标记",
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular),
                    ForeColor = Color.FromArgb(250, 204, 21),
                    Location = new Point(15, 959),
                    AutoSize = true,
                    Checked = true
                };
                chkShowReversalYellowLines.CheckedChanged += (s, e) =>
                {
                    _engine.ShowReversalYellowLines = chkShowReversalYellowLines.Checked;
                    SaveSettingsFromUi();
                    RenderPlot();
                };

                chkShowObservationCycles = new CheckBox
                {
                    Text = "⏱️ 在Tick窗口标记出观察周期",
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular),
                    ForeColor = Color.FromArgb(56, 189, 248),
                    Location = new Point(15, 985),
                    AutoSize = true,
                    Checked = true
                };
                chkShowObservationCycles.CheckedChanged += (s, e) =>
                {
                    _engine.ShowObservationCycles = chkShowObservationCycles.Checked;
                    SaveSettingsFromUi();
                    if (_cachedRawTicks != null && _cachedRawTicks.Length > 0 && _cachedTickStartIndex >= 0 && _cachedTickEndIndex >= 0)
                    {
                        DisplayTicksInternal(_cachedTickStartIndex, _cachedTickEndIndex, _cachedRawTicks);
                    }
                };

                tabChannel.Controls.AddRange(new Control[] {
                    lblL, numLeftLen, lblR, numRightLen, lblTotalLength,
                    lblMode, cboCalcMode, lblWin, cboWindowMode,
                    lblChartType, cboChartType,
                    chkAutoScale, chkFollowLatest, chkShowTouchMarkers, chkShowKlineHighLow,
                    chkEnableRetainChannel, lblConfirmBars, numConfirmBars,
                    lblBreakoutRule, cboBreakoutRule, chkSpecialRetained,
                    lblSpecialBars, numSpecialRetainedBars, lblSpecialAngle, numSpecialRetainedAngle,
                    chkEnableVPattern, lblVThreshold, numVPatternThreshold, chkShowVPatternLines,
                    chkEnableConsecutiveTrend, lblConsecBars, numConsecutiveBars, lblConsecPct, numConsecutivePct,
                    lblConsecPriceMode, cboConsecPriceMode,
                    chkShowConsecutiveChannel,
                    chkEnableChannelAutoUpdate, lblChannelUpdateMode, cboChannelUpdateMode, chkShowHistoricalChannels,
                    chkEnableReversalOrder, lblRevMin, numReversalMinutes,
                    lblRevPLong, numReversalPLong, lblRevPMed, numReversalPMedium, numReversalPShort,
                    lblRevPullback, numReversalTickPullback,
                    lblRevChannelDivision, numReversalChannelZone,
                    chkShowReversalYellowLines,
                    chkShowObservationCycles
                });
            }

            // Tab 2: 真实行情数据源设置
            {
                var lblCoin = CreateLabel("交易币种:", 15, 18);
                cboCoin = new ComboBox { Location = new Point(110, 15), Width = 205, DropDownStyle = ComboBoxStyle.DropDownList };
                cboCoin.Items.AddRange(new object[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "NEARUSDT" });
                cboCoin.SelectedIndex = 0;
                cboCoin.SelectedIndexChanged += (s, e) =>
                {
                    AutoSetDatesForCoin();
                    SaveSettingsFromUi();
                };

                var lblInt = CreateLabel("K线周期:", 15, 48);
                cboInterval = new ComboBox { Location = new Point(110, 45), Width = 205, DropDownStyle = ComboBoxStyle.DropDownList };
                cboInterval.Items.AddRange(new object[] {
                    "1m (1分钟)", "3m (3分钟)", "5m (5分钟)",
                    "15m (15分钟)", "30m (30分钟)", "1h (1小时)",
                    "2h (2小时)", "4h (4小时)", "1d (日线)"
                });
                cboInterval.SelectedIndex = 0;
                cboInterval.SelectedIndexChanged += (s, e) => SaveSettingsFromUi();

                var lblStart = CreateLabel("起始日期:", 15, 78);
                dtpStart = new DateTimePicker { Location = new Point(110, 75), Width = 205, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd" };
                dtpStart.Value = new DateTime(2024, 1, 1);
                dtpStart.ValueChanged += (s, e) => SaveSettingsFromUi();

                var lblEnd = CreateLabel("结束日期:", 15, 108);
                dtpEnd = new DateTimePicker { Location = new Point(110, 105), Width = 205, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd" };
                dtpEnd.Value = new DateTime(2024, 1, 1);
                dtpEnd.ValueChanged += (s, e) => SaveSettingsFromUi();

                btnLoadData = new Button
                {
                    Text = "📂 加载真实 K 线数据",
                    Location = new Point(15, 142),
                    Size = new Size(300, 36),
                    BackColor = Color.FromArgb(37, 99, 235), // Blue 600
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Microsoft YaHei", 9.5F, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnLoadData.FlatAppearance.BorderSize = 0;
                btnLoadData.Click += async (s, e) => await LoadSelectedDataAsync();

                lblDataHint = new Label
                {
                    Text = "💡 提示: 严格采用本地真实 Binance Parquet 数据回放，若所选周期非 1m 则自动基于 1m 真实成交规范合成，绝不伪造加工。",
                    Location = new Point(15, 188),
                    Size = new Size(300, 60),
                    ForeColor = Color.FromArgb(148, 163, 184),
                    Font = new Font("Microsoft YaHei", 8F)
                };

                tabData.Controls.AddRange(new Control[] {
                    lblCoin, cboCoin, lblInt, cboInterval, lblStart, dtpStart, lblEnd, dtpEnd,
                    btnLoadData, lblDataHint
                });
            }

            // Tab 3: 实时监控特征看板
            {
                lblMetricPrice = CreateStatLabel("当前 K 线: -", 15, 12);

                lblMetricType = CreateStatLabel("通道结构: -", 15, 36);
                lblMetricType.ForeColor = Color.FromArgb(56, 189, 248); // Sky 400
                lblMetricType.Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold);

                lblMetricP1P2 = CreateStatLabel("基准锚点 1&2: -", 15, 60);
                lblMetricP1P2.ForeColor = Color.FromArgb(74, 222, 128); // Green 400

                lblMetricP3 = CreateStatLabel("对侧锚点 3: -", 15, 84);
                lblMetricP3.ForeColor = Color.FromArgb(248, 113, 113); // Red 400

                lblMetricHeight = CreateStatLabel("通道高度 (H): -", 15, 108);
                lblMetricHeight.ForeColor = Color.FromArgb(250, 204, 21); // Yellow 400

                lblMetricAngle = CreateStatLabel("通道角度 (Angle): -", 15, 132);
                lblMetricSlope = CreateStatLabel("通道斜率 (Slope): -", 15, 156);

                lblMetricUpper = CreateStatLabel("通道上轨 (Upper): -", 15, 180);
                lblMetricLower = CreateStatLabel("通道下轨 (Lower): -", 15, 204);
                lblMetricTouch = CreateStatLabel("外包络状态: -", 15, 228);
                lblMetricTouch.ForeColor = Color.FromArgb(232, 121, 249); // Fuchsia 400

                lblMetricRetainedStatus = CreateStatLabel("保留通道: 无活跃保留通道", 15, 252);
                lblMetricRetainedStatus.ForeColor = Color.FromArgb(251, 146, 60); // Orange 400
                lblMetricRetainedStatus.Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold);

                lblMetricRetainedExtreme = CreateStatLabel("相对极值点: -", 15, 276);
                lblMetricRetainedExtreme.ForeColor = Color.FromArgb(250, 204, 21); // Yellow 400

                lblMetricRetainedBoundary = CreateStatLabel("保留通道延伸边界: -", 15, 300);
                lblMetricRetainedBoundary.ForeColor = Color.FromArgb(56, 189, 248); // Sky 400

                lblMetricRetainedBreakout = CreateStatLabel("突破监测: -", 15, 324);
                lblMetricRetainedBreakout.ForeColor = Color.FromArgb(239, 68, 68); // Red 500

                lblMetricVPatternSummary = CreateStatLabel("形态识别: 🟢V底: 0 个 | 🔴倒V顶: 0 个 (价差≥5.0%)", 15, 350);
                lblMetricVPatternSummary.ForeColor = Color.FromArgb(74, 222, 128); // Green 400
                lblMetricVPatternSummary.Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold);

                lblMetricLatestVPattern = CreateStatLabel("最新形态: 暂无识别形态", 15, 374);
                lblMetricLatestVPattern.ForeColor = Color.FromArgb(148, 163, 184); // Slate 400

                lblMetricConsecutiveTrendSummary = CreateStatLabel("连涨连跌: 🟢连涨: 0 段 | 🔴连跌: 0 段 (≥5根&≥2.5%)", 15, 400);
                lblMetricConsecutiveTrendSummary.ForeColor = Color.FromArgb(251, 146, 60); // Orange 400
                lblMetricConsecutiveTrendSummary.Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold);

                lblMetricLatestConsecutiveTrend = CreateStatLabel("最新连动: 暂无识别形态", 15, 424);
                lblMetricLatestConsecutiveTrend.ForeColor = Color.FromArgb(148, 163, 184); // Slate 400
                lblMetricLatestConsecutiveTrend.Cursor = Cursors.Hand;
                lblMetricLatestConsecutiveTrend.Click += (s, e) =>
                {
                    if (_engine.EnableConsecutiveTrend && _engine.ConsecutiveTrendDetector.DetectedTrends.Count > 0)
                    {
                        var trends = _engine.ConsecutiveTrendDetector.DetectedTrends;
                        var latest = trends.LastOrDefault(t => t.ConfirmedBarIndex <= _currentBarIndex);
                        if (latest != null)
                        {
                            SelectConsecutiveTrend(latest);
                        }
                    }
                };

                lblMetricReversalSummary = CreateStatLabel("反转做单: ⚡做空: 0 次 | ⚡做多: 0 次 (3m周期)", 15, 450);
                lblMetricReversalSummary.ForeColor = Color.FromArgb(250, 204, 21); // Yellow 400
                lblMetricReversalSummary.Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold);

                lblMetricLatestReversalSignal = CreateStatLabel("最新反转: 暂无做单信号", 15, 474);
                lblMetricLatestReversalSignal.ForeColor = Color.FromArgb(148, 163, 184); // Slate 400
                lblMetricLatestReversalSignal.Cursor = Cursors.Hand;
                lblMetricLatestReversalSignal.Click += (s, e) =>
                {
                    if (_engine.EnableReversalOrder && _engine.ReversalOrderEngine.AllSignals.Count > 0)
                    {
                        var latest = _engine.ReversalOrderEngine.AllSignals.LastOrDefault(sig => sig.BigBarIndex <= _currentBarIndex);
                        if (latest != null)
                        {
                            _selectedBarIndex = latest.BigBarIndex;
                            RenderPlot();
                            _ = LoadAndDisplayKlineTicksAsync(latest.BigBarIndex, latest.BigBarIndex);
                        }
                    }
                };

                tabMetrics.AutoScroll = true;
                tabMetrics.Controls.AddRange(new Control[] {
                    lblMetricPrice, lblMetricType, lblMetricP1P2, lblMetricP3,
                    lblMetricHeight, lblMetricAngle, lblMetricSlope,
                    lblMetricUpper, lblMetricLower, lblMetricTouch,
                    lblMetricRetainedStatus, lblMetricRetainedExtreme,
                    lblMetricRetainedBoundary, lblMetricRetainedBreakout,
                    lblMetricVPatternSummary, lblMetricLatestVPattern,
                    lblMetricConsecutiveTrendSummary, lblMetricLatestConsecutiveTrend,
                    lblMetricReversalSummary, lblMetricLatestReversalSignal
                });
            }

            tabRight.TabPages.AddRange(new TabPage[] { tabChannel, tabData, tabMetrics });

            // 将组件加入右侧面板 (先加入 TabControl，再加入常驻置顶的播放控制，DockTop 正确排布)
            panelRight.Controls.Add(tabRight);
            panelRight.Controls.Add(grpPlayback);

            this.Controls.Add(splitMain);
        }

        private GroupBox CreateGroupBox(string title, int top, int height)
        {
            return new GroupBox
            {
                Text = title,
                Location = new Point(0, top),
                Size = new Size(330, height),
                ForeColor = Color.FromArgb(241, 245, 249),
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold)
            };
        }

        private Label CreateLabel(string text, int x, int y)
        {
            return new Label
            {
                Text = text,
                Location = new Point(x, y + 3),
                AutoSize = true,
                ForeColor = Color.FromArgb(203, 213, 225),
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular)
            };
        }

        private Label CreateStatLabel(string text, int x, int y)
        {
            return new Label
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(300, 22),
                ForeColor = Color.FromArgb(226, 232, 240),
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular)
            };
        }

        private void UpdateTotalLengthLabel()
        {
            int l = (int)numLeftLen.Value;
            int r = (int)numRightLen.Value;
            lblTotalLength.Text = $"📐 通道总长度: {l + r} 根 (左 {l} + 右 {r})";
        }

        private void UpdateSpecialRetainedLabel()
        {
            if (chkSpecialRetained != null && numSpecialRetainedBars != null && numSpecialRetainedAngle != null)
            {
                chkSpecialRetained.Text = $"🟣 通道>{numSpecialRetainedBars.Value}根且角度≥{numSpecialRetainedAngle.Value:F0}°保留0.8f紫色";
            }
        }

        private void UpdateVPatternThresholdLabel()
        {
            if (chkEnableVPattern != null && numVPatternThreshold != null)
            {
                chkEnableVPattern.Text = $"🎯 识别并标记 V / 倒V 形态 (价差≥{numVPatternThreshold.Value:F1}%)";
            }
        }

        private void UpdateConsecutiveTrendLabel()
        {
            if (chkEnableConsecutiveTrend != null && numConsecutiveBars != null && numConsecutivePct != null)
            {
                chkEnableConsecutiveTrend.Text = $"🔥 连续涨跌 (≥{numConsecutiveBars.Value}根且≥{numConsecutivePct.Value:F1}%)";
            }
        }

        private void AutoSetDatesForCoin()
        {
            string coin = cboCoin.SelectedItem?.ToString() ?? "BTCUSDT";
            if (coin == "NEARUSDT")
            {
                dtpStart.Value = new DateTime(2025, 1, 1);
                dtpEnd.Value = new DateTime(2025, 1, 7);
            }
            else
            {
                dtpStart.Value = new DateTime(2024, 1, 1);
                dtpEnd.Value = new DateTime(2024, 1, 1);
            }
        }

        #endregion

        #region 参数记忆与持久化恢复

        private void LoadSettingsToUi()
        {
            try
            {
                var s = ChannelPlaybackSettingsManager.Load();

                // 恢复窗口尺寸与位置
                if (s.WindowWidth >= this.MinimumSize.Width && s.WindowHeight >= this.MinimumSize.Height)
                {
                    this.Size = new Size(s.WindowWidth, s.WindowHeight);
                }

                if (s.WindowLeft >= 0 && s.WindowTop >= 0)
                {
                    this.StartPosition = FormStartPosition.Manual;
                    this.Location = new Point(s.WindowLeft, s.WindowTop);
                }

                if (s.IsMaximized)
                {
                    this.WindowState = FormWindowState.Maximized;
                }

                // 恢复分割条位置 (安全范围校验，防止抛出异常)
                this.BeginInvoke(new Action(() =>
                {
                    int defaultMain = Math.Max(200, splitMain.Width - 380);
                    SafeSetSplitterDistance(splitMain, s.SplitterMainDistance, defaultMain);

                    int defaultLeft = Math.Max(150, (int)(splitLeft.Height * 0.75));
                    SafeSetSplitterDistance(splitLeft, s.SplitterLeftDistance, defaultLeft);

                    int defaultBottom = Math.Max(150, (int)(splitBottom.Width * 0.45));
                    SafeSetSplitterDistance(splitBottom, s.SplitterBottomDistance, defaultBottom);
                }));

                // 恢复数据源设置
                if (!string.IsNullOrEmpty(s.Coin))
                {
                    int coinIdx = cboCoin.FindStringExact(s.Coin);
                    if (coinIdx >= 0) cboCoin.SelectedIndex = coinIdx;
                }

                if (!string.IsNullOrEmpty(s.Interval))
                {
                    int intIdx = cboInterval.FindString(s.Interval);
                    if (intIdx >= 0) cboInterval.SelectedIndex = intIdx;
                }

                if (s.StartDate >= dtpStart.MinDate && s.StartDate <= dtpStart.MaxDate)
                    dtpStart.Value = s.StartDate;

                if (s.EndDate >= dtpEnd.MinDate && s.EndDate <= dtpEnd.MaxDate)
                    dtpEnd.Value = s.EndDate;

                // 恢复通道核心设置
                numLeftLen.Value = Math.Clamp(s.LeftLength, numLeftLen.Minimum, numLeftLen.Maximum);
                numRightLen.Value = Math.Clamp(s.RightExtendLength, numRightLen.Minimum, numRightLen.Maximum);
                cboCalcMode.SelectedIndex = Math.Clamp(s.CalculationMode, 0, cboCalcMode.Items.Count - 1);
                cboWindowMode.SelectedIndex = Math.Clamp(s.WindowMode, 0, cboWindowMode.Items.Count - 1);

                int cTypeIdx = Math.Clamp(s.ChartType, 0, cboChartType.Items.Count - 1);
                cboChartType.SelectedIndex = cTypeIdx;
                cboHeaderChartType.SelectedIndex = cTypeIdx;

                chkAutoScale.Checked = s.AutoScale;
                chkFollowLatest.Checked = s.FollowLatest;
                chkShowTouchMarkers.Checked = s.ShowTouchMarkers;
                chkShowKlineHighLow.Checked = s.ShowKlineHighLow;
                chkHeaderShowKlineHighLow.Checked = s.ShowKlineHighLow;
                chkShowLegend.Checked = s.ShowLegend;
                chkEnableRetainChannel.Checked = s.EnableRetainedChannel;
                numConfirmBars.Value = Math.Clamp(s.RetainedConfirmBars, numConfirmBars.Minimum, numConfirmBars.Maximum);
                cboBreakoutRule.SelectedIndex = Math.Clamp(s.BreakoutRule, 0, cboBreakoutRule.Items.Count - 1);
                chkSpecialRetained.Checked = s.EnableSpecialRetainedStyle;
                numSpecialRetainedBars.Value = Math.Clamp(s.SpecialRetainedMinBars, numSpecialRetainedBars.Minimum, numSpecialRetainedBars.Maximum);
                numSpecialRetainedAngle.Value = (decimal)Math.Clamp(s.SpecialRetainedMinAngle, (double)numSpecialRetainedAngle.Minimum, (double)numSpecialRetainedAngle.Maximum);
                UpdateSpecialRetainedLabel();

                chkEnableVPattern.Checked = s.EnableVPattern;
                numVPatternThreshold.Value = Math.Clamp(s.VPatternMinPriceDiffPct, numVPatternThreshold.Minimum, numVPatternThreshold.Maximum);
                chkShowVPatternLines.Checked = s.ShowVPatternLines;
                UpdateVPatternThresholdLabel();

                chkEnableConsecutiveTrend.Checked = s.EnableConsecutiveTrend;
                numConsecutiveBars.Value = Math.Clamp(s.ConsecutiveTrendMinBars, numConsecutiveBars.Minimum, numConsecutiveBars.Maximum);
                decimal consecPct = s.ConsecutiveTrendMinPct == 2.5m ? 0.0m : s.ConsecutiveTrendMinPct;
                numConsecutivePct.Value = Math.Clamp(consecPct, numConsecutivePct.Minimum, numConsecutivePct.Maximum);
                cboConsecPriceMode.SelectedIndex = Math.Clamp(s.ConsecutiveChannelPriceMode, 0, 1);
                chkShowConsecutiveChannel.Checked = s.ShowConsecutiveChannel;
                chkEnableChannelAutoUpdate.Checked = s.EnableChannelAutoUpdate;
                cboChannelUpdateMode.SelectedIndex = Math.Clamp(s.ChannelUpdateMode, 0, 1);
                chkShowHistoricalChannels.Checked = s.ShowHistoricalChannels;
                UpdateConsecutiveTrendLabel();

                chkEnableReversalOrder.Checked = s.EnableReversalOrder;
                numReversalMinutes.Value = Math.Clamp(s.ReversalObservationMinutes, numReversalMinutes.Minimum, numReversalMinutes.Maximum);
                numReversalPLong.Value = Math.Clamp(s.ReversalPLong, numReversalPLong.Minimum, numReversalPLong.Maximum);
                numReversalPMedium.Value = Math.Clamp(s.ReversalPMedium, numReversalPMedium.Minimum, numReversalPMedium.Maximum);
                numReversalPShort.Value = Math.Clamp(s.ReversalPShort, numReversalPShort.Minimum, numReversalPShort.Maximum);
                decimal pullbackVal = s.ReversalTickPullbackPct <= 0 ? 0.06m : s.ReversalTickPullbackPct;
                numReversalTickPullback.Value = Math.Clamp(pullbackVal, numReversalTickPullback.Minimum, numReversalTickPullback.Maximum);
                decimal channelZoneVal = s.ReversalChannelZonePct <= 0 ? 25.0m : s.ReversalChannelZonePct;
                numReversalChannelZone.Value = Math.Clamp(channelZoneVal, numReversalChannelZone.Minimum, numReversalChannelZone.Maximum);
                chkShowReversalYellowLines.Checked = s.ShowReversalYellowLines;
                chkShowObservationCycles.Checked = s.ShowObservationCycles;

                // 恢复回放速度
                tbSpeed.Value = Math.Clamp(s.SpeedIntervalMs, tbSpeed.Minimum, tbSpeed.Maximum);
                _engine.SetSpeed(tbSpeed.Value);
                double barsPerSec = 1000.0 / tbSpeed.Value;
                lblSpeedVal.Text = $"回放间隔: {tbSpeed.Value} ms ({barsPerSec:F1} bar/s)";

                // 同步至引擎
                _engine.LeftLength = (int)numLeftLen.Value;
                _engine.RightExtendLength = (int)numRightLen.Value;
                _engine.CalculationMode = (ChannelCalculationMode)cboCalcMode.SelectedIndex;
                _engine.CumulativeMode = cboWindowMode.SelectedIndex == 1;
                _engine.EnableRetainedChannel = chkEnableRetainChannel.Checked;
                _engine.RetainedConfirmBars = (int)numConfirmBars.Value;
                _engine.BreakoutRule = (BreakoutRule)cboBreakoutRule.SelectedIndex;
                _engine.EnableSpecialRetainedStyle = chkSpecialRetained.Checked;
                _engine.SpecialRetainedMinBars = (int)numSpecialRetainedBars.Value;
                _engine.SpecialRetainedMinAngle = (double)numSpecialRetainedAngle.Value;
                _engine.EnableVPattern = chkEnableVPattern.Checked;
                _engine.VPatternMinPriceDiffPct = numVPatternThreshold.Value;
                _engine.ShowVPatternLines = chkShowVPatternLines.Checked;
                _engine.EnableConsecutiveTrend = chkEnableConsecutiveTrend.Checked;
                _engine.ConsecutiveTrendMinBars = (int)numConsecutiveBars.Value;
                _engine.ConsecutiveTrendMinPct = numConsecutivePct.Value;
                _engine.ConsecutiveChannelPriceMode = (ConsecutiveChannelPriceMode)cboConsecPriceMode.SelectedIndex;
                _engine.ShowConsecutiveChannel = chkShowConsecutiveChannel.Checked;
                _engine.EnableChannelAutoUpdate = chkEnableChannelAutoUpdate.Checked;
                _engine.ChannelUpdateMode = (ChannelUpdateMode)cboChannelUpdateMode.SelectedIndex;
                _engine.ShowHistoricalChannels = chkShowHistoricalChannels.Checked;
                _engine.EnableReversalOrder = chkEnableReversalOrder.Checked;
                _engine.ReversalObservationMinutes = (int)numReversalMinutes.Value;
                _engine.ReversalPLong = numReversalPLong.Value;
                _engine.ReversalPMedium = numReversalPMedium.Value;
                _engine.ReversalPShort = numReversalPShort.Value;
                _engine.ReversalTickPullbackPct = numReversalTickPullback.Value;
                _engine.ReversalChannelZonePct = numReversalChannelZone.Value;
                _engine.ShowReversalYellowLines = chkShowReversalYellowLines.Checked;
                _engine.ShowObservationCycles = chkShowObservationCycles.Checked;

                // 恢复 Tick 查看周期设置
                _tickPeriodMode = Math.Clamp(s.TickPeriodMode, 0, 4);
                numCustomTickPeriod.Value = Math.Clamp(s.CustomTickMinutes, (int)numCustomTickPeriod.Minimum, (int)numCustomTickPeriod.Maximum);
                _customTickMinutes = (int)numCustomTickPeriod.Value;
                UpdateTickPeriodButtonsState();

                UpdateTotalLengthLabel();
                AppendLog($"[配置管理] 成功从本地恢复历史参数记忆 (币种: {s.Coin}, 周期: {s.Interval})。");
            }
            catch (Exception ex)
            {
                AppendLog($"[配置管理] 加载参数记忆异常: {ex.Message}");
            }
        }

        private void SaveSettingsFromUi()
        {
            if (_isInitializing) return;

            try
            {
                var s = new ChannelPlaybackSettings
                {
                    WindowWidth = this.WindowState == FormWindowState.Normal ? this.Width : this.RestoreBounds.Width,
                    WindowHeight = this.WindowState == FormWindowState.Normal ? this.Height : this.RestoreBounds.Height,
                    WindowLeft = this.WindowState == FormWindowState.Normal ? this.Left : this.RestoreBounds.Left,
                    WindowTop = this.WindowState == FormWindowState.Normal ? this.Top : this.RestoreBounds.Top,
                    IsMaximized = this.WindowState == FormWindowState.Maximized,
                    SplitterMainDistance = splitMain.SplitterDistance,
                    SplitterLeftDistance = splitLeft.SplitterDistance,
                    SplitterBottomDistance = splitBottom.SplitterDistance,

                    Coin = cboCoin.SelectedItem?.ToString() ?? "BTCUSDT",
                    Interval = cboInterval.SelectedItem?.ToString() ?? "1m (1分钟)",
                    StartDate = dtpStart.Value.Date,
                    EndDate = dtpEnd.Value.Date,

                    LeftLength = (int)numLeftLen.Value,
                    RightExtendLength = (int)numRightLen.Value,
                    CalculationMode = cboCalcMode.SelectedIndex,
                    WindowMode = cboWindowMode.SelectedIndex,
                    AutoScale = chkAutoScale.Checked,
                    FollowLatest = chkFollowLatest.Checked,
                    ShowTouchMarkers = chkShowTouchMarkers.Checked,
                    ShowKlineHighLow = chkShowKlineHighLow.Checked,
                    ShowLegend = chkShowLegend.Checked,
                    ChartType = cboChartType.SelectedIndex,
                    SpeedIntervalMs = tbSpeed.Value,
                    EnableRetainedChannel = chkEnableRetainChannel.Checked,
                    RetainedConfirmBars = (int)numConfirmBars.Value,
                    BreakoutRule = cboBreakoutRule.SelectedIndex,
                    EnableSpecialRetainedStyle = chkSpecialRetained.Checked,
                    SpecialRetainedMinBars = (int)numSpecialRetainedBars.Value,
                    SpecialRetainedMinAngle = (double)numSpecialRetainedAngle.Value,
                    EnableVPattern = chkEnableVPattern.Checked,
                    VPatternMinPriceDiffPct = numVPatternThreshold.Value,
                    ShowVPatternLines = chkShowVPatternLines.Checked,
                    EnableConsecutiveTrend = chkEnableConsecutiveTrend.Checked,
                    ConsecutiveTrendMinBars = (int)numConsecutiveBars.Value,
                    ConsecutiveTrendMinPct = numConsecutivePct.Value,
                    ConsecutiveChannelPriceMode = cboConsecPriceMode.SelectedIndex,
                    ShowConsecutiveChannel = chkShowConsecutiveChannel.Checked,
                    EnableChannelAutoUpdate = chkEnableChannelAutoUpdate.Checked,
                    ChannelUpdateMode = cboChannelUpdateMode.SelectedIndex,
                    ShowHistoricalChannels = chkShowHistoricalChannels.Checked,
                    EnableReversalOrder = chkEnableReversalOrder.Checked,
                    ReversalObservationMinutes = (int)numReversalMinutes.Value,
                    ReversalPLong = numReversalPLong.Value,
                    ReversalPMedium = numReversalPMedium.Value,
                    ReversalPShort = numReversalPShort.Value,
                    ReversalTickPullbackPct = numReversalTickPullback.Value,
                    ReversalChannelZonePct = numReversalChannelZone.Value,
                    ShowReversalYellowLines = chkShowReversalYellowLines.Checked,
                    ShowObservationCycles = chkShowObservationCycles.Checked,
                    TickPeriodMode = _tickPeriodMode,
                    CustomTickMinutes = (int)numCustomTickPeriod.Value
                };

                ChannelPlaybackSettingsManager.Save(s);
            }
            catch { }
        }

        private void SafeSetSplitterDistance(SplitContainer split, int desiredDistance, int fallbackDistance)
        {
            try
            {
                if (split == null || split.IsDisposed) return;

                int total = split.Orientation == Orientation.Vertical ? split.Width : split.Height;
                int minAllowed = Math.Max(25, split.Panel1MinSize);
                int maxAllowed = total - Math.Max(25, split.Panel2MinSize) - split.SplitterWidth;

                if (total > 0 && maxAllowed > minAllowed)
                {
                    int target = (desiredDistance >= minAllowed && desiredDistance <= maxAllowed)
                        ? desiredDistance
                        : fallbackDistance;

                    target = Math.Clamp(target, minAllowed, maxAllowed);
                    split.SplitterDistance = target;
                }
            }
            catch
            {
                // 忽略未完成渲染布局时的初始尺寸边界异常
            }
        }

        #endregion

        #region 数据加载与引擎事件绑定

        private async System.Threading.Tasks.Task LoadSelectedDataAsync()
        {
            btnLoadData.Enabled = false;
            btnLoadData.Text = "⏳ 正在检索真实 K 线...";

            string coin = cboCoin.SelectedItem?.ToString() ?? "BTCUSDT";
            DateTime startDate = dtpStart.Value.Date;
            DateTime endDate = dtpEnd.Value.Date;
            string intervalStr = cboInterval.SelectedItem?.ToString() ?? "1m";

            KlineInterval interval = KlineInterval.OneMinute;
            if (intervalStr.StartsWith("3m")) interval = KlineInterval.ThreeMinutes;
            else if (intervalStr.StartsWith("5m")) interval = KlineInterval.FiveMinutes;
            else if (intervalStr.StartsWith("15m")) interval = KlineInterval.FifteenMinutes;
            else if (intervalStr.StartsWith("30m")) interval = KlineInterval.ThirtyMinutes;
            else if (intervalStr.StartsWith("1h")) interval = KlineInterval.OneHour;
            else if (intervalStr.StartsWith("2h")) interval = KlineInterval.TwoHour;
            else if (intervalStr.StartsWith("4h")) interval = KlineInterval.FourHour;
            else if (intervalStr.StartsWith("1d")) interval = KlineInterval.OneDay;

            await _engine.LoadDataAsync(coin, startDate, endDate, interval);

            btnLoadData.Enabled = true;
            btnLoadData.Text = "📂 加载真实 K 线数据";
        }

        private void BindEngineEvents()
        {
            _engine.OnDataLoaded += total =>
            {
                if (this.InvokeRequired)
                {
                    this.BeginInvoke(new Action(() => UpdateOnDataLoaded(total)));
                }
                else
                {
                    UpdateOnDataLoaded(total);
                }
            };

            _engine.OnBarReplayed += (kline, index, channel) =>
            {
                _currentKline = kline;
                _currentBarIndex = index;
                _currentChannel = channel;

                if (this.InvokeRequired)
                {
                    this.BeginInvoke(new Action(RenderCurrentState));
                }
                else
                {
                    RenderCurrentState();
                }
            };

            _engine.OnStateChanged += state =>
            {
                if (this.InvokeRequired)
                {
                    this.BeginInvoke(new Action(() => UpdatePlaybackButtonStates(state)));
                }
                else
                {
                    UpdatePlaybackButtonStates(state);
                }
            };

            _engine.OnLogMessage += msg =>
            {
                if (msg.Contains("[通道动态更新]"))
                {
                    AppendLog(msg, Color.FromArgb(56, 189, 248)); // 天蓝色醒目高亮
                }
                else
                {
                    AppendLog(msg);
                }
            };

            _engine.OnExtremeConfirmed += (channel, idx, price, isHigh) =>
            {
                LogExtremeConfirmed(channel);
            };

            _engine.OnBreakoutDetected += (channel, breakoutBarIndex, breakoutPrice, isUpward) =>
            {
                LogBreakoutDetected(channel, breakoutBarIndex, breakoutPrice, isUpward);
            };

            _engine.OnConsecutiveTrendDetected += async (t, idx) =>
            {
                if (_engine.EnableReversalOrder)
                {
                    try
                    {
                        string coin = cboCoin.SelectedItem?.ToString() ?? "BTCUSDT";
                        await _engine.ReversalOrderEngine.ProcessTrendTicksAsync(coin, t, _engine.AllKlines, _currentBarIndex, _engine.Raw1mKlines).ConfigureAwait(true);
                        RenderPlot();
                    }
                    catch { }
                }
            };

            _engine.OnReversalStrategyActivated += (trend, desc) =>
            {
                if (this.InvokeRequired)
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        AppendLog(desc, Color.FromArgb(250, 204, 21)); // 金黄色醒目日志
                        RenderPlot();
                    }));
                }
                else
                {
                    AppendLog(desc, Color.FromArgb(250, 204, 21));
                    RenderPlot();
                }
            };

            _engine.OnReversalOrderSignal += (signal, desc) =>
            {
                if (this.InvokeRequired)
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        AppendLog(desc, Color.FromArgb(250, 204, 21)); // 金黄色反转做单信号
                        RenderPlot();
                        if (!_isEvaluatingReversalFromTicks && _cachedRawTicks != null && _cachedRawTicks.Length > 0 && _cachedTickStartIndex >= 0)
                        {
                            DisplayTicksInternal(_cachedTickStartIndex, _cachedTickEndIndex, _cachedRawTicks);
                        }
                    }));
                }
                else
                {
                    AppendLog(desc, Color.FromArgb(250, 204, 21));
                    RenderPlot();
                    if (!_isEvaluatingReversalFromTicks && _cachedRawTicks != null && _cachedRawTicks.Length > 0 && _cachedTickStartIndex >= 0)
                    {
                        DisplayTicksInternal(_cachedTickStartIndex, _cachedTickEndIndex, _cachedRawTicks);
                    }
                }
            };

            _engine.OnObservationCycleUpdated += (cycle, desc) =>
            {
                Color c;
                if (desc.Contains("已满足反转做单条件"))
                {
                    c = Color.FromArgb(74, 222, 128); // 翠绿色 (触发成功)
                }
                else if (desc.Contains("波段失效"))
                {
                    c = Color.FromArgb(248, 113, 113); // 珊瑚红 / 警示红 (波段终结失效)
                }
                else if (desc.Contains("通道失效") || desc.Contains("未激活"))
                {
                    c = Color.FromArgb(148, 163, 184); // 板岩灰 (未满足极值区未激活)
                }
                else
                {
                    c = Color.FromArgb(56, 189, 248); // 天蓝色 (常规周期推演与未触发理由)
                }

                if (this.InvokeRequired)
                {
                    this.BeginInvoke(new Action(() => AppendLog(desc, c)));
                }
                else
                {
                    AppendLog(desc, c);
                }
            };
        }

        private void LogExtremeConfirmed(RetainedChannel channel)
        {
            string dir = channel.IsDownward ? "下降通道 (确认相对高点)" : "上升通道 (确认相对低点)";
            AppendLog($"[极值确认] 锁定保留{dir} 锚点#{channel.AnchorExtremeIndex} (价: {channel.AnchorExtremePrice:F2})，后续延伸 {channel.RequiredNoCollisionBars} 根无碰撞！");
        }

        private void LogBreakoutDetected(RetainedChannel channel, int breakoutBarIndex, decimal breakoutPrice, bool isUpward)
        {
            string dir = isUpward ? "下降通道 向上突破 🚀" : "上升通道 向下跌破 🔻";
            decimal boundary = channel.BoundaryPriceAtBreakout;
            AppendLog($"[通道突破] ⚡ #{breakoutBarIndex} {dir}！突破价: {breakoutPrice:F2} (关键线位: {boundary:F2}, 幅度: {channel.BreakoutPct:+0.00;-0.00}%)！");
        }

        private void UpdateOnDataLoaded(int total)
        {
            tbProgress.Maximum = Math.Max(1, total - 1);
            tbProgress.Value = Math.Clamp(_engine.CurrentIndex, 0, tbProgress.Maximum);
            lblProgressVal.Text = $"回放进度: {_engine.CurrentIndex + 1} / {total} 根";

            _selectedBarIndex = null;
            _selectedBarStartIndex = null;
            _selectedBarEndIndex = null;
            _selectedConsecutiveTrend = null;
            _displayedTicks.Clear();
            if (dgvTicks != null && !dgvTicks.IsDisposed) dgvTicks.RowCount = 0;
            if (formsPlotTick != null && !formsPlotTick.IsDisposed)
            {
                formsPlotTick.Plot.Clear();
                formsPlotTick.Refresh();
            }
            if (lblTickInfo != null && !lblTickInfo.IsDisposed)
            {
                lblTickInfo.Text = "未选中 K 线 (在上方图表中点击任意 K 线查看对应周期的逐笔成交)";
            }
        }

        private void UpdatePlaybackButtonStates(PlaybackState state)
        {
            btnPlay.Enabled = state != PlaybackState.Playing;
            btnPause.Enabled = state == PlaybackState.Playing;
        }

        private void AppendLog(string message)
        {
            if (txtLog.IsDisposed) return;
            if (txtLog.InvokeRequired)
            {
                txtLog.BeginInvoke(new Action(() => AppendLog(message)));
                return;
            }
            string time = DateTime.Now.ToString("HH:mm:ss.fff");
            txtLog.AppendText($"[{time}] {message}\n");
            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.ScrollToCaret();
        }

        private void AppendLog(string message, Color color)
        {
            if (txtLog.IsDisposed) return;
            if (txtLog.InvokeRequired)
            {
                txtLog.BeginInvoke(new Action(() => AppendLog(message, color)));
                return;
            }
            string time = DateTime.Now.ToString("HH:mm:ss.fff");
            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.SelectionLength = 0;
            txtLog.SelectionColor = color;
            txtLog.AppendText($"[{time}] {message}\n");
            txtLog.SelectionColor = txtLog.ForeColor;
            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.ScrollToCaret();
        }

        #endregion

        #region 核心图表与指标实时渲染

        private void RenderCurrentState()
        {
            if (_isRendering) return;
            _isRendering = true;

            try
            {
                if (_engine.AllKlines.Count == 0 || _currentBarIndex < 0) return;

                // 1. 更新进度控件
                if (tbProgress.Maximum >= _currentBarIndex)
                {
                    tbProgress.Value = _currentBarIndex;
                }
                lblProgressVal.Text = $"回放进度: {_currentBarIndex + 1} / {_engine.TotalKlines} 根";

                // 2. 更新右侧看板指标
                UpdateMetricsBoard();

                // 3. 渲染 ScottPlot 画布
                RenderPlot();
            }
            finally
            {
                _isRendering = false;
            }
        }

        private void UpdateMetricsBoard()
        {
            var k = _currentKline;
            DateTime dt = TimeHelper.FromUnixTimeMilliseconds(k.OpenTime);
            string coin = cboCoin.SelectedItem?.ToString() ?? "BTCUSDT";
            string intervalStr = cboInterval.SelectedItem?.ToString() ?? "1m";

            lblChartHeaderTitle.Text = $"⚡ {coin} · {intervalStr}";

            lblMetricPrice.Text = $"当前 K 线: #{_currentBarIndex} [{dt:HH:mm}] 收: {k.Close:F2} (高: {k.High:F2}, 低: {k.Low:F2})";

            if (_currentChannel.IsValid)
            {
                var ch = _currentChannel;
                string sign = ch.SlopeK >= 0 ? "+" : "";

                string anchorSummary = ch.IsThreePointChannel
                    ? (ch.DirectionType == ChannelDirectionType.TwoLowsOneHigh
                        ? $"2低[#{ch.BasePoint1Index},#{ch.BasePoint2Index}] vs 1高[#{ch.OppositePointIndex}]"
                        : $"2高[#{ch.BasePoint1Index},#{ch.BasePoint2Index}] vs 1低[#{ch.OppositePointIndex}]")
                    : $"高#{ch.TouchHighIndex} vs 低#{ch.TouchLowIndex}";

                lblChartHeaderStats.Text = $"{ch.DirectionDescription}  |  收: {k.Close:F2}  |  高: {ch.ChannelHeight:F2} ({ch.ChannelHeightPct:F2}%)  |  角: {ch.AngleDeg:+0.0;-0.0}°  |  斜率: {sign}{ch.SlopeK:F2}  |  三点: {anchorSummary}";

                lblMetricType.Text = $"通道结构: {ch.DirectionDescription}";
                if (ch.IsThreePointChannel)
                {
                    bool isLows = ch.DirectionType == ChannelDirectionType.TwoLowsOneHigh;
                    string bType = isLows ? "低点" : "高点";
                    string oType = isLows ? "高点" : "低点";
                    lblMetricP1P2.Text = $"基准锚点 (两{bType}): #{ch.BasePoint1Index} ({ch.BasePoint1Price:F2}) & #{ch.BasePoint2Index} ({ch.BasePoint2Price:F2})";
                    lblMetricP3.Text = $"对侧锚点 (单{oType}): #{ch.OppositePointIndex} ({ch.OppositePointPrice:F2}) [确定高度]";
                }
                else
                {
                    lblMetricP1P2.Text = "基准锚点: 线性回归拟合中心趋势线";
                    lblMetricP3.Text = "对侧锚点: 双侧极值截距外包络";
                }

                lblMetricHeight.Text = $"通道高度 (H): {ch.ChannelHeight:F2} USDT ({ch.ChannelHeightPct:F2}%)";
                lblMetricAngle.Text = $"通道角度 (Angle): {ch.AngleDeg:+0.0;-0.0}°";
                lblMetricSlope.Text = $"通道斜率 (Slope): {sign}{ch.SlopeK:F3} USDT/bar ({sign}{ch.SlopePct:F3}%/bar)";

                decimal upCurr = ch.GetUpperPrice(ch.CurrentX);
                decimal upEnd = ch.GetUpperPrice(ch.EndX);
                lblMetricUpper.Text = $"通道上轨: 现值 {upCurr:F2} -> 远端 {upEnd:F2}";

                decimal lowCurr = ch.GetLowerPrice(ch.CurrentX);
                decimal lowEnd = ch.GetLowerPrice(ch.EndX);
                lblMetricLower.Text = $"通道下轨: 现值 {lowCurr:F2} -> 远端 {lowEnd:F2}";

                lblMetricTouch.Text = $"外包络检验: 严格包络 [#{ch.StartX}..#{ch.CurrentX}] 共 {ch.LeftLength} 根全部高低点";
            }
            else
            {
                lblChartHeaderStats.Text = $"收: {k.Close:F2}  |  #{_currentBarIndex} (计算中...)";
                lblMetricType.Text = "通道结构: 计算中 (K线不足)...";
                lblMetricP1P2.Text = "基准锚点 1&2: -";
                lblMetricP3.Text = "对侧锚点 3: -";
                lblMetricHeight.Text = "通道高度 (H): -";
                lblMetricAngle.Text = "通道角度 (Angle): -";
                lblMetricSlope.Text = "通道斜率 (Slope): -";
                lblMetricUpper.Text = "通道上轨: -";
                lblMetricLower.Text = "通道下轨: -";
                lblMetricTouch.Text = "外包络状态: -";
            }

            // 4. 更新保留通道与突破监测看板
            int specialCount = _engine.RetentionTracker.SpecialRetainedChannels.Count;
            if (_engine.EnableRetainedChannel && _engine.RetentionTracker.ActiveChannel != null)
            {
                var retCh = _engine.RetentionTracker.ActiveChannel;
                int totalSpan = Math.Max(retCh.BaseChannel.LeftLength, _currentBarIndex - retCh.BaseChannel.StartX + 1);
                bool isSpecialRetained = (chkSpecialRetained?.Checked ?? true) &&
                    (retCh.IsSpecialStrongTrend || (totalSpan >= _engine.SpecialRetainedMinBars && Math.Abs(retCh.BaseChannel.AngleDeg) >= _engine.SpecialRetainedMinAngle));

                string retDir = retCh.IsDownward ? "下降通道 (关注上轨向上突破)" : "上升通道 (关注下轨向下跌破)";
                string specialBadge = isSpecialRetained ? $" 🟣[强趋势 0.8f紫色永久保留]" : "";
                string totalPurpleStr = specialCount > 0 ? $" (已永久保留 {specialCount} 条紫色强趋势通道 🟣)" : "";
                lblMetricRetainedStatus.Text = $"保留通道: #{retCh.BaseChannel.StartX}..#{retCh.BaseChannel.EndX} {retDir}{specialBadge}{totalPurpleStr}";
                lblMetricRetainedStatus.ForeColor = isSpecialRetained ? Color.FromArgb(192, 132, 252) : Color.FromArgb(241, 245, 249);

                string extremeType = retCh.IsDownward ? "相对高点" : "相对低点";
                lblMetricRetainedExtreme.Text = $"相对极值点: {extremeType} #{retCh.AnchorExtremeIndex} (价: {retCh.AnchorExtremePrice:F2}), 确认于 #{retCh.ConfirmedBarIndex} (连续 {retCh.RequiredNoCollisionBars} 根无碰)";

                decimal currBound = retCh.GetBreakoutBoundaryPrice(_currentBarIndex);
                lblMetricRetainedBoundary.Text = $"关键边界位: 当前 #{_currentBarIndex} 关键界线: {currBound:F2} (原通道高: {retCh.BaseChannel.ChannelHeight:F2})";

                if (retCh.IsBrokenOut)
                {
                    string arrow = retCh.IsDownward ? "🚀 向上突破" : "🔻 向下跌破";
                    lblMetricRetainedBreakout.Text = $"突破状态: {arrow}！发生于 #{retCh.BreakoutBarIndex}，价: {retCh.BreakoutPrice:F2} ({retCh.BreakoutPct:+0.00;-0.00}%)";
                    lblMetricRetainedBreakout.ForeColor = Color.FromArgb(74, 222, 128); // Green 400
                }
                else
                {
                    decimal diff = _currentKline.Close - currBound;
                    string diffStr = retCh.IsDownward ? $"距突破差 {Math.Abs(diff):F2} USDT" : $"距跌破差 {Math.Abs(diff):F2} USDT";
                    lblMetricRetainedBreakout.Text = $"突破状态: ⏳ 监测中... ({diffStr})";
                    lblMetricRetainedBreakout.ForeColor = Color.FromArgb(248, 113, 113); // Red 400
                }

                string purpleIcon = isSpecialRetained ? "🟣" : "🔒";
                string extraCountBadge = specialCount > 0 ? $" [🟣永久保留:{specialCount}条]" : "";
                string badge = $"  |  {purpleIcon}保留: {(retCh.IsDownward ? "相对高" : "相对低")}#{retCh.AnchorExtremeIndex} ({(retCh.IsBrokenOut ? "已突破🚀" : "监测中⏳")}){extraCountBadge}";
                lblChartHeaderStats.Text += badge;
            }
            else
            {
                if (specialCount > 0)
                {
                    lblMetricRetainedStatus.Text = $"保留通道: 暂无活跃监控通道 (已永久保留 {specialCount} 条强趋势紫色通道 🟣)";
                    lblMetricRetainedStatus.ForeColor = Color.FromArgb(192, 132, 252);
                    lblChartHeaderStats.Text += $"  |  🟣已永久保留 {specialCount} 条强趋势通道";
                }
                else
                {
                    lblMetricRetainedStatus.Text = _engine.EnableRetainedChannel
                        ? "保留通道: 暂无锁定通道 (等待极值确认)"
                        : "保留通道: 功能未开启";
                    lblMetricRetainedStatus.ForeColor = Color.FromArgb(241, 245, 249);
                }
                lblMetricRetainedExtreme.Text = "相对极值点: -";
                lblMetricRetainedBoundary.Text = "保留通道延伸边界: -";
                lblMetricRetainedBreakout.Text = "突破监测: -";
                lblMetricRetainedBreakout.ForeColor = Color.FromArgb(148, 163, 184);
            }

            // 5. 更新 V 形态与倒 V 形态看板
            if (_engine.EnableVPattern)
            {
                var vList = _engine.VPatternDetector.DetectedPatterns;
                int vBottomCount = 0;
                int invTopCount = 0;
                VPatternItem? latestPattern = null;

                for (int i = 0; i < vList.Count; i++)
                {
                    var p = vList[i];
                    if (p.ConfirmedBarIndex <= _currentBarIndex)
                    {
                        if (p.Type == VPatternType.VBottom) vBottomCount++;
                        else invTopCount++;

                        if (latestPattern == null || p.ConfirmedBarIndex > latestPattern.ConfirmedBarIndex)
                        {
                            latestPattern = p;
                        }
                    }
                }

                lblMetricVPatternSummary.Text = $"形态识别: 🟢V底: {vBottomCount} 个 | 🔴倒V顶: {invTopCount} 个 (门槛≥{_engine.VPatternMinPriceDiffPct:F1}%)";
                if (latestPattern != null)
                {
                    string pName = latestPattern.Type == VPatternType.VBottom ? "🟢V底" : "🔴倒V顶";
                    lblMetricLatestVPattern.Text = $"最新形态: {pName} #{latestPattern.VertexIndex} (价:{latestPattern.VertexPrice:F2}), 价差:{latestPattern.PriceDiffPct:F1}% (确认于#{latestPattern.ConfirmedBarIndex})";
                    lblMetricLatestVPattern.ForeColor = latestPattern.Type == VPatternType.VBottom ? Color.FromArgb(74, 222, 128) : Color.FromArgb(248, 113, 113);
                }
                else
                {
                    lblMetricLatestVPattern.Text = "最新形态: 暂无确认形态 (等待价差≥门槛)";
                    lblMetricLatestVPattern.ForeColor = Color.FromArgb(148, 163, 184);
                }

                if (vBottomCount > 0 || invTopCount > 0)
                {
                    lblChartHeaderStats.Text += $"  |  🎯形态: 🟢V底:{vBottomCount} 🔴倒V:{invTopCount}";
                }
            }
            else
            {
                lblMetricVPatternSummary.Text = "形态识别: 功能未开启";
                lblMetricLatestVPattern.Text = "最新形态: -";
                lblMetricLatestVPattern.ForeColor = Color.FromArgb(148, 163, 184);
            }

            // 6. 更新连续涨跌形态看板
            if (_engine.EnableConsecutiveTrend)
            {
                var tList = _engine.ConsecutiveTrendDetector.DetectedTrends;
                int upCount = 0;
                int downCount = 0;
                ConsecutiveTrendItem? latestTrend = null;

                for (int i = 0; i < tList.Count; i++)
                {
                    var t = tList[i];
                    if (t.ConfirmedBarIndex <= _currentBarIndex)
                    {
                        if (t.Type == ConsecutiveTrendType.Bullish) upCount++;
                        else downCount++;

                        if (latestTrend == null || t.ConfirmedBarIndex > latestTrend.ConfirmedBarIndex)
                        {
                            latestTrend = t;
                        }
                    }
                }

                lblMetricConsecutiveTrendSummary.Text = $"连涨连跌: 🔥连涨: {upCount} 段 | ❄️连跌: {downCount} 段 (≥{_engine.ConsecutiveTrendMinBars}根&≥{_engine.ConsecutiveTrendMinPct:F1}%)";
                if (latestTrend != null)
                {
                    string tName = latestTrend.Type == ConsecutiveTrendType.Bullish ? "🔥连涨" : "❄️连跌";
                    int curEnd = Math.Min(latestTrend.EndIndex, _currentBarIndex);
                    int curBars = Math.Max(1, curEnd - latestTrend.StartIndex + 1);
                    decimal curPct = latestTrend.PriceChangePct;
                    if (curEnd < latestTrend.EndIndex && curEnd >= 0 && curEnd < _engine.AllKlines.Count)
                    {
                        var curBar = _engine.AllKlines[curEnd];
                        curPct = latestTrend.StartPrice > 0 ? (curBar.Close - latestTrend.StartPrice) / latestTrend.StartPrice * 100m : 0m;
                    }

                    lblMetricLatestConsecutiveTrend.Text = $"最新连动: {tName} #{latestTrend.StartIndex}~#{curEnd} ({curBars}根, {curPct:+0.00;-0.00;0.00}%, 确认于#{latestTrend.ConfirmedBarIndex})";
                    lblMetricLatestConsecutiveTrend.ForeColor = latestTrend.Type == ConsecutiveTrendType.Bullish ? Color.FromArgb(74, 222, 128) : Color.FromArgb(248, 113, 113);
                }
                else
                {
                    lblMetricLatestConsecutiveTrend.Text = "最新连动: 暂无确认形态 (等待达到要求)";
                    lblMetricLatestConsecutiveTrend.ForeColor = Color.FromArgb(148, 163, 184);
                }

                if (upCount > 0 || downCount > 0)
                {
                    lblChartHeaderStats.Text += $"  |  📈连动: 🔥连涨:{upCount} ❄️连跌:{downCount}";
                }
            }
            else
            {
                lblMetricConsecutiveTrendSummary.Text = "连涨连跌: 功能未开启";
                lblMetricLatestConsecutiveTrend.Text = "最新连动: -";
                lblMetricLatestConsecutiveTrend.ForeColor = Color.FromArgb(148, 163, 184);
            }

            // 7. 更新反转做单信号看板
            if (_engine.EnableReversalOrder)
            {
                var sigList = _engine.ReversalOrderEngine.AllSignals;
                int sellCount = 0;
                int buyCount = 0;
                ReversalOrderSignal? latestSig = null;

                for (int i = 0; i < sigList.Count; i++)
                {
                    var s = sigList[i];
                    if (s.BigBarIndex <= _currentBarIndex)
                    {
                        if (s.Direction == OrderSignalDirection.Sell) sellCount++;
                        else if (s.Direction == OrderSignalDirection.Buy) buyCount++;

                        if (latestSig == null || s.TriggerTime > latestSig.TriggerTime)
                        {
                            latestSig = s;
                        }
                    }
                }

                lblMetricReversalSummary.Text = $"反转做单: 🔴做空: {sellCount} 次 | 🟢做多: {buyCount} 次 ({_engine.ReversalObservationMinutes}m周期, 四等分观察线)";
                if (latestSig != null)
                {
                    string modeStr = latestSig.IsTickStreamTriggered ? "Tick实时" : "1m回退";
                    string dirStr = latestSig.IsBreakoutTrendFollowing
                        ? (latestSig.Direction == OrderSignalDirection.Sell ? "🔴顺势高空" : "🟢顺势低多")
                        : (latestSig.Direction == OrderSignalDirection.Sell ? "🔴高点做空" : "🟢低点做多");
                    string peakInfo = latestSig.IsTickStreamTriggered ? $" 极值:{latestSig.PeakTroughPrice:F2} 回撤:{latestSig.PullbackPct:F2}% |" : "";
                    string lineReactionInfo = !string.IsNullOrEmpty(latestSig.ChannelLineReaction) ? $"【{latestSig.ChannelLineReaction}】" : "";
                    string tickExtra = latestSig.IsTickStreamTriggered && latestSig.TriggerTick.HasValue
                        ? $" [Tick #{latestSig.TriggerTickIndex + 1}/{latestSig.CycleTotalTicks} {(latestSig.TriggerTick.Value.IsBuyerMaker ? "卖" : "买")} 量:{latestSig.TriggerTick.Value.Qty:F2}]"
                        : "";
                    lblMetricLatestReversalSignal.Text = $"最新反转: [{modeStr}] {lineReactionInfo}{dirStr} @ {latestSig.Price:F2}{tickExtra} ({peakInfo} 止损:{latestSig.StopLossPrice:F2}, 止盈:{latestSig.TakeProfitPrice:F2}) [#{latestSig.Pattern.PatternId} C{latestSig.ObservationCycleIndex}] (Bar #{latestSig.BigBarIndex})";
                    lblMetricLatestReversalSignal.ForeColor = Color.FromArgb(250, 204, 21);
                }
                else
                {
                    lblMetricLatestReversalSignal.Text = "最新反转: 暂无做单信号";
                    lblMetricLatestReversalSignal.ForeColor = Color.FromArgb(148, 163, 184);
                }

                if (sellCount > 0 || buyCount > 0)
                {
                    lblChartHeaderStats.Text += $"  |  ⚡反转: 🔴空:{sellCount} 🟢多:{buyCount}";
                }
            }
            else
            {
                lblMetricReversalSummary.Text = "反转做单: 功能未开启";
                lblMetricLatestReversalSignal.Text = "最新反转: -";
                lblMetricLatestReversalSignal.ForeColor = Color.FromArgb(148, 163, 184);
            }
        }

        private void RenderPlot()
        {
            var plot = formsPlot.Plot;
            plot.Clear();

            // 主题配色
            plot.FigureBackground.Color = ScottPlot.Color.FromHex("#0f172a"); // Slate 900
            plot.DataBackground.Color = ScottPlot.Color.FromHex("#1e293b");   // Slate 800
            plot.Axes.Color(ScottPlot.Color.FromHex("#94a3b8"));              // Slate 400
            plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#334155");    // Slate 700

            string chineseFont = PlotHelper.GetInstalledChineseFont();
            Fonts.Default = chineseFont;

            var allKlines = _engine.AllKlines;
            int total = allKlines.Count;
            if (total == 0 || _currentBarIndex < 0) return;

            int displayStart = 0;
            int displayEnd = _currentBarIndex;

            int barCount = displayEnd - displayStart + 1;
            bool isLineChart = (cboChartType != null && cboChartType.SelectedIndex == 1) ||
                               (cboHeaderChartType != null && cboHeaderChartType.SelectedIndex == 1);

            if (!isLineChart)
            {
                // 1. 绘制红绿蜡烛图 (Candlestick)
                var ohlcList = new List<OHLC>(barCount);
                for (int i = displayStart; i <= displayEnd; i++)
                {
                    var bar = allKlines[i];
                    var ohlc = new OHLC(
                        (double)bar.Open,
                        (double)bar.High,
                        (double)bar.Low,
                        (double)bar.Close,
                        DateTime.FromOADate(i),
                        TimeSpan.FromDays(0.8));
                    ohlcList.Add(ohlc);
                }

                var candlePlot = plot.Add.Candlestick(ohlcList);
                candlePlot.RisingColor = ScottPlot.Color.FromHex("#22c55e"); // 绿色阳线
                candlePlot.FallingColor = ScottPlot.Color.FromHex("#ef4444"); // 红色阴线
            }
            else
            {
                // 1. 绘制收盘价折线图 (Line Chart)
                double[] xs = new double[barCount];
                double[] ys = new double[barCount];
                for (int i = 0; i < barCount; i++)
                {
                    int kIdx = displayStart + i;
                    xs[i] = kIdx;
                    ys[i] = (double)allKlines[kIdx].Close;
                }

                var linePlot = plot.Add.ScatterLine(xs, ys);
                linePlot.Color = ScottPlot.Color.FromHex("#38bdf8"); // 亮天蓝 (Sky 400)
                linePlot.LineWidth = 1.8f;
                linePlot.MarkerSize = 0;
                linePlot.LegendText = "收盘价折线";

                // 在最新推进处绘制当前价格高亮圆点
                if (barCount > 0)
                {
                    var tipMarker = plot.Add.Marker(displayEnd, (double)allKlines[displayEnd].Close);
                    tipMarker.Shape = MarkerShape.FilledCircle;
                    tipMarker.Size = 7;
                    tipMarker.Color = ScottPlot.Color.FromHex("#38bdf8");
                }
            }

            // 1.5 绘制 K 线高点与低点小圆形标记 (如果勾选)
            if (chkShowKlineHighLow.Checked && barCount > 0)
            {
                double[] xsHigh = new double[barCount];
                double[] ysHigh = new double[barCount];
                double[] xsLow = new double[barCount];
                double[] ysLow = new double[barCount];

                for (int i = 0; i < barCount; i++)
                {
                    int kIdx = displayStart + i;
                    var bar = allKlines[kIdx];
                    xsHigh[i] = kIdx;
                    ysHigh[i] = (double)bar.High;
                    xsLow[i] = kIdx;
                    ysLow[i] = (double)bar.Low;
                }

                // 高点小圆点标记 (翡翠绿实心小圆)
                var highScatter = plot.Add.Scatter(xsHigh, ysHigh);
                highScatter.LineWidth = 0;
                highScatter.MarkerShape = MarkerShape.FilledCircle;
                highScatter.MarkerSize = 4.5f;
                highScatter.Color = ScottPlot.Color.FromHex("#22c55e");
                highScatter.LegendText = "K线高点";

                // 低点小圆点标记 (珊瑚红实心小圆)
                var lowScatter = plot.Add.Scatter(xsLow, ysLow);
                lowScatter.LineWidth = 0;
                lowScatter.MarkerShape = MarkerShape.FilledCircle;
                lowScatter.MarkerSize = 4.5f;
                lowScatter.Color = ScottPlot.Color.FromHex("#ef4444");
                lowScatter.LegendText = "K线低点";
            }

            // 2. 绘制动态通道
            if (_currentChannel.IsValid)
            {
                var ch = _currentChannel;
                double xStart = ch.StartX;
                double xCurr = ch.CurrentX;
                double xEnd = ch.EndX;

                double yUpStart = (double)ch.GetUpperPrice(xStart);
                double yUpCurr = (double)ch.GetUpperPrice(xCurr);
                double yUpEnd = (double)ch.GetUpperPrice(xEnd);

                double yLowStart = (double)ch.GetLowerPrice(xStart);
                double yLowCurr = (double)ch.GetLowerPrice(xCurr);
                double yLowEnd = (double)ch.GetLowerPrice(xEnd);

                double yMidStart = (double)ch.GetCenterPrice(xStart);
                double yMidEnd = (double)ch.GetCenterPrice(xEnd);

                // ① 通道半透明区域多边形填充 (TradingView 风格)
                var polyCoords = new Coordinates[]
                {
                    new Coordinates(xStart, yUpStart),
                    new Coordinates(xEnd, yUpEnd),
                    new Coordinates(xEnd, yLowEnd),
                    new Coordinates(xStart, yLowStart)
                };
                var poly = plot.Add.Polygon(polyCoords);
                poly.FillColor = ScottPlot.Color.FromHex("#38bdf8").WithAlpha(18); // Sky Blue 半透明
                poly.LineWidth = 0;

                // ② 上轨 (左侧分析实线，右侧延伸虚线)
                var lineUpLeft = plot.Add.Line(xStart, yUpStart, xCurr, yUpCurr);
                lineUpLeft.Color = ScottPlot.Color.FromHex("#f59e0b"); // 琥珀金 Amber 500
                lineUpLeft.LineWidth = 2.0f;
                lineUpLeft.LegendText = ch.DirectionType == ChannelDirectionType.TwoHighsOneLow
                    ? $"通道上轨 (2高点阻力基准线)"
                    : $"通道上轨 (阻力/高度 {ch.ChannelHeight:F1})";

                var lineUpRight = plot.Add.Line(xCurr, yUpCurr, xEnd, yUpEnd);
                lineUpRight.Color = ScottPlot.Color.FromHex("#fbbf24"); // Amber 400
                lineUpRight.LineWidth = 1.6f;
                lineUpRight.LinePattern = LinePattern.Dashed;

                // ③ 下轨 (左侧分析实线，右侧延伸虚线)
                var lineLowLeft = plot.Add.Line(xStart, yLowStart, xCurr, yLowCurr);
                lineLowLeft.Color = ScottPlot.Color.FromHex("#38bdf8"); // 天空蓝 Sky 400
                lineLowLeft.LineWidth = 2.0f;
                lineLowLeft.LegendText = ch.DirectionType == ChannelDirectionType.TwoLowsOneHigh
                    ? $"通道下轨 (2低点支撑基准线)"
                    : $"通道下轨 (支撑/高度 {ch.ChannelHeight:F1})";

                var lineLowRight = plot.Add.Line(xCurr, yLowCurr, xEnd, yLowEnd);
                lineLowRight.Color = ScottPlot.Color.FromHex("#7dd3fc"); // Sky 300
                lineLowRight.LineWidth = 1.6f;
                lineLowRight.LinePattern = LinePattern.Dashed;

                // ④ 中轨 (全长虚线)
                var lineMid = plot.Add.Line(xStart, yMidStart, xEnd, yMidEnd);
                lineMid.Color = ScottPlot.Color.FromHex("#94a3b8"); // Slate 400
                lineMid.LineWidth = 1.0f;
                lineMid.LinePattern = LinePattern.Dashed;
                lineMid.LegendText = "通道中轨 (中心回归线)";

                // ⑤ 当前 K 线位置分割虚线
                var vLine = plot.Add.VerticalLine(xCurr);
                vLine.Color = ScottPlot.Color.FromHex("#e2e8f0").WithAlpha(90);
                vLine.LineWidth = 1.2f;
                vLine.LinePattern = LinePattern.Dotted;

                // ⑥ 核心三点确认锚定标记 (如果勾选)
                if (chkShowTouchMarkers.Checked)
                {
                    if (ch.IsThreePointChannel)
                    {
                        if (ch.DirectionType == ChannelDirectionType.TwoLowsOneHigh)
                        {
                            // 两个低点确认方向 (基准支撑线)
                            if (ch.BasePoint1Index >= 0 && ch.BasePoint1Index <= _currentBarIndex)
                            {
                                var m1 = plot.Add.Marker(ch.BasePoint1Index, (double)ch.BasePoint1Price);
                                m1.Shape = MarkerShape.FilledTriangleUp;
                                m1.Size = 11;
                                m1.Color = ScottPlot.Color.FromHex("#22c55e"); // 鲜亮绿
                                m1.LegendText = $"基准低点 1 #{ch.BasePoint1Index}";
                            }

                            if (ch.BasePoint2Index >= 0 && ch.BasePoint2Index <= _currentBarIndex)
                            {
                                var m2 = plot.Add.Marker(ch.BasePoint2Index, (double)ch.BasePoint2Price);
                                m2.Shape = MarkerShape.FilledTriangleUp;
                                m2.Size = 11;
                                m2.Color = ScottPlot.Color.FromHex("#10b981"); // 祖母绿
                                m2.LegendText = $"基准低点 2 #{ch.BasePoint2Index}";
                            }

                            // 一个高点确认对侧边界
                            if (ch.OppositePointIndex >= 0 && ch.OppositePointIndex <= _currentBarIndex)
                            {
                                var m3 = plot.Add.Marker(ch.OppositePointIndex, (double)ch.OppositePointPrice);
                                m3.Shape = MarkerShape.FilledTriangleDown;
                                m3.Size = 11;
                                m3.Color = ScottPlot.Color.FromHex("#ef4444"); // 亮红
                                m3.LegendText = $"对侧高点 #{ch.OppositePointIndex}";
                            }
                        }
                        else // TwoHighsOneLow
                        {
                            // 两个高点确认方向 (基准阻力线)
                            if (ch.BasePoint1Index >= 0 && ch.BasePoint1Index <= _currentBarIndex)
                            {
                                var m1 = plot.Add.Marker(ch.BasePoint1Index, (double)ch.BasePoint1Price);
                                m1.Shape = MarkerShape.FilledTriangleDown;
                                m1.Size = 11;
                                m1.Color = ScottPlot.Color.FromHex("#ef4444"); // 亮红
                                m1.LegendText = $"基准高点 1 #{ch.BasePoint1Index}";
                            }

                            if (ch.BasePoint2Index >= 0 && ch.BasePoint2Index <= _currentBarIndex)
                            {
                                var m2 = plot.Add.Marker(ch.BasePoint2Index, (double)ch.BasePoint2Price);
                                m2.Shape = MarkerShape.FilledTriangleDown;
                                m2.Size = 11;
                                m2.Color = ScottPlot.Color.FromHex("#f97316"); // 橙红
                                m2.LegendText = $"基准高点 2 #{ch.BasePoint2Index}";
                            }

                            // 一个低点确认对侧边界
                            if (ch.OppositePointIndex >= 0 && ch.OppositePointIndex <= _currentBarIndex)
                            {
                                var m3 = plot.Add.Marker(ch.OppositePointIndex, (double)ch.OppositePointPrice);
                                m3.Shape = MarkerShape.FilledTriangleUp;
                                m3.Size = 11;
                                m3.Color = ScottPlot.Color.FromHex("#22c55e"); // 鲜亮绿
                                m3.LegendText = $"对侧低点 #{ch.OppositePointIndex}";
                            }
                        }
                    }
                    else
                    {
                        if (ch.TouchHighIndex >= 0 && ch.TouchHighIndex <= _currentBarIndex)
                        {
                            var highMarker = plot.Add.Marker(ch.TouchHighIndex, (double)ch.TouchHighPrice);
                            highMarker.Shape = MarkerShape.FilledTriangleDown;
                            highMarker.Size = 10;
                            highMarker.Color = ScottPlot.Color.FromHex("#ef4444");
                            highMarker.LegendText = $"锚定高点 #{ch.TouchHighIndex}";
                        }

                        if (ch.TouchLowIndex >= 0 && ch.TouchLowIndex <= _currentBarIndex)
                        {
                            var lowMarker = plot.Add.Marker(ch.TouchLowIndex, (double)ch.TouchLowPrice);
                            lowMarker.Shape = MarkerShape.FilledTriangleUp;
                            lowMarker.Size = 10;
                            lowMarker.Color = ScottPlot.Color.FromHex("#22c55e");
                            lowMarker.LegendText = $"锚定低点 #{ch.TouchLowIndex}";
                        }
                    }
                }
            }

            // 3. 绘制保留通道 (锁定相对极值通道与突破识别，被识别的紫色强趋势通道永久保留不删除)
            if (_engine.EnableRetainedChannel)
            {
                var purpleColor = ScottPlot.Color.FromHex("#a855f7");
                var specialList = _engine.RetentionTracker.SpecialRetainedChannels;
                bool enablePurple = chkSpecialRetained?.Checked ?? true;

                // ① 绘制所有被识别的永久保留紫色通道 (不再删除，一直保留)
                if (enablePurple)
                {
                    for (int pIdx = 0; pIdx < specialList.Count; pIdx++)
                    {
                        var pCh = specialList[pIdx];
                        if (pCh.ConfirmedBarIndex > _currentBarIndex) continue;

                        var bCh = pCh.BaseChannel;
                        double rStartX = bCh.StartX;
                        double rEndX = Math.Max(bCh.EndX, _currentBarIndex + 25);

                        double rUpStart = (double)pCh.GetUpperPrice(rStartX);
                        double rUpEnd = (double)pCh.GetUpperPrice(rEndX);
                        double rLowStart = (double)pCh.GetLowerPrice(rStartX);
                        double rLowEnd = (double)pCh.GetLowerPrice(rEndX);

                        if (pCh.IsDownward)
                        {
                            var lineUp = plot.Add.Line(rStartX, rUpStart, rEndX, rUpEnd);
                            lineUp.Color = purpleColor;
                            lineUp.LineWidth = 0.8f;
                            lineUp.LinePattern = LinePattern.Dashed;
                            if (pIdx == 0 || pCh == _engine.RetentionTracker.ActiveChannel)
                                lineUp.LegendText = $"[永久保留-强趋势] 下降通道阻力上轨 (0.8f紫色 #{pCh.AnchorExtremeIndex})";

                            var lineLow = plot.Add.Line(rStartX, rLowStart, rEndX, rLowEnd);
                            lineLow.Color = purpleColor.WithAlpha(110);
                            lineLow.LineWidth = 0.8f;
                            lineLow.LinePattern = LinePattern.Dotted;
                        }
                        else
                        {
                            var lineLow = plot.Add.Line(rStartX, rLowStart, rEndX, rLowEnd);
                            lineLow.Color = purpleColor;
                            lineLow.LineWidth = 0.8f;
                            lineLow.LinePattern = LinePattern.Dashed;
                            if (pIdx == 0 || pCh == _engine.RetentionTracker.ActiveChannel)
                                lineLow.LegendText = $"[永久保留-强趋势] 上升通道支撑下轨 (0.8f紫色 #{pCh.AnchorExtremeIndex})";

                            var lineUp = plot.Add.Line(rStartX, rUpStart, rEndX, rUpEnd);
                            lineUp.Color = purpleColor.WithAlpha(110);
                            lineUp.LineWidth = 0.8f;
                            lineUp.LinePattern = LinePattern.Dotted;
                        }

                        // 绘制相对极值确认钻石标记
                        if (pCh.AnchorExtremeIndex >= 0 && pCh.AnchorExtremeIndex <= _currentBarIndex)
                        {
                            var extMarker = plot.Add.Marker(pCh.AnchorExtremeIndex, (double)pCh.AnchorExtremePrice);
                            extMarker.Shape = MarkerShape.FilledDiamond;
                            extMarker.Size = 12;
                            extMarker.Color = purpleColor;
                        }

                        // 若已产生突破，绘制突破发生位置
                        if (pCh.IsBrokenOut && pCh.BreakoutBarIndex >= 0 && pCh.BreakoutBarIndex <= _currentBarIndex)
                        {
                            var boMarker = plot.Add.Marker(pCh.BreakoutBarIndex, (double)pCh.BreakoutPrice);
                            boMarker.Shape = pCh.IsDownward ? MarkerShape.FilledTriangleUp : MarkerShape.FilledTriangleDown;
                            boMarker.Size = 13;
                            boMarker.Color = pCh.IsDownward ? ScottPlot.Color.FromHex("#22c55e") : ScottPlot.Color.FromHex("#ef4444");

                            var boLine = plot.Add.VerticalLine(pCh.BreakoutBarIndex);
                            boLine.Color = (pCh.IsDownward ? ScottPlot.Color.FromHex("#22c55e") : ScottPlot.Color.FromHex("#ef4444")).WithAlpha(140);
                            boLine.LineWidth = 1.0f;
                            boLine.LinePattern = LinePattern.Dashed;
                        }
                    }
                }

                // ② 绘制当前活跃的普通保留通道 (非紫色通道，如普通金色/天蓝色 2.4f 粗虚线)
                var retCh = _engine.RetentionTracker.ActiveChannel;
                if (retCh != null && (!enablePurple || !retCh.IsSpecialStrongTrend))
                {
                    var baseCh = retCh.BaseChannel;
                    double rStartX = baseCh.StartX;
                    double rEndX = Math.Max(baseCh.EndX, _currentBarIndex + 25);

                    double rUpStart = (double)retCh.GetUpperPrice(rStartX);
                    double rUpEnd = (double)retCh.GetUpperPrice(rEndX);
                    double rLowStart = (double)retCh.GetLowerPrice(rStartX);
                    double rLowEnd = (double)retCh.GetLowerPrice(rEndX);

                    if (retCh.IsDownward)
                    {
                        var retUpLine = plot.Add.Line(rStartX, rUpStart, rEndX, rUpEnd);
                        retUpLine.Color = ScottPlot.Color.FromHex("#fbbf24"); // Amber 400
                        retUpLine.LineWidth = 2.4f;
                        retUpLine.LinePattern = LinePattern.Dashed;
                        retUpLine.LegendText = $"[保留] 下降通道阻力上轨 (相对高点 #{retCh.AnchorExtremeIndex})";

                        var retLowLine = plot.Add.Line(rStartX, rLowStart, rEndX, rLowEnd);
                        retLowLine.Color = ScottPlot.Color.FromHex("#f59e0b").WithAlpha(90);
                        retLowLine.LineWidth = 1.2f;
                        retLowLine.LinePattern = LinePattern.Dotted;
                    }
                    else
                    {
                        var retLowLine = plot.Add.Line(rStartX, rLowStart, rEndX, rLowEnd);
                        retLowLine.Color = ScottPlot.Color.FromHex("#38bdf8"); // Sky 400
                        retLowLine.LineWidth = 2.4f;
                        retLowLine.LinePattern = LinePattern.Dashed;
                        retLowLine.LegendText = $"[保留] 上升通道支撑下轨 (相对低点 #{retCh.AnchorExtremeIndex})";

                        var retUpLine = plot.Add.Line(rStartX, rUpStart, rEndX, rUpEnd);
                        retUpLine.Color = ScottPlot.Color.FromHex("#0284c7").WithAlpha(90);
                        retUpLine.LineWidth = 1.2f;
                        retUpLine.LinePattern = LinePattern.Dotted;
                    }

                    if (retCh.AnchorExtremeIndex >= 0 && retCh.AnchorExtremeIndex <= _currentBarIndex)
                    {
                        var extMarker = plot.Add.Marker(retCh.AnchorExtremeIndex, (double)retCh.AnchorExtremePrice);
                        extMarker.Shape = MarkerShape.FilledDiamond;
                        extMarker.Size = 13;
                        extMarker.Color = retCh.IsDownward ? ScottPlot.Color.FromHex("#f59e0b") : ScottPlot.Color.FromHex("#06b6d4");
                        extMarker.LegendText = retCh.IsDownward ? $"相对高点 #{retCh.AnchorExtremeIndex}" : $"相对低点 #{retCh.AnchorExtremeIndex}";
                    }

                    if (retCh.IsBrokenOut && retCh.BreakoutBarIndex >= 0 && retCh.BreakoutBarIndex <= _currentBarIndex)
                    {
                        var boMarker = plot.Add.Marker(retCh.BreakoutBarIndex, (double)retCh.BreakoutPrice);
                        boMarker.Shape = retCh.IsDownward ? MarkerShape.FilledTriangleUp : MarkerShape.FilledTriangleDown;
                        boMarker.Size = 15;
                        boMarker.Color = retCh.IsDownward ? ScottPlot.Color.FromHex("#22c55e") : ScottPlot.Color.FromHex("#ef4444");
                        boMarker.LegendText = retCh.IsDownward ? $"向上突破 #{retCh.BreakoutBarIndex}" : $"向下跌破 #{retCh.BreakoutBarIndex}";

                        var boLine = plot.Add.VerticalLine(retCh.BreakoutBarIndex);
                        boLine.Color = (retCh.IsDownward ? ScottPlot.Color.FromHex("#22c55e") : ScottPlot.Color.FromHex("#ef4444")).WithAlpha(150);
                        boLine.LineWidth = 1.5f;
                        boLine.LinePattern = LinePattern.Dashed;
                    }
                }

                // ③ 绘制历史已突破普通通道的标记点（辅助复盘历史突破）
                var history = _engine.RetentionTracker.RetainedHistory;
                for (int hIdx = 0; hIdx < history.Count; hIdx++)
                {
                    var past = history[hIdx];
                    if (past == retCh || (enablePurple && past.IsSpecialStrongTrend)) continue;
                    if (past.IsBrokenOut && past.BreakoutBarIndex >= 0 && past.BreakoutBarIndex <= _currentBarIndex)
                    {
                        var pMarker = plot.Add.Marker(past.BreakoutBarIndex, (double)past.BreakoutPrice);
                        pMarker.Shape = past.IsDownward ? MarkerShape.FilledTriangleUp : MarkerShape.FilledTriangleDown;
                        pMarker.Size = 11;
                        pMarker.Color = (past.IsDownward ? ScottPlot.Color.FromHex("#22c55e") : ScottPlot.Color.FromHex("#ef4444")).WithAlpha(160);
                    }
                }
            }

            // 4. 绘制当前选中的 K 线高亮标识 (支持单击单选 / Shift 连续多选，若绿色通道被选中则由通道自身高亮呈现)
            if (_selectedBarStartIndex.HasValue && _selectedBarEndIndex.HasValue && _selectedConsecutiveTrend == null)
            {
                int sIdx = Math.Min(_selectedBarStartIndex.Value, _selectedBarEndIndex.Value);
                int eIdx = Math.Max(_selectedBarStartIndex.Value, _selectedBarEndIndex.Value);
                sIdx = Math.Min(sIdx, _currentBarIndex);
                eIdx = Math.Min(eIdx, _currentBarIndex);

                if (sIdx >= 0 && eIdx >= sIdx && sIdx < allKlines.Count)
                {
                    if (sIdx == eIdx)
                    {
                        var selBar = allKlines[sIdx];
                        var selVLine = plot.Add.VerticalLine(sIdx);
                        selVLine.Color = ScottPlot.Color.FromHex("#38bdf8").WithAlpha(170); // Sky 400
                        selVLine.LineWidth = 1.8f;
                        selVLine.LinePattern = LinePattern.Dashed;
                        selVLine.LegendText = $"[选中] Bar #{sIdx}";

                        var selMarker = plot.Add.Marker(sIdx, (double)selBar.High);
                        selMarker.Shape = MarkerShape.FilledCircle;
                        selMarker.Size = 8;
                        selMarker.Color = ScottPlot.Color.FromHex("#38bdf8");
                    }
                    else
                    {
                        // 多选连续区间高亮：绘制选区边界垂直虚线与半透明选区阴影框
                        var vLine1 = plot.Add.VerticalLine(sIdx - 0.45);
                        vLine1.Color = ScottPlot.Color.FromHex("#38bdf8").WithAlpha(170);
                        vLine1.LineWidth = 1.6f;
                        vLine1.LinePattern = LinePattern.Dashed;

                        var vLine2 = plot.Add.VerticalLine(eIdx + 0.45);
                        vLine2.Color = ScottPlot.Color.FromHex("#38bdf8").WithAlpha(170);
                        vLine2.LineWidth = 1.6f;
                        vLine2.LinePattern = LinePattern.Dashed;

                        double rangeMinY = double.MaxValue;
                        double rangeMaxY = double.MinValue;
                        for (int b = sIdx; b <= eIdx; b++)
                        {
                            if ((double)allKlines[b].Low < rangeMinY) rangeMinY = (double)allKlines[b].Low;
                            if ((double)allKlines[b].High > rangeMaxY) rangeMaxY = (double)allKlines[b].High;
                        }

                        double yPad = Math.Max(20.0, (rangeMaxY - rangeMinY) * 0.5);
                        var shadePts = new Coordinates[]
                        {
                            new Coordinates(sIdx - 0.45, rangeMinY - yPad),
                            new Coordinates(eIdx + 0.45, rangeMinY - yPad),
                            new Coordinates(eIdx + 0.45, rangeMaxY + yPad),
                            new Coordinates(sIdx - 0.45, rangeMaxY + yPad)
                        };
                        var selShade = plot.Add.Polygon(shadePts);
                        selShade.FillColor = ScottPlot.Color.FromHex("#38bdf8").WithAlpha(25);
                        selShade.LineWidth = 0;
                        selShade.LegendText = $"[多选] Bar #{sIdx} ~ #{eIdx} (共{eIdx - sIdx + 1}根)";
                    }
                }
            }

            // 5. 绘制 V 形态与倒 V 形态标记 (价差 ≥ 5%)
            if (_engine.EnableVPattern && _engine.ShowVPatternLines)
            {
                var vPatterns = _engine.VPatternDetector.DetectedPatterns;
                for (int vIdx = 0; vIdx < vPatterns.Count; vIdx++)
                {
                    var p = vPatterns[vIdx];
                    if (p.ConfirmedBarIndex > _currentBarIndex) continue;

                    int rEnd = Math.Min(p.RightIndex, _currentBarIndex);
                    decimal rPrice = p.RightPrice;
                    if (rEnd < p.RightIndex && rEnd >= 0 && rEnd < allKlines.Count)
                    {
                        rPrice = p.Type == VPatternType.VBottom ? allKlines[rEnd].High : allKlines[rEnd].Low;
                    }

                    if (p.Type == VPatternType.VBottom)
                    {
                        var greenColor = ScottPlot.Color.FromHex("#10b981"); // Emerald 500

                        // 左翼折线 (高点 -> 谷底)
                        var lineLeft = plot.Add.Line(p.LeftIndex, (double)p.LeftPrice, p.BottomIndex, (double)p.BottomPrice);
                        lineLeft.Color = greenColor;
                        lineLeft.LineWidth = 1.8f;
                        lineLeft.LinePattern = LinePattern.Solid;
                        if (vIdx == 0) lineLeft.LegendText = "[V形态] 谷底支撑反弹 (≥5%)";

                        // 右翼折线 (谷底 -> 右翼/最新价)
                        var lineRight = plot.Add.Line(p.BottomIndex, (double)p.BottomPrice, rEnd, (double)rPrice);
                        lineRight.Color = greenColor;
                        lineRight.LineWidth = 1.8f;
                        lineRight.LinePattern = LinePattern.Solid;

                        // 谷底关键反转点高亮菱形标记
                        var mBottom = plot.Add.Marker(p.BottomIndex, (double)p.BottomPrice);
                        mBottom.Shape = MarkerShape.FilledDiamond;
                        mBottom.Size = 12;
                        mBottom.Color = greenColor;

                        // 确立点垂直虚线
                        var confLine = plot.Add.VerticalLine(p.ConfirmedBarIndex);
                        confLine.Color = greenColor.WithAlpha(120);
                        confLine.LineWidth = 1.0f;
                        confLine.LinePattern = LinePattern.Dashed;

                        // 文字标注在谷底下方
                        var txt = plot.Add.Text($"V底 +{p.PriceDiffPct:F1}%", p.BottomIndex, (double)p.BottomPrice - 10);
                        txt.LabelFontColor = greenColor;
                        txt.LabelFontSize = 10;
                        txt.LabelBold = true;
                        txt.Alignment = Alignment.UpperCenter;
                    }
                    else
                    {
                        var redColor = ScottPlot.Color.FromHex("#f43f5e"); // Rose 500

                        // 左翼折线 (低点 -> 顶峰)
                        var lineLeft = plot.Add.Line(p.LeftIndex, (double)p.LeftPrice, p.PeakIndex, (double)p.PeakPrice);
                        lineLeft.Color = redColor;
                        lineLeft.LineWidth = 1.8f;
                        lineLeft.LinePattern = LinePattern.Solid;
                        if (vIdx == 0) lineLeft.LegendText = "[倒V形态] 冲顶回落反转 (≥5%)";

                        // 右翼折线 (顶峰 -> 右翼/最新价)
                        var lineRight = plot.Add.Line(p.PeakIndex, (double)p.PeakPrice, rEnd, (double)rPrice);
                        lineRight.Color = redColor;
                        lineRight.LineWidth = 1.8f;
                        lineRight.LinePattern = LinePattern.Solid;

                        // 顶峰关键反转点高亮菱形标记
                        var mPeak = plot.Add.Marker(p.PeakIndex, (double)p.PeakPrice);
                        mPeak.Shape = MarkerShape.FilledDiamond;
                        mPeak.Size = 12;
                        mPeak.Color = redColor;

                        // 确立点垂直虚线
                        var confLine = plot.Add.VerticalLine(p.ConfirmedBarIndex);
                        confLine.Color = redColor.WithAlpha(120);
                        confLine.LineWidth = 1.0f;
                        confLine.LinePattern = LinePattern.Dashed;

                        // 文字标注在顶峰上方
                        var txt = plot.Add.Text($"倒V -{p.PriceDiffPct:F1}%", p.PeakIndex, (double)p.PeakPrice + 10);
                        txt.LabelFontColor = redColor;
                        txt.LabelFontSize = 10;
                        txt.LabelBold = true;
                        txt.Alignment = Alignment.LowerCenter;
                    }
                }
            }

            // 6. 绘制连续上涨 / 连续下跌形态高亮标记与平行通道 (通道绿色，线宽0.8f，选中时高亮展示)
            if (_engine.EnableConsecutiveTrend)
            {
                var trends = _engine.ConsecutiveTrendDetector.DetectedTrends;
                for (int tIdx = 0; tIdx < trends.Count; tIdx++)
                {
                    var tr = trends[tIdx];
                    if (tr.ConfirmedBarIndex > _currentBarIndex) continue;

                    int sIdx = tr.ChannelStartIndex > 0 ? tr.ChannelStartIndex : tr.StartIndex;
                    int eIdx = Math.Min(tr.EndIndex, _currentBarIndex);
                    if (sIdx < 0 || sIdx >= allKlines.Count || eIdx < sIdx || eIdx >= allKlines.Count) continue;

                    bool isBullish = tr.Type == ConsecutiveTrendType.Bullish;
                    bool isSelected = _selectedConsecutiveTrend != null && _selectedConsecutiveTrend.Id == tr.Id;

                    // 绘制被替换的历史旧通道 (若开启且存在历史快照)
                    if (_engine.ShowHistoricalChannels && tr.PreviousChannels.Count > 0)
                    {
                        for (int pIdx = 0; pIdx < tr.PreviousChannels.Count; pIdx++)
                        {
                            var prev = tr.PreviousChannels[pIdx];
                            int pStart = prev.StartIndex;
                            int pEnd = Math.Min(prev.EndIndex, _currentBarIndex);
                            if (pStart >= 0 && pEnd >= pStart && pEnd < allKlines.Count)
                            {
                                double pUpStart = (double)(prev.SlopeK * pStart + prev.UpperIntercept);
                                double pUpEnd = (double)(prev.SlopeK * pEnd + prev.UpperIntercept);
                                double pLowStart = (double)(prev.SlopeK * pStart + prev.LowerIntercept);
                                double pLowEnd = (double)(prev.SlopeK * pEnd + prev.LowerIntercept);

                                var oldUp = plot.Add.Line(pStart, pUpStart, pEnd, pUpEnd);
                                oldUp.Color = ScottPlot.Color.FromHex("#94a3b8").WithAlpha(80);
                                oldUp.LineWidth = 0.6f;
                                oldUp.LinePattern = LinePattern.Dashed;

                                var oldLow = plot.Add.Line(pStart, pLowStart, pEnd, pLowEnd);
                                oldLow.Color = ScottPlot.Color.FromHex("#94a3b8").WithAlpha(80);
                                oldLow.LineWidth = 0.6f;
                                oldLow.LinePattern = LinePattern.Dashed;
                            }

                            // 在触发更新的 K 线上打上淡蓝小菱形标记
                            if (prev.TriggerBarIndex >= 0 && prev.TriggerBarIndex <= _currentBarIndex && prev.TriggerBarIndex < allKlines.Count)
                            {
                                var updMarker = plot.Add.Marker(prev.TriggerBarIndex, (double)prev.TriggerPrice);
                                updMarker.Shape = MarkerShape.FilledDiamond;
                                updMarker.Size = 6;
                                updMarker.Color = ScottPlot.Color.FromHex("#38bdf8"); // Sky 400
                            }
                        }
                    }

                    // 核心逻辑：通道斜率与截距由确立时刻或最新自动更新时刻锁定，零未来函数，无需等待下一根！
                    decimal slopeK = tr.SlopeK;
                    decimal upperB = tr.UpperIntercept;
                    decimal lowerB = tr.LowerIntercept;
                    if (!tr.HasChannel)
                    {
                        ConsecutiveTrendDetector.FitParallelChannel(allKlines, sIdx, tr.ConfirmedBarIndex, out slopeK, out upperB, out lowerB, tr.PriceMode);
                    }

                    // 绘制绿色 0.8f 平行通道 (选中时线宽加粗至1.5f且背景更亮)
                    if (_engine.ShowConsecutiveChannel && upperB > lowerB)
                    {
                        var greenColor = isSelected
                            ? ScottPlot.Color.FromHex("#34d399") // 选中时采用亮翡翠绿
                            : ScottPlot.Color.FromHex("#10b981"); // 默认纯正绿色

                        double yUpStart = (double)(slopeK * sIdx + upperB);
                        double yUpEnd = (double)(slopeK * eIdx + upperB);
                        double yLowStart = (double)(slopeK * sIdx + lowerB);
                        double yLowEnd = (double)(slopeK * eIdx + lowerB);
                        double yMidStart = (double)(slopeK * sIdx + (upperB + lowerB) / 2m);
                        double yMidEnd = (double)(slopeK * eIdx + (upperB + lowerB) / 2m);

                        // ① 平行通道内部半透明绿色微光填充
                        var channelCoords = new Coordinates[]
                        {
                            new Coordinates(sIdx, yUpStart),
                            new Coordinates(eIdx, yUpEnd),
                            new Coordinates(eIdx, yLowEnd),
                            new Coordinates(sIdx, yLowStart)
                        };
                        var channelPoly = plot.Add.Polygon(channelCoords);
                        channelPoly.FillColor = greenColor.WithAlpha(isSelected ? (byte)48 : (byte)22);
                        channelPoly.LineWidth = 0;

                        // ② 上通道线 (100% 通道上轨，绿色，线宽 0.8f，选中时 1.5f)
                        var lineUp = plot.Add.Line(sIdx, yUpStart, eIdx, yUpEnd);
                        lineUp.Color = greenColor;
                        lineUp.LineWidth = isSelected ? 1.5f : 0.8f;
                        lineUp.LinePattern = LinePattern.Solid;
                        if (tIdx == 0 || isSelected) lineUp.LegendText = isSelected ? "[选中] 100% 通道上轨" : "[连续走势] 100% 通道上轨 (0.8f绿色)";

                        // ③ 下通道线 (0% 通道下轨，绿色，线宽 0.8f，选中时 1.5f)
                        var lineLow = plot.Add.Line(sIdx, yLowStart, eIdx, yLowEnd);
                        lineLow.Color = greenColor;
                        lineLow.LineWidth = isSelected ? 1.5f : 0.8f;
                        lineLow.LinePattern = LinePattern.Solid;
                        if (tIdx == 0 || isSelected) lineLow.LegendText = isSelected ? "[选中] 0% 通道下轨" : "[连续走势] 0% 通道下轨 (0.8f绿色)";

                        // ④ 中通道线 (50% 通道中线，绿色虚线，线宽 0.8f，选中时 1.2f)
                        var lineMid = plot.Add.Line(sIdx, yMidStart, eIdx, yMidEnd);
                        lineMid.Color = greenColor.WithAlpha(isSelected ? (byte)230 : (byte)170);
                        lineMid.LineWidth = isSelected ? 1.2f : 0.8f;
                        lineMid.LinePattern = LinePattern.Dashed;
                        if (isSelected) lineMid.LegendText = "[选中] 50% 通道中线";

                        // ⑤ 极值四等分观察线 (25% 青蓝虚线, 75% 琥珀橙虚线)
                        if (_engine.EnableReversalOrder)
                        {
                            decimal heightB = upperB - lowerB;
                            decimal quarter25B = lowerB + heightB * 0.25m;
                            double y25Start = (double)(slopeK * sIdx + quarter25B);
                            double y25End = (double)(slopeK * eIdx + quarter25B);
                            var line25 = plot.Add.Line(sIdx, y25Start, eIdx, y25End);
                            line25.Color = ScottPlot.Color.FromHex("#06b6d4").WithAlpha(isSelected ? (byte)210 : (byte)150);
                            line25.LineWidth = isSelected ? 1.0f : 0.8f;
                            line25.LinePattern = LinePattern.Dashed;
                            if (isSelected) line25.LegendText = "[选中] 25% 通道高度线";

                            decimal quarter75B = lowerB + heightB * 0.75m;
                            double y75Start = (double)(slopeK * sIdx + quarter75B);
                            double y75End = (double)(slopeK * eIdx + quarter75B);
                            var line75 = plot.Add.Line(sIdx, y75Start, eIdx, y75End);
                            line75.Color = ScottPlot.Color.FromHex("#f59e0b").WithAlpha(isSelected ? (byte)210 : (byte)150);
                            line75.LineWidth = isSelected ? 1.0f : 0.8f;
                            line75.LinePattern = LinePattern.Dashed;
                            if (isSelected) line75.LegendText = "[选中] 75% 通道高度线";

                            if (isSelected)
                            {
                                var txt100 = plot.Add.Text("100%", eIdx + 0.5, yUpEnd);
                                txt100.LabelFontColor = greenColor;
                                txt100.LabelFontSize = 9f;
                                txt100.Alignment = Alignment.MiddleLeft;

                                var txt75 = plot.Add.Text("75%", eIdx + 0.5, y75End);
                                txt75.LabelFontColor = ScottPlot.Color.FromHex("#f59e0b");
                                txt75.LabelFontSize = 9f;
                                txt75.Alignment = Alignment.MiddleLeft;

                                var txt50 = plot.Add.Text("50%", eIdx + 0.5, yMidEnd);
                                txt50.LabelFontColor = greenColor;
                                txt50.LabelFontSize = 9f;
                                txt50.Alignment = Alignment.MiddleLeft;

                                var txt25 = plot.Add.Text("25%", eIdx + 0.5, y25End);
                                txt25.LabelFontColor = ScottPlot.Color.FromHex("#06b6d4");
                                txt25.LabelFontSize = 9f;
                                txt25.Alignment = Alignment.MiddleLeft;

                                var txt0 = plot.Add.Text("0%", eIdx + 0.5, yLowEnd);
                                txt0.LabelFontColor = greenColor;
                                txt0.LabelFontSize = 9f;
                                txt0.Alignment = Alignment.MiddleLeft;
                            }
                        }

                        // ⑥ 确立点三角标记 (在首次达标或新通道截止的 K 线打上显式三角形标记)
                        int cIdx = tr.ChannelEndIndex > 0 ? tr.ChannelEndIndex : tr.ConfirmedBarIndex;
                        if (cIdx >= sIdx && cIdx < allKlines.Count)
                        {
                            double yConf = (double)allKlines[cIdx].Close;
                            var confMarker = plot.Add.Marker(cIdx, yConf);
                            confMarker.Shape = isBullish ? MarkerShape.FilledTriangleUp : MarkerShape.FilledTriangleDown;
                            confMarker.Size = isSelected ? 10 : 8;
                            confMarker.Color = isSelected ? ScottPlot.Color.FromHex("#fbbf24") : greenColor;
                        }

                        // ⑦ 观察阶段进入点圆环标记 (若已到达通道顶部/底部进入观察阶段)
                        if (_engine.EnableReversalOrder && tr.IsObservationEntered && tr.ObservationEntryBarIndex >= sIdx && tr.ObservationEntryBarIndex < allKlines.Count)
                        {
                            int oIdx = tr.ObservationEntryBarIndex;
                            double yEntry = (double)tr.ObservationEntryPrice;
                            var obsMarker = plot.Add.Marker(oIdx, yEntry);
                            obsMarker.Shape = MarkerShape.OpenCircle;
                            obsMarker.Size = isSelected ? 12 : 9;
                            obsMarker.Color = ScottPlot.Color.FromHex("#facc15"); // 琥珀金黄色圆圈
                        }

                        // ⑥ 向右延伸适量虚线方便观察后续突破
                        int extEnd = Math.Min(allKlines.Count - 1, Math.Max(eIdx + 8, _currentBarIndex + 3));
                        if (extEnd > eIdx)
                        {
                            double yUpExt = (double)(slopeK * extEnd + upperB);
                            double yLowExt = (double)(slopeK * extEnd + lowerB);

                            var extUp = plot.Add.Line(eIdx, yUpEnd, extEnd, yUpExt);
                            extUp.Color = greenColor.WithAlpha(130);
                            extUp.LineWidth = 0.8f;
                            extUp.LinePattern = LinePattern.Dashed;

                            var extLow = plot.Add.Line(eIdx, yLowEnd, extEnd, yLowExt);
                            extLow.Color = greenColor.WithAlpha(130);
                            extLow.LineWidth = 0.8f;
                            extLow.LinePattern = LinePattern.Dashed;
                        }

                        // ⑦ 四个角点微小圆点标记
                        var m1 = plot.Add.Marker(sIdx, yUpStart); m1.Shape = MarkerShape.FilledCircle; m1.Size = isSelected ? 6 : 4; m1.Color = greenColor;
                        var m2 = plot.Add.Marker(sIdx, yLowStart); m2.Shape = MarkerShape.FilledCircle; m2.Size = isSelected ? 6 : 4; m2.Color = greenColor;
                        var m3 = plot.Add.Marker(eIdx, yUpEnd); m3.Shape = MarkerShape.FilledCircle; m3.Size = isSelected ? 6 : 4; m3.Color = greenColor;
                        var m4 = plot.Add.Marker(eIdx, yLowEnd); m4.Shape = MarkerShape.FilledCircle; m4.Size = isSelected ? 6 : 4; m4.Color = greenColor;

                        // ⑧ 文字气泡标签标注在通道外沿
                        int curCount = eIdx - tr.StartIndex + 1;
                        decimal curPct = tr.StartPrice > 0 ? (allKlines[eIdx].Close - tr.StartPrice) / tr.StartPrice * 100m : 0m;
                        string updateTag = tr.IsChannelUpdated ? $" 🔄[新通道#{tr.ChannelUpdateCount}]" : "";
                        string baseTag = isBullish ? $"🔥连涨 {curCount}根 (+{curPct:F1}%){updateTag}" : $"❄️连跌 {curCount}根 ({curPct:F1}%){updateTag}";
                        string tag = isSelected ? $"⭐ [已选中] {baseTag}" : baseTag;

                        double midX = (sIdx + eIdx) / 2.0;
                        double textY = isBullish
                            ? (double)(slopeK * (decimal)midX + upperB)
                            : (double)(slopeK * (decimal)midX + lowerB);

                        var txt = plot.Add.Text(tag, midX, textY);
                        txt.LabelFontColor = isSelected ? ScottPlot.Color.FromHex("#34d399") : greenColor;
                        txt.LabelFontSize = isSelected ? 10.5f : 10;
                        txt.LabelBold = true;
                        txt.Alignment = isBullish ? Alignment.LowerCenter : Alignment.UpperCenter;

                        // ⑨ 微观 Tick 跌破/突破通道标记 (上涨跌破下轨 / 下跌突破上轨)
                        if (tr.HasTickBreakthrough && tr.TickBreakthroughBarIndex >= sIdx && tr.TickBreakthroughBarIndex < allKlines.Count)
                        {
                            int btIdx = tr.TickBreakthroughBarIndex;
                            double btPrice = (double)tr.TickBreakthroughPrice;
                            var btMarker = plot.Add.Marker(btIdx, btPrice);
                            if (isBullish)
                            {
                                btMarker.Shape = MarkerShape.FilledTriangleDown;
                                btMarker.Size = isSelected ? 13 : 10;
                                btMarker.Color = ScottPlot.Color.FromHex("#f43f5e"); // Rose 500

                                var btTxt = plot.Add.Text($"⚡跌破下轨 @ {btPrice:F2}", btIdx, btPrice);
                                btTxt.LabelFontColor = ScottPlot.Color.FromHex("#f43f5e");
                                btTxt.LabelFontSize = 9.5f;
                                btTxt.LabelBold = true;
                                btTxt.Alignment = Alignment.UpperCenter;
                            }
                            else
                            {
                                btMarker.Shape = MarkerShape.FilledTriangleUp;
                                btMarker.Size = isSelected ? 13 : 10;
                                btMarker.Color = ScottPlot.Color.FromHex("#fbbf24"); // Amber 400

                                var btTxt = plot.Add.Text($"⚡突破上轨 @ {btPrice:F2}", btIdx, btPrice);
                                btTxt.LabelFontColor = ScottPlot.Color.FromHex("#fbbf24");
                                btTxt.LabelFontSize = 9.5f;
                                btTxt.LabelBold = true;
                                btTxt.Alignment = Alignment.LowerCenter;
                            }
                        }
                    }
                    else
                    {
                        // 备用简易标注模式 (未勾选平行通道时)
                        var themeColor = isBullish ? ScottPlot.Color.FromHex("#10b981") : ScottPlot.Color.FromHex("#f43f5e");
                        double pStart = (double)allKlines[sIdx].Close;
                        double pEnd = (double)allKlines[eIdx].Close;
                        var trendLine = plot.Add.Line(sIdx, pStart, eIdx, pEnd);
                        trendLine.Color = themeColor.WithAlpha(200);
                        trendLine.LineWidth = 1.8f;
                        trendLine.LinePattern = LinePattern.Dashed;

                        var startDot = plot.Add.Marker(sIdx, pStart);
                        startDot.Shape = MarkerShape.FilledCircle;
                        startDot.Size = 6;
                        startDot.Color = themeColor;

                        var endDot = plot.Add.Marker(eIdx, pEnd);
                        endDot.Shape = isBullish ? MarkerShape.FilledTriangleUp : MarkerShape.FilledTriangleDown;
                        endDot.Size = 9;
                        endDot.Color = themeColor;

                        int curCount = eIdx - sIdx + 1;
                        decimal curPct = tr.StartPrice > 0 ? (allKlines[eIdx].Close - tr.StartPrice) / tr.StartPrice * 100m : 0m;
                        string tag = isBullish ? $"🔥连涨 {curCount}根 (+{curPct:F1}%)" : $"❄️连跌 {curCount}根 ({curPct:F1}%)";

                        var txt = plot.Add.Text(tag, (sIdx + eIdx) / 2.0, (pStart + pEnd) / 2.0);
                        txt.LabelFontColor = isBullish ? ScottPlot.Color.FromHex("#34d399") : ScottPlot.Color.FromHex("#fb7185");
                        txt.LabelFontSize = 10;
                        txt.LabelBold = true;
                        txt.Alignment = isBullish ? Alignment.LowerCenter : Alignment.UpperCenter;
                    }
                }
            }

            // 6.5 绘制反转做单信号短黄线标记 (Yellow Line Marker)
            if (_engine.EnableReversalOrder && _engine.ShowReversalYellowLines)
            {
                var signals = _engine.ReversalOrderEngine.AllSignals;
                var yellowColor = ScottPlot.Color.FromHex("#facc15"); // Gold / Yellow 400
                for (int sIdx = 0; sIdx < signals.Count; sIdx++)
                {
                    var sig = signals[sIdx];
                    if (sig.BigBarIndex > _currentBarIndex || sig.BigBarIndex < 0 || sig.BigBarIndex >= allKlines.Count) continue;

                    double x = sig.BigBarIndex;
                    double y = (double)sig.Price;

                    // 短黄线：横穿当前K线，水平跨度 [x - 0.42, x + 0.42]，线宽 2.5f
                    var yellowLine = plot.Add.Line(x - 0.42, y, x + 0.42, y);
                    yellowLine.Color = yellowColor;
                    yellowLine.LineWidth = 2.5f;
                    yellowLine.LinePattern = LinePattern.Solid;
                    if (sIdx == 0) yellowLine.LegendText = "⚡ 反转做单信号 (短黄线)";

                    // 两端微型圆点增加质感
                    var mLeft = plot.Add.Marker(x - 0.42, y);
                    mLeft.Shape = MarkerShape.FilledCircle;
                    mLeft.Size = 4;
                    mLeft.Color = yellowColor;

                    var mRight = plot.Add.Marker(x + 0.42, y);
                    mRight.Shape = MarkerShape.FilledCircle;
                    mRight.Size = 4;
                    mRight.Color = yellowColor;

                    // 方向指示箭头标记
                    var arrowMarker = plot.Add.Marker(x, y);
                    arrowMarker.Shape = sig.Direction == OrderSignalDirection.Sell ? MarkerShape.FilledTriangleDown : MarkerShape.FilledTriangleUp;
                    arrowMarker.Size = 9;
                    arrowMarker.Color = sig.Direction == OrderSignalDirection.Sell ? ScottPlot.Color.FromHex("#ef4444") : ScottPlot.Color.FromHex("#22c55e");

                    // 气泡标签文字：如 "⚡顺势低多 @ 68480.00" 或 "⚡高点做空 @ 68480.00"
                    string dirStr = sig.IsBreakoutTrendFollowing
                        ? (sig.Direction == OrderSignalDirection.Sell ? "顺势高空" : "顺势低多")
                        : (sig.Direction == OrderSignalDirection.Sell ? "高点做空" : "低点做多");
                    string lineTag = !string.IsNullOrEmpty(sig.ChannelLineReaction) ? $"[{sig.ChannelLineReaction}] " : "";
                    string modeTag = sig.IsTickStreamTriggered ? $" (极值:{sig.PeakTroughPrice:F2})" : "";
                    string labelText = $"⚡{lineTag}{dirStr} @ {sig.Price:F2}{modeTag} [#{sig.Pattern.PatternId} C{sig.ObservationCycleIndex}]";
                    double barSpan = (double)(allKlines[sig.BigBarIndex].High - allKlines[sig.BigBarIndex].Low);
                    if (barSpan <= 0) barSpan = 10.0;
                    double labelY = sig.Direction == OrderSignalDirection.Sell ? y + barSpan * 0.25 : y - barSpan * 0.25;

                    var sigTxt = plot.Add.Text(labelText, x, labelY);
                    sigTxt.LabelFontColor = yellowColor;
                    sigTxt.LabelFontSize = 9.5f;
                    sigTxt.LabelBold = true;
                    sigTxt.Alignment = sig.Direction == OrderSignalDirection.Sell ? Alignment.LowerCenter : Alignment.UpperCenter;
                }
            }

            // 7. 坐标轴视口控制 (跟随最新与自动缩放，留出合理的上下边距)
            if (chkAutoScale.Checked)
            {
                if (chkFollowLatest.Checked && _currentChannel.IsValid)
                {
                    double minX = Math.Max(0, _currentChannel.StartX - 10);
                    double maxX = _currentChannel.EndX + 15;

                    double minY = double.MaxValue;
                    double maxY = double.MinValue;

                    for (int i = Math.Max(0, _currentChannel.StartX); i <= _currentBarIndex; i++)
                    {
                        minY = Math.Min(minY, (double)allKlines[i].Low);
                        maxY = Math.Max(maxY, (double)allKlines[i].High);
                    }

                    minY = Math.Min(minY, (double)_currentChannel.GetLowerPrice(_currentChannel.StartX));
                    minY = Math.Min(minY, (double)_currentChannel.GetLowerPrice(_currentChannel.EndX));
                    maxY = Math.Max(maxY, (double)_currentChannel.GetUpperPrice(_currentChannel.StartX));
                    maxY = Math.Max(maxY, (double)_currentChannel.GetUpperPrice(_currentChannel.EndX));

                    if (_engine.EnableRetainedChannel && _engine.RetentionTracker.ActiveChannel != null)
                    {
                        var rch = _engine.RetentionTracker.ActiveChannel;
                        minY = Math.Min(minY, (double)rch.GetLowerPrice(minX));
                        minY = Math.Min(minY, (double)rch.GetLowerPrice(maxX));
                        maxY = Math.Max(maxY, (double)rch.GetUpperPrice(minX));
                        maxY = Math.Max(maxY, (double)rch.GetUpperPrice(maxX));
                    }

                    double yMargin = (maxY - minY) * 0.08;
                    plot.Axes.SetLimits(minX, maxX, minY - yMargin, maxY + yMargin);
                }
                else
                {
                    plot.Axes.Margins(0.02, 0.08);
                    plot.Axes.AutoScale();
                }
            }

            // 纯净画布体验：图表上方无文字堆叠遮挡；若用户勾选右上角图例则微型半透明渲染
            if (chkShowLegend.Checked)
            {
                plot.ShowLegend(Alignment.UpperRight);
                plot.Legend.FontName = chineseFont;
                plot.Legend.FontSize = 8.5f;
                plot.Legend.BackgroundColor = ScottPlot.Color.FromHex("#1e293b").WithAlpha(180);
                plot.Legend.FontColor = ScottPlot.Color.FromHex("#f1f5f9");
            }
            else
            {
                plot.HideLegend();
            }

            formsPlot.Refresh();
        }

        #region 微观 Tick 数据联动与图表点击交互

        private void OnFormsPlotMouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _isPlotMouseDown = true;
                _plotMouseDownPos = e.Location;
            }
        }

        private void OnFormsPlotMouseUp(object? sender, MouseEventArgs e)
        {
            if (_isPlotMouseDown)
            {
                _isPlotMouseDown = false;
                int dx = Math.Abs(e.Location.X - _plotMouseDownPos.X);
                int dy = Math.Abs(e.Location.Y - _plotMouseDownPos.Y);

                // 鼠标位移微小判定为单击 (排除拖拽平移或框选缩放操作)
                if (dx <= 8 && dy <= 8)
                {
                    bool isCtrl = (ModifierKeys & Keys.Control) == Keys.Control;
                    // 默认优先检测是否点击了绿色平行通道 (若按住 Ctrl 则强制命中单根 K 线)
                    if (!isCtrl && HitTestConsecutiveChannel(e.X, e.Y))
                    {
                        return;
                    }
                    HitTestKline(e.X, e.Y);
                }
            }
        }

        private void OnFormsPlotMouseMove(object? sender, MouseEventArgs e)
        {
            if (!_isPlotMouseDown && _engine.AllKlines.Count > 0 && _currentBarIndex >= 0)
            {
                bool isNearChannel = IsNearConsecutiveChannel(e.X, e.Y);
                bool isNear = isNearChannel || IsNearAnyKline(e.X, e.Y);
                formsPlot.Cursor = isNear ? Cursors.Hand : Cursors.Default;
            }
        }

        private bool IsNearConsecutiveChannel(double mouseX, double mouseY)
        {
            try
            {
                if (!_engine.EnableConsecutiveTrend || !_engine.ShowConsecutiveChannel) return false;
                var allKlines = _engine.AllKlines;
                if (allKlines.Count == 0 || _currentBarIndex < 0) return false;

                var trends = _engine.ConsecutiveTrendDetector.DetectedTrends;
                if (trends.Count == 0) return false;

                var mouseCoord = formsPlot.Plot.GetCoordinates(new ScottPlot.Pixel(mouseX, mouseY));
                double mX = mouseCoord.X;

                for (int tIdx = trends.Count - 1; tIdx >= 0; tIdx--)
                {
                    var tr = trends[tIdx];
                    if (tr.ConfirmedBarIndex > _currentBarIndex || !tr.HasChannel) continue;

                    int sIdx = tr.StartIndex;
                    int eIdx = Math.Min(tr.EndIndex, _currentBarIndex);
                    if (sIdx < 0 || sIdx >= allKlines.Count || eIdx < sIdx || eIdx >= allKlines.Count) continue;

                    bool isBullish = tr.Type == ConsecutiveTrendType.Bullish;
                    double midX = (sIdx + eIdx) / 2.0;
                    double tagY = isBullish
                        ? (double)(tr.SlopeK * (decimal)midX + tr.UpperIntercept)
                        : (double)(tr.SlopeK * (decimal)midX + tr.LowerIntercept);

                    var pixTag = formsPlot.Plot.GetPixel(new ScottPlot.Coordinates(midX, tagY));
                    if (Math.Abs(mouseX - pixTag.X) <= 75 && Math.Abs(mouseY - pixTag.Y) <= 25) return true;

                    if (mX >= sIdx - 0.5 && mX <= eIdx + 0.5)
                    {
                        double yUp = (double)(tr.SlopeK * (decimal)mX + tr.UpperIntercept);
                        double yLow = (double)(tr.SlopeK * (decimal)mX + tr.LowerIntercept);

                        var pixUp = formsPlot.Plot.GetPixel(new ScottPlot.Coordinates(mX, yUp));
                        var pixLow = formsPlot.Plot.GetPixel(new ScottPlot.Coordinates(mX, yLow));

                        double topPix = Math.Min(pixUp.Y, pixLow.Y) - 15;
                        double botPix = Math.Max(pixUp.Y, pixLow.Y) + 15;

                        if (mouseY >= topPix && mouseY <= botPix) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private bool HitTestConsecutiveChannel(double mouseX, double mouseY)
        {
            try
            {
                if (!_engine.EnableConsecutiveTrend || !_engine.ShowConsecutiveChannel) return false;
                var allKlines = _engine.AllKlines;
                if (allKlines.Count == 0 || _currentBarIndex < 0) return false;

                var trends = _engine.ConsecutiveTrendDetector.DetectedTrends;
                if (trends.Count == 0) return false;

                var mouseCoord = formsPlot.Plot.GetCoordinates(new ScottPlot.Pixel(mouseX, mouseY));
                double mX = mouseCoord.X;

                for (int tIdx = trends.Count - 1; tIdx >= 0; tIdx--)
                {
                    var tr = trends[tIdx];
                    if (tr.ConfirmedBarIndex > _currentBarIndex || !tr.HasChannel) continue;

                    int sIdx = tr.StartIndex;
                    int eIdx = Math.Min(tr.EndIndex, _currentBarIndex);
                    if (sIdx < 0 || sIdx >= allKlines.Count || eIdx < sIdx || eIdx >= allKlines.Count) continue;

                    decimal slopeK = tr.SlopeK;
                    decimal upperB = tr.UpperIntercept;
                    decimal lowerB = tr.LowerIntercept;
                    if (upperB <= lowerB) continue;

                    // ① 检查文字标签气泡区域 (如 🔥连涨 5根 (+3.6%))
                    bool isBullish = tr.Type == ConsecutiveTrendType.Bullish;
                    double midX = (sIdx + eIdx) / 2.0;
                    double tagY = isBullish
                        ? (double)(slopeK * (decimal)midX + upperB)
                        : (double)(slopeK * (decimal)midX + lowerB);

                    var pixTag = formsPlot.Plot.GetPixel(new ScottPlot.Coordinates(midX, tagY));
                    if (Math.Abs(mouseX - pixTag.X) <= 75 && Math.Abs(mouseY - pixTag.Y) <= 25)
                    {
                        SelectConsecutiveTrend(tr);
                        return true;
                    }

                    // ② 检查通道主体区间 (sIdx 到 eIdx，带 0.5 bar 容差)
                    if (mX >= sIdx - 0.5 && mX <= eIdx + 0.5)
                    {
                        double yUp = (double)(slopeK * (decimal)mX + upperB);
                        double yLow = (double)(slopeK * (decimal)mX + lowerB);

                        var pixUp = formsPlot.Plot.GetPixel(new ScottPlot.Coordinates(mX, yUp));
                        var pixLow = formsPlot.Plot.GetPixel(new ScottPlot.Coordinates(mX, yLow));

                        double topPix = Math.Min(pixUp.Y, pixLow.Y) - 15;
                        double botPix = Math.Max(pixUp.Y, pixLow.Y) + 15;

                        if (mouseY >= topPix && mouseY <= botPix)
                        {
                            SelectConsecutiveTrend(tr);
                            return true;
                        }
                    }

                    // ③ 检查向前延伸虚线区域 (eIdx 到 extEnd)
                    int extEnd = Math.Min(allKlines.Count - 1, Math.Max(eIdx + 8, _currentBarIndex + 3));
                    if (extEnd > eIdx && mX > eIdx + 0.5 && mX <= extEnd + 0.5)
                    {
                        double yUp = (double)(slopeK * (decimal)mX + upperB);
                        double yLow = (double)(slopeK * (decimal)mX + lowerB);
                        var pixUp = formsPlot.Plot.GetPixel(new ScottPlot.Coordinates(mX, yUp));
                        var pixLow = formsPlot.Plot.GetPixel(new ScottPlot.Coordinates(mX, yLow));

                        if (Math.Abs(mouseY - pixUp.Y) <= 12 || Math.Abs(mouseY - pixLow.Y) <= 12)
                        {
                            SelectConsecutiveTrend(tr);
                            return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private void SelectConsecutiveTrend(ConsecutiveTrendItem tr)
        {
            _selectedConsecutiveTrend = tr;
            _selectedBarStartIndex = tr.StartIndex;
            _selectedBarEndIndex = Math.Min(tr.EndIndex, _currentBarIndex);
            _selectedBarIndex = tr.StartIndex;

            var allKlines = _engine.AllKlines;
            int sIdx = tr.StartIndex;
            int eIdx = Math.Min(tr.EndIndex, _currentBarIndex);
            int count = eIdx - sIdx + 1;
            DateTime openTime = TimeHelper.FromUnixTimeMilliseconds(allKlines[sIdx].OpenTime);
            DateTime closeTime = TimeHelper.FromUnixTimeMilliseconds(allKlines[eIdx].CloseTime);

            AppendLog($"\n================== 🟩 选中连续走势绿色平行通道 #{sIdx}~#{eIdx} ==================");
            AppendLog($"【形态类型】: {tr.TypeName} (方向: {(tr.Type == ConsecutiveTrendType.Bullish ? "↗ 连续上涨" : "↘ 连续下跌")})");
            AppendLog($"【K线跨度】: Bar #{sIdx} ~ #{eIdx} (共 {count} 根 K 线，于 Bar #{tr.ConfirmedBarIndex} 确立成型)");
            AppendLog($"【时间区间】: {openTime:yyyy-MM-dd HH:mm:ss} ~ {closeTime:HH:mm:ss}");
            AppendLog($"【价格变动】: 起点 {tr.StartPrice:F2} -> 终点 {allKlines[eIdx].Close:F2} (累计涨跌幅: {tr.PriceChangePct:+0.00;-0.00}%)");
            AppendLog($"【通道几何】: 斜率 k = {tr.SlopeK:F4} USDT/bar");
            AppendLog($"             上通道线: y = {tr.SlopeK:F4} * x + {tr.UpperIntercept:F4}");
            AppendLog($"             下通道线: y = {tr.SlopeK:F4} * x + {tr.LowerIntercept:F4}");
            AppendLog($"             中通道线: y = {tr.SlopeK:F4} * x + {tr.MidIntercept:F4}");
            AppendLog($"             通道高度: {tr.ChannelHeight:F4} USDT");
            AppendLog($"【运行状态】: {(tr.IsActive ? "🔥当前活跃延伸中" : "已闭合历史形态")}");
            AppendLog($"=================================================================================\n");

            RenderPlot();
            _ = LoadAndDisplayKlineTicksAsync(sIdx, eIdx);
        }

        private bool IsNearAnyKline(double mouseX, double mouseY)
        {
            try
            {
                var allKlines = _engine.AllKlines;
                if (allKlines.Count == 0 || _currentBarIndex < 0) return false;

                var mouseCoord = formsPlot.Plot.GetCoordinates(new ScottPlot.Pixel(mouseX, mouseY));
                int targetIndex = (int)Math.Round(mouseCoord.X);

                if (targetIndex >= 0 && targetIndex <= _currentBarIndex && targetIndex < allKlines.Count)
                {
                    var kline = allKlines[targetIndex];
                    var pixelHigh = formsPlot.Plot.GetPixel(new ScottPlot.Coordinates(targetIndex, (double)kline.High));
                    var pixelLow = formsPlot.Plot.GetPixel(new ScottPlot.Coordinates(targetIndex, (double)kline.Low));

                    double topY = Math.Min(pixelHigh.Y, pixelLow.Y) - 25;
                    double bottomY = Math.Max(pixelHigh.Y, pixelLow.Y) + 25;

                    return mouseY >= topY && mouseY <= bottomY;
                }
            }
            catch { }
            return false;
        }

        private bool HitTestKline(double mouseX, double mouseY)
        {
            try
            {
                var allKlines = _engine.AllKlines;
                if (allKlines.Count == 0 || _currentBarIndex < 0) return false;

                var mouseCoord = formsPlot.Plot.GetCoordinates(new ScottPlot.Pixel(mouseX, mouseY));
                int targetIndex = (int)Math.Round(mouseCoord.X);

                if (targetIndex >= 0 && targetIndex <= _currentBarIndex && targetIndex < allKlines.Count)
                {
                    var kline = allKlines[targetIndex];
                    var pixelClose = formsPlot.Plot.GetPixel(new ScottPlot.Coordinates(targetIndex, (double)kline.Close));
                    var pixelHigh = formsPlot.Plot.GetPixel(new ScottPlot.Coordinates(targetIndex, (double)kline.High));
                    var pixelLow = formsPlot.Plot.GetPixel(new ScottPlot.Coordinates(targetIndex, (double)kline.Low));

                    double topY = Math.Min(pixelHigh.Y, pixelLow.Y) - 30;
                    double bottomY = Math.Max(pixelHigh.Y, pixelLow.Y) + 30;
                    double leftX = pixelClose.X - 25;
                    double rightX = pixelClose.X + 25;

                    if (mouseX >= leftX && mouseX <= rightX && mouseY >= topY && mouseY <= bottomY)
                    {
                        bool isShift = (ModifierKeys & Keys.Shift) == Keys.Shift;

                        _selectedConsecutiveTrend = null; // 点击单根K线退出通道整体模式

                        if (isShift && _selectedBarIndex.HasValue)
                        {
                            // Shift 连续多选：以首次点击的 _selectedBarIndex 为锚点，延伸至 targetIndex
                            int start = Math.Min(_selectedBarIndex.Value, targetIndex);
                            int end = Math.Max(_selectedBarIndex.Value, targetIndex);
                            _selectedBarStartIndex = start;
                            _selectedBarEndIndex = end;
                        }
                        else
                        {
                            // 单击单选，并设为后续 Shift 多选的锚点
                            _selectedBarIndex = targetIndex;
                            _selectedBarStartIndex = targetIndex;
                            _selectedBarEndIndex = targetIndex;
                        }

                        RenderPlot(); // 刷新主图以展示选中高亮标记
                        _ = LoadAndDisplayKlineTicksAsync(_selectedBarStartIndex.Value, _selectedBarEndIndex.Value);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private async System.Threading.Tasks.Task LoadAndDisplayKlineTicksAsync(int startIndex, int endIndex)
        {
            try
            {
                var allKlines = _engine.AllKlines;
                if (allKlines.Count == 0) return;

                startIndex = Math.Clamp(startIndex, 0, Math.Min(_currentBarIndex, allKlines.Count - 1));
                endIndex = Math.Clamp(endIndex, 0, Math.Min(_currentBarIndex, allKlines.Count - 1));
                if (startIndex > endIndex)
                {
                    int tmp = startIndex; startIndex = endIndex; endIndex = tmp;
                }

                var klineStart = allKlines[startIndex];
                var klineEnd = allKlines[endIndex];
                int barCount = endIndex - startIndex + 1;
                string coin = cboCoin.SelectedItem?.ToString() ?? "BTCUSDT";
                DateTime openTime = TimeHelper.FromUnixTimeMilliseconds(klineStart.OpenTime);
                DateTime closeTime = TimeHelper.FromUnixTimeMilliseconds(klineEnd.CloseTime);

                string barRangeStr = barCount == 1 ? $"Bar #{startIndex}" : $"Bar #{startIndex} ~ #{endIndex} (共 {barCount} 根 K 线)";
                lblTickTitle.Text = $"🎯 {barRangeStr} 微观逐笔成交明细";
                lblTickInfo.Text = $"⏳ 正在读取 {barRangeStr} [{openTime:HH:mm} ~ {closeTime:HH:mm}] 逐笔 Tick 数据...";

                var ticks = await ParquetDataReader.ReadTicksForTimeRangeAsync(coin, klineStart.OpenTime, klineEnd.CloseTime).ConfigureAwait(true);
                DisplayTicksInternal(startIndex, endIndex, ticks);

                AppendLog($"[Tick明细] 选中 {barRangeStr} (时间: {openTime:HH:mm:ss} ~ {closeTime:HH:mm:ss}), 成功读取 {ticks.Length:N0} 笔 Tick 数据。");
            }
            catch (Exception ex)
            {
                string barRangeStr = startIndex == endIndex ? $"Bar #{startIndex}" : $"Bar #{startIndex}~#{endIndex}";
                lblTickInfo.Text = $"{barRangeStr} | 读取 Tick 数据异常: {ex.Message}";
                AppendLog($"[Tick明细] 读取 {barRangeStr} 异常: {ex.Message}");
            }
        }

        private void DisplayTicksInternal(int startIndex, int endIndex, RawTick[]? ticks)
        {
            _cachedTickStartIndex = startIndex;
            _cachedTickEndIndex = endIndex;
            _cachedRawTicks = ticks;

            var allKlines = _engine.AllKlines;
            int totalKlines = allKlines.Count;
            if (startIndex < 0 || endIndex >= totalKlines || startIndex > endIndex) return;

            var klineStart = allKlines[startIndex];
            var klineEnd = allKlines[endIndex];
            int barCount = endIndex - startIndex + 1;
            string barRangeStr = barCount == 1 ? $"Bar #{startIndex}" : $"Bar #{startIndex} ~ #{endIndex} (共 {barCount} 根 K 线)";

            if (ticks == null || ticks.Length == 0)
            {
                DateTime openTime = TimeHelper.FromUnixTimeMilliseconds(klineStart.OpenTime);
                DateTime closeTime = TimeHelper.FromUnixTimeMilliseconds(klineEnd.CloseTime);
                string dir = klineEnd.Close >= klineStart.Open ? "🟢 涨" : "🔴 跌";
                lblTickInfo.Text = $"{barRangeStr} ({dir}) | {openTime:yyyy-MM-dd HH:mm:ss} ~ {closeTime:HH:mm:ss} | 本地暂无该周期的逐笔 Tick 数据";
                lblPeriodSummary.Text = "暂无可用 Tick 数据";
                formsPlotTick.Plot.Clear();
                formsPlotTick.Plot.Title($"{barRangeStr} 暂无可用 Tick 数据");
                formsPlotTick.Refresh();
                _displayedTicks.Clear();
                dgvTicks.RowCount = 0;
                _displayedKlines.Clear();
                dgvSynthesizedKlines.RowCount = 0;
                return;
            }

            int totalTicks = ticks.Length;
            DateTime kOpenTime = TimeHelper.FromUnixTimeMilliseconds(klineStart.OpenTime);
            DateTime kCloseTime = TimeHelper.FromUnixTimeMilliseconds(klineEnd.CloseTime);

            decimal totalVol = 0m;
            decimal totalQuote = 0m;
            decimal totalTakerBuyVol = 0m;
            decimal overallHigh = decimal.MinValue;
            decimal overallLow = decimal.MaxValue;

            for (int i = 0; i < totalTicks; i++)
            {
                var t = ticks[i];
                if (t.Price > overallHigh) overallHigh = t.Price;
                if (t.Price < overallLow) overallLow = t.Price;
                totalVol += t.Qty;
                totalQuote += t.QuoteQty;
                if (!t.IsBuyerMaker) totalTakerBuyVol += t.Qty;
            }

            string dirStr = klineEnd.Close >= klineStart.Open ? "🟢 涨" : "🔴 跌";
            decimal takerBuyRatio = totalVol > 0 ? (totalTakerBuyVol / totalVol * 100m) : 0m;

            // --- 通道穿过判定与计算 (优先判定绿色连续平行通道) ---
            ConsecutiveTrendItem? consecCh = _selectedConsecutiveTrend;
            if (consecCh == null && _engine.EnableConsecutiveTrend && _engine.ShowConsecutiveChannel)
            {
                var trends = _engine.ConsecutiveTrendDetector.DetectedTrends;
                for (int t = trends.Count - 1; t >= 0; t--)
                {
                    var tr = trends[t];
                    if (tr.ConfirmedBarIndex <= _currentBarIndex && tr.HasChannel)
                    {
                        long trStartT = allKlines[tr.StartIndex].OpenTime;
                        long trEndBarT = allKlines[Math.Min(tr.EndIndex, allKlines.Count - 1)].CloseTime;
                        long obsReachT = trEndBarT + (long)_engine.ReversalOrderEngine.ObservationMinutes * 60_000L * 15;

                        // 扩大判定：不仅包含波段本体，还包含后续的观察期跨度
                        if (Math.Max(startIndex, tr.StartIndex) <= Math.Min(endIndex, tr.EndIndex) ||
                            (klineEnd.CloseTime >= trStartT && klineStart.OpenTime <= obsReachT))
                        {
                            consecCh = tr;
                            break;
                        }
                    }
                }
            }
            bool hasConsecChannel = consecCh != null && consecCh.HasChannel;
            double consecSlope = hasConsecChannel ? (double)consecCh!.SlopeK : 0;
            double consecUpB = hasConsecChannel ? (double)consecCh!.UpperIntercept : 0;
            double consecLowB = hasConsecChannel ? (double)consecCh!.LowerIntercept : 0;
            double consecMidB = hasConsecChannel ? (double)consecCh!.MidIntercept : 0;

            bool hasDynamicChannel = _currentChannel.IsValid &&
                                     Math.Max(startIndex, _currentChannel.StartX) <= Math.Min(endIndex, _currentChannel.EndX);

            RetainedChannel? retCh = null;
            if (_engine.EnableRetainedChannel)
            {
                var specialList = _engine.RetentionTracker.SpecialRetainedChannels;
                for (int sIdx = specialList.Count - 1; sIdx >= 0; sIdx--)
                {
                    var sp = specialList[sIdx];
                    if (sp.ConfirmedBarIndex <= endIndex && endIndex >= sp.AnchorExtremeIndex)
                    {
                        retCh = sp;
                        break;
                    }
                }
                if (retCh == null && _engine.RetentionTracker.ActiveChannel != null)
                {
                    var act = _engine.RetentionTracker.ActiveChannel;
                    if (endIndex >= act.AnchorExtremeIndex)
                    {
                        retCh = act;
                    }
                }
            }
            bool hasRetainedChannel = retCh != null;

            double chSlope = hasDynamicChannel ? (double)_currentChannel.SlopeK : 0;
            double chUpB = hasDynamicChannel ? (double)_currentChannel.UpperIntercept : 0;
            double chLowB = hasDynamicChannel ? (double)_currentChannel.LowerIntercept : 0;
            double chMidB = hasDynamicChannel ? (double)_currentChannel.CenterIntercept : 0;

            long tStart = klineStart.OpenTime;
            long tEnd = klineEnd.CloseTime;
            long spanMs = Math.Max(1L, tEnd - tStart);

            // 预分配并计算每个 Tick 对应的连续 K 线坐标与通道位
            int countAboveUpper = 0, countBelowLower = 0, countRetBreakout = 0;
            int firstUpperIdx = -1, firstLowerIdx = -1, firstRetIdx = -1;
            int maxUpperPierceIdx = -1, maxLowerPierceIdx = -1;
            double maxUpperDiff = 0, maxLowerDiff = 0;

            var tickBarIndices = new int[totalTicks];
            var tickChannelRelations = new string[totalTicks];

            int currentScanBar = startIndex;
            for (int i = 0; i < totalTicks; i++)
            {
                var t = ticks[i];
                long tTime = t.Time;

                // 定位该 Tick 归属哪根 K 线
                while (currentScanBar < endIndex && tTime > allKlines[currentScanBar].CloseTime)
                {
                    currentScanBar++;
                }
                tickBarIndices[i] = currentScanBar;

                // 计算时间归一化位置与连续坐标 X
                double alpha = Math.Clamp((double)(tTime - tStart) / spanMs, 0.0, 1.0);
                double contX = startIndex - 0.5 + alpha * barCount;

                string rel = "-";
                bool brokeRet = false;
                if (hasRetainedChannel && retCh != null)
                {
                    double retBound = (double)retCh.GetBreakoutBoundaryPrice(contX);
                    if (retCh.IsDownward && (double)t.Price >= retBound)
                    {
                        brokeRet = true;
                        countRetBreakout++;
                        if (firstRetIdx < 0) firstRetIdx = i;
                        rel = "🚀突破保留阻力";
                    }
                    else if (!retCh.IsDownward && (double)t.Price <= retBound)
                    {
                        brokeRet = true;
                        countRetBreakout++;
                        if (firstRetIdx < 0) firstRetIdx = i;
                        rel = "💥跌破保留支撑";
                    }
                }

                if (!brokeRet && hasConsecChannel && consecCh != null)
                {
                    double yUp = consecSlope * contX + consecUpB;
                    double yLow = consecSlope * contX + consecLowB;

                    if ((double)t.Price > yUp)
                    {
                        countAboveUpper++;
                        if (firstUpperIdx < 0)
                        {
                            firstUpperIdx = i;
                            if (consecCh.Type == ConsecutiveTrendType.Bearish && !consecCh.HasTickBreakthrough)
                            {
                                consecCh.HasTickBreakthrough = true;
                                consecCh.TickBreakthroughBarIndex = tickBarIndices[i];
                                consecCh.TickBreakthroughPrice = t.Price;
                                consecCh.TickBreakthroughTime = t.Time;
                                consecCh.TickBreakthroughTickIndex = i;
                                consecCh.TickBreakthroughType = "⚡下跌突破上轨";
                            }
                        }
                        double diff = (double)t.Price - yUp;
                        if (diff > maxUpperDiff) { maxUpperDiff = diff; maxUpperPierceIdx = i; }
                        rel = "⚡破绿色上轨";
                    }
                    else if ((double)t.Price < yLow)
                    {
                        countBelowLower++;
                        if (firstLowerIdx < 0)
                        {
                            firstLowerIdx = i;
                            if (consecCh.Type == ConsecutiveTrendType.Bullish && !consecCh.HasTickBreakthrough)
                            {
                                consecCh.HasTickBreakthrough = true;
                                consecCh.TickBreakthroughBarIndex = tickBarIndices[i];
                                consecCh.TickBreakthroughPrice = t.Price;
                                consecCh.TickBreakthroughTime = t.Time;
                                consecCh.TickBreakthroughTickIndex = i;
                                consecCh.TickBreakthroughType = "⚡上涨跌破下轨";
                            }
                        }
                        double diff = yLow - (double)t.Price;
                        if (diff > maxLowerDiff) { maxLowerDiff = diff; maxLowerPierceIdx = i; }
                        rel = "⚡破绿色下轨";
                    }
                    else
                    {
                        rel = "绿色通道内";
                    }
                }
                else if (!brokeRet && hasDynamicChannel)
                {
                    double yUp = chSlope * contX + chUpB;
                    double yLow = chSlope * contX + chLowB;

                    if ((double)t.Price > yUp)
                    {
                        countAboveUpper++;
                        if (firstUpperIdx < 0) firstUpperIdx = i;
                        double diff = (double)t.Price - yUp;
                        if (diff > maxUpperDiff) { maxUpperDiff = diff; maxUpperPierceIdx = i; }
                        rel = "⚡破通道上轨";
                    }
                    else if ((double)t.Price < yLow)
                    {
                        countBelowLower++;
                        if (firstLowerIdx < 0) firstLowerIdx = i;
                        double diff = yLow - (double)t.Price;
                        if (diff > maxLowerDiff) { maxLowerDiff = diff; maxLowerPierceIdx = i; }
                        rel = "⚡破通道下轨";
                    }
                    else
                    {
                        rel = "通道内";
                    }
                }

                tickChannelRelations[i] = rel;
            }

            // 更新状态栏文字摘要
            string chSummary = "";
            if (hasConsecChannel && consecCh != null)
            {
                double yUpEnd = consecSlope * (endIndex + 0.5) + consecUpB;
                double yLowEnd = consecSlope * (endIndex + 0.5) + consecLowB;
                string modeDesc = consecCh.PriceMode == ConsecutiveChannelPriceMode.Close ? "Close窄通道" : "HighLow宽通道";
                chSummary += $" | 🟩绿色通道({modeDesc}): 上轨{yUpEnd:F2} 下轨{yLowEnd:F2} (高度:{consecCh.ChannelHeight:F2})";
                if (countAboveUpper > 0) chSummary += $" (破上轨:{countAboveUpper}笔)";
                if (countBelowLower > 0) chSummary += $" (破下轨:{countBelowLower}笔)";
                if (consecCh.Type == ConsecutiveTrendType.Bullish && firstLowerIdx >= 0)
                {
                    chSummary += $" | 🚨【微观跌破】Tick #{firstLowerIdx + 1} 跌破下轨 (${ticks[firstLowerIdx].Price:F2})";
                }
                else if (consecCh.Type == ConsecutiveTrendType.Bearish && firstUpperIdx >= 0)
                {
                    chSummary += $" | 🚨【微观突破】Tick #{firstUpperIdx + 1} 突破上轨 (${ticks[firstUpperIdx].Price:F2})";
                }
            }
            else if (hasDynamicChannel)
            {
                double yUpEnd = chSlope * (endIndex + 0.5) + chUpB;
                double yLowEnd = chSlope * (endIndex + 0.5) + chLowB;
                chSummary += $" | 📐通道穿过: 上轨{yUpEnd:F2} 下轨{yLowEnd:F2}";
                if (countAboveUpper > 0) chSummary += $" (破上轨:{countAboveUpper}笔)";
                if (countBelowLower > 0) chSummary += $" (破下轨:{countBelowLower}笔)";
            }
            if (hasRetainedChannel && retCh != null)
            {
                double retEnd = (double)retCh.GetBreakoutBoundaryPrice(endIndex + 0.5);
                chSummary += $" | 🔒保留线: {retEnd:F2}";
                if (countRetBreakout > 0) chSummary += $" (突破:{countRetBreakout}笔🚀)";
            }

            // 驱动连续 5 根小周期观察期反转做单推演
            if (_engine.EnableReversalOrder && ticks != null && ticks.Length > 0 && !_isEvaluatingReversalFromTicks)
            {
                try
                {
                    _isEvaluatingReversalFromTicks = true;
                    var trends = _engine.ConsecutiveTrendDetector.DetectedTrends;
                    bool hasNewSignals = false;
                    for (int t = 0; t < trends.Count; t++)
                    {
                        var tr = trends[t];
                        if (tr.ConfirmedBarIndex <= _currentBarIndex)
                        {
                            long trStartT = allKlines[tr.StartIndex].OpenTime;
                            long trEndBarT = allKlines[Math.Min(tr.EndIndex, allKlines.Count - 1)].CloseTime;
                            long obsReachT = trEndBarT + (long)_engine.ReversalOrderEngine.ObservationMinutes * 60_000L * 15;
                            long selStartT = klineStart.OpenTime;
                            long selEndT = klineEnd.CloseTime;

                            if (selEndT >= trStartT && selStartT <= obsReachT)
                            {
                                var sigs = _engine.ReversalOrderEngine.EvaluateObservationCycles(tr, allKlines, ticks, Math.Min(_currentBarIndex, endIndex));
                                if (sigs.Count > 0) hasNewSignals = true;
                            }
                        }
                    }
                    if (hasNewSignals)
                    {
                        RenderPlot();
                    }
                }
                finally
                {
                    _isEvaluatingReversalFromTicks = false;
                }
            }

            // 提取当前可见 Tick 窗口覆盖的所有反转观察周期
            var relevantCycles = new List<ReversalObservationCycle>();
            if (_engine.EnableReversalOrder && _engine.ShowObservationCycles && totalTicks > 0)
            {
                long minT = ticks[0].Time;
                long maxT = ticks[totalTicks - 1].Time;

                var allCycles = _engine.ReversalOrderEngine.AllCycles;
                for (int c = 0; c < allCycles.Count; c++)
                {
                    var cyc = allCycles[c];
                    if (cyc.StartTime <= maxT && cyc.EndTime >= minT)
                    {
                        relevantCycles.Add(cyc);
                    }
                }
            }

            string obsSummary = "";
            if (relevantCycles.Count > 0)
            {
                obsSummary = $" | ⏱️观察周期: 共{relevantCycles.Count}期 (" +
                    string.Join(", ", relevantCycles.Select(c => $"C{c.CycleIndex}:" + (c.IsSignalTriggered ? "⚡已做单" : (c.IsCompleted ? "推演完" : "观察中")))) +
                    ")";
            }

            if (_tickPeriodMode == 0)
            {
                UpdateTickPeriodTabs(0, totalTicks, "原始Tick");
                lblPeriodSummary.Text = $"当前为原始逐笔 Tick (共 {totalTicks:N0} 笔)";

                if (hasConsecChannel && consecCh != null)
                {
                    string obsTag = relevantCycles.Count > 0 ? $" [⏱️观察期: {relevantCycles.Count}期]" : "";
                    lblTickTitle.Text = $"🟩 [连续走势绿色平行通道] #{consecCh.StartIndex}~#{consecCh.EndIndex} ({consecCh.TypeName}) 详情与逐笔 Tick{obsTag}";
                    lblTickTitle.ForeColor = Color.FromArgb(74, 222, 128);

                    lblTickInfo.Text = $"【{consecCh.TypeName}】Bar #{consecCh.StartIndex}~#{consecCh.EndIndex} (共{barCount}根, 确立于#{consecCh.ConfirmedBarIndex}) | " +
                                       $"时间: {kOpenTime:yyyy-MM-dd HH:mm:ss} ~ {kCloseTime:HH:mm:ss} | " +
                                       $"涨跌: {consecCh.PriceChangePct:+0.00;-0.00}% ({consecCh.StartPrice:F2} -> {consecCh.EndPrice:F2}) | " +
                                       $"斜率k: {consecCh.SlopeK:F4} | 高度: {consecCh.ChannelHeight:F2} USDT | " +
                                       $"Tick总量: {totalTicks:N0}笔 (主买:{takerBuyRatio:F1}%)" + chSummary + obsSummary;
                }
                else
                {
                    string obsTag = relevantCycles.Count > 0 ? $" [⏱️包含 {relevantCycles.Count} 个观察期]" : "";
                    lblTickTitle.Text = $"🎯 {barRangeStr} 微观逐笔成交明细{obsTag}";
                    lblTickTitle.ForeColor = Color.FromArgb(56, 189, 248);
                    lblTickInfo.Text = $"{barRangeStr} ({dirStr}) | {kOpenTime:HH:mm:ss} ~ {kCloseTime:HH:mm:ss} | {totalTicks:N0} 笔 Tick | 范围: {overallLow:F2} ~ {overallHigh:F2} | 主买: {takerBuyRatio:F1}%" + chSummary + obsSummary;
                }

            // 1. 渲染微观 Tick 分时走势图 (ScottPlot 5)
            formsPlotTick.Plot.Clear();
            formsPlotTick.Plot.FigureBackground.Color = ScottPlot.Color.FromHex("#0f172a");
            formsPlotTick.Plot.DataBackground.Color = ScottPlot.Color.FromHex("#1e293b");
            formsPlotTick.Plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#334155").WithAlpha(0.6);

            formsPlotTick.Plot.Axes.Left.TickLabelStyle.ForeColor = ScottPlot.Color.FromHex("#94a3b8");
            formsPlotTick.Plot.Axes.Bottom.TickLabelStyle.ForeColor = ScottPlot.Color.FromHex("#94a3b8");
            formsPlotTick.Plot.Axes.Left.FrameLineStyle.Color = ScottPlot.Color.FromHex("#475569");
            formsPlotTick.Plot.Axes.Bottom.FrameLineStyle.Color = ScottPlot.Color.FromHex("#475569");

            string fontName = PlotHelper.GetInstalledChineseFont();
            string chartTitle = hasConsecChannel
                ? $"🟩 [绿色通道 #{consecCh!.StartIndex}~#{consecCh.EndIndex}] 内部 Tick 走势与通道穿过 ({totalTicks:N0} 笔, {kOpenTime:HH:mm:ss} ~ {kCloseTime:HH:mm:ss})"
                : $"{barRangeStr} 内部 Tick 走势与通道穿过 ({totalTicks:N0} 笔, {kOpenTime:HH:mm:ss} ~ {kCloseTime:HH:mm:ss})";

            formsPlotTick.Plot.Title(chartTitle, 10.5f);
            formsPlotTick.Plot.Axes.Title.Label.FontName = fontName;
            formsPlotTick.Plot.Axes.Title.Label.ForeColor = hasConsecChannel ? ScottPlot.Color.FromHex("#10b981") : ScottPlot.Color.FromHex("#38bdf8");

            double[] xs = new double[totalTicks];
            double[] ys = new double[totalTicks];
            double maxVol = 0;
            int maxIdx = 0, minIdx = 0;
            decimal maxVal = decimal.MinValue, minVal = decimal.MaxValue;

            for (int i = 0; i < totalTicks; i++)
            {
                xs[i] = i;
                ys[i] = (double)ticks[i].Price;

                if (ticks[i].Price > maxVal) { maxVal = ticks[i].Price; maxIdx = i; }
                if (ticks[i].Price < minVal) { minVal = ticks[i].Price; minIdx = i; }

                double v = (double)ticks[i].Qty;
                if (v > maxVol) maxVol = v;
            }

            // 成交量柱状图
            int maxVolBins = 200;
            var volBars = new List<ScottPlot.Bar>(Math.Min(totalTicks, maxVolBins));

            if (totalTicks <= maxVolBins)
            {
                for (int i = 0; i < totalTicks; i++)
                {
                    double v = (double)ticks[i].Qty;
                    bool isBuyer = !ticks[i].IsBuyerMaker;
                    var vCol = isBuyer ? ScottPlot.Color.FromHex("#22c55e").WithAlpha(0.45) : ScottPlot.Color.FromHex("#ef4444").WithAlpha(0.45);

                    volBars.Add(new ScottPlot.Bar
                    {
                        Position = i,
                        Value = v,
                        ValueBase = 0,
                        Size = 0.8,
                        FillColor = vCol,
                        LineWidth = 0
                    });
                }
            }
            else
            {
                double binSize = (double)totalTicks / maxVolBins;
                for (int bin = 0; bin < maxVolBins; bin++)
                {
                    int startT = (int)(bin * binSize);
                    int endT = Math.Min(totalTicks - 1, (int)((bin + 1) * binSize));
                    if (startT > endT) continue;

                    double binVol = 0;
                    double binBuyVol = 0;
                    double binSellVol = 0;

                    for (int t = startT; t <= endT; t++)
                    {
                        double q = (double)ticks[t].Qty;
                        binVol += q;
                        if (!ticks[t].IsBuyerMaker) binBuyVol += q;
                        else binSellVol += q;
                    }

                    double binPos = (startT + endT) / 2.0;
                    bool isBuyer = binBuyVol >= binSellVol;
                    var vCol = isBuyer ? ScottPlot.Color.FromHex("#22c55e").WithAlpha(0.45) : ScottPlot.Color.FromHex("#ef4444").WithAlpha(0.45);

                    volBars.Add(new ScottPlot.Bar
                    {
                        Position = binPos,
                        Value = binVol,
                        ValueBase = 0,
                        Size = binSize * 0.85,
                        FillColor = vCol,
                        LineWidth = 0
                    });
                }
            }

            if (volBars.Count > 0)
            {
                var vPlot = formsPlotTick.Plot.Add.Bars(volBars);
                vPlot.Axes.YAxis = formsPlotTick.Plot.Axes.Right;
                formsPlotTick.Plot.Axes.Right.TickLabelStyle.ForeColor = ScottPlot.Color.FromHex("#64748b");
                formsPlotTick.Plot.Axes.Right.FrameLineStyle.Color = ScottPlot.Color.FromHex("#334155");
                if (maxVol <= 0) maxVol = 1;
                formsPlotTick.Plot.Axes.SetLimitsY(0, maxVol * 4.0, formsPlotTick.Plot.Axes.Right);
            }

            // 绘制绿色平行通道 (若当前选中或被绿色通道覆盖)
            double chUpStart = 0, chUpEnd2 = 0, chLowStart = 0, chLowEnd2 = 0;
            if (hasConsecChannel && consecCh != null)
            {
                double x0 = startIndex - 0.5;
                double x1 = endIndex + 0.5;
                chUpStart = consecSlope * x0 + consecUpB;
                chUpEnd2 = consecSlope * x1 + consecUpB;
                chLowStart = consecSlope * x0 + consecLowB;
                chLowEnd2 = consecSlope * x1 + consecLowB;
                double chMidStart = consecSlope * x0 + consecMidB;
                double chMidEnd2 = consecSlope * x1 + consecMidB;

                var greenColor = ScottPlot.Color.FromHex("#10b981");

                // 通道半透明微光填充带
                var shadePts = new Coordinates[]
                {
                    new Coordinates(0, chLowStart),
                    new Coordinates(totalTicks - 1, chLowEnd2),
                    new Coordinates(totalTicks - 1, chUpEnd2),
                    new Coordinates(0, chUpStart)
                };
                var chPoly = formsPlotTick.Plot.Add.Polygon(shadePts);
                chPoly.FillColor = greenColor.WithAlpha(22);
                chPoly.LineWidth = 0;

                // 通道上轨
                var lUp = formsPlotTick.Plot.Add.Line(0, chUpStart, totalTicks - 1, chUpEnd2);
                lUp.Color = greenColor;
                lUp.LineWidth = 1.4f;
                lUp.LinePattern = LinePattern.Solid;
                lUp.LegendText = $"绿色通道上轨 ({chUpEnd2:F2})";

                // 通道下轨
                var lLow = formsPlotTick.Plot.Add.Line(0, chLowStart, totalTicks - 1, chLowEnd2);
                lLow.Color = greenColor;
                lLow.LineWidth = 1.4f;
                lLow.LinePattern = LinePattern.Solid;
                lLow.LegendText = $"绿色通道下轨 ({chLowEnd2:F2})";

                // 通道中轨
                var lMid = formsPlotTick.Plot.Add.Line(0, chMidStart, totalTicks - 1, chMidEnd2);
                lMid.Color = greenColor.WithAlpha(160);
                lMid.LineWidth = 0.8f;
                lMid.LinePattern = LinePattern.Dashed;
                lMid.LegendText = $"绿色通道中轨 ({chMidEnd2:F2})";

                // 若有穿透上轨，标注最大穿透点
                if (maxUpperPierceIdx >= 0)
                {
                    var mUp = formsPlotTick.Plot.Add.Marker(maxUpperPierceIdx, (double)ticks[maxUpperPierceIdx].Price);
                    mUp.Shape = MarkerShape.FilledTriangleUp;
                    mUp.Size = 10;
                    mUp.Color = ScottPlot.Color.FromHex("#fbbf24");
                    mUp.LegendText = $"穿透绿色上轨 #{maxUpperPierceIdx + 1} (+{maxUpperDiff:F2})";
                }

                // 若有穿透下轨，标注最大穿透点
                if (maxLowerPierceIdx >= 0)
                {
                    var mLowP = formsPlotTick.Plot.Add.Marker(maxLowerPierceIdx, (double)ticks[maxLowerPierceIdx].Price);
                    mLowP.Shape = MarkerShape.FilledTriangleDown;
                    mLowP.Size = 10;
                    mLowP.Color = ScottPlot.Color.FromHex("#f43f5e");
                    mLowP.LegendText = $"穿透绿色下轨 #{maxLowerPierceIdx + 1} (-{maxLowerDiff:F2})";
                }

                // 重点突破/跌破标记：上涨跌破下轨 / 下跌突破上轨 (双图联动标记)
                if (consecCh.Type == ConsecutiveTrendType.Bullish && firstLowerIdx >= 0)
                {
                    var mBreak = formsPlotTick.Plot.Add.Marker(firstLowerIdx, (double)ticks![firstLowerIdx].Price);
                    mBreak.Shape = MarkerShape.FilledTriangleDown;
                    mBreak.Size = 14;
                    mBreak.Color = ScottPlot.Color.FromHex("#f43f5e");
                    mBreak.LegendText = $"⚡首次跌破下轨 #{firstLowerIdx + 1} ({ticks[firstLowerIdx].Price:F2})";

                    var tBreak = formsPlotTick.Plot.Add.Text($"⚡跌破下轨 @ {ticks[firstLowerIdx].Price:F2} (Tick #{firstLowerIdx + 1})", firstLowerIdx, (double)ticks[firstLowerIdx].Price);
                    tBreak.LabelFontColor = ScottPlot.Color.FromHex("#f43f5e");
                    tBreak.LabelFontSize = 10f;
                    tBreak.LabelBold = true;
                    tBreak.Alignment = Alignment.UpperCenter;
                }
                else if (consecCh.Type == ConsecutiveTrendType.Bearish && firstUpperIdx >= 0)
                {
                    var mBreak = formsPlotTick.Plot.Add.Marker(firstUpperIdx, (double)ticks![firstUpperIdx].Price);
                    mBreak.Shape = MarkerShape.FilledTriangleUp;
                    mBreak.Size = 14;
                    mBreak.Color = ScottPlot.Color.FromHex("#fbbf24");
                    mBreak.LegendText = $"⚡首次突破上轨 #{firstUpperIdx + 1} ({ticks[firstUpperIdx].Price:F2})";

                    var tBreak = formsPlotTick.Plot.Add.Text($"⚡突破上轨 @ {ticks[firstUpperIdx].Price:F2} (Tick #{firstUpperIdx + 1})", firstUpperIdx, (double)ticks[firstUpperIdx].Price);
                    tBreak.LabelFontColor = ScottPlot.Color.FromHex("#fbbf24");
                    tBreak.LabelFontSize = 10f;
                    tBreak.LabelBold = true;
                    tBreak.Alignment = Alignment.LowerCenter;
                }
            }
            else if (hasDynamicChannel)
            {
                double x0 = startIndex - 0.5;
                double x1 = endIndex + 0.5;
                chUpStart = chSlope * x0 + chUpB;
                chUpEnd2 = chSlope * x1 + chUpB;
                chLowStart = chSlope * x0 + chLowB;
                chLowEnd2 = chSlope * x1 + chLowB;
                double chMidStart = chSlope * x0 + chMidB;
                double chMidEnd2 = chSlope * x1 + chMidB;

                // 通道半透明填充带
                var shadePts = new Coordinates[]
                {
                    new Coordinates(0, chLowStart),
                    new Coordinates(totalTicks - 1, chLowEnd2),
                    new Coordinates(totalTicks - 1, chUpEnd2),
                    new Coordinates(0, chUpStart)
                };
                var chPoly = formsPlotTick.Plot.Add.Polygon(shadePts);
                chPoly.FillColor = ScottPlot.Color.FromHex("#38bdf8").WithAlpha(18);
                chPoly.LineWidth = 0;

                // 通道上轨
                var lUp = formsPlotTick.Plot.Add.Line(0, chUpStart, totalTicks - 1, chUpEnd2);
                lUp.Color = ScottPlot.Color.FromHex("#38bdf8"); // Sky 400
                lUp.LineWidth = 1.8f;
                lUp.LinePattern = LinePattern.Dashed;
                lUp.LegendText = $"通道上轨 ({chUpEnd2:F2})";

                // 通道下轨
                var lLow = formsPlotTick.Plot.Add.Line(0, chLowStart, totalTicks - 1, chLowEnd2);
                lLow.Color = ScottPlot.Color.FromHex("#7dd3fc"); // Sky 300
                lLow.LineWidth = 1.8f;
                lLow.LinePattern = LinePattern.Dashed;
                lLow.LegendText = $"通道下轨 ({chLowEnd2:F2})";

                // 通道中轨
                var lMid = formsPlotTick.Plot.Add.Line(0, chMidStart, totalTicks - 1, chMidEnd2);
                lMid.Color = ScottPlot.Color.FromHex("#94a3b8"); // Slate 400
                lMid.LineWidth = 1.0f;
                lMid.LinePattern = LinePattern.Dotted;
                lMid.LegendText = $"通道中轨 ({chMidEnd2:F2})";

                // 若有穿透上轨，标注最大穿透点
                if (maxUpperPierceIdx >= 0)
                {
                    var mUp = formsPlotTick.Plot.Add.Marker(maxUpperPierceIdx, (double)ticks[maxUpperPierceIdx].Price);
                    mUp.Shape = MarkerShape.FilledTriangleUp;
                    mUp.Size = 10;
                    mUp.Color = ScottPlot.Color.FromHex("#fbbf24"); // Amber 400
                    mUp.LegendText = $"穿透上轨 #{maxUpperPierceIdx + 1} (+{maxUpperDiff:F2})";
                }

                // 若有穿透下轨，标注最大穿透点
                if (maxLowerPierceIdx >= 0)
                {
                    var mLowP = formsPlotTick.Plot.Add.Marker(maxLowerPierceIdx, (double)ticks[maxLowerPierceIdx].Price);
                    mLowP.Shape = MarkerShape.FilledTriangleDown;
                    mLowP.Size = 10;
                    mLowP.Color = ScottPlot.Color.FromHex("#f43f5e"); // Rose 500
                    mLowP.LegendText = $"穿透下轨 #{maxLowerPierceIdx + 1} (-{maxLowerDiff:F2})";
                }
            }

            // 绘制保留通道关键线 (如果有保留通道穿过)
            double retStart = 0, retEnd2 = 0;
            if (hasRetainedChannel && retCh != null)
            {
                int totalSpan = Math.Max(retCh.BaseChannel.LeftLength, _currentBarIndex - retCh.BaseChannel.StartX + 1);
                bool isSpecialRet = (chkSpecialRetained?.Checked ?? true) &&
                    (retCh.IsSpecialStrongTrend || (totalSpan >= _engine.SpecialRetainedMinBars && Math.Abs(retCh.BaseChannel.AngleDeg) >= _engine.SpecialRetainedMinAngle));

                double x0 = startIndex - 0.5;
                double x1 = endIndex + 0.5;
                retStart = (double)retCh.GetBreakoutBoundaryPrice(x0);
                retEnd2 = (double)retCh.GetBreakoutBoundaryPrice(x1);

                var lRet = formsPlotTick.Plot.Add.Line(0, retStart, totalTicks - 1, retEnd2);
                lRet.Color = isSpecialRet
                    ? ScottPlot.Color.FromHex("#a855f7")
                    : (retCh.IsDownward ? ScottPlot.Color.FromHex("#fbbf24") : ScottPlot.Color.FromHex("#06b6d4"));
                lRet.LineWidth = isSpecialRet ? 0.8f : 2.2f;
                lRet.LinePattern = LinePattern.Dashed;
                lRet.LegendText = isSpecialRet
                    ? $"[保留-0.8f紫色] {(retCh.IsDownward ? "阻力突破线" : "支撑跌破线")} ({retEnd2:F2})"
                    : $"[保留] {(retCh.IsDownward ? "阻力突破线" : "支撑跌破线")} ({retEnd2:F2})";

                if (firstRetIdx >= 0)
                {
                    var mRet = formsPlotTick.Plot.Add.Marker(firstRetIdx, (double)ticks[firstRetIdx].Price);
                    mRet.Shape = MarkerShape.FilledDiamond;
                    mRet.Size = 12;
                    mRet.Color = retCh.IsDownward ? ScottPlot.Color.FromHex("#22c55e") : ScottPlot.Color.FromHex("#ef4444");
                    mRet.LegendText = $"[保留突破] #{firstRetIdx + 1}";
                }
            }

            // 3. 在 Tick 分时图上精细划分各根 K 线的时域区间 (垂直分割虚线 + 顶部时间标签 + 斑马纹背景分区)
            if (barCount > 1)
            {
                var barTickStarts = new int[barCount];
                var barTickEnds = new int[barCount];
                int scanT = 0;
                for (int b = 0; b < barCount; b++)
                {
                    int bIdx = startIndex + b;
                    long bOpen = allKlines[bIdx].OpenTime;

                    while (scanT < totalTicks && ticks[scanT].Time < bOpen)
                    {
                        scanT++;
                    }
                    barTickStarts[b] = Math.Min(scanT, totalTicks - 1);
                }
                for (int b = 0; b < barCount; b++)
                {
                    barTickEnds[b] = (b < barCount - 1) ? Math.Max(barTickStarts[b], barTickStarts[b + 1] - 1) : totalTicks - 1;
                }

                double labelY = (double)overallHigh + Math.Max(1.0, (double)(overallHigh - overallLow) * 0.10);

                for (int b = 0; b < barCount; b++)
                {
                    int barIdx = startIndex + b;
                    int sT = barTickStarts[b];
                    int eT = barTickEnds[b];
                    if (eT < sT) continue;

                    // ① 奇数 K 线交替斑马纹背景分区
                    if (b % 2 == 1)
                    {
                        var zebraCoords = new Coordinates[]
                        {
                            new Coordinates(sT, (double)overallLow - 1000),
                            new Coordinates(eT, (double)overallLow - 1000),
                            new Coordinates(eT, (double)overallHigh + 1000),
                            new Coordinates(sT, (double)overallHigh + 1000)
                        };
                        var zebraPoly = formsPlotTick.Plot.Add.Polygon(zebraCoords);
                        zebraPoly.FillColor = ScottPlot.Color.FromHex("#38bdf8").WithAlpha(12); // 极轻微天蓝底透
                        zebraPoly.LineWidth = 0;
                    }

                    // ② 垂直分割虚线 (自第 1 根开始绘制)
                    if (b > 0)
                    {
                        var bSep = formsPlotTick.Plot.Add.VerticalLine(sT);
                        bSep.Color = ScottPlot.Color.FromHex("#38bdf8").WithAlpha(160);
                        bSep.LineWidth = 1.2f;
                        bSep.LinePattern = LinePattern.Dashed;
                    }

                    // ③ 顶部 K 线编号与开盘时间标签
                    double midTick = (sT + eT) / 2.0;
                    DateTime bTime = TimeHelper.FromUnixTimeMilliseconds(allKlines[barIdx].OpenTime);
                    string timeTag = $"Bar #{barIdx}\n{bTime:HH:mm}";
                    var txtTag = formsPlotTick.Plot.Add.Text(timeTag, midTick, labelY);
                    txtTag.LabelFontColor = (b % 2 == 0) ? ScottPlot.Color.FromHex("#38bdf8") : ScottPlot.Color.FromHex("#a78bfa");
                    txtTag.LabelFontSize = 8.5f;
                    txtTag.LabelBold = true;
                    txtTag.Alignment = Alignment.UpperCenter;
                }
            }

            // 4. 绘制反转策略观察周期半透明背景底色带 (置于价格折线之下)
            if (relevantCycles.Count > 0)
            {
                for (int c = 0; c < relevantCycles.Count; c++)
                {
                    var cyc = relevantCycles[c];
                    int sTick = -1, eTick = -1;
                    for (int i = 0; i < totalTicks; i++)
                    {
                        if (sTick < 0 && ticks[i].Time >= cyc.StartTime) sTick = i;
                        if (ticks[i].Time <= cyc.EndTime) eTick = i;
                    }
                    if (sTick < 0 && ticks[0].Time >= cyc.StartTime) sTick = 0;
                    if (eTick < 0 && ticks[totalTicks - 1].Time <= cyc.EndTime) eTick = totalTicks - 1;
                    if (sTick < 0) sTick = 0;
                    if (eTick < sTick) eTick = sTick;

                    bool isBullish = cyc.PriorTrendType == ConsecutiveTrendType.Bullish;
                    var bandColor = isBullish ? ScottPlot.Color.FromHex("#f59e0b") : ScottPlot.Color.FromHex("#06b6d4");
                    byte alphaVal = cyc.IsSignalTriggered ? (byte)32 : (byte)16;

                    var shadeCoords = new Coordinates[]
                    {
                        new Coordinates(sTick, (double)overallLow - 2000),
                        new Coordinates(eTick, (double)overallLow - 2000),
                        new Coordinates(eTick, (double)overallHigh + 2000),
                        new Coordinates(sTick, (double)overallHigh + 2000)
                    };
                    var cPoly = formsPlotTick.Plot.Add.Polygon(shadeCoords);
                    cPoly.FillColor = bandColor.WithAlpha(alphaVal);
                    cPoly.LineWidth = 0;
                }
            }

            // 绘制微观 Tick 价格折线
            var scatter = formsPlotTick.Plot.Add.ScatterLine(xs, ys);
            scatter.Color = klineEnd.Close >= klineStart.Open ? ScottPlot.Color.FromHex("#22c55e") : ScottPlot.Color.FromHex("#ef4444");
            scatter.LineWidth = 1.4f;
            scatter.MarkerSize = 0;

            // 标注高低极值与起止点标记
            var mHigh = formsPlotTick.Plot.Add.Marker(maxIdx, (double)maxVal);
            mHigh.Shape = MarkerShape.FilledTriangleUp;
            mHigh.Size = 9;
            mHigh.Color = ScottPlot.Color.FromHex("#ef4444");

            var mLow = formsPlotTick.Plot.Add.Marker(minIdx, (double)minVal);
            mLow.Shape = MarkerShape.FilledTriangleDown;
            mLow.Size = 9;
            mLow.Color = ScottPlot.Color.FromHex("#22c55e");

            var mOpen = formsPlotTick.Plot.Add.Marker(0, (double)ticks[0].Price);
            mOpen.Shape = MarkerShape.FilledCircle;
            mOpen.Size = 7;
            mOpen.Color = ScottPlot.Color.FromHex("#38bdf8");

            var mClose = formsPlotTick.Plot.Add.Marker(totalTicks - 1, (double)ticks[totalTicks - 1].Price);
            mClose.Shape = MarkerShape.FilledSquare;
            mClose.Size = 7;
            mClose.Color = ScottPlot.Color.FromHex("#f97316");

            // 5. 绘制反转策略观察周期的垂直边界线、顶部跨度标尺与状态徽标
            if (relevantCycles.Count > 0)
            {
                double spanH = (double)(overallHigh - overallLow);
                if (spanH <= 0) spanH = 10.0;
                double obsBracketY = barCount > 1
                    ? (double)overallHigh + spanH * 0.16
                    : (double)overallHigh + spanH * 0.08;
                double dropTick = Math.Max(0.2, spanH * 0.025);

                for (int c = 0; c < relevantCycles.Count; c++)
                {
                    var cyc = relevantCycles[c];
                    int sTick = -1, eTick = -1;
                    for (int i = 0; i < totalTicks; i++)
                    {
                        if (sTick < 0 && ticks[i].Time >= cyc.StartTime) sTick = i;
                        if (ticks[i].Time <= cyc.EndTime) eTick = i;
                    }
                    if (sTick < 0 && ticks[0].Time >= cyc.StartTime) sTick = 0;
                    if (eTick < 0 && ticks[totalTicks - 1].Time <= cyc.EndTime) eTick = totalTicks - 1;
                    if (sTick < 0) sTick = 0;
                    if (eTick < sTick) eTick = sTick;

                    bool isBullish = cyc.PriorTrendType == ConsecutiveTrendType.Bullish;
                    var bandColor = isBullish ? ScottPlot.Color.FromHex("#f59e0b") : ScottPlot.Color.FromHex("#06b6d4");

                    // 起始垂直虚线
                    var vLineStart = formsPlotTick.Plot.Add.VerticalLine(sTick);
                    vLineStart.Color = bandColor.WithAlpha(210);
                    vLineStart.LineWidth = 1.4f;
                    vLineStart.LinePattern = LinePattern.Dashed;

                    // 结束垂直虚线 (若未贴最右边缘)
                    if (eTick < totalTicks - 1)
                    {
                        var vLineEnd = formsPlotTick.Plot.Add.VerticalLine(eTick);
                        vLineEnd.Color = bandColor.WithAlpha(150);
                        vLineEnd.LineWidth = 1.1f;
                        vLineEnd.LinePattern = LinePattern.Dotted;
                    }

                    // 顶部横向跨度标尺指示线
                    var hLine = formsPlotTick.Plot.Add.Line(sTick, obsBracketY, eTick, obsBracketY);
                    hLine.Color = cyc.IsSignalTriggered ? ScottPlot.Color.FromHex("#facc15") : bandColor;
                    hLine.LineWidth = 1.8f;
                    hLine.LinePattern = LinePattern.Solid;
                    if (c == 0) hLine.LegendText = $"⏱️ 观察周期 ({_engine.ReversalObservationMinutes}分钟)";

                    // 左右向下小刻度
                    var endDropL = formsPlotTick.Plot.Add.Line(sTick, obsBracketY, sTick, obsBracketY - dropTick);
                    endDropL.Color = hLine.Color; endDropL.LineWidth = 1.8f;
                    var endDropR = formsPlotTick.Plot.Add.Line(eTick, obsBracketY, eTick, obsBracketY - dropTick);
                    endDropR.Color = hLine.Color; endDropR.LineWidth = 1.8f;

                    // 顶部徽标文字
                    double midX = (sTick + eTick) / 2.0;
                    DateTime dtStart = TimeHelper.FromUnixTimeMilliseconds(cyc.StartTime).ToLocalTime();
                    DateTime dtEnd = TimeHelper.FromUnixTimeMilliseconds(cyc.EndTime).ToLocalTime();
                    string timeStr = $"{dtStart:HH:mm:ss}~{dtEnd:HH:mm:ss}";

                    string badgeText;
                    ScottPlot.Color badgeColor;

                    if (cyc.IsSignalTriggered && cyc.Signal != null)
                    {
                        string sigDirName = cyc.Signal.IsBreakoutTrendFollowing
                            ? (cyc.Signal.Direction == OrderSignalDirection.Sell ? "顺势高空" : "顺势低多")
                            : (cyc.Signal.Direction == OrderSignalDirection.Sell ? "高点做空" : "低点做多");
                        badgeText = $"⏱️ 第{cyc.CycleIndex}观察期 ({_engine.ReversalObservationMinutes}m) ⚡【{sigDirName}】\n{timeStr}";
                        badgeColor = ScottPlot.Color.FromHex("#facc15"); // 亮金色
                    }
                    else if (cyc.IsCompleted)
                    {
                        badgeText = $"⏱️ 第{cyc.CycleIndex}观察期 ({_engine.ReversalObservationMinutes}m) [推演完毕]\n{timeStr}";
                        badgeColor = isBullish ? ScottPlot.Color.FromHex("#fb923c") : ScottPlot.Color.FromHex("#38bdf8");
                    }
                    else
                    {
                        badgeText = $"⏱️ 第{cyc.CycleIndex}观察期 ({_engine.ReversalObservationMinutes}m) [推演中...]\n{timeStr}";
                        badgeColor = ScottPlot.Color.FromHex("#94a3b8");
                    }

                    var txtBadge = formsPlotTick.Plot.Add.Text(badgeText, midX, obsBracketY + dropTick * 0.6);
                    txtBadge.LabelFontColor = badgeColor;
                    txtBadge.LabelFontSize = 8.8f;
                    txtBadge.LabelBold = true;
                    txtBadge.Alignment = Alignment.LowerCenter;

                    if (obsBracketY + dropTick * 3.5 > (double)overallHigh)
                    {
                        overallHigh = (decimal)(obsBracketY + dropTick * 3.5);
                    }
                }
            }

            // 绘制反转做单短黄线标记 (如果命中反转做单信号)
            if (_engine.EnableReversalOrder && _engine.ShowReversalYellowLines)
            {
                var allSignals = _engine.ReversalOrderEngine.AllSignals;
                var yellowCol = ScottPlot.Color.FromHex("#facc15"); // 琥珀亮金黄色
                long minT = ticks[0].Time;
                long maxT = ticks[totalTicks - 1].Time;

                for (int sIdx = 0; sIdx < allSignals.Count; sIdx++)
                {
                    var sig = allSignals[sIdx];
                    bool isInScope = (sig.TriggerTime >= minT && sig.TriggerTime <= maxT) ||
                                     (sig.ObservationStartTime <= maxT && sig.ObservationEndTime >= minT) ||
                                     (sig.BigBarIndex >= startIndex && sig.BigBarIndex <= endIndex);

                    if (!isInScope) continue;

                    // 定位离触发时间最近的 Tick 点索引 (X 轴)
                    int targetTickIdx = -1;
                    long minDiff = long.MaxValue;
                    for (int ti = 0; ti < totalTicks; ti++)
                    {
                        long diff = Math.Abs(ticks[ti].Time - sig.TriggerTime);
                        if (diff < minDiff)
                        {
                            minDiff = diff;
                            targetTickIdx = ti;
                        }
                    }

                    if (targetTickIdx < 0) targetTickIdx = totalTicks / 2;

                    double y = (double)sig.Price;

                    // 短黄线：水平跨度自适应 (覆盖当前 Tick 总量的 3.5% 或至少 6 个 Tick 点)
                    double halfSpan = Math.Max(5.0, totalTicks * 0.035);
                    double x1 = Math.Max(0, targetTickIdx - halfSpan);
                    double x2 = Math.Min(totalTicks - 1, targetTickIdx + halfSpan);

                    var yLine = formsPlotTick.Plot.Add.Line(x1, y, x2, y);
                    yLine.Color = yellowCol;
                    yLine.LineWidth = 2.5f;
                    yLine.LinePattern = LinePattern.Solid;
                    if (sIdx == 0) yLine.LegendText = "⚡ 反转做单信号 (短黄线)";

                    // 两端微型圆点增加质感
                    var m1 = formsPlotTick.Plot.Add.Marker(x1, y);
                    m1.Shape = MarkerShape.FilledCircle;
                    m1.Size = 4;
                    m1.Color = yellowCol;

                    var m2 = formsPlotTick.Plot.Add.Marker(x2, y);
                    m2.Shape = MarkerShape.FilledCircle;
                    m2.Size = 4;
                    m2.Color = yellowCol;

                    // 信号方向三角箭头 (做空向下红色，做多向上绿色)
                    var arrow = formsPlotTick.Plot.Add.Marker(targetTickIdx, y);
                    arrow.Shape = sig.Direction == OrderSignalDirection.Sell ? MarkerShape.FilledTriangleDown : MarkerShape.FilledTriangleUp;
                    arrow.Size = 10;
                    arrow.Color = sig.Direction == OrderSignalDirection.Sell ? ScottPlot.Color.FromHex("#ef4444") : ScottPlot.Color.FromHex("#22c55e");

                    // 气泡文字标签
                    string sigDirStr = sig.IsBreakoutTrendFollowing
                        ? (sig.Direction == OrderSignalDirection.Sell ? "顺势高空" : "顺势低多")
                        : (sig.Direction == OrderSignalDirection.Sell ? "高点做空" : "低点做多");
                    string tickDetail = sig.IsTickStreamTriggered 
                        ? $" (极值:{sig.PeakTroughPrice:F2}, 回落:{sig.PullbackPct:F2}%, 止损:{sig.StopLossPrice:F2})" 
                        : $" (止损:{sig.StopLossPrice:F2})";
                    string labelText = $"⚡{sigDirStr} @ {sig.Price:F2}{tickDetail} [#{sig.Pattern.PatternId} C{sig.ObservationCycleIndex}]";
                    double spanEst = (double)(overallHigh - overallLow);
                    if (spanEst <= 0) spanEst = 10.0;
                    double tagY = sig.Direction == OrderSignalDirection.Sell ? y + spanEst * 0.06 : y - spanEst * 0.06;

                    var txtTag = formsPlotTick.Plot.Add.Text(labelText, targetTickIdx, tagY);
                    txtTag.LabelFontColor = yellowCol;
                    txtTag.LabelFontSize = 9.5f;
                    txtTag.LabelBold = true;
                    txtTag.Alignment = sig.Direction == OrderSignalDirection.Sell ? Alignment.LowerCenter : Alignment.UpperCenter;

                    // 若存在波峰/波谷，在波峰/波谷处打一个小三角标记辅助观察极值点
                    if (sig.IsTickStreamTriggered && sig.PeakTroughPrice > 0)
                    {
                        double pY = (double)sig.PeakTroughPrice;
                        int pIdx = -1;
                        for (int pi = 0; pi < totalTicks; pi++)
                        {
                            if (ticks[pi].Price == sig.PeakTroughPrice && Math.Abs(ticks[pi].Time - sig.TriggerTime) <= 180_000L)
                            {
                                pIdx = pi;
                                break;
                            }
                        }
                        if (pIdx >= 0)
                        {
                            var peakM = formsPlotTick.Plot.Add.Marker(pIdx, pY);
                            peakM.Shape = sig.Direction == OrderSignalDirection.Sell ? MarkerShape.FilledTriangleUp : MarkerShape.FilledTriangleDown;
                            peakM.Size = 8;
                            peakM.Color = yellowCol;
                            peakM.LegendText = sig.Direction == OrderSignalDirection.Sell ? $"波峰高点 ({pY:F2})" : $"波谷低点 ({pY:F2})";
                        }
                    }

                    // 更新包络范围，确保黄线和标签不被裁剪
                    if (y < (double)overallLow) overallLow = (decimal)y;
                    if (y > (double)overallHigh) overallHigh = (decimal)y;
                    if (tagY < (double)overallLow) overallLow = (decimal)tagY;
                    if (tagY > (double)overallHigh) overallHigh = (decimal)tagY;
                }
            }

            // 坐标轴限制：自适应计算并合理留白
            double yMin = (double)overallLow;
            double yMax = (double)overallHigh;
            double tickSpan = yMax - yMin;

            if (hasConsecChannel && consecCh != null)
            {
                double maxChUp = Math.Max(chUpStart, chUpEnd2);
                if (maxChUp - yMax <= Math.Max(15.0, tickSpan * 3.0)) yMax = Math.Max(yMax, maxChUp);

                double minChLow = Math.Min(chLowStart, chLowEnd2);
                if (yMin - minChLow <= Math.Max(15.0, tickSpan * 3.0)) yMin = Math.Min(yMin, minChLow);
            }
            else if (hasDynamicChannel)
            {
                double maxChUp = Math.Max(chUpStart, chUpEnd2);
                if (maxChUp - yMax <= Math.Max(15.0, tickSpan * 3.0)) yMax = Math.Max(yMax, maxChUp);

                double minChLow = Math.Min(chLowStart, chLowEnd2);
                if (yMin - minChLow <= Math.Max(15.0, tickSpan * 3.0)) yMin = Math.Min(yMin, minChLow);
            }

            if (hasRetainedChannel && retCh != null)
            {
                double maxRet = Math.Max(retStart, retEnd2);
                double minRet = Math.Min(retStart, retEnd2);
                if (maxRet - yMax <= Math.Max(15.0, tickSpan * 3.0)) yMax = Math.Max(yMax, maxRet);
                if (yMin - minRet <= Math.Max(15.0, tickSpan * 3.0)) yMin = Math.Min(yMin, minRet);
            }

            double yPad = Math.Max(0.5, (yMax - yMin) * 0.16);
            formsPlotTick.Plot.Axes.SetLimitsY(yMin - yPad * 0.5, yMax + yPad * 1.3);
            formsPlotTick.Plot.Axes.SetLimitsX(-totalTicks * 0.02, totalTicks * 1.02);

            // 显示图例
            formsPlotTick.Plot.ShowLegend(Alignment.UpperRight);
            formsPlotTick.Plot.Legend.FontName = fontName;
            formsPlotTick.Plot.Legend.FontSize = 8.5f;
            formsPlotTick.Plot.Legend.BackgroundColor = ScottPlot.Color.FromHex("#1e293b").WithAlpha(200);
            formsPlotTick.Plot.Legend.FontColor = ScottPlot.Color.FromHex("#f1f5f9");

            formsPlotTick.Refresh();
            }
            else
            {
                // === 合成 K 线模式 (1m, 5m, 15m, 自定义M分钟) ===
                int intervalMinutes = _tickPeriodMode switch
                {
                    1 => 1,
                    2 => 5,
                    3 => 15,
                    _ => Math.Max(1, (int)numCustomTickPeriod.Value)
                };
                string periodDesc = _tickPeriodMode switch
                {
                    1 => "1分钟",
                    2 => "5分钟",
                    3 => "15分钟",
                    _ => $"{intervalMinutes}分钟(自定义)"
                };

                var synthKlines = KlinePlaybackEngine.AggregateTicksToKlines(ticks, intervalMinutes);
                UpdateTickPeriodTabs(_tickPeriodMode, totalTicks, periodDesc);
                lblPeriodSummary.Text = $"已聚合生成 {synthKlines.Count} 根 {periodDesc} K线 (覆盖 {totalTicks:N0} 笔 Tick)";

                // 填充 _displayedKlines
                _displayedKlines.Clear();
                if (_displayedKlines.Capacity < synthKlines.Count)
                    _displayedKlines.Capacity = synthKlines.Count;

                for (int i = 0; i < synthKlines.Count; i++)
                {
                    var k = synthKlines[i];
                    DateTime sDt = TimeHelper.FromUnixTimeMilliseconds(k.OpenTime).ToLocalTime();
                    DateTime eDt = TimeHelper.FromUnixTimeMilliseconds(k.CloseTime).ToLocalTime();
                    string timeRange = intervalMinutes == 1 ? sDt.ToString("HH:mm") : $"{sDt:HH:mm}~{eDt:HH:mm}";
                    decimal chgPct = k.Open > 0 ? (k.Close - k.Open) / k.Open * 100m : 0m;
                    decimal ampPct = k.Low > 0 ? (k.High - k.Low) / k.Low * 100m : 0m;
                    decimal buyRatio = k.Volume > 0 ? (k.TakerBuyVolume / k.Volume * 100m) : 0m;

                    string chRel = "通道内";
                    if (hasConsecChannel)
                    {
                        double barCenterTime = (k.OpenTime + k.CloseTime) / 2.0;
                        double spanRatio = (spanMs > 0) ? (barCenterTime - tStart) / (double)spanMs : 0;
                        double barMainIdx = startIndex + spanRatio * barCount;
                        double upP = consecSlope * barMainIdx + consecUpB;
                        double lowP = consecSlope * barMainIdx + consecLowB;
                        if ((double)k.High > upP) chRel = "⚡破绿色上轨";
                        else if ((double)k.Low < lowP) chRel = "⚡破绿色下轨";
                        else chRel = "绿色通道内";
                    }

                    _displayedKlines.Add(new SynthesizedKlineViewModel
                    {
                        Index = i + 1,
                        OpenTime = k.OpenTime,
                        CloseTime = k.CloseTime,
                        TimeRangeStr = timeRange,
                        Open = k.Open,
                        High = k.High,
                        Low = k.Low,
                        Close = k.Close,
                        ChangePct = chgPct,
                        AmplitudePct = ampPct,
                        Volume = k.Volume,
                        QuoteVolume = k.QuoteVolume,
                        TradeCount = k.TradeCount,
                        TakerBuyVolume = k.TakerBuyVolume,
                        TakerBuyRatio = buyRatio,
                        ChannelRelation = chRel
                    });
                }

                if (hasConsecChannel && consecCh != null)
                {
                    lblTickTitle.Text = $"🟩 [连续走势绿色平行通道] #{consecCh.StartIndex}~#{consecCh.EndIndex} ({consecCh.TypeName}) 聚合 {periodDesc} K线 (共 {synthKlines.Count} 根)";
                    lblTickTitle.ForeColor = Color.FromArgb(74, 222, 128);
                    lblTickInfo.Text = $"【{consecCh.TypeName}】Bar #{consecCh.StartIndex}~#{consecCh.EndIndex} (共{barCount}根) | " +
                                       $"聚合周期: {periodDesc} (生成{synthKlines.Count}根K线) | " +
                                       $"时间: {kOpenTime:yyyy-MM-dd HH:mm:ss} ~ {kCloseTime:HH:mm:ss} | " +
                                       $"涨跌: {consecCh.PriceChangePct:+0.00;-0.00}% | " +
                                       $"Tick总量: {totalTicks:N0}笔 (主买:{takerBuyRatio:F1}%)" + chSummary;
                }
                else
                {
                    lblTickTitle.Text = $"🎯 {barRangeStr} 聚合 {periodDesc} K线明细 (共 {synthKlines.Count} 根)";
                    lblTickTitle.ForeColor = Color.FromArgb(56, 189, 248);
                    lblTickInfo.Text = $"{barRangeStr} ({dirStr}) | 聚合周期: {periodDesc} ({synthKlines.Count}根K线) | {kOpenTime:HH:mm:ss} ~ {kCloseTime:HH:mm:ss} | 覆盖 {totalTicks:N0} 笔 Tick | 均价: {(totalVol > 0 ? (totalQuote / totalVol) : 0):F2} | 主买: {takerBuyRatio:F1}%" + chSummary;
                }

                formsPlotTick.Plot.Clear();
                formsPlotTick.Plot.FigureBackground.Color = ScottPlot.Color.FromHex("#0f172a");
                formsPlotTick.Plot.DataBackground.Color = ScottPlot.Color.FromHex("#1e293b");
                formsPlotTick.Plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#334155").WithAlpha(0.6);

                formsPlotTick.Plot.Axes.Left.TickLabelStyle.ForeColor = ScottPlot.Color.FromHex("#94a3b8");
                formsPlotTick.Plot.Axes.Bottom.TickLabelStyle.ForeColor = ScottPlot.Color.FromHex("#94a3b8");
                formsPlotTick.Plot.Axes.Left.FrameLineStyle.Color = ScottPlot.Color.FromHex("#475569");
                formsPlotTick.Plot.Axes.Bottom.FrameLineStyle.Color = ScottPlot.Color.FromHex("#475569");

                string fontNameK = PlotHelper.GetInstalledChineseFont();
                string chartTitleK = hasConsecChannel
                    ? $"🟩 [绿色通道 #{consecCh!.StartIndex}~#{consecCh.EndIndex}] 聚合 {periodDesc} K线 ({synthKlines.Count} 根, 覆盖 {totalTicks:N0} 笔 Tick, {kOpenTime:HH:mm} ~ {kCloseTime:HH:mm})"
                    : $"{barRangeStr} 聚合 {periodDesc} K线 ({synthKlines.Count} 根, 覆盖 {totalTicks:N0} 笔 Tick, {kOpenTime:HH:mm} ~ {kCloseTime:HH:mm})";

                formsPlotTick.Plot.Title(chartTitleK, 10.5f);
                formsPlotTick.Plot.Axes.Title.Label.FontName = fontNameK;
                formsPlotTick.Plot.Axes.Title.Label.ForeColor = hasConsecChannel ? ScottPlot.Color.FromHex("#10b981") : ScottPlot.Color.FromHex("#38bdf8");

                int totalKCount = _displayedKlines.Count;
                if (totalKCount > 0)
                {
                    var ohlcList = new List<OHLC>(totalKCount);
                    double minP = double.MaxValue, maxP = double.MinValue;
                    int minPIdx = 0, maxPIdx = 0;
                    double maxKVol = 0;

                    for (int i = 0; i < totalKCount; i++)
                    {
                        var k = _displayedKlines[i];
                        double o = (double)k.Open, h = (double)k.High, l = (double)k.Low, c = (double)k.Close;
                        if (h > maxP) { maxP = h; maxPIdx = i; }
                        if (l < minP) { minP = l; minPIdx = i; }
                        if ((double)k.Volume > maxKVol) maxKVol = (double)k.Volume;

                        ohlcList.Add(new OHLC(o, h, l, c, DateTime.FromOADate(i), TimeSpan.FromDays(0.75)));
                    }

                    var candlePlot = formsPlotTick.Plot.Add.Candlestick(ohlcList);
                    candlePlot.RisingColor = ScottPlot.Color.FromHex("#22c55e");
                    candlePlot.FallingColor = ScottPlot.Color.FromHex("#ef4444");

                    // 绘制合成 K 线的高低点小圆形标记 (如果勾选)
                    if (chkShowKlineHighLow.Checked)
                    {
                        double[] xsH = new double[totalKCount];
                        double[] ysH = new double[totalKCount];
                        double[] xsL = new double[totalKCount];
                        double[] ysL = new double[totalKCount];
                        for (int i = 0; i < totalKCount; i++)
                        {
                            xsH[i] = i;
                            ysH[i] = (double)_displayedKlines[i].High;
                            xsL[i] = i;
                            ysL[i] = (double)_displayedKlines[i].Low;
                        }

                        var hScatter = formsPlotTick.Plot.Add.Scatter(xsH, ysH);
                        hScatter.LineWidth = 0;
                        hScatter.MarkerShape = MarkerShape.FilledCircle;
                        hScatter.MarkerSize = 4f;
                        hScatter.Color = ScottPlot.Color.FromHex("#22c55e");

                        var lScatter = formsPlotTick.Plot.Add.Scatter(xsL, ysL);
                        lScatter.LineWidth = 0;
                        lScatter.MarkerShape = MarkerShape.FilledCircle;
                        lScatter.MarkerSize = 4f;
                        lScatter.Color = ScottPlot.Color.FromHex("#ef4444");
                    }

                    // 成交量柱状图 (右 Y 轴)
                    var volBars = new List<ScottPlot.Bar>(totalKCount);
                    for (int i = 0; i < totalKCount; i++)
                    {
                        var k = _displayedKlines[i];
                        bool isUp = k.Close >= k.Open;
                        var vCol = isUp ? ScottPlot.Color.FromHex("#22c55e").WithAlpha(0.35) : ScottPlot.Color.FromHex("#ef4444").WithAlpha(0.35);
                        volBars.Add(new ScottPlot.Bar
                        {
                            Position = i,
                            Value = (double)k.Volume,
                            ValueBase = 0,
                            Size = 0.6,
                            FillColor = vCol,
                            LineWidth = 0
                        });
                    }
                    if (volBars.Count > 0)
                    {
                        var vPlot = formsPlotTick.Plot.Add.Bars(volBars);
                        vPlot.Axes.YAxis = formsPlotTick.Plot.Axes.Right;
                        formsPlotTick.Plot.Axes.Right.TickLabelStyle.ForeColor = ScottPlot.Color.FromHex("#64748b");
                        formsPlotTick.Plot.Axes.Right.FrameLineStyle.Color = ScottPlot.Color.FromHex("#334155");
                        if (maxKVol > 0)
                        {
                            formsPlotTick.Plot.Axes.SetLimitsY(0, maxKVol * 4.0, formsPlotTick.Plot.Axes.Right);
                        }
                    }

                    // 绿色平行通道投影
                    double yUpStart = 0, yUpEnd = 0, yLowStart = 0, yLowEnd = 0;
                    if (hasConsecChannel)
                    {
                        double x0 = totalKCount == 1 ? -0.4 : 0;
                        double x1 = totalKCount == 1 ? 0.4 : totalKCount - 1;

                        yUpStart = consecSlope * startIndex + consecUpB;
                        yUpEnd = consecSlope * endIndex + consecUpB;
                        yLowStart = consecSlope * startIndex + consecLowB;
                        yLowEnd = consecSlope * endIndex + consecLowB;
                        double yMidStart = consecSlope * startIndex + consecMidB;
                        double yMidEnd = consecSlope * endIndex + consecMidB;

                        var shadePts = new Coordinates[]
                        {
                            new Coordinates(x0, yUpStart),
                            new Coordinates(x1, yUpEnd),
                            new Coordinates(x1, yLowEnd),
                            new Coordinates(x0, yLowStart)
                        };
                        var poly = formsPlotTick.Plot.Add.Polygon(shadePts);
                        poly.FillColor = ScottPlot.Color.FromHex("#10b981").WithAlpha(22);
                        poly.LineWidth = 0;

                        var lUp = formsPlotTick.Plot.Add.Line(x0, yUpStart, x1, yUpEnd);
                        lUp.Color = ScottPlot.Color.FromHex("#10b981");
                        lUp.LineWidth = 1.4f;
                        lUp.LegendText = $"绿色上轨 ({yUpStart:F2}->{yUpEnd:F2})";

                        var lLow = formsPlotTick.Plot.Add.Line(x0, yLowStart, x1, yLowEnd);
                        lLow.Color = ScottPlot.Color.FromHex("#10b981");
                        lLow.LineWidth = 1.4f;
                        lLow.LegendText = $"绿色下轨 ({yLowStart:F2}->{yLowEnd:F2})";

                        var lMid = formsPlotTick.Plot.Add.Line(x0, yMidStart, x1, yMidEnd);
                        lMid.Color = ScottPlot.Color.FromHex("#10b981").WithAlpha(160);
                        lMid.LineWidth = 0.8f;
                        lMid.LinePattern = LinePattern.Dashed;

                        for (int i = 0; i < totalKCount; i++)
                        {
                            var k = _displayedKlines[i];
                            double ratio = (totalKCount > 1) ? (double)i / (totalKCount - 1) : 0.5;
                            double expUp = yUpStart + ratio * (yUpEnd - yUpStart);
                            double expLow = yLowStart + ratio * (yLowEnd - yLowStart);

                            if ((double)k.High > expUp)
                            {
                                var m = formsPlotTick.Plot.Add.Marker(i, (double)k.High);
                                m.Shape = MarkerShape.FilledTriangleDown;
                                m.Size = 9;
                                m.Color = ScottPlot.Color.FromHex("#facc15");
                            }
                            if ((double)k.Low < expLow)
                            {
                                var m = formsPlotTick.Plot.Add.Marker(i, (double)k.Low);
                                m.Shape = MarkerShape.FilledTriangleUp;
                                m.Size = 9;
                                m.Color = ScottPlot.Color.FromHex("#f43f5e");
                            }
                        }
                    }

                    // 最高 / 最低点价格标注
                    if (maxP >= minP)
                    {
                        var mH = formsPlotTick.Plot.Add.Marker(maxPIdx, maxP);
                        mH.Shape = MarkerShape.FilledTriangleDown;
                        mH.Size = 9;
                        mH.Color = ScottPlot.Color.FromHex("#ef4444");

                        double spanP = Math.Max(1.0, maxP - minP);
                        var txtH = formsPlotTick.Plot.Add.Text($"高 {maxP:F2}", maxPIdx, maxP + spanP * 0.03 + 1);
                        txtH.LabelFontColor = ScottPlot.Color.FromHex("#f87171");
                        txtH.LabelFontSize = 9.5f;
                        txtH.LabelBold = true;
                        txtH.Alignment = Alignment.LowerCenter;

                        var mL = formsPlotTick.Plot.Add.Marker(minPIdx, minP);
                        mL.Shape = MarkerShape.FilledTriangleUp;
                        mL.Size = 9;
                        mL.Color = ScottPlot.Color.FromHex("#22c55e");

                        var txtL = formsPlotTick.Plot.Add.Text($"低 {minP:F2}", minPIdx, minP - spanP * 0.03 - 1);
                        txtL.LabelFontColor = ScottPlot.Color.FromHex("#4ade80");
                        txtL.LabelFontSize = 9.5f;
                        txtL.LabelBold = true;
                        txtL.Alignment = Alignment.UpperCenter;
                    }

                    // 绘制反转做单短黄线标记 (如果命中反转形态)
                    if (_engine.EnableReversalOrder && _engine.ShowReversalYellowLines)
                    {
                        var allSignals = _engine.ReversalOrderEngine.AllSignals;
                        var yellowCol = ScottPlot.Color.FromHex("#facc15");
                        for (int kIdx = 0; kIdx < totalKCount; kIdx++)
                        {
                            var k = _displayedKlines[kIdx];
                            var sig = allSignals.FirstOrDefault(s => (s.TriggerTime >= k.OpenTime && s.TriggerTime <= k.CloseTime) || (s.ObservationStartTime >= k.OpenTime && s.ObservationEndTime <= k.CloseTime));
                            if (sig != null)
                            {
                                double y = (double)sig.Price;
                                var line = formsPlotTick.Plot.Add.Line(kIdx - 0.42, y, kIdx + 0.42, y);
                                line.Color = yellowCol;
                                line.LineWidth = 2.5f;

                                var m1 = formsPlotTick.Plot.Add.Marker(kIdx - 0.42, y); m1.Shape = MarkerShape.FilledCircle; m1.Size = 4; m1.Color = yellowCol;
                                var m2 = formsPlotTick.Plot.Add.Marker(kIdx + 0.42, y); m2.Shape = MarkerShape.FilledCircle; m2.Size = 4; m2.Color = yellowCol;

                                var arrow = formsPlotTick.Plot.Add.Marker(kIdx, y);
                                arrow.Shape = sig.Direction == OrderSignalDirection.Sell ? MarkerShape.FilledTriangleDown : MarkerShape.FilledTriangleUp;
                                arrow.Size = 9;
                                arrow.Color = sig.Direction == OrderSignalDirection.Sell ? ScottPlot.Color.FromHex("#ef4444") : ScottPlot.Color.FromHex("#22c55e");

                                string revDirStr = sig.IsBreakoutTrendFollowing
                                    ? (sig.Direction == OrderSignalDirection.Sell ? "顺势高空" : "顺势低多")
                                    : (sig.Direction == OrderSignalDirection.Sell ? "做空" : "做多");
                                var txt = formsPlotTick.Plot.Add.Text($"⚡{revDirStr} [#{sig.Pattern.PatternId} C{sig.ObservationCycleIndex}]", kIdx, y);
                                txt.LabelFontColor = yellowCol;
                                txt.LabelFontSize = 9.0f;
                                txt.LabelBold = true;
                                txt.Alignment = sig.Direction == OrderSignalDirection.Sell ? Alignment.LowerCenter : Alignment.UpperCenter;
                            }
                        }
                    }

                    // 绘制反转策略观察周期标注 (在合成K线模式下)
                    if (_engine.EnableReversalOrder && _engine.ShowObservationCycles && totalKCount > 0)
                    {
                        var allCycles = _engine.ReversalOrderEngine.AllCycles;
                        double spanHK = Math.Max(1.0, maxP - minP);
                        double obsBracketYK = maxP + spanHK * 0.12;
                        double dropTickK = Math.Max(0.2, spanHK * 0.025);

                        for (int c = 0; c < allCycles.Count; c++)
                        {
                            var cyc = allCycles[c];
                            int sK = -1, eK = -1;
                            for (int i = 0; i < totalKCount; i++)
                            {
                                var k = _displayedKlines[i];
                                if (sK < 0 && k.CloseTime >= cyc.StartTime) sK = i;
                                if (k.OpenTime <= cyc.EndTime) eK = i;
                            }
                            if (sK >= 0 && eK >= sK && sK < totalKCount && eK < totalKCount)
                            {
                                bool isBullish = cyc.PriorTrendType == ConsecutiveTrendType.Bullish;
                                var bandCol = isBullish ? ScottPlot.Color.FromHex("#f59e0b") : ScottPlot.Color.FromHex("#06b6d4");

                                // 观察期垂直虚线边界
                                var vStart = formsPlotTick.Plot.Add.VerticalLine(sK - 0.45);
                                vStart.Color = bandCol.WithAlpha(180);
                                vStart.LineWidth = 1.2f;
                                vStart.LinePattern = LinePattern.Dashed;

                                if (eK < totalKCount - 1)
                                {
                                    var vEnd = formsPlotTick.Plot.Add.VerticalLine(eK + 0.45);
                                    vEnd.Color = bandCol.WithAlpha(140);
                                    vEnd.LineWidth = 1.0f;
                                    vEnd.LinePattern = LinePattern.Dotted;
                                }

                                // 顶部横向标尺
                                double x0 = sK - 0.45;
                                double x1 = eK + 0.45;
                                var hLineK = formsPlotTick.Plot.Add.Line(x0, obsBracketYK, x1, obsBracketYK);
                                hLineK.Color = cyc.IsSignalTriggered ? ScottPlot.Color.FromHex("#facc15") : bandCol;
                                hLineK.LineWidth = 1.6f;

                                var bL = formsPlotTick.Plot.Add.Line(x0, obsBracketYK, x0, obsBracketYK - dropTickK);
                                bL.Color = hLineK.Color; bL.LineWidth = 1.6f;
                                var bR = formsPlotTick.Plot.Add.Line(x1, obsBracketYK, x1, obsBracketYK - dropTickK);
                                bR.Color = hLineK.Color; bR.LineWidth = 1.6f;

                                double midXK = (x0 + x1) / 2.0;
                                string bText = cyc.IsSignalTriggered
                                    ? $"⏱️ 观察期 C{cyc.CycleIndex} ⚡触发"
                                    : (cyc.IsCompleted ? $"⏱️ 观察期 C{cyc.CycleIndex} [完毕]" : $"⏱️ 观察期 C{cyc.CycleIndex}");
                                var txtB = formsPlotTick.Plot.Add.Text(bText, midXK, obsBracketYK + dropTickK * 0.5);
                                txtB.LabelFontColor = cyc.IsSignalTriggered ? ScottPlot.Color.FromHex("#facc15") : bandCol;
                                txtB.LabelFontSize = 8.5f;
                                txtB.LabelBold = true;
                                txtB.Alignment = Alignment.LowerCenter;

                                if (obsBracketYK + dropTickK * 3.0 > maxP)
                                {
                                    maxP = obsBracketYK + dropTickK * 3.0;
                                }
                            }
                        }
                    }

                    // 底部时间刻度
                    var manualTicks = new List<Tick>();
                    int labelStep = Math.Max(1, totalKCount / 10);
                    for (int i = 0; i < totalKCount; i++)
                    {
                        if (i % labelStep == 0 || i == totalKCount - 1)
                        {
                            var k = _displayedKlines[i];
                            string tStr = TimeHelper.FromUnixTimeMilliseconds(k.OpenTime).ToLocalTime().ToString("HH:mm");
                            manualTicks.Add(new Tick(i, tStr));
                        }
                    }
                    formsPlotTick.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(manualTicks.ToArray());

                    // 视口边界
                    double yMinK = minP;
                    double yMaxK = maxP;
                    if (hasConsecChannel)
                    {
                        yMaxK = Math.Max(yMaxK, Math.Max(yUpStart, yUpEnd));
                        yMinK = Math.Min(yMinK, Math.Min(yLowStart, yLowEnd));
                    }
                    double yPadK = Math.Max(1.0, (yMaxK - yMinK) * 0.15);
                    formsPlotTick.Plot.Axes.SetLimitsY(yMinK - yPadK * 0.5, yMaxK + yPadK * 1.2);
                    if (totalKCount == 1)
                    {
                        formsPlotTick.Plot.Axes.SetLimitsX(-1.0, 1.0);
                    }
                    else
                    {
                        formsPlotTick.Plot.Axes.SetLimitsX(-totalKCount * 0.05, totalKCount * 1.05);
                    }
                }

                formsPlotTick.Plot.ShowLegend(Alignment.UpperRight);
                formsPlotTick.Plot.Legend.FontName = fontNameK;
                formsPlotTick.Plot.Legend.FontSize = 8.5f;
                formsPlotTick.Plot.Legend.BackgroundColor = ScottPlot.Color.FromHex("#1e293b").WithAlpha(200);
                formsPlotTick.Plot.Legend.FontColor = ScottPlot.Color.FromHex("#f1f5f9");
                formsPlotTick.Refresh();
            }

            // 2. 装载 VirtualMode 虚拟数据源 (划分各 K 线时段并标记首笔 Tick)
            _displayedTicks.Clear();
            if (_displayedTicks.Capacity < totalTicks)
            {
                _displayedTicks.Capacity = totalTicks;
            }

            decimal kOpen = klineStart.Open;
            int lastBar = -1;
            for (int t = 0; t < totalTicks; t++)
            {
                var tick = ticks[t];
                bool isBuyer = !tick.IsBuyerMaker;
                decimal diffFromOpen = kOpen > 0 ? (tick.Price - kOpen) / kOpen * 100m : 0m;

                int curBar = tickBarIndices[t];
                bool isFirst = curBar != lastBar;
                lastBar = curBar;

                DateTime barOpenDt = TimeHelper.FromUnixTimeMilliseconds(allKlines[curBar].OpenTime);
                string barTimeStr = barOpenDt.ToString("HH:mm");

                _displayedTicks.Add(new TickViewModel
                {
                    TickIndex = t + 1,
                    BarIndex = curBar,
                    BarTimeStr = barTimeStr,
                    IsFirstTickOfBar = isFirst,
                    Time = tick.Time,
                    Price = tick.Price,
                    Qty = tick.Qty,
                    QuoteQty = tick.QuoteQty,
                    IsBuyer = isBuyer,
                    DiffPct = diffFromOpen,
                    ChannelRelation = tickChannelRelations[t]
                });
            }

            dgvTicks.RowCount = _displayedTicks.Count;
            dgvTicks.Invalidate();

            if (_tickPeriodMode != 0)
            {
                dgvSynthesizedKlines.RowCount = _displayedKlines.Count;
                dgvSynthesizedKlines.Invalidate();
            }
            else
            {
                _displayedKlines.Clear();
                dgvSynthesizedKlines.RowCount = 0;
            }
        }

        private void OnDgvTicksCellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _displayedTicks.Count) return;
            var tick = _displayedTicks[e.RowIndex];

            e.Value = e.ColumnIndex switch
            {
                0 => tick.TickIndex,
                1 => tick.IsFirstTickOfBar ? $"#{tick.BarIndex} [{tick.BarTimeStr}]" : $"#{tick.BarIndex}",
                2 => TimeHelper.FromUnixTimeMilliseconds(tick.Time).ToLocalTime().ToString("HH:mm:ss.fff"),
                3 => tick.Price.ToString("F2"),
                4 => tick.Qty.ToString("F4"),
                5 => tick.QuoteQty.ToString("F2"),
                6 => tick.IsBuyer ? "🟢 买方主动" : "🔴 卖方主动",
                7 => $"{tick.DiffPct:+0.00;-0.00;0.00}%",
                8 => tick.ChannelRelation,
                _ => null
            };
        }

        private void OnDgvTicksCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _displayedTicks.Count || e.CellStyle == null) return;
            var tick = _displayedTicks[e.RowIndex];

            if (tick.IsFirstTickOfBar)
            {
                e.CellStyle.BackColor = Color.FromArgb(24, 38, 64);
                if (e.ColumnIndex == 1)
                {
                    e.CellStyle.ForeColor = Color.FromArgb(56, 189, 248);
                    e.CellStyle.Font = new Font(dgvTicks.Font, FontStyle.Bold);
                }
            }

            if (e.ColumnIndex == 6)
            {
                e.CellStyle.ForeColor = tick.IsBuyer
                    ? Color.FromArgb(74, 222, 128)
                    : Color.FromArgb(248, 113, 113);
            }
            else if (e.ColumnIndex == 8)
            {
                if (tick.ChannelRelation.Contains("破通道上轨") || tick.ChannelRelation.Contains("突破保留") || tick.ChannelRelation.Contains("破绿色上轨"))
                {
                    e.CellStyle.ForeColor = Color.FromArgb(250, 204, 21); // Yellow 400
                    e.CellStyle.Font = new Font(dgvTicks.Font, FontStyle.Bold);
                }
                else if (tick.ChannelRelation.Contains("破通道下轨") || tick.ChannelRelation.Contains("跌破保留") || tick.ChannelRelation.Contains("破绿色下轨"))
                {
                    e.CellStyle.ForeColor = Color.FromArgb(244, 63, 94); // Rose 500
                    e.CellStyle.Font = new Font(dgvTicks.Font, FontStyle.Bold);
                }
                else if (tick.ChannelRelation.Contains("绿色通道内"))
                {
                    e.CellStyle.ForeColor = Color.FromArgb(74, 222, 128); // Emerald 400
                }
                else if (tick.ChannelRelation == "通道内")
                {
                    e.CellStyle.ForeColor = Color.FromArgb(148, 163, 184); // Slate 400
                }
            }
        }

        private void OnDgvTicksCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex >= 0 && e.RowIndex < _displayedTicks.Count)
            {
                var tick = _displayedTicks[e.RowIndex];
                if (tick.IsFirstTickOfBar && e.RowIndex > 0)
                {
                    // 在跨越 K 线的首笔 Tick 行顶部绘制清晰天蓝色分割线，完美视觉划分不同 K 线
                    e.Paint(e.ClipBounds, DataGridViewPaintParts.All);
                    using var pen = new Pen(Color.FromArgb(56, 189, 248), 1.5f);
                    e.Graphics?.DrawLine(pen, e.CellBounds.Left, e.CellBounds.Top, e.CellBounds.Right, e.CellBounds.Top);
                    e.Handled = true;
                }
            }
        }

        private void OnDgvTicksSelectionChanged(object? sender, EventArgs e)
        {
            if (dgvTicks.SelectedRows.Count > 1 && _displayedTicks.Count > 0)
            {
                int selCount = dgvTicks.SelectedRows.Count;
                decimal selQty = 0;
                decimal selQuote = 0;
                decimal takerBuyQty = 0;

                foreach (DataGridViewRow row in dgvTicks.SelectedRows)
                {
                    int rIdx = row.Index;
                    if (rIdx >= 0 && rIdx < _displayedTicks.Count)
                    {
                        var t = _displayedTicks[rIdx];
                        selQty += t.Qty;
                        selQuote += t.QuoteQty;
                        if (t.IsBuyer) takerBuyQty += t.Qty;
                    }
                }

                decimal avgP = selQty > 0 ? (selQuote / selQty) : 0;
                decimal buyRatio = selQty > 0 ? (takerBuyQty / selQty * 100m) : 0;

                int sIdx = _selectedBarStartIndex ?? 0;
                int eIdx = _selectedBarEndIndex ?? sIdx;
                string barRangeStr = sIdx == eIdx ? $"Bar #{sIdx}" : $"Bar #{sIdx}~#{eIdx}";

                lblTickTitle.Text = $"🎯 {barRangeStr} [已选 {selCount:N0} 笔 Tick | 均价: {avgP:F2} | 量: {selQty:F4} | 主买: {buyRatio:F1}%]";
            }
            else if (_displayedTicks.Count > 0)
            {
                int sIdx = _selectedBarStartIndex ?? 0;
                int eIdx = _selectedBarEndIndex ?? sIdx;
                int barCount = Math.Max(1, eIdx - sIdx + 1);
                string barRangeStr = barCount == 1 ? $"Bar #{sIdx}" : $"Bar #{sIdx} ~ #{eIdx} (共 {barCount} 根 K 线)";
                lblTickTitle.Text = $"🎯 {barRangeStr} 微观逐笔成交明细";
            }
        }

        #region Tick 周期切换与合成 K 线事件响应

        private Button CreatePeriodButton(string text, int mode)
        {
            var btn = new Button
            {
                Text = text,
                Height = 25,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei", 8F),
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.FromArgb(203, 213, 225),
                Cursor = Cursors.Hand,
                Margin = new Padding(2, 2, 2, 2),
                Padding = new Padding(4, 0, 4, 0)
            };
            btn.FlatAppearance.BorderSize = 1;
            btn.FlatAppearance.BorderColor = Color.FromArgb(51, 65, 85);
            btn.Click += (s, e) => SetTickPeriodMode(mode);
            return btn;
        }

        private void SetTickPeriodMode(int mode)
        {
            _tickPeriodMode = mode;
            UpdateTickPeriodButtonsState();
            SaveSettingsFromUi();

            if (_cachedRawTicks != null && _cachedRawTicks.Length > 0 && _cachedTickStartIndex >= 0)
            {
                DisplayTicksInternal(_cachedTickStartIndex, _cachedTickEndIndex, _cachedRawTicks);
            }
        }

        private void UpdateTickPeriodButtonsState()
        {
            Color activeBg = Color.FromArgb(14, 116, 144); // Cyan 700 / Ocean Blue
            Color activeFg = Color.White;
            Color inactiveBg = Color.FromArgb(30, 41, 59); // Slate 800
            Color inactiveFg = Color.FromArgb(203, 213, 225); // Slate 300

            btnPeriodRaw.BackColor = _tickPeriodMode == 0 ? activeBg : inactiveBg;
            btnPeriodRaw.ForeColor = _tickPeriodMode == 0 ? activeFg : inactiveFg;
            btnPeriodRaw.Font = new Font(btnPeriodRaw.Font, _tickPeriodMode == 0 ? FontStyle.Bold : FontStyle.Regular);

            btnPeriod1m.BackColor = _tickPeriodMode == 1 ? activeBg : inactiveBg;
            btnPeriod1m.ForeColor = _tickPeriodMode == 1 ? activeFg : inactiveFg;
            btnPeriod1m.Font = new Font(btnPeriod1m.Font, _tickPeriodMode == 1 ? FontStyle.Bold : FontStyle.Regular);

            btnPeriod5m.BackColor = _tickPeriodMode == 2 ? activeBg : inactiveBg;
            btnPeriod5m.ForeColor = _tickPeriodMode == 2 ? activeFg : inactiveFg;
            btnPeriod5m.Font = new Font(btnPeriod5m.Font, _tickPeriodMode == 2 ? FontStyle.Bold : FontStyle.Regular);

            btnPeriod15m.BackColor = _tickPeriodMode == 3 ? activeBg : inactiveBg;
            btnPeriod15m.ForeColor = _tickPeriodMode == 3 ? activeFg : inactiveFg;
            btnPeriod15m.Font = new Font(btnPeriod15m.Font, _tickPeriodMode == 3 ? FontStyle.Bold : FontStyle.Regular);

            btnPeriodCustom.BackColor = _tickPeriodMode == 4 ? activeBg : inactiveBg;
            btnPeriodCustom.ForeColor = _tickPeriodMode == 4 ? activeFg : inactiveFg;
            btnPeriodCustom.Font = new Font(btnPeriodCustom.Font, _tickPeriodMode == 4 ? FontStyle.Bold : FontStyle.Regular);

            numCustomTickPeriod.Enabled = (_tickPeriodMode == 4);
        }

        private void UpdateTickPeriodTabs(int mode, int totalTicks, string periodDesc)
        {
            int currentSelIndex = tabTickViews.SelectedIndex;
            tabTickViews.SuspendLayout();

            if (mode == 0)
            {
                tabTickViews.TabPages.Clear();
                tabTickTable.Text = $"📑 逐笔成交明细表 ({totalTicks:N0}笔)";
                tabTickPlot.Text = "📈 内部 Tick 分时走势";
                tabTickViews.TabPages.Add(tabTickTable);
                tabTickViews.TabPages.Add(tabTickPlot);
            }
            else
            {
                tabTickViews.TabPages.Clear();
                tabKlines.Text = $"📊 合成 K 线明细 ({periodDesc})";
                tabTickPlot.Text = $"🕯️ 合成 K 线蜡烛图 ({periodDesc})";
                tabTickTable.Text = $"📑 原始逐笔明细 ({totalTicks:N0}笔)";
                tabTickViews.TabPages.Add(tabKlines);
                tabTickViews.TabPages.Add(tabTickPlot);
                tabTickViews.TabPages.Add(tabTickTable);
            }

            if (currentSelIndex >= 0 && currentSelIndex < tabTickViews.TabPages.Count)
            {
                tabTickViews.SelectedIndex = currentSelIndex;
            }
            else
            {
                tabTickViews.SelectedIndex = 0;
            }

            tabTickViews.ResumeLayout();
        }

        private void OnDgvSynthesizedKlinesCellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _displayedKlines.Count) return;
            var k = _displayedKlines[e.RowIndex];

            e.Value = e.ColumnIndex switch
            {
                0 => k.Index,
                1 => k.TimeRangeStr,
                2 => k.Open.ToString("F2"),
                3 => k.High.ToString("F2"),
                4 => k.Low.ToString("F2"),
                5 => k.Close.ToString("F2"),
                6 => $"{k.ChangePct:+0.00;-0.00;0.00}%",
                7 => k.Volume.ToString("F4"),
                8 => k.QuoteVolume.ToString("F2"),
                9 => k.TradeCount.ToString("N0"),
                10 => $"{k.TakerBuyRatio:F1}%",
                11 => k.ChannelRelation,
                _ => null
            };
        }

        private void OnDgvSynthesizedKlinesCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _displayedKlines.Count || e.CellStyle == null) return;
            var k = _displayedKlines[e.RowIndex];

            if (e.ColumnIndex == 5 || e.ColumnIndex == 6)
            {
                bool isUp = k.Close >= k.Open;
                e.CellStyle.ForeColor = isUp ? Color.FromArgb(74, 222, 128) : Color.FromArgb(248, 113, 113);
                e.CellStyle.Font = new Font(dgvSynthesizedKlines.Font, FontStyle.Bold);
            }
            else if (e.ColumnIndex == 10)
            {
                e.CellStyle.ForeColor = k.TakerBuyRatio >= 50m ? Color.FromArgb(74, 222, 128) : Color.FromArgb(248, 113, 113);
            }
            else if (e.ColumnIndex == 11)
            {
                if (k.ChannelRelation.Contains("破") || k.ChannelRelation.Contains("突破"))
                {
                    e.CellStyle.ForeColor = Color.FromArgb(250, 204, 21);
                    e.CellStyle.Font = new Font(dgvSynthesizedKlines.Font, FontStyle.Bold);
                }
                else if (k.ChannelRelation.Contains("通道内"))
                {
                    e.CellStyle.ForeColor = Color.FromArgb(74, 222, 128);
                }
            }
        }

        private void OnDgvSynthesizedKlinesSelectionChanged(object? sender, EventArgs e)
        {
            if (dgvSynthesizedKlines.SelectedRows.Count == 1 && _displayedKlines.Count > 0)
            {
                int rIdx = dgvSynthesizedKlines.SelectedRows[0].Index;
                if (rIdx >= 0 && rIdx < _displayedKlines.Count)
                {
                    var k = _displayedKlines[rIdx];
                    lblTickTitle.Text = $"🎯 合成K线 #{k.Index} [{k.TimeRangeStr}] O:{k.Open:F2} H:{k.High:F2} L:{k.Low:F2} C:{k.Close:F2} ({k.ChangePct:+0.00;-0.00}%) | 量:{k.Volume:F4} | {k.TradeCount:N0}笔Tick";
                }
            }
        }

        #endregion

        #endregion

        private void ApplyDarkTheme()
        {
            this.BackColor = Color.FromArgb(15, 23, 42); // Slate 900
            formsPlot.BackColor = Color.FromArgb(30, 41, 59);
        }

        #endregion

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveSettingsFromUi();
            _engine?.Dispose();
            base.OnFormClosing(e);
        }
    }
}
