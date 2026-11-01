using System;
using System.Collections.Generic;
using System.Numerics;
using System.Windows;

namespace CameraPath.Preview
{
    /// <summary>置いたラベルの番号と、画面上の大きさ。</summary>
    internal readonly record struct PlacedLabel(int Index, Rect Bounds);

    /// <summary>
    /// ラベルが重ならないように、優先度の高いものから順に置いていく。
    /// 重なったラベルは描かない（アイテム名がいくつも重なって読めなくなるのを防ぐ）。
    /// </summary>
    internal static class PreviewLabelLayout
    {
        /// <summary>重なりとみなす余白（画面上のpx）。</summary>
        private const double Padding = 2.0;

        /// <param name="labels">描きたいラベル。</param>
        /// <param name="project">ラベルの画面位置。画面の外やカメラの後ろなら null。</param>
        /// <param name="measure">文字の大きさ（幅・高さ）。</param>
        public static List<PlacedLabel> Arrange(
            IReadOnlyList<PreviewLabel> labels,
            Func<PreviewLabel, Vector2?> project,
            Func<PreviewLabel, Vector2> measure)
        {
            var placed = new List<PlacedLabel>(labels.Count);
            if (labels.Count == 0)
                return placed;

            var order = new int[labels.Count];
            for (var i = 0; i < order.Length; i++)
                order[i] = i;

            // 優先度が同じなら登録順を残す（並べ替えを安定にする）
            Array.Sort(order, (a, b) =>
            {
                var priority = labels[b].Priority.CompareTo(labels[a].Priority);
                return priority != 0 ? priority : a.CompareTo(b);
            });

            var boxes = new List<Rect>(labels.Count);

            foreach (var index in order)
            {
                var label = labels[index];

                if (project(label) is not { } screen
                    || !float.IsFinite(screen.X)
                    || !float.IsFinite(screen.Y))
                {
                    continue;
                }

                var size = measure(label);
                if (!(size.X > 0f) || !(size.Y > 0f))
                    continue;

                var bounds = new Rect(screen.X + 6.0, screen.Y - 6.0, size.X, size.Y);
                if (bounds.IsEmpty)
                    continue;

                var overlaps = false;

                foreach (var other in boxes)
                {
                    var padded = new Rect(
                        other.X - Padding,
                        other.Y - Padding,
                        other.Width + Padding * 2.0,
                        other.Height + Padding * 2.0);

                    if (!padded.IntersectsWith(bounds))
                        continue;

                    overlaps = true;
                    break;
                }

                if (overlaps)
                    continue;

                boxes.Add(bounds);
                placed.Add(new PlacedLabel(index, bounds));
            }

            return placed;
        }
    }
}
