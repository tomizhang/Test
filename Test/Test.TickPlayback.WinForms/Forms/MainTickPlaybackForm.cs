using Binance.Net.Enums;
using Common;
using Common.Helper;
using ScottPlot;
using ScottPlot.WinForms;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Color = System.Drawing.Color;
using Font = System.Drawing.Font;
using FontStyle = System.Drawing.FontStyle;
using Label = System.Windows.Forms.Label;
using ProgressBar = System.Windows.Forms.ProgressBar;
using Orientation = System.Windows.Forms.Orientation;
using Test.TickPlayback.WinForms.Engine;
using Test.TickPlayback.WinForms.Models;

namespace Test.TickPlayback.WinForms.Forms
{
    /// <summary>
    /// 流式队列加载逐笔 Tick 回放与实盘行情可视化主窗体 (左图右控、60FPS 流畅回放、实盘直连)
    /// </summary>
    public class MainTickPlaybackForm : Form
    {
        #region 核心引擎与数据缓冲

        private readonly TickPlaybackPipeline _pipeline;
        private readonly System.Windows.Forms.Timer _renderTimer;
        private readonly ConcurrentQueue<(string Message, System.Drawing.Color Color)> _logQueue = new();

        // 预分配零 GC 绘图数组 (双缓冲模式)
        private int _plotCapacity = 10000;
        private RawTick[] _tickSnapshotBuffer;
        private double[] _plotXs;
        private double[] _plotYs;
        private ScottPlot.Plottables.Scatter? _priceScatter;
        private ScottPlot.Plottables.BarPlot? _volBarPlot;
        private List<ScottPlot.Bar> _volBarsList;
        private ScottPlot.Plottables.Text? _textHighMarker;
        private ScottPlot.Plottables.Text? _textLowMarker;
        private ScottPlot.Plottables.Marker? _markerHigh;
        private ScottPlot.Plottables.Marker? _markerLow;

        private long _renderedTicksCount = 0;
        private int _fpsCounter = 0;
        private long _lastFpsTimestamp = 0;
        private double _currentRenderFps = 60.0;

        #endregion

        #region UI 布局与控件声明

        private SplitContainer splitMain = null!;
        private SplitContainer splitLeft = null!;
        private FormsPlot formsPlotTick = null!;
        private TabControl tabBottom = null!;
        private TabPage tabGrid = null!;
        private TabPage tabLogs = null!;
        private DataGridView dgvTicks = null!;
        private RichTextBox txtLogs = null!;

        private Panel panelRight = null!;

        // Group 1: 数据源与运行模式
        private GroupBox grpMode = null!;
        private RadioButton rdoHistorical = null!;
        private RadioButton rdoLive = null!;
        private ComboBox cboMarket = null!;
        private ComboBox cboCoin = null!;
        private Label lblDateRange = null!;
        private DateTimePicker dtpStart = null!;
        private DateTimePicker dtpEnd = null!;

        // Group 2: 回放与连接控制
        private GroupBox grpControl = null!;
        private Button btnStart = null!;
        private Button btnPause = null!;
        private Button btnStop = null!;
        private Button btnStep = null!;
        private Button btnResetAxes = null!;
        private TrackBar tbSpeed = null!;
        private Label lblSpeedVal = null!;
        private FlowLayoutPanel flowSpeedPresets = null!;

        // Group 3: 队列与性能监控
        private GroupBox grpQueue = null!;
        private Label lblQueueStatus = null!;
        private ProgressBar pbQueue = null!;
        private Label lblQueueCount = null!;
        private Label lblPlayedCount = null!;
        private Label lblTpsFps = null!;
        private Label lblCurrentTime = null!;

        // Group 4: 微观买卖盘统计
        private GroupBox grpMetrics = null!;
        private Label lblPrice = null!;
        private Label lblHighLow = null!;
        private ProgressBar pbBuyRatio = null!;
        private Label lblBuySellVol = null!;
        private Label lblVwap = null!;

        // Group 5: 显示偏好与指标开关
        private GroupBox grpDisplay = null!;
        private ComboBox cboChartStyle = null!;
        private CheckBox chkAutoFollow = null!;
        private CheckBox chkShowVolume = null!;
        private CheckBox chkShowHighLowBadges = null!;
        private NumericUpDown numWindowCapacity = null!;

        #endregion

        public MainTickPlaybackForm()
        {
            _tickSnapshotBuffer = new RawTick[_plotCapacity];
            _plotXs = new double[_plotCapacity];
            _plotYs = new double[_plotCapacity];
            _volBarsList = new List<ScottPlot.Bar>(250);

            _pipeline = new TickPlaybackPipeline(_plotCapacity);

            InitializeComponent();
            ApplyDarkTheme();
            InitializeScottPlot();

            // 60 FPS UI 极速批量刷新定时器 (~16ms)
            _renderTimer = new System.Windows.Forms.Timer
            {
                Interval = 16
            };
            _renderTimer.Tick += OnRenderTimerTick;
            _renderTimer.Start();

            BindPipelineEvents();
        }

        #region 窗体与控件初始化布局

        private void InitializeComponent()
        {
            this.Text = "🚀 Tick 流式队列回放与实盘可视化工作台 (Test.TickPlayback)";
            this.Size = new Size(1600, 950);
            this.MinimumSize = new Size(1200, 750);
            this.StartPosition = FormStartPosition.CenterScreen;

            // 主分割容器 (左右分割: 左侧图表 72%, 右侧控制面板 28%)
            splitMain = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 6,
                BackColor = System.Drawing.Color.FromArgb(30, 41, 59)
            };
            this.Controls.Add(splitMain);

            BuildLeftChartPanel();
            BuildRightControlPanel();

