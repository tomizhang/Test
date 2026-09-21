using Common;
using System;
using Test.ChannelPlayback.WinForms.Models;

namespace Test.ChannelPlayback.WinForms.Engine
{
    /// <summary>
    /// 18 种 K 线量化形态高精度分类与语义识别引擎
    /// 严格遵循公式基准与三分类量化逻辑 (饱满短影线、长上影线、长下影线)
    /// </summary>
    public static class CandlestickPatternClassifier
    {
        public const decimal DefaultPLong = 1.0m;    // 长实体阈值 (大K线 >= 1.0%)
        public const decimal DefaultPMedium = 0.55m; // 中实体阈值 (0.35% <= 中实体 < 1.0%)
        public const decimal DefaultPShort = 0.35m;  // 短实体阈值 (< 0.35% 为小K线)

        /// <summary>
        /// 对任意单根 K 线进行 18 种形态量化分类
        /// </summary>
        public static CandlestickPatternResult Classify(
            RawKline kline,
            decimal pLong = DefaultPLong,
            decimal pMedium = DefaultPMedium,
            decimal pShort = DefaultPShort)
        {
            return Classify(kline.Open, kline.High, kline.Low, kline.Close, pLong, pMedium, pShort);
        }

        /// <summary>
        /// 对指定 OHLC 四价进行 18 种形态量化分类
        /// </summary>
        public static CandlestickPatternResult Classify(
            decimal open,
            decimal high,
            decimal low,
            decimal close,
            decimal pLong = DefaultPLong,
            decimal pMedium = DefaultPMedium,
            decimal pShort = DefaultPShort)
        {
            var res = new CandlestickPatternResult();
            if (open <= 0m || high < low) return res;

            decimal totalRange = high - low;
            decimal body = Math.Abs(close - open);
            decimal upperShadow = high - Math.Max(open, close);
            decimal lowerShadow = Math.Min(open, close) - low;

            res.TotalRange = totalRange;
            res.Body = body;
            res.UpperShadow = upperShadow;
            res.LowerShadow = lowerShadow;
            res.IsBullish = close > open;

            if (totalRange <= 0m)
            {
                res.PatternName = "无波动水平线 (TotalRange=0)";
                return res;
            }

            decimal bodyPct = (body / open) * 100m;
            decimal bodyRangeRatio = body / totalRange;
            decimal upperShadowRangeRatio = upperShadow / totalRange;
            decimal lowerShadowRangeRatio = lowerShadow / totalRange;

            res.BodyPct = bodyPct;
            res.BodyRangeRatio = bodyRangeRatio;
            res.UpperShadowRangeRatio = upperShadowRangeRatio;
            res.LowerShadowRangeRatio = lowerShadowRangeRatio;

            bool isUp = close > open;
            bool isDown = close < open;

            // =========================================================================
            // 一、 K线状态饱满影线很短
            // 条件：body / total_range >= 0.70 且 upper_shadow/range <= 0.15 且 lower_shadow/range <= 0.15
            // =========================================================================
            if (bodyRangeRatio >= 0.70m && upperShadowRangeRatio <= 0.15m && lowerShadowRangeRatio <= 0.15m)
            {
                res.Category = CandlestickCategory.FullBodyShortShadow;
                res.CategoryName = "K线状态饱满影线很短";

                if (bodyPct >= pLong && isUp)
                {
                    res.PatternType = CandlestickPatternType.FullBodyLongBullish;
                    res.PatternName = "实体长 + 上涨 (大阳线 / 巨阳破位)";
                    res.SemanticLogic = "多头主导的极强动能爆发。主动市价买盘以摧枯拉朽之势扫空上方挂单，空方全线撤退或被动爆仓止损，全周期无获利回吐，单边定势。";
                    res.SuggestedDirection = OrderSignalDirection.Buy;
                    return res;
                }
                if (bodyPct >= pLong && isDown)
                {
                    res.PatternType = CandlestickPatternType.FullBodyLongBearish;
                    res.PatternName = "实体长 + 下跌 (大阴线 / 巨阴杀跌)";
                    res.SemanticLogic = "空头主导的极度恐慌溃败。主动抛压连续击穿买盘深度，多方接盘意愿归零，形成流动性真空与多头连环强平践踏，收盘钉死在最底部。";
                    res.SuggestedDirection = OrderSignalDirection.Sell;
                    return res;
                }
                if (bodyPct >= pMedium && bodyPct < pLong && isUp)
                {
                    res.PatternType = CandlestickPatternType.FullBodyMediumBullish;
                    res.PatternName = "实体中 + 上涨 (中阳线 / 稳步推升)";
                    res.SemanticLogic = "健康的可持续单边推进。买方有序吃单建仓，日内无明显阻力与反扑，多空换手平稳，属于最健康的趋势中继形态。";
                    res.SuggestedDirection = OrderSignalDirection.Buy;
                    return res;
                }
                if (bodyPct >= pMedium && bodyPct < pLong && isDown)
                {
                    res.PatternType = CandlestickPatternType.FullBodyMediumBearish;
                    res.PatternName = "实体中 + 下跌 (中阴线 / 稳步阴跌)";
                    res.SemanticLogic = "持续单向出货与打压。买盘承接偏弱，空头按节奏抛售，未触发极端踩踏但也无多头有效抵抗，下行趋势保持良好。";
                    res.SuggestedDirection = OrderSignalDirection.Sell;
                    return res;
                }
                if (bodyPct < pShort && isUp)
                {
                    res.PatternType = CandlestickPatternType.FullBodyShortBullish;
                    res.PatternName = "实体短 + 上涨 (微幅寸劲阳 / 窄幅破位阳)";
                    res.SemanticLogic = "低波动率下的微弱买盘占优。通常出现在极致收敛末端或节假日低流动性阶段，代表波动率极度压缩，变盘在即。";
                    return res;
                }
                if (bodyPct < pShort && isDown)
                {
                    res.PatternType = CandlestickPatternType.FullBodyShortBearish;
                    res.PatternName = "实体短 + 下跌 (微幅寸劲阴 / 窄幅阴跌)";
                    res.SemanticLogic = "低波动率下的微弱卖盘占优。虽无大抛压，但买方完全不挂单护盘，价格因微量成交滑落，同样为变盘前兆。";
                    return res;
                }
            }

            // =========================================================================
            // 二、 上影线很长 (upper_shadow / total_range >= 0.40，高位强供给/价格拒绝)
            // =========================================================================
            bool checkUpperFirst = upperShadowRangeRatio >= 0.40m && upperShadowRangeRatio >= lowerShadowRangeRatio;
            if (checkUpperFirst)
            {
                res.Category = CandlestickCategory.LongUpperShadow;
                res.CategoryName = "上影线很长 (高位强供给/价格拒绝)";

                if (bodyPct >= pLong && upperShadow >= 0.8m * body && isUp)
                {
                    res.PatternType = CandlestickPatternType.UpperShadowLongBullish;
                    res.PatternName = "实体长 + 上涨 (巨幅放量滞涨阳 / 冲高遭遇沉重阻击)";
                    res.SemanticLogic = "日内多空激烈交火。多头动用巨资发动波澜壮阔的进攻（实体仍长），但在高位撞上机构天量被动限价卖单（冰山挂单），攻势受阻被强力压回，后续买力面临枯竭。";
                    res.SuggestedDirection = OrderSignalDirection.Sell;
                    return res;
                }
                if (bodyPct >= pLong && upperShadow >= 0.6m * body && isDown)
                {
                    res.PatternType = CandlestickPatternType.UpperShadowLongBearish;
                    res.PatternName = "实体长 + 下跌 (冲高反杀断头阴 / 假突破全面崩盘)";
                    res.SemanticLogic = "极致的诱多杀多（Bull Trap）。开盘强力拉升创出新高，诱发追多盘与空头止损，随后主力以天量市价单倾泻砸盘，不仅吞没全部涨幅，还深砸击穿开盘价，多头全线被套。";
                    res.SuggestedDirection = OrderSignalDirection.Sell;
                    return res;
                }
                if (bodyPct >= pMedium && bodyPct < pLong && upperShadow >= 1.0m * body && isUp)
                {
                    res.PatternType = CandlestickPatternType.UpperShadowMediumBullish;
                    res.PatternName = "实体中 + 上涨 (冲高回落中阳 / 阻力探测线)";
                    res.SemanticLogic = "多头试盘遭遇阶段性解套盘抛压。多方虽保住胜果，但冲高回落表明上方筹码密集，跟风买力不足，多方主动撤退重新蓄力。";
                    return res;
                }
                if (bodyPct >= pMedium && bodyPct < pLong && upperShadow >= 1.0m * body && isDown)
                {
                    res.PatternType = CandlestickPatternType.UpperShadowMediumBearish;
                    res.PatternName = "实体中 + 下跌 (常规冲高杀跌阴 / 射击之星中阴)";
                    res.SemanticLogic = "空头占优的高位防御与反扑。高位买盘断层，前期做多资金止盈叠加做空资金介入，价格翻绿下行，为空头反转信号。";
                    res.SuggestedDirection = OrderSignalDirection.Sell;
                    return res;
                }
                if (bodyPct < pShort && upperShadowRangeRatio >= 0.60m && isUp)
                {
                    res.PatternType = CandlestickPatternType.UpperShadowShortBullish;
                    res.PatternName = "实体短 + 上涨 (倒锤子阳 / 墓碑倒锤阳)";
                    res.SemanticLogic = "多头进攻努力几乎被完全抹平，仅守住毫厘微利。高位遭绝对拒绝；若出现在深度下跌末端，常为主力低位试盘、探测浮筹厚度（仙人指路）。";
                    res.SuggestedDirection = OrderSignalDirection.Buy;
                    return res;
                }
                if (bodyPct < pShort && upperShadowRangeRatio >= 0.60m && isDown)
                {
                    res.PatternType = CandlestickPatternType.UpperShadowShortBearish;
                    res.PatternName = "实体短 + 下跌 (标准流星线 Shooting Star / 墓碑十字阴)";
                    res.SemanticLogic = "绝对的价格拒绝（Absolute Rejection）。盘中冲高追入者全军覆没，收盘钉死在最底端，多头买力彻底枯竭，极其明确的短线见顶信号。";
                    res.SuggestedDirection = OrderSignalDirection.Sell;
                    return res;
                }
            }

            // =========================================================================
            // 三、 下影线很长 (lower_shadow / total_range >= 0.40，低位强需求/流动性吸收)
            // =========================================================================
            if (lowerShadowRangeRatio >= 0.40m)
            {
                res.Category = CandlestickCategory.LongLowerShadow;
                res.CategoryName = "下影线很长 (低位强需求/流动性吸收)";

                if (bodyPct >= pLong && lowerShadow >= 0.6m * body && isUp)
                {
                    res.PatternType = CandlestickPatternType.LowerShadowLongBullish;
                    res.PatternName = "实体长 + 上涨 (深水反包巨阳 / 惊天 V 转)";
                    res.SemanticLogic = "极端暴烈的洗盘反转。早盘恐慌暴跌下探，在低位遭遇大资金全量吸收吞噬（Absorption），随后以狂暴买盘逼空拉升，空头发生毁灭性踩踏，多头极度强势。";
                    res.SuggestedDirection = OrderSignalDirection.Buy;
                    return res;
                }
                if (bodyPct >= pLong && lowerShadow >= 0.8m * body && isDown)
                {
                    res.PatternType = CandlestickPatternType.LowerShadowLongBearish;
                    res.PatternName = "实体长 + 下跌 (宽幅杀跌抵抗阴 / 深水反弹未果阴)";
                    res.SemanticLogic = "空方势能极度狂暴。空头极力砸盘创出极深低点，虽在深水区触发了左侧抄底盘与空头止盈，但空方抛压过于沉重，多头承接力被大幅消耗，仍收出大实体阴线。";
                    res.SuggestedDirection = OrderSignalDirection.Sell;
                    return res;
                }
                if (bodyPct >= pMedium && bodyPct < pLong && lowerShadow >= 1.0m * body && isUp)
                {
                    res.PatternType = CandlestickPatternType.LowerShadowMediumBullish;
                    res.PatternName = "实体中 + 上涨 (探底回升中阳 / 稳固托底线)";
                    res.SemanticLogic = "低位支撑扎实明确。下跌触及强支撑区后，买方稳健进场托底并反推价格翻阳，空头抛售动能被有效瓦解。";
                    res.SuggestedDirection = OrderSignalDirection.Buy;
                    return res;
                }
                if (bodyPct >= pMedium && bodyPct < pLong && lowerShadow >= 1.0m * body && isDown)
                {
                    res.PatternType = CandlestickPatternType.LowerShadowMediumBearish;
                    res.PatternName = "实体中 + 下跌 (抵抗下跌中阴 / 减速探底阴)";
                    res.SemanticLogic = "空方进攻踢到铁板。空头无法将价格按死在最低点，低位承接盘浮现，下行动能初现衰竭，市场步入多空相持。";
                    res.SuggestedDirection = OrderSignalDirection.Buy;
                    return res;
                }
                if (bodyPct < pShort && lowerShadowRangeRatio >= 0.60m && isUp)
                {
                    res.PatternType = CandlestickPatternType.LowerShadowShortBullish;
                    res.PatternName = "实体短 + 上涨 (经典锤子阳 Hammer / 蜻蜓阳线)";
                    res.SemanticLogic = "假跌破与流动性扫荡（Spring / Liquidity Sweep）。低位止损与诱空筹码被大资金一次性吃干抹净并强势拉回开盘价之上，高胜率底部反转形态。";
                    res.SuggestedDirection = OrderSignalDirection.Buy;
                    return res;
                }
                if (bodyPct < pShort && lowerShadowRangeRatio >= 0.60m && isDown)
                {
                    res.PatternType = CandlestickPatternType.LowerShadowShortBearish;
                    res.PatternName = "实体短 + 下跌 (吊颈线 Hanging Man / 蜻蜓阴线)";
                    res.SemanticLogic = "低位出现：虽收微阴，但长下影表明空方砸盘被全盘接下，属于空头动能枯竭的筑底信号；高位出现（吊颈线）：警惕形态。说明多头防线在盘中曾被深度击穿，多头护盘已显吃力，筹码结构出现松动裂痕。";
                    // 默认方向：低位筑底为做多，高位吊颈为做空
                    res.SuggestedDirection = OrderSignalDirection.None;
                    return res;
                }
            }

            // 若尚未命中，但 upperShadowRangeRatio >= 0.40m 且此前未处理（例如下影线相等时）
            if (upperShadowRangeRatio >= 0.40m)
            {
                res.Category = CandlestickCategory.LongUpperShadow;
                res.CategoryName = "上影线很长 (高位强供给/价格拒绝)";

                if (bodyPct >= pLong && upperShadow >= 0.8m * body && isUp)
                {
                    res.PatternType = CandlestickPatternType.UpperShadowLongBullish;
                    res.PatternName = "实体长 + 上涨 (巨幅放量滞涨阳 / 冲高遭遇沉重阻击)";
                    res.SemanticLogic = "日内多空激烈交火。多头动用巨资发动波澜壮阔的进攻（实体仍长），但在高位撞上机构天量被动限价卖单（冰山挂单），攻势受阻被强力压回，后续买力面临枯竭。";
                    res.SuggestedDirection = OrderSignalDirection.Sell;
                    return res;
                }
                if (bodyPct >= pLong && upperShadow >= 0.6m * body && isDown)
                {
                    res.PatternType = CandlestickPatternType.UpperShadowLongBearish;
                    res.PatternName = "实体长 + 下跌 (冲高反杀断头阴 / 假突破全面崩盘)";
                    res.SemanticLogic = "极致的诱多杀多（Bull Trap）。开盘强力拉升创出新高，诱发追多盘与空头止损，随后主力以天量市价单倾泻砸盘，不仅吞没全部涨幅，还深砸击穿开盘价，多头全线被套。";
                    res.SuggestedDirection = OrderSignalDirection.Sell;
                    return res;
                }
                if (bodyPct >= pMedium && bodyPct < pLong && upperShadow >= 1.0m * body && isUp)
                {
                    res.PatternType = CandlestickPatternType.UpperShadowMediumBullish;
                    res.PatternName = "实体中 + 上涨 (冲高回落中阳 / 阻力探测线)";
                    res.SemanticLogic = "多头试盘遭遇阶段性解套盘抛压。多方虽保住胜果，但冲高回落表明上方筹码密集，跟风买力不足，多方主动撤退重新蓄力。";
                    res.SuggestedDirection = OrderSignalDirection.Sell;
                    return res;
                }
                if (bodyPct >= pMedium && bodyPct < pLong && upperShadow >= 1.0m * body && isDown)
                {
                    res.PatternType = CandlestickPatternType.UpperShadowMediumBearish;
                    res.PatternName = "实体中 + 下跌 (常规冲高杀跌阴 / 射击之星中阴)";
                    res.SemanticLogic = "空头占优的高位防御与反扑。高位买盘断层，前期做多资金止盈叠加做空资金介入，价格翻绿下行，为空头反转信号。";
                    res.SuggestedDirection = OrderSignalDirection.Sell;
                    return res;
                }
                if (bodyPct < pShort && upperShadowRangeRatio >= 0.60m && isUp)
                {
                    res.PatternType = CandlestickPatternType.UpperShadowShortBullish;
                    res.PatternName = "实体短 + 上涨 (倒锤子阳 / 墓碑倒锤阳)";
                    res.SemanticLogic = "多头进攻努力几乎被完全抹平，仅守住毫厘微利。高位遭绝对拒绝；若出现在深度下跌末端，常为主力低位试盘、探测浮筹厚度（仙人指路）。";
                    res.SuggestedDirection = OrderSignalDirection.Buy;
                    return res;
                }
                if (bodyPct < pShort && upperShadowRangeRatio >= 0.60m && isDown)
                {
                    res.PatternType = CandlestickPatternType.UpperShadowShortBearish;
                    res.PatternName = "实体短 + 下跌 (标准流星线 Shooting Star / 墓碑十字阴)";
                    res.SemanticLogic = "绝对的价格拒绝（Absolute Rejection）。盘中冲高追入者全军覆没，收盘钉死在最底端，多头买力彻底枯竭，极其明确的短线见顶信号。";
                    res.SuggestedDirection = OrderSignalDirection.Sell;
                    return res;
                }
            }

            res.PatternName = isUp ? "常规普通阳线" : (isDown ? "常规普通阴线" : "十字平盘线");
            res.CategoryName = "未达到严格影线/实体分类门槛";
            return res;
        }

