using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ReceiptCapture.Services;

namespace ReceiptCapture;

/// <summary>
/// Hosts one page per tool, chosen from the sidebar. Each tool is a self-contained view with its own view model.
/// The sidebar collapses to an icon rail when the window is narrow (or when the person collapses it).
/// </summary>
public partial class MainWindow : Window
{
    private const double ExpandedWidth = 232;
    private const double CollapsedWidth = 66;
    private const double AutoCollapseBelow = 1180;   // window width under which the sidebar becomes the icon rail

    private readonly List<RadioButton> _navItems = new();
    private bool _userCollapsed;
    private bool _collapsed;
    private bool _sidebarReady;

    public MainWindow()
    {
        InitializeComponent();

        // Sidebar: one item per page, in the same order as the pages.
        CollectNavItems(NavHost);
        NavHost.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler(NavItem_Checked));
        _userCollapsed = SidebarSettings.LoadCollapsed();
        SizeChanged += (_, _) => ApplySidebar(animate: true);
        Loaded += (_, _) =>
        {
            _sidebarReady = true;
            ApplySidebar(animate: false);
        };

        // A green dot on Workflows while at least one folder is being watched.
        var badgeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        badgeTimer.Tick += (_, _) => UpdateWorkflowBadge();
        Loaded += (_, _) => { UpdateWorkflowBadge(); badgeTimer.Start(); };
        Closed += (_, _) => badgeTimer.Stop();
        Nav.SetBadgeBrush(WorkflowsNav, new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)));
        Nav.SetBadgeBrush(UpdateButton, new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)));

        SourceInitialized += (_, _) => AppTheme.ApplyTitleBar(this);
        AppTheme.Changed += OnThemeChanged;
        Closed += (_, _) => AppTheme.Changed -= OnThemeChanged;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F1)
            {
                ShowHelp();
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.B)
            {
                ToggleSidebar();
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is >= Key.D1 and <= Key.D9)
            {
                // Ctrl+1 ... Ctrl+9 jump straight to a page.
                int index = e.Key - Key.D1;
                if (index < _navItems.Count) _navItems[index].IsChecked = true;
                e.Handled = true;
            }
        };

        Closing += OnClosing;

        // A quiet look for a new version once the window is up. The Updates button changes if one is found.
        Updater.StateChanged += UpdateUpdateButton;
        Closed += (_, _) => Updater.StateChanged -= UpdateUpdateButton;
        Loaded += async (_, _) =>
        {
            try { await Updater.CheckInBackgroundAsync(); }
            catch (Exception) { /* an update check is a nicety: it must never get in the way of the program */ }
        };

        UpdateThemeButton();
        UpdateUpdateButton();
    }

    /// <summary>
    /// Gets the program ready to close so an update can be installed. If a meeting is being recorded, asks first and then
    /// stops and saves it. Returns false if the person said no.
    /// </summary>
    public bool PrepareToClose()
    {
        if (!MeetingsTab.IsRecording) return true;

        if (!Dialogs.Confirm(this, "A meeting is still being recorded.\n\nStop recording, save it, and install the update?"))
            return false;

        MeetingsTab.StopForExit();
        return true;
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e) => new UpdateDialog(this).ShowDialog();

    private void UpdateUpdateButton()
    {
        bool waiting = Updater.IsUpdateWaiting;
        UpdateButton.Content = waiting ? "Update available" : "Updates";
        Nav.SetBadge(UpdateButton, waiting);
    }

    // Never lose a meeting: if one is being recorded, ask first, then stop and save it before the app closes.
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!MeetingsTab.IsRecording) return;

        if (!Dialogs.Confirm(this, "A meeting is still being recorded.\n\nStop recording, save it, and close Aegis Docs?"))
        {
            e.Cancel = true;
            return;
        }

        MeetingsTab.StopForExit();
    }

    private HelpWindow? _help;

    private void ShowHelp()
    {
        if (_help == null)
        {
            _help = new HelpWindow { Owner = this };
            _help.Closed += (_, _) => _help = null;
            _help.Show();
        }
        else
        {
            if (_help.WindowState == WindowState.Minimized) _help.WindowState = WindowState.Normal;
            _help.Activate();
        }
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e) => ShowHelp();

    private void OnThemeChanged()
    {
        AppTheme.ApplyTitleBar(this);
        UpdateThemeButton();
    }

    private void UpdateThemeButton()
    {
        var label = AppTheme.Choice switch
        {
            ThemeChoice.System => "Auto",
            ThemeChoice.Light => "Light",
            _ => "Dark",
        };
        ThemeButton.Content = $"Theme: {label}";
    }

    // ------------------------------------------------------------------
    // Sidebar
    // ------------------------------------------------------------------
    private void CollectNavItems(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is RadioButton radio) _navItems.Add(radio);
            else if (child is DependencyObject node) CollectNavItems(node);
        }
    }

    private void NavItem_Checked(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not RadioButton radio) return;
        int index = _navItems.IndexOf(radio);
        if (index >= 0 && index < Pages.Items.Count) Pages.SelectedIndex = index;
    }

    private void UpdateWorkflowBadge()
    {
        try
        {
            var engine = Workflows.WorkflowEngine.Instance;
            Nav.SetBadge(WorkflowsNav, engine.Store.Workflows.Any(w => engine.IsWatching(w.Id)));
        }
        catch (Exception)
        {
            // A status dot is a nicety: never let it get in the way.
        }
    }

    private void ToggleSidebar()
    {
        _userCollapsed = !_userCollapsed;
        SidebarSettings.SaveCollapsed(_userCollapsed);
        ApplySidebar(animate: true);
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e) => ToggleSidebar();

    /// <summary>Collapsed when the person chose that, or when the window is too narrow to spare the room.</summary>
    private void ApplySidebar(bool animate)
    {
        if (!_sidebarReady) return;

        bool collapsed = _userCollapsed || ActualWidth < AutoCollapseBelow;
        if (animate && collapsed == _collapsed) return;
        _collapsed = collapsed;

        Nav.SetIsCollapsed(Sidebar, collapsed);
        CollapseButton.Content = collapsed ? "Expand" : "Collapse";
        CollapseButton.Tag = FindResource(collapsed ? "IconChevronRight" : "IconChevronLeft");
        CollapseButton.ToolTip = collapsed ? "Expand the sidebar (Ctrl+B)" : "Collapse the sidebar (Ctrl+B)";

        double target = collapsed ? CollapsedWidth : ExpandedWidth;
        if (!animate)
        {
            Sidebar.BeginAnimation(WidthProperty, null);
            Sidebar.Width = target;
            return;
        }

        Sidebar.BeginAnimation(WidthProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e) => AppTheme.Cycle();
}
