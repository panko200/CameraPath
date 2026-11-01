using System;
using System.Collections.Generic;
using System.Numerics;

namespace CameraPath.Preview
{
    /// <summary>
    /// 内蔵3Dプレビュー用の自由カメラ。
    /// YMM4の描画空間（X=右, Y=下, Z=手前）をそのまま見るため、
    /// 左手系のビュー・投影行列を自前で作る。
    /// </summary>
    internal sealed class PreviewCamera
    {
        private const float MinDistance = 50f;
        private const float MaxDistance = 200000f;
        private const float RotateSensitivity = 0.008f;

        /// <summary>注視点。</summary>
        public Vector3 Target;

        /// <summary>注視点からの距離。</summary>
        public float Distance;

        /// <summary>水平回転（ラジアン）。</summary>
        public float Yaw;

        /// <summary>仰俯角（ラジアン）。</summary>
        public float Pitch;

        /// <summary>垂直画角（ラジアン）。</summary>
        public float FieldOfView = 45f * MathF.PI / 180f;

        public PreviewCamera() => Reset();

        /// <summary>YMM4の標準カメラ位置付近を俯瞰する初期状態に戻す。</summary>
        public void Reset()
        {
            Target = new Vector3(0f, 0f, CameraPathSolver.BasePerspectiveDistance * 0.5f);
            Distance = 3200f;
            Yaw = 0.18f;
            Pitch = 0.30f;
        }

        /// <summary>指定した点群が画面に収まるようにカメラを合わせる。</summary>
        /// <param name="aspect">ビューポートの横幅÷縦幅。省略すると16:9。</param>
        public void Fit(IReadOnlyList<Vector3> points, float aspect = 16f / 9f)
        {
            if (points.Count == 0)
            {
                Reset();
                return;
            }

            if (!float.IsFinite(aspect) || aspect <= 0f)
                aspect = 16f / 9f;

            var min = points[0];
            var max = points[0];
            foreach (var point in points)
            {
                min = Vector3.Min(min, point);
                max = Vector3.Max(max, point);
            }

            var center = (min + max) * 0.5f;
            var radius = MathF.Max(Vector3.Distance(min, max) * 0.5f, 100f);

            Target = center;
            Distance = Math.Clamp(radius / MathF.Sin(FieldOfView * 0.5f), MinDistance, MaxDistance);

            // 外接球で合わせると余白が残るので、実際の投影結果が画面の85%に収まるまで詰める
            for (var iteration = 0; iteration < 4; iteration++)
            {
                var projection = CreateProjection(aspect, 1f);
                var extent = 0f;

                foreach (var point in points)
                {
                    if (!projection.TryProjectPoint(point, out var screen))
                        continue;

                    var ndcX = MathF.Abs(screen.X / aspect * 2f - 1f);
                    var ndcY = MathF.Abs(screen.Y * 2f - 1f);
                    extent = MathF.Max(extent, MathF.Max(ndcX, ndcY));
                }

                if (extent <= 1e-4f)
                    break;

                Distance = Math.Clamp(Distance * extent / 0.85f, MinDistance, MaxDistance);
            }
        }

        public void Orbit(float deltaX, float deltaY)
        {
            Yaw -= deltaX * RotateSensitivity;

            // 左右と同じで、掴んだところがマウスに付いてくる向きにする
            // （下へドラッグすると見下ろし、上へドラッグすると見上げる）
            Pitch = Math.Clamp(Pitch + deltaY * RotateSensitivity, -1.55f, 1.55f);
        }

        public void Zoom(float delta)
        {
            var factor = 1f - delta * 0.0012f;
            Distance = Math.Clamp(Distance * factor, MinDistance, MaxDistance);
        }

        /// <summary>画面上の1画素が、指定した奥行きでどれだけの世界の長さになるか。</summary>
        public float GetWorldPerPixel(float depth, float viewportHeight)
        {
            var height = MathF.Max(1f, viewportHeight);
            var length = 2f * depth * MathF.Tan(FieldOfView * 0.5f) / height;
            return float.IsFinite(length) && length > 0f ? length : 1f;
        }

        /// <summary>画面上のドラッグ量だけ注視点を平行移動する。</summary>
        public void Pan(float deltaX, float deltaY)
        {
            var scale = Distance * 0.0016f;
            var axes = GetViewAxes();

            Target += (-axes.Right * deltaX + axes.Up * deltaY) * scale;
        }

        /// <summary>視点位置。</summary>
        public Vector3 Eye
        {
            get
            {
                var cosPitch = MathF.Cos(Pitch);
                var offset = new Vector3(
                    MathF.Sin(Yaw) * cosPitch,
                    -MathF.Sin(Pitch),
                    MathF.Cos(Yaw) * cosPitch);

                return Target + offset * Distance;
            }
        }

        /// <summary>カメラ空間で「前」とみなす最小のZ。</summary>
        public float NearPlane => MathF.Max(1f, Distance * 0.01f);

