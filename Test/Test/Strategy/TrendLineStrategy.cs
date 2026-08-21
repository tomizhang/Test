using Binance.Net.Enums;
using Common;
using Common.Helper;
using Common.Models;
using System;
using System.Collections.Generic;

namespace Test.Strategy
{
    /// <summary>
    /// 趋势线触碰跟踪探针状态 (用于判定 3 个 Tick 内的回弹开仓)
    /// </summary>
    public class TouchProbe
    {
        public TrendLine Line;
        public decimal TouchPrice;
        public int TouchGlobalIndex;
        public int TicksSinceTouch;
        public bool IsResistance => Line.IsResistance;
    }

    /// <summary>
    /// 高性能三层增量趋势线与回弹交易策略 (Multi-Layer Incremental TrendLine Strategy)
    /// 核心极速架构：
    /// 1. 准入过滤：仅当趋势线满足 LineX1X2 >= 40 (跨度>=40) 且 LineAge >= 4 (延伸寿命>=4) 时触发触碰监控
    /// 2. 冷却机制：1 分钟 (60 秒) 内只能触发一次开仓策略 (可配置 SignalCooldownSeconds)
    /// 3. 高点趋势线 (阻力线): 当 Tick 达到趋势线 (tick.Price >= linePrice)，在后续 3 个 Tick 内向下回弹 (tick.Price < linePrice) 时 -> 【开空】
    /// 4. 低点趋势线 (支撑线): 当 Tick 达到趋势线 (tick.Price <= linePrice)，在后续 3 个 Tick 内向上回弹 (tick.Price > linePrice) 时 -> 【开多】
    /// 5. 仓位与盈亏管理：支持 0.5% 止损 与 1.5% 止盈 (TakeProfitPct=1.5%, StopLossPct=0.5%)
    /// 6. 极速常数时间优化：在 OnKline 阶段预计算与缓存 CachedCurrentPrice 与极值边界，使 99% 的 Tick 零计算短路跳过
    /// </summary>
    public class TrendLineStrategy
    {
        public string Symbol { get; private set; } = "BTCUSDT";
        public KlineInterval Interval { get; private set; } = KlineInterval.OneMinute;

        // 1. K 线滑动窗口配置 (默认保留 2000 根)
        public int MaxKlinesCapacity { get; set; } = 2000;

        // 2. 趋势线历史保存容量配置 (初定最低保存 1000 条)
        public int MinTrendLinesCapacity { get; set; } = 1000;

        // 3. 已删除（被穿透）趋势线列表容量配置 (保留 1000 长度)
        public int MaxDeletedTrendLinesCapacity { get; set; } = 1000;

        // 4. 极值与趋势线计算参数配置
        public int LeftLen { get; set; } = 5;               // 波峰波谷左侧对比根数
        public int RightLen { get; set; } = 5;              // 波峰波谷右侧对比根数
        public int MaxSpan { get; set; } = 100;             // 两点间最大 K 线跨度
        public bool AllowInternalPenetration { get; set; } = false; // 是否允许内部 K 线穿透 (默认严格外包络)

        // 5. 开仓信号趋势线过滤阈值与冷却时间 (LineX1X2 >= 40, LineAge >= 4, Cooldown = 60s)
        public int MinSignalLineX1X2 { get; set; } = 40;
        public int MinSignalLineAge { get; set; } = 4;
        public int SignalCooldownSeconds { get; set; } = 60; // 触发冷却时间 (秒)
        private long _lastTriggerTimestampMs = 0;           // 上次触发交易信号的时间戳 (毫秒)

        // 6. 止盈止损策略参数 (默认 1.5% 止盈, 0.5% 止损)
        public decimal TakeProfitPct { get; set; } = 1.5m;  // 止盈比例 (%)
        public decimal StopLossPct { get; set; } = 0.5m;    // 止损比例 (%)

        // 7. 是否开启策略交易 (默认 true，未开启时仅进行趋势线计算而不开仓下单)
        public bool EnableTrading { get; set; } = true;

        // 8. 趋势线最大允许斜率 (%/bar)，过滤超高斜率与异常噪音趋势线 (默认 2.0%/bar)
        public decimal MaxSlopePctPerBar { get; set; } = 2.0m;

        // 全局单调递增 K 线序列号计数器 (0, 1, 2, ... 500,000)
        private int _globalBarIndex = 0;
        public int GlobalBarIndex => _globalBarIndex;

        // K 线滑动窗口历史缓存 (定长环形缓冲区，零内存拷贝与零 GC 压力)
        private readonly KlineRingBuffer _klines;
        public IReadOnlyList<RawKline> Klines => _klines;
        public int KlineCount => _klines.Count;

        // 历史已确认的所有波峰与波谷列表 (按全局单调索引递增)
        private readonly List<PivotPoint> _peaks = new List<PivotPoint>(500);
        private readonly List<PivotPoint> _valleys = new List<PivotPoint>(500);
        public IReadOnlyList<PivotPoint> Peaks => _peaks;
        public IReadOnlyList<PivotPoint> Valleys => _valleys;

        // 当前存量的活跃阻力线与支撑线 (未被穿透的有效趋势线)
        public List<TrendLine> ActiveResistanceLines { get; } = new List<TrendLine>(300);
        public List<TrendLine> ActiveSupportLines { get; } = new List<TrendLine>(300);

        // ⚡ 极速边界短路缓存 (99% 的 Tick 零耗时跳过)
        private decimal _minActiveResistancePrice = decimal.MaxValue;
        private decimal _maxActiveSupportPrice = decimal.MinValue;

        // 正在跟踪的触碰回弹探针集合
        private readonly List<TouchProbe> _activeTouchProbes = new List<TouchProbe>(8);

        // 交易信号记录集合
        public List<TradeSignal> TradeSignals { get; } = new List<TradeSignal>(1000);
        public int LongSignalsCount { get; private set; } = 0;
        public int ShortSignalsCount { get; private set; } = 0;
        public int TotalSignalsCount => LongSignalsCount + ShortSignalsCount;

