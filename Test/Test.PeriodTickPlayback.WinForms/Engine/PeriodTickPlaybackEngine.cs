using Common;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Test.PeriodTickPlayback.WinForms.Models;

namespace Test.PeriodTickPlayback.WinForms.Engine
{
    /// <summary>
    /// 大周期与逐笔 Tick 嵌套流式回放调度引擎
    /// 核心推进法则：
    /// 1. 在当前大周期 (如 30 分钟) 内部，优先逐笔/倍速派发输出该周期内的所有 Tick 数据；
    /// 2. 只有当该 30 分钟周期内的全部 Tick 均派发输出完毕后，才正式触发定型并输出这一根 30 分钟 K 线；
    /// 3. 接着自动推进至下一个大周期，开始新周期的逐笔 Tick 输出循环。
    /// </summary>
    public class PeriodTickPlaybackEngine : IDisposable
    {
        private List<PeriodBucket> _buckets = new();
        private readonly List<MacroKline> _completedMacroBars = new();

        private int _currentBucketIndex = 0;
        private int _currentTickIndex = 0;
        private FormingMacroKline _formingBar = new();

        private volatile PlaybackState _state = PlaybackState.Idle;
        private CancellationTokenSource? _playbackCts;
        private Task? _playbackTask;

        private double _speedMultiplier = 10.0;
        private int _ticksPerFrame = 1;
        private int _delayMs = 20;

        private readonly object _lock = new();

        // 性能统计
        private long _totalTicksPlayed = 0;
        private readonly Stopwatch _tpsStopwatch = new();
        private long _lastTpsTickCount = 0;
        private double _currentTps = 0;

        #region 事件通知

        /// <summary>
        /// 当 Tick 被派发输出时触发 (包含最新 Tick、当前形成中的 K 线快照、当前桶内进度)
        /// </summary>
        public event Action<RawTick, FormingMacroKline, int, int>? OnTickDispatched;

        /// <summary>
        /// 当一个完整大周期内的所有 Tick 全部输出完毕后，正式触发定型输出该大周期 K 线
        /// </summary>
        public event Action<MacroKline, int, int>? OnMacroBarCompleted;

        /// <summary>
        /// 当进入新的大周期桶时触发
        /// </summary>
        public event Action<PeriodBucket, int, int>? OnBucketStarted;

        /// <summary>
        /// 回放状态变更通知 (Idle, Playing, Paused, Completed 等)
        /// </summary>
        public event Action<PlaybackState>? OnStateChanged;

        /// <summary>
        /// 当当前加载的所有周期桶播放完毕，需要异步追加新批次数据时触发。
        /// 返回 true 表示成功追加新批次数据；返回 false 表示已无更多批次数据。
        /// </summary>
        public event Func<Task<bool>>? OnRequestMoreBucketsAsync;

        /// <summary>
        /// 日志或诊断消息通知
        /// </summary>
        public event Action<string, System.Drawing.Color>? OnLogMessage;

        #endregion

        #region 公开属性

        public PlaybackState State => _state;
        public IReadOnlyList<PeriodBucket> Buckets => _buckets;
        public IReadOnlyList<MacroKline> CompletedMacroBars => _completedMacroBars;
        public FormingMacroKline CurrentFormingBar => _formingBar;
        public int CurrentBucketIndex => _currentBucketIndex;
        public int TotalBuckets => _buckets.Count;
        public int CurrentTickIndex => _currentTickIndex;
        public double SpeedMultiplier => _speedMultiplier;
        public double CurrentTps => _currentTps;
        public long TotalTicksPlayed => _totalTicksPlayed;

        public PeriodBucket? CurrentBucket =>
            (_currentBucketIndex >= 0 && _currentBucketIndex < _buckets.Count) ? _buckets[_currentBucketIndex] : null;

        /// <summary>
        /// 获取已定型大周期 K 线的线程安全快照 (防止图表渲染与引擎追加并发冲突)
        /// </summary>
        public MacroKline[] GetCompletedMacroBarsSnapshot()
        {
            lock (_lock)
            {
                return _completedMacroBars.ToArray();
            }
        }

        #endregion

        public PeriodTickPlaybackEngine()
        {
            _tpsStopwatch.Start();
        }

