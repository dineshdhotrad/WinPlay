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
    private readonly IntPtr _hwnd;
    private bool _suppressDeactivateHide;

    public FlyoutWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        // Window-level acrylic. Note the known consequence: a window backdrop is composited by
        // the window, so it appears at full opacity the moment the window is shown while only
        // the content animates — the glass does not fade in with the card.
        //
        // The documented fix is an in-tree SystemBackdropElement (Windows App SDK 2.x), which
        // WAS attempted here: it compiles, but at runtime it shifts the x:Bind connection ids
        // so the generated Connect() casts the wrong element and the window throws
        // InvalidCastException on load. Verified twice, including after a full obj/bin clean.
        // A cosmetic improvement is not worth a window that cannot open, so the window backdrop
        // stays until that codegen interaction is understood.
        SystemBackdrop = new DesktopAcrylicBackdrop();

        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        _appWindow.IsShownInSwitchers = false;

        var presenter = (OverlappedPresenter)_appWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;

        // Win11 rounded corners for a borderless popup.
        int cornerPreference = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(_hwnd, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/,
            ref cornerPreference, sizeof(int));

        Activated += OnActivated;
        _appWindow.Closing += OnAppWindowClosing;

        // Escape dismisses, the same as every other Windows flyout. Its absence is the kind of
        // thing you only notice by reaching for it and finding nothing there.
        if (Content is UIElement root)
        {
            root.KeyDown += (_, e) =>
            {
                if (e.Key != Windows.System.VirtualKey.Escape) return;
                e.Handled = true;
                AnimateAndHide();
            };
        }
        ViewModel.RequestPin = ShowPinDialogAsync;
    }

    private bool _allowClose;

    /// <summary>Raised once the flyout has finished hiding — the app is idle again.</summary>
    public event Action? Hidden;

    /// <summary>Permits the window to close for real — called during an explicit Quit.</summary>
    public void AllowClose() => _allowClose = true;

    /// <summary>
    /// The flyout is WinPlay's persistent window (this is a tray app). Never actually close
    /// it — hide instead — so closing the flyout (Alt+F4, system menu) can never terminate
    /// the app by dropping the open-window count to zero. Only the tray "Quit" exits,
    /// via Application.Exit() after <see cref="AllowClose"/>.
    /// </summary>
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        if (_appWindow.IsVisible) AnimateAndHide();
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

        // Take the foreground explicitly. Show() makes the window visible and topmost, but topmost
        // is z-order, not activation — and the click that opened us landed on Explorer's tray
        // window, so this process did not "receive the last input event" that SetForegroundWindow's
        // rules require and WinUI's Activate() is not reliably granted. The flyout then sat there
        // visible but never actually active, which means it had nothing to deactivate FROM: click
        // anywhere else and no Deactivated ever arrived, so it never closed.
        //
        // TrayIcon.ShowMenu already does exactly this, for exactly this reason, one call up the
        // stack. It was simply never extended to the window itself.
        SetForegroundWindow(_hwnd);
        Activate();
        _suppressDeactivateHide = false;
        AnimateIn();
    }

    public void HideFlyout() => AnimateAndHide();

    private bool _closing;

    private Visual RootVisual => ElementCompositionPreview.GetElementVisual((UIElement)Content);

    /// <summary>
    /// Whether the user wants animations at all. Read fresh each time rather than cached, because
    /// it is a setting someone can change while the app is running and expect to take effect.
    /// </summary>
    private static bool AnimationsEnabled
    {
        get
        {
            try { return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled; }
            catch { return true; }   // never let a settings read stop the window from opening
        }
    }

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

        // Honour the system's animation setting before doing anything else. Windows exposes this
        // in Settings > Accessibility > Visual effects, and people turn it off for real reasons —
        // motion sensitivity, or an older machine where every animation is a stutter. An app that
        // animates anyway is not being polished, it is ignoring an explicit instruction.
        if (!AnimationsEnabled)
        {
            visual.Opacity = 1f;
            visual.Offset = Vector3.Zero;
            visual.Scale = Vector3.One;
            return;
        }

        // Size the transform origin from the WINDOW, not from ActualSize.
        //
        // The window has only just been shown, so on the first open the content has not been laid
        // out yet and ActualSize is still (0,0). That put CenterPoint at the top-left corner, so
        // the very first time the user ever opened the picker it grew out of the corner of the
        // screen instead of rising off the tray — the one opening that forms their impression of
        // the app, and the only one that looked broken. The window's own client size is known the
        // moment it is positioned, and is the same size the content is about to take.
        float dpiScale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96f;
        var client = _appWindow.ClientSize;
        var size = root.ActualSize.X > 0
            ? root.ActualSize
            : new Vector2(client.Width / dpiScale, client.Height / dpiScale);
        visual.CenterPoint = new Vector3(size.X / 2f, size.Y, 0f); // grow from the bottom (tray) edge

        // Fluent's actual entrance curve — "Fast Out, Slow In", cubic-bezier(0,0,0,1). Windows
        // uses this for anything spawning into view; its very fast start and long tail are what
        // make platform motion recognisable. (Previous values here were invented and read wrong.)
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0f, 0f), new Vector2(0f, 1f));
        var dur = TimeSpan.FromMilliseconds(300); // matches the platform's "expand" duration

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

        if (!AnimationsEnabled)
        {
            _appWindow.Hide();
            Hidden?.Invoke();
            return;
        }

        // Fluent's actual exit curve — "Slow Out, Fast In", cubic-bezier(1,0,1,1) — and the
        // platform's 150 ms exit duration. Exits are deliberately faster than entrances.
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(1f, 0f), new Vector2(1f, 1f));
        var dur = TimeSpan.FromMilliseconds(150);

        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);

        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 0f, ease); fade.Duration = dur;
        visual.StartAnimation("Opacity", fade);

        var slide = c.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(1f, new Vector3(0f, 16f, 0f), ease); slide.Duration = dur;
        visual.StartAnimation("Offset", slide);

        batch.Completed += (_, _) =>
        {
            if (!_closing) return;
            _appWindow.Hide();
            Hidden?.Invoke();
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

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
