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
        public ChannelCalculationMode CalculationMode { get; set; } = ChannelCalculationMode.LinearRegression;

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

        public KlinePlaybackEngine()
        {
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = _speedIntervalMs;
            _timer.Tick += (s, e) => ProcessNextStep();
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
            _currentIndex = 0;

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
