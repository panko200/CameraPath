using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using YukkuriMovieMaker.Commons;

namespace CameraPath
{
    /// <summary>
    /// カメラ設定点リストのカスタムエディタ。
    /// 設定点の追加・削除・並べ替えと、選択中の設定点の編集、
    /// そして軌道を確認するための3Dプレビューをまとめて表示する。
    /// 時間の制御が「区間ごとのベジェ時間カーブ」のときは、
    /// 設定点の行の間に区間の行が挟まり、そこをクリックするとその場でカーブを編集できる。
    /// </summary>
    public partial class CameraKeyframeEditor : UserControl, IPropertyEditorControl2, IPropertyEditorControl
    {
        public static readonly DependencyProperty KeyframesProperty =
            DependencyProperty.Register(
                nameof(Keyframes),
                typeof(ImmutableList<CameraKeyframe>),
                typeof(CameraKeyframeEditor),
                new FrameworkPropertyMetadata(
                    ImmutableList<CameraKeyframe>.Empty,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    (sender, _) =>
                    {
                        if (sender is CameraKeyframeEditor editor)
                        {
                            editor.SyncLookAtFlag();
                            editor.RebuildRows();
                            editor.preview.Refresh();
                        }
                    }));

        public static readonly DependencyProperty TargetKeyframesProperty =
            DependencyProperty.Register(
                nameof(TargetKeyframes),
                typeof(IEnumerable<CameraKeyframe>),
                typeof(CameraKeyframeEditor),
                new PropertyMetadata(null));

        /// <summary>選択中の行（設定点）の背景。控えめな暖色。</summary>
        private static readonly Brush SelectedBackground = Frozen(Color.FromArgb(0x3A, 0xFF, 0xC4, 0x6B));

        /// <summary>マウスが乗っている行の背景。</summary>
        private static readonly Brush HoverBackground = Frozen(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));

        /// <summary>選択中の行の左端の線。</summary>
        private static readonly Brush SelectedAccent = Frozen(Color.FromRgb(0xFF, 0xC4, 0x6B));

        /// <summary>区間の行のカーブの色。</summary>
        private static readonly Brush CurveStroke = Frozen(Color.FromRgb(0x3D, 0x7F, 0xB8));

        /// <summary>編集中の区間のカーブの色。</summary>
        private static readonly Brush CurveStrokeActive = Frozen(Color.FromRgb(0xFF, 0xC4, 0x6B));

        /// <summary>区間の行の、左端の縦線の色。</summary>
        private static readonly Brush SegmentGuide = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

        /// <summary>編集中の区間の、左端の縦線の色。</summary>
        private static readonly Brush SegmentGuideActive = Frozen(Color.FromArgb(0x77, 0xFF, 0xC4, 0x6B));

        private ItemProperty[]? properties;
        private CameraPathEffect? effect;
        private IEditorInfo? editorInfo;

        /// <summary>いま表示している行。設定点の行と区間の行が混ざる。</summary>
        private readonly List<RowVisual> rows = new();

        /// <summary>区間の時間カーブを編集するエディタ（初めて必要になったときに作る）。</summary>
        private readonly CameraBezierAnimationEditorAttribute segmentAttribute = new();
        private YukkuriMovieMaker.Controls.BezierAnimationEditor? segmentEditor;

        /// <summary>いまカーブを編集している区間の、始まりの設定点。null なら閉じている。</summary>
        private CameraKeyframe? segmentOwner;

        /// <summary>選択中の設定点（いちばん最後に選んだ点）。設定点が無いときは -1。</summary>
        private int selectedIndex = -1;

        /// <summary>選択中の設定点の番号。Ctrl+クリックで複数選べる。</summary>
        private readonly SortedSet<int> selection = new();

        public event EventHandler? BeginEdit;

        public event EventHandler? EndEdit;

        public ImmutableList<CameraKeyframe> Keyframes
        {
            get => (ImmutableList<CameraKeyframe>)GetValue(KeyframesProperty);
            set => SetValue(KeyframesProperty, value);
        }

        public IEnumerable<CameraKeyframe>? TargetKeyframes
        {
            get => (IEnumerable<CameraKeyframe>?)GetValue(TargetKeyframesProperty);
            set => SetValue(TargetKeyframesProperty, value);
        }

