using System.Windows;
using System.Windows.Media.Animation;

namespace IdolClick.UI;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
    }

    public void UpdateStatus(string status, double progress)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = status;
            
            // Animate progress bar
            var animation = new DoubleAnimation
            {
                To = progress * 200, // 200 is the max width
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            ProgressBar.BeginAnimation(WidthProperty, animation);
        });
    }

    public void FadeOut(Action onComplete)
    {
        var fadeOut = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        
        fadeOut.Completed += (s, e) =>
        {
            onComplete?.Invoke();
            Close();
        };
        
        BeginAnimation(OpacityProperty, fadeOut);
    }
}
