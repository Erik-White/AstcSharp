// Port of astc-codec/src/base/bottom_n.h
using System;
using System.Collections.Generic;

namespace AstcSharp
{
    // Aggregates the lowest N values according to a comparer.
    public class BottomN<T>
    {
        private readonly int _maxSize;
        private readonly List<T> _data;
        private readonly Comparer<T> _comparer;

        public BottomN(int maxSize, IComparer<T>? comparer = null)
        {
            if (maxSize <= 0) throw new ArgumentOutOfRangeException(nameof(maxSize));
            _maxSize = maxSize;
            _data = new List<T>();
            _comparer = comparer is null ? Comparer<T>.Default : Comparer<T>.Create(comparer.Compare);
        }

        public bool Empty => _data.Count == 0;
        public int Size => _data.Count;

        // The "Top" in the C++ implementation is the largest element in the
        // current bottom-N heap (root of max-heap), which is at index 0 when
        // using a heap with the reverse comparer.
        public T Top => _data[0];

        public void Push(T value)
        {
            if (_data.Count < _maxSize || _comparer.Compare(value, Top) < 0)
            {
                _data.Add(value);
                // Use heapify up pattern via .Sort with custom comparer is O(n log n),
                // but acceptable at this stage; implement heap operations if needed.
                _data.Sort(new ReverseComparer<T>(_comparer));

                if (_data.Count > _maxSize)
                {
                    PopTop();
                }
            }
        }

        public List<T> Pop()
        {
            int len = _data.Count;
            var result = new List<T>(len);
            for (int i = 0; i < len; i++)
            {
                result.Insert(0, PopTop());
            }
            return result;
        }

        private T PopTop()
        {
            // Pop the largest element (root) which is at index 0.
            T result = _data[0];
            _data.RemoveAt(0);
            return result;
        }

        private class ReverseComparer<U> : IComparer<U>
        {
            private readonly Comparer<U> _inner;
            public ReverseComparer(Comparer<U> inner) { _inner = inner; }
            public int Compare(U? x, U? y) => -_inner.Compare(x!, y!);
        }
    }
}
