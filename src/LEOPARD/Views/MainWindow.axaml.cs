using Avalonia.Controls;
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
}
