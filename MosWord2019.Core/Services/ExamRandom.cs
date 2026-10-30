using System;
using System.Collections.Generic;

namespace MosWord2019.Core.Services
{
    // SplitMix64 (Steele/Vigna): fixed unsigned 64-bit arithmetic, independent of System.Random.
    // This algorithm and seed mapping are part of the persisted exam reproducibility contract.
    internal sealed class ExamRandom
    {
        private ulong state;
        internal ExamRandom(long seed, ulong stream = 0) { state = unchecked((ulong)seed) ^ stream; }
        internal ulong Next()
        {
            unchecked
            {
                ulong z = (state += 0x9E3779B97F4A7C15UL);
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }
        internal ulong Below(ulong bound)
        {
            if (bound == 0) throw new ArgumentOutOfRangeException(nameof(bound));
            ulong threshold = unchecked(0UL - bound) % bound;
            ulong value;
            do { value = Next(); } while (value < threshold);
            return value % bound;
        }
        internal void Shuffle<T>(IList<T> values)
        {
            for (int i = values.Count - 1; i > 0; i--)
            {
                int j = (int)Below((ulong)i + 1);
                T value = values[i]; values[i] = values[j]; values[j] = value;
            }
        }
    }
}
