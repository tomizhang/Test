using Binance.Net.Enums;
using Common;
using Common.Helper;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Test.ChannelPlayback.WinForms.Models;

namespace Test.ChannelPlayback.WinForms.Engine
{
    public enum PlaybackState
    {
        Stopped,
        Playing,
        Paused
    }

    /// <summary>
    /// K 线回放核心引擎 (100% 真实币安行情数据，严格不生成任何模拟/加工仿真数据)
    /// 支持任意指定周期 (1m, 3m, 5m, 15m, 30m, 1h, 2h, 4h, 1d) 真实还原
    /// </summary>
    public class KlinePlaybackEngine : IDisposable
    {
        private readonly List<RawKline> _allKlines = new();
        private System.Windows.Forms.Timer? _timer;
        private volatile PlaybackState _state = PlaybackState.Stopped;
        private int _currentIndex = 0;
        private int _speedIntervalMs = 50; // 默认 50ms 推进一根 K 线

        // 通道配置参数
        public int LeftLength { get; set; } = 100;
        public int RightExtendLength { get; set; } = 100;
        public bool CumulativeMode { get; set; } = false;
        public ChannelCalculationMode CalculationMode { get; set; } = ChannelCalculationMode.ThreePointTrendDirectional;

        // 相对极值通道保留与突破识别配置
        private readonly ChannelRetentionTracker _retentionTracker = new();
        public bool EnableRetainedChannel { get; set; } = true;
        public int RetainedConfirmBars { get; set; } = 3;
        public BreakoutRule BreakoutRule { get; set; } = BreakoutRule.ClosePrice;
        public bool EnableSpecialRetainedStyle { get; set; } = true;
        public int SpecialRetainedMinBars { get; set; } = 100;
        public double SpecialRetainedMinAngle { get; set; } = 35.0;
        public ChannelRetentionTracker RetentionTracker => _retentionTracker;

        // V 形态与倒 V 形态识别配置 (价差 ≥ 5%)
        private readonly VPatternDetector _vPatternDetector = new();
        public bool EnableVPattern { get; set; } = true;
        public decimal VPatternMinPriceDiffPct { get; set; } = 5.0m;
        public bool ShowVPatternLines { get; set; } = true;
        public VPatternDetector VPatternDetector => _vPatternDetector;

        // 连续上涨 / 连续下跌动能形态识别配置 (默认 0.0% 门槛，只要满足连续 5 根同向即刻确立)
        private readonly ConsecutiveTrendDetector _consecutiveTrendDetector = new();
        public bool EnableConsecutiveTrend { get; set; } = true;
        public int ConsecutiveTrendMinBars { get; set; } = 5;
        public decimal ConsecutiveTrendMinPct { get; set; } = 0.0m;
        public bool ShowConsecutiveChannel { get; set; } = true; // 连续走势绿色 0.8f 平行通道
        public ConsecutiveChannelPriceMode ConsecutiveChannelPriceMode { get; set; } = ConsecutiveChannelPriceMode.Close;
        public bool EnableChannelAutoUpdate { get; set; } = true;
        public ChannelUpdateMode ChannelUpdateMode { get; set; } = ChannelUpdateMode.Rolling;
        public bool ShowHistoricalChannels { get; set; } = true;
        public ConsecutiveTrendDetector ConsecutiveTrendDetector => _consecutiveTrendDetector;

        // 连续 5 根 K 线 3 分钟观察期反转做单驱动引擎
        private readonly ReversalOrderEngine _reversalOrderEngine = new();
        public bool EnableReversalOrder { get; set; } = true;
        public int ReversalObservationMinutes { get; set; } = 3;
        public decimal ReversalPLong { get; set; } = CandlestickPatternClassifier.DefaultPLong;
        public decimal ReversalPMedium { get; set; } = CandlestickPatternClassifier.DefaultPMedium;
        public decimal ReversalPShort { get; set; } = CandlestickPatternClassifier.DefaultPShort;
        public decimal ReversalTickPullbackPct { get; set; } = 0.06m;
        public decimal ReversalChannelZonePct { get; set; } = 25.0m;
        public bool ShowReversalYellowLines { get; set; } = true;
        public bool ShowObservationCycles { get; set; } = true;
        public ReversalOrderEngine ReversalOrderEngine => _reversalOrderEngine;

