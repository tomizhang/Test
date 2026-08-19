using Binance.Net.Enums;
using Common;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Test
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("===============================================================================");
            Console.WriteLine("        Binance ParquetDataReader 高性能真实数据读取器 (DuckDB 引擎)          ");
            Console.WriteLine("===============================================================================");

            // 配置要读取的交易对和回测时间范围 (根据本地下载的数据目录实际情况调整)
            string coin = "BTCUSDT";
            DateTime startDate = new DateTime(2025, 1, 1);
            DateTime endDate = new DateTime(2025, 3, 31);

            Console.WriteLine($"-> 数据根目录: {Config.GetRootPath()}");
            Console.WriteLine($"-> 目标交易对: {coin}");
            Console.WriteLine($"-> 读取日期范围: {startDate:yyyy-MM-dd} 至 {endDate:yyyy-MM-dd}");

            // 1. 初始化 ParquetDataReader 帮助类
            using var dataReader = new ParquetDataReader();

            // 演示 1: 单日主动加载 (适合回测引擎单步推进)
            Console.WriteLine("\n--- 演示 1: 单日主动读取 (LoadDayAsync: 2025-01-01) ---");
            var sw = Stopwatch.StartNew();
            var (klineCount, tickCount) = await dataReader.LoadDayAsync(coin, startDate, KlineInterval.OneMinute);
            sw.Stop();
            Console.WriteLine($"-> 单日加载耗时: {sw.ElapsedMilliseconds} ms");
            Console.WriteLine($"-> 2025-01-01 加载结果: K线 {klineCount:N0} 根, Tick {tickCount:N0} 条");
            Console.WriteLine($"-> 当前统计: {dataReader.GetStatusSummary()}");

            // 清空队列以进入流式预取演示
            dataReader.Clear();

            // 演示 2: 3 线程有序滑动窗口流式并发预取 (StartTickStreamingAsync + StartKlineStreamingAsync)
            Console.WriteLine($"\n--- 演示 2: 3 线程有序滑动窗口流式预取 ({startDate:yyyy-MM-dd} 至 {endDate:yyyy-MM-dd}) ---");
            using var cts = new CancellationTokenSource();

            // 后台异步加载 K 线
            var klineTask = Task.Run(() => 
                dataReader.StartKlineStreamingAsync(coin, startDate, endDate, KlineInterval.OneMinute, cts.Token)
            );

            // 后台 3 线程有序滑动窗口预取 Tick (3 线程并行读取，内存暂存保序，严格按日时序入队)
            var tickTask = Task.Run(() => 
                dataReader.StartTickStreamingAsync(coin, startDate, endDate, parallelDays: 3, maxBufferedDays: 3, cts.Token)
            );

            // 2. 消费端：实时消费双队列并严格校验时间戳时序单调性
            Console.WriteLine("\n[消费端启动] 正在实时消费双队列并校验时间戳时序...");

            long consumedTicks = 0;
            long consumedKlines = 0;
            long lastTickTime = 0;
            long lastKlineTime = 0;
            bool isOrderValid = true;
            RawTick[] tickBuffer = new RawTick[4096];

            while (!tickTask.IsCompleted || !klineTask.IsCompleted || dataReader.TickQueueCount > 0 || dataReader.KlineQueueCount > 0)
            {
                // 极速批量消费 Tick 队列
                int readCount;
                while ((readCount = dataReader.DequeueTickBatch(tickBuffer)) > 0)
                {
                    for (int i = 0; i < readCount; i++)
                    {
                        var tick = tickBuffer[i];
                        if (tick.Time < lastTickTime)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"[严重错误] Tick 发生时间倒流! 当前: {tick.Time}, 上一条: {lastTickTime}");
                            Console.ResetColor();
                            isOrderValid = false;
                        }
                        lastTickTime = tick.Time;
                        consumedTicks++;
                    }
                }

                // 消费 K 线队列
                while (dataReader.TryDequeueKline(out RawKline kline))
                {
                    if (kline.OpenTime < lastKlineTime)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"[严重错误] K线 发生时间倒流! 当前: {kline.OpenTime}, 上一条: {lastKlineTime}");
                        Console.ResetColor();
                        isOrderValid = false;
                    }
                    lastKlineTime = kline.OpenTime;
                    consumedKlines++;
                }

                await Task.Delay(5);
            }

            await Task.WhenAll(klineTask, tickTask);

            // 3. 统计与测试结果汇报
            Console.WriteLine("\n[测试完成] 统计报告与时序验证结果如下：");
            Console.WriteLine("-------------------------------------------------------------------------------");
            Console.WriteLine(dataReader.GetStatusSummary());
            Console.WriteLine($"-> 消费端已校验 Tick 总数: {consumedTicks:N0} 条");
            Console.WriteLine($"-> 消费端已校验 K线 总数: {consumedKlines:N0} 根");
            
            if (isOrderValid)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("-> [验证通过] 3 线程并发预取下，所有 Tick 和 K 线的时间戳 100% 严格单调递增，零乱序！");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("-> [验证失败] 检测到时间戳乱序！");
                Console.ResetColor();
            }

            Console.WriteLine("===============================================================================");
        }
    }
}
