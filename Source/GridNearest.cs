using System;

namespace RoboRumble.Training
{
    // Exact nearest free grid point; inspect nearby rings instead of the whole field.
    public static class GridNearest
    {
        public static int Find(bool[] blocked, int width, int height, float left, float bottom,
            float step, float px, float py)
        {
            int cx = Math.Max(0, Math.Min(width - 1, (int)Math.Floor((px - left) / step + .5f)));
            int cy = Math.Max(0, Math.Min(height - 1, (int)Math.Floor((py - bottom) / step + .5f)));
            int best = -1;
            float distance = float.PositiveInfinity;
            for (int radius = 0; radius < Math.Max(width, height); radius++)
            {
                for (int y = Math.Max(0, cy - radius); y <= Math.Min(height - 1, cy + radius); y++)
                    for (int x = Math.Max(0, cx - radius); x <= Math.Min(width - 1, cx + radius); x++)
                    {
                        if (radius > 0 && Math.Abs(x - cx) != radius && Math.Abs(y - cy) != radius) continue;
                        int index = y * width + x;
                        if (blocked[index]) continue;
                        float dx = left + x * step - px, dy = bottom + y * step - py;
                        float squared = dx * dx + dy * dy;
                        if (squared < distance || squared == distance && (best < 0 || index < best))
                        { distance = squared; best = index; }
                    }
                // An unvisited point is at least this far away along one grid axis.
                // Include a small rounding allowance and visit the adjacent ring for ties.
                float limit = Math.Max(0f, (radius + .5f) * step - .00001f);
                if (best >= 0 && distance < limit * limit) return best;
            }
            return best;
        }
    }
}
