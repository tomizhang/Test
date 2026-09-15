using Common;
using System;
using System.Collections.Generic;
using Test.ChannelPlayback.WinForms.Models;

namespace Test.ChannelPlayback.WinForms.Engine
{
    /// <summary>
    /// V 形态与倒 V 形态专业识别引擎
    /// 核心算法：
    /// 1. V底形态：左翼高点 -> 尖锐谷底 -> 右翼高点，左侧跌幅与右侧反弹价差均 >= 门槛 (默认 5%)
    /// 2. 倒V顶形态：左翼低点 -> 尖锐峰顶 -> 右翼低点，左侧涨幅与右侧回落价差均 >= 门槛 (默认 5%)
    /// 3. 实时确立：在右翼变动幅度首次触碰门槛百分比的当根 K 线立即锁定确认
    /// 4. 动态展开：在右翼未形成新的拐点前，右翼端点随 K 线推演实时自适应追踪最高/最低极值
    /// 5. 时间轴回滚：支持 SyncTo 安全回滚历史任意帧
    /// </summary>
    public class VPatternDetector
    {
        private readonly List<VPatternItem> _detectedPatterns = new();
        private decimal _minPriceDiffPct = 5.0m;
        private bool _enableDetection = true;

        /// <summary>
        /// 当前已识别且已确认的所有形态列表
        /// </summary>
        public IReadOnlyList<VPatternItem> DetectedPatterns => _detectedPatterns;

        /// <summary>
        /// 是否开启形态识别
        /// </summary>
        public bool EnableDetection
        {
            get => _enableDetection;
            set => _enableDetection = value;
        }

        /// <summary>
        /// 最小有效价差百分比门槛 (默认 5.0%)
        /// </summary>
        public decimal MinPriceDiffPct
        {
            get => _minPriceDiffPct;
            set => _minPriceDiffPct = Math.Max(0.5m, value);
        }

        /// <summary>
        /// 形态新确认事件 (形态对象, 确认K线索引)
        /// </summary>
        public event Action<VPatternItem, int>? OnPatternDetected;

        /// <summary>
        /// 清空所有已识别形态
        /// </summary>
        public void Reset()
        {
            _detectedPatterns.Clear();
        }

        /// <summary>
        /// 时间轴跳转或回滚至指定 K 线位置
        /// </summary>
        public void SyncTo(int targetBarIndex)
        {
            if (targetBarIndex < 0)
            {
                Reset();
                return;
            }

            // 移除在 targetBarIndex 之后才被首次确认的形态
            _detectedPatterns.RemoveAll(p => p.ConfirmedBarIndex > targetBarIndex);

            // 对保留的形态，将处于开放状态的右翼截断至 targetBarIndex
            foreach (var p in _detectedPatterns)
            {
                if (p.RightIndex > targetBarIndex)
                {
                    p.RightIndex = targetBarIndex;
                    p.IsRightWingOpen = true;
                }
            }
        }

        /// <summary>
        /// 针对当前推进到的最新 K 线序列，全量自适应扫描并更新形态列表
        /// 算法时间复杂度 O(N)，毫秒级完成，保证与回放、拖动进度条 100% 严密一致
        /// </summary>
        public void ProcessCurrentSequence(IReadOnlyList<RawKline> allKlines, int currentBarIndex)
        {
            if (!_enableDetection || allKlines == null || currentBarIndex < 2)
            {
                _detectedPatterns.Clear();
                return;
            }

            int maxIndex = Math.Clamp(currentBarIndex, 0, allKlines.Count - 1);
            var scanned = ScanPatterns(allKlines, maxIndex, _minPriceDiffPct);

            // 比对新触发的确认事件
            var existingIds = new HashSet<int>();
            for (int i = 0; i < _detectedPatterns.Count; i++) existingIds.Add(_detectedPatterns[i].Id);

            _detectedPatterns.Clear();
            _detectedPatterns.AddRange(scanned);

            for (int i = 0; i < scanned.Count; i++)
            {
                var p = scanned[i];
                if (!existingIds.Contains(p.Id))
                {
                    OnPatternDetected?.Invoke(p, p.ConfirmedBarIndex);
                }
            }
        }

