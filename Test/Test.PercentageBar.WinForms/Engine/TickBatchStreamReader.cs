using Common;
using DuckDB.NET.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Test.PercentageBar.WinForms.Engine
{
    /// <summary>
    /// 单批次/单天 Tick 数据包
    /// </summary>
    public readonly struct TickBatchResult
    {
        public DateTime Date { get; init; }
        public string FilePath { get; init; }
        public RawTick[] Ticks { get; init; }
        public int DayIndex { get; init; }
        public int TotalDays { get; init; }
        public long ReadElapsedMs { get; init; }
    }

    /// <summary>
    /// 高性能分批次流式 Tick 数据读取器 (按天分批流式加载, 零内存堆积, 极速响应)
    /// </summary>
    public static class TickBatchStreamReader
    {
        /// <summary>
        /// 异步流式逐天读取指定币种与日期范围内的 Tick 数据
        /// </summary>
        public static async IAsyncEnumerable<TickBatchResult> StreamDayBatchesAsync(
            string coin,
            DateTime startDate,
            DateTime endDate,
            [EnumeratorCancellation] CancellationToken ct = default,
            Action<string>? logCallback = null)
        {
            if (startDate > endDate) yield break;

            List<DateTime> allDates = new List<DateTime>();
            for (DateTime d = startDate.Date; d <= endDate.Date; d = d.AddDays(1))
            {
                allDates.Add(d);
            }

            int totalDays = allDates.Count;

            for (int i = 0; i < totalDays; i++)
            {
                if (ct.IsCancellationRequested) break;

                DateTime currDate = allDates[i];
                string filePath = ResolveTickFilePath(coin, currDate);

                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                {
                    logCallback?.Invoke($"[分批读取] {currDate:yyyy-MM-dd} 未找到 Parquet Tick 文件，跳过。");
                    continue;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var ticks = await Task.Run(() => ReadSingleTickFile(filePath, ct), ct).ConfigureAwait(false);
                sw.Stop();

                yield return new TickBatchResult
                {
                    Date = currDate,
                    FilePath = filePath,
                    Ticks = ticks,
                    DayIndex = i + 1,
                    TotalDays = totalDays,
                    ReadElapsedMs = sw.ElapsedMilliseconds
                };
            }
        }

        /// <summary>
        /// 解析单日 Tick Parquet 文件路径 (支持标准路径与递归回退检索)
        /// </summary>
        public static string ResolveTickFilePath(string coin, DateTime date)
        {
            string stdPath = Config.GetTradeFilePath(coin, date, ".parquet");
            if (File.Exists(stdPath)) return stdPath;

            string fallbackDir = Config.GetTradeDataPath(coin);
            if (Directory.Exists(fallbackDir))
            {
                var files = Directory.GetFiles(fallbackDir, $"*{date:yyyy-MM-dd}*.parquet", SearchOption.AllDirectories);
                if (files.Length > 0) return files[0];
            }

            return string.Empty;
        }

        /// <summary>
        /// 使用 DuckDB 原生向量化引擎读取单日 Tick 文件
        /// </summary>
        private static RawTick[] ReadSingleTickFile(string filePath, CancellationToken ct)
        {
            if (!File.Exists(filePath)) return Array.Empty<RawTick>();

            try
            {
                using var connection = new DuckDBConnection("DataSource=:memory:");
                connection.Open();

                using var command = connection.CreateCommand();
                string normalizedPath = filePath.Replace('\\', '/');
                command.CommandText = $"SELECT * FROM read_parquet('{normalizedPath}')";

                using var reader = command.ExecuteReader();

                int idIdx = FindColumnOrdinal(reader, "trade_id", "id", "tradeId", "agg_trade_id");
                int priceIdx = FindColumnOrdinal(reader, "price", "p");
                int qtyIdx = FindColumnOrdinal(reader, "qty", "quantity", "q");
                int quoteQtyIdx = FindColumnOrdinal(reader, "quote_qty", "quoteQty", "quote_volume");
                int timeIdx = FindColumnOrdinal(reader, "time", "transact_time", "timestamp", "trade_time", "T");
                int isBuyerMakerIdx = FindColumnOrdinal(reader, "is_buyer_maker", "isBuyerMaker", "buyer_maker", "m");
                int isBestMatchIdx = FindColumnOrdinal(reader, "is_best_match", "isBestMatch", "M");

                var list = new List<RawTick>(300000);

                while (reader.Read())
                {
                    if (ct.IsCancellationRequested) break;

                    long tradeId = idIdx >= 0 ? reader.GetInt64(idIdx) : 0;
                    decimal price = priceIdx >= 0 ? reader.GetDecimal(priceIdx) : 0m;
                    decimal qty = qtyIdx >= 0 ? reader.GetDecimal(qtyIdx) : 0m;
                    decimal quoteQty = quoteQtyIdx >= 0 ? reader.GetDecimal(quoteQtyIdx) : (price * qty);
                    long time = timeIdx >= 0 ? reader.GetInt64(timeIdx) : 0;
                    bool isBuyerMaker = isBuyerMakerIdx >= 0 && reader.GetBoolean(isBuyerMakerIdx);
                    bool isBestMatch = isBestMatchIdx >= 0 && reader.GetBoolean(isBestMatchIdx);

                    list.Add(new RawTick
                    {
                        TradeId = tradeId,
                        Price = price,
                        Qty = qty,
                        QuoteQty = quoteQty,
                        Time = time,
                        IsBuyerMaker = isBuyerMaker,
                        IsBestMatch = isBestMatch
                    });
                }

                if (list.Count > 1)
                {
                    list.Sort((a, b) => a.Time == b.Time ? a.TradeId.CompareTo(b.TradeId) : a.Time.CompareTo(b.Time));
                }

                return list.ToArray();
            }
            catch (Exception ex)
            {
                Logger.Log($"[TickBatchStreamReader] Error reading {filePath}: {ex.Message}");
                return Array.Empty<RawTick>();
            }
        }

        private static int FindColumnOrdinal(System.Data.Common.DbDataReader reader, params string[] possibleNames)
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                string colName = reader.GetName(i);
                foreach (var name in possibleNames)
                {
                    if (string.Equals(colName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }
            }
            return -1;
        }
    }
}
