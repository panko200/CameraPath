using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Effects;
using YukkuriMovieMaker.UndoRedo;

namespace CameraPath
{
    /// <summary>
    /// カメラパス映像エフェクト。
    /// 設定点にカメラのXYZ座標・XYZ角度・視野角をまとめて持ち、
    /// 設定点の間を直線またはベジェ曲線で補間しながらカメラを移動させる。
    /// 「注視点を使う」をONにすると、角度の代わりに注視点から向きを決める。
    /// </summary>
    [VideoEffect("カメラパス", ["カメラ"], ["カメラ", "パス", "camera", "path", "ベジェ", "視野角", "FOV", "注視点", "LookAt"], IsAviUtlSupported = false)]
    public class CameraPathEffect : VideoEffectBase
    {
        public override string Label => "カメラパス";

        // ─────────────────────────────────────────
        //  軌道
        // ─────────────────────────────────────────

        [Display(GroupName = "軌道", Name = "軌道の種類", Description = "設定点どうしを結ぶ線の種類です。\n二次・三次ベジェ曲線を選ぶと、下の3D表示で制御点（接線）をつかんで動かせます。", Order = 100)]
        [EnumComboBox]
        public CameraCurveType CurveType
        {
            get => curveType;
            set
            {
                if (curveType == value)
                    return;

                Set(ref curveType, value);

                // 直線などから接線を使う軌道へ切り替えたときに、
                // 接線が未設定の設定点へ前後の位置から自動で接線を入れる
                if (value.UsesTangent())
                    Keyframes = CameraTangentTools.WithAutoTangents(Keyframes);
            }
        }
        private CameraCurveType curveType = CameraCurveType.Straight;

        [Display(GroupName = "軌道", Name = "カーブの丸み", Description = "ベジェ曲線のときの丸みです。0で直線的、5で標準、10で大きく膨らみます。", Order = 110)]
        [ShowPropertyEditorWhen(nameof(CurveType), CameraCurveType.Bezier)]
        [AnimationSlider("F1", "", 0, 10)]
        public Animation CurveRoundness { get; } = new Animation(5, 0, 10);

        [Display(GroupName = "軌道", Name = "角度の飛び出し許容", Description = "補間するときに、設定点で決めた角度や視野角をどれだけ超えてよいか（度）です。\n0にすると、超えないような回り方（行き過ぎない接線）で補間します。急に止まるのではなく、前後がなめらかに繋がります。\n値を大きくすると、自然なカトムルム・ロム補間になり、少し行き過ぎて戻る動きが戻ります。\n位置（座標）は軌道の形そのものなので、この設定の影響を受けません。", Order = 120)]
        [AnimationSlider("F1", "°", 0, 180)]
        public Animation AngleOvershoot { get; } = new Animation(180, 0, 100000);

        // ─────────────────────────────────────────
        //  時間の制御
        // ─────────────────────────────────────────

        [Display(GroupName = "時間の制御", Name = "時間の制御", Description = "パス上を進む時間の決め方です。", Order = 200)]
        [EnumComboBox]
        public CameraTimeControl TimeControl
        {
            get => timeControl;
            set => Set(ref timeControl, value);
        }
        private CameraTimeControl timeControl = CameraTimeControl.KeyframeTime;

        [Display(GroupName = "時間の制御", Name = "動きの滑らかさ", Description = "0=カクカク動く、10=滑らかに動く。キーフレーム時刻のときに使います。", Order = 210)]
        [ShowPropertyEditorWhen(nameof(TimeControl), CameraTimeControl.KeyframeTime)]
        [AnimationSlider("F1", "", 0, 10)]
        public Animation TimeSmoothness { get; } = new Animation(5, 0, 10);

        [Display(GroupName = "時間の制御", Name = "イージングの種類", Description = "アイテム全体の時間に対するイージングの種類です。", Order = 220)]
        [ShowPropertyEditorWhen(nameof(TimeControl), CameraTimeControl.Easing)]
        [EnumComboBox]
        public EasingType EasingType
        {
            get => easingType;
            set => Set(ref easingType, value);
        }
        private EasingType easingType = EasingType.Cubic;

        [Display(GroupName = "時間の制御", Name = "イージングの方向", Description = "イージングをどちら側に効かせるかです。", Order = 230)]
        [ShowPropertyEditorWhen(nameof(TimeControl), CameraTimeControl.Easing)]
        [EnumComboBox]
        public EasingMode EasingMode
        {
            get => easingMode;
            set => Set(ref easingMode, value);
        }
        private EasingMode easingMode = EasingMode.InOut;

        [Display(GroupName = "時間の制御", Name = "ベジェ時間カーブ", Description = "時間に対する進行カーブをベジェ曲線で編集します。", Order = 240)]
        [ShowPropertyEditorWhen(nameof(TimeControl), CameraTimeControl.TimeBezier)]
        [CameraBezierAnimationEditor]
        public BezierAnimation TimeBezier { get; } = new BezierAnimation();

        [Display(GroupName = "時間の制御", Name = "進行の反転", Description = "ONにするとパスの終点から始点に向かって移動します。キーフレーム時刻以外のときに使います。", Order = 250)]
        [ShowPropertyEditorWhen(nameof(TimeControl), CameraTimeControl.Easing | CameraTimeControl.TimeBezier)]
        [ToggleSlider]
        public bool Reverse
        {
            get => reverse;
            set => Set(ref reverse, value);
        }
        private bool reverse;

