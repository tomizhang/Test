using Common;
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
        private Button btnLoad = null!;
        private Button btnNextBatch = null!;
        private Button btnPlay = null!;
        private Button btnPause = null!;
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

        // 微观 Tick 窗口独立时间周期与图表类型切换控制器
        private Label lblTickPeriod = null!;
        private ComboBox cboTickPeriod = null!;
        private NumericUpDown numTickCustomMinutes = null!;
        private Button btnToggleTickChartType = null!;
        private CheckBox chkShowTickRatio = null!;

        // K线点击与Shift多选状态
        private int? _selectedBarAnchor = null;
        private int? _selectedBarStartIndex = null;
        private int? _selectedBarEndIndex = null;
        private Point _macroMouseDownPoint;

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

            AppendLog("[系统就绪] 大周期与逐笔 Tick 嵌套流式回放系统已启动！", Color.FromArgb(74, 222, 128));
            AppendLog("[核心规则] 播放 30 分钟 (或指定大周期) 时，将先播放完毕该 30 分钟内所有的逐笔 Tick，然后再向大图定型输出对应的 30 分钟 K 线。", Color.FromArgb(56, 189, 248));
            AppendLog("[大图指标] 遵循用户指定规范：大图除了成交量副图，不添加任何 MA 均线或其它干扰指标，纯净展现金融蜡烛体。", Color.FromArgb(250, 204, 21));
            AppendLog("[显示类型] 支持在【蜡烛图】与【收盘折线图】间随时自由切换，满足多样化盯盘看盘需求。", Color.FromArgb(56, 189, 248));
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
                "自定义分钟"
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
            cboPeriod.SelectedIndexChanged += (s, e) =>
            {
                numCustomMinutes.Visible = cboPeriod.SelectedIndex == 9;
                SaveSettingsFromUi();
            };
            numCustomMinutes.ValueChanged += (s, e) => SaveSettingsFromUi();

            // 分批载入按钮：首批数据
            btnLoad = CreateButton("载入首批", 676, 5, 80, 26, Color.FromArgb(2, 132, 199));
            btnLoad.Click += async (s, e) => await LoadFirstBatchAsync();

            // 载入下一批按钮
            btnNextBatch = CreateButton("载入下批", 762, 5, 84, 26, Color.FromArgb(14, 165, 233));
            btnNextBatch.Enabled = false;
            btnNextBatch.Click += async (s, e) => await LoadNextBatchAsync(isBackgroundPreload: false);

            chkAutoAppend = new CheckBox
            {
                Text = "自动追加下批",
                Location = new Point(854, 8),
                AutoSize = true,
                Checked = true,
                ForeColor = Color.FromArgb(74, 222, 128),
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold)
            };
            chkAutoAppend.CheckedChanged += (s, e) => SaveSettingsFromUi();

            lblBatchBadge = CreateLabel("批次: 准备就绪", 965, 9);
            lblBatchBadge.ForeColor = Color.FromArgb(56, 189, 248);
            lblBatchBadge.Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold);

            // ==================== Row 2: 播放控制与显示设置 (y = 35) ====================
            btnPlay = CreateButton("播放", 10, 35, 64, 26, Color.FromArgb(16, 185, 129));
            btnPlay.Click += (s, e) =>
            {
                if (!chkAutoFollow.Checked)
                {
                    chkAutoFollow.Checked = true;
                }
                ClearBarSelection();
                _engine.Play();
            };

            btnPause = CreateButton("暂停", 80, 35, 64, 26, Color.FromArgb(217, 119, 6));
            btnPause.Click += (s, e) => _engine.Pause();

            btnStop = CreateButton("停止", 150, 35, 64, 26, Color.FromArgb(225, 29, 72));
            btnStop.Click += (s, e) => _engine.Stop();

            btnNextBucket = CreateButton("完成当前周期", 220, 35, 102, 26, Color.FromArgb(124, 58, 237));
            btnNextBucket.Click += (s, e) => _engine.FastForwardCurrentBucket();

            btnStepTick = CreateButton("单步Tick", 328, 35, 74, 26, Color.FromArgb(71, 85, 105));
            btnStepTick.Click += (s, e) => _engine.StepTick(1);

            var lblSpeed = CreateLabel("倍速:", 410, 38);
            cboSpeed = new ComboBox
            {
                Location = new Point(448, 35),
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
                Location = new Point(534, 38),
                AutoSize = true,
                Checked = true,
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
                Location = new Point(616, 38),
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

            var lblChartType = CreateLabel("显示类型:", 684, 38);
            cboChartType = new ComboBox
            {
                Location = new Point(748, 35),
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

            pnlTop.Controls.AddRange(new Control[]
            {
                lblCoin, cboCoin,
                lblStart, dtpStart,
                lblEnd, dtpEnd,
                lblPeriod, cboPeriod, numCustomMinutes,
                btnLoad, btnNextBatch,
                chkAutoAppend, lblBatchBadge,
                btnPlay, btnPause, btnStop, btnNextBucket, btnStepTick,
                lblSpeed, cboSpeed,
                chkAutoFollow, chkShowVolume,
                lblChartType, cboChartType,
                chkConsecutiveTrend, lblConsecutiveBars, numConsecutiveBars, lblConsecutivePct, numConsecutivePct,
                lblProgOverall, prgOverall, lblProgBucket, prgBucket
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
                Location = new Point(380, 7),
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

            lblMacroTitle.SizeChanged += (s, e) => lblMacroBadge.Left = lblMacroTitle.Right + 15;
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
                Height = 34,
                BackColor = Color.FromArgb(30, 41, 59),
                Padding = new Padding(8, 5, 8, 4)
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

            cboTickPeriod = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Microsoft YaHei", 8.0F, FontStyle.Bold),
                Width = 115,
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.FromArgb(226, 232, 240)
            };
            cboTickPeriod.Items.AddRange(new object[]
            {
                "全部 (当前大周期)",
                "1分钟 (1m)",
                "3分钟 (3m)",
                "5分钟 (5m)",
                "15分钟 (15m)",
                "30分钟 (30m)",
                "1小时 (1h)",
                "自定义分钟"
            });
            cboTickPeriod.SelectedIndex = 0;
            cboTickPeriod.SelectedIndexChanged += (s, e) =>
            {
                numTickCustomMinutes.Visible = cboTickPeriod.SelectedIndex == 7;
                RepositionTickHeaderControls();
                SaveSettingsFromUi();
                RefreshTickPlotDirectly();
            };

            numTickCustomMinutes = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 1440,
                Value = 5,
                Width = 48,
                Font = new Font("Microsoft YaHei", 8.0F),
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.FromArgb(226, 232, 240),
                Visible = false
            };
            numTickCustomMinutes.ValueChanged += (s, e) =>
            {
                SaveSettingsFromUi();
                RefreshTickPlotDirectly();
            };

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
                Text = "恢复实时",
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(241, 245, 249),
                BackColor = Color.FromArgb(37, 99, 235),
                FlatStyle = FlatStyle.Flat,
                Size = new Size(85, 24),
                Cursor = Cursors.Hand,
                Visible = false
            };
            btnResumeLiveFollow.FlatAppearance.BorderSize = 0;
            btnResumeLiveFollow.Click += (s, e) => ClearBarSelection();

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
            chkShowTickRatio.SizeChanged += (s, e) => RepositionTickHeaderControls();
            lblTickRatioBadge.SizeChanged += (s, e) => RepositionTickHeaderControls();
            lblVolRatioBadge.SizeChanged += (s, e) => RepositionTickHeaderControls();
            btnResumeLiveFollow.VisibleChanged += (s, e) => RepositionTickHeaderControls();
            pnlTickHeader.Resize += (s, e) => RepositionTickHeaderControls();

            pnlTickHeader.Controls.AddRange(new Control[]
            {
                lblTickTitle,
                lblTickPeriod,
                cboTickPeriod,
                numTickCustomMinutes,
                btnToggleTickChartType,
                chkShowTickRatio,
                lblTickBadge,
                lblTickRatioBadge,
                lblVolRatioBadge,
                btnRatioProximity,
                btnResumeLiveFollow
            });

            formsPlotTick = new FormsPlot { Dock = DockStyle.Fill, BackColor = Color.FromArgb(15, 23, 42) };
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
                Height = 110,
                BackColor = Color.FromArgb(30, 41, 59),
                Padding = new Padding(6),
                AutoScroll = true
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
            pnlLogHeader.Controls.AddRange(new Control[] { lblLTitle, btnClearLogs });

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
                Size = new Size(135, 46),
                BackColor = Color.FromArgb(15, 23, 42),
                Margin = new Padding(3),
                Padding = new Padding(5, 3, 5, 3)
            };
            var lblTitle = new Label
            {
                Text = title,
                ForeColor = Color.FromArgb(148, 163, 184),
                Font = new Font("Microsoft YaHei", 7.5F),
                Location = new Point(4, 3),
                AutoSize = true
            };
            var lblValue = new Label
            {
                Text = initialValue,
                ForeColor = accentColor,
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold),
                Location = new Point(4, 21),
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

                lblTickBadge.Text = $"准备就绪 (第 1 周期: {firstBatchBuckets[0].StartTime:HH:mm} ~ {firstBatchBuckets[0].EndTime:HH:mm}, 共 {firstBatchBuckets[0].TickCount:N0} Ticks)";

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
                9 => TimeSpan.FromMinutes((double)numCustomMinutes.Value),
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
                _ => "30分钟"
            };
        }

        private TimeSpan? GetSelectedTickPeriodSpan()
        {
            return cboTickPeriod.SelectedIndex switch
            {
                0 => null, // 全部 (当前大周期)
                1 => TimeSpan.FromMinutes(1),
                2 => TimeSpan.FromMinutes(3),
                3 => TimeSpan.FromMinutes(5),
                4 => TimeSpan.FromMinutes(15),
                5 => TimeSpan.FromMinutes(30),
                6 => TimeSpan.FromHours(1),
                7 => TimeSpan.FromMinutes((double)numTickCustomMinutes.Value),
                _ => null
            };
        }

        private string GetSelectedTickPeriodTitle()
        {
            return cboTickPeriod.SelectedIndex switch
            {
                0 => "全部",
                1 => "1分钟",
                2 => "3分钟",
                3 => "5分钟",
                4 => "15分钟",
                5 => "30分钟",
                6 => "1小时",
                7 => $"{numTickCustomMinutes.Value}分钟",
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

            lblTickTitle.Location = new Point(10, 7);
            lblTickPeriod.Location = new Point(lblTickTitle.Right + 10, 8);
            cboTickPeriod.Location = new Point(lblTickPeriod.Right + 4, 5);

            int flowX = cboTickPeriod.Right + 4;
            if (numTickCustomMinutes.Visible)
            {
                numTickCustomMinutes.Location = new Point(flowX, 6);
                flowX = numTickCustomMinutes.Right + 6;
            }

            if (btnToggleTickChartType != null)
            {
                btnToggleTickChartType.Location = new Point(flowX, 4);
                flowX = btnToggleTickChartType.Right + 6;
            }

            if (chkShowTickRatio != null)
            {
                chkShowTickRatio.Location = new Point(flowX, 7);
                flowX = chkShowTickRatio.Right + 8;
            }

            lblTickBadge.Location = new Point(flowX, 7);

            int rightEdge = pnlTickHeader.ClientSize.Width - 10;
            if (btnResumeLiveFollow.Visible)
            {
                btnResumeLiveFollow.Location = new Point(Math.Max(flowX + 10, rightEdge - btnResumeLiveFollow.Width), 4);
                rightEdge -= (btnResumeLiveFollow.Width + 8);
            }
            if (btnRatioProximity.Visible)
            {
                btnRatioProximity.Location = new Point(Math.Max(flowX + 10, rightEdge - btnRatioProximity.Width), 4);
                rightEdge -= (btnRatioProximity.Width + 8);
            }

            if (lblVolRatioBadge.Visible)
            {
                lblVolRatioBadge.Location = new Point(Math.Max(flowX + 10, rightEdge - lblVolRatioBadge.Width), 5);
                rightEdge -= (lblVolRatioBadge.Width + 8);
            }
            if (lblTickRatioBadge.Visible)
            {
                lblTickRatioBadge.Location = new Point(Math.Max(flowX + 10, rightEdge - lblTickRatioBadge.Width), 5);
                rightEdge -= (lblTickRatioBadge.Width + 8);
            }
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
                ? $"微观周期走势 [{periodLabel}] (大周期: {GetSelectedPeriodTitle()})"
                : $"当前大周期微观逐笔 Tick 走势 ({GetSelectedPeriodTitle()})";

            lblTickTitle.Text = customTickTitle;
            lblTickBadge.Text = $"周期 #{_engine.CurrentBucketIndex + 1}/{_engine.TotalBuckets} | {curBucket.StartTime:HH:mm}~{curBucket.EndTime:HH:mm} (当前 {cursor:N0}/{curBucket.Ticks.Count:N0} Ticks)";
            lblTickBadge.Left = (btnToggleTickChartType != null ? btnToggleTickChartType.Right : (numTickCustomMinutes.Visible ? numTickCustomMinutes.Right : cboTickPeriod.Right)) + 10;

            var stats = TickLongShortStats.Calculate(curBucket.Ticks, cursor);
            string tR = stats.TickRatio >= 999.0 ? "∞" : stats.TickRatio.ToString("F2");
            string vR = stats.VolumeRatio >= 999.0 ? "∞" : stats.VolumeRatio.ToString("F2");

            if (cursor == 0)
            {
                lblTickRatioBadge.Text = "🎯 Tick多空比: --";
                lblTickRatioBadge.ForeColor = Color.FromArgb(148, 163, 184);
                lblVolRatioBadge.Text = "📊 成交量比: --";
                lblVolRatioBadge.ForeColor = Color.FromArgb(148, 163, 184);
            }
            else
            {
                lblTickRatioBadge.Text = $"🎯 Tick多空比: {tR} (多{stats.BuyTickPct:F1}% : 空{stats.SellTickPct:F1}%)";
                lblTickRatioBadge.ForeColor = stats.TickRatio >= 1.0 ? Color.FromArgb(74, 222, 128) : Color.FromArgb(248, 113, 113);
                lblVolRatioBadge.Text = $"📊 成交量比: {vR} (多{stats.BuyVolumePct:F1}% : 空{stats.SellVolumePct:F1}%)";
                lblVolRatioBadge.ForeColor = stats.VolumeRatio >= 1.0 ? Color.FromArgb(74, 222, 128) : Color.FromArgb(248, 113, 113);
            }
            lblTickRatioBadge.Visible = _settings.ShowTickRatio;
            lblVolRatioBadge.Visible = _settings.ShowTickRatio;

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
                showConsecutiveTrend: _settings.ShowConsecutiveTrend,
                consecutiveMinBars: _settings.ConsecutiveMinBars,
                consecutiveMinPct: _settings.ConsecutiveMinPct,
                showRatio: _settings.ShowTickRatio);

            formsPlotTick.Refresh();

            // 更新右侧最近 Tick 表格 (最新 25 笔)
            UpdateRecentTicksGrid(curBucket.Ticks, cursor);
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
                    if (!_selectedBarStartIndex.HasValue)
                    {
                        lblTickBadge.Text = $"周期 #{bucketIdx + 1}/{totalBuckets}: {bucket.StartTime:HH:mm} ~ {bucket.EndTime:HH:mm} (共 {bucket.TickCount:N0} Ticks)";
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
            btnPlay.Enabled = state != PlaybackState.Playing && _engine.TotalBuckets > 0;
            btnPause.Enabled = state == PlaybackState.Playing;
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
                        MacroPlotHelper.BuildMacroPlot(
                            formsPlotMacro.Plot,
                            _engine.GetCompletedMacroBarsSnapshot(),
                            _engine.CurrentFormingBar,
                            cboCoin.Text,
                            GetSelectedPeriodTitle(),
                            displayType: (MacroChartDisplayType)cboChartType.SelectedIndex,
                            showVolume: chkShowVolume.Checked,
                            autoFollow: chkAutoFollow.Checked && !_selectedBarStartIndex.HasValue,
                            showConsecutiveTrend: chkConsecutiveTrend.Checked,
                            consecutiveMinBars: (int)numConsecutiveBars.Value,
                            consecutiveMinPct: numConsecutivePct.Value,
                            selectedStartIndex: _selectedBarStartIndex,
                            selectedEndIndex: _selectedBarEndIndex,
                            channelExtensionBars: _settings.ChannelExtensionBars);

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
                        // 🌟 若用户当前正在查看选中的历史 K 线或多选区间，则不被正在播放的 live tick 覆盖
                        if (!_selectedBarStartIndex.HasValue)
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
            if (_selectedBarStartIndex.HasValue) return; // 选中态由 DisplaySelectedBarsTicks 接管

            var curBucket = _engine.CurrentBucket;
            if (curBucket != null)
            {
                lblCardPeriod.Text = $"{curBucket.StartTime:HH:mm} ~ {curBucket.EndTime:HH:mm}";
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
                    AppendLog("[视角平移] 检测到手动拖拽图表视角，已自动临时解除「自动跟随」。若需恢复跟随最新K线，请勾选「自动跟随」或点击「播放」。", Color.FromArgb(250, 204, 21));
                }
                return;
            }

            // 否则判定为点击事件，执行 K 线点击查看与 Shift 连续多选
            int completedCount = _engine.CompletedMacroBars.Count;
            bool hasForming = _engine.CurrentFormingBar.HasTicks;
            int totalDisplayCount = completedCount + (hasForming ? 1 : 0);

            if (totalDisplayCount == 0) return;

            try
            {
                var mouseCoord = formsPlotMacro.Plot.GetCoordinates(new ScottPlot.Pixel(e.X, e.Y));
                int targetIndex = (int)Math.Round(mouseCoord.X);

                if (targetIndex >= 0 && targetIndex < totalDisplayCount)
                {
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
                if (b == _engine.CurrentBucketIndex && _engine.State != PlaybackState.Idle && _engine.CurrentTickIndex < bucket.Ticks.Count)
                {
                    int takeCount = Math.Max(1, _engine.CurrentTickIndex + 1);
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

            string customTitle = span.HasValue
                ? (barCount == 1
                    ? $"微观周期走势: Bar #{sIdx} [{periodLabel}] - 共 {aggregatedTicks.Count:N0} Ticks"
                    : $"微观周期走势: Bar #{sIdx}~#{eIdx} (共 {barCount} 根) [{periodLabel}] - 共 {aggregatedTicks.Count:N0} Ticks")
                : (barCount == 1
                    ? $"微观 Tick 走势: Bar #{sIdx} ({rangeStart:MM-dd HH:mm} ~ {rangeEnd:HH:mm}) - 共 {aggregatedTicks.Count:N0} Ticks"
                    : $"微观 Tick 走势: Bar #{sIdx} ~ #{eIdx} (共 {barCount} 根K线, {rangeStart:MM-dd HH:mm} ~ {rangeEnd:HH:mm}) - 共 {aggregatedTicks.Count:N0} Ticks");

            lblTickTitle.Text = span.HasValue
                ? (barCount == 1
                    ? $"已选中 Bar #{sIdx} [{periodLabel}]"
                    : $"已选中 Bar #{sIdx}~#{eIdx} [{periodLabel}]")
                : (barCount == 1
                    ? $"已选中 Bar #{sIdx} 内部微观 Tick 走势"
                    : $"已选中 Bar #{sIdx} ~ #{eIdx} (共 {barCount} 根K线) 聚合微观 Tick 走势");

            lblTickBadge.Text = $"时间范围: {rangeStart:MM-dd HH:mm} ~ {rangeEnd:HH:mm} | 共 {aggregatedTicks.Count:N0} 笔 Tick";
            lblTickBadge.Left = (btnToggleTickChartType != null ? btnToggleTickChartType.Right : (numTickCustomMinutes.Visible ? numTickCustomMinutes.Right : cboTickPeriod.Right)) + 10;
            btnResumeLiveFollow.Visible = true;

            // 统计所选区间的 Tick 多空笔数比与成交量多空比
            var stats = TickLongShortStats.Calculate(aggregatedTicks);

            string tickRatioStr = stats.TickRatio >= 999.0 ? "∞" : stats.TickRatio.ToString("F2");
            string volRatioStr = stats.VolumeRatio >= 999.0 ? "∞" : stats.VolumeRatio.ToString("F2");

            if (aggregatedTicks.Count == 0)
            {
                lblTickRatioBadge.Text = "🎯 Tick多空比: --";
                lblTickRatioBadge.ForeColor = Color.FromArgb(148, 163, 184);
                lblVolRatioBadge.Text = "📊 成交量比: --";
                lblVolRatioBadge.ForeColor = Color.FromArgb(148, 163, 184);
            }
            else
            {
                lblTickRatioBadge.Text = $"🎯 Tick多空比: {tickRatioStr} (多{stats.BuyTickPct:F1}% : 空{stats.SellTickPct:F1}%)";
                lblTickRatioBadge.ForeColor = stats.TickRatio >= 1.0 ? Color.FromArgb(74, 222, 128) : Color.FromArgb(248, 113, 113);
                lblVolRatioBadge.Text = $"📊 成交量比: {volRatioStr} (多{stats.BuyVolumePct:F1}% : 空{stats.SellVolumePct:F1}%)";
                lblVolRatioBadge.ForeColor = stats.VolumeRatio >= 1.0 ? Color.FromArgb(74, 222, 128) : Color.FromArgb(248, 113, 113);
            }
            lblTickRatioBadge.Visible = _settings.ShowTickRatio;
            lblVolRatioBadge.Visible = _settings.ShowTickRatio;

            // 更新右侧监控卡片
            lblCardPeriod.Text = barCount == 1 ? $"Bar #{sIdx}" : $"Bar #{sIdx}~#{eIdx}";
            lblCardTickProgress.Text = $"{stats.TotalTicks:N0} Ticks (选中)";
            lblCardTickRatio.Text = $"{tickRatioStr} ({stats.BuyTickPct:F1}%:{stats.SellTickPct:F1}%)";
            lblCardTickRatio.ForeColor = lblTickRatioBadge.ForeColor;
            lblCardVolRatio.Text = $"{volRatioStr} ({stats.BuyVolumePct:F1}%:{stats.SellVolumePct:F1}%)";
            lblCardVolRatio.ForeColor = lblVolRatioBadge.ForeColor;

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
                showConsecutiveTrend: _settings.ShowConsecutiveTrend,
                consecutiveMinBars: _settings.ConsecutiveMinBars,
                consecutiveMinPct: _settings.ConsecutiveMinPct,
                showRatio: _settings.ShowTickRatio);

            formsPlotTick.Refresh();

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

            AppendLog($"[K线选中] 已切换微观视窗至 Bar #{sIdx}{(barCount > 1 ? $" ~ #{eIdx} (共 {barCount} 根)" : "")} [{periodLabel}]，共 {aggregatedTicks.Count:N0} 笔 Tick | 🎯Tick多空比: {tickRatioStr} (买{stats.BuyTickPct:F1}%:卖{stats.SellTickPct:F1}%) | 📊成交量比: {volRatioStr} (买{stats.BuyVolumePct:F1}%:卖{stats.SellVolumePct:F1}%, 净买量{TickLongShortStats.FormatVolume(stats.NetVolume)})", Color.FromArgb(56, 189, 248));
        }

        private void ClearBarSelection()
        {
            if (!_selectedBarStartIndex.HasValue && !_selectedBarEndIndex.HasValue)
            {
                btnResumeLiveFollow.Visible = false;
                RefreshTickPlotDirectly();
                return;
            }

            _selectedBarAnchor = null;
            _selectedBarStartIndex = null;
            _selectedBarEndIndex = null;
            btnResumeLiveFollow.Visible = false;
            lblTickRatioBadge.Visible = false;
            lblVolRatioBadge.Visible = false;

            lblTickTitle.Text = "当前大周期内部微观逐笔 Tick 走势";
            var curBucket = _engine.CurrentBucket;
            if (curBucket != null)
            {
                lblTickBadge.Text = $"周期 #{_engine.CurrentBucketIndex + 1}/{_engine.TotalBuckets}: {curBucket.StartTime:HH:mm} ~ {curBucket.EndTime:HH:mm} (共 {curBucket.TickCount:N0} Ticks)";
            }
            else
            {
                lblTickBadge.Text = "当前无活动周期";
            }
            lblTickBadge.Left = (btnToggleTickChartType != null ? btnToggleTickChartType.Right : (numTickCustomMinutes.Visible ? numTickCustomMinutes.Right : cboTickPeriod.Right)) + 10;

            _macroPlotNeedsRefresh = true;
            _tickPlotNeedsRefresh = true;

            AppendLog("[恢复实时] 已退出 K 线选择模式，恢复跟随实时回放 Tick 走势。", Color.FromArgb(74, 222, 128));
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
            string timeStr = DateTime.Now.ToString("HH:mm:ss.fff");
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

                // 3. 周期与自定义分钟
                if (_settings.PeriodIndex >= 0 && _settings.PeriodIndex < cboPeriod.Items.Count)
                {
                    cboPeriod.SelectedIndex = _settings.PeriodIndex;
                }
                numCustomMinutes.Value = Math.Clamp(_settings.CustomMinutes, 1, 1440);
                numCustomMinutes.Visible = cboPeriod.SelectedIndex == 9;

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
                numConsecutiveBars.Value = Math.Clamp(_settings.ConsecutiveMinBars, 2, 50);
                numConsecutivePct.Value = Math.Clamp(_settings.ConsecutiveMinPct, 0.1m, 50.0m);

                // 8. 价格与比值曲线凑近对齐状态
                UpdateProximityButtonState();

                // 9. 微观 Tick 周期与自定义分钟
                if (_settings.TickPeriodIndex >= 0 && _settings.TickPeriodIndex < cboTickPeriod.Items.Count)
                {
                    cboTickPeriod.SelectedIndex = _settings.TickPeriodIndex;
                }
                numTickCustomMinutes.Value = Math.Clamp(_settings.TickCustomMinutes, 1, 1440);
                numTickCustomMinutes.Visible = cboTickPeriod.SelectedIndex == 7;

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
            _settings.SpeedIndex = cboSpeed.SelectedIndex;
            _settings.AutoFollow = chkAutoFollow.Checked;
            _settings.ShowVolume = chkShowVolume.Checked;
            _settings.ChartTypeIndex = cboChartType.SelectedIndex;
            _settings.AutoAppendNextBatch = chkAutoAppend.Checked;

            // 连续涨跌形态标记参数
            _settings.ShowConsecutiveTrend = chkConsecutiveTrend.Checked;
            _settings.ConsecutiveMinBars = (int)numConsecutiveBars.Value;
            _settings.ConsecutiveMinPct = numConsecutivePct.Value;

            // 价格与比值凑近配置
            _settings.RatioProximity = btnRatioProximity != null && btnRatioProximity.Text.Contains("开");

            // 微观 Tick 周期与自定义分钟
            _settings.TickPeriodIndex = cboTickPeriod.SelectedIndex;
            _settings.TickCustomMinutes = (int)numTickCustomMinutes.Value;

            // 微观 Tick 图表类型 (0: 蜡烛图, 1: 折线图)
            _settings.TickChartTypeIndex = Math.Clamp(_settings.TickChartTypeIndex, 0, 1);

            // 微观 Tick 是否显示多空比值
            if (chkShowTickRatio != null)
            {
                _settings.ShowTickRatio = chkShowTickRatio.Checked;
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
