using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Net.ViaTheFalcon.JnapUpMon.Remote.ViewModels;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace Net.ViaTheFalcon.JnapUpMon.Remote;

/// <summary>
/// The single window of the app: hosts the device drop down, the read-only
/// characteristic display and the action buttons.
/// </summary>
public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(IntPtr hInst, string exeFileName, int iconIndex);

    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        ViewModel = new MainViewModel();
        ViewModel.ConfirmRebootAsync = ShowRebootConfirmationAsync;
        InitializeComponent();

        Title = Localizer.Get("AppTitle/Text");

        // The embedded ApplicationIcon covers Explorer and the taskbar; reuse the
        // very same icon resource for the title bar caption instead of shipping a
        // separate file. Apply it once the window has been activated, otherwise the
        // title bar does not pick the icon up.
        Activated += OnFirstActivated;

        // Size the window to its content before it is shown, so the user never
        // sees the default size flash.
        SizeToContent();

        // Once the content has gone through a real layout pass (with the final
        // theme, fonts and monitor DPI resolved) correct the size once more.
        RootGrid.Loaded += OnRootGridLoaded;

        // Begin background scanning straight away and clean up on close.
        ViewModel.Start();
        Closed += OnClosed;
    }

    /// <summary>
    /// Extracts the first icon embedded in this executable (the one declared via
    /// <c>ApplicationIcon</c>) and applies it to the window's title bar caption,
    /// so no separate icon file needs to be shipped.
    /// </summary>
    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;
        SetTitleBarIconFromExecutable();
    }

    private void SetTitleBarIconFromExecutable()
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            return;
        }

        // ExtractIcon returns 1 when the file holds no icons, NULL on failure.
        IntPtr hIcon = ExtractIcon(IntPtr.Zero, exePath, 0);
        if (hIcon == IntPtr.Zero || hIcon.ToInt64() == 1)
        {
            return;
        }

        // The IconId references this HICON rather than copying it. The title bar
        // reads the icon immediately, but the taskbar button reads it lazily, so
        // the handle must outlive this method. Keep it for the app's lifetime
        // (a single icon) and let the OS reclaim it on exit.
        IconId iconId = Win32Interop.GetIconIdFromIcon(hIcon);
        AppWindow.SetIcon(iconId);
    }

    private void OnRootGridLoaded(object sender, RoutedEventArgs e)
    {
        // A one-shot correction is enough; detach so later layout passes (e.g. the
        // user resizing the window) are not overridden.
        RootGrid.Loaded -= OnRootGridLoaded;

        // Prefer the framework's own rasterization scale now that it is available.
        double scale = RootGrid.XamlRoot?.RasterizationScale ?? 0.0;
        SizeToContent(scale > 0 ? scale : null);
    }

    /// <summary>
    /// Resizes the window's client area to the content's natural size: just wide
    /// enough for the action buttons and just tall enough for the status line.
    /// The empty star-sized row collapses during an unconstrained measure, so the
    /// height tracks the content rather than padding it out.
    /// </summary>
    /// <param name="scaleOverride">
    /// The DIP-to-pixel scale to use. When <c>null</c> (the pre-activation case),
    /// the scale is read from the window handle's DPI.
    /// </param>
    private void SizeToContent(double? scaleOverride = null)
    {
        // The element tree exists after InitializeComponent, so we can measure it
        // directly even though it has not been rendered yet.
        RootGrid.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size desired = RootGrid.DesiredSize;

        // XamlRoot is not available until the window is activated, so before then
        // read the DPI from the window handle. DesiredSize is in DIPs; AppWindow
        // works in physical pixels.
        double scale;
        if (scaleOverride is double provided)
        {
            scale = provided;
        }
        else
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            uint dpi = GetDpiForWindow(hwnd);
            scale = dpi == 0 ? 1.0 : dpi / 96.0;
        }

        var size = new SizeInt32(
            (int)Math.Ceiling(desired.Width * scale),
            (int)Math.Ceiling(desired.Height * scale));

        AppWindow.ResizeClient(size);
    }

    private void OnClosed(object sender, WindowEventArgs args)
        => ViewModel.Dispose();

    /// <summary>
    /// Shows a WinUI 3 <see cref="ContentDialog"/> that requires the user to tick a
    /// confirmation checkbox before the primary button becomes enabled.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the user ticked the checkbox and clicked the primary button;
    /// <c>false</c> if the user dismissed or cancelled the dialog.
    /// </returns>
    private async Task<bool> ShowRebootConfirmationAsync()
    {
        var checkBox = new CheckBox
        {
            Content = Localizer.Get("RebootConfirm_Check"),
        };

        var dialog = new ContentDialog
        {
            Title = Localizer.Get("RebootConfirm_Title"),
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = Localizer.Get("RebootConfirm_Message"),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    checkBox,
                },
            },
            PrimaryButtonText = Localizer.Get("RebootConfirm_Proceed"),
            CloseButtonText = Localizer.Get("RebootConfirm_Cancel"),
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
            XamlRoot = RootGrid.XamlRoot,
        };

        checkBox.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
        checkBox.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;

        ContentDialogResult result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }
}