        /// <summary>
        /// 静态极速形态扫描算法
        /// </summary>
        public static List<VPatternItem> ScanPatterns(IReadOnlyList<RawKline> klines, int maxIndex, decimal thresholdPct)
        {
            var result = new List<VPatternItem>();
            if (klines == null || maxIndex < 3) return result;

            decimal minRatio = thresholdPct / 100.0m;

            // 1. 提取波段极值转折点 (ZigZag 动态特征)
            // 设定微波过滤门槛 (为捕捉 5% 的大波段，使用 1.5% 作为转向确认步长)
            decimal reversalStep = Math.Max(0.015m, minRatio * 0.3m);

            var swingPoints = ExtractSwingPoints(klines, maxIndex, reversalStep);
            if (swingPoints.Count < 2) return result;

            int nextId = 1;

            // 2. 在极值序列中搜索满足条件的 V 底 (高 -> 低 -> 高) 与 倒V顶 (低 -> 高 -> 低)
            for (int i = 1; i < swingPoints.Count; i++)
            {
                var prev = swingPoints[i - 1];
                var curr = swingPoints[i];

                // 情况 A: curr 为低点 (谷底)，后续寻找反弹形成 V 底
                if (!curr.IsPeak && prev.IsPeak)
                {
                    int leftIdx = prev.Index;
                    decimal leftPrice = prev.Price;
                    int vertexIdx = curr.Index;
                    decimal vertexPrice = curr.Price;

                    if (vertexPrice <= 0m) continue;

                    decimal dropPct = (leftPrice - vertexPrice) / vertexPrice * 100m;
                    if (dropPct >= thresholdPct)
                    {
                        // 校验左翼单调性：区间内无高于 leftPrice 的点，且最低点为 vertexPrice
                        bool validLeft = true;
                        for (int k = leftIdx; k <= vertexIdx; k++)
                        {
                            if (klines[k].High > leftPrice * 1.005m || klines[k].Low < vertexPrice - 0.0001m)
                            {
                                validLeft = false;
                                break;
                            }
                        }
                        if (!validLeft) continue;

                        // 寻找右翼反弹：自 vertexIdx 之后直至下一个明显转折或 maxIndex
                        int rightEndLimit = (i + 1 < swingPoints.Count) ? swingPoints[i + 1].Index : maxIndex;
                        rightEndLimit = Math.Min(rightEndLimit, maxIndex);

                        // 找到从 vertexIdx 到 rightEndLimit 之间的最高点与首次达到 thresholdPct 的确认点
                        decimal highestRebound = vertexPrice;
                        int highestIdx = vertexIdx;
                        int confirmedBar = -1;

                        for (int k = vertexIdx + 1; k <= rightEndLimit; k++)
                        {
                            if (klines[k].Low < vertexPrice)
                            {
                                // 跌破了谷底，V底被破坏
                                break;
                            }

                            if (klines[k].High > highestRebound)
                            {
                                highestRebound = klines[k].High;
                                highestIdx = k;
                            }

                            decimal currentReboundPct = (klines[k].High - vertexPrice) / vertexPrice * 100m;
                            if (currentReboundPct >= thresholdPct && confirmedBar < 0)
                            {
                                confirmedBar = k;
                            }
                        }

                        decimal reboundPct = (highestRebound - vertexPrice) / vertexPrice * 100m;
                        if (confirmedBar >= 0 && reboundPct >= thresholdPct)
                        {
                            bool isRightOpen = (i + 1 >= swingPoints.Count) && (highestIdx == maxIndex || maxIndex - highestIdx <= 2);

                            var item = new VPatternItem
                            {
                                Id = nextId++,
                                Type = VPatternType.VBottom,
                                LeftIndex = leftIdx,
                                LeftPrice = leftPrice,
                                VertexIndex = vertexIdx,
                                VertexPrice = vertexPrice,
                                RightIndex = highestIdx,
                                RightPrice = highestRebound,
                                ConfirmedBarIndex = confirmedBar,
                                LeftSpanPct = dropPct,
                                RightSpanPct = reboundPct,
                                IsRightWingOpen = isRightOpen
                            };
                            result.Add(item);
                        }
                    }
                }
                // 情况 B: curr 为高点 (峰顶)，后续寻找回落形成 倒V顶
                else if (curr.IsPeak && !prev.IsPeak)
                {
                    int leftIdx = prev.Index;
                    decimal leftPrice = prev.Price;
                    int vertexIdx = curr.Index;
                    decimal vertexPrice = curr.Price;

                    if (leftPrice <= 0m) continue;

                    decimal risePct = (vertexPrice - leftPrice) / leftPrice * 100m;
                    if (risePct >= thresholdPct)
                    {
                        // 校验左翼单调性：区间内无低于 leftPrice 的点，且最高点为 vertexPrice
                        bool validLeft = true;
                        for (int k = leftIdx; k <= vertexIdx; k++)
                        {
                            if (klines[k].Low < leftPrice * 0.995m || klines[k].High > vertexPrice + 0.0001m)
                            {
                                validLeft = false;
                                break;
                            }
                        }
                        if (!validLeft) continue;

                        // 寻找右翼回落：自 vertexIdx 之后直至下一个明显转折或 maxIndex
                        int rightEndLimit = (i + 1 < swingPoints.Count) ? swingPoints[i + 1].Index : maxIndex;
                        rightEndLimit = Math.Min(rightEndLimit, maxIndex);

                        decimal lowestFall = vertexPrice;
                        int lowestIdx = vertexIdx;
                        int confirmedBar = -1;

                        for (int k = vertexIdx + 1; k <= rightEndLimit; k++)
                        {
                            if (klines[k].High > vertexPrice)
                            {
                                // 突破了峰顶，倒V顶被破坏
                                break;
                            }

                            if (klines[k].Low < lowestFall)
                            {
                                lowestFall = klines[k].Low;
                                lowestIdx = k;
                            }

                            decimal currentFallPct = lowestFall > 0m ? (vertexPrice - lowestFall) / lowestFall * 100m : 0m;
                            if (currentFallPct >= thresholdPct && confirmedBar < 0)
                            {
                                confirmedBar = k;
                            }
                        }

                        decimal fallPct = lowestFall > 0m ? (vertexPrice - lowestFall) / lowestFall * 100m : 0m;
                        if (confirmedBar >= 0 && fallPct >= thresholdPct)
                        {
                            bool isRightOpen = (i + 1 >= swingPoints.Count) && (lowestIdx == maxIndex || maxIndex - lowestIdx <= 2);

                            var item = new VPatternItem
                            {
                                Id = nextId++,
                                Type = VPatternType.InvertedVTop,
                                LeftIndex = leftIdx,
                                LeftPrice = leftPrice,
                                VertexIndex = vertexIdx,
                                VertexPrice = vertexPrice,
                                RightIndex = lowestIdx,
                                RightPrice = lowestFall,
                                ConfirmedBarIndex = confirmedBar,
                                LeftSpanPct = risePct,
                                RightSpanPct = fallPct,
                                IsRightWingOpen = isRightOpen
                            };
                            result.Add(item);
                        }
                    }
                }
            }

            // 3. 去重与精简：若相邻形态顶点极度接近 (例如柱距 < 3 且同类型)，保留振幅更大者
            if (result.Count > 1)
            {
                var filtered = new List<VPatternItem>();
                for (int i = 0; i < result.Count; i++)
                {
                    var item = result[i];
                    if (filtered.Count > 0)
                    {
                        var last = filtered[^1];
                        if (last.Type == item.Type && Math.Abs(last.VertexIndex - item.VertexIndex) < 5)
                        {
                            if (item.PriceDiffPct > last.PriceDiffPct)
                            {
                                filtered[^1] = item;
                            }
                            continue;
                        }
                    }
                    filtered.Add(item);
                }
                return filtered;
            }

            return result;
        }

