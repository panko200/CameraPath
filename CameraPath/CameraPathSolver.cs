using System;
using System.Collections.Generic;
using System.Numerics;
using YukkuriMovieMaker.Commons;

namespace CameraPath
{
    /// <summary>
    /// ある1フレーム時点でのカメラ状態。
    /// Position / Rotation は YMM4 標準のカメラと同じ意味を持つ。
    /// </summary>
    internal readonly struct CameraState
    {
        /// <summary>カメラ位置。X=右, Y=下, Z=奥行き（+で奥）</summary>
        public readonly Vector3 Position;

        /// <summary>カメラ角度（度）。</summary>
        public readonly Vector3 Rotation;

        /// <summary>視野角（度）。</summary>
        public readonly float Fov;

        public CameraState(Vector3 position, Vector3 rotation, float fov)
        {
            Position = position;
            Rotation = rotation;
            Fov = fov;
        }

        public static CameraState Default => new(Vector3.Zero, Vector3.Zero, (float)CameraPathSolver.DefaultFovDegrees);
    }

    /// <summary>
    /// 設定点での軌道の接線。
    /// 向きは保存されている座標（X=右, Y=下, Z=奥）での角度で表す。
    /// 先の長さは進行方向側、手前の長さはそこへ入ってくる側の長さ。
    /// </summary>
    internal readonly record struct CameraTangent(
        double Azimuth, double Elevation, double LengthIn, double LengthOut);

    /// <summary>
    /// パスの補間設定。エフェクトから毎フレーム渡される。
    /// </summary>
    internal readonly record struct CameraPathSettings(
        CameraCurveType CurveType,
        float CurveRoundness,
        float AngleOvershoot,
        CameraTimeControl TimeControl,
        float TimeSmoothness,
        EasingType EasingType,
        EasingMode EasingMode,
        BezierAnimation? TimeBezier,
        bool Reverse,
        bool LookAt);

    /// <summary>
    /// 設定点リストから各フレームのカメラ状態を求める計算部分。
    /// エフェクト本体と内蔵3Dプレビューの両方がここを使う。
    /// </summary>
    internal static class CameraPathSolver
    {
        /// <summary>YMM4標準のカメラ視野角。</summary>
        public const double DefaultFovDegrees = 56.7380926;

        public const double MinFovDegrees = 0.1;
        public const double MaxFovDegrees = 179.9;

        /// <summary>
        /// 新しく作る設定点の接線（手前・先）の既定の長さ。
        /// 0 だと3D表示で握りが小さすぎて、毎回数値を入れてからでないと動かせないため。
        /// </summary>
        public const double DefaultTangentLength = 100.0;

        /// <summary>
        /// 新しく作る設定点の注視点の奥行き。
        /// カメラの標準位置（原点）から画面中央の少し奥を見る値。
        /// </summary>
        public const double DefaultLookAtDepth = 1000.0;

        /// <summary>YMM4の基準焦点距離。標準ではカメラはこの距離だけ離れている。</summary>
        public const float BasePerspectiveDistance = 1000f;

        // ─────────────────────────────────────────────────────
        //  カメラ行列
        // ─────────────────────────────────────────────────────

        /// <summary>
        /// カメラ状態を、YMM4の DrawDescription.Camera に乗算する行列へ変換する。
        /// 計算内容はYMM4標準の「カメラ」エフェクトと同じ。
        /// </summary>
        public static Matrix4x4 BuildCameraMatrix(in CameraState state)
        {
            const float degToRad = MathF.PI / 180f;

            // YMM4内部では Z+ が手前なので、ユーザー指定の「奥行き」は符号を反転する
            var position = new Vector3(state.Position.X, state.Position.Y, -state.Position.Z);
            var pivot = new Vector3(0f, 0f, BasePerspectiveDistance);

            // 回転はカメラ自身の位置を中心に行う
            return Matrix4x4.CreateTranslation(-position)
                 * Matrix4x4.CreateRotationY(state.Rotation.Y * degToRad, pivot)
                 * Matrix4x4.CreateRotationX(state.Rotation.X * degToRad, pivot)
                 * Matrix4x4.CreateRotationZ(state.Rotation.Z * degToRad, pivot);
        }

