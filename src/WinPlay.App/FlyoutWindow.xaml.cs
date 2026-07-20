// SPDX-License-Identifier: GPL-3.0-or-later
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using WinPlay.App.ViewModels;
using WinRT.Interop;

namespace WinPlay.App;

/// <summary>
/// The quick-settings–style flyout: borderless, Acrylic ("liquid glass"), rounded,
/// always-on-top, anchored above the taskbar tray corner, hidden on focus loss.
/// </summary>
public sealed partial class FlyoutWindow : Window
{
    private const int BaseWidth = 384;
    private const int BaseMaxHeight = 560;

    public MainViewModel ViewModel { get; }

    private readonly AppWindow _appWindow;
    private bool _suppressDeactivateHide;

    public FlyoutWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        SystemBackdrop = new DesktopAcrylicBackdrop();

        IntPtr hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        _appWindow.IsShownInSwitchers = false;

        var presenter = (OverlappedPresenter)_appWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;

        // Win11 rounded corners for a borderless popup.
        int cornerPreference = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/,
            ref cornerPreference, sizeof(int));

        Activated += OnActivated;
        ViewModel.RequestPin = ShowPinDialogAsync;
    }

    /// <summary>
    /// Modal PIN entry for first-time pairing with a PIN-protected receiver. Runs on
    /// the UI thread regardless of the caller; keeps the flyout open while shown.
    /// </summary>
    private Task<string?> ShowPinDialogAsync(string deviceName)
    {
        var completion = new TaskCompletionSource<string?>();
        bool queued = DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                _suppressDeactivateHide = true;
                if (!_appWindow.IsVisible) Toggle();

                var pinBox = new TextBox
                {
                    PlaceholderText = "PIN",
                    MaxLength = 4,
                    Width = 160,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                var dialog = new ContentDialog
                {
                    Title = $"Pair with “{deviceName}”",
                    Content = new StackPanel
                    {
                        Spacing = 12,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = $"Enter the PIN shown on {deviceName}. You only need to do this once.",
                                TextWrapping = TextWrapping.Wrap,
                            },
                            pinBox,
                        },
                    },
                    PrimaryButtonText = "Pair",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = Content.XamlRoot,
                };
                var result = await dialog.ShowAsync();
                completion.TrySetResult(result == ContentDialogResult.Primary ? pinBox.Text?.Trim() : null);
            }
            catch (Exception)
            {
                completion.TrySetResult(null);
            }
            finally
            {
                _suppressDeactivateHide = false;
            }
        });
        if (!queued) completion.TrySetResult(null);
        return completion.Task;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated
            && !_suppressDeactivateHide && _appWindow.IsVisible)
        {
            AnimateAndHide();
        }
    }

    public bool IsVisible => _appWindow.IsVisible;

    public void Toggle()
    {
        if (_appWindow.IsVisible) AnimateAndHide();
        else ShowNearTray();
    }

    /// <summary>Shows the flyout anchored above the tray with an iOS-style fade + slide-up.</summary>
    public void ShowNearTray()
    {
        _closing = false;
        PositionNearTray();
        _suppressDeactivateHide = true;
        _appWindow.Show();
        Activate();
        _suppressDeactivateHide = false;
        AnimateIn();
    }

    public void HideFlyout() => AnimateAndHide();

    private bool _closing;

    private Visual RootVisual => ElementCompositionPreview.GetElementVisual((UIElement)Content);

    /// <summary>
    /// Smooth, continuous fade + slide-up + subtle grow of the whole card, one ease-out
    /// curve over the full motion (no spring bounce). WinUI's system acrylic backdrop is
    /// painted by the window and can't be animated as content, so the card content carries
    /// the motion; keeping the fade opacity-led from 0 keeps the reveal continuous.
    /// </summary>
    private void AnimateIn()
    {
        var root = (UIElement)Content;
        var visual = RootVisual;
        var c = visual.Compositor;
        var size = root.ActualSize;
        visual.CenterPoint = new Vector3(size.X / 2f, size.Y, 0f); // grow from the bottom (tray) edge

        // Windows-flyout-like ease-out: fast start, gentle deceleration, no overshoot.
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
        var dur = TimeSpan.FromMilliseconds(300);

        visual.Opacity = 0f;
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, ease);
        fade.Duration = dur;
        visual.StartAnimation("Opacity", fade);

        visual.Offset = new Vector3(0f, 44f, 0f);
        var slide = c.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0f, new Vector3(0f, 44f, 0f));
        slide.InsertKeyFrame(1f, Vector3.Zero, ease);
        slide.Duration = dur;
        visual.StartAnimation("Offset", slide);

        visual.Scale = new Vector3(0.92f, 0.92f, 1f);
        var scale = c.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0f, new Vector3(0.92f, 0.92f, 1f));
        scale.InsertKeyFrame(1f, Vector3.One, ease);
        scale.Duration = dur;
        visual.StartAnimation("Scale", scale);
    }

    /// <summary>Fade out + slide down, then hide the window when the animation completes (~140 ms).</summary>
    private void AnimateAndHide()
    {
        if (_closing) return;
        _closing = true;
        var visual = RootVisual;
        var c = visual.Compositor;
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f)); // ease-in
        var dur = TimeSpan.FromMilliseconds(130);

        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);

        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 0f, ease); fade.Duration = dur;
        visual.StartAnimation("Opacity", fade);

        var slide = c.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(1f, new Vector3(0f, 16f, 0f), ease); slide.Duration = dur;
        visual.StartAnimation("Offset", slide);

        batch.Completed += (_, _) =>
        {
            if (_closing) _appWindow.Hide();
        };
        batch.End();
    }

    private void PositionNearTray()
    {
        var display = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        var work = display.WorkArea;

        double scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        int width = (int)(BaseWidth * scale);
        int maxHeight = (int)(BaseMaxHeight * scale);
        int margin = (int)(12 * scale);

        // Content-sized height: measure the root element, clamp to max.
        int height = maxHeight;
        if (Content is FrameworkElement root && root.ActualHeight > 0)
            height = Math.Min(maxHeight, (int)((root.ActualHeight + 8) * scale));

        // Bottom-right of the work area = above the taskbar, near the tray corner.
        int x = work.X + work.Width - width - margin;
        int y = work.Y + work.Height - height - margin;
        _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute,
        ref int value, int size);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