        // 底层 1m 真实 K 线缓存 (用于反转做单 3m 观察期双模自适应回退聚合)
        private readonly List<RawKline> _raw1mKlines = new();
        public IReadOnlyList<RawKline> Raw1mKlines => _raw1mKlines;

        // 状态读取
        public PlaybackState State => _state;
        public int CurrentIndex => _currentIndex;
        public int TotalKlines => _allKlines.Count;
        public IReadOnlyList<RawKline> AllKlines => _allKlines;
        public int SpeedIntervalMs => _speedIntervalMs;

        // 事件委托
        public event Action<RawKline, int, DynamicChannelResult>? OnBarReplayed;
        public event Action<PlaybackState>? OnStateChanged;
        public event Action<int>? OnDataLoaded;
        public event Action<string>? OnLogMessage;
        public event Action<RetainedChannel, int, decimal, bool>? OnBreakoutDetected;
        public event Action<RetainedChannel, int, decimal, bool>? OnExtremeConfirmed;
        public event Action<VPatternItem, int>? OnVPatternDetected;
        public event Action<ConsecutiveTrendItem, int>? OnConsecutiveTrendDetected;
        public event Action<ConsecutiveTrendItem, string>? OnReversalStrategyActivated;
        public event Action<ReversalOrderSignal, string>? OnReversalOrderSignal;
        public event Action<ReversalObservationCycle?, string>? OnObservationCycleUpdated;

        public KlinePlaybackEngine()
        {
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = _speedIntervalMs;
            _timer.Tick += (s, e) => ProcessNextStep();

            _retentionTracker.OnBreakoutDetected += (ch, idx, price, isUp) =>
            {
                OnBreakoutDetected?.Invoke(ch, idx, price, isUp);
                string dir = isUp ? "🚀 向上突破" : "💥 向下跌破";
                string chType = ch.IsDownward ? "下降通道" : "上升通道";
                OnLogMessage?.Invoke($"[通道突破提醒] {chType} 在 K线 #{idx} 发生 {dir}！突破价={price:F2} (超越幅度:{ch.BreakoutPct:+0.00;-0.00}%)");
            };

            _retentionTracker.OnExtremeConfirmed += (ch, idx, price, isPeak) =>
            {
                OnExtremeConfirmed?.Invoke(ch, idx, price, isPeak);
                string extType = isPeak ? "相对高点" : "相对低点";
                string chType = ch.IsDownward ? "下降通道" : "上升通道";
                OnLogMessage?.Invoke($"[极值确认保留] 确认 {extType} #{idx} (${price:F2}) 无碰撞延展达标！已锁定并保留该 {chType} 用于监控后续突破。");
            };

            _vPatternDetector.OnPatternDetected += (p, idx) =>
            {
                OnVPatternDetected?.Invoke(p, idx);
                string pType = p.Type == VPatternType.VBottom ? "🟢 V底形态 (V型反转)" : "🔴 倒V顶形态 (倒V顶反转)";
                string vertexType = p.Type == VPatternType.VBottom ? "谷底" : "峰顶";
                OnLogMessage?.Invoke($"[形态识别] ⚡ 在 K线 #{idx} 确认 {pType}！{vertexType}锚点: #{p.VertexIndex} (${p.VertexPrice:F2}), 价差幅度: {p.PriceDiffPct:F2}% (左:{p.LeftSpanPct:F1}%, 右:{p.RightSpanPct:F1}%)");
            };

            _consecutiveTrendDetector.OnTrendDetected += (t, idx) =>
            {
                OnConsecutiveTrendDetected?.Invoke(t, idx);
                string sign = t.PriceChangePct >= 0m ? "+" : "";
                string icon = t.Type == ConsecutiveTrendType.Bullish ? "🔥 连续上涨形态" : "❄️ 连续下跌形态";
                OnLogMessage?.Invoke($"[连涨连跌] ⚡ 在 K线 #{idx} 确认 {icon}！跨度: {t.BarCount} 根 (#{t.StartIndex}..#{t.EndIndex}), 累计涨跌幅: {sign}{t.PriceChangePct:F2}% (基准价:{t.StartPrice:F2} -> 现价:{t.EndPrice:F2})");
            };

            // 注入交易信号判定委托，供通道在突破时检查是否已产生做单信号
            _consecutiveTrendDetector.HasTradingSignalFunc = (trendId, barIdx) => _reversalOrderEngine.HasTradingSignal(trendId, barIdx);

            _consecutiveTrendDetector.OnChannelUpdated += (item, snap, msg) =>
            {
                OnLogMessage?.Invoke(msg);
            };

            _reversalOrderEngine.OnStrategyActivated += (trend, desc) =>
            {
                OnReversalStrategyActivated?.Invoke(trend, desc);
            };

            _reversalOrderEngine.OnReversalOrderSignal += (sig, desc) =>
            {
                OnReversalOrderSignal?.Invoke(sig, desc);
                OnLogMessage?.Invoke(desc);
            };

            _reversalOrderEngine.OnObservationCycleUpdated += (cyc, desc) =>
            {
                OnObservationCycleUpdated?.Invoke(cyc, desc);
            };
        }