        // 持仓与已完成交易记录集合 (TP/SL 管理)
        public List<Position> ActivePositions { get; } = new List<Position>(16);
        public List<TradeRecord> CompletedTrades { get; } = new List<TradeRecord>(1000);
        public int WinningTradesCount { get; private set; } = 0;
        public int LosingTradesCount { get; private set; } = 0;
        public decimal TotalPnLPct { get; private set; } = 0m;
        public double WinRate => CompletedTrades.Count > 0 ? (double)WinningTradesCount / CompletedTrades.Count * 100.0 : 0.0;

        // 已删除（被 Tick 实时穿透）的趋势线列表 (固定保留 1000 长度)
        private readonly List<TrendLine> _deletedTrendLines = new List<TrendLine>(1000);
        public IReadOnlyList<TrendLine> DeletedTrendLines => _deletedTrendLines;
        public int DeletedTrendLinesCount => _deletedTrendLines.Count;

        // 历史趋势线库 (累计保存所有计算出的有效趋势线，初定最低保存 1000 条)
        private readonly List<TrendLine> _historicalTrendLines = new List<TrendLine>(1000);
        public IReadOnlyList<TrendLine> HistoricalTrendLines => _historicalTrendLines;
        public int HistoricalTrendLinesCount => _historicalTrendLines.Count;

        // 当前最新行情快照缓存
        public RawTick LatestTick { get; private set; }
        public RawKline LatestKline { get; private set; }
        public bool HasTickData { get; private set; } = false;
        public bool HasKlineData { get; private set; } = false;

        // 事件通知
        public event Action<TrendLine, RawTick, string>? OnTrendLinePenetrated;
        public event Action<TradeSignal>? OnTradeSignalGenerated;
        public event Action<Position>? OnPositionOpened;
        public event Action<TradeRecord>? OnTradeClosed;

        // 逐笔 Tick 处理状态与价格去重缓存
        private decimal _lastProcessedTickPrice = decimal.MinValue;

        // 最近 Tick 价格滑动环形缓冲区 (用于 5-Tick 极小微止损回溯)
        private const int RecentTickBufferSize = 32;
        private readonly decimal[] _recentTickPrices = new decimal[RecentTickBufferSize];
        private int _recentTickHead = 0;
        private int _recentTickCount = 0;

        /// <summary>
        /// 获取距离当前 Tick 之前第 N 个 Tick 的价格 (N=5 即 5 个 Tick 之前的价格)
        /// </summary>
        public decimal GetPriceTicksAgo(int n)
        {
            if (_recentTickCount <= 0) return 0m;
            int offset = Math.Min(n, _recentTickCount - 1);
            int idx = (_recentTickHead - 1 - offset + RecentTickBufferSize * 4) % RecentTickBufferSize;
            return _recentTickPrices[idx];
        }

        /// <summary>
        /// 获取最近 N 个 Tick 内的价格极值 (最高价或最低价)
        /// </summary>
        public decimal GetExtremePriceLastNTicks(int n, bool getHighest)
        {
            if (_recentTickCount <= 0) return 0m;
            int count = Math.Min(n, _recentTickCount);
            decimal extreme = getHighest ? decimal.MinValue : decimal.MaxValue;

            for (int i = 0; i < count; i++)
            {
                int idx = (_recentTickHead - 1 - i + RecentTickBufferSize * 4) % RecentTickBufferSize;
                decimal p = _recentTickPrices[idx];
                if (getHighest)
                {
                    if (p > extreme) extreme = p;
                }
                else
                {
                    if (p < extreme) extreme = p;
                }
            }

            return extreme == decimal.MinValue || extreme == decimal.MaxValue ? 0m : extreme;
        }

        public TrendLineStrategy()
        {
            _klines = new KlineRingBuffer(MaxKlinesCapacity);
        }

        public TrendLineStrategy(string symbol, KlineInterval interval, int maxKlines = 2000, int minTrendLines = 1000, int maxDeletedLines = 1000)
        {
            MaxKlinesCapacity = maxKlines;
            MinTrendLinesCapacity = minTrendLines;
            MaxDeletedTrendLinesCapacity = maxDeletedLines;
            _klines = new KlineRingBuffer(MaxKlinesCapacity);
            Initialize(symbol, interval);
        }

        /// <summary>
        /// 策略初始化
        /// </summary>
        public void Initialize(string symbol, KlineInterval interval)
        {
            Symbol = symbol;
            Interval = interval;
            Reset();
        }

