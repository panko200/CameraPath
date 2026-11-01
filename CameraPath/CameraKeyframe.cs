using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes;

namespace CameraPath
{
    /// <summary>
    /// カメラパスの設定点。
    /// ひとつの設定点が、カメラのXYZ座標・XYZ角度・視野角をまとめて持つ。
    /// </summary>
    public class CameraKeyframe : Animatable
    {
        [Display(GroupName = "時間", Name = "フレーム", Description = "この設定点に到達するフレーム番号です。（時間の制御が「キーフレーム時刻」のときのみ使用します）")]
        [AnimationSlider("F0", "f", 0, 300)]
        public Animation Frame { get; } = new Animation(0, 0, 1000000);

        [Display(GroupName = "位置", Name = "X座標", Description = "カメラの位置（横方向）。+で右に移動します。")]
        [AnimationSlider("F0", "px", -500, 500)]
        public Animation X { get; } = new Animation(0, -100000, 100000);

        [Display(GroupName = "位置", Name = "Y座標", Description = "カメラの位置（縦方向）。+で下に移動します。")]
        [AnimationSlider("F0", "px", -500, 500)]
        public Animation Y { get; } = new Animation(0, -100000, 100000);

        [Display(GroupName = "位置", Name = "Z座標", Description = "カメラの位置（奥行き）。+で奥に移動します。\n標準ではカメラは原点から1000の距離にあるので、1000に近づけるほど被写体は大きく写ります。")]
        [AnimationSlider("F0", "px", -500, 500)]
        public Animation Z { get; } = new Animation(0, -100000, 100000);

        [Display(GroupName = "角度", Name = "X角度", Description = "カメラの仰俯角です。\n「注視点を使う」がONのときは、注視点から自動で決まるため使いません。")]
        [ShowPropertyEditorWhen(nameof(UseLookAt), false)]
        [AnimationSlider("F1", "°", -360, 360)]
        public Animation RotationX { get; } = new Animation(0, -100000, 100000);

        [Display(GroupName = "角度", Name = "Y角度", Description = "カメラの水平回転です。\n「注視点を使う」がONのときは、注視点から自動で決まるため使いません。")]
        [ShowPropertyEditorWhen(nameof(UseLookAt), false)]
        [AnimationSlider("F1", "°", -360, 360)]
        public Animation RotationY { get; } = new Animation(0, -100000, 100000);

        [Display(GroupName = "角度", Name = "Z角度", Description = "カメラのロール（画面の回転）です。")]
        [AnimationSlider("F1", "°", -360, 360)]
        public Animation RotationZ { get; } = new Animation(0, -100000, 100000);

        [Display(GroupName = "視野角", Name = "視野角", Description = "カメラの視野角です。YMM4標準のカメラと同じく56.738°が基準です。\n90°に近づけるほど広角、30°に近づけるほど望遠になります。")]
        [AnimationSlider("F1", "°", 0, 179)]
        public Animation Fov { get; } = new Animation(CameraPathSolver.DefaultFovDegrees, CameraPathSolver.MinFovDegrees, CameraPathSolver.MaxFovDegrees);

        // ─────────────────────────────
        //  接線（二次・三次ベジェ曲線のときだけ使う）
        // ─────────────────────────────

        [Display(GroupName = "軌道の接線", Name = "方位角", Description = "この設定点で軌道が向かう向き（横回り）です。0°で右（+X）、90°で奥（+Z）、-90°で手前（-Z）へ進みます。\n軌道の種類が二次・三次ベジェ曲線のときに使います。", Order = 400)]
        [AnimationSlider("F1", "°", -360, 360)]
        public Animation TangentAngleH { get; } = new Animation(0, -100000, 100000);

        [Display(GroupName = "軌道の接線", Name = "仰俯角", Description = "この設定点で軌道が向かう向き（上下）です。+で上向き、-で下向きになります。\n軌道の種類が二次・三次ベジェ曲線のときに使います。", Order = 410)]
        [AnimationSlider("F1", "°", -90, 90)]
        public Animation TangentAngleV { get; } = new Animation(0, -100000, 100000);

        [Display(GroupName = "軌道の接線", Name = "手前の長さ", Description = "この設定点へ入ってくるときの接線の長さです。0にすると角ばった軌道になります。\n軌道の種類が二次・三次ベジェ曲線のときに使います。", Order = 420)]
        [AnimationSlider("F1", "px", 0, 500)]
        public Animation TangentLengthIn { get; } = new Animation(CameraPathSolver.DefaultTangentLength, 0, 100000);

