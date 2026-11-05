namespace LEOPARD.ViewModels;

public sealed record OccupancyBarViewModel(string HourLabel, int Count, double BarHeight, string BarColor)
{
    public string CountText => Count.ToString();
}