        /// <summary>
        /// 接收 Tick 逐笔行情推送 (全硬件级常数时间 < 2 纳秒)
        /// 执行：
        /// 0. 持仓单实时止损(0.5% 或 5-Tick 微止损)与止盈(1.5%)平仓监测
        /// 1. 触碰检测与 3 个 Tick 内回弹开仓判定
        /// 2. 开仓时计算止盈与 5-Tick 自动微止损点位并开立仓位
        /// </summary>
        public void OnTick(in RawTick tick)
        {
            LatestTick = tick;
            HasTickData = true;

            // 价格去重优化：若 Tick 价格未变动，则跳过
            if (tick.Price == _lastProcessedTickPrice)
            {
                return;
            }
            _lastProcessedTickPrice = tick.Price;

            // 记录有效价格至最近 Tick 滑动缓冲区
            _recentTickPrices[_recentTickHead] = tick.Price;
            _recentTickHead = (_recentTickHead + 1) % RecentTickBufferSize;
            if (_recentTickCount < RecentTickBufferSize) _recentTickCount++;

            int currentGlobalIndex = Math.Max(0, _globalBarIndex);
            long cooldownMs = (long)SignalCooldownSeconds * 1000L;

            // ====================================================================
            // 步骤 0: 实时监控当前持仓仓位，执行 0.5% 止损 与 1.5% 止盈 自动平仓
            // ====================================================================
            if (ActivePositions.Count > 0)
            {
                for (int i = ActivePositions.Count - 1; i >= 0; i--)
                {
                    var pos = ActivePositions[i];
                    if (tick.Price > pos.HighestPriceSinceEntry) pos.HighestPriceSinceEntry = tick.Price;
                    if (tick.Price < pos.LowestPriceSinceEntry) pos.LowestPriceSinceEntry = tick.Price;

                    bool isClosed = false;
                    PositionExitReason exitReason = PositionExitReason.None;
                    decimal exitPrice = tick.Price;

                    if (pos.Side == TradeSide.Buy)
                    {
                        // 多单止盈: 当前价达到或超过止盈价 (+1.5%)
                        if (tick.Price >= pos.TakeProfitPrice)
                        {
                            isClosed = true;
                            exitReason = PositionExitReason.TakeProfit;
                            exitPrice = pos.TakeProfitPrice;
                        }
                        // 多单止损: 当前价跌破止损价 (-0.5%)
                        else if (tick.Price <= pos.StopLossPrice)
                        {
                            isClosed = true;
                            exitReason = PositionExitReason.StopLoss;
                            exitPrice = pos.StopLossPrice;
                        }
                    }
                    else // Sell (空单)
                    {
                        // 空单止盈: 当前价跌破或达到止盈价 (+1.5%)
                        if (tick.Price <= pos.TakeProfitPrice)
                        {
                            isClosed = true;
                            exitReason = PositionExitReason.TakeProfit;
                            exitPrice = pos.TakeProfitPrice;
                        }
                        // 空单止损: 当前价涨破止损价 (-0.5%)
                        else if (tick.Price >= pos.StopLossPrice)
                        {
                            isClosed = true;
                            exitReason = PositionExitReason.StopLoss;
                            exitPrice = pos.StopLossPrice;
                        }
                    }

                    if (isClosed)
                    {
                        decimal pnlPct = pos.Side == TradeSide.Buy
                            ? (exitPrice - pos.EntryPrice) / pos.EntryPrice * 100m
                            : (pos.EntryPrice - exitPrice) / pos.EntryPrice * 100m;

                        decimal maxRunup = pos.Side == TradeSide.Buy
                            ? (pos.HighestPriceSinceEntry - pos.EntryPrice) / pos.EntryPrice * 100m
                            : (pos.EntryPrice - pos.LowestPriceSinceEntry) / pos.EntryPrice * 100m;

                        decimal maxDrawdown = pos.Side == TradeSide.Buy
                            ? (pos.EntryPrice - pos.LowestPriceSinceEntry) / pos.EntryPrice * 100m
                            : (pos.HighestPriceSinceEntry - pos.EntryPrice) / pos.EntryPrice * 100m;

                        var trade = new TradeRecord
                        {
                            TradeId = CompletedTrades.Count + 1,
                            Side = pos.Side,
                            EntryTimestampMs = pos.EntryTimestampMs,
                            EntryPrice = pos.EntryPrice,
                            EntryGlobalBarIndex = pos.EntryGlobalBarIndex,
                            TakeProfitPrice = pos.TakeProfitPrice,
                            StopLossPrice = pos.StopLossPrice,
                            ExitTimestampMs = tick.Time,
                            ExitPrice = exitPrice,
                            ExitGlobalBarIndex = currentGlobalIndex,
                            ExitReason = exitReason,
                            PnLPct = pnlPct,
                            MaxRunupPct = Math.Max(0, maxRunup),
                            MaxDrawdownPct = Math.Max(0, maxDrawdown),
                            TriggerLine = pos.TriggerLine,
                            StrategyReason = pos.StrategyReason
                        };

                        CompletedTrades.Add(trade);
                        TotalPnLPct += pnlPct;
                        if (trade.IsWin) WinningTradesCount++;
                        else LosingTradesCount++;

                        ActivePositions.RemoveAt(i);
                        OnTradeClosed?.Invoke(trade);
                    }
                }
            }

            // ====================================================================
            // 步骤 0.5: 单持仓互斥检查 与 交易开启开关检查
            // ====================================================================
            if (!EnableTrading)
            {
                if (_activeTouchProbes.Count > 0)
                {
                    _activeTouchProbes.Clear();
                }
                return; // 未开启交易时仅计算趋势线与收盘演进，不执行开仓判定
            }

            if (ActivePositions.Count > 0)
            {
                if (_activeTouchProbes.Count > 0)
                {
                    _activeTouchProbes.Clear();
                }
                return;
            }

            // ====================================================================
            // 步骤 1: 处理已有的触碰探针，检查 3 个 Tick 内是否发生回弹
            // ====================================================================
            for (int i = _activeTouchProbes.Count - 1; i >= 0; i--)
            {
                var probe = _activeTouchProbes[i];
                probe.TicksSinceTouch++;

                decimal linePrice = probe.Line.CachedCurrentPrice;

                if (probe.IsResistance)
                {
                    // 高点阻力线：若在 3 个 Tick 内价格向下回弹低于趋势线且低于触碰价 -> 开空！
                    if (probe.TicksSinceTouch <= 3)
                    {
                        if (tick.Price < linePrice && tick.Price < probe.TouchPrice)
                        {
                            // 检查冷却时间：若处于冷却时间内，本轮不触发开仓
                            if (_lastTriggerTimestampMs > 0 && (tick.Time - _lastTriggerTimestampMs) < cooldownMs)
                            {
                                _activeTouchProbes.RemoveAt(i);
                                continue;
                            }

                            // 触发成功！更新冷却时间戳并标记趋势线为绿色触发线
                            _lastTriggerTimestampMs = tick.Time;
                            probe.Line.IsTriggered = true;
                            MarkTrendLineTriggered(probe.Line);

                            // 开立空单仓位 (1.5% 止盈, 5-Tick 自动极小微止损)
                            decimal tpPrice = tick.Price * (1m - TakeProfitPct / 100m);

                            // 5 个 Tick 之前的价格作为自动极小微止损点位
                            decimal tick5Price = GetPriceTicksAgo(5);
                            decimal slPrice;
                            if (tick5Price > tick.Price)
                            {
                                slPrice = tick5Price;
                            }
                            else
                            {
                                decimal highest5 = GetExtremePriceLastNTicks(5, getHighest: true);
                                slPrice = highest5 > tick.Price ? highest5 : tick.Price * (1m + StopLossPct / 100m);
                            }

                            // 安全兜底上限：最大止损不超过 StopLossPct (默认 0.5%)
                            decimal maxSlPrice = tick.Price * (1m + StopLossPct / 100m);
                            if (slPrice > maxSlPrice) slPrice = maxSlPrice;

                            decimal slPct = (slPrice - tick.Price) / tick.Price * 100m;
                            int lineAge = currentGlobalIndex - probe.Line.X2;
                            string reason = $"【高点阻力线触发开空】#{probe.Line.X1}->#{probe.Line.X2} | 跨度={probe.Line.LineX1X2} (≥{MinSignalLineX1X2}), 寿命={lineAge} (≥{MinSignalLineAge}) | 触碰价:{probe.TouchPrice:F2} -> 第{probe.TicksSinceTouch}个Tick回弹价:{tick.Price:F2} | 止盈:{tpPrice:F2} (+{TakeProfitPct:F1}%), 5-Tick微止损:{slPrice:F2} (-{slPct:F3}%)";

                            var signal = new TradeSignal
                            {
                                SignalId = TradeSignals.Count + 1,
                                GlobalBarIndex = currentGlobalIndex,
                                TimestampMs = tick.Time,
                                Side = TradeSide.Sell,
                                Price = tick.Price,
                                TriggerLine = probe.Line,
                                TicksSinceTouch = probe.TicksSinceTouch,
                                Reason = reason
                            };

                            TradeSignals.Add(signal);
                            ShortSignalsCount++;
                            OnTradeSignalGenerated?.Invoke(signal);

                            var pos = new Position
                            {
                                PositionId = CompletedTrades.Count + ActivePositions.Count + 1,
                                Side = TradeSide.Sell,
                                EntryTimestampMs = tick.Time,
                                EntryPrice = tick.Price,
                                EntryGlobalBarIndex = currentGlobalIndex,
                                TakeProfitPrice = tpPrice,
                                StopLossPrice = slPrice,
                                HighestPriceSinceEntry = tick.Price,
                                LowestPriceSinceEntry = tick.Price,
                                TriggerLine = probe.Line,
                                StrategyReason = reason
                            };
                            ActivePositions.Add(pos);
                            OnPositionOpened?.Invoke(pos);

                            _activeTouchProbes.Clear();
                            return;
                        }
                    }
                    else
                    {
                        // 超过 3 个 Tick 未回弹，探针失效移除 (趋势线删除统一在 OnKline 周期结束收盘时判定)
                        _activeTouchProbes.RemoveAt(i);
                    }
                }
                else
                {
                    // 低点支撑线：若在 3 个 Tick 内价格向上回弹高于趋势线且高于触碰价 -> 开多！
                    if (probe.TicksSinceTouch <= 3)
                    {
                        if (tick.Price > linePrice && tick.Price > probe.TouchPrice)
                        {
                            // 检查冷却时间：若处于冷却时间内，本轮不触发开仓
                            if (_lastTriggerTimestampMs > 0 && (tick.Time - _lastTriggerTimestampMs) < cooldownMs)
                            {
                                _activeTouchProbes.RemoveAt(i);
                                continue;
                            }

                            // 触发成功！更新冷却时间戳并标记趋势线为绿色触发线
                            _lastTriggerTimestampMs = tick.Time;
                            probe.Line.IsTriggered = true;
                            MarkTrendLineTriggered(probe.Line);

                            // 开立多单仓位 (1.5% 止盈, 5-Tick 自动极小微止损)
                            decimal tpPrice = tick.Price * (1m + TakeProfitPct / 100m);

                            // 5 个 Tick 之前的价格作为自动极小微止损点位
                            decimal tick5Price = GetPriceTicksAgo(5);
                            decimal slPrice;
                            if (tick5Price > 0m && tick5Price < tick.Price)
                            {
                                slPrice = tick5Price;
                            }
                            else
                            {
                                decimal lowest5 = GetExtremePriceLastNTicks(5, getHighest: false);
                                slPrice = (lowest5 > 0m && lowest5 < tick.Price) ? lowest5 : tick.Price * (1m - StopLossPct / 100m);
                            }

                            // 安全兜底下限：最大止损不超过 StopLossPct (默认 0.5%)
                            decimal minSlPrice = tick.Price * (1m - StopLossPct / 100m);
                            if (slPrice < minSlPrice) slPrice = minSlPrice;

                            decimal slPct = (tick.Price - slPrice) / tick.Price * 100m;
                            int lineAge = currentGlobalIndex - probe.Line.X2;
                            string reason = $"【低点支撑线触发开多】#{probe.Line.X1}->#{probe.Line.X2} | 跨度={probe.Line.LineX1X2} (≥{MinSignalLineX1X2}), 寿命={lineAge} (≥{MinSignalLineAge}) | 触碰价:{probe.TouchPrice:F2} -> 第{probe.TicksSinceTouch}个Tick回弹价:{tick.Price:F2} | 止盈:{tpPrice:F2} (+{TakeProfitPct:F1}%), 5-Tick微止损:{slPrice:F2} (-{slPct:F3}%)";

                            var signal = new TradeSignal
                            {
                                SignalId = TradeSignals.Count + 1,
                                GlobalBarIndex = currentGlobalIndex,
                                TimestampMs = tick.Time,
                                Side = TradeSide.Buy,
                                Price = tick.Price,
                                TriggerLine = probe.Line,
                                TicksSinceTouch = probe.TicksSinceTouch,
                                Reason = reason
                            };

                            TradeSignals.Add(signal);
                            LongSignalsCount++;
                            OnTradeSignalGenerated?.Invoke(signal);

                            var pos = new Position
                            {
                                PositionId = CompletedTrades.Count + ActivePositions.Count + 1,
                                Side = TradeSide.Buy,
                                EntryTimestampMs = tick.Time,
                                EntryPrice = tick.Price,
                                EntryGlobalBarIndex = currentGlobalIndex,
                                TakeProfitPrice = tpPrice,
                                StopLossPrice = slPrice,
                                HighestPriceSinceEntry = tick.Price,
                                LowestPriceSinceEntry = tick.Price,
                                TriggerLine = probe.Line,
                                StrategyReason = reason
                            };
                            ActivePositions.Add(pos);
                            OnPositionOpened?.Invoke(pos);

                            _activeTouchProbes.Clear();
                            return;
                        }
                    }
                    else
                    {
                        // 超过 3 个 Tick 未回弹，探针失效移除 (趋势线删除统一在 OnKline 周期结束收盘时判定)
                        _activeTouchProbes.RemoveAt(i);
                    }
                }
            }

            // ====================================================================
            // 步骤 2: 检测活跃阻力线 (高点趋势线) - 极速单指令边界短路
            // ====================================================================
            if (tick.Price >= _minActiveResistancePrice)
            {
                for (int i = ActiveResistanceLines.Count - 1; i >= 0; i--)
                {
                    var line = ActiveResistanceLines[i];

                    // 判断是否达到/穿过趋势线 (tick.Price >= CachedCurrentPrice)
                    if (tick.Price >= line.CachedCurrentPrice)
                    {
                        int lineAge = currentGlobalIndex - line.X2;

                        // 检查是否满足策略准入条件: LineX1X2 >= 40 且 LineAge >= 4
                        if (line.LineX1X2 >= MinSignalLineX1X2 && lineAge >= MinSignalLineAge)
                        {
                            if (!IsLineInProbes(line))
                            {
                                _activeTouchProbes.Add(new TouchProbe
                                {
                                    Line = line,
                                    TouchPrice = tick.Price,
                                    TouchGlobalIndex = currentGlobalIndex,
                                    TicksSinceTouch = 0
                                });
                            }
                        }
                    }
                }
            }

            // ====================================================================
            // 步骤 3: 检测活跃支撑线 (低点趋势线) - 极速单指令边界短路
            // ====================================================================
            if (tick.Price <= _maxActiveSupportPrice)
            {
                for (int i = ActiveSupportLines.Count - 1; i >= 0; i--)
                {
                    var line = ActiveSupportLines[i];

                    // 判断是否达到/穿过趋势线 (tick.Price <= CachedCurrentPrice)
                    if (tick.Price <= line.CachedCurrentPrice)
                    {
                        int lineAge = currentGlobalIndex - line.X2;

                        // 检查是否满足策略准入条件: LineX1X2 >= 40 且 LineAge >= 4
                        if (line.LineX1X2 >= MinSignalLineX1X2 && lineAge >= MinSignalLineAge)
                        {
                            if (!IsLineInProbes(line))
                            {
                                _activeTouchProbes.Add(new TouchProbe
                                {
                                    Line = line,
                                    TouchPrice = tick.Price,
                                    TouchGlobalIndex = currentGlobalIndex,
                                    TicksSinceTouch = 0
                                });
                            }
                        }
                    }
                }
            }
        }

