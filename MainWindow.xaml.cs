using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace STRAFTATWpfTrainer;

public partial class MainWindow : Window
{
    private const string DonationAddress = "3BwF9vvbhrQSecmYvzHaGVtMbH4R3r4K3n";

    private readonly TrainerBackend _backend = new();
    private readonly DispatcherTimer _rgbTimer;
    private readonly DispatcherTimer _bannerTimer;
    private readonly List<BitmapSource> _bannerFrames = [];
    private string _lastBackendStatus = "Not attached";
    private int _settingsRevision;
    private double _rgbHue;
    private int _bannerFrameIndex;

    public MainWindow()
    {
        InitializeComponent();

        _backend.StatusChanged += status =>
            Dispatcher.Invoke(() => SetStatus(status));

        Loaded += (_, _) => EnableAcrylicBlur();

        _rgbTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(12)
        };
        _rgbTimer.Tick += (_, _) => AnimateRgbBorder();

        _bannerTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(70)
        };
        _bannerTimer.Tick += (_, _) => AdvanceBannerFrame();

        Loaded += (_, _) => LoadLootbetBanner();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        OkButton.IsEnabled = false;
        SetStatus("Attaching to STRAFTAT.exe...");

        var enabled = await _backend.AttachAndEnableAsync(GetCurrentSettings());
        OkButton.IsEnabled = true;

        if (!enabled)
        {
            MessageBox.Show(
                _lastBackendStatus,
                "STRAFTAT Trainer",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        SplashPanel.Visibility = Visibility.Collapsed;
        ControlsPanel.Visibility = Visibility.Visible;
        GlassSurface.Background = (Brush)FindResource("PurpleGlassFill");
        _rgbTimer.Start();
        Height = 700;
        MinHeight = 700;
        SpeedHackCheckBox.IsEnabled = true;
    }

    private void Disable_Click(object sender, RoutedEventArgs e)
    {
        _backend.DisableHooks();
        ControlsPanel.Visibility = Visibility.Collapsed;
        SplashPanel.Visibility = Visibility.Visible;
        GlassSurface.Background = (Brush)FindResource("GreyGlassFill");
        _rgbTimer.Stop();
        RgbStopA.Color = Color.FromRgb(0x90, 0x90, 0x90);
        RgbStopB.Color = Color.FromRgb(0x6F, 0x6F, 0x76);
        RgbStopC.Color = Color.FromRgb(0x90, 0x90, 0x90);
        Height = 680;
        MinHeight = 540;
    }

    private void CopyDonate_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(DonationAddress);
        SetStatus("Donation address copied");
    }

    private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SpeedValueText != null)
        {
            SpeedValueText.Text = $"{e.NewValue:0.0}x";
        }

        PushSettings();
    }

    private void FireDelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FireDelayText != null)
        {
            var delay = GetFireDelay();
            FireDelayText.Text = $"{delay:0.00000}s";
        }

        PushSettings();
    }

    private void OptionChanged(object sender, RoutedEventArgs e)
    {
        PushSettings();
    }

    private void GodMode_Click(object sender, RoutedEventArgs e)
    {
        GodModeCheckBox.IsChecked = false;
        MessageBox.Show(
            "go fuck your self",
            "Windows Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    protected override void OnClosed(EventArgs e)
    {
        _backend.Dispose();
        base.OnClosed(e);
    }

    private void PushSettings()
    {
        if (SpeedSlider == null || FireDelaySlider == null)
        {
            return;
        }

        var settings = GetCurrentSettings();
        var revision = Interlocked.Increment(ref _settingsRevision);
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            if (revision == Volatile.Read(ref _settingsRevision))
            {
                _backend.UpdateSettings(settings);
            }
        });
    }

    private TrainerSettings GetCurrentSettings()
    {
        return new TrainerSettings(
            EnableInfAmmo: InfiniteAmmoCheckBox?.IsChecked == true,
            EnableFastFire: FastFireCheckBox?.IsChecked == true,
            EnableNoSpread: NoSpreadCheckBox?.IsChecked == true,
            EnableNoRecoil: NoRecoilCheckBox?.IsChecked == true,
            EnableSpeed: SpeedHackCheckBox?.IsChecked == true,
            FireDelay: (float)GetFireDelay(),
            SpeedMultiplier: (float)(SpeedSlider?.Value ?? 4.0));
    }

    private double GetFireDelay()
    {
        return Math.Clamp(0.001 + ((FireDelaySlider?.Value ?? 900.0) / 100000.0), 0.001, 0.20);
    }

    private void SetStatus(string status)
    {
        _lastBackendStatus = status;
        if (StatusText != null)
        {
            StatusText.Text = status;
        }
    }

    private void AnimateRgbBorder()
    {
        _rgbHue = (_rgbHue + 12.0) % 360.0;
        RgbStopA.Color = FromHsv(_rgbHue, 0.95, 1.0);
        RgbStopB.Color = FromHsv((_rgbHue + 120.0) % 360.0, 0.95, 1.0);
        RgbStopC.Color = FromHsv((_rgbHue + 240.0) % 360.0, 0.95, 1.0);

        if (GodModeText != null)
        {
            GodModeText.Foreground = new SolidColorBrush(FromHsv((_rgbHue + 80.0) % 360.0, 1.0, 1.0));
        }
    }

    private void LoadLootbetBanner()
    {
        var resourceInfo = Application.GetResourceStream(
            new Uri("lootbet-csgo-betting-1479745011.gif", UriKind.Relative));
        if (resourceInfo == null)
        {
            return;
        }

        var decoder = new GifBitmapDecoder(
            resourceInfo.Stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);

        _bannerFrames.Clear();
        ComposeGifFrames(decoder, _bannerFrames);

        if (_bannerFrames.Count == 0)
        {
            return;
        }

        _bannerFrameIndex = 0;
        LootbetBannerImage.Source = _bannerFrames[0];

        if (_bannerFrames.Count > 1)
        {
            _bannerTimer.Start();
        }
    }

    private static void ComposeGifFrames(BitmapDecoder decoder, List<BitmapSource> output)
    {
        if (decoder.Frames.Count == 0)
        {
            return;
        }

        var width = decoder.Frames[0].PixelWidth;
        var height = decoder.Frames[0].PixelHeight;
        BitmapSource? baseFrame = null;

        foreach (var frame in decoder.Frames)
        {
            var metadata = frame.Metadata as BitmapMetadata;
            var left = GetMetadataInt(metadata, "/imgdesc/Left", 0);
            var top = GetMetadataInt(metadata, "/imgdesc/Top", 0);
            var disposal = GetMetadataInt(metadata, "/grctlext/Disposal", 0);

            var composed = RenderCompositedFrame(baseFrame, frame, width, height, left, top);
            output.Add(composed);

            baseFrame = disposal == 2
                ? baseFrame
                : composed;
        }
    }

    private static BitmapSource RenderCompositedFrame(
        BitmapSource? baseFrame,
        BitmapSource frame,
        int width,
        int height,
        int left,
        int top)
    {
        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            if (baseFrame != null)
            {
                context.DrawImage(baseFrame, new Rect(0, 0, width, height));
            }

            context.DrawImage(frame, new Rect(left, top, frame.PixelWidth, frame.PixelHeight));
        }

        var render = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        render.Render(visual);
        render.Freeze();
        return render;
    }

    private static int GetMetadataInt(BitmapMetadata? metadata, string query, int fallback)
    {
        if (metadata == null || !metadata.ContainsQuery(query))
        {
            return fallback;
        }

        var value = metadata.GetQuery(query);
        return value switch
        {
            byte number => number,
            ushort number => number,
            short number => number,
            uint number => (int)number,
            int number => number,
            _ => fallback
        };
    }

    private void AdvanceBannerFrame()
    {
        if (_bannerFrames.Count == 0)
        {
            return;
        }

        _bannerFrameIndex = (_bannerFrameIndex + 1) % _bannerFrames.Count;
        LootbetBannerImage.Source = _bannerFrames[_bannerFrameIndex];
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs((hue / 60.0 % 2) - 1));
        var m = value - chroma;

        (double r, double g, double b) = hue switch
        {
            < 60 => (chroma, x, 0.0),
            < 120 => (x, chroma, 0.0),
            < 180 => (0, chroma, x),
            < 240 => (0, x, chroma),
            < 300 => (x, 0, chroma),
            _ => (chroma, 0.0, x)
        };

        return Color.FromRgb(
            (byte)((r + m) * 255),
            (byte)((g + m) * 255),
            (byte)((b + m) * 255));
    }

    private void EnableAcrylicBlur()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var accent = new AccentPolicy
        {
            AccentState = AccentState.AccentEnableAcrylicBlurBehind,
            GradientColor = unchecked((int)0x4A8A1F78)
        };

        var accentSize = Marshal.SizeOf(accent);
        var accentPointer = Marshal.AllocHGlobal(accentSize);

        try
        {
            Marshal.StructureToPtr(accent, accentPointer, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttribute.WcaAccentPolicy,
                SizeOfData = accentSize,
                Data = accentPointer
            };

            SetWindowCompositionAttribute(handle, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(accentPointer);
        }
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    private enum AccentState
    {
        AccentDisabled = 0,
        AccentEnableGradient = 1,
        AccentEnableTransparentGradient = 2,
        AccentEnableBlurBehind = 3,
        AccentEnableAcrylicBlurBehind = 4
    }

    private enum WindowCompositionAttribute
    {
        WcaAccentPolicy = 19
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public WindowCompositionAttribute Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }
}
