using Xunit;
using AstcSharp;
using System.Collections.Generic;

namespace AstcSharp.Tests
{
    public class BottomNTests
    {
        [Fact]
        public void SortBasic()
        {
            var heap = new BottomN<int>(10);
            Assert.True(heap.Empty);
            int[] list = { 1, 2 };
            foreach (var v in list) heap.Push(v);
            Assert.Equal(2, heap.Size);
            Assert.False(heap.Empty);
            var popped = heap.Pop();
            Assert.Equal(new List<int> { 1, 2 }, popped);

            heap = new BottomN<int>(6);
            int[] list2 = { 1, 4, 3, 2, 2, 1 };
            foreach (var v in list2) heap.Push(v);
            Assert.Equal(6, heap.Size);
            popped = heap.Pop();
            Assert.Equal(new List<int> { 1, 1, 2, 2, 3, 4 }, popped);
        }

        [Fact]
        public void Bounds()
        {
            var heap = new BottomN<int>(4);
            int[] list = { 1, 2, 3, 4 };
            foreach (var v in list) heap.Push(v);
            Assert.Equal(4, heap.Size);

            heap.Push(0);
            Assert.Equal(4, heap.Size);
            var popped = heap.Pop();
            Assert.Equal(new List<int> { 0, 1, 2, 3 }, popped);

            heap = new BottomN<int>(4);
            int[] list3 = { 4, 3, 2, 1 };
            foreach (var v in list3) heap.Push(v);
            int[] list2 = { 4, 4, 4, 4 };
            foreach (var v in list2) heap.Push(v);
            Assert.Equal(4, heap.Size);
            popped = heap.Pop();
            Assert.Equal(new List<int> { 1, 2, 3, 4 }, popped);

            heap = new BottomN<int>(4);
            foreach (var v in list3) heap.Push(v);
            int[] list5 = { 5, 5, 5, 5 };
            foreach (var v in list5) heap.Push(v);
            Assert.Equal(4, heap.Size);
            popped = heap.Pop();
            Assert.Equal(new List<int> { 1, 2, 3, 4 }, popped);

            heap = new BottomN<int>(4);
            foreach (var v in list3) heap.Push(v);
            int[] list6 = { 0, 0, 0, 0 };
            foreach (var v in list6) heap.Push(v);
            Assert.Equal(4, heap.Size);
            popped = heap.Pop();
            Assert.Equal(new List<int> { 0, 0, 0, 0 }, popped);
        }
    }
}