        /// <summary>
        /// 加载指定币种、指定日期区间与指定周期的真实 K 线数据
        /// 严格直接读取本地真实 Parquet 文件，若目标周期无预制文件，则直接由底层 1m 真实 K 线按交易所标准聚合
        /// 绝不生成任何伪造仿真数据
        /// </summary>
        public async Task<int> LoadDataAsync(string coin, DateTime startDate, DateTime endDate, KlineInterval interval, CancellationToken ct = default)
        {
            Stop();
            _allKlines.Clear();
            _raw1mKlines.Clear();
            _currentIndex = 0;
            _retentionTracker.Reset();
            _vPatternDetector.Reset();
            _consecutiveTrendDetector.Reset();
            _reversalOrderEngine.Reset();

            if (startDate.Date > endDate.Date)
            {
                OnLogMessage?.Invoke("[错误] 起始日期不能大于结束日期！");
                OnDataLoaded?.Invoke(0);
                return 0;
            }

            string intervalStr = interval.ToIntervalString();
            OnLogMessage?.Invoke($"[真实数据] 开始检索 {coin} ({startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd}, 周期: {intervalStr}) 本地真实行情...");

            var raw1mList = new List<RawKline>();
            var directTargetList = new List<RawKline>();
            bool hasDirectTargetFiles = false;

            // 1. 检查是否存在目标周期的直属 Parquet 文件 (例如 30m)
            for (DateTime d = startDate.Date; d <= endDate.Date; d = d.AddDays(1))
            {
                string targetPath = Config.GetKlineFilePath(coin, interval, d, ".parquet");
                if (File.Exists(targetPath))
                {
                    hasDirectTargetFiles = true;
                    break;
                }
            }

            try
            {
                using var reader = new ParquetDataReader();

                if (hasDirectTargetFiles && interval != KlineInterval.OneMinute)
                {
                    // 直接读取目标周期真实 Parquet
                    for (DateTime d = startDate.Date; d <= endDate.Date; d = d.AddDays(1))
                    {
                        string targetPath = Config.GetKlineFilePath(coin, interval, d, ".parquet");
                        if (File.Exists(targetPath))
                        {
                            await reader.LoadKlineDayAsync(coin, d, interval, ct).ConfigureAwait(false);
                        }
                    }

                    while (reader.TryDequeueKline(out var kline))
                    {
                        directTargetList.Add(kline);
                    }

                    _allKlines.AddRange(directTargetList);
                    OnLogMessage?.Invoke($"[真实数据] 成功直接读取 {coin} {intervalStr} 真实 Parquet 文件，共加载 {_allKlines.Count:N0} 根真实 K 线。");

                    // 为反转做单小周期推演并行检索 1m 真实 K 线 (若本地存在)
                    try
                    {
                        using var reader1m = new ParquetDataReader();
                        for (DateTime d = startDate.Date; d <= endDate.Date; d = d.AddDays(1))
                        {
                            string p1mPath = Config.GetKlineFilePath(coin, KlineInterval.OneMinute, d, ".parquet");
                            if (File.Exists(p1mPath))
                            {
                                await reader1m.LoadKlineDayAsync(coin, d, KlineInterval.OneMinute, ct).ConfigureAwait(false);
                            }
                        }
                        while (reader1m.TryDequeueKline(out var kline1m))
                        {
                            _raw1mKlines.Add(kline1m);
                        }
                    }
                    catch { }
                }
                else
                {
                    // 读取 1m 真实原始文件并按需聚合
                    int foundDays = 0;
                    for (DateTime d = startDate.Date; d <= endDate.Date; d = d.AddDays(1))
                    {
                        string p1mPath = Config.GetKlineFilePath(coin, KlineInterval.OneMinute, d, ".parquet");
                        if (File.Exists(p1mPath))
                        {
                            await reader.LoadKlineDayAsync(coin, d, KlineInterval.OneMinute, ct).ConfigureAwait(false);
                            foundDays++;
                        }
                    }

                    while (reader.TryDequeueKline(out var kline))
                    {
                        raw1mList.Add(kline);
                    }

                    if (raw1mList.Count == 0)
                    {
                        OnLogMessage?.Invoke($"[错误] 未找到 {coin} 在 {startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd} 的真实 Parquet 数据文件！请检查本地数据目录是否有对应数据。");
                        OnDataLoaded?.Invoke(0);
                        return 0;
                    }

                    _raw1mKlines.AddRange(raw1mList);

                    if (interval == KlineInterval.OneMinute)
                    {
                        _allKlines.AddRange(raw1mList);
                        OnLogMessage?.Invoke($"[真实数据] 成功加载 {foundDays} 天 1m 原始行情，共计 {_allKlines.Count:N0} 根真实 K 线 (100% 原始数据，无加工)。");
                    }
                    else
                    {
                        // 严格按交易所标准，将真实 1m K 线合成指定目标周期 K 线 (保证每根高低收均为真实成交)
                        var aggregated = AggregateKlines(raw1mList, interval);
                        _allKlines.AddRange(aggregated);
                        OnLogMessage?.Invoke($"[真实数据] 成功将 {raw1mList.Count:N0} 根 1m 真实行情合成为 {_allKlines.Count:N0} 根 {intervalStr} 真实 K 线 (高低开收均由真实 1m 成交严格换算)。");
                    }
                }
            }
            catch (Exception ex)
            {
                OnLogMessage?.Invoke($"[错误] 读取真实 Parquet 数据发生异常: {ex.Message}");
                OnDataLoaded?.Invoke(0);
                return 0;
            }

            if (_allKlines.Count == 0)
            {
                OnLogMessage?.Invoke($"[错误] 未能成功解析出任何有效真实 K 线数据。");
                OnDataLoaded?.Invoke(0);
                return 0;
            }

            // 初始回放位置：若数据量大于等于左侧计算跨度，默认停在第 LeftLength 根处即刻展示通道
            if (_allKlines.Count >= LeftLength)
            {
                _currentIndex = LeftLength - 1;
            }
            else
            {
                _currentIndex = Math.Max(0, _allKlines.Count - 1);
            }

            OnDataLoaded?.Invoke(_allKlines.Count);

            // 触发首帧渲染
            TriggerCurrentFrame();
            return _allKlines.Count;
        }

