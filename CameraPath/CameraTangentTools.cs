using System;
using System.Collections.Immutable;
using System.Numerics;
using YukkuriMovieMaker.Commons;

namespace CameraPath
{
    /// <summary>
    /// 3Dプレビューから接線（制御点）を編集するための道具。
    /// 値の書き込みと、前後の設定点からの接線の自動計算をまとめる。
    /// </summary>
    /// <remarks>
    /// 接線の角度と長さは設定点ごとのアニメーションなので、
    /// 時間方向のキーフレームが無い場合は先頭の値を書き換える。
    /// </remarks>
    internal static class CameraTangentTools
    {
        /// <summary>編集用に見るときのフレーム（設定点の基準値を見るため）。</summary>
        private const long EditFrame = 0L;
        private const long EditLength = 1L;
        private const int EditFps = 30;

        /// <summary>接線が未設定（両方0）の設定点に、前後の設定点から接線を入れる。</summary>
        public static ImmutableList<CameraKeyframe> WithAutoTangents(ImmutableList<CameraKeyframe> keyframes)
        {
            if (keyframes.Count == 0)
                return keyframes;

            for (var i = 0; i < keyframes.Count; i++)
            {
                var keyframe = keyframes[i];
                var tangent = CameraPathSolver.EvaluateTangent(keyframe, EditFrame, EditLength, EditFps);

                // すでに長さが入っている設定点は、ユーザーが決めた接線として触らない
                if (!IsUntouched(tangent))
                    continue;

                SetAutoTangent(
                    keyframe,
                    i > 0 ? Position(keyframes[i - 1]) : null,
                    i < keyframes.Count - 1 ? Position(keyframes[i + 1]) : null);
            }

            return keyframes;
        }

        /// <summary>
        /// 前後の設定点から、この設定点の接線（向きと長さ）を自動で決める。
        /// 前後を結ぶ向きを接線にして、長さは隣までの距離の1/3にする。
        /// これで、自動のベジェ曲線と同じような滑らかな軌道になる。
        /// </summary>
        public static void SetAutoTangent(CameraKeyframe keyframe, Vector3? previous, Vector3? next)
        {
            var current = Position(keyframe);

            var direction = previous.HasValue && next.HasValue
                ? next.Value - previous.Value
                : next.HasValue
                    ? next.Value - current
                    : previous.HasValue
                        ? current - previous.Value
                        : Vector3.Zero;

            if (direction.LengthSquared() < 1e-6f)
                return;

            var tangent = CameraPathSolver.EvaluateTangent(keyframe, EditFrame, EditLength, EditFps);
            CameraPathSolver.GetTangentAngles(direction, tangent.Azimuth, out var azimuth, out var elevation);

            var lengthIn = previous.HasValue ? Math.Max(1.0, (current - previous.Value).Length() / 3.0) : 0.0;
            var lengthOut = next.HasValue ? Math.Max(1.0, (next.Value - current).Length() / 3.0) : 0.0;

            if (previous is null)
                lengthIn = lengthOut;
            if (next is null)
                lengthOut = lengthIn;

            SetValue(keyframe.TangentAngleH, EditFrame, azimuth);
            SetValue(keyframe.TangentAngleV, EditFrame, elevation);
            SetValue(keyframe.TangentLengthIn, EditFrame, lengthIn);
            SetValue(keyframe.TangentLengthOut, EditFrame, lengthOut);
        }

        /// <summary>
        /// 接線がまだ決まっていないか。
        /// 長さが 0 のもの（以前のファイル）と、新規作成時の既定値
        /// （長さが既定値どうしで、向きも0°）のものを未設定とみなす。
        /// </summary>
        private static bool IsUntouched(in CameraTangent tangent)
        {
            if (tangent.LengthIn == 0.0 && tangent.LengthOut == 0.0)
                return true;

            const double tolerance = 1e-6;

            return Math.Abs(tangent.LengthIn - CameraPathSolver.DefaultTangentLength) < tolerance
                && Math.Abs(tangent.LengthOut - CameraPathSolver.DefaultTangentLength) < tolerance
                && Math.Abs(tangent.Azimuth) < tolerance
                && Math.Abs(tangent.Elevation) < tolerance;
        }

        /// <summary>設定点の位置（保存座標）。</summary>
        public static Vector3 Position(CameraKeyframe keyframe)
            => CameraPathSolver.EvaluateKeyframe(keyframe, EditFrame, EditLength, EditFps).Position;

        /// <summary>
        /// アニメーションの値を書き換える。
        /// 時間方向のキーフレームが無ければ先頭の値、あれば今のフレームにいちばん近いキーフレームの値を書き換える。
        /// </summary>
        public static void SetValue(Animation animation, long frame, double value)
        {
            var keyFrames = animation.KeyFrames;

            if (keyFrames is null || keyFrames.Frames.Count == 0)
            {
                if (animation.Values.Count > 0)
                    animation.Values[0].Value = value;
                return;
            }

            var index = 1;
            var nearest = double.MaxValue;

            for (var i = 0; i < keyFrames.Frames.Count; i++)
            {
                var distance = Math.Abs(frame - keyFrames.Frames[i]);
                if (distance >= nearest)
                    continue;

                nearest = distance;
                index = i + 1;
            }

            if (index < animation.Values.Count)
                animation.Values[index].Value = value;
        }
    }
}
