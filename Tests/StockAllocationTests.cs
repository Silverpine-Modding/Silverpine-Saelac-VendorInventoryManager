using System;
using System.Linq;
using SilverpineMods.VendorInventoryManager;

// Standalone regression harness; excluded from the plugin assembly.
public static class StockAllocationTests
{
    private static int passed;

    public static void Run()
    {
        passed = 0;
        Expect("empty stock leaves all five random targets to generate",
            new[] { 1, 1, 1, 1, 1 },
            Enumerable.Range(0, 5).Select(_ => new bool[0]).ToArray(),
            new[] { 0, 0, 0, 0, 0 });
        Expect("one existing item cannot fill five pools",
            new[] { 1, 1, 1, 1, 1 },
            Enumerable.Range(0, 5).Select(_ => new[] { true }).ToArray(),
            new[] { 1, 0, 0, 0, 0 });

        // Brier has four fixed furniture entries, a Legendary pool, and
        // four Any/Template pools. Before repair only one random item exists.
        int[] brierTargets = Enumerable.Repeat(1, 9).ToArray();
        bool[][] before = BrierEligibility(1);
        Expect("Brier's four fixed items and Legendary item leave four shortages",
            brierTargets, before, new[] { 1, 1, 1, 1, 1, 0, 0, 0, 0 });
        Expect("Brier after repair meets all targets without adding another batch",
            brierTargets, BrierEligibility(5), Enumerable.Repeat(1, 9).ToArray());

        Expect("fixed stock has priority over overlapping pools",
            new[] { 2, 1, 1 },
            new[] { new[] { true, true }, new[] { true, true }, new[] { true, true } },
            new[] { 2, 0, 0 });
        Expect("broad pool yields the sole Legendary item to a restrictive pool",
            new[] { 1, 1 },
            new[] { new[] { true, true }, new[] { true, false } },
            new[] { 1, 1 });
        Expect("chained reallocation avoids unnecessary stock",
            new[] { 1, 1, 1 },
            new[] {
                new[] { true, true, false },
                new[] { false, true, true },
                new[] { true, false, false }
            }, new[] { 1, 1, 1 });
        Expect("per-pool quantities larger than one stay separate",
            new[] { 2, 2 },
            new[] { new[] { true, true, true }, new[] { true, true, true } },
            new[] { 2, 1 });
        Expect("zero-quantity or failed-chance pool reserves nothing",
            new[] { 0, 1 },
            new[] { new[] { true }, new[] { true } },
            new[] { 0, 1 });
        Expect("ineligible stock cannot meet a quality/source filter",
            new[] { 1, 1 },
            new[] { new[] { false, false }, new[] { true, false } },
            new[] { 0, 1 });
        Expect("no rules", new int[0], new bool[0][], new int[0]);

        // Exhaustive small cases check maximum matching against a brute-force
        // oracle, including every overlap pattern for three rules/items.
        for (int mask = 0; mask < 512; mask++)
        {
            bool[][] eligible = Enumerable.Range(0, 3).Select(rule =>
                Enumerable.Range(0, 3).Select(item =>
                    (mask & (1 << (rule * 3 + item))) != 0).ToArray()).ToArray();
            int[] actual = StockAllocation.Allocate(new[] { 1, 1, 1 }, eligible);
            int[] best = null;
            for (int assignment = 0; assignment < 64; assignment++)
            {
                int[] counts = new int[3];
                bool valid = true;
                for (int item = 0; item < 3; item++)
                {
                    int owner = ((assignment >> (item * 2)) & 3) - 1;
                    if (owner < 0)
                        continue;
                    if (!eligible[owner][item] || ++counts[owner] > 1)
                    {
                        valid = false;
                        break;
                    }
                }
                if (valid && (best == null || IsBetter(counts, best)))
                    best = counts;
            }
            if (!actual.SequenceEqual(best))
                throw new Exception("Allocation differs from oracle for mask " + mask);
        }
        Console.WriteLine("PASS: 512 exhaustive overlap/priority cases.");
        Console.WriteLine("Passed " + passed + " named regression cases and 512 exhaustive cases.");
    }

    private static bool[][] BrierEligibility(int randomItems)
    {
        int count = 4 + randomItems;
        bool[][] rows = new bool[9][];
        for (int fixedRule = 0; fixedRule < 4; fixedRule++)
            rows[fixedRule] = Enumerable.Range(0, count)
                .Select(item => item == fixedRule).ToArray();
        rows[4] = Enumerable.Range(0, count).Select(item => item == 4).ToArray();
        for (int pool = 5; pool < 9; pool++)
            rows[pool] = Enumerable.Repeat(true, count).ToArray();
        return rows;
    }

    private static bool IsBetter(int[] left, int[] right)
    {
        // Earlier rules (fixed stock first) keep their priority.
        for (int index = 0; index < left.Length; index++)
            if (left[index] != right[index])
                return left[index] > right[index];
        return false;
    }

    private static void Expect(string name, int[] targets, bool[][] eligible, int[] expected)
    {
        int[] actual = StockAllocation.Allocate(targets, eligible);
        if (!actual.SequenceEqual(expected))
            throw new Exception(name + ": expected " + string.Join(",", expected) +
                "; got " + string.Join(",", actual));
        passed++;
        Console.WriteLine("PASS: " + name);
    }
}
