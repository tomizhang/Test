using Common;
using System;
using System.Collections.Generic;
using Test.ChannelPlayback.WinForms.Models;

namespace Test.ChannelPlayback.WinForms.Engine
{
    /// <summary>
    /// 相对极值通道保留与突破实时监测跟踪引擎
    /// 核心逻辑：
    /// 1. 若为下降通道，上通道线向后延展最近无 K 线碰撞，确认该锚点为相对高点，将该通道保留锁定；
    /// 2. 若为上升通道，下通道线向后延展最近无 K 线碰撞，确认该锚点为相对低点，将该通道保留锁定；
    /// 3. 保留后的通道持续向前延展，实时监测后续 K 线收盘价/极值价对通道边界的向上突破或向下跌破。
    /// </summary>
    public class ChannelRetentionTracker
    {
        private readonly List<RetainedChannel> _retainedHistory = new();
        private readonly List<RetainedChannel> _specialRetainedChannels = new();
        private RetainedChannel? _activeChannel = null;

        /// <summary>
        /// 当前正在活跃监控的保留通道 (未突破或刚刚突破)
        /// </summary>
        public RetainedChannel? ActiveChannel => _activeChannel;

        /// <summary>
        /// 历史所有已识别的保留通道列表 (包括已突破和进行中)
        /// </summary>
        public IReadOnlyList<RetainedChannel> RetainedHistory => _retainedHistory;

        /// <summary>
        /// 永久保留的强趋势紫色通道列表 (不再删除，一直保留)
        /// </summary>
        public IReadOnlyList<RetainedChannel> SpecialRetainedChannels => _specialRetainedChannels;

        /// <summary>
        /// 突破事件委托 (保留通道, 突破K线索引, 突破价格, 是否为向上突破)
        /// </summary>
        public event Action<RetainedChannel, int, decimal, bool>? OnBreakoutDetected;

        /// <summary>
        /// 相对极值确认事件委托 (保留通道, 相对极值索引, 相对极值价格, 是否为相对高点)
        /// </summary>
        public event Action<RetainedChannel, int, decimal, bool>? OnExtremeConfirmed;

        /// <summary>
        /// 强趋势大跨度特殊保留规则参数
        /// </summary>
        public bool EnableSpecialRetained { get; set; } = true;
        public int SpecialRetainedMinBars { get; set; } = 100;
        public double SpecialRetainedMinAngle { get; set; } = 35.0;

        /// <summary>
        /// 重置所有保留通道与状态
        /// </summary>
        public void Reset()
        {
            _retainedHistory.Clear();
            _specialRetainedChannels.Clear();
            _activeChannel = null;
        }

        /// <summary>
        /// 随 K 线回放流式推进，处理当前帧并更新通道保留与突破状态
        /// </summary>
        public void ProcessBar(
            IReadOnlyList<RawKline> allKlines,
            int currentBarIndex,
            in DynamicChannelResult currentDynamicChannel,
            bool enableRetention = true,
            int requiredNoCollisionBars = 3,
            BreakoutRule breakoutRule = BreakoutRule.ClosePrice,
            bool? enableSpecialRetained = null,
            int? specialRetainedMinBars = null,
            double? specialRetainedMinAngle = null)
        {
            if (!enableRetention || allKlines == null || currentBarIndex < 0 || currentBarIndex >= allKlines.Count)
            {
                return;
            }

            bool effEnableSpecial = enableSpecialRetained ?? EnableSpecialRetained;
            int effSpecialBars = specialRetainedMinBars ?? SpecialRetainedMinBars;
            double effSpecialAngle = specialRetainedMinAngle ?? SpecialRetainedMinAngle;

            var currBar = allKlines[currentBarIndex];

            // 1. 监测所有永久保留的紫色通道突破情况 (紫色通道一直保留并持续被监测)
            for (int i = 0; i < _specialRetainedChannels.Count; i++)
            {
                var sCh = _specialRetainedChannels[i];
                if (!sCh.IsBrokenOut)
                {
                    sCh.EndTrackIndex = currentBarIndex;
                    CheckChannelBreakout(sCh, currBar, currentBarIndex, breakoutRule);
                }
            }

            // 2. 若当前活跃保留通道不是紫色通道，也检查其突破
            if (_activeChannel != null && !_activeChannel.IsBrokenOut && !_specialRetainedChannels.Contains(_activeChannel))
            {
                _activeChannel.EndTrackIndex = currentBarIndex;
                CheckChannelBreakout(_activeChannel, currBar, currentBarIndex, breakoutRule);
            }

            // 3. 动态检查活跃通道是否随着 K 线推进，跨度与角度达到强趋势门槛，升级为永久紫色保留通道
            if (_activeChannel != null && !_activeChannel.IsSpecialStrongTrend && effEnableSpecial)
            {
                int totalSpan = Math.Max(_activeChannel.BaseChannel.LeftLength, currentBarIndex - _activeChannel.BaseChannel.StartX + 1);
                if (totalSpan >= effSpecialBars && Math.Abs(_activeChannel.BaseChannel.AngleDeg) >= effSpecialAngle)
                {
                    _activeChannel.IsSpecialStrongTrend = true;
                    if (!_specialRetainedChannels.Contains(_activeChannel))
                    {
                        _specialRetainedChannels.Add(_activeChannel);
                    }
                }
            }

            // 2. 检验当前动态外包络通道是否形成新的“相对极值点（无碰撞延展）”并进行保留
            if (currentDynamicChannel.IsValid)
            {
                CheckAndRetainNewChannel(
                    allKlines,
                    currentBarIndex,
                    currentDynamicChannel,
                    requiredNoCollisionBars,
                    effEnableSpecial,
                    effSpecialBars,
                    effSpecialAngle);
            }
        }

