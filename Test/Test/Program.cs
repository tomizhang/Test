using Binance.Net.Enums;
using Common;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Test.Strategy;

namespace Test
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("===============================================================================");
            Console.WriteLine("    Binance ParquetDataReader - 真实交易模式时钟驱动输出 (Tick在周期内，跨周期K线收盘)   ");
            Console.WriteLine("===============================================================================");

            // 1. 设置指定的目标币种与时间窗口 (支持任意自定义日期区间，如 2025-01-01 到 2025-07-31)
            string coin = "BTCUSDT";
            DateTime startDate = new DateTime(2025, 1, 1);
            DateTime endDate = new DateTime(2025, 7, 31);
            KlineInterval klineInterval = KlineInterval.OneMinute;

            Console.WriteLine($"-> 数据根目录: {Config.GetRootPath()}");
            Console.WriteLine($"-> 目标交易对: {coin}");
            Console.WriteLine($"-> 时间窗口  : {startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd}");
            Console.WriteLine($"-> K线周期   : {klineInterval.ToIntervalString()}");

            // 2. 初始化读取器与趋势线策略实例
            using var dataReader = new ParquetDataReader();
            var strategy = new TrendLineStrategy(coin, klineInterval, maxKlines: 2000, minTrendLines: 1000);

            Console.WriteLine($"\n[1/3] 正在通过 DuckDB 原生文件下推与 3 线程有序滑动窗口并行加载数据 ({startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd})...");
            var sw = Stopwatch.StartNew();

            var (klineCount, tickCount) = await dataReader.LoadDateRangeAsync(coin, startDate, endDate, klineInterval, parallelDays: 3);

            sw.Stop();
            Console.WriteLine($"-> 加载完成，总耗时: {sw.ElapsedMilliseconds} ms");
            Console.WriteLine($"-> 内部统计指标: {dataReader.GetStatusSummary()}");

            // 3. 模拟真实交易/回测时钟驱动输出与策略推送
            Console.WriteLine("\n===============================================================================");
            Console.WriteLine($"[2/3] 开始模拟真实交易时序输出 (共 K线 {klineCount:N0} 根, Tick {tickCount:N0} 条) :");
            Console.WriteLine("-------------------------------------------------------------------------------");

            int currentKlineIndex = 0;
            int currentTickIndex = 0;
            RawKline? currentKline = null;

            // 取出第一根 K 线作为当前周期基准
            if (dataReader.TryDequeueKline(out RawKline firstKline))
            {
                currentKline = firstKline;
                currentKlineIndex++;
            }

            while (dataReader.TryDequeueTick(out RawTick tick))
            {
                // 若当前 Tick 的时间戳已经超出当前 K 线的收盘时间，说明当前周期已走完，必须先触发 K 线收盘输出并推送策略！
                while (currentKline.HasValue && tick.Time > currentKline.Value.CloseTime)
                {
                    // 1. 推送已完结周期的 K 线到策略 (触发滑动窗口更新与趋势线计算)
                    strategy.OnKline(currentKline.Value);

                    // 2. 输出已收盘 K 线信息
                    PrintClosedKline(currentKline.Value, currentKlineIndex, strategy);

                    // 3. 推进到下一个 K 线周期
                    if (dataReader.TryDequeueKline(out RawKline nextKline))
                    {
                        currentKline = nextKline;
                        currentKlineIndex++;
                    }
                    else
                    {
                        currentKline = null;
                    }
                }

                // 推送逐笔 Tick 到策略
                strategy.OnTick(tick);

                // 在当前大周期内输出 Tick 逐笔价格与成交数据
                currentTickIndex++;
                PrintTick(tick, currentTickIndex);
            }

            // 所有 Tick 消费完毕后，收盘输出剩余的 K 线周期并推送策略
            while (currentKline.HasValue)
            {
                strategy.OnKline(currentKline.Value);
                PrintClosedKline(currentKline.Value, currentKlineIndex, strategy);

                if (dataReader.TryDequeueKline(out RawKline nextKline))
                {
                    currentKline = nextKline;
                    currentKlineIndex++;
                }
                else
                {
                    currentKline = null;
                }
            }

            Console.WriteLine("===============================================================================");
            Console.WriteLine($"[完成] 真实交易模拟输出完毕！已输出 Tick: {currentTickIndex:N0} 条，已收盘 K线: {currentKlineIndex:N0} 根。");
            Console.WriteLine($"-> 策略最终状态: {strategy.GetStrategySummary()}");

            // 4. 使用 ScottPlot 绘制分析图表并落地保存
            Console.WriteLine("\n[3/3] 正在使用 ScottPlot 渲染价格折线图、高低点与趋势线结构图...");
            string desc = $"币种: {coin}, 周期: {klineInterval.ToIntervalString()}, 时间窗口: {startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd}\n" +
                          $"策略模式: 三层增量计算流水线 + OnTick 实时穿透删除 (已删除: {strategy.DeletedTrendLinesCount}条)";
            string chartPath = strategy.PlotChart(desc);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"-> 【图表绘制完成】分析图片已落地保存至:\n   {chartPath}\n");
            Console.ResetColor();
            Console.WriteLine("===============================================================================");
        }

        /// <summary>
        /// 格式化输出周期内 Tick 逐笔成交信息
        /// </summary>
        private static void PrintTick(RawTick tick, int index)
        {
            DateTime tickTime = DateTimeOffset.FromUnixTimeMilliseconds(tick.Time).LocalDateTime;
            string side = tick.IsBuyerMaker ? "SELL(卖方吃单)" : "BUY (买方吃单)";

            Console.WriteLine($"  [Tick #{index:D6}] 时间: {tickTime:HH:mm:ss.fff} | 方向: {side} | " +
                              $"价格: {tick.Price,10:F2} | 数量: {tick.Qty,8:F4} | 金额: {tick.QuoteQty,12:F2} | ID: {tick.TradeId}");
        }

        /// <summary>
        /// 格式化高亮输出已收盘的周期 K 线汇总信息及策略计算概况
        /// </summary>
        private static void PrintClosedKline(RawKline kline, int index, TrendLineStrategy strategy)
        {
            DateTime openTime = DateTimeOffset.FromUnixTimeMilliseconds(kline.OpenTime).LocalDateTime;
            DateTime closeTime = DateTimeOffset.FromUnixTimeMilliseconds(kline.CloseTime).LocalDateTime;

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n>>> 【K线周期收盘 #{index:D4}】 周期: {openTime:yyyy-MM-dd HH:mm:ss} ~ {closeTime:HH:mm:ss} | " +
                              $"开: {kline.Open,10:F2} | 高: {kline.High,10:F2} | 低: {kline.Low,10:F2} | 收: {kline.Close,10:F2} | " +
                              $"成交量: {kline.Volume,10:F4} | 成交额: {kline.QuoteVolume,12:F2} | 笔数: {kline.TradeCount,6}");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"    └── 策略状态 (增量模式): 滑动窗口K线={strategy.KlineCount}根 | 累计波峰={strategy.Peaks.Count}个, 累计波谷={strategy.Valleys.Count}个 | " +
                              $"活跃阻力线={strategy.ActiveResistanceLines.Count}条, 活跃支撑线={strategy.ActiveSupportLines.Count}条 | 已穿透删除={strategy.DeletedTrendLinesCount}条 | 历史库累计={strategy.HistoricalTrendLinesCount}条\n");
            Console.ResetColor();
        }
    }
}