        /// <summary>選択中の設定点の番号。-1 は未選択。</summary>
        internal int SelectedIndex => selectedIndex;

        /// <summary>選択中の設定点の番号（昇順）。</summary>
        internal IReadOnlyCollection<int> SelectedIndices => selection;

        /// <summary>いまカーブを編集している区間の、始まりの設定点。</summary>
        internal CameraKeyframe? SegmentOwner => segmentOwner;

        /// <summary>表示中の行。</summary>
        internal IReadOnlyList<RowVisual> Rows => rows;

        public ItemProperty[]? Properties
        {
            get => properties;
            set
            {
                if (properties == value)
                    return;

                properties = value;
                HookEffect();
                SelectKeyframe(selectedIndex);
            }
        }

        public CameraKeyframeEditor()
        {
            InitializeComponent();

            // 3Dプレビューでつかんだ設定点を、リストの選択に合わせる
            preview.KeyframePicked = PickKeyframe;

            // 前回の高さで開く
            var settings = CameraPathEditorSettings.Default;
            listRow.Height = new GridLength(settings.ListHeight);
            previewRow.Height = new GridLength(settings.PreviewHeight);

            // 高さを変えたら覚えておく
            listResizeThumb.DragCompleted += ResizeThumb_DragCompleted;
            previewResizeThumb.DragCompleted += ResizeThumb_DragCompleted;
        }

        // ─────────────────────────────────────────
        //  エフェクト本体の変更をプレビューへ伝える
        // ─────────────────────────────────────────

        private void HookEffect()
        {
            var owner = properties is { Length: > 0 }
                ? properties[0].PropertyOwner as CameraPathEffect
                : null;

            if (ReferenceEquals(owner, effect))
                return;

            if (effect is not null)
                effect.PropertyChanged -= Effect_PropertyChanged;

            effect = owner;

            if (effect is not null)
                effect.PropertyChanged += Effect_PropertyChanged;

            preview.SetEffect(effect);
            SyncLookAtFlag();
            RebuildRows();
        }

        private void Effect_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // 設定点リストの変更はバインディング経由で受け取るので二重更新しない
            if (e.PropertyName == nameof(CameraPathEffect.Keyframes))
                return;

            // 「注視点を使う」が切り替わったら、各設定点の角度/注視点の表示を入れ替える
            if (e.PropertyName == nameof(CameraPathEffect.LookAt))
                SyncLookAtFlag();

            // 時間の制御が変わったら、区間の行を出し入れする
            if (e.PropertyName == nameof(CameraPathEffect.TimeControl))
            {
                if (effect?.TimeControl != CameraTimeControl.SegmentBezier)
                    CloseSegmentEditor();

                RebuildRows();
            }

