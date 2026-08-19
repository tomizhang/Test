using Binance.Net.Enums;
using Common;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Test
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("===============================================================================");
            Console.WriteLine("    Binance ParquetDataReader - 真实交易模式时钟驱动输出 (Tick在周期内，跨周期K线收盘)   ");
            Console.WriteLine("===============================================================================");

            // 1. 设置指定的目标币种与日期
            string coin = "BTCUSDT";
            DateTime targetDate = new DateTime(2025, 1, 1);
            KlineInterval klineInterval = KlineInterval.OneMinute;

            Console.WriteLine($"-> 数据根目录: {Config.GetRootPath()}");
            Console.WriteLine($"-> 目标交易对: {coin}");
            Console.WriteLine($"-> 目标日期  : {targetDate:yyyy-MM-dd}");
            Console.WriteLine($"-> K线周期   : {klineInterval.ToIntervalString()}");

            // 2. 初始化读取器并加载指定日期的 K线 和 Tick 数据
            using var dataReader = new ParquetDataReader();

            Console.WriteLine("\n[1/2] 正在通过 DuckDB 原生文件下推读取数据中...");
            var sw = Stopwatch.StartNew();

            var (klineCount, tickCount) = await dataReader.LoadDayAsync(coin, targetDate, klineInterval);

            sw.Stop();
            Console.WriteLine($"-> 加载完成，总耗时: {sw.ElapsedMilliseconds} ms");
            Console.WriteLine($"-> 内部统计指标: {dataReader.GetStatusSummary()}");

            // 3. 模拟真实交易/回测时钟驱动输出
            // 规则：Tick 在大周期内持续输出；当时间跨入下一个周期时，上一周期K线收盘并输出该周期K线汇总信息
            Console.WriteLine("\n===============================================================================");
            Console.WriteLine($"[2/2] 开始模拟真实交易时序输出 (共 K线 {klineCount:N0} 根, Tick {tickCount:N0} 条) :");
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
                // 若当前 Tick 的时间戳已经超出当前 K 线的收盘时间，说明当前周期已走完，必须先触发 K 线收盘输出！
                while (currentKline.HasValue && tick.Time > currentKline.Value.CloseTime)
                {
                    // 输出已完结周期的 K 线信息
                    PrintClosedKline(currentKline.Value, currentKlineIndex);

                    // 推进到下一个 K 线周期
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

                // 在当前大周期内输出 Tick 逐笔价格与成交数据
                currentTickIndex++;
                PrintTick(tick, currentTickIndex);
            }

            // 所有 Tick 消费完毕后，收盘输出剩余的 K 线周期
            while (currentKline.HasValue)
            {
                PrintClosedKline(currentKline.Value, currentKlineIndex);
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
        /// 格式化高亮输出已收盘的周期 K 线汇总信息
        /// </summary>
        private static void PrintClosedKline(RawKline kline, int index)
        {
            DateTime openTime = DateTimeOffset.FromUnixTimeMilliseconds(kline.OpenTime).LocalDateTime;
            DateTime closeTime = DateTimeOffset.FromUnixTimeMilliseconds(kline.CloseTime).LocalDateTime;

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n>>> 【K线周期收盘 #{index:D4}】 周期: {openTime:yyyy-MM-dd HH:mm:ss} ~ {closeTime:HH:mm:ss} | " +
                              $"开: {kline.Open,10:F2} | 高: {kline.High,10:F2} | 低: {kline.Low,10:F2} | 收: {kline.Close,10:F2} | " +
                              $"成交量: {kline.Volume,10:F4} | 成交额: {kline.QuoteVolume,12:F2} | 笔数: {kline.TradeCount,6}\n");
            Console.ResetColor();
        }
    }
}