        /// <summary>
        /// カメラの視点位置と姿勢を、ワールド座標（YMM4の描画空間）で求める。
        /// 3Dプレビューの描画に使う。
        /// </summary>
        public static void GetView(in CameraState state, out Vector3 eye, out Vector3 right, out Vector3 up, out Vector3 forward)
        {
            var camera = BuildCameraMatrix(state);
            if (!Matrix4x4.Invert(camera, out var inverse))
                inverse = Matrix4x4.Identity;

            eye = Vector3.Transform(new Vector3(0f, 0f, BasePerspectiveDistance), inverse);
            right = Vector3.TransformNormal(Vector3.UnitX, inverse);
            // カメラ空間の -Y が画面上方向
            up = Vector3.TransformNormal(new Vector3(0f, -1f, 0f), inverse);
            // カメラは -Z 方向を向く
            forward = Vector3.TransformNormal(new Vector3(0f, 0f, -1f), inverse);

            if (right.LengthSquared() > 1e-12f) right = Vector3.Normalize(right);
            if (up.LengthSquared() > 1e-12f) up = Vector3.Normalize(up);
            if (forward.LengthSquared() > 1e-12f) forward = Vector3.Normalize(forward);
        }

        /// <summary>
        /// 視野角(度)から、YMM4が使う透視補正用の距離を求める。
        /// YMM4標準のカメラと同じ計算式。
        /// </summary>
        public static float CalculatePerspectiveDistance(double fovDegrees, int screenHeight)
        {
            if (!double.IsFinite(fovDegrees))
                fovDegrees = DefaultFovDegrees;

            fovDegrees = Math.Clamp(fovDegrees, 0.0, MaxFovDegrees);

            if (fovDegrees < MinFovDegrees)
                return float.PositiveInfinity;

            if (screenHeight <= 0)
                screenHeight = 1080;

            var distance = screenHeight / 2.0 / Math.Tan(fovDegrees * Math.PI / 360.0);
            return !double.IsFinite(distance) || distance <= 0.0 || distance > float.MaxValue
                ? BasePerspectiveDistance
                : (float)distance;
        }

        // ─────────────────────────────────────────────────────
        //  評価
        // ─────────────────────────────────────────────────────

        private struct RawState
        {
            public double Frame;
            public double X, Y, Z;
            public double RotationX, RotationY, RotationZ;
            public double Fov;
            public double TangentAngleH, TangentAngleV, TangentLengthIn, TangentLengthOut;
            public double TargetX, TargetY, TargetZ;

            /// <summary>この設定点から先の区間の時間カーブ（区間ごとのベジェ時間カーブのときだけ使う）。</summary>
            public BezierAnimation? SegmentBezier;

            public static RawState From(CameraKeyframe keyframe, long frame, long length, int fps) => new()
            {
                Frame = keyframe.Frame.GetValue(frame, length, fps),
                X = keyframe.X.GetValue(frame, length, fps),
                Y = keyframe.Y.GetValue(frame, length, fps),
                Z = keyframe.Z.GetValue(frame, length, fps),
                RotationX = keyframe.RotationX.GetValue(frame, length, fps),
                RotationY = keyframe.RotationY.GetValue(frame, length, fps),
                RotationZ = keyframe.RotationZ.GetValue(frame, length, fps),
                Fov = keyframe.Fov.GetValue(frame, length, fps),
                TangentAngleH = keyframe.TangentAngleH.GetValue(frame, length, fps),
                TangentAngleV = keyframe.TangentAngleV.GetValue(frame, length, fps),
                TangentLengthIn = keyframe.TangentLengthIn.GetValue(frame, length, fps),
                TangentLengthOut = keyframe.TangentLengthOut.GetValue(frame, length, fps),
                TargetX = keyframe.TargetX.GetValue(frame, length, fps),
                TargetY = keyframe.TargetY.GetValue(frame, length, fps),
                TargetZ = keyframe.TargetZ.GetValue(frame, length, fps),
                SegmentBezier = keyframe.SegmentTimeBezier,
            };

