using System;
using System.Collections.Generic;

namespace CrashSniffer.Services;

/// <summary>
/// 线程安全的固定容量环形缓冲：超过容量后覆盖最旧数据。
/// 用于保存最近 N 次硬件读数（300 次 × 2s = 10 分钟）。
/// </summary>
public class RingBuffer<T>
{
    private readonly T[] _buffer;
    private readonly int _capacity;
    private int _head;   // 下一个写入位置
    private int _count;  // 当前元素数
    private readonly object _lock = new();

    public RingBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _buffer = new T[capacity];
    }

    public int Capacity => _capacity;

    public int Count
    {
        get { lock (_lock) return _count; }
    }

    /// <summary>追加元素，满则覆盖最旧</summary>
    public void Add(T item)
    {
        lock (_lock)
        {
            _buffer[_head] = item;
            _head = (_head + 1) % _capacity;
            if (_count < _capacity) _count++;
        }
    }

    /// <summary>清空</summary>
    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            _head = 0;
            _count = 0;
        }
    }

    /// <summary>按时间顺序（旧→新）返回快照数组</summary>
    public T[] ToArray()
    {
        lock (_lock)
        {
            var result = new T[_count];
            if (_count == 0) return result;
            int start = (_head - _count + _capacity) % _capacity;
            for (int i = 0; i < _count; i++)
            {
                result[i] = _buffer[(start + i) % _capacity];
            }
            return result;
        }
    }

    /// <summary>返回时间范围内的元素（含两端）</summary>
    public T[] GetRange(DateTime from, DateTime to, Func<T, DateTime> timeSelector)
    {
        var all = ToArray();
        var list = new List<T>(all.Length);
        foreach (var item in all)
        {
            DateTime t = timeSelector(item);
            if (t >= from && t <= to) list.Add(item);
        }
        return list.ToArray();
    }
}
