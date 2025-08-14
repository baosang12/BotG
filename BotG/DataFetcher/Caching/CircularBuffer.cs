using System;
using System.Collections;
using System.Collections.Generic;

namespace DataFetcher.Caching
{
    public class CircularBuffer<T> : IEnumerable<T>
    {
        private readonly T[] buffer;
        private int head;
        private int count;

        public int Capacity { get; }
        public int Count => count;
        /// <summary>
        /// Indicates whether the buffer has reached its capacity.
        /// </summary>
        public bool IsFull => count >= Capacity;

        public CircularBuffer(int capacity)
        {
            if (capacity <= 0) throw new ArgumentException("Capacity must be greater than zero.");
            Capacity = capacity;
            buffer = new T[capacity];
            head = 0;
            count = 0;
        }

        public void Add(T item)
        {
            buffer[head] = item;
            head = (head + 1) % Capacity;
            if (count < Capacity) count++;
        }

        public T this[int index]
        {
            get
            {
                if (index < 0 || index >= count) throw new IndexOutOfRangeException();
                int idx = (head - count + index + Capacity) % Capacity;
                return buffer[idx];
            }
        }

        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < count; i++)
                yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
