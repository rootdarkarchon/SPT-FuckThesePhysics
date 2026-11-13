using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace FuckThesePhysics
{
    // Lists what registers rigidbodies with EFT's PhysicsExtensions.UpdateController and which of them keep
    // PhysicsExtensions.Simulation running. Read-only: nothing here writes physics, collider or rigidbody state.
    internal static class SimulationOwners
    {
        const int MaxIdleRows = 200;
        const int MaxVerdictTypes = 25;
        const int MaxGroupKeyTypes = 6;
        const int MaxPathDepth = 40;

        sealed class Row
        {
            public int Index;
            public bool Destroyed;
            public bool Counts;
            public string Path = "";
            public bool Active;
            public bool Kinematic;
            public bool Sleeping;
            public float Speed;
            public string Colliders = "";
            public string Visibility = "";
            public List<string> Owners = new List<string>();
            public string Tripwire;
            public string Error;
        }

        sealed class Group
        {
            public string Key;
            public int Total;
            public int Counting;
            public string Sample;
        }

        sealed class TypeTotals
        {
            public int Total;
            public int Counting;
        }

        internal static void Write(StringBuilder sb)
        {
            sb.Append("Physics.simulationMode=").Append(Physics.simulationMode)
                .Append(" Physics.autoSyncTransforms=").Append(Physics.autoSyncTransforms).AppendLine();
            sb.Append("Simulation.UpdateMode=").Append(PhysicsExtensions.Simulation.UpdateMode)
                .Append(" UpdateEnabled=").Append(PhysicsExtensions.Simulation.UpdateEnabled)
                .Append(" CanRunSimulate=").Append(PhysicsExtensions.Simulation.CanRunSimulate)
                .Append(" Quality=").Append(PhysicsExtensions.Simulation.Quality.ToString("F3", CultureInfo.InvariantCulture))
                .Append(" UpdateController.Enabled=").Append(PhysicsExtensions.UpdateController.Enabled).AppendLine();

            var entries = PhysicsExtensions.UpdateController._rigidbodies;
            if (entries == null)
            {
                sb.AppendLine("UpdateController._rigidbodies is null.");
                sb.AppendLine("Verdict: 0 entries count toward keeping simulation on.");
                return;
            }

            var rows = new List<Row>(entries.Count);
            for (int i = 0; i < entries.Count; i++) rows.Add(Inspect(i, entries[i]));

            int counting = 0, destroyed = 0, inactive = 0, hidden = 0, sleeping = 0, kinematic = 0;
            var groups = new Dictionary<string, Group>();
            var types = new Dictionary<string, TypeTotals>();
            foreach (var row in rows)
            {
                if (row.Destroyed) { destroyed++; }
                else
                {
                    if (!row.Active) inactive++;
                    else if (!row.Counts) hidden++;
                    if (row.Sleeping) sleeping++;
                    if (row.Kinematic) kinematic++;
                }
                if (row.Counts) counting++;

                string key = GroupKey(row);
                if (!groups.TryGetValue(key, out var group)) groups[key] = group = new Group { Key = key, Sample = row.Path };
                group.Total++;
                if (row.Counts) group.Counting++;
                foreach (var owner in row.Owners)
                {
                    if (!types.TryGetValue(owner, out var totals)) types[owner] = totals = new TypeTotals();
                    totals.Total++;
                    if (row.Counts) totals.Counting++;
                }
            }

            sb.Append("UpdateController._rigidbodies: total=").Append(rows.Count)
                .Append(" counting=").Append(counting)
                .Append(" destroyed/null=").Append(destroyed)
                .Append(" inactiveInHierarchy=").Append(inactive)
                .Append(" activeButNotVisible=").Append(hidden)
                .Append(" sleeping=").Append(sleeping)
                .Append(" kinematic=").Append(kinematic).AppendLine();
            sb.AppendLine("An entry counts when rigidbody != null && gameObject.activeInHierarchy && (visibilityChecker == null || visibilityChecker.IsVisible); same rule as UpdateController.CheckRigidbodiesDone.");

            WriteGroups(sb, groups);
            WriteTypeTotals(sb, types);
            WriteRows(sb, rows);
            WriteVerdict(sb, counting, types);
        }

        static Row Inspect(int index, PhysicsExtensions.UpdateController.RigidbodyData data)
        {
            var row = new Row { Index = index };
            var rb = data.rigidbody;
            if (rb == null)
            {
                row.Destroyed = true;
                row.Path = "<destroyed or null rigidbody>";
                row.Visibility = VisibilityText(data, out _);
                return row;
            }

            try
            {
                var go = rb.gameObject;
                row.Path = PathOf(go.transform);
                row.Active = go.activeInHierarchy;
                row.Kinematic = rb.isKinematic;
                row.Sleeping = rb.IsSleeping();
                row.Speed = rb.velocity.magnitude;
                row.Colliders = ColliderText(rb, go);
                row.Visibility = VisibilityText(data, out bool visibleOrAbsent);
                row.Counts = row.Active && visibleOrAbsent;
                CollectOwners(go, row.Owners);
                row.Tripwire = TripwireRelease.DescribeEntry(go);
            }
            catch (Exception error)
            {
                row.Error = error.GetType().Name + ": " + error.Message;
                // Recompute the counting flag defensively so a failed inspection cannot hide a counting entry.
                try
                {
                    row.Active = rb.gameObject.activeInHierarchy;
                    VisibilityText(data, out bool visibleOrAbsent);
                    row.Counts = row.Active && visibleOrAbsent;
                }
                catch { row.Counts = false; }
            }
            return row;
        }

        static string PathOf(Transform transform)
        {
            var parts = new List<string>();
            int depth = 0;
            for (var t = transform; t != null; t = t.parent)
            {
                if (depth++ >= MaxPathDepth) { parts.Add("..."); break; }
                parts.Add(t.name);
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        static string ColliderText(Rigidbody rb, GameObject go)
        {
            var colliders = go.GetComponentsInChildren<Collider>(true);
            var byType = new Dictionary<string, int[]>();
            int attached = 0;
            foreach (var collider in colliders)
            {
                if (collider == null || collider.attachedRigidbody != rb) continue;
                attached++;
                string name = collider.GetType().Name;
                if (!byType.TryGetValue(name, out var counts)) byType[name] = counts = new int[2];
                counts[0]++;
                if (collider.isTrigger) counts[1]++;
            }
            if (attached == 0) return "0";

            var text = new StringBuilder();
            text.Append(attached).Append(" (");
            bool first = true;
            foreach (var pair in byType)
            {
                if (!first) text.Append(", ");
                first = false;
                text.Append(pair.Key).Append(" x").Append(pair.Value[0]);
                if (pair.Value[1] > 0) text.Append(" [").Append(pair.Value[1]).Append(" trigger]");
            }
            return text.Append(')').ToString();
        }

        // visibleOrAbsent mirrors the "(visibilityChecker == null || visibilityChecker.IsVisible)" half of CheckRigidbodiesDone.
        static string VisibilityText(PhysicsExtensions.UpdateController.RigidbodyData data, out bool visibleOrAbsent)
        {
            var checker = data.visibilityChecker;
            if (checker == null)
            {
                visibleOrAbsent = true;
                return "no checker";
            }
            try
            {
                bool visible = checker.IsVisible;
                visibleOrAbsent = visible;
                return visible ? "checker visible" : "checker NOT visible";
            }
            catch (Exception error)
            {
                // Same failure would surface in CheckRigidbodiesDone; treat as not visible for the report.
                visibleOrAbsent = false;
                return "checker threw " + error.GetType().Name;
            }
        }

        // Script types on the rigidbody's GameObject and its ancestors, nearest first, de-duplicated.
        static void CollectOwners(GameObject go, List<string> owners)
        {
            var seen = new HashSet<string>();
            foreach (var behaviour in go.GetComponentsInParent<MonoBehaviour>(true))
            {
                if (behaviour == null) continue;
                var type = behaviour.GetType();
                string ns = type.Namespace;
                if (ns != null && (ns.StartsWith("UnityEngine", StringComparison.Ordinal) || ns.StartsWith("Unity.", StringComparison.Ordinal))) continue;
                string name = type.Name;
                if (seen.Add(name)) owners.Add(name);
            }
        }

        static string GroupKey(Row row)
        {
            if (row.Destroyed) return "<destroyed or null rigidbody>";
            if (row.Owners.Count == 0) return "<no script owner>";
            int take = Math.Min(row.Owners.Count, MaxGroupKeyTypes);
            string key = string.Join(" + ", row.Owners.GetRange(0, take));
            return take < row.Owners.Count ? key + " + ..." : key;
        }

        static void WriteGroups(StringBuilder sb, Dictionary<string, Group> groups)
        {
            var list = new List<Group>(groups.Values);
            list.Sort((a, b) =>
            {
                int c = b.Counting.CompareTo(a.Counting);
                if (c != 0) return c;
                c = b.Total.CompareTo(a.Total);
                return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
            });
            sb.AppendLine().AppendLine("### Entries grouped by owner scripts on the rigidbody's GameObject and ancestors (nearest first)");
            sb.Append("groups=").Append(list.Count).AppendLine();
            for (int i = 0; i < list.Count; i++)
            {
                var g = list[i];
                sb.Append("- ").Append(g.Key).Append(": entries=").Append(g.Total).Append(" counting=").Append(g.Counting)
                    .Append(" e.g. ").AppendLine(g.Sample);
            }
        }

        static void WriteTypeTotals(StringBuilder sb, Dictionary<string, TypeTotals> types)
        {
            var list = new List<KeyValuePair<string, TypeTotals>>(types);
            list.Sort((a, b) =>
            {
                int c = b.Value.Counting.CompareTo(a.Value.Counting);
                if (c != 0) return c;
                c = b.Value.Total.CompareTo(a.Value.Total);
                return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
            });
            sb.AppendLine().AppendLine("### Entries per owner script type (an entry appears under every script type found on it or its ancestors)");
            sb.Append("distinct types=").Append(list.Count).AppendLine();
            for (int i = 0; i < list.Count; i++)
            {
                sb.Append("- ").Append(list[i].Key).Append(": entries=").Append(list[i].Value.Total)
                    .Append(" counting=").Append(list[i].Value.Counting).AppendLine();
            }
        }

        static void WriteRows(StringBuilder sb, List<Row> rows)
        {
            sb.AppendLine().AppendLine("### Entries (counting first, then the rest in registration order)");
            int idleShown = 0, idleHidden = 0;
            foreach (var row in rows)
            {
                if (row.Counts) AppendRow(sb, row);
            }
            foreach (var row in rows)
            {
                if (row.Counts) continue;
                if (idleShown >= MaxIdleRows) { idleHidden++; continue; }
                idleShown++;
                AppendRow(sb, row);
            }
            if (idleHidden > 0) sb.Append("... ").Append(idleHidden).Append(" more non-counting entries not listed (total non-counting=").Append(idleShown + idleHidden).AppendLine(")");
        }

        static void AppendRow(StringBuilder sb, Row row)
        {
            sb.Append('[').Append(row.Index).Append("] ").Append(row.Counts ? "COUNTS " : "idle   ").Append(row.Path);
            if (row.Destroyed)
            {
                sb.Append(" | ").AppendLine(row.Visibility);
                return;
            }
            sb.Append(" | active=").Append(row.Active)
                .Append(" kinematic=").Append(row.Kinematic)
                .Append(" sleeping=").Append(row.Sleeping)
                .Append(" speed=").Append(row.Speed.ToString("F3", CultureInfo.InvariantCulture))
                .Append(" colliders=").Append(row.Colliders)
                .Append(" | ").Append(row.Visibility)
                .Append(" | owners=").Append(row.Owners.Count == 0 ? "<none>" : string.Join(", ", row.Owners));
            if (row.Tripwire != null) sb.Append(" | ").Append(row.Tripwire);
            if (row.Error != null) sb.Append(" | inspect error: ").Append(row.Error);
            sb.AppendLine();
        }

        static void WriteVerdict(StringBuilder sb, int counting, Dictionary<string, TypeTotals> types)
        {
            var counted = new List<KeyValuePair<string, int>>();
            foreach (var pair in types)
            {
                if (pair.Value.Counting > 0) counted.Add(new KeyValuePair<string, int>(pair.Key, pair.Value.Counting));
            }
            counted.Sort((a, b) =>
            {
                int c = b.Value.CompareTo(a.Value);
                return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
            });

            sb.AppendLine().Append("Verdict: ").Append(counting).Append(" entries count toward keeping simulation on");
            if (counted.Count == 0)
            {
                sb.AppendLine(counting == 0 ? "; no owner types." : "; owner types unknown (no script found on those entries or their ancestors).");
                return;
            }
            sb.Append("; owner types among them: ");
            int shown = Math.Min(counted.Count, MaxVerdictTypes);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(counted[i].Key).Append(" x").Append(counted[i].Value);
            }
            if (counted.Count > shown) sb.Append(", ... +").Append(counted.Count - shown).Append(" more");
            sb.AppendLine(".");
        }
    }
}
