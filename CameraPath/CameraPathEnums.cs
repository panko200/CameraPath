using System.ComponentModel.DataAnnotations;

namespace CameraPath
{
    /// <summary>
    /// 設定点どうしを結ぶ軌道の種類。
    /// </summary>
    public enum CameraCurveType
    {
        [Display(Name = "直線", Description = "設定点どうしを直線で結びます。")]
        Straight = 1,

        [Display(Name = "ベジェ曲線", Description = "設定点を滑らかな曲線（カトムルム・ロムスプライン）で結びます。曲線の形は自動で決まり、細かい調整はできません。")]
        Bezier = 2,

        [Display(Name = "二次ベジェ曲線", Description = "設定点の接線から制御点をひとつ求めて結びます。三次より単純な曲線です。\n3Dプレビューで設定点や制御点をつかんで動かせます。")]
        QuadraticBezier = 3,

        [Display(Name = "三次ベジェ曲線", Description = "設定点ごとの接線と長さから制御点をふたつ求めて結びます。自由度が高く、好きな軌道を作れます。\n3Dプレビューで設定点や制御点をつかんで動かせます。")]
        CubicBezier = 4,
    }

    /// <summary>
    /// 軌道の種類ごとの見分け方。
    /// </summary>
    internal static class CameraCurveTypeExtensions
    {
        /// <summary>設定点の接線（制御点）を使って曲線を作る種類か。</summary>
        public static bool UsesTangent(this CameraCurveType curveType)
            => curveType is CameraCurveType.QuadraticBezier or CameraCurveType.CubicBezier;
    }

    /// <summary>
    /// パス上の進行を何で制御するか。
    /// </summary>
    public enum CameraTimeControl
    {
        [Display(Name = "キーフレーム時刻", Description = "各設定点に設定したフレーム番号に従ってカメラを移動します。")]
        KeyframeTime = 1,

        [Display(Name = "イージング関数", Description = "アイテム全体の時間に対して、イージング関数に従ってパスを進みます。")]
        Easing = 2,

        [Display(Name = "ベジェ時間カーブ", Description = "ベジェ曲線エディタで時間に対する進行カーブを自由に編集します。")]
        TimeBezier = 4,

        [Display(Name = "区間ごとのベジェ時間カーブ", Description = "設定点どうしの間ごとに、時間に対する進行カーブをベジェ曲線で編集します。\n各設定点のフレーム番号は、その区間の長さとして使います。")]
        SegmentBezier = 8,
    }
}
