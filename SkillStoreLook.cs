using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Shape = System.Windows.Shapes.Shape;

namespace TerminalNotes;

/// <summary>The skill store windows' look, the main window's: their own title bar (icon, title, a muted subtitle, actions,
/// close) over the fading gold line, Pretendard, neutral blacks, and gold only for what is on and the main action. Each window
/// carries these resources itself, so it looks the same wherever it opens and in the tests' previews.</summary>
internal static class StoreLook
{
    internal static readonly string MoreGlyph = ((char)0xE712).ToString(); // ⋯ in Segoe Fluent Icons
    internal static readonly string RefreshGlyph = ((char)0xE72C).ToString();
    internal static readonly string LockGlyph = ((char)0xE72E).ToString();
    internal static readonly string LinkGlyph = ((char)0xE71B).ToString();
    internal static readonly string ChevronGlyph = ((char)0xE70D).ToString();
    private const double CaptionHeight = 44;
    private static readonly Uri Component = new("pack://application:,,,/TerminalNotes;component/");
    private static ResourceDictionary? _resources;
    private static ImageSource? _icon;
    private static bool _iconLoaded;

    internal static ResourceDictionary Resources => _resources ??= Load();

    internal static FontFamily Mono => (FontFamily)Resources["MonoFont"];

    /// <summary>Gives a window the store look and its own title bar around its body. Esc closes it: through its own 취소 when it
    /// has one, else through ×.</summary>
    internal static void Dress(Window window, string caption, UIElement body, string? subtitle = null, params UIElement[] actions)
    {
        foreach (object key in Resources.Keys) window.Resources[key] = Resources[key];
        window.FontFamily = (FontFamily)Resources["UiFont"];
        window.FontSize = 14;
        window.UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Ideal);
        window.SetResourceReference(Window.BackgroundProperty, "Canvas");
        window.SetResourceReference(Window.ForegroundProperty, "TextPrimary");
        if (AppIcon is { } icon) window.Icon = icon;
        WindowChrome.SetWindowChrome(window, new WindowChrome { CaptionHeight = CaptionHeight, GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false, ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness });

