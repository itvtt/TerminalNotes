using System.Reflection;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TerminalNotes;

// Light motion for the main window. Only transforms and opacity are animated, never the brushes
// the window compares by reference. The user asked for motion, so it runs even when Windows
// animations are off (this PC renders in software and reports them off).
// Automated UI checks host the window in their own process and keep a still, deterministic screen.
internal static class Motion
{
    private static readonly bool InApp = string.Equals(Assembly.GetEntryAssembly()?.GetName().Name, "TerminalNotes", StringComparison.OrdinalIgnoreCase);
    private static readonly IEasingFunction EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction EaseInOut = Freeze(new SineEase { EasingMode = EasingMode.EaseInOut });
    private static bool _pressRegistered;

    public static bool Enabled => InApp;

    // Every button sinks slightly while pressed.
    public static void RegisterPressFeedback()
    {
        if (_pressRegistered) return;
        _pressRegistered = true;
        EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler((sender, _) => ScaleTo((UIElement)sender, 0.95, 80)), true);
        EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler((sender, _) => ScaleTo((UIElement)sender, 1, 220)), true);
        EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.MouseLeaveEvent,
            new MouseEventHandler((sender, _) => ScaleTo((UIElement)sender, 1, 220)), true);
    }

    // Rises a few pixels while fading in.
    public static void Reveal(UIElement element, double rise = 10, int milliseconds = 320)
    {
        if (!Enabled) return;
        var shift = OwnTransform<TranslateTransform>(element);
        if (shift is null) return;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Duration(milliseconds)) { EasingFunction = EaseOut });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(rise, 0, Duration(milliseconds)) { EasingFunction = EaseOut });
    }

    // Slides a row sideways (hover) and back.
    public static void Nudge(UIElement element, double x)
    {
        if (!Enabled) return;
        var shift = OwnTransform<TranslateTransform>(element);
        shift?.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, Duration(180)) { EasingFunction = EaseOut });
    }

    // Turns the Loop ouroboros continuously while the loop is running.
    public static void Spin(UIElement element, bool on)
    {
        var rotation = element.RenderTransform as RotateTransform;
        if (!on || !Enabled)
        {
            if (rotation is not null) { rotation.BeginAnimation(RotateTransform.AngleProperty, null); rotation.Angle = 0; }
            return;
        }
        if (rotation is null)
        {
            if (!ReferenceEquals(element.RenderTransform, Transform.Identity)) return;
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = rotation = new RotateTransform();
        }
        if (rotation.HasAnimatedProperties) return;
        rotation.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, Duration(2600)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    // Slow breathing glow for a status dot that means "working".
    public static void Breathe(UIElement element, bool on)
    {
        if (!on || !Enabled)
        {
            if (element.HasAnimatedProperties) { element.BeginAnimation(UIElement.OpacityProperty, null); element.Opacity = 1; }
            return;
        }
        if (element.HasAnimatedProperties) return;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.35, Duration(1100))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = EaseInOut });
    }

    // A band of light sweeps across a title; repeats every few seconds.
    public static void Shimmer(System.Windows.Controls.TextBlock text, Color ink, Color glint)
    {
        if (!Enabled) return;
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5), MappingMode = BrushMappingMode.RelativeToBoundingBox };
        var lead = new GradientStop(ink, -0.4); var peak = new GradientStop(glint, -0.25); var tail = new GradientStop(ink, -0.1);
        brush.GradientStops.Add(new GradientStop(ink, 0)); brush.GradientStops.Add(lead); brush.GradientStops.Add(peak); brush.GradientStops.Add(tail);
        text.Foreground = brush;
        var sweep = new ParallelTimeline { Duration = Duration(5200), RepeatBehavior = RepeatBehavior.Forever };
        void Move(GradientStop stop, double from)
        {
            var move = new DoubleAnimation(from, from + 1.6, Duration(1500)) { EasingFunction = EaseInOut, BeginTime = TimeSpan.FromMilliseconds(500) };
            Storyboard.SetTarget(move, text);
            Storyboard.SetTargetProperty(move, new PropertyPath("Foreground.GradientStops[" + brush.GradientStops.IndexOf(stop) + "].Offset"));
            sweep.Children.Add(move);
        }
        Move(lead, -0.4); Move(peak, -0.25); Move(tail, -0.1);
        var board = new Storyboard(); board.Children.Add(sweep);
        board.Begin(text, true);
    }

    // A band of light keeps running through an icon, so the icon itself reads as "working".
    // Each call makes its own brush: only that brush's transform is animated.
    public static Brush Wave(Color ink, Color crest)
    {
        if (!Enabled)
        {
            var still = new SolidColorBrush(ink);
            still.Freeze();
            return still;
        }
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5), MappingMode = BrushMappingMode.RelativeToBoundingBox };
        brush.GradientStops.Add(new GradientStop(ink, 0.3));
        brush.GradientStops.Add(new GradientStop(crest, 0.5));
        brush.GradientStops.Add(new GradientStop(ink, 0.7));
        var shift = new TranslateTransform();
        brush.RelativeTransform = shift;
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-0.8, 0.8, Duration(1500))
            { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = EaseInOut });
        return brush;
    }

    // Terminal-style blinking cursor.
    public static void Blink(UIElement element)
    {
        if (!Enabled) return;
        var blink = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = Duration(1100) };
        blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(550))));
        element.BeginAnimation(UIElement.OpacityProperty, blink);
    }

    private static void ScaleTo(UIElement element, double scale, int milliseconds)
    {
        if (!Enabled) return;
        var size = element.RenderTransform as ScaleTransform;
        if (size is null)
        {
            if (scale == 1 || !ReferenceEquals(element.RenderTransform, Transform.Identity)) return;
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = size = new ScaleTransform();
        }
        var animation = new DoubleAnimation(scale, Duration(milliseconds)) { EasingFunction = EaseOut };
        size.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        size.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private static T? OwnTransform<T>(UIElement element) where T : Transform, new()
    {
        if (element.RenderTransform is T existing && !existing.IsFrozen) return existing;
        if (!ReferenceEquals(element.RenderTransform, Transform.Identity)) return null;
        var created = new T();
        element.RenderTransform = created;
        return created;
    }

    private static Duration Duration(int milliseconds) => new(TimeSpan.FromMilliseconds(milliseconds));

    private static IEasingFunction Freeze(EasingFunctionBase ease) { ease.Freeze(); return ease; }
}