        /// <summary>
        /// 严格遵循交易所金融规范，将真实 1m K 线合并为多周期 K 线 (保证高低点与成交量绝对真实无失真)
        /// </summary>
        public static List<RawKline> AggregateKlines(IReadOnlyList<RawKline> raw1m, KlineInterval targetInterval)
        {
            if (raw1m == null || raw1m.Count == 0 || targetInterval == KlineInterval.OneMinute)
            {
                return raw1m != null ? new List<RawKline>(raw1m) : new List<RawKline>();
            }

            long intervalMs = TimeHelper.GetIntervalMilliseconds(targetInterval.ToIntervalString());
            if (intervalMs <= 60000)
            {
                return new List<RawKline>(raw1m);
            }

            var result = new List<RawKline>(raw1m.Count / (int)(intervalMs / 60000) + 10);
            long bucketStart = -1;

            decimal open = 0m;
            decimal high = decimal.MinValue;
            decimal low = decimal.MaxValue;
            decimal close = 0m;
            decimal volume = 0m;
            decimal quoteVolume = 0m;
            long tradeCount = 0;
            decimal takerBuyVol = 0m;
            decimal takerBuyQuoteVol = 0m;

            for (int i = 0; i < raw1m.Count; i++)
            {
                var bar = raw1m[i];
                long currentBucket = (bar.OpenTime / intervalMs) * intervalMs;

                if (bucketStart != -1 && currentBucket != bucketStart)
                {
                    // 封顶上一根合并 Bar
                    result.Add(new RawKline
                    {
                        OpenTime = bucketStart,
                        Open = open,
                        High = high,
                        Low = low,
                        Close = close,
                        Volume = volume,
                        CloseTime = bucketStart + intervalMs - 1,
                        QuoteVolume = quoteVolume,
                        TradeCount = tradeCount,
                        TakerBuyVolume = takerBuyVol,
                        TakerBuyQuoteVolume = takerBuyQuoteVol
                    });

                    bucketStart = -1;
                }

                if (bucketStart == -1)
                {
                    bucketStart = currentBucket;
                    open = bar.Open;
                    high = bar.High;
                    low = bar.Low;
                    close = bar.Close;
                    volume = bar.Volume;
                    quoteVolume = bar.QuoteVolume;
                    tradeCount = bar.TradeCount;
                    takerBuyVol = bar.TakerBuyVolume;
                    takerBuyQuoteVol = bar.TakerBuyQuoteVolume;
                }
                else
                {
                    high = Math.Max(high, bar.High);
                    low = Math.Min(low, bar.Low);
                    close = bar.Close;
                    volume += bar.Volume;
                    quoteVolume += bar.QuoteVolume;
                    tradeCount += bar.TradeCount;
                    takerBuyVol += bar.TakerBuyVolume;
                    takerBuyQuoteVol += bar.TakerBuyQuoteVolume;
                }
            }

            if (bucketStart != -1)
            {
                result.Add(new RawKline
                {
                    OpenTime = bucketStart,
                    Open = open,
                    High = high,
                    Low = low,
                    Close = close,
                    Volume = volume,
                    CloseTime = bucketStart + intervalMs - 1,
                    QuoteVolume = quoteVolume,
                    TradeCount = tradeCount,
                    TakerBuyVolume = takerBuyVol,
                    TakerBuyQuoteVolume = takerBuyQuoteVol
                });
            }

            return result;
        }

