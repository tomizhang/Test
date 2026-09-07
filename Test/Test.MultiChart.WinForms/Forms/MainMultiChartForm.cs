using Binance.Net.Enums;
using Common;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Color = System.Drawing.Color;
using Font = System.Drawing.Font;
using FontStyle = System.Drawing.FontStyle;
using Label = System.Windows.Forms.Label;
using Panel = System.Windows.Forms.Panel;
using Test.MultiChart.WinForms.Controls;
using Test.MultiChart.WinForms.Models;
using Test.MultiChart.WinForms.Services;

namespace Test.MultiChart.WinForms.Forms
{
    /// <summary>
    /// 多周期合约实盘多窗口图表与跨窗绘图同步主工作台
    /// </summary>
    public class MainMultiChartForm : Form
    {
        #region 服务与状态

        private readonly BinanceFuturesMarketDataService _marketService;
        private readonly DrawingSyncService _syncService;
        private MultiChartSettings _settings;

        private readonly List<SingleChartPaneControl> _paneControls = new(6);
        private readonly List<string> _availableFuturesSymbols = new();

        private ChartLayoutMode _currentLayoutMode = ChartLayoutMode.Quad_2x2;
        private DrawingToolMode _currentToolMode = DrawingToolMode.Pointer;

        #endregion

        #region UI 控件声明

        private Panel pnlTopBar = null!;
        private Panel pnlChartContainer = null!;
        private StatusStrip statusStrip = null!;
        private ToolStripStatusLabel lblStatusText = null!;
        private ToolStripStatusLabel lblStatusSymbolsCount = null!;

        // 顶部工具栏控件
        private Label lblLiveBadge = null!;
        private ComboBox cboGlobalSymbol = null!;
        private Button btnSyncAllSymbols = null!;

        private Button btnLayout1x1 = null!;
        private Button btnLayout1x2 = null!;
        private Button btnLayout2x1 = null!;
        private Button btnLayout2x2 = null!;
        private Button btnLayout3x2 = null!;
        private Button btnResetSplitters = null!;

        private Button btnToolPointer = null!;
        private Button btnToolTrendLine = null!;
        private Button btnToolHorizontalLine = null!;
        private Button btnToolDelete = null!;
        private Button btnToolClearAll = null!;

        private CheckBox chkAutoSync = null!;
        private CheckBox chkCrosshairSync = null!;
        private Button btnGlobalResetZoom = null!;

        #endregion

        public MainMultiChartForm()
        {
            _marketService = new BinanceFuturesMarketDataService();
            _syncService = DrawingSyncService.Instance;
            _settings = MultiChartSettingsManager.Load();

            InitializeComponents();
            ApplyDarkTheme();

            InitializePanes();
            ApplySettingsToUi();

            _ = InitializeMarketDataAndSymbolsAsync();
        }

        #region UI 初始化与工具栏构建

        private void InitializeComponents()
        {
            this.Text = "🚀 币安 USDT 永续合约多周期实盘工作台 (跨窗绘图同步 & 多形态自适应分屏)";
            this.Size = new Size(_settings.FormWidth, _settings.FormHeight);
            this.MinimumSize = new Size(1100, 700);
            this.StartPosition = FormStartPosition.CenterScreen;

            if (_settings.IsMaximized)
            {
                this.WindowState = FormWindowState.Maximized;
            }

            // 1. 顶部工具栏
            BuildTopBar();

            // 2. 底部状态栏
            statusStrip = new StatusStrip
            {
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.FromArgb(203, 213, 225),
                SizingGrip = true
            };
            lblStatusText = new ToolStripStatusLabel { Text = "系统就绪，正在拉取币安 USDT 本位永续合约全币种列表..." };
            lblStatusSymbolsCount = new ToolStripStatusLabel { Text = "合约总数: 0", Alignment = ToolStripItemAlignment.Right };
            statusStrip.Items.AddRange(new ToolStripItem[] { lblStatusText, lblStatusSymbolsCount });
            this.Controls.Add(statusStrip);

            // 3. 主图表分屏容器
            pnlChartContainer = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(15, 23, 42)
            };
            this.Controls.Add(pnlChartContainer);

