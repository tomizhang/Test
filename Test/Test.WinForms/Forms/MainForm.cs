using Binance.Net.Enums;
using Common;
using Common.Helper;
using Common.Models;
using Common.Services;
using ScottPlot.WinForms;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Test.Strategy;

namespace Test.WinForms.Forms
{
    /// <summary>
    /// 专业量化回测系统主窗体 (WinForms 主界面)
    /// 核心功能：
    /// 1. 策略 0.5% 止损 与 1.5% 止盈 仓位全生命周期管理
    /// 2. 自动化生成独立交互式 HTML 回测分析报告并落盘
    /// 3. 自动记忆与持久化界面配置信息 (交易对、周期、起止时间、策略参数、窗口尺寸及分割布局)
    /// 4. 趋势线触碰 3-Tick 内回弹开仓策略 (高点回弹开空，低点回弹开多，LineX1X2>=40, LineAge>=4, 1分钟冷却)
    /// 5. 触发开仓的趋势线在图表上渲染为显目绿色 (IsTriggered=true)，日志输出完整趋势线特征
    /// 6. UI 采用 Timer 定时批量轮询抽取机制 (杜绝 BeginInvoke 消息风暴，窗口拖拽与交互 100% 丝滑)
    /// </summary>
    public class MainForm : Form
    {
        private readonly IBacktestEngineService _engineService;
        private CancellationTokenSource? _cts;
        private BacktestResult? _latestResult;

        // 线程安全运行态缓存
        private volatile bool _isRealtimeChartEnabled = true;
        private volatile bool _isAutoScaleEnabled = true;
        private volatile bool _isUiLogEnabled = true;
        private volatile string _currentRunningCoin = "BTCUSDT";
        private volatile string _currentRunningInterval = "1m";

        // UI 异步消息解耦缓冲
        private readonly ConcurrentQueue<(string Message, Color Color)> _logQueue = new ConcurrentQueue<(string, Color)>();
        private volatile BacktestProgress? _latestProgress = null;
        private ChartSnapshot? _latestChartSnapshot = null;
        private long _renderedSnapshotVersion = 0;
        private long _lastChartSnapshotTicks = 0;
        private System.Windows.Forms.Timer _uiRefreshTimer = null!;

        // 图表渲染态趋势线缓存 (用于点击图表拾取与特征详细日志输出)
        private readonly object _plottedLinesLock = new object();
        private List<TrendLine> _currentPlottedLines = new List<TrendLine>();
        private int _currentPlotStartGlobalIndex = 0;
        private int _currentPlotEndGlobalIndex = 0;
        private TrendLine? _selectedTrendLine = null; // 当前用户点击选中的趋势线 (以红色高亮显示)
        private int? _selectedKlineIndex = null;      // 🌟 当前用户点击选中的 K 线序号 (以青色光标高亮显示)
        private ChartType _chartType = ChartType.Candlestick; // 🌟 当前图表渲染模式 (蜡烛图 / 收盘折线)

        // UI 控件定义
        private SplitContainer splitMain = null!;
        private SplitContainer splitLeft = null!;
        private FormsPlot formsPlot = null!;
        private SplitContainer splitBottom = null!;
        private RichTextBox txtLogs = null!;
        private Panel panelLogHeader = null!;
        private Label lblLogTitle = null!;
        private Button btnClearLogsTop = null!;

        // 🌟 K线微观逐笔 Tick 详情与走势图控件 (位于日志右侧)
        private Panel panelTickDetail = null!;
        private Panel panelTickHeader = null!;
        private Label lblTickTitle = null!;
        private Label lblTickInfo = null!;
        private TabControl tabTickViews = null!;
        private TabPage tabTickPlot = null!;
        private TabPage tabTickTable = null!;
        private FormsPlot formsPlotTick = null!;
        private DataGridView dgvTicks = null!;
        private readonly List<TickViewModel> _displayedTicks = new();

        private struct TickViewModel
        {
            public int TickIndex;
            public long Time;
            public decimal Price;
            public decimal Qty;
            public decimal QuoteQty;
            public bool IsBuyer;
            public decimal DiffPct;
        }

        // 右侧控制面板控件
        private Panel panelRight = null!;
        private GroupBox grpData = null!;
        private ComboBox cboCoin = null!;
        private ComboBox cboInterval = null!;
        private ComboBox cboChartType = null!;
        private DateTimePicker dtpStart = null!;
        private DateTimePicker dtpEnd = null!;

        private GroupBox grpStrategy = null!;
        private ComboBox cboTradeStrategy = null!;
        private ComboBox cboPivotAlgorithm = null!;
        private NumericUpDown numZigZagDeviation = null!;
        private NumericUpDown numZigZagDepth = null!;
        private NumericUpDown numMaxKlines = null!;
        private NumericUpDown numMinTrendLines = null!;
        private NumericUpDown numLeftLen = null!;
        private NumericUpDown numRightLen = null!;
        private NumericUpDown numMaxSpan = null!;
        private NumericUpDown numMinSignalSpan = null!;
        private NumericUpDown numMinSignalAge = null!;
        private NumericUpDown numMinSlope = null!;
        private NumericUpDown numCooldown = null!;
        private NumericUpDown numTakeProfit = null!;
        private NumericUpDown numStopLoss = null!;
        private NumericUpDown numLineWidth = null!;
        private CheckBox chkEnableTrading = null!;
        private CheckBox chkEnableTickStopLoss = null!;
        private CheckBox chkStrictEnvelope = null!;
        private CheckBox chkRealtimeChart = null!;
        private CheckBox chkAutoScale = null!;
        private CheckBox chkEnableUiLogs = null!;
        private CheckBox chkShowTpSl = null!;
        private CheckBox chkShowPivots = null!;
        private CheckBox chkShowTrendLines = null!;

        private GroupBox grpControl = null!;
        private Button btnStart = null!;
        private Button btnPause = null!;
        private Button btnStop = null!;
        private Button btnResetAxes = null!;
        private Button btnExportChart = null!;
        private Button btnOpenReport = null!;
        private Button btnClearLogs = null!;
        private ProgressBar progressBar = null!;
        private Label lblProgress = null!;

        private GroupBox grpStatus = null!;
        private Label lblStatTime = null!;
        private Label lblStatThroughput = null!;
        private Label lblStatTrades = null!;
        private Label lblStatSignals = null!;
        private Label lblStatKlines = null!;
        private Label lblStatTicks = null!;
        private Label lblStatPeaksValleys = null!;
        private Label lblStatActiveLines = null!;
        private Label lblStatDeletedLines = null!;

        public MainForm(IBacktestEngineService engineService)
        {
            _engineService = engineService ?? throw new ArgumentNullException(nameof(engineService));

            InitializeComponents();
            BindEngineEvents();
            SetupUiRefreshTimer();
            ApplyDarkTheme();
        }

        #region 初始化组件与布局

        private void InitializeComponents()
        {
            this.Text = "币安量化回测系统 - 趋势线与极值增量引擎 (Binance Backtest Platform)";
            this.Size = new Size(1600, 980);
            this.MinimumSize = new Size(1280, 800);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular, GraphicsUnit.Point);

            // 1. 主分割容器 (左右分割)
            splitMain = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 6,
                FixedPanel = FixedPanel.Panel2
            };

