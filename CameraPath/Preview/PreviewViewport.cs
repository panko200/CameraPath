using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CameraPath.Preview
{
    internal readonly record struct PreviewSegment(Vector3 From, Vector3 To, Color Color, double Thickness);

    internal readonly record struct PreviewLabel(Vector3 Position, string Text, Color Color, double Size, int Priority = 0);

    /// <summary>3Dプレビューでつかめるもの。設定点そのものと、その前後につく制御点。</summary>
    internal enum PreviewHandleKind
    {
        /// <summary>設定点（カメラの位置）。</summary>
        Keyframe,

        /// <summary>手前側の制御点。接線の向きと手前の長さを変える。</summary>
        HandleIn,

        /// <summary>先側の制御点。接線の向きと先の長さを変える。</summary>
        HandleOut,

        /// <summary>注視点。カメラが向く点を直接動かす。</summary>
        LookAtTarget,
    }

    /// <summary>つかめる場所。Position は世界座標（Zが手前）。</summary>
    internal readonly record struct PreviewGrabPoint(Vector3 Position, PreviewHandleKind Kind, int Index);

    /// <summary>制御点をつかんで動かしたときの知らせ。</summary>
    internal sealed class PreviewHandleEventArgs(PreviewHandleKind kind, int index, Vector3 delta) : EventArgs
    {
        public PreviewHandleKind Kind { get; } = kind;

        /// <summary>設定点の番号。</summary>
        public int Index { get; } = index;

        /// <summary>つかんだところからの移動量（世界座標）。</summary>
        public Vector3 Delta { get; } = delta;
    }

    /// <summary>プレビューに描く線とラベルの集まり。</summary>
    internal sealed class PreviewScene
    {
        public List<PreviewSegment> Segments { get; } = [];

        public List<PreviewLabel> Labels { get; } = [];

        public void Clear()
        {
            Segments.Clear();
            Labels.Clear();
        }

        public void AddLine(Vector3 from, Vector3 to, Color color, double thickness = 1.0)
            => Segments.Add(new PreviewSegment(from, to, color, thickness));

        /// <summary>2点を結ぶ線を6本集めて、目印用の十字を作る。</summary>
        public void AddCross(Vector3 center, float size, Color color, double thickness = 2.0)
        {
            AddLine(center - new Vector3(size, 0f, 0f), center + new Vector3(size, 0f, 0f), color, thickness);
            AddLine(center - new Vector3(0f, size, 0f), center + new Vector3(0f, size, 0f), color, thickness);
            AddLine(center - new Vector3(0f, 0f, size), center + new Vector3(0f, 0f, size), color, thickness);
        }

        /// <param name="priority">重なったときに残す優先度。大きいほど残る。</param>
        public void AddLabel(Vector3 position, string text, Color color, double size = 10.0, int priority = 0)
            => Labels.Add(new PreviewLabel(position, text, color, size, priority));
    }

    /// <summary>
    /// 3Dの線をソフトウェア投影して描くビューポート。
    /// マウスで自由にカメラを回せる。
    /// アイテムの絵は PreviewItemLayer（3D）が別に描き、ここには線とラベルだけを重ねる。
    /// </summary>
    internal sealed class PreviewViewport : FrameworkElement
    {
        private static readonly Typeface LabelTypeface = new("Yu Gothic UI");

        private static readonly Color HighlightColor = Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF);
        private static readonly Color HighlightDragColor = Color.FromArgb(0x99, 0xFF, 0xE2, 0x8A);

        /// <summary>
        /// マウス操作を受け取るための透明な塗り。
        /// （塗りが無いと、線の上でしかマウスを拾えなくなる）
        /// </summary>
        private static readonly Brush HitTestBrush = Brushes.Transparent;

        private readonly Dictionary<(uint Color, int Thickness), Pen> penCache = [];

        /// <summary>つかめる場所を探すときに見る、マウスからの距離（画面の画素数）。</summary>
        private const double GrabRadius = 11.0;

        private Point lastPosition;
        private bool isOrbiting;
        private bool isPanning;

        private int hoveredGrabIndex = -1;
        private int draggedGrabIndex = -1;
        private Point dragAnchor;
        private Vector3 dragRight = Vector3.UnitX;
        private Vector3 dragUp = Vector3.UnitY;
        private float dragWorldPerPixel = 1f;

        public PreviewCamera Camera { get; } = new();

        public PreviewScene Scene { get; } = new();

        /// <summary>アイテムの絵を描く3D層。カメラを合わせるために持っておく。</summary>
        public PreviewItemLayer? ItemLayer { get; set; }

        /// <summary>つかめる場所。描画のたびにプレビュー側が入れ直す。</summary>
        public List<PreviewGrabPoint> GrabPoints { get; } = [];

        /// <summary>マウス操作でカメラが動いたときに発生する。</summary>
        public event EventHandler? ViewChanged;

        /// <summary>制御点をつかんだとき。</summary>
        public event EventHandler<PreviewHandleEventArgs>? HandleDragStarted;

        /// <summary>制御点を動かしているとき。</summary>
        public event EventHandler<PreviewHandleEventArgs>? HandleDragged;

        /// <summary>制御点を離したとき。</summary>
        public event EventHandler? HandleDragEnded;

        /// <summary>つかんだ設定点の番号。リストの選択を合わせるために使う。</summary>
        public Action<int>? KeyframePicked { get; set; }

        public PreviewViewport()
        {
            ClipToBounds = true;
            Focusable = true;
            SnapsToDevicePixels = true;
        }

        // ─────────────────────────────────────────
        //  描画
        // ─────────────────────────────────────────

        protected override void OnRender(DrawingContext drawingContext)
        {
            var width = (float)ActualWidth;
            var height = (float)ActualHeight;

            if (width < 2f || height < 2f)
                return;

            // 背景は親（Border）が塗る。ここは透明で塗ってマウスを受け取れるようにする。
            drawingContext.DrawRectangle(HitTestBrush, null, new Rect(0, 0, width, height));

            var projection = Camera.CreateProjection(width, height);
            penCache.Clear();

            // カメラが動くたびにここを通るので、アイテムの3D層も一緒に追従する
            ItemLayer?.SyncCamera(Camera, width, height);

            foreach (var segment in Scene.Segments)
            {
                if (!projection.TryProjectSegment(segment.From, segment.To, out var from, out var to))
                    continue;

                drawingContext.DrawLine(
                    GetPen(segment.Color, segment.Thickness),
                    new Point(from.X, from.Y),
                    new Point(to.X, to.Y));
            }

            DrawLabels(drawingContext, projection);
            DrawGrabHighlight(drawingContext, projection);
        }

        /// <summary>
        /// つかめる場所にマウスが乗っている（またはつかんでいる）とき、そこを囲んで分かるようにする。
        /// </summary>
        private void DrawGrabHighlight(DrawingContext drawingContext, in PreviewProjection projection)
        {
            var index = draggedGrabIndex >= 0 ? draggedGrabIndex : hoveredGrabIndex;
            if (index < 0 || index >= GrabPoints.Count)
                return;

            if (!projection.TryProjectPoint(GrabPoints[index].Position, out var position))
                return;

            var center = new Point(position.X, position.Y);
            var isDragging = draggedGrabIndex >= 0;
            var fill = GetBrush(isDragging ? HighlightDragColor : HighlightColor);
            var ring = GetPen(isDragging ? HighlightDragColor : HighlightColor, 1.6);

            drawingContext.DrawEllipse(fill, ring, center, isDragging ? 9.0 : 11.0, isDragging ? 9.0 : 11.0);
        }

        private void DrawLabels(DrawingContext drawingContext, in PreviewProjection projection)
        {
            var labels = Scene.Labels;
            if (labels.Count == 0)
                return;

            var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var texts = new FormattedText[labels.Count];

            for (var i = 0; i < labels.Count; i++)
            {
                var label = labels[i];
                texts[i] = new FormattedText(
                    label.Text,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    LabelTypeface,
                    label.Size,
                    GetBrush(label.Color),
                    pixelsPerDip);
            }

            var sizes = new Dictionary<PreviewLabel, Vector2>(labels.Count);
            for (var i = 0; i < labels.Count; i++)
                sizes[labels[i]] = new Vector2((float)texts[i].Width, (float)texts[i].Height);

            // ref/in の引数はラムダから見られないので、値として控えてから使う
            var projector = projection;

            // 重なったラベルは優先度の高いものだけを描く（アイテム名が重なって読めなくなるのを防ぐ）
            var placed = PreviewLabelLayout.Arrange(
                labels,
                label => projector.TryProjectPoint(label.Position, out var screen) ? screen : null,
                label => sizes[label]);

            foreach (var item in placed)
                drawingContext.DrawText(texts[item.Index], item.Bounds.TopLeft);
        }

        private Pen GetPen(Color color, double thickness)
        {
            var key = (ToKey(color), (int)Math.Round(Math.Clamp(thickness, 0.5, 6.0) * 2));
            if (penCache.TryGetValue(key, out var pen))
                return pen;

            pen = new Pen(GetBrush(color), key.Item2 / 2.0);
            if (pen.CanFreeze)
                pen.Freeze();

            penCache[key] = pen;
            return pen;
        }

        private static Brush GetBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            if (brush.CanFreeze)
                brush.Freeze();
            return brush;
        }

        private static uint ToKey(Color color)
            => ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

        // ─────────────────────────────────────────
        //  マウス操作
        // ─────────────────────────────────────────

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            CaptureMouse();

            var position = e.GetPosition(this);
            lastPosition = position;

            var plainLeft = e.ChangedButton == MouseButton.Left
                && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

            // 設定点や制御点の上で押したときは、カメラを回さずにつかんで動かす
            if (plainLeft && BeginGrab(position))
            {
                e.Handled = true;
                return;
            }

            isPanning = e.ChangedButton == MouseButton.Middle
                || (e.ChangedButton == MouseButton.Left && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            isOrbiting = !isPanning
                && (e.ChangedButton == MouseButton.Left || e.ChangedButton == MouseButton.Right);

            if (isOrbiting || isPanning)
                e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            var position = e.GetPosition(this);

            if (draggedGrabIndex >= 0)
            {
                DragGrab(position);
                e.Handled = true;
                return;
            }

            if (!isOrbiting && !isPanning)
            {
                UpdateHover(position);
                return;
            }

            var deltaX = (float)(position.X - lastPosition.X);
            var deltaY = (float)(position.Y - lastPosition.Y);
            lastPosition = position;

            if (isOrbiting)
                Camera.Orbit(deltaX, deltaY);
            else
                Camera.Pan(deltaX, deltaY);

            InvalidateVisual();
            ViewChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);

            if (IsMouseCaptured)
                ReleaseMouseCapture();

            isOrbiting = false;
            isPanning = false;
            EndGrab();
        }

        // ─────────────────────────────────────────
        //  つかんで動かす
        // ─────────────────────────────────────────

        /// <summary>画面上の位置からつかめるものを探し、つかむ。つかめたら true。</summary>
        internal bool BeginGrab(Point position)
        {
            var index = FindGrabPoint(position);
            if (index < 0)
                return false;

            var grab = GrabPoints[index];
            var projection = Camera.CreateProjection((float)ActualWidth, (float)ActualHeight);
            var cameraSpace = projection.ToCameraSpace(grab.Position);
            if (cameraSpace.Z <= Camera.NearPlane)
                return false;

            var (right, up, _) = Camera.GetViewAxes();

            draggedGrabIndex = index;
            dragAnchor = position;
            dragRight = right;
            dragUp = up;

            // つかんだところと同じ奥行きの面を動かす。この面での1画素あたりの世界の長さ。
            dragWorldPerPixel = Camera.GetWorldPerPixel(cameraSpace.Z, (float)ActualHeight);

            Cursor = Cursors.SizeAll;
            KeyframePicked?.Invoke(grab.Index);
            HandleDragStarted?.Invoke(this, new PreviewHandleEventArgs(grab.Kind, grab.Index, Vector3.Zero));
            InvalidateVisual();
            return true;
        }

        /// <summary>つかんでいるものを、マウスの位置に合わせて動かす。</summary>
        internal void DragGrab(Point position)
        {
            if (draggedGrabIndex < 0 || draggedGrabIndex >= GrabPoints.Count)
                return;

            var grab = GrabPoints[draggedGrabIndex];
            HandleDragged?.Invoke(this, new PreviewHandleEventArgs(grab.Kind, grab.Index, GetDragDelta(position)));
        }

        /// <summary>つかんでいるものを離す。</summary>
        internal void EndGrab()
        {
            if (draggedGrabIndex < 0)
                return;

            draggedGrabIndex = -1;
            Cursor = Cursors.Arrow;
            HandleDragEnded?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }

        /// <summary>つかんだところからの移動量を、カメラの向きに沿った世界座標で求める。</summary>
        private Vector3 GetDragDelta(Point position)
        {
            var deltaX = (float)(position.X - dragAnchor.X) * dragWorldPerPixel;
            var deltaY = (float)(position.Y - dragAnchor.Y) * dragWorldPerPixel;

            // 画面の下方向はカメラの上方向の逆
            return dragRight * deltaX - dragUp * deltaY;
        }

        private int FindGrabPoint(Point position)
        {
            if (GrabPoints.Count == 0 || ActualWidth < 2.0 || ActualHeight < 2.0)
                return -1;

            var projection = Camera.CreateProjection((float)ActualWidth, (float)ActualHeight);

            var nearest = GrabRadius;
            var found = -1;

            for (var i = 0; i < GrabPoints.Count; i++)
            {
                if (!projection.TryProjectPoint(GrabPoints[i].Position, out var projected))
                    continue;

                var deltaX = projected.X - position.X;
                var deltaY = projected.Y - position.Y;
                var distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);

                if (distance > nearest)
                    continue;

                nearest = distance;
                found = i;
            }

            return found;
        }

        private void UpdateHover(Point position)
        {
            var index = FindGrabPoint(position);
            if (index == hoveredGrabIndex)
                return;

            hoveredGrabIndex = index;
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Arrow;
            InvalidateVisual();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            Camera.Zoom(e.Delta);
            InvalidateVisual();
            ViewChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }
}