            pnlChartContainer.BringToFront();
        }

        private void BuildTopBar()
        {
            pnlTopBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 44,
                BackColor = Color.FromArgb(30, 41, 59), // Slate 800
                Padding = new Padding(6, 6, 6, 6)
            };

            int left = 8;

            // 状态指示灯
            lblLiveBadge = new Label
            {
                Text = "⚡ 币安合约",
                Location = new Point(left, 10),
                AutoSize = true,
                ForeColor = Color.FromArgb(74, 222, 128), // Green 400
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold)
            };
            pnlTopBar.Controls.Add(lblLiveBadge);
            left += 85;

            // 全局交易对选择
            cboGlobalSymbol = new ComboBox
            {
                Location = new Point(left, 8),
                Width = 120,
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.FromArgb(250, 204, 21), // Yellow 400
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold)
            };
            cboGlobalSymbol.Items.AddRange(new object[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "HEMIUSDT", "1000PEPEUSDT", "DOGEUSDT" });
            cboGlobalSymbol.Text = _settings.GlobalSymbol;
            pnlTopBar.Controls.Add(cboGlobalSymbol);
            left += 125;

            btnSyncAllSymbols = CreateButton("一键同币", left, 26, 75, Color.FromArgb(37, 99, 235));
            btnSyncAllSymbols.Click += (s, e) => SyncAllPanesToSymbol(cboGlobalSymbol.Text);
            pnlTopBar.Controls.Add(btnSyncAllSymbols);
            left += 82;

            // 分割竖线
            pnlTopBar.Controls.Add(CreateVerticalSeparator(left));
            left += 10;

            // 分屏布局按钮
            btnLayout1x1 = CreateButton("1×1", left, 26, 42, Color.FromArgb(51, 65, 85));
            btnLayout1x1.Click += (s, e) => SwitchLayout(ChartLayoutMode.Single_1x1);
            pnlTopBar.Controls.Add(btnLayout1x1);
            left += 46;

            btnLayout1x2 = CreateButton("1×2", left, 26, 42, Color.FromArgb(51, 65, 85));
            btnLayout1x2.Click += (s, e) => SwitchLayout(ChartLayoutMode.DualVertical_1x2);
            pnlTopBar.Controls.Add(btnLayout1x2);
            left += 46;

            btnLayout2x1 = CreateButton("2×1", left, 26, 42, Color.FromArgb(51, 65, 85));
            btnLayout2x1.Click += (s, e) => SwitchLayout(ChartLayoutMode.DualHorizontal_2x1);
            pnlTopBar.Controls.Add(btnLayout2x1);
            left += 46;

            btnLayout2x2 = CreateButton("2×2", left, 26, 42, Color.FromArgb(5, 150, 105)); // 默认选中绿
            btnLayout2x2.Click += (s, e) => SwitchLayout(ChartLayoutMode.Quad_2x2);
            pnlTopBar.Controls.Add(btnLayout2x2);
            left += 46;

            btnLayout3x2 = CreateButton("3×2", left, 26, 42, Color.FromArgb(51, 65, 85));
            btnLayout3x2.Click += (s, e) => SwitchLayout(ChartLayoutMode.Six_3x2);
            pnlTopBar.Controls.Add(btnLayout3x2);
            left += 46;

            btnResetSplitters = CreateButton("⚖️ 均分", left, 26, 55, Color.FromArgb(71, 85, 105));
            btnResetSplitters.Click += (s, e) => ResetSplitterRatios();
            pnlTopBar.Controls.Add(btnResetSplitters);
            left += 62;

            // 分割竖线
            pnlTopBar.Controls.Add(CreateVerticalSeparator(left));
            left += 10;

            // 绘图工具栏
            btnToolPointer = CreateButton("🖱️ 光标", left, 26, 62, Color.FromArgb(5, 150, 105));
            btnToolPointer.Click += (s, e) => SelectDrawingTool(DrawingToolMode.Pointer);
            pnlTopBar.Controls.Add(btnToolPointer);
            left += 66;

            btnToolTrendLine = CreateButton("📐 趋势线", left, 26, 75, Color.FromArgb(51, 65, 85));
            btnToolTrendLine.Click += (s, e) => SelectDrawingTool(DrawingToolMode.TrendLine);
            pnlTopBar.Controls.Add(btnToolTrendLine);
            left += 79;

            btnToolHorizontalLine = CreateButton("➖ 水平线", left, 26, 75, Color.FromArgb(51, 65, 85));
            btnToolHorizontalLine.Click += (s, e) => SelectDrawingTool(DrawingToolMode.HorizontalLine);
            pnlTopBar.Controls.Add(btnToolHorizontalLine);
            left += 79;

            btnToolDelete = CreateButton("🗑️ 删线", left, 26, 58, Color.FromArgb(220, 38, 38));
            btnToolDelete.Click += (s, e) => DeleteSelectedDrawingOnAllPanes();
            pnlTopBar.Controls.Add(btnToolDelete);
            left += 62;

            btnToolClearAll = CreateButton("🧹 清空", left, 26, 58, Color.FromArgb(127, 29, 29));
            btnToolClearAll.Click += (s, e) => ClearAllDrawingsCurrentSymbol();
            pnlTopBar.Controls.Add(btnToolClearAll);
            left += 66;

            // 分割竖线
            pnlTopBar.Controls.Add(CreateVerticalSeparator(left));
            left += 10;

            // 同步控制开关
            chkAutoSync = new CheckBox
            {
                Text = "☑️ 跨窗自动同步绘图",
                Location = new Point(left, 10),
                AutoSize = true,
                Checked = _settings.AutoSyncDrawings,
                ForeColor = Color.FromArgb(74, 222, 128),
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold)
            };
            chkAutoSync.CheckedChanged += (s, e) =>
            {
                _syncService.AutoSyncEnabled = chkAutoSync.Checked;
                _settings.AutoSyncDrawings = chkAutoSync.Checked;
            };
            pnlTopBar.Controls.Add(chkAutoSync);
            left += 155;

            chkCrosshairSync = new CheckBox
            {
                Text = "🎯 十字光标时间轴同步",
                Location = new Point(left, 10),
                AutoSize = true,
                Checked = _settings.SyncCrosshair,
                ForeColor = Color.FromArgb(56, 189, 248),
                Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold)
            };
            chkCrosshairSync.CheckedChanged += (s, e) =>
            {
                _syncService.CrosshairSyncEnabled = chkCrosshairSync.Checked;
                _settings.SyncCrosshair = chkCrosshairSync.Checked;
            };
            pnlTopBar.Controls.Add(chkCrosshairSync);
            left += 165;

            btnGlobalResetZoom = CreateButton("🔍 全局复位", left, 26, 85, Color.FromArgb(14, 116, 144));
            btnGlobalResetZoom.Click += (s, e) =>
            {
                foreach (var pane in _paneControls)
                {
                    pane.RedrawPlot(autoScale: true);
                }
            };
            pnlTopBar.Controls.Add(btnGlobalResetZoom);

            this.Controls.Add(pnlTopBar);
        }

        private Button CreateButton(string text, int x, int height, int width, Color backColor)
        {
            var btn = new Button
            {
                Text = text,
                Location = new Point(x, 8),
                Size = new Size(width, height),
                FlatStyle = FlatStyle.Flat,
                BackColor = backColor,
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei", 8.5F),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }

        private Label CreateVerticalSeparator(int x)
        {
            return new Label
            {
                Location = new Point(x, 8),
                Size = new Size(2, 28),
                BackColor = Color.FromArgb(71, 85, 105)
            };
        }

        private void ApplyDarkTheme()
        {
            this.BackColor = Color.FromArgb(15, 23, 42); // Slate 900
            this.ForeColor = Color.FromArgb(248, 250, 252);
        }

        #endregion

        #region 分屏初始化与动态无级拉伸 SplitContainer 树构建

        private void InitializePanes()
        {
            _paneControls.Clear();
            for (int i = 0; i < 6; i++)
            {
                var pane = new SingleChartPaneControl(i, _marketService, _syncService);
                pane.OnSettingsChanged += OnPaneSettingsChanged;
                _paneControls.Add(pane);
            }
        }

        private void ApplySettingsToUi()
        {
            _currentLayoutMode = _settings.LayoutMode;
            _syncService.AutoSyncEnabled = _settings.AutoSyncDrawings;
            _syncService.CrosshairSyncEnabled = _settings.SyncCrosshair;

            for (int i = 0; i < _settings.Panes.Count && i < _paneControls.Count; i++)
            {
                _paneControls[i].ApplySettings(_settings.Panes[i], _availableFuturesSymbols);
            }

            RebuildLayoutContainers(_currentLayoutMode);
        }

        private void SwitchLayout(ChartLayoutMode mode)
        {
            _currentLayoutMode = mode;
            _settings.LayoutMode = mode;
            RebuildLayoutContainers(mode);
            UpdateLayoutButtonStyles(mode);
        }

        private void UpdateLayoutButtonStyles(ChartLayoutMode mode)
        {
            btnLayout1x1.BackColor = mode == ChartLayoutMode.Single_1x1 ? Color.FromArgb(5, 150, 105) : Color.FromArgb(51, 65, 85);
            btnLayout1x2.BackColor = mode == ChartLayoutMode.DualVertical_1x2 ? Color.FromArgb(5, 150, 105) : Color.FromArgb(51, 65, 85);
            btnLayout2x1.BackColor = mode == ChartLayoutMode.DualHorizontal_2x1 ? Color.FromArgb(5, 150, 105) : Color.FromArgb(51, 65, 85);
            btnLayout2x2.BackColor = mode == ChartLayoutMode.Quad_2x2 ? Color.FromArgb(5, 150, 105) : Color.FromArgb(51, 65, 85);
            btnLayout3x2.BackColor = mode == ChartLayoutMode.Six_3x2 ? Color.FromArgb(5, 150, 105) : Color.FromArgb(51, 65, 85);
        }

        /// <summary>
        /// 动态构建多层嵌套的 SplitContainer 树 (支持自由拖拽边框调整每一个分屏的长宽比例)
        /// </summary>
        private void RebuildLayoutContainers(ChartLayoutMode mode)
        {
            pnlChartContainer.SuspendLayout();
            pnlChartContainer.Controls.Clear();

            switch (mode)
            {
                case ChartLayoutMode.Single_1x1:
                    {
                        var pane0 = _paneControls[0];
                        pane0.Dock = DockStyle.Fill;
                        pnlChartContainer.Controls.Add(pane0);
                        break;
                    }

                case ChartLayoutMode.DualVertical_1x2:
                    {
                        var split = CreateSplitContainer(Orientation.Horizontal);
                        split.Panel1.Controls.Add(_paneControls[0]);
                        split.Panel2.Controls.Add(_paneControls[1]);
                        _paneControls[0].Dock = DockStyle.Fill;
                        _paneControls[1].Dock = DockStyle.Fill;
                        pnlChartContainer.Controls.Add(split);
                        SafeSetSplitterDistance(split, 0.5);
                        break;
                    }

                case ChartLayoutMode.DualHorizontal_2x1:
                    {
                        var split = CreateSplitContainer(Orientation.Vertical);
                        split.Panel1.Controls.Add(_paneControls[0]);
                        split.Panel2.Controls.Add(_paneControls[1]);
                        _paneControls[0].Dock = DockStyle.Fill;
                        _paneControls[1].Dock = DockStyle.Fill;
                        pnlChartContainer.Controls.Add(split);
                        SafeSetSplitterDistance(split, 0.5);
                        break;
                    }

                case ChartLayoutMode.Quad_2x2:
                    {
                        var splitMain = CreateSplitContainer(Orientation.Vertical);

                        var splitLeft = CreateSplitContainer(Orientation.Horizontal);
                        splitLeft.Panel1.Controls.Add(_paneControls[0]);
                        splitLeft.Panel2.Controls.Add(_paneControls[1]);
                        _paneControls[0].Dock = DockStyle.Fill;
                        _paneControls[1].Dock = DockStyle.Fill;

                        var splitRight = CreateSplitContainer(Orientation.Horizontal);
                        splitRight.Panel1.Controls.Add(_paneControls[2]);
                        splitRight.Panel2.Controls.Add(_paneControls[3]);
                        _paneControls[2].Dock = DockStyle.Fill;
                        _paneControls[3].Dock = DockStyle.Fill;

                        splitMain.Panel1.Controls.Add(splitLeft);
                        splitMain.Panel2.Controls.Add(splitRight);
                        pnlChartContainer.Controls.Add(splitMain);

                        SafeSetSplitterDistance(splitMain, 0.5);
                        SafeSetSplitterDistance(splitLeft, 0.5);
                        SafeSetSplitterDistance(splitRight, 0.5);
                        break;
                    }

                case ChartLayoutMode.Six_3x2:
                    {
                        var splitMain = CreateSplitContainer(Orientation.Vertical);

                        var splitCol1 = CreateSplitContainer(Orientation.Horizontal);
                        splitCol1.Panel1.Controls.Add(_paneControls[0]);
                        splitCol1.Panel2.Controls.Add(_paneControls[1]);
                        _paneControls[0].Dock = DockStyle.Fill;
                        _paneControls[1].Dock = DockStyle.Fill;

                        var splitSub = CreateSplitContainer(Orientation.Vertical);

                        var splitCol2 = CreateSplitContainer(Orientation.Horizontal);
                        splitCol2.Panel1.Controls.Add(_paneControls[2]);
                        splitCol2.Panel2.Controls.Add(_paneControls[3]);
                        _paneControls[2].Dock = DockStyle.Fill;
                        _paneControls[3].Dock = DockStyle.Fill;

                        var splitCol3 = CreateSplitContainer(Orientation.Horizontal);
                        splitCol3.Panel1.Controls.Add(_paneControls[4]);
                        splitCol3.Panel2.Controls.Add(_paneControls[5]);
                        _paneControls[4].Dock = DockStyle.Fill;
                        _paneControls[5].Dock = DockStyle.Fill;

                        splitSub.Panel1.Controls.Add(splitCol2);
                        splitSub.Panel2.Controls.Add(splitCol3);

                        splitMain.Panel1.Controls.Add(splitCol1);
                        splitMain.Panel2.Controls.Add(splitSub);

                        pnlChartContainer.Controls.Add(splitMain);

                        SafeSetSplitterDistance(splitMain, 0.333);
                        SafeSetSplitterDistance(splitSub, 0.5);
                        SafeSetSplitterDistance(splitCol1, 0.5);
                        SafeSetSplitterDistance(splitCol2, 0.5);
                        SafeSetSplitterDistance(splitCol3, 0.5);
                        break;
                    }
            }

            pnlChartContainer.ResumeLayout(true);

            // 触发所有子窗口刷新
            foreach (var pane in _paneControls)
            {
                pane.RedrawPlot(autoScale: true);
            }
        }

        private void SafeSetSplitterDistance(SplitContainer split, double ratio = 0.5)
        {
            try
            {
                int total = split.Orientation == Orientation.Vertical ? split.Width : split.Height;
                int minAllowed = split.Panel1MinSize;
                int maxAllowed = total - split.Panel2MinSize - split.SplitterWidth;

                if (maxAllowed > minAllowed && total > 0)
                {
                    int dist = (int)(total * ratio);
                    dist = Math.Clamp(dist, minAllowed, maxAllowed);
                    split.SplitterDistance = dist;
                }
            }
            catch
            {
                // 忽略未完成渲染布局时的初始尺寸边界异常
            }
        }

        private SplitContainer CreateSplitContainer(Orientation orientation)
        {
            return new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = orientation,
                SplitterWidth = 5,
                Panel1MinSize = 50,
                Panel2MinSize = 50,
                BackColor = Color.FromArgb(51, 65, 85),
                BorderStyle = BorderStyle.None
            };
        }

        private void ResetSplitterRatios()
        {
            RebuildLayoutContainers(_currentLayoutMode);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 窗体正式展示后，根据实际屏幕像素尺寸平滑重置一次分割线比例
            this.BeginInvoke(() =>
            {
                RebuildLayoutContainers(_currentLayoutMode);
            });
        }

        #endregion

        #region 行情与交易对异步加载

        private async Task InitializeMarketDataAndSymbolsAsync()
        {
            _marketService.OnStatusLog += (msg, isErr) =>
            {
                if (this.IsDisposed || !this.IsHandleCreated) return;
                this.BeginInvoke(() =>
                {
                    lblStatusText.Text = msg;
                    lblStatusText.ForeColor = isErr ? Color.FromArgb(248, 113, 113) : Color.FromArgb(203, 213, 225);
                });
            };

            var symbols = await _marketService.GetUsdtFuturesSymbolsAsync();
            _availableFuturesSymbols.Clear();
            _availableFuturesSymbols.AddRange(symbols);

            lblStatusSymbolsCount.Text = $"USDT合约总数: {symbols.Count}";

            cboGlobalSymbol.Items.Clear();
            cboGlobalSymbol.Items.AddRange(symbols.Cast<object>().ToArray());
            if (string.IsNullOrWhiteSpace(cboGlobalSymbol.Text))
            {
                cboGlobalSymbol.Text = "BTCUSDT";
            }

            foreach (var pane in _paneControls)
            {
                pane.SetSymbolList(symbols);
            }
        }

        private void SyncAllPanesToSymbol(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return;
            symbol = symbol.Trim().ToUpperInvariant();
            cboGlobalSymbol.Text = symbol;
            _settings.GlobalSymbol = symbol;

            foreach (var pane in _paneControls)
            {
                pane.ChangeSymbol(symbol);
            }
        }

        #endregion

        #region 绘图工具箱与事件控制

        private void SelectDrawingTool(DrawingToolMode mode)
        {
            _currentToolMode = mode;
            foreach (var pane in _paneControls)
            {
                pane.CurrentToolMode = mode;
            }

            btnToolPointer.BackColor = mode == DrawingToolMode.Pointer ? Color.FromArgb(5, 150, 105) : Color.FromArgb(51, 65, 85);
            btnToolTrendLine.BackColor = mode == DrawingToolMode.TrendLine ? Color.FromArgb(5, 150, 105) : Color.FromArgb(51, 65, 85);
            btnToolHorizontalLine.BackColor = mode == DrawingToolMode.HorizontalLine ? Color.FromArgb(5, 150, 105) : Color.FromArgb(51, 65, 85);
        }

        private void DeleteSelectedDrawingOnAllPanes()
        {
            foreach (var pane in _paneControls)
            {
                pane.DeleteSelectedDrawing();
            }
        }

        private void ClearAllDrawingsCurrentSymbol()
        {
            string sym = cboGlobalSymbol.Text.Trim().ToUpperInvariant();
            if (MessageBox.Show($"确定要清空合约 {sym} 下的所有趋势线与水平线吗？", "清空确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                _syncService.ClearDrawings(sym);
                foreach (var pane in _paneControls)
                {
                    pane.RedrawPlot(autoScale: false);
                }
            }
        }

        private void OnPaneSettingsChanged(ChartPaneSettings s)
        {
            if (s.PaneIndex >= 0 && s.PaneIndex < _settings.Panes.Count)
            {
                _settings.Panes[s.PaneIndex] = s;
            }
        }

        #endregion

        #region 窗体生命周期与配置保存

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 保存持久化配置
            _settings.FormWidth = this.WindowState == FormWindowState.Normal ? this.Width : this.RestoreBounds.Width;
            _settings.FormHeight = this.WindowState == FormWindowState.Normal ? this.Height : this.RestoreBounds.Height;
            _settings.IsMaximized = this.WindowState == FormWindowState.Maximized;
            _settings.GlobalSymbol = cboGlobalSymbol.Text;
            _settings.AutoSyncDrawings = chkAutoSync.Checked;
            _settings.SyncCrosshair = chkCrosshairSync.Checked;
            _settings.LayoutMode = _currentLayoutMode;

            for (int i = 0; i < _paneControls.Count; i++)
            {
                if (i < _settings.Panes.Count)
                {
                    _settings.Panes[i] = _paneControls[i].GetSettings();
                }
            }

            MultiChartSettingsManager.Save(_settings);

            _marketService.Dispose();
            base.OnFormClosing(e);
        }

        #endregion
    }
}