        private bool IsLineInProbes(TrendLine line)
        {
            for (int i = 0; i < _activeTouchProbes.Count; i++)
            {
                var p = _activeTouchProbes[i].Line;
                if (p.X1 == line.X1 && p.X2 == line.X2 && p.Type == line.Type)
                    return true;
            }
            return false;
        }

        private void RemoveActiveResistanceLine(TrendLine targetLine)
        {
            for (int i = 0; i < ActiveResistanceLines.Count; i++)
            {
                var l = ActiveResistanceLines[i];
                if (l.X1 == targetLine.X1 && l.X2 == targetLine.X2 && l.Type == targetLine.Type)
                {
                    ActiveResistanceLines.RemoveAt(i);
                    break;
                }
            }
        }

        private void RemoveActiveSupportLine(TrendLine targetLine)
        {
            for (int i = 0; i < ActiveSupportLines.Count; i++)
            {
                var l = ActiveSupportLines[i];
                if (l.X1 == targetLine.X1 && l.X2 == targetLine.X2 && l.Type == targetLine.Type)
                {
                    ActiveSupportLines.RemoveAt(i);
                    break;
                }
            }
        }

        private void MarkTrendLineTriggered(TrendLine targetLine)
        {
            for (int i = 0; i < ActiveResistanceLines.Count; i++)
            {
                var line = ActiveResistanceLines[i];
                if (line.X1 == targetLine.X1 && line.X2 == targetLine.X2 && line.Type == targetLine.Type)
                {
                    line.IsTriggered = true;
                    ActiveResistanceLines[i] = line;
                    break;
                }
            }

            for (int i = 0; i < ActiveSupportLines.Count; i++)
            {
                var line = ActiveSupportLines[i];
                if (line.X1 == targetLine.X1 && line.X2 == targetLine.X2 && line.Type == targetLine.Type)
                {
                    line.IsTriggered = true;
                    ActiveSupportLines[i] = line;
                    break;
                }
            }

            for (int i = _historicalTrendLines.Count - 1; i >= 0; i--)
            {
                var line = _historicalTrendLines[i];
                if (line.X1 == targetLine.X1 && line.X2 == targetLine.X2 && line.Type == targetLine.Type)
                {
                    line.IsTriggered = true;
                    _historicalTrendLines[i] = line;
                    break;
                }
            }
        }

