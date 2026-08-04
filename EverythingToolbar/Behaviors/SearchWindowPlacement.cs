using System;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using Microsoft.Xaml.Behaviors;
using NLog;
using Windows.Win32.Foundation;
using Size = System.Windows.Size;

namespace EverythingToolbar.Behaviors
{
    public class SearchWindowPlacement : Behavior<SearchWindow>
    {
        private static readonly ILogger Logger = ToolbarLogger.GetLogger<SearchWindowPlacement>();

        public FrameworkElement? PlacementTarget
        {
            get => _placementTarget;
            set
            {
                if (ReferenceEquals(_placementTarget, value))
                    return;

                // The target can be swapped after attaching, so the subscription has to move with it.
                if (_isAttached && _placementTarget != null)
                    _placementTarget.Loaded -= OnPlacementTargetLoaded;

                _placementTarget = value;

                if (_isAttached && _placementTarget != null)
                    _placementTarget.Loaded += OnPlacementTargetLoaded;
            }
        }

        private FrameworkElement? _placementTarget;
        private bool _isAttached;
        private double _dpiScalingFactor = 1.0;
        private readonly TaskbarInfoProvider _taskbarState;
        private readonly ISettings _settings;
        private readonly WindowsPolicy _windowsPolicy;

        public SearchWindowPlacement(TaskbarInfoProvider taskbarState, ISettings settings, WindowsPolicy windowsPolicy)
        {
            _taskbarState = taskbarState;
            _settings = settings;
            _windowsPolicy = windowsPolicy;
        }

        protected override void OnAttached()
        {
            AssociatedObject.Left = 100000;
            AssociatedObject.Top = 100000;

            AssociatedObject.Showing += OnShowing;
            AssociatedObject.Hiding += OnHiding;

            _isAttached = true;

            if (_placementTarget != null)
                _placementTarget.Loaded += OnPlacementTargetLoaded;
        }

        protected override void OnDetaching()
        {
            AssociatedObject.Showing -= OnShowing;
            AssociatedObject.Hiding -= OnHiding;

            if (_placementTarget != null)
                _placementTarget.Loaded -= OnPlacementTargetLoaded;

            _isAttached = false;
        }

        private void OnPlacementTargetLoaded(object sender, RoutedEventArgs e)
        {
            _dpiScalingFactor = GetScalingFactor();
        }

        private void OnHiding(object? sender, EventArgs e)
        {
            AssociatedObject.AnimateHide(_taskbarState.TaskbarEdge);
        }

        private void OnShowing(object? sender, ShowingEventArgs e)
        {
            _ = e; // AtCursor/PlacementTarget ignored: local fork always centers.
            _dpiScalingFactor = GetScalingFactor();

            // Always center on the working area (both axes), matching pre-v3 WindowPlacement.
            var position = CalculateCenteredPosition();
            var size = GetTargetWindowSizeDip();

            AssociatedObject.AnimateShow(
                position.left * _dpiScalingFactor,
                position.top * _dpiScalingFactor,
                size.Width,
                size.Height,
                _taskbarState.TaskbarEdge
            );
        }

        /// <summary>
        /// Center the popup on the monitor that contains the cursor (working area, both axes).
        /// </summary>
        private RECT CalculateCenteredPosition()
        {
            var screen = Screen.FromPoint(Cursor.Position);
            var taskbar = FindDockedTaskBar(screen);
            _taskbarState.TaskbarEdge = taskbar.Edge;

            var windowSize = GetTargetWindowSize();
            var margin = GetMargin();
            var workingArea = screen.WorkingArea;

            var centeredWidth = Math.Min((int)windowSize.Width, Math.Max(0, workingArea.Width - 2 * margin));
            var centeredHeight = Math.Min((int)windowSize.Height, Math.Max(0, workingArea.Height - 2 * margin));

            var left = workingArea.Left + Math.Max(margin, (workingArea.Width - centeredWidth) / 2);
            var top = workingArea.Top + Math.Max(margin, (workingArea.Height - centeredHeight) / 2);

            return new RECT
            {
                left = left,
                top = top,
                right = Math.Min(left + centeredWidth, workingArea.Right - margin),
                bottom = Math.Min(top + centeredHeight, workingArea.Bottom - margin),
            };
        }



        private Size GetTargetWindowSizeDip()
        {
            return new Size(
                Math.Max(_settings.PopupWidth, AssociatedObject.MinWidth),
                Math.Max(_settings.PopupHeight, AssociatedObject.MinHeight)
            );
        }

        private Size GetTargetWindowSize()
        {
            var windowSize = GetTargetWindowSizeDip();
            return new Size(windowSize.Width / _dpiScalingFactor, windowSize.Height / _dpiScalingFactor);
        }


        private TaskbarLocation FindDockedTaskBar(Screen screen)
        {
            var topDockedHeight = Math.Abs(Math.Abs(screen.Bounds.Top) - Math.Abs(screen.WorkingArea.Top));
            var bottomDockedHeight = screen.Bounds.Height - topDockedHeight - screen.WorkingArea.Height;
            var leftDockedWidth = Math.Abs(Math.Abs(screen.Bounds.Left) - Math.Abs(screen.WorkingArea.Left));
            var rightDockedWidth = screen.Bounds.Width - leftDockedWidth - screen.WorkingArea.Width;

            if (leftDockedWidth > 0 && bottomDockedHeight == 0)
            {
                return new TaskbarLocation
                {
                    Position = new Rectangle(
                        screen.Bounds.Left,
                        screen.Bounds.Top,
                        leftDockedWidth,
                        screen.Bounds.Height
                    ),
                    Edge = Edge.Left,
                };
            }
            if (rightDockedWidth > 0 && bottomDockedHeight == 0)
            {
                return new TaskbarLocation
                {
                    Position = new Rectangle(
                        screen.WorkingArea.Right,
                        screen.Bounds.Top,
                        rightDockedWidth,
                        screen.Bounds.Height
                    ),
                    Edge = Edge.Right,
                };
            }
            if (topDockedHeight > 0 && bottomDockedHeight == 0)
            {
                return new TaskbarLocation
                {
                    Position = new Rectangle(
                        screen.WorkingArea.Left,
                        screen.Bounds.Top,
                        screen.WorkingArea.Width,
                        topDockedHeight
                    ),
                    Edge = Edge.Top,
                };
            }

            return new TaskbarLocation
            {
                Position = new Rectangle(
                    screen.WorkingArea.Left,
                    screen.WorkingArea.Bottom,
                    screen.WorkingArea.Width,
                    bottomDockedHeight
                ),
                Edge = Edge.Bottom,
            };
        }

        private double GetScalingFactor()
        {
            var visual = PlacementTarget ?? (System.Windows.Media.Visual)AssociatedObject;
            if (PresentationSource.FromVisual(visual) is not HwndSource hwndSource)
            {
                Logger.Error("Failed to get display scaling factor. This may result in incorrect window placement.");
                return 1.0;
            }

            return 96.0 / NativeMethods.GetDpiForWindow(hwndSource.Handle);
        }

        private int GetMargin()
        {
            var marginDip = _windowsPolicy.GetEffectiveWindowsVersion() >= WindowsVersion.Windows11 ? 12 : 0;
            return (int)Math.Round(marginDip / GetScalingFactor());
        }

        private struct TaskbarLocation
        {
            public Rectangle Position;
            public Edge Edge;
        }
    }
}