        private void CheckAndRetainNewChannel(
            IReadOnlyList<RawKline> allKlines,
            int currentBarIndex,
            in DynamicChannelResult ch,
            int requiredNoCollisionBars,
            bool enableSpecialRetained = true,
            int specialRetainedMinBars = 100,
            double specialRetainedMinAngle = 35.0)
        {
            // 至少要求向后延展 >= requiredNoCollisionBars 根无碰撞
            int minBars = Math.Max(2, requiredNoCollisionBars);

            // A. 下降通道：检验上通道线向后延伸无碰撞 -> 确认相对高点
            if (ch.SlopeK < 0m)
            {
                // 获取上轨关键锚定高点索引
                int highAnchorIdx = ch.DirectionType == ChannelDirectionType.TwoHighsOneLow
                    ? Math.Max(ch.BasePoint1Index, ch.BasePoint2Index)
                    : (ch.TouchHighIndex2 >= 0 ? Math.Max(ch.TouchHighIndex, ch.TouchHighIndex2) : ch.TouchHighIndex);

                if (highAnchorIdx >= 0 && highAnchorIdx < currentBarIndex)
                {
                    int elapsed = currentBarIndex - highAnchorIdx;
                    if (elapsed >= minBars)
                    {
                        // 检验从锚定高点之后至当前 bar，是否有任何 K 线发生上轨碰撞 (High >= y_upper)
                        bool hasCollision = false;
                        decimal tolerance = 0.0001m;

                        for (int j = highAnchorIdx + 1; j <= currentBarIndex; j++)
                        {
                            decimal upAtJ = ch.GetUpperPrice(j);
                            if (allKlines[j].High >= upAtJ - tolerance)
                            {
                                hasCollision = true;
                                break;
                            }
                        }

                        if (!hasCollision)
                        {
                            // 确定该锚点为相对高点！若尚未保留过此高点通道，则锁定保留
                            if (_activeChannel == null || 
                                _activeChannel.IsBrokenOut || 
                                _activeChannel.AnchorExtremeIndex != highAnchorIdx || 
                                _activeChannel.IsUpward)
                            {
                                var retained = new RetainedChannel
                                {
                                    BaseChannel = ch,
                                    AnchorExtremeIndex = highAnchorIdx,
                                    AnchorExtremePrice = allKlines[highAnchorIdx].High,
                                    ConfirmedBarIndex = currentBarIndex,
                                    RequiredNoCollisionBars = minBars,
                                    EndTrackIndex = currentBarIndex,
                                    IsBrokenOut = false
                                };

                                _activeChannel = retained;
                                _retainedHistory.Add(retained);

                                OnExtremeConfirmed?.Invoke(retained, highAnchorIdx, retained.AnchorExtremePrice, true);
                            }
                        }
                    }
                }
            }
            // B. 上升通道：检验下通道线向后延伸无碰撞 -> 确认相对低点
            else if (ch.SlopeK > 0m)
            {
                // 获取下轨关键锚定低点索引
                int lowAnchorIdx = ch.DirectionType == ChannelDirectionType.TwoLowsOneHigh
                    ? Math.Max(ch.BasePoint1Index, ch.BasePoint2Index)
                    : (ch.TouchLowIndex2 >= 0 ? Math.Max(ch.TouchLowIndex, ch.TouchLowIndex2) : ch.TouchLowIndex);

                if (lowAnchorIdx >= 0 && lowAnchorIdx < currentBarIndex)
                {
                    int elapsed = currentBarIndex - lowAnchorIdx;
                    if (elapsed >= minBars)
                    {
                        // 检验从锚定低点之后至当前 bar，是否有任何 K 线发生下轨碰撞 (Low <= y_lower)
                        bool hasCollision = false;
                        decimal tolerance = 0.0001m;

                        for (int j = lowAnchorIdx + 1; j <= currentBarIndex; j++)
                        {
                            decimal lowAtJ = ch.GetLowerPrice(j);
                            if (allKlines[j].Low <= lowAtJ + tolerance)
                            {
                                hasCollision = true;
                                break;
                            }
                        }

                        if (!hasCollision)
                        {
                            // 确定该锚点为相对低点！若尚未保留过此低点通道，则锁定保留
                            if (_activeChannel == null || 
                                _activeChannel.IsBrokenOut || 
                                _activeChannel.AnchorExtremeIndex != lowAnchorIdx || 
                                _activeChannel.IsDownward)
                            {
                                var retained = new RetainedChannel
                                {
                                    BaseChannel = ch,
                                    AnchorExtremeIndex = lowAnchorIdx,
                                    AnchorExtremePrice = allKlines[lowAnchorIdx].Low,
                                    ConfirmedBarIndex = currentBarIndex,
                                    RequiredNoCollisionBars = minBars,
                                    EndTrackIndex = currentBarIndex,
                                    IsBrokenOut = false
                                };

                                _activeChannel = retained;
                                _retainedHistory.Add(retained);

                                OnExtremeConfirmed?.Invoke(retained, lowAnchorIdx, retained.AnchorExtremePrice, false);
                            }
                        }
                    }
                }
            }

            // C. 强趋势大跨度特殊保留规则：若通道 K 线超过设定根数且角度达到设定阈值，直接锁定保留为强趋势通道 (不再删除，一直保留)
            if (enableSpecialRetained && ch.LeftLength >= specialRetainedMinBars && Math.Abs(ch.AngleDeg) >= specialRetainedMinAngle)
            {
                int anchorIdx = ch.DirectionType == ChannelDirectionType.TwoHighsOneLow
                    ? Math.Max(ch.BasePoint1Index, ch.BasePoint2Index)
                    : (ch.DirectionType == ChannelDirectionType.TwoLowsOneHigh
                        ? Math.Max(ch.BasePoint1Index, ch.BasePoint2Index)
                        : ch.CurrentX);
                if (anchorIdx < 0 || anchorIdx >= allKlines.Count) anchorIdx = ch.CurrentX;

                // 避免同一锚点或高度相近的连续帧重复堆叠相同紫色通道
                bool isDownward = ch.SlopeK < 0m;
                int chStartX = ch.StartX;
                bool alreadyExists = false;
                for (int sIdx = 0; sIdx < _specialRetainedChannels.Count; sIdx++)
                {
                    var p = _specialRetainedChannels[sIdx];
                    if (p.IsDownward == isDownward)
                    {
                        // 1. 同方向的前一个紫色通道尚未突破，绝不重复生成同方向新通道
                        if (!p.IsBrokenOut)
                        {
                            alreadyExists = true;
                            break;
                        }

                        // 2. 前一个紫色通道虽已突破，但当前通道起点仍在前一个通道突破点之前（说明仍然是旧趋势区间的滑动重叠），也不视为独立新通道
                        if (p.BreakoutBarIndex >= 0 && chStartX <= p.BreakoutBarIndex && currentBarIndex <= p.BreakoutBarIndex + 40)
                        {
                            alreadyExists = true;
                            break;
                        }

                        // 3. 同一锚点或确认柱距过近或起点重叠
                        if (p.AnchorExtremeIndex == anchorIdx ||
                            Math.Abs(currentBarIndex - p.ConfirmedBarIndex) < 50 ||
                            Math.Abs(chStartX - p.BaseChannel.StartX) < 40)
                        {
                            alreadyExists = true;
                            break;
                        }
                    }
                }

                if (!alreadyExists)
                {
                    decimal anchorPrice = ch.SlopeK < 0m ? allKlines[anchorIdx].High : allKlines[anchorIdx].Low;

                    var retained = new RetainedChannel
                    {
                        BaseChannel = ch,
                        AnchorExtremeIndex = anchorIdx,
                        AnchorExtremePrice = anchorPrice,
                        ConfirmedBarIndex = currentBarIndex,
                        RequiredNoCollisionBars = minBars,
                        EndTrackIndex = currentBarIndex,
                        IsBrokenOut = false,
                        IsSpecialStrongTrend = true
                    };

                    _activeChannel = retained;
                    _specialRetainedChannels.Add(retained);
                    _retainedHistory.Add(retained);

                    OnExtremeConfirmed?.Invoke(retained, anchorIdx, anchorPrice, ch.SlopeK < 0m);
                }
            }
        }

