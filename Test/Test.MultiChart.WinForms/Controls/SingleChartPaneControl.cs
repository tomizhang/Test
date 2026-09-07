using Binance.Net.Enums;
using Common;
using Common.Helper;
using ScottPlot;
using ScottPlot.WinForms;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Color = System.Drawing.Color;
using Font = System.Drawing.Font;
using FontStyle = System.Drawing.FontStyle;
using Label = System.Windows.Forms.Label;
using Panel = System.Windows.Forms.Panel;
using Test.MultiChart.WinForms.Models;
using Test.MultiChart.WinForms.Services;

namespace Test.MultiChart.WinForms.Controls
{
    /// <summary>
    /// 单个独立多功能分屏图表控件 (支持多周期、多K线形态、三种价格坐标、交互画线与跨窗自动同步)
    /// </summary>
    public class SingleChartPaneControl : UserControl
    {
        #region 属性与状态

        public int PaneIndex { get; set; } = 0;
        public string Symbol { get; private set; } = "BTCUSDT";
        public ChartInterval Interval { get; private set; } = ChartInterval.OneMinute;
        public ChartDisplayType DisplayType { get; private set; } = ChartDisplayType.Candlestick;
        public PriceScaleMode PriceScale { get; private set; } = PriceScaleMode.AbsolutePrice;

        public DrawingToolMode CurrentToolMode { get; set; } = DrawingToolMode.Pointer;

        private readonly BinanceFuturesMarketDataService _marketService;
        private readonly DrawingSyncService _syncService;

        private readonly List<RawKline> _klines = new(1000);
        private readonly object _klineLock = new();
        private CancellationTokenSource? _klineCts;

        // 画线临时状态机
        private bool _isDrawingActive = false;
        private long _drawTime1 = 0;
        private double _drawPrice1 = 0;
        private double _previewX2 = 0;
        private double _previewY2 = 0;

        // 十字光标 Plottable
        private ScottPlot.Plottables.Crosshair? _crosshair;

        // 选中的画线
        private string? _selectedDrawingId = null;

        // 帧率节流与平滑渲染定时器 (30FPS，彻底解决高频 Tick 导致 UI 卡顿问题)
        private readonly System.Windows.Forms.Timer _renderTimer;
        private volatile bool _needsRender = false;

        #endregion

        #region UI 控件

        private Panel pnlHeader = null!;
        private Label lblPaneBadge = null!;
        private ComboBox cboSymbol = null!;
        private ComboBox cboInterval = null!;
        private ComboBox cboChartType = null!;
        private ComboBox cboPriceScale = null!;
        private Button btnResetZoom = null!;
        private Label lblLivePrice = null!;

        private Label lblHudInfo = null!;
        private FormsPlot formsPlot = null!;

        #endregion

        public event Action<ChartPaneSettings>? OnSettingsChanged;

        public SingleChartPaneControl(int paneIndex, BinanceFuturesMarketDataService marketService, DrawingSyncService syncService)
        {
            PaneIndex = paneIndex;
            _marketService = marketService;
            _syncService = syncService;

            InitializeComponents();
            BindSyncEvents();

            // 启动 30FPS (33ms) 节流渲染定时器，批量聚合实盘高频推流
            _renderTimer = new System.Windows.Forms.Timer
            {
                Interval = 33
            };
            _renderTimer.Tick += (s, e) =>
            {
                if (_needsRender)
                {
                    _needsRender = false;
                    RedrawPlot(autoScale: false);
                }
            };
            _renderTimer.Start();
        }

        #region UI 初始化与布局

        private void InitializeComponents()
        {
            this.BackColor = Color.FromArgb(15, 23, 42); // Slate 900
            this.Padding = new Padding(2);

            // 1. 顶部操作栏
            pnlHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 32,
                BackColor = Color.FromArgb(30, 41, 59), // Slate 800
                Padding = new Padding(4, 2, 4, 2)
            };

            lblPaneBadge = new Label
            {
                Text = $"#{PaneIndex + 1}",
                Location = new Point(4, 6),
                Size = new Size(24, 20),
                ForeColor = Color.FromArgb(148, 163, 184),
                Font = new Font("Consolas", 9F, FontStyle.Bold)
            };
            pnlHeader.Controls.Add(lblPaneBadge);

            cboSymbol = new ComboBox
            {
                Location = new Point(32, 4),
                Width = 115,
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.FromArgb(250, 204, 21), // Yellow 400
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold)
            };
            cboSymbol.Items.AddRange(new object[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "HEMIUSDT", "DOGEUSDT", "1000PEPEUSDT", "BNBUSDT", "XRPUSDT" });
            cboSymbol.Text = Symbol;
            cboSymbol.SelectedIndexChanged += (s, e) => ChangeSymbol(cboSymbol.Text);
            pnlHeader.Controls.Add(cboSymbol);

