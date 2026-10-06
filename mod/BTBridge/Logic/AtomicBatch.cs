using System;
using System.Collections.Generic;

namespace BTBridge.Logic
{
    /// <summary>
    /// Update a keyed plan all or nothing: build and validate every entry first, then touch the
    /// live plan. Standing orders used to clear the old plan, install entries one by one, and
    /// throw halfway; the rejected batch had already replaced the plan, and its early entries
    /// then executed (a review reproduced this).
    /// </summary>
    public static class AtomicBatch
    {
        /// <param name="stage">Builds and validates every entry; throws to reject the whole batch.</param>
        public static void Apply<T>(IDictionary<string, T> live, Func<IList<KeyValuePair<string, T>>> stage, bool replace)
        {
            var staged = stage(); // any exception leaves `live` exactly as it was
            var seen = new HashSet<string>();
            foreach (var kv in staged)
            {
                if (!seen.Add(kv.Key))
                {
                    throw new ArgumentException($"'{kv.Key}' appears twice in one batch");
                }
            }
            if (replace)
            {
                live.Clear();
            }
            foreach (var kv in staged)
            {
                live[kv.Key] = kv.Value;
            }
        }
    }
}