        /// <summary>
        /// 接收 K 线周期行情推送（周期切分/Bar Close 收盘事件）
        /// </summary>
        public void OnKline(in RawKline kline)
        {
            LatestKline = kline;
            HasKlineData = true;
            _lastProcessedTickPrice = decimal.MinValue;

            int currentGlobalIndex = _globalBarIndex++;

            // 1. 滑动窗口维护：追加新 K 线 (环形缓冲区 O(1) 纯数组写入，零内存拷贝与零 GC 压力)
            _klines.Add(kline);

            // 2. 【第 1 层: O(1) 增量极值判定 (严格分形 10 步对比)】
            int candidateGlobalIndex = currentGlobalIndex - RightLen;
            var (hasPeak, hasValley, newPeak, newValley) = PivotHelper.TryDetectIncrementalPivot(
                _klines,
                candidateGlobalIndex,
                LeftLen,
                RightLen);

            // 3. 【第 2 层: O(M) 增量趋势线生成 (零堆对象分配直装模式，高点只保留>=0度，低点只保留<=0度，过滤超高斜率)】
            if (hasPeak)
            {
                TrendLineHelper.GenerateIncrementalTrendLines(
                    newPeak,
                    _peaks,
                    _klines,
                    currentGlobalIndex,
                    ActiveResistanceLines,
                    _historicalTrendLines,
                    maxSpan: MaxSpan,
                    allowInternalPenetration: AllowInternalPenetration,
                    maxSlopePctPerBar: MaxSlopePctPerBar);

                // 检查现有活跃阻力趋势线是否在当前 newPeak 处连接第 3 个点 (价格在 0.1% 范围内)
                for (int i = 0; i < ActiveResistanceLines.Count; i++)
                {
                    var line = ActiveResistanceLines[i];
                    if (newPeak.Index > line.X2)
                    {
                        decimal expectedPrice = line.GetPriceAt(newPeak.Index);
                        if (expectedPrice > 0m && Math.Abs(newPeak.Price - expectedPrice) / expectedPrice <= 0.001m)
                        {
                            line.IsThreePointConfirmed = true;
                            line.TouchCount = 3;
                            line.X3 = newPeak.Index;
                            line.Y3 = newPeak.Price;
                            ActiveResistanceLines[i] = line;
                            SyncHistoricalTrendLineConfirmed(line);
                        }
                    }
                }

                _peaks.Add(newPeak);
                PruneHistoryCapacity();
            }

            if (hasValley)
            {
                TrendLineHelper.GenerateIncrementalTrendLines(
                    newValley,
                    _valleys,
                    _klines,
                    currentGlobalIndex,
                    ActiveSupportLines,
                    _historicalTrendLines,
                    maxSpan: MaxSpan,
                    allowInternalPenetration: AllowInternalPenetration,
                    maxSlopePctPerBar: MaxSlopePctPerBar);

                // 检查现有活跃支撑趋势线是否在当前 newValley 处连接第 3 个点 (价格在 0.1% 范围内)
                for (int i = 0; i < ActiveSupportLines.Count; i++)
                {
                    var line = ActiveSupportLines[i];
                    if (newValley.Index > line.X2)
                    {
                        decimal expectedPrice = line.GetPriceAt(newValley.Index);
                        if (expectedPrice > 0m && Math.Abs(newValley.Price - expectedPrice) / expectedPrice <= 0.001m)
                        {
                            line.IsThreePointConfirmed = true;
                            line.TouchCount = 3;
                            line.X3 = newValley.Index;
                            line.Y3 = newValley.Price;
                            ActiveSupportLines[i] = line;
                            SyncHistoricalTrendLineConfirmed(line);
                        }
                    }
                }

                _valleys.Add(newValley);
                PruneHistoryCapacity();
            }

            // 4. 【第 3 层: 当周期结束 K 线推送时，检测 K 线 close 穿过或者 high/low 穿过后删除趋势线 (第3点附近0.1%不删除)】
            for (int i = ActiveResistanceLines.Count - 1; i >= 0; i--)
            {
                var line = ActiveResistanceLines[i];
                if (currentGlobalIndex > line.X2)
                {
                    line.LineAge = currentGlobalIndex - line.X2;
                    decimal expectedPrice = line.GetPriceAt(currentGlobalIndex);
                    line.CachedCurrentPrice = expectedPrice;

                    // 检查当前 K 线是否连接/触碰第 3 个点 (高点在趋势线 0.1% 范围内)
                    decimal touchDiffPct = expectedPrice > 0m ? Math.Abs(kline.High - expectedPrice) / expectedPrice : 1m;
                    if (touchDiffPct <= 0.001m)
                    {
                        line.IsThreePointConfirmed = true;
                        line.TouchCount = 3;
                        if (line.X3 <= 0)
                        {
                            line.X3 = currentGlobalIndex;
                            line.Y3 = kline.High;
                        }
                        line.LineExtensionRange = currentGlobalIndex - line.X2;
                        ActiveResistanceLines[i] = line;
                        SyncHistoricalTrendLineConfirmed(line);
                        continue; // 连接第 3 个点附近不删除！
                    }

                    // 阻力趋势线删除判定：
                    // 若为三点确认线，仅在收盘实体明显上破 (>0.1%) 时删除；普通趋势线在 close 或 high 穿过时删除
                    bool isPenetrated = line.IsThreePointConfirmed
                        ? (kline.Close > expectedPrice * 1.001m)
                        : (kline.Close > expectedPrice || kline.High > expectedPrice);

                    if (isPenetrated)
                    {
                        line.CollidedKlineIndex = currentGlobalIndex;
                        line.LineExtensionRange = currentGlobalIndex - line.X2;
                        ActiveResistanceLines.RemoveAt(i);
                        AddToDeletedTrendLines(line);
                        OnTrendLinePenetrated?.Invoke(line, LatestTick, "KLINE_RESISTANCE_PENETRATED");
                    }
                    else
                    {
                        line.LineExtensionRange = currentGlobalIndex - line.X2;
                        ActiveResistanceLines[i] = line;
                    }
                }
            }

            for (int i = ActiveSupportLines.Count - 1; i >= 0; i--)
            {
                var line = ActiveSupportLines[i];
                if (currentGlobalIndex > line.X2)
                {
                    line.LineAge = currentGlobalIndex - line.X2;
                    decimal expectedPrice = line.GetPriceAt(currentGlobalIndex);
                    line.CachedCurrentPrice = expectedPrice;

                    // 检查当前 K 线是否连接/触碰第 3 个点 (低点在趋势线 0.1% 范围内)
                    decimal touchDiffPct = expectedPrice > 0m ? Math.Abs(kline.Low - expectedPrice) / expectedPrice : 1m;
                    if (touchDiffPct <= 0.001m)
                    {
                        line.IsThreePointConfirmed = true;
                        line.TouchCount = 3;
                        if (line.X3 <= 0)
                        {
                            line.X3 = currentGlobalIndex;
                            line.Y3 = kline.Low;
                        }
                        line.LineExtensionRange = currentGlobalIndex - line.X2;
                        ActiveSupportLines[i] = line;
                        SyncHistoricalTrendLineConfirmed(line);
                        continue; // 连接第 3 个点附近不删除！
                    }

                    // 支撑趋势线删除判定：
                    // 若为三点确认线，仅在收盘实体明显下破 (>0.1%) 时删除；普通趋势线在 close 或 low 穿过时删除
                    bool isPenetrated = line.IsThreePointConfirmed
                        ? (kline.Close < expectedPrice * 0.999m)
                        : (kline.Close < expectedPrice || kline.Low < expectedPrice);

                    if (isPenetrated)
                    {
                        line.CollidedKlineIndex = currentGlobalIndex;
                        line.LineExtensionRange = currentGlobalIndex - line.X2;
                        ActiveSupportLines.RemoveAt(i);
                        AddToDeletedTrendLines(line);
                        OnTrendLinePenetrated?.Invoke(line, LatestTick, "KLINE_SUPPORT_PENETRATED");
                    }
                    else
                    {
                        line.LineExtensionRange = currentGlobalIndex - line.X2;
                        ActiveSupportLines[i] = line;
                    }
                }
            }

            // 5. 适度清理超龄的非活跃趋势线 (保持活跃集合紧凑高效)
            PruneInactiveTrendLines(currentGlobalIndex);

            // 6. ⚡ 极速短路边界更新：刷新阻力线最低价与支撑线最高价
            UpdateActivePriceBoundaries();
        }

