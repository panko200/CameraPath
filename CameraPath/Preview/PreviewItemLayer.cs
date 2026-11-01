using System;
using System.Collections.Generic;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace CameraPath.Preview
{
    /// <summary>3D空間の四角形に貼る画像（アイテムの中身）。A→B→C→D の順に回る。</summary>
    internal readonly record struct PreviewImageQuad(
        Vector3 A, Vector3 B, Vector3 C, Vector3 D, ImageSource Image);

    /// <summary>
    /// アイテムの絵を3Dの板として貼る層。
    ///
    /// 2Dの描画（DrawingContext）では透視投影をそのまま扱えないため、
    /// 四角形を細かく分割して1区画ずつアフィン変換で貼るしかない。
    /// この方法はアイテムが画面へ大きく傾くほどずれが広がり、絵が崩れてしまう。
    /// WPFの3D（Viewport3D）なら透視補正も奥行きの前後関係も3D側が受け持つので、
    /// どの角度から見てもアイテムが正しく並ぶ。
    ///
    /// 座標はYMM4の描画空間（X=右, Y=下, Z=手前）のまま受け取り、
    /// WPFの3D（Yが上）へ移すときにYの符号だけを反転する。
    /// カメラも同じ変換で渡すので、2D側のソフトウェア投影と見え方が一致する。
    /// </summary>
    internal sealed class PreviewItemLayer : Viewport3D
    {
        private readonly ModelVisual3D root = new();
        private readonly ModelVisual3D lights = new();
        private readonly PerspectiveCamera camera = new();

        public PreviewItemLayer()
        {
            // 絵は DiffuseMaterial で貼る。発光マテリアルだと重なったところが
            // 加算になって白く飛び、後ろのアイテムが見えなくなってしまう。
            // 拡散マテリアルは環境光だけで照らすと、貼った絵がそのままの色で出る。
            lights.Content = new AmbientLight(Colors.White);
            Children.Add(lights);
            Children.Add(root);
            Camera = camera;
            ClipToBounds = true;

            // マウスは上に重ねた2D側（線を描く層）で受ける
            IsHitTestVisible = false;
        }

        /// <summary>貼る画像を入れ替える。null の画像は枠だけになるので飛ばす。</summary>
        public void SetItems(IReadOnlyList<PreviewImageQuad>? items)
        {
            root.Children.Clear();

            if (items is null)
                return;

            foreach (var quad in items)
            {
                if (quad.Image is null || quad.Image.Width <= 0.0 || quad.Image.Height <= 0.0)
                    continue;

                root.Children.Add(CreateVisual(quad));
            }
        }

        /// <summary>
        /// 2D側のカメラに合わせる。
        /// 2D側は描画のたびにこれを呼ぶので、カメラを回したらそのまま追従する。
        /// </summary>
        public void SyncCamera(PreviewCamera source, double width, double height)
        {
            var aspect = width > 1.0 && height > 1.0 ? width / height : 16.0 / 9.0;
            var (_, up, forward) = source.GetViewAxes();

            camera.Position = To3D(source.Eye);
            camera.LookDirection = To3DVector(forward);
            camera.UpDirection = To3DVector(up);

            // WPFの画角は横方向。こちらの FieldOfView は縦方向なので、縦横比で直す。
            camera.FieldOfView = HorizontalFieldOfViewDegrees(source.FieldOfView, aspect);

            // 前後の平面は投影の位置には効かない（奥行きの精度のためだけのもの）
            camera.NearPlaneDistance = Math.Max(0.5, source.NearPlane);
            camera.FarPlaneDistance = Math.Max(
                camera.NearPlaneDistance * 8.0,
                source.Distance * 6.0 + 60000.0);
        }

        private static double HorizontalFieldOfViewDegrees(float verticalFieldOfView, double aspect)
        {
            var horizontal = 2.0 * Math.Atan(Math.Tan(verticalFieldOfView * 0.5) * aspect);
            return horizontal * 180.0 / Math.PI;
        }

        private static ModelVisual3D CreateVisual(in PreviewImageQuad quad)
        {
            var mesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection(4)
                {
                    To3D(quad.A), To3D(quad.B), To3D(quad.C), To3D(quad.D),
                },
                TextureCoordinates = new PointCollection(4)
                {
                    new Point(0.0, 0.0), new Point(1.0, 0.0), new Point(1.0, 1.0), new Point(0.0, 1.0),
                },
                TriangleIndices = new Int32Collection([0, 1, 2, 0, 2, 3]),
            };

            var brush = new ImageBrush(quad.Image) { Stretch = Stretch.Fill };
            if (brush.CanFreeze)
                brush.Freeze();

            // 環境光だけで照らすので、絵がそのままの色（と透明度）で出る。
            // 裏から見ても同じ絵になるようにする。
            var material = new DiffuseMaterial(brush);

            return new ModelVisual3D
            {
                Content = new GeometryModel3D(mesh, material) { BackMaterial = material },
            };
        }

        /// <summary>YMM4の座標（Yが下）をWPFの3D（Yが上）へ移す。</summary>
        private static Point3D To3D(Vector3 value) => new(value.X, -value.Y, value.Z);

        private static Vector3D To3DVector(Vector3 value) => new(value.X, -value.Y, value.Z);
    }
}
