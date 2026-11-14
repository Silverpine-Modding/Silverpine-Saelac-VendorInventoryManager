#nullable enable

using System;
using System.Collections.Generic;

namespace SilverpineMods.VendorInventoryManager;

/// <summary>
/// Assigns individual existing stock items to minimum quantities without
/// counting any item twice. Put fixed entries before random pools so their
/// allocations take priority. Compatible assignments may move to make room
/// for a more restrictive later rule, but a filled minimum is never reduced.
/// </summary>
internal static class StockAllocation
{
    internal static int[] Allocate(
        IReadOnlyList<int> targets,
        IReadOnlyList<bool[]> eligibleItems)
    {
        if (targets.Count != eligibleItems.Count)
            throw new ArgumentException("Each stock rule needs an eligibility row.");
        int itemCount = targets.Count == 0 ? 0 : eligibleItems[0].Length;
        for (int rule = 0; rule < targets.Count; rule++)
        {
            if (targets[rule] < 0 || eligibleItems[rule].Length != itemCount)
                throw new ArgumentException("Invalid stock-allocation input.");
        }

        var owners = new int[itemCount];
        for (int item = 0; item < itemCount; item++)
            owners[item] = -1;

        for (int rule = 0; rule < targets.Count; rule++)
        {
            for (int unit = 0; unit < targets[rule]; unit++)
            {
                if (!TryAssign(rule, eligibleItems, owners, new bool[itemCount]))
                    break;
            }
        }

        var counts = new int[targets.Count];
        foreach (int owner in owners)
            if (owner >= 0)
                counts[owner]++;
        return counts;
    }

    private static bool TryAssign(
        int rule,
        IReadOnlyList<bool[]> eligibleItems,
        int[] owners,
        bool[] visited)
    {
        for (int item = 0; item < owners.Length; item++)
        {
            if (visited[item] || owners[item] == rule || !eligibleItems[rule][item])
                continue;
            visited[item] = true;
            int previousOwner = owners[item];
            if (previousOwner < 0 ||
                TryAssign(previousOwner, eligibleItems, owners, visited))
            {
                owners[item] = rule;
                return true;
            }
        }
        return false;
    }
}