        /// <summary>
        /// 将微观逐笔成交 RawTick 序列流式聚合为指定分钟周期的标准 K 线序列
        /// </summary>
        /// <param name="ticks">按时间升序排列的 RawTick 序列</param>
        /// <param name="intervalMinutes">聚合目标分钟周期 (>=1，例如 1m, 5m, 15m, 自定义M分钟)</param>
        /// <param name="fillEmptyBars">若中间出现无成交空白时段，是否自动填充平价0量延续K线以保持时间轴连续</param>
        public static List<RawKline> AggregateTicksToKlines(IReadOnlyList<RawTick> ticks, int intervalMinutes, bool fillEmptyBars = true)
        {
            if (ticks == null || ticks.Count == 0) return new List<RawKline>();
            if (intervalMinutes < 1) intervalMinutes = 1;

            long intervalMs = (long)intervalMinutes * 60_000L;
            var result = new List<RawKline>();

            long minTickTime = ticks[0].Time;
            long maxTickTime = ticks[ticks.Count - 1].Time;

            long startBucket = (minTickTime / intervalMs) * intervalMs;
            long endBucket = (maxTickTime / intervalMs) * intervalMs;

            int tickIdx = 0;
            int totalTicks = ticks.Count;
            decimal lastClose = ticks[0].Price;

            long totalBucketsEstimate = (endBucket - startBucket) / intervalMs + 1;
            bool doFill = fillEmptyBars && totalBucketsEstimate <= 2000;

            for (long curBucket = startBucket; curBucket <= endBucket; curBucket += intervalMs)
            {
                decimal open = 0m, high = decimal.MinValue, low = decimal.MaxValue, close = 0m;
                decimal volume = 0m, quoteVolume = 0m;
                long tradeCount = 0;
                decimal takerBuyVol = 0m, takerBuyQuoteVol = 0m;
                bool hasTrades = false;

                while (tickIdx < totalTicks)
                {
                    long tTime = ticks[tickIdx].Time;
                    if (tTime < curBucket)
                    {
                        tickIdx++;
                        continue;
                    }
                    if (tTime >= curBucket + intervalMs)
                    {
                        break;
                    }

                    var t = ticks[tickIdx];
                    if (!hasTrades)
                    {
                        open = t.Price;
                        high = t.Price;
                        low = t.Price;
                        hasTrades = true;
                    }
                    if (t.Price > high) high = t.Price;
                    if (t.Price < low) low = t.Price;
                    close = t.Price;
                    volume += t.Qty;
                    decimal tQuote = t.QuoteQty > 0m ? t.QuoteQty : (t.Price * t.Qty);
                    quoteVolume += tQuote;
                    tradeCount++;
                    if (!t.IsBuyerMaker)
                    {
                        takerBuyVol += t.Qty;
                        takerBuyQuoteVol += tQuote;
                    }
                    tickIdx++;
                }

                if (hasTrades)
                {
                    lastClose = close;
                    result.Add(new RawKline
                    {
                        OpenTime = curBucket,
                        Open = open,
                        High = high,
                        Low = low,
                        Close = close,
                        Volume = volume,
                        CloseTime = curBucket + intervalMs - 1,
                        QuoteVolume = quoteVolume,
                        TradeCount = tradeCount,
                        TakerBuyVolume = takerBuyVol,
                        TakerBuyQuoteVolume = takerBuyQuoteVol
                    });
                }
                else if (doFill && result.Count > 0)
                {
                    result.Add(new RawKline
                    {
                        OpenTime = curBucket,
                        Open = lastClose,
                        High = lastClose,
                        Low = lastClose,
                        Close = lastClose,
                        Volume = 0m,
                        CloseTime = curBucket + intervalMs - 1,
                        QuoteVolume = 0m,
                        TradeCount = 0,
                        TakerBuyVolume = 0m,
                        TakerBuyQuoteVolume = 0m
                    });
                }
            }

            return result;
        }

