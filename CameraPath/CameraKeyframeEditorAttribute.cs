using System;
using System.Windows;
using System.Windows.Data;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Views.Converters;

namespace CameraPath
{
    /// <summary>カメラ設定点リストのカスタムエディタ属性。</summary>
    internal class CameraKeyframeEditorAttribute : PropertyEditorAttribute2
    {
        public CameraKeyframeEditorAttribute() => PropertyEditorSize = PropertyEditorSize.FullWidth;

        public override FrameworkElement Create() => new CameraKeyframeEditor();

        public override void SetBindings(FrameworkElement control, ItemProperty[] itemProperties)
        {
            if (control is not CameraKeyframeEditor editor)
                throw new ArgumentException("control is not CameraKeyframeEditor");

            control.SetBinding(
                CameraKeyframeEditor.KeyframesProperty,
                ItemPropertiesBinding.Create2(itemProperties));

            editor.Properties = itemProperties;
            editor.SelectKeyframe(0);
        }

        public override void ClearBindings(FrameworkElement control)
        {
            if (control is not CameraKeyframeEditor editor)
                throw new ArgumentException("control is not CameraKeyframeEditor");

            BindingOperations.ClearBinding(control, CameraKeyframeEditor.KeyframesProperty);
            editor.Properties = null;
        }
    }
}
