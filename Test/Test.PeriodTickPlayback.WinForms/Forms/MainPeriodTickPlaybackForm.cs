using Common;
using Common.Helper;
using Common.Models;
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
using Test.PeriodTickPlayback.WinForms.Engine;
using Test.PeriodTickPlayback.WinForms.Helper;
using Test.PeriodTickPlayback.WinForms.Models;

namespace Test.PeriodTickPlayback.WinForms.Forms
{
    /// <summary>
    /// 大周期与逐笔 Tick 嵌套流式回放系统主窗体
    /// 核心交互法则：
    /// 1. 播放指定时间段 K 线，集成大周期与逐笔 Tick 协同回放；
    /// 2. 若选择 30 分钟大周期，先在下方微观面板输出完 30 分钟内的所有 Tick 后，再在上方大图正式定型输出该 30 分钟 K 线；
    /// 3. 大图除了成交量，其它 MA 等指标全部不要 (纯净金融级 OHLCV)。
    /// </summary>
    public class MainPeriodTickPlaybackForm : Form
    {
        private readonly PeriodTickPlaybackEngine _engine = new();
        private PeriodPlaybackSettings _settings = new();
        private bool _isApplyingSettings = true;

        private CancellationTokenSource? _loadCts = null;
        private bool _isLoading = false;

        // UI 异步日志队列与平滑刷新定时器
        private readonly ConcurrentQueue<(string Message, Color Color)> _logQueue = new();
        private System.Windows.Forms.Timer _uiRefreshTimer = null!;
        private volatile bool _macroPlotNeedsRefresh = false;
        private volatile bool _tickPlotNeedsRefresh = false;
        private readonly ToolTip _toolTip = new();

        // 内部缓存供图表极速渲染
        private readonly List<RawTick> _currentBucketTicks = new(50000);
        private int _currentBucketTickCursor = 0;
        private DateTime _currentBucketStart = DateTime.MinValue;
        private DateTime _currentBucketEnd = DateTime.MinValue;

        // 分批加载调度控制与并发锁
        private readonly List<DateTime> _batchDates = new();
        private int _nextBatchDateIndex = 0;
        private bool _isBatchLoading = false;
        private readonly SemaphoreSlim _batchLock = new(1, 1);

        #region 控件定义

        // 顶部工具栏
        private Panel pnlTop = null!;
        private ComboBox cboCoin = null!;
        private DateTimePicker dtpStart = null!;
        private DateTimePicker dtpEnd = null!;
        private ComboBox cboPeriod = null!;
        private NumericUpDown numCustomMinutes = null!;
        private NumericUpDown numCustomSeconds = null!;
        private Button btnLoad = null!;
        private Button btnNextBatch = null!;
        private Button btnPlay = null!;
        private Button btnStop = null!;
        private Button btnNextBucket = null!;
        private Button btnStepTick = null!;
        private ComboBox cboSpeed = null!;
        private CheckBox chkAutoFollow = null!;
        private CheckBox chkShowVolume = null!;
        private CheckBox chkAutoAppend = null!;
        private ComboBox cboChartType = null!;
        private Button btnToggleChartType = null!;
        private Label lblBatchBadge = null!;
        private CheckBox chkConsecutiveTrend = null!;
        private CheckBox chkAngleLines = null!;
        private CheckBox chkMacroRecentTrendLines = null!;
        private Label lblCustomAngles = null!;
        private TextBox txtCustomAngles = null!;
        private NumericUpDown numConsecutiveBars = null!;
        private NumericUpDown numConsecutivePct = null!;

        // 主分割容器
        private SplitContainer splitMain = null!;
        private SplitContainer splitBottom = null!;

        // 图表
        private FormsPlot formsPlotMacro = null!;
        private FormsPlot formsPlotTick = null!;

        // 标头面板与状态徽章
        private Panel pnlMacroHeader = null!;
        private Label lblMacroTitle = null!;
        private Label lblMacroBadge = null!;

        private Panel pnlTickHeader = null!;
        private Label lblTickTitle = null!;
        private Label lblTickBadge = null!;
        private Label lblTickRatioBadge = null!; // 🎯 Tick 笔数多空比徽章
        private Label lblVolRatioBadge = null!;  // 📊 成交量多空比徽章
        private Button btnResumeLiveFollow = null!;
        private Button btnRatioProximity = null!;

        // 微观 Tick 窗口独立时间周期一排按钮与自定义秒/分聚合控制器
        private Label lblTickPeriod = null!;
        private readonly List<Button> _tickPeriodButtons = new();
        private int _selectedTickPeriodIndex = 0;
        private Button _btnCustomSeconds = null!;
        private Button _btnCustomMinutes = null!;
        private NumericUpDown numTickCustomSeconds = null!;
        private NumericUpDown numTickCustomMinutes = null!;
        private Button btnToggleTickChartType = null!;
        private CheckBox chkShowTickRatio = null!;
        private CheckBox chkTickConsecutiveTrend = null!;
        private CheckBox chkTickChannel = null!;
        private Label lblTickConsecutiveBars = null!;
        private NumericUpDown numTickConsecutiveBars = null!;
        private Label lblTickConsecutivePct = null!;
        private NumericUpDown numTickConsecutivePct = null!;
        private CheckBox chkTickAngleLines = null!;
        private CheckBox chkTickRecentTrendLines = null!;

        // 动态通道趋势线记录与管理 (只有通道信号产生后才绘制，且每个通道的趋势线均保留观察)
        private bool _hasConsecutiveTrendTriggered = false;
        private RecentSubPeriodTrendLineResult? _latestDynamicTrendLines = null;
        private long _lastEvaluatedTotalTicks = -1;
        private readonly List<Common.Models.TrendLine> _persistentRetainedLines = new();
        private readonly Dictionary<int, ChannelTrendLineRecord> _channelTrendLineRecords = new();
        private TimeSpan _lastTrendSubPeriodSpan = TimeSpan.Zero;
        private int _lastEvaluatedTrendsCount = -1;
        private int _lastEvaluatedEndIndex = -1;

        // K线点击与Shift多选状态 (大周期)
        private int? _selectedBarAnchor = null;
        private int? _selectedBarStartIndex = null;
        private int? _selectedBarEndIndex = null;
        private MacroConsecutiveTrendItem? _selectedConsecutiveTrend = null;
        private Point _macroMouseDownPoint;

        // 微观 Tick 窗口 K线点击与Shift多选状态
        private Point _tickMouseDownPoint;
        private bool _tickIsDragging = false;
        private bool _tickAutoFollow = true;
        private int? _selectedMicroBarAnchor = null;
        private int? _selectedMicroBarStartIndex = null;
        private int? _selectedMicroBarEndIndex = null;
        private IReadOnlyList<PeriodBucket>? _currentMicroBuckets = null;

        // 微观 Tick 鼠标悬停交互指示器 (十字准星 + 吸附标记 + 悬浮看板)
        private readonly TickHoverIndicator _tickHoverIndicator = new();
        private string _lastDefaultTickBadgeText = "当前无活动周期";
        private Color _lastDefaultTickBadgeColor = Color.FromArgb(148, 163, 184);

        // 右下侧监控看板与流水
        private TabControl tabControlRight = null!;
        private TabPage tabDashboard = null!;
        private TabPage tabLogs = null!;

        private Label lblCardPeriod = null!;
        private Label lblCardTickProgress = null!;
        private Label lblCardMacroCount = null!;
        private Label lblCardFormingPrice = null!;
        private Label lblCardTps = null!;
        private Label lblCardTickRatio = null!; // 🎯 Tick 笔数多空比卡片
        private Label lblCardVolRatio = null!;  // 📊 成交量多空比卡片
        private ProgressBar prgOverall = null!;
        private ProgressBar prgBucket = null!;

        private DataGridView dgvRecentTicks = null!;
        private RichTextBox txtLogs = null!;
        private Button btnClearLogs = null!;
        private CheckBox chkVerboseLog = null!;

        #endregion

        public MainPeriodTickPlaybackForm()
        {
            _isApplyingSettings = true;
            _settings = PeriodPlaybackSettings.Load();

            InitializeComponent();
            ApplyDarkTheme();
            SetupEngineEvents();
            SetupUiRefreshTimer();

            ApplySettingsToUi();
            _isApplyingSettings = false;

            AppendLog("[系统就绪] 大周期与逐笔 Tick 嵌套流式回放系统已就绪，支持自由切片聚合与历史巡检。", Color.FromArgb(74, 222, 128));
        }

        #region 初始化界面与布局

        private void InitializeComponent()
        {
            this.Text = "大周期与逐笔 Tick 嵌套流式回放系统 - [先播完周期内全部Tick后再输出大周期K线]";
            this.Size = new Size(1600, 960);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.MinimumSize = new Size(1150, 720);

            // 1. 顶部控制工具栏 (采用三行紧凑自适应布局，彻底杜绝任何分辨率下的遮挡与溢出)
            pnlTop = new Panel
            {
                Dock = DockStyle.Top,
                Height = 94,
                BackColor = Color.FromArgb(30, 41, 59),
                Padding = new Padding(10, 4, 10, 4),
                AutoScroll = true
            };

            // ==================== Row 1: 数据源选择与分批载入 (y = 6) ====================
            var lblCoin = CreateLabel("币种:", 10, 9);
            cboCoin = new ComboBox
            {
                Location = new Point(48, 6),
                Width = 92,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold)
            };
            cboCoin.Items.AddRange(new object[] { "BTCUSDT", "ETHUSDT", "NEARUSDT", "SOLUSDT" });
            cboCoin.SelectedIndex = 0;
            cboCoin.SelectedIndexChanged += (s, e) => SaveSettingsFromUi();

            // 起止日期
            var lblStart = CreateLabel("起始:", 148, 9);
            dtpStart = new DateTimePicker
            {
                Location = new Point(184, 6),
                Width = 105,
                Format = DateTimePickerFormat.Custom,
                CustomFormat = "yyyy-MM-dd",
                Font = new Font("Microsoft YaHei", 8.5F)
            };
            dtpStart.ValueChanged += (s, e) => SaveSettingsFromUi();

            var lblEnd = CreateLabel("截止:", 296, 9);
            dtpEnd = new DateTimePicker
            {
                Location = new Point(332, 6),
                Width = 105,
                Format = DateTimePickerFormat.Custom,
                CustomFormat = "yyyy-MM-dd",
                Font = new Font("Microsoft YaHei", 8.5F)
            };
            dtpEnd.ValueChanged += (s, e) => SaveSettingsFromUi();

            // 周期选择
            var lblPeriod = CreateLabel("大周期:", 445, 9);
            cboPeriod = new ComboBox
            {
                Location = new Point(498, 6),
                Width = 118,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold)
            };
            cboPeriod.Items.AddRange(new object[]
            {
                "30分钟 (30m) [默认]",
                "15分钟 (15m)",
                "5分钟 (5m)",
                "3分钟 (3m)",
                "1分钟 (1m)",
                "1小时 (1h)",
                "2小时 (2h)",
                "4小时 (4h)",
                "1天 (1d)",
                "自定义分钟",
                "自定义秒钟",
                "30秒 (30s)",
                "15秒 (15s)",
                "5秒 (5s)",
                "1秒 (1s)"
            });
            cboPeriod.SelectedIndex = 0;

            numCustomMinutes = new NumericUpDown
            {
                Location = new Point(620, 6),
                Width = 48,
                Minimum = 1,
                Maximum = 1440,
                Value = 30,
                Visible = false,
                Font = new Font("Microsoft YaHei", 8.5F)
            };
            _toolTip.SetToolTip(numCustomMinutes, "自定义 K 线大周期分钟数 (1~1440 分钟)");

            numCustomSeconds = new NumericUpDown
            {
                Location = new Point(620, 6),
                Width = 48,
                Minimum = 1,
                Maximum = 3600,
                Value = 5,
                Visible = false,
                Font = new Font("Microsoft YaHei", 8.5F)
            };
            _toolTip.SetToolTip(numCustomSeconds, "自定义 K 线大周期秒数 (1~3600 秒，如 5s、10s、15s、30s、60s)");

            cboPeriod.SelectedIndexChanged += (s, e) =>
            {
                numCustomMinutes.Visible = (cboPeriod.SelectedIndex == 9);
                numCustomSeconds.Visible = (cboPeriod.SelectedIndex == 10);
                SaveSettingsFromUi();
            };
            numCustomMinutes.ValueChanged += (s, e) => SaveSettingsFromUi();
            numCustomSeconds.ValueChanged += (s, e) => SaveSettingsFromUi();

            // 分批载入按钮：首批数据
            btnLoad = CreateButton("载入首批", 674, 5, 78, 26, Color.FromArgb(2, 132, 199));
            btnLoad.Click += async (s, e) => await LoadFirstBatchAsync();

            // 载入下一批按钮
            btnNextBatch = CreateButton("载入下批", 756, 5, 80, 26, Color.FromArgb(14, 165, 233));
            btnNextBatch.Enabled = false;
            btnNextBatch.Click += async (s, e) => await LoadNextBatchAsync(isBackgroundPreload: false);