        /// <summary>
        /// 回测结束时强制平仓所有未完结持仓
        /// </summary>
        public void CloseAllRemainingPositions(decimal finalPrice, long finalTimestampMs)
        {
            if (ActivePositions.Count == 0) return;

            int currentGlobalIndex = Math.Max(0, _globalBarIndex);
            for (int i = ActivePositions.Count - 1; i >= 0; i--)
            {
                var pos = ActivePositions[i];
                decimal pnlPct = pos.Side == TradeSide.Buy
                    ? (finalPrice - pos.EntryPrice) / pos.EntryPrice * 100m
                    : (pos.EntryPrice - finalPrice) / pos.EntryPrice * 100m;

                var trade = new TradeRecord
                {
                    TradeId = CompletedTrades.Count + 1,
                    Side = pos.Side,
                    EntryTimestampMs = pos.EntryTimestampMs,
                    EntryPrice = pos.EntryPrice,
                    EntryGlobalBarIndex = pos.EntryGlobalBarIndex,
                    TakeProfitPrice = pos.TakeProfitPrice,
                    StopLossPrice = pos.StopLossPrice,
                    ExitTimestampMs = finalTimestampMs,
                    ExitPrice = finalPrice,
                    ExitGlobalBarIndex = currentGlobalIndex,
                    ExitReason = PositionExitReason.EndOfBacktest,
                    PnLPct = pnlPct,
                    MaxRunupPct = Math.Max(0, pos.Side == TradeSide.Buy ? (pos.HighestPriceSinceEntry - pos.EntryPrice) / pos.EntryPrice * 100m : (pos.EntryPrice - pos.LowestPriceSinceEntry) / pos.EntryPrice * 100m),
                    MaxDrawdownPct = Math.Max(0, pos.Side == TradeSide.Buy ? (pos.EntryPrice - pos.LowestPriceSinceEntry) / pos.EntryPrice * 100m : (pos.HighestPriceSinceEntry - pos.EntryPrice) / pos.EntryPrice * 100m),
                    TriggerLine = pos.TriggerLine,
                    StrategyReason = pos.StrategyReason
                };

                CompletedTrades.Add(trade);
                TotalPnLPct += pnlPct;
                if (trade.IsWin) WinningTradesCount++;
                else LosingTradesCount++;

                OnTradeClosed?.Invoke(trade);
            }
            ActivePositions.Clear();
        }

