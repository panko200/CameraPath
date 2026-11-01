using System;
using System.ComponentModel;
using YukkuriMovieMaker.Plugin;

namespace CameraPath
{
    /// <summary>
    /// カメラパス エフェクトのエディタの表示状態。
    /// YMM4の設定フォルダに保存されるので、開き直しても同じ高さで始まる。
    /// </summary>
    internal sealed class CameraPathEditorSettings : SettingsBase<CameraPathEditorSettings>
    {
        /// <summary>リストの既定の高さ。</summary>
        public const double DefaultListHeight = 150.0;

        /// <summary>3D表示の既定の高さ。</summary>
        public const double DefaultPreviewHeight = 350.0;

        public const double MinListHeight = 60.0;
        public const double MinPreviewHeight = 150.0;

        /// <summary>保存されている高さが異常でも、画面が潰れないようにする上限。</summary>
        private const double MaxHeight = 4000.0;

        public override SettingsCategory Category => SettingsCategory.Other;

        public override string Name => "カメラパス";

        public override bool HasSettingView => true;

        public override object? SettingView => new CameraPathSettingsView { DataContext = this };

        /// <summary>設定点リストの高さ。</summary>
        public double ListHeight
        {
            get => listHeight;
            set => Set(ref listHeight, value);
        }
        private double listHeight = DefaultListHeight;

        /// <summary>3D表示の高さ。</summary>
        public double PreviewHeight
        {
            get => previewHeight;
            set => Set(ref previewHeight, value);
        }
        private double previewHeight = DefaultPreviewHeight;

        /// <summary>3D表示の配色。</summary>
        public CameraPathColors Colors
        {
            get => colors;
            set
            {
                var old = colors;
                if (!Set(ref colors, value))
                    return;

                if (old is not null)
                    old.PropertyChanged -= Colors_PropertyChanged;

                if (colors is not null)
                    colors.PropertyChanged += Colors_PropertyChanged;
            }
        }
        private CameraPathColors colors = new();

        public CameraPathEditorSettings()
        {
            colors.PropertyChanged += Colors_PropertyChanged;
        }

        /// <summary>
        /// 色がひとつ変わったことを、設定そのものの変更として知らせる。
        /// 3D表示はこれを合図に描き直す。
        /// </summary>
        private void Colors_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(Colors));
        }

        public override void Initialize()
        {
            ListHeight = Clamp(ListHeight, MinListHeight, MaxHeight);
            PreviewHeight = Clamp(PreviewHeight, MinPreviewHeight, MaxHeight);

            // 古い設定ファイルには色が無い（読み込みでnullになることもある）
            if (Colors is null)
                Colors = new CameraPathColors();
        }

        private static double Clamp(double value, double min, double max)
            => double.IsFinite(value) ? Math.Clamp(value, min, max) : min;
    }
}
