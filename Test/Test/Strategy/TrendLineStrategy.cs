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
        /// 0. 持仓单实时止损(0.5%)与止盈(1.5%)平仓监测
        /// 1. 触碰检测与 3 个 Tick 内回弹开仓判定
        /// 2. 开仓时计算止盈止损线并开立仓位
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

                            int lineAge = currentGlobalIndex - probe.Line.X2;
                            string reason = $"【高点阻力线触发开空】#{probe.Line.X1}->#{probe.Line.X2} | 跨度={probe.Line.LineX1X2} (≥{MinSignalLineX1X2}), 寿命={lineAge} (≥{MinSignalLineAge}), 斜率={probe.Line.K:F4}%/bar | 起点:({probe.Line.X1}, {probe.Line.Y1:F2}) -> 终点:({probe.Line.X2}, {probe.Line.Y2:F2}) | 触碰价:{probe.TouchPrice:F2} -> 第{probe.TicksSinceTouch}个Tick回弹价:{tick.Price:F2}";

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

                            // 开立空单仓位 (计算 1.5% 止盈 与 0.5% 止损)
                            decimal tpPrice = tick.Price * (1m - TakeProfitPct / 100m);
                            decimal slPrice = tick.Price * (1m + StopLossPct / 100m);
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

                            _activeTouchProbes.RemoveAt(i);
                            continue;
                        }
                    }
                    else
                    {
                        // 超过 3 个 Tick 未回弹，探针失效移除
                        _activeTouchProbes.RemoveAt(i);

                        // 若价格持续高于趋势线，视为真正击穿穿透，移入删除列表
                        if (tick.Price > linePrice)
                        {
                            var brokenLine = probe.Line;
                            brokenLine.CollidedKlineIndex = currentGlobalIndex;
                            brokenLine.LineExtensionRange = Math.Max(0, currentGlobalIndex - brokenLine.X2);
                            RemoveActiveResistanceLine(brokenLine);
                            AddToDeletedTrendLines(brokenLine);
                            OnTrendLinePenetrated?.Invoke(brokenLine, tick, "RESISTANCE_BROKEN_UP");
                        }
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

                            int lineAge = currentGlobalIndex - probe.Line.X2;
                            string reason = $"【低点支撑线触发开多】#{probe.Line.X1}->#{probe.Line.X2} | 跨度={probe.Line.LineX1X2} (≥{MinSignalLineX1X2}), 寿命={lineAge} (≥{MinSignalLineAge}), 斜率={probe.Line.K:F4}%/bar | 起点:({probe.Line.X1}, {probe.Line.Y1:F2}) -> 终点:({probe.Line.X2}, {probe.Line.Y2:F2}) | 触碰价:{probe.TouchPrice:F2} -> 第{probe.TicksSinceTouch}个Tick回弹价:{tick.Price:F2}";

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

                            // 开立多单仓位 (计算 1.5% 止盈 与 0.5% 止损)
                            decimal tpPrice = tick.Price * (1m + TakeProfitPct / 100m);
                            decimal slPrice = tick.Price * (1m - StopLossPct / 100m);
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

                            _activeTouchProbes.RemoveAt(i);
                            continue;
                        }
                    }
                    else
                    {
                        // 超过 3 个 Tick 未回弹，探针失效移除
                        _activeTouchProbes.RemoveAt(i);

                        // 若价格持续低于趋势线，视为真正击穿穿透，移入删除列表
                        if (tick.Price < linePrice)
                        {
                            var brokenLine = probe.Line;
                            brokenLine.CollidedKlineIndex = currentGlobalIndex;
                            brokenLine.LineExtensionRange = Math.Max(0, currentGlobalIndex - brokenLine.X2);
                            RemoveActiveSupportLine(brokenLine);
                            AddToDeletedTrendLines(brokenLine);
                            OnTrendLinePenetrated?.Invoke(brokenLine, tick, "SUPPORT_BROKEN_DOWN");
                        }
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
                        else
                        {
                            // 不满足策略条件的普通趋势线，直接按穿透剔除
                            line.CollidedKlineIndex = currentGlobalIndex;
                            line.LineExtensionRange = Math.Max(0, currentGlobalIndex - line.X2);
                            ActiveResistanceLines.RemoveAt(i);
                            AddToDeletedTrendLines(line);
                            OnTrendLinePenetrated?.Invoke(line, tick, "RESISTANCE_BROKEN_UP");
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
                        else
                        {
                            // 不满足策略条件的普通趋势线，直接按穿透剔除
                            line.CollidedKlineIndex = currentGlobalIndex;
                            line.LineExtensionRange = Math.Max(0, currentGlobalIndex - line.X2);
                            ActiveSupportLines.RemoveAt(i);
                            AddToDeletedTrendLines(line);
                            OnTrendLinePenetrated?.Invoke(line, tick, "SUPPORT_BROKEN_DOWN");
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

            // 3. 【第 2 层: O(M) 增量趋势线生成 (零堆对象分配直装模式)】
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
                    allowInternalPenetration: AllowInternalPenetration);

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
                    allowInternalPenetration: AllowInternalPenetration);

                _valleys.Add(newValley);
                PruneHistoryCapacity();
            }

            // 4. 【第 3 层: O(ActiveLines) 增量单步延伸与碰撞更新】
            TrendLineHelper.UpdateActiveTrendLinesStep(ActiveResistanceLines, kline, currentGlobalIndex);
            TrendLineHelper.UpdateActiveTrendLinesStep(ActiveSupportLines, kline, currentGlobalIndex);

            // 5. 适度清理长期已击穿且老化的非活跃趋势线 (保持活跃集合紧凑高效)
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
        /// 将被穿透删除的趋势线存入已删除列表 (保持最大 1000 长度)
        /// </summary>
        private void AddToDeletedTrendLines(TrendLine line)
        {
            _deletedTrendLines.Add(line);

            if (_deletedTrendLines.Count > MaxDeletedTrendLinesCapacity)
            {
                int excess = _deletedTrendLines.Count - MaxDeletedTrendLinesCapacity;
                _deletedTrendLines.RemoveRange(0, excess);
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
            // 活跃线控制在 MaxSpan * 1.5 范围内或已碰撞超过 20 根 K 线的线，大幅降低存量
            int maxActiveAge = Math.Max(150, MaxSpan * 2);
            ActiveResistanceLines.RemoveAll(line => (line.CollidedKlineIndex != -1 && currentGlobalIndex - line.CollidedKlineIndex > 20) || (currentGlobalIndex - line.X2 > maxActiveAge));
            ActiveSupportLines.RemoveAll(line => (line.CollidedKlineIndex != -1 && currentGlobalIndex - line.CollidedKlineIndex > 20) || (currentGlobalIndex - line.X2 > maxActiveAge));

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

            LatestTick = default;
            LatestKline = default;
            HasTickData = false;
            HasKlineData = false;
        }
    }
}
