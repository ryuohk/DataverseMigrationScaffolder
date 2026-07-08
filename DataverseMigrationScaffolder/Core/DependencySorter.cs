using System;
using System.Collections.Generic;
using System.Linq;

namespace DataverseMigrationScaffolder.Core
{
    /// <summary>
    /// Produces dependency TIERS:
    ///   tier 0 = tables with no dependencies (within the selection),
    ///   tier n = tables whose deepest dependency chain has length n.
    /// Self-references and polymorphic lookups are already excluded by the mapper.
    ///
    /// Cycles (mutually-referencing tables, common in Dataverse) are handled in two
    /// phases so they do NOT inflate the tier count:
    ///   Phase 1 finds the minimal set of dependency EDGES to drop: whenever no table
    ///           is free, the "hub" (the pending table the most others are waiting on)
    ///           has its remaining unmet dependency edges removed, with a warning.
    ///   Phase 2 computes normal Kahn waves on the now-acyclic graph, so a
    ///           cycle-broken table merges into its natural tier instead of
    ///           occupying a tier of its own.
    /// Each dropped edge means that lookup must be resolved with a deferred update
    /// pass during the load, regardless of table order.
    /// </summary>
    public static class DependencySorter
    {
        public static List<List<TableModel>> SortIntoTiers(IList<TableModel> tables, List<string> warnings)
        {
            return SortIntoTiers(tables, warnings, null);
        }

        /// <param name="droppedEdgesOut">Optional: receives table -> dependency edges dropped to break cycles.</param>
        public static List<List<TableModel>> SortIntoTiers(IList<TableModel> tables, List<string> warnings,
            Dictionary<string, HashSet<string>> droppedEdgesOut)
        {
            var byName = tables.ToDictionary(t => t.LogicalName.ToLowerInvariant(), t => t);
            var selected = new HashSet<string>(byName.Keys);

            Func<TableModel, HashSet<string>> depsInSelection = t =>
                new HashSet<string>(t.Dependencies.Where(d => selected.Contains(d)));

            // ---------------- Phase 1: find dependency edges to drop --------------
            var pending = tables.ToDictionary(
                t => t.LogicalName.ToLowerInvariant(),
                t => depsInSelection(t));

            var droppedEdges = new Dictionary<string, HashSet<string>>();   // table -> deps to ignore

            while (pending.Count > 0)
            {
                var ready = pending.Where(kv => kv.Value.Count == 0)
                                   .Select(kv => kv.Key)
                                   .ToList();

                if (ready.Count == 0)
                {
                    // Cycle. Drop the remaining unmet dependency edges of the hub:
                    // the pending table the most other pending tables are waiting on.
                    var dependentCounts = pending.Keys.ToDictionary(
                        k => k,
                        k => pending.Count(kv => kv.Value.Contains(k)));

                    var victim = pending
                        .OrderByDescending(kv => dependentCounts[kv.Key])
                        .ThenBy(kv => kv.Value.Count)
                        .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                        .First();

                    warnings.Add(string.Format(
                        "Cycle: [{0}] placed before its lookup target(s) [{1}] - resolve those lookups with a deferred update pass.",
                        victim.Key, string.Join(", ", victim.Value.OrderBy(v => v))));

                    HashSet<string> dropped;
                    if (!droppedEdges.TryGetValue(victim.Key, out dropped))
                    {
                        dropped = new HashSet<string>();
                        droppedEdges[victim.Key] = dropped;
                    }
                    foreach (var dep in victim.Value) dropped.Add(dep);
                    victim.Value.Clear();
                    continue;
                }

                foreach (var name in ready) pending.Remove(name);
                foreach (var kv in pending)
                {
                    foreach (var name in ready) kv.Value.Remove(name);
                }
            }

            if (droppedEdgesOut != null)
            {
                foreach (var kv in droppedEdges) droppedEdgesOut[kv.Key] = kv.Value;
            }

            // ---------------- Phase 2: tier assignment on the acyclic graph -------
            var remaining = tables.ToDictionary(
                t => t.LogicalName.ToLowerInvariant(),
                t =>
                {
                    var deps = depsInSelection(t);
                    HashSet<string> dropped;
                    if (droppedEdges.TryGetValue(t.LogicalName.ToLowerInvariant(), out dropped))
                    {
                        deps.ExceptWith(dropped);
                    }
                    return deps;
                });

            var tiers = new List<List<TableModel>>();

            while (remaining.Count > 0)
            {
                var wave = remaining.Where(kv => kv.Value.Count == 0)
                                    .Select(kv => kv.Key)
                                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                                    .ToList();

                if (wave.Count == 0)
                {
                    // Cannot happen: phase 1 guarantees the graph is acyclic. Guard anyway.
                    wave = remaining.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
                }

                tiers.Add(wave.Select(name => byName[name]).ToList());

                foreach (var name in wave) remaining.Remove(name);
                foreach (var kv in remaining)
                {
                    foreach (var name in wave) kv.Value.Remove(name);
                }
            }

            return tiers;
        }
    }
}