        var bar = new Grid { Height = CaptionHeight };
        bar.SetResourceReference(Panel.BackgroundProperty, "CaptionSurface");
        foreach (var width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        if (AppIcon is { } image)
            bar.Children.Add(new Image { Source = image, Width = 16, Height = 16, Margin = new Thickness(16, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        Add(bar, new TextBlock { Text = caption, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(AppIcon is null ? 16 : 0, 0, 0, 0) }, 1);
        if (subtitle is { Length: > 0 })
        {
            var muted = new TextBlock { Text = subtitle, FontSize = 12.5, Margin = new Thickness(12, 1, 12, 0), VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis };
            muted.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            Add(bar, muted, 2);
        }
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var action in actions) tools.Children.Add(action);
        WindowChrome.SetIsHitTestVisibleInChrome(tools, true);
        Add(bar, tools, 3);
        var cross = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 0,0 L 10,10 M 10,0 L 0,10"), StrokeThickness = 1, Width = 10, Height = 10 };
        cross.SetResourceReference(Shape.StrokeProperty, "TextSecondary");
        var close = new Button { Content = cross, IsCancel = !Descendants(body).OfType<Button>().Any(button => button.IsCancel) };
        close.SetResourceReference(FrameworkElement.StyleProperty, "CaptionButton");
        AutomationProperties.SetName(close, "닫기");
        close.Click += (_, _) => window.Close();
        Add(bar, close, 4);
        var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
        line.SetResourceReference(Border.BackgroundProperty, "GoldLine");
        Grid.SetColumnSpan(line, 5);
        bar.Children.Add(line);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.Children.Add(bar);
        Grid.SetRow(body, 1);
        layout.Children.Add(body);
        var frame = new Border { Child = layout, BorderThickness = new Thickness(1), SnapsToDevicePixels = true };
        frame.SetResourceReference(Border.BorderBrushProperty, "WindowEdge");
        frame.SetResourceReference(Border.BackgroundProperty, "Canvas");
        window.Content = frame;
        // A dialog does not maximize (its title bar is ours); if Windows does it anyway, the frame stays on screen.
        window.SourceInitialized += (_, _) =>
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            SetWindowLong(handle, -16, GetWindowLong(handle, -16) & ~0x30000);
            // Windows recomputes the frame only when told (SWP_FRAMECHANGED); without it, the pointer over the title bar made
            // Windows paint its old-style caption (a classic close button and a white line) over this one.
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
            HwndSource.FromHwnd(handle)?.AddHook(KeepOwnCaption);
        };
        window.StateChanged += (_, _) =>
            frame.Margin = window.WindowState == WindowState.Maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        window.Loaded += (_, _) =>
        {
            // After the app's own frame colors: a neutral edge (no bronze) and round corners on Windows 11.
            IntPtr handle = new WindowInteropHelper(window).Handle;
            int edge = 0x00333333, round = 2;
            _ = DwmSetWindowAttribute(handle, 34, ref edge, sizeof(int));
            _ = DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));
            Motion.Reveal(body, 6, 240);
        };
    }

    internal static TextBlock Glyph(string glyph, double size)
    {
        var text = new TextBlock { Text = glyph, FontSize = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        return text;
    }

    /// <summary>Tabs over a hairline that spans the width; the chosen tab is bright with a gold underline.</summary>
    internal static Grid Tabs(params RadioButton[] tabs)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var tab in tabs)
        {
            tab.SetResourceReference(FrameworkElement.StyleProperty, "StoreTab");
            row.Children.Add(tab);
        }
        var hairline = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom };
        hairline.SetResourceReference(Border.BackgroundProperty, "Line");
        var grid = new Grid();
        grid.Children.Add(hairline);
        grid.Children.Add(row);
        return grid;
    }

    /// <summary>A window's buttons at the bottom right, and a message on their left.</summary>
    internal static Grid Footer(TextBlock? message, params Button[] buttons)
    {
        var row = new Grid { Margin = new Thickness(24, 14, 24, 20) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (message is not null)
        {
            message.VerticalAlignment = VerticalAlignment.Center;
            message.TextWrapping = TextWrapping.Wrap;
            message.Margin = new Thickness(0, 0, 16, 0);
            row.Children.Add(message);
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (int index = 0; index < buttons.Length; index++)
        {
            buttons[index].Margin = new Thickness(index == 0 ? 0 : 8, 0, 0, 0);
            actions.Children.Add(buttons[index]);
        }
        Add(row, actions, 1);
        return row;
    }

    /// <summary>A row's menu, opened by its ⋯ (<see cref="Open"/>) or by a right click at the pointer.</summary>
    internal static ContextMenu Menu()
    {
        var menu = new ContextMenu { Style = (Style)Resources["StoreMenu"] };
        menu.Closed += (_, _) =>
        {
            foreach (var property in new[] { ContextMenu.PlacementProperty, ContextMenu.PlacementTargetProperty, ContextMenu.HorizontalOffsetProperty,
                         ContextMenu.VerticalOffsetProperty })
                menu.ClearValue(property);
        };
        return menu;
    }

    internal static void Item(ContextMenu menu, string header, Action click)
    {
        var item = new MenuItem { Header = header, Style = (Style)Resources["StoreMenuItem"] };
        item.Click += (_, _) => click();
        menu.Items.Add(item);
    }

    /// <summary>Opens a menu under its button, the right edges lined up.</summary>
    internal static void Open(ContextMenu menu, FrameworkElement button)
    {
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Left;
        menu.HorizontalOffset = button.ActualWidth;
        menu.VerticalOffset = button.ActualHeight + 4;
        menu.IsOpen = true;
    }

    private static void Add(Grid grid, UIElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject element)
            {
                yield return element;
                foreach (var descendant in Descendants(element)) yield return descendant;
            }
    }

    private static ImageSource? AppIcon
    {
        get
        {
            if (_iconLoaded) return _icon;
            _iconLoaded = true;
            try
            {
                var frames = BitmapDecoder.Create(new Uri(Component, "Assets/AppIcon.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames;
                _icon = frames.Where(frame => frame.PixelWidth >= 32).OrderBy(frame => frame.PixelWidth).FirstOrDefault() ?? frames.LastOrDefault();
            }
            catch (Exception exception) when (exception is IOException or NotSupportedException or FileFormatException or UriFormatException) { }
            return _icon;
        }
    }

    private static ResourceDictionary Load()
    {
        var dictionary = (ResourceDictionary)XamlReader.Parse(Xaml, new ParserContext { BaseUri = Component });
        foreach (object value in dictionary.Values)
            if (value is Freezable { CanFreeze: true } freezable) freezable.Freeze();
        return dictionary;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    // WM_NCUAHDRAWCAPTION and WM_NCUAHDRAWFRAME: Windows about to draw its own caption or frame, as it does when visual styles
    // are off (often over remote desktop). This window draws its own title bar, so they are dropped.
    private static IntPtr KeepOwnCaption(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is 0xAE or 0xAF) handled = true;
        return IntPtr.Zero;
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    // The main window's values (MainWindow.xaml); gold stays bright, everything else neutral.
    private const string Xaml = """
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                            xmlns:shell="clr-namespace:System.Windows.Shell;assembly=PresentationFramework">
         <FontFamily x:Key="UiFont">./Assets/Fonts/#Pretendard, Segoe UI, Malgun Gothic</FontFamily>
         <FontFamily x:Key="MonoFont">Cascadia Mono, Consolas, ./Assets/Fonts/#Pretendard, Malgun Gothic</FontFamily>
         <FontFamily x:Key="IconFont">Segoe Fluent Icons, Segoe MDL2 Assets</FontFamily>
         <SolidColorBrush x:Key="Canvas" Color="#121212"/>
         <SolidColorBrush x:Key="CaptionSurface" Color="#161616"/>
         <SolidColorBrush x:Key="Surface" Color="#1A1A1A"/>
         <SolidColorBrush x:Key="SurfaceHover" Color="#1E1E1E"/>
         <SolidColorBrush x:Key="SurfaceElevated" Color="#222222"/>
         <SolidColorBrush x:Key="Well" Color="#0F0F0F"/>
         <SolidColorBrush x:Key="Line" Color="#262626"/>
         <SolidColorBrush x:Key="PanelEdge" Color="#555555"/>
         <SolidColorBrush x:Key="WindowEdge" Color="#333333"/>
         <SolidColorBrush x:Key="TextPrimary" Color="#F2F2F2"/>
         <SolidColorBrush x:Key="TextSecondary" Color="#BDBDBD"/>
         <SolidColorBrush x:Key="Muted" Color="#8C8C8C"/>
         <SolidColorBrush x:Key="Accent" Color="#E5C07B"/>
         <SolidColorBrush x:Key="AccentBright" Color="#F4DCA6"/>
         <LinearGradientBrush x:Key="GoldFill" StartPoint="0,0" EndPoint="0,1"><GradientStop Color="#F6DDA2" Offset="0"/><GradientStop Color="#E3BC6E" Offset="0.55"/><GradientStop Color="#C99D50" Offset="1"/></LinearGradientBrush>
         <LinearGradientBrush x:Key="GoldFillHover" StartPoint="0,0" EndPoint="0,1"><GradientStop Color="#FBE7B6" Offset="0"/><GradientStop Color="#EDC97E" Offset="0.55"/><GradientStop Color="#D4A95C" Offset="1"/></LinearGradientBrush>
         <LinearGradientBrush x:Key="GoldFillPressed" StartPoint="0,0" EndPoint="0,1"><GradientStop Color="#C99D50" Offset="0"/><GradientStop Color="#B88D44" Offset="1"/></LinearGradientBrush>
         <SolidColorBrush x:Key="GoldInk" Color="#212121"/>
         <LinearGradientBrush x:Key="GoldLine" StartPoint="0,0" EndPoint="1,0"><GradientStop Color="#00E6C47E" Offset="0"/><GradientStop Color="#B0E6C47E" Offset="0.25"/><GradientStop Color="#FFF4DCA6" Offset="0.5"/><GradientStop Color="#B0E6C47E" Offset="0.75"/><GradientStop Color="#00E6C47E" Offset="1"/></LinearGradientBrush>
         <SolidColorBrush x:Key="InputWell" Color="#0D0D0D"/>
         <SolidColorBrush x:Key="InputWellReadOnly" Color="#101010"/>
         <SolidColorBrush x:Key="InputLine" Color="#2C2C2C"/>
         <SolidColorBrush x:Key="InputLineHover" Color="#4A4A4A"/>
         <SolidColorBrush x:Key="InputPlaceholder" Color="#686868"/>
         <SolidColorBrush x:Key="ButtonSurface" Color="#222222"/>
         <SolidColorBrush x:Key="ButtonSurfaceHover" Color="#2B2B2B"/>
         <SolidColorBrush x:Key="ButtonSurfacePressed" Color="#161616"/>
         <SolidColorBrush x:Key="ButtonLine" Color="#363636"/>
         <SolidColorBrush x:Key="ButtonLineHover" Color="#575757"/>

         <!-- Only keyboard focus draws a ring; a click does not. -->
         <Style x:Key="StoreFocus">
          <Setter Property="Control.Template"><Setter.Value><ControlTemplate>
           <Border Margin="-3" BorderBrush="{StaticResource Accent}" BorderThickness="1.5" CornerRadius="9" SnapsToDevicePixels="True"/>
          </ControlTemplate></Setter.Value></Setter>
         </Style>
         <!-- The owner's implicit text style must not recolor text inside buttons. -->
         <Style TargetType="TextBlock"/>
         <Style TargetType="ToolTip">
          <Setter Property="Foreground" Value="{StaticResource TextPrimary}"/><Setter Property="Background" Value="{StaticResource SurfaceElevated}"/>
          <Setter Property="BorderBrush" Value="{StaticResource ButtonLine}"/><Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="10,6"/>
          <Setter Property="FontFamily" Value="{StaticResource UiFont}"/><Setter Property="FontSize" Value="12.5"/>
         </Style>
         <Style TargetType="Button">
          <Setter Property="FontSize" Value="13"/><Setter Property="FontWeight" Value="Medium"/>
          <Setter Property="Foreground" Value="{StaticResource TextPrimary}"/><Setter Property="Background" Value="{StaticResource ButtonSurface}"/>
          <Setter Property="BorderBrush" Value="{StaticResource ButtonLine}"/><Setter Property="BorderThickness" Value="1"/>
          <Setter Property="Padding" Value="14,7"/><Setter Property="MinHeight" Value="34"/><Setter Property="Cursor" Value="Hand"/>
          <Setter Property="HorizontalContentAlignment" Value="Center"/><Setter Property="VerticalContentAlignment" Value="Center"/>
          <Setter Property="FocusVisualStyle" Value="{StaticResource StoreFocus}"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
           <Border x:Name="Chrome" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"
                   CornerRadius="7" Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
            <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}" RecognizesAccessKey="True"/>
           </Border>
           <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{StaticResource ButtonSurfaceHover}"/><Setter TargetName="Chrome" Property="BorderBrush" Value="{StaticResource ButtonLineHover}"/></Trigger>
            <Trigger Property="IsPressed" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{StaticResource ButtonSurfacePressed}"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Chrome" Property="Opacity" Value="0.4"/></Trigger>
           </ControlTemplate.Triggers>
          </ControlTemplate></Setter.Value></Setter>
         </Style>
         <Style x:Key="PrimaryButton" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
          <Setter Property="Background" Value="{StaticResource GoldFill}"/><Setter Property="BorderBrush" Value="#F8E4B0"/>
          <Setter Property="Foreground" Value="{StaticResource GoldInk}"/><Setter Property="FontWeight" Value="SemiBold"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
           <Grid>
            <Border x:Name="Glow" CornerRadius="10" Margin="-3" BorderThickness="3" BorderBrush="#55E5C07B" Opacity="0" IsHitTestVisible="False"/>
            <Border x:Name="Chrome" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="7" Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
             <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" TextElement.Foreground="{TemplateBinding Foreground}" RecognizesAccessKey="True"/>
            </Border>
           </Grid>
           <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
             <Trigger.EnterActions><BeginStoryboard><Storyboard><DoubleAnimation Storyboard.TargetName="Glow" Storyboard.TargetProperty="Opacity" To="1" Duration="0:0:0.2"/></Storyboard></BeginStoryboard></Trigger.EnterActions>
             <Trigger.ExitActions><BeginStoryboard><Storyboard><DoubleAnimation Storyboard.TargetName="Glow" Storyboard.TargetProperty="Opacity" To="0" Duration="0:0:0.25"/></Storyboard></BeginStoryboard></Trigger.ExitActions>
             <Setter TargetName="Chrome" Property="Background" Value="{StaticResource GoldFillHover}"/>
            </Trigger>
            <Trigger Property="IsPressed" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{StaticResource GoldFillPressed}"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Chrome" Property="Background" Value="#1E1E1E"/><Setter TargetName="Chrome" Property="BorderBrush" Value="#2C2C2C"/><Setter Property="Foreground" Value="#6E6E6E"/></Trigger>
           </ControlTemplate.Triggers>
          </ControlTemplate></Setter.Value></Setter>
         </Style>
         <!-- Gold outline: the store's one button in its title bar (+ 스킬 공유). -->
         <Style x:Key="OutlineButton" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
          <Setter Property="Background" Value="Transparent"/><Setter Property="BorderBrush" Value="{StaticResource Accent}"/>
          <Setter Property="Foreground" Value="{StaticResource Accent}"/><Setter Property="FontWeight" Value="SemiBold"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
           <Border x:Name="Chrome" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="7" Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True"/>
           </Border>
           <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="Background" Value="#202020"/><Setter TargetName="Chrome" Property="BorderBrush" Value="{StaticResource AccentBright}"/><Setter Property="Foreground" Value="{StaticResource AccentBright}"/></Trigger>
            <Trigger Property="IsPressed" Value="True"><Setter TargetName="Chrome" Property="Background" Value="#161616"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Chrome" Property="Opacity" Value="0.4"/></Trigger>
           </ControlTemplate.Triggers>
          </ControlTemplate></Setter.Value></Setter>
         </Style>
         <!-- Text-only actions: no box until the pointer is on them. -->
         <Style x:Key="QuietButton" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
          <Setter Property="Foreground" Value="{StaticResource TextSecondary}"/><Setter Property="FontWeight" Value="Normal"/>
          <Setter Property="Padding" Value="10,5"/><Setter Property="MinHeight" Value="30"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
           <Border x:Name="Chrome" Background="Transparent" CornerRadius="6" Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True"/>
           </Border>
           <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="Background" Value="#252525"/><Setter Property="Foreground" Value="{StaticResource TextPrimary}"/></Trigger>
            <Trigger Property="IsPressed" Value="True"><Setter TargetName="Chrome" Property="Background" Value="#1C1C1C"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Chrome" Property="Opacity" Value="0.4"/></Trigger>
           </ControlTemplate.Triggers>
          </ControlTemplate></Setter.Value></Setter>
         </Style>
         <!-- Gold text: the one extra action a row may show (업데이트). -->
         <Style x:Key="AccentButton" TargetType="Button" BasedOn="{StaticResource QuietButton}">
          <Setter Property="Foreground" Value="{StaticResource Accent}"/><Setter Property="FontWeight" Value="SemiBold"/>
          <Style.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter Property="Foreground" Value="{StaticResource AccentBright}"/></Trigger></Style.Triggers>
         </Style>
         <Style x:Key="IconButton" TargetType="Button" BasedOn="{StaticResource QuietButton}">
          <Setter Property="Width" Value="34"/><Setter Property="Height" Value="32"/><Setter Property="MinHeight" Value="0"/><Setter Property="Padding" Value="0"/>
         </Style>
         <Style x:Key="CaptionButton" TargetType="Button">
          <Setter Property="Width" Value="46"/><Setter Property="Height" Value="44"/><Setter Property="Focusable" Value="False"/>
          <Setter Property="Background" Value="Transparent"/><Setter Property="shell:WindowChrome.IsHitTestVisibleInChrome" Value="True"/>
          <!-- The whole 46×44 answers the pointer; the hover shows as a rounded square like the other icon buttons, clear of the
               window's edge and the gold line. -->
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
           <Grid Background="Transparent">
            <Border x:Name="Chrome" Width="32" Height="32" CornerRadius="6" Background="{TemplateBinding Background}" SnapsToDevicePixels="True"/>
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
           </Grid>
           <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="Background" Value="#2A2A2A"/></Trigger>
            <Trigger Property="IsPressed" Value="True"><Setter TargetName="Chrome" Property="Background" Value="#202020"/></Trigger>
           </ControlTemplate.Triggers>
          </ControlTemplate></Setter.Value></Setter>
         </Style>
         <!-- Inputs are recessed wells; gold only while typing. Tag is the placeholder. -->
         <Style TargetType="TextBox">
          <Setter Property="Foreground" Value="{StaticResource TextPrimary}"/><Setter Property="Background" Value="{StaticResource InputWell}"/>
          <Setter Property="BorderBrush" Value="{StaticResource InputLine}"/><Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="11,8"/>
          <Setter Property="CaretBrush" Value="{StaticResource Accent}"/><Setter Property="SelectionBrush" Value="#5C5C5C"/><Setter Property="SelectionTextBrush" Value="#FFFFFF"/>
          <Setter Property="FontSize" Value="14"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TextBox">
           <Border x:Name="Chrome" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="7" SnapsToDevicePixels="True">
            <Grid>
             <TextBlock x:Name="Hint" Text="{TemplateBinding Tag}" Foreground="{StaticResource InputPlaceholder}" Margin="{TemplateBinding Padding}" Padding="2,0,0,0"
                        FontFamily="{TemplateBinding FontFamily}" FontSize="{TemplateBinding FontSize}" TextWrapping="Wrap" IsHitTestVisible="False" Visibility="Collapsed"/>
             <ScrollViewer x:Name="PART_ContentHost" Padding="{TemplateBinding Padding}" Focusable="False"
                           HorizontalScrollBarVisibility="{Binding Path=(ScrollViewer.HorizontalScrollBarVisibility), RelativeSource={RelativeSource TemplatedParent}}"
                           VerticalScrollBarVisibility="{Binding Path=(ScrollViewer.VerticalScrollBarVisibility), RelativeSource={RelativeSource TemplatedParent}}"/>
            </Grid>
           </Border>
           <ControlTemplate.Triggers>
            <Trigger Property="Text" Value=""><Setter TargetName="Hint" Property="Visibility" Value="Visible"/></Trigger>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="{StaticResource InputLineHover}"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="{StaticResource Accent}"/></Trigger>
            <Trigger Property="IsReadOnly" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{StaticResource InputWellReadOnly}"/><Setter Property="Foreground" Value="{StaticResource TextSecondary}"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Chrome" Property="Opacity" Value="0.6"/></Trigger>
           </ControlTemplate.Triggers>
          </ControlTemplate></Setter.Value></Setter>
         </Style>
         <!-- Text tabs: the chosen one bright with a gold underline on the hairline below. -->
         <Style x:Key="StoreTab" TargetType="RadioButton">
          <Setter Property="Foreground" Value="{StaticResource Muted}"/><Setter Property="FontSize" Value="13.5"/><Setter Property="FontWeight" Value="Medium"/>
          <Setter Property="Height" Value="36"/><Setter Property="Padding" Value="2,0,2,2"/><Setter Property="Margin" Value="0,0,24,0"/>
          <Setter Property="Cursor" Value="Hand"/><Setter Property="FocusVisualStyle" Value="{StaticResource StoreFocus}"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RadioButton">
           <Grid Background="Transparent">
            <ContentPresenter Margin="{TemplateBinding Padding}" VerticalAlignment="Center" RecognizesAccessKey="True"/>
            <Border x:Name="Underline" Height="2" VerticalAlignment="Bottom" CornerRadius="1" Background="{StaticResource Accent}" Visibility="Hidden"/>
           </Grid>
           <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter Property="Foreground" Value="{StaticResource TextSecondary}"/></Trigger>
            <Trigger Property="IsChecked" Value="True"><Setter TargetName="Underline" Property="Visibility" Value="Visible"/><Setter Property="Foreground" Value="{StaticResource TextPrimary}"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.5"/></Trigger>
           </ControlTemplate.Triggers>
          </ControlTemplate></Setter.Value></Setter>
         </Style>
         <!-- The skill store's left list (전체, 사용 중, categories): the chosen one on a raised row with a gold bar and bright text. -->
         <Style x:Key="StoreSideItem" TargetType="RadioButton">
          <Setter Property="Foreground" Value="{StaticResource TextSecondary}"/><Setter Property="FontSize" Value="13.5"/>
          <Setter Property="Height" Value="34"/><Setter Property="Padding" Value="12,0,10,0"/><Setter Property="Margin" Value="0,0,0,2"/>
          <Setter Property="HorizontalContentAlignment" Value="Stretch"/><Setter Property="Cursor" Value="Hand"/>
          <Setter Property="FocusVisualStyle" Value="{StaticResource StoreFocus}"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RadioButton">
           <Border x:Name="Row" Background="Transparent" CornerRadius="7">
            <Grid>
             <Border x:Name="Bar" Width="3" Height="16" HorizontalAlignment="Left" CornerRadius="1.5" Background="{StaticResource Accent}" Visibility="Hidden"/>
             <ContentPresenter Margin="{TemplateBinding Padding}" VerticalAlignment="Center" HorizontalAlignment="Stretch"/>
            </Grid>
           </Border>
           <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Row" Property="Background" Value="#FF1F1F1F"/></Trigger>
            <Trigger Property="IsChecked" Value="True"><Setter TargetName="Row" Property="Background" Value="#FF262626"/><Setter TargetName="Bar" Property="Visibility" Value="Visible"/><Setter Property="Foreground" Value="{StaticResource TextPrimary}"/></Trigger>
           </ControlTemplate.Triggers>
          </ControlTemplate></Setter.Value></Setter>
         </Style>
         <!-- A card's star: an outline, gold and filled when the skill is kept at the top. -->
         <Style x:Key="StoreStar" TargetType="ToggleButton">
          <Setter Property="Width" Value="28"/><Setter Property="Height" Value="28"/><Setter Property="Cursor" Value="Hand"/>
          <Setter Property="Foreground" Value="{StaticResource Muted}"/><Setter Property="FocusVisualStyle" Value="{StaticResource StoreFocus}"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ToggleButton">
           <Border x:Name="Chrome" Background="Transparent" CornerRadius="6">
            <Grid>
             <TextBlock x:Name="Off" Text="&#xE734;" FontFamily="{StaticResource IconFont}" FontSize="14" HorizontalAlignment="Center" VerticalAlignment="Center" Foreground="{TemplateBinding Foreground}"/>
             <TextBlock x:Name="On" Text="&#xE735;" FontFamily="{StaticResource IconFont}" FontSize="14" HorizontalAlignment="Center" VerticalAlignment="Center" Foreground="{StaticResource Accent}" Visibility="Collapsed"/>
            </Grid>
           </Border>
           <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="Background" Value="#252525"/><Setter Property="Foreground" Value="{StaticResource TextSecondary}"/></Trigger>
            <Trigger Property="IsChecked" Value="True"><Setter TargetName="Off" Property="Visibility" Value="Collapsed"/><Setter TargetName="On" Property="Visibility" Value="Visible"/></Trigger>
           </ControlTemplate.Triggers>
          </ControlTemplate></Setter.Value></Setter>
         </Style>
         <!-- On (gold) is applied on this PC. -->
         <Style x:Key="StoreSwitch" TargetType="ToggleButton">
          <Setter Property="Width" Value="40"/><Setter Property="Height" Value="22"/><Setter Property="Cursor" Value="Hand"/>
          <Setter Property="FocusVisualStyle" Value="{StaticResource StoreFocus}"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ToggleButton">
           <Grid Background="Transparent">
            <Border x:Name="Track" CornerRadius="11" Background="#262626" BorderBrush="#404040" BorderThickness="1" SnapsToDevicePixels="True"/>
            <Ellipse x:Name="Knob" Width="14" Height="14" Margin="4,0" HorizontalAlignment="Left" VerticalAlignment="Center" Fill="#8C8C8C"/>
           </Grid>
           <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Track" Property="BorderBrush" Value="#5E5E5E"/><Setter TargetName="Knob" Property="Fill" Value="#BDBDBD"/></Trigger>
            <Trigger Property="IsChecked" Value="True">
             <Setter TargetName="Track" Property="Background" Value="{StaticResource Accent}"/><Setter TargetName="Track" Property="BorderBrush" Value="{StaticResource Accent}"/>
             <Setter TargetName="Knob" Property="HorizontalAlignment" Value="Right"/><Setter TargetName="Knob" Property="Fill" Value="{StaticResource GoldInk}"/>
            </Trigger>
            <MultiTrigger>
             <MultiTrigger.Conditions><Condition Property="IsChecked" Value="True"/><Condition Property="IsMouseOver" Value="True"/></MultiTrigger.Conditions>
             <Setter TargetName="Track" Property="Background" Value="{StaticResource AccentBright}"/><Setter TargetName="Track" Property="BorderBrush" Value="{StaticResource AccentBright}"/>
            </MultiTrigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
           </ControlTemplate.Triggers>
          </ControlTemplate></Setter.Value></Setter>
         </Style>
         <!-- The share window's drop zone: a dashed edge (gold while a file is dragged over it), and a click opens the file picker. -->
         <Style x:Key="DropButton" TargetType="Button">
          <Setter Property="Background" Value="{StaticResource Surface}"/><Setter Property="BorderBrush" Value="{StaticResource PanelEdge}"/>
          <Setter Property="Cursor" Value="Hand"/><Setter Property="FocusVisualStyle" Value="{StaticResource StoreFocus}"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
           <Grid>
            <Rectangle x:Name="Edge" RadiusX="10" RadiusY="10" Fill="{TemplateBinding Background}" Stroke="{TemplateBinding BorderBrush}" StrokeThickness="1.5" StrokeDashArray="5 4" SnapsToDevicePixels="True"/>
            <ContentPresenter Margin="16" HorizontalAlignment="Center" VerticalAlignment="Center"/>
           </Grid>
           <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Edge" Property="Fill" Value="{StaticResource SurfaceHover}"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.5"/></Trigger>
           </ControlTemplate.Triggers>
          </ControlTemplate></Setter.Value></Setter>
         </Style>
         <Style x:Key="StoreMenu" TargetType="ContextMenu">
          <Setter Property="Background" Value="{StaticResource SurfaceElevated}"/><Setter Property="BorderBrush" Value="{StaticResource ButtonLine}"/>
          <Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="4"/>
          <Setter Property="Foreground" Value="{StaticResource TextPrimary}"/><Setter Property="FontFamily" Value="{StaticResource UiFont}"/><Setter Property="FontSize" Value="13"/>
          <Setter Property="TextOptions.TextFormattingMode" Value="Ideal"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ContextMenu">
           <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="8" Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
            <StackPanel IsItemsHost="True" KeyboardNavigation.DirectionalNavigation="Cycle"/>
           </Border>
          </ControlTemplate></Setter.Value></Setter>
         </Style>
         <Style x:Key="StoreMenuItem" TargetType="MenuItem">
          <Setter Property="Foreground" Value="{StaticResource TextPrimary}"/><Setter Property="Padding" Value="12,7"/><Setter Property="MinWidth" Value="128"/>
          <Setter Property="Cursor" Value="Hand"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="MenuItem">
           <Border x:Name="Chrome" Background="Transparent" CornerRadius="5" Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
            <ContentPresenter ContentSource="Header" RecognizesAccessKey="True" VerticalAlignment="Center"/>
           </Border>
           <ControlTemplate.Triggers>
            <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Chrome" Property="Background" Value="#2E2E2E"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Chrome" Property="Opacity" Value="0.4"/></Trigger>
           </ControlTemplate.Triggers>
          </ControlTemplate></Setter.Value></Setter>
         </Style>
         <Style x:Key="ScrollPage" TargetType="RepeatButton">
          <Setter Property="Focusable" Value="False"/><Setter Property="IsTabStop" Value="False"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></Setter.Value></Setter>
         </Style>
         <Style x:Key="ScrollThumb" TargetType="Thumb">
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Thumb">
           <Border x:Name="Knob" Margin="3" Background="#383838" CornerRadius="3"/>
           <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Knob" Property="Background" Value="#6E6E6E"/></Trigger>
            <Trigger Property="IsDragging" Value="True"><Setter TargetName="Knob" Property="Background" Value="#E5C07B"/></Trigger>
           </ControlTemplate.Triggers>
          </ControlTemplate></Setter.Value></Setter>
         </Style>
         <Style TargetType="ScrollBar">
          <Setter Property="Background" Value="Transparent"/><Setter Property="Width" Value="11"/><Setter Property="MinWidth" Value="11"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollBar">
           <Track x:Name="PART_Track" IsDirectionReversed="True" Orientation="Vertical">
            <Track.DecreaseRepeatButton><RepeatButton Command="{x:Static ScrollBar.PageUpCommand}" Style="{StaticResource ScrollPage}"/></Track.DecreaseRepeatButton>
            <Track.Thumb><Thumb Style="{StaticResource ScrollThumb}"/></Track.Thumb>
            <Track.IncreaseRepeatButton><RepeatButton Command="{x:Static ScrollBar.PageDownCommand}" Style="{StaticResource ScrollPage}"/></Track.IncreaseRepeatButton>
           </Track>
          </ControlTemplate></Setter.Value></Setter>
          <Style.Triggers>
           <Trigger Property="Orientation" Value="Horizontal">
            <Setter Property="Width" Value="Auto"/><Setter Property="MinWidth" Value="0"/><Setter Property="Height" Value="11"/><Setter Property="MinHeight" Value="11"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollBar">
             <Track x:Name="PART_Track" Orientation="Horizontal">
              <Track.DecreaseRepeatButton><RepeatButton Command="{x:Static ScrollBar.PageLeftCommand}" Style="{StaticResource ScrollPage}"/></Track.DecreaseRepeatButton>
              <Track.Thumb><Thumb Style="{StaticResource ScrollThumb}"/></Track.Thumb>
              <Track.IncreaseRepeatButton><RepeatButton Command="{x:Static ScrollBar.PageRightCommand}" Style="{StaticResource ScrollPage}"/></Track.IncreaseRepeatButton>
             </Track>
            </ControlTemplate></Setter.Value></Setter>
           </Trigger>
          </Style.Triggers>
         </Style>
        </ResourceDictionary>
        """;
}
