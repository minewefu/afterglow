using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Afterglow.App.ViewModels;

namespace Afterglow.App.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
    }

    private void OnTileClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is DashboardViewModel vm && sender is FrameworkElement { Tag: string key })
        {
            vm.ToggleExpand(key);
        }
    }
}
