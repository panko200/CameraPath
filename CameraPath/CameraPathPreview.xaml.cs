using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CameraPath.Preview;
using YukkuriMovieMaker.Commons;

namespace CameraPath
{
    /// <summary>
    /// カメラの軌道を3Dの線で表示する内蔵プレビュー。
    /// エフェクトの設定Xaml内に埋め込んで使う。
    /// </summary>
    public partial class CameraPathPreview : UserControl
    {
        private const int PathSampleCount = 256;

        // 色はYMM4の設定画面で変えられる。描くたびに今の設定を読む。
        private static CameraPathColors Palette => CameraPathEditorSettings.Default.Colors;

        private static Color ScreenFrameColor => Palette.ScreenFrame;
        private static Color GridColor => Palette.Grid;
        private static Color AxisXColor => Palette.AxisX;
        private static Color AxisYColor => Palette.AxisY;
        private static Color AxisZColor => Palette.AxisZ;
        private static Color PathColor => Palette.Trajectory;
        private static Color KeyframeColor => Palette.Keyframe;
        private static Color SelectedKeyframeColor => Palette.SelectedKeyframe;
        private static Color CurrentCameraColor => Palette.CurrentCamera;
        private static Color FrustumColor => Palette.Frustum;
        private static Color CurrentCameraUpColor => Palette.CurrentCameraUp;
        private static Color ItemColor => Palette.Item;
        private static Color OtherItemColor => Palette.OtherItem;
        private static Color HandleColor => Palette.Handle;
        private static Color LookTargetColor => Palette.LookTarget;

        /// <summary>接線の長さがほぼ0でも、つかめるように出す握りの長さ（画面上のpx）。</summary>
        private const float PreviewGripPixels = 7f;

        private readonly PreviewViewport viewport = new();
        private readonly PreviewItemLayer itemLayer = new();
        private readonly List<PreviewImageQuad> itemQuads = [];
        private readonly List<CameraState> samples = [];
        private readonly List<Vector3> pathPoints = [];
        private readonly List<Vector3> overviewPoints = [];

        private SolidColorBrush? backgroundBrush;

        private bool hasAutoFitted;
        private bool fitIncludedItem;

        /// <summary>前回シーンを作ったとき、3D表示の大きさが決まっていたか。</summary>
        private bool builtWithValidSize;
        private bool isViewAdjusted;
        private int seenCaptureVersion = -1;
        private bool isUpdateLoopAttached;

        private CameraPathEffect? effect;
        private IEditorInfo? editorInfo;
        private long frame;
        private long length = 1;
        private int fps = 30;
        private int screenWidth = 1920;
        private int screenHeight = 1080;

        /// <summary>強調表示する設定点。Ctrl+クリックで複数を選べる。</summary>
        private readonly List<int> selectedIndices = [];

        /// <summary>再生用のフレーム。再生中だけ使う。</summary>
        private double playFrame;
        private bool isPlaying;
        private DispatcherTimer? playTimer;

        private PreviewHandleKind dragKind;
        private int dragIndex = -1;

        /// <summary>いっしょに動かす設定点の番号（複数選択しているとき）。</summary>
        private readonly List<int> dragIndices = [];

        /// <summary>つかんだ瞬間の、各設定点の座標。</summary>
        private readonly List<(double X, double Y, double Z)> dragPositions = [];

        /// <summary>つかんだときの制御点の位置（保存座標の、設定点からの向き）。</summary>
        private Vector3 dragOffset;



        /// <summary>注視点をつかんだときの座標。</summary>
        private double dragBaseTargetX;
        private double dragBaseTargetY;
        private double dragBaseTargetZ;

        /// <summary>3Dプレビューでの編集を始めたとき。UndoRedoにまとめるために使う。</summary>
        public event EventHandler? BeginEdit;

        /// <summary>3Dプレビューでの編集が終わったとき。</summary>
        public event EventHandler? EndEdit;

        /// <summary>3Dプレビューでつかんだ設定点の番号。リストの選択を合わせるために使う。</summary>
        public Action<int>? KeyframePicked { get; set; }

