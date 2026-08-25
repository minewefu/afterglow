using System.Windows.Controls;
using Afterglow.App.ViewModels;

namespace Afterglow.App.Views;

public partial class VfCurveView : UserControl
{
    public VfCurveView()
    {
        InitializeComponent();
        Chart.TargetPicked += (_, point) =>
        {
            if (DataContext is VfCurveViewModel vm)
            {
                vm.OnTargetPicked(point.VoltageMv, point.ClockMHz);
            }
        };
    }
}