        /// <summary>
        /// 载入已切片完毕的宏观周期桶集合并重置状态
        /// </summary>
        public void LoadBuckets(List<PeriodBucket> buckets)
        {
            Stop();

            lock (_lock)
            {
                _buckets = buckets ?? new List<PeriodBucket>();
                _completedMacroBars.Clear();
                _currentBucketIndex = 0;
                _currentTickIndex = 0;
                _totalTicksPlayed = 0;
                InitCurrentBucket();

                _state = _buckets.Count > 0 ? PlaybackState.Ready : PlaybackState.Idle;
            }

            OnStateChanged?.Invoke(_state);
        }

        /// <summary>
        /// 动态向引擎追加新的周期桶 (分批流式加载支持)
        /// </summary>
        public void AppendBuckets(List<PeriodBucket> newBuckets)
        {
            if (newBuckets == null || newBuckets.Count == 0) return;

            lock (_lock)
            {
                int startIndex = _buckets.Count;
                for (int i = 0; i < newBuckets.Count; i++)
                {
                    newBuckets[i].BucketIndex = startIndex + i;
                    if (newBuckets[i].FinalKline != null)
                    {
                        newBuckets[i].FinalKline!.BarIndex = startIndex + i;
                    }
                    _buckets.Add(newBuckets[i]);
                }

                if (_state == PlaybackState.Completed && _currentBucketIndex < _buckets.Count)
                {
                    _state = PlaybackState.Ready;
                    InitCurrentBucket();
                }
                else if (_state == PlaybackState.Idle && _buckets.Count > 0)
                {
                    _state = PlaybackState.Ready;
                    InitCurrentBucket();
                }
            }

            OnStateChanged?.Invoke(_state);
        }

        #region 回放控制 (Play, Pause, Stop, Step, FastForward)

        /// <summary>
        /// 开始或继续嵌套回放
        /// </summary>
        public void Play()
        {
            if (_buckets.Count == 0) return;
            if (_state == PlaybackState.Playing) return;

            if (_currentBucketIndex >= _buckets.Count)
            {
                // 若已在末尾，重置回起点
                ResetToBeginning();
            }

            _state = PlaybackState.Playing;
            OnStateChanged?.Invoke(_state);

            _playbackCts = new CancellationTokenSource();
            _playbackTask = Task.Run(() => PlaybackLoopAsync(_playbackCts.Token));
        }

        /// <summary>
        /// 暂停回放
        /// </summary>
        public void Pause()
        {
            if (_state != PlaybackState.Playing) return;

            _playbackCts?.Cancel();
            _state = PlaybackState.Paused;
            OnStateChanged?.Invoke(_state);
        }

        /// <summary>
        /// 停止回放并复位至起点
        /// </summary>
        public void Stop()
        {
            _playbackCts?.Cancel();

            lock (_lock)
            {
                ResetToBeginning();
                _state = _buckets.Count > 0 ? PlaybackState.Ready : PlaybackState.Idle;
            }

            OnStateChanged?.Invoke(_state);
        }