            cboInterval = new ComboBox
            {
                Location = new Point(152, 4),
                Width = 70,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.FromArgb(56, 189, 248), // Sky Blue
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold)
            };
            cboInterval.Items.AddRange(new object[] { "Tick", "1s", "1m", "3m", "5m", "15m", "30m", "1h", "4h", "1d" });
            cboInterval.SelectedIndex = 2; // 1m
            cboInterval.SelectedIndexChanged += OnIntervalChanged;
            pnlHeader.Controls.Add(cboInterval);

            cboChartType = new ComboBox
            {
                Location = new Point(226, 4),
                Width = 95,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.FromArgb(74, 222, 128), // Green 400
                Font = new Font("Microsoft YaHei", 8.5F)
            };
            cboChartType.Items.AddRange(new object[] { "🕯️ 蜡烛", "📈 折线", "🏔️ 面积", "📊 美国线", "☯️ 平均K" });
            cboChartType.SelectedIndex = 0;
            cboChartType.SelectedIndexChanged += (s, e) =>
            {
                DisplayType = (ChartDisplayType)cboChartType.SelectedIndex;
                RedrawPlot();
                NotifySettingsChanged();
            };
            pnlHeader.Controls.Add(cboChartType);

            cboPriceScale = new ComboBox
            {
                Location = new Point(325, 4),
                Width = 95,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.FromArgb(232, 121, 249), // Fuchsia 400
                Font = new Font("Microsoft YaHei", 8.5F)
            };
            cboPriceScale.Items.AddRange(new object[] { "💰 真实USDT", "📊 百分比%", "💵 100U基准" });
            cboPriceScale.SelectedIndex = 0;
            cboPriceScale.SelectedIndexChanged += (s, e) =>
            {
                PriceScale = (PriceScaleMode)cboPriceScale.SelectedIndex;
                RedrawPlot(autoScale: true);
                NotifySettingsChanged();
            };
            pnlHeader.Controls.Add(cboPriceScale);

            btnResetZoom = new Button
            {
                Text = "🔍",
                Location = new Point(424, 4),
                Size = new Size(26, 24),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(51, 65, 85),
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            btnResetZoom.FlatAppearance.BorderSize = 0;
            btnResetZoom.Click += (s, e) =>
            {
                formsPlot.Plot.Axes.Margins(0.02, 0.12);
                formsPlot.Plot.Axes.AutoScale();
                formsPlot.Refresh();
            };
            pnlHeader.Controls.Add(btnResetZoom);

            lblLivePrice = new Label
            {
                Text = "---",
                Location = new Point(455, 6),
                AutoSize = true,
                ForeColor = Color.FromArgb(74, 222, 128),
                Font = new Font("Consolas", 9.5F, FontStyle.Bold)
            };
            pnlHeader.Controls.Add(lblLivePrice);

            this.Controls.Add(pnlHeader);

            // 2. HUD 数据信息栏 (开高低收实时展示)
            lblHudInfo = new Label
            {
                Dock = DockStyle.Top,
                Height = 22,
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.FromArgb(203, 213, 225),
                Font = new Font("Consolas", 8.5F),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0),
                Text = $"[{Symbol} {cboInterval.Text}] 正在加载合约行情..."
            };
            this.Controls.Add(lblHudInfo);

            // 3. 主绘图区 ScottPlot
            formsPlot = new FormsPlot
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(15, 23, 42)
            };
            formsPlot.MouseMove += OnPlotMouseMove;
            formsPlot.MouseDown += OnPlotMouseDown;
            this.Controls.Add(formsPlot);

            // 控件叠放顺序
            formsPlot.BringToFront();
            lblHudInfo.BringToFront();
            pnlHeader.BringToFront();

            InitializePlotStyle();
        }

        private void InitializePlotStyle()
        {
            string fontName = PlotHelper.GetInstalledChineseFont();
            Fonts.Default = fontName;

            formsPlot.Plot.FigureBackground.Color = ScottPlot.Color.FromHex("#0f172a");
            formsPlot.Plot.DataBackground.Color = ScottPlot.Color.FromHex("#1e293b");
            formsPlot.Plot.Axes.Color(ScottPlot.Color.FromHex("#94a3b8"));
            formsPlot.Plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#334155");

            formsPlot.Plot.Axes.Left.TickLabelStyle.ForeColor = ScottPlot.Color.FromHex("#cbd5e1");
            formsPlot.Plot.Axes.Bottom.TickLabelStyle.ForeColor = ScottPlot.Color.FromHex("#cbd5e1");

            // 十字光标
            _crosshair = formsPlot.Plot.Add.Crosshair(0, 0);
            _crosshair.LineColor = ScottPlot.Color.FromHex("#94a3b8").WithAlpha(0.6);
            _crosshair.LinePattern = LinePattern.Dashed;
            _crosshair.IsVisible = false;

            formsPlot.Refresh();
        }

        #endregion

        #region 行情加载与实时数据驱动

        public void ApplySettings(ChartPaneSettings s, List<string>? availableSymbols = null)
        {
            if (s == null) return;
            PaneIndex = s.PaneIndex;
            lblPaneBadge.Text = $"#{PaneIndex + 1}";

            if (availableSymbols != null && availableSymbols.Count > 0)
            {
                cboSymbol.Items.Clear();
                cboSymbol.Items.AddRange(availableSymbols.Cast<object>().ToArray());
            }

            Symbol = s.Symbol;
            cboSymbol.Text = Symbol;

            Interval = s.Interval;
            cboInterval.Text = Interval.ToDisplayString();

            DisplayType = s.DisplayType;
            cboChartType.SelectedIndex = Math.Clamp((int)DisplayType, 0, 4);

            PriceScale = s.PriceScale;
            cboPriceScale.SelectedIndex = Math.Clamp((int)PriceScale, 0, 2);

            _ = ReloadMarketDataAsync();
        }

        public ChartPaneSettings GetSettings()
        {
            return new ChartPaneSettings
            {
                PaneIndex = this.PaneIndex,
                Symbol = this.Symbol,
                Interval = this.Interval,
                DisplayType = this.DisplayType,
                PriceScale = this.PriceScale,
                ShowVolume = true,
                ShowMa = true
            };
        }

        public void SetSymbolList(List<string> symbols)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            this.BeginInvoke(() =>
            {
                string curr = cboSymbol.Text;
                cboSymbol.Items.Clear();
                cboSymbol.Items.AddRange(symbols.Cast<object>().ToArray());
                cboSymbol.Text = curr;
            });
        }

        public void ChangeSymbol(string newSymbol)
        {
            if (string.IsNullOrWhiteSpace(newSymbol)) return;
            newSymbol = newSymbol.Trim().ToUpperInvariant();
            if (newSymbol == Symbol && _klines.Count > 0) return;

            Symbol = newSymbol;
            cboSymbol.Text = Symbol;
            _ = ReloadMarketDataAsync();
            NotifySettingsChanged();
        }

        private void OnIntervalChanged(object? sender, EventArgs e)
        {
            Interval = ChartIntervalExtensions.ParseChartInterval(cboInterval.Text);

            _ = ReloadMarketDataAsync();
            NotifySettingsChanged();
        }

        public async Task ReloadMarketDataAsync()
        {
            _klineCts?.Cancel();
            _klineCts = new CancellationTokenSource();
            var ct = _klineCts.Token;

            lblHudInfo.Text = $"[{Symbol} {cboInterval.Text}] 正在加载历史合约 K 线与接入实时流...";

            // 1. 获取历史 K 线
            var history = await _marketService.GetHistoricalKlinesAsync(Symbol, Interval, limit: 500, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;

            lock (_klineLock)
            {
                _klines.Clear();
                _klines.AddRange(history);
            }

            // 2. 订阅 WebSocket 实时更新
            await _marketService.SubscribeKlineUpdatesAsync(Symbol, Interval, OnRealtimeKlineReceived, ct).ConfigureAwait(true);

            // 3. 初始重绘
            RedrawPlot(autoScale: true);
        }

        private void OnRealtimeKlineReceived(RawKline liveKline)
        {
            if (this.IsDisposed) return;

            lock (_klineLock)
            {
                if (_klines.Count == 0)
                {
                    _klines.Add(liveKline);
                }
                else
                {
                    int lastIdx = _klines.Count - 1;
                    var last = _klines[lastIdx];

                    if (Interval == ChartInterval.Tick)
                    {
                        // 逐笔 Tick 模式：每笔成交作为独立数据点追加，上限保持 1000 笔
                        _klines.Add(liveKline);
                        if (_klines.Count > 1000)
                        {
                            _klines.RemoveAt(0);
                        }
                    }
                    else if (liveKline.OpenTime <= last.OpenTime)
                    {
                        // 实时更新当前正在跳动的这根 K 线 (保留开盘价，实时拉伸最高/最低影线与最新收盘价)
                        _klines[lastIdx] = new RawKline
                        {
                            OpenTime = last.OpenTime,
                            CloseTime = last.CloseTime,
                            Open = last.Open > 0 ? last.Open : liveKline.Open,
                            High = Math.Max(last.High, liveKline.High),
                            Low = last.Low > 0 ? Math.Min(last.Low, liveKline.Low) : liveKline.Low,
                            Close = liveKline.Close,
                            Volume = last.Volume + liveKline.Volume,
                            QuoteVolume = last.QuoteVolume + liveKline.QuoteVolume,
                            TradeCount = last.TradeCount + liveKline.TradeCount,
                            TakerBuyVolume = last.TakerBuyVolume + liveKline.TakerBuyVolume,
                            TakerBuyQuoteVolume = last.TakerBuyQuoteVolume + liveKline.TakerBuyQuoteVolume
                        };
                    }
                    else
                    {
                        // 新的周期到来，开启新一根 K 线
                        _klines.Add(liveKline);
                        if (_klines.Count > 1000)
                        {
                            _klines.RemoveAt(0);
                        }
                    }
                }
            }

            // 标记待渲染，由 30FPS 定时器批量聚合渲染，彻底杜绝高频推流导致 UI 消息队列阻塞与卡顿
            _needsRender = true;
        }

        #endregion

        #region 图表与画线渲染引擎 (RedrawPlot)

        public void RedrawPlot(bool autoScale = false)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;

            List<RawKline> snapshot;
            lock (_klineLock)
            {
                snapshot = new List<RawKline>(_klines);
            }

            if (snapshot.Count == 0) return;

            formsPlot.Plot.Clear();

            string fontName = PlotHelper.GetInstalledChineseFont();
            formsPlot.Plot.FigureBackground.Color = ScottPlot.Color.FromHex("#0f172a");
            formsPlot.Plot.DataBackground.Color = ScottPlot.Color.FromHex("#1e293b");
            formsPlot.Plot.Axes.Color(ScottPlot.Color.FromHex("#94a3b8"));
            formsPlot.Plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#334155");

            int count = snapshot.Count;
            double basePrice = (double)snapshot[0].Close;
            if (basePrice <= 0) basePrice = 1.0;

            // 1. 坐标与数据转换
            double[] xs = new double[count];
            double[] opens = new double[count];
            double[] highs = new double[count];
            double[] lows = new double[count];
            double[] closes = new double[count];

            for (int i = 0; i < count; i++)
            {
                xs[i] = i;
                var k = snapshot[i];
                opens[i] = TransformPrice((double)k.Open, basePrice);
                highs[i] = TransformPrice((double)k.High, basePrice);
                lows[i] = TransformPrice((double)k.Low, basePrice);
                closes[i] = TransformPrice((double)k.Close, basePrice);
            }

            // 2. 根据 DisplayType 绘制主形态
            switch (DisplayType)
            {
                case ChartDisplayType.Candlestick:
                    {
                        var ohlcList = new List<OHLC>(count);
                        double microDelta = (highs.Max() - lows.Min()) * 0.001;
                        if (microDelta <= 0) microDelta = basePrice * 0.0001;

                        for (int i = 0; i < count; i++)
                        {
                            double o = opens[i];
                            double h = highs[i];
                            double l = lows[i];
                            double c = closes[i];

                            if (Interval == ChartInterval.Tick)
                            {
                                o = i > 0 ? closes[i - 1] : opens[0];
                                h = Math.Max(o, c);
                                l = Math.Min(o, c);
                                if (h == l)
                                {
                                    h += microDelta;
                                    l -= microDelta;
                                }
                            }
                            else if (h == l)
                            {
                                h += microDelta;
                                l -= microDelta;
                            }

                            ohlcList.Add(new OHLC(o, h, l, c, DateTime.FromOADate(i), TimeSpan.FromDays(0.8)));
                        }
                        var candlePlot = formsPlot.Plot.Add.Candlestick(ohlcList);
                        candlePlot.RisingColor = ScottPlot.Color.FromHex("#22c55e");
                        candlePlot.FallingColor = ScottPlot.Color.FromHex("#ef4444");
                        break;
                    }
                case ChartDisplayType.Line:
                    {
                        var line = formsPlot.Plot.Add.Scatter(xs, closes);
                        line.Color = ScottPlot.Color.FromHex("#38bdf8");
                        line.LineWidth = 1.5f;
                        line.MarkerSize = 0;
                        break;
                    }
                case ChartDisplayType.Mountain:
                    {
                        // 面积图
                        double minLow = lows.Min();
                        double baseY = minLow - (highs.Max() - minLow) * 0.05;
                        var polyXs = new List<double>(count + 2);
                        var polyYs = new List<double>(count + 2);
                        polyXs.Add(xs[0]); polyYs.Add(baseY);
                        for (int i = 0; i < count; i++) { polyXs.Add(xs[i]); polyYs.Add(closes[i]); }
                        polyXs.Add(xs[count - 1]); polyYs.Add(baseY);

                        var poly = formsPlot.Plot.Add.Polygon(polyXs.ToArray(), polyYs.ToArray());
                        poly.FillColor = ScottPlot.Color.FromHex("#0284c7").WithAlpha(0.3);
                        poly.LineColor = ScottPlot.Color.FromHex("#38bdf8");
                        poly.LineWidth = 1.5f;
                        break;
                    }
                case ChartDisplayType.OhlcBars:
                    {
                        var ohlcList = new List<OHLC>(count);
                        double microDelta = (highs.Max() - lows.Min()) * 0.001;
                        if (microDelta <= 0) microDelta = basePrice * 0.0001;

                        for (int i = 0; i < count; i++)
                        {
                            double o = opens[i];
                            double h = highs[i];
                            double l = lows[i];
                            double c = closes[i];

                            if (Interval == ChartInterval.Tick)
                            {
                                o = i > 0 ? closes[i - 1] : opens[0];
                                h = Math.Max(o, c);
                                l = Math.Min(o, c);
                                if (h == l)
                                {
                                    h += microDelta;
                                    l -= microDelta;
                                }
                            }
                            else if (h == l)
                            {
                                h += microDelta;
                                l -= microDelta;
                            }

                            ohlcList.Add(new OHLC(o, h, l, c, DateTime.FromOADate(i), TimeSpan.FromDays(0.8)));
                        }
                        var ohlcPlot = formsPlot.Plot.Add.OHLC(ohlcList);
                        break;
                    }
                case ChartDisplayType.HeikinAshi:
                    {
                        var haOhlc = CalculateHeikinAshi(snapshot, basePrice);
                        var candlePlot = formsPlot.Plot.Add.Candlestick(haOhlc);
                        candlePlot.RisingColor = ScottPlot.Color.FromHex("#10b981");
                        candlePlot.FallingColor = ScottPlot.Color.FromHex("#f43f5e");
                        break;
                    }
            }

            // 3. 绘制所有同步与本地画线 (Drawings)
            RenderDrawings(snapshot, basePrice);

            // 4. 重建十字光标
            _crosshair = formsPlot.Plot.Add.Crosshair(0, 0);
            _crosshair.LineColor = ScottPlot.Color.FromHex("#94a3b8").WithAlpha(0.6);
            _crosshair.LinePattern = LinePattern.Dashed;
            _crosshair.IsVisible = false;

            // 5. 更新顶部实时报价与 HUD
            var latestK = snapshot[count - 1];
            double currClose = (double)latestK.Close;
            double prevClose = count > 1 ? (double)snapshot[count - 2].Close : (double)latestK.Open;
            double chgPct = prevClose > 0 ? (currClose - prevClose) / prevClose * 100.0 : 0.0;
            string sign = chgPct >= 0 ? "+" : "";

            lblLivePrice.Text = $"{currClose:F2} ({sign}{chgPct:F2}%)";
            lblLivePrice.ForeColor = chgPct >= 0 ? Color.FromArgb(74, 222, 128) : Color.FromArgb(248, 113, 113);

            DateTime dt = TimeHelper.FromUnixTimeMilliseconds(latestK.CloseTime).ToLocalTime();
            lblHudInfo.Text = $"[{Symbol} {cboInterval.Text}] {dt:yyyy-MM-dd HH:mm:ss} | O: {latestK.Open:F2} H: {latestK.High:F2} L: {latestK.Low:F2} C: {latestK.Close:F2} | Vol: {latestK.Volume:N2} ({sign}{chgPct:F2}%)";

            if (autoScale)
            {
                formsPlot.Plot.Axes.Margins(0.02, 0.12);
                formsPlot.Plot.Axes.AutoScale();
            }

            formsPlot.Refresh();
        }

        private void RenderDrawings(List<RawKline> snapshot, double basePrice)
        {
            var drawings = _syncService.GetDrawings(Symbol);
            int count = snapshot.Count;

            foreach (var d in drawings)
            {
                double y1 = TransformPrice(d.Price1, basePrice);
                double y2 = TransformPrice(d.Price2, basePrice);
                double x1 = FindXIndexByTime(snapshot, d.Time1);

                ScottPlot.Color col = ScottPlot.Color.FromHex(d.ColorHex);
                if (d.Id == _selectedDrawingId)
                {
                    col = ScottPlot.Color.FromHex("#facc15"); // 高亮黄色
                }

                if (d.Type == DrawingType.HorizontalLine)
                {
                    // 绘制水平线
                    var hLine = formsPlot.Plot.Add.HorizontalLine(y1);
                    hLine.Color = col;
                    hLine.LineWidth = d.LineWidth;
                    hLine.LinePattern = LinePattern.Dashed;
                }
                else
                {
                    // 绘制趋势线
                    double x2 = FindXIndexByTime(snapshot, d.Time2);
                    var line = formsPlot.Plot.Add.Line(x1, y1, x2, y2);
                    line.Color = col;
                    line.LineWidth = d.LineWidth;

                    // 端点标记
                    var m1 = formsPlot.Plot.Add.Marker(x1, y1);
                    m1.Shape = MarkerShape.FilledCircle;
                    m1.Size = 5;
                    m1.Color = col;

                    var m2 = formsPlot.Plot.Add.Marker(x2, y2);
                    m2.Shape = MarkerShape.FilledCircle;
                    m2.Size = 5;
                    m2.Color = col;
                }
            }

            // 如果当前处于绘制预览中
            if (_isDrawingActive)
            {
                double x1 = FindXIndexByTime(snapshot, _drawTime1);
                double y1 = TransformPrice(_drawPrice1, basePrice);
                var previewLine = formsPlot.Plot.Add.Line(x1, y1, _previewX2, _previewY2);
                previewLine.Color = ScottPlot.Color.FromHex("#f59e0b");
                previewLine.LineWidth = 2.0f;
                previewLine.LinePattern = LinePattern.Dotted;
            }
        }

        private double FindXIndexByTime(List<RawKline> snapshot, long timeMs)
        {
            if (snapshot.Count == 0) return 0;
            if (timeMs <= snapshot[0].OpenTime) return 0;
            if (timeMs >= snapshot[^1].CloseTime) return snapshot.Count - 1;

            for (int i = 0; i < snapshot.Count; i++)
            {
                if (timeMs >= snapshot[i].OpenTime && timeMs <= snapshot[i].CloseTime)
                {
                    return i;
                }
                if (i < snapshot.Count - 1 && timeMs < snapshot[i + 1].OpenTime)
                {
                    return i;
                }
            }
            return snapshot.Count - 1;
        }

        private long FindTimeByXIndex(List<RawKline> snapshot, double x)
        {
            if (snapshot.Count == 0) return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int idx = Math.Clamp((int)Math.Round(x), 0, snapshot.Count - 1);
            return snapshot[idx].OpenTime;
        }

        private double TransformPrice(double price, double basePrice)
        {
            return PriceScale switch
            {
                PriceScaleMode.AbsolutePrice => price,
                PriceScaleMode.PercentageChange => basePrice > 0 ? (price - basePrice) / basePrice * 100.0 : 0.0,
                PriceScaleMode.Normalized100U => basePrice > 0 ? (price / basePrice) * 100.0 : 100.0,
                _ => price
            };
        }

        private double InverseTransformPrice(double transformedPrice, double basePrice)
        {
            return PriceScale switch
            {
                PriceScaleMode.AbsolutePrice => transformedPrice,
                PriceScaleMode.PercentageChange => basePrice > 0 ? basePrice * (1.0 + transformedPrice / 100.0) : transformedPrice,
                PriceScaleMode.Normalized100U => basePrice > 0 ? (transformedPrice / 100.0) * basePrice : transformedPrice,
                _ => transformedPrice
            };
        }

        private List<OHLC> CalculateHeikinAshi(List<RawKline> snapshot, double basePrice)
        {
            int count = snapshot.Count;
            var list = new List<OHLC>(count);
            if (count == 0) return list;

            double prevHaOpen = TransformPrice((double)snapshot[0].Open, basePrice);
            double prevHaClose = TransformPrice((double)snapshot[0].Close, basePrice);

            for (int i = 0; i < count; i++)
            {
                var k = snapshot[i];
                double o = TransformPrice((double)k.Open, basePrice);
                double h = TransformPrice((double)k.High, basePrice);
                double l = TransformPrice((double)k.Low, basePrice);
                double c = TransformPrice((double)k.Close, basePrice);

                double haClose = (o + h + l + c) / 4.0;
                double haOpen = (prevHaOpen + prevHaClose) / 2.0;
                double haHigh = Math.Max(h, Math.Max(haOpen, haClose));
                double haLow = Math.Min(l, Math.Min(haOpen, haClose));

                list.Add(new OHLC(haOpen, haHigh, haLow, haClose, DateTime.FromOADate(i), TimeSpan.FromDays(0.8)));

                prevHaOpen = haOpen;
                prevHaClose = haClose;
            }
            return list;
        }

        #endregion

        #region 鼠标画线与十字光标交互状态机

        private void OnPlotMouseMove(object? sender, MouseEventArgs e)
        {
            Coordinates mouseCoord;
            try
            {
                mouseCoord = formsPlot.Plot.GetCoordinates(new Pixel(e.X, e.Y));
            }
            catch
            {
                return;
            }

            List<RawKline> snapshot;
            lock (_klineLock) { snapshot = new List<RawKline>(_klines); }
            if (snapshot.Count == 0) return;

            double basePrice = (double)snapshot[0].Close;

            // 1. 十字光标跟随与广播
            if (_crosshair != null)
            {
                _crosshair.Position = mouseCoord;
                _crosshair.IsVisible = true;
            }

            int hoverIdx = Math.Clamp((int)Math.Round(mouseCoord.X), 0, snapshot.Count - 1);
            var hoverK = snapshot[hoverIdx];
            DateTime hoverTime = TimeHelper.FromUnixTimeMilliseconds(hoverK.OpenTime).ToLocalTime();
            double originalPrice = InverseTransformPrice(mouseCoord.Y, basePrice);

            lblHudInfo.Text = $"[{Symbol} {cboInterval.Text}] Bar #{hoverIdx} @ {hoverTime:yyyy-MM-dd HH:mm:ss} | O: {hoverK.Open:F2} H: {hoverK.High:F2} L: {hoverK.Low:F2} C: {hoverK.Close:F2} | 光标价格: {originalPrice:F2}";

            // 广播十字光标
            _syncService.BroadcastCrosshair(Symbol, hoverK.OpenTime, mouseCoord.Y, this);

            // 2. 趋势线实时橡皮筋拉伸预览
            if (_isDrawingActive && CurrentToolMode == DrawingToolMode.TrendLine)
            {
                _previewX2 = mouseCoord.X;
                _previewY2 = mouseCoord.Y;
                RedrawPlot(autoScale: false);
            }
            else
            {
                formsPlot.Refresh();
            }
        }

        private void OnPlotMouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                if (_isDrawingActive)
                {
                    _isDrawingActive = false;
                    RedrawPlot(autoScale: false);
                }
                return;
            }

            Coordinates mouseCoord;
            try
            {
                mouseCoord = formsPlot.Plot.GetCoordinates(new Pixel(e.X, e.Y));
            }
            catch
            {
                return;
            }

            List<RawKline> snapshot;
            lock (_klineLock) { snapshot = new List<RawKline>(_klines); }
            if (snapshot.Count == 0) return;

            double basePrice = (double)snapshot[0].Close;
            long clickTime = FindTimeByXIndex(snapshot, mouseCoord.X);
            double originalPrice = InverseTransformPrice(mouseCoord.Y, basePrice);

            switch (CurrentToolMode)
            {
                case DrawingToolMode.Pointer:
                    // 检查是否点击了已有画线进行选中
                    CheckHitTestDrawing(mouseCoord, snapshot, basePrice);
                    break;

                case DrawingToolMode.HorizontalLine:
                    {
                        var hItem = new DrawingItem
                        {
                            Symbol = this.Symbol,
                            Type = DrawingType.HorizontalLine,
                            Time1 = clickTime,
                            Price1 = originalPrice,
                            Time2 = clickTime,
                            Price2 = originalPrice,
                            ColorHex = "#f43f5e" // 玫瑰红
                        };
                        _syncService.AddDrawing(hItem, this);
                        RedrawPlot(autoScale: false);
                        break;
                    }

                case DrawingToolMode.TrendLine:
                    {
                        if (!_isDrawingActive)
                        {
                            // 第一点
                            _isDrawingActive = true;
                            _drawTime1 = clickTime;
                            _drawPrice1 = originalPrice;
                            _previewX2 = mouseCoord.X;
                            _previewY2 = mouseCoord.Y;
                        }
                        else
                        {
                            // 第二点完成
                            _isDrawingActive = false;
                            var tItem = new DrawingItem
                            {
                                Symbol = this.Symbol,
                                Type = DrawingType.TrendLine,
                                Time1 = _drawTime1,
                                Price1 = _drawPrice1,
                                Time2 = clickTime,
                                Price2 = originalPrice,
                                ColorHex = "#38bdf8" // 天蓝色
                            };
                            _syncService.AddDrawing(tItem, this);
                            RedrawPlot(autoScale: false);
                        }
                        break;
                    }

                case DrawingToolMode.Eraser:
                    // 擦除光标附近的线
                    DeleteNearestDrawing(mouseCoord, snapshot, basePrice);
                    break;
            }
        }

        private void CheckHitTestDrawing(Coordinates mouseCoord, List<RawKline> snapshot, double basePrice)
        {
            var drawings = _syncService.GetDrawings(Symbol);
            double minDist = double.MaxValue;
            string? hitId = null;

            foreach (var d in drawings)
            {
                double y1 = TransformPrice(d.Price1, basePrice);
                if (d.Type == DrawingType.HorizontalLine)
                {
                    double dist = Math.Abs(mouseCoord.Y - y1);
                    if (dist < minDist && dist < 5.0) // 容差
                    {
                        minDist = dist;
                        hitId = d.Id;
                    }
                }
                else
                {
                    double x1 = FindXIndexByTime(snapshot, d.Time1);
                    double x2 = FindXIndexByTime(snapshot, d.Time2);
                    double y2 = TransformPrice(d.Price2, basePrice);

                    double dx = x2 - x1;
                    double dy = y2 - y1;
                    double lenSq = dx * dx + dy * dy;
                    if (lenSq > 0)
                    {
                        double t = Math.Clamp(((mouseCoord.X - x1) * dx + (mouseCoord.Y - y1) * dy) / lenSq, 0.0, 1.0);
                        double px = x1 + t * dx;
                        double py = y1 + t * dy;
                        double dist = Math.Sqrt((mouseCoord.X - px) * (mouseCoord.X - px) + (mouseCoord.Y - py) * (mouseCoord.Y - py));
                        if (dist < minDist && dist < 8.0)
                        {
                            minDist = dist;
                            hitId = d.Id;
                        }
                    }
                }
            }

            _selectedDrawingId = hitId;
            RedrawPlot(autoScale: false);
        }

        private void DeleteNearestDrawing(Coordinates mouseCoord, List<RawKline> snapshot, double basePrice)
        {
            CheckHitTestDrawing(mouseCoord, snapshot, basePrice);
            if (!string.IsNullOrEmpty(_selectedDrawingId))
            {
                _syncService.DeleteDrawing(_selectedDrawingId, Symbol, this);
                _selectedDrawingId = null;
                RedrawPlot(autoScale: false);
            }
        }

        public void DeleteSelectedDrawing()
        {
            if (!string.IsNullOrEmpty(_selectedDrawingId))
            {
                _syncService.DeleteDrawing(_selectedDrawingId, Symbol, this);
                _selectedDrawingId = null;
                RedrawPlot(autoScale: false);
            }
        }

        #endregion

        #region 跨窗口同步监听绑定

        private void BindSyncEvents()
        {
            _syncService.OnDrawingAdded += (item, sender) =>
            {
                if (sender == this) return;
                if (item.Symbol.Equals(Symbol, StringComparison.OrdinalIgnoreCase))
                {
                    if (this.IsDisposed || !this.IsHandleCreated) return;
                    this.BeginInvoke(() => RedrawPlot(autoScale: false));
                }
            };

            _syncService.OnDrawingDeleted += (id, sym, sender) =>
            {
                if (sender == this) return;
                if (sym.Equals(Symbol, StringComparison.OrdinalIgnoreCase))
                {
                    if (this.IsDisposed || !this.IsHandleCreated) return;
                    this.BeginInvoke(() => RedrawPlot(autoScale: false));
                }
            };

            _syncService.OnDrawingsCleared += (sym, sender) =>
            {
                if (sender == this) return;
                if (sym.Equals(Symbol, StringComparison.OrdinalIgnoreCase))
                {
                    if (this.IsDisposed || !this.IsHandleCreated) return;
                    this.BeginInvoke(() => RedrawPlot(autoScale: false));
                }
            };

            _syncService.OnCrosshairMoved += (sym, time, price, sender) =>
            {
                if (sender == this) return;
                if (sym.Equals(Symbol, StringComparison.OrdinalIgnoreCase))
                {
                    if (this.IsDisposed || !this.IsHandleCreated) return;
                    this.BeginInvoke(() =>
                    {
                        List<RawKline> snapshot;
                        lock (_klineLock) { snapshot = new List<RawKline>(_klines); }
                        if (snapshot.Count == 0) return;

                        double x = FindXIndexByTime(snapshot, time);
                        if (_crosshair != null)
                        {
                            _crosshair.Position = new Coordinates(x, price);
                            _crosshair.IsVisible = true;
                            formsPlot.Refresh();
                        }
                    });
                }
            };
        }

        private void NotifySettingsChanged()
        {
            OnSettingsChanged?.Invoke(GetSettings());
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _renderTimer.Stop();
                _renderTimer.Dispose();
                _klineCts?.Cancel();
                _klineCts?.Dispose();
                _ = _marketService.UnsubscribeKlineUpdatesAsync(Symbol, Interval);
            }
            base.Dispose(disposing);
        }

        #endregion
    }
}