            // 2. 左侧分割容器 (上下分割)
            splitLeft = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = 6
            };

            // ① 左上：ScottPlot 交互式图表控件
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

            // ② 左下：日志控制面板与微观 Tick 详情分析 (水平左右分割)
            splitBottom = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 6
            };

            txtLogs = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.FromArgb(15, 23, 42), // Slate 900
                ForeColor = Color.FromArgb(241, 245, 249),
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9.5F, FontStyle.Regular, GraphicsUnit.Point),
                HideSelection = false
            };

            panelLogHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 32,
                BackColor = Color.FromArgb(30, 41, 59) // Slate 800
            };

            lblLogTitle = new Label
            {
                Text = "⚡ 关键日志与信号监控",
                Font = new Font("Microsoft YaHei", 9.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(226, 232, 240),
                AutoSize = true,
                Location = new Point(8, 7)
            };

            btnClearLogsTop = new Button
            {
                Text = "清空日志",
                Size = new Size(70, 24),
                Location = new Point(190, 4),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei", 8F),
                BackColor = Color.FromArgb(51, 65, 85),
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            btnClearLogsTop.FlatAppearance.BorderSize = 0;
            btnClearLogsTop.Click += (s, e) => txtLogs.Clear();

            panelLogHeader.Controls.Add(lblLogTitle);
            panelLogHeader.Controls.Add(btnClearLogsTop);

            var panelLogContainer = new Panel { Dock = DockStyle.Fill };
            panelLogContainer.Controls.Add(txtLogs);
            panelLogContainer.Controls.Add(panelLogHeader);
            splitBottom.Panel1.Controls.Add(panelLogContainer);

            // ③ 日志右侧：微观 Tick 详情视口 (Tick 折线走势图 + 逐笔流水明细表)
            panelTickDetail = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(15, 23, 42)
            };

            panelTickHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 32,
                BackColor = Color.FromArgb(30, 41, 59)
            };

            lblTickTitle = new Label
            {
                Text = "⚡ K线微观逐笔 Tick 明细:",
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248),
                Location = new Point(8, 7),
                AutoSize = true
            };

            lblTickInfo = new Label
            {
                Text = "未选择 K 线 (点击上方图表中的任意一根 K 线查看详细 Tick 走势)",
                Font = new Font("Microsoft YaHei", 8.5F),
                ForeColor = Color.FromArgb(203, 213, 225),
                Location = new Point(195, 8),
                AutoSize = true
            };
            panelTickHeader.Controls.AddRange(new Control[] { lblTickTitle, lblTickInfo });

            tabTickViews = new TabControl
            {
                Dock = DockStyle.Fill
            };

            tabTickPlot = new TabPage("📈 内部 Tick 价格路径") { BackColor = Color.FromArgb(15, 23, 42) };
            formsPlotTick = new FormsPlot
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(15, 23, 42)
            };
            tabTickPlot.Controls.Add(formsPlotTick);

            tabTickTable = new TabPage("📑 逐笔 Tick 流水明细表") { BackColor = Color.FromArgb(15, 23, 42) };
            dgvTicks = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(15, 23, 42),
                GridColor = Color.FromArgb(51, 65, 85),
                BorderStyle = BorderStyle.None,
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                EnableHeadersVisualStyles = false,
                Font = new Font("Consolas", 9F),
                VirtualMode = true
            };

            typeof(DataGridView).InvokeMember(
                "DoubleBuffered",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.SetProperty,
                null, dgvTicks, new object[] { true });

            dgvTicks.CellValueNeeded += OnDgvTicksCellValueNeeded;
            dgvTicks.CellFormatting += OnDgvTicksCellFormatting;

            dgvTicks.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(30, 41, 59);
            dgvTicks.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(56, 189, 248);
            dgvTicks.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold);
            dgvTicks.DefaultCellStyle.BackColor = Color.FromArgb(15, 23, 42);
            dgvTicks.DefaultCellStyle.ForeColor = Color.FromArgb(241, 245, 249);
            dgvTicks.DefaultCellStyle.SelectionBackColor = Color.FromArgb(30, 58, 138);

            dgvTicks.Columns.Add("ColIdx", "#");
            dgvTicks.Columns.Add("ColTime", "精确时间 (UTC+8)");
            dgvTicks.Columns.Add("ColPrice", "价格 (USDT)");
            dgvTicks.Columns.Add("ColQty", "数量 (Qty)");
            dgvTicks.Columns.Add("ColQuote", "成交额 (USDT)");
            dgvTicks.Columns.Add("ColSide", "主动买卖");
            dgvTicks.Columns.Add("ColChange", "偏离开盘 (%)");

            dgvTicks.Columns[0].Width = 50;
            dgvTicks.Columns[1].Width = 140;
            dgvTicks.Columns[2].Width = 100;
            dgvTicks.Columns[3].Width = 90;
            dgvTicks.Columns[4].Width = 100;
            dgvTicks.Columns[5].Width = 100;
            dgvTicks.Columns[6].Width = 100;

            tabTickTable.Controls.Add(dgvTicks);

            tabTickViews.TabPages.Add(tabTickPlot);
            tabTickViews.TabPages.Add(tabTickTable);

            panelTickDetail.Controls.Add(tabTickViews);
            panelTickDetail.Controls.Add(panelTickHeader);
            splitBottom.Panel2.Controls.Add(panelTickDetail);

            splitLeft.Panel2.Controls.Add(splitBottom);
            splitMain.Panel1.Controls.Add(splitLeft);

            // 3. 右侧控制面板
            panelRight = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(10),
                BackColor = Color.FromArgb(30, 41, 59) // Slate 800
            };

            BuildRightControlPanel();
            splitMain.Panel2.Controls.Add(panelRight);

            this.Controls.Add(splitMain);

            // 窗体加载与关闭事件：自动加载与保存用户界面配置
            this.Load += (s, e) =>
            {
                LoadSettingsToUi();
                InitializeDefaultPlot();
            };

            this.FormClosing += (s, e) =>
            {
                SaveSettingsFromUi();
            };
        }

        private void BuildRightControlPanel()
        {
            int top = 10;

            // Group 1: 基础数据设置
            grpData = CreateGroupBox("1. 基础数据配置", top, 205);
            {
                var lblCoin = CreateLabel("交易对:", 15, 25);
                cboCoin = new ComboBox { Location = new Point(90, 22), Width = 250, DropDownStyle = ComboBoxStyle.DropDownList };
                cboCoin.Items.AddRange(new object[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "DOGEUSDT", "XRPUSDT" });
                cboCoin.SelectedIndex = 0;

                var lblInterval = CreateLabel("K线周期:", 15, 58);
                cboInterval = new ComboBox { Location = new Point(90, 55), Width = 250, DropDownStyle = ComboBoxStyle.DropDownList };
                cboInterval.Items.AddRange(new object[] { "1m (1分钟)", "3m (3分钟)", "5m (5分钟)", "15m (15分钟)", "30m (30分钟)", "1h (1小时)", "2h (2小时)", "4h (4小时)", "1d (1天)" });
                cboInterval.SelectedIndex = 0;

                var lblChartType = CreateLabel("图表模式:", 15, 93);
                cboChartType = new ComboBox
                {
                    Location = new Point(90, 90),
                    Width = 250,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = Color.FromArgb(30, 41, 59),
                    ForeColor = Color.FromArgb(74, 222, 128), // Green 400
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold)
                };
                cboChartType.Items.AddRange(new object[] { "🕯️ 蜡烛图 (Candlestick)", "📈 收盘折线 (Line Chart)" });
                cboChartType.SelectedIndex = 0;
                cboChartType.SelectedIndexChanged += (s, e) =>
                {
                    _chartType = (ChartType)cboChartType.SelectedIndex;
                    RedrawCurrentPlot(autoScale: false);
                };

                var lblStart = CreateLabel("起始日期:", 15, 128);
                dtpStart = new DateTimePicker { Location = new Point(90, 125), Width = 250, Format = DateTimePickerFormat.Short, Value = new DateTime(2025, 1, 1) };

                var lblEnd = CreateLabel("结束日期:", 15, 163);
                dtpEnd = new DateTimePicker { Location = new Point(90, 160), Width = 250, Format = DateTimePickerFormat.Short, Value = new DateTime(2025, 1, 5) };

                grpData.Controls.AddRange(new Control[] { lblCoin, cboCoin, lblInterval, cboInterval, lblChartType, cboChartType, lblStart, dtpStart, lblEnd, dtpEnd });
            }
            panelRight.Controls.Add(grpData);
            top += grpData.Height + 10;

            // Group 2: 趋势线策略参数 (增加 交易策略下拉选择、极值算法下拉选择、ZigZag反转幅度/深度、开启交易开关、1.5% 止盈、0.5% 止损、跨度>=40、寿命>=4、60s 冷却)
            grpStrategy = CreateGroupBox("2. 策略模式与参数配置", top, 675);
            {
                var lblStrategy = CreateLabel("交易执行策略:", 15, 25);
                cboTradeStrategy = new ComboBox
                {
                    Location = new Point(130, 22),
                    Width = 210,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = Color.FromArgb(30, 41, 59),
                    ForeColor = Color.FromArgb(56, 189, 248),
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold)
                };
                cboTradeStrategy.Items.AddRange(new object[] {
                    "经典趋势线触碰回弹策略",
                    "紫色特殊趋势线穿透策略",
                    "宏观Level3假突破猎杀策略 (SFP)",
                    "多策略组合模式 (触碰+穿透+SFP)"
                });
                cboTradeStrategy.SelectedIndex = 0;

                var lblAlgorithm = CreateLabel("极值计算算法:", 15, 53);
                cboPivotAlgorithm = new ComboBox
                {
                    Location = new Point(130, 50),
                    Width = 210,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = Color.FromArgb(30, 41, 59),
                    ForeColor = Color.FromArgb(248, 250, 252),
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold)
                };
                cboPivotAlgorithm.Items.AddRange(new object[] { "分形对比法 (Fractal)", "ZigZag之字转向法 (ZigZag)" });
                cboPivotAlgorithm.SelectedIndex = 0;

                var lblLeft = CreateLabel("分形左侧对比:", 15, 81);
                numLeftLen = new NumericUpDown { Location = new Point(130, 78), Width = 210, Minimum = 1, Maximum = 100, Value = 5 };

                var lblRight = CreateLabel("分形右侧对比:", 15, 109);
                numRightLen = new NumericUpDown { Location = new Point(130, 106), Width = 210, Minimum = 1, Maximum = 100, Value = 5 };

                var lblZigZagDev = CreateLabel("ZigZag反转幅度%:", 15, 137);
                numZigZagDeviation = new NumericUpDown { Location = new Point(130, 134), Width = 210, Minimum = 0.1m, Maximum = 50.0m, DecimalPlaces = 2, Increment = 0.1m, Value = 1.00m, Enabled = false };
                numZigZagDeviation.ForeColor = Color.FromArgb(232, 121, 249); // Fuchsia 400

                var lblZigZagDepth = CreateLabel("ZigZag最小深度:", 15, 165);
                numZigZagDepth = new NumericUpDown { Location = new Point(130, 162), Width = 210, Minimum = 1, Maximum = 100, Value = 5, Enabled = false };
                numZigZagDepth.ForeColor = Color.FromArgb(232, 121, 249);

                cboPivotAlgorithm.SelectedIndexChanged += (s, e) =>
                {
                    bool isZigZag = cboPivotAlgorithm.SelectedIndex == 1;
                    numZigZagDeviation.Enabled = isZigZag;
                    numZigZagDepth.Enabled = isZigZag;
                    numLeftLen.Enabled = !isZigZag;
                    numRightLen.Enabled = !isZigZag;
                };

                var lblMaxK = CreateLabel("K线滑动窗口:", 15, 193);
                numMaxKlines = new NumericUpDown { Location = new Point(130, 190), Width = 210, Minimum = 100, Maximum = 100000, Value = 2000 };

                var lblMinT = CreateLabel("历史趋势线库:", 15, 221);
                numMinTrendLines = new NumericUpDown { Location = new Point(130, 218), Width = 210, Minimum = 100, Maximum = 50000, Value = 1000 };

                var lblSpan = CreateLabel("最大配对跨度:", 15, 249);
                numMaxSpan = new NumericUpDown { Location = new Point(130, 246), Width = 210, Minimum = 10, Maximum = 2000, Value = 100 };

                var lblSignalSpan = CreateLabel("趋势线跨度≥(X1X2):", 15, 277);
                numMinSignalSpan = new NumericUpDown { Location = new Point(130, 274), Width = 210, Minimum = 1, Maximum = 1000, Value = 40 };
                numMinSignalSpan.ValueChanged += (s, e) => RedrawCurrentPlot(autoScale: false);

                var lblSignalAge = CreateLabel("趋势线寿命≥(Age):", 15, 305);
                numMinSignalAge = new NumericUpDown { Location = new Point(130, 302), Width = 210, Minimum = 0, Maximum = 500, Value = 4 };
                numMinSignalAge.ValueChanged += (s, e) => RedrawCurrentPlot(autoScale: false);

                var lblMinSlope = CreateLabel("开仓整体斜率≥(%):", 15, 333);
                numMinSlope = new NumericUpDown { Location = new Point(130, 330), Width = 210, Minimum = 0.00m, Maximum = 50.00m, DecimalPlaces = 2, Increment = 0.10m, Value = 0.50m };
                numMinSlope.ForeColor = Color.FromArgb(250, 204, 21); // Yellow 400

                var lblCooldown = CreateLabel("触发冷却(秒):", 15, 361);
                numCooldown = new NumericUpDown { Location = new Point(130, 358), Width = 210, Minimum = 0, Maximum = 3600, Value = 60 };

                var lblTP = CreateLabel("止盈比例 (%):", 15, 389);
                numTakeProfit = new NumericUpDown { Location = new Point(130, 386), Width = 210, Minimum = 0.1m, Maximum = 100m, DecimalPlaces = 2, Increment = 0.1m, Value = 1.50m };
                numTakeProfit.ForeColor = Color.FromArgb(74, 222, 128); // Green

                var lblSL = CreateLabel("止损比例 (%):", 15, 417);
                numStopLoss = new NumericUpDown { Location = new Point(130, 414), Width = 210, Minimum = 0.1m, Maximum = 100m, DecimalPlaces = 2, Increment = 0.1m, Value = 0.50m };
                numStopLoss.ForeColor = Color.FromArgb(244, 63, 94); // Red

                var lblLineWidth = CreateLabel("趋势线线宽 (px):", 15, 445);
                numLineWidth = new NumericUpDown { Location = new Point(130, 442), Width = 210, Minimum = 0.1m, Maximum = 10.0m, DecimalPlaces = 1, Increment = 0.1m, Value = 0.8m };
                numLineWidth.ForeColor = Color.FromArgb(56, 189, 248); // Sky Blue
                numLineWidth.ValueChanged += (s, e) => RedrawCurrentPlot(autoScale: false);

                chkEnableTrading = new CheckBox
                {
                    Text = "开启策略交易 (触碰回弹/紫色穿透/止盈止损)",
                    Location = new Point(15, 472),
                    Width = 320,
                    Checked = true,
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                    ForeColor = Color.FromArgb(74, 222, 128)
                };

                chkEnableTickStopLoss = new CheckBox
                {
                    Text = "以 Tick 级别止损 (5-Tick 点位 / 取消为固定止损)",
                    Location = new Point(15, 497),
                    Width = 320,
                    Checked = true
                };

                chkStrictEnvelope = new CheckBox { Text = "严格外包络 (禁止内部穿透)", Location = new Point(15, 522), Width = 320, Checked = true };

                chkRealtimeChart = new CheckBox { Text = "实时推送图表走势 (UI 定时刷新)", Location = new Point(15, 547), Width = 320, Checked = true };
                chkRealtimeChart.CheckedChanged += (s, e) => _isRealtimeChartEnabled = chkRealtimeChart.Checked;

                chkAutoScale = new CheckBox { Text = "回放时自动调节 X/Y 轴 (Auto-Scale)", Location = new Point(15, 572), Width = 320, Checked = true };
                chkAutoScale.CheckedChanged += (s, e) => _isAutoScaleEnabled = chkAutoScale.Checked;

                chkEnableUiLogs = new CheckBox { Text = "输出界面实时日志 (取消勾选可防卡顿并提速)", Location = new Point(15, 597), Width = 320, Checked = true };
                chkEnableUiLogs.CheckedChanged += (s, e) => _isUiLogEnabled = chkEnableUiLogs.Checked;

                chkShowTpSl = new CheckBox
                {
                    Text = "🎯 显示止盈止损线 (TP/SL 价格与平仓点)",
                    Location = new Point(15, 622),
                    Width = 320,
                    Checked = true,
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                    ForeColor = Color.FromArgb(250, 204, 21) // Yellow 400
                };
                chkShowTpSl.CheckedChanged += (s, e) => RedrawCurrentPlot(autoScale: false);

                chkShowPivots = new CheckBox
                {
                    Text = "📍 显示波段极值高低点 (Peaks / Valleys)",
                    Location = new Point(15, 647),
                    Width = 320,
                    Checked = true,
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                    ForeColor = Color.FromArgb(56, 189, 248) // Sky Blue 400
                };
                chkShowPivots.CheckedChanged += (s, e) => RedrawCurrentPlot(autoScale: false);

                chkShowTrendLines = new CheckBox
                {
                    Text = "📐 显示趋势线与通道 (TrendLines / Channels)",
                    Location = new Point(15, 672),
                    Width = 320,
                    Checked = true,
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                    ForeColor = Color.FromArgb(249, 115, 22) // Orange 500
                };
                chkShowTrendLines.CheckedChanged += (s, e) => RedrawCurrentPlot(autoScale: false);

                var lblStrategyNote = new Label
                {
                    Text = "🎯 策略: 触碰回弹 / 紫色穿透(1m穿透跌破开多/突破开空)",
                    Location = new Point(15, 700),
                    Size = new Size(325, 36),
                    ForeColor = Color.FromArgb(74, 222, 128), // Green 400
                    Font = new Font("Microsoft YaHei", 8F)
                };

                grpStrategy.Controls.AddRange(new Control[] {
                    lblStrategy, cboTradeStrategy,
                    lblAlgorithm, cboPivotAlgorithm,
                    lblLeft, numLeftLen, lblRight, numRightLen,
                    lblZigZagDev, numZigZagDeviation, lblZigZagDepth, numZigZagDepth,
                    lblMaxK, numMaxKlines, lblMinT, numMinTrendLines,
                    lblSpan, numMaxSpan, lblSignalSpan, numMinSignalSpan,
                    lblSignalAge, numMinSignalAge, lblMinSlope, numMinSlope,
                    lblCooldown, numCooldown, lblTP, numTakeProfit,
                    lblSL, numStopLoss, lblLineWidth, numLineWidth,
                    chkEnableTrading, chkEnableTickStopLoss, chkStrictEnvelope,
                    chkRealtimeChart, chkAutoScale, chkEnableUiLogs,
                    chkShowTpSl, chkShowPivots, chkShowTrendLines, lblStrategyNote
                });
            }
            grpStrategy.Height = 745;
            panelRight.Controls.Add(grpStrategy);
            top += grpStrategy.Height + 10;

            // Group 3: 控制按钮与进度条
            grpControl = CreateGroupBox("3. 执行控制与测试报告", top, 210);
            {
                btnStart = new Button
                {
                    Text = "▶ 开始",
                    Location = new Point(15, 25),
                    Size = new Size(102, 36),
                    BackColor = Color.FromArgb(5, 150, 105), // Green 600
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Microsoft YaHei", 9.5F, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnStart.FlatAppearance.BorderSize = 0;
                btnStart.Click += async (s, e) => await StartBacktestAsync();

                btnPause = new Button
                {
                    Text = "⏸ 暂停",
                    Location = new Point(124, 25),
                    Size = new Size(102, 36),
                    BackColor = Color.FromArgb(217, 119, 6), // Amber 600
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Microsoft YaHei", 9.5F, FontStyle.Bold),
                    Enabled = false,
                    Cursor = Cursors.Hand
                };
                btnPause.FlatAppearance.BorderSize = 0;
                btnPause.Click += (s, e) => TogglePause();

                btnStop = new Button
                {
                    Text = "⏹ 停止",
                    Location = new Point(233, 25),
                    Size = new Size(107, 36),
                    BackColor = Color.FromArgb(220, 38, 38), // Red 600
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Microsoft YaHei", 9.5F, FontStyle.Bold),
                    Enabled = false,
                    Cursor = Cursors.Hand
                };
                btnStop.FlatAppearance.BorderSize = 0;
                btnStop.Click += (s, e) => StopBacktest();

                btnResetAxes = new Button
                {
                    Text = "🔍 复位坐标轴",
                    Location = new Point(15, 68),
                    Size = new Size(102, 30),
                    BackColor = Color.FromArgb(14, 116, 144), // Cyan 700
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand
                };
                btnResetAxes.FlatAppearance.BorderSize = 0;
                btnResetAxes.Click += (s, e) =>
                {
                    formsPlot.Plot.Axes.Margins(0.02, 0.08);
                    formsPlot.Plot.Axes.AutoScale();
                    formsPlot.Refresh();
                };

                btnExportChart = new Button
                {
                    Text = "🖼 走势图表",
                    Location = new Point(124, 68),
                    Size = new Size(102, 30),
                    BackColor = Color.FromArgb(37, 99, 235), // Blue 600
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand
                };
                btnExportChart.FlatAppearance.BorderSize = 0;
                btnExportChart.Click += (s, e) => OpenOrExportChart();

                btnOpenReport = new Button
                {
                    Text = "📊 HTML报告",
                    Location = new Point(233, 68),
                    Size = new Size(107, 30),
                    BackColor = Color.FromArgb(124, 58, 237), // Purple 600
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnOpenReport.FlatAppearance.BorderSize = 0;
                btnOpenReport.Click += (s, e) => OpenHtmlReport();

                btnClearLogs = new Button
                {
                    Text = "🗑 清空日志",
                    Location = new Point(15, 104),
                    Size = new Size(325, 26),
                    BackColor = Color.FromArgb(71, 85, 105), // Slate 600
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand
                };
                btnClearLogs.FlatAppearance.BorderSize = 0;
                btnClearLogs.Click += (s, e) => txtLogs.Clear();

                progressBar = new ProgressBar
                {
                    Location = new Point(15, 136),
                    Size = new Size(325, 18),
                    Minimum = 0,
                    Maximum = 100,
                    Value = 0
                };

                lblProgress = new Label
                {
                    Text = "系统就绪，点击【▶ 开始】启动回测",
                    Location = new Point(15, 160),
                    Size = new Size(325, 30),
                    ForeColor = Color.FromArgb(148, 163, 184)
                };

                grpControl.Controls.AddRange(new Control[] { btnStart, btnPause, btnStop, btnResetAxes, btnExportChart, btnOpenReport, btnClearLogs, progressBar, lblProgress });
            }
            panelRight.Controls.Add(grpControl);
            top += grpControl.Height + 10;

            // Group 4: 实时统计看板 (增加平仓交易与收益率统计指标)
            grpStatus = CreateGroupBox("4. 统计监控看板", top, 255);
            {
                lblStatTime = CreateStatLabel("执行耗时: -", 15, 25);
                lblStatThroughput = CreateStatLabel("吞吐速率: -", 15, 48);
                lblStatTrades = CreateStatLabel("交易战绩: 完成 0 笔 | 胜率: 0.0% | 盈亏: 0.00%", 15, 71);
                lblStatTrades.ForeColor = Color.FromArgb(74, 222, 128); // Green 400
                lblStatSignals = CreateStatLabel("开仓信号: 多单 0 | 空单 0 (总计 0)", 15, 94);
                lblStatSignals.ForeColor = Color.FromArgb(232, 121, 249); // Fuchsia 400
                lblStatKlines = CreateStatLabel("K线总量: -", 15, 117);
                lblStatTicks = CreateStatLabel("Tick总量: -", 15, 140);
                lblStatPeaksValleys = CreateStatLabel("识别极值: 高点 0 | 低点 0", 15, 163);
                lblStatActiveLines = CreateStatLabel("活跃趋势线: 阻力 0 | 支撑 0", 15, 186);
                lblStatDeletedLines = CreateStatLabel("已击穿删除: 0 条", 15, 209);

                grpStatus.Controls.AddRange(new Control[] { lblStatTime, lblStatThroughput, lblStatTrades, lblStatSignals, lblStatKlines, lblStatTicks, lblStatPeaksValleys, lblStatActiveLines, lblStatDeletedLines });
            }
            panelRight.Controls.Add(grpStatus);
        }

        private GroupBox CreateGroupBox(string text, int top, int height)
        {
            return new GroupBox
            {
                Text = text,
                Location = new Point(10, top),
                Size = new Size(355, height),
                ForeColor = Color.FromArgb(248, 250, 252),
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
                Size = new Size(330, 20),
                ForeColor = Color.FromArgb(56, 189, 248), // Sky 400
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular)
            };
        }

        #endregion

        #region 界面配置自动持久化与加载恢复

        private void LoadSettingsToUi()
        {
            var settings = UiSettingsManager.Load();

            if (!string.IsNullOrEmpty(settings.Coin))
            {
                int coinIdx = cboCoin.FindStringExact(settings.Coin);
                if (coinIdx >= 0) cboCoin.SelectedIndex = coinIdx;
                else cboCoin.Text = settings.Coin;
            }

            if (!string.IsNullOrEmpty(settings.Interval))
            {
                int intIdx = cboInterval.FindString(settings.Interval);
                if (intIdx >= 0) cboInterval.SelectedIndex = intIdx;
                else cboInterval.Text = settings.Interval;
            }

            if (settings.StartDate >= dtpStart.MinDate && settings.StartDate <= dtpStart.MaxDate)
                dtpStart.Value = settings.StartDate;

            if (settings.EndDate >= dtpEnd.MinDate && settings.EndDate <= dtpEnd.MaxDate)
                dtpEnd.Value = settings.EndDate;

            cboTradeStrategy.SelectedIndex = Math.Clamp(settings.TradeStrategy, 0, 2);
            cboPivotAlgorithm.SelectedIndex = Math.Clamp(settings.PivotAlgorithm, 0, 1);
            numZigZagDeviation.Value = Math.Clamp(settings.ZigZagDeviationPct > 0 ? settings.ZigZagDeviationPct : 1.0m, numZigZagDeviation.Minimum, numZigZagDeviation.Maximum);
            numZigZagDepth.Value = Math.Clamp(settings.ZigZagDepth > 0 ? settings.ZigZagDepth : 5, numZigZagDepth.Minimum, numZigZagDepth.Maximum);

            numMaxKlines.Value = Math.Clamp(settings.MaxKlinesCapacity, numMaxKlines.Minimum, numMaxKlines.Maximum);
            numMinTrendLines.Value = Math.Clamp(settings.MinTrendLinesCapacity, numMinTrendLines.Minimum, numMinTrendLines.Maximum);
            numLeftLen.Value = Math.Clamp(settings.LeftLen, numLeftLen.Minimum, numLeftLen.Maximum);
            numRightLen.Value = Math.Clamp(settings.RightLen, numRightLen.Minimum, numRightLen.Maximum);
            numMaxSpan.Value = Math.Clamp(settings.MaxSpan, numMaxSpan.Minimum, numMaxSpan.Maximum);
            numMinSignalSpan.Value = Math.Clamp(settings.MinSignalLineX1X2, numMinSignalSpan.Minimum, numMinSignalSpan.Maximum);
            numMinSignalAge.Value = Math.Clamp(settings.MinSignalLineAge, numMinSignalAge.Minimum, numMinSignalAge.Maximum);
            numMinSlope.Value = Math.Clamp(settings.MinSignalOverallSlopePct >= 0 ? settings.MinSignalOverallSlopePct : 0.50m, numMinSlope.Minimum, numMinSlope.Maximum);
            numCooldown.Value = Math.Clamp(settings.SignalCooldownSeconds, numCooldown.Minimum, numCooldown.Maximum);
            numTakeProfit.Value = Math.Clamp(settings.TakeProfitPct > 0 ? settings.TakeProfitPct : 1.5m, numTakeProfit.Minimum, numTakeProfit.Maximum);
            numStopLoss.Value = Math.Clamp(settings.StopLossPct > 0 ? settings.StopLossPct : 0.5m, numStopLoss.Minimum, numStopLoss.Maximum);
            numLineWidth.Value = Math.Clamp(settings.LineWidth > 0 ? settings.LineWidth : 0.8m, numLineWidth.Minimum, numLineWidth.Maximum);

            chkEnableTrading.Checked = settings.EnableTrading;
            chkEnableTickStopLoss.Checked = settings.EnableTickStopLoss;
            chkStrictEnvelope.Checked = settings.StrictEnvelope;
            chkRealtimeChart.Checked = settings.RealtimeChart;
            chkAutoScale.Checked = settings.AutoScale;
            chkEnableUiLogs.Checked = settings.EnableUiLogs;
            chkShowTpSl.Checked = settings.ShowTpSl;
            chkShowPivots.Checked = settings.ShowPivots;
            chkShowTrendLines.Checked = settings.ShowTrendLines;

            _isRealtimeChartEnabled = chkRealtimeChart.Checked;
            _isAutoScaleEnabled = chkAutoScale.Checked;
            _isUiLogEnabled = chkEnableUiLogs.Checked;

            // 窗体尺寸与状态恢复
            if (settings.FormWidth >= this.MinimumSize.Width && settings.FormHeight >= this.MinimumSize.Height)
            {
                this.Size = new Size(settings.FormWidth, settings.FormHeight);
            }
            if (settings.IsMaximized)
            {
                this.WindowState = FormWindowState.Maximized;
            }

            try
            {
                if (settings.SplitMainDistance > 100 && settings.SplitMainDistance < splitMain.Width - 100)
                {
                    splitMain.SplitterDistance = settings.SplitMainDistance;
                }
                else
                {
                    splitMain.SplitterDistance = Math.Max(200, this.ClientSize.Width - 380);
                }

                if (settings.SplitLeftDistance > 100 && settings.SplitLeftDistance < splitLeft.Height - 50)
                {
                    splitLeft.SplitterDistance = settings.SplitLeftDistance;
                }
                else
                {
                    splitLeft.SplitterDistance = (int)(splitLeft.Height * 0.65);
                }
            }
            catch
            {
                // 忽略分割栏初次调整异常
            }
        }

        private void SaveSettingsFromUi()
        {
            try
            {
                var settings = new UiSettings
                {
                    Coin = cboCoin.SelectedItem?.ToString() ?? cboCoin.Text,
                    Interval = cboInterval.SelectedItem?.ToString() ?? cboInterval.Text,
                    StartDate = dtpStart.Value.Date,
                    EndDate = dtpEnd.Value.Date,
                    TradeStrategy = cboTradeStrategy.SelectedIndex,
                    PivotAlgorithm = cboPivotAlgorithm.SelectedIndex,
                    ZigZagDeviationPct = numZigZagDeviation.Value,
                    ZigZagDepth = (int)numZigZagDepth.Value,
                    MaxKlinesCapacity = (int)numMaxKlines.Value,
                    MinTrendLinesCapacity = (int)numMinTrendLines.Value,
                    LeftLen = (int)numLeftLen.Value,
                    RightLen = (int)numRightLen.Value,
                    MaxSpan = (int)numMaxSpan.Value,
                    MinSignalLineX1X2 = (int)numMinSignalSpan.Value,
                    MinSignalLineAge = (int)numMinSignalAge.Value,
                    MinSignalOverallSlopePct = numMinSlope.Value,
                    SignalCooldownSeconds = (int)numCooldown.Value,
                    TakeProfitPct = numTakeProfit.Value,
                    StopLossPct = numStopLoss.Value,
                    LineWidth = numLineWidth.Value,
                    EnableTrading = chkEnableTrading.Checked,
                    EnableTickStopLoss = chkEnableTickStopLoss.Checked,
                    StrictEnvelope = chkStrictEnvelope.Checked,
                    RealtimeChart = chkRealtimeChart.Checked,
                    AutoScale = chkAutoScale.Checked,
                    EnableUiLogs = chkEnableUiLogs.Checked,
                    ShowTpSl = chkShowTpSl.Checked,
                    ShowPivots = chkShowPivots.Checked,
                    ShowTrendLines = chkShowTrendLines.Checked,
                    FormWidth = this.WindowState == FormWindowState.Normal ? this.Width : this.RestoreBounds.Width,
                    FormHeight = this.WindowState == FormWindowState.Normal ? this.Height : this.RestoreBounds.Height,
                    IsMaximized = this.WindowState == FormWindowState.Maximized,
                    SplitMainDistance = splitMain.SplitterDistance,
                    SplitLeftDistance = splitLeft.SplitterDistance
                };

                UiSettingsManager.Save(settings);
            }
            catch (Exception ex)
            {
                Logger.Log($"[MainForm] 保存界面配置失败: {ex.Message}");
            }
        }

        #endregion

        #region UI 定时批量刷新引擎 (彻底消除 Windows 消息风暴与界面卡死)

        private void SetupUiRefreshTimer()
        {
            _uiRefreshTimer = new System.Windows.Forms.Timer
            {
                Interval = 60 // 约 16 FPS
            };
            _uiRefreshTimer.Tick += OnUiRefreshTimerTick;
            _uiRefreshTimer.Start();
        }

        private void OnUiRefreshTimerTick(object? sender, EventArgs e)
        {
            // 1. 批量消费日志队列 (积压熔断保护：当队列堆积过多时丢弃超额数据，杜绝消息泵卡死)
            if (_logQueue.Count > 200)
            {
                while (_logQueue.Count > 30) _logQueue.TryDequeue(out _);
            }

            int logDrainCount = 0;
            while (_logQueue.TryDequeue(out var item) && logDrainCount < 15)
            {
                AppendLogInternal(item.Message, item.Color);
                logDrainCount++;
            }

            // 2. 刷新进度条与统计面板
            var p = _latestProgress;
            if (p != null)
            {
                progressBar.Value = (int)Math.Clamp(p.Percentage, 0, 100);
                lblProgress.Text = p.Message;
                string sign = p.CurrentTotalPnLPct >= 0 ? "+" : "";
                lblStatTrades.Text = $"交易战绩: 完成 {p.CompletedTradesCount} 笔 | 胜率: {p.WinRate:F1}% | 盈亏: {sign}{p.CurrentTotalPnLPct:F2}%";
                lblStatSignals.Text = $"开仓信号: 多单 {p.LongSignalsCount} | 空单 {p.ShortSignalsCount} (总计 {p.TotalSignalsCount})";
                lblStatKlines.Text = $"K线总量: {p.ProcessedKlines:N0} / {p.TotalKlines:N0}";
                lblStatTicks.Text = $"Tick总量: {p.ProcessedTicks:N0} / {p.TotalTicks:N0}";
                lblStatActiveLines.Text = $"活跃趋势线: 阻力 {p.ActiveResistanceCount} | 支撑 {p.ActiveSupportCount}";
                lblStatDeletedLines.Text = $"已击穿删除: {p.DeletedLinesCount} 条";
            }

            // 3. 刷新 ScottPlot 图表 (含绿色触发趋势线与交易信号标记)
            var snap = _latestChartSnapshot;
            if (_isRealtimeChartEnabled && snap != null && snap.SnapshotVersion > _renderedSnapshotVersion)
            {
                _renderedSnapshotVersion = snap.SnapshotVersion;
                try
                {
                    if (!formsPlot.IsDisposed && snap.Klines.Length > 0)
                    {
                        lock (_plottedLinesLock)
                        {
                            _currentPlottedLines = snap.Lines != null ? new List<TrendLine>(snap.Lines) : new List<TrendLine>();
                            _currentPlotStartGlobalIndex = snap.StartGlobalIndex;
                            _currentPlotEndGlobalIndex = snap.StartGlobalIndex + snap.Klines.Length - 1;
                        }

                        PlotHelper.BuildPlot(
                            formsPlot.Plot,
                            snap.Klines,
                            snap.Peaks,
                            snap.Valleys,
                            snap.Lines,
                            snap.Summary,
                            title: snap.Title,
                            startGlobalIndex: snap.StartGlobalIndex,
                            autoScaleAxes: snap.AutoScale,
                            tradeSignals: snap.TradeSignals,
                            lineWidth: snap.LineWidth,
                            selectedTrendLine: _selectedTrendLine,
                            selectedKlineIndex: _selectedKlineIndex,
                            chartType: _chartType,
                            completedTrades: snap.CompletedTrades,
                            activePositions: snap.ActivePositions,
                            showTpSl: chkShowTpSl.Checked,
                            minLineX1X2: (int)numMinSignalSpan.Value,
                            minLineAge: (int)numMinSignalAge.Value,
                            showPivots: chkShowPivots.Checked,
                            showTrendLines: chkShowTrendLines.Checked);

                        formsPlot.Refresh();
                    }
                }
                catch
                {
                    // 忽略刷新异常
                }
            }
        }

        #endregion

        #region 事件绑定与暗黑主题

        private void BindEngineEvents()
        {
            _engineService.OnLogMessage += message =>
            {
                if (_isUiLogEnabled || message.Contains("[启动回测]") || message.Contains("完成") || message.Contains("异常") || message.Contains("错误"))
                {
                    _logQueue.Enqueue((message, Color.FromArgb(241, 245, 249)));
                }
            };

            _engineService.OnTradeSignalGenerated += signal =>
            {
                if (_isUiLogEnabled)
                {
                    Color sigColor = signal.Side == TradeSide.Buy ? Color.FromArgb(74, 222, 128) : Color.FromArgb(244, 63, 94);
                    _logQueue.Enqueue(("\n------------------------------------------------------------", Color.FromArgb(74, 222, 128)));
                    _logQueue.Enqueue((signal.ToString(), sigColor));
                    _logQueue.Enqueue(($"  📌 [趋势线详情] {signal.Reason}", Color.FromArgb(226, 232, 240)));
                    _logQueue.Enqueue(("------------------------------------------------------------\n", Color.FromArgb(74, 222, 128)));
                }
            };

            _engineService.OnPositionOpened += pos =>
            {
                if (_isUiLogEnabled)
                {
                    string side = pos.Side == TradeSide.Buy ? "多单" : "空单";
                    decimal slDistPct = pos.EntryPrice > 0 ? Math.Abs((pos.StopLossPrice - pos.EntryPrice) / pos.EntryPrice * 100m) : 0m;
                    decimal tpDistPct = pos.EntryPrice > 0 ? Math.Abs((pos.TakeProfitPrice - pos.EntryPrice) / pos.EntryPrice * 100m) : 0m;
                    _logQueue.Enqueue(($"📥 [开立仓位] #{pos.PositionId} {side} @ 进场价:{pos.EntryPrice:F2} | 止盈目标:{pos.TakeProfitPrice:F2} (+{tpDistPct:F2}%), 5-Tick自动微止损:{pos.StopLossPrice:F2} (-{slDistPct:F3}%)", Color.FromArgb(56, 189, 248)));
                }
            };

            _engineService.OnTradeClosed += trade =>
            {
                if (_isUiLogEnabled)
                {
                    Color tradeColor = trade.IsWin ? Color.FromArgb(74, 222, 128) : Color.FromArgb(244, 63, 94);
                    _logQueue.Enqueue((trade.ToString(), tradeColor));
                }
            };

            _engineService.OnKlineClosed += (kline, index, strategy) =>
            {
                // ⚡ 实时图表快照提取 (100ms 节流提取，UI 定时器异步渲染，杜绝高频日志卡顿)
                if (_isRealtimeChartEnabled)
                {
                    long now = Environment.TickCount64;
                    if (now - _lastChartSnapshotTicks >= 100)
                    {
                        _lastChartSnapshotTicks = now;

                        var klinesSnapshot = strategy.Klines.ToArray();
                        var peaksSnapshot = strategy.Peaks.ToArray();
                        var valleysSnapshot = strategy.Valleys.ToArray();

                        var linesSnapshot = new List<TrendLine>(150);
                        if (strategy.ActiveResistanceLines.Count > 0) linesSnapshot.AddRange(strategy.ActiveResistanceLines);
                        if (strategy.ActiveSupportLines.Count > 0) linesSnapshot.AddRange(strategy.ActiveSupportLines);

                        int delCount = strategy.DeletedTrendLines.Count;
                        int takeDel = Math.Min(60, delCount);
                        for (int i = delCount - takeDel; i < delCount; i++)
                        {
                            linesSnapshot.Add(strategy.DeletedTrendLines[i]);
                        }

                        int sigCount = strategy.TradeSignals.Count;
                        int takeSig = Math.Min(100, sigCount);
                        var signalsSnapshot = new TradeSignal[takeSig];
                        for (int i = 0; i < takeSig; i++)
                        {
                            signalsSnapshot[i] = strategy.TradeSignals[sigCount - takeSig + i];
                        }

                        int startGlobal = Math.Max(0, strategy.GlobalBarIndex - strategy.KlineCount);
                        string coin = _currentRunningCoin;
                        string intervalStr = _currentRunningInterval;
                        bool autoScale = _isAutoScaleEnabled;

                        string sign = strategy.TotalPnLPct >= 0 ? "+" : "";
                        string realtimeSummary = $"实时回测推进中: {coin} {intervalStr} | 当前 K 线: #{index:D4} (最新收: {kline.Close:F2})\n" +
                                                 $"交易战绩: {strategy.CompletedTrades.Count}笔 (胜率:{strategy.WinRate:F1}%, 盈亏:{sign}{strategy.TotalPnLPct:F2}%) | 开仓信号: {strategy.TotalSignalsCount}笔\n" +
                                                 $"识别极值: 高(L1:{strategy.PeaksL1.Count}/L2:{strategy.PeaksL2.Count}/L3:{strategy.PeaksL3.Count}) | 低(L1:{strategy.ValleysL1.Count}/L2:{strategy.ValleysL2.Count}/L3:{strategy.ValleysL3.Count}) | 活跃阻力={strategy.ActiveResistanceLines.Count}, 支撑={strategy.ActiveSupportLines.Count}";

                        _latestChartSnapshot = new ChartSnapshot
                        {
                            Klines = klinesSnapshot,
                            Peaks = peaksSnapshot,
                            Valleys = valleysSnapshot,
                            Lines = linesSnapshot,
                            TradeSignals = signalsSnapshot,
                            CompletedTrades = strategy.CompletedTrades.ToArray(),
                            ActivePositions = strategy.ActivePositions.ToArray(),
                            Summary = realtimeSummary,
                            Title = $"{coin} {intervalStr} - 实时回测动态走势 (K线 #{index:D4})",
                            StartGlobalIndex = startGlobal,
                            AutoScale = autoScale,
                            LineWidth = (float)numLineWidth.Value,
                            SnapshotVersion = now
                        };
                    }
                }
            };

            _engineService.OnTrendLinePenetrated += (line, tick, reason) =>
            {
                // 静默处理趋势线穿透，数据已在统计面板与图表实时反映
            };

            _engineService.OnProgressChanged += progress =>
            {
                _latestProgress = progress;
            };
        }

        private void ApplyDarkTheme()
        {
            this.BackColor = Color.FromArgb(15, 23, 42); // Slate 900
            this.ForeColor = Color.FromArgb(248, 250, 252);
        }

        private void InitializeDefaultPlot()
        {
            string chineseFont = PlotHelper.GetInstalledChineseFont();
            formsPlot.Plot.Clear();
            formsPlot.Plot.FigureBackground.Color = ScottPlot.Color.FromHex("#0f172a");
            formsPlot.Plot.DataBackground.Color = ScottPlot.Color.FromHex("#1e293b");
            formsPlot.Plot.Axes.Color(ScottPlot.Color.FromHex("#94a3b8"));
            formsPlot.Plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#334155");

            formsPlot.Plot.Title("等待回测启动，点击【▶ 开始】加载实时折线图与开仓信号...", size: 16);
            formsPlot.Plot.Axes.Title.Label.FontName = chineseFont;
            formsPlot.Plot.Axes.Title.Label.ForeColor = ScottPlot.Color.FromHex("#f8fafc");

            formsPlot.Plot.Axes.Bottom.Label.Text = "全局 K 线序列号 (Global Bar Index)";
            formsPlot.Plot.Axes.Bottom.Label.FontName = chineseFont;
            formsPlot.Plot.Axes.Bottom.Label.ForeColor = ScottPlot.Color.FromHex("#cbd5e1");

            formsPlot.Plot.Axes.Left.Label.Text = "价格 (USDT)";
            formsPlot.Plot.Axes.Left.Label.FontName = chineseFont;
            formsPlot.Plot.Axes.Left.Label.ForeColor = ScottPlot.Color.FromHex("#cbd5e1");

            formsPlot.Refresh();
        }

        #endregion

        #region 回测执行核心逻辑

        private async Task StartBacktestAsync()
        {
            if (dtpStart.Value.Date > dtpEnd.Value.Date)
            {
                MessageBox.Show("起始日期不能大于结束日期！", "参数错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 自动保存当前界面配置
            SaveSettingsFromUi();

            // 锁定按钮状态
            btnStart.Enabled = false;
            btnPause.Enabled = true;
            btnPause.Text = "⏸ 暂停";
            btnPause.BackColor = Color.FromArgb(217, 119, 6); // Amber 600
            btnStop.Enabled = true;

            _cts = new CancellationTokenSource();
            _lastChartSnapshotTicks = 0;
            _renderedSnapshotVersion = 0;
            _latestChartSnapshot = null;
            _latestProgress = null;

            // 初始化重置看板战绩
            lblStatTrades.Text = "交易战绩: 完成 0 笔 | 胜率: 0.0% | 盈亏: 0.00%";
            lblStatSignals.Text = "开仓信号: 多单 0 | 空单 0 (总计 0)";
            lblStatKlines.Text = "K线总量: -";
            lblStatTicks.Text = "Tick总量: -";
            lblStatActiveLines.Text = "活跃趋势线: 阻力 0 | 支撑 0";
            lblStatDeletedLines.Text = "已击穿删除: 0 条";

            // 解析 K 线周期
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

            var request = new BacktestRequest
            {
                Coin = cboCoin.SelectedItem?.ToString() ?? "BTCUSDT",
                StartDate = dtpStart.Value.Date,
                EndDate = dtpEnd.Value.Date,
                Interval = interval,
                TradeStrategy = (TradeStrategyType)cboTradeStrategy.SelectedIndex,
                PivotAlgorithm = (PivotAlgorithmType)cboPivotAlgorithm.SelectedIndex,
                ZigZagDeviationPct = numZigZagDeviation.Value,
                ZigZagDepth = (int)numZigZagDepth.Value,
                MaxKlinesCapacity = (int)numMaxKlines.Value,
                MinTrendLinesCapacity = (int)numMinTrendLines.Value,
                LeftLen = (int)numLeftLen.Value,
                RightLen = (int)numRightLen.Value,
                MaxSpan = (int)numMaxSpan.Value,
                MinSignalLineX1X2 = (int)numMinSignalSpan.Value,
                MinSignalLineAge = (int)numMinSignalAge.Value,
                MinSignalOverallSlopePct = numMinSlope.Value,
                SignalCooldownSeconds = (int)numCooldown.Value,
                TakeProfitPct = numTakeProfit.Value,
                StopLossPct = numStopLoss.Value,
                LineWidth = numLineWidth.Value,
                EnableTrading = chkEnableTrading.Checked,
                EnableTickStopLoss = chkEnableTickStopLoss.Checked,
                AllowInternalPenetration = !chkStrictEnvelope.Checked,
                ParallelDays = 3,
                GenerateChart = true,
                GenerateHtmlReport = true
            };

            // 缓存运行态变量供后台线程安全读取
            _isRealtimeChartEnabled = chkRealtimeChart.Checked;
            _isAutoScaleEnabled = chkAutoScale.Checked;
            _isUiLogEnabled = chkEnableUiLogs.Checked;
            _currentRunningCoin = request.Coin;
            _currentRunningInterval = interval.ToIntervalString();

            string algoDesc = request.PivotAlgorithm == PivotAlgorithmType.ZigZag
                ? $"【ZigZag之字转向】(反转幅度: {request.ZigZagDeviationPct:F2}%, 最小深度: {request.ZigZagDepth})"
                : $"【经典分形对比】(左侧: {request.LeftLen}根, 右侧: {request.RightLen}根)";

            string stratDesc = request.TradeStrategy switch
            {
                TradeStrategyType.PurpleBreakout => "🟣 紫色特殊趋势线穿透策略 (1m收盘跌破开空 / 突破开多)",
                TradeStrategyType.Level3FalseBreakout => "🎯 宏观Level 3假突破猎杀策略 (SFP / 刺破L3诱多诱空后持续3分钟确认开仓)",
                TradeStrategyType.Combined => "⚡ 多策略组合模式 (触碰回弹 + 紫色穿透 + 宏观L3假突破)",
                _ => "🎯 经典趋势线触碰回弹策略 (3点线 0.001%触碰 / 3分钟持续回弹确认开仓)"
            };

            string tradeModeStr = request.EnableTrading ? $"已开启 - {stratDesc}" : "已关闭 (仅纯结构与趋势线分析)";
            string slModeDesc = request.EnableTickStopLoss ? "5-Tick 级别微止损" : $"固定比例止损 (-{request.StopLossPct:F2}%)";
            _logQueue.Enqueue(("\n========================================================", Color.FromArgb(56, 189, 248)));
            _logQueue.Enqueue(($"[启动回测] 目标: {request.Coin}, 周期: {interval.ToIntervalString()}, 窗口: {request.StartDate:yyyy-MM-dd} ~ {request.EndDate:yyyy-MM-dd}", Color.FromArgb(56, 189, 248)));
            _logQueue.Enqueue(($"[交易策略] {stratDesc}", Color.FromArgb(56, 189, 248)));
            _logQueue.Enqueue(($"[极值算法] {algoDesc}", Color.FromArgb(232, 121, 249)));
            _logQueue.Enqueue(($"[策略模式] 交易开关: {tradeModeStr} | 止损模式: {slModeDesc} | 开仓整体斜率≥{request.MinSignalOverallSlopePct:F2}% | 线宽: {request.LineWidth:F1}px | 止盈: +{request.TakeProfitPct:F2}% | 冷却: {request.SignalCooldownSeconds}s", Color.FromArgb(250, 204, 21)));
            _logQueue.Enqueue(("========================================================", Color.FromArgb(56, 189, 248)));

            try
            {
                var progress = new Progress<BacktestProgress>();
                _latestResult = await Task.Run(() => _engineService.RunBacktestAsync(request, progress, _cts.Token));

                if (_latestResult.Success && _latestResult.Strategy != null)
                {
                    // 回测完成后执行最终 100% 完整图表渲染与统计对齐
                    var strat = _latestResult.Strategy;
                    int startGlobal = Math.Max(0, strat.GlobalBarIndex - strat.KlineCount);

                    var allLines = new List<TrendLine>();
                    if (strat.ActiveResistanceLines.Count > 0) allLines.AddRange(strat.ActiveResistanceLines);
                    if (strat.ActiveSupportLines.Count > 0) allLines.AddRange(strat.ActiveSupportLines);

                    int delCount = strat.DeletedTrendLines.Count;
                    int takeDel = Math.Min(100, delCount);
                    for (int i = delCount - takeDel; i < delCount; i++)
                    {
                        allLines.Add(strat.DeletedTrendLines[i]);
                    }

                    string sign = _latestResult.TotalPnLPct >= 0 ? "+" : "";
                    string summary = $"币种: {request.Coin}, 周期: {interval.ToIntervalString()}, 窗口: {request.StartDate:yyyy-MM-dd} ~ {request.EndDate:yyyy-MM-dd}\n" +
                                     $"交易战绩: {_latestResult.TotalTrades} 笔 (胜率: {_latestResult.WinRate:F1}%, 累计收益: {sign}{_latestResult.TotalPnLPct:F2}%) | 耗时: {_latestResult.ElapsedMilliseconds} ms";

                    lock (_plottedLinesLock)
                    {
                        _currentPlottedLines = new List<TrendLine>(allLines);
                        _currentPlotStartGlobalIndex = startGlobal;
                        _currentPlotEndGlobalIndex = startGlobal + strat.KlineCount - 1;
                    }

                    PlotHelper.BuildPlot(
                        formsPlot.Plot,
                        strat.Klines,
                        strat.Peaks,
                        strat.Valleys,
                        allLines,
                        summary,
                        title: $"{request.Coin} {interval.ToIntervalString()} 趋势线与极值结构折线图 (回测完成)",
                        startGlobalIndex: startGlobal,
                        autoScaleAxes: true,
                        tradeSignals: strat.TradeSignals,
                        lineWidth: (float)request.LineWidth,
                        selectedTrendLine: _selectedTrendLine,
                        selectedKlineIndex: _selectedKlineIndex,
                        chartType: _chartType,
                        completedTrades: strat.CompletedTrades,
                        activePositions: strat.ActivePositions,
                        showTpSl: chkShowTpSl.Checked,
                        minLineX1X2: (int)numMinSignalSpan.Value,
                        minLineAge: (int)numMinSignalAge.Value,
                        showPivots: chkShowPivots.Checked,
                        showTrendLines: chkShowTrendLines.Checked);

                    formsPlot.Refresh();

                    // 更新统计看板
                    lblStatTime.Text = $"执行耗时: {_latestResult.ElapsedMilliseconds:N0} ms";
                    lblStatThroughput.Text = $"吞吐速率: {_latestResult.TicksPerSecond:N0} ticks/s";
                    lblStatTrades.Text = $"交易战绩: 完成 {_latestResult.TotalTrades} 笔 | 胜率: {_latestResult.WinRate:F1}% | 盈亏: {sign}{_latestResult.TotalPnLPct:F2}%";
                    lblStatSignals.Text = $"开仓信号: 多单 {strat.LongSignalsCount} | 空单 {strat.ShortSignalsCount} (总计 {strat.TotalSignalsCount})";
                    lblStatPeaksValleys.Text = $"识别极值: 高(L1:{strat.PeaksL1.Count}/L2:{strat.PeaksL2.Count}/L3:{strat.PeaksL3.Count}) | 低(L1:{strat.ValleysL1.Count}/L2:{strat.ValleysL2.Count}/L3:{strat.ValleysL3.Count})";
                    lblStatActiveLines.Text = $"活跃趋势线: 阻力 {strat.ActiveResistanceLines.Count} | 支撑 {strat.ActiveSupportLines.Count}";
                    lblStatDeletedLines.Text = $"已击穿删除: {strat.DeletedTrendLinesCount} 条";

                    _logQueue.Enqueue(($"\n[回测成功] 耗时: {_latestResult.ElapsedMilliseconds} ms, 完成交易: {_latestResult.TotalTrades} 笔 (胜率: {_latestResult.WinRate:F1}%, 累计盈亏: {sign}{_latestResult.TotalPnLPct:F2}%)！", Color.FromArgb(74, 222, 128)));
                    if (!string.IsNullOrEmpty(_latestResult.ReportHtmlPath))
                    {
                        _logQueue.Enqueue(($"[HTML报告] 已生成落地: {_latestResult.ReportHtmlPath}", Color.FromArgb(192, 132, 252)));
                        _logQueue.Enqueue(($"👉 提示: 点击右侧面板【📊 HTML报告】即可直接在浏览器中查看交互式图表与逐笔明细！", Color.FromArgb(250, 204, 21)));
                    }
                }
                else
                {
                    _logQueue.Enqueue(($"\n[回测异常] {_latestResult?.ErrorMessage ?? "未知错误"}", Color.FromArgb(248, 113, 113)));
                }
            }
            catch (Exception ex)
            {
                _logQueue.Enqueue(($"[回测错误] {ex.Message}", Color.FromArgb(248, 113, 113)));
            }
            finally
            {
                btnStart.Enabled = true;
                btnPause.Enabled = false;
                btnPause.Text = "⏸ 暂停";
                btnPause.BackColor = Color.FromArgb(217, 119, 6);
                btnStop.Enabled = false;
                _cts = null;
            }
        }

        private void TogglePause()
        {
            if (_engineService.IsPaused)
            {
                _engineService.Resume();
                btnPause.Text = "⏸ 暂停";
                btnPause.BackColor = Color.FromArgb(217, 119, 6); // Amber 600
                lblProgress.Text = "回测已恢复继续运行...";
            }
            else
            {
                _engineService.Pause();
                btnPause.Text = "▶ 继续";
                btnPause.BackColor = Color.FromArgb(16, 185, 129); // Emerald 500
                lblProgress.Text = "回测已暂停，点击【▶ 继续】恢复推进";
            }
        }

        private void StopBacktest()
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                if (_engineService.IsPaused)
                {
                    _engineService.Resume();
                }

                _cts.Cancel();
                _logQueue.Enqueue(("[用户操作] 已发送停止回测请求...", Color.FromArgb(251, 146, 60)));
            }
        }

        private void OpenOrExportChart()
        {
            if (_latestResult != null && !string.IsNullOrEmpty(_latestResult.ChartPath) && File.Exists(_latestResult.ChartPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _latestResult.ChartPath,
                    UseShellExecute = true
                });
            }
            else
            {
                string chartsDir = Config.GetChartsPath();
                if (Directory.Exists(chartsDir))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = chartsDir,
                        UseShellExecute = true
                    });
                }
                else
                {
                    MessageBox.Show("尚未生成任何图表，请先执行回测！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        private void OpenHtmlReport()
        {
            //if (_latestResult != null && !string.IsNullOrEmpty(_latestResult.ReportHtmlPath) && File.Exists(_latestResult.ReportHtmlPath))
            //{
            //    Process.Start(new ProcessStartInfo
            //    {
            //        FileName = _latestResult.ReportHtmlPath,
            //        UseShellExecute = true
            //    });
            //}
            //else
            //{
            //    string reportsDir = Config.GetReportsPath();
            //    if (Directory.Exists(reportsDir))
            //    {
            //        Process.Start(new ProcessStartInfo
            //        {
            //            FileName = reportsDir,
            //            UseShellExecute = true
            //        });
            //    }
            //    else
            //    {
            //        MessageBox.Show("尚未生成任何 HTML 报告，请先执行回测！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            //    }
            //}
        }

        #endregion

        #region 日志内部安全写入 (仅在 UI 线程执行)

        private void AppendLogInternal(string message, Color color)
        {
            if (txtLogs.IsDisposed) return;

            if (txtLogs.TextLength > 20000)
            {
                txtLogs.Select(0, 8000);
                txtLogs.SelectedText = "";
            }

            txtLogs.SelectionStart = txtLogs.TextLength;
            txtLogs.SelectionLength = 0;
            txtLogs.SelectionColor = color;
            txtLogs.AppendText(message + "\n");
            txtLogs.ScrollToCaret();
        }

        #endregion

        #region 图表交互与趋势线点击特征输出

        private Point _plotMouseDownPos;
        private bool _isPlotMouseDown = false;

        private void OnFormsPlotMouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
            {
                _plotMouseDownPos = e.Location;
                _isPlotMouseDown = true;
            }
        }

        private void OnFormsPlotMouseUp(object? sender, MouseEventArgs e)
        {
            if (_isPlotMouseDown)
            {
                _isPlotMouseDown = false;
                int dx = Math.Abs(e.Location.X - _plotMouseDownPos.X);
                int dy = Math.Abs(e.Location.Y - _plotMouseDownPos.Y);

                // 鼠标微小位移判定为单击 (排除拖拽/缩放交互)
                if (dx <= 8 && dy <= 8)
                {
                    // 1. 优先尝试拾取趋势线
                    bool trendLineHit = HitTestTrendLine(e.X, e.Y);

                    // 2. 若未点击到趋势线，尝试拾取点击的 K 线
                    if (!trendLineHit)
                    {
                        HitTestKline(e.X, e.Y);
                    }
                }
            }
        }

        private void OnFormsPlotMouseMove(object? sender, MouseEventArgs e)
        {
            if (!_isPlotMouseDown)
            {
                bool isNear = IsNearAnyTrendLine(e.X, e.Y, 15.0);
                formsPlot.Cursor = isNear ? Cursors.Hand : Cursors.Default;
            }
        }

        private List<TrendLine> GetCandidateTrendLines()
        {
            var list = new List<TrendLine>();

            lock (_plottedLinesLock)
            {
                if (_currentPlottedLines != null && _currentPlottedLines.Count > 0)
                {
                    list.AddRange(_currentPlottedLines);
                    return list;
                }
            }

            if (_latestChartSnapshot?.Lines != null && _latestChartSnapshot.Lines.Count > 0)
            {
                list.AddRange(_latestChartSnapshot.Lines);
                return list;
            }

            if (_latestResult?.Strategy != null)
            {
                var strat = _latestResult.Strategy;
                if (strat.ActiveResistanceLines.Count > 0) list.AddRange(strat.ActiveResistanceLines);
                if (strat.ActiveSupportLines.Count > 0) list.AddRange(strat.ActiveSupportLines);
                if (strat.DeletedTrendLines.Count > 0) list.AddRange(strat.DeletedTrendLines);
            }

            return list;
        }

        private bool HitTestTrendLine(double mouseX, double mouseY)
        {
            if (!chkShowTrendLines.Checked) return false;

            var candidateLines = GetCandidateTrendLines();
            if (candidateLines == null || candidateLines.Count == 0) return false;

            double minDistance = double.MaxValue;
            TrendLine? selectedLine = null;
            double tolerancePixels = 20.0; // 20 像素舒适拾取容差

            var limits = formsPlot.Plot.Axes.GetLimits();
            double plotLeft = limits.Left;
            double plotRight = limits.Right;

            ScottPlot.Coordinates mouseCoord;
            try
            {
                mouseCoord = formsPlot.Plot.GetCoordinates(new ScottPlot.Pixel(mouseX, mouseY));
            }
            catch
            {
                mouseCoord = new ScottPlot.Coordinates(0, 0);
            }

            for (int i = candidateLines.Count - 1; i >= 0; i--)
            {
                var line = candidateLines[i];

                int effectiveEndX;
                if (line.CollidedKlineIndex >= 0)
                {
                    effectiveEndX = line.CollidedKlineIndex;
                }
                else
                {
                    effectiveEndX = Math.Max(line.X2, (int)Math.Ceiling(plotRight));
                }

                // 过滤完全不在视口范围内的趋势线
                if (effectiveEndX < plotLeft - 100 || line.X1 > plotRight + 100)
                    continue;

                // 过滤未满足界面参数跨度与寿命要求的趋势线
                int minSpan = (int)numMinSignalSpan.Value;
                int minAge = (int)numMinSignalAge.Value;
                int currentAge = Math.Max(line.LineAge, effectiveEndX - line.X2);
                if (line.LineX1X2 < minSpan || currentAge < minAge)
                    continue;

                double xStart = line.X1;
                double xEnd = effectiveEndX;
                double yStart = (double)line.Y1;
                double yEnd = (double)line.GetPriceAt(effectiveEndX);

                try
                {
                    var pixelA = formsPlot.Plot.GetPixel(new ScottPlot.Coordinates(xStart, yStart));
                    var pixelB = formsPlot.Plot.GetPixel(new ScottPlot.Coordinates(xEnd, yEnd));

                    double px = mouseX;
                    double py = mouseY;
                    double ax = pixelA.X;
                    double ay = pixelA.Y;
                    double bx = pixelB.X;
                    double by = pixelB.Y;

                    double dx = bx - ax;
                    double dy = by - ay;
                    double lenSq = dx * dx + dy * dy;

                    double dist;
                    if (lenSq < 1e-6)
                    {
                        dist = Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));
                    }
                    else
                    {
                        double t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lenSq, 0.0, 1.0);
                        double projX = ax + t * dx;
                        double projY = ay + t * dy;
                        dist = Math.Sqrt((px - projX) * (px - projX) + (py - projY) * (py - projY));
                    }

                    // 辅助检查：鼠标位于线段 X 跨度内时的垂直像素差距
                    if (mouseCoord.X >= line.X1 - 1.0 && mouseCoord.X <= effectiveEndX + 1.0)
                    {
                        double linePriceAtMouse = (double)line.Y1 + (double)line.RawK * (mouseCoord.X - line.X1);
                        var pixelAtMouse = formsPlot.Plot.GetPixel(new ScottPlot.Coordinates(mouseCoord.X, linePriceAtMouse));
                        double vertDist = Math.Abs(pixelAtMouse.Y - mouseY);
                        if (vertDist < dist)
                        {
                            dist = vertDist;
                        }
                    }

                    if (dist < minDistance && dist <= tolerancePixels)
                    {
                        minDistance = dist;
                        selectedLine = line;
                    }
                }
                catch
                {
                    // 忽略坐标换算异常
                }
            }

            if (selectedLine.HasValue)
            {
                _selectedTrendLine = selectedLine.Value;
                _selectedKlineIndex = null; // 清除选中的 K 线
                OutputTrendLineDetails(selectedLine.Value);

                // 立即在图表上以鲜亮红色重绘高亮选中的趋势线 (保持当前缩放与视口不变)
                RedrawCurrentPlot(autoScale: false);

                // 立即强制直接刷新写入至日志框 (免等待 Timer 轮询)
                while (_logQueue.TryDequeue(out var item))
                {
                    AppendLogInternal(item.Message, item.Color);
                }
                return true;
            }

            return false;
        }

        private bool HitTestKline(double mouseX, double mouseY)
        {
            try
            {
                RawKline[]? klines = null;
                int startGlobal = 0;

                var snap = _latestChartSnapshot;
                if (snap != null && snap.Klines.Length > 0)
                {
                    klines = snap.Klines;
                    startGlobal = snap.StartGlobalIndex;
                }
                else if (_latestResult?.Strategy != null && _latestResult.Strategy.KlineCount > 0)
                {
                    klines = _latestResult.Strategy.Klines.ToArray();
                    startGlobal = Math.Max(0, _latestResult.Strategy.GlobalBarIndex - _latestResult.Strategy.KlineCount);
                }

                if (klines == null || klines.Length == 0) return false;

                var mouseCoord = formsPlot.Plot.GetCoordinates(new ScottPlot.Pixel(mouseX, mouseY));
                int targetGlobalIndex = (int)Math.Round(mouseCoord.X);
                int localIndex = targetGlobalIndex - startGlobal;

                if (localIndex >= 0 && localIndex < klines.Length)
                {
                    var kline = klines[localIndex];

                    var pixelClose = formsPlot.Plot.GetPixel(new ScottPlot.Coordinates(targetGlobalIndex, (double)kline.Close));
                    var pixelHigh = formsPlot.Plot.GetPixel(new ScottPlot.Coordinates(targetGlobalIndex, (double)kline.High));
                    var pixelLow = formsPlot.Plot.GetPixel(new ScottPlot.Coordinates(targetGlobalIndex, (double)kline.Low));

                    double topY = Math.Min(pixelHigh.Y, pixelLow.Y) - 30;
                    double bottomY = Math.Max(pixelHigh.Y, pixelLow.Y) + 30;
                    double leftX = pixelClose.X - 25;
                    double rightX = pixelClose.X + 25;

                    if (mouseX >= leftX && mouseX <= rightX && mouseY >= topY && mouseY <= bottomY)
                    {
                        _selectedKlineIndex = targetGlobalIndex;
                        _selectedTrendLine = null; // 清除选中的趋势线

                        OutputKlineDetails(kline, targetGlobalIndex);
                        RedrawCurrentPlot(autoScale: false);
                        _ = DisplayKlineTickDetailsAsync(kline, targetGlobalIndex);

                        while (_logQueue.TryDequeue(out var item))
                        {
                            AppendLogInternal(item.Message, item.Color);
                        }
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private void OutputKlineDetails(RawKline kline, int globalIndex)
        {
            DateTime openTime = TimeHelper.FromUnixTimeMilliseconds(kline.OpenTime);
            DateTime closeTime = TimeHelper.FromUnixTimeMilliseconds(kline.CloseTime);

            decimal change = kline.Close - kline.Open;
            decimal changePct = kline.Open > 0 ? (change / kline.Open) * 100m : 0m;
            decimal amplitude = kline.Low > 0 ? ((kline.High - kline.Low) / kline.Low) * 100m : 0m;
            decimal body = Math.Abs(kline.Close - kline.Open);
            decimal upperShadow = kline.High - Math.Max(kline.Open, kline.Close);
            decimal lowerShadow = Math.Min(kline.Open, kline.Close) - kline.Low;

            bool isBull = kline.Close >= kline.Open;
            string barTypeStr = isBull ? "🟢 阳线 (Bullish)" : "🔴 阴线 (Bearish)";
            Color themeColor = isBull ? Color.FromArgb(74, 222, 128) : Color.FromArgb(244, 63, 94); // Green 400 / Rose 500

            // 检查极值高低点
            string pivotInfo = "普通K线 (无极值分型)";
            var strat = _latestResult?.Strategy;
            if (strat != null)
            {
                var peak = strat.Peaks.FirstOrDefault(p => p.Index == globalIndex);
                var valley = strat.Valleys.FirstOrDefault(v => v.Index == globalIndex);
                if (peak.Index == globalIndex)
                {
                    pivotInfo = $"🔺 【高点极值 Peak】Level {peak.Level} (高点价: {peak.Price:F2})";
                }
                else if (valley.Index == globalIndex)
                {
                    pivotInfo = $"🔻 【低点极值 Valley】Level {valley.Level} (低点价: {valley.Price:F2})";
                }
            }

            // 检查是否在此处产生交易信号或开平仓
            string tradeInfo = "无交易动作";
            if (strat != null)
            {
                var sig = strat.TradeSignals.FirstOrDefault(s => s.GlobalBarIndex == globalIndex);
                if (sig.GlobalBarIndex == globalIndex)
                {
                    tradeInfo = $"⚡ 产生开仓信号: {(sig.Side == TradeSide.Buy ? "🟢 开多" : "🔴 开空")} @ {sig.Price:F2} ({sig.Reason})";
                }

                var tradeEntry = strat.CompletedTrades.FirstOrDefault(t => t.EntryGlobalBarIndex == globalIndex);
                if (tradeEntry != null)
                {
                    tradeInfo += $"\n  📥 包含开仓: #{tradeEntry.TradeId} {(tradeEntry.Side == TradeSide.Buy ? "多单" : "空单")} @ {tradeEntry.EntryPrice:F2}";
                }

                var tradeExit = strat.CompletedTrades.FirstOrDefault(t => t.ExitGlobalBarIndex == globalIndex);
                if (tradeExit != null)
                {
                    string sign = tradeExit.PnLPct >= 0 ? "+" : "";
                    tradeInfo += $"\n  🏁 包含平仓: #{tradeExit.TradeId} {(tradeExit.IsWin ? "💰 止盈" : "🛑 止损")} @ {tradeExit.ExitPrice:F2} ({sign}{tradeExit.PnLPct:F2}%)";
                }
            }

            _logQueue.Enqueue(("\n========================================================", themeColor));
            _logQueue.Enqueue(($"📊 [选中K线行情详情] Bar #{globalIndex} 【{barTypeStr}】", themeColor));
            _logQueue.Enqueue(($"  • 时间周期: {openTime:yyyy-MM-dd HH:mm:ss} ~ {closeTime:HH:mm:ss} (UTC+0)", Color.FromArgb(241, 245, 249)));
            _logQueue.Enqueue(($"  • 价格行情: 开={kline.Open:F2} | 高={kline.High:F2} | 低={kline.Low:F2} | 收={kline.Close:F2}", Color.FromArgb(241, 245, 249)));
            _logQueue.Enqueue(($"  • 涨跌幅度: 涨跌额={change:+0.00;-0.00;0.00}, 涨跌幅={changePct:+0.00;-0.00;0.00}%, 振幅={amplitude:F2}%", themeColor));
            _logQueue.Enqueue(($"  • 形态特征: 实体={body:F2} | 上影线={upperShadow:F2} | 下影线={lowerShadow:F2} | 结构属性: {pivotInfo}", Color.FromArgb(226, 232, 240)));
            _logQueue.Enqueue(($"  • 成交量能: 成交量={kline.Volume:N2} (Base), 成交额={kline.QuoteVolume:N2} USDT, 笔数={kline.TradeCount:N0} 笔", Color.FromArgb(241, 245, 249)));
            if (kline.Volume > 0)
            {
                decimal takerBuyPct = (kline.TakerBuyVolume / kline.Volume) * 100m;
                _logQueue.Enqueue(($"  • 买卖力量: 主动买入量={kline.TakerBuyVolume:N2} ({takerBuyPct:F1}%), 主动买入额={kline.TakerBuyQuoteVolume:N2} USDT", Color.FromArgb(250, 204, 21)));
            }
            if (tradeInfo != "无交易动作")
            {
                _logQueue.Enqueue(($"  • 策略动作: {tradeInfo}", Color.FromArgb(168, 85, 247)));
            }
            _logQueue.Enqueue(("========================================================", themeColor));
        }

        private void RedrawCurrentPlot(bool autoScale = false)
        {
            try
            {
                if (formsPlot.IsDisposed) return;

                // 优先从最新快照重绘
                var snap = _latestChartSnapshot;
                if (snap != null && snap.Klines.Length > 0)
                {
                    PlotHelper.BuildPlot(
                        formsPlot.Plot,
                        snap.Klines,
                        snap.Peaks,
                        snap.Valleys,
                        snap.Lines,
                        snap.Summary,
                        title: snap.Title,
                        startGlobalIndex: snap.StartGlobalIndex,
                        autoScaleAxes: autoScale,
                        tradeSignals: snap.TradeSignals,
                        lineWidth: snap.LineWidth,
                        selectedTrendLine: _selectedTrendLine,
                        selectedKlineIndex: _selectedKlineIndex,
                        chartType: _chartType,
                        completedTrades: snap.CompletedTrades,
                        activePositions: snap.ActivePositions,
                        showTpSl: chkShowTpSl.Checked,
                        minLineX1X2: (int)numMinSignalSpan.Value,
                        minLineAge: (int)numMinSignalAge.Value,
                        showPivots: chkShowPivots.Checked,
                        showTrendLines: chkShowTrendLines.Checked);

                    formsPlot.Refresh();
                    return;
                }

                // 否则从回测最终结果重绘
                if (_latestResult?.Strategy != null && _latestResult.Strategy.KlineCount > 0)
                {
                    var strat = _latestResult.Strategy;
                    int startGlobal = Math.Max(0, strat.GlobalBarIndex - strat.KlineCount);
                    var allLines = new List<TrendLine>();
                    if (strat.ActiveResistanceLines.Count > 0) allLines.AddRange(strat.ActiveResistanceLines);
                    if (strat.ActiveSupportLines.Count > 0) allLines.AddRange(strat.ActiveSupportLines);
                    int delCount = strat.DeletedTrendLines.Count;
                    int takeDel = Math.Min(100, delCount);
                    for (int i = delCount - takeDel; i < delCount; i++)
                    {
                        allLines.Add(strat.DeletedTrendLines[i]);
                    }

                    string sign = _latestResult.TotalPnLPct >= 0 ? "+" : "";
                    string summary = $"币种: {_currentRunningCoin}, 周期: {_currentRunningInterval}\n" +
                                     $"交易战绩: {_latestResult.TotalTrades} 笔 (胜率: {_latestResult.WinRate:F1}%, 累计收益: {sign}{_latestResult.TotalPnLPct:F2}%)";

                    PlotHelper.BuildPlot(
                        formsPlot.Plot,
                        strat.Klines,
                        strat.Peaks,
                        strat.Valleys,
                        allLines,
                        summary,
                        title: $"{_currentRunningCoin} {_currentRunningInterval} 趋势线与极值结构折线图 (已选中趋势线)",
                        startGlobalIndex: startGlobal,
                        autoScaleAxes: autoScale,
                        tradeSignals: strat.TradeSignals,
                        lineWidth: (float)numLineWidth.Value,
                        selectedTrendLine: _selectedTrendLine,
                        selectedKlineIndex: _selectedKlineIndex,
                        chartType: _chartType,
                        completedTrades: strat.CompletedTrades,
                        activePositions: strat.ActivePositions,
                        showTpSl: chkShowTpSl.Checked,
                        minLineX1X2: (int)numMinSignalSpan.Value,
                        minLineAge: (int)numMinSignalAge.Value,
                        showPivots: chkShowPivots.Checked,
                        showTrendLines: chkShowTrendLines.Checked);

                    formsPlot.Refresh();
                }
            }
            catch
            {
                // 忽略刷新异常
            }
        }

        private bool IsNearAnyTrendLine(double mouseX, double mouseY, double tolerancePixels)
        {
            if (!chkShowTrendLines.Checked) return false;

            var candidateLines = GetCandidateTrendLines();
            if (candidateLines == null || candidateLines.Count == 0) return false;

            var limits = formsPlot.Plot.Axes.GetLimits();
            double plotLeft = limits.Left;
            double plotRight = limits.Right;

            for (int i = candidateLines.Count - 1; i >= 0; i--)
            {
                var line = candidateLines[i];
                int effectiveEndX = line.CollidedKlineIndex >= 0 ? line.CollidedKlineIndex : Math.Max(line.X2, (int)Math.Ceiling(plotRight));
                if (effectiveEndX < plotLeft - 50 || line.X1 > plotRight + 50) continue;

                // 过滤未满足界面参数跨度与寿命要求的趋势线
                int minSpan = (int)numMinSignalSpan.Value;
                int minAge = (int)numMinSignalAge.Value;
                int currentAge = Math.Max(line.LineAge, effectiveEndX - line.X2);
                if (line.LineX1X2 < minSpan || currentAge < minAge)
                    continue;

                try
                {
                    var pixelA = formsPlot.Plot.GetPixel(new ScottPlot.Coordinates(line.X1, (double)line.Y1));
                    var pixelB = formsPlot.Plot.GetPixel(new ScottPlot.Coordinates(effectiveEndX, (double)line.GetPriceAt(effectiveEndX)));

                    double dx = pixelB.X - pixelA.X;
                    double dy = pixelB.Y - pixelA.Y;
                    double lenSq = dx * dx + dy * dy;
                    if (lenSq < 1e-6) continue;

                    double t = Math.Clamp(((mouseX - pixelA.X) * dx + (mouseY - pixelA.Y) * dy) / lenSq, 0.0, 1.0);
                    double projX = pixelA.X + t * dx;
                    double projY = pixelA.Y + t * dy;
                    double dist = Math.Sqrt((mouseX - projX) * (mouseX - projX) + (mouseY - projY) * (mouseY - projY));

                    if (dist <= tolerancePixels) return true;
                }
                catch
                {
                }
            }
            return false;
        }

        private void OutputTrendLineDetails(TrendLine line)
        {
            string typeName = line.IsResistance ? "高点阻力趋势线 (Peak Resistance)" : "低点支撑趋势线 (Valley Support)";
            string stateStr;
            Color themeColor;

            if (line.CollidedKlineIndex >= 0)
            {
                string extra = line.IsSpecialTrendLine ? " 【🟣 特殊结构突破线 (紫色高亮 0.8f)】" : "";
                stateStr = $"已击穿删除 (于 Bar #{line.CollidedKlineIndex} 发生穿透失效){extra}";
                themeColor = line.IsSpecialTrendLine ? Color.FromArgb(168, 85, 247) : Color.FromArgb(148, 163, 184); // Purple 500 / Slate 400
            }
            else if (line.IsInChannel)
            {
                stateStr = $"活跃 (🔴 趋势通道 - 红色高亮 0.8f, 通道 #{line.ChannelId})";
                themeColor = Color.FromArgb(239, 68, 68); // Red 500
            }
            else if (line.IsSpecialTrendLine)
            {
                stateStr = "活跃 (🟣 特殊趋势线 - 紫色高亮 0.8f)";
                themeColor = Color.FromArgb(168, 85, 247); // Purple 500
            }
            else if (line.IsTriggered)
            {
                stateStr = "活跃 (已触发交易开仓 - 绿色高亮)";
                themeColor = Color.FromArgb(74, 222, 128); // Green 400
            }
            else if (line.IsThreePointConfirmed)
            {
                stateStr = "活跃 (3点强确认线 - 金黄高亮)";
                themeColor = Color.FromArgb(250, 204, 21); // Yellow 400
            }
            else
            {
                stateStr = "活跃 (有效延长监控中)";
                themeColor = line.IsResistance ? Color.FromArgb(249, 115, 22) : Color.FromArgb(6, 182, 212); // Orange / Cyan
            }

            string t1 = line.Time1 != DateTime.MinValue ? line.Time1.ToUtc0String() : "N/A";
            string t2 = line.Time2 != DateTime.MinValue ? line.Time2.ToUtc0String() : "N/A";

            _logQueue.Enqueue(("\n========================================================", themeColor));
            _logQueue.Enqueue(($"🎯 [选中趋势线] 【{typeName}】", themeColor));
            _logQueue.Enqueue(($"  • 运行状态: {stateStr}", Color.FromArgb(241, 245, 249)));
            if (line.IsSpecialTrendLine)
            {
                _logQueue.Enqueue(($"  • 特殊形态: 【🟣 特殊结构突破趋势线】(源于极值大底/大顶，长时间顺势后发生突破，紫色高亮 0.8f)", Color.FromArgb(168, 85, 247)));
            }
            if (line.IsInChannel)
            {
                _logQueue.Enqueue(($"  • 通道形态: 【🔴 符合条件的趋势通道】属于通道 #{line.ChannelId} (红色高亮 0.8f)", Color.FromArgb(239, 68, 68)));
            }
            _logQueue.Enqueue(($"  • 端点 1 (X1, Y1): Bar #{line.X1} (价格: {line.Y1:F4} @ {t1})", Color.FromArgb(241, 245, 249)));
            _logQueue.Enqueue(($"  • 端点 2 (X2, Y2): Bar #{line.X2} (价格: {line.Y2:F4} @ {t2})", Color.FromArgb(241, 245, 249)));

            if (line.IsThreePointConfirmed && line.X3 >= 0)
            {
                _logQueue.Enqueue(($"  • 第3确认点 (X3, Y3): Bar #{line.X3} (价格: {line.Y3:F4}) [⭐ 3点共线确认]", Color.FromArgb(250, 204, 21)));
            }

            _logQueue.Enqueue(($"  • 跨度指标: 跨度(X1->X2) = {line.LineX1X2} bars, 寿命(X2->当前) = {line.LineAge} bars, 全局总长 = {line.TotalAge} bars", Color.FromArgb(241, 245, 249)));
            _logQueue.Enqueue(($"  • 斜率指标: 整体斜率 = {line.OverallSlopePct:+0.00;-0.00;0.00}%, 归一化斜率 = {line.K:F4}%/bar, 原始斜率 = {line.RawK:F6} $/bar", Color.FromArgb(241, 245, 249)));

            if (line.CachedCurrentPrice > 0)
            {
                _logQueue.Enqueue(($"  • 当前延伸价: {line.CachedCurrentPrice:F4} (总延伸整体斜率: {line.TotalOverallSlopePct:+0.00;-0.00;0.00}%)", Color.FromArgb(241, 245, 249)));
            }

            if (line.CollidedKlineIndex >= 0)
            {
                _logQueue.Enqueue(($"  • 击穿信息: 于 Bar #{line.CollidedKlineIndex} 发生穿透, 延伸跨度 = {line.LineExtensionRange} bars", Color.FromArgb(248, 113, 113)));
            }
            else
            {
                _logQueue.Enqueue(($"  • 击穿信息: 未被击穿, 持续向右延伸监控 (ExtensionRange = {line.LineExtensionRange})", Color.FromArgb(74, 222, 128)));
            }

            // 🌟 若存在关联通道，同时输出通道对轨与通道全貌
            TrendLine? channelPartner = null;
            if (_currentPlottedLines != null && _currentPlottedLines.Count > 0)
            {
                if (line.ChannelId > 0)
                {
                    for (int i = 0; i < _currentPlottedLines.Count; i++)
                    {
                        var l = _currentPlottedLines[i];
                        if (l.ChannelId == line.ChannelId && l.Type != line.Type)
                        {
                            channelPartner = l;
                            break;
                        }
                    }
                }
                else
                {
                    for (int i = 0; i < _currentPlottedLines.Count; i++)
                    {
                        var l = _currentPlottedLines[i];
                        if (l.Type != line.Type && l.IsValid)
                        {
                            var rLine = line.IsResistance ? line : l;
                            var sLine = line.IsSupport ? line : l;
                            var testChannels = TrendLineHelper.DetectTrendChannels(
                                new List<TrendLine> { rLine },
                                new List<TrendLine> { sLine },
                                _currentPlotEndGlobalIndex);
                            if (testChannels.Count > 0)
                            {
                                channelPartner = l;
                                break;
                            }
                        }
                    }
                }
            }

            if (channelPartner.HasValue)
            {
                var p = channelPartner.Value;
                string pType = p.IsResistance ? "阻力线上轨 (Peak Upper)" : "支撑线下轨 (Valley Lower)";
                string pt1 = p.Time1 != DateTime.MinValue ? p.Time1.ToUtc0String() : "N/A";
                string pt2 = p.Time2 != DateTime.MinValue ? p.Time2.ToUtc0String() : "N/A";

                int startX = Math.Max(line.X1, p.X1);
                int endX = Math.Min(line.CollidedKlineIndex >= 0 ? line.CollidedKlineIndex : line.X2 + line.LineAge,
                                    p.CollidedKlineIndex >= 0 ? p.CollidedKlineIndex : p.X2 + p.LineAge);
                int overlapSpan = Math.Max(0, endX - startX);
                decimal avgSlope = (line.K + p.K) / 2m;
                decimal avgOverallSlope = (line.OverallSlopePct + p.OverallSlopePct) / 2m;

                var upper = line.IsResistance ? line : p;
                var lower = line.IsSupport ? line : p;
                decimal widthStart = lower.GetPriceAt(startX) > 0 ? (upper.GetPriceAt(startX) - lower.GetPriceAt(startX)) / lower.GetPriceAt(startX) * 100m : 0m;
                decimal widthEnd = lower.GetPriceAt(endX) > 0 ? (upper.GetPriceAt(endX) - lower.GetPriceAt(endX)) / lower.GetPriceAt(endX) * 100m : 0m;

                _logQueue.Enqueue(("\n--------------------------------------------------------", Color.FromArgb(239, 68, 68)));
                _logQueue.Enqueue(($"🔴 [关联趋势通道详情] (双轨同时显示高亮)", Color.FromArgb(239, 68, 68)));
                _logQueue.Enqueue(($"  • 通道特征: 有效重叠跨度 = {overlapSpan} bars (Bar #{startX} -> #{endX})", Color.FromArgb(241, 245, 249)));
                _logQueue.Enqueue(($"  • 斜率指标: 通道平均归一化斜率 = {avgSlope:F4}%/bar (整体斜率: {avgOverallSlope:+0.00;-0.00;0.00}%)", Color.FromArgb(241, 245, 249)));
                _logQueue.Enqueue(($"  • 宽度指标: 起始宽度 = {widthStart:F2}%, 终止宽度 = {widthEnd:F2}%, 平均宽度 = {((widthStart + widthEnd) / 2m):F2}%", Color.FromArgb(241, 245, 249)));
                _logQueue.Enqueue(($"  • 配对通道轨: 【{pType}】", Color.FromArgb(250, 204, 21)));
                _logQueue.Enqueue(($"    ↳ 端点 1: Bar #{p.X1} (价格: {p.Y1:F4} @ {pt1})", Color.FromArgb(241, 245, 249)));
                _logQueue.Enqueue(($"    ↳ 端点 2: Bar #{p.X2} (价格: {p.Y2:F4} @ {pt2}) | 归一化斜率 = {p.K:F4}%/bar", Color.FromArgb(241, 245, 249)));
                _logQueue.Enqueue(("--------------------------------------------------------", Color.FromArgb(239, 68, 68)));
            }

            _logQueue.Enqueue(("========================================================", themeColor));
        }

        #endregion

        #region 微观逐笔 Tick 走势渲染与流水加载 (K线点击右侧明细联动)

        private async Task DisplayKlineTickDetailsAsync(RawKline kline, int globalIndex)
        {
            try
            {
                string coin = _currentRunningCoin;
                if (string.IsNullOrWhiteSpace(coin) && cboCoin != null)
                {
                    coin = cboCoin.Text;
                }
                if (string.IsNullOrWhiteSpace(coin)) coin = "BTCUSDT";

                lblTickInfo.Text = $"正在读取 Bar #{globalIndex} 内部微观逐笔 Tick 数据...";

                var ticks = await ParquetDataReader.ReadTicksForTimeRangeAsync(coin, kline.OpenTime, kline.CloseTime).ConfigureAwait(true);
                DisplayTicksInternal(kline, globalIndex, ticks);
            }
            catch (Exception ex)
            {
                lblTickInfo.Text = $"Bar #{globalIndex} | 读取 Tick 数据异常: {ex.Message}";
            }
        }

        private void DisplayTicksInternal(RawKline kline, int globalIndex, RawTick[]? ticks)
        {
            if (ticks == null || ticks.Length == 0)
            {
                DateTime openTime = TimeHelper.FromUnixTimeMilliseconds(kline.OpenTime);
                DateTime closeTime = TimeHelper.FromUnixTimeMilliseconds(kline.CloseTime);
                string dir = kline.Close >= kline.Open ? "🟢 阳线" : "🔴 阴线";
                lblTickInfo.Text = $"Bar #{globalIndex} ({dir}) | {openTime:yyyy-MM-dd HH:mm:ss} ~ {closeTime:HH:mm:ss} | 本地暂无该周期的逐笔 Tick 数据";
                formsPlotTick.Plot.Clear();
                formsPlotTick.Plot.Title($"Bar #{globalIndex} 暂无可用 Tick 数据");
                formsPlotTick.Refresh();
                _displayedTicks.Clear();
                dgvTicks.RowCount = 0;
                return;
            }

            int totalTicks = ticks.Length;
            DateTime kOpenTime = TimeHelper.FromUnixTimeMilliseconds(kline.OpenTime);
            DateTime kCloseTime = TimeHelper.FromUnixTimeMilliseconds(kline.CloseTime);
            TimeSpan duration = kCloseTime - kOpenTime;

            decimal totalVol = 0m;
            decimal totalTakerBuyVol = 0m;
            decimal overallHigh = decimal.MinValue;
            decimal overallLow = decimal.MaxValue;

            for (int i = 0; i < totalTicks; i++)
            {
                var t = ticks[i];
                if (t.Price > overallHigh) overallHigh = t.Price;
                if (t.Price < overallLow) overallLow = t.Price;
                totalVol += t.Qty;
                if (!t.IsBuyerMaker) totalTakerBuyVol += t.Qty;
            }

            string dirStr = kline.Close >= kline.Open ? "🟢 阳线/涨" : "🔴 阴线/跌";
            decimal takerBuyRatio = totalVol > 0 ? (totalTakerBuyVol / totalVol * 100m) : 0m;

            lblTickInfo.Text = $"Bar #{globalIndex} ({dirStr}) | 耗时: {FormatDuration(duration)} | Tick: {totalTicks:N0} 笔 | 极值: {overallLow:F2} ~ {overallHigh:F2} | 主买: {takerBuyRatio:F1}%";

            // 1. 渲染微观 Tick 分时走势图 (ScottPlot 5)
            formsPlotTick.Plot.Clear();
            formsPlotTick.Plot.FigureBackground.Color = ScottPlot.Color.FromHex("#0f172a");
            formsPlotTick.Plot.DataBackground.Color = ScottPlot.Color.FromHex("#1e293b");
            formsPlotTick.Plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#334155").WithAlpha(0.6);
            formsPlotTick.Plot.Grid.MinorLineColor = ScottPlot.Color.FromHex("#1e293b").WithAlpha(0.3);

            formsPlotTick.Plot.Axes.Left.TickLabelStyle.ForeColor = ScottPlot.Color.FromHex("#94a3b8");
            formsPlotTick.Plot.Axes.Bottom.TickLabelStyle.ForeColor = ScottPlot.Color.FromHex("#94a3b8");
            formsPlotTick.Plot.Axes.Left.FrameLineStyle.Color = ScottPlot.Color.FromHex("#475569");
            formsPlotTick.Plot.Axes.Bottom.FrameLineStyle.Color = ScottPlot.Color.FromHex("#475569");

            string fontName = PlotHelper.GetInstalledChineseFont();
            string chartTitle = $"Bar #{globalIndex} 内部 Tick 价格路径 (共 {totalTicks:N0} 笔 Tick, {kOpenTime.ToLocalTime():HH:mm:ss.fff} ~ {kCloseTime.ToLocalTime():HH:mm:ss.fff})";

            formsPlotTick.Plot.Title(chartTitle, 11);
            formsPlotTick.Plot.Axes.Title.Label.FontName = fontName;
            formsPlotTick.Plot.Axes.Title.Label.ForeColor = ScottPlot.Color.FromHex("#38bdf8");

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

            // 成交量柱状图极速聚合渲染 (最大 250 个 bin，杜绝百万图形对象卡死)
            int maxVolBins = 250;
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

            var scatter = formsPlotTick.Plot.Add.ScatterLine(xs, ys);
            scatter.Color = kline.Close >= kline.Open ? ScottPlot.Color.FromHex("#22c55e") : ScottPlot.Color.FromHex("#ef4444");
            scatter.LineWidth = 1.3f;
            scatter.MarkerSize = 0;

            // 标注最高与最低点位 (防重叠安全间距)
            double priceRange = (double)(maxVal - minVal);
            if (priceRange <= 1e-6) priceRange = (double)maxVal * 0.005;
            if (priceRange <= 0) priceRange = 1.0;
            double yOffsetMarker = priceRange * 0.024;

            static ScottPlot.Alignment GetSafeAlign(int tickIdx, int total, bool isUpper)
            {
                if (tickIdx < total * 0.12)
                    return isUpper ? ScottPlot.Alignment.UpperLeft : ScottPlot.Alignment.LowerLeft;
                if (tickIdx > total * 0.88)
                    return isUpper ? ScottPlot.Alignment.UpperRight : ScottPlot.Alignment.LowerRight;
                return isUpper ? ScottPlot.Alignment.UpperCenter : ScottPlot.Alignment.LowerCenter;
            }

            var mHigh = formsPlotTick.Plot.Add.Marker(maxIdx, (double)maxVal);
            mHigh.Shape = ScottPlot.MarkerShape.FilledTriangleUp;
            mHigh.Size = 10;
            mHigh.Color = ScottPlot.Color.FromHex("#ef4444");

            var textHigh = formsPlotTick.Plot.Add.Text($"▲ 最高 {maxVal:F2}", maxIdx, (double)maxVal + yOffsetMarker);
            textHigh.LabelFontName = fontName;
            textHigh.LabelFontSize = 8.0f;
            textHigh.LabelFontColor = ScottPlot.Color.FromHex("#fca5a5");
            textHigh.LabelAlignment = GetSafeAlign(maxIdx, totalTicks, isUpper: false);
            textHigh.LabelBackgroundColor = ScottPlot.Color.FromHex("#0f172a").WithAlpha(0.9);
            textHigh.LabelBorderColor = ScottPlot.Color.FromHex("#ef4444");
            textHigh.LabelBorderWidth = 1f;

            var mLow = formsPlotTick.Plot.Add.Marker(minIdx, (double)minVal);
            mLow.Shape = ScottPlot.MarkerShape.FilledTriangleDown;
            mLow.Size = 10;
            mLow.Color = ScottPlot.Color.FromHex("#ef4444");

            var textLow = formsPlotTick.Plot.Add.Text($"▼ 最低 {minVal:F2}", minIdx, (double)minVal - yOffsetMarker);
            textLow.LabelFontName = fontName;
            textLow.LabelFontSize = 8.0f;
            textLow.LabelFontColor = ScottPlot.Color.FromHex("#fca5a5");
            textLow.LabelAlignment = GetSafeAlign(minIdx, totalTicks, isUpper: true);
            textLow.LabelBackgroundColor = ScottPlot.Color.FromHex("#0f172a").WithAlpha(0.9);
            textLow.LabelBorderColor = ScottPlot.Color.FromHex("#ef4444");
            textLow.LabelBorderWidth = 1f;

            var mOpen = formsPlotTick.Plot.Add.Marker(0, (double)ticks[0].Price);
            mOpen.Shape = ScottPlot.MarkerShape.FilledCircle;
            mOpen.Size = 8;
            mOpen.Color = ScottPlot.Color.FromHex("#38bdf8");

            var mClose = formsPlotTick.Plot.Add.Marker(totalTicks - 1, (double)ticks[totalTicks - 1].Price);
            mClose.Shape = ScottPlot.MarkerShape.FilledSquare;
            mClose.Size = 8;
            mClose.Color = ScottPlot.Color.FromHex("#f97316");

            formsPlotTick.Plot.Axes.Margins(0.03, 0.28);
            formsPlotTick.Plot.Axes.AutoScale();
            formsPlotTick.Refresh();

            // 2. 极速装载 VirtualMode 虚拟数据源
            _displayedTicks.Clear();
            if (_displayedTicks.Capacity < totalTicks)
            {
                _displayedTicks.Capacity = totalTicks;
            }

            decimal kOpen = kline.Open;
            for (int t = 0; t < totalTicks; t++)
            {
                var tick = ticks[t];
                bool isBuyer = !tick.IsBuyerMaker;
                decimal diffFromOpen = kOpen > 0 ? (tick.Price - kOpen) / kOpen * 100m : 0m;

                _displayedTicks.Add(new TickViewModel
                {
                    TickIndex = t + 1,
                    Time = tick.Time,
                    Price = tick.Price,
                    Qty = tick.Qty,
                    QuoteQty = tick.QuoteQty,
                    IsBuyer = isBuyer,
                    DiffPct = diffFromOpen
                });
            }

            dgvTicks.RowCount = _displayedTicks.Count;
            dgvTicks.Invalidate();
        }

        private void OnDgvTicksCellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _displayedTicks.Count) return;
            var tick = _displayedTicks[e.RowIndex];

            e.Value = e.ColumnIndex switch
            {
                0 => tick.TickIndex,
                1 => TimeHelper.FromUnixTimeMilliseconds(tick.Time).ToLocalTime().ToString("HH:mm:ss.fff"),
                2 => tick.Price.ToString("F2"),
                3 => tick.Qty.ToString("F4"),
                4 => tick.QuoteQty.ToString("F2"),
                5 => tick.IsBuyer ? "🟢 买方主动" : "🔴 卖方主动",
                6 => $"{tick.DiffPct:+0.00;-0.00;0.00}%",
                _ => null
            };
        }

        private void OnDgvTicksCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _displayedTicks.Count || e.CellStyle == null) return;
            var tick = _displayedTicks[e.RowIndex];

            e.CellStyle.ForeColor = tick.IsBuyer
                ? Color.FromArgb(74, 222, 128)
                : Color.FromArgb(248, 113, 113);
        }

        private static string FormatDuration(TimeSpan span)
        {
            if (span.TotalDays >= 1) return $"{(int)span.TotalDays}天{span.Hours}小时";
            if (span.TotalHours >= 1) return $"{(int)span.TotalHours}小时{span.Minutes}分";
            if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}分{span.Seconds}秒";
            if (span.TotalSeconds >= 1) return $"{span.TotalSeconds:F2}秒";
            return $"{span.TotalMilliseconds:F0}ms";
        }

        #endregion

        #region 快照数据模型

        private class ChartSnapshot
        {
            public RawKline[] Klines { get; init; } = Array.Empty<RawKline>();
            public PivotPoint[] Peaks { get; init; } = Array.Empty<PivotPoint>();
            public PivotPoint[] Valleys { get; init; } = Array.Empty<PivotPoint>();
            public List<TrendLine> Lines { get; init; } = new List<TrendLine>();
            public TradeSignal[] TradeSignals { get; init; } = Array.Empty<TradeSignal>();
            public TradeRecord[] CompletedTrades { get; init; } = Array.Empty<TradeRecord>();
            public Position[] ActivePositions { get; init; } = Array.Empty<Position>();
            public string Summary { get; init; } = string.Empty;
            public string Title { get; init; } = string.Empty;
            public int StartGlobalIndex { get; init; }
            public bool AutoScale { get; init; }
            public float LineWidth { get; init; } = 0.8f;
            public ChartType ChartType { get; init; } = ChartType.Candlestick;
            public long SnapshotVersion { get; init; }
        }

        #endregion
    }
}
