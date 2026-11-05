using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using LEOPARD.ViewModels;

namespace LEOPARD.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
        Opened += (_, _) => UpdateCompactState();
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateCompactState();

    private void UpdateCompactState()
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.SetCompact(Bounds.Width < 760);
    }

    private void MinimizeWindow_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void ToggleMaximizeWindow_Click(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow_Click(object? sender, RoutedEventArgs e) => Close();

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && ReferenceEquals(e.Source, sender))
            BeginMoveDrag(e);
    }

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not MainViewModel viewModel)
            return;

        viewModel.SelectedPage = "Сеанси";
    }
}
