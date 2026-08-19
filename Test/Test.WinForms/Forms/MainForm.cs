using Binance.Net.Enums;
using Common;
using Common.Helper;
using Common.Models;
using Common.Services;
using ScottPlot.WinForms;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Test.Strategy;

namespace Test.WinForms.Forms
{
    /// <summary>
    /// 专业量化回测系统主窗体 (WinForms 主界面)
    /// 布局：
    /// - 左边上部：交互式 ScottPlot 价格折线图、高低点标记与趋势线结构图表
    /// - 左边下部：实时回测日志与事件监控框
    /// - 右边区域：交易对、K线周期、日期选择、策略参数与回测控制面板
    /// </summary>
    public class MainForm : Form
    {
        private readonly IBacktestEngineService _engineService;
        private CancellationTokenSource? _cts;
        private BacktestResult? _latestResult;

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
        private CheckBox chkStrictEnvelope = null!;

        private GroupBox grpControl = null!;
        private Button btnStart = null!;
        private Button btnStop = null!;
        private Button btnExportChart = null!;
        private Button btnClearLogs = null!;
        private ProgressBar progressBar = null!;
        private Label lblProgress = null!;

        private GroupBox grpStatus = null!;
        private Label lblStatTime = null!;
        private Label lblStatThroughput = null!;
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

            // 1. 主分割容器 (左右分割: 左边为图表+日志, 右边为控制台)
            splitMain = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 6,
                FixedPanel = FixedPanel.Panel2
            };

            // 2. 左侧分割容器 (上下分割: 上边为ScottPlot图表, 下边为日志框)
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
                Text = "⚡ 实时日志与回测监控流",
                Font = new Font("Microsoft YaHei", 9.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(226, 232, 240),
                AutoSize = true,
                Location = new Point(8, 7)
            };

