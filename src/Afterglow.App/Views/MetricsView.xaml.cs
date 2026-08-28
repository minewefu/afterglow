using System.Windows.Controls;
using Afterglow.App.ViewModels;
using Afterglow.Core.Metrics;

namespace Afterglow.App.Views;

public partial class MetricsView : UserControl
{
    public MetricsView()
    {
        InitializeComponent();
    }

    private void OnSessionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MetricsViewModel vm && sender is ListBox list)
        {
            vm.OnSessionSelectionChanged(list.SelectedItems.OfType<SessionReport>().ToList());
        }
    }
}