            splitMain.SplitterDistance = (int)(this.ClientSize.Width * 0.72);
        }

        private void BuildLeftChartPanel()
        {
            // 左侧纵向分割容器 (上部图表 68%, 下部表格与日志 32%)
            splitLeft = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = 6,
                BackColor = System.Drawing.Color.FromArgb(30, 41, 59)
            };
            splitMain.Panel1.Controls.Add(splitLeft);

            // 1. 上半部：ScottPlot 逐笔 Tick 曲线图表
            formsPlotTick = new FormsPlot
            {
                Dock = DockStyle.Fill,
                BackColor = System.Drawing.Color.FromArgb(15, 23, 42)
            };
            splitLeft.Panel1.Controls.Add(formsPlotTick);

            // 2. 下半部：Tab 选项卡 (逐笔流水表格 vs 运行日志)
            tabBottom = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular)
            };

            tabGrid = new TabPage("📋 逐笔 Tick 流水明细 (VirtualMode 60fps)");
            tabGrid.BackColor = System.Drawing.Color.FromArgb(15, 23, 42);
            {
                dgvTicks = new DataGridView
                {
                    Dock = DockStyle.Fill,
                    VirtualMode = true,
                    ReadOnly = true,
                    AllowUserToAddRows = false,
                    AllowUserToDeleteRows = false,
                    AllowUserToResizeRows = false,
                    RowHeadersVisible = false,
                    SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                    BackgroundColor = System.Drawing.Color.FromArgb(15, 23, 42),
                    ForeColor = System.Drawing.Color.FromArgb(241, 245, 249),
                    BorderStyle = BorderStyle.None,
                    ColumnHeadersHeight = 28,
                    RowTemplate = { Height = 22 },
                    Font = new Font("Consolas", 9F)
                };

                // 双缓冲优化
                typeof(DataGridView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.SetValue(dgvTicks, true, null);

                dgvTicks.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "# 序号", Width = 70 });
                dgvTicks.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "成交时间 (Time)", Width = 140 });
                dgvTicks.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "成交价格 (USDT)", Width = 110 });
                dgvTicks.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "成交数量 (Qty)", Width = 95 });
                dgvTicks.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "成交金额 (Quote)", Width = 110 });
                dgvTicks.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "主动买卖 (Taker)", Width = 110 });
                dgvTicks.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "相比首笔涨跌", Width = 100 });

                dgvTicks.CellValueNeeded += OnDgvTicksCellValueNeeded;
                dgvTicks.CellFormatting += OnDgvTicksCellFormatting;

                tabGrid.Controls.Add(dgvTicks);
            }
            tabBottom.TabPages.Add(tabGrid);

            tabLogs = new TabPage("⚡ 引擎运行与网络日志");
            tabLogs.BackColor = System.Drawing.Color.FromArgb(15, 23, 42);
            {
                txtLogs = new RichTextBox
                {
                    Dock = DockStyle.Fill,
                    ReadOnly = true,
                    BackColor = System.Drawing.Color.FromArgb(15, 23, 42),
                    ForeColor = System.Drawing.Color.FromArgb(241, 245, 249),
                    BorderStyle = BorderStyle.None,
                    Font = new Font("Consolas", 9F)
                };
                tabLogs.Controls.Add(txtLogs);
            }
            tabBottom.TabPages.Add(tabLogs);

            splitLeft.Panel2.Controls.Add(tabBottom);
            splitLeft.SplitterDistance = (int)(this.ClientSize.Height * 0.65);
        }

        private void BuildRightControlPanel()
        {
            panelRight = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = System.Drawing.Color.FromArgb(15, 23, 42),
                Padding = new Padding(8)
            };
            splitMain.Panel2.Controls.Add(panelRight);

            int top = 8;

            // -------------------------------------------------------------
            // Group 1: 运行模式与数据源 (Mode & Source)
            // -------------------------------------------------------------
            grpMode = CreateGroupBox("1. 运行模式与数据源", top, 195);
            {
                rdoHistorical = new RadioButton
                {
                    Text = "📁 历史 Parquet 队列流式回放",
                    Location = new Point(15, 25),
                    Size = new Size(250, 22),
                    Checked = true,
                    ForeColor = System.Drawing.Color.FromArgb(56, 189, 248),
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold)
                };
                rdoHistorical.CheckedChanged += OnModeChanged;

                rdoLive = new RadioButton
                {
                    Text = "⚡ 币安实盘 WebSocket 实时直连",
                    Location = new Point(15, 48),
                    Size = new Size(250, 22),
                    ForeColor = System.Drawing.Color.FromArgb(74, 222, 128),
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold)
                };
                rdoLive.CheckedChanged += OnModeChanged;

                var lblMarket = CreateLabel("市场类别:", 15, 76);
                cboMarket = new ComboBox
                {
                    Location = new Point(90, 73),
                    Width = 230,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = System.Drawing.Color.FromArgb(30, 41, 59),
                    ForeColor = System.Drawing.Color.White
                };
                cboMarket.Items.AddRange(new object[] { "🪙 现货市场 (Spot)", "📈 USDT-M 永续合约 (Futures)" });
                cboMarket.SelectedIndex = 0;

                var lblCoin = CreateLabel("交易对:", 15, 106);
                cboCoin = new ComboBox
                {
                    Location = new Point(90, 103),
                    Width = 230,
                    BackColor = System.Drawing.Color.FromArgb(30, 41, 59),
                    ForeColor = System.Drawing.Color.FromArgb(250, 204, 21),
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold)
                };
                cboCoin.Items.AddRange(new object[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "DOGEUSDT", "BNBUSDT", "XRPUSDT" });
                cboCoin.SelectedIndex = 0;

                lblDateRange = CreateLabel("历史日期:", 15, 136);
                dtpStart = new DateTimePicker { Location = new Point(90, 133), Width = 110, Format = DateTimePickerFormat.Short, Value = new DateTime(2025, 1, 1) };
                dtpEnd = new DateTimePicker { Location = new Point(210, 133), Width = 110, Format = DateTimePickerFormat.Short, Value = new DateTime(2025, 1, 3) };

                grpMode.Controls.AddRange(new Control[] { rdoHistorical, rdoLive, lblMarket, cboMarket, lblCoin, cboCoin, lblDateRange, dtpStart, dtpEnd });
            }
            panelRight.Controls.Add(grpMode);
            top += grpMode.Height + 10;

            // -------------------------------------------------------------
            // Group 2: 回放与直连控制 (Playback & Control)
            // -------------------------------------------------------------
            grpControl = CreateGroupBox("2. 回放与直连控制", top, 200);
            {
                btnStart = new Button
                {
                    Text = "▶ 开始回放",
                    Location = new Point(15, 25),
                    Size = new Size(95, 34),
                    BackColor = System.Drawing.Color.FromArgb(5, 150, 105), // Green 600
                    ForeColor = System.Drawing.Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Microsoft YaHei", 9.5F, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnStart.FlatAppearance.BorderSize = 0;
                btnStart.Click += async (s, e) => await OnStartClickedAsync();

                btnPause = new Button
                {
                    Text = "⏸ 暂停",
                    Location = new Point(116, 25),
                    Size = new Size(68, 34),
                    BackColor = System.Drawing.Color.FromArgb(217, 119, 6), // Amber 600
                    ForeColor = System.Drawing.Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                    Enabled = false,
                    Cursor = Cursors.Hand
                };
                btnPause.FlatAppearance.BorderSize = 0;
                btnPause.Click += (s, e) => TogglePause();

                btnStep = new Button
                {
                    Text = "⏭ +100",
                    Location = new Point(190, 25),
                    Size = new Size(65, 34),
                    BackColor = System.Drawing.Color.FromArgb(79, 70, 229), // Indigo 600
                    ForeColor = System.Drawing.Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold),
                    Enabled = false,
                    Cursor = Cursors.Hand
                };
                btnStep.FlatAppearance.BorderSize = 0;
                btnStep.Click += (s, e) => _pipeline.Step(100);

                btnStop = new Button
                {
                    Text = "⏹ 停止",
                    Location = new Point(261, 25),
                    Size = new Size(60, 34),
                    BackColor = System.Drawing.Color.FromArgb(220, 38, 38), // Red 600
                    ForeColor = System.Drawing.Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                    Enabled = false,
                    Cursor = Cursors.Hand
                };
                btnStop.FlatAppearance.BorderSize = 0;
                btnStop.Click += async (s, e) => await _pipeline.StopAsync();

                var lblSpeed = CreateLabel("回放速率倍数:", 15, 68);
                lblSpeedVal = new Label
                {
                    Text = "10x",
                    Location = new Point(270, 68),
                    Size = new Size(60, 18),
                    TextAlign = ContentAlignment.MiddleRight,
                    ForeColor = System.Drawing.Color.FromArgb(250, 204, 21),
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold)
                };

                tbSpeed = new TrackBar
                {
                    Location = new Point(15, 90),
                    Width = 308,
                    Minimum = 1,
                    Maximum = 6,
                    Value = 3,
                    TickStyle = TickStyle.BottomRight
                };
                tbSpeed.ValueChanged += OnSpeedTrackBarChanged;

                flowSpeedPresets = new FlowLayoutPanel
                {
                    Location = new Point(15, 130),
                    Size = new Size(308, 30),
                    BackColor = System.Drawing.Color.Transparent
                };
                BuildSpeedButtons();

                btnResetAxes = new Button
                {
                    Text = "🔍 复位坐标轴",
                    Location = new Point(15, 162),
                    Size = new Size(308, 26),
                    BackColor = System.Drawing.Color.FromArgb(14, 116, 144),
                    ForeColor = System.Drawing.Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand
                };
                btnResetAxes.FlatAppearance.BorderSize = 0;
                btnResetAxes.Click += (s, e) =>
                {
                    formsPlotTick.Plot.Axes.Margins(0.02, 0.15);
                    formsPlotTick.Plot.Axes.AutoScale();
                    formsPlotTick.Refresh();
                };

                grpControl.Controls.AddRange(new Control[] { btnStart, btnPause, btnStep, btnStop, lblSpeed, lblSpeedVal, tbSpeed, flowSpeedPresets, btnResetAxes });
            }
            panelRight.Controls.Add(grpControl);
            top += grpControl.Height + 10;

            // -------------------------------------------------------------
            // Group 3: 队列与性能监控 (Queue & Diagnostics)
            // -------------------------------------------------------------
            grpQueue = CreateGroupBox("3. 队列流式缓冲监控", top, 165);
            {
                lblQueueStatus = CreateStatLabel("生产者状态: 待机", 15, 24);
                lblQueueStatus.ForeColor = System.Drawing.Color.FromArgb(56, 189, 248);

                pbQueue = new ProgressBar
                {
                    Location = new Point(15, 46),
                    Size = new Size(305, 14),
                    Minimum = 0,
                    Maximum = 200000,
                    Value = 0
                };

                lblQueueCount = CreateStatLabel("队列缓冲积压: 0 / 200,000", 15, 66);
                lblPlayedCount = CreateStatLabel("已回放处理总量: 0 笔", 15, 88);
                lblTpsFps = CreateStatLabel("吞吐速率: 0 ticks/s | 渲染: 60 FPS", 15, 110);
                lblTpsFps.ForeColor = System.Drawing.Color.FromArgb(74, 222, 128);

                lblCurrentTime = CreateStatLabel("当前成交时间: -", 15, 132);
                lblCurrentTime.ForeColor = System.Drawing.Color.FromArgb(250, 204, 21);

                grpQueue.Controls.AddRange(new Control[] { lblQueueStatus, pbQueue, lblQueueCount, lblPlayedCount, lblTpsFps, lblCurrentTime });
            }
            panelRight.Controls.Add(grpQueue);
            top += grpQueue.Height + 10;

            // -------------------------------------------------------------
            // Group 4: 微观买卖盘统计 (Micro Metrics)
            // -------------------------------------------------------------
            grpMetrics = CreateGroupBox("4. 微观量价指标看板", top, 155);
            {
                lblPrice = CreateStatLabel("当前价格: - (涨跌: 0.00%)", 15, 24);
                lblPrice.Font = new Font("Microsoft YaHei", 9.5F, FontStyle.Bold);
                lblPrice.ForeColor = System.Drawing.Color.FromArgb(56, 189, 248);

                lblHighLow = CreateStatLabel("极值范围: 最高 - | 最低 -", 15, 48);

                var lblRatioTitle = CreateStatLabel("主动买入占比 (Taker Buy %):", 15, 70);
                pbBuyRatio = new ProgressBar
                {
                    Location = new Point(15, 92),
                    Size = new Size(305, 14),
                    Minimum = 0,
                    Maximum = 100,
                    Value = 50
                };

                lblBuySellVol = CreateStatLabel("买入量: 0.00 | 卖出量: 0.00", 15, 110);
                lblVwap = CreateStatLabel("成交均价 VWAP: -", 15, 130);
                lblVwap.ForeColor = System.Drawing.Color.FromArgb(232, 121, 249);

                grpMetrics.Controls.AddRange(new Control[] { lblPrice, lblHighLow, lblRatioTitle, pbBuyRatio, lblBuySellVol, lblVwap });
            }
            panelRight.Controls.Add(grpMetrics);
            top += grpMetrics.Height + 10;

            // -------------------------------------------------------------
            // Group 5: 显示偏好与指标开关 (Display & Indicators)
            // -------------------------------------------------------------
            grpDisplay = CreateGroupBox("5. 图表视图与指标开关", top, 160);
            {
                var lblStyle = CreateLabel("走势线型:", 15, 25);
                cboChartStyle = new ComboBox
                {
                    Location = new Point(90, 22),
                    Width = 230,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = System.Drawing.Color.FromArgb(30, 41, 59),
                    ForeColor = System.Drawing.Color.White
                };
                cboChartStyle.Items.AddRange(new object[] { "📈 平滑价格折线 (Line)", "📊 阶梯台阶走势 (Step Line)" });
                cboChartStyle.SelectedIndex = 0;
                cboChartStyle.SelectedIndexChanged += (s, e) => RebuildPlotSeries();

                chkAutoFollow = new CheckBox
                {
                    Text = "🎯 自动平滑跟随最新 Tick (Auto-Follow)",
                    Location = new Point(15, 54),
                    Size = new Size(300, 20),
                    Checked = true,
                    ForeColor = System.Drawing.Color.FromArgb(74, 222, 128),
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold)
                };

                chkShowVolume = new CheckBox
                {
                    Text = "📊 显示底部买卖量柱 (Taker Volume Bars)",
                    Location = new Point(15, 78),
                    Size = new Size(300, 20),
                    Checked = true
                };
                chkShowVolume.CheckedChanged += (s, e) => RebuildPlotSeries();

                chkShowHighLowBadges = new CheckBox
                {
                    Text = "🏷️ 标注视口最高/最低价标签 (▲/▼)",
                    Location = new Point(15, 102),
                    Size = new Size(300, 20),
                    Checked = true
                };

                var lblCap = CreateLabel("窗口容量:", 15, 128);
                numWindowCapacity = new NumericUpDown
                {
                    Location = new Point(90, 125),
                    Width = 100,
                    Minimum = 500,
                    Maximum = 50000,
                    Increment = 1000,
                    Value = 10000,
                    BackColor = System.Drawing.Color.FromArgb(30, 41, 59),
                    ForeColor = System.Drawing.Color.White
                };
                numWindowCapacity.ValueChanged += OnWindowCapacityChanged;

                var lblCapTip = new Label
                {
                    Text = "根Tick",
                    Location = new Point(195, 128),
                    AutoSize = true,
                    ForeColor = System.Drawing.Color.FromArgb(148, 163, 184)
                };

                grpDisplay.Controls.AddRange(new Control[] { lblStyle, cboChartStyle, chkAutoFollow, chkShowVolume, chkShowHighLowBadges, lblCap, numWindowCapacity, lblCapTip });
            }
            panelRight.Controls.Add(grpDisplay);
        }

        private void BuildSpeedButtons()
        {
            double[] speeds = new double[] { 1, 5, 20, 50, 100, 500, 5000 };
            string[] labels = new string[] { "1x", "5x", "20x", "50x", "100x", "500x", "极速" };

            for (int i = 0; i < speeds.Length; i++)
            {
                double sp = speeds[i];
                var btn = new Button
                {
                    Text = labels[i],
                    Size = new Size(40, 24),
                    Margin = new Padding(2),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = System.Drawing.Color.FromArgb(51, 65, 85),
                    ForeColor = System.Drawing.Color.White,
                    Font = new Font("Microsoft YaHei", 7.5F),
                    Cursor = Cursors.Hand
                };
                btn.FlatAppearance.BorderSize = 0;
                btn.Click += (s, e) =>
                {
                    _pipeline.SetSpeed(sp);
                    lblSpeedVal.Text = sp >= 5000 ? "极速 (Max)" : $"{sp:0.#}x";
                };
                flowSpeedPresets.Controls.Add(btn);
            }
        }

        private void ApplyDarkTheme()
        {
            this.BackColor = System.Drawing.Color.FromArgb(15, 23, 42); // Slate 900
            this.ForeColor = System.Drawing.Color.FromArgb(248, 250, 252);
        }

        private GroupBox CreateGroupBox(string title, int top, int height)
        {
            return new GroupBox
            {
                Text = title,
                Location = new Point(8, top),
                Size = new Size(335, height),
                ForeColor = System.Drawing.Color.FromArgb(226, 232, 240),
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold)
            };
        }

        private Label CreateLabel(string text, int x, int y)
        {
            return new Label
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = true,
                ForeColor = System.Drawing.Color.FromArgb(203, 213, 225),
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular)
            };
        }

        private Label CreateStatLabel(string text, int x, int y)
        {
            return new Label
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(305, 18),
                ForeColor = System.Drawing.Color.FromArgb(226, 232, 240),
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Regular)
            };
        }

        #endregion

        #region ScottPlot 图表初始化与重构

        private void InitializeScottPlot()
        {
            string chineseFont = PlotHelper.GetInstalledChineseFont();
            Fonts.Default = chineseFont;

            formsPlotTick.Plot.Clear();
            formsPlotTick.Plot.FigureBackground.Color = ScottPlot.Color.FromHex("#0f172a");
            formsPlotTick.Plot.DataBackground.Color = ScottPlot.Color.FromHex("#1e293b");
            formsPlotTick.Plot.Axes.Color(ScottPlot.Color.FromHex("#94a3b8"));
            formsPlotTick.Plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#334155");

            formsPlotTick.Plot.Title("等待回放启动，点击【▶ 开始】或【⚡ 连接实盘】进入流式 Tick 视口...", size: 14);
            formsPlotTick.Plot.Axes.Title.Label.FontName = chineseFont;
            formsPlotTick.Plot.Axes.Title.Label.ForeColor = ScottPlot.Color.FromHex("#f8fafc");

            formsPlotTick.Plot.Axes.Bottom.Label.Text = "逐笔 Tick 推进序列号 (Sequence)";
            formsPlotTick.Plot.Axes.Bottom.Label.FontName = chineseFont;
            formsPlotTick.Plot.Axes.Bottom.Label.ForeColor = ScottPlot.Color.FromHex("#cbd5e1");

            formsPlotTick.Plot.Axes.Left.Label.Text = "价格 (USDT)";
            formsPlotTick.Plot.Axes.Left.Label.FontName = chineseFont;
            formsPlotTick.Plot.Axes.Left.Label.ForeColor = ScottPlot.Color.FromHex("#cbd5e1");

            RebuildPlotSeries();
            formsPlotTick.Refresh();
        }

        private void RebuildPlotSeries()
        {
            formsPlotTick.Plot.Clear();

            string chineseFont = PlotHelper.GetInstalledChineseFont();
            formsPlotTick.Plot.FigureBackground.Color = ScottPlot.Color.FromHex("#0f172a");
            formsPlotTick.Plot.DataBackground.Color = ScottPlot.Color.FromHex("#1e293b");
            formsPlotTick.Plot.Axes.Color(ScottPlot.Color.FromHex("#94a3b8"));
            formsPlotTick.Plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#334155");

            // 1. 成交量柱状图 (挂在右 Y 轴)
            if (chkShowVolume.Checked)
            {
                _volBarsList.Clear();
                _volBarPlot = formsPlotTick.Plot.Add.Bars(_volBarsList);
                _volBarPlot.Axes.YAxis = formsPlotTick.Plot.Axes.Right;
                formsPlotTick.Plot.Axes.Right.TickLabelStyle.ForeColor = ScottPlot.Color.FromHex("#64748b");
                formsPlotTick.Plot.Axes.Right.FrameLineStyle.Color = ScottPlot.Color.FromHex("#334155");
            }
            else
            {
                _volBarPlot = null;
            }

            // 2. 主价格折线
            _priceScatter = formsPlotTick.Plot.Add.Scatter(_plotXs, _plotYs);
            _priceScatter.LineWidth = 1.2f;
            _priceScatter.MarkerSize = 0;
            _priceScatter.Color = ScottPlot.Color.FromHex("#38bdf8"); // Sky Blue 400

            if (cboChartStyle.SelectedIndex == 1)
            {
                _priceScatter.ConnectStyle = ConnectStyle.StepHorizontal; // 阶梯线
            }
            else
            {
                _priceScatter.ConnectStyle = ConnectStyle.Straight; // 直线
            }

            // 3. 最高/最低标记
            _markerHigh = formsPlotTick.Plot.Add.Marker(0, 0);
            _markerHigh.Shape = MarkerShape.FilledTriangleUp;
            _markerHigh.Size = 8;
            _markerHigh.Color = ScottPlot.Color.FromHex("#22c55e");
            _markerHigh.IsVisible = false;

            _textHighMarker = formsPlotTick.Plot.Add.Text("▲ 最高", 0, 0);
            _textHighMarker.LabelFontName = chineseFont;
            _textHighMarker.LabelFontSize = 9.0f;
            _textHighMarker.LabelFontColor = ScottPlot.Color.FromHex("#86efac");
            _textHighMarker.LabelBackgroundColor = ScottPlot.Color.FromHex("#0f172a").WithAlpha(0.9);
            _textHighMarker.LabelBorderColor = ScottPlot.Color.FromHex("#22c55e");
            _textHighMarker.LabelBorderWidth = 1f;
            _textHighMarker.IsVisible = false;

            _markerLow = formsPlotTick.Plot.Add.Marker(0, 0);
            _markerLow.Shape = MarkerShape.FilledTriangleDown;
            _markerLow.Size = 8;
            _markerLow.Color = ScottPlot.Color.FromHex("#ef4444");
            _markerLow.IsVisible = false;

            _textLowMarker = formsPlotTick.Plot.Add.Text("▼ 最低", 0, 0);
            _textLowMarker.LabelFontName = chineseFont;
            _textLowMarker.LabelFontSize = 9.0f;
            _textLowMarker.LabelFontColor = ScottPlot.Color.FromHex("#fca5a5");
            _textLowMarker.LabelBackgroundColor = ScottPlot.Color.FromHex("#0f172a").WithAlpha(0.9);
            _textLowMarker.LabelBorderColor = ScottPlot.Color.FromHex("#ef4444");
            _textLowMarker.LabelBorderWidth = 1f;
            _textLowMarker.IsVisible = false;

            formsPlotTick.Refresh();
        }

        #endregion

        #region 60 FPS 批量渲染循环 (Render Timer Tick)

        private void OnRenderTimerTick(object? sender, EventArgs e)
        {
            // 1. 处理日志队列
            while (_logQueue.TryDequeue(out var logItem))
            {
                AppendLog(logItem.Message, logItem.Color);
            }

            // 2. FPS 计算
            _fpsCounter++;
            long now = Stopwatch.GetTimestamp();
            double elapsedFpsSec = (now - _lastFpsTimestamp) / (double)Stopwatch.Frequency;
            if (elapsedFpsSec >= 0.5)
            {
                _currentRenderFps = _fpsCounter / elapsedFpsSec;
                _fpsCounter = 0;
                _lastFpsTimestamp = now;
            }

            // 3. 更新右侧监控看板
            var stats = _pipeline.GetDashboardStats(_currentRenderFps);
            UpdateDashboardUI(stats);

            // 4. 从 RingBuffer 零堆内存复制当前视口数据
            int count = _pipeline.RingBuffer.CopyTo(_tickSnapshotBuffer);
            if (count == 0) return;

            long totalPlayed = _pipeline.RingBuffer.TotalPushed;
            long startSeq = Math.Max(1, totalPlayed - count + 1);

            double minPrice = double.MaxValue;
            double maxPrice = double.MinValue;
            int minIdx = 0;
            int maxIdx = 0;
            double maxVol = 0;

            for (int i = 0; i < count; i++)
            {
                double p = (double)_tickSnapshotBuffer[i].Price;
                _plotXs[i] = startSeq + i;
                _plotYs[i] = p;

                if (p > maxPrice) { maxPrice = p; maxIdx = i; }
                if (p < minPrice) { minPrice = p; minIdx = i; }

                double q = (double)_tickSnapshotBuffer[i].Qty;
                if (q > maxVol) maxVol = q;
            }

            // 更新 DataGridView 行数 (仅在数量增长时刷新)
            if (count != _renderedTicksCount || totalPlayed != dgvTicks.RowCount)
            {
                _renderedTicksCount = count;
                dgvTicks.RowCount = count;
                if (chkAutoFollow.Checked && dgvTicks.RowCount > 0)
                {
                    dgvTicks.FirstDisplayedScrollingRowIndex = Math.Max(0, dgvTicks.RowCount - 1);
                }
            }

            // 5. 更新 ScottPlot 图元
            if (_priceScatter != null)
            {
                formsPlotTick.Plot.PlottableList.Remove(_priceScatter);
                _priceScatter = formsPlotTick.Plot.Add.Scatter(_plotXs.AsSpan(0, count).ToArray(), _plotYs.AsSpan(0, count).ToArray());
                _priceScatter.LineWidth = 1.2f;
                _priceScatter.MarkerSize = 0;
                _priceScatter.Color = ScottPlot.Color.FromHex("#38bdf8");
                if (cboChartStyle.SelectedIndex == 1)
                {
                    _priceScatter.ConnectStyle = ConnectStyle.StepHorizontal;
                }
                else
                {
                    _priceScatter.ConnectStyle = ConnectStyle.Straight;
                }
            }

            // 成交量柱状图聚合更新
            if (chkShowVolume.Checked && _volBarPlot != null)
            {
                UpdateVolumeBars(count, startSeq, maxVol);
            }

            // 最高最低价标记
            if (chkShowHighLowBadges.Checked && _textHighMarker != null && _textLowMarker != null && _markerHigh != null && _markerLow != null && count > 1)
            {
                double xMax = startSeq + maxIdx;
                double xMin = startSeq + minIdx;
                double priceRange = maxPrice - minPrice;
                double offset = priceRange > 0 ? priceRange * 0.04 : maxPrice * 0.001;

                _markerHigh.Location = new Coordinates(xMax, maxPrice);
                _markerHigh.IsVisible = true;
                _textHighMarker.Location = new Coordinates(xMax, maxPrice + offset);
                _textHighMarker.LabelText = $"▲ 最高 {maxPrice:F2}";
                _textHighMarker.IsVisible = true;

                _markerLow.Location = new Coordinates(xMin, minPrice);
                _markerLow.IsVisible = true;
                _textLowMarker.Location = new Coordinates(xMin, minPrice - offset);
                _textLowMarker.LabelText = $"▼ 最低 {minPrice:F2}";
                _textLowMarker.IsVisible = true;
            }
            else
            {
                if (_textHighMarker != null) _textHighMarker.IsVisible = false;
                if (_textLowMarker != null) _textLowMarker.IsVisible = false;
                if (_markerHigh != null) _markerHigh.IsVisible = false;
                if (_markerLow != null) _markerLow.IsVisible = false;
            }

            // 自动跟随最新点位
            if (chkAutoFollow.Checked)
            {
                double xRight = startSeq + count;
                double xLeft = Math.Max(1, xRight - count);
                double yMargin = (maxPrice - minPrice) * 0.12;
                if (yMargin <= 0) yMargin = maxPrice * 0.002;

                formsPlotTick.Plot.Axes.SetLimitsX(xLeft, xRight + count * 0.05);
                formsPlotTick.Plot.Axes.SetLimitsY(minPrice - yMargin, maxPrice + yMargin);
            }

            formsPlotTick.Refresh();
        }

        private void UpdateVolumeBars(int count, long startSeq, double maxVol)
        {
            _volBarsList.Clear();

            // 严格 1:1 对应每一个 Tick，X 坐标完全与价格点对齐 (startSeq + idx)
            // 彻底去除动态分组 binning，确保量柱稳定与折线点位严格对应，杜绝画面跳变晃动
            int maxRenderCount = Math.Min(count, 3000);
            int offset = count - maxRenderCount;

            for (int i = 0; i < maxRenderCount; i++)
            {
                int idx = offset + i;
                var t = _tickSnapshotBuffer[idx];
                var col = !t.IsBuyerMaker
                    ? ScottPlot.Color.FromHex("#22c55e").WithAlpha(0.45) // 买方主动 (买入绿色)
                    : ScottPlot.Color.FromHex("#ef4444").WithAlpha(0.45); // 卖方主动 (卖出红色)

                _volBarsList.Add(new ScottPlot.Bar
                {
                    Position = startSeq + idx, // 与 _plotXs[idx] 完全一致，上下严格对齐
                    Value = (double)t.Qty,
                    ValueBase = 0,
                    Size = 0.8,
                    FillColor = col,
                    LineWidth = 0
                });
            }

            if (_volBarPlot != null && maxVol > 0)
            {
                // 量柱高度控制在图表底部约 22% 区域，顶部 78% 留给价格走势线
                formsPlotTick.Plot.Axes.SetLimitsY(0, maxVol * 4.5, formsPlotTick.Plot.Axes.Right);
            }
        }

        private void UpdateDashboardUI(PlaybackDashboardStats stats)
        {
            lblQueueStatus.Text = stats.Mode == DataSourceMode.LiveWebSocket
                ? $"实盘状态: {(stats.Status == PlaybackStatus.LiveConnected ? "🟢 已直连实时行情" : "🟡 连接中...")}"
                : $"生产者状态: {stats.ProducerStatusMessage} ({stats.ProducerDaysLoaded}天)";

            pbQueue.Maximum = stats.MaxQueueCapacity;
            pbQueue.Value = Math.Clamp(stats.QueueBufferCount, 0, stats.MaxQueueCapacity);
            lblQueueCount.Text = $"队列缓冲积压: {stats.QueueBufferCount:N0} / {stats.MaxQueueCapacity:N0}";

            lblPlayedCount.Text = stats.Mode == DataSourceMode.LiveWebSocket
                ? $"实盘已接收处理: {stats.TotalTicksPlayed:N0} 笔"
                : $"已回放处理总量: {stats.TotalTicksPlayed:N0} / {stats.TotalTicksLoaded:N0}";

            lblTpsFps.Text = $"吞吐: {stats.PlaybackThroughputTps:N0} ticks/s | 渲染: {stats.RenderFps:F0} FPS";

            if (stats.CurrentTickTimeMs > 0)
            {
                DateTime dt = TimeHelper.FromUnixTimeMilliseconds(stats.CurrentTickTimeMs).ToLocalTime();
                lblCurrentTime.Text = $"成交时间: {dt:yyyy-MM-dd HH:mm:ss.fff}";
            }

            // 价格与指标
            if (stats.CurrentPrice > 0)
            {
                string sign = stats.PriceChangePct >= 0 ? "+" : "";
                lblPrice.Text = $"当前价格: {stats.CurrentPrice:F2} ({sign}{stats.PriceChangePct:F2}%)";
                lblPrice.ForeColor = stats.PriceChangePct >= 0 ? System.Drawing.Color.FromArgb(74, 222, 128) : System.Drawing.Color.FromArgb(248, 113, 113);
            }

            lblHighLow.Text = $"极值范围: 最高 {stats.HighPrice:F2} | 最低 {stats.LowPrice:F2}";
            pbBuyRatio.Value = Math.Clamp((int)stats.BuyVolumeRatioPct, 0, 100);
            lblBuySellVol.Text = $"买入: {stats.TotalBuyVolume:N2} | 卖出: {stats.TotalSellVolume:N2} ({stats.BuyVolumeRatioPct:F1}%)";
            lblVwap.Text = $"成交均价 VWAP: {stats.VWAP:F2}";
        }

        #endregion

        #region DataGridView VirtualMode 虚拟数据加载

        private void OnDgvTicksCellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _renderedTicksCount) return;

            if (_pipeline.RingBuffer.TryGetAt(e.RowIndex, out var tick))
            {
                long startSeq = Math.Max(1, _pipeline.RingBuffer.TotalPushed - _renderedTicksCount + 1);
                decimal firstPrice = _plotYs.Length > 0 ? (decimal)_plotYs[0] : tick.Price;
                decimal diff = firstPrice > 0 ? (tick.Price - firstPrice) / firstPrice * 100m : 0m;

                e.Value = e.ColumnIndex switch
                {
                    0 => startSeq + e.RowIndex,
                    1 => TimeHelper.FromUnixTimeMilliseconds(tick.Time).ToLocalTime().ToString("HH:mm:ss.fff"),
                    2 => tick.Price.ToString("F2"),
                    3 => tick.Qty.ToString("F4"),
                    4 => tick.QuoteQty.ToString("F2"),
                    5 => !tick.IsBuyerMaker ? "🟢 买方主动" : "🔴 卖方主动",
                    6 => $"{diff:+0.00;-0.00;0.00}%",
                    _ => null
                };
            }
        }

        private void OnDgvTicksCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _renderedTicksCount || e.CellStyle == null) return;

            if (_pipeline.RingBuffer.TryGetAt(e.RowIndex, out var tick))
            {
                e.CellStyle.ForeColor = !tick.IsBuyerMaker
                    ? System.Drawing.Color.FromArgb(74, 222, 128) // 买方主动 绿色
                    : System.Drawing.Color.FromArgb(248, 113, 113); // 卖方主动 红色
            }
        }

        #endregion

        #region 事件与控制逻辑 (Start, Pause, Speed, Mode)

        private void BindPipelineEvents()
        {
            _pipeline.OnStatusChanged += status =>
            {
                if (this.IsDisposed || !this.IsHandleCreated) return;
                this.BeginInvoke(() =>
                {
                    UpdateControlButtonsState(status);
                });
            };

            _pipeline.OnLogMessage += (msg, isErr) =>
            {
                System.Drawing.Color col = isErr ? System.Drawing.Color.FromArgb(248, 113, 113) : System.Drawing.Color.FromArgb(226, 232, 240);
                _logQueue.Enqueue((msg, col));
            };
        }

        private async Task OnStartClickedAsync()
        {
            string coin = !string.IsNullOrWhiteSpace(cboCoin.Text)
                ? cboCoin.Text.Trim().ToUpperInvariant()
                : (cboCoin.SelectedItem?.ToString()?.Trim().ToUpperInvariant() ?? "BTCUSDT");

            cboCoin.Text = coin;
            if (!cboCoin.Items.Contains(coin))
            {
                cboCoin.Items.Add(coin);
            }

            if (rdoHistorical.Checked)
            {
                DateTime start = dtpStart.Value.Date;
                DateTime end = dtpEnd.Value.Date;
                if (start > end)
                {
                    MessageBox.Show("起始日期不能大于结束日期！", "参数错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                formsPlotTick.Plot.Title($"{coin} 逐笔 Tick 流式队列回放 ({start:yyyy-MM-dd} ~ {end:yyyy-MM-dd})", size: 14);
                await _pipeline.StartHistoricalPlaybackAsync(coin, start, end);
            }
            else
            {
                MarketType market = cboMarket.SelectedIndex == 0 ? MarketType.Spot : MarketType.UsdtFutures;
                formsPlotTick.Plot.Title($"⚡ {coin} 币安实盘实时 Tick 行情直连 ({(market == MarketType.Spot ? "现货" : "USDT合约")})", size: 14);
                await _pipeline.StartLiveStreamingAsync(coin, market);
            }
        }

        private void TogglePause()
        {
            if (_pipeline.Status == PlaybackStatus.Playing)
            {
                _pipeline.Pause();
            }
            else if (_pipeline.Status == PlaybackStatus.Paused)
            {
                _pipeline.Resume();
            }
        }

        private void UpdateControlButtonsState(PlaybackStatus status)
        {
            bool isRunning = status == PlaybackStatus.Playing || status == PlaybackStatus.Buffering || status == PlaybackStatus.LiveConnected;
            bool isPaused = status == PlaybackStatus.Paused;

            btnStart.Enabled = !isRunning && !isPaused;
            btnPause.Enabled = isRunning || isPaused;
            btnPause.Text = isPaused ? "▶ 继续" : "⏸ 暂停";
            btnStep.Enabled = isPaused || isRunning;
            btnStop.Enabled = isRunning || isPaused;

            rdoHistorical.Enabled = !isRunning && !isPaused;
            rdoLive.Enabled = !isRunning && !isPaused;
            cboMarket.Enabled = !isRunning && !isPaused;
            cboCoin.Enabled = !isRunning && !isPaused;
            dtpStart.Enabled = !isRunning && !isPaused;
            dtpEnd.Enabled = !isRunning && !isPaused;
        }

        private void OnSpeedTrackBarChanged(object? sender, EventArgs e)
        {
            double speed = tbSpeed.Value switch
            {
                1 => 1.0,
                2 => 5.0,
                3 => 20.0,
                4 => 50.0,
                5 => 100.0,
                6 => 500.0,
                _ => 10.0
            };
            _pipeline.SetSpeed(speed);
            lblSpeedVal.Text = $"{speed:0.#}x";
        }

        private void OnModeChanged(object? sender, EventArgs e)
        {
            bool isLive = rdoLive.Checked;
            cboMarket.Visible = isLive;
            lblDateRange.Visible = !isLive;
            dtpStart.Visible = !isLive;
            dtpEnd.Visible = !isLive;
            btnStart.Text = isLive ? "⚡ 连接实盘" : "▶ 开始回放";
        }

        private void OnWindowCapacityChanged(object? sender, EventArgs e)
        {
            int newCap = (int)numWindowCapacity.Value;
            if (newCap != _plotCapacity)
            {
                _plotCapacity = newCap;
                _tickSnapshotBuffer = new RawTick[_plotCapacity];
                _plotXs = new double[_plotCapacity];
                _plotYs = new double[_plotCapacity];
                RebuildPlotSeries();
            }
        }

        private void AppendLog(string message, System.Drawing.Color color)
        {
            if (txtLogs.IsDisposed) return;
            if (txtLogs.TextLength > 30000)
            {
                txtLogs.Select(0, 10000);
                txtLogs.SelectedText = "";
            }
            txtLogs.SelectionStart = txtLogs.TextLength;
            txtLogs.SelectionLength = 0;
            txtLogs.SelectionColor = color;
            txtLogs.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
            txtLogs.ScrollToCaret();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _renderTimer.Stop();
            _renderTimer.Dispose();
            _pipeline.Dispose();
            base.OnFormClosing(e);
        }

        #endregion
    }
}
