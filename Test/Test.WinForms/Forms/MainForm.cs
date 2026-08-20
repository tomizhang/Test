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
    /// 1. 自动记忆与持久化界面配置信息 (交易对、周期、起止时间、策略参数、窗口尺寸及分割布局)
    /// 2. 趋势线触碰 3-Tick 内回弹开仓策略 (高点回弹开空，低点回弹开多，LineX1X2>=40, LineAge>=4, 1分钟冷却)
    /// 3. 触发开仓的趋势线在图表上渲染为显目绿色 (IsTriggered=true)，日志输出完整趋势线特征
    /// 4. UI 采用 Timer 定时批量轮询抽取机制 (杜绝 BeginInvoke 消息风暴，窗口拖拽与交互 100% 丝滑)
    /// 5. ScottPlot 交互折线图实时呈现价格、高低点极值、趋势线延伸与交易信号标记
    /// </summary>
    public class MainForm : Form
    {
        private readonly IBacktestEngineService _engineService;
        private CancellationTokenSource? _cts;
        private BacktestResult? _latestResult;

        // 线程安全运行态缓存
        private volatile bool _isRealtimeChartEnabled = true;
        private volatile bool _isAutoScaleEnabled = true;
        private volatile string _currentRunningCoin = "BTCUSDT";
        private volatile string _currentRunningInterval = "1m";

        // UI 异步消息解耦缓冲
        private readonly ConcurrentQueue<(string Message, Color Color)> _logQueue = new ConcurrentQueue<(string, Color)>();
        private volatile BacktestProgress? _latestProgress = null;
        private ChartSnapshot? _latestChartSnapshot = null;
        private long _renderedSnapshotVersion = 0;
        private long _lastChartSnapshotTicks = 0;
        private System.Windows.Forms.Timer _uiRefreshTimer = null!;

        // UI 控件定义
        private SplitContainer splitMain = null!;
        private SplitContainer splitLeft = null!;
        private FormsPlot formsPlot = null!;
        private RichTextBox txtLogs = null!;
        private Panel panelLogHeader = null!;
        private Label lblLogTitle = null!;
        private Button btnClearLogsTop = null!;

        // 右侧控制面板控件
        private Panel panelRight = null!;
        private GroupBox grpData = null!;
        private ComboBox cboCoin = null!;
        private ComboBox cboInterval = null!;
        private DateTimePicker dtpStart = null!;
        private DateTimePicker dtpEnd = null!;

        private GroupBox grpStrategy = null!;
        private NumericUpDown numMaxKlines = null!;
        private NumericUpDown numMinTrendLines = null!;
        private NumericUpDown numMaxDeleted = null!;
        private NumericUpDown numLeftLen = null!;
        private NumericUpDown numRightLen = null!;
        private NumericUpDown numMaxSpan = null!;
        private NumericUpDown numMinSignalSpan = null!;
        private NumericUpDown numMinSignalAge = null!;
        private NumericUpDown numCooldown = null!;
        private CheckBox chkStrictEnvelope = null!;
        private CheckBox chkRealtimeChart = null!;
        private CheckBox chkAutoScale = null!;

        private GroupBox grpControl = null!;
        private Button btnStart = null!;
        private Button btnPause = null!;
        private Button btnStop = null!;
        private Button btnResetAxes = null!;
        private Button btnExportChart = null!;
        private Button btnClearLogs = null!;
        private ProgressBar progressBar = null!;
        private Label lblProgress = null!;

        private GroupBox grpStatus = null!;
        private Label lblStatTime = null!;
        private Label lblStatThroughput = null!;
        private Label lblStatKlines = null!;
        private Label lblStatTicks = null!;
        private Label lblStatSignals = null!;
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
            this.Size = new Size(1600, 960);
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
            splitLeft.Panel1.Controls.Add(formsPlot);

            // ② 左下：日志控制面板与 RichTextBox
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
                Text = "⚡ 关键日志与开仓信号监控",
                Font = new Font("Microsoft YaHei", 9.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(226, 232, 240),
                AutoSize = true,
                Location = new Point(8, 7)
            };

            btnClearLogsTop = new Button
            {
                Text = "清空日志",
                Size = new Size(70, 24),
                Location = new Point(185, 4),
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

            splitLeft.Panel2.Controls.Add(panelLogContainer);
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
            grpData = CreateGroupBox("1. 基础数据配置", top, 170);
            {
                var lblCoin = CreateLabel("交易对:", 15, 25);
                cboCoin = new ComboBox { Location = new Point(90, 22), Width = 250, DropDownStyle = ComboBoxStyle.DropDownList };
                cboCoin.Items.AddRange(new object[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "DOGEUSDT", "XRPUSDT" });
                cboCoin.SelectedIndex = 0;

                var lblInterval = CreateLabel("K线周期:", 15, 58);
                cboInterval = new ComboBox { Location = new Point(90, 55), Width = 250, DropDownStyle = ComboBoxStyle.DropDownList };
                cboInterval.Items.AddRange(new object[] { "1m (1分钟)", "3m (3分钟)", "5m (5分钟)", "15m (15分钟)", "30m (30分钟)", "1h (1小时)", "2h (2小时)", "4h (4小时)", "1d (1天)" });
                cboInterval.SelectedIndex = 0;

                var lblStart = CreateLabel("起始日期:", 15, 93);
                dtpStart = new DateTimePicker { Location = new Point(90, 90), Width = 250, Format = DateTimePickerFormat.Short, Value = new DateTime(2025, 1, 1) };

                var lblEnd = CreateLabel("结束日期:", 15, 128);
                dtpEnd = new DateTimePicker { Location = new Point(90, 125), Width = 250, Format = DateTimePickerFormat.Short, Value = new DateTime(2025, 1, 5) };

                grpData.Controls.AddRange(new Control[] { lblCoin, cboCoin, lblInterval, cboInterval, lblStart, dtpStart, lblEnd, dtpEnd });
            }
            panelRight.Controls.Add(grpData);
            top += grpData.Height + 10;

            // Group 2: 趋势线策略参数 (增加 LineX1X2 >= 40, LineAge >= 4 与 1分钟冷却阈值)
            grpStrategy = CreateGroupBox("2. 趋势线与开仓策略参数", top, 375);
            {
                var lblMaxK = CreateLabel("K线滑动窗口:", 15, 25);
                numMaxKlines = new NumericUpDown { Location = new Point(130, 22), Width = 210, Minimum = 100, Maximum = 100000, Value = 2000 };

                var lblMinT = CreateLabel("历史趋势线库:", 15, 53);
                numMinTrendLines = new NumericUpDown { Location = new Point(130, 50), Width = 210, Minimum = 100, Maximum = 50000, Value = 1000 };

                var lblLeft = CreateLabel("波峰左侧对比:", 15, 81);
                numLeftLen = new NumericUpDown { Location = new Point(130, 78), Width = 210, Minimum = 1, Maximum = 100, Value = 5 };

                var lblRight = CreateLabel("波峰右侧对比:", 15, 109);
                numRightLen = new NumericUpDown { Location = new Point(130, 106), Width = 210, Minimum = 1, Maximum = 100, Value = 5 };

                var lblSpan = CreateLabel("最大配对跨度:", 15, 137);
                numMaxSpan = new NumericUpDown { Location = new Point(130, 134), Width = 210, Minimum = 10, Maximum = 2000, Value = 100 };

                var lblSignalSpan = CreateLabel("开仓跨度≥(X1X2):", 15, 165);
                numMinSignalSpan = new NumericUpDown { Location = new Point(130, 162), Width = 210, Minimum = 1, Maximum = 500, Value = 40 };

                var lblSignalAge = CreateLabel("开仓寿命≥(Age):", 15, 193);
                numMinSignalAge = new NumericUpDown { Location = new Point(130, 190), Width = 210, Minimum = 1, Maximum = 100, Value = 4 };

                var lblCooldown = CreateLabel("触发冷却(秒):", 15, 221);
                numCooldown = new NumericUpDown { Location = new Point(130, 218), Width = 210, Minimum = 0, Maximum = 3600, Value = 60 };

                chkStrictEnvelope = new CheckBox { Text = "严格外包络 (禁止内部穿透)", Location = new Point(15, 248), Width = 320, Checked = true };

                chkRealtimeChart = new CheckBox { Text = "实时推送图表走势 (UI 定时刷新)", Location = new Point(15, 273), Width = 320, Checked = true };
                chkRealtimeChart.CheckedChanged += (s, e) => _isRealtimeChartEnabled = chkRealtimeChart.Checked;

                chkAutoScale = new CheckBox { Text = "回放时自动调节 X/Y 轴 (Auto-Scale)", Location = new Point(15, 298), Width = 320, Checked = true };
                chkAutoScale.CheckedChanged += (s, e) => _isAutoScaleEnabled = chkAutoScale.Checked;

                var lblStrategyNote = new Label
                {
                    Text = "🎯 策略规则: 触碰趋势线 3 个 Tick 内回弹即开仓\n(高点回弹开空，低点回弹开多，触发线变绿)",
                    Location = new Point(15, 324),
                    Size = new Size(325, 36),
                    ForeColor = Color.FromArgb(74, 222, 128), // Green 400
                    Font = new Font("Microsoft YaHei", 8F)
                };

                grpStrategy.Controls.AddRange(new Control[] { lblMaxK, numMaxKlines, lblMinT, numMinTrendLines, lblLeft, numLeftLen, lblRight, numRightLen, lblSpan, numMaxSpan, lblSignalSpan, numMinSignalSpan, lblSignalAge, numMinSignalAge, lblCooldown, numCooldown, chkStrictEnvelope, chkRealtimeChart, chkAutoScale, lblStrategyNote });
            }
            panelRight.Controls.Add(grpStrategy);
            top += grpStrategy.Height + 10;

            // Group 3: 控制按钮与进度条
            grpControl = CreateGroupBox("3. 执行控制与进度", top, 205);
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
                    Text = "🔍 复位/自适应轴",
                    Location = new Point(15, 70),
                    Size = new Size(155, 30),
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
                    Text = "🖼 导出/打开图表",
                    Location = new Point(185, 70),
                    Size = new Size(155, 30),
                    BackColor = Color.FromArgb(37, 99, 235), // Blue 600
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand
                };
                btnExportChart.FlatAppearance.BorderSize = 0;
                btnExportChart.Click += (s, e) => OpenOrExportChart();

                btnClearLogs = new Button
                {
                    Text = "🗑 清空日志",
                    Location = new Point(15, 105),
                    Size = new Size(325, 28),
                    BackColor = Color.FromArgb(71, 85, 105), // Slate 600
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand
                };
                btnClearLogs.FlatAppearance.BorderSize = 0;
                btnClearLogs.Click += (s, e) => txtLogs.Clear();

                progressBar = new ProgressBar
                {
                    Location = new Point(15, 140),
                    Size = new Size(325, 18),
                    Minimum = 0,
                    Maximum = 100,
                    Value = 0
                };

                lblProgress = new Label
                {
                    Text = "系统就绪，点击【▶ 开始】启动回测",
                    Location = new Point(15, 165),
                    Size = new Size(325, 30),
                    ForeColor = Color.FromArgb(148, 163, 184)
                };

                grpControl.Controls.AddRange(new Control[] { btnStart, btnPause, btnStop, btnResetAxes, btnExportChart, btnClearLogs, progressBar, lblProgress });
            }
            panelRight.Controls.Add(grpControl);
            top += grpControl.Height + 10;

            // Group 4: 实时统计看板 (增加开仓信号统计指标)
            grpStatus = CreateGroupBox("4. 统计监控看板", top, 235);
            {
                lblStatTime = CreateStatLabel("执行耗时: -", 15, 25);
                lblStatThroughput = CreateStatLabel("吞吐速率: -", 15, 48);
                lblStatSignals = CreateStatLabel("开仓信号: 多单 0 | 空单 0 (总计 0)", 15, 71);
                lblStatSignals.ForeColor = Color.FromArgb(232, 121, 249); // Fuchsia 400
                lblStatKlines = CreateStatLabel("K线总量: -", 15, 94);
                lblStatTicks = CreateStatLabel("Tick总量: -", 15, 117);
                lblStatPeaksValleys = CreateStatLabel("识别极值: 高点 0 | 低点 0", 15, 140);
                lblStatActiveLines = CreateStatLabel("活跃趋势线: 阻力 0 | 支撑 0", 15, 163);
                lblStatDeletedLines = CreateStatLabel("已击穿删除: 0 条", 15, 186);

                grpStatus.Controls.AddRange(new Control[] { lblStatTime, lblStatThroughput, lblStatSignals, lblStatKlines, lblStatTicks, lblStatPeaksValleys, lblStatActiveLines, lblStatDeletedLines });
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

            numMaxKlines.Value = Math.Clamp(settings.MaxKlinesCapacity, numMaxKlines.Minimum, numMaxKlines.Maximum);
            numMinTrendLines.Value = Math.Clamp(settings.MinTrendLinesCapacity, numMinTrendLines.Minimum, numMinTrendLines.Maximum);
            numLeftLen.Value = Math.Clamp(settings.LeftLen, numLeftLen.Minimum, numLeftLen.Maximum);
            numRightLen.Value = Math.Clamp(settings.RightLen, numRightLen.Minimum, numRightLen.Maximum);
            numMaxSpan.Value = Math.Clamp(settings.MaxSpan, numMaxSpan.Minimum, numMaxSpan.Maximum);
            numMinSignalSpan.Value = Math.Clamp(settings.MinSignalLineX1X2, numMinSignalSpan.Minimum, numMinSignalSpan.Maximum);
            numMinSignalAge.Value = Math.Clamp(settings.MinSignalLineAge, numMinSignalAge.Minimum, numMinSignalAge.Maximum);
            numCooldown.Value = Math.Clamp(settings.SignalCooldownSeconds, numCooldown.Minimum, numCooldown.Maximum);

            chkStrictEnvelope.Checked = settings.StrictEnvelope;
            chkRealtimeChart.Checked = settings.RealtimeChart;
            chkAutoScale.Checked = settings.AutoScale;

            _isRealtimeChartEnabled = chkRealtimeChart.Checked;
            _isAutoScaleEnabled = chkAutoScale.Checked;

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
                    MaxKlinesCapacity = (int)numMaxKlines.Value,
                    MinTrendLinesCapacity = (int)numMinTrendLines.Value,
                    LeftLen = (int)numLeftLen.Value,
                    RightLen = (int)numRightLen.Value,
                    MaxSpan = (int)numMaxSpan.Value,
                    MinSignalLineX1X2 = (int)numMinSignalSpan.Value,
                    MinSignalLineAge = (int)numMinSignalAge.Value,
                    SignalCooldownSeconds = (int)numCooldown.Value,
                    StrictEnvelope = chkStrictEnvelope.Checked,
                    RealtimeChart = chkRealtimeChart.Checked,
                    AutoScale = chkAutoScale.Checked,
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
            // 1. 批量消费日志队列
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
                            tradeSignals: snap.TradeSignals);

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
                _logQueue.Enqueue((message, Color.FromArgb(241, 245, 249)));
            };

            _engineService.OnTradeSignalGenerated += signal =>
            {
                Color sigColor = signal.Side == TradeSide.Buy ? Color.FromArgb(74, 222, 128) : Color.FromArgb(244, 63, 94);
                _logQueue.Enqueue(("\n------------------------------------------------------------", Color.FromArgb(74, 222, 128)));
                _logQueue.Enqueue((signal.ToString(), sigColor));
                _logQueue.Enqueue(($"  📌 [趋势线详情] {signal.Reason}", Color.FromArgb(226, 232, 240)));
                _logQueue.Enqueue(("------------------------------------------------------------\n", Color.FromArgb(74, 222, 128)));
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

                        int histCount = strategy.HistoricalTrendLines.Count;
                        int takeCount = Math.Min(100, histCount);
                        for (int i = histCount - takeCount; i < histCount; i++)
                        {
                            linesSnapshot.Add(strategy.HistoricalTrendLines[i]);
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

                        string realtimeSummary = $"实时回测推进中: {coin} {intervalStr} | 当前 K 线: #{index:D4} (最新收: {kline.Close:F2})\n" +
                                                 $"开仓信号: 多单={strategy.LongSignalsCount}笔, 空单={strategy.ShortSignalsCount}笔 (总计 {strategy.TotalSignalsCount}笔)\n" +
                                                 $"识别极值: 高点={peaksSnapshot.Length}, 低点={valleysSnapshot.Length} | 活跃阻力={strategy.ActiveResistanceLines.Count}, 支撑={strategy.ActiveSupportLines.Count}";

                        _latestChartSnapshot = new ChartSnapshot
                        {
                            Klines = klinesSnapshot,
                            Peaks = peaksSnapshot,
                            Valleys = valleysSnapshot,
                            Lines = linesSnapshot,
                            TradeSignals = signalsSnapshot,
                            Summary = realtimeSummary,
                            Title = $"{coin} {intervalStr} - 实时回测动态走势 (K线 #{index:D4})",
                            StartGlobalIndex = startGlobal,
                            AutoScale = autoScale,
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
                MaxKlinesCapacity = (int)numMaxKlines.Value,
                MinTrendLinesCapacity = (int)numMinTrendLines.Value,
                LeftLen = (int)numLeftLen.Value,
                RightLen = (int)numRightLen.Value,
                MaxSpan = (int)numMaxSpan.Value,
                MinSignalLineX1X2 = (int)numMinSignalSpan.Value,
                MinSignalLineAge = (int)numMinSignalAge.Value,
                SignalCooldownSeconds = (int)numCooldown.Value,
                AllowInternalPenetration = !chkStrictEnvelope.Checked,
                ParallelDays = 3,
                GenerateChart = true
            };

            // 缓存运行态变量供后台线程安全读取
            _isRealtimeChartEnabled = chkRealtimeChart.Checked;
            _isAutoScaleEnabled = chkAutoScale.Checked;
            _currentRunningCoin = request.Coin;
            _currentRunningInterval = interval.ToIntervalString();

            _logQueue.Enqueue(("\n========================================================", Color.FromArgb(56, 189, 248)));
            _logQueue.Enqueue(($"[启动回测] 目标: {request.Coin}, 周期: {interval.ToIntervalString()}, 窗口: {request.StartDate:yyyy-MM-dd} ~ {request.EndDate:yyyy-MM-dd}", Color.FromArgb(56, 189, 248)));
            _logQueue.Enqueue(($"[策略模式] 触碰 3-Tick 内回弹开仓 (过滤: LineX1X2 >= {request.MinSignalLineX1X2}, LineAge >= {request.MinSignalLineAge}, 冷却: {request.SignalCooldownSeconds}s)", Color.FromArgb(250, 204, 21)));
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

                    var allLines = new List<TrendLine>(strat.HistoricalTrendLines);
                    if (strat.ActiveResistanceLines.Count > 0) allLines.AddRange(strat.ActiveResistanceLines);
                    if (strat.ActiveSupportLines.Count > 0) allLines.AddRange(strat.ActiveSupportLines);

                    string summary = $"币种: {request.Coin}, 周期: {interval.ToIntervalString()}, 窗口: {request.StartDate:yyyy-MM-dd} ~ {request.EndDate:yyyy-MM-dd}\n" +
                                     $"耗时: {_latestResult.ElapsedMilliseconds} ms, 吞吐: {_latestResult.TicksPerSecond:N0} ticks/s | 开仓: 多 {strat.LongSignalsCount} | 空 {strat.ShortSignalsCount}";

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
                        tradeSignals: strat.TradeSignals);

                    formsPlot.Refresh();

                    // 更新统计看板
                    lblStatTime.Text = $"执行耗时: {_latestResult.ElapsedMilliseconds:N0} ms";
                    lblStatThroughput.Text = $"吞吐速率: {_latestResult.TicksPerSecond:N0} ticks/s";
                    lblStatSignals.Text = $"开仓信号: 多单 {strat.LongSignalsCount} | 空单 {strat.ShortSignalsCount} (总计 {strat.TotalSignalsCount})";
                    lblStatPeaksValleys.Text = $"识别极值: 高点 {strat.Peaks.Count} | 低点 {strat.Valleys.Count}";
                    lblStatActiveLines.Text = $"活跃趋势线: 阻力 {strat.ActiveResistanceLines.Count} | 支撑 {strat.ActiveSupportLines.Count}";
                    lblStatDeletedLines.Text = $"已击穿删除: {strat.DeletedTrendLinesCount} 条";

                    _logQueue.Enqueue(($"\n[回测成功] 耗时: {_latestResult.ElapsedMilliseconds} ms, 触发开仓信号: 多单 {strat.LongSignalsCount} 笔, 空单 {strat.ShortSignalsCount} 笔 (总计 {strat.TotalSignalsCount} 笔)！", Color.FromArgb(74, 222, 128)));
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

        #endregion

        #region 日志内部安全写入 (仅在 UI 线程执行)

        private void AppendLogInternal(string message, Color color)
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
            txtLogs.AppendText(message + "\n");
            txtLogs.ScrollToCaret();
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
            public string Summary { get; init; } = string.Empty;
            public string Title { get; init; } = string.Empty;
            public int StartGlobalIndex { get; init; }
            public bool AutoScale { get; init; }
            public long SnapshotVersion { get; init; }
        }

        #endregion
    }
}
