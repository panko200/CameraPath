using System.Windows;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;

namespace CameraPath
{
    /// <summary>
    /// ベジェ時間カーブ用のエディタ属性。
    /// 標準の BezierAnimationEditor は高さを持たないため、
    /// 自作エディタの中では潰れてしまう。高さを明示して表示する。
    /// </summary>
    internal class CameraBezierAnimationEditorAttribute : BezierAnimationEditorAttribute
    {
        public CameraBezierAnimationEditorAttribute()
        {
            PropertyEditorSize = PropertyEditorSize.FullWidth;
            MinHeight = 200;
        }

        public override FrameworkElement Create()
        {
            var editor = (BezierAnimationEditor)base.Create();
            editor.Height = 200;
            editor.MinHeight = 200;
            return editor;
        }
    }
}