            public readonly Vector3 Position => new((float)X, (float)Y, (float)Z);

            public readonly Vector3 Target => new((float)TargetX, (float)TargetY, (float)TargetZ);

            public readonly CameraTangent Tangent => new(
                TangentAngleH, TangentAngleV, TangentLengthIn, TangentLengthOut);

            public readonly CameraState ToState() => new(
                new Vector3((float)X, (float)Y, (float)Z),
                new Vector3((float)RotationX, (float)RotationY, (float)RotationZ),
                (float)Fov);
        }

        /// <summary>
        /// 指定フレームでのカメラ状態を求める。
        /// </summary>
        public static CameraState Evaluate(
            IReadOnlyList<CameraKeyframe> keyframes,
            in CameraPathSettings settings,
            long frame,
            long length,
            int fps)
        {
            if (keyframes.Count == 0)
                return CameraState.Default;

            var raw = new RawState[keyframes.Count];
            for (var i = 0; i < raw.Length; i++)
                raw[i] = RawState.From(keyframes[i], frame, length, fps);

            if (raw.Length == 1)
            {
                var single = raw[0].ToState();
                if (settings.LookAt)
                    single = ApplyLookAt(single, raw[0].Target);

                return Sanitize(single);
            }

            var order = BuildOrder(raw, settings.TimeControl);
            GetSegment(raw, order, settings, frame, length, out var segment, out var t);

            var roundness = settings.CurveType == CameraCurveType.Bezier
                ? Math.Clamp(settings.CurveRoundness, 0f, 10f) / 5f
                : 0f;

            // 二次・三次ベジェでは、位置を3Dのベジェ曲線で結ぶ。
            // 角度や視野角は接線の向きとは別の量なので、これまでどおり滑らかに補間する。
            var isBezierPath = settings.CurveType.UsesTangent();

            // 座標は軌道の形そのものなので、行き過ぎの制限をかけない。
            var position = isBezierPath
                ? BezierPosition(raw, order, segment, t, settings.CurveType)
                : new Vector3(
                    (float)Channel(BuildTrack(raw, order, roundness, double.PositiveInfinity, static r => r.X), segment, t),
                    (float)Channel(BuildTrack(raw, order, roundness, double.PositiveInfinity, static r => r.Y), segment, t),
                    (float)Channel(BuildTrack(raw, order, roundness, double.PositiveInfinity, static r => r.Z), segment, t));

            var scalarRoundness = isBezierPath ? 1f : roundness;

            // 角度と視野角は、補間で設定点の値から飛び出す量を制限できる。
            // 値が出たら切り捨てるのではなく、そもそも行き過ぎない回り方（接線の強さ）にする。
            var overshoot = Math.Max(0.0, settings.AngleOvershoot);

            var state = new CameraState(
                position,
                new Vector3(
                    (float)Channel(BuildTrack(raw, order, scalarRoundness, overshoot, static r => r.RotationX), segment, t),
                    (float)Channel(BuildTrack(raw, order, scalarRoundness, overshoot, static r => r.RotationY), segment, t),
                    (float)Channel(BuildTrack(raw, order, scalarRoundness, overshoot, static r => r.RotationZ), segment, t)),
                (float)Channel(BuildTrack(raw, order, scalarRoundness, overshoot, static r => r.Fov), segment, t));

            if (settings.LookAt)
            {
                var target = new Vector3(
                    (float)Channel(BuildTrack(raw, order, scalarRoundness, double.PositiveInfinity, static r => r.TargetX), segment, t),
                    (float)Channel(BuildTrack(raw, order, scalarRoundness, double.PositiveInfinity, static r => r.TargetY), segment, t),
                    (float)Channel(BuildTrack(raw, order, scalarRoundness, double.PositiveInfinity, static r => r.TargetZ), segment, t));

                state = ApplyLookAt(state, target);
            }

            return Sanitize(state);
        }