            preview.Refresh();
        }

        /// <summary>
        /// 「注視点を使う」の状態を各設定点へ配る。
        /// 設定点はエフェクト本体の設定を参照できないので、エディタから同期する。
        /// </summary>
        private void SyncLookAtFlag()
        {
            var lookAt = effect?.LookAt ?? false;

            foreach (var keyframe in Keyframes)
                keyframe.UseLookAt = lookAt;
        }

        // ─────────────────────────────────────────
        //  行の組み立て
        // ─────────────────────────────────────────

        /// <summary>
        /// リストの行を作り直す。設定点の行を並べ、その間に区間の行を挟む。
        /// 区間の行は「区間ごとのベジェ時間カーブ」のときだけ出す。
        /// </summary>
        private void RebuildRows()
        {
            var current = segmentOwner;

            // 行を消す前に、中に入っているエディタを外へ出す
            DetachSegmentEditor();

            foreach (var row in rows)
                ReleaseRow(row);

            rows.Clear();
            rowsPanel.Children.Clear();

            if (Keyframes.Count == 0)
            {
                rowsPanel.Children.Add(new TextBlock
                {
                    Text = "「＋」でカメラの設定点を追加します",
                    FontSize = 11,
                    Opacity = 0.6,
                    Margin = new Thickness(4, 6, 4, 0),
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            var showSegments = effect?.TimeControl == CameraTimeControl.SegmentBezier;

            for (var i = 0; i < Keyframes.Count; i++)
            {
                AddPointRow(i, Keyframes[i]);

                if (showSegments && i < Keyframes.Count - 1)
                    AddSegmentRow(i, Keyframes[i], Keyframes[i + 1]);
            }

            // 編集していた区間が残っていれば、その行に繋ぎ直す
            segmentOwner = current is not null && Keyframes.Contains(current) ? current : null;

            // 設定点が減ったときに、选择が残らないようにする
            selection.RemoveWhere(i => i < 0 || i >= Keyframes.Count);

            if (Keyframes.Count == 0)
                selectedIndex = -1;
            else if (!selection.Contains(selectedIndex))
                selectedIndex = selection.Count > 0 ? selection.Max : 0;

            if (selectedIndex >= 0)
                selection.Add(selectedIndex);

            ShowSegmentEditor();
            AfterSelectionChanged();
        }

        private void AddPointRow(int index, CameraKeyframe keyframe)
        {
            var content = new ContentControl
            {
                Content = keyframe,
                ContentTemplate = (DataTemplate)FindResource("PointRowTemplate"),
                Focusable = false,
                IsTabStop = false,
            };

            var border = new Border
            {
                Child = content,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(3, 1, 2, 1),
                Cursor = Cursors.Hand,
                Tag = index,
                ToolTip = "クリックで選択、ダブルクリックでこの設定点へ視点移動",
            };

            border.MouseLeftButtonDown += PointRow_MouseLeftButtonDown;
            border.MouseEnter += PointRow_MouseEnter;
            border.MouseLeave += PointRow_MouseLeave;

            rowsPanel.Children.Add(border);
            rows.Add(new RowVisual { Index = index, Border = border, Keyframe = keyframe });
        }

        private void AddSegmentRow(int index, CameraKeyframe from, CameraKeyframe to)
        {
            var cell = new CameraSegmentCell(index, from, to)
            {
                CurveGeometry = CameraTimeCurveThumbnail.Create(from.SegmentTimeBezier),
                StrokeBrush = CurveStroke,
            };

            var content = new ContentControl
            {
                Content = cell,
                ContentTemplate = (DataTemplate)FindResource("SegmentRowTemplate"),
                Focusable = false,
                IsTabStop = false,
            };

            var contentBorder = new Border
            {
                Child = content,
                Background = Brushes.Transparent,
                Padding = new Thickness(2, 1, 2, 1),
                Cursor = Cursors.Hand,
                Tag = index,
                ToolTip = "クリックで、この区間の時間カーブをここで編集します（もう一度クリックで閉じます）",
            };

            contentBorder.MouseLeftButtonDown += SegmentRow_MouseLeftButtonDown;
            contentBorder.MouseEnter += SegmentRow_MouseEnter;
            contentBorder.MouseLeave += SegmentRow_MouseLeave;

            var wrapper = new Border
            {
                Child = contentBorder,
                BorderBrush = SegmentGuide,
                BorderThickness = new Thickness(1, 0, 0, 0),
                Margin = new Thickness(12, 0, 0, 0),
            };

            var row = new RowVisual
            {
                Index = index,
                Border = wrapper,
                Cell = cell,
                Curve = from.SegmentTimeBezier,
            };

            row.CurveChanged = (_, _) => cell.CurveGeometry = CameraTimeCurveThumbnail.Create(from.SegmentTimeBezier);
            from.SegmentTimeBezier.Changed += row.CurveChanged;

            rowsPanel.Children.Add(wrapper);
            rows.Add(row);
        }

        /// <summary>行を捨てるときに、購読を外す。</summary>
        private void ReleaseRow(RowVisual row)
        {
            if (row.Curve is not null && row.CurveChanged is not null)
                row.Curve.Changed -= row.CurveChanged;

            if (row.Cell is null)
            {
                row.Border.MouseLeftButtonDown -= PointRow_MouseLeftButtonDown;
                row.Border.MouseEnter -= PointRow_MouseEnter;
                row.Border.MouseLeave -= PointRow_MouseLeave;
                return;
            }

            // 区間の行は、中の要素に付けたイベントを外す
            if (row.Border.Child is not Border contentBorder)
                return;

            contentBorder.MouseLeftButtonDown -= SegmentRow_MouseLeftButtonDown;
            contentBorder.MouseEnter -= SegmentRow_MouseEnter;
            contentBorder.MouseLeave -= SegmentRow_MouseLeave;
        }

        // ─────────────────────────────────────────
        //  行のクリック
        // ─────────────────────────────────────────

        private void PointRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border { Tag: int index })
                return;

            // Ctrl+クリックで、選択に加えたり外したりできる
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                ToggleSelection(index);
            else
                SelectKeyframe(index);

            if (e.ClickCount >= 2)
                FocusSelectedKeyframe();

            e.Handled = true;
        }

        private void PointRow_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Border border && border.Tag is int index && index != selectedIndex)
                border.Background = HoverBackground;
        }

        private void PointRow_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is not Border border)
                return;

            border.Background = border.Tag is int index && index == selectedIndex
                ? SelectedBackground
                : Brushes.Transparent;
        }

        private void SegmentRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border { Tag: int index })
                return;

            if (index < 0 || index >= Keyframes.Count - 1)
                return;

            var owner = Keyframes[index];

            // 同じ区間をもう一度クリックしたら閉じる
            SetSegmentOwner(ReferenceEquals(segmentOwner, owner) ? null : owner);
            e.Handled = true;
        }

        private void SegmentRow_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Border border)
                border.Background = HoverBackground;
        }

        private void SegmentRow_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Border border)
                border.Background = Brushes.Transparent;
        }

        /// <summary>設定点を選ぶ。範囲外の番号は、いちばん近い設定点に丸める。</summary>
        internal void SelectKeyframe(int index)
        {
            selectedIndex = Keyframes.Count == 0
                ? -1
                : Math.Clamp(index < 0 ? 0 : index, 0, Keyframes.Count - 1);

            selection.Clear();
            if (selectedIndex >= 0)
                selection.Add(selectedIndex);

            AfterSelectionChanged();
        }

        /// <summary>
        /// 3D表示で設定点をつかんだときの選択。
        /// すでに選んでいる複数の点のうちのひとつをつかんだときは、その選択を保つ。
        /// ここで選択をひとつに絞ってしまうと、まとめて動かせなくなる。
        /// </summary>
        internal void PickKeyframe(int index)
        {
            if (index < 0 || index >= Keyframes.Count)
                return;

            if (selection.Count > 1 && selection.Contains(index))
            {
                // 選択の組み合わせはそのまま。動かす対象だけ差し替える。
                // （3D表示を作り直すと、つかんでいる途中の握りがずれるので、選択の絵だけ更新する）
                selectedIndex = index;
                UpdateSelectionVisuals();
                UpdateButtonState();
                UpdateTargetKeyframes();
                return;
            }

            SelectKeyframe(index);
        }

        /// <summary>選択に加える／外す。</summary>
        internal void ToggleSelection(int index)
        {
            if (index < 0 || index >= Keyframes.Count)
                return;

            if (!selection.Remove(index))
            {
                selection.Add(index);
                selectedIndex = index;
            }
            else if (selectedIndex == index)
            {
                selectedIndex = selection.Count > 0 ? selection.Max : -1;
            }

            AfterSelectionChanged();
        }

        private void AfterSelectionChanged()
        {
            UpdateSelectionVisuals();
            UpdateButtonState();
            preview.SetSelectedIndices(selection.ToList());
            UpdateTargetKeyframes();
        }

        private void UpdateSelectionVisuals()
        {
            foreach (var row in rows)
            {
                if (row.Cell is not null)
                {
                    var editing = ReferenceEquals(row.Cell.From, segmentOwner);
                    row.Cell.IsEditing = editing;
                    row.Cell.StrokeBrush = editing ? CurveStrokeActive : CurveStroke;
                    row.Border.BorderBrush = editing ? SegmentGuideActive : SegmentGuide;
                    continue;
                }

                var selected = selection.Contains(row.Index);
                row.Border.Background = selected ? SelectedBackground : Brushes.Transparent;
                row.Border.BorderBrush = selected ? SelectedAccent : Brushes.Transparent;
            }
        }

        // ─────────────────────────────────────
        //  区間ごとのベジェ時間カーブ
        // ─────────────────────────────────────

        private void SetSegmentOwner(CameraKeyframe? owner)
        {
            if (ReferenceEquals(segmentOwner, owner))
                return;

            segmentOwner = owner;
            ShowSegmentEditor();
        }

        /// <summary>
        /// 選ばれている区間のカーブエディタを、リストの下に出す。
        /// 区間が選ばれていなければパネルを畳む。
        /// </summary>
        private void ShowSegmentEditor()
        {
            DetachSegmentEditor();

            var row = segmentOwner is null
                ? null
                : rows.FirstOrDefault(x => x.Cell is not null && ReferenceEquals(x.Cell.From, segmentOwner));

            if (row?.Cell is null)
            {
                segmentOwner = null;
                segmentPanel.Visibility = Visibility.Collapsed;
                UpdateSelectionVisuals();
                return;
            }

            EnsureSegmentEditor();

            if (segmentEditor is null)
                return;

            BindSegmentEditor(segmentOwner);

            segmentHeader.Text = $"区間 {row.Cell.NumberText} の時間カーブ";
            segmentHost.Children.Add(segmentEditor);
            segmentPanel.Visibility = Visibility.Visible;
            UpdateSelectionVisuals();
        }

        /// <summary>エディタを外して、区間の行の見た目を元に戻す。</summary>
        private void DetachSegmentEditor()
        {
            if (segmentEditor is not null)
                segmentHost.Children.Remove(segmentEditor);

            foreach (var row in rows)
            {
                if (row.Cell is null)
                    continue;

                row.Cell.IsEditing = false;
                row.Cell.StrokeBrush = CurveStroke;
            }
        }

        /// <summary>区間の編集を閉じる。</summary>
        private void CloseSegmentEditor()
        {
            segmentOwner = null;
            DetachSegmentEditor();
            segmentPanel.Visibility = Visibility.Collapsed;
            UpdateSelectionVisuals();
        }


        /// <summary>区間のカーブエディタを、指定した設定点の区間カーブに繋ぎ直す。</summary>
        private void BindSegmentEditor(CameraKeyframe? keyframe)
        {
            if (segmentEditor is null || keyframe is null)
                return;

            if (ReferenceEquals(segmentEditor.Value, keyframe.SegmentTimeBezier))
                return;

            segmentAttribute.ClearBindings(segmentEditor);

            var property = new ItemProperty(
                keyframe,
                keyframe,
                typeof(CameraKeyframe).GetProperty(nameof(CameraKeyframe.SegmentTimeBezier))!,
                null);

            segmentAttribute.SetBindings(segmentEditor, [property]);
        }

        private void EnsureSegmentEditor()
        {
            if (segmentEditor is not null)
                return;

            segmentEditor = (YukkuriMovieMaker.Controls.BezierAnimationEditor)segmentAttribute.Create();
            segmentEditor.BeginEdit += Editor_BeginEdit;
            segmentEditor.EndEdit += Editor_EndEdit;

            if (editorInfo is not null)
                segmentEditor.SetEditorInfo(editorInfo);
        }

        // ─────────────────────────────────────────
        //  設定点の操作
        // ─────────────────────────────────────────

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            if (Keyframes.Count == 0)
            {
                BeginEdit?.Invoke(this, EventArgs.Empty);
                Keyframes = Keyframes.Add(new CameraKeyframe(0, 0, 0, 0, 0, 0, 0, CameraPathSolver.DefaultFovDegrees));
                EndEdit?.Invoke(this, EventArgs.Empty);
                SelectKeyframe(0);
                return;
            }

            var index = Math.Max(0, selectedIndex);
            if (index >= Keyframes.Count - 1)
            {
                // 末尾に追加：最後の設定点をコピーして、少し先のフレームに置く
                var last = Keyframes[^1];
                var newFrame = (int)last.Frame.GetValue(0L, 1L, 30) + 30;
                var added = Clone(last, newFrame);

                BeginEdit?.Invoke(this, EventArgs.Empty);
                Keyframes = Keyframes.Add(added);
                EndEdit?.Invoke(this, EventArgs.Empty);

                SelectKeyframe(Keyframes.Count - 1);
                return;
            }

            // 中間に挿入：前後の設定点の中間値
            var previous = Keyframes[index];
            var next = Keyframes[index + 1];
            var previousFrame = (int)previous.Frame.GetValue(0L, 1L, 30);
            var nextFrame = (int)next.Frame.GetValue(0L, 1L, 30);
            var insertedFrame = previousFrame + Math.Max(1, (nextFrame - previousFrame) / 2);
            var inserted = Blend(previous, next, insertedFrame);

            // 前後の位置から、この設定点を通る向きの接線を入れておく
            if (effect is not null && effect.CurveType.UsesTangent())
            {
                CameraTangentTools.SetAutoTangent(
                    inserted,
                    CameraTangentTools.Position(previous),
                    CameraTangentTools.Position(next));
            }

            BeginEdit?.Invoke(this, EventArgs.Empty);
            Keyframes = Keyframes.Insert(index + 1, inserted);
            EndEdit?.Invoke(this, EventArgs.Empty);

            SelectKeyframe(index + 1);
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (selection.Count == 0 || Keyframes.Count <= selection.Count)
                return;

            var first = selection.Min;
            var removed = selection.Count;
            var list = Keyframes;

            // 後ろから消すと、番号がずれない
            foreach (var index in selection.Reverse())
                list = list.RemoveAt(index);

            BeginEdit?.Invoke(this, EventArgs.Empty);
            Keyframes = list;
            EndEdit?.Invoke(this, EventArgs.Empty);

            SelectKeyframe(Math.Max(0, first - removed + 1));
        }

        /// <summary>
        /// 選択中の設定点を複製して、少し後のフレームに置く。
        /// 接線や注視点も引き継ぐので、ベジェ軌道のままコピーしても軌道が折れない。
        /// </summary>
        private void DuplicateButton_Click(object sender, RoutedEventArgs e)
        {
            if (selection.Count == 0)
                return;

            const int gap = 30;

            var list = Keyframes;
            var duplicated = new List<int>();

            // 後ろから挿入して、前の番号がずれないようにする
            foreach (var index in selection.Reverse())
            {
                if (index < 0 || index >= list.Count)
                    continue;

                var source = list[index];
                var frame = (int)Math.Round(source.Frame.GetValue(0L, 1L, 30)) + gap;

                list = list.Insert(index + 1, Clone(source, frame));
                duplicated.Add(index + 1);
            }

            BeginEdit?.Invoke(this, EventArgs.Empty);
            Keyframes = list;
            EndEdit?.Invoke(this, EventArgs.Empty);

            SelectKeyframe(duplicated.Count > 0 ? duplicated.Min() : selectedIndex);
        }

        /// <summary>
        /// 軌道全体を逆向きにする。
        /// フレームも反転するので、同じ時間幅で逆再生の動きになる。
        /// </summary>
        private void ReverseButton_Click(object sender, RoutedEventArgs e)
        {
            if (Keyframes.Count < 2)
                return;

            var list = Keyframes;
            var frames = list.Select(x => x.Frame.GetValue(0L, 1L, 30)).ToList();
            var start = frames.Min();
            var end = frames.Max();

            var builder = ImmutableList.CreateBuilder<CameraKeyframe>();

            for (var i = list.Count - 1; i >= 0; i--)
            {
                var clone = Clone(list[i], (int)Math.Round(start + end - frames[i]));
                ReverseTangent(clone);
                builder.Add(clone);
            }

            BeginEdit?.Invoke(this, EventArgs.Empty);
            Keyframes = builder.ToImmutable();
            EndEdit?.Invoke(this, EventArgs.Empty);

            SelectKeyframe(selectedIndex < 0 ? 0 : Keyframes.Count - 1 - selectedIndex);
        }

        /// <summary>
        /// 軌道の終点から始点へ戻る点を足して、往復する動きにする。
        /// 戻りの点は接線を逆向きにするので、折り返しもなめらかになる。
        /// </summary>
        private void LoopButton_Click(object sender, RoutedEventArgs e)
        {
            if (Keyframes.Count < 2)
                return;

            var list = Keyframes;
            var frames = list.Select(x => x.Frame.GetValue(0L, 1L, 30)).ToList();
            var last = frames.Max();

            var builder = list.ToBuilder();

            // 終点（最後の点）はそのまま折り返しに使うので、途中の点から遡って足す
            for (var i = list.Count - 2; i >= 0; i--)
            {
                var clone = Clone(list[i], (int)Math.Round(last + (last - frames[i])));
                ReverseTangent(clone);
                builder.Add(clone);
            }

            BeginEdit?.Invoke(this, EventArgs.Empty);
            Keyframes = builder.ToImmutable();
            EndEdit?.Invoke(this, EventArgs.Empty);

            SelectKeyframe(Keyframes.Count - 1);
        }

        /// <summary>逆向きに進むための接線にする。手前と先の長さを入れ替える。</summary>
        private static void ReverseTangent(CameraKeyframe keyframe)
        {
            var azimuth = keyframe.TangentAngleH.GetValue(0L, 1L, 30);
            var elevation = keyframe.TangentAngleV.GetValue(0L, 1L, 30);
            var lengthIn = keyframe.TangentLengthIn.GetValue(0L, 1L, 30);
            var lengthOut = keyframe.TangentLengthOut.GetValue(0L, 1L, 30);

            keyframe.TangentAngleH.ActiveValues.First().Value = azimuth + 180.0;
            keyframe.TangentAngleV.ActiveValues.First().Value = -elevation;
            keyframe.TangentLengthIn.ActiveValues.First().Value = lengthOut;
            keyframe.TangentLengthOut.ActiveValues.First().Value = lengthIn;
        }

        private void UpButton_Click(object sender, RoutedEventArgs e)
        {
            var index = selectedIndex;
            if (index <= 0)
                return;

            BeginEdit?.Invoke(this, EventArgs.Empty);
            var keyframe = Keyframes[index];
            Keyframes = Keyframes.RemoveAt(index).Insert(index - 1, keyframe);
            EndEdit?.Invoke(this, EventArgs.Empty);

            SelectKeyframe(index - 1);
        }

        private void DownButton_Click(object sender, RoutedEventArgs e)
        {
            var index = selectedIndex;
            if (index < 0 || index >= Keyframes.Count - 1)
                return;

            BeginEdit?.Invoke(this, EventArgs.Empty);
            var keyframe = Keyframes[index];
            Keyframes = Keyframes.RemoveAt(index).Insert(index + 1, keyframe);
            EndEdit?.Invoke(this, EventArgs.Empty);

            SelectKeyframe(index + 1);
        }

        /// <summary>
        /// 設定点を複製する。接線と注視点も引き継ぐので、
        /// ベジェ軌道のまま末尾に追加しても、新しい点で軌道が折れない。
        /// </summary>
        internal static CameraKeyframe Clone(CameraKeyframe source, int frame)
        {
            var clone = new CameraKeyframe();
            clone.Frame.CopyFrom(source.Frame);
            clone.X.CopyFrom(source.X);
            clone.Y.CopyFrom(source.Y);
            clone.Z.CopyFrom(source.Z);
            clone.RotationX.CopyFrom(source.RotationX);
            clone.RotationY.CopyFrom(source.RotationY);
            clone.RotationZ.CopyFrom(source.RotationZ);
            clone.Fov.CopyFrom(source.Fov);
            clone.TangentAngleH.CopyFrom(source.TangentAngleH);
            clone.TangentAngleV.CopyFrom(source.TangentAngleV);
            clone.TangentLengthIn.CopyFrom(source.TangentLengthIn);
            clone.TangentLengthOut.CopyFrom(source.TangentLengthOut);
            clone.TargetX.CopyFrom(source.TargetX);
            clone.TargetY.CopyFrom(source.TargetY);
            clone.TargetZ.CopyFrom(source.TargetZ);
            clone.SegmentTimeBezier.CopyFrom(source.SegmentTimeBezier);
            clone.Frame.ActiveValues.First().Value = frame;
            return clone;
        }

        private static CameraKeyframe Blend(CameraKeyframe a, CameraKeyframe b, int frame)
        {
            var blended = new CameraKeyframe();

            static double Average(Animation first, Animation second)
                => (first.GetValue(0L, 1L, 30) + second.GetValue(0L, 1L, 30)) / 2.0;

            blended.X.ActiveValues.First().Value = Average(a.X, b.X);
            blended.Y.ActiveValues.First().Value = Average(a.Y, b.Y);
            blended.Z.ActiveValues.First().Value = Average(a.Z, b.Z);
            blended.RotationX.ActiveValues.First().Value = Average(a.RotationX, b.RotationX);
            blended.RotationY.ActiveValues.First().Value = Average(a.RotationY, b.RotationY);
            blended.RotationZ.ActiveValues.First().Value = Average(a.RotationZ, b.RotationZ);
            blended.Fov.ActiveValues.First().Value = Average(a.Fov, b.Fov);
            blended.TargetX.ActiveValues.First().Value = Average(a.TargetX, b.TargetX);
            blended.TargetY.ActiveValues.First().Value = Average(a.TargetY, b.TargetY);
            blended.TargetZ.ActiveValues.First().Value = Average(a.TargetZ, b.TargetZ);
            blended.Frame.ActiveValues.First().Value = frame;
            return blended;
        }

        // ─────────────────────────────────────
        //  高さの調整
        // ─────────────────────────────────────

        private void ListResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
            => ResizeRow(listRow, e.VerticalChange);

        private void PreviewResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
            => ResizeRow(previewRow, e.VerticalChange);

        private void ResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e) => SaveSizes();

        /// <summary>いまの高さを覚える。次に開いたときは同じ高さで始まる。</summary>
        private void SaveSizes()
        {
            if (double.IsNaN(listRow.Height.Value) || double.IsNaN(previewRow.Height.Value))
                return;

            var settings = CameraPathEditorSettings.Default;
            settings.ListHeight = listRow.Height.Value;
            settings.PreviewHeight = previewRow.Height.Value;
            settings.Save();
        }

        /// <summary>行の高さを、最小・最大の範囲で変える。</summary>
        private static void ResizeRow(RowDefinition row, double delta)
        {
            if (!double.IsFinite(delta) || delta == 0.0)
                return;

            var current = double.IsNaN(row.Height.Value) ? 0.0 : row.Height.Value;
            row.Height = new GridLength(Math.Clamp(current + delta, row.MinHeight, row.MaxHeight));
        }

        // ─────────────────────────────────────
        //  リストと3Dプレビューの往復
        // ─────────────────────────────────────

        /// <summary>選択中の設定点へ、3D表示の視点を移す。</summary>
        internal void FocusSelectedKeyframe()
        {
            if (selectedIndex < 0)
                return;

            preview.FocusKeyframe(selectedIndex);
        }

        private void UpdateTargetKeyframes()
        {
            if (properties is null || properties.Length == 0)
                return;

            var targets = new List<CameraKeyframe>();

            foreach (var property in properties)
            {
                var list = property.GetValue<ImmutableList<CameraKeyframe>>();
                if (list is not null && selectedIndex >= 0 && selectedIndex < list.Count)
                    targets.Add(list[selectedIndex]);
            }

            TargetKeyframes = targets;
        }

        private void UpdateButtonState()
        {
            var index = selectedIndex;
            var count = selection.Count;

            removeButton.IsEnabled = count > 0 && Keyframes.Count > count;
            upButton.IsEnabled = count == 1 && index > 0;
            downButton.IsEnabled = count == 1 && index >= 0 && index < Keyframes.Count - 1;
            duplicateButton.IsEnabled = count > 0;
            reverseButton.IsEnabled = Keyframes.Count >= 2;
            loopButton.IsEnabled = Keyframes.Count >= 2;
        }

        private void Editor_BeginEdit(object? sender, EventArgs e) => BeginEdit?.Invoke(this, EventArgs.Empty);

        private void Editor_EndEdit(object? sender, EventArgs e) => EndEdit?.Invoke(this, EventArgs.Empty);

        public void SetEditorInfo(IEditorInfo? info)
        {
            editorInfo = info;
            pointEditor.SetEditorInfo(info);
            preview.SetEditorInfo(info);
            segmentEditor?.SetEditorInfo(info);
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        /// <summary>リストに並ぶ行ひとつ分。</summary>
        internal sealed class RowVisual
        {
            /// <summary>設定点の番号。区間の行は、始まりの設定点の番号。</summary>
            public int Index { get; init; }

            public Border Border { get; init; } = null!;

            /// <summary>設定点の行のとき、その設定点。</summary>
            public CameraKeyframe? Keyframe { get; init; }

            /// <summary>区間の行のとき、その区間の表示。</summary>
            public CameraSegmentCell? Cell { get; init; }

                /// <summary>区間の行のとき、見張っている時間カーブ。</summary>
            public BezierAnimation? Curve { get; init; }

            public EventHandler? CurveChanged { get; set; }
        }
    }
}
