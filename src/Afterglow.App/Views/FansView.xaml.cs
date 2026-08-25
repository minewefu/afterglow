using System.Windows.Controls;
using Afterglow.App.ViewModels;

namespace Afterglow.App.Views;

public partial class FansView : UserControl
{
    public FansView()
    {
        InitializeComponent();
        CurveEditor.CurveEdited += (_, config) =>
        {
            if (DataContext is FansViewModel vm)
            {
                vm.OnCurveEdited(config);
            }
        };
    }
}
