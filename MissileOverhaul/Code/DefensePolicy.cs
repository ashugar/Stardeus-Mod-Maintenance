using System;
using System.Collections.Generic;

namespace MissileOverhaul {
    internal enum DefenseTargetKind {
        Other,
        IncomingMissile,
        Hullpiercer,
        Meteoroid,
        SuspectedStorageCapsule
    }

    internal static class DefensePolicy {
        public static bool Supports(string weaponId) {
            return weaponId == "RocketPod" || weaponId == "InterceptorTurret";
        }

        public static bool Manages(string weaponId, bool isPlayer, bool isAutomatic) {
            return Supports(weaponId) && (!isPlayer || isAutomatic);
        }

        public static bool RoleAllows(string weaponId, DefenseTargetKind target) {
            bool interceptorTarget = target == DefenseTargetKind.IncomingMissile
                || target == DefenseTargetKind.Hullpiercer || target == DefenseTargetKind.Meteoroid;
            return weaponId == "InterceptorTurret" ? interceptorTarget
                : weaponId == "RocketPod" && !interceptorTarget;
        }

        // A matching pass lets a launcher with fewer eligible targets retain coverage.
        public static int[] Assign(int[,] priorities, float[,] distances,
                int[] existing, int[] flights) {
            int weapons = priorities.GetLength(0), targets = priorities.GetLength(1);
            var result = new int[weapons];
            var owners = new int[targets];
            var loads = (int[])flights.Clone();
            for (int w = 0; w < weapons; w++) result[w] = -1;
            for (int t = 0; t < targets; t++) owners[t] = -1;
            var order = new List<int>[weapons];
            for (int w = 0; w < weapons; w++) {
                int row = w;
                order[w] = new List<int>();
                for (int t = 0; t < targets; t++)
                    if (priorities[w, t] > 0) order[w].Add(t);
                order[w].Sort((a, b) => {
                    int cmp = priorities[row, b].CompareTo(priorities[row, a]);
                    if (cmp != 0) return cmp;
                    cmp = (existing[row] == b).CompareTo(existing[row] == a);
                    if (cmp != 0) return cmp;
                    cmp = distances[row, a].CompareTo(distances[row, b]);
                    return cmp != 0 ? cmp : a.CompareTo(b);
                });
            }
            // Cover threats without a missile in flight before other threats.
            for (int w = 0; w < weapons; w++)
                Match(w, true, new bool[targets], priorities, order, result, owners, flights);
            // Lock that coverage, then cover the remaining distinct threats.
            var locked = (int[])owners.Clone();
            for (int w = 0; w < weapons; w++) {
                if (result[w] >= 0) continue;
                Match(w, false, new bool[targets], priorities, order, result, owners, flights, locked);
            }
            for (int w = 0; w < weapons; w++)
                if (result[w] >= 0) loads[result[w]]++;
            // Surplus launchers support the least covered eligible threat.
            for (int w = 0; w < weapons; w++) {
                if (result[w] >= 0) continue;
                int best = -1;
                foreach (int t in order[w])
                    if (best < 0 || loads[t] < loads[best]) best = t;
                if (best >= 0) { result[w] = best; loads[best]++; }
            }
            return result;
        }

        private static bool Match(int w, bool uncoveredOnly, bool[] visited,
                int[,] priorities, List<int>[] order, int[] result, int[] owners,
                int[] flights, int[] locked = null) {
            // Prefer a free target before moving another valid assignment.
            foreach (int t in order[w]) {
                if (visited[t] || (uncoveredOnly && flights[t] > 0)
                        || (locked != null && locked[t] >= 0) || owners[t] >= 0) continue;
                visited[t] = true;
                owners[t] = w; result[w] = t; return true;
            }
            foreach (int t in order[w]) {
                if (visited[t] || (uncoveredOnly && flights[t] > 0)
                        || (locked != null && locked[t] >= 0)) continue;
                visited[t] = true;
                if (owners[t] < 0 || Match(owners[t], uncoveredOnly, visited,
                        priorities, order, result, owners, flights, locked)) {
                    owners[t] = w; result[w] = t; return true;
                }
            }
            return false;
        }

        public static bool Intercept(double x, double y, double vx, double vy,
                double speed, out double time) {
            time = 0;
            if (!(speed > 0) || double.IsInfinity(speed)) return false;
            double a = vx * vx + vy * vy - speed * speed;
            double b = 2 * (x * vx + y * vy), c = x * x + y * y;
            if (c < 0.000001) return true;
            if (Math.Abs(a) < 0.000001) {
                if (Math.Abs(b) < 0.000001) return false;
                time = -c / b;
            } else {
                double disc = b * b - 4 * a * c;
                if (!(disc >= 0)) return false;
                double root = Math.Sqrt(disc);
                double t1 = (-b - root) / (2 * a), t2 = (-b + root) / (2 * a);
                time = t1 >= 0 && t2 >= 0 ? Math.Min(t1, t2) : Math.Max(t1, t2);
            }
            return time >= 0 && !double.IsNaN(time) && !double.IsInfinity(time);
        }

        public static float HitChance(float accuracy, float evade, bool trajectoryAligned = false) {
            if (trajectoryAligned) return 1f;
            return Math.Max(0, Math.Min(1, accuracy))
                * Math.Max(0.01f, Math.Min(1, 1 - evade));
        }

        public static bool SuspectedStorageRaidActive(bool wasActive, int activeCapsules) {
            return activeCapsules > 0 && (wasActive || activeCapsules > 5);
        }
    }

    internal sealed class ShotLease<T> where T : class {
        public readonly T Target;
        private bool completed;
        public ShotLease(T target) { Target = target; }
        public bool Complete() {
            if (completed) return false;
            completed = true;
            return true;
        }
    }
}
