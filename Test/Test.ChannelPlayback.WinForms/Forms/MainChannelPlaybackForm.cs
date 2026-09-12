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
    /// 核心功能：
    /// 1. 采用 Test 内的高性能 ParquetDataReader 读取并回放币安 K 线序列
    /// 2. 默认绘制动态包络通道：默认 200 长度 (左侧 100 根基准分析，右侧预测延伸 100 根)
    /// 3. 通道严格包含所有高点与低点 (上轨 >= 所有High，下轨 <= 所有Low)
    /// 4. 随新增 K 线实时动态演变通道高度与倾斜角度
    /// 5. 完备的播放、暂停、单步、重置、调速与进度任意拖拽跳转控制
    /// </summary>
    public class MainChannelPlaybackForm : Form
    {
        private readonly KlinePlaybackEngine _engine;
        private DynamicChannelResult _currentChannel;
        private RawKline _currentKline;
        private int _currentBarIndex = -1;

        // UI 控件
        private SplitContainer splitMain = null!;
        private FormsPlot formsPlot = null!;
        private Panel panelRight = null!;

        // 图表顶部独立状态栏控件 (彻底解耦画布，杜绝信息遮挡K线与图表)
        private Panel panelChartHeader = null!;
        private Label lblChartHeaderTitle = null!;
        private Label lblChartHeaderStats = null!;
        private CheckBox chkShowLegend = null!;

        // 数据源控件
        private GroupBox grpData = null!;
        private ComboBox cboCoin = null!;
        private ComboBox cboInterval = null!;
        private DateTimePicker dtpStart = null!;
        private DateTimePicker dtpEnd = null!;
        private Button btnLoadData = null!;

        // 通道参数控件
        private GroupBox grpChannel = null!;
        private NumericUpDown numLeftLen = null!;
        private NumericUpDown numRightLen = null!;
        private Label lblTotalLength = null!;
        private ComboBox cboCalcMode = null!;
        private ComboBox cboWindowMode = null!;
        private CheckBox chkAutoScale = null!;
        private CheckBox chkFollowLatest = null!;
        private CheckBox chkShowTouchMarkers = null!;

        // 回放控制控件
        private GroupBox grpPlayback = null!;
        private Button btnPlay = null!;
        private Button btnPause = null!;
        private Button btnStep = null!;
        private Button btnReset = null!;
        private TrackBar tbSpeed = null!;
        private Label lblSpeedVal = null!;
        private TrackBar tbProgress = null!;
        private Label lblProgressVal = null!;

        // 实时监控看板控件
        private GroupBox grpMetrics = null!;
        private Label lblMetricPrice = null!;
        private Label lblMetricHeight = null!;
        private Label lblMetricAngle = null!;
        private Label lblMetricSlope = null!;
        private Label lblMetricUpper = null!;
        private Label lblMetricLower = null!;
        private Label lblMetricTouch = null!;

        // 底部日志控件
        private RichTextBox txtLog = null!;

        public MainChannelPlaybackForm()
        {
            _engine = new KlinePlaybackEngine();
            InitializeComponents();
            BindEngineEvents();
            ApplyDarkTheme();

            // 窗体加载完成后异步加载默认数据 (BTCUSDT 2024-01-01)
            this.Shown += async (s, e) =>
            {
                await LoadSelectedDataAsync();
            };
        }

        #region 初始化组件与布局

        private void InitializeComponents()
        {
            this.Text = "动态包络通道与 K 线流式回放系统 - Dynamic Envelope Channel Playback";
            this.Size = new Size(1680, 1000);
            this.MinimumSize = new Size(1366, 800);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular, GraphicsUnit.Point);

            // 主分割容器 (左右分割，左侧图表+日志，右侧控制台)
            splitMain = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 6,
                FixedPanel = FixedPanel.Panel2
            };

            // 左侧分割容器 (上下分割，上方图表，下方日志)
            var splitLeft = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = 6,
                FixedPanel = FixedPanel.Panel2
            };

            // 1. 图表顶部独立状态栏 (原生 WinForms 容器，放置标题与实时指标，彻底释放画布空间)
            panelChartHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 36,
                BackColor = Color.FromArgb(15, 23, 42), // Slate 900
                Padding = new Padding(10, 5, 10, 5)
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
                Text = "数据就绪中...",
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular),
                ForeColor = Color.FromArgb(226, 232, 240), // Slate 200
                AutoSize = true,
                Location = new Point(270, 9)
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
            chkShowLegend.CheckedChanged += (s, e) => formsPlot.Refresh();

            panelChartHeader.Controls.Add(lblChartHeaderTitle);
            panelChartHeader.Controls.Add(lblChartHeaderStats);
            panelChartHeader.Controls.Add(chkShowLegend);

            // 2. ScottPlot 图表控件 (纯净全屏展示 K 线与动态通道)
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

            // 2. 底部日志控件
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
            splitLeft.SplitterDistance = 800;

            splitMain.Panel1.Controls.Add(splitLeft);

            // 3. 右侧控制面板
            panelRight = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.FromArgb(30, 41, 59) // Slate 800
            };
            splitMain.Panel2.Controls.Add(panelRight);

            int panelWidth = 380;
            splitMain.SplitterDistance = this.ClientSize.Width - panelWidth;

            // 构造右侧面板各个 GroupBox
            int currentTop = 12;

            // --- Group 1: 数据源设置 ---
            grpData = CreateGroupBox("1. 真实数据源与周期选择", currentTop, 180);
            {
                var lblCoin = CreateLabel("交易对:", 15, 23);
                cboCoin = new ComboBox { Location = new Point(120, 20), Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
                cboCoin.Items.AddRange(new object[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "NEARUSDT" });
                cboCoin.SelectedIndex = 0;
                cboCoin.SelectedIndexChanged += (s, e) => AutoSetDatesForCoin();

                var lblInt = CreateLabel("K线周期:", 15, 51);
                cboInterval = new ComboBox { Location = new Point(120, 48), Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
                cboInterval.Items.AddRange(new object[] {
                    "1m (1分钟)", "3m (3分钟)", "5m (5分钟)",
                    "15m (15分钟)", "30m (30分钟)", "1h (1小时)",
                    "2h (2小时)", "4h (4小时)", "1d (日线)"
                });
                cboInterval.SelectedIndex = 0;

                var lblStart = CreateLabel("起始日期:", 15, 79);
                dtpStart = new DateTimePicker { Location = new Point(120, 76), Width = 220, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd" };
                dtpStart.Value = new DateTime(2024, 1, 1);

                var lblEnd = CreateLabel("结束日期:", 15, 107);
                dtpEnd = new DateTimePicker { Location = new Point(120, 104), Width = 220, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd" };
                dtpEnd.Value = new DateTime(2024, 1, 1);

                btnLoadData = new Button
                {
                    Text = "📂 加载真实 K 线数据",
                    Location = new Point(15, 136),
                    Size = new Size(325, 32),
                    BackColor = Color.FromArgb(37, 99, 235), // Blue 600
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Microsoft YaHei", 9.5F, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnLoadData.FlatAppearance.BorderSize = 0;
                btnLoadData.Click += async (s, e) => await LoadSelectedDataAsync();

                grpData.Controls.AddRange(new Control[] { lblCoin, cboCoin, lblInt, cboInterval, lblStart, dtpStart, lblEnd, dtpEnd, btnLoadData });
            }
            panelRight.Controls.Add(grpData);
            currentTop += grpData.Height + 10;

            // --- Group 2: 通道核心参数 ---
            grpChannel = CreateGroupBox("2. 动态包络通道核心参数", currentTop, 205);
            {
                var lblL = CreateLabel("左侧分析长度:", 15, 25);
                numLeftLen = new NumericUpDown { Location = new Point(130, 22), Width = 210, Minimum = 10, Maximum = 1000, Value = 100 };
                numLeftLen.ValueChanged += (s, e) =>
                {
                    _engine.LeftLength = (int)numLeftLen.Value;
                    UpdateTotalLengthLabel();
                    _engine.TriggerCurrentFrame();
                };

                var lblR = CreateLabel("右侧延长跨度:", 15, 53);
                numRightLen = new NumericUpDown { Location = new Point(130, 50), Width = 210, Minimum = 0, Maximum = 1000, Value = 100 };
                numRightLen.ValueChanged += (s, e) =>
                {
                    _engine.RightExtendLength = (int)numRightLen.Value;
                    UpdateTotalLengthLabel();
                    _engine.TriggerCurrentFrame();
                };

                lblTotalLength = new Label
                {
                    Text = "📐 通道总长度: 200 根 (左 100 根 + 右延长 100 根)",
                    Location = new Point(15, 78),
                    AutoSize = true,
                    ForeColor = Color.FromArgb(250, 204, 21), // Yellow 400
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold)
                };

                var lblMode = CreateLabel("拟合算法模式:", 15, 103);
                cboCalcMode = new ComboBox { Location = new Point(130, 100), Width = 210, DropDownStyle = ComboBoxStyle.DropDownList };
                cboCalcMode.Items.AddRange(new object[] { "经典线性回归包络 (标准趋势)", "极小高度紧致包络 (几何最优)" });
                cboCalcMode.SelectedIndex = 0;
                cboCalcMode.SelectedIndexChanged += (s, e) =>
                {
                    _engine.CalculationMode = (ChannelCalculationMode)cboCalcMode.SelectedIndex;
                    _engine.TriggerCurrentFrame();
                };

                var lblWin = CreateLabel("计算窗口模式:", 15, 131);
                cboWindowMode = new ComboBox { Location = new Point(130, 128), Width = 210, DropDownStyle = ComboBoxStyle.DropDownList };
                cboWindowMode.Items.AddRange(new object[] { "滑动窗口 (严格取左侧指定数量)", "全量累计 (从起点到当前所有K线)" });
                cboWindowMode.SelectedIndex = 0;
                cboWindowMode.SelectedIndexChanged += (s, e) =>
                {
                    _engine.CumulativeMode = cboWindowMode.SelectedIndex == 1;
                    _engine.TriggerCurrentFrame();
                };

                chkAutoScale = new CheckBox
                {
                    Text = "自动适配合理坐标轴 (Auto-Scale)",
                    Location = new Point(15, 155),
                    AutoSize = true,
                    Checked = true
                };

                chkFollowLatest = new CheckBox
                {
                    Text = "视图自动跟随最新推进的 K 线",
                    Location = new Point(15, 178),
                    AutoSize = true,
                    Checked = true
                };

                chkShowTouchMarkers = new CheckBox
                {
                    Text = "显示最高最低触碰标记点",
                    Location = new Point(200, 178),
                    AutoSize = true,
                    Checked = true
                };
                chkShowTouchMarkers.CheckedChanged += (s, e) => _engine.TriggerCurrentFrame();

                grpChannel.Controls.AddRange(new Control[] {
                    lblL, numLeftLen, lblR, numRightLen, lblTotalLength,
                    lblMode, cboCalcMode, lblWin, cboWindowMode,
                    chkAutoScale, chkFollowLatest, chkShowTouchMarkers
                });
            }
            panelRight.Controls.Add(grpChannel);
            currentTop += grpChannel.Height + 10;

            // --- Group 3: 回放操作控制 ---
            grpPlayback = CreateGroupBox("3. K 线回放控制", currentTop, 205);
            {
                btnPlay = new Button
                {
                    Text = "▶ 开始播放",
                    Location = new Point(15, 25),
                    Size = new Size(100, 36),
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
                    Location = new Point(125, 25),
                    Size = new Size(100, 36),
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
                    Text = "⏭ 单步 (+1)",
                    Location = new Point(235, 25),
                    Size = new Size(105, 36),
                    BackColor = Color.FromArgb(79, 70, 229), // Indigo 600
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Microsoft YaHei", 9.5F, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnStep.FlatAppearance.BorderSize = 0;
                btnStep.Click += (s, e) => _engine.StepForward(1);

                btnReset = new Button
                {
                    Text = "⏹ 重置起点",
                    Location = new Point(15, 68),
                    Size = new Size(325, 26),
                    BackColor = Color.FromArgb(71, 85, 105), // Slate 600
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Microsoft YaHei", 8.5F),
                    Cursor = Cursors.Hand
                };
                btnReset.FlatAppearance.BorderSize = 0;
                btnReset.Click += (s, e) => _engine.Reset();

                lblSpeedVal = CreateLabel("回放间隔: 50 ms (20 bar/s)", 15, 100);
                tbSpeed = new TrackBar
                {
                    Location = new Point(10, 120),
                    Width = 335,
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
                };

                lblProgressVal = CreateLabel("进度: 0 / 0 根", 15, 155);
                tbProgress = new TrackBar
                {
                    Location = new Point(10, 172),
                    Width = 335,
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
            panelRight.Controls.Add(grpPlayback);
            currentTop += grpPlayback.Height + 10;

            // --- Group 4: 实时动态监控看板 ---
            grpMetrics = CreateGroupBox("4. 实时动态通道与 K 线特征看板", currentTop, 210);
            {
                lblMetricPrice = CreateStatLabel("当前最新 K 线: -", 15, 25);
                lblMetricHeight = CreateStatLabel("通道高度 (H): -", 15, 50);
                lblMetricHeight.ForeColor = Color.FromArgb(74, 222, 128); // Green 400

                lblMetricAngle = CreateStatLabel("通道角度 (Angle): -", 15, 75);
                lblMetricAngle.ForeColor = Color.FromArgb(250, 204, 21); // Yellow 400

                lblMetricSlope = CreateStatLabel("通道斜率 (Slope): -", 15, 100);
                lblMetricSlope.ForeColor = Color.FromArgb(56, 189, 248); // Sky 400

                lblMetricUpper = CreateStatLabel("通道上轨 (Upper): -", 15, 125);
                lblMetricLower = CreateStatLabel("通道下轨 (Lower): -", 15, 150);
                lblMetricTouch = CreateStatLabel("高低触碰锚定点: -", 15, 175);
                lblMetricTouch.ForeColor = Color.FromArgb(232, 121, 249); // Fuchsia 400

                grpMetrics.Controls.AddRange(new Control[] {
                    lblMetricPrice, lblMetricHeight, lblMetricAngle, lblMetricSlope,
                    lblMetricUpper, lblMetricLower, lblMetricTouch
                });
            }
            panelRight.Controls.Add(grpMetrics);

            this.Controls.Add(splitMain);
        }

        private GroupBox CreateGroupBox(string title, int top, int height)
        {
            return new GroupBox
            {
                Text = title,
                Location = new Point(12, top),
                Size = new Size(356, height),
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
                Size = new Size(330, 20),
                ForeColor = Color.FromArgb(226, 232, 240),
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular)
            };
        }

        private void UpdateTotalLengthLabel()
        {
            int l = (int)numLeftLen.Value;
            int r = (int)numRightLen.Value;
            lblTotalLength.Text = $"📐 通道总长度: {l + r} 根 (左 {l} 根 + 右延长 {r} 根)";
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

        #region 数据加载与引擎事件绑定

        private async System.Threading.Tasks.Task LoadSelectedDataAsync()
        {
            btnLoadData.Enabled = false;
            btnLoadData.Text = "⏳ 正在读取真实 K 线...";

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
            lblProgressVal.Text = $"进度: {_engine.CurrentIndex + 1} / {total} 根";
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

        private volatile bool _isRendering = false;

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
                lblProgressVal.Text = $"进度: {_currentBarIndex + 1} / {_engine.TotalKlines} 根";

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
                string sign = _currentChannel.SlopeK >= 0 ? "+" : "";
                lblChartHeaderStats.Text = $"收: {k.Close:F2}  |  高度: {_currentChannel.ChannelHeight:F2} ({_currentChannel.ChannelHeightPct:F2}%)  |  角度: {_currentChannel.AngleDeg:+0.0;-0.0}°  |  斜率: {sign}{_currentChannel.SlopeK:F2}  |  长: {_currentChannel.TotalLength} (左{_currentChannel.LeftLength}+延{_currentChannel.RightExtendLength})  |  #{_currentBarIndex}";

                lblMetricHeight.Text = $"通道高度 (H): {_currentChannel.ChannelHeight:F2} USDT ({_currentChannel.ChannelHeightPct:F2}%)";
                lblMetricAngle.Text = $"通道角度 (Angle): {_currentChannel.AngleDeg:+0.0;-0.0}°";
                lblMetricSlope.Text = $"通道斜率 (Slope): {sign}{_currentChannel.SlopeK:F3} USDT/bar ({sign}{_currentChannel.SlopePct:F3}%/bar)";

                decimal upCurr = _currentChannel.GetUpperPrice(_currentChannel.CurrentX);
                decimal upEnd = _currentChannel.GetUpperPrice(_currentChannel.EndX);
                lblMetricUpper.Text = $"通道上轨: 现值 {upCurr:F2} -> 远端 {upEnd:F2}";

                decimal lowCurr = _currentChannel.GetLowerPrice(_currentChannel.CurrentX);
                decimal lowEnd = _currentChannel.GetLowerPrice(_currentChannel.EndX);
                lblMetricLower.Text = $"通道下轨: 现值 {lowCurr:F2} -> 远端 {lowEnd:F2}";

                lblMetricTouch.Text = $"锚定触碰: 高点 #{_currentChannel.TouchHighIndex} ({_currentChannel.TouchHighPrice:F2}) | 低点 #{_currentChannel.TouchLowIndex} ({_currentChannel.TouchLowPrice:F2})";
            }
            else
            {
                lblChartHeaderStats.Text = $"收: {k.Close:F2}  |  #{_currentBarIndex} (计算中...)";
                lblMetricHeight.Text = "通道高度 (H): 计算中 (K线不足)...";
                lblMetricAngle.Text = "通道角度 (Angle): -";
                lblMetricSlope.Text = "通道斜率 (Slope): -";
                lblMetricUpper.Text = "通道上轨: -";
                lblMetricLower.Text = "通道下轨: -";
                lblMetricTouch.Text = "锚定触碰: -";
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

            // 确定当前绘制的 K 线窗口 (展示当前已推进的全部 K 线，或者当前点前后合理视野)
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

                // ② 上轨 (左侧分析实线，右侧延伸虚线/醒目颜色)
                var lineUpLeft = plot.Add.Line(xStart, yUpStart, xCurr, yUpCurr);
                lineUpLeft.Color = ScottPlot.Color.FromHex("#f59e0b"); // 琥珀金 Amber 500
                lineUpLeft.LineWidth = 2.0f;
                lineUpLeft.LegendText = $"通道上轨 (阻力/包络)";

                var lineUpRight = plot.Add.Line(xCurr, yUpCurr, xEnd, yUpEnd);
                lineUpRight.Color = ScottPlot.Color.FromHex("#fbbf24"); // Amber 400
                lineUpRight.LineWidth = 1.6f;
                lineUpRight.LinePattern = LinePattern.Dashed;

                // ③ 下轨 (左侧分析实线，右侧延伸虚线)
                var lineLowLeft = plot.Add.Line(xStart, yLowStart, xCurr, yLowCurr);
                lineLowLeft.Color = ScottPlot.Color.FromHex("#38bdf8"); // 天空蓝 Sky 400
                lineLowLeft.LineWidth = 2.0f;
                lineLowLeft.LegendText = $"通道下轨 (支撑/包络)";

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

                // ⑤ 当前 K 线位置分割虚线 (标示出左侧 100 根历史与右侧 100 根延展)
                var vLine = plot.Add.VerticalLine(xCurr);
                vLine.Color = ScottPlot.Color.FromHex("#e2e8f0").WithAlpha(90);
                vLine.LineWidth = 1.2f;
                vLine.LinePattern = LinePattern.Dotted;

                // ⑥ 触碰极值标记点 (如果勾选)
                if (chkShowTouchMarkers.Checked)
                {
                    if (ch.TouchHighIndex >= 0 && ch.TouchHighIndex <= _currentBarIndex)
                    {
                        var highMarker = plot.Add.Marker(ch.TouchHighIndex, (double)ch.TouchHighPrice);
                        highMarker.Shape = MarkerShape.FilledTriangleDown;
                        highMarker.Size = 10;
                        highMarker.Color = ScottPlot.Color.FromHex("#ef4444"); // 红色向下箭头高点
                        highMarker.LegendText = $"锚定高点 #{ch.TouchHighIndex}";
                    }

                    if (ch.TouchLowIndex >= 0 && ch.TouchLowIndex <= _currentBarIndex)
                    {
                        var lowMarker = plot.Add.Marker(ch.TouchLowIndex, (double)ch.TouchLowPrice);
                        lowMarker.Shape = MarkerShape.FilledTriangleUp;
                        lowMarker.Size = 10;
                        lowMarker.Color = ScottPlot.Color.FromHex("#22c55e"); // 绿色向上箭头低点
                        lowMarker.LegendText = $"锚定低点 #{ch.TouchLowIndex}";
                    }
                }

            }

            // 3. 坐标轴视口控制 (跟随最新与自动缩放，留出合理的上下边距)
            if (chkAutoScale.Checked)
            {
                if (chkFollowLatest.Checked && _currentChannel.IsValid)
                {
                    // 聚焦在通道覆盖的完整视野 [StartX - 10, EndX + 10]
                    double minX = Math.Max(0, _currentChannel.StartX - 10);
                    double maxX = _currentChannel.EndX + 15;

                    // 计算该视野内的 Y 范围 (包含 K 线与通道端点)
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
            _engine?.Dispose();
            base.OnFormClosing(e);
        }
    }
}