        /// <summary>
        /// 位置から注視点の方向を向くように、X角度（仰俯角）とY角度（水平）を求める。
        /// Z角度（ロール）はそのまま使う。
        /// </summary>
        private static CameraState ApplyLookAt(in CameraState state, Vector3 target)
        {
            if (!TryGetLookAngles(state.Position, target, out var pitch, out var yaw))
                return state;

            return new CameraState(
                state.Position,
                new Vector3((float)pitch, (float)yaw, state.Rotation.Z),
                state.Fov);
        }

        /// <summary>
        /// 位置から注視点を見るためのX角度（仰俯角）とY角度（水平）を求める。
        /// 保存座標は X=右, Y=下, Z=奥なので、YMM4の描画空間（Z+が手前）に直してから計算する。
        /// </summary>
        /// <remarks>
        /// YMM4のカメラ（BuildCameraMatrix と同じ回転順）で視線が向く方向は
        /// (cosX·sinY, -sinX, -cosX·cosY)。これを注視点への向きに合わせて角度を戻す。
        /// 真上・真下を向くときは水平の角度が決まらないが、視線はどちらでも同じになる。
        /// </remarks>
        public static bool TryGetLookAngles(Vector3 position, Vector3 target, out double pitch, out double yaw)
        {
            const double radToDeg = 180.0 / Math.PI;

            pitch = 0.0;
            yaw = 0.0;

            // カメラの視点は、指定した位置から画面中央の手前（奥行き1000）にある。
            // 注視点はそこから見た向きで決める。
            var dx = (double)target.X - position.X;
            var dy = (double)target.Y - position.Y;
            var dz = (double)position.Z - target.Z - BasePerspectiveDistance;

            var squared = dx * dx + dy * dy + dz * dz;
            if (!(squared > 1e-9) || !double.IsFinite(squared))
                return false;

            var length = Math.Sqrt(squared);
            var ndx = dx / length;
            var ndy = Math.Clamp(dy / length, -1.0, 1.0);
            var ndz = dz / length;

            pitch = -Math.Asin(ndy) * radToDeg;
            yaw = Math.Atan2(ndx, -ndz) * radToDeg;

            return double.IsFinite(pitch) && double.IsFinite(yaw);
        }

        /// <summary>
        /// 設定点1つぶんのカメラ状態を求める。3Dプレビューの目印描画に使う。
        /// </summary>
        public static CameraState EvaluateKeyframe(CameraKeyframe keyframe, long frame, long length, int fps)
            => Sanitize(RawState.From(keyframe, frame, length, fps).ToState());

        // ─────────────────────────────────────────────────────
        //  接線と制御点
        // ─────────────────────────────────────────────────────

        /// <summary>
        /// 方位角と仰俯角から、軌道の向き（保存座標）を求める。
        /// 方位角0°で +X（右）、90°で +Z（奥）。仰俯角は + で上向き（-Y）。
        /// </summary>
        public static Vector3 GetTangentDirection(double azimuthDegrees, double elevationDegrees)
        {
            const double degToRad = Math.PI / 180.0;

            var azimuth = azimuthDegrees * degToRad;
            var elevation = elevationDegrees * degToRad;
            var cosElevation = Math.Cos(elevation);

            return new Vector3(
                (float)(cosElevation * Math.Cos(azimuth)),
                (float)(-Math.Sin(elevation)),
                (float)(cosElevation * Math.Sin(azimuth)));
        }

        /// <summary>
        /// 向きから方位角と仰俯角を求める。真上・真下を向いていると方位角が決まらないので、
        /// そのときは fallbackAzimuth をそのまま残す。
        /// </summary>
        public static void GetTangentAngles(Vector3 direction, double fallbackAzimuth, out double azimuth, out double elevation)
        {
            const double radToDeg = 180.0 / Math.PI;

            if (direction.LengthSquared() < 1e-9f)
            {
                azimuth = fallbackAzimuth;
                elevation = 0.0;
                return;
            }

            var directionUnit = Vector3.Normalize(direction);
            var sinElevation = (double)Math.Clamp(-directionUnit.Y, -1f, 1f);
            elevation = Math.Asin(sinElevation) * radToDeg;

            var cosElevation = Math.Sqrt(Math.Max(0.0, 1.0 - sinElevation * sinElevation));
            azimuth = cosElevation < 1e-4
                ? fallbackAzimuth
                : Math.Atan2(directionUnit.Z, directionUnit.X) * radToDeg;
        }

