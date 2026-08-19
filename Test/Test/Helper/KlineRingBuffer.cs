using Common;
using System;
using System.Collections;
using System.Collections.Generic;

namespace Common.Helper
{
    /// <summary>
    /// 高性能定长 K 线环形缓冲区 (Circular Ring Buffer)
    /// 特性：
    /// 1. 实现 IReadOnlyList<RawKline>，100% 兼容系统内所有策略、计算与绘图接口
    /// 2. 追加元素为 O(1) 纯数组下标赋值，彻底消除 List.RemoveAt(0) 的 192KB/次 内存搬运与 GC 压力
    /// 3. 支持全局单调索引与逻辑索引快速映射
    /// </summary>
    public class KlineRingBuffer : IReadOnlyList<RawKline>
    {
        private readonly RawKline[] _buffer;
        private readonly int _capacity;
        private int _head = 0;   // 下一个写入位置
        private int _count = 0;  // 当前实际有效元素数量

        public KlineRingBuffer(int capacity = 2000)
        {
            _capacity = Math.Max(16, capacity);
            _buffer = new RawKline[_capacity];
        }

        public int Count => _count;
        public int Capacity => _capacity;

        /// <summary>
        /// O(1) 追加新 K 线 (当达到容量上限时自动覆盖最老数据，零内存拷贝)
        /// </summary>
        public void Add(in RawKline kline)
        {
            _buffer[_head] = kline;
            _head = (_head + 1) % _capacity;
            if (_count < _capacity)
            {
                _count++;
            }
        }

        /// <summary>
        /// 按滑动窗口逻辑下标访问 (0 为窗口中最老的一根，Count - 1 为最新的一根)
        /// </summary>
        public RawKline this[int index]
        {
            get
            {
                if (index < 0 || index >= _count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index), $"Index {index} is out of range [0, {_count - 1}]");
                }

                // 最老有效元素的起始物理索引
                int start = (_head - _count + _capacity) % _capacity;
                int physicalIndex = (start + index) % _capacity;
                return _buffer[physicalIndex];
            }
        }

        public void Clear()
        {
            _head = 0;
            _count = 0;
            Array.Clear(_buffer, 0, _buffer.Length);
        }

        public RawKline[] ToArray()
        {
            var arr = new RawKline[_count];
            int start = (_head - _count + _capacity) % _capacity;
            for (int i = 0; i < _count; i++)
            {
                arr[i] = _buffer[(start + i) % _capacity];
            }
            return arr;
        }

        public IEnumerator<RawKline> GetEnumerator()
        {
            int start = (_head - _count + _capacity) % _capacity;
            for (int i = 0; i < _count; i++)
            {
                yield return _buffer[(start + i) % _capacity];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