        // ─────────────────────────────────────────
        //  設定点
        // ─────────────────────────────────────────

        [Display(GroupName = "注視点", Name = "注視点を使う", Description = "ONにすると、カメラの向きをXYZ角度ではなく「注視点」から決めます。\n各設定点の注視点へ常に向くので、被写体を回り込む撮影が角度を計算せずに作れます。\nOFFのときは今までどおり角度の設定を使います。", Order = 290)]
        [ToggleSlider]
        public bool LookAt
        {
            get => lookAt;
            set => Set(ref lookAt, value);
        }
        private bool lookAt;

        [Display(GroupName = "カメラ設定点", Description = "カメラの位置・角度・視野角をひとつの設定点として登録します。下の3D表示で軌道を確認できます。", Order = 300)]
        [CameraKeyframeEditor]
        public ImmutableList<CameraKeyframe> Keyframes
        {
            get => keyframes;
            set => Set(ref keyframes, value);
        }
        private ImmutableList<CameraKeyframe> keyframes = ImmutableList.Create(
            new CameraKeyframe(0, 0, 0, 0, 0, 0, 0, CameraPathSolver.DefaultFovDegrees),
            new CameraKeyframe(90, 200, -100, 200, 0, 20, 0, CameraPathSolver.DefaultFovDegrees));

        public CameraPathEffect()
        {
            SubscribeChildUndoRedoable(TimeBezier);
        }

        // ─────────────────────────────────────────
        //  3D表示
        // ─────────────────────────────────────────

        [Display(GroupName = "3D表示", Name = "アイテムを表示", Description = "ONにすると、3D表示にアイテムの絵と枠を奥行き順に描きます。\n重なって見づらいときはOFFにすると、軌道と設定点だけを見られます。\n（この設定は3D表示だけに使われ、動画の出力には影響しません）", Order = 310)]
        [ToggleSlider]
        public bool ShowItems
        {
            get => showItems;
            set => Set(ref showItems, value);
        }
        private bool showItems = true;

        // ─────────────────────────────────────────
        //  ベジェ時間カーブの UndoRedo
        // ─────────────────────────────────────────

        // ベジェポイントのドラッグ中に UndoRedoCommandCreated が BezierAnimation まで
        // 伝搬しないため、ポイント単位で購読しエフェクトへ転送する。
        // 区間ごとの時間カーブのポイントも、エフェクトへ転送する。
        private void SubscribeBezierPoints()
        {
            SetBezierPointSubscription(true);
        }

        private void UnsubscribeBezierPoints()
        {
            SetBezierPointSubscription(false);
        }

        private void SetBezierPointSubscription(bool subscribe)
        {
            foreach (var point in TimeBezier.Points)
                Set(point);

            foreach (var keyframe in Keyframes)
            foreach (var point in keyframe.SegmentTimeBezier.Points)
                Set(point);

            void Set(BezierAnimationPoint point)
            {
                if (subscribe)
                    point.UndoRedoCommandCreated += BezierPoint_UndoRedoCommandCreated;
                else
                    point.UndoRedoCommandCreated -= BezierPoint_UndoRedoCommandCreated;
            }
        }

        private void BezierPoint_UndoRedoCommandCreated(object? sender, UndoRedoEventArgs e)
        {
            RaiseUndoRedoPointCreatedEvent(sender, e);
        }

        public override void BeginEdit()
        {
            base.BeginEdit();

            // 二重に購読しないよう、いったん外してから付ける
            UnsubscribeBezierPoints();
            SubscribeBezierPoints();
        }

        public override ValueTask EndEditAsync()
        {
            UnsubscribeBezierPoints();
            return base.EndEditAsync();
        }

        // ─────────────────────────────────────────
        //  プレビュー用の公開
        // ─────────────────────────────────────────

        /// <summary>
        /// 現在の設定で、指定フレームでのカメラ状態を求める。
        /// </summary>
        internal CameraState EvaluateCameraState(long frame, long length, int fps)
            => CameraPathSolver.Evaluate(Keyframes, CreateSettings(frame, length, fps), frame, length, fps);

        internal CameraPathSettings CreateSettings(long frame, long length, int fps) => new(
            CurveType,
            (float)CurveRoundness.GetValue(frame, length, fps),
            (float)AngleOvershoot.GetValue(frame, length, fps),
            TimeControl,
            (float)TimeSmoothness.GetValue(frame, length, fps),
            EasingType,
            EasingMode,
            TimeBezier,
            Reverse,
            LookAt);

        // ─────────────────────────────────────────
        //  YMM4 インターフェース
        // ─────────────────────────────────────────

        public override IEnumerable<string> CreateExoVideoFilters(int keyFrameIndex, ExoOutputDescription exoOutputDescription) => [];

        public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
            => new CameraPathEffectProcessor(devices, this);

        protected override IEnumerable<IAnimatable> GetAnimatables()
        {
            // BezierAnimation は IAnimatable ではないので、UndoRedo は
            // コンストラクタの SubscribeChildUndoRedoable で別途つないでいる
            var list = new List<IAnimatable>
            {
                CurveRoundness,
                AngleOvershoot,
                TimeSmoothness,
            };

            foreach (var keyframe in Keyframes)
                list.Add(keyframe);

            return list;
        }
    }
}