            chkAutoAppend = new CheckBox
            {
                Text = "自动追加下批",
                Location = new Point(842, 8),
                AutoSize = true,
                Checked = true,
                ForeColor = Color.FromArgb(74, 222, 128),
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold)
            };
            chkAutoAppend.CheckedChanged += (s, e) => SaveSettingsFromUi();

            lblBatchBadge = CreateLabel("批次: 准备就绪", 955, 9);
            lblBatchBadge.ForeColor = Color.FromArgb(56, 189, 248);
            lblBatchBadge.Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold);

            // ==================== Row 2: 播放控制与显示设置 (y = 35) ====================
            btnPlay = CreateButton("播放", 10, 35, 64, 26, Color.FromArgb(16, 185, 129));
            btnPlay.Click += (s, e) =>
            {
                if (_engine.State == PlaybackState.Playing)
                {
                    _engine.Pause();
                }
                else
                {
                    _engine.Play();
                }
            };

            btnStop = CreateButton("停止", 80, 35, 64, 26, Color.FromArgb(225, 29, 72));
            btnStop.Click += (s, e) =>
            {
                _engine.Stop();
                _hasConsecutiveTrendTriggered = false;
                _latestDynamicTrendLines = null;
                _lastEvaluatedTotalTicks = -1;
                _lastEvaluatedTrendsCount = -1;
                _lastEvaluatedEndIndex = -1;
                _persistentRetainedLines.Clear();
                _channelTrendLineRecords.Clear();
            };

            btnNextBucket = CreateButton("完成当前周期", 150, 35, 102, 26, Color.FromArgb(124, 58, 237));
            btnNextBucket.Click += (s, e) => _engine.FastForwardCurrentBucket();

            btnStepTick = CreateButton("单步Tick", 258, 35, 74, 26, Color.FromArgb(71, 85, 105));
            btnStepTick.Click += (s, e) => _engine.StepTick(1);

            var lblSpeed = CreateLabel("倍速:", 340, 38);
            cboSpeed = new ComboBox
            {
                Location = new Point(378, 35),
                Width = 78,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Microsoft YaHei", 8.5F)
            };
            cboSpeed.Items.AddRange(new object[] { "1x (实时)", "5x", "10x", "50x", "100x", "500x", "极速" });
            cboSpeed.SelectedIndex = 2; // 默认 10x
            cboSpeed.SelectedIndexChanged += (s, e) =>
            {
                UpdatePlaybackSpeed();
                SaveSettingsFromUi();
            };

            chkAutoFollow = new CheckBox
            {
                Text = "自动跟随",
                Location = new Point(464, 38),
                AutoSize = true,
                Checked = false, // 默认不勾选自动跟随
                ForeColor = Color.FromArgb(226, 232, 240),
                Font = new Font("Microsoft YaHei", 8.5F)
            };
            chkAutoFollow.CheckedChanged += (s, e) =>
            {
                if (chkAutoFollow.Checked)
                {
                    ClearBarSelection();
                }
                _macroPlotNeedsRefresh = true;
                formsPlotMacro.Refresh();
                SaveSettingsFromUi();
            };

            chkShowVolume = new CheckBox
            {
                Text = "成交量",
                Location = new Point(546, 38),
                AutoSize = true,
                Checked = true,
                ForeColor = Color.FromArgb(226, 232, 240),
                Font = new Font("Microsoft YaHei", 8.5F)
            };
            chkShowVolume.CheckedChanged += (s, e) =>
            {
                _macroPlotNeedsRefresh = true;
                SaveSettingsFromUi();
            };

            var lblChartType = CreateLabel("显示类型:", 614, 38);
            cboChartType = new ComboBox
            {
                Location = new Point(678, 35),
                Width = 125,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold)
            };
            cboChartType.Items.AddRange(new object[] { "蜡烛图 (Candles)", "收盘折线 (Line)" });
            cboChartType.SelectedIndex = 0;
            cboChartType.SelectedIndexChanged += (s, e) =>
            {
                bool isLine = cboChartType.SelectedIndex == 1;
                if (btnToggleChartType != null)
                {
                    btnToggleChartType.Text = isLine ? "切换为蜡烛图" : "切换为折线图";
                    btnToggleChartType.ForeColor = isLine ? Color.FromArgb(74, 222, 128) : Color.FromArgb(56, 189, 248);
                    btnToggleChartType.FlatAppearance.BorderColor = btnToggleChartType.ForeColor;
                }
                lblMacroTitle.Text = isLine
                    ? "大周期收盘折线走势图 (纯净无额外 MA 指标，含成交量副图)"
                    : "大周期 K 线走势图 (纯净无额外 MA 指标，含成交量副图)";
                _macroPlotNeedsRefresh = true;
                SaveSettingsFromUi();
            };

            // ==================== Row 3: 连续涨跌标记与双进度条 (y = 64) ====================
            chkConsecutiveTrend = new CheckBox
            {
                Text = "连续涨跌标记",
                Location = new Point(10, 64),
                AutoSize = true,
                Checked = true,
                ForeColor = Color.FromArgb(245, 158, 11), // 琥珀金
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold)
            };
            chkConsecutiveTrend.CheckedChanged += (s, e) =>
            {
                _macroPlotNeedsRefresh = true;
                _tickPlotNeedsRefresh = true;
                SaveSettingsFromUi();
            };

            var lblConsecutiveBars = CreateLabel("最少:", 118, 66);
            numConsecutiveBars = new NumericUpDown
            {
                Location = new Point(152, 63),
                Width = 44,
                Minimum = 2,
                Maximum = 50,
                Value = 5,
                Font = new Font("Microsoft YaHei", 8.5F)
            };
            numConsecutiveBars.ValueChanged += (s, e) =>
            {
                _macroPlotNeedsRefresh = true;
                _tickPlotNeedsRefresh = true;
                SaveSettingsFromUi();
            };

            var lblConsecutivePct = CreateLabel("幅度%:", 202, 66);
            numConsecutivePct = new NumericUpDown
            {
                Location = new Point(248, 63),
                Width = 52,
                Minimum = 0.1m,
                Maximum = 50.0m,
                DecimalPlaces = 1,
                Increment = 0.1m,
                Value = 2.5m,
                Font = new Font("Microsoft YaHei", 8.5F)
            };
            numConsecutivePct.ValueChanged += (s, e) =>
            {
                _macroPlotNeedsRefresh = true;
                _tickPlotNeedsRefresh = true;
                SaveSettingsFromUi();
            };

            var lblProgOverall = CreateLabel("总进度:", 312, 66);
            prgOverall = new ProgressBar { Location = new Point(360, 66), Width = 160, Height = 14 };

            var lblProgBucket = CreateLabel("周期进度:", 532, 66);
            prgBucket = new ProgressBar { Location = new Point(594, 66), Width = 160, Height = 14 };

            chkAngleLines = new CheckBox
            {
                Text = "K线角度线",
                Location = new Point(764, 64),
                AutoSize = true,
                Checked = _settings.ShowMacroAngleLines,
                ForeColor = Color.FromArgb(168, 85, 247), // 科技紫
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _toolTip.SetToolTip(chkAngleLines, "开启/隐藏连续涨跌第一根 K 线高低双点位发射的多角度趋势线 (大周期K线)");
            chkAngleLines.CheckedChanged += (s, e) =>
            {
                _settings.ShowMacroAngleLines = chkAngleLines.Checked;
                _macroPlotNeedsRefresh = true;
                formsPlotMacro.Refresh();
                SaveSettingsFromUi();
            };

            lblCustomAngles = CreateLabel("角度:", 860, 66);
            lblCustomAngles.ForeColor = Color.FromArgb(203, 213, 225);

            txtCustomAngles = new TextBox
            {
                Location = new Point(898, 63),
                Width = 86,
                Text = _settings.CustomAngles,
                Font = new Font("Consolas", 8.5F, FontStyle.Bold),
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.FromArgb(56, 189, 248),
                BorderStyle = BorderStyle.FixedSingle
            };
            _toolTip.SetToolTip(txtCustomAngles, "自定义趋势线角度（度数，逗号或空格隔开，如: 25, 45, 65 或 15, 30, 45, 60）");
            txtCustomAngles.TextChanged += (s, e) =>
            {
                _settings.CustomAngles = txtCustomAngles.Text;
                _macroPlotNeedsRefresh = true;
                _tickPlotNeedsRefresh = true;
                SaveSettingsFromUi();
                RefreshTickPlotDirectly();
            };

            chkMacroRecentTrendLines = new CheckBox
            {
                Text = "300根趋势线",
                Location = new Point(995, 64),
                AutoSize = true,
                Checked = _settings.ShowMacroRecentTrendLines,
                ForeColor = Color.FromArgb(56, 189, 248), // 亮天蓝
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _toolTip.SetToolTip(chkMacroRecentTrendLines, "当出现连续涨跌后，提取最近300根小周期K线的高低点绘制动态趋势线 (大周期K线图显示)");
            chkMacroRecentTrendLines.CheckedChanged += (s, e) =>
            {
                _settings.ShowMacroRecentTrendLines = chkMacroRecentTrendLines.Checked;
                _macroPlotNeedsRefresh = true;
                formsPlotMacro.Refresh();
                SaveSettingsFromUi();
            };

            pnlTop.Controls.AddRange(new Control[]
            {
                lblCoin, cboCoin,
                lblStart, dtpStart,
                lblEnd, dtpEnd,
                lblPeriod, cboPeriod, numCustomMinutes, numCustomSeconds,
                btnLoad, btnNextBatch,
                chkAutoAppend, lblBatchBadge,
                btnPlay, btnStop, btnNextBucket, btnStepTick,
                lblSpeed, cboSpeed,
                chkAutoFollow, chkShowVolume,
                lblChartType, cboChartType,
                chkConsecutiveTrend, lblConsecutiveBars, numConsecutiveBars, lblConsecutivePct, numConsecutivePct,
                lblProgOverall, prgOverall, lblProgBucket, prgBucket,
                chkAngleLines, lblCustomAngles, txtCustomAngles,
                chkMacroRecentTrendLines
            });

            // 2. 主分割容器 (上下切分：上部大周期 K 线，下部 Tick 图表 + 右侧监控看板)
            splitMain = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 480,
                SplitterWidth = 6,
                BackColor = Color.FromArgb(30, 41, 59)
            };
            splitMain.SplitterMoved += (s, e) => SaveSettingsFromUi();

            // 2.1 上部容器：大周期 K 线图
            var pnlTopChartContainer = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(15, 23, 42) };
            pnlMacroHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 32,
                BackColor = Color.FromArgb(30, 41, 59),
                Padding = new Padding(8, 6, 8, 4)
            };
            lblMacroTitle = new Label
            {
                Text = "大周期 K 线走势图 (纯净无额外 MA 指标，含成交量副图)",
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(250, 204, 21),
                Location = new Point(10, 6),
                AutoSize = true
            };
            lblMacroBadge = new Label
            {
                Text = "[等待载入数据...]",
                Font = new Font("Microsoft YaHei", 8.5F),
                ForeColor = Color.FromArgb(148, 163, 184),
                Location = new Point(410, 7),
                AutoSize = true
            };

            btnToggleChartType = new Button
            {
                Text = "切换为折线图",
                Size = new Size(115, 24),
                Location = new Point(pnlMacroHeader.ClientSize.Width - 125, 4),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.FromArgb(56, 189, 248),
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnToggleChartType.FlatAppearance.BorderColor = Color.FromArgb(56, 189, 248);
            btnToggleChartType.FlatAppearance.BorderSize = 1;
            btnToggleChartType.Click += (s, e) =>
            {
                cboChartType.SelectedIndex = cboChartType.SelectedIndex == 0 ? 1 : 0;
            };

            lblMacroTitle.SizeChanged += (s, e) => lblMacroBadge.Left = lblMacroTitle.Right + 12;
            lblMacroTitle.TextChanged += (s, e) => lblMacroBadge.Left = lblMacroTitle.Right + 12;
            lblMacroBadge.Left = lblMacroTitle.Right + 12;
            pnlMacroHeader.Resize += (s, e) => btnToggleChartType.Location = new Point(Math.Max(300, pnlMacroHeader.ClientSize.Width - btnToggleChartType.Width - 10), 4);

            pnlMacroHeader.Controls.AddRange(new Control[] { lblMacroTitle, lblMacroBadge, btnToggleChartType });

            formsPlotMacro = new FormsPlot { Dock = DockStyle.Fill, BackColor = Color.FromArgb(15, 23, 42) };
            formsPlotMacro.MouseDown += OnFormsPlotMacroMouseDown;
            formsPlotMacro.MouseUp += OnFormsPlotMacroMouseUp;
            formsPlotMacro.MouseMove += OnFormsPlotMacroMouseMove;
            formsPlotMacro.MouseDoubleClick += (s, e) => ClearBarSelection();
            pnlTopChartContainer.Controls.Add(formsPlotMacro);
            pnlTopChartContainer.Controls.Add(pnlMacroHeader);
            splitMain.Panel1.Controls.Add(pnlTopChartContainer);

            // 2.2 下部容器：左右切分 (左边 Tick 走势折线图，右边指标卡与逐笔流水)
            splitBottom = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 950,
                SplitterWidth = 6,
                BackColor = Color.FromArgb(30, 41, 59)
            };
            splitBottom.SplitterMoved += (s, e) => SaveSettingsFromUi();

            // 下部左侧：微观 Tick 图表
            var pnlBottomLeft = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(15, 23, 42) };
            pnlTickHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 62,
                BackColor = Color.FromArgb(30, 41, 59),
                Padding = new Padding(8, 4, 8, 4)
            };
            lblTickTitle = new Label
            {
                Text = "微观逐笔 Tick",
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248),
                Location = new Point(10, 7),
                AutoSize = true
            };

            lblTickPeriod = new Label
            {
                Text = "周期:",
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(148, 163, 184),
                AutoSize = true
            };

            _tickPeriodButtons.Clear();
            _tickPeriodButtons.Add(CreateTickPeriodButton("全部", 0));
            _tickPeriodButtons.Add(CreateTickPeriodButton("1s", 1));
            _tickPeriodButtons.Add(CreateTickPeriodButton("5s", 2));
            _tickPeriodButtons.Add(CreateTickPeriodButton("15s", 3));
            _tickPeriodButtons.Add(CreateTickPeriodButton("30s", 4));
            _tickPeriodButtons.Add(CreateTickPeriodButton("1m", 5));
            _tickPeriodButtons.Add(CreateTickPeriodButton("3m", 6));
            _tickPeriodButtons.Add(CreateTickPeriodButton("5m", 7));
            _tickPeriodButtons.Add(CreateTickPeriodButton("15m", 8));
            _tickPeriodButtons.Add(CreateTickPeriodButton("30m", 9));
            _tickPeriodButtons.Add(CreateTickPeriodButton("1h", 10));

            _btnCustomSeconds = CreateTickPeriodButton("自定秒(5s)", 11);
            _tickPeriodButtons.Add(_btnCustomSeconds);

            numTickCustomSeconds = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 3600,
                Value = 5,
                Width = 46,
                Font = new Font("Microsoft YaHei", 8.0F, FontStyle.Bold),
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.FromArgb(56, 189, 248),
                Visible = false
            };
            numTickCustomSeconds.ValueChanged += (s, e) =>
            {
                if (_btnCustomSeconds != null)
                {
                    _btnCustomSeconds.Text = $"自定秒({numTickCustomSeconds.Value}s)";
                }
                SaveSettingsFromUi();
                if (_selectedTickPeriodIndex == 11)
                {
                    RefreshTickPlotDirectly();
                }
            };
            numTickCustomSeconds.Enter += (s, e) =>
            {
                if (_selectedTickPeriodIndex != 11)
                {
                    SelectTickPeriodButton(11);
                }
            };

            _btnCustomMinutes = CreateTickPeriodButton("自定分(5m)", 12);
            _tickPeriodButtons.Add(_btnCustomMinutes);

            numTickCustomMinutes = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 1440,
                Value = 5,
                Width = 46,
                Font = new Font("Microsoft YaHei", 8.0F, FontStyle.Bold),
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.FromArgb(56, 189, 248),
                Visible = false
            };
            numTickCustomMinutes.ValueChanged += (s, e) =>
            {
                if (_btnCustomMinutes != null)
                {
                    _btnCustomMinutes.Text = $"自定分({numTickCustomMinutes.Value}m)";
                }
                SaveSettingsFromUi();
                if (_selectedTickPeriodIndex == 12)
                {
                    RefreshTickPlotDirectly();
                }
            };
            numTickCustomMinutes.Enter += (s, e) =>
            {
                if (_selectedTickPeriodIndex != 12)
                {
                    SelectTickPeriodButton(12);
                }
            };

            UpdateTickPeriodButtonsUi();

            btnToggleTickChartType = new Button
            {
                Text = "切换为折线图",
                Font = new Font("Microsoft YaHei", 8.0F, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248),
                BackColor = Color.FromArgb(15, 23, 42),
                FlatStyle = FlatStyle.Flat,
                Size = new Size(95, 24),
                Cursor = Cursors.Hand
            };
            btnToggleTickChartType.FlatAppearance.BorderColor = Color.FromArgb(56, 189, 248);
            btnToggleTickChartType.Click += (s, e) =>
            {
                _settings.TickChartTypeIndex = _settings.TickChartTypeIndex == 0 ? 1 : 0;
                _selectedMicroBarAnchor = null;
                _selectedMicroBarStartIndex = null;
                _selectedMicroBarEndIndex = null;
                _tickAutoFollow = true;
                UpdateLiveFollowButtonState(isLive: !_selectedBarStartIndex.HasValue);
                if (tabDashboard != null) tabDashboard.Text = "实时仪表盘 & 逐笔流水";
                UpdateTickChartTypeButtonState();
                SaveSettingsFromUi();
                RefreshTickPlotDirectly();
            };

            lblTickBadge = new Label
            {
                Text = "当前无活动周期",
                Font = new Font("Microsoft YaHei", 8.5F),
                ForeColor = Color.FromArgb(148, 163, 184),
                Location = new Point(230, 7),
                AutoSize = true
            };

            lblTickRatioBadge = new Label
            {
                Text = "",
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(74, 222, 128),
                BackColor = Color.FromArgb(15, 23, 42),
                Padding = new Padding(4, 2, 4, 2),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(460, 5),
                AutoSize = true,
                Visible = false
            };

            lblVolRatioBadge = new Label
            {
                Text = "",
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(250, 204, 21),
                BackColor = Color.FromArgb(15, 23, 42),
                Padding = new Padding(4, 2, 4, 2),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(680, 5),
                AutoSize = true,
                Visible = false
            };

            btnRatioProximity = new Button
            {
                Text = "🎯 凑近比值: 开",
                Font = new Font("Microsoft YaHei", 8.0F, FontStyle.Bold),
                ForeColor = Color.FromArgb(241, 245, 249),
                BackColor = Color.FromArgb(2, 132, 199),
                FlatStyle = FlatStyle.Flat,
                Size = new Size(115, 24),
                Cursor = Cursors.Hand,
                Visible = true
            };
            btnRatioProximity.FlatAppearance.BorderSize = 0;
            btnRatioProximity.Click += (s, e) =>
            {
                _settings.RatioProximity = !_settings.RatioProximity;
                UpdateProximityButtonState();
                SaveSettingsFromUi();
                RefreshTickPlotDirectly();
            };

            btnResumeLiveFollow = new Button
            {
                Text = "🟢 实时播放中",
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(52, 211, 153),
                BackColor = Color.FromArgb(15, 23, 42),
                FlatStyle = FlatStyle.Flat,
                Size = new Size(100, 24),
                Cursor = Cursors.Default,
                Visible = true
            };
            btnResumeLiveFollow.FlatAppearance.BorderColor = Color.FromArgb(30, 41, 59);
            btnResumeLiveFollow.FlatAppearance.BorderSize = 1;
            btnResumeLiveFollow.Click += (s, e) =>
            {
                if (_selectedMicroBarStartIndex.HasValue || _selectedMicroBarEndIndex.HasValue)
                {
                    ClearMicroBarSelection();
                    return;
                }
                if (_selectedBarStartIndex.HasValue || _selectedBarEndIndex.HasValue || _selectedConsecutiveTrend != null)
                {
                    ClearBarSelection();
                }
                else
                {
                    _tickAutoFollow = true;
                    UpdateLiveFollowButtonState(isLive: true);
                    RefreshTickPlotDirectly();
                }
            };

            chkTickConsecutiveTrend = new CheckBox
            {
                Text = "大通道",
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(245, 158, 11), // 琥珀金
                AutoSize = true,
                Checked = _settings.ShowTickConsecutiveTrend,
                Cursor = Cursors.Hand
            };
            _toolTip.SetToolTip(chkTickConsecutiveTrend, "在微观视窗内投影大周期平行通道 (内部微观小通道已移除)");
            chkTickConsecutiveTrend.CheckedChanged += (s, e) =>
            {
                _settings.ShowTickConsecutiveTrend = chkTickConsecutiveTrend.Checked;
                SaveSettingsFromUi();
                RefreshTickPlotDirectly();
                RepositionTickHeaderControls();
            };

            chkTickChannel = new CheckBox
            {
                Text = "通道",
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(16, 185, 129), // 翡翠绿
                AutoSize = true,
                Checked = _settings.ShowTickChannel,
                Cursor = Cursors.Hand
            };
            _toolTip.SetToolTip(chkTickChannel, "在微观视窗内根据当前连续涨跌拟合并绘制微观专属通道 (独立控制与设置)");
            chkTickChannel.CheckedChanged += (s, e) =>
            {
                _settings.ShowTickChannel = chkTickChannel.Checked;
                lblTickConsecutiveBars.Enabled = chkTickChannel.Checked;
                numTickConsecutiveBars.Enabled = chkTickChannel.Checked;
                lblTickConsecutivePct.Enabled = chkTickChannel.Checked;
                numTickConsecutivePct.Enabled = chkTickChannel.Checked;
                SaveSettingsFromUi();
                RefreshTickPlotDirectly();
                RepositionTickHeaderControls();
            };

            lblTickConsecutiveBars = new Label
            {
                Text = "连:",
                Font = new Font("Microsoft YaHei", 8.0F, FontStyle.Regular),
                ForeColor = Color.FromArgb(148, 163, 184),
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Enabled = _settings.ShowTickChannel
            };
            _toolTip.SetToolTip(lblTickConsecutiveBars, "微观通道连续最小根数");

            numTickConsecutiveBars = new NumericUpDown
            {
                Minimum = 2,
                Maximum = 50,
                Value = Math.Clamp(_settings.TickConsecutiveMinBars, 2, 50),
                Width = 42,
                Font = new Font("Microsoft YaHei", 8.0F),
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.FromArgb(241, 245, 249),
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = HorizontalAlignment.Center,
                Enabled = _settings.ShowTickChannel
            };
            _toolTip.SetToolTip(numTickConsecutiveBars, "微观通道连续最少根数 (2~50 根，默认 5)");
            numTickConsecutiveBars.ValueChanged += (s, e) =>
            {
                _settings.TickConsecutiveMinBars = (int)numTickConsecutiveBars.Value;
                SaveSettingsFromUi();
                RefreshTickPlotDirectly();
            };

            lblTickConsecutivePct = new Label
            {
                Text = "幅%:",
                Font = new Font("Microsoft YaHei", 8.0F, FontStyle.Regular),
                ForeColor = Color.FromArgb(148, 163, 184),
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Enabled = _settings.ShowTickChannel
            };
            _toolTip.SetToolTip(lblTickConsecutivePct, "微观通道最小累计涨跌幅百分比 (%)");

            numTickConsecutivePct = new NumericUpDown
            {
                Minimum = 0.05m,
                Maximum = 50.0m,
                DecimalPlaces = 2,
                Increment = 0.1m,
                Value = Math.Clamp(_settings.TickConsecutiveMinPct, 0.05m, 50.0m),
                Width = 52,
                Font = new Font("Microsoft YaHei", 8.0F),
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.FromArgb(241, 245, 249),
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = HorizontalAlignment.Center,
                Enabled = _settings.ShowTickChannel
            };
            _toolTip.SetToolTip(numTickConsecutivePct, "微观通道连续累计最小涨跌幅百分比 (0.05%~50.00%，默认 0.80%)");
            numTickConsecutivePct.ValueChanged += (s, e) =>
            {
                _settings.TickConsecutiveMinPct = numTickConsecutivePct.Value;
                SaveSettingsFromUi();
                RefreshTickPlotDirectly();
            };

            chkTickAngleLines = new CheckBox
            {
                Text = "角度线",
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(168, 85, 247), // 科技紫
                AutoSize = true,
                Checked = _settings.ShowTickAngleLines,
                Cursor = Cursors.Hand
            };
            _toolTip.SetToolTip(chkTickAngleLines, "在微观 Tick 视窗内同频投影大周期高低多角度趋势线 (独立控制)");
            chkTickAngleLines.CheckedChanged += (s, e) =>
            {
                _settings.ShowTickAngleLines = chkTickAngleLines.Checked;
                SaveSettingsFromUi();
                RefreshTickPlotDirectly();
                RepositionTickHeaderControls();
            };

            chkTickRecentTrendLines = new CheckBox
            {
                Text = "300根线",
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(249, 115, 22), // 珊瑚橙
                AutoSize = true,
                Checked = _settings.ShowTickRecentTrendLines,
                Cursor = Cursors.Hand
            };
            _toolTip.SetToolTip(chkTickRecentTrendLines, "当出现连续涨跌后，提取最近300根小周期K线的高低点绘制动态趋势线 (微观Tick视窗显示)");
            chkTickRecentTrendLines.CheckedChanged += (s, e) =>
            {
                _settings.ShowTickRecentTrendLines = chkTickRecentTrendLines.Checked;
                SaveSettingsFromUi();
                RefreshTickPlotDirectly();
                RepositionTickHeaderControls();
            };

            chkShowTickRatio = new CheckBox
            {
                Text = "显示比值",
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248), // 亮天蓝
                AutoSize = true,
                Checked = _settings.ShowTickRatio,
                Cursor = Cursors.Hand
            };
            chkShowTickRatio.CheckedChanged += (s, e) =>
            {
                _settings.ShowTickRatio = chkShowTickRatio.Checked;
                btnRatioProximity.Visible = _settings.ShowTickRatio;
                lblTickRatioBadge.Visible = _settings.ShowTickRatio;
                lblVolRatioBadge.Visible = _settings.ShowTickRatio;
                SaveSettingsFromUi();
                RefreshTickPlotDirectly();
                RepositionTickHeaderControls();
            };

            lblTickTitle.SizeChanged += (s, e) => RepositionTickHeaderControls();
            lblTickBadge.SizeChanged += (s, e) => RepositionTickHeaderControls();
            chkTickConsecutiveTrend.SizeChanged += (s, e) => RepositionTickHeaderControls();
            chkTickChannel.SizeChanged += (s, e) => RepositionTickHeaderControls();
            lblTickConsecutiveBars.SizeChanged += (s, e) => RepositionTickHeaderControls();
            numTickConsecutiveBars.SizeChanged += (s, e) => RepositionTickHeaderControls();
            lblTickConsecutivePct.SizeChanged += (s, e) => RepositionTickHeaderControls();
            numTickConsecutivePct.SizeChanged += (s, e) => RepositionTickHeaderControls();
            chkTickAngleLines.SizeChanged += (s, e) => RepositionTickHeaderControls();
            chkTickRecentTrendLines.SizeChanged += (s, e) => RepositionTickHeaderControls();
            chkShowTickRatio.SizeChanged += (s, e) => RepositionTickHeaderControls();
            lblTickRatioBadge.SizeChanged += (s, e) => RepositionTickHeaderControls();
            lblVolRatioBadge.SizeChanged += (s, e) => RepositionTickHeaderControls();
            btnResumeLiveFollow.VisibleChanged += (s, e) => RepositionTickHeaderControls();
            pnlTickHeader.Resize += (s, e) => RepositionTickHeaderControls();

            var tickControls = new List<Control>
            {
                lblTickTitle,
                lblTickPeriod
            };
            tickControls.AddRange(_tickPeriodButtons);
            tickControls.Add(numTickCustomSeconds);
            tickControls.Add(numTickCustomMinutes);
            tickControls.Add(btnToggleTickChartType);
            tickControls.Add(chkTickConsecutiveTrend);
            tickControls.Add(chkTickChannel);
            tickControls.Add(lblTickConsecutiveBars);
            tickControls.Add(numTickConsecutiveBars);
            tickControls.Add(lblTickConsecutivePct);
            tickControls.Add(numTickConsecutivePct);
            tickControls.Add(chkTickAngleLines);
            tickControls.Add(chkTickRecentTrendLines);
            tickControls.Add(chkShowTickRatio);
            tickControls.Add(lblTickBadge);
            tickControls.Add(lblTickRatioBadge);
            tickControls.Add(lblVolRatioBadge);
            tickControls.Add(btnRatioProximity);
            tickControls.Add(btnResumeLiveFollow);

            pnlTickHeader.Controls.AddRange(tickControls.ToArray());

            formsPlotTick = new FormsPlot { Dock = DockStyle.Fill, BackColor = Color.FromArgb(15, 23, 42) };
            formsPlotTick.MouseDown += OnFormsPlotTickMouseDown;
            formsPlotTick.MouseUp += OnFormsPlotTickMouseUp;
            formsPlotTick.MouseMove += OnFormsPlotTickMouseMove;
            formsPlotTick.MouseLeave += OnFormsPlotTickMouseLeave;
            formsPlotTick.MouseWheel += OnFormsPlotTickMouseWheel;
            formsPlotTick.MouseDoubleClick += (s, e) =>
            {
                if (_selectedMicroBarStartIndex.HasValue || _selectedMicroBarEndIndex.HasValue)
                {
                    ClearMicroBarSelection();
                    return;
                }
                if (_selectedBarStartIndex.HasValue || _selectedBarEndIndex.HasValue || _selectedConsecutiveTrend != null)
                {
                    ClearBarSelection();
                }
                else
                {
                    _tickAutoFollow = true;
                    UpdateLiveFollowButtonState(isLive: true);
                    RefreshTickPlotDirectly();
                }
            };
            pnlBottomLeft.Controls.Add(formsPlotTick);
            pnlBottomLeft.Controls.Add(pnlTickHeader);
            splitBottom.Panel1.Controls.Add(pnlBottomLeft);

            // 下部右侧：Tab 页面 (Tab 1: 实时指标仪表盘与逐笔表格, Tab 2: 周期完成日志)
            tabControlRight = new TabControl { Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei", 8.5F) };
            tabDashboard = new TabPage("实时仪表盘 & 逐笔流水") { BackColor = Color.FromArgb(15, 23, 42) };
            tabLogs = new TabPage("运行日志与定型报告") { BackColor = Color.FromArgb(15, 23, 42) };

            SetupDashboardTab();
            SetupLogsTab();

            tabControlRight.TabPages.Add(tabDashboard);
            tabControlRight.TabPages.Add(tabLogs);
            splitBottom.Panel2.Controls.Add(tabControlRight);

            splitMain.Panel2.Controls.Add(splitBottom);

            this.Controls.Add(splitMain);
            this.Controls.Add(pnlTop);

            this.Load += (s, e) =>
            {
                ApplySettingsToUi();
                this.BeginInvoke(() =>
                {
                    try
                    {
                        if (_settings.SplitMainDistance > 100 && _settings.SplitMainDistance < splitMain.Height - 100)
                        {
                            splitMain.SplitterDistance = _settings.SplitMainDistance;
                        }
                        if (_settings.SplitBottomDistance > 100 && _settings.SplitBottomDistance < splitBottom.Width - 100)
                        {
                            splitBottom.SplitterDistance = _settings.SplitBottomDistance;
                        }
                    }
                    catch { }
                });
            };

            this.FormClosing += (s, e) =>
            {
                _engine.Stop();
                SaveSettingsFromUi();
            };
        }

        private void SetupDashboardTab()
        {
            var pnlCards = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.FromArgb(30, 41, 59),
                Padding = new Padding(6),
                AutoScroll = false
            };

            lblCardPeriod = CreateStatCard(pnlCards, "当前大周期跨度", "未开始", Color.FromArgb(250, 204, 21));
            lblCardTickProgress = CreateStatCard(pnlCards, "周期内 Tick 进度", "0 / 0 (0%)", Color.FromArgb(56, 189, 248));
            lblCardTickRatio = CreateStatCard(pnlCards, "Tick 多空比值", "1.00 (50%:50%)", Color.FromArgb(74, 222, 128));
            lblCardVolRatio = CreateStatCard(pnlCards, "成交量多空比", "1.00 (50%:50%)", Color.FromArgb(250, 204, 21));
            lblCardMacroCount = CreateStatCard(pnlCards, "大图已定型 K 线", "0 根", Color.FromArgb(74, 222, 128));
            lblCardFormingPrice = CreateStatCard(pnlCards, "形成态现价与涨跌", "--", Color.FromArgb(244, 114, 182));
            lblCardTps = CreateStatCard(pnlCards, "回放吞吐率", "0 TPS", Color.FromArgb(226, 232, 240));

            dgvRecentTicks = new DataGridView
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
                Font = new Font("Consolas", 8.5F)
            };

            dgvRecentTicks.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(30, 41, 59);
            dgvRecentTicks.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(56, 189, 248);
            dgvRecentTicks.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold);
            dgvRecentTicks.DefaultCellStyle.BackColor = Color.FromArgb(15, 23, 42);
            dgvRecentTicks.DefaultCellStyle.ForeColor = Color.FromArgb(241, 245, 249);
            dgvRecentTicks.DefaultCellStyle.SelectionBackColor = Color.FromArgb(30, 58, 138);

            dgvRecentTicks.Columns.Add("ColIdx", "#");
            dgvRecentTicks.Columns.Add("ColTime", "时间");
            dgvRecentTicks.Columns.Add("ColPrice", "成交价 (USDT)");
            dgvRecentTicks.Columns.Add("ColQty", "成交量");
            dgvRecentTicks.Columns.Add("ColQuote", "成交额 (USDT)");
            dgvRecentTicks.Columns.Add("ColSide", "主动方向");

            dgvRecentTicks.Columns[0].Width = 45;
            dgvRecentTicks.Columns[1].Width = 90;
            dgvRecentTicks.Columns[2].Width = 95;
            dgvRecentTicks.Columns[3].Width = 85;
            dgvRecentTicks.Columns[4].Width = 95;
            dgvRecentTicks.Columns[5].Width = 70;

            tabDashboard.Controls.Add(dgvRecentTicks);
            tabDashboard.Controls.Add(pnlCards);
        }

        private void SetupLogsTab()
        {
            var pnlLogHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 32,
                BackColor = Color.FromArgb(30, 41, 59)
            };
            var lblLTitle = new Label
            {
                Text = "系统运行与大周期定型日志看板",
                ForeColor = Color.FromArgb(226, 232, 240),
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold),
                Location = new Point(10, 7),
                AutoSize = true
            };
            chkVerboseLog = new CheckBox
            {
                Text = "详细定型日志",
                ForeColor = Color.FromArgb(148, 163, 184),
                Font = new Font("Microsoft YaHei", 8.5F),
                Location = new Point(pnlLogHeader.Width - 190, 5),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                AutoSize = true,
                Checked = _settings.VerboseLog,
                Cursor = Cursors.Hand
            };
            chkVerboseLog.CheckedChanged += (s, e) =>
            {
                _engine.VerboseLog = chkVerboseLog.Checked;
                _settings.VerboseLog = chkVerboseLog.Checked;
                SaveSettingsFromUi();
            };

            btnClearLogs = new Button
            {
                Text = "清空日志",
                Size = new Size(70, 22),
                Location = new Point(pnlLogHeader.Width - 80, 5),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                BackColor = Color.FromArgb(51, 65, 85),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnClearLogs.FlatAppearance.BorderSize = 0;
            btnClearLogs.Click += (s, e) => txtLogs.Clear();
            pnlLogHeader.Controls.AddRange(new Control[] { lblLTitle, chkVerboseLog, btnClearLogs });

            txtLogs = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(11, 15, 25),
                ForeColor = Color.FromArgb(241, 245, 249),
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9F),
                ReadOnly = true
            };

            tabLogs.Controls.Add(txtLogs);
            tabLogs.Controls.Add(pnlLogHeader);
        }

        private static Label CreateStatCard(Control parent, string title, string initialValue, Color accentColor)
        {
            var pnl = new Panel
            {
                Size = new Size(125, 42),
                BackColor = Color.FromArgb(15, 23, 42),
                Margin = new Padding(2),
                Padding = new Padding(4, 2, 4, 2)
            };
            var lblTitle = new Label
            {
                Text = title,
                ForeColor = Color.FromArgb(148, 163, 184),
                Font = new Font("Microsoft YaHei", 7.5F),
                Location = new Point(4, 2),
                AutoSize = true
            };
            var lblValue = new Label
            {
                Text = initialValue,
                ForeColor = accentColor,
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold),
                Location = new Point(4, 18),
                AutoSize = true
            };
            pnl.Controls.AddRange(new Control[] { lblTitle, lblValue });
            parent.Controls.Add(pnl);
            return lblValue;
        }

        private static Label CreateLabel(string text, int x, int y)
        {
            return new Label
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = true,
                ForeColor = Color.FromArgb(226, 232, 240),
                Font = new Font("Microsoft YaHei", 8.5F)
            };
        }

        private static Button CreateButton(string text, int x, int y, int width, int height, Color bg)
        {
            var btn = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(width, height),
                BackColor = bg,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }

        private void ApplyDarkTheme()
        {
            this.BackColor = Color.FromArgb(15, 23, 42);
            this.ForeColor = Color.FromArgb(241, 245, 249);
        }

        #endregion

        #region 数据分批载入与切片

        private async Task LoadFirstBatchAsync()
        {
            if (_isLoading || _isBatchLoading) return;
            SaveSettingsFromUi();
            _isLoading = true;
            btnLoad.Enabled = false;
            btnNextBatch.Enabled = false;

            ClearBarSelection();
            _engine.Stop();
            _hasConsecutiveTrendTriggered = false;
            _latestDynamicTrendLines = null;
            _lastEvaluatedTotalTicks = -1;
            _lastEvaluatedTrendsCount = -1;
            _lastEvaluatedEndIndex = -1;
            _persistentRetainedLines.Clear();
            _channelTrendLineRecords.Clear();

            string coin = cboCoin.SelectedItem?.ToString() ?? "BTCUSDT";
            DateTime startDate = dtpStart.Value.Date;
            DateTime endDate = dtpEnd.Value.Date;

            if (startDate > endDate)
            {
                MessageBox.Show("起始日期不能大于截止日期！", "参数错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _isLoading = false;
                btnLoad.Enabled = true;
                btnNextBatch.Enabled = false;
                return;
            }

            // 1. 构建本次计划回放的日期批次列表
            _batchDates.Clear();
            for (DateTime d = startDate; d <= endDate; d = d.AddDays(1))
            {
                _batchDates.Add(d);
            }
            _nextBatchDateIndex = 0;

            TimeSpan periodSpan = GetSelectedPeriodSpan();
            _loadCts = new CancellationTokenSource();
            var ct = _loadCts.Token;

            prgOverall.Value = 0;
            prgBucket.Value = 0;

            try
            {
                DateTime firstDate = _batchDates[0];
                AppendLog($"[首批加载] 正在按天分批加载 {coin} 第 1 批数据 ({firstDate:yyyy-MM-dd}，总区间计划 {_batchDates.Count} 天)...", Color.FromArgb(56, 189, 248));

                var firstBatchBuckets = await PeriodBucketLoader.LoadSingleDayBatchAsync(
                    coin,
                    firstDate,
                    periodSpan,
                    startingBucketIndex: 0,
                    (msg, pct) =>
                    {
                        this.BeginInvoke(() =>
                        {
                            lblMacroBadge.Text = msg;
                        });
                    },
                    ct);

                if (firstBatchBuckets.Count == 0)
                {
                    MessageBox.Show($"未在 {firstDate:yyyy-MM-dd} 找到有效 Tick 数据，请确认本地是否存在该日期的 Parquet 文件！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                _engine.LoadBuckets(firstBatchBuckets);
                _nextBatchDateIndex = 1;

                UpdateBatchStatusUi();

                AppendLog($"[首批就绪] 成功载入第 1 批 ({firstDate:yyyy-MM-dd})，构建 {firstBatchBuckets.Count:N0} 个 {GetSelectedPeriodTitle()} 周期桶 (共 {firstBatchBuckets.Sum(b => b.TickCount):N0} Ticks)！无需等待后续批次，点击「播放」即可即刻开始回放。", Color.FromArgb(74, 222, 128));

                if (_batchDates.Count > 1)
                {
                    AppendLog($"[分批机制] 本次计划共有 {_batchDates.Count} 天数据。播放推进时将自动在后台流式预加载后续批次（也可随时点击「载入下批」手动追加）。", Color.FromArgb(56, 189, 248));
                }

                lblTickBadge.Text = $"准备就绪 (第 1 周期: {FormatTimeSpanRange(firstBatchBuckets[0].StartTime, firstBatchBuckets[0].EndTime)}, 共 {firstBatchBuckets[0].TickCount:N0} Ticks)";

                _macroPlotNeedsRefresh = true;
                _tickPlotNeedsRefresh = true;
            }
            catch (OperationCanceledException)
            {
                AppendLog("[加载取消] 用户取消了分批数据载入。", Color.FromArgb(250, 204, 21));
            }
            catch (Exception ex)
            {
                AppendLog($"❌ [加载失败] {ex.Message}", Color.FromArgb(248, 113, 113));
                MessageBox.Show($"载入失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _isLoading = false;
                btnLoad.Enabled = true;
                btnNextBatch.Enabled = _nextBatchDateIndex < _batchDates.Count;
            }
        }

        private async Task<bool> LoadNextBatchAsync(bool isBackgroundPreload = false)
        {
            if (_nextBatchDateIndex >= _batchDates.Count) return false;

            await _batchLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_nextBatchDateIndex >= _batchDates.Count) return false;
                _isBatchLoading = true;

                string coin = "BTCUSDT";
                TimeSpan periodSpan = TimeSpan.FromMinutes(30);

                if (this.IsHandleCreated)
                {
                    this.Invoke(() =>
                    {
                        coin = cboCoin.SelectedItem?.ToString() ?? "BTCUSDT";
                        periodSpan = GetSelectedPeriodSpan();
                        if (!isBackgroundPreload) btnNextBatch.Enabled = false;
                    });
                }
                else
                {
                    coin = _settings.Coin;
                    periodSpan = GetSelectedPeriodSpan();
                }

                DateTime nextDate = _batchDates[_nextBatchDateIndex];
                int currentBatchNo = _nextBatchDateIndex + 1;
                int totalBatches = _batchDates.Count;

                string loadDesc = isBackgroundPreload ? "后台流式预加载" : "手动加载";
                AppendLog($"[分批追加] 正在{loadDesc}第 {currentBatchNo}/{totalBatches} 批数据 ({nextDate:yyyy-MM-dd})...", Color.FromArgb(56, 189, 248));

                int startBucketIdx = _engine.TotalBuckets;
                var nextBuckets = await PeriodBucketLoader.LoadSingleDayBatchAsync(
                    coin,
                    nextDate,
                    periodSpan,
                    startingBucketIndex: startBucketIdx,
                    (msg, pct) =>
                    {
                        if (!isBackgroundPreload && this.IsHandleCreated)
                        {
                            this.BeginInvoke(() => lblMacroBadge.Text = msg);
                        }
                    });

                _nextBatchDateIndex++;

                if (nextBuckets.Count > 0)
                {
                    _engine.AppendBuckets(nextBuckets);
                    AppendLog($"✅ [分批已追加] 第 {currentBatchNo}/{totalBatches} 批 ({nextDate:yyyy-MM-dd}) 追加完成！新增 {nextBuckets.Count:N0} 个周期桶 (共 {nextBuckets.Sum(b => b.TickCount):N0} Ticks)，累计已构建 {_engine.TotalBuckets:N0} 桶。", Color.FromArgb(74, 222, 128));
                    _macroPlotNeedsRefresh = true;
                }
                else
                {
                    AppendLog($"[分批跳过] 第 {currentBatchNo}/{totalBatches} 批 ({nextDate:yyyy-MM-dd}) 未读取到有效数据。", Color.FromArgb(250, 204, 21));
                }

                if (this.IsHandleCreated)
                {
                    this.BeginInvoke(() => UpdateBatchStatusUi());
                }
                return nextBuckets.Count > 0;
            }
            catch (Exception ex)
            {
                AppendLog($"[分批加载异常] {ex.Message}", Color.FromArgb(248, 113, 113));
                return false;
            }
            finally
            {
                _isBatchLoading = false;
                if (this.IsHandleCreated)
                {
                    this.BeginInvoke(() =>
                    {
                        btnNextBatch.Enabled = _nextBatchDateIndex < _batchDates.Count;
                    });
                }
                _batchLock.Release();
            }
        }

        private void UpdateBatchStatusUi()
        {
            int total = Math.Max(1, _batchDates.Count);
            int current = Math.Min(total, _nextBatchDateIndex);
            lblBatchBadge.Text = $"批次: {current}/{total} 天";
            int pct = (int)((double)current / total * 100.0);
            prgOverall.Value = Math.Clamp(pct, 0, 100);

            if (_nextBatchDateIndex >= _batchDates.Count)
            {
                btnNextBatch.Text = "已全载入";
                btnNextBatch.Enabled = false;
            }
            else
            {
                btnNextBatch.Text = $"载入下批 ({_batchDates[_nextBatchDateIndex]:MM-dd})";
                btnNextBatch.Enabled = true;
            }

            lblMacroBadge.Text = $"{cboCoin.Text} | {GetSelectedPeriodTitle()} | 批次: {current}/{total} 天 (已载入 {_engine.TotalBuckets} 桶)";
            lblMacroBadge.Left = lblMacroTitle.Right + 15;
        }

        private TimeSpan GetSelectedPeriodSpan()
        {
            return cboPeriod.SelectedIndex switch
            {
                0 => TimeSpan.FromMinutes(30), // 默认 30 分钟
                1 => TimeSpan.FromMinutes(15),
                2 => TimeSpan.FromMinutes(5),
                3 => TimeSpan.FromMinutes(3),
                4 => TimeSpan.FromMinutes(1),
                5 => TimeSpan.FromHours(1),
                6 => TimeSpan.FromHours(2),
                7 => TimeSpan.FromHours(4),
                8 => TimeSpan.FromDays(1),
                9 => TimeSpan.FromMinutes(Math.Max(1, (double)numCustomMinutes.Value)),
                10 => TimeSpan.FromSeconds(Math.Max(1, (double)numCustomSeconds.Value)),
                11 => TimeSpan.FromSeconds(30),
                12 => TimeSpan.FromSeconds(15),
                13 => TimeSpan.FromSeconds(5),
                14 => TimeSpan.FromSeconds(1),
                _ => TimeSpan.FromMinutes(30)
            };
        }

        private string GetSelectedPeriodTitle()
        {
            return cboPeriod.SelectedIndex switch
            {
                0 => "30分钟",
                1 => "15分钟",
                2 => "5分钟",
                3 => "3分钟",
                4 => "1分钟",
                5 => "1小时",
                6 => "2小时",
                7 => "4小时",
                8 => "1天",
                9 => $"{numCustomMinutes.Value}分钟",
                10 => $"{numCustomSeconds.Value}秒",
                11 => "30秒",
                12 => "15秒",
                13 => "5秒",
                14 => "1秒",
                _ => "30分钟"
            };
        }

        private string FormatTimeSpanRange(DateTime start, DateTime end, bool includeDate = false)
        {
            var span = GetSelectedPeriodSpan();
            string timeFmt = span.TotalMinutes < 1 ? "HH:mm:ss" : "HH:mm";
            string datePrefix = includeDate ? start.ToString("MM-dd ") : "";
            return $"{datePrefix}{start.ToString(timeFmt)} ~ {end.ToString(timeFmt)}";
        }

        private Button CreateTickPeriodButton(string text, int index)
        {
            var btn = new Button
            {
                Text = text,
                Font = new Font("Microsoft YaHei", 8.0F, FontStyle.Bold),
                ForeColor = Color.FromArgb(148, 163, 184),
                BackColor = Color.FromArgb(15, 23, 42),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Height = 24,
                AutoSize = true,
                Padding = new Padding(3, 0, 3, 0),
                Margin = new Padding(1, 0, 1, 0)
            };
            btn.FlatAppearance.BorderSize = 1;
            btn.FlatAppearance.BorderColor = Color.FromArgb(51, 65, 85);
            btn.Click += (s, e) => SelectTickPeriodButton(index);
            return btn;
        }

        private void SelectTickPeriodButton(int index)
        {
            if (index < 0 || index >= _tickPeriodButtons.Count) index = 0;
            _selectedTickPeriodIndex = index;
            _selectedMicroBarAnchor = null;
            _selectedMicroBarStartIndex = null;
            _selectedMicroBarEndIndex = null;
            _tickAutoFollow = true;
            UpdateLiveFollowButtonState(isLive: !_selectedBarStartIndex.HasValue);
            if (tabDashboard != null) tabDashboard.Text = "实时仪表盘 & 逐笔流水";
            UpdateTickPeriodButtonsUi();
            RepositionTickHeaderControls();
            SaveSettingsFromUi();
            RefreshTickPlotDirectly();
        }

        private void UpdateTickPeriodButtonsUi()
        {
            for (int i = 0; i < _tickPeriodButtons.Count; i++)
            {
                var btn = _tickPeriodButtons[i];
                bool isSelected = (i == _selectedTickPeriodIndex);
                if (isSelected)
                {
                    btn.BackColor = Color.FromArgb(2, 132, 199);
                    btn.ForeColor = Color.White;
                    btn.FlatAppearance.BorderColor = Color.FromArgb(56, 189, 248);
                }
                else
                {
                    btn.BackColor = Color.FromArgb(15, 23, 42);
                    btn.ForeColor = Color.FromArgb(148, 163, 184);
                    btn.FlatAppearance.BorderColor = Color.FromArgb(51, 65, 85);
                }
            }

            if (numTickCustomSeconds != null)
            {
                numTickCustomSeconds.Visible = (_selectedTickPeriodIndex == 11);
            }
            if (numTickCustomMinutes != null)
            {
                numTickCustomMinutes.Visible = (_selectedTickPeriodIndex == 12);
            }

            if (_btnCustomSeconds != null && numTickCustomSeconds != null)
            {
                _btnCustomSeconds.Text = $"自定秒({numTickCustomSeconds.Value}s)";
            }
            if (_btnCustomMinutes != null && numTickCustomMinutes != null)
            {
                _btnCustomMinutes.Text = $"自定分({numTickCustomMinutes.Value}m)";
            }
        }

        private TimeSpan? GetSelectedTickPeriodSpan()
        {
            return _selectedTickPeriodIndex switch
            {
                0 => null, // 全部 (当前大周期)
                1 => TimeSpan.FromSeconds(1),
                2 => TimeSpan.FromSeconds(5),
                3 => TimeSpan.FromSeconds(15),
                4 => TimeSpan.FromSeconds(30),
                5 => TimeSpan.FromMinutes(1),
                6 => TimeSpan.FromMinutes(3),
                7 => TimeSpan.FromMinutes(5),
                8 => TimeSpan.FromMinutes(15),
                9 => TimeSpan.FromMinutes(30),
                10 => TimeSpan.FromHours(1),
                11 => TimeSpan.FromSeconds(Math.Max(1, (double)numTickCustomSeconds.Value)),
                12 => TimeSpan.FromMinutes(Math.Max(1, (double)numTickCustomMinutes.Value)),
                _ => null
            };
        }

        private string GetSelectedTickPeriodTitle()
        {
            return _selectedTickPeriodIndex switch
            {
                0 => "全部",
                1 => "1秒",
                2 => "5秒",
                3 => "15秒",
                4 => "30秒",
                5 => "1分钟",
                6 => "3分钟",
                7 => "5分钟",
                8 => "15分钟",
                9 => "30分钟",
                10 => "1小时",
                11 => $"{numTickCustomSeconds.Value}秒",
                12 => $"{numTickCustomMinutes.Value}分钟",
                _ => "全部"
            };
        }

        private void UpdateTickChartTypeButtonState()
        {
            if (btnToggleTickChartType == null) return;
            if (_settings.TickChartTypeIndex == 1)
            {
                btnToggleTickChartType.Text = "📈 折线图 (切蜡烛)";
                btnToggleTickChartType.BackColor = Color.FromArgb(59, 130, 246);
            }
            else
            {
                btnToggleTickChartType.Text = "🕯️ 蜡烛图 (切折线)";
                btnToggleTickChartType.BackColor = Color.FromArgb(16, 185, 129);
            }
        }

        private void RepositionTickHeaderControls()
        {
            if (lblTickTitle == null || pnlTickHeader == null) return;

            // ==================== Row 1: 功能操作与状态栏 (y = 5) ====================
            lblTickTitle.Location = new Point(10, 8);

            int row1LeftX = lblTickTitle.Right + 8;
            if (btnToggleTickChartType != null)
            {
                btnToggleTickChartType.Location = new Point(row1LeftX, 5);
                row1LeftX = btnToggleTickChartType.Right + 8;
            }

            if (chkTickConsecutiveTrend != null)
            {
                chkTickConsecutiveTrend.Location = new Point(row1LeftX, 7);
                row1LeftX = chkTickConsecutiveTrend.Right + 8;
            }

            if (chkTickChannel != null)
            {
                chkTickChannel.Location = new Point(row1LeftX, 7);
                row1LeftX = chkTickChannel.Right + 4;
            }

            if (lblTickConsecutiveBars != null)
            {
                lblTickConsecutiveBars.Location = new Point(row1LeftX, 9);
                row1LeftX = lblTickConsecutiveBars.Right + 2;
            }

            if (numTickConsecutiveBars != null)
            {
                numTickConsecutiveBars.Location = new Point(row1LeftX, 6);
                row1LeftX = numTickConsecutiveBars.Right + 6;
            }

            if (lblTickConsecutivePct != null)
            {
                lblTickConsecutivePct.Location = new Point(row1LeftX, 9);
                row1LeftX = lblTickConsecutivePct.Right + 2;
            }

            if (numTickConsecutivePct != null)
            {
                numTickConsecutivePct.Location = new Point(row1LeftX, 6);
                row1LeftX = numTickConsecutivePct.Right + 8;
            }

            if (chkTickAngleLines != null)
            {
                chkTickAngleLines.Location = new Point(row1LeftX, 7);
                row1LeftX = chkTickAngleLines.Right + 8;
            }

            if (chkTickRecentTrendLines != null)
            {
                chkTickRecentTrendLines.Location = new Point(row1LeftX, 7);
                row1LeftX = chkTickRecentTrendLines.Right + 8;
            }

            if (chkShowTickRatio != null)
            {
                chkShowTickRatio.Location = new Point(row1LeftX, 7);
                row1LeftX = chkShowTickRatio.Right + 8;
            }

            if (btnRatioProximity != null && btnRatioProximity.Visible)
            {
                btnRatioProximity.Location = new Point(row1LeftX, 5);
                row1LeftX = btnRatioProximity.Right + 8;
            }

            if (btnResumeLiveFollow != null && btnResumeLiveFollow.Visible)
            {
                btnResumeLiveFollow.Location = new Point(row1LeftX, 5);
                row1LeftX = btnResumeLiveFollow.Right + 8;
            }

            // Row 1 右侧徽章从右向左对齐
            int row1RightEdge = pnlTickHeader.ClientSize.Width - 10;
            if (lblVolRatioBadge != null && lblVolRatioBadge.Visible)
            {
                lblVolRatioBadge.Location = new Point(row1RightEdge - lblVolRatioBadge.Width, 5);
                row1RightEdge = lblVolRatioBadge.Left - 6;
            }
            if (lblTickRatioBadge != null && lblTickRatioBadge.Visible)
            {
                lblTickRatioBadge.Location = new Point(row1RightEdge - lblTickRatioBadge.Width, 5);
                row1RightEdge = lblTickRatioBadge.Left - 8;
            }

            // 周期进度徽章放在 Row 1 中间剩余空间，绝不遮挡左右按钮与比值徽章
            if (lblTickBadge != null)
            {
                int badgeLeft = row1LeftX + 6;
                int maxBadgeWidth = Math.Max(50, row1RightEdge - badgeLeft);
                lblTickBadge.Location = new Point(badgeLeft, 7);
                lblTickBadge.MaximumSize = new Size(maxBadgeWidth, 22);
                lblTickBadge.AutoEllipsis = true;
                lblTickBadge.Visible = row1RightEdge > badgeLeft + 30;
            }

            // ==================== Row 2: 周期一排全展示 (y = 33) ====================
            lblTickPeriod.Location = new Point(10, 36);

            int flowX2 = lblTickPeriod.Right + 6;
            for (int i = 0; i < _tickPeriodButtons.Count; i++)
            {
                var btn = _tickPeriodButtons[i];
                btn.Location = new Point(flowX2, 33);
                flowX2 = btn.Right + 2;

                if (i == 11 && numTickCustomSeconds != null && numTickCustomSeconds.Visible)
                {
                    numTickCustomSeconds.Location = new Point(flowX2, 34);
                    flowX2 = numTickCustomSeconds.Right + 3;
                }
                else if (i == 12 && numTickCustomMinutes != null && numTickCustomMinutes.Visible)
                {
                    numTickCustomMinutes.Location = new Point(flowX2, 34);
                    flowX2 = numTickCustomMinutes.Right + 3;
                }
            }
        }

        /// <summary>
        /// 评估并管理基于通道信号的 300 根小周期 K 线高低点与动态趋势线
        /// 核心规则：
        /// 1. 只有在通道信号产生后才进行趋势线绘制 (通道信号产生前绝不画趋势线)
        /// 2. 每个通道产生的趋势线均独立保留并在后续回放中持续观察
        /// </summary>
        private RecentSubPeriodTrendLineResult? EvaluateRecentDynamicTrendLines()
        {
            if (!_settings.ShowMacroRecentTrendLines && !_settings.ShowTickRecentTrendLines)
            {
                return null;
            }

            var completed = _engine.GetCompletedMacroBarsSnapshot();
            var forming = _engine.CurrentFormingBar;
            var trends = MacroConsecutiveTrendDetector.ScanTrends(
                completed,
                forming,
                _settings.ConsecutiveMinBars,
                _settings.ConsecutiveMinPct);

            // 🌟 核心要求 1：只有在通道信号产生后才进行趋势线绘制
            // 若当前尚未产生任何通道信号，且历史记录为空，绝不绘制任何趋势线
            if (trends.Count == 0 && _channelTrendLineRecords.Count == 0)
            {
                _hasConsecutiveTrendTriggered = false;
                _latestDynamicTrendLines = null;
                return null;
            }

            var subSpan = GetSelectedTickPeriodSpan() ?? TimeSpan.FromMinutes(1);
            if (_lastTrendSubPeriodSpan != subSpan)
            {
                _lastTrendSubPeriodSpan = subSpan;
                _channelTrendLineRecords.Clear();
                _lastEvaluatedTrendsCount = -1;
                _lastEvaluatedEndIndex = -1;
            }

            int activeEndIndex = trends.Count > 0 ? trends[^1].EndIndex : -1;
            long curTotalTicks = _engine.TotalTicksPlayed;

            bool needsUpdate = false;
            // 通道数量增加 (产生新通道信号)
            if (trends.Count != _lastEvaluatedTrendsCount)
            {
                needsUpdate = true;
            }
            // 存在尚未记录生成趋势线的通道
            else if (trends.Any(t => !_channelTrendLineRecords.ContainsKey(t.Id)))
            {
                needsUpdate = true;
            }
            // 最新通道仍在活跃形成中，随 K 线推进或经过一定 Tick 量动态精细更新
            else if (trends.Count > 0 && trends[^1].IsActive &&
                     (activeEndIndex != _lastEvaluatedEndIndex || Math.Abs(curTotalTicks - _lastEvaluatedTotalTicks) >= 30))
            {
                needsUpdate = true;
            }

            if (!needsUpdate && _latestDynamicTrendLines != null)
            {
                return _latestDynamicTrendLines;
            }

            _lastEvaluatedTrendsCount = trends.Count;
            _lastEvaluatedEndIndex = activeEndIndex;
            _lastEvaluatedTotalTicks = curTotalTicks;

            // 🌟 核心要求 2：每个通道产生的趋势线均需要保留以便后续观察
            int chIndex = 1;
            foreach (var tr in trends)
            {
                if (!_channelTrendLineRecords.TryGetValue(tr.Id, out var existingRecord))
                {
                    // 为新确立的通道生成 300 根小周期动态趋势线记录
                    var rec = RecentSubPeriodTrendLineDetector.GenerateForChannel(
                        tr,
                        chIndex,
                        _engine.Buckets,
                        _engine.CurrentBucketIndex,
                        _engine.CurrentTickIndex,
                        subSpan,
                        maxSmallBars: 300);

                    if (rec != null)
                    {
                        _channelTrendLineRecords[tr.Id] = rec;
                    }
                }
                else if (tr.IsActive)
                {
                    // 活跃通道推进中更新其最新高低点与趋势线
                    var updatedRec = RecentSubPeriodTrendLineDetector.GenerateForChannel(
                        tr,
                        existingRecord.ChannelIndex,
                        _engine.Buckets,
                        _engine.CurrentBucketIndex,
                        _engine.CurrentTickIndex,
                        subSpan,
                        maxSmallBars: 300);

                    if (updatedRec != null)
                    {
                        _channelTrendLineRecords[tr.Id] = updatedRec;
                    }
                }
                chIndex++;
            }

            if (_channelTrendLineRecords.Count == 0)
            {
                _hasConsecutiveTrendTriggered = false;
                _latestDynamicTrendLines = null;
                return null;
            }

            _hasConsecutiveTrendTriggered = true;

            // 汇总所有已保留通道的趋势线与高低极值点，供图表全面渲染与前向延伸观察
            var allSelected = new List<TrendLine>();
            var allPeaks = new List<PivotPoint>();
            var allValleys = new List<PivotPoint>();
            var allRes = new List<TrendLine>();
            var allSup = new List<TrendLine>();

            foreach (var rec in _channelTrendLineRecords.Values.OrderBy(r => r.ChannelIndex))
            {
                if (rec.SelectedLines != null && rec.SelectedLines.Count > 0)
                {
                    allSelected.AddRange(rec.SelectedLines);
                }
                if (rec.Peaks != null && rec.Peaks.Count > 0)
                {
                    allPeaks.AddRange(rec.Peaks);
                }
                if (rec.Valleys != null && rec.Valleys.Count > 0)
                {
                    allValleys.AddRange(rec.Valleys);
                }
                if (rec.ResistanceLines != null && rec.ResistanceLines.Count > 0)
                {
                    allRes.AddRange(rec.ResistanceLines);
                }
                if (rec.SupportLines != null && rec.SupportLines.Count > 0)
                {
                    allSup.AddRange(rec.SupportLines);
                }
            }

            _latestDynamicTrendLines = new RecentSubPeriodTrendLineResult
            {
                SubPeriodSpan = subSpan,
                SelectedLines = allSelected,
                Peaks = allPeaks,
                Valleys = allValleys,
                ResistanceLines = allRes,
                SupportLines = allSup,
                ChannelRecords = _channelTrendLineRecords.Values.ToList()
            };

            return _latestDynamicTrendLines;
        }

        private void RenderLiveTickPlot()
        {
            var curBucket = _engine.CurrentBucket;
            if (curBucket == null || curBucket.Ticks.Count == 0) return;

            int cursor = Math.Min(curBucket.Ticks.Count, _engine.CurrentTickIndex);
            var span = GetSelectedTickPeriodSpan();
            string periodLabel = GetSelectedTickPeriodTitle();
            var displayType = (MacroChartDisplayType)Math.Clamp(_settings.TickChartTypeIndex, 0, 1);

            string customTickTitle = span.HasValue
                ? $"微观周期 [{periodLabel}]"
                : "微观逐笔走势";

            lblTickTitle.Text = customTickTitle;
            lblTickBadge.Text = $"周期 #{_engine.CurrentBucketIndex + 1}/{_engine.TotalBuckets} | {FormatTimeSpanRange(curBucket.StartTime, curBucket.EndTime)} ({cursor:N0}/{curBucket.Ticks.Count:N0} T)";
            lblTickBadge.ForeColor = Color.FromArgb(148, 163, 184);

            var stats = TickLongShortStats.Calculate(curBucket.Ticks, cursor);
            string tR = stats.TickRatio >= 999.0 ? "∞" : stats.TickRatio.ToString("F2");
            string vR = stats.VolumeRatio >= 999.0 ? "∞" : stats.VolumeRatio.ToString("F2");

            if (cursor == 0)
            {
                lblTickRatioBadge.Text = "🎯 Tick比: --";
                lblTickRatioBadge.ForeColor = Color.FromArgb(148, 163, 184);
                lblVolRatioBadge.Text = "📊 量比: --";
                lblVolRatioBadge.ForeColor = Color.FromArgb(148, 163, 184);
            }
            else
            {
                lblTickRatioBadge.Text = $"🎯 Tick比: {tR} ({stats.BuyTickPct:F0}%:{stats.SellTickPct:F0}%)";
                lblTickRatioBadge.ForeColor = stats.TickRatio >= 1.0 ? Color.FromArgb(74, 222, 128) : Color.FromArgb(248, 113, 113);
                lblVolRatioBadge.Text = $"📊 量比: {vR} ({stats.BuyVolumePct:F0}%:{stats.SellVolumePct:F0}%)";
                lblVolRatioBadge.ForeColor = stats.VolumeRatio >= 1.0 ? Color.FromArgb(74, 222, 128) : Color.FromArgb(248, 113, 113);
            }
            lblTickRatioBadge.Visible = _settings.ShowTickRatio;
            lblVolRatioBadge.Visible = _settings.ShowTickRatio;
            _lastDefaultTickBadgeText = lblTickBadge.Text;
            _lastDefaultTickBadgeColor = lblTickBadge.ForeColor;
            RepositionTickHeaderControls();

            MacroConsecutiveTrendItem? liveMacroTrend = null;
            List<MacroConsecutiveTrendItem>? liveMacroChannels = null;
            if (_settings.ShowTickConsecutiveTrend || _settings.ShowTickAngleLines)
            {
                var completedBars = _engine.GetCompletedMacroBarsSnapshot();
                var formingBar = _engine.CurrentFormingBar;
                var trends = MacroConsecutiveTrendDetector.ScanTrends(
                    completedBars,
                    formingBar,
                    _settings.ConsecutiveMinBars,
                    _settings.ConsecutiveMinPct);

                int curIdx = _engine.CurrentBucketIndex;
                int forwardBars = Math.Max(20, _settings.ChannelExtensionBars);
                var matched = trends.Where(tr =>
                {
                    if (tr.MacroSlope45 <= 0 && MacroPlotHelper.LastSlope45 > 0) tr.MacroSlope45 = MacroPlotHelper.LastSlope45;
                    if (!_settings.ShowTickAngleLines && !tr.HasChannel) return false;
                    return curIdx >= tr.StartIndex; // 🌟 且保留：所有历史通道持续在微观视窗中保留并前向投影
                }).ToList();

                if (matched.Count > 0)
                {
                    liveMacroChannels = matched;
                    liveMacroTrend = matched.LastOrDefault(tr => curIdx >= tr.StartIndex && curIdx <= tr.EndIndex) ?? matched.Last();
                }
            }

            var parsedAngles = PeriodPlaybackSettings.ParseAngles(_settings.CustomAngles);
            var dynTrendLines = EvaluateRecentDynamicTrendLines();

            TickPlotHelper.BuildTickPlot(
                formsPlotTick.Plot,
                curBucket.Ticks,
                cursor,
                curBucket.StartTime,
                curBucket.EndTime,
                cboCoin.Text,
                periodLabel == "全部" ? GetSelectedPeriodTitle() : periodLabel,
                customTitle: customTickTitle,
                precomputedStats: stats,
                ratioProximity: _settings.RatioProximity,
                subPeriodSpan: span,
                displayType: displayType,
                showConsecutiveTrend: _settings.ShowTickConsecutiveTrend,
                consecutiveMinBars: _settings.ConsecutiveMinBars,
                consecutiveMinPct: _settings.ConsecutiveMinPct,
                showTickChannel: _settings.ShowTickChannel,
                tickConsecutiveMinBars: _settings.TickConsecutiveMinBars,
                tickConsecutiveMinPct: _settings.TickConsecutiveMinPct,
                showRatio: _settings.ShowTickRatio,
                showAngleLines: _settings.ShowTickAngleLines,
                customAngles: parsedAngles,
                selectedTrend: liveMacroTrend,
                activeChannels: liveMacroChannels,
                selectedBarStartIndex: _engine.CurrentBucketIndex,
                selectedBarEndIndex: _engine.CurrentBucketIndex,
                macroBarTimes: _engine.Buckets?.Select(b => (b.StartTime, b.EndTime)).ToList(),
                hoverIndicator: _tickHoverIndicator,
                showRecentTrendLines: _settings.ShowTickRecentTrendLines && _hasConsecutiveTrendTriggered && (dynTrendLines?.HasLines ?? false),
                recentTrendLineResult: dynTrendLines,
                selectedMicroBarStartIndex: _selectedMicroBarStartIndex,
                selectedMicroBarEndIndex: _selectedMicroBarEndIndex,
                autoFollow: _tickAutoFollow);

            _currentMicroBuckets = TickPlotHelper.LastSubBuckets;
            formsPlotTick.Refresh();

            // 若当前未选定特定微观 K 线，更新右侧最近 Tick 表格 (最新 25 笔)
            if (!_selectedMicroBarStartIndex.HasValue)
            {
                UpdateRecentTicksGrid(curBucket.Ticks, cursor);
            }
        }

        #endregion

        #region 引擎事件绑定与回放循环

        private void SetupEngineEvents()
        {
            // 1. Tick 派发
            _engine.OnTickDispatched += (tick, formingBar, curTickIdx, totalTicks) =>
            {
                lock (_currentBucketTicks)
                {
                    _currentBucketTicks.Add(tick);
                    _currentBucketTickCursor = curTickIdx;
                }

                _tickPlotNeedsRefresh = true;
                _macroPlotNeedsRefresh = true;
            };

            // 2. 宏观大周期 K 线定型输出
            _engine.OnMacroBarCompleted += (finalKline, bucketIdx, totalBuckets) =>
            {
                this.BeginInvoke(() =>
                {
                    lblMacroBadge.Text = $"{cboCoin.Text} | {GetSelectedPeriodTitle()} | 已定型输出: {_engine.CompletedMacroBars.Count} 根 K 线";
                    lblMacroBadge.Left = lblMacroTitle.Right + 15;
                    _macroPlotNeedsRefresh = true;
                });
            };

            // 3. 进入新大周期桶
            _engine.OnBucketStarted += (bucket, bucketIdx, totalBuckets) =>
            {
                lock (_currentBucketTicks)
                {
                    _currentBucketTicks.Clear();
                    _currentBucketTickCursor = 0;
                    _currentBucketStart = bucket.StartTime;
                    _currentBucketEnd = bucket.EndTime;
                }

                this.BeginInvoke(() =>
                {
                    _tickAutoFollow = true;
                    UpdateLiveFollowButtonState(isLive: !_selectedBarStartIndex.HasValue);
                    if (!_selectedBarStartIndex.HasValue)
                    {
                        lblTickBadge.Text = $"周期 #{bucketIdx + 1}/{totalBuckets}: {FormatTimeSpanRange(bucket.StartTime, bucket.EndTime)} (共 {bucket.TickCount:N0} Ticks)";
                    }
                    _tickPlotNeedsRefresh = true;
                    _macroPlotNeedsRefresh = true;
                });
            };

            // 4. 状态变更
            _engine.OnStateChanged += state =>
            {
                this.BeginInvoke(() =>
                {
                    UpdateControlButtonsState(state);
                });
            };

            // 5. 日志回调
            _engine.OnLogMessage += (msg, color) =>
            {
                AppendLog(msg, color);
            };

            // 6. 请求更多分批数据 (流式续播)
            _engine.OnRequestMoreBucketsAsync += async () =>
            {
                bool autoAppend = true;
                if (this.IsHandleCreated)
                {
                    this.Invoke(() => autoAppend = chkAutoAppend.Checked);
                }
                if (!autoAppend || _nextBatchDateIndex >= _batchDates.Count)
                {
                    return false;
                }
                return await LoadNextBatchAsync(isBackgroundPreload: true).ConfigureAwait(false);
            };
        }

        private void UpdateControlButtonsState(PlaybackState state)
        {
            if (state == PlaybackState.Playing)
            {
                btnPlay.Text = "暂停";
                btnPlay.BackColor = Color.FromArgb(217, 119, 6); // 琥珀橙
                btnPlay.Enabled = true;
            }
            else
            {
                btnPlay.Text = "播放";
                btnPlay.BackColor = Color.FromArgb(16, 185, 129); // 翡翠绿
                btnPlay.Enabled = _engine.TotalBuckets > 0;
            }

            btnStop.Enabled = state == PlaybackState.Playing || state == PlaybackState.Paused;
            btnNextBucket.Enabled = state == PlaybackState.Playing || state == PlaybackState.Paused;
            btnStepTick.Enabled = state != PlaybackState.Playing && _engine.TotalBuckets > 0;
        }

        private void UpdatePlaybackSpeed()
        {
            double mult = cboSpeed.SelectedIndex switch
            {
                0 => 1.0,
                1 => 5.0,
                2 => 10.0,
                3 => 50.0,
                4 => 100.0,
                5 => 500.0,
                6 => 10000.0, // 极速批量
                _ => 10.0
            };
            _engine.SetSpeed(mult);
        }

        #endregion

        #region 高帧率节流刷新定时器 (30 FPS)

        private void SetupUiRefreshTimer()
        {
            _uiRefreshTimer = new System.Windows.Forms.Timer { Interval = 40 }; // 25 FPS
            _uiRefreshTimer.Tick += (s, e) =>
            {
                // 0. 后台分批流式预加载检查 (当剩余未播周期 <= 5 时自动提前预取下一批)
                if (_engine.State == PlaybackState.Playing && chkAutoAppend.Checked && !_isBatchLoading && _nextBatchDateIndex < _batchDates.Count)
                {
                    if (_engine.CurrentBucketIndex >= Math.Max(0, _engine.TotalBuckets - 5))
                    {
                        _ = LoadNextBatchAsync(isBackgroundPreload: true);
                    }
                }

                // 1. 批量消化日志
                int logCount = 0;
                while (_logQueue.TryDequeue(out var log) && logCount < 20)
                {
                    AppendLogInternal(log.Message, log.Color);
                    logCount++;
                }

                // 2. 刷新大图 (大周期 K 线图表：蜡烛 + 成交量，无额外 MA 指标)
                if (_macroPlotNeedsRefresh)
                {
                    _macroPlotNeedsRefresh = false;
                    try
                    {
                        var dynTrendLines = EvaluateRecentDynamicTrendLines();
                        MacroPlotHelper.BuildMacroPlot(
                            formsPlotMacro.Plot,
                            _engine.GetCompletedMacroBarsSnapshot(),
                            _engine.CurrentFormingBar,
                            cboCoin.Text,
                            GetSelectedPeriodTitle(),
                            displayType: (MacroChartDisplayType)cboChartType.SelectedIndex,
                            showVolume: chkShowVolume.Checked,
                            autoFollow: chkAutoFollow.Checked,
                            showConsecutiveTrend: chkConsecutiveTrend.Checked,
                            consecutiveMinBars: (int)numConsecutiveBars.Value,
                            consecutiveMinPct: numConsecutivePct.Value,
                            selectedStartIndex: _selectedBarStartIndex,
                            selectedEndIndex: _selectedBarEndIndex,
                            channelExtensionBars: _settings.ChannelExtensionBars,
                            showAngleLines: chkAngleLines.Checked,
                            customAngles: PeriodPlaybackSettings.ParseAngles(_settings.CustomAngles),
                            showRecentTrendLines: _settings.ShowMacroRecentTrendLines && _hasConsecutiveTrendTriggered && (dynTrendLines?.HasLines ?? false),
                            recentTrendLineResult: dynTrendLines,
                            canvasWidth: formsPlotMacro.Width,
                            canvasHeight: formsPlotMacro.Height);

                        formsPlotMacro.Refresh();
                    }
                    catch
                    {
                    }
                }

                // 3. 刷新小图 (微观 Tick 走势折线图)
                if (_tickPlotNeedsRefresh)
                {
                    _tickPlotNeedsRefresh = false;
                    try
                    {
                        // 🌟 若用户当前正在查看选中的历史 K 线或微观多选区间，则不被正在播放的 live tick 覆盖
                        if (!_selectedBarStartIndex.HasValue && !_selectedMicroBarStartIndex.HasValue)
                        {
                            RenderLiveTickPlot();
                        }
                    }
                    catch
                    {
                    }
                }

                // 4. 更新监控看板卡片
                UpdateDashboardCards();
            };
            _uiRefreshTimer.Start();
        }

        private void UpdateDashboardCards()
        {
            // 1. 本周期/选中周期卡片 (若无历史或微观选中，跟随当前实时周期桶)
            if (!_selectedBarStartIndex.HasValue && !_selectedMicroBarStartIndex.HasValue)
            {
                var curBucket = _engine.CurrentBucket;
                if (curBucket != null)
                {
                    lblCardPeriod.Text = FormatTimeSpanRange(curBucket.StartTime, curBucket.EndTime);
                    double bPct = curBucket.TickCount > 0 ? ((double)_engine.CurrentTickIndex / curBucket.TickCount) * 100.0 : 0;
                    lblCardTickProgress.Text = $"{_engine.CurrentTickIndex:N0} / {curBucket.TickCount:N0} ({bPct:F1}%)";
                    prgBucket.Value = Math.Clamp((int)bPct, 0, 100);

                    if (curBucket.Ticks.Count > 0 && _engine.CurrentTickIndex > 0)
                    {
                        int cursor = Math.Min(curBucket.Ticks.Count, _engine.CurrentTickIndex);
                        var liveStats = TickLongShortStats.Calculate(curBucket.Ticks, cursor);
                        string tR = liveStats.TickRatio >= 999.0 ? "∞" : liveStats.TickRatio.ToString("F2");
                        string vR = liveStats.VolumeRatio >= 999.0 ? "∞" : liveStats.VolumeRatio.ToString("F2");

                        lblCardTickRatio.Text = $"{tR} ({liveStats.BuyTickPct:F1}%:{liveStats.SellTickPct:F1}%)";
                        lblCardTickRatio.ForeColor = liveStats.TickRatio >= 1.0 ? Color.FromArgb(74, 222, 128) : Color.FromArgb(248, 113, 113);

                        lblCardVolRatio.Text = $"{vR} ({liveStats.BuyVolumePct:F1}%:{liveStats.SellVolumePct:F1}%)";
                        lblCardVolRatio.ForeColor = liveStats.VolumeRatio >= 1.0 ? Color.FromArgb(74, 222, 128) : Color.FromArgb(248, 113, 113);
                    }
                }
            }

            // 2. 全局回放状态卡片 —— 无论是否选中历史K线，只要引擎在跑，这些卡片必须持续实时更新！
            lblCardMacroCount.Text = $"{_engine.CompletedMacroBars.Count:N0} 根";

            var forming = _engine.CurrentFormingBar;
            if (forming.HasTicks)
            {
                lblCardFormingPrice.Text = $"{forming.CurrentPrice:F2} ({forming.ChangePct:+0.00;-0.00;0.00}%)";
                lblCardFormingPrice.ForeColor = forming.IsBullish ? Color.FromArgb(74, 222, 128) : Color.FromArgb(248, 113, 113);
            }
            else
            {
                lblCardFormingPrice.Text = "--";
                lblCardFormingPrice.ForeColor = Color.FromArgb(148, 163, 184);
            }

            lblCardTps.Text = $"{_engine.CurrentTps:N0} TPS";

            if (_batchDates.Count > 0)
            {
                int currentBatch = Math.Min(_batchDates.Count, _nextBatchDateIndex);
                double batchBase = (currentBatch - 1.0) / _batchDates.Count * 100.0;
                double bucketRatio = _engine.TotalBuckets > 0 ? ((double)_engine.CurrentBucketIndex / _engine.TotalBuckets) : 0;
                int overallPct = (int)Math.Clamp(batchBase + (bucketRatio / _batchDates.Count * 100.0), 0, 100);
                prgOverall.Value = overallPct;
            }
            else if (_engine.TotalBuckets > 0)
            {
                int overallPct = (int)(((double)_engine.CurrentBucketIndex / _engine.TotalBuckets) * 100.0);
                prgOverall.Value = Math.Clamp(overallPct, 0, 100);
            }
        }

        private void UpdateRecentTicksGrid(IReadOnlyList<RawTick> ticks, int cursor)
        {
            if (ticks == null || ticks.Count == 0 || cursor <= 0) return;

            int take = Math.Min(25, cursor);
            int start = cursor - take;

            dgvRecentTicks.Rows.Clear();
            dgvRecentTicks.SuspendLayout();

            for (int i = cursor - 1; i >= start; i--)
            {
                var t = ticks[i];
                DateTime time = DateTimeOffset.FromUnixTimeMilliseconds(t.Time).UtcDateTime;
                bool isBuy = !t.IsBuyerMaker;
                string side = isBuy ? "买入 (Buy)" : "卖出 (Sell)";

                int rowIdx = dgvRecentTicks.Rows.Add(
                    i + 1,
                    time.ToString("HH:mm:ss.fff"),
                    t.Price.ToString("F2"),
                    t.Qty.ToString("F4"),
                    t.QuoteQty.ToString("F2"),
                    side);

                dgvRecentTicks.Rows[rowIdx].DefaultCellStyle.ForeColor = isBuy
                    ? Color.FromArgb(74, 222, 128)
                    : Color.FromArgb(248, 113, 113);
            }

            dgvRecentTicks.ResumeLayout();
        }

        #endregion

        #region K 线点击与 Shift 连续多选查看详细 Tick

        private void OnFormsPlotMacroMouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _macroMouseDownPoint = e.Location;
            }
        }

        private void OnFormsPlotMacroMouseUp(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            int dx = Math.Abs(e.Location.X - _macroMouseDownPoint.X);
            int dy = Math.Abs(e.Location.Y - _macroMouseDownPoint.Y);

            // 如果鼠标位移大于 4 像素，判定为用户手动平移或缩放图表视角
            if (dx > 4 || dy > 4)
            {
                if (chkAutoFollow.Checked)
                {
                    chkAutoFollow.Checked = false;
                }
                return;
            }

            // 否则判定为点击事件，执行 通道点击 / K 线点击查看与 Shift 连续多选
            int completedCount = _engine.CompletedMacroBars.Count;
            bool hasForming = _engine.CurrentFormingBar.HasTicks;
            int totalDisplayCount = completedCount + (hasForming ? 1 : 0);

            if (totalDisplayCount == 0) return;

            // 切换宏观大周期 K 线时，自动重置微观内部选中项
            _selectedMicroBarAnchor = null;
            _selectedMicroBarStartIndex = null;
            _selectedMicroBarEndIndex = null;
            if (tabDashboard != null) tabDashboard.Text = "实时仪表盘 & 逐笔流水";

            try
            {
                var mouseCoord = formsPlotMacro.Plot.GetCoordinates(new ScottPlot.Pixel(e.X, e.Y));

                // 1. 优先进行平行通道命中检测 (Hit-Test)
                if ((chkConsecutiveTrend.Checked || _settings.ShowMacroAngleLines) && totalDisplayCount >= (int)numConsecutiveBars.Value)
                {
                    var completedBars = _engine.GetCompletedMacroBarsSnapshot();
                    var formingBar = _engine.CurrentFormingBar;
                    var trends = MacroConsecutiveTrendDetector.ScanTrends(
                        completedBars,
                        formingBar,
                        (int)numConsecutiveBars.Value,
                        numConsecutivePct.Value);

                    var hitChannel = MacroPlotHelper.FindHitChannel(
                        formsPlotMacro.Plot,
                        new ScottPlot.Pixel(e.X, e.Y),
                        mouseCoord,
                        trends,
                        totalDisplayCount,
                        _settings.ChannelExtensionBars);

                    if (hitChannel != null)
                    {
                        if (hitChannel.MacroSlope45 <= 0 && MacroPlotHelper.LastSlope45 > 0)
                        {
                            hitChannel.MacroSlope45 = MacroPlotHelper.LastSlope45;
                        }
                        _selectedConsecutiveTrend = hitChannel;
                        _selectedBarAnchor = hitChannel.StartIndex;
                        _selectedBarStartIndex = hitChannel.StartIndex;
                        _selectedBarEndIndex = hitChannel.EndIndex;

                        _tickAutoFollow = true;
                        DisplaySelectedBarsTicks();
                        _macroPlotNeedsRefresh = true;
                        return;
                    }
                }

                // 2. 未命中通道，重置通道选中状态，执行普通单根 K 线 / Shift 连续多选
                _selectedConsecutiveTrend = null;
                int targetIndex = (int)Math.Round(mouseCoord.X);

                if (targetIndex >= 0 && targetIndex < totalDisplayCount)
                {
                    // 若点击的是当前正在实时凝聚生成的最新 K 线，直接无缝切入实时跟随播放
                    if (targetIndex == completedCount)
                    {
                        ClearBarSelection();
                        return;
                    }

                    bool isShift = (ModifierKeys & Keys.Shift) == Keys.Shift;

                    if (isShift && _selectedBarAnchor.HasValue)
                    {
                        // 按住 Shift 键连续多选：以锚点为基准扩展选中范围
                        _selectedBarStartIndex = Math.Min(_selectedBarAnchor.Value, targetIndex);
                        _selectedBarEndIndex = Math.Max(_selectedBarAnchor.Value, targetIndex);
                    }
                    else
                    {
                        // 普通单击单选：设置锚点与唯一选中项
                        _selectedBarAnchor = targetIndex;
                        _selectedBarStartIndex = targetIndex;
                        _selectedBarEndIndex = targetIndex;
                    }

                    _tickAutoFollow = true;
                    DisplaySelectedBarsTicks();
                    _macroPlotNeedsRefresh = true;
                }
            }
            catch
            {
            }
        }

        private void OnFormsPlotMacroMouseMove(object? sender, MouseEventArgs e)
        {
            try
            {
                int completedCount = _engine.CompletedMacroBars.Count;
                bool hasForming = _engine.CurrentFormingBar.HasTicks;
                int totalDisplayCount = completedCount + (hasForming ? 1 : 0);

                if (totalDisplayCount == 0)
                {
                    formsPlotMacro.Cursor = Cursors.Default;
                    return;
                }

                var mouseCoord = formsPlotMacro.Plot.GetCoordinates(new ScottPlot.Pixel(e.X, e.Y));

                // 优先检测是否悬停在连续涨跌平行通道线上
                if (chkConsecutiveTrend.Checked && totalDisplayCount >= (int)numConsecutiveBars.Value)
                {
                    var completedBars = _engine.GetCompletedMacroBarsSnapshot();
                    var formingBar = _engine.CurrentFormingBar;
                    var trends = MacroConsecutiveTrendDetector.ScanTrends(
                        completedBars,
                        formingBar,
                        (int)numConsecutiveBars.Value,
                        numConsecutivePct.Value);

                    var hitChannel = MacroPlotHelper.FindHitChannel(
                        formsPlotMacro.Plot,
                        new ScottPlot.Pixel(e.X, e.Y),
                        mouseCoord,
                        trends,
                        totalDisplayCount,
                        _settings.ChannelExtensionBars);

                    if (hitChannel != null)
                    {
                        formsPlotMacro.Cursor = Cursors.Hand;
                        return;
                    }
                }

                int targetIndex = (int)Math.Round(mouseCoord.X);

                if (targetIndex >= 0 && targetIndex < totalDisplayCount)
                {
                    formsPlotMacro.Cursor = Cursors.Hand;
                }
                else
                {
                    formsPlotMacro.Cursor = Cursors.Default;
                }
            }
            catch
            {
                formsPlotMacro.Cursor = Cursors.Default;
            }
        }

        private void OnFormsPlotTickMouseMove(object? sender, MouseEventArgs e)
        {
            try
            {
                if (_tickIsDragging && _tickAutoFollow)
                {
                    int dx = Math.Abs(e.Location.X - _tickMouseDownPoint.X);
                    int dy = Math.Abs(e.Location.Y - _tickMouseDownPoint.Y);
                    if (dx > 4 || dy > 4)
                    {
                        _tickAutoFollow = false;
                        UpdateLiveFollowButtonState(isLive: !_selectedBarStartIndex.HasValue);
                    }
                }

                var subBuckets = _tickHoverIndicator.ActiveSubBuckets ?? _currentMicroBuckets;
                if (subBuckets != null && subBuckets.Count > 0)
                {
                    var mouseCoord = formsPlotTick.Plot.GetCoordinates(new ScottPlot.Pixel(e.X, e.Y));
                    int targetIndex = (int)Math.Round(mouseCoord.X);
                    if (targetIndex >= 0 && targetIndex < subBuckets.Count)
                    {
                        formsPlotTick.Cursor = Cursors.Hand;
                    }
                    else
                    {
                        formsPlotTick.Cursor = Cursors.Default;
                    }
                }
                else
                {
                    formsPlotTick.Cursor = Cursors.Default;
                }

                if (!_tickHoverIndicator.IsAttached) return;

                var mouseCoordHover = formsPlotTick.Plot.GetCoordinates(new ScottPlot.Pixel(e.X, e.Y));
                string chineseFont = TickPlotHelper.GetInstalledChineseFont();

                if (_tickHoverIndicator.UpdateHover(
                    mouseCoordHover.X,
                    mouseCoordHover.Y,
                    chineseFont,
                    out string headerBadgeText,
                    out Color badgeColor))
                {
                    lblTickBadge.Text = headerBadgeText;
                    lblTickBadge.ForeColor = badgeColor;
                    formsPlotTick.Refresh();
                }
            }
            catch
            {
                formsPlotTick.Cursor = Cursors.Default;
            }
        }

        private void OnFormsPlotTickMouseLeave(object? sender, EventArgs e)
        {
            try
            {
                _tickIsDragging = false;
                formsPlotTick.Cursor = Cursors.Default;
                if (!_tickHoverIndicator.IsAttached) return;

                _tickHoverIndicator.Clear();
                lblTickBadge.Text = _lastDefaultTickBadgeText;
                lblTickBadge.ForeColor = _lastDefaultTickBadgeColor;
                formsPlotTick.Refresh();
            }
            catch
            {
            }
        }

        private void OnFormsPlotTickMouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle)
            {
                _tickMouseDownPoint = e.Location;
                _tickIsDragging = true;
            }
        }

        private void OnFormsPlotTickMouseWheel(object? sender, MouseEventArgs e)
        {
            if (_tickAutoFollow)
            {
                _tickAutoFollow = false;
                UpdateLiveFollowButtonState(isLive: !_selectedBarStartIndex.HasValue);
            }
        }

        private void OnFormsPlotTickMouseUp(object? sender, MouseEventArgs e)
        {
            _tickIsDragging = false;

            int dx = Math.Abs(e.Location.X - _tickMouseDownPoint.X);
            int dy = Math.Abs(e.Location.Y - _tickMouseDownPoint.Y);

            // 如果鼠标位移大于 4 像素，判定为用户手动平移或缩放图表视角
            if (dx > 4 || dy > 4)
            {
                if (_tickAutoFollow)
                {
                    _tickAutoFollow = false;
                    UpdateLiveFollowButtonState(isLive: !_selectedBarStartIndex.HasValue);
                }
                return;
            }

            if (e.Button != MouseButtons.Left) return;

            var subBuckets = _tickHoverIndicator.ActiveSubBuckets ?? _currentMicroBuckets;
            if (subBuckets == null || subBuckets.Count == 0) return;

            int totalDisplayCount = subBuckets.Count;

            try
            {
                var mouseCoord = formsPlotTick.Plot.GetCoordinates(new ScottPlot.Pixel(e.X, e.Y));
                int targetIndex = (int)Math.Round(mouseCoord.X);

                if (targetIndex >= 0 && targetIndex < totalDisplayCount)
                {
                    bool isShift = (ModifierKeys & Keys.Shift) == Keys.Shift;

                    if (isShift && _selectedMicroBarAnchor.HasValue)
                    {
                        // 按住 Shift 键连续多选微观 K 线
                        _selectedMicroBarStartIndex = Math.Min(_selectedMicroBarAnchor.Value, targetIndex);
                        _selectedMicroBarEndIndex = Math.Max(_selectedMicroBarAnchor.Value, targetIndex);
                    }
                    else
                    {
                        // 单击单选微观 K 线
                        _selectedMicroBarAnchor = targetIndex;
                        _selectedMicroBarStartIndex = targetIndex;
                        _selectedMicroBarEndIndex = targetIndex;
                    }

                    DisplaySelectedMicroBarsTicks();
                }
            }
            catch
            {
            }
        }

        private void DisplaySelectedMicroBarsTicks()
        {
            if (!_selectedMicroBarStartIndex.HasValue || !_selectedMicroBarEndIndex.HasValue) return;
            var subBuckets = _tickHoverIndicator.ActiveSubBuckets ?? _currentMicroBuckets;
            if (subBuckets == null || subBuckets.Count == 0) return;

            int sIdx = Math.Clamp(_selectedMicroBarStartIndex.Value, 0, subBuckets.Count - 1);
            int eIdx = Math.Clamp(_selectedMicroBarEndIndex.Value, 0, subBuckets.Count - 1);
            if (sIdx > eIdx)
            {
                int tmp = sIdx;
                sIdx = eIdx;
                eIdx = tmp;
            }

            int barCount = eIdx - sIdx + 1;
            DateTime rangeStart = subBuckets[sIdx].StartTime;
            DateTime rangeEnd = subBuckets[eIdx].EndTime;
            string rangeDesc = FormatTimeSpanRange(rangeStart, rangeEnd);

            // 聚合所选微观 K 线内的所有 Tick
            var aggregatedTicks = new List<RawTick>();
            for (int i = sIdx; i <= eIdx; i++)
            {
                var b = subBuckets[i];
                if (b.Ticks != null && b.Ticks.Count > 0)
                {
                    aggregatedTicks.AddRange(b.Ticks);
                }
            }

            // 1. 切换右侧 Tab 至 "实时仪表盘 & 逐笔流水" 并更新标题
            tabControlRight.SelectedTab = tabDashboard;
            tabDashboard.Text = $"逐笔流水 ({aggregatedTicks.Count:N0}条)";

            // 2. 统计所选区间的 Tick 多空比与成交量多空比
            var stats = TickLongShortStats.Calculate(aggregatedTicks);
            string tickRatioStr = stats.TickRatio >= 999.0 ? "∞" : stats.TickRatio.ToString("F2");
            string volRatioStr = stats.VolumeRatio >= 999.0 ? "∞" : stats.VolumeRatio.ToString("F2");

            // 3. 更新微观顶部 Badge
            string barTitle = barCount == 1 ? $"Bar #{sIdx + 1}" : $"Bar #{sIdx + 1}~#{eIdx + 1}";
            lblTickBadge.Text = $"🕒[微观选中 {barTitle}{(barCount > 1 ? $" (共{barCount}根)" : "")}]: {rangeDesc} | {aggregatedTicks.Count:N0} Ticks | 🎯Tick比 {tickRatioStr} | 📊量比 {volRatioStr}";
            lblTickBadge.ForeColor = Color.FromArgb(56, 189, 248);
            _lastDefaultTickBadgeText = lblTickBadge.Text;
            _lastDefaultTickBadgeColor = lblTickBadge.ForeColor;

            // 4. 更新顶部多空比 Badge
            lblTickRatioBadge.Text = $"🎯 Tick比: {tickRatioStr} ({stats.BuyTickPct:F0}%:{stats.SellTickPct:F0}%)";
            lblTickRatioBadge.ForeColor = stats.TickRatio >= 1.0 ? Color.FromArgb(74, 222, 128) : Color.FromArgb(248, 113, 113);
            lblVolRatioBadge.Text = $"📊 量比: {volRatioStr} ({stats.BuyVolumePct:F0}%:{stats.SellVolumePct:F0}%)";
            lblVolRatioBadge.ForeColor = stats.VolumeRatio >= 1.0 ? Color.FromArgb(74, 222, 128) : Color.FromArgb(248, 113, 113);
            lblTickRatioBadge.Visible = _settings.ShowTickRatio;
            lblVolRatioBadge.Visible = _settings.ShowTickRatio;
            RepositionTickHeaderControls();

            // 5. 更新右侧监控卡片
            lblCardPeriod.Text = $"微观 {barTitle} (共{barCount}根)";
            lblCardTickProgress.Text = $"{stats.TotalTicks:N0} Ticks (微观流水)";
            lblCardTickRatio.Text = $"{tickRatioStr} ({stats.BuyTickPct:F1}%:{stats.SellTickPct:F1}%)";
            lblCardTickRatio.ForeColor = lblTickRatioBadge.ForeColor;
            lblCardVolRatio.Text = $"{volRatioStr} ({stats.BuyVolumePct:F1}%:{stats.SellVolumePct:F1}%)";
            lblCardVolRatio.ForeColor = lblVolRatioBadge.ForeColor;

            if (aggregatedTicks.Count > 0)
            {
                decimal openPrice = aggregatedTicks[0].Price;
                decimal closePrice = aggregatedTicks[^1].Price;
                decimal chgPct = openPrice > 0 ? ((closePrice - openPrice) / openPrice * 100m) : 0m;
                lblCardFormingPrice.Text = $"现价:{closePrice:F2} ({chgPct:+0.00;-0.00;0.00}%)";
                lblCardFormingPrice.ForeColor = chgPct >= 0 ? Color.FromArgb(74, 222, 128) : Color.FromArgb(248, 113, 113);
            }

            // 6. 填充右侧 DataGridView 逐笔流水明细 (倒序排列，最新成交在最上方，最多展示 1000 笔)
            int totalCount = aggregatedTicks.Count;
            int showCount = Math.Min(1000, totalCount);
            int startIdx = totalCount - showCount;

            dgvRecentTicks.Rows.Clear();
            dgvRecentTicks.SuspendLayout();

            for (int i = totalCount - 1; i >= startIdx; i--)
            {
                var t = aggregatedTicks[i];
                DateTime time = DateTimeOffset.FromUnixTimeMilliseconds(t.Time).UtcDateTime;
                bool isBuy = !t.IsBuyerMaker;
                string side = isBuy ? "买入 (Buy)" : "卖出 (Sell)";

                int rowIdx = dgvRecentTicks.Rows.Add(
                    i + 1,
                    time.ToString("HH:mm:ss.fff"),
                    t.Price.ToString("F2"),
                    t.Qty.ToString("F4"),
                    t.QuoteQty.ToString("F2"),
                    side);

                dgvRecentTicks.Rows[rowIdx].DefaultCellStyle.ForeColor = isBuy
                    ? Color.FromArgb(74, 222, 128)
                    : Color.FromArgb(248, 113, 113);
            }

            dgvRecentTicks.ResumeLayout();

            // 7. 更新实时跟随按钮状态为可一键返回
            UpdateLiveFollowButtonState(isLive: false);

            // 8. 触发微观图表重绘以渲染选中高亮框
            RefreshTickPlotDirectly();
        }

        private void ClearMicroBarSelection()
        {
            _selectedMicroBarAnchor = null;
            _selectedMicroBarStartIndex = null;
            _selectedMicroBarEndIndex = null;
            _tickAutoFollow = true;
            if (tabDashboard != null) tabDashboard.Text = "实时仪表盘 & 逐笔流水";

            if (_selectedBarStartIndex.HasValue && _selectedBarEndIndex.HasValue)
            {
                DisplaySelectedBarsTicks();
            }
            else
            {
                ClearBarSelection();
            }
        }

        private void DisplaySelectedBarsTicks()
        {
            if (!_selectedBarStartIndex.HasValue || !_selectedBarEndIndex.HasValue) return;
            if (_engine.Buckets.Count == 0) return;

            int sIdx = Math.Clamp(_selectedBarStartIndex.Value, 0, _engine.Buckets.Count - 1);
            int eIdx = Math.Clamp(_selectedBarEndIndex.Value, 0, _engine.Buckets.Count - 1);
            if (sIdx > eIdx)
            {
                int tmp = sIdx;
                sIdx = eIdx;
                eIdx = tmp;
            }

            var aggregatedTicks = new List<RawTick>();
            var boundaryIndices = new List<int>();

            for (int b = sIdx; b <= eIdx; b++)
            {
                var bucket = _engine.Buckets[b];
                if (bucket.Ticks == null || bucket.Ticks.Count == 0) continue;

                if (b > sIdx && aggregatedTicks.Count > 0)
                {
                    boundaryIndices.Add(aggregatedTicks.Count);
                }

                // 若选中的是当前正在播放的大周期桶，且未播放完毕，则只取已播放的 Tick
                if (b == _engine.CurrentBucketIndex && _engine.State != PlaybackState.Idle)
                {
                    int takeCount = Math.Clamp(_engine.CurrentTickIndex, 0, bucket.Ticks.Count);
                    for (int i = 0; i < takeCount; i++)
                    {
                        aggregatedTicks.Add(bucket.Ticks[i]);
                    }
                }
                else
                {
                    aggregatedTicks.AddRange(bucket.Ticks);
                }
            }

            if (aggregatedTicks.Count == 0) return;

            DateTime rangeStart = _engine.Buckets[sIdx].StartTime;
            DateTime rangeEnd = _engine.Buckets[eIdx].EndTime;
            int barCount = eIdx - sIdx + 1;

            var span = GetSelectedTickPeriodSpan();
            string periodLabel = GetSelectedTickPeriodTitle();
            var displayType = (MacroChartDisplayType)Math.Clamp(_settings.TickChartTypeIndex, 0, 1);

            // 检测选中的 K 线集合中是否存在大周期平行通道或多角度趋势线
            var relevantChannels = new List<MacroConsecutiveTrendItem>();
            if (_selectedConsecutiveTrend != null)
            {
                if (_selectedConsecutiveTrend.MacroSlope45 <= 0 && MacroPlotHelper.LastSlope45 > 0)
                {
                    _selectedConsecutiveTrend.MacroSlope45 = MacroPlotHelper.LastSlope45;
                }
                relevantChannels.Add(_selectedConsecutiveTrend);
            }
            else if (_settings.ShowConsecutiveTrend || _settings.ShowTickAngleLines || _settings.ShowTickConsecutiveTrend)
            {
                var completedBars = _engine.GetCompletedMacroBarsSnapshot();
                var formingBar = _engine.CurrentFormingBar;
                var trends = MacroConsecutiveTrendDetector.ScanTrends(
                    completedBars,
                    formingBar,
                    _settings.ConsecutiveMinBars,
                    _settings.ConsecutiveMinPct);

                int forwardBars = Math.Max(20, _settings.ChannelExtensionBars);
                foreach (var tr in trends)
                {
                    if (tr.MacroSlope45 <= 0 && MacroPlotHelper.LastSlope45 > 0) tr.MacroSlope45 = MacroPlotHelper.LastSlope45;
                    if (!_settings.ShowTickAngleLines && !tr.HasChannel) continue;
                    int extEnd = tr.EndIndex + forwardBars;
                    if (Math.Max(sIdx, tr.StartIndex) <= Math.Min(eIdx, extEnd))
                    {
                        relevantChannels.Add(tr);
                    }
                }
            }

            bool hasChannel = relevantChannels.Count > 0;
            var primaryTrend = relevantChannels.FirstOrDefault(tr => sIdx >= tr.StartIndex && sIdx <= tr.EndIndex) ?? relevantChannels.FirstOrDefault();
            bool isExactChannel = primaryTrend != null &&
                                  primaryTrend.StartIndex == sIdx &&
                                  primaryTrend.EndIndex == eIdx;
            string dirText = primaryTrend != null ? (primaryTrend.IsBullish ? "连涨" : "连跌") : "";

            string rangeDesc = FormatTimeSpanRange(rangeStart, rangeEnd, includeDate: true);
            string customTitle;
            if (isExactChannel)
            {
                customTitle = span.HasValue
                    ? $"⭐ [已选中{dirText}平行通道] Bar #{sIdx}~#{eIdx} (共 {barCount} 根, 拟合基准 {primaryTrend!.ChannelBaseBars} 根) [{periodLabel}] - 共 {aggregatedTicks.Count:N0} Ticks"
                    : $"⭐ [已选中{dirText}平行通道] Bar #{sIdx}~#{eIdx} (共 {barCount} 根K线, 拟合基准 {primaryTrend!.ChannelBaseBars} 根, {rangeDesc}) - 共 {aggregatedTicks.Count:N0} Ticks";
            }
            else if (hasChannel)
            {
                customTitle = span.HasValue
                    ? (barCount == 1
                        ? $"⭐ [通道内走势(Bar #{sIdx})] 大周期{dirText}通道 #{primaryTrend!.StartIndex}~#{primaryTrend!.EndIndex} [{periodLabel}] - 共 {aggregatedTicks.Count:N0} Ticks"
                        : $"⭐ [选区包含通道走势] Bar #{sIdx}~#{eIdx} (含{dirText}通道 #{primaryTrend!.StartIndex}~#{primaryTrend!.EndIndex}) [{periodLabel}] - 共 {aggregatedTicks.Count:N0} Ticks")
                    : (barCount == 1
                        ? $"⭐ [通道内微观 Tick 走势: Bar #{sIdx}] 大周期{dirText}通道 #{primaryTrend!.StartIndex}~#{primaryTrend!.EndIndex} ({rangeDesc}) - 共 {aggregatedTicks.Count:N0} Ticks"
                        : $"⭐ [微观 Tick 走势: Bar #{sIdx}~#{eIdx}] 含大周期{dirText}通道 #{primaryTrend!.StartIndex}~#{primaryTrend!.EndIndex} ({rangeDesc}) - 共 {aggregatedTicks.Count:N0} Ticks");
            }
            else
            {
                customTitle = span.HasValue
                    ? (barCount == 1
                        ? $"微观周期走势: Bar #{sIdx} [{periodLabel}] - 共 {aggregatedTicks.Count:N0} Ticks"
                        : $"微观周期走势: Bar #{sIdx}~#{eIdx} (共 {barCount} 根) [{periodLabel}] - 共 {aggregatedTicks.Count:N0} Ticks")
                    : (barCount == 1
                        ? $"微观 Tick 走势: Bar #{sIdx} ({rangeDesc}) - 共 {aggregatedTicks.Count:N0} Ticks"
                        : $"微观 Tick 走势: Bar #{sIdx} ~ #{eIdx} (共 {barCount} 根K线, {rangeDesc}) - 共 {aggregatedTicks.Count:N0} Ticks");
            }

            // 统计所选区间的 Tick 多空笔数比与成交量多空比
            var stats = TickLongShortStats.Calculate(aggregatedTicks);
            string tickRatioStr = stats.TickRatio >= 999.0 ? "∞" : stats.TickRatio.ToString("F2");
            string volRatioStr = stats.VolumeRatio >= 999.0 ? "∞" : stats.VolumeRatio.ToString("F2");

            if (!_selectedMicroBarStartIndex.HasValue)
            {
                lblTickTitle.Text = isExactChannel
                    ? $"⭐通道 #{sIdx}~#{eIdx}"
                    : (hasChannel
                        ? (barCount == 1 ? $"⭐通道内 #{sIdx}" : $"⭐含通道 #{sIdx}~#{eIdx}")
                        : (barCount == 1 ? $"Bar #{sIdx}" : $"Bar #{sIdx}~#{eIdx}"));

                if (isExactChannel)
                {
                    var tr = primaryTrend!;
                    lblTickBadge.Text = $"⭐通道: {dirText} {tr.BarCount}根 ({tr.PriceChangePct:+0.00;-0.00;0.00}%) | k={tr.SlopeK:+0.0000;-0.0000} | 高度 {tr.ChannelHeight:F2} | {rangeDesc} ({aggregatedTicks.Count:N0} T)";
                    lblTickBadge.ForeColor = Color.FromArgb(251, 191, 36);
                }
                else if (hasChannel)
                {
                    var tr = primaryTrend!;
                    string barPart = barCount == 1 ? $"Bar #{sIdx}" : $"Bar #{sIdx}~#{eIdx}";
                    lblTickBadge.Text = $"⭐通道内({barPart}): {dirText} #{tr.StartIndex}~#{tr.EndIndex} ({tr.PriceChangePct:+0.00;-0.00;0.00}%) | {rangeDesc} ({aggregatedTicks.Count:N0} T)";
                    lblTickBadge.ForeColor = Color.FromArgb(251, 191, 36);
                }
                else
                {
                    lblTickBadge.Text = $"🕒[历史查看 Bar #{sIdx}{(barCount > 1 ? $"~#{eIdx}" : "")}]: {rangeDesc} ({aggregatedTicks.Count:N0} Ticks)";
                    lblTickBadge.ForeColor = Color.FromArgb(251, 191, 36);
                }
                UpdateLiveFollowButtonState(isLive: false);

                if (aggregatedTicks.Count == 0)
                {
                    lblTickRatioBadge.Text = "🎯 Tick比: --";
                    lblTickRatioBadge.ForeColor = Color.FromArgb(148, 163, 184);
                    lblVolRatioBadge.Text = "📊 量比: --";
                    lblVolRatioBadge.ForeColor = Color.FromArgb(148, 163, 184);
                }
                else
                {
                    lblTickRatioBadge.Text = $"🎯 Tick比: {tickRatioStr} ({stats.BuyTickPct:F0}%:{stats.SellTickPct:F0}%)";
                    lblTickRatioBadge.ForeColor = stats.TickRatio >= 1.0 ? Color.FromArgb(74, 222, 128) : Color.FromArgb(248, 113, 113);
                    lblVolRatioBadge.Text = $"📊 量比: {volRatioStr} ({stats.BuyVolumePct:F0}%:{stats.SellVolumePct:F0}%)";
                    lblVolRatioBadge.ForeColor = stats.VolumeRatio >= 1.0 ? Color.FromArgb(74, 222, 128) : Color.FromArgb(248, 113, 113);
                }
                lblTickRatioBadge.Visible = _settings.ShowTickRatio;
                lblVolRatioBadge.Visible = _settings.ShowTickRatio;
                RepositionTickHeaderControls();

                // 更新右侧监控卡片
                lblCardPeriod.Text = isExactChannel
                    ? $"⭐通道 #{sIdx}~#{eIdx}"
                    : (hasChannel
                        ? (barCount == 1 ? $"⭐通道内 #{sIdx}" : $"⭐含通道 #{sIdx}~#{eIdx}")
                        : (barCount == 1 ? $"Bar #{sIdx}" : $"Bar #{sIdx}~#{eIdx}"));
                lblCardTickProgress.Text = $"{stats.TotalTicks:N0} Ticks (选中)";
                lblCardTickRatio.Text = $"{tickRatioStr} ({stats.BuyTickPct:F1}%:{stats.SellTickPct:F1}%)";
                lblCardTickRatio.ForeColor = lblTickRatioBadge.ForeColor;
                lblCardVolRatio.Text = $"{volRatioStr} ({stats.BuyVolumePct:F1}%:{stats.SellVolumePct:F1}%)";
                lblCardVolRatio.ForeColor = lblVolRatioBadge.ForeColor;

                _lastDefaultTickBadgeText = lblTickBadge.Text;
                _lastDefaultTickBadgeColor = lblTickBadge.ForeColor;
            }

            RecentSubPeriodTrendLineResult? selDynTrendLines = null;
            if (_settings.ShowTickRecentTrendLines && (hasChannel || primaryTrend != null || _channelTrendLineRecords.Count > 0))
            {
                if (primaryTrend != null && _channelTrendLineRecords.TryGetValue(primaryTrend.Id, out var chRec))
                {
                    selDynTrendLines = new RecentSubPeriodTrendLineResult
                    {
                        SubPeriodSpan = span ?? TimeSpan.FromMinutes(1),
                        SelectedLines = chRec.SelectedLines,
                        Peaks = chRec.Peaks,
                        Valleys = chRec.Valleys,
                        ResistanceLines = chRec.ResistanceLines,
                        SupportLines = chRec.SupportLines,
                        ChannelRecords = new[] { chRec }
                    };
                }
                else if (primaryTrend != null)
                {
                    var newChRec = RecentSubPeriodTrendLineDetector.GenerateForChannel(
                        primaryTrend,
                        1,
                        _engine.Buckets,
                        primaryTrend.EndIndex,
                        0,
                        span ?? TimeSpan.FromMinutes(1),
                        maxSmallBars: 300);

                    if (newChRec != null)
                    {
                        selDynTrendLines = new RecentSubPeriodTrendLineResult
                        {
                            SubPeriodSpan = span ?? TimeSpan.FromMinutes(1),
                            SelectedLines = newChRec.SelectedLines,
                            Peaks = newChRec.Peaks,
                            Valleys = newChRec.Valleys,
                            ResistanceLines = newChRec.ResistanceLines,
                            SupportLines = newChRec.SupportLines,
                            ChannelRecords = new[] { newChRec }
                        };
                    }
                }
                else if (hasChannel)
                {
                    selDynTrendLines = RecentSubPeriodTrendLineDetector.DetectFromTicks(
                        aggregatedTicks,
                        span ?? TimeSpan.FromMinutes(1),
                        maxSmallBars: 300);
                }
            }

            // 绘制微观走势 (蜡烛图或折线图，含副图与 HUD)
            TickPlotHelper.BuildTickPlot(
                formsPlotTick.Plot,
                aggregatedTicks,
                aggregatedTicks.Count,
                rangeStart,
                rangeEnd,
                cboCoin.Text,
                periodLabel == "全部" ? GetSelectedPeriodTitle() : periodLabel,
                customTitle: customTitle,
                boundaryTickIndices: span.HasValue ? null : boundaryIndices,
                precomputedStats: stats,
                ratioProximity: _settings.RatioProximity,
                subPeriodSpan: span,
                displayType: displayType,
                showConsecutiveTrend: _settings.ShowTickConsecutiveTrend,
                consecutiveMinBars: _settings.ConsecutiveMinBars,
                consecutiveMinPct: _settings.ConsecutiveMinPct,
                showTickChannel: _settings.ShowTickChannel,
                tickConsecutiveMinBars: _settings.TickConsecutiveMinBars,
                tickConsecutiveMinPct: _settings.TickConsecutiveMinPct,
                showRatio: _settings.ShowTickRatio,
                showAngleLines: _settings.ShowTickAngleLines,
                customAngles: PeriodPlaybackSettings.ParseAngles(_settings.CustomAngles),
                selectedTrend: (_settings.ShowTickConsecutiveTrend || _settings.ShowTickAngleLines) ? primaryTrend : null,
                activeChannels: (_settings.ShowTickConsecutiveTrend || _settings.ShowTickAngleLines) ? relevantChannels : null,
                selectedBarStartIndex: sIdx,
                selectedBarEndIndex: eIdx,
                macroBarTimes: _engine.Buckets.Select(b => (b.StartTime, b.EndTime)).ToList(),
                hoverIndicator: _tickHoverIndicator,
                showRecentTrendLines: _settings.ShowTickRecentTrendLines && (selDynTrendLines?.HasLines ?? false),
                recentTrendLineResult: selDynTrendLines,
                selectedMicroBarStartIndex: _selectedMicroBarStartIndex,
                selectedMicroBarEndIndex: _selectedMicroBarEndIndex,
                autoFollow: _tickAutoFollow);

            _currentMicroBuckets = TickPlotHelper.LastSubBuckets;
            formsPlotTick.Refresh();

            if (!_selectedMicroBarStartIndex.HasValue)
            {
                // 填充右侧最近 Tick 表格 (展示选中集合的最近 100 笔流水)
                int showCount = Math.Min(100, aggregatedTicks.Count);
                int startIdx = aggregatedTicks.Count - showCount;

                dgvRecentTicks.Rows.Clear();
                dgvRecentTicks.SuspendLayout();

                for (int i = aggregatedTicks.Count - 1; i >= startIdx; i--)
                {
                    var t = aggregatedTicks[i];
                    DateTime time = DateTimeOffset.FromUnixTimeMilliseconds(t.Time).UtcDateTime;
                    bool isBuy = !t.IsBuyerMaker;
                    string side = isBuy ? "买入 (Buy)" : "卖出 (Sell)";

                    int rowIdx = dgvRecentTicks.Rows.Add(
                        i + 1,
                        time.ToString("HH:mm:ss.fff"),
                        t.Price.ToString("F2"),
                        t.Qty.ToString("F4"),
                        t.QuoteQty.ToString("F2"),
                        side);

                    dgvRecentTicks.Rows[rowIdx].DefaultCellStyle.ForeColor = isBuy
                        ? Color.FromArgb(74, 222, 128)
                        : Color.FromArgb(248, 113, 113);
                }

                dgvRecentTicks.ResumeLayout();

                if (hasChannel)
                {
                    foreach (var tr in relevantChannels)
                    {
                        OutputConsecutiveTrendDetails(tr, stats, aggregatedTicks.Count, rangeStart, rangeEnd);
                    }
                }
                else
                {
                    AppendLog($"[K线选中] 已切换微观视窗至 Bar #{sIdx}{(barCount > 1 ? $" ~ #{eIdx} (共 {barCount} 根)" : "")} [{periodLabel}]，共 {aggregatedTicks.Count:N0} 笔 Tick | 🎯Tick多空比: {tickRatioStr} (买{stats.BuyTickPct:F1}%:卖{stats.SellTickPct:F1}%) | 📊成交量比: {volRatioStr} (买{stats.BuyVolumePct:F1}%:卖{stats.SellVolumePct:F1}%, 净买量{TickLongShortStats.FormatVolume(stats.NetVolume)})", Color.FromArgb(56, 189, 248));
                }
            }
        }

        private void OutputConsecutiveTrendDetails(
            MacroConsecutiveTrendItem tr,
            TickLongShortStats? tickStats = null,
            int tickCount = 0,
            DateTime? rangeStart = null,
            DateTime? rangeEnd = null)
        {
            bool isBull = tr.IsBullish;
            Color themeColor = isBull ? Color.FromArgb(74, 222, 128) : Color.FromArgb(248, 113, 113);
            string dirName = isBull ? "▲ 连续上涨波段 (多头通道)" : "▼ 连续下跌波段 (空头通道)";

            DateTime tStart = rangeStart ?? (tr.StartIndex < _engine.Buckets.Count ? _engine.Buckets[tr.StartIndex].StartTime : DateTime.MinValue);
            DateTime tEnd = rangeEnd ?? (tr.EndIndex < _engine.Buckets.Count ? _engine.Buckets[tr.EndIndex].EndTime : DateTime.MinValue);

            string timeRangeStr = tStart != DateTime.MinValue && tEnd != DateTime.MinValue
                ? $"{tStart:yyyy-MM-dd HH:mm} ~ {tEnd:yyyy-MM-dd HH:mm}"
                : "时间范围获取中";

            decimal priceDiff = tr.EndPrice - tr.StartPrice;
            decimal priceRange = tr.MaxHigh - tr.MinLow;
            decimal channelH = tr.ChannelHeight;
            decimal channelHPct = tr.StartPrice > 0 ? (channelH / tr.StartPrice * 100m) : 0m;

            AppendLog($"🌟 [通道 #{tr.StartIndex}~#{tr.EndIndex}] {dirName} | {tr.BarCount} 根K线 ({timeRangeStr}) | 价格走势: {tr.StartPrice:F2} -> {tr.EndPrice:F2} ({tr.PriceChangePct:+0.00;-0.00;0.00}%)", themeColor);

            if (tr.HasChannel)
            {
                AppendLog($"   📏 拟合: 基准{tr.ChannelBaseBars}根 | 斜率 k={tr.SlopeK:+0.000000;-0.000000;0.000000} | 通道高 {channelH:F2} USDT (约 {channelHPct:F2}%)", Color.FromArgb(168, 85, 247));
            }

            if (_settings.ShowAngleLines)
            {
                string signStr = tr.IsBullish ? "+" : "-";
                AppendLog($"   📐 趋势线: 第一根 #{tr.StartIndex} 高低双锚点 | 射线角度: {signStr}25°, {signStr}45°, {signStr}65°", Color.FromArgb(168, 85, 247));
            }

            if (tickStats.HasValue)
            {
                var ts = tickStats.Value;
                string tR = ts.TickRatio >= 999.0 ? "∞" : ts.TickRatio.ToString("F2");
                string vR = ts.VolumeRatio >= 999.0 ? "∞" : ts.VolumeRatio.ToString("F2");
                Color tickColor = ts.TickRatio >= 1.0 ? Color.FromArgb(74, 222, 128) : Color.FromArgb(248, 113, 113);
                AppendLog($"   🎯 统计: {tickCount:N0} Ticks | 多空比 {tR} (买{ts.BuyTickPct:F0}%:卖{ts.SellTickPct:F0}%) | 量比 {vR} (净买量 {TickLongShortStats.FormatVolume(ts.NetVolume)})", tickColor);
            }
        }

        private void ClearBarSelection()
        {
            _tickAutoFollow = true;
            if (!_selectedBarStartIndex.HasValue && !_selectedBarEndIndex.HasValue && _selectedConsecutiveTrend == null)
            {
                UpdateLiveFollowButtonState(isLive: true);
                RefreshTickPlotDirectly();
                return;
            }

            _selectedBarAnchor = null;
            _selectedBarStartIndex = null;
            _selectedBarEndIndex = null;
            _selectedConsecutiveTrend = null;
            _selectedMicroBarAnchor = null;
            _selectedMicroBarStartIndex = null;
            _selectedMicroBarEndIndex = null;
            if (tabDashboard != null) tabDashboard.Text = "实时仪表盘 & 逐笔流水";
            UpdateLiveFollowButtonState(isLive: true);
            lblTickRatioBadge.Visible = false;
            lblVolRatioBadge.Visible = false;

            lblTickTitle.Text = "微观逐笔走势";
            var curBucket = _engine.CurrentBucket;
            if (curBucket != null)
            {
                int cursor = Math.Min(curBucket.Ticks.Count, _engine.CurrentTickIndex);
                lblTickBadge.Text = $"[实时回放] 周期 #{_engine.CurrentBucketIndex + 1}/{_engine.TotalBuckets}: {FormatTimeSpanRange(curBucket.StartTime, curBucket.EndTime)} ({cursor:N0}/{curBucket.Ticks.Count:N0} T)";
            }
            else
            {
                lblTickBadge.Text = "当前无活动周期";
            }
            lblTickBadge.ForeColor = Color.FromArgb(52, 211, 153);
            _lastDefaultTickBadgeText = lblTickBadge.Text;
            RepositionTickHeaderControls();

            _macroPlotNeedsRefresh = true;
            _tickPlotNeedsRefresh = true;

            RenderLiveTickPlot();
        }

        private void UpdateLiveFollowButtonState(bool isLive)
        {
            if (btnResumeLiveFollow == null) return;
            if (isLive)
            {
                if (_tickAutoFollow)
                {
                    btnResumeLiveFollow.Text = "🟢 实时跟随中";
                    btnResumeLiveFollow.Size = new Size(100, 24);
                    btnResumeLiveFollow.BackColor = Color.FromArgb(15, 23, 42);
                    btnResumeLiveFollow.ForeColor = Color.FromArgb(52, 211, 153);
                    btnResumeLiveFollow.FlatAppearance.BorderColor = Color.FromArgb(30, 41, 59);
                    btnResumeLiveFollow.FlatAppearance.BorderSize = 1;
                    btnResumeLiveFollow.Cursor = Cursors.Default;
                    _toolTip.SetToolTip(btnResumeLiveFollow, "微观视窗当前正在跟随最新实时播放的 Tick 数据。拖拽或滚轮可自由缩放平移视口。点击上方 K 线可切出巡检历史周期的微观 Tick。");
                }
                else
                {
                    btnResumeLiveFollow.Text = "🔍 视口自定义 (点击重置)";
                    btnResumeLiveFollow.Size = new Size(160, 24);
                    btnResumeLiveFollow.BackColor = Color.FromArgb(30, 41, 59);
                    btnResumeLiveFollow.ForeColor = Color.FromArgb(251, 191, 36);
                    btnResumeLiveFollow.FlatAppearance.BorderColor = Color.FromArgb(245, 158, 11);
                    btnResumeLiveFollow.FlatAppearance.BorderSize = 1;
                    btnResumeLiveFollow.Cursor = Cursors.Hand;
                    _toolTip.SetToolTip(btnResumeLiveFollow, "微观视窗当前已被手动平移/缩放，不会强行重置视口。点击此处或双击图表可恢复视口自动跟随。");
                }
            }
            else
            {
                btnResumeLiveFollow.Text = "▶ 切换到实时播放";
                btnResumeLiveFollow.Size = new Size(125, 24);
                btnResumeLiveFollow.BackColor = Color.FromArgb(16, 185, 129);
                btnResumeLiveFollow.ForeColor = Color.White;
                btnResumeLiveFollow.FlatAppearance.BorderColor = Color.FromArgb(52, 211, 153);
                btnResumeLiveFollow.FlatAppearance.BorderSize = 1;
                btnResumeLiveFollow.Cursor = Cursors.Hand;
                _toolTip.SetToolTip(btnResumeLiveFollow, "微观视窗当前正在查看历史 K 线的 Tick (上方宏观回放持续运行)。点击此处或双击图表可立即切回跟随最新实时播放。");
            }
            btnResumeLiveFollow.Visible = true;
            RepositionTickHeaderControls();
        }

        #endregion

        #region 日志输出与配置持久化

        private void AppendLog(string message, Color color)
        {
            _logQueue.Enqueue((message, color));
        }

        private void AppendLogInternal(string message, Color color)
        {
            if (txtLogs.IsDisposed) return;

            // 限制最大行数 (保留最新 500 行)，防止长时间运行导致内存与控件排版卡顿
            if (txtLogs.Lines.Length > 800)
            {
                string[] lines = txtLogs.Lines;
                string[] trimmed = new string[400];
                Array.Copy(lines, lines.Length - 400, trimmed, 0, 400);
                txtLogs.Lines = trimmed;
            }

            string timeStr = DateTime.Now.ToString("HH:mm:ss");
            txtLogs.SelectionStart = txtLogs.TextLength;
            txtLogs.SelectionLength = 0;
            txtLogs.SelectionColor = Color.FromArgb(148, 163, 184);
            txtLogs.AppendText($"[{timeStr}] ");

            txtLogs.SelectionColor = color;
            txtLogs.AppendText(message + "\n");
            txtLogs.ScrollToCaret();
        }

        private void ApplySettingsToUi()
        {
            _isApplyingSettings = true;
            try
            {
                // 1. 币种
                if (!string.IsNullOrEmpty(_settings.Coin))
                {
                    int coinIdx = cboCoin.FindStringExact(_settings.Coin);
                    if (coinIdx >= 0)
                    {
                        cboCoin.SelectedIndex = coinIdx;
                    }
                    else
                    {
                        cboCoin.Items.Add(_settings.Coin);
                        cboCoin.SelectedItem = _settings.Coin;
                    }
                }

                // 2. 起止日期
                if (_settings.StartDate >= dtpStart.MinDate && _settings.StartDate <= dtpStart.MaxDate)
                    dtpStart.Value = _settings.StartDate;
                if (_settings.EndDate >= dtpEnd.MinDate && _settings.EndDate <= dtpEnd.MaxDate)
                    dtpEnd.Value = _settings.EndDate;

                // 3. 周期与自定义分/秒
                if (_settings.PeriodIndex >= 0 && _settings.PeriodIndex < cboPeriod.Items.Count)
                {
                    cboPeriod.SelectedIndex = _settings.PeriodIndex;
                }
                numCustomMinutes.Value = Math.Clamp(_settings.CustomMinutes, 1, 1440);
                numCustomMinutes.Visible = (cboPeriod.SelectedIndex == 9);
                numCustomSeconds.Value = Math.Clamp(_settings.CustomSeconds, 1, 3600);
                numCustomSeconds.Visible = (cboPeriod.SelectedIndex == 10);

                // 4. 倍速
                if (_settings.SpeedIndex >= 0 && _settings.SpeedIndex < cboSpeed.Items.Count)
                {
                    cboSpeed.SelectedIndex = _settings.SpeedIndex;
                }
                UpdatePlaybackSpeed();

                // 5. 选项复选框
                chkAutoFollow.Checked = _settings.AutoFollow;
                chkShowVolume.Checked = _settings.ShowVolume;
                chkAutoAppend.Checked = _settings.AutoAppendNextBatch;

                // 6. 图表类型与切换按钮同步
                if (_settings.ChartTypeIndex >= 0 && _settings.ChartTypeIndex < cboChartType.Items.Count)
                {
                    cboChartType.SelectedIndex = _settings.ChartTypeIndex;
                }
                bool isLine = cboChartType.SelectedIndex == 1;
                if (btnToggleChartType != null)
                {
                    btnToggleChartType.Text = isLine ? "切换为蜡烛图" : "切换为折线图";
                    btnToggleChartType.ForeColor = isLine ? Color.FromArgb(74, 222, 128) : Color.FromArgb(56, 189, 248);
                    btnToggleChartType.FlatAppearance.BorderColor = btnToggleChartType.ForeColor;
                }
                lblMacroTitle.Text = isLine
                    ? "大周期收盘折线走势图 (纯净无额外 MA 指标，含成交量副图)"
                    : "大周期 K 线走势图 (纯净无额外 MA 指标，含成交量副图)";

                // 7. 连续涨跌形态标记参数
                chkConsecutiveTrend.Checked = _settings.ShowConsecutiveTrend;
                if (chkAngleLines != null) chkAngleLines.Checked = _settings.ShowMacroAngleLines;
                if (chkTickAngleLines != null) chkTickAngleLines.Checked = _settings.ShowTickAngleLines;
                if (chkMacroRecentTrendLines != null) chkMacroRecentTrendLines.Checked = _settings.ShowMacroRecentTrendLines;
                if (chkTickRecentTrendLines != null) chkTickRecentTrendLines.Checked = _settings.ShowTickRecentTrendLines;
                if (txtCustomAngles != null) txtCustomAngles.Text = _settings.CustomAngles;
                numConsecutiveBars.Value = Math.Clamp(_settings.ConsecutiveMinBars, 2, 50);
                numConsecutivePct.Value = Math.Clamp(_settings.ConsecutiveMinPct, 0.1m, 50.0m);

                // 8. 价格与比值曲线凑近对齐状态
                UpdateProximityButtonState();

                // 9. 微观 Tick 周期与自定义秒/分
                numTickCustomSeconds.Value = Math.Clamp(_settings.TickCustomSeconds, 1, 3600);
                numTickCustomMinutes.Value = Math.Clamp(_settings.TickCustomMinutes, 1, 1440);
                SelectTickPeriodButton(_settings.TickPeriodIndex);

                // 10. 微观 Tick 图表类型 (0: 蜡烛图, 1: 折线图)
                UpdateTickChartTypeButtonState();

                // 11. 微观 Tick 是否显示多空比值
                if (chkShowTickRatio != null)
                {
                    chkShowTickRatio.Checked = _settings.ShowTickRatio;
                    btnRatioProximity.Visible = _settings.ShowTickRatio;
                    lblTickRatioBadge.Visible = _settings.ShowTickRatio;
                    lblVolRatioBadge.Visible = _settings.ShowTickRatio;
                }

                // 12. 微观 Tick 是否显示大周期平行通道与独立微观通道设置
                if (chkTickConsecutiveTrend != null)
                {
                    chkTickConsecutiveTrend.Checked = _settings.ShowTickConsecutiveTrend;
                }
                if (chkTickChannel != null)
                {
                    chkTickChannel.Checked = _settings.ShowTickChannel;
                }
                if (lblTickConsecutiveBars != null)
                {
                    lblTickConsecutiveBars.Enabled = _settings.ShowTickChannel;
                }
                if (numTickConsecutiveBars != null)
                {
                    numTickConsecutiveBars.Enabled = _settings.ShowTickChannel;
                    numTickConsecutiveBars.Value = Math.Clamp(_settings.TickConsecutiveMinBars, numTickConsecutiveBars.Minimum, numTickConsecutiveBars.Maximum);
                }
                if (lblTickConsecutivePct != null)
                {
                    lblTickConsecutivePct.Enabled = _settings.ShowTickChannel;
                }
                if (numTickConsecutivePct != null)
                {
                    numTickConsecutivePct.Enabled = _settings.ShowTickChannel;
                    numTickConsecutivePct.Value = Math.Clamp(_settings.TickConsecutiveMinPct, numTickConsecutivePct.Minimum, numTickConsecutivePct.Maximum);
                }

                // 13. 详细定型日志开关
                if (chkVerboseLog != null)
                {
                    chkVerboseLog.Checked = _settings.VerboseLog;
                }
                _engine.VerboseLog = _settings.VerboseLog;

                AppendLog($"[配置恢复] 已成功恢复上次界面配置: {cboCoin.Text} | {GetSelectedPeriodTitle()} | 微观周期: {GetSelectedTickPeriodTitle()} | 倍速: {cboSpeed.Text} | 自动跟随: {(chkAutoFollow.Checked ? "开启" : "关闭")}", Color.FromArgb(74, 222, 128));
            }
            finally
            {
                _isApplyingSettings = false;
            }
        }

        private void UpdateProximityButtonState()
        {
            if (btnRatioProximity == null) return;
            if (_settings.RatioProximity)
            {
                btnRatioProximity.Text = "🎯 凑近比值: 开";
                btnRatioProximity.BackColor = Color.FromArgb(2, 132, 199);
                btnRatioProximity.ForeColor = Color.FromArgb(241, 245, 249);
            }
            else
            {
                btnRatioProximity.Text = "🎯 凑近比值: 关";
                btnRatioProximity.BackColor = Color.FromArgb(51, 65, 85);
                btnRatioProximity.ForeColor = Color.FromArgb(148, 163, 184);
            }
        }

        private void RefreshTickPlotDirectly()
        {
            try
            {
                // 若处于选定历史 K 线态，重绘当前选定区间的聚合 Tick
                if (_selectedBarStartIndex.HasValue && _selectedBarEndIndex.HasValue)
                {
                    DisplaySelectedBarsTicks();
                    return;
                }

                // 否则重绘实时当前回放桶的 Tick
                RenderLiveTickPlot();
            }
            catch { }
        }

        private void SaveSettingsFromUi()
        {
            if (_isApplyingSettings) return;

            _settings.Coin = cboCoin.Text;
            _settings.StartDate = dtpStart.Value.Date;
            _settings.EndDate = dtpEnd.Value.Date;
            _settings.PeriodIndex = cboPeriod.SelectedIndex;
            _settings.CustomMinutes = (int)numCustomMinutes.Value;
            _settings.CustomSeconds = (int)numCustomSeconds.Value;
            _settings.SpeedIndex = cboSpeed.SelectedIndex;
            _settings.AutoFollow = chkAutoFollow.Checked;
            _settings.ShowVolume = chkShowVolume.Checked;
            _settings.ChartTypeIndex = cboChartType.SelectedIndex;
            _settings.AutoAppendNextBatch = chkAutoAppend.Checked;

            // 连续涨跌形态标记参数
            _settings.ShowConsecutiveTrend = chkConsecutiveTrend.Checked;
            if (chkAngleLines != null) _settings.ShowMacroAngleLines = chkAngleLines.Checked;
            if (chkTickAngleLines != null) _settings.ShowTickAngleLines = chkTickAngleLines.Checked;
            if (chkMacroRecentTrendLines != null) _settings.ShowMacroRecentTrendLines = chkMacroRecentTrendLines.Checked;
            if (chkTickRecentTrendLines != null) _settings.ShowTickRecentTrendLines = chkTickRecentTrendLines.Checked;
            if (txtCustomAngles != null && !string.IsNullOrWhiteSpace(txtCustomAngles.Text)) _settings.CustomAngles = txtCustomAngles.Text.Trim();
            _settings.ConsecutiveMinBars = (int)numConsecutiveBars.Value;
            _settings.ConsecutiveMinPct = numConsecutivePct.Value;

            // 价格与比值凑近配置
            _settings.RatioProximity = btnRatioProximity != null && btnRatioProximity.Text.Contains("开");

            // 微观 Tick 周期与自定义秒/分
            _settings.TickPeriodIndex = _selectedTickPeriodIndex;
            _settings.TickCustomSeconds = (int)numTickCustomSeconds.Value;
            _settings.TickCustomMinutes = (int)numTickCustomMinutes.Value;

            // 微观 Tick 图表类型 (0: 蜡烛图, 1: 折线图)
            _settings.TickChartTypeIndex = Math.Clamp(_settings.TickChartTypeIndex, 0, 1);

            // 微观 Tick 是否显示多空比值
            if (chkShowTickRatio != null)
            {
                _settings.ShowTickRatio = chkShowTickRatio.Checked;
            }

            // 微观 Tick 窗口大周期通道与独立通道开关
            if (chkTickConsecutiveTrend != null)
            {
                _settings.ShowTickConsecutiveTrend = chkTickConsecutiveTrend.Checked;
            }
            if (chkTickChannel != null)
            {
                _settings.ShowTickChannel = chkTickChannel.Checked;
            }
            if (numTickConsecutiveBars != null)
            {
                _settings.TickConsecutiveMinBars = (int)numTickConsecutiveBars.Value;
            }
            if (numTickConsecutivePct != null)
            {
                _settings.TickConsecutiveMinPct = numTickConsecutivePct.Value;
            }

            // 详细定型日志开关
            if (chkVerboseLog != null)
            {
                _settings.VerboseLog = chkVerboseLog.Checked;
            }

            try
            {
                _settings.SplitMainDistance = splitMain.SplitterDistance;
                _settings.SplitBottomDistance = splitBottom.SplitterDistance;
            }
            catch
            {
            }

            _settings.Save();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveSettingsFromUi();
            base.OnFormClosing(e);
        }

        #endregion
    }
}