        private void CheckChannelBreakout(RetainedChannel chRet, RawKline currBar, int currentBarIndex, BreakoutRule breakoutRule)
        {
            if (chRet.IsBrokenOut) return;

            if (chRet.IsDownward)
            {
                // 下降通道：监测向上突破上轨
                decimal upperPrice = chRet.GetUpperPrice(currentBarIndex);
                decimal testPrice = breakoutRule == BreakoutRule.ClosePrice ? currBar.Close : currBar.High;

                if (testPrice > upperPrice)
                {
                    chRet.IsBrokenOut = true;
                    chRet.BreakoutBarIndex = currentBarIndex;
                    chRet.BreakoutPrice = currBar.Close;
                    chRet.BoundaryPriceAtBreakout = upperPrice;
                    chRet.BreakoutPct = upperPrice > 0m ? ((testPrice - upperPrice) / upperPrice) * 100m : 0m;

                    OnBreakoutDetected?.Invoke(chRet, currentBarIndex, currBar.Close, true);
                }
            }
            else if (chRet.IsUpward)
            {
                // 上升通道：监测向下跌破下轨
                decimal lowerPrice = chRet.GetLowerPrice(currentBarIndex);
                decimal testPrice = breakoutRule == BreakoutRule.ClosePrice ? currBar.Close : currBar.Low;

                if (testPrice < lowerPrice)
                {
                    chRet.IsBrokenOut = true;
                    chRet.BreakoutBarIndex = currentBarIndex;
                    chRet.BreakoutPrice = currBar.Close;
                    chRet.BoundaryPriceAtBreakout = lowerPrice;
                    chRet.BreakoutPct = lowerPrice > 0m ? ((testPrice - lowerPrice) / lowerPrice) * 100m : 0m;

                    OnBreakoutDetected?.Invoke(chRet, currentBarIndex, currBar.Close, false);
                }
            }
        }