        [Display(GroupName = "軌道の接線", Name = "先の長さ", Description = "この設定点から出ていくときの接線の長さです。0にすると角ばった軌道になります。\n軌道の種類が二次・三次ベジェ曲線のときに使います。", Order = 430)]
        [AnimationSlider("F1", "px", 0, 500)]
        public Animation TangentLengthOut { get; } = new Animation(CameraPathSolver.DefaultTangentLength, 0, 100000);

        // ─────────────────────────────
        //  注視点（「注視点を使う」がONのときだけ使う）
        // ─────────────────────────────

        [Display(GroupName = "注視点", Name = "注視点X", Description = "「注視点を使う」がONのとき、カメラが向く点のX座標です。\nOFFのときは角度の設定がそのまま使われます。", Order = 500)]
        [ShowPropertyEditorWhen(nameof(UseLookAt), true)]
        [AnimationSlider("F0", "px", -1000, 1000)]
        public Animation TargetX { get; } = new Animation(0, -100000, 100000);

        [Display(GroupName = "注視点", Name = "注視点Y", Description = "「注視点を使う」がONのとき、カメラが向く点のY座標です。+で下になります。\nOFFのときは角度の設定がそのまま使われます。", Order = 510)]
        [ShowPropertyEditorWhen(nameof(UseLookAt), true)]
        [AnimationSlider("F0", "px", -1000, 1000)]
        public Animation TargetY { get; } = new Animation(0, -100000, 100000);

        [Display(GroupName = "注視点", Name = "注視点Z", Description = "「注視点を使う」がONのとき、カメラが向く点の奥行きです。標準では1000で、画面中央の少し奥を見ます。\nOFFのときは角度の設定がそのまま使われます。", Order = 520)]
        [ShowPropertyEditorWhen(nameof(UseLookAt), true)]
        [AnimationSlider("F0", "px", -1000, 3000)]
        public Animation TargetZ { get; } = new Animation(CameraPathSolver.DefaultLookAtDepth, -100000, 100000);

        /// <summary>
        /// この設定点から次の設定点までの時間カーブ。
        /// 「時間の制御」が「区間ごとのベジェ時間カーブ」のときだけ使う。
        /// この設定点のフレーム番号から次の設定点のフレーム番号までが、このカーブの横軸になる。
        /// </summary>
        public BezierAnimation SegmentTimeBezier { get; } = new BezierAnimation();

        /// <summary>
        /// エフェクト本体の「注視点を使う」の状態。
        /// 設定点エディタが開いたとき・切り替えたときに同期され、
        /// 角度と注視点のどちらの項目を表示するかの判定にだけ使う。
        /// 表示の切り替えにだけ使う値で、正となるのはエフェクト本体の設定のほう。
        /// </summary>
        public bool UseLookAt
        {
            get => useLookAt;
            set => SetWithoutUndoRedo(ref useLookAt, value);
        }
        private bool useLookAt;


        public CameraKeyframe()
        {
        }

        public CameraKeyframe(int frame, double x, double y, double z, double rotationX, double rotationY, double rotationZ, double fov)
            : this(frame, x, y, z, rotationX, rotationY, rotationZ, fov,
                0, 0, CameraPathSolver.DefaultTangentLength, CameraPathSolver.DefaultTangentLength)
        {
        }

        public CameraKeyframe(
            int frame, double x, double y, double z,
            double rotationX, double rotationY, double rotationZ, double fov,
            double tangentAngleH, double tangentAngleV, double tangentLengthIn, double tangentLengthOut)
        {
            Frame.ActiveValues.First().Value = frame;
            X.ActiveValues.First().Value = x;
            Y.ActiveValues.First().Value = y;
            Z.ActiveValues.First().Value = z;
            RotationX.ActiveValues.First().Value = rotationX;
            RotationY.ActiveValues.First().Value = rotationY;
            RotationZ.ActiveValues.First().Value = rotationZ;
            Fov.ActiveValues.First().Value = fov;
            TangentAngleH.ActiveValues.First().Value = tangentAngleH;
            TangentAngleV.ActiveValues.First().Value = tangentAngleV;
            TangentLengthIn.ActiveValues.First().Value = tangentLengthIn;
            TangentLengthOut.ActiveValues.First().Value = tangentLengthOut;
        }

        protected override IEnumerable<IAnimatable> GetAnimatables()
        {
            return [Frame, X, Y, Z, RotationX, RotationY, RotationZ, Fov,
                TangentAngleH, TangentAngleV, TangentLengthIn, TangentLengthOut,
                TargetX, TargetY, TargetZ];
        }
    }
}
