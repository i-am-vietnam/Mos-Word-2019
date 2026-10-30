using System;
using System.Linq;

namespace MosWord2019.Core.Services
{
    public static class ScoreAllocator
    {
        public static int[] Allocate(int count, long seed)
        {
            if (count < 34 || count > 40) throw new ArgumentOutOfRangeException(nameof(count), "Expected 34-40 tasks.");
            int basePoints = 1000 / count;
            int remainder = 1000 - basePoints * count;
            int[] points = Enumerable.Repeat(basePoints, count).ToArray();
            int[] indices = Enumerable.Range(0, count).ToArray();
            // Separate stream: changing composition's draw count cannot change score allocation.
            new ExamRandom(seed, 0x53434F52455F4231UL).Shuffle(indices);
            for (int i = 0; i < remainder; i++) points[indices[i]]++;
            return points;
        }
    }
}