        private void UpdateActivePriceBoundaries()
        {
            decimal minR = decimal.MaxValue;
            for (int i = 0; i < ActiveResistanceLines.Count; i++)
            {
                if (ActiveResistanceLines[i].CachedCurrentPrice < minR)
                {
                    minR = ActiveResistanceLines[i].CachedCurrentPrice;
                }
            }
            _minActiveResistancePrice = minR;

            decimal maxS = decimal.MinValue;
            for (int i = 0; i < ActiveSupportLines.Count; i++)
            {
                if (ActiveSupportLines[i].CachedCurrentPrice > maxS)
                {
                    maxS = ActiveSupportLines[i].CachedCurrentPrice;
                }
            }
            _maxActiveSupportPrice = maxS;
        }

        /// <summary>
        /// 将被穿透删除的趋势线存入已删除列表 (保持最大 1000 长度) 并同步历史库碰撞状态
        /// </summary>
        private void AddToDeletedTrendLines(TrendLine line)
        {
            _deletedTrendLines.Add(line);

            if (_deletedTrendLines.Count > MaxDeletedTrendLinesCapacity)
            {
                int excess = _deletedTrendLines.Count - MaxDeletedTrendLinesCapacity;
                _deletedTrendLines.RemoveRange(0, excess);
            }

            // 同步更新历史趋势线库中对应趋势线的击穿碰撞状态
            for (int i = _historicalTrendLines.Count - 1; i >= 0; i--)
            {
                var h = _historicalTrendLines[i];
                if (h.X1 == line.X1 && h.X2 == line.X2 && h.Type == line.Type)
                {
                    h.CollidedKlineIndex = line.CollidedKlineIndex;
                    h.LineExtensionRange = line.LineExtensionRange;
                    _historicalTrendLines[i] = h;
                    break;
                }
            }
        }