        /// <summary>
        /// 判定当前形态是否符合前序趋势的反转做单信号
        /// </summary>
        /// <param name="priorTrend">前序大周期连续趋势 (Bullish 连涨 / Bearish 连跌)</param>
        /// <param name="pattern">分类识别结果</param>
        /// <param name="direction">输出做单方向</param>
        /// <returns>是否满足反转做单</returns>
        public static bool IsReversalOrderSignal(
            ConsecutiveTrendType priorTrend,
            CandlestickPatternResult pattern,
            out OrderSignalDirection direction)
        {
            direction = OrderSignalDirection.None;
            if (pattern == null || pattern.PatternType == CandlestickPatternType.None) return false;

            if (priorTrend == ConsecutiveTrendType.Bullish)
            {
                // 前序为连续上涨 (5根以上)，反转方向为：观察达到高点后做空 (Sell)
                // 必须是在高点受到空方强阻力压制（具备显著上影线或滞涨拒收特征），
                // 排除无上影线顺势砸盘的大阴线/中阴线(避免在低点追空导致止损过大无利润)
                switch (pattern.PatternType)
                {
                    case CandlestickPatternType.UpperShadowLongBearish:   // [8] 冲高反杀断头阴 / 假突破全面崩盘
                    case CandlestickPatternType.UpperShadowMediumBearish: // [10] 常规冲高杀跌阴 / 射击之星中阴
                    case CandlestickPatternType.UpperShadowShortBearish:  // [12] 标准流星线 Shooting Star / 墓碑十字阴
                    case CandlestickPatternType.UpperShadowLongBullish:   // [7] 巨幅放量滞涨阳 (高位买力耗尽)
                    case CandlestickPatternType.UpperShadowMediumBullish: // [9] 冲高回落中阳 (阻力探测线)
                    case CandlestickPatternType.LowerShadowShortBearish:  // [18] 高位吊颈线 (高位防线被深度击穿)
                        direction = OrderSignalDirection.Sell;
                        pattern.IsReversalSignal = true;
                        pattern.SuggestedDirection = OrderSignalDirection.Sell;
                        return true;

                    default:
                        return false;
                }
            }
            else if (priorTrend == ConsecutiveTrendType.Bearish)
            {
                // 前序为连续下跌 (5根以上)，反转方向为：观察达到低点后做多 (Buy)
                // 必须是在低位受到多方强支撑托底（具备显著下影线或探底回升特征），
                // 排除无下影线顺势暴拉的大阳线/中阳线(避免在高点追多导致止损过大无利润)
                switch (pattern.PatternType)
                {
                    case CandlestickPatternType.LowerShadowLongBullish:   // [13] 深水反包巨阳 / 惊天 V 转
                    case CandlestickPatternType.LowerShadowMediumBullish: // [15] 探底回升中阳 / 稳固托底线
                    case CandlestickPatternType.LowerShadowShortBullish:  // [17] 经典锤子阳 Hammer / 蜻蜓阳线
                    case CandlestickPatternType.LowerShadowLongBearish:   // [14] 宽幅杀跌抵抗阴 / 深水承接
                    case CandlestickPatternType.LowerShadowMediumBearish: // [16] 抵抗下跌中阴 / 减速探底阴
                    case CandlestickPatternType.LowerShadowShortBearish:  // [18] 低位蜻蜓阴线 (空方砸盘被全盘接下筑底)
                    case CandlestickPatternType.UpperShadowShortBullish:  // [11] 倒锤子阳 / 仙人指路 (低位试盘)
                        direction = OrderSignalDirection.Buy;
                        pattern.IsReversalSignal = true;
                        pattern.SuggestedDirection = OrderSignalDirection.Buy;
                        return true;

                    default:
                        return false;
                }
            }

            return false;
        }
    }
}