        /// <summary>設定点1つぶんの接線を求める。3Dプレビューの制御点描画に使う。</summary>
        public static CameraTangent EvaluateTangent(CameraKeyframe keyframe, long frame, long length, int fps)
            => RawState.From(keyframe, frame, length, fps).Tangent;

        /// <summary>設定点1つぶんの注視点を求める。3Dプレビューの目印描画に使う。</summary>
        public static Vector3 EvaluateTarget(CameraKeyframe keyframe, long frame, long length, int fps)
            => RawState.From(keyframe, frame, length, fps).Target;

        /// <summary>
        /// 保存座標（X=右, Y=下, Z=奥）の向きを、3Dプレビューの世界座標（Z=手前）の向きへ移す。
        /// </summary>
        public static Vector3 ToPreviewSpace(Vector3 direction)
            => new(direction.X, direction.Y, -direction.Z);

        /// <summary>
        /// 二次・三次ベジェの曲線上の位置を求める。
        /// 二次ベジェは手前と先の制御点の中点を制御点にするので、
        /// 三次ベジェと同じ接線の設定をそのまま使える。
        /// </summary>
        private static Vector3 BezierPosition(
            RawState[] raw,
            int[] order,
            int segment,
            float t,
            CameraCurveType curveType)
        {
            var count = order.Length;
            var first = raw[order[segment]];
            var second = raw[order[Math.Min(segment + 1, count - 1)]];

            var start = first.Position;
            var end = second.Position;
            var outControl = OutControlPoint(first);
            var inControl = InControlPoint(second);
            var u = 1f - t;

            if (curveType == CameraCurveType.QuadraticBezier)
            {
                var control = (outControl + inControl) * 0.5f;
                return start * (u * u) + control * (2f * u * t) + end * (t * t);
            }

            return start * (u * u * u)
                 + outControl * (3f * u * u * t)
                 + inControl * (3f * u * t * t)
                 + end * (t * t * t);
        }

        /// <summary>設定点から先へ伸びる制御点。</summary>
        private static Vector3 OutControlPoint(in RawState raw)
            => raw.Position + GetTangentDirection(raw.TangentAngleH, raw.TangentAngleV) * (float)raw.TangentLengthOut;

        /// <summary>設定点へ手前から入ってくる制御点。</summary>
        private static Vector3 InControlPoint(in RawState raw)
            => raw.Position - GetTangentDirection(raw.TangentAngleH, raw.TangentAngleV) * (float)raw.TangentLengthIn;

        /// <summary>NaN等が混ざっても描画を壊さないように補正する。</summary>
        private static CameraState Sanitize(in CameraState state)
        {
            static float Fix(float value) => float.IsFinite(value) ? value : 0f;

            var fov = Fix(state.Fov);
            if (fov < MinFovDegrees || fov > MaxFovDegrees)
                fov = (float)DefaultFovDegrees;

            return new CameraState(
                new Vector3(Fix(state.Position.X), Fix(state.Position.Y), Fix(state.Position.Z)),
                new Vector3(Fix(state.Rotation.X), Fix(state.Rotation.Y), Fix(state.Rotation.Z)),
                fov);
        }

        /// <summary>
        /// キーフレーム時刻モードではフレーム番号順、それ以外では登録順に並べる。
        /// </summary>
        private static int[] BuildOrder(RawState[] raw, CameraTimeControl timeControl)
        {
            var order = new int[raw.Length];
            for (var i = 0; i < order.Length; i++)
                order[i] = i;

            if (timeControl is CameraTimeControl.KeyframeTime or CameraTimeControl.SegmentBezier)
                Array.Sort(order, (a, b) => raw[a].Frame.CompareTo(raw[b].Frame));

            return order;
        }

