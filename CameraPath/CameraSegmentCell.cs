using System;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using YukkuriMovieMaker.Commons;

namespace CameraPath
{
    /// <summary>
    /// 設定点と設定点の間に挟まる、時間カーブの行。
    /// 行のテンプレートはこのオブジェクトに束縛される。
    /// </summary>
    internal sealed class CameraSegmentCell : Bindable
    {
        public CameraSegmentCell(int index, CameraKeyframe from, CameraKeyframe to)
        {
            Index = index;
            From = from;
            To = to;
        }

        /// <summary>区間の番号（0始まり）。この設定点から次の設定点まで。</summary>
        public int Index { get; }

        public CameraKeyframe From { get; }

        public CameraKeyframe To { get; }

        /// <summary>「1→2」のような、区間の見出し。</summary>
        public string NumberText => $"{Index + 1}→{Index + 2}";

        /// <summary>行に描くカーブの形。</summary>
        public Geometry? CurveGeometry
        {
            get => curveGeometry;
            set => Set(ref curveGeometry, value);
        }
        private Geometry? curveGeometry;

        /// <summary>カーブの線の色。編集中は目立つ色にする。</summary>
        public Brush StrokeBrush
        {
            get => strokeBrush;
            set => Set(ref strokeBrush, value);
        }
        private Brush strokeBrush = Brushes.Transparent;

        /// <summary>この区間のカーブを、いま編集しているか。</summary>
        public bool IsEditing
        {
            get => isEditing;
            set => Set(ref isEditing, value);
        }
        private bool isEditing;
    }

    /// <summary>
    /// 時間カーブを、行に描く小さな図形にする。
    /// 横軸が区間内の時間、縦軸が区間内の進み。上が進み100%。
    /// </summary>
    internal static class CameraTimeCurveThumbnail
    {
        public const double Width = 52;

        public const double Height = 16;

        public static Geometry? Create(BezierAnimation? curve)
        {
            if (curve is null)
                return null;

            var points = curve.Points;
            if (points.Count < 2)
                return null;

            var figure = new PathFigure
            {
                StartPoint = ToPoint(points[0].Point),
                IsClosed = false,
                IsFilled = false,
            };

            for (var i = 1; i < points.Count; i++)
            {
                var previous = points[i - 1];
                var current = points[i];

                figure.Segments.Add(new BezierSegment(
                    ToPoint(previous.Point + previous.ControlPoint2),
                    ToPoint(current.Point + current.ControlPoint1),
                    ToPoint(current.Point),
                    true));
            }

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Freeze();
            return geometry;
        }

        private static Point ToPoint(Vector2 value)
            => new(value.X * Width, (1.0 - value.Y) * Height);
    }
}