        public CameraPathPreview()
        {
            InitializeComponent();

            // 下から順に：背景（Border）→ アイテムの絵（3D）→ 軌道の線（2D）
            var layers = new Grid();
            layers.Children.Add(itemLayer);
            layers.Children.Add(viewport);

            viewport.ItemLayer = itemLayer;
            viewport.KeyframePicked = index => KeyframePicked?.Invoke(index);
            viewport.HandleDragStarted += Viewport_HandleDragStarted;
            viewport.HandleDragged += Viewport_HandleDragged;
            viewport.HandleDragEnded += Viewport_HandleDragEnded;
            viewportHost.Child = layers;
            viewport.SizeChanged += (_, _) => OnViewportSizeChanged();
            viewport.ViewChanged += (_, _) => isViewAdjusted = true;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
        {
            if (!isUpdateLoopAttached)
            {
                CompositionTarget.Rendering += OnRendering;
                isUpdateLoopAttached = true;
            }

            // 表示中だけ、YMM4の描画からアイテム情報を控える
            ItemSceneCapture.Subscribe();

            // 設定画面で色を変えたら、開いたままでも描き直す
            CameraPathEditorSettings.Default.PropertyChanged += Settings_PropertyChanged;
            Rebuild();
        }

        private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CameraPathEditorSettings.Colors))
                Rebuild();
        }

        private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
        {
            ItemSceneCapture.Unsubscribe();
            CameraPathEditorSettings.Default.PropertyChanged -= Settings_PropertyChanged;
            StopPlayback();

            if (!isUpdateLoopAttached)
                return;

            CompositionTarget.Rendering -= OnRendering;
            isUpdateLoopAttached = false;
        }

        /// <summary>
        /// YMM4の描画スレッドがアイテム情報を更新したら描き直す。
        /// 同じ内容のうちは何もしないので、停止中は負荷がかからない。
        /// </summary>
        private void OnRendering(object? sender, EventArgs e)
        {
            var version = ItemSceneCapture.Version;
            if (version == seenCaptureVersion)
                return;

            seenCaptureVersion = version;
            Rebuild();
        }

        /// <summary>表示対象のエフェクトを設定する。</summary>
        public void SetEffect(CameraPathEffect? value)
        {
            effect = value;
            hasAutoFitted = false;
            fitIncludedItem = false;
            isViewAdjusted = false;
            Rebuild();
        }

        /// <summary>タイムラインの情報を設定する。</summary>
        /// <remarks>
        /// YMM4は再生ヘッドが動くたびに新しい IEditorInfo を渡してくるので、
        /// ここではアイテムの取得結果を使い回す（アイテムが変わったら SetEffect が来る）。
        /// </remarks>
        public void SetEditorInfo(IEditorInfo? info)
        {
            editorInfo = info;
            Rebuild();
        }

        /// <summary>リストで選択中の設定点を強調表示する。</summary>
        public void SetSelectedIndex(int index)
            => SetSelectedIndices(index < 0 ? [] : [index]);

        /// <summary>
        /// 強調表示する設定点を複数指定する。
        /// 複数のときは、ここでつかむとまとめて動かせる。
        /// </summary>
        public void SetSelectedIndices(IReadOnlyList<int> indices)
        {
            selectedIndices.Clear();
            foreach (var index in indices)
                selectedIndices.Add(index);

            Rebuild();
        }

        private bool IsSelected(int index) => selectedIndices.Contains(index);

        /// <summary>再生中は、タイムラインのフレームではなく再生用のフレームで描く。</summary>
        private long CurrentFrame => isPlaying ? (long)Math.Floor(playFrame) : frame;

        // ─────────────────────────────────────────
        //  3D表示の中での再生
        // ─────────────────────────────────────────

        /// <summary>
        /// 軌道にそったカメラの動きを、この3D表示の中で再生する。
        /// タイムラインの再生ヘッドは動かさないので、編集中のフレームはそのまま。
        /// </summary>
        private void PlayButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (isPlaying)
            {
                StopPlayback();
                return;
            }

            StartPlayback();
        }

        private void StartPlayback()
        {
            if (effect is null)
                return;

            // フレーム範囲を最新にしてから始める
            Rebuild();
            CameraPathSolver.GetFrameRange(effect.Keyframes, effect.TimeControl, length, out var start, out var end);
            if (end <= start)
                return;

            playFrame = start;
            isPlaying = true;
            playButton.Content = "■ 停止";

            playTimer ??= new DispatcherTimer(DispatcherPriority.Render);
            playTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, fps));
            playTimer.Tick -= PlayTimer_Tick;
            playTimer.Tick += PlayTimer_Tick;
            playTimer.Start();

            Rebuild();
        }

        private void PlayTimer_Tick(object? sender, EventArgs e)
        {
            if (effect is null)
            {
                StopPlayback();
                return;
            }

            CameraPathSolver.GetFrameRange(effect.Keyframes, effect.TimeControl, length, out _, out var end);

            playFrame += 1.0;
            if (playFrame >= end)
            {
                playFrame = end;
                Rebuild();
                StopPlayback();
                return;
            }

            Rebuild();
        }

        private void StopPlayback()
        {
            playTimer?.Stop();

            if (!isPlaying)
                return;

            isPlaying = false;
            playButton.Content = "▶ 再生";
            Rebuild();
        }

        /// <summary>設定が変わったときに呼ぶ。</summary>
        public void Refresh() => Rebuild();

        /// <summary>
        /// 指定した設定点へ視点を移す。
        /// いまの向き・距離はそのままで、回転の中心だけをその点に合わせる。
        /// </summary>
        public void FocusKeyframe(int index)
        {
            if (effect is null)
                return;

            var keyframes = effect.Keyframes;
            if (index < 0 || index >= keyframes.Count)
                return;

            // 先に作り直して、フレームや画面サイズを最新にしておく
            Rebuild();

            var state = CameraPathSolver.EvaluateKeyframe(keyframes[index], frame, length, fps);
            CameraPathSolver.GetView(state, out var eye, out _, out _, out _);

            viewport.Camera.Target = eye;

            // 視点を動かしたので、自動フィットで元に戻されないようにする
            isViewAdjusted = true;
            viewport.InvalidateVisual();
        }

        /// <summary>3D表示のカメラ。確認用。</summary>
        internal PreviewCamera ViewCamera => viewport.Camera;

        private void ResetButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            viewport.Camera.Reset();
            viewport.InvalidateVisual();
        }

        private void FitButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            Rebuild();
            viewport.Camera.Fit(pathPoints, GetViewportAspect());
            viewport.InvalidateVisual();
            isViewAdjusted = true;
        }

        /// <summary>
        /// 全体が入るように視点を合わせる。
        /// ユーザーが視点を動かしたあとは何もしない。
        /// アイテムの情報が後から届いたときは、それを含めて一度だけ合わせ直す。
        /// </summary>
        private void TryAutoFit()
        {
            if (isViewAdjusted || overviewPoints.Count == 0)
                return;

            var width = viewport.ActualWidth;
            var height = viewport.ActualHeight;
            if (width < 4 || height < 4)
                return;

            var hasItem = ItemSceneCapture.Latest is { Items.Count: > 0 };
            if (hasAutoFitted && (fitIncludedItem || !hasItem))
                return;

            viewport.Camera.Fit(overviewPoints, (float)(width / height));
            hasAutoFitted = true;
            fitIncludedItem = hasItem;
            viewport.InvalidateVisual();
        }

        /// <summary>
        /// 3D表示の大きさが決まった（または変わった）とき。
        /// 大きさが分からないうちに作った絵は、制御点の握りの位置が狂うので作り直す。
        /// </summary>
        private void OnViewportSizeChanged()
        {
            if (builtWithValidSize)
            {
                TryAutoFit();
                return;
            }

            Rebuild();
        }

        private float GetViewportAspect()
        {
            var width = (float)viewport.ActualWidth;
            var height = (float)viewport.ActualHeight;
            return width > 2f && height > 2f ? width / height : 16f / 9f;
        }

        // ─────────────────────────────────────────
        //  シーン構築
        // ─────────────────────────────────────────

        private void Rebuild()
        {
            var scene = viewport.Scene;

            // 大きさが決まる前に作ったかどうかを控える（決まったら作り直すため）
            builtWithValidSize = viewport.ActualWidth >= 4.0 && viewport.ActualHeight >= 4.0;

            try
            {
                ApplyBackground();
                scene.Clear();
                itemQuads.Clear();
                viewport.GrabPoints.Clear();
                itemLayer.SetItems(itemQuads);
                pathPoints.Clear();
                overviewPoints.Clear();
                UpdateFrameInfo();

                if (effect is null)
                {
                    statusText.Text = string.Empty;
                    return;
                }

                var keyframes = effect.Keyframes;
                var settings = effect.CreateSettings(CurrentFrame, length, fps);

                CameraPathSolver.SamplePath(keyframes, settings, length, fps, PathSampleCount, samples);

                AddScreenPlane(scene);
                AddAxes(scene);
                AddItems(scene);
                itemLayer.SetItems(itemQuads);
                AddPath(scene);
                // 制御点の足が設定点の目印を横切らないよう、目印をあとから描く
                AddHandles(scene, keyframes);
                AddLookTargets(scene, keyframes);
                AddKeyframeMarkers(scene, keyframes);
                AddCurrentCamera(scene, effect.EvaluateCameraState(CurrentFrame, length, fps));

                statusText.Text = isPlaying
                    ? $"{keyframes.Count}点 / 再生中 F{CurrentFrame}"
                    : $"{keyframes.Count}点 / F{CurrentFrame}";
                TryAutoFit();
            }
            catch
            {
                // 編集中の一時的な不整合でプレビューが落ちないようにする
                statusText.Text = "プレビューを更新できませんでした";
            }
            finally
            {
                viewport.InvalidateVisual();
            }
        }

        /// <summary>背景色は設定で変えられる。変わっていないときは作り直さない。</summary>
        private void ApplyBackground()
        {
            var color = Palette.Background;
            if (backgroundBrush is not null && backgroundBrush.Color == color)
                return;

            backgroundBrush = new SolidColorBrush(color);
            viewportHost.Background = backgroundBrush;
        }

        private void UpdateFrameInfo()
        {
            if (editorInfo is null)
                return;

            frame = editorInfo.ItemPosition.Frame;
            length = Math.Max(1, editorInfo.ItemDuration.Frame);

            var video = editorInfo.VideoInfo;
            if (video is null)
                return;

            if (video.Width > 0) screenWidth = video.Width;
            if (video.Height > 0) screenHeight = video.Height;
            if (video.FPS > 0) fps = video.FPS;
        }

        /// <summary>アイテムが標準で描かれる面（z=0）の枠と目盛り。</summary>
        private void AddScreenPlane(PreviewScene scene)
        {
            var halfWidth = screenWidth * 0.5f;
            var halfHeight = screenHeight * 0.5f;

            var topLeft = new Vector3(-halfWidth, -halfHeight, 0f);
            var topRight = new Vector3(halfWidth, -halfHeight, 0f);
            var bottomRight = new Vector3(halfWidth, halfHeight, 0f);
            var bottomLeft = new Vector3(-halfWidth, halfHeight, 0f);

            scene.AddLine(topLeft, topRight, ScreenFrameColor, 1.5);
            scene.AddLine(topRight, bottomRight, ScreenFrameColor, 1.5);
            scene.AddLine(bottomRight, bottomLeft, ScreenFrameColor, 1.5);
            scene.AddLine(bottomLeft, topLeft, ScreenFrameColor, 1.5);

            const float step = 240f;

            // 枠の内側だけに目盛りを引く
            for (var offset = step; offset < halfWidth - 1f; offset += step)
            {
                scene.AddLine(new Vector3(offset, -halfHeight, 0f), new Vector3(offset, halfHeight, 0f), GridColor);
                scene.AddLine(new Vector3(-offset, -halfHeight, 0f), new Vector3(-offset, halfHeight, 0f), GridColor);
            }

            for (var offset = step; offset < halfHeight - 1f; offset += step)
            {
                scene.AddLine(new Vector3(-halfWidth, offset, 0f), new Vector3(halfWidth, offset, 0f), GridColor);
                scene.AddLine(new Vector3(-halfWidth, -offset, 0f), new Vector3(halfWidth, -offset, 0f), GridColor);
            }

            overviewPoints.Add(topLeft);
            overviewPoints.Add(topRight);
            overviewPoints.Add(bottomLeft);
            overviewPoints.Add(bottomRight);
        }

        private void AddAxes(PreviewScene scene)
        {
            const float axisLength = 400f;

            scene.AddLine(Vector3.Zero, new Vector3(axisLength, 0f, 0f), AxisXColor, 2.0);
            scene.AddLine(Vector3.Zero, new Vector3(0f, axisLength, 0f), AxisYColor, 2.0);
            scene.AddLine(Vector3.Zero, new Vector3(0f, 0f, axisLength), AxisZColor, 2.0);

            var labelOffset = axisLength * 1.15f;
            scene.AddLabel(new Vector3(labelOffset, 0f, 0f), "X", AxisXColor, 12, 1);
            scene.AddLabel(new Vector3(0f, labelOffset, 0f), "Y", AxisYColor, 12, 1);
            scene.AddLabel(new Vector3(0f, 0f, labelOffset), "Z", AxisZColor, 12, 1);
        }

        /// <summary>
        /// いまYMM4が描いているアイテムを、カメラを掛ける前の位置に四角形で並べる。
        /// グループ制御の中のアイテムも含めて全部出るので、どのアイテムがどこにあるか分かる。
        /// 絵は3D層（PreviewItemLayer）が貼るので、ここでは枠とラベルだけを描く。
        /// このエフェクトが効いているアイテムは紫、それ以外は灰色で囲む。
        /// </summary>
        private void AddItems(PreviewScene scene)
        {
            // 重なって見づらいときは、アイテムの絵と枠をまとめて消せるようにする
            if (effect is null || !effect.ShowItems)
                return;

            var captured = ItemSceneCapture.Latest;
            if (captured is null)
                return;

            foreach (var item in captured.Items)
            {
                if (item.Texture is not null)
                    itemQuads.Add(new PreviewImageQuad(item.A, item.B, item.C, item.D, item.Texture));

                var corners = new[] { item.A, item.B, item.C, item.D };
                var color = item.Affected ? ItemColor : OtherItemColor;
                var thickness = item.Affected ? 2.0 : 1.2;

                for (var i = 0; i < corners.Length; i++)
                    scene.AddLine(corners[i], corners[(i + 1) % corners.Length], color, thickness);

                var center = (item.A + item.B + item.C + item.D) * 0.25f;
                var prefix = item.Affected ? "▶ " : string.Empty;
                scene.AddLabel(center, $"{prefix}{item.Label} L{item.Layer}", color, item.Affected ? 11 : 9);

                foreach (var corner in corners)
                {
                    pathPoints.Add(corner);
                    overviewPoints.Add(corner);
                }
            }
        }

        private void AddPath(PreviewScene scene)
        {
            Vector3? previous = null;

            foreach (var sample in samples)
            {
                CameraPathSolver.GetView(sample, out var eye, out _, out _, out _);

                if (previous.HasValue)
                    scene.AddLine(previous.Value, eye, PathColor, 2.2);

                previous = eye;
                pathPoints.Add(eye);
                overviewPoints.Add(eye);
            }
        }

        private void AddKeyframeMarkers(PreviewScene scene, IReadOnlyList<CameraKeyframe> keyframes)
        {
            for (var i = 0; i < keyframes.Count; i++)
            {
                var state = CameraPathSolver.EvaluateKeyframe(keyframes[i], frame, length, fps);
                CameraPathSolver.GetView(state, out var eye, out _, out _, out _);

                var isSelected = IsSelected(i);
                var color = isSelected ? SelectedKeyframeColor : KeyframeColor;
                scene.AddCross(eye, isSelected ? 70f : 45f, color, isSelected ? 2.5 : 1.5);

                var frameNumber = (int)Math.Round(keyframes[i].Frame.GetValue(frame, length, fps));
                scene.AddLabel(eye, $"{(isSelected ? "▶" : string.Empty)}{i + 1}  F{frameNumber}", color, 10, 2);

                // つかんで動かせるように登録する
                viewport.GrabPoints.Add(new PreviewGrabPoint(eye, PreviewHandleKind.Keyframe, i));

                pathPoints.Add(eye);
                overviewPoints.Add(eye);
            }
        }

        /// <summary>
        /// 「注視点を使う」がONのとき、各設定点の注視点を線と目印で示す。
        /// 目印はつかんで動かせて、その設定点の注視点の座標を書き換える。
        /// </summary>
        private void AddLookTargets(PreviewScene scene, IReadOnlyList<CameraKeyframe> keyframes)
        {
            if (effect is null || !effect.LookAt)
                return;

            for (var i = 0; i < keyframes.Count; i++)
            {
                var keyframe = keyframes[i];
                var target = CameraPathSolver.ToPreviewSpace(
                    CameraPathSolver.EvaluateTarget(keyframe, frame, length, fps));

                var state = CameraPathSolver.EvaluateKeyframe(keyframe, frame, length, fps);
                CameraPathSolver.GetView(state, out var eye, out _, out _, out _);

                var color = IsSelected(i) ? SelectedKeyframeColor : LookTargetColor;
                var isSelected = IsSelected(i);

                scene.AddLine(eye, target, color, 1.0);
                scene.AddCross(target, isSelected ? 34f : 26f, color, isSelected ? 2.2 : 1.8);
                scene.AddLabel(target, $"{(isSelected ? "▶" : string.Empty)}注視点", color, 10, 2);

                // つかんで動かせるように登録する
                viewport.GrabPoints.Add(new PreviewGrabPoint(target, PreviewHandleKind.LookAtTarget, i));

                pathPoints.Add(target);
                overviewPoints.Add(target);
            }
        }

        /// <summary>
        /// 二次・三次ベジェのときに、軌道の接線（制御点）を描く。
        /// 制御点はプレビューのカメラで投影するので、視点を回すと画面上での位置も一緒に動く。
        /// </summary>
        private void AddHandles(PreviewScene scene, IReadOnlyList<CameraKeyframe> keyframes)
        {
            if (effect is null || !effect.CurveType.UsesTangent())
                return;

            for (var i = 0; i < keyframes.Count; i++)
            {
                var keyframe = keyframes[i];
                var tangent = CameraPathSolver.EvaluateTangent(keyframe, frame, length, fps);
                var direction = CameraPathSolver.GetTangentDirection(tangent.Azimuth, tangent.Elevation);

                var state = CameraPathSolver.EvaluateKeyframe(keyframe, frame, length, fps);
                CameraPathSolver.GetView(state, out var eye, out _, out _, out _);

                var color = IsSelected(i) ? SelectedKeyframeColor : HandleColor;

                AddHandleArm(scene, i, PreviewHandleKind.HandleIn, eye, direction, -tangent.LengthIn, color);
                AddHandleArm(scene, i, PreviewHandleKind.HandleOut, eye, direction, tangent.LengthOut, color);
            }
        }

        /// <summary>
        /// 制御点の足を伸ばす向き。手前の制御点は逆側へ伸ばす。
        /// 長さ 0 のときは向きが決まらないので、手前は -1・先は +1 にする。
        /// </summary>
        private static float SignedLengthSign(double signedLength) => signedLength < 0.0 ? -1f : 1f;

        /// <summary>
        /// 制御点の足の長さを決める。実際の長さをそのまま使い、ごく短いときだけ
        /// 画面上で数px離して描く（長さ0でも握りを見つけられるようにするため）。
        /// </summary>
        private float GetHandleArmLength(Vector3 eye, double signedLength)
        {
            var actual = MathF.Abs((float)signedLength);
            if (!float.IsFinite(actual))
                return 0f;

            var viewportWidth = (float)viewport.ActualWidth;
            var viewportHeight = (float)viewport.ActualHeight;

            // 大きさが決まる前（幅・高さが0）は、1pxあたりの長さを求められない。
            // ここで換算すると握りが数万pxに伸び、自動フィットまで引きずられるので、
            // そのときは実際の長さのままにする。
            if (viewportWidth < 4f || viewportHeight < 4f)
                return actual;

            var projection = viewport.Camera.CreateProjection(viewportWidth, viewportHeight);
            var depth = projection.ToCameraSpace(eye).Z;

            if (!float.IsFinite(depth) || depth <= 0f)
                return actual;

            var minimum = viewport.Camera.GetWorldPerPixel(depth, viewportHeight) * PreviewGripPixels;
            return MathF.Max(actual, minimum);
        }

        /// <summary>制御点ひとつぶんの足を描く。向きは保存座標なので、プレビューの世界座標へ直してから視点に足す。</summary>
        /// <remarks>
        /// 手前の制御点は向きが逆なので、長さは符号付きで受け取る。
        /// 握りは実際の長さの位置に描く（長さを変えたら見た目もそのまま動く）。
        /// </remarks>
        private void AddHandleArm(
            PreviewScene scene,
            int index,
            PreviewHandleKind kind,
            Vector3 eye,
            Vector3 direction,
            double signedLength,
            Color color)
        {
            var worldDirection = CameraPathSolver.ToPreviewSpace(direction);
            var armLength = GetHandleArmLength(eye, signedLength);
            var tip = eye + worldDirection * (SignedLengthSign(signedLength) * armLength);

            scene.AddLine(eye, tip, color, 1.4);
            scene.AddCross(tip, 22f, color, 2.0);

            viewport.GrabPoints.Add(new PreviewGrabPoint(tip, kind, index));
            pathPoints.Add(tip);
            overviewPoints.Add(tip);
        }

        // ─────────────────────────────────────────
        //  3Dプレビューでの編集
        // ─────────────────────────────────────────

        /// <summary>制御点をつかんだ。ここから離すまでをひとつのUndoRedoにする。</summary>
        private void Viewport_HandleDragStarted(object? sender, PreviewHandleEventArgs e)
        {
            dragKind = e.Kind;
            dragIndex = -1;
            dragOffset = Vector3.Zero;
            dragIndices.Clear();
            dragPositions.Clear();

            if (effect is null)
                return;

            var keyframes = effect.Keyframes;
            if (e.Index < 0 || e.Index >= keyframes.Count)
                return;

            dragIndex = e.Index;

            var keyframe = keyframes[dragIndex];

            // つかんだ点が選択に含まれていれば、選んでいる点を全部いっしょに動かす
            if (e.Kind == PreviewHandleKind.Keyframe)
            {
                if (selectedIndices.Count > 1 && selectedIndices.Contains(dragIndex))
                    dragIndices.AddRange(selectedIndices.Where(i => i >= 0 && i < keyframes.Count));
                else
                    dragIndices.Add(dragIndex);

                // 移動は「つかんだ瞬間の座標＋移動量」で決める。
                // （毎回その場の値に足すと、動かすほど加速して値が跳ねてしまう）
                foreach (var index in dragIndices)
                {
                    var target = keyframes[index];
                    dragPositions.Add((
                        target.X.GetValue(frame, length, fps),
                        target.Y.GetValue(frame, length, fps),
                        target.Z.GetValue(frame, length, fps)));
                }
            }

            dragBaseTargetX = keyframe.TargetX.GetValue(frame, length, fps);
            dragBaseTargetY = keyframe.TargetY.GetValue(frame, length, fps);
            dragBaseTargetZ = keyframe.TargetZ.GetValue(frame, length, fps);

            var tangent = CameraPathSolver.EvaluateTangent(keyframe, frame, length, fps);
            var direction = CameraPathSolver.GetTangentDirection(tangent.Azimuth, tangent.Elevation);

            // 描いている握りと同じ位置を基準にする（長さがごく短いときは少し離して描いているため）
            var isIn = dragKind == PreviewHandleKind.HandleIn;
            var signedLength = isIn ? -tangent.LengthIn : tangent.LengthOut;
            var state = CameraPathSolver.EvaluateKeyframe(keyframe, frame, length, fps);
            CameraPathSolver.GetView(state, out var eye, out _, out _, out _);

            var armLength = GetHandleArmLength(eye, signedLength);
            dragOffset = direction * (SignedLengthSign(signedLength) * armLength);

            BeginEdit?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// つかんだものを動かす。
        /// 設定点なら位置を、制御点なら接線の向きと長さを書き換える。
        /// </summary>
        private void Viewport_HandleDragged(object? sender, PreviewHandleEventArgs e)
        {
            if (effect is null)
                return;

            var keyframes = effect.Keyframes;
            if (dragIndex < 0 || dragIndex >= keyframes.Count)
                return;

            var keyframe = keyframes[dragIndex];

            // 世界座標（Zが手前）の移動量を、保存座標（Zが奥）の移動量へ直す
            var delta = new Vector3(e.Delta.X, e.Delta.Y, -e.Delta.Z);

            if (dragKind == PreviewHandleKind.Keyframe)
            {
                for (var i = 0; i < dragIndices.Count && i < dragPositions.Count; i++)
                {
                    var index = dragIndices[i];
                    if (index < 0 || index >= keyframes.Count)
                        continue;

                    var moved = keyframes[index];
                    var start = dragPositions[i];

                    CameraTangentTools.SetValue(moved.X, frame, start.X + delta.X);
                    CameraTangentTools.SetValue(moved.Y, frame, start.Y + delta.Y);
                    CameraTangentTools.SetValue(moved.Z, frame, start.Z + delta.Z);
                }
            }
            else if (dragKind == PreviewHandleKind.LookAtTarget)
            {
                CameraTangentTools.SetValue(keyframe.TargetX, frame, dragBaseTargetX + delta.X);
                CameraTangentTools.SetValue(keyframe.TargetY, frame, dragBaseTargetY + delta.Y);
                CameraTangentTools.SetValue(keyframe.TargetZ, frame, dragBaseTargetZ + delta.Z);
            }
            else
            {
                var target = dragOffset + delta;
                if (target.LengthSquared() < 1e-6f)
                    return;

                var tangent = CameraPathSolver.EvaluateTangent(keyframe, frame, length, fps);

                // 手前の制御点は設定点から逆向きに出ているので、その位置をそのまま
                // 接線の向きにすると180°反転してしまう。向きは反転してから角度にする。
                var aim = dragKind == PreviewHandleKind.HandleIn ? -target : target;
                CameraPathSolver.GetTangentAngles(aim, tangent.Azimuth, out var azimuth, out var elevation);

                // 接線の向きは手前と先で共通なので、どちらをつかんでも両方が回る
                CameraTangentTools.SetValue(keyframe.TangentAngleH, frame, azimuth);
                CameraTangentTools.SetValue(keyframe.TangentAngleV, frame, elevation);

                if (dragKind == PreviewHandleKind.HandleIn)
                    CameraTangentTools.SetValue(keyframe.TangentLengthIn, frame, target.Length());
                else
                    CameraTangentTools.SetValue(keyframe.TangentLengthOut, frame, target.Length());
            }

            Rebuild();
        }

        private void Viewport_HandleDragEnded(object? sender, EventArgs e)
        {
            if (dragIndex < 0)
                return;

            dragIndex = -1;
            dragIndices.Clear();
            dragPositions.Clear();
            EndEdit?.Invoke(this, EventArgs.Empty);
        }

        private void AddCurrentCamera(PreviewScene scene, in CameraState state)
        {
            CameraPathSolver.GetView(state, out var eye, out var right, out var up, out var forward);

            scene.AddCross(eye, 60f, CurrentCameraColor, 2.5);
            // 軸ラベルと重ならないよう、視点マーカーの上側に出す
            scene.AddLabel(eye + up * 130f, "現在のカメラ", CurrentCameraColor, 11, 3);
            pathPoints.Add(eye);
            overviewPoints.Add(eye);

            // 視野角が分かるように視錐台を描く（実際の見え方ではなく画角の目安）
            const float distance = 500f;
            var halfHeight = MathF.Tan(state.Fov * MathF.PI / 360f) * distance;
            var aspect = screenHeight > 0 ? screenWidth / (float)screenHeight : 16f / 9f;
            var halfWidth = halfHeight * aspect;

            var center = eye + forward * distance;
            var corners = new[]
            {
                center + right * halfWidth + up * halfHeight,
                center - right * halfWidth + up * halfHeight,
                center - right * halfWidth - up * halfHeight,
                center + right * halfWidth - up * halfHeight,
            };

            for (var i = 0; i < corners.Length; i++)
            {
                scene.AddLine(eye, corners[i], FrustumColor, 1.2);
                scene.AddLine(corners[i], corners[(i + 1) % corners.Length], FrustumColor, 1.2);
            }

            // 上方向の目印（ロール確認用）
            scene.AddLine(eye, eye + up * 90f, CurrentCameraUpColor, 2.0);
        }
    }
}
