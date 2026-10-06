using System;

namespace RoboRumble.Training
{
    // Route around the hub using short arcs. All positions are in the field's XY plane.
    public static class PlanarRoute
    {
        public static void Waypoint(float px, float py, float gx, float gy, float hx, float hy,
            float clearance, out float x, out float y)
        {
            double ax = px - hx, ay = py - hy, bx = gx - hx, by = gy - hy;
            double radius = clearance;
            double aLength = Math.Sqrt(ax * ax + ay * ay);
            double bLength = Math.Sqrt(bx * bx + by * by);
            if (aLength < radius)
            {
                double angle = aLength > .001 ? Math.Atan2(ay, ax) : Math.Atan2(by, bx);
                x = hx + (float)((radius + .2) * Math.Cos(angle));
                y = hy + (float)((radius + .2) * Math.Sin(angle));
                return;
            }
            if (bLength < radius + .1)
            {
                if (bLength < .001) { bx = ax; by = ay; bLength = aLength; }
                bx *= (radius + .1) / bLength;
                by *= (radius + .1) / bLength;
            }
            double dx = bx - ax, dy = by - ay;
            double squared = dx * dx + dy * dy;
            double t = squared > .000001 ? Math.Max(0, Math.Min(1, -(ax * dx + ay * dy) / squared)) : 0;
            double cx = ax + t * dx, cy = ay + t * dy;
            if (cx * cx + cy * cy < radius * radius)
            {
                double side = ax * by - ay * bx >= 0 ? 1 : -1;
                double angle = Math.Atan2(ay, ax) + side * .5;
                bx = (radius + .4) * Math.Cos(angle);
                by = (radius + .4) * Math.Sin(angle);
            }
            x = hx + (float)bx;
            y = hy + (float)by;
        }
    }
}