        public void Play()
        {
            if (_allKlines.Count == 0) return;
            if (_currentIndex >= _allKlines.Count - 1)
            {
                _currentIndex = Math.Min(_allKlines.Count - 1, LeftLength - 1);
            }

            _state = PlaybackState.Playing;
            _timer?.Start();
            OnStateChanged?.Invoke(_state);
            OnLogMessage?.Invoke("[回放] ▶ 开始自动播放");
        }

        public void Pause()
        {
            _state = PlaybackState.Paused;
            _timer?.Stop();
            OnStateChanged?.Invoke(_state);
            OnLogMessage?.Invoke("[回放] ⏸ 暂停播放");
        }

        public void Stop()
        {
            _state = PlaybackState.Stopped;
            _timer?.Stop();
            OnStateChanged?.Invoke(_state);
        }

        public void StepForward(int count = 1)
        {
            if (_allKlines.Count == 0) return;
            Pause();

            for (int i = 0; i < count; i++)
            {
                if (_currentIndex < _allKlines.Count - 1)
                {
                    _currentIndex++;
                }
            }

            TriggerCurrentFrame();
        }

        public void Reset()
        {
            Pause();
            _retentionTracker.Reset();
            _vPatternDetector.Reset();
            _consecutiveTrendDetector.Reset();
            _reversalOrderEngine.Reset();
            if (_allKlines.Count >= LeftLength)
            {
                _currentIndex = LeftLength - 1;
            }
            else
            {
                _currentIndex = 0;
            }

            _state = PlaybackState.Stopped;
            OnStateChanged?.Invoke(_state);
            TriggerCurrentFrame();
            OnLogMessage?.Invoke($"[回放] ⏹ 已重置回初始位置 (第 {_currentIndex + 1} 根)");
        }

        public void SeekTo(int index)
        {
            if (_allKlines.Count == 0) return;
            int clamped = Math.Clamp(index, 0, _allKlines.Count - 1);
            if (_currentIndex != clamped)
            {
                _currentIndex = clamped;
                _retentionTracker.SyncTo(clamped);
                _vPatternDetector.SyncTo(clamped);
                _consecutiveTrendDetector.SyncTo(clamped);
                _reversalOrderEngine.SyncTo(clamped, _allKlines);
                TriggerCurrentFrame();
            }
        }

        public void SetSpeed(int intervalMs)
        {
            _speedIntervalMs = Math.Clamp(intervalMs, 5, 2000);
            if (_timer != null)
            {
                _timer.Interval = _speedIntervalMs;
            }
        }