        /// <summary>
        /// 当用户拖拽回放进度条跳转至 targetBarIndex 时，安全回滚保留状态
        /// </summary>
        public void SyncTo(int targetBarIndex)
        {
            if (targetBarIndex < 0)
            {
                Reset();
                return;
            }

            // 移除在 targetBarIndex 之后才被确认的通道
            _retainedHistory.RemoveAll(r => r.ConfirmedBarIndex > targetBarIndex);
            _specialRetainedChannels.RemoveAll(r => r.ConfirmedBarIndex > targetBarIndex);

            // 对保留下来的通道，回滚其跟踪截止与突破状态
            foreach (var r in _retainedHistory)
            {
                r.EndTrackIndex = Math.Min(r.EndTrackIndex, targetBarIndex);
                if (r.IsBrokenOut && r.BreakoutBarIndex > targetBarIndex)
                {
                    r.IsBrokenOut = false;
                    r.BreakoutBarIndex = -1;
                    r.BreakoutPrice = 0m;
                    r.BoundaryPriceAtBreakout = 0m;
                    r.BreakoutPct = 0m;
                }
            }
            foreach (var r in _specialRetainedChannels)
            {
                r.EndTrackIndex = Math.Min(r.EndTrackIndex, targetBarIndex);
                if (r.IsBrokenOut && r.BreakoutBarIndex > targetBarIndex)
                {
                    r.IsBrokenOut = false;
                    r.BreakoutBarIndex = -1;
                    r.BreakoutPrice = 0m;
                    r.BoundaryPriceAtBreakout = 0m;
                    r.BreakoutPct = 0m;
                }
            }

            // 重新选定活跃监控通道 (最后一个未突破者，或最后一个已确认者)
            _activeChannel = null;
            for (int i = _retainedHistory.Count - 1; i >= 0; i--)
            {
                var r = _retainedHistory[i];
                if (!r.IsBrokenOut)
                {
                    _activeChannel = r;
                    break;
                }
            }

            if (_activeChannel == null && _retainedHistory.Count > 0)
            {
                _activeChannel = _retainedHistory[_retainedHistory.Count - 1];
            }
        }
    }
}