        /// <summary>
        /// 現在フレームがどの区間のどこにあるかを求める。
        /// </summary>
        private static void GetSegment(
            RawState[] raw,
            int[] order,
            in CameraPathSettings settings,
            long frame,
            long length,
            out int segment,
            out float t)
        {
            var count = order.Length;

            if (settings.TimeControl is CameraTimeControl.KeyframeTime or CameraTimeControl.SegmentBezier)
            {
                var first = raw[order[0]].Frame;
                var last = raw[order[count - 1]].Frame;

                if (last - first <= 0.0)
                {
                    segment = 0;
                    t = frame >= first ? 1f : 0f;
                    return;
                }

                if (frame <= first) { segment = 0; t = 0f; return; }
                if (frame >= last) { segment = count - 2; t = 1f; return; }

                for (var i = 0; i < count - 1; i++)
                {
                    var from = raw[order[i]].Frame;
                    var to = raw[order[i + 1]].Frame;
                    if (frame < from || frame > to)
                        continue;

                    segment = i;
                    var local = to - from <= 0.0 ? 1.0 : (frame - from) / (to - from);
                    t = settings.TimeControl == CameraTimeControl.SegmentBezier
                        ? SegmentProgress(raw[order[i]].SegmentBezier, (float)local)
                        : SmoothInterpolate((float)local, settings.TimeSmoothness);
                    return;
                }

                segment = count - 2;
                t = 1f;
                return;
            }

            // イージング関数 / ベジェ時間カーブ：アイテム全体の時間を 0〜1 に正規化してから使う
            var total = Math.Max(1f, length - 1);
            var rate = Math.Clamp((float)frame / total, 0f, 1f);
            if (settings.Reverse)
                rate = 1f - rate;

            var progress = settings.TimeControl == CameraTimeControl.Easing
                ? Easing.GetValue(settings.EasingType, settings.EasingMode, rate)
                : settings.TimeBezier?.GetAnimation(rate) ?? rate;

            progress = Math.Clamp(progress, 0.0, 1.0);

            var position = progress * (count - 1);
            var index = Math.Clamp((int)Math.Floor(position), 0, count - 2);
            segment = index;
            t = (float)Math.Clamp(position - index, 0.0, 1.0);
        }

        /// <summary>
        /// 区間の時間カーブで、区間内の進み（0〜1）を求める。
        /// カーブが無いときや壊れているときは、そのままの割合を使う。
        /// </summary>
        private static float SegmentProgress(BezierAnimation? animation, float local)
        {
            var rate = Math.Clamp(local, 0f, 1f);
            if (animation is null)
                return rate;

            var progress = animation.GetAnimation(rate);
            return double.IsFinite(progress) ? (float)Math.Clamp(progress, 0.0, 1.0) : rate;
        }

        /// <summary>
        /// ひとつのチャンネル（角度や視野角など）の、各設定点での値と接線。
        /// 接線は、補間で行き過ぎないように調整したもの。
        /// </summary>
        private readonly struct ChannelTrack
        {
            public readonly double[] Values;
            public readonly double[] Tangents;

            /// <summary>丸みが0で、ただの直線として結ぶか。</summary>
            public readonly bool Linear;

            public ChannelTrack(double[] values, double[] tangents, bool linear)
            {
                Values = values;
                Tangents = tangents;
                Linear = linear;
            }
        }