        private void ProcessNextStep()
        {
            if (_currentIndex < _allKlines.Count - 1)
            {
                _currentIndex++;
                TriggerCurrentFrame();
            }
            else
            {
                Pause();
                OnLogMessage?.Invoke("[回放] 全部真实 K 线回放完毕。");
            }
        }

        public void TriggerCurrentFrame()
        {
            if (_allKlines.Count == 0 || _currentIndex < 0 || _currentIndex >= _allKlines.Count) return;

            var currentKline = _allKlines[_currentIndex];
            var channel = DynamicChannelCalculator.Calculate(
                _allKlines,
                _currentIndex,
                LeftLength,
                RightExtendLength,
                CumulativeMode,
                CalculationMode);

            // 驱动相对极值通道保留与突破实时跟踪器
            _retentionTracker.ProcessBar(
                _allKlines,
                _currentIndex,
                channel,
                EnableRetainedChannel,
                RetainedConfirmBars,
                BreakoutRule,
                EnableSpecialRetainedStyle,
                SpecialRetainedMinBars,
                SpecialRetainedMinAngle);

            // 驱动 V 形态与倒 V 形态识别 (价差 ≥ 5%)
            _vPatternDetector.EnableDetection = EnableVPattern;
            _vPatternDetector.MinPriceDiffPct = VPatternMinPriceDiffPct;
            _vPatternDetector.ProcessCurrentSequence(_allKlines, _currentIndex);

            // 驱动连续上涨 / 连续下跌形态识别 (默认 0.0% 门槛，连续 5 根同向即刻识别)
            _consecutiveTrendDetector.EnableDetection = EnableConsecutiveTrend;
            _consecutiveTrendDetector.MinBars = ConsecutiveTrendMinBars;
            _consecutiveTrendDetector.MinPriceChangePct = ConsecutiveTrendMinPct;
            _consecutiveTrendDetector.ChannelPriceMode = ConsecutiveChannelPriceMode;
            _consecutiveTrendDetector.EnableChannelAutoUpdate = EnableChannelAutoUpdate;
            _consecutiveTrendDetector.ChannelUpdateMode = ChannelUpdateMode;
            _consecutiveTrendDetector.ProcessCurrentSequence(_allKlines, _currentIndex);

            // 驱动连续 5 根反转做单引擎配置同步
            _reversalOrderEngine.EnableReversalOrder = EnableReversalOrder;
            _reversalOrderEngine.ObservationMinutes = ReversalObservationMinutes;
            _reversalOrderEngine.PLong = ReversalPLong;
            _reversalOrderEngine.PMedium = ReversalPMedium;
            _reversalOrderEngine.PShort = ReversalPShort;
            _reversalOrderEngine.TickPullbackThresholdPct = ReversalTickPullbackPct;
            _reversalOrderEngine.ChannelZonePct = ReversalChannelZonePct;

            // 驱动反转做单策略：在连续同向第 5 根收盘时刻激活并实时推演观察期
            if (EnableReversalOrder && _consecutiveTrendDetector.DetectedTrends.Count > 0)
            {
                var trends = _consecutiveTrendDetector.DetectedTrends;
                for (int i = 0; i < trends.Count; i++)
                {
                    var tr = trends[i];
                    int fifthBarIndex = tr.StartIndex + 4;
                    if (_currentIndex >= fifthBarIndex)
                    {
                        // 1. 尝试激活策略 (在第 5 根收盘时刻触发 OnStrategyActivated 与高亮日志)
                        _reversalOrderEngine.TryActivateStrategy(tr, _allKlines, _currentIndex, out _);

                        // 2. 推进观察期推演与形态识别 (优先使用已缓存 Tick，若无则自适应回退到 1m 真实 K 线)
                        _reversalOrderEngine.EvaluateObservationCycles(
                            tr,
                            _allKlines,
                            ticks: null,
                            _currentIndex,
                            fallback1mKlines: _raw1mKlines.Count > 0 ? _raw1mKlines : _allKlines);
                    }
                }
            }

            OnBarReplayed?.Invoke(currentKline, _currentIndex, channel);
        }

        public void Dispose()
        {
            _timer?.Stop();
            _timer?.Dispose();
            _timer = null;
        }
    }
}
