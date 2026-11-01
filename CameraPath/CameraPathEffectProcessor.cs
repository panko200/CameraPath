using System.Numerics;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;

namespace CameraPath
{
    /// <summary>
    /// カメラパスエフェクトのプロセッサ。
    /// 画像には手を加えず、DrawDescription のカメラ行列と視野角だけを書き換える。
    /// </summary>
    internal class CameraPathEffectProcessor : IVideoEffectProcessor
    {
        private readonly IGraphicsDevicesAndContext devices;
        private readonly CameraPathEffect item;
        private ID2D1Image? input;

        public CameraPathEffectProcessor(IGraphicsDevicesAndContext devices, CameraPathEffect item)
        {
            this.devices = devices;
            this.item = item;
        }

        // 画像には手を加えないので、入力をそのまま出力する（YMM4標準のカメラと同じ振る舞い）
        public ID2D1Image Output => input!;

        public DrawDescription Update(EffectDescription effectDescription)
        {
            var drawDescription = effectDescription.DrawDescription;

            var state = item.EvaluateCameraState(
                effectDescription.ItemPosition.Frame,
                effectDescription.ItemDuration.Frame,
                effectDescription.FPS);

            var camera = drawDescription.Camera * CameraPathSolver.BuildCameraMatrix(state);

            var perspectiveDistance = CameraPathSolver.CalculatePerspectiveDistance(
                state.Fov,
                effectDescription.ScreenSize.Height);

            return drawDescription with
            {
                Camera = camera,
                PerspectiveDistance = perspectiveDistance,
            };
        }

        public void SetInput(ID2D1Image? input) => this.input = input;

        public void ClearInput() => input = null;

        public void Dispose()
        {
        }
    }
}
