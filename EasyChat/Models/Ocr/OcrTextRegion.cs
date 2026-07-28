using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;

namespace EasyChat.Models.Ocr;

public sealed record OcrTextRegion(
    string Text,
    IReadOnlyList<Point> Polygon,
    double Angle,
    double Confidence = 1d)
{
    public Point Center
        => Polygon.Count == 0
            ? Bounds.Center
            : new Point(Polygon.Average(point => point.X), Polygon.Average(point => point.Y));

    public Size OrientedSize
    {
        get
        {
            if (Polygon.Count < 2)
                return Bounds.Size;

            var edges = new List<double>(Polygon.Count);
            for (var index = 0; index < Polygon.Count; index++)
            {
                var start = Polygon[index];
                var end = Polygon[(index + 1) % Polygon.Count];
                var dx = end.X - start.X;
                var dy = end.Y - start.Y;
                var length = Math.Sqrt(dx * dx + dy * dy);
                if (length > 0.01)
                    edges.Add(length);
            }

            return edges.Count == 0
                ? Bounds.Size
                : new Size(edges.Max(), edges.Min());
        }
    }

    public static double CalculateTextAngle(IReadOnlyList<Point> polygon, double fallback = 0)
    {
        if (polygon.Count < 2)
            return NormalizeAngle(fallback);

        var longestLengthSquared = 0d;
        var angle = fallback;
        for (var index = 0; index < polygon.Count; index++)
        {
            var start = polygon[index];
            var end = polygon[(index + 1) % polygon.Count];
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var lengthSquared = dx * dx + dy * dy;
            if (lengthSquared <= longestLengthSquared)
                continue;

            longestLengthSquared = lengthSquared;
            angle = Math.Atan2(dy, dx) * 180d / Math.PI;
        }

        angle = NormalizeAngle(angle);
        return Math.Abs(angle) < 2 ? 0 : angle;
    }

    private static double NormalizeAngle(double angle)
    {
        while (angle > 90) angle -= 180;
        while (angle <= -90) angle += 180;
        return angle;
    }

    public Rect Bounds
    {
        get
        {
            if (Polygon.Count == 0)
                return new Rect();

            var left = double.MaxValue;
            var top = double.MaxValue;
            var right = double.MinValue;
            var bottom = double.MinValue;
            foreach (var point in Polygon)
            {
                left = Math.Min(left, point.X);
                top = Math.Min(top, point.Y);
                right = Math.Max(right, point.X);
                bottom = Math.Max(bottom, point.Y);
            }

            return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
        }
    }
}