        /// <summary>
        /// 单步推进 Tick (单步调试模式)
        /// </summary>
        public void StepTick(int count = 1)
        {
            if (_state == PlaybackState.Playing) Pause();

            lock (_lock)
            {
                for (int i = 0; i < count; i++)
                {
                    if (!StepOneTickInternal())
                    {
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// 一键直接完成当前大周期：瞬间派发完当前周期剩余的所有 Tick，立即定型输出该大周期 K 线并推进至下个周期
        /// </summary>
        public void FastForwardCurrentBucket()
        {
            lock (_lock)
            {
                if (_currentBucketIndex >= _buckets.Count) return;
                var bucket = _buckets[_currentBucketIndex];

                // 快速把当前桶内剩余的 Tick 全部吸纳
                while (_currentTickIndex < bucket.Ticks.Count)
                {
                    var tick = bucket.Ticks[_currentTickIndex];
                    UpdateFormingBar(tick);
                    _currentTickIndex++;
                    _totalTicksPlayed++;
                }

                // 触发周期定型输出
                FinishCurrentBucketAndOutput();
            }
        }

        /// <summary>
        /// 设置播放倍速
        /// </summary>
        public void SetSpeed(double multiplier)
        {
            _speedMultiplier = Math.Max(0.1, multiplier);

            if (_speedMultiplier <= 1.0)
            {
                _ticksPerFrame = 1;
                _delayMs = 50;
            }
            else if (_speedMultiplier <= 5.0)
            {
                _ticksPerFrame = 2;
                _delayMs = 30;
            }
            else if (_speedMultiplier <= 10.0)
            {
                _ticksPerFrame = 5;
                _delayMs = 20;
            }
            else if (_speedMultiplier <= 50.0)
            {
                _ticksPerFrame = 25;
                _delayMs = 15;
            }
            else if (_speedMultiplier <= 100.0)
            {
                _ticksPerFrame = 60;
                _delayMs = 10;
            }
            else if (_speedMultiplier <= 500.0)
            {
                _ticksPerFrame = 250;
                _delayMs = 5;
            }
            else // 极速模式
            {
                _ticksPerFrame = 1000;
                _delayMs = 1;
            }
        }

        #endregion

        #region 内部调度循环与状态推进

        private async Task PlaybackLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && _state == PlaybackState.Playing)
                {
                    bool hasMore = false;

                    lock (_lock)
                    {
                        for (int i = 0; i < _ticksPerFrame; i++)
                        {
                            hasMore = StepOneTickInternal();
                            if (!hasMore) break;
                        }
                    }

                    // 计算实时 TPS 吞吐率
                    UpdateTps();

                    if (!hasMore)
                    {
                        // 检查是否有注册的分批追加回调
                        if (OnRequestMoreBucketsAsync != null)
                        {
                            OnLogMessage?.Invoke("[流式缓冲] 正在载入后续分批数据，请稍候...", System.Drawing.Color.FromArgb(250, 204, 21));
                            bool gotMore = false;
                            try
                            {
                                gotMore = await OnRequestMoreBucketsAsync().ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                            catch (Exception ex)
                            {
                                OnLogMessage?.Invoke($"[分批载入异常] {ex.Message}", System.Drawing.Color.FromArgb(248, 113, 113));
                            }

                            if (gotMore && !ct.IsCancellationRequested && _state == PlaybackState.Playing)
                            {
                                lock (_lock)
                                {
                                    if (_currentBucketIndex < _buckets.Count)
                                    {
                                        hasMore = true;
                                    }
                                }

                                if (hasMore)
                                {
                                    OnLogMessage?.Invoke("[无缝续播] 后续分批数据已成功追加，继续回放！", System.Drawing.Color.FromArgb(74, 222, 128));
                                    continue; // 成功追加新批次，无缝继续播放！
                                }
                            }
                        }

                        _state = PlaybackState.Completed;
                        OnStateChanged?.Invoke(_state);
                        OnLogMessage?.Invoke("[回放完毕] 所有已加载大周期与 Tick 数据已全量回放完成！", System.Drawing.Color.FromArgb(74, 222, 128));
                        break;
                    }

                    if (_delayMs > 0)
                    {
                        await Task.Delay(_delayMs, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                OnLogMessage?.Invoke($"[回放异常] {ex.Message}", System.Drawing.Color.FromArgb(248, 113, 113));
                _state = PlaybackState.Paused;
                OnStateChanged?.Invoke(_state);
            }
        }

        /// <summary>
        /// 推进单个 Tick。若当前桶结束，自动输出该大周期 K 线并步进到下一桶。
        /// 返回值：是否还有后续数据可继续播放
        /// </summary>
        private bool StepOneTickInternal()
        {
            if (_currentBucketIndex >= _buckets.Count) return false;

            var bucket = _buckets[_currentBucketIndex];

            // 1. 若当前桶内还有未派发的 Tick，派发出一个 Tick
            if (_currentTickIndex < bucket.Ticks.Count)
            {
                var tick = bucket.Ticks[_currentTickIndex];
                UpdateFormingBar(tick);

                _currentTickIndex++;
                _totalTicksPlayed++;

                OnTickDispatched?.Invoke(tick, _formingBar, _currentTickIndex, bucket.Ticks.Count);
            }

            // 2. 关键业务时序判定：若当前桶内的所有 Tick 刚刚全部播放完毕！
            if (_currentTickIndex >= bucket.Ticks.Count)
            {
                FinishCurrentBucketAndOutput();

                // 若已无后续桶，返回 false
                if (_currentBucketIndex >= _buckets.Count)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 完成当前周期桶：定型并向大图表输出这根大周期 K 线，随后推进至下一周期桶
        /// </summary>
        private void FinishCurrentBucketAndOutput()
        {
            if (_currentBucketIndex >= _buckets.Count) return;

            var bucket = _buckets[_currentBucketIndex];
            var finalKline = bucket.FinalKline ?? new MacroKline
            {
                BarIndex = _currentBucketIndex,
                OpenTime = bucket.StartTime,
                CloseTime = bucket.EndTime,
                Open = _formingBar.Open,
                High = _formingBar.High,
                Low = _formingBar.Low,
                Close = _formingBar.CurrentPrice,
                Volume = _formingBar.Volume,
                QuoteVolume = _formingBar.QuoteVolume,
                TradeCount = bucket.Ticks.Count
            };

            // 1. 正式压入已定型宏观 K 线列表
            _completedMacroBars.Add(finalKline);

            // 2. 触发大周期 K 线输出事件
            OnMacroBarCompleted?.Invoke(finalKline, _currentBucketIndex, _buckets.Count);

            OnLogMessage?.Invoke(
                $"[周期完成] 跨度 {bucket.StartTime:HH:mm} ~ {bucket.EndTime:HH:mm} (共 {bucket.Ticks.Count:N0} Ticks) 播放完毕 -> 正式输出 Bar #{_currentBucketIndex}: O:{finalKline.Open:F2} H:{finalKline.High:F2} L:{finalKline.Low:F2} C:{finalKline.Close:F2} V:{finalKline.Volume:F2} ({finalKline.ChangePct:+0.00;-0.00;0.00}%)",
                finalKline.IsBullish ? System.Drawing.Color.FromArgb(74, 222, 128) : System.Drawing.Color.FromArgb(248, 113, 113));

            // 3. 推进到下一个周期桶
            _currentBucketIndex++;
            _currentTickIndex = 0;

            if (_currentBucketIndex < _buckets.Count)
            {
                InitCurrentBucket();
            }
        }

        private void InitCurrentBucket()
        {
            if (_currentBucketIndex < _buckets.Count)
            {
                var b = _buckets[_currentBucketIndex];
                _formingBar = new FormingMacroKline
                {
                    BucketIndex = _currentBucketIndex,
                    StartTime = b.StartTime,
                    EndTime = b.EndTime,
                    TotalTicksInBucket = b.Ticks.Count,
                    TicksProcessed = 0,
                    Open = b.Ticks.Count > 0 ? b.Ticks[0].Price : 0m,
                    High = decimal.MinValue,
                    Low = decimal.MaxValue,
                    CurrentPrice = b.Ticks.Count > 0 ? b.Ticks[0].Price : 0m,
                    Volume = 0m,
                    QuoteVolume = 0m
                };

                OnBucketStarted?.Invoke(b, _currentBucketIndex, _buckets.Count);
            }
        }

        private void UpdateFormingBar(RawTick tick)
        {
            if (_formingBar.TicksProcessed == 0)
            {
                _formingBar.Open = tick.Price;
                _formingBar.High = tick.Price;
                _formingBar.Low = tick.Price;
            }
            else
            {
                if (tick.Price > _formingBar.High) _formingBar.High = tick.Price;
                if (tick.Price < _formingBar.Low) _formingBar.Low = tick.Price;
            }

            _formingBar.CurrentPrice = tick.Price;
            _formingBar.Volume += tick.Qty;
            _formingBar.QuoteVolume += tick.QuoteQty;
            _formingBar.TicksProcessed++;
        }

        private void ResetToBeginning()
        {
            _completedMacroBars.Clear();
            _currentBucketIndex = 0;
            _currentTickIndex = 0;
            _totalTicksPlayed = 0;
            InitCurrentBucket();
        }

        private void UpdateTps()
        {
            if (_tpsStopwatch.ElapsedMilliseconds >= 500)
            {
                long ticksDelta = _totalTicksPlayed - _lastTpsTickCount;
                double seconds = _tpsStopwatch.Elapsed.TotalSeconds;
                if (seconds > 0)
                {
                    _currentTps = ticksDelta / seconds;
                }
                _lastTpsTickCount = _totalTicksPlayed;
                _tpsStopwatch.Restart();
            }
        }

        #endregion

        public void Dispose()
        {
            _playbackCts?.Cancel();
            _playbackCts?.Dispose();
        }
    }
}
