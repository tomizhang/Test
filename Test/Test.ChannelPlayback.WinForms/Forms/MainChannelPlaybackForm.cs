using Binance.Net.Enums;
using Common;
using Common.Helper;
using ScottPlot;
using ScottPlot.WinForms;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
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
        private CheckBox chkAutoScale = null!;
        private CheckBox chkFollowLatest = null!;
        private CheckBox chkShowTouchMarkers = null!;

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

        // 底部日志面板控件
        private Panel panelLogHeader = null!;
        private Label lblLogTitle = null!;
        private Button btnClearLog = null!;
        private RichTextBox txtLog = null!;

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
                Checked = false
            };
            chkShowLegend.CheckedChanged += (s, e) =>
            {
                SaveSettingsFromUi();
                formsPlot.Refresh();
            };

            panelChartHeader.Controls.Add(lblChartHeaderTitle);
            panelChartHeader.Controls.Add(lblChartHeaderStats);
            panelChartHeader.Controls.Add(chkShowLegend);

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

            splitLeft.Panel1.Controls.Add(formsPlot);
            splitLeft.Panel1.Controls.Add(panelChartHeader); // Dock Top 居于上方，Fill 填满剩余区域

            // ③ 底部日志面板 (带标题与一键清空按钮)
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
                Location = new Point(200, 3),
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

            splitLeft.Panel2.Controls.Add(txtLog);
            splitLeft.Panel2.Controls.Add(panelLogHeader);

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
                cboCalcMode = new ComboBox { Location = new Point(125, 105), Width = 190, DropDownStyle = ComboBoxStyle.DropDownList };
                cboCalcMode.Items.AddRange(new object[] {
                    "三点智能自适应 (2低1高/2高1低)",
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

                chkAutoScale = new CheckBox
                {
                    Text = "自动适配合理坐标轴 (Auto-Scale)",
                    Location = new Point(15, 170),
                    AutoSize = true,
                    Checked = true,
                    ForeColor = Color.FromArgb(226, 232, 240)
                };
                chkAutoScale.CheckedChanged += (s, e) => SaveSettingsFromUi();

                chkFollowLatest = new CheckBox
                {
                    Text = "视图自动跟随最新推进的 K 线",
                    Location = new Point(15, 198),
                    AutoSize = true,
                    Checked = true,
                    ForeColor = Color.FromArgb(226, 232, 240)
                };
                chkFollowLatest.CheckedChanged += (s, e) => SaveSettingsFromUi();

                chkShowTouchMarkers = new CheckBox
                {
                    Text = "显示最高与最低触碰锚定标记",
                    Location = new Point(15, 226),
                    AutoSize = true,
                    Checked = true,
                    ForeColor = Color.FromArgb(226, 232, 240)
                };
                chkShowTouchMarkers.CheckedChanged += (s, e) =>
                {
                    SaveSettingsFromUi();
                    _engine.TriggerCurrentFrame();
                };

                tabChannel.Controls.AddRange(new Control[] {
                    lblL, numLeftLen, lblR, numRightLen, lblTotalLength,
                    lblMode, cboCalcMode, lblWin, cboWindowMode,
                    chkAutoScale, chkFollowLatest, chkShowTouchMarkers
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

                tabMetrics.Controls.AddRange(new Control[] {
                    lblMetricPrice, lblMetricType, lblMetricP1P2, lblMetricP3,
                    lblMetricHeight, lblMetricAngle, lblMetricSlope,
                    lblMetricUpper, lblMetricLower, lblMetricTouch
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
                chkAutoScale.Checked = s.AutoScale;
                chkFollowLatest.Checked = s.FollowLatest;
                chkShowTouchMarkers.Checked = s.ShowTouchMarkers;
                chkShowLegend.Checked = s.ShowLegend;

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
                    ShowLegend = chkShowLegend.Checked,
                    SpeedIntervalMs = tbSpeed.Value
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
                if (this.InvokeRequired)
                {
                    this.BeginInvoke(new Action(() => AppendLog(msg)));
                }
                else
                {
                    AppendLog(msg);
                }
            };
        }

        private void UpdateOnDataLoaded(int total)
        {
            tbProgress.Maximum = Math.Max(1, total - 1);
            tbProgress.Value = Math.Clamp(_engine.CurrentIndex, 0, tbProgress.Maximum);
            lblProgressVal.Text = $"回放进度: {_engine.CurrentIndex + 1} / {total} 根";
        }

        private void UpdatePlaybackButtonStates(PlaybackState state)
        {
            btnPlay.Enabled = state != PlaybackState.Playing;
            btnPause.Enabled = state == PlaybackState.Playing;
        }

        private void AppendLog(string message)
        {
            if (txtLog.IsDisposed) return;
            string time = DateTime.Now.ToString("HH:mm:ss.fff");
            txtLog.AppendText($"[{time}] {message}\n");
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

            // 1. 绘制红绿蜡烛图
            var ohlcList = new List<OHLC>(_currentBarIndex + 1);
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

            // 3. 坐标轴视口控制 (跟随最新与自动缩放，留出合理的上下边距)
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
