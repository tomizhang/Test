using Common;
using Common.Helper;
using DuckDB.NET.Data;
using ScottPlot.WinForms;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Test.PercentageBar.WinForms.Engine;
using Test.PercentageBar.WinForms.Helper;
using Test.PercentageBar.WinForms.Models;

namespace Test.PercentageBar.WinForms.Forms
{
    public class MainPercentBarForm : Form
    {
        // 核心 K 线与统计数据 (零逐笔 Tick 内存堆积, 纯流式增量生成)
        private List<PercentageKline> _currentBars = new List<PercentageKline>();
        private PercentBarGenerationStats? _currentStats = null;
        private int? _selectedBarStart = null;
        private int? _selectedBarEnd = null;
        private TrendLine? _selectedTrendLine = null;
        private PercentChartType _chartType = PercentChartType.Candlestick;

        private CancellationTokenSource? _cts = null;
        private bool _isRunning = false;
        private volatile bool _isPaused = false;

        // UI 日志防卡顿批量队列
        private readonly ConcurrentQueue<(string Message, System.Drawing.Color Color)> _logQueue = new();
        private System.Windows.Forms.Timer _uiRefreshTimer = null!;

        // 界面控件
        private SplitContainer splitMain = null!;
        private SplitContainer splitLeft = null!;
        private FormsPlot formsPlot = null!;

        // 底部水平分割与 Tick 明细控件
        private SplitContainer splitBottom = null!;
        private RichTextBox txtLogs = null!;
        private Panel panelLogHeader = null!;
        private Label lblLogTitle = null!;
        private Button btnClearLogs = null!;

        private Panel panelTickDetail = null!;
        private Panel panelTickHeader = null!;
        private Label lblTickTitle = null!;
        private Label lblTickInfo = null!;
        private TabControl tabTickViews = null!;
        private FormsPlot formsPlotTick = null!;
        private DataGridView dgvTicks = null!;
        private readonly List<TickViewModel> _displayedTicks = new();

        private struct TickViewModel
        {
            public int BarIndex;
            public int TickIndex;
            public long Time;
            public decimal Price;
            public decimal Qty;
            public decimal QuoteQty;
            public bool IsBuyer;
            public decimal DiffPct;
        }

        // 策略报告 Tab 控件
        private TabPage tabStrategyReport = null!;
        private Panel panelStrategyHeader = null!;
        private Label lblStrategyTitle = null!;
        private Label lblStrategyMetrics = null!;
        private DataGridView dgvTrades = null!;
        private Test.PercentageBar.WinForms.Engine.FirstTickBacktestReport? _lastBacktestReport = null;

        // 右侧控制面板控件
        private Panel panelRight = null!;
        private GroupBox grpData = null!;
        private ComboBox cboCoin = null!;
        private DateTimePicker dtpStart = null!;
        private DateTimePicker dtpEnd = null!;

        private GroupBox grpParams = null!;
        private ComboBox cboSliceUnit = null!;
        private Label lblThreshold = null!;
        private NumericUpDown numThresholdValue = null!;
        private ComboBox cboBarMode = null!;
        private ComboBox cboChartType = null!;
        private FlowLayoutPanel flowPresets = null!;

        private GroupBox grpAnalysis = null!;
        private CheckBox chkShowVolume = null!;
        private CheckBox chkShowPivots = null!;
        private CheckBox chkShowGlobalHighLow = null!;
        private CheckBox chkShowZigZag = null!;
        private CheckBox chkShowTrendLines = null!;
        private NumericUpDown numPivotWindow = null!;
        private NumericUpDown numTouchTolerance = null!;

        // 首 Tick 动量策略回测控件
        private GroupBox grpStrategy = null!;
        private NumericUpDown numInitialCapital = null!;
        private NumericUpDown numFeeRate = null!;
        private CheckBox chkCompound = null!;
        private CheckBox chkShowStrategyMarkers = null!;
        private Button btnRunBacktest = null!;
        private Button btnExportBacktestReport = null!;

        private GroupBox grpPlayback = null!;
        private ComboBox cboPlaybackMode = null!;
        private ComboBox cboPlaybackSpeed = null!;
        private CheckBox chkAutoFollow = null!;

        private GroupBox grpControl = null!;
        private Button btnStart = null!;
        private Button btnPause = null!;
        private Button btnStop = null!;
        private Button btnResetAxes = null!;
        private ProgressBar progressBar = null!;
        private Label lblProgress = null!;

        private Label lblStatTicks = null!;
        private Label lblStatBars = null!;
        private Label lblStatAvgDuration = null!;
        private Label lblStatMinMaxDuration = null!;
        private Label lblStatPriceRange = null!;
        private Label lblStatThroughput = null!;

        public MainPercentBarForm()
        {
            InitializeComponent();
            ApplyDarkTheme();
            SetupUiTimer();

            // 🚀 1. 反射自动加载用户保存的全部历史配置参数 (多轮依赖感知安全恢复)
            bool configLoaded = FormConfigHelper.LoadFormConfig(this);

            // 🚀 2. 自动绑定全窗体控件变更监听 (修改任何控件即时防抖自动保存至 AppData 及本地，无惧异常退出)
            FormConfigHelper.BindAutoSave(this);

            this.FormClosing += (s, e) =>
            {
                // 💾 窗体关闭时自动使用反射保存所有配置
                FormConfigHelper.SaveFormConfig(this);
            };

            AppendLogInternal("🚀 [系统就绪] 百分比变化 K 线生成与可视化引擎已加载，X 轴为纯 Bar 序号，支持逐条动态回放与毫秒级时间跨度计算。", System.Drawing.Color.FromArgb(74, 222, 128));
            if (configLoaded)
            {
                AppendLogInternal("💾 [配置系统] 已通过反射自动加载历史参数配置 (已开启实时变更全自动保存，修改即存)。", System.Drawing.Color.FromArgb(56, 189, 248));
            }
        }

        private void InitializeComponent()
        {
            this.Text = "量化分析 - 基于 Tick 数据的百分比变化 K 线生成引擎 (逐条动态回放 & 精确时间跨度)";
            this.Size = new Size(1600, 950);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.MinimumSize = new Size(1100, 700);

            // 主分割容器 (左侧图表+日志, 右侧控制面板)
            splitMain = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 1200,
                SplitterWidth = 6,
                BackColor = System.Drawing.Color.FromArgb(30, 41, 59)
            };

            // 左侧分割容器 (上方图表, 下方日志控制台)
            splitLeft = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 620,
                SplitterWidth = 6,
                BackColor = System.Drawing.Color.FromArgb(30, 41, 59)
            };

            // 1. ScottPlot 5 图表控件
            formsPlot = new FormsPlot
            {
                Dock = DockStyle.Fill,
                BackColor = System.Drawing.Color.FromArgb(15, 23, 42)
            };
            formsPlot.MouseDown += OnFormsPlotMouseDown;
            splitLeft.Panel1.Controls.Add(formsPlot);

