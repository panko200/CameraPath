using System.Windows.Media;
using YukkuriMovieMaker.Commons;

namespace CameraPath
{
    /// <summary>
    /// 3Dプレビューの配色。
    /// YMM4の設定画面からひとつずつ変えられ、エフェクトの設定ファイルにそのまま保存される。
    /// 見づらいところだけ好きな色に替えて、自分で見やすい配色にできるようにするためのもの。
    /// </summary>
    internal sealed class CameraPathColors : Bindable
    {
        /// <summary>既定の色。設定画面の「既定の色に戻す」でここへ戻す。</summary>
        private static readonly CameraPathColors Defaults = new();

        /// <summary>3D表示の背景。</summary>
        public Color Background
        {
            get => background;
            set => Set(ref background, value);
        }
        private Color background = Color.FromRgb(0x12, 0x14, 0x1A);

        /// <summary>アイテムが標準で描かれる面（画面枠）の色。</summary>
        public Color ScreenFrame
        {
            get => screenFrame;
            set => Set(ref screenFrame, value);
        }
        private Color screenFrame = Color.FromRgb(0x5A, 0x64, 0x74);

        /// <summary>画面枠の中の目盛りの色。</summary>
        public Color Grid
        {
            get => grid;
            set => Set(ref grid, value);
        }
        private Color grid = Color.FromRgb(0x2B, 0x31, 0x3C);

        /// <summary>X軸（右方向）の色。</summary>
        public Color AxisX
        {
            get => axisX;
            set => Set(ref axisX, value);
        }
        private Color axisX = Color.FromRgb(0xE0, 0x5A, 0x55);

        /// <summary>Y軸（下方向）の色。</summary>
        public Color AxisY
        {
            get => axisY;
            set => Set(ref axisY, value);
        }
        private Color axisY = Color.FromRgb(0x66, 0xC8, 0x6A);

        /// <summary>Z軸（手前方向）の色。</summary>
        public Color AxisZ
        {
            get => axisZ;
            set => Set(ref axisZ, value);
        }
        private Color axisZ = Color.FromRgb(0x52, 0x9B, 0xE8);

        /// <summary>カメラが通る軌道の色。</summary>
        public Color Trajectory
        {
            get => trajectory;
            set => Set(ref trajectory, value);
        }
        private Color trajectory = Color.FromRgb(0xF2, 0xA6, 0x38);

        /// <summary>設定点の色。</summary>
        public Color Keyframe
        {
            get => keyframe;
            set => Set(ref keyframe, value);
        }
        private Color keyframe = Color.FromRgb(0xD8, 0xDE, 0xE8);

        /// <summary>リストで選択中の設定点の色。</summary>
        public Color SelectedKeyframe
        {
            get => selectedKeyframe;
            set => Set(ref selectedKeyframe, value);
        }
        private Color selectedKeyframe = Color.FromRgb(0xFF, 0x5A, 0xD2);

        /// <summary>ベジェ曲線の制御点（接線の握り）の色。</summary>
        public Color Handle
        {
            get => handle;
            set => Set(ref handle, value);
        }
        private Color handle = Color.FromRgb(0x5F, 0xE3, 0xC8);

        /// <summary>このエフェクトが効いているアイテムの色。</summary>
        public Color Item
        {
            get => item;
            set => Set(ref item, value);
        }
        private Color item = Color.FromRgb(0xC8, 0x9B, 0xFF);

        /// <summary>このエフェクトが効いていないアイテムの色。</summary>
        public Color OtherItem
        {
            get => otherItem;
            set => Set(ref otherItem, value);
        }
        private Color otherItem = Color.FromRgb(0x74, 0x8A, 0xA8);

        /// <summary>いまのフレームのカメラの色。</summary>
        public Color CurrentCamera
        {
            get => currentCamera;
            set => Set(ref currentCamera, value);
        }
        private Color currentCamera = Color.FromRgb(0x3C, 0xD6, 0xFF);

        /// <summary>カメラの上方向の目印の色。</summary>
        public Color CurrentCameraUp
        {
            get => currentCameraUp;
            set => Set(ref currentCameraUp, value);
        }
        private Color currentCameraUp = Color.FromRgb(0x9B, 0xF0, 0xFF);

        /// <summary>視野角の目安として描く四角錐の色。</summary>
        public Color Frustum
        {
            get => frustum;
            set => Set(ref frustum, value);
        }
        private Color frustum = Color.FromArgb(0x88, 0x3C, 0xD6, 0xFF);

        /// <summary>注視点の色。</summary>
        public Color LookTarget
        {
            get => lookTarget;
            set => Set(ref lookTarget, value);
        }
        private Color lookTarget = Color.FromRgb(0x9B, 0xE8, 0x6B);

        /// <summary>既定の色に戻す。</summary>
        public void Reset()
        {
            Background = Defaults.Background;
            ScreenFrame = Defaults.ScreenFrame;
            Grid = Defaults.Grid;
            AxisX = Defaults.AxisX;
            AxisY = Defaults.AxisY;
            AxisZ = Defaults.AxisZ;
            Trajectory = Defaults.Trajectory;
            Keyframe = Defaults.Keyframe;
            SelectedKeyframe = Defaults.SelectedKeyframe;
            Handle = Defaults.Handle;
            Item = Defaults.Item;
            OtherItem = Defaults.OtherItem;
            CurrentCamera = Defaults.CurrentCamera;
            CurrentCameraUp = Defaults.CurrentCameraUp;
            Frustum = Defaults.Frustum;
            LookTarget = Defaults.LookTarget;
        }
    }
}
