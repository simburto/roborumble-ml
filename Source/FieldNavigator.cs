using System.Collections.Generic;
using UnityEngine;

namespace RoboRumble.Training
{
    // Plans movement around the real static colliders, with room for robot bumpers.
    public sealed class FieldNavigator
    {
        const float Step = .35f, Left = -8.4f, Bottom = -4.55f;
        const int Width = 49, Height = 27;
        readonly bool[] blocked = new bool[Width * Height];
        readonly List<Vector2> path = new List<Vector2>();
        Vector2 previousGoal;
        float nextPlan;
        readonly Vector3 half;
        readonly float SliceZ;
        readonly Vector3 bodyHalf, bodyCenter;
        readonly float rootZ;

        public FieldNavigator(BoxCollider body)
        {
            Bounds bodyBounds = body.bounds;
            bodyHalf = Vector3.Scale(body.size * .5f, body.transform.lossyScale);
            bodyCenter = Vector3.Scale(body.center, body.transform.lossyScale);
            rootZ = body.transform.position.z;
            // A square robot's corners extend farther while it turns during a route.
            float rotatingRadius = Mathf.Sqrt(bodyHalf.x * bodyHalf.x + bodyHalf.y * bodyHalf.y);
            half = new Vector3(rotatingRadius, rotatingRadius, bodyBounds.extents.z * .98f);
            SliceZ = bodyBounds.center.z;
            for (int n = 0; n < blocked.Length; n++)
            {
                Vector2 p = Point(n);
                foreach (Collider c in Physics.OverlapBox(new Vector3(p.x, p.y, SliceZ), half, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore))
                    if (c.attachedRigidbody == null && c.name != "Ground") { blocked[n] = true; break; }
            }
        }

        Vector2 Point(int n) { return new Vector2(Left + (n % Width) * Step, Bottom + (n / Width) * Step); }
        int Nearest(Vector2 p)
        {
            return GridNearest.Find(blocked, Width, Height, Left, Bottom, Step, p.x, p.y);
        }

        public bool CanApproach(Vector2 cargo)
        {
            int n = Nearest(cargo);
            return n >= 0 && Vector2.Distance(cargo, Point(n)) < .95f;
        }

        public bool IsFree(Vector2 position)
        {
            foreach (Collider c in Physics.OverlapBox(new Vector3(position.x, position.y, SliceZ), half, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore))
                if (c.attachedRigidbody == null && c.name != "Ground") return false;
            return true;
        }

        public bool IsPoseFree(Vector2 position, float heading)
        {
            Quaternion rotation = Quaternion.Euler(0, 0, heading);
            Vector3 center = new Vector3(position.x, position.y, rootZ) + rotation * bodyCenter;
            foreach (Collider c in Physics.OverlapBox(center, bodyHalf, rotation, ~0, QueryTriggerInteraction.Ignore))
                if (c.attachedRigidbody == null && c.name != "Ground") return false;
            return true;
        }

        public bool ClearPose(Vector2 from, Vector2 to, float heading)
        {
            Quaternion rotation = Quaternion.Euler(0, 0, heading);
            Vector3 center = new Vector3(from.x, from.y, rootZ) + rotation * bodyCenter;
            Vector2 delta = to - from;
            foreach (RaycastHit hit in Physics.BoxCastAll(center, bodyHalf,
                new Vector3(delta.x, delta.y, 0).normalized, rotation, delta.magnitude, ~0, QueryTriggerInteraction.Ignore))
                if (hit.collider.attachedRigidbody == null && hit.collider.name != "Ground") return false;
            return IsPoseFree(to, heading);
        }

        bool Clear(Vector2 from, Vector2 to)
        {
            Vector2 delta = to - from;
            foreach (RaycastHit hit in Physics.BoxCastAll(new Vector3(from.x, from.y, SliceZ), half,
                new Vector3(delta.x, delta.y, 0).normalized, Quaternion.identity, delta.magnitude, ~0, QueryTriggerInteraction.Ignore))
                if (hit.collider.attachedRigidbody == null && hit.collider.name != "Ground") return false;
            return true;
        }

        public Vector2 Waypoint(Vector2 position, Vector2 goal)
        {
            if (Clear(position, goal)) return goal;
            if (Time.time >= nextPlan || Vector2.Distance(goal, previousGoal) > .5f || path.Count == 0)
            {
                Plan(position, goal);
                previousGoal = goal;
                nextPlan = Time.time + .5f;
            }
            while (path.Count > 1 && Vector2.Distance(position, path[0]) < .4f) path.RemoveAt(0);
            for (int n = path.Count - 1; n >= 0; n--)
                if (Clear(position, path[n])) return path[n];
            return path.Count > 0 ? path[0] : position;
        }

        void Plan(Vector2 position, Vector2 goal)
        {
            path.Clear();
            int start = Nearest(position), end = Nearest(goal);
            if (start < 0 || end < 0) return;
            var cost = new float[blocked.Length];
            var parent = new int[blocked.Length];
            var closed = new bool[blocked.Length];
            for (int n = 0; n < cost.Length; n++) { cost[n] = float.PositiveInfinity; parent[n] = -1; }
            var open = new List<int> { start };
            cost[start] = 0;
            while (open.Count > 0)
            {
                int index = 0;
                float best = float.PositiveInfinity;
                for (int n = 0; n < open.Count; n++)
                {
                    float estimate = cost[open[n]] + Vector2.Distance(Point(open[n]), Point(end));
                    if (estimate < best) { best = estimate; index = n; }
                }
                int current = open[index];
                open.RemoveAt(index);
                if (current == end)
                {
                    for (int n = end; n != -1; n = parent[n]) path.Add(Point(n));
                    path.Reverse();
                    return;
                }
                closed[current] = true;
                int x = current % Width, y = current / Width;
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0 || x + dx < 0 || x + dx >= Width || y + dy < 0 || y + dy >= Height) continue;
                    int next = (y + dy) * Width + x + dx;
                    if (blocked[next] || closed[next]) continue;
                    if (dx != 0 && dy != 0 && (blocked[y * Width + x + dx] || blocked[(y + dy) * Width + x])) continue;
                    float value = cost[current] + Step * (dx != 0 && dy != 0 ? 1.414214f : 1f);
                    if (value >= cost[next]) continue;
                    cost[next] = value;
                    parent[next] = current;
                    if (!open.Contains(next)) open.Add(next);
                }
            }
        }
    }
}