            // 2. 底部控制台：横向切分为 [左侧：运行日志与指标分析] 与 [右侧：选中 Bar 内部全量 Tick 走势与流水]
            splitBottom = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 580,
                SplitterWidth = 6,
                BackColor = System.Drawing.Color.FromArgb(30, 41, 59)
            };

            // 2.1 底部左侧：运行日志看板
            panelLogHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 32,
                BackColor = System.Drawing.Color.FromArgb(30, 41, 59)
            };
            lblLogTitle = new Label
            {
                Text = "📋 系统运行日志 & K 线指标分析看板",
                ForeColor = System.Drawing.Color.FromArgb(226, 232, 240),
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                Location = new Point(10, 6),
                AutoSize = true
            };
            btnClearLogs = new Button
            {
                Text = "清空日志",
                Size = new Size(80, 24),
                Location = new Point(panelLogHeader.Width - 90, 4),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                BackColor = System.Drawing.Color.FromArgb(51, 65, 85),
                ForeColor = System.Drawing.Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            btnClearLogs.FlatAppearance.BorderSize = 0;
            btnClearLogs.Click += (s, e) => txtLogs.Clear();
            panelLogHeader.Controls.AddRange(new Control[] { lblLogTitle, btnClearLogs });

            txtLogs = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = System.Drawing.Color.FromArgb(15, 23, 42),
                ForeColor = System.Drawing.Color.FromArgb(241, 245, 249),
                Font = new Font("Consolas", 9.5F),
                ReadOnly = true,
                BorderStyle = BorderStyle.None
            };

            var panelLogContainer = new Panel { Dock = DockStyle.Fill };
            panelLogContainer.Controls.Add(txtLogs);
            panelLogContainer.Controls.Add(panelLogHeader);
            splitBottom.Panel1.Controls.Add(panelLogContainer);

            // 2.2 底部右侧：选中 Bar 内部 Tick 微观走势图与逐笔流水看板
            panelTickDetail = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = System.Drawing.Color.FromArgb(15, 23, 42)
            };

            panelTickHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 32,
                BackColor = System.Drawing.Color.FromArgb(30, 41, 59)
            };

            lblTickTitle = new Label
            {
                Text = "🔬 选中 Bar 内部 Tick 微观走势与切片",
                ForeColor = System.Drawing.Color.FromArgb(56, 189, 248),
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                Location = new Point(10, 6),
                AutoSize = true
            };

            lblTickInfo = new Label
            {
                Text = "未选择 K 线 (请在上方图表中点击任意一根 K 线)",
                ForeColor = System.Drawing.Color.FromArgb(250, 204, 21),
                Font = new Font("Microsoft YaHei", 8F),
                Location = new Point(285, 8),
                AutoSize = true
            };
            panelTickHeader.Controls.AddRange(new Control[] { lblTickTitle, lblTickInfo });

            tabTickViews = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Microsoft YaHei", 8.5F)
            };

            var tabTickPlot = new TabPage("📈 微观 Tick 分时走势图") { BackColor = System.Drawing.Color.FromArgb(15, 23, 42) };
            formsPlotTick = new FormsPlot
            {
                Dock = DockStyle.Fill,
                BackColor = System.Drawing.Color.FromArgb(15, 23, 42)
            };
            tabTickPlot.Controls.Add(formsPlotTick);

            var tabTickTable = new TabPage("📑 逐笔 Tick 流水明细表") { BackColor = System.Drawing.Color.FromArgb(15, 23, 42) };
            dgvTicks = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = System.Drawing.Color.FromArgb(15, 23, 42),
                GridColor = System.Drawing.Color.FromArgb(51, 65, 85),
                BorderStyle = BorderStyle.None,
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                EnableHeadersVisualStyles = false,
                Font = new Font("Consolas", 9F),
                VirtualMode = true
            };

            // 启用双缓冲防止闪烁与卡顿
            typeof(DataGridView).InvokeMember(
                "DoubleBuffered",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.SetProperty,
                null, dgvTicks, new object[] { true });

            dgvTicks.CellValueNeeded += OnDgvTicksCellValueNeeded;
            dgvTicks.CellFormatting += OnDgvTicksCellFormatting;

            dgvTicks.ColumnHeadersDefaultCellStyle.BackColor = System.Drawing.Color.FromArgb(30, 41, 59);
            dgvTicks.ColumnHeadersDefaultCellStyle.ForeColor = System.Drawing.Color.FromArgb(56, 189, 248);
            dgvTicks.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold);
            dgvTicks.DefaultCellStyle.BackColor = System.Drawing.Color.FromArgb(15, 23, 42);
            dgvTicks.DefaultCellStyle.ForeColor = System.Drawing.Color.FromArgb(241, 245, 249);
            dgvTicks.DefaultCellStyle.SelectionBackColor = System.Drawing.Color.FromArgb(30, 58, 138);

            dgvTicks.Columns.Add("ColBar", "所属Bar");
            dgvTicks.Columns.Add("ColIdx", "#");
            dgvTicks.Columns.Add("ColTime", "精确时间 (UTC+8)");
            dgvTicks.Columns.Add("ColPrice", "价格 (USDT)");
            dgvTicks.Columns.Add("ColQty", "数量 (Qty)");
            dgvTicks.Columns.Add("ColQuote", "成交额 (USDT)");
            dgvTicks.Columns.Add("ColSide", "主动买卖");
            dgvTicks.Columns.Add("ColChange", "偏离基准 (%)");

            dgvTicks.Columns[0].Width = 72;
            dgvTicks.Columns[1].Width = 48;
            dgvTicks.Columns[2].Width = 135;
            dgvTicks.Columns[3].Width = 95;
            dgvTicks.Columns[4].Width = 85;
            dgvTicks.Columns[5].Width = 95;
            dgvTicks.Columns[6].Width = 100;
            dgvTicks.Columns[7].Width = 95;

            tabTickTable.Controls.Add(dgvTicks);

            // Tab 3: 🏆 策略回测报告与交易清单
            tabStrategyReport = new TabPage("🏆 首Tick策略回测报告") { BackColor = System.Drawing.Color.FromArgb(15, 23, 42) };
            panelStrategyHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 36,
                BackColor = System.Drawing.Color.FromArgb(30, 41, 59)
            };
            lblStrategyTitle = new Label
            {
                Text = "🏆 策略回测概要:",
                ForeColor = System.Drawing.Color.FromArgb(250, 204, 21),
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                Location = new Point(10, 8),
                AutoSize = true
            };
            lblStrategyMetrics = new Label
            {
                Text = "尚未运行回测 (请在右侧控制面板点击「🚀 运行首Tick策略回测」)",
                ForeColor = System.Drawing.Color.FromArgb(226, 232, 240),
                Font = new Font("Microsoft YaHei", 8.5F),
                Location = new Point(130, 9),
                AutoSize = true
            };
            panelStrategyHeader.Controls.AddRange(new Control[] { lblStrategyTitle, lblStrategyMetrics });

            dgvTrades = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = System.Drawing.Color.FromArgb(15, 23, 42),
                GridColor = System.Drawing.Color.FromArgb(51, 65, 85),
                BorderStyle = BorderStyle.None,
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                EnableHeadersVisualStyles = false,
                Font = new Font("Consolas", 9F)
            };

            typeof(DataGridView).InvokeMember(
                "DoubleBuffered",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.SetProperty,
                null, dgvTrades, new object[] { true });
            dgvTrades.ColumnHeadersDefaultCellStyle.BackColor = System.Drawing.Color.FromArgb(30, 41, 59);
            dgvTrades.ColumnHeadersDefaultCellStyle.ForeColor = System.Drawing.Color.FromArgb(56, 189, 248);
            dgvTrades.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold);
            dgvTrades.DefaultCellStyle.BackColor = System.Drawing.Color.FromArgb(15, 23, 42);
            dgvTrades.DefaultCellStyle.ForeColor = System.Drawing.Color.FromArgb(241, 245, 249);
            dgvTrades.DefaultCellStyle.SelectionBackColor = System.Drawing.Color.FromArgb(30, 58, 138);

            dgvTrades.Columns.Add("ColTradeId", "#");
            dgvTrades.Columns.Add("ColEntryBar", "持仓Bar范围");
            dgvTrades.Columns.Add("ColSide", "方向");
            dgvTrades.Columns.Add("ColEntryTime", "开仓时间");
            dgvTrades.Columns.Add("ColEntryPrice", "开仓价 (USDT)");
            dgvTrades.Columns.Add("ColExitTime", "平仓时间");
            dgvTrades.Columns.Add("ColExitPrice", "平仓价 (USDT)");
            dgvTrades.Columns.Add("ColExitReason", "平仓结果");
            dgvTrades.Columns.Add("ColNetPnL", "净收益 (USDT)");
            dgvTrades.Columns.Add("ColReturnPct", "收益率 (%)");
            dgvTrades.Columns.Add("ColEquity", "账户净值 (USDT)");

            dgvTrades.Columns[0].Width = 45;
            dgvTrades.Columns[1].Width = 110;
            dgvTrades.Columns[2].Width = 65;
            dgvTrades.Columns[3].Width = 110;
            dgvTrades.Columns[4].Width = 95;
            dgvTrades.Columns[5].Width = 110;
            dgvTrades.Columns[6].Width = 95;
            dgvTrades.Columns[7].Width = 95;
            dgvTrades.Columns[8].Width = 95;
            dgvTrades.Columns[9].Width = 85;
            dgvTrades.Columns[10].Width = 110;

            dgvTrades.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex >= 0 && _lastBacktestReport != null && e.RowIndex < _lastBacktestReport.Trades.Count)
                {
                    var trade = _lastBacktestReport.Trades[e.RowIndex];
                    if (trade.EntryBarIndex >= 0 && trade.EntryBarIndex < _currentBars.Count)
                    {
                        _selectedBarStart = trade.EntryBarIndex;
                        _selectedBarEnd = trade.ExitBarIndex;
                        var selBars = new List<PercentageKline>();
                        for (int b = _selectedBarStart.Value; b <= _selectedBarEnd.Value; b++)
                        {
                            selBars.Add(_currentBars[b]);
                        }
                        DisplayBarsTickDetails(selBars);
                        RedrawCurrentPlot(autoScale: false);
                    }
                }
            };

            tabStrategyReport.Controls.Add(dgvTrades);
            tabStrategyReport.Controls.Add(panelStrategyHeader);

            tabTickViews.TabPages.Add(tabTickPlot);
            tabTickViews.TabPages.Add(tabTickTable);
            tabTickViews.TabPages.Add(tabStrategyReport);

            panelTickDetail.Controls.Add(tabTickViews);
            panelTickDetail.Controls.Add(panelTickHeader);
            splitBottom.Panel2.Controls.Add(panelTickDetail);

            splitLeft.Panel2.Controls.Add(splitBottom);

            splitMain.Panel1.Controls.Add(splitLeft);

            // 3. 右侧控制面板
            BuildRightControlPanel();
            splitMain.Panel2.Controls.Add(panelRight);

            this.Controls.Add(splitMain);
        }

        private void BuildRightControlPanel()
        {
            panelRight = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = System.Drawing.Color.FromArgb(15, 23, 42),
                Padding = new Padding(10)
            };

            int top = 10;

            // Group 1: 基础数据配置
            grpData = CreateGroupBox("1. 基础数据配置", top, 140);
            {
                var lblCoin = CreateLabel("交易对:", 15, 25);
                cboCoin = new ComboBox { Location = new Point(90, 22), Width = 250, DropDownStyle = ComboBoxStyle.DropDownList };
                cboCoin.Items.AddRange(new object[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "DOGEUSDT", "XRPUSDT" });
                cboCoin.SelectedIndex = 0;

                var lblStart = CreateLabel("起始日期:", 15, 60);
                dtpStart = new DateTimePicker { Location = new Point(90, 57), Width = 250, Format = DateTimePickerFormat.Short, Value = new DateTime(2025, 1, 1) };

                var lblEnd = CreateLabel("结束日期:", 15, 95);
                dtpEnd = new DateTimePicker { Location = new Point(90, 92), Width = 250, Format = DateTimePickerFormat.Short, Value = new DateTime(2025, 1, 5) };

                grpData.Controls.AddRange(new Control[] { lblCoin, cboCoin, lblStart, dtpStart, lblEnd, dtpEnd });
            }
            panelRight.Controls.Add(grpData);
            top += grpData.Height + 10;

            // Group 2: 百分比 / 固定价格 K 线生成参数
            grpParams = CreateGroupBox("2. K 线切分与聚合参数", top, 275);
            {
                var lblUnit = CreateLabel("切分方式:", 15, 25);
                cboSliceUnit = new ComboBox
                {
                    Location = new Point(90, 22),
                    Width = 250,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = System.Drawing.Color.FromArgb(30, 41, 59),
                    ForeColor = System.Drawing.Color.FromArgb(250, 204, 21),
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold)
                };
                cboSliceUnit.Items.AddRange(new object[]
                {
                    "📊 百分比涨跌切分 (%)",
                    "💰 固定价格/价差切分 (USDT)"
                });
                cboSliceUnit.SelectedIndex = 0;
                cboSliceUnit.SelectedIndexChanged += OnSliceUnitChanged;

                lblThreshold = CreateLabel("涨跌幅度 (%):", 15, 60);
                numThresholdValue = new NumericUpDown
                {
                    Location = new Point(130, 57),
                    Width = 210,
                    Minimum = 0.01m,
                    Maximum = 100.0m,
                    DecimalPlaces = 2,
                    Increment = 0.1m,
                    Value = 1.0m,
                    Font = new Font("Microsoft YaHei", 9.5F, FontStyle.Bold),
                    ForeColor = System.Drawing.Color.FromArgb(56, 189, 248) // Sky Blue
                };
                numThresholdValue.ValueChanged += (s, e) => AutoRegenerateIfLoaded();

                var lblPresets = CreateLabel("快速预设:", 15, 95);
                flowPresets = new FlowLayoutPanel
                {
                    Location = new Point(90, 92),
                    Size = new Size(255, 34),
                    BackColor = System.Drawing.Color.Transparent
                };
                UpdatePresetsUI();

                var lblBarMode = CreateLabel("切分模式:", 15, 132);
                cboBarMode = new ComboBox { Location = new Point(90, 129), Width = 250, DropDownStyle = ComboBoxStyle.DropDownList };
                cboBarMode.Items.AddRange(new object[]
                {
                    "基于开盘价涨跌幅度 (From Open ±%)",
                    "基于极值全振幅 (High-Low Range %)",
                    "Renko 趋势砖块 (Renko %)"
                });
                cboBarMode.SelectedIndex = 0;
                cboBarMode.SelectedIndexChanged += (s, e) => AutoRegenerateIfLoaded();

                var lblChartType = CreateLabel("图表模式:", 15, 168);
                cboChartType = new ComboBox
                {
                    Location = new Point(90, 165),
                    Width = 250,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = System.Drawing.Color.FromArgb(30, 41, 59),
                    ForeColor = System.Drawing.Color.FromArgb(74, 222, 128),
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold)
                };
                cboChartType.Items.AddRange(new object[] { "🕯️ 蜡烛图 (Candlestick)", "📈 收盘折线 (Line Chart)" });
                cboChartType.SelectedIndex = 0;
                cboChartType.SelectedIndexChanged += (s, e) =>
                {
                    _chartType = (PercentChartType)cboChartType.SelectedIndex;
                    RedrawCurrentPlot(autoScale: false, autoFollow: chkAutoFollow.Checked);
                };

                var lblTip = new Label
                {
                    Text = "💡 说明: X 轴不依赖时间，价格每达到设定的百分比或固定价差即生成一根 Bar。\n点击图表任意 K 线可查看其起止时间与耗时跨度。",
                    Location = new Point(15, 205),
                    Size = new Size(325, 55),
                    ForeColor = System.Drawing.Color.FromArgb(148, 163, 184),
                    Font = new Font("Microsoft YaHei", 8F)
                };

                grpParams.Controls.AddRange(new Control[]
                {
                    lblUnit, cboSliceUnit,
                    lblThreshold, numThresholdValue,
                    lblPresets, flowPresets,
                    lblBarMode, cboBarMode,
                    lblChartType, cboChartType,
                    lblTip
                });
            }
            panelRight.Controls.Add(grpParams);
            top += grpParams.Height + 10;

            // Group 3: 高低点位与形态分析
            grpAnalysis = CreateGroupBox("3. 高低点位与形态分析", top, 210);
            {
                chkShowVolume = new CheckBox
                {
                    Text = "📊 显示底部成交量柱状图 (Volume)",
                    Location = new Point(15, 22),
                    AutoSize = true,
                    Checked = true,
                    ForeColor = System.Drawing.Color.FromArgb(148, 163, 184),
                    Font = new Font("Microsoft YaHei", 8.5F)
                };
                chkShowVolume.CheckedChanged += (s, e) => RedrawCurrentPlot(autoScale: false, autoFollow: chkAutoFollow.Checked);

                chkShowPivots = new CheckBox
                {
                    Text = "📍 显示局部波段高低点 (Swing High/Low)",
                    Location = new Point(15, 44),
                    AutoSize = true,
                    Checked = true,
                    ForeColor = System.Drawing.Color.FromArgb(226, 232, 240),
                    Font = new Font("Microsoft YaHei", 8.5F)
                };
                chkShowPivots.CheckedChanged += (s, e) => RedrawCurrentPlot(autoScale: false, autoFollow: chkAutoFollow.Checked);

                chkShowGlobalHighLow = new CheckBox
                {
                    Text = "👑 显示历史全局最高/最低水平线",
                    Location = new Point(15, 66),
                    AutoSize = true,
                    Checked = true,
                    ForeColor = System.Drawing.Color.FromArgb(250, 204, 21),
                    Font = new Font("Microsoft YaHei", 8.5F)
                };
                chkShowGlobalHighLow.CheckedChanged += (s, e) => RedrawCurrentPlot(autoScale: false, autoFollow: chkAutoFollow.Checked);

                chkShowZigZag = new CheckBox
                {
                    Text = "⚡ 显示波段高低趋势连线 (ZigZag)",
                    Location = new Point(15, 88),
                    AutoSize = true,
                    Checked = true,
                    ForeColor = System.Drawing.Color.FromArgb(56, 189, 248),
                    Font = new Font("Microsoft YaHei", 8.5F)
                };
                chkShowZigZag.CheckedChanged += (s, e) => RedrawCurrentPlot(autoScale: false, autoFollow: chkAutoFollow.Checked);

                chkShowTrendLines = new CheckBox
                {
                    Text = "📈 绘制高低点支撑/阻力趋势线 (Auto Trendlines)",
                    Location = new Point(15, 110),
                    AutoSize = true,
                    Checked = true,
                    ForeColor = System.Drawing.Color.FromArgb(244, 114, 182), // Pink 400
                    Font = new Font("Microsoft YaHei", 8.5F)
                };
                chkShowTrendLines.CheckedChanged += (s, e) => RedrawCurrentPlot(autoScale: false, autoFollow: chkAutoFollow.Checked);

                var lblWin = CreateLabel("确认窗口(Bar):", 15, 136);
                numPivotWindow = new NumericUpDown
                {
                    Location = new Point(125, 134),
                    Width = 65,
                    Minimum = 1,
                    Maximum = 20,
                    Value = 3,
                    Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold),
                    ForeColor = System.Drawing.Color.FromArgb(56, 189, 248)
                };
                numPivotWindow.ValueChanged += (s, e) => RedrawCurrentPlot(autoScale: false, autoFollow: chkAutoFollow.Checked);

                var lblWinTip = new Label
                {
                    Text = "(左右极值确认根数)",
                    Location = new Point(195, 136),
                    AutoSize = true,
                    ForeColor = System.Drawing.Color.FromArgb(148, 163, 184),
                    Font = new Font("Microsoft YaHei", 8F)
                };

                var lblTol = CreateLabel("3点共线容差(%):", 15, 164);
                numTouchTolerance = new NumericUpDown
                {
                    Location = new Point(125, 162),
                    Width = 65,
                    DecimalPlaces = 3,
                    Increment = 0.005m,
                    Minimum = 0.005m,
                    Maximum = 0.100m,
                    Value = 0.030m, // 默认 0.03% 精准共线
                    Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold),
                    ForeColor = System.Drawing.Color.FromArgb(192, 132, 252) // Purple 400
                };
                numTouchTolerance.ValueChanged += (s, e) => RedrawCurrentPlot(autoScale: false, autoFollow: chkAutoFollow.Checked);

                var lblTolTip = new Label
                {
                    Text = "(🟣超精准点位触碰)",
                    Location = new Point(195, 164),
                    AutoSize = true,
                    ForeColor = System.Drawing.Color.FromArgb(192, 132, 252),
                    Font = new Font("Microsoft YaHei", 8F)
                };

                grpAnalysis.Controls.AddRange(new Control[]
                {
                    chkShowVolume,
                    chkShowPivots,
                    chkShowGlobalHighLow,
                    chkShowZigZag,
                    chkShowTrendLines,
                    lblWin, numPivotWindow, lblWinTip,
                    lblTol, numTouchTolerance, lblTolTip
                });
            }
            panelRight.Controls.Add(grpAnalysis);
            top += grpAnalysis.Height + 10;

            // Group 4: 逐条回放与速度设置
            grpPlayback = CreateGroupBox("4. 动态回放与推进控制", top, 135);
            {
                var lblPlayMode = CreateLabel("回放方式:", 15, 25);
                cboPlaybackMode = new ComboBox
                {
                    Location = new Point(90, 22),
                    Width = 250,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    ForeColor = System.Drawing.Color.FromArgb(56, 189, 248),
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold)
                };
                cboPlaybackMode.Items.AddRange(new object[]
                {
                    "🎬 逐条动态回放 (Real-time Playback)",
                    "⚡ 极速生成 (Batch Full Speed)"
                });
                cboPlaybackMode.SelectedIndex = 0;

                var lblSpeed = CreateLabel("回放速度:", 15, 60);
                cboPlaybackSpeed = new ComboBox
                {
                    Location = new Point(90, 57),
                    Width = 250,
                    DropDownStyle = ComboBoxStyle.DropDownList
                };
                cboPlaybackSpeed.Items.AddRange(new object[]
                {
                    "⚡ 极速回放 (每20根批量刷新/10ms)",
                    "🚀 快速回放 (每5根批量刷新/15ms)",
                    "🎬 流畅回放 (逐根平滑刷新/20ms)",
                    "🐢 慢速步进 (逐根刷新/100ms)",
                    "🔍 极慢沉浸 (逐根刷新/300ms)"
                });
                cboPlaybackSpeed.SelectedIndex = 2; // 默认流畅回放

                chkAutoFollow = new CheckBox
                {
                    Text = "回放时图表自动跟随最新 K 线 (Auto-Follow)",
                    Location = new Point(15, 95),
                    AutoSize = true,
                    Checked = true,
                    ForeColor = System.Drawing.Color.FromArgb(226, 232, 240),
                    Font = new Font("Microsoft YaHei", 8.5F)
                };
                chkAutoFollow.CheckedChanged += (s, e) =>
                {
                    if (chkAutoFollow.Checked && _currentBars.Count > 0)
                    {
                        RedrawCurrentPlot(autoScale: false, autoFollow: true);
                    }
                };

                grpPlayback.Controls.AddRange(new Control[]
                {
                    lblPlayMode, cboPlaybackMode,
                    lblSpeed, cboPlaybackSpeed,
                    chkAutoFollow
                });
            }
            panelRight.Controls.Add(grpPlayback);
            top += grpPlayback.Height + 10;

            // Group 5: 执行控制与统计看板
            grpControl = CreateGroupBox("5. 执行控制与统计看板", top, 380);
            {
                btnStart = new Button
                {
                    Text = "▶ 开始回放",
                    Location = new Point(15, 25),
                    Size = new Size(100, 36),
                    BackColor = System.Drawing.Color.FromArgb(5, 150, 105), // Green 600
                    ForeColor = System.Drawing.Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnStart.FlatAppearance.BorderSize = 0;
                btnStart.Click += async (s, e) => await StartGenerateAsync();

                btnPause = new Button
                {
                    Text = "⏸ 暂停",
                    Location = new Point(122, 25),
                    Size = new Size(72, 36),
                    BackColor = System.Drawing.Color.FromArgb(217, 119, 6), // Amber 600
                    ForeColor = System.Drawing.Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                    Enabled = false,
                    Cursor = Cursors.Hand
                };
                btnPause.FlatAppearance.BorderSize = 0;
                btnPause.Click += (s, e) => TogglePause();

                btnStop = new Button
                {
                    Text = "⏹ 停止",
                    Location = new Point(200, 25),
                    Size = new Size(68, 36),
                    BackColor = System.Drawing.Color.FromArgb(220, 38, 38), // Red 600
                    ForeColor = System.Drawing.Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                    Enabled = false,
                    Cursor = Cursors.Hand
                };
                btnStop.FlatAppearance.BorderSize = 0;
                btnStop.Click += (s, e) => StopGenerate();

                btnResetAxes = new Button
                {
                    Text = "🔍 复位",
                    Location = new Point(274, 25),
                    Size = new Size(66, 36),
                    BackColor = System.Drawing.Color.FromArgb(14, 116, 144), // Cyan 700
                    ForeColor = System.Drawing.Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnResetAxes.FlatAppearance.BorderSize = 0;
                btnResetAxes.Click += (s, e) =>
                {
                    RedrawCurrentPlot(autoScale: true, autoFollow: false);
                };

                progressBar = new ProgressBar
                {
                    Location = new Point(15, 68),
                    Size = new Size(325, 14),
                    Style = ProgressBarStyle.Continuous
                };

                lblProgress = new Label
                {
                    Text = "就绪 (等待执行)",
                    Location = new Point(15, 86),
                    Size = new Size(325, 20),
                    ForeColor = System.Drawing.Color.FromArgb(148, 163, 184),
                    Font = new Font("Microsoft YaHei", 8F)
                };

                // 统计指标 Labels
                lblStatTicks = CreateStatLabel("读取 Tick: -- | 耗时: --", 112);
                lblStatBars = CreateStatLabel("生成 K 线: -- (纯序号 X 轴)", 144);
                lblStatAvgDuration = CreateStatLabel("平均时间跨度: --", 176);
                lblStatMinMaxDuration = CreateStatLabel("最快突破: -- | 最长盘整: --", 208);
                lblStatPriceRange = CreateStatLabel("最高价: -- | 最低价: --", 240);
                lblStatThroughput = CreateStatLabel("吞吐速率: -- ticks/s", 272);

                var lblDetailTip = new Label
                {
                    Text = "🎯 交互说明: 在图表中鼠标左键点击任意一根百分比 K 线，底部日志控制台将即时输出该 Bar 的完整四值行情、精确时间跨度、Tick 笔数、量能与买卖力量分解分析！",
                    Location = new Point(15, 305),
                    Size = new Size(325, 65),
                    ForeColor = System.Drawing.Color.FromArgb(250, 204, 21), // Yellow 400
                    Font = new Font("Microsoft YaHei", 8F)
                };

                grpControl.Controls.AddRange(new Control[]
                {
                    btnStart, btnPause, btnStop, btnResetAxes, progressBar, lblProgress,
                    lblStatTicks, lblStatBars, lblStatAvgDuration, lblStatMinMaxDuration,
                    lblStatPriceRange, lblStatThroughput, lblDetailTip
                });
            }
            panelRight.Controls.Add(grpControl);
            top += grpControl.Height + 10;

            // Group 6: 🎯 首 Tick 动量策略回测 (1:1 止盈止损)
            grpStrategy = CreateGroupBox("6. 🎯 首 Tick 动量策略回测 (1:1 止盈止损)", top, 200);
            {
                var lblCap = CreateLabel("初始本金(U):", 15, 25);
                numInitialCapital = new NumericUpDown
                {
                    Location = new Point(105, 22),
                    Width = 90,
                    Minimum = 100,
                    Maximum = 10000000,
                    Value = 10000,
                    Increment = 1000,
                    Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold),
                    ForeColor = System.Drawing.Color.FromArgb(56, 189, 248)
                };

                var lblFee = CreateLabel("手续费率(%):", 205, 25);
                numFeeRate = new NumericUpDown
                {
                    Location = new Point(285, 22),
                    Width = 55,
                    Minimum = 0,
                    Maximum = 1,
                    DecimalPlaces = 3,
                    Increment = 0.01m,
                    Value = 0.040m,
                    Font = new Font("Microsoft YaHei", 8.5F)
                };

                chkCompound = new CheckBox
                {
                    Text = "复利模式 (每笔按动态净值开仓)",
                    Location = new Point(15, 54),
                    AutoSize = true,
                    Checked = false,
                    ForeColor = System.Drawing.Color.FromArgb(226, 232, 240),
                    Font = new Font("Microsoft YaHei", 8.5F)
                };

                chkShowStrategyMarkers = new CheckBox
                {
                    Text = "在主图表叠加开平仓信号标记 (▲/▼)",
                    Location = new Point(15, 78),
                    AutoSize = true,
                    Checked = true,
                    ForeColor = System.Drawing.Color.FromArgb(74, 222, 128),
                    Font = new Font("Microsoft YaHei", 8.5F)
                };
                chkShowStrategyMarkers.CheckedChanged += (s, e) => RedrawCurrentPlot(autoScale: false, autoFollow: chkAutoFollow.Checked);

                btnRunBacktest = new Button
                {
                    Text = "🚀 运行策略回测并生成 HTML 报告",
                    Location = new Point(15, 108),
                    Size = new Size(325, 38),
                    BackColor = System.Drawing.Color.FromArgb(16, 185, 129), // Emerald 500
                    ForeColor = System.Drawing.Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Microsoft YaHei", 9.5F, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnRunBacktest.FlatAppearance.BorderSize = 0;
                btnRunBacktest.Click += (s, e) => RunFirstTickStrategyBacktest();

                btnExportBacktestReport = new Button
                {
                    Text = "🌐 导出 / 另存 HTML 交互回测报告",
                    Location = new Point(15, 152),
                    Size = new Size(325, 32),
                    BackColor = System.Drawing.Color.FromArgb(14, 116, 144), // Cyan 700
                    ForeColor = System.Drawing.Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnExportBacktestReport.FlatAppearance.BorderSize = 0;
                btnExportBacktestReport.Click += (s, e) => ExportBacktestReportToFile();

                grpStrategy.Controls.AddRange(new Control[]
                {
                    lblCap, numInitialCapital,
                    lblFee, numFeeRate,
                    chkCompound, chkShowStrategyMarkers,
                    btnRunBacktest, btnExportBacktestReport
                });
            }
            panelRight.Controls.Add(grpStrategy);
        }

        private void SetupUiTimer()
        {
            _uiRefreshTimer = new System.Windows.Forms.Timer { Interval = 50 };
            _uiRefreshTimer.Tick += (s, e) =>
            {
                int count = 0;
                while (_logQueue.TryDequeue(out var item) && count < 25)
                {
                    AppendLogInternal(item.Message, item.Color);
                    count++;
                }
            };
            _uiRefreshTimer.Start();
        }

        private void TogglePause()
        {
            if (!_isRunning) return;

            _isPaused = !_isPaused;
            if (_isPaused)
            {
                btnPause.Text = "▶ 继续";
                btnPause.BackColor = System.Drawing.Color.FromArgb(16, 185, 129); // Emerald 500
                lblProgress.Text = "已暂停回放 (点击继续恢复)";
                _logQueue.Enqueue(("[回放状态] 回放已暂停。", System.Drawing.Color.FromArgb(250, 204, 21)));
            }
            else
            {
                btnPause.Text = "⏸ 暂停";
                btnPause.BackColor = System.Drawing.Color.FromArgb(217, 119, 6); // Amber 600
                lblProgress.Text = "正在继续逐条回放...";
                _logQueue.Enqueue(("[回放状态] 回放已恢复继续推进。", System.Drawing.Color.FromArgb(74, 222, 128)));
            }
        }

        private void UpdatePresetsUI()
        {
            flowPresets.Controls.Clear();
            var sliceUnit = (SliceUnitType)(cboSliceUnit?.SelectedIndex ?? 0);

            decimal[] presets = sliceUnit == SliceUnitType.Percentage
                ? new decimal[] { 0.2m, 0.5m, 1.0m, 2.0m, 3.0m, 5.0m }
                : new decimal[] { 20m, 50m, 100m, 200m, 500m, 1000m };

            foreach (var pVal in presets)
            {
                string text = sliceUnit == SliceUnitType.Percentage ? $"{pVal}%" : $"{pVal:F0}U";
                var btnPreset = new Button
                {
                    Text = text,
                    Size = new Size(38, 26),
                    BackColor = System.Drawing.Color.FromArgb(51, 65, 85),
                    ForeColor = System.Drawing.Color.FromArgb(241, 245, 249),
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Microsoft YaHei", 7.5F, FontStyle.Bold),
                    Cursor = Cursors.Hand,
                    Margin = new Padding(1)
                };
                btnPreset.FlatAppearance.BorderSize = 0;
                decimal targetVal = pVal;
                btnPreset.Click += (s, e) =>
                {
                    numThresholdValue.Value = targetVal;
                };
                flowPresets.Controls.Add(btnPreset);
            }
        }

        private void OnSliceUnitChanged(object? sender, EventArgs e)
        {
            var sliceUnit = (SliceUnitType)cboSliceUnit.SelectedIndex;
            if (sliceUnit == SliceUnitType.Percentage)
            {
                lblThreshold.Text = "涨跌幅度 (%):";
                numThresholdValue.DecimalPlaces = 2;
                numThresholdValue.Minimum = 0.01m;
                numThresholdValue.Maximum = 100.0m;
                numThresholdValue.Increment = 0.1m;
                if (numThresholdValue.Value > 50m || numThresholdValue.Value < 0.01m)
                {
                    numThresholdValue.Value = 1.0m;
                }

                int prevModeIdx = cboBarMode.SelectedIndex;
                cboBarMode.Items.Clear();
                cboBarMode.Items.AddRange(new object[]
                {
                    "基于开盘价涨跌幅度 (From Open ±%)",
                    "基于极值全振幅 (High-Low Range %)",
                    "Renko 趋势砖块 (Renko %)"
                });
                cboBarMode.SelectedIndex = Math.Clamp(prevModeIdx, 0, 2);
            }
            else
            {
                lblThreshold.Text = "固定价差 (USDT):";
                numThresholdValue.DecimalPlaces = 2;
                numThresholdValue.Minimum = 0.0001m;
                numThresholdValue.Maximum = 1000000.0m;
                numThresholdValue.Increment = 10.0m;
                if (numThresholdValue.Value <= 5.0m)
                {
                    numThresholdValue.Value = 100.0m;
                }

                int prevModeIdx = cboBarMode.SelectedIndex;
                cboBarMode.Items.Clear();
                cboBarMode.Items.AddRange(new object[]
                {
                    "开盘基准固定价差 (From Open ±USDT)",
                    "极值全价差振幅 (High-Low Range USDT)",
                    "Renko 固定价差砖块 (Renko USDT)"
                });
                cboBarMode.SelectedIndex = Math.Clamp(prevModeIdx, 0, 2);
            }

            UpdatePresetsUI();
            AutoRegenerateIfLoaded();
        }

        private async Task StartGenerateAsync()
        {
            if (_isRunning) return;

            string coin = cboCoin.SelectedItem?.ToString() ?? "BTCUSDT";
            DateTime startDate = dtpStart.Value.Date;
            DateTime endDate = dtpEnd.Value.Date;
            SliceUnitType sliceUnit = (SliceUnitType)cboSliceUnit.SelectedIndex;
            decimal thresholdValue = numThresholdValue.Value;
            PercentBarMode mode = (PercentBarMode)cboBarMode.SelectedIndex;
            bool isStreamingMode = cboPlaybackMode.SelectedIndex == 0;

            if (startDate > endDate)
            {
                MessageBox.Show("起始日期不能大于结束日期！", "参数错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _isRunning = true;
            _isPaused = false;
            btnStart.Enabled = false;
            btnPause.Enabled = isStreamingMode;
            btnPause.Text = "⏸ 暂停";
            btnPause.BackColor = System.Drawing.Color.FromArgb(217, 119, 6);
            btnStop.Enabled = true;
            progressBar.Value = 0;
            lblProgress.Text = "正在启动分批流式读取与生成...";

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            _currentBars.Clear();
            _selectedBarStart = null;
            _selectedBarEnd = null;

            int batchYield = 1;
            int sleepMs = 20;

            if (isStreamingMode)
            {
                switch (cboPlaybackSpeed.SelectedIndex)
                {
                    case 0: batchYield = 20; sleepMs = 10; break;
                    case 1: batchYield = 5; sleepMs = 15; break;
                    case 2: batchYield = 1; sleepMs = 20; break;
                    case 3: batchYield = 1; sleepMs = 100; break;
                    case 4: batchYield = 1; sleepMs = 300; break;
                }
            }

            string unitDesc = sliceUnit == SliceUnitType.Percentage ? $"±{thresholdValue:F2}%" : $"±{thresholdValue:F2} USDT";
            _logQueue.Enqueue(($"[引擎启动] 正在以 {unitDesc} 切分基准开始【分批流式{(isStreamingMode ? "动态回放" : "极速生成")}】(币种: {coin}, 日期: {startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd})...", System.Drawing.Color.FromArgb(250, 204, 21)));

            var session = new IncrementalPercentageBarSession(thresholdValue, sliceUnit, mode);
            long lastPlotRefreshTime = 0;
            var localSw = Stopwatch.StartNew();
            int totalDaysLoaded = 0;

            try
            {
                await foreach (var batch in TickBatchStreamReader.StreamDayBatchesAsync(
                    coin,
                    startDate,
                    endDate,
                    ct,
                    msg => _logQueue.Enqueue((msg, System.Drawing.Color.FromArgb(250, 204, 21)))))
                {
                    if (ct.IsCancellationRequested) break;

                    totalDaysLoaded++;
                    _logQueue.Enqueue(($"[分批读取 #{batch.DayIndex}/{batch.TotalDays}] {batch.Date:yyyy-MM-dd} 读取 {batch.Ticks.Length:N0} 笔 Tick (耗时 {batch.ReadElapsedMs} ms)，正在增量生成...", System.Drawing.Color.FromArgb(56, 189, 248)));

                    await session.ProcessBatchAsync(
                        batch.Ticks,
                        async (bar, processedTicks, stats) =>
                        {
                            _currentBars.Add(bar);
                            _currentStats = stats;

                            if (isStreamingMode)
                            {
                                long now = localSw.ElapsedMilliseconds;
                                if (now - lastPlotRefreshTime >= 25 || _currentBars.Count <= 5)
                                {
                                    lastPlotRefreshTime = now;
                                    await this.InvokeAsync(() =>
                                    {
                                        int pct = (int)((double)batch.DayIndex / batch.TotalDays * 100);
                                        progressBar.Value = Math.Clamp(pct, 0, 100);
                                        lblProgress.Text = $"第 {batch.DayIndex}/{batch.TotalDays} 天 ({batch.Date:MM-dd}) | 已生成: {_currentBars.Count:N0} 根 Bar";

                                        UpdateStatLabels(stats);
                                        RedrawCurrentPlot(autoScale: false, autoFollow: true);
                                    });
                                }
                            }
                        },
                        () => _isPaused,
                        ct,
                        batchYieldBars: batchYield,
                        sleepIntervalMs: isStreamingMode ? sleepMs : 0);

                    if (!isStreamingMode)
                    {
                        await this.InvokeAsync(() =>
                        {
                            int pct = (int)((double)batch.DayIndex / batch.TotalDays * 100);
                            progressBar.Value = Math.Clamp(pct, 0, 100);
                            lblProgress.Text = $"已批量处理 {batch.DayIndex}/{batch.TotalDays} 天 ({batch.Date:yyyy-MM-dd}) | K线: {_currentBars.Count:N0} 根";
                            UpdateStatLabels(session.Stats);
                        });
                    }
                }

                if (totalDaysLoaded == 0 && !ct.IsCancellationRequested)
                {
                    _logQueue.Enqueue(($"[警告] 未在本地找到 {coin} 在 {startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd} 的 Tick Parquet 数据文件！", System.Drawing.Color.FromArgb(244, 63, 94)));
                    MessageBox.Show($"未找到 {coin} 在 {startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd} 的 Tick Parquet 数据文件，请检查数据目录。", "无数据", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                if (!ct.IsCancellationRequested)
                {
                    var lastBar = session.FlushLastBar();
                    if (lastBar.HasValue)
                    {
                        _currentBars.Add(lastBar.Value);
                    }
                    _currentStats = session.Stats;

                    await this.InvokeAsync(() =>
                    {
                        UpdateStatLabels(session.Stats);
                        if (chkAutoFollow.Checked)
                        {
                            RedrawCurrentPlot(autoScale: false, autoFollow: true);
                        }
                        else
                        {
                            RedrawCurrentPlot(autoScale: true, autoFollow: false);
                        }
                        progressBar.Value = 100;
                        lblProgress.Text = $"全部分批处理完毕！共 {_currentBars.Count:N0} 根 K 线";
                    });

                    _logQueue.Enqueue(($"[完成] 成功分批处理完成！生成 {_currentBars.Count:N0} 根 K 线，平均每根跨度: {FormatTimeSpan(_currentStats?.AverageBarDuration ?? TimeSpan.Zero)} | 速率: {_currentStats?.TicksPerSecond:N0} ticks/s", System.Drawing.Color.FromArgb(74, 222, 128)));
                }
            }
            catch (OperationCanceledException)
            {
                _logQueue.Enqueue(("[操作中断] 用户停止了分批生成与回放。", System.Drawing.Color.FromArgb(250, 204, 21)));
                lblProgress.Text = "已停止";
            }
            catch (Exception ex)
            {
                _logQueue.Enqueue(($"[异常错误] {ex.Message}\n{ex.StackTrace}", System.Drawing.Color.FromArgb(244, 63, 94)));
                MessageBox.Show($"生成异常: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _isRunning = false;
                _isPaused = false;
                btnStart.Enabled = true;
                btnPause.Enabled = false;
                btnPause.Text = "⏸ 暂停";
                btnStop.Enabled = false;
            }
        }

        private void UpdateStatLabels(PercentBarGenerationStats stats)
        {
            lblStatTicks.Text = $"读取 Tick: {stats.TotalTicks:N0} 笔 | 耗时: {stats.ElapsedMilliseconds} ms";
            lblStatBars.Text = $"生成 K 线: {stats.TotalBars:N0} 根 (纯序号 X 轴)";
            lblStatAvgDuration.Text = $"平均时间跨度: {FormatTimeSpan(stats.AverageBarDuration)}";
            lblStatMinMaxDuration.Text = $"最快突破: {FormatTimeSpan(stats.MinBarDuration)} | 最长: {FormatTimeSpan(stats.MaxBarDuration)}";
            lblStatPriceRange.Text = $"最高价: {stats.MaxPrice:F2} | 最低价: {stats.MinPrice:F2}";
            lblStatThroughput.Text = $"吞吐速率: {stats.TicksPerSecond:N0} ticks/s";
        }

        private void AutoRegenerateIfLoaded()
        {
            if (_currentBars.Count > 0 && !_isRunning)
            {
                _ = StartGenerateAsync();
            }
        }

        private void StopGenerate()
        {
            _cts?.Cancel();
        }

        private void RedrawCurrentPlot(bool autoScale = false, bool autoFollow = false)
        {
            if (_currentBars.Count == 0 || formsPlot.IsDisposed) return;

            var oldLimits = formsPlot.Plot.Axes.GetLimits();

            string coin = cboCoin.SelectedItem?.ToString() ?? "BTCUSDT";
            SliceUnitType sliceUnit = (SliceUnitType)cboSliceUnit.SelectedIndex;
            decimal threshold = numThresholdValue.Value;
            PercentBarMode mode = (PercentBarMode)cboBarMode.SelectedIndex;
            string unitDesc = sliceUnit == SliceUnitType.Percentage ? $"涨跌每达到 ±{threshold:F2}%" : $"价格每变化 ±{threshold:F2} USDT";

            PercentPlotHelper.BuildPlot(
                formsPlot.Plot,
                _currentBars,
                coin: coin,
                thresholdValue: threshold,
                sliceUnit: sliceUnit,
                mode: mode,
                stats: _currentStats,
                title: $"{coin} 基于 Tick 数据的 {(sliceUnit == SliceUnitType.Percentage ? "百分比" : "固定价格")} K 线走势图 ({unitDesc} 递增)",
                autoScaleAxes: autoScale,
                selectedBarStartIndex: _selectedBarStart,
                selectedBarEndIndex: _selectedBarEnd,
                chartType: _chartType,
                showPivots: chkShowPivots?.Checked ?? true,
                showGlobalHighLow: chkShowGlobalHighLow?.Checked ?? true,
                showZigZag: chkShowZigZag?.Checked ?? true,
                showTrendLines: chkShowTrendLines?.Checked ?? true,
                pivotWindow: (int)(numPivotWindow?.Value ?? 3),
                selectedTrendLine: _selectedTrendLine,
                touchTolerancePct: (numTouchTolerance?.Value ?? 0.030m) / 100m,
                showVolume: chkShowVolume?.Checked ?? true,
                strategyTrades: _lastBacktestReport?.Trades,
                showStrategyMarkers: chkShowStrategyMarkers?.Checked ?? true);

            int total = _currentBars.Count;

            if (autoFollow && chkAutoFollow.Checked && total > 0)
            {
                int windowSize = 75;
                int startIdx = Math.Max(0, total - windowSize);
                decimal visibleMinPrice = decimal.MaxValue;
                decimal visibleMaxPrice = decimal.MinValue;
                double visibleMaxVolume = 0;

                for (int b = startIdx; b < total; b++)
                {
                    var k = _currentBars[b];
                    if (k.Low < visibleMinPrice) visibleMinPrice = k.Low;
                    if (k.High > visibleMaxPrice) visibleMaxPrice = k.High;
                    double v = (double)k.Volume;
                    if (v > visibleMaxVolume) visibleMaxVolume = v;
                }

                if (visibleMinPrice <= visibleMaxPrice && visibleMinPrice > 0)
                {
                    double padding = (double)(visibleMaxPrice - visibleMinPrice) * 0.12;
                    if (padding <= 0) padding = (double)visibleMaxPrice * 0.01;
                    double yMin = (double)visibleMinPrice - padding;
                    double yMax = (double)visibleMaxPrice + padding;
                    double xMin = Math.Max(-0.5, total - windowSize);
                    double xMax = total + 3.5;

                    formsPlot.Plot.Axes.SetLimits(xMin, xMax, yMin, yMax);

                    if (chkShowVolume?.Checked ?? true)
                    {
                        if (visibleMaxVolume <= 0) visibleMaxVolume = 1;
                        formsPlot.Plot.Axes.SetLimitsY(0, visibleMaxVolume * 3.8, formsPlot.Plot.Axes.Right);
                    }
                }
                else
                {
                    formsPlot.Plot.Axes.AutoScale();
                }
            }
            else if (!autoScale && oldLimits.Right > oldLimits.Left && oldLimits.Top > oldLimits.Bottom)
            {
                formsPlot.Plot.Axes.SetLimits(oldLimits);
            }

            formsPlot.Refresh();
        }

        private void OnFormsPlotMouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || _currentBars.Count == 0) return;

            try
            {
                // 1. 优先判定是否点击命中趋势线 (屏幕像素距离 <= 8.5 像素)
                if (chkShowTrendLines != null && chkShowTrendLines.Checked)
                {
                    decimal tol = (numTouchTolerance?.Value ?? 0.030m) / 100m;
                    var pivotAnalysis = PivotDetector.CalculatePivots(_currentBars, window: (int)(numPivotWindow?.Value ?? 3), alternateHighLow: true);
                    var trendlines = PivotDetector.CalculateTrendLines(_currentBars, pivotAnalysis, maxLines: 1000, maxSpanBars: 1000, extensionBars: 8, strictWickPenetration: true, touchTolerancePct: tol);

                    TrendLine? closestLine = null;
                    double minPixelDist = double.MaxValue;

                    PointF mousePixel = new PointF(e.X, e.Y);

                    foreach (var tl in trendlines)
                    {
                        var pixStart = formsPlot.Plot.GetPixel(new ScottPlot.Coordinates(tl.StartBarIndex, (double)tl.StartPrice));
                        var pixEnd = formsPlot.Plot.GetPixel(new ScottPlot.Coordinates(tl.ExtendedBarIndex, (double)tl.ExtendedPrice));

                        double dist = DistanceToSegment(mousePixel, new PointF((float)pixStart.X, (float)pixStart.Y), new PointF((float)pixEnd.X, (float)pixEnd.Y));
                        if (dist <= 8.5 && dist < minPixelDist)
                        {
                            minPixelDist = dist;
                            closestLine = tl;
                        }
                    }

                    if (closestLine.HasValue)
                    {
                        _selectedTrendLine = closestLine.Value;
                        _selectedBarStart = null;
                        _selectedBarEnd = null;
                        OutputTrendLineDetails(closestLine.Value, _currentBars[_currentBars.Count - 1]);
                        RedrawCurrentPlot(autoScale: false);
                        return;
                    }
                }

                // 2. 若未点击到趋势线，则判定是否点击选中 K 线 (支持单选与按住 Shift 连续多选)
                var mouseCoord = formsPlot.Plot.GetCoordinates(new ScottPlot.Pixel(e.X, e.Y));
                int targetIndex = (int)Math.Round(mouseCoord.X);

                if (targetIndex >= 0 && targetIndex < _currentBars.Count)
                {
                    var bar = _currentBars[targetIndex];

                    var pixelClose = formsPlot.Plot.GetPixel(new ScottPlot.Coordinates(targetIndex, (double)bar.Close));
                    var pixelHigh = formsPlot.Plot.GetPixel(new ScottPlot.Coordinates(targetIndex, (double)bar.High));
                    var pixelLow = formsPlot.Plot.GetPixel(new ScottPlot.Coordinates(targetIndex, (double)bar.Low));

                    double topY = Math.Min(pixelHigh.Y, pixelLow.Y) - 30;
                    double bottomY = Math.Max(pixelHigh.Y, pixelLow.Y) + 30;
                    double leftX = pixelClose.X - 25;
                    double rightX = pixelClose.X + 25;

                    if (e.X >= leftX && e.X <= rightX && e.Y >= topY && e.Y <= bottomY)
                    {
                        bool isShift = (ModifierKeys & Keys.Shift) == Keys.Shift;

                        if (isShift && _selectedBarStart.HasValue)
                        {
                            // 🌟 按住 Shift 连续多选：扩展选区终点
                            _selectedBarEnd = targetIndex;
                        }
                        else
                        {
                            // 🌟 单选：重置起点与终点
                            _selectedBarStart = targetIndex;
                            _selectedBarEnd = targetIndex;
                        }

                        _selectedTrendLine = null;

                        int minBar = Math.Min(_selectedBarStart.Value, _selectedBarEnd.Value);
                        int maxBar = Math.Max(_selectedBarStart.Value, _selectedBarEnd.Value);

                        if (minBar == maxBar)
                        {
                            OutputBarDetails(bar);
                            DisplayBarsTickDetails(new[] { bar });
                        }
                        else
                        {
                            var selectedBars = new List<PercentageKline>(maxBar - minBar + 1);
                            for (int b = minBar; b <= maxBar; b++)
                            {
                                selectedBars.Add(_currentBars[b]);
                            }
                            OutputMultipleBarsDetails(selectedBars);
                            DisplayBarsTickDetails(selectedBars);
                        }

                        RedrawCurrentPlot(autoScale: false);
                    }
                }
            }
            catch
            {
            }
        }

        private static double DistanceToSegment(PointF p, PointF a, PointF b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double l2 = dx * dx + dy * dy;
            if (l2 < 1e-6)
            {
                double px = p.X - a.X;
                double py = p.Y - a.Y;
                return Math.Sqrt(px * px + py * py);
            }
            double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2;
            t = Math.Clamp(t, 0.0, 1.0);
            double projX = a.X + t * dx;
            double projY = a.Y + t * dy;
            double diffX = p.X - projX;
            double diffY = p.Y - projY;
            return Math.Sqrt(diffX * diffX + diffY * diffY);
        }

        /// <summary>
        /// 🌟 全量输出点击趋势线的所有明细指标到日志栏 (包含起止拐点、多点共线验证、几何斜率、存活跨度、与当前最新价差及突破防线状态)
        /// </summary>
        private void OutputTrendLineDetails(in TrendLine tl, in PercentageKline lastBar)
        {
            bool isRes = tl.Type == TrendLineType.Resistance;
            bool isThreePoint = tl.IsThreePointConfirmed;

            string typeTitle = isThreePoint
                ? (isRes ? $"🟣 3点+共线强阻力趋势线 (共{tl.TouchCount}点触碰，紫色核心线)" : $"🟣 3点+共线强支撑趋势线 (共{tl.TouchCount}点触碰，紫色核心线)")
                : (isRes ? "🔴 高点阻力趋势线 (Upper Resistance Trendline)" : "🟢 低点支撑趋势线 (Lower Support Trendline)");

            var themeColor = isThreePoint
                ? System.Drawing.Color.FromArgb(192, 132, 252) // Purple 400
                : (isRes ? System.Drawing.Color.FromArgb(244, 63, 94) : System.Drawing.Color.FromArgb(74, 222, 128));

            decimal slopePctPerBar = tl.StartPrice > 0 ? (tl.Slope / tl.StartPrice) * 100m : 0m;
            decimal diffFromCurrent = lastBar.Close - tl.CurrentBarPrice;
            decimal diffPctFromCurrent = tl.CurrentBarPrice > 0 ? (diffFromCurrent / tl.CurrentBarPrice) * 100m : 0m;

            DateTime startTime = DateTimeOffset.FromUnixTimeMilliseconds(tl.StartTime).UtcDateTime;
            DateTime endTime = DateTimeOffset.FromUnixTimeMilliseconds(tl.EndTime).UtcDateTime;
            TimeSpan spanDuration = endTime - startTime;

            _logQueue.Enqueue(("\n╔══════════════════════════════════════════════════════════════════════════════════════════", themeColor));
            _logQueue.Enqueue(($"║ 📈【自动高低点趋势线明细报告】 ★ 选中趋势线 {(isThreePoint ? "【🟣 3点+共线强趋势线】" : "")}", themeColor));
            _logQueue.Enqueue(("╠──────────────────────────────────────────────────────────────────────────────────────────", System.Drawing.Color.FromArgb(71, 85, 105)));
            _logQueue.Enqueue(($"║ 🏷️ 趋势线形态: {typeTitle}", themeColor));

            if (isThreePoint && tl.TouchBarIndices != null && tl.TouchBarIndices.Length >= 3)
            {
                string touchPointsStr = string.Join(", ", tl.TouchBarIndices.Select(idx => $"Bar #{idx}"));
                _logQueue.Enqueue(($"║ 🟣【多点共线验证】: 获得 {tl.TouchCount} 个极值点高精度共线确认！(共线极值点: {touchPointsStr})", System.Drawing.Color.FromArgb(233, 213, 255)));
            }

            _logQueue.Enqueue(($"║ 📍 起始极值点 (Point 1): Bar #{tl.StartBarIndex} | 价格: {tl.StartPrice:F2} USDT | 发生时间(UTC): {startTime:yyyy-MM-dd HH:mm:ss}", System.Drawing.Color.FromArgb(241, 245, 249)));
            _logQueue.Enqueue(($"║ 📍 关键锚定点 (Point 2): Bar #{tl.EndBarIndex}   | 价格: {tl.EndPrice:F2} USDT   | 发生时间(UTC): {endTime:yyyy-MM-dd HH:mm:ss}", System.Drawing.Color.FromArgb(241, 245, 249)));
            _logQueue.Enqueue(("╠──────────────────────────────────────────────────────────────────────────────────────────", System.Drawing.Color.FromArgb(71, 85, 105)));
            _logQueue.Enqueue(($"║ 📐 趋势线几何与时空特征:", System.Drawing.Color.FromArgb(56, 189, 248)));
            _logQueue.Enqueue(($"║    • 跨度跨越 Bar: {tl.SpanBars} 根 Bar (真实时间跨越: {PercentPlotHelper.FormatTimeSpan(spanDuration)})", System.Drawing.Color.FromArgb(241, 245, 249)));
            _logQueue.Enqueue(($"║    • 价格斜率 (Slope): {tl.Slope:+0.0000;-0.0000;0.0000} USDT/Bar (单Bar斜率变动: {slopePctPerBar:+0.000%;-0.000%;0.000%})", themeColor));
            _logQueue.Enqueue(($"║    • 存活年龄 (Age): 距离最新已持续存活 {tl.AgeBars} 根 Bar (全程未被任何后续 K 线破坏击穿)", System.Drawing.Color.FromArgb(250, 204, 21)));
            _logQueue.Enqueue(("╠──────────────────────────────────────────────────────────────────────────────────────────", System.Drawing.Color.FromArgb(71, 85, 105)));
            _logQueue.Enqueue(($"║ 🎯 当前价格映射与通道研判:", System.Drawing.Color.FromArgb(250, 204, 21)));
            if (tl.IsBroken)
            {
                _logQueue.Enqueue(($"║    • 穿透终结点: 在 Bar #{tl.BreakBarIndex} 处被 K 线穿透破坏，生命周期终止 (不再参与后续判定与延伸)", System.Drawing.Color.FromArgb(250, 204, 21)));
                _logQueue.Enqueue(($"║    • 穿透终点价格: {tl.ExtendedPrice:F2} USDT", System.Drawing.Color.FromArgb(241, 245, 249)));
            }
            else
            {
                _logQueue.Enqueue(($"║    • 最新 Bar #{lastBar.BarIndex} 处趋势线价格: {tl.CurrentBarPrice:F2} USDT", System.Drawing.Color.FromArgb(241, 245, 249)));
                _logQueue.Enqueue(($"║    • 当前最新收盘价 ({lastBar.Close:F2}) 相对趋势线位差: {diffFromCurrent:+0.00;-0.00;0.00} USDT ({diffPctFromCurrent:+0.00%;-0.00%;0.00%})", themeColor));
                _logQueue.Enqueue(($"║    • 延伸端点 Bar #{tl.ExtendedBarIndex} 远期投影价格: {tl.ExtendedPrice:F2} USDT", System.Drawing.Color.FromArgb(148, 163, 184)));
            }

            string statusDesc = isThreePoint
                ? (tl.IsBroken ? $"⚡ 历史强共线结构 (在 Bar #{tl.BreakBarIndex} 被穿透并终结，作为核心紫色历史线保留)" : "✅ 活跃有效 (全程未被破坏击穿，🟣3点+永久保留)")
                : "✅ 活跃有效 (区间内部与后续行进已通过严格防穿透校验，至少保留1000根)";

            _logQueue.Enqueue(($"║ 🛡️ 状态判定: {statusDesc}", tl.IsBroken ? System.Drawing.Color.FromArgb(250, 204, 21) : System.Drawing.Color.FromArgb(74, 222, 128)));
            _logQueue.Enqueue(("╚══════════════════════════════════════════════════════════════════════════════════════════", themeColor));
        }

        /// <summary>
        /// 🌟 全量输出点击 K 线的所有明细指标到日志栏 (包含高低极值定位、精确时间跨度、价格行情、波动形态与逐笔量能)
        /// </summary>
        private void OutputBarDetails(in PercentageKline bar)
        {
            bool isBull = bar.Close >= bar.Open;
            string barType = isBull ? "🟢 阳线 (上涨突破)" : "🔴 阴线 (下跌突破)";
            var themeColor = isBull ? System.Drawing.Color.FromArgb(74, 222, 128) : System.Drawing.Color.FromArgb(244, 63, 94);

            decimal body = Math.Abs(bar.Close - bar.Open);
            decimal upperShadow = bar.High - Math.Max(bar.Open, bar.Close);
            decimal lowerShadow = Math.Min(bar.Open, bar.Close) - bar.Low;
            decimal totalRange = bar.High - bar.Low;
            decimal bodyRatio = totalRange > 0 ? (body / totalRange) * 100m : 100m;
            decimal upperRatio = totalRange > 0 ? (upperShadow / totalRange) * 100m : 0m;
            decimal lowerRatio = totalRange > 0 ? (lowerShadow / totalRange) * 100m : 0m;

            decimal takerBuyPct = bar.Volume > 0 ? (bar.TakerBuyVolume / bar.Volume) * 100m : 0m;
            decimal takerSellPct = 100m - takerBuyPct;
            decimal takerSellVol = Math.Max(0m, bar.Volume - bar.TakerBuyVolume);
            decimal takerSellQuote = Math.Max(0m, bar.QuoteVolume - bar.TakerBuyQuoteVolume);
            decimal avgPricePerTrade = bar.TradeCount > 0 ? bar.QuoteVolume / bar.TradeCount : 0m;
            double tickDensity = bar.Duration.TotalSeconds > 0 ? bar.TickCount / bar.Duration.TotalSeconds : bar.TickCount;

            _logQueue.Enqueue(("\n╔══════════════════════════════════════════════════════════════════════════════════════════", themeColor));
            _logQueue.Enqueue(($"║ 📊【百分比 K 线完整信息报告】 Bar #{bar.BarIndex}  {barType}", themeColor));

            // 高低点位分析与定位
            int currentBarIdx = bar.BarIndex;
            var pivotAnalysis = PivotDetector.CalculatePivots(_currentBars, window: (int)(numPivotWindow?.Value ?? 3), alternateHighLow: true);
            var matchedPivot = pivotAnalysis.Pivots.Find(p => p.BarIndex == currentBarIdx);
            bool isGlobalHigh = pivotAnalysis.GlobalHigh.HasValue && pivotAnalysis.GlobalHigh.Value.BarIndex == currentBarIdx;
            bool isGlobalLow = pivotAnalysis.GlobalLow.HasValue && pivotAnalysis.GlobalLow.Value.BarIndex == currentBarIdx;

            if (isGlobalHigh || isGlobalLow || matchedPivot.BarIndex == currentBarIdx)
            {
                _logQueue.Enqueue(("╠──────────────────────────────────────────────────────────────────────────────────────────", System.Drawing.Color.FromArgb(71, 85, 105)));
                string extremeTag = isGlobalHigh ? "👑 历史最高点 (Global High)" : (isGlobalLow ? "👑 历史最低点 (Global Low)" : "");
                string pivotTag = matchedPivot.Type == PivotPointType.High ? "🔴 局部波峰高点 (Swing High)" : "🟢 局部波谷低点 (Swing Low)";
                string diffInfo = matchedPivot.BarsFromPrev > 0
                    ? $" | 距前一拐点: {matchedPivot.BarsFromPrev} 根Bar, 波动 {matchedPivot.PriceChangeFromPrev:+0.00;-0.00;0.00} USDT ({matchedPivot.PriceChangePctFromPrev:+0.00;-0.00;0.00}%)"
                    : "";

                string finalMsg = string.IsNullOrEmpty(extremeTag)
                    ? $"║ 🎯【高低极值定位】: {pivotTag}{diffInfo}"
                    : $"║ 🎯【高低极值定位】: {extremeTag} ({pivotTag}){diffInfo}";

                _logQueue.Enqueue((finalMsg, System.Drawing.Color.FromArgb(250, 204, 21)));
            }

            _logQueue.Enqueue(("╠──────────────────────────────────────────────────────────────────────────────────────────", System.Drawing.Color.FromArgb(71, 85, 105)));
            _logQueue.Enqueue(($"║ ⏱️ 🌟【精确时间跨度】: {bar.GetFormattedDuration()} (耗时共计 {bar.Duration.TotalSeconds:F3} 秒, {bar.Duration.TotalMilliseconds:N0} ms)", System.Drawing.Color.FromArgb(250, 204, 21)));
            _logQueue.Enqueue(($"║    • 开盘时间 (UTC+0): {bar.OpenDateTime:yyyy-MM-dd HH:mm:ss.fff}  |  本地: {bar.OpenDateTime.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}", System.Drawing.Color.FromArgb(241, 245, 249)));
            _logQueue.Enqueue(($"║    • 收盘时间 (UTC+0): {bar.CloseDateTime:yyyy-MM-dd HH:mm:ss.fff}  |  本地: {bar.CloseDateTime.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}", System.Drawing.Color.FromArgb(241, 245, 249)));
            _logQueue.Enqueue(("╠──────────────────────────────────────────────────────────────────────────────────────────", System.Drawing.Color.FromArgb(71, 85, 105)));
            _logQueue.Enqueue(($"║ 💰【四值价格行情】: 开盘价={bar.Open:F2}  |  最高价={bar.High:F2}  |  最低价={bar.Low:F2}  |  收盘价={bar.Close:F2}", System.Drawing.Color.FromArgb(241, 245, 249)));
            _logQueue.Enqueue(($"║    • 涨跌额与幅度: 净涨跌={bar.PriceChange:+0.00;-0.00;0.00}  |  涨跌幅={bar.PriceChangePct:+0.00;-0.00;0.00}%  |  极值全振幅={bar.PriceAmplitudePct:F2}%", themeColor));
            _logQueue.Enqueue(($"║    • K线结构分解: 实体={body:F2} ({bodyRatio:F1}%)  |  上影线={upperShadow:F2} ({upperRatio:F1}%)  |  下影线={lowerShadow:F2} ({lowerRatio:F1}%)", System.Drawing.Color.FromArgb(226, 232, 240)));
            _logQueue.Enqueue(("╠──────────────────────────────────────────────────────────────────────────────────────────", System.Drawing.Color.FromArgb(71, 85, 105)));
            _logQueue.Enqueue(($"║ ⚡【逐笔撮合成交与量能分析】", System.Drawing.Color.FromArgb(56, 189, 248)));
            _logQueue.Enqueue(($"║    • 涵盖 Tick 笔数: {bar.TickCount:N0} 笔 (Tick 撮合频率密度: {tickDensity:F1} 笔/秒)", System.Drawing.Color.FromArgb(56, 189, 248)));
            _logQueue.Enqueue(($"║    • 基础币总成交量: {bar.Volume:N4} (Base Coin)", System.Drawing.Color.FromArgb(241, 245, 249)));
            _logQueue.Enqueue(($"║    • 计价币总成交额: {bar.QuoteVolume:N2} USDT (总笔数: {bar.TradeCount:N0} 笔, 均笔成交额: {avgPricePerTrade:N2} USDT)", System.Drawing.Color.FromArgb(241, 245, 249)));
            _logQueue.Enqueue(($"║    • 主动买入成交量: {bar.TakerBuyVolume:N4} ({takerBuyPct:F1}%)  |  主动买入额: {bar.TakerBuyQuoteVolume:N2} USDT", System.Drawing.Color.FromArgb(74, 222, 128)));
            _logQueue.Enqueue(($"║    • 主动卖出成交量: {takerSellVol:N4} ({takerSellPct:F1}%)  |  主动卖出额: {takerSellQuote:N2} USDT", System.Drawing.Color.FromArgb(244, 63, 94)));
            _logQueue.Enqueue(("╚══════════════════════════════════════════════════════════════════════════════════════════", themeColor));
        }

        /// <summary>
        /// 🌟 全量输出 Shift 连续选中的多根 K 线的综合指标报告到日志栏
        /// </summary>
        private void OutputMultipleBarsDetails(IReadOnlyList<PercentageKline> bars)
        {
            if (bars == null || bars.Count == 0) return;

            var firstBar = bars[0];
            var lastBar = bars[^1];

            decimal totalVol = 0m;
            decimal totalQuote = 0m;
            decimal totalTakerBuyVol = 0m;
            decimal totalTakerBuyQuote = 0m;
            long totalTrades = 0;
            int totalTicks = 0;

            decimal highPrice = decimal.MinValue;
            decimal lowPrice = decimal.MaxValue;

            for (int i = 0; i < bars.Count; i++)
            {
                var b = bars[i];
                if (b.High > highPrice) highPrice = b.High;
                if (b.Low < lowPrice) lowPrice = b.Low;
                totalVol += b.Volume;
                totalQuote += b.QuoteVolume;
                totalTakerBuyVol += b.TakerBuyVolume;
                totalTakerBuyQuote += b.TakerBuyQuoteVolume;
                totalTrades += b.TradeCount;
                totalTicks += b.TickCount;
            }

            decimal openPrice = firstBar.Open;
            decimal closePrice = lastBar.Close;
            decimal netChange = closePrice - openPrice;
            decimal netChangePct = openPrice > 0 ? (netChange / openPrice) * 100m : 0m;
            decimal rangeAmp = lowPrice > 0 ? ((highPrice - lowPrice) / lowPrice) * 100m : 0m;

            bool isBull = closePrice >= openPrice;
            var themeColor = isBull ? System.Drawing.Color.FromArgb(74, 222, 128) : System.Drawing.Color.FromArgb(244, 63, 94);
            TimeSpan totalSpan = lastBar.CloseDateTime - firstBar.OpenDateTime;

            decimal takerBuyPct = totalVol > 0 ? (totalTakerBuyVol / totalVol) * 100m : 0m;
            decimal takerSellPct = 100m - takerBuyPct;
            decimal takerSellVol = Math.Max(0m, totalVol - totalTakerBuyVol);
            decimal takerSellQuote = Math.Max(0m, totalQuote - totalTakerBuyQuote);

            _logQueue.Enqueue(("\n╔══════════════════════════════════════════════════════════════════════════════════════════", themeColor));
            _logQueue.Enqueue(($"║ 📊【多根 K 线连续选中分析报告】 共连续选中 {bars.Count} 根 Bar (Bar #{firstBar.BarIndex} ~ #{lastBar.BarIndex}) {(isBull ? "🟢 整体上涨" : "🔴 整体下跌")}", themeColor));
            _logQueue.Enqueue(("╠──────────────────────────────────────────────────────────────────────────────────────────", System.Drawing.Color.FromArgb(71, 85, 105)));
            _logQueue.Enqueue(($"║ ⏱️ 连续时间跨度: {firstBar.OpenDateTime:yyyy-MM-dd HH:mm:ss.fff} ~ {lastBar.CloseDateTime:yyyy-MM-dd HH:mm:ss.fff} (总耗时: {FormatTimeSpan(totalSpan)})", System.Drawing.Color.FromArgb(250, 204, 21)));
            _logQueue.Enqueue(("╠──────────────────────────────────────────────────────────────────────────────────────────", System.Drawing.Color.FromArgb(71, 85, 105)));
            _logQueue.Enqueue(($"║ 💰 连续价格走势: 起点开盘={openPrice:F2} -> 终点收盘={closePrice:F2} | 全局最高={highPrice:F2} | 全局最低={lowPrice:F2}", System.Drawing.Color.FromArgb(241, 245, 249)));
            _logQueue.Enqueue(($"║    • 区间净涨跌: {netChange:+0.00;-0.00;0.00} USDT ({netChangePct:+0.00;-0.00;0.00}%) | 极值全振幅: {rangeAmp:F2}%", themeColor));
            _logQueue.Enqueue(("╠──────────────────────────────────────────────────────────────────────────────────────────", System.Drawing.Color.FromArgb(71, 85, 105)));
            _logQueue.Enqueue(($"║ ⚡ 涵盖逐笔量能: 总 Tick={totalTicks:N0} 笔 | 基础币总成交量={totalVol:N4} | 计价币总成交额={totalQuote:N2} USDT (总撮合: {totalTrades:N0} 笔)", System.Drawing.Color.FromArgb(56, 189, 248)));
            _logQueue.Enqueue(($"║    • 主动买入量: {totalTakerBuyVol:N4} ({takerBuyPct:F1}%) | 主动买入额: {totalTakerBuyQuote:N2} USDT", System.Drawing.Color.FromArgb(74, 222, 128)));
            _logQueue.Enqueue(($"║    • 主动卖出量: {takerSellVol:N4} ({takerSellPct:F1}%) | 主动卖出额: {takerSellQuote:N2} USDT", System.Drawing.Color.FromArgb(244, 63, 94)));
            _logQueue.Enqueue(("╚══════════════════════════════════════════════════════════════════════════════════════════", themeColor));
        }

        /// <summary>
        /// 🌟 在日志右侧区域极速渲染选中单根或 Shift 连续多根 Bar 内部的全部 Tick 走势图与逐笔流水表 (采用 VirtualMode 与量能聚合，零延迟瞬开)
        /// </summary>
        private void DisplayBarsTickDetails(IReadOnlyList<PercentageKline> bars)
        {
            if (bars == null || bars.Count == 0)
            {
                lblTickInfo.Text = "未选择 K 线 (请在上方图表中点击任意一根 K 线，或按住 Shift 连续多选)";
                formsPlotTick.Plot.Clear();
                formsPlotTick.Plot.Title("未选择 K 线");
                formsPlotTick.Refresh();
                _displayedTicks.Clear();
                dgvTicks.RowCount = 0;
                return;
            }

            // 收集所有选中 Bar 的 Tick 切片及边界索引
            var allTicks = new List<Common.RawTick>();
            var barBoundaries = new List<(int TickIndex, int BarIndex)>();

            for (int b = 0; b < bars.Count; b++)
            {
                var curBar = bars[b];
                if (curBar.Ticks != null && curBar.Ticks.Length > 0)
                {
                    barBoundaries.Add((allTicks.Count, curBar.BarIndex));
                    allTicks.AddRange(curBar.Ticks);
                }
            }

            int totalTicks = allTicks.Count;
            if (totalTicks == 0)
            {
                string barRangeStr = bars.Count == 1 ? $"Bar #{bars[0].BarIndex}" : $"Bar #{bars[0].BarIndex} ~ #{bars[^1].BarIndex}";
                lblTickInfo.Text = $"{barRangeStr} | 所选 Bar 暂无可用 Tick 逐笔数据";
                formsPlotTick.Plot.Clear();
                formsPlotTick.Plot.Title($"{barRangeStr} 无 Tick 数据");
                formsPlotTick.Refresh();
                _displayedTicks.Clear();
                dgvTicks.RowCount = 0;
                return;
            }

            var firstBar = bars[0];
            var lastBar = bars[^1];

            decimal overallOpen = firstBar.Open;
            decimal overallClose = lastBar.Close;
            decimal overallHigh = decimal.MinValue;
            decimal overallLow = decimal.MaxValue;
            decimal totalVol = 0m;
            decimal totalTakerBuyVol = 0m;

            for (int b = 0; b < bars.Count; b++)
            {
                if (bars[b].High > overallHigh) overallHigh = bars[b].High;
                if (bars[b].Low < overallLow) overallLow = bars[b].Low;
                totalVol += bars[b].Volume;
                totalTakerBuyVol += bars[b].TakerBuyVolume;
            }

            string dirStr = overallClose >= overallOpen ? "🟢 阳线/涨" : "🔴 阴线/跌";
            decimal takerBuyRatio = totalVol > 0 ? (totalTakerBuyVol / totalVol * 100m) : 0m;
            TimeSpan totalSpan = lastBar.CloseDateTime - firstBar.OpenDateTime;

            if (bars.Count == 1)
            {
                lblTickInfo.Text = $"Bar #{firstBar.BarIndex} ({dirStr}) | 耗时: {firstBar.GetFormattedDuration()} | Tick: {totalTicks:N0} 笔 | 极值: {overallLow:F2} ~ {overallHigh:F2} | 主买: {takerBuyRatio:F1}%";
            }
            else
            {
                lblTickInfo.Text = $"★ 选中连续 {bars.Count} 根 K 线 (Bar #{firstBar.BarIndex} ~ #{lastBar.BarIndex}, {dirStr}) | 跨度: {FormatTimeSpan(totalSpan)} | Tick: {totalTicks:N0} 笔 | 极值: {overallLow:F2} ~ {overallHigh:F2} | 主买: {takerBuyRatio:F1}%";
            }

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

            string fontName = PercentPlotHelper.GetSafeChineseFont();
            string chartTitle = bars.Count == 1
                ? $"Bar #{firstBar.BarIndex} 内部 Tick 价格路径 (共 {totalTicks:N0} 笔 Tick, {firstBar.OpenDateTime.ToLocalTime():HH:mm:ss.fff} ~ {firstBar.CloseDateTime.ToLocalTime():HH:mm:ss.fff})"
                : $"连续 {bars.Count} 根 K 线 (Bar #{firstBar.BarIndex} ~ #{lastBar.BarIndex}) 逐笔 Tick 路径 (共 {totalTicks:N0} 笔, {firstBar.OpenDateTime.ToLocalTime():MM-dd HH:mm:ss} ~ {lastBar.CloseDateTime.ToLocalTime():MM-dd HH:mm:ss})";

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
                ys[i] = (double)allTicks[i].Price;

                if (allTicks[i].Price > maxVal) { maxVal = allTicks[i].Price; maxIdx = i; }
                if (allTicks[i].Price < minVal) { minVal = allTicks[i].Price; minIdx = i; }

                double v = (double)allTicks[i].Qty;
                if (v > maxVol) maxVol = v;
            }

            // 🌟 成交量柱状图极速聚合渲染 (若 Tick 超过 250 笔则自动像素级分箱聚合，避免为几万个 Tick 单独分配多边形导致 CPU 爆满)
            int maxVolBins = 250;
            var volBars = new List<ScottPlot.Bar>(Math.Min(totalTicks, maxVolBins));

            if (totalTicks <= maxVolBins)
            {
                for (int i = 0; i < totalTicks; i++)
                {
                    double v = (double)allTicks[i].Qty;
                    bool isBuyer = !allTicks[i].IsBuyerMaker;
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
                        double q = (double)allTicks[t].Qty;
                        binVol += q;
                        if (!allTicks[t].IsBuyerMaker) binBuyVol += q;
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

            // 绘制成交量柱状图 (绑定右侧 Y 轴)
            if (volBars.Count > 0)
            {
                var vPlot = formsPlotTick.Plot.Add.Bars(volBars);
                vPlot.Axes.YAxis = formsPlotTick.Plot.Axes.Right;
                formsPlotTick.Plot.Axes.Right.TickLabelStyle.ForeColor = ScottPlot.Color.FromHex("#64748b");
                formsPlotTick.Plot.Axes.Right.FrameLineStyle.Color = ScottPlot.Color.FromHex("#334155");
                if (maxVol <= 0) maxVol = 1;
                formsPlotTick.Plot.Axes.SetLimitsY(0, maxVol * 4.0, formsPlotTick.Plot.Axes.Right);
            }

            // 绘制主折线走势 (禁用点阵标记以实现 1ms 极限刷新)
            var scatter = formsPlotTick.Plot.Add.ScatterLine(xs, ys);
            scatter.Color = overallClose >= overallOpen ? ScottPlot.Color.FromHex("#22c55e") : ScottPlot.Color.FromHex("#ef4444");
            scatter.LineWidth = 1.3f;
            scatter.MarkerSize = 0;

            // 🌟 绘制多根 Bar 之间的分界虚线与 Bar 序号标签
            if (bars.Count > 1 && barBoundaries.Count > 1)
            {
                for (int k = 1; k < barBoundaries.Count; k++)
                {
                    int tIdx = barBoundaries[k].TickIndex;
                    int bIdx = barBoundaries[k].BarIndex;

                    var bLine = formsPlotTick.Plot.Add.VerticalLine(tIdx);
                    bLine.Color = ScottPlot.Color.FromHex("#64748b").WithAlpha(0.7);
                    bLine.LineWidth = 0.8f;
                    bLine.LinePattern = ScottPlot.LinePattern.Dashed;

                    var bText = formsPlotTick.Plot.Add.Text($"Bar #{bIdx}", tIdx, (double)overallHigh);
                    bText.LabelFontName = fontName;
                    bText.LabelFontSize = 8.0f;
                    bText.LabelFontColor = ScottPlot.Color.FromHex("#cbd5e1");
                    bText.LabelAlignment = ScottPlot.Alignment.LowerLeft;
                    bText.LabelBackgroundColor = ScottPlot.Color.FromHex("#0f172a").WithAlpha(0.85);
                }
            }

            // 🌟 标注关键最高与最低点位：红色正三角 (▲) 与 红色倒三角 (▼)
            var mHigh = formsPlotTick.Plot.Add.Marker(maxIdx, (double)maxVal);
            mHigh.Shape = ScottPlot.MarkerShape.FilledTriangleUp;
            mHigh.Size = 11;
            mHigh.Color = ScottPlot.Color.FromHex("#ef4444");

            var textHigh = formsPlotTick.Plot.Add.Text($"▲ 最高 {maxVal:F2}", maxIdx, (double)maxVal);
            textHigh.LabelFontName = fontName;
            textHigh.LabelFontSize = 8.5f;
            textHigh.LabelFontColor = ScottPlot.Color.FromHex("#fca5a5");
            textHigh.LabelAlignment = ScottPlot.Alignment.LowerCenter;
            textHigh.LabelBackgroundColor = ScottPlot.Color.FromHex("#0f172a").WithAlpha(0.85);
            textHigh.LabelBorderColor = ScottPlot.Color.FromHex("#ef4444");
            textHigh.LabelBorderWidth = 1f;

            var mLow = formsPlotTick.Plot.Add.Marker(minIdx, (double)minVal);
            mLow.Shape = ScottPlot.MarkerShape.FilledTriangleDown;
            mLow.Size = 11;
            mLow.Color = ScottPlot.Color.FromHex("#ef4444");

            var textLow = formsPlotTick.Plot.Add.Text($"▼ 最低 {minVal:F2}", minIdx, (double)minVal);
            textLow.LabelFontName = fontName;
            textLow.LabelFontSize = 8.5f;
            textLow.LabelFontColor = ScottPlot.Color.FromHex("#fca5a5");
            textLow.LabelAlignment = ScottPlot.Alignment.UpperCenter;
            textLow.LabelBackgroundColor = ScottPlot.Color.FromHex("#0f172a").WithAlpha(0.85);
            textLow.LabelBorderColor = ScottPlot.Color.FromHex("#ef4444");
            textLow.LabelBorderWidth = 1f;

            // 开盘与收盘点标记
            var mOpen = formsPlotTick.Plot.Add.Marker(0, (double)allTicks[0].Price);
            mOpen.Shape = ScottPlot.MarkerShape.FilledCircle;
            mOpen.Size = 8;
            mOpen.Color = ScottPlot.Color.FromHex("#38bdf8");

            var mClose = formsPlotTick.Plot.Add.Marker(totalTicks - 1, (double)allTicks[totalTicks - 1].Price);
            mClose.Shape = ScottPlot.MarkerShape.FilledSquare;
            mClose.Size = 8;
            mClose.Color = ScottPlot.Color.FromHex("#f97316");

            formsPlotTick.Plot.Axes.Margins(0.02, 0.12);
            formsPlotTick.Plot.Axes.AutoScale();
            formsPlotTick.Refresh();

            // 2. 🌟 极速装载 VirtualMode 虚拟数据源 (内存构建毫秒级完成，杜绝百万控件分配与阻塞)
            _displayedTicks.Clear();
            if (_displayedTicks.Capacity < totalTicks)
            {
                _displayedTicks.Capacity = totalTicks;
            }

            int tickCounter = 0;
            for (int b = 0; b < bars.Count; b++)
            {
                var curBar = bars[b];
                if (curBar.Ticks == null) continue;

                for (int t = 0; t < curBar.Ticks.Length; t++)
                {
                    var tick = curBar.Ticks[t];
                    tickCounter++;
                    bool isBuyer = !tick.IsBuyerMaker;
                    decimal diffFromOpen = overallOpen > 0 ? (tick.Price - overallOpen) / overallOpen * 100m : 0m;

                    _displayedTicks.Add(new TickViewModel
                    {
                        BarIndex = curBar.BarIndex,
                        TickIndex = tickCounter,
                        Time = tick.Time,
                        Price = tick.Price,
                        Qty = tick.Qty,
                        QuoteQty = tick.QuoteQty,
                        IsBuyer = isBuyer,
                        DiffPct = diffFromOpen
                    });
                }
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
                0 => $"Bar #{tick.BarIndex}",
                1 => tick.TickIndex,
                2 => TimeHelper.FromUnixTimeMilliseconds(tick.Time).ToLocalTime().ToString("HH:mm:ss.fff"),
                3 => tick.Price.ToString("F2"),
                4 => tick.Qty.ToString("F4"),
                5 => tick.QuoteQty.ToString("F2"),
                6 => tick.IsBuyer ? "🟢 买方主动" : "🔴 卖方主动",
                7 => $"{tick.DiffPct:+0.00;-0.00;0.00}%",
                _ => null
            };
        }

        private void OnDgvTicksCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _displayedTicks.Count || e.CellStyle == null) return;
            bool isBuyer = _displayedTicks[e.RowIndex].IsBuyer;
            e.CellStyle.ForeColor = isBuyer
                ? System.Drawing.Color.FromArgb(74, 222, 128)
                : System.Drawing.Color.FromArgb(248, 113, 113);
        }

        private void AppendLogInternal(string message, System.Drawing.Color color)
        {
            if (txtLogs.IsDisposed) return;

            txtLogs.SelectionStart = txtLogs.TextLength;
            txtLogs.SelectionLength = 0;
            txtLogs.SelectionColor = color;
            txtLogs.AppendText(message + "\n");
            txtLogs.SelectionColor = txtLogs.ForeColor;
            txtLogs.ScrollToCaret();
        }

        private void ApplyDarkTheme()
        {
            this.BackColor = System.Drawing.Color.FromArgb(15, 23, 42);
            this.ForeColor = System.Drawing.Color.FromArgb(248, 250, 252);
        }

        /// <summary>
        /// 🚀 运行首 Tick 动量策略回测并生成全量 HTML 综合报告与交易明细
        /// </summary>
        private void RunFirstTickStrategyBacktest()
        {
            if (_currentBars.Count == 0)
            {
                MessageBox.Show("请先点击「▶ 开始回放」或生成 K 线数据后再执行策略回测！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string coin = cboCoin.SelectedItem?.ToString() ?? "BTCUSDT";
            SliceUnitType sliceUnit = (SliceUnitType)cboSliceUnit.SelectedIndex;
            decimal threshold = numThresholdValue.Value;
            decimal initialCapital = numInitialCapital.Value;
            decimal feeRate = numFeeRate.Value / 100.0m;
            bool compound = chkCompound.Checked;

            // 运行首 Tick 动量策略回测计算
            _lastBacktestReport = Test.PercentageBar.WinForms.Engine.FirstTickStrategyEngine.RunBacktest(
                _currentBars,
                coin: coin,
                thresholdValue: threshold,
                sliceUnit: sliceUnit,
                initialCapital: initialCapital,
                positionSizePct: 100m,
                feeRate: feeRate,
                compoundInterest: compound);

            // 1. 生成专业交互式 HTML 量化回测报告
            string htmlReportPath = _lastBacktestReport.GenerateHtmlReport();

            // 2. 输出控制台格式化结构报告卡片
            string textReport = _lastBacktestReport.GenerateTextReport();
            var reportColor = _lastBacktestReport.TotalNetProfit >= 0
                ? System.Drawing.Color.FromArgb(74, 222, 128)
                : System.Drawing.Color.FromArgb(244, 63, 94);

            _logQueue.Enqueue(("\n" + textReport, reportColor));
            _logQueue.Enqueue(($"🌐 交互式 HTML 回测报告已成功生成:\n   {htmlReportPath}\n", System.Drawing.Color.FromArgb(56, 189, 248)));

            // 3. 刷新 Tab 3 回测报告界面
            string retSign = _lastBacktestReport.TotalNetProfit >= 0 ? "+" : "";
            lblStrategyMetrics.Text = $"胜率: {_lastBacktestReport.WinRatePct:F2}% ({_lastBacktestReport.WinTrades}胜/{_lastBacktestReport.LossTrades}负) | 净收益: {retSign}{_lastBacktestReport.TotalNetProfit:N2} U ({retSign}{_lastBacktestReport.TotalReturnPct:F2}%) | 利润因子: {_lastBacktestReport.ProfitFactor:F2} | 最大回撤: -{_lastBacktestReport.MaxDrawdownPct:F2}% | 总交易: {_lastBacktestReport.TotalTrades:N0}笔";
            lblStrategyMetrics.ForeColor = reportColor;

            dgvTrades.Rows.Clear();
            dgvTrades.SuspendLayout();

            foreach (var trade in _lastBacktestReport.Trades)
            {
                bool isLong = trade.Side == Common.Models.TradeSide.Buy;
                bool isWin = trade.IsWin;
                string sideStr = isLong ? "🟢 开多 (Buy)" : "🔴 开空 (Sell)";
                string exitReasonStr = trade.ExitReason switch
                {
                    Common.Models.PositionExitReason.StopLoss => "🛑 阈值止损",
                    Common.Models.PositionExitReason.SignalReversal => trade.IsWin ? $"🔄 反向平仓 (+{trade.ReturnPct:F2}%)" : $"🔄 反向平仓 ({trade.ReturnPct:F2}%)",
                    _ => "⌛ 期末平仓"
                };

                int rowIdx = dgvTrades.Rows.Add(
                    trade.TradeId,
                    trade.BarRangeDesc,
                    sideStr,
                    trade.EntryDateTime.ToString("HH:mm:ss.fff"),
                    trade.EntryPrice.ToString("F2"),
                    trade.ExitDateTime.ToString("HH:mm:ss.fff"),
                    trade.ExitPrice.ToString("F2"),
                    exitReasonStr,
                    $"{trade.NetPnL:+0.00;-0.00;0.00}",
                    $"{trade.ReturnPct:+0.00;-0.00;0.00}%",
                    trade.AccountEquityAfter.ToString("N2")
                );

                dgvTrades.Rows[rowIdx].DefaultCellStyle.ForeColor = isWin
                    ? System.Drawing.Color.FromArgb(74, 222, 128)
                    : System.Drawing.Color.FromArgb(248, 113, 113);
            }

            dgvTrades.ResumeLayout();

            // 自动切换到策略报告 Tab
            tabTickViews.SelectedTab = tabStrategyReport;

            // 4. 刷新主图叠加标记
            RedrawCurrentPlot(autoScale: false, autoFollow: false);

            // 5. 自动在浏览器中打开 HTML 回测报告
            try
            {
                Process.Start(new ProcessStartInfo(htmlReportPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _logQueue.Enqueue(($"[提示] 自动打开浏览器报告失败: {ex.Message}，您可直接访问上述路径查看。", System.Drawing.Color.FromArgb(148, 163, 184)));
            }
        }

        /// <summary>
        /// 🌐 导出 / 另存 HTML 交互回测报告、CSV 交易流水或文本报告
        /// </summary>
        private void ExportBacktestReportToFile()
        {
            if (_lastBacktestReport == null || _lastBacktestReport.Trades.Count == 0)
            {
                MessageBox.Show("暂无回测报告数据，请先运行策略回测！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                using var sfd = new SaveFileDialog
                {
                    Title = "导出策略量化回测报告与交易明细",
                    Filter = "HTML 交互式图表报告 (*.html)|*.html|CSV 交易流水明细 (*.csv)|*.csv|文本分析报告 (*.txt)|*.txt",
                    FilterIndex = 1,
                    FileName = $"{_lastBacktestReport.Coin}_FirstTick_Backtest_{DateTime.Now:yyyyMMdd_HHmmss}.html"
                };

                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    if (sfd.FilterIndex == 1 || sfd.FileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                    {
                        _lastBacktestReport.GenerateHtmlReport(sfd.FileName);
                        if (MessageBox.Show($"HTML 回测报告已成功导出至:\n{sfd.FileName}\n\n是否立即在默认浏览器中打开？", "导出成功", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                        {
                            Process.Start(new ProcessStartInfo(sfd.FileName) { UseShellExecute = true });
                        }
                    }
                    else if (sfd.FilterIndex == 3 || sfd.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    {
                        File.WriteAllText(sfd.FileName, _lastBacktestReport.GenerateTextReport(), Encoding.UTF8);
                        MessageBox.Show($"文本报告已成功导出至:\n{sfd.FileName}", "导出成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine("TradeId,EntryBar,ExitBar,HoldingBars,Side,EntryTime,EntryPrice,ExitTime,ExitPrice,ExitReason,PositionValue,GrossPnL,Fee,NetPnL,ReturnPct,AccountEquity");
                        foreach (var t in _lastBacktestReport.Trades)
                        {
                            sb.AppendLine($"{t.TradeId},{t.EntryBarIndex},{t.ExitBarIndex},{t.HoldingBarsCount},{(t.Side == Common.Models.TradeSide.Buy ? "Buy" : "Sell")},{t.EntryDateTime:yyyy-MM-dd HH:mm:ss.fff},{t.EntryPrice:F2},{t.ExitDateTime:yyyy-MM-dd HH:mm:ss.fff},{t.ExitPrice:F2},{t.ExitReason},{t.PositionValue:F2},{t.GrossPnL:F2},{t.Fee:F4},{t.NetPnL:F2},{t.ReturnPct:F4},{t.AccountEquityAfter:F2}");
                        }
                        File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
                        MessageBox.Show($"CSV 交易流水已成功导出至:\n{sfd.FileName}", "导出成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private GroupBox CreateGroupBox(string text, int top, int height)
        {
            return new GroupBox
            {
                Text = text,
                Location = new Point(10, top),
                Width = 360,
                Height = height,
                ForeColor = System.Drawing.Color.FromArgb(56, 189, 248), // Sky Blue
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                BackColor = System.Drawing.Color.FromArgb(30, 41, 59)
            };
        }

        private Label CreateLabel(string text, int x, int y)
        {
            return new Label
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = true,
                ForeColor = System.Drawing.Color.FromArgb(226, 232, 240),
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Regular)
            };
        }

        private Label CreateStatLabel(string text, int top)
        {
            return new Label
            {
                Text = text,
                Location = new Point(15, top),
                Size = new Size(325, 24),
                ForeColor = System.Drawing.Color.FromArgb(241, 245, 249),
                Font = new Font("Microsoft YaHei", 8.5F)
            };
        }

        private static string FormatTimeSpan(TimeSpan ts)
        {
            if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}天{ts.Hours}时{ts.Minutes}分";
            if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}时{ts.Minutes}分{ts.Seconds}秒";
            if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}分{ts.Seconds}秒";
            if (ts.TotalSeconds >= 1) return $"{ts.Seconds}秒{ts.Milliseconds:D3}ms";
            return $"{ts.Milliseconds}ms";
        }
    }

    /// <summary>
    /// 扩展方法：安全在 UI 线程异步执行委托
    /// </summary>
    public static class ControlExtensions
    {
        public static Task InvokeAsync(this Control control, Action action)
        {
            if (control.IsDisposed || !control.IsHandleCreated) return Task.CompletedTask;
            if (!control.InvokeRequired)
            {
                action();
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource();
            control.BeginInvoke(() =>
            {
                try
                {
                    action();
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            return tcs.Task;
        }
    }
}
