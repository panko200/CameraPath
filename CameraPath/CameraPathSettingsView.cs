using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace CameraPath
{
    /// <summary>
    /// YMM4の設定画面に出す、3Dプレビューの配色設定。
    /// 見づらいところだけ好きな色に変えられるように、色をひとつずつ並べる。
    /// </summary>
    internal sealed class CameraPathSettingsView : UserControl
    {
        public CameraPathSettingsView()
        {
            var panel = new StackPanel { Margin = new Thickness(6) };

            panel.Children.Add(CreateSection(
                "背景と基準面",
                ("背景", nameof(CameraPathColors.Background)),
                ("画面枠", nameof(CameraPathColors.ScreenFrame)),
                ("目盛り", nameof(CameraPathColors.Grid))));

            panel.Children.Add(CreateSection(
                "軸",
                ("X軸（右）", nameof(CameraPathColors.AxisX)),
                ("Y軸（下）", nameof(CameraPathColors.AxisY)),
                ("Z軸（手前）", nameof(CameraPathColors.AxisZ))));

            panel.Children.Add(CreateSection(
                "軌道と設定点",
                ("軌道", nameof(CameraPathColors.Trajectory)),
                ("設定点", nameof(CameraPathColors.Keyframe)),
                ("選択中の設定点", nameof(CameraPathColors.SelectedKeyframe)),
                ("制御点", nameof(CameraPathColors.Handle))));

            panel.Children.Add(CreateSection(
                "アイテム",
                ("このエフェクトのアイテム", nameof(CameraPathColors.Item)),
                ("そのほかのアイテム", nameof(CameraPathColors.OtherItem))));

            panel.Children.Add(CreateSection(
                "カメラと注視点",
                ("現在のカメラ", nameof(CameraPathColors.CurrentCamera)),
                ("カメラの上方向", nameof(CameraPathColors.CurrentCameraUp)),
                ("視野角の枠", nameof(CameraPathColors.Frustum)),
                ("注視点", nameof(CameraPathColors.LookTarget))));

            var reset = new Button
            {
                Content = "既定の色に戻す",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 4, 0, 6),
            };
            reset.Click += ResetButton_Click;
            panel.Children.Add(reset);

            panel.Children.Add(new TextBlock
            {
                Text = "※この色は3Dプレビューの見た目だけに使います。開いているプレビューにはすぐ反映されます。"
                     + "\n※アイテムの絵を出すかどうかは、エフェクトの「アイテムを表示」で切り替えられます。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Opacity = 0.7,
                Margin = new Thickness(4, 0, 4, 4),
            });

            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = panel,
            };
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is CameraPathEditorSettings settings)
                settings.Colors.Reset();
        }

        /// <summary>見出しと、色の行をまとめた区切りを作る。</summary>
        private static Expander CreateSection(string header, params (string Label, string Property)[] rows)
        {
            var panel = new StackPanel();

            foreach (var (label, property) in rows)
                panel.Children.Add(CreateColorRow(label, property));

            return new Expander
            {
                Header = header,
                IsExpanded = true,
                Margin = new Thickness(0, 0, 0, 6),
                Content = panel,
            };
        }

        /// <summary>「名前＋カラーピッカー」の一行。</summary>
        private static Grid CreateColorRow(string label, string property)
        {
            var row = new Grid { Margin = new Thickness(12, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            Grid.SetColumn(text, 0);
            row.Children.Add(text);

            var picker = new YukkuriMovieMaker.Controls.ColorPicker
            {
                Width = 132,
                Height = 24,
                VerticalAlignment = VerticalAlignment.Center,
            };
            picker.SetBinding(
                YukkuriMovieMaker.Controls.ColorPicker.ValueProperty,
                new Binding($"Colors.{property}")
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                });
            Grid.SetColumn(picker, 1);
            row.Children.Add(picker);

            return row;
        }
    }
}