        private struct SwingPoint
        {
            public int Index;
            public decimal Price;
            public bool IsPeak; // true = 高点峰顶, false = 低点谷底
        }

        private static List<SwingPoint> ExtractSwingPoints(IReadOnlyList<RawKline> klines, int maxIndex, decimal reversalStep)
        {
            var points = new List<SwingPoint>();
            if (klines.Count == 0 || maxIndex < 1) return points;

            int dir = 0; // +1 上涨中寻找高点, -1 下跌中寻找低点, 0 初始
            int extremeIdx = 0;
            decimal extremePrice = klines[0].Close;

            int initHighIdx = 0;
            decimal initHighPrice = klines[0].High;
            int initLowIdx = 0;
            decimal initLowPrice = klines[0].Low;

            // 初始趋势探测
            for (int i = 1; i <= maxIndex; i++)
            {
                decimal high = klines[i].High;
                decimal low = klines[i].Low;

                if (dir == 0)
                {
                    if (high > initHighPrice)
                    {
                        initHighPrice = high;
                        initHighIdx = i;
                    }
                    if (low < initLowPrice)
                    {
                        initLowPrice = low;
                        initLowIdx = i;
                    }

                    if (high > initLowPrice * (1m + reversalStep))
                    {
                        // 向上启动，起点为初始最低谷点
                        points.Add(new SwingPoint { Index = initLowIdx, Price = initLowPrice, IsPeak = false });
                        dir = 1;
                        extremeIdx = i;
                        extremePrice = high;
                    }
                    else if (low < initHighPrice * (1m - reversalStep))
                    {
                        // 向下启动，起点为初始最高峰点
                        points.Add(new SwingPoint { Index = initHighIdx, Price = initHighPrice, IsPeak = true });
                        dir = -1;
                        extremeIdx = i;
                        extremePrice = low;
                    }
                }
                else if (dir == 1) // 向上寻找更高点
                {
                    if (high >= extremePrice)
                    {
                        extremePrice = high;
                        extremeIdx = i;
                    }
                    else if (low <= extremePrice * (1m - reversalStep))
                    {
                        // 确认高点拐点
                        points.Add(new SwingPoint { Index = extremeIdx, Price = extremePrice, IsPeak = true });
                        dir = -1;
                        extremeIdx = i;
                        extremePrice = low;
                    }
                }
                else // dir == -1 向下寻找更低点
                {
                    if (low <= extremePrice)
                    {
                        extremePrice = low;
                        extremeIdx = i;
                    }
                    else if (high >= extremePrice * (1m + reversalStep))
                    {
                        // 确认低点拐点
                        points.Add(new SwingPoint { Index = extremeIdx, Price = extremePrice, IsPeak = false });
                        dir = 1;
                        extremeIdx = i;
                        extremePrice = high;
                    }
                }
            }

            // 加入尾部未完成的极值点
            if (points.Count == 0 || points[^1].Index != extremeIdx)
            {
                points.Add(new SwingPoint { Index = extremeIdx, Price = extremePrice, IsPeak = dir >= 0 });
            }

            return points;
        }
    }
}
