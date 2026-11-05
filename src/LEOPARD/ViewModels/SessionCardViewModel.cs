using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LEOPARD.Models;

namespace LEOPARD.ViewModels;

public partial class SessionCardViewModel : ObservableObject
{
    private readonly ClubSession _session;
    private readonly Action<int, int> _extend;
    private readonly Action<int> _end;
    private DateTime _now = DateTime.Now;

    public SessionCardViewModel(ClubSession session, Action<int, int> extend, Action<int> end)
    {
        _session = session;
        _extend = extend;
        _end = end;
    }

    public string ComputerLabel => _session.ComputerLabel;
    public string ClientName => _session.ClientName;
    public string Game => _session.Game;
    public string TariffName => _session.TariffName;
    public string TariffRateText => $"{_session.HourlyRate:0} ₴ / год";
    public string StartedAtText => _session.StartedAt.ToString("HH:mm");
    public string DurationText => FormatDuration(_now - _session.StartedAt);
    public string RemainingText
    {
        get
        {
            var remaining = _session.PlannedEnd - _now;
            return remaining <= TimeSpan.Zero
                ? $"Перевищено на {FormatDuration(remaining.Duration())}"
                : $"Залишилось {FormatDuration(remaining)}";
        }
    }

    public string StatusText => _session.PlannedEnd <= _now ? "Час вичерпано" : "Активний";
    public string StatusColor => _session.PlannedEnd <= _now ? "#FFB454" : "#C7F36B";

    public void UpdateTime(DateTime now)
    {
        _now = now;
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusColor));
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var minutes = Math.Max(0, (int)duration.TotalMinutes);
        var hours = minutes / 60;
        var rest = minutes % 60;
        return hours > 0 ? $"{hours} год {rest:00} хв" : $"{rest} хв";
    }

    [RelayCommand]
    private void Extend() => _extend(_session.Id, 30);

    [RelayCommand]
    private void End() => _end(_session.Id);
}
