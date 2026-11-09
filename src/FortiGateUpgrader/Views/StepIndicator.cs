using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FortiGateUpgrader.Views;

/// <summary>
/// Wizard progress: numbered circles joined by lines. Done steps are green, the current step is blue,
/// later steps are grey. Steps the user may jump to are clickable.
/// </summary>
public sealed class StepIndicator : StackPanel
{
    private static SolidColorBrush B(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }
    private static readonly Brush Done = B("#16A34A"), Current = B("#2563EB"), Future = B("#9CA3AF"), FutureText = B("#6B7280"), Line = B("#D1D5DB");

    private string[] _titles = Array.Empty<string>();

    public StepIndicator()
    {
        Orientation = Orientation.Horizontal;
        HorizontalAlignment = HorizontalAlignment.Center;
    }

    public event EventHandler<int>? StepClicked;

    public void SetSteps(params string[] titles) => _titles = titles;

    /// <param name="current">Index of the current step.</param>
    /// <param name="done">Per step: completed (shown green).</param>
    /// <param name="reachable">Per step: the user may jump there.</param>
    public void Update(int current, IReadOnlyList<bool> done, IReadOnlyList<bool> reachable)
    {
        Children.Clear();
        for (var i = 0; i < _titles.Length; i++)
        {
            var isCurrent = i == current;
            var isDone = !isCurrent && done[i];
            var color = isCurrent ? Current : isDone ? Done : Future;

            var circle = new Grid { Width = 26, Height = 26, VerticalAlignment = VerticalAlignment.Center };
            circle.Children.Add(new Ellipse
            {
                Fill = isCurrent || isDone ? color : Brushes.White,
                Stroke = color, StrokeThickness = 1.5,
            });
            circle.Children.Add(new TextBlock
            {
                Text = (i + 1).ToString(), FontWeight = FontWeights.SemiBold, FontSize = 12.5,
                Foreground = isCurrent || isDone ? Brushes.White : FutureText,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            });

            var label = new TextBlock
            {
                Text = _titles[i], Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Foreground = isCurrent ? Brushes.Black : isDone ? Done : FutureText,
                FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
            };

            var item = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };
            item.Children.Add(circle);
            item.Children.Add(label);
            if (reachable[i] && !isCurrent)
            {
                var index = i;
                item.Cursor = Cursors.Hand;
                item.ToolTip = $"Go to step {i + 1}: {_titles[i]}";
                item.MouseLeftButtonUp += (_, _) => StepClicked?.Invoke(this, index);
            }
            Children.Add(item);

            if (i < _titles.Length - 1)
            {
                Children.Add(new Rectangle
                {
                    Width = 34, Height = 2, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                    Fill = isDone ? Done : Line,
                });
            }
        }
    }
}