        /// <summary>
        /// ひとつのチャンネルの値を取り出し、補間で両端の値から飛び出さないような接線を求める。
        /// overshoot は飛び出してよい量。0 なら設定点の値の範囲から外れない。
        /// double.PositiveInfinity なら、これまでどおり制限なしの接線を使う。
        /// </summary>
        /// <remarks>
        /// 値が出てから切り捨てると動きが途中で止まって見えるので、
        /// 接線（速度）のほうを抑えて、そもそも行き過ぎないカーブにする。
        /// 接線は設定点ごとに共有なので、前後の区間でなめらかに繋がる。
        /// </remarks>
        private static ChannelTrack BuildTrack(
            RawState[] raw,
            int[] order,
            float roundness,
            double overshoot,
            Func<RawState, double> select)
        {
            var count = order.Length;
            var values = new double[count];
            for (var i = 0; i < count; i++)
                values[i] = select(raw[order[i]]);

            if (roundness <= 0f || count < 2)
                return new ChannelTrack(values, [], true);

            var natural = new double[count];
            var limited = new double[count];

            for (var i = 0; i < count; i++)
            {
                // 端では、隣の点を鏡像で補う
                var prev = i - 1 >= 0 ? values[i - 1] : 2.0 * values[i] - values[i + 1];
                var next = i + 1 < count ? values[i + 1] : 2.0 * values[i] - values[i - 1];

                natural[i] = roundness * (next - prev) * 0.5;
                limited[i] = LimitTangent(prev, values[i], next, natural[i]);
            }

            if (!double.IsFinite(overshoot))
                return new ChannelTrack(values, natural, false);

            // 許容量に収まるうちで、いちばん自然な（滑らかな）接線を選ぶ
            var weight = 0.0;
            if (WithinOvershoot(values, natural, limited, 1.0, overshoot))
            {
                weight = 1.0;
            }
            else if (overshoot > 0.0)
            {
                var low = 0.0;
                var high = 1.0;
                for (var i = 0; i < 20; i++)
                {
                    var middle = (low + high) * 0.5;
                    if (WithinOvershoot(values, natural, limited, middle, overshoot))
                        low = middle;
                    else
                        high = middle;
                }

                weight = low;
            }

            if (weight >= 1.0)
                return new ChannelTrack(values, natural, false);

            var tangents = new double[count];
            for (var i = 0; i < count; i++)
                tangents[i] = limited[i] + (natural[i] - limited[i]) * weight;

            return new ChannelTrack(values, tangents, false);
        }

        /// <summary>
        /// 設定点での接線を、値が両端の範囲から飛び出さない向き・大きさに抑える。
        /// 前後で値の増減が食い違う（山や谷）のときは 0 にする。
        /// </summary>
        private static double LimitTangent(double prev, double current, double next, double tangent)
        {
            var before = current - prev;
            var after = next - current;

            if (before * after <= 0.0)
                return 0.0;

            var limit = 3.0 * Math.Min(Math.Abs(before), Math.Abs(after));

            return after > 0.0
                ? Math.Clamp(tangent, 0.0, limit)
                : Math.Clamp(tangent, -limit, 0.0);
        }

