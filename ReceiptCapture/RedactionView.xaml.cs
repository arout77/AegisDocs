using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using ReceiptCapture.Core.Redaction;
using ReceiptCapture.Services;
using ReceiptCapture.ViewModels;

namespace ReceiptCapture;

public partial class RedactionView : UserControl
{
    private readonly RedactionViewModel _vm;
    private Point? _dragStart;

    public RedactionView()
    {
        InitializeComponent();

        _vm = new RedactionViewModel(new WindowsRedactionService())
        {
            ShowMessage = text => Dialogs.Info(this, text),

            PickFile = () =>
            {
                var dialog = new OpenFileDialog { Title = "Choose a PDF to redact", Filter = "PDF files|*.pdf" };
                return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
            },

            PickSavePath = suggestedName =>
            {
                var dialog = new SaveFileDialog
                {
                    Title = "Save the redacted copy",
                    Filter = "PDF file|*.pdf",
                    FileName = suggestedName,
                };
                return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
            },
        };

        DataContext = _vm;
    }

    // ---------------- drag a PDF onto the tab ----------------
    private void View_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private async void View_PreviewDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        var pdf = paths.FirstOrDefault(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        if (pdf == null)
        {
            Dialogs.Info(this, "Drop a PDF file here.");
            return;
        }

        try
        {
            await _vm.OpenAsync(pdf);
        }
        catch (Exception ex)
        {
            Dialogs.Info(this, ex.Message);
        }
    }

    // ---------------- zoom ----------------
    private void PageScroller_SizeChanged(object sender, SizeChangedEventArgs e)
        => _vm.SetViewport(PageScroller.ActualWidth, PageScroller.ActualHeight);

    // Ctrl + mouse wheel zooms; a plain wheel scrolls the page as usual.
    private void PageScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        Run(e.Delta > 0 ? _vm.ZoomInCommand : _vm.ZoomOutCommand);
        e.Handled = true;
    }

    private void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;

        switch (e.Key)
        {
            case Key.OemPlus:
            case Key.Add:
                Run(_vm.ZoomInCommand);
                e.Handled = true;
                break;
            case Key.OemMinus:
            case Key.Subtract:
                Run(_vm.ZoomOutCommand);
                e.Handled = true;
                break;
            case Key.D0:
            case Key.NumPad0:
                Run(_vm.ZoomFitCommand);
                e.Handled = true;
                break;
        }
    }

    private static void Run(ICommand command)
    {
        if (command.CanExecute(null)) command.Execute(null);
    }

    // ---------------- draw a new box ----------------
    // Positions are read relative to the canvas, which is sized in page-image pixels, so no scaling is needed.
    private void DrawCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var start = e.GetPosition(DrawCanvas);
        _dragStart = start;

        Canvas.SetLeft(PreviewRect, start.X);
        Canvas.SetTop(PreviewRect, start.Y);
        PreviewRect.Width = 0;
        PreviewRect.Height = 0;
        PreviewRect.Visibility = Visibility.Visible;
        DrawCanvas.CaptureMouse();
    }

    private void DrawCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;

        var now = e.GetPosition(DrawCanvas);
        Canvas.SetLeft(PreviewRect, Math.Min(start.X, now.X));
        Canvas.SetTop(PreviewRect, Math.Min(start.Y, now.Y));
        PreviewRect.Width = Math.Abs(now.X - start.X);
        PreviewRect.Height = Math.Abs(now.Y - start.Y);
    }

    private void DrawCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is not { } start) return;

        var end = e.GetPosition(DrawCanvas);
        _dragStart = null;
        DrawCanvas.ReleaseMouseCapture();
        PreviewRect.Visibility = Visibility.Collapsed;

        double width = Math.Abs(end.X - start.X);
        double height = Math.Abs(end.Y - start.Y);
        if (width >= 8 && height >= 8)
            _vm.AddManualRegion(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), width, height);
    }

    // ---------------- click existing boxes ----------------
    private void Region_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RedactionRegion region })
        {
            region.Approved = !region.Approved;
            e.Handled = true;
        }
    }

    private void Region_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RedactionRegion { IsManual: true } region })
        {
            _vm.RemoveRegion(region);
            e.Handled = true;
        }
    }
}
