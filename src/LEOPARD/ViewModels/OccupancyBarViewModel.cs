namespace LEOPARD.ViewModels;

public sealed record OccupancyBarViewModel(string HourLabel, int Count, double BarHeight)
{
    public string CountText => Count.ToString();
}