        /// <summary>カメラの向きを作る3つの軸。アイテムの3D層も同じ軸を使う。</summary>
        internal (Vector3 Right, Vector3 Up, Vector3 Forward) GetViewAxes()
        {
            var eye = Eye;
            var forward = Target - eye;
            if (forward.LengthSquared() < 1e-9f)
                forward = new Vector3(0f, 0f, -1f);
            forward = Vector3.Normalize(forward);

            // YMM4は上方向が -Y
            var worldUp = new Vector3(0f, -1f, 0f);
            var right = Vector3.Cross(worldUp, forward);
            if (right.LengthSquared() < 1e-9f)
                right = Vector3.UnitX;
            right = Vector3.Normalize(right);

            var up = Vector3.Cross(forward, right);
            return (right, up, forward);
        }

        /// <summary>Z+が前方向になる左手系ビュー行列。</summary>
        public Matrix4x4 GetViewMatrix()
        {
            var eye = Eye;
            var (right, up, forward) = GetViewAxes();

            return new Matrix4x4(
                right.X, up.X, forward.X, 0f,
                right.Y, up.Y, forward.Y, 0f,
                right.Z, up.Z, forward.Z, 0f,
                -Vector3.Dot(right, eye), -Vector3.Dot(up, eye), -Vector3.Dot(forward, eye), 1f);
        }

        /// <summary>Zが [0,1] に入る左手系の透視投影行列。</summary>
        public Matrix4x4 GetProjectionMatrix(float aspect)
        {
            if (!float.IsFinite(aspect) || aspect <= 0f)
                aspect = 16f / 9f;

            var near = NearPlane;
            var far = Distance * 40f + 200000f;
            if (far <= near * 2f)
                far = near * 2f;

            var h = 1f / MathF.Tan(FieldOfView * 0.5f);
            var w = h / aspect;
            var range = far / (far - near);

            return new Matrix4x4(
                w, 0f, 0f, 0f,
                0f, h, 0f, 0f,
                0f, 0f, range, 1f,
                0f, 0f, -range * near, 0f);
        }

        /// <summary>このカメラで3Dの線を画面座標へ落とすための投影器を作る。</summary>
        public PreviewProjection CreateProjection(float width, float height)
            => new(GetViewMatrix(), GetProjectionMatrix(width / MathF.Max(1f, height)), width, height, NearPlane);
    }

    /// <summary>3Dの点・線を画面座標へ変換する。カメラ後方の線はクリップする。</summary>
    internal readonly struct PreviewProjection
    {
        private readonly Matrix4x4 view;
        private readonly Matrix4x4 projection;
        private readonly float width;
        private readonly float height;
        private readonly float near;

        public PreviewProjection(Matrix4x4 view, Matrix4x4 projection, float width, float height, float near)
        {
            this.view = view;
            this.projection = projection;
            this.width = width;
            this.height = height;
            this.near = near;
        }

        /// <summary>カメラ空間へ変換する。</summary>
        public Vector3 ToCameraSpace(Vector3 world) => Vector3.Transform(world, view);

        public bool TryProjectPoint(Vector3 world, out Vector2 screen)
        {
            var cameraSpace = ToCameraSpace(world);
            if (cameraSpace.Z <= near)
            {
                screen = default;
                return false;
            }

            screen = ToScreen(cameraSpace);
            return float.IsFinite(screen.X) && float.IsFinite(screen.Y);
        }

        /// <summary>線分を画面座標へ変換する。視点の後ろに隠れる部分は切り落とす。</summary>
        public bool TryProjectSegment(Vector3 a, Vector3 b, out Vector2 screenA, out Vector2 screenB)
        {
            screenA = default;
            screenB = default;

            var cameraA = ToCameraSpace(a);
            var cameraB = ToCameraSpace(b);

            var visibleA = cameraA.Z > near;
            var visibleB = cameraB.Z > near;

            if (!visibleA && !visibleB)
                return false;

            if (!visibleA)
            {
                var t = (near - cameraA.Z) / (cameraB.Z - cameraA.Z);
                cameraA = Vector3.Lerp(cameraA, cameraB, t);
            }
            else if (!visibleB)
            {
                var t = (near - cameraB.Z) / (cameraA.Z - cameraB.Z);
                cameraB = Vector3.Lerp(cameraB, cameraA, t);
            }

            screenA = ToScreen(cameraA);
            screenB = ToScreen(cameraB);

            return float.IsFinite(screenA.X) && float.IsFinite(screenA.Y)
                && float.IsFinite(screenB.X) && float.IsFinite(screenB.Y);
        }

        private Vector2 ToScreen(Vector3 cameraSpace)
        {
            var clip = Vector4.Transform(new Vector4(cameraSpace, 1f), projection);
            if (MathF.Abs(clip.W) < 1e-9f)
                return new Vector2(float.NaN, float.NaN);

            var ndcX = clip.X / clip.W;
            var ndcY = clip.Y / clip.W;

            return new Vector2(
                (ndcX * 0.5f + 0.5f) * width,
                (1f - (ndcY * 0.5f + 0.5f)) * height);
        }
    }
}