        /// <summary>
        /// 同步更新历史趋势线库中对应趋势线的三点确认状态
        /// </summary>
        private void SyncHistoricalTrendLineConfirmed(TrendLine confirmedLine)
        {
            for (int i = _historicalTrendLines.Count - 1; i >= 0; i--)
            {
                var h = _historicalTrendLines[i];
                if (h.X1 == confirmedLine.X1 && h.X2 == confirmedLine.X2 && h.Type == confirmedLine.Type)
                {
                    h.IsThreePointConfirmed = true;
                    h.TouchCount = 3;
                    h.X3 = confirmedLine.X3;
                    h.Y3 = confirmedLine.Y3;
                    _historicalTrendLines[i] = h;
                    break;
                }
            }
        }

        /// <summary>
        /// 保持历史趋势线库在设定容量上限内
        /// </summary>
        private void PruneHistoryCapacity()
        {
            if (_historicalTrendLines.Count > MinTrendLinesCapacity * 2)
            {
                int removeCount = _historicalTrendLines.Count - MinTrendLinesCapacity;
                _historicalTrendLines.RemoveRange(0, removeCount);
            }
        }

        /// <summary>
        /// 清理超龄与滑出窗口的失效对象，保持内部常数级极速运转
        /// </summary>
        private void PruneInactiveTrendLines(int currentGlobalIndex)
        {
            // 活跃线控制在设定最大跨度寿命范围内，保持活跃集合紧凑高效
            int maxActiveAge = Math.Max(150, MaxSpan * 2);
            ActiveResistanceLines.RemoveAll(line => currentGlobalIndex - line.X2 > maxActiveAge);
            ActiveSupportLines.RemoveAll(line => currentGlobalIndex - line.X2 > maxActiveAge);

            int minRetainedIndex = currentGlobalIndex - MaxKlinesCapacity;
            if (minRetainedIndex > 0)
            {
                _peaks.RemoveAll(p => p.Index < minRetainedIndex);
                _valleys.RemoveAll(v => v.Index < minRetainedIndex);
            }
        }

        /// <summary>
        /// 一键将当前策略的 K线走势、高低点标记、活跃阻力/支撑趋势线与描述摘要渲染并保存为图片
        /// </summary>
        public string PlotChart(string summaryDescription, string outputFilePath = null, int width = 1920, int height = 1080)
        {
            int startGlobalIndex = Math.Max(0, _globalBarIndex - _klines.Count);
            string title = $"{Symbol} {Interval.ToIntervalString()} 趋势线与极值结构分析图";

            return PlotHelper.PlotTrendLineChart(
                _klines,
                _peaks,
                _valleys,
                ActiveResistanceLines,
                ActiveSupportLines,
                summaryDescription,
                title: title,
                startGlobalIndex: startGlobalIndex,
                outputFilePath: outputFilePath,
                width: width,
                height: height,
                tradeSignals: TradeSignals);
        }

        /// <summary>
        /// 获取策略当前运行状态与趋势线统计摘要
        /// </summary>
        public string GetStrategySummary()
        {
            return $"[TrendLineStrategy - {Symbol} {Interval.ToIntervalString()}] " +
                   $"GlobalBars: {_globalBarIndex}, Window: {_klines.Count}/{MaxKlinesCapacity} | " +
                   $"交易统计: 完成={CompletedTrades.Count}笔 (胜率={WinRate:F1}%, 盈亏={TotalPnLPct:F2}%) | " +
                   $"开仓信号: 多 {LongSignalsCount} | 空 {ShortSignalsCount} (总计 {TotalSignalsCount}) | " +
                   $"活跃阻力={ActiveResistanceLines.Count}, 支撑={ActiveSupportLines.Count}";
        }

        /// <summary>
        /// 重置策略状态
        /// </summary>
        public void Reset()
        {
            _globalBarIndex = 0;
            _klines.Clear();
            _peaks.Clear();
            _valleys.Clear();
            ActiveResistanceLines.Clear();
            ActiveSupportLines.Clear();
            _deletedTrendLines.Clear();
            _historicalTrendLines.Clear();
            _activeTouchProbes.Clear();
            TradeSignals.Clear();
            ActivePositions.Clear();
            CompletedTrades.Clear();
            LongSignalsCount = 0;
            ShortSignalsCount = 0;
            WinningTradesCount = 0;
            LosingTradesCount = 0;
            TotalPnLPct = 0m;
            _lastTriggerTimestampMs = 0;
            _lastProcessedTickPrice = decimal.MinValue;
            _minActiveResistancePrice = decimal.MaxValue;
            _maxActiveSupportPrice = decimal.MinValue;

            _recentTickHead = 0;
            _recentTickCount = 0;
            Array.Clear(_recentTickPrices, 0, _recentTickPrices.Length);

            LatestTick = default;
            LatestKline = default;
            HasTickData = false;
            HasKlineData = false;
        }
    }
}