        /// <summary>
        /// 接線を limited と natural の間で混ぜたとき、どの区間も行き過ぎが overshoot 以内に収まるか。
        /// </summary>
        private static bool WithinOvershoot(
            double[] values,
            double[] natural,
            double[] limited,
            double weight,
            double overshoot)
        {
            for (var i = 0; i < values.Length - 1; i++)
            {
                var m0 = limited[i] + (natural[i] - limited[i]) * weight;
                var m1 = limited[i + 1] + (natural[i + 1] - limited[i + 1]) * weight;

                GetCurveRange(values[i], values[i + 1], m0, m1, out var low, out var high);

                if (high > Math.Max(values[i], values[i + 1]) + overshoot + 1e-9)
                    return false;

                if (low < Math.Min(values[i], values[i + 1]) - overshoot - 1e-9)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// エルミート補間した曲線が、区間の中でどこまで動くかを求める。
        /// </summary>
        private static void GetCurveRange(
            double from, double to, double m0, double m1, out double low, out double high)
        {
            low = Math.Min(from, to);
            high = Math.Max(from, to);

            // h(t) = from + m0·t + c2·t² + c3·t³
            var delta = to - from;
            var c2 = 3.0 * delta - 2.0 * m0 - m1;
            var c3 = -2.0 * delta + m0 + m1;

            // h'(t) = m0 + 2·c2·t + 3·c3·t² の根で曲線が折り返す
            foreach (var root in QuadraticRoots(3.0 * c3, 2.0 * c2, m0))
            {
                if (!(root > 0.0 && root < 1.0))
                    continue;

                var value = Hermite(from, to, m0, m1, root);
                if (!double.IsFinite(value))
                    continue;

                low = Math.Min(low, value);
                high = Math.Max(high, value);
            }
        }

        private static IEnumerable<double> QuadraticRoots(double a, double b, double c)
        {
            if (Math.Abs(a) < 1e-12)
            {
                if (Math.Abs(b) > 1e-12)
                    yield return -c / b;

                yield break;
            }

            var discriminant = b * b - 4.0 * a * c;
            if (discriminant < 0.0)
                yield break;

            var sqrt = Math.Sqrt(discriminant);
            yield return (-b + sqrt) / (2.0 * a);
            yield return (-b - sqrt) / (2.0 * a);
        }

        /// <summary>
        /// ひとつのチャンネルを、区間の中の割合 t で補間する。
        /// </summary>
        private static double Channel(ChannelTrack track, int segment, float t)
        {
            var count = track.Values.Length;
            var index2 = Math.Min(segment + 1, count - 1);

            var from = track.Values[segment];
            var to = track.Values[index2];

            if (track.Linear || track.Tangents.Length != count)
                return from + (to - from) * t;

            return Hermite(from, to, track.Tangents[segment], track.Tangents[index2], t);
        }

        private static double Hermite(double from, double to, double m0, double m1, double t)
        {
            double t2 = t * t;
            double t3 = t2 * t;

            return (2 * t3 - 3 * t2 + 1) * from
                 + (t3 - 2 * t2 + t) * m0
                 + (-2 * t3 + 3 * t2) * to
                 + (t3 - t2) * m1;
        }

        /// <summary>
        /// 0=カクカク, 10=滑らか。キーフレーム時刻モードでの時間方向の平滑化。
        /// </summary>
        private static float SmoothInterpolate(float t, float smoothness)
        {
            if (smoothness <= 0f)
                return t;

            var factor = Math.Clamp(smoothness, 0f, 10f) / 10f;
            var smooth = t * t * (3f - 2f * t);
            return t + (smooth - t) * factor;
        }

        // ─────────────────────────────────────────────────────
        //  3Dプレビュー用
        // ─────────────────────────────────────────────────────

        /// <summary>
        /// パスを描画するためのフレーム範囲を求める。
        /// </summary>
        public static void GetFrameRange(
            IReadOnlyList<CameraKeyframe> keyframes,
            CameraTimeControl timeControl,
            long length,
            out long start,
            out long end)
        {
            if ((timeControl is CameraTimeControl.KeyframeTime or CameraTimeControl.SegmentBezier)
                && keyframes.Count > 0)
            {
                double min = double.MaxValue;
                double max = double.MinValue;
                foreach (var keyframe in keyframes)
                {
                    var value = keyframe.Frame.GetValue(0L, 1L, 30);
                    min = Math.Min(min, value);
                    max = Math.Max(max, value);
                }

                start = (long)Math.Floor(min);
                end = (long)Math.Ceiling(max);
                if (end <= start)
                    end = start + 1;
                return;
            }

            start = 0;
            end = Math.Max(1, length - 1);
        }

        /// <summary>
        /// パス上を等間隔にサンプリングする。3Dプレビューの線を引くために使う。
        /// </summary>
        public static void SamplePath(
            IReadOnlyList<CameraKeyframe> keyframes,
            in CameraPathSettings settings,
            long length,
            int fps,
            int sampleCount,
            List<CameraState> destination)
        {
            destination.Clear();

            if (keyframes.Count == 0)
            {
                destination.Add(CameraState.Default);
                return;
            }

            GetFrameRange(keyframes, settings.TimeControl, length, out var start, out var end);
            var count = Math.Max(2, sampleCount);

            for (var i = 0; i < count; i++)
            {
                var frame = start + (long)Math.Round((end - start) * (i / (double)(count - 1)));
                destination.Add(Evaluate(keyframes, settings, frame, length, fps));
            }
        }
    }
}