            btnClearLogsTop = new Button
            {
                Text = "清空日志",
                Size = new Size(70, 24),
                Location = new Point(180, 4),
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

            // 调整分割比例
            this.Load += (s, e) =>
            {
                splitMain.SplitterDistance = this.ClientSize.Width - 380;
                splitLeft.SplitterDistance = (int)(splitLeft.Height * 0.65);
                InitializeDefaultPlot();
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

            // Group 2: 趋势线策略参数
            grpStrategy = CreateGroupBox("2. 趋势线策略参数", top, 240);
            {
                var lblMaxK = CreateLabel("K线滑动窗口:", 15, 25);
                numMaxKlines = new NumericUpDown { Location = new Point(130, 22), Width = 210, Minimum = 100, Maximum = 100000, Value = 2000 };

                var lblMinT = CreateLabel("历史趋势线库:", 15, 55);
                numMinTrendLines = new NumericUpDown { Location = new Point(130, 52), Width = 210, Minimum = 100, Maximum = 50000, Value = 1000 };

                var lblMaxD = CreateLabel("穿透删除列表:", 15, 85);
                numMaxDeleted = new NumericUpDown { Location = new Point(130, 82), Width = 210, Minimum = 100, Maximum = 50000, Value = 1000 };

                var lblLeft = CreateLabel("波峰左侧对比:", 15, 115);
                numLeftLen = new NumericUpDown { Location = new Point(130, 112), Width = 210, Minimum = 1, Maximum = 100, Value = 5 };

                var lblRight = CreateLabel("波峰右侧对比:", 15, 145);
                numRightLen = new NumericUpDown { Location = new Point(130, 142), Width = 210, Minimum = 1, Maximum = 100, Value = 5 };

                var lblSpan = CreateLabel("最大配对跨度:", 15, 175);
                numMaxSpan = new NumericUpDown { Location = new Point(130, 172), Width = 210, Minimum = 10, Maximum = 2000, Value = 100 };

                chkStrictEnvelope = new CheckBox { Text = "严格外包络 (禁止内部穿透)", Location = new Point(15, 205), Width = 320, Checked = true };

                grpStrategy.Controls.AddRange(new Control[] { lblMaxK, numMaxKlines, lblMinT, numMinTrendLines, lblMaxD, numMaxDeleted, lblLeft, numLeftLen, lblRight, numRightLen, lblSpan, numMaxSpan, chkStrictEnvelope });
            }
            panelRight.Controls.Add(grpStrategy);
            top += grpStrategy.Height + 10;

            // Group 3: 控制按钮与进度条
            grpControl = CreateGroupBox("3. 执行控制与进度", top, 175);
            {
                btnStart = new Button
                {
                    Text = "▶ 开始回测",
                    Location = new Point(15, 25),
                    Size = new Size(155, 36),
                    BackColor = Color.FromArgb(5, 150, 105), // Green 600
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Microsoft YaHei", 10F, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnStart.FlatAppearance.BorderSize = 0;
                btnStart.Click += async (s, e) => await StartBacktestAsync();

                btnStop = new Button
                {
                    Text = "⏹ 停止回测",
                    Location = new Point(185, 25),
                    Size = new Size(155, 36),
                    BackColor = Color.FromArgb(220, 38, 38), // Red 600
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Microsoft YaHei", 10F, FontStyle.Bold),
                    Enabled = false,
                    Cursor = Cursors.Hand
                };
                btnStop.FlatAppearance.BorderSize = 0;
                btnStop.Click += (s, e) => StopBacktest();

                btnExportChart = new Button
                {
                    Text = "🖼 导出/打开图表",
                    Location = new Point(15, 70),
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
                    Location = new Point(185, 70),
                    Size = new Size(155, 30),
                    BackColor = Color.FromArgb(71, 85, 105), // Slate 600
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand
                };
                btnClearLogs.FlatAppearance.BorderSize = 0;
                btnClearLogs.Click += (s, e) => txtLogs.Clear();

                progressBar = new ProgressBar
                {
                    Location = new Point(15, 110),
                    Size = new Size(325, 18),
                    Minimum = 0,
                    Maximum = 100,
                    Value = 0
                };

                lblProgress = new Label
                {
                    Text = "系统就绪，点击【开始回测】启动",
                    Location = new Point(15, 135),
                    Size = new Size(325, 30),
                    ForeColor = Color.FromArgb(148, 163, 184)
                };

                grpControl.Controls.AddRange(new Control[] { btnStart, btnStop, btnExportChart, btnClearLogs, progressBar, lblProgress });
            }
            panelRight.Controls.Add(grpControl);
            top += grpControl.Height + 10;

            // Group 4: 实时统计看板
            grpStatus = CreateGroupBox("4. 统计监控看板", top, 210);
            {
                lblStatTime = CreateStatLabel("执行耗时: -", 15, 25);
                lblStatThroughput = CreateStatLabel("吞吐速率: -", 15, 50);
                lblStatKlines = CreateStatLabel("K线总量: -", 15, 75);
                lblStatTicks = CreateStatLabel("Tick总量: -", 15, 100);
                lblStatPeaksValleys = CreateStatLabel("识别极值: 高点 0 | 低点 0", 15, 125);
                lblStatActiveLines = CreateStatLabel("活跃趋势线: 阻力 0 | 支撑 0", 15, 150);
                lblStatDeletedLines = CreateStatLabel("已击穿删除: 0 条", 15, 175);

                grpStatus.Controls.AddRange(new Control[] { lblStatTime, lblStatThroughput, lblStatKlines, lblStatTicks, lblStatPeaksValleys, lblStatActiveLines, lblStatDeletedLines });
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

        #region 事件绑定与暗黑主题

        private void BindEngineEvents()
        {
            _engineService.OnLogMessage += message =>
            {
                AppendLogSafe(message, Color.FromArgb(241, 245, 249));
            };

            _engineService.OnKlineClosed += (kline, index, strategy) =>
            {
                if (index % 10 == 0 || strategy.ActiveResistanceLines.Count > 0)
                {
                    DateTime time = TimeHelper.FromUnixTimeMilliseconds(kline.OpenTime);
                    string msg = $"[K线收盘 #{index:D4}] {time:yyyy-MM-dd HH:mm:ss} | 开:{kline.Open:F2} 高:{kline.High:F2} 低:{kline.Low:F2} 收:{kline.Close:F2} | 活跃阻力:{strategy.ActiveResistanceLines.Count} 支撑:{strategy.ActiveSupportLines.Count} 删除:{strategy.DeletedTrendLinesCount}";
                    AppendLogSafe(msg, Color.FromArgb(250, 204, 21)); // Yellow
                }
            };

            _engineService.OnTrendLinePenetrated += (line, tick, reason) =>
            {
                string symbol = line.IsResistance ? "阻力线(High)" : "支撑线(Low)";
                string msg = $"  ⚡ [趋势线穿透] {reason} | {symbol} #{line.X1}->#{line.X2} | 穿透价格: {tick.Price:F2}";
                AppendLogSafe(msg, Color.FromArgb(244, 114, 182)); // Pink
            };

            _engineService.OnProgressChanged += progress =>
            {
                this.BeginInvoke(() =>
                {
                    progressBar.Value = (int)Math.Clamp(progress.Percentage, 0, 100);
                    lblProgress.Text = progress.Message;
                    lblStatKlines.Text = $"K线总量: {progress.ProcessedKlines:N0} / {progress.TotalKlines:N0}";
                    lblStatTicks.Text = $"Tick总量: {progress.ProcessedTicks:N0} / {progress.TotalTicks:N0}";
                    lblStatActiveLines.Text = $"活跃趋势线: 阻力 {progress.ActiveResistanceCount} | 支撑 {progress.ActiveSupportCount}";
                    lblStatDeletedLines.Text = $"已击穿删除: {progress.DeletedLinesCount} 条";
                });
            };
        }

        private void ApplyDarkTheme()
        {
            this.BackColor = Color.FromArgb(15, 23, 42); // Slate 900
            this.ForeColor = Color.FromArgb(248, 250, 252);
        }

        private void InitializeDefaultPlot()
        {
            formsPlot.Plot.Clear();
            formsPlot.Plot.FigureBackground.Color = ScottPlot.Color.FromHex("#0f172a");
            formsPlot.Plot.DataBackground.Color = ScottPlot.Color.FromHex("#1e293b");
            formsPlot.Plot.Axes.Color(ScottPlot.Color.FromHex("#94a3b8"));
            formsPlot.Plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#334155");
            formsPlot.Plot.Title("等待回测启动，点击【▶ 开始回测】加载图表...", size: 16);
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

            // 锁定按钮
            btnStart.Enabled = false;
            btnStop.Enabled = true;
            _cts = new CancellationTokenSource();

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
                MaxDeletedTrendLinesCapacity = (int)numMaxDeleted.Value,
                LeftLen = (int)numLeftLen.Value,
                RightLen = (int)numRightLen.Value,
                MaxSpan = (int)numMaxSpan.Value,
                AllowInternalPenetration = !chkStrictEnvelope.Checked,
                ParallelDays = 3,
                GenerateChart = true
            };

            AppendLogSafe($"\n========================================================", Color.FromArgb(56, 189, 248));
            AppendLogSafe($"[启动回测] 目标: {request.Coin}, 周期: {interval.ToIntervalString()}, 时间窗口: {request.StartDate:yyyy-MM-dd} ~ {request.EndDate:yyyy-MM-dd}", Color.FromArgb(56, 189, 248));
            AppendLogSafe($"========================================================", Color.FromArgb(56, 189, 248));

            try
            {
                var progress = new Progress<BacktestProgress>();
                _latestResult = await Task.Run(() => _engineService.RunBacktestAsync(request, progress, _cts.Token));

                if (_latestResult.Success && _latestResult.Strategy != null)
                {
                    // 在 UI 线程上完整渲染折线图表
                    this.Invoke(() =>
                    {
                        var strat = _latestResult.Strategy;
                        int startGlobal = Math.Max(0, strat.GlobalBarIndex - strat.KlineCount);

                        // 合并所有历史与活跃趋势线，确保所有生成的趋势线均可在折线图上呈现
                        var allLines = new List<TrendLine>(strat.HistoricalTrendLines);
                        if (strat.ActiveResistanceLines.Count > 0) allLines.AddRange(strat.ActiveResistanceLines);
                        if (strat.ActiveSupportLines.Count > 0) allLines.AddRange(strat.ActiveSupportLines);

                        string summary = $"币种: {request.Coin}, 周期: {interval.ToIntervalString()}, 窗口: {request.StartDate:yyyy-MM-dd} ~ {request.EndDate:yyyy-MM-dd}\n" +
                                         $"耗时: {_latestResult.ElapsedMilliseconds} ms, 吞吐: {_latestResult.TicksPerSecond:N0} ticks/s, 已穿透删除: {strat.DeletedTrendLinesCount}条";

                        PlotHelper.BuildPlot(
                            formsPlot.Plot,
                            strat.Klines,
                            strat.Peaks,
                            strat.Valleys,
                            allLines,
                            summary,
                            title: $"{request.Coin} {interval.ToIntervalString()} 趋势线与极值结构折线图",
                            startGlobalIndex: startGlobal);

                        formsPlot.Refresh();

                        // 更新统计看板
                        lblStatTime.Text = $"执行耗时: {_latestResult.ElapsedMilliseconds:N0} ms";
                        lblStatThroughput.Text = $"吞吐速率: {_latestResult.TicksPerSecond:N0} ticks/s";
                        lblStatPeaksValleys.Text = $"识别极值: 高点 {strat.Peaks.Count} | 低点 {strat.Valleys.Count}";
                        lblStatActiveLines.Text = $"活跃趋势线: 阻力 {strat.ActiveResistanceLines.Count} | 支撑 {strat.ActiveSupportLines.Count}";
                        lblStatDeletedLines.Text = $"已击穿删除: {strat.DeletedTrendLinesCount} 条";

                        AppendLogSafe($"\n[回测成功] 耗时: {_latestResult.ElapsedMilliseconds} ms, 价格折线图、高低点与趋势线已成功绘制！", Color.FromArgb(74, 222, 128));
                    });
                }
                else
                {
                    AppendLogSafe($"\n[回测异常] {_latestResult?.ErrorMessage ?? "未知错误"}", Color.FromArgb(248, 113, 113));
                }
            }
            catch (Exception ex)
            {
                AppendLogSafe($"[回测错误] {ex.Message}", Color.FromArgb(248, 113, 113));
            }
            finally
            {
                btnStart.Enabled = true;
                btnStop.Enabled = false;
                _cts = null;
            }
        }

        private void StopBacktest()
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
                AppendLogSafe("[用户操作] 已发送停止回测请求...", Color.FromArgb(251, 146, 60));
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

        #region 日志线程安全写入

        private void AppendLogSafe(string message, Color color)
        {
            if (txtLogs.IsDisposed) return;

            if (txtLogs.InvokeRequired)
            {
                txtLogs.BeginInvoke(() => AppendLogSafe(message, color));
                return;
            }

            // 限制日志文本长度，防止内存过载
            if (txtLogs.TextLength > 500000)
            {
                txtLogs.Select(0, 100000);
                txtLogs.SelectedText = "";
            }

            txtLogs.SelectionStart = txtLogs.TextLength;
            txtLogs.SelectionLength = 0;
            txtLogs.SelectionColor = color;
            txtLogs.AppendText(message + "\n");
            txtLogs.ScrollToCaret();
        }

        #endregion
    }
}
