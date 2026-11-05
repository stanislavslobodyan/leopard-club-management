using System.Globalization;

namespace LEOPARD.Models;

public sealed record ClubSession(
    int Id,
    int ComputerId,
    string ComputerLabel,
    string ClientName,
    string Game,
    string TariffName,
    DateTime StartedAt,
    DateTime PlannedEnd,
    decimal HourlyRate);

public sealed record ReservationCard(
    int Id,
    string ComputerLabel,
    string ClientName,
    DateTime StartsAt,
    int DurationMinutes,
    string StartText,
    string DurationText,
    string Status);

public sealed record ComputerCard(
    int Id,
    string ComputerLabel,
    string Zone,
    string Status,
    string Details,
    string Processor,
    string GraphicsCard,
    string Memory,
    string Monitor,
    string Storage,
    string AgentEndpoint,
    string AgentStatus)
{
    public string StatusColor => Status switch
    {
        "Активний" => "#C7F36B",
        "Заброньований" => "#72B8FF",
        _ => "#9FE2B2"
    };

    public string ChoiceText => $"{ComputerLabel} · {Zone} · {Processor}";
    public string AgentConnectionText => string.IsNullOrWhiteSpace(AgentEndpoint)
        ? "Агент не налаштовано"
        : AgentStatus switch
        {
            "Online" => "Агент на зв’язку",
            "Offline" => "Агент недоступний",
            _ => "Очікує перевірки"
        };
    public string AgentConnectionColor => AgentStatus switch
    {
        "Online" => "#9FE2B2",
        "Offline" => "#FFB454",
        _ => "#929BA8"
    };
}

public sealed record ClientCard(
    int Id,
    string Name,
    string Phone,
    int Visits,
    string FavoriteGame,
    int LoyaltyPoints)
{
    public string VisitsText => $"Візитів: {Visits}";
    public string LoyaltyText => $"Бали: {LoyaltyPoints}";
    public string ChoiceText => $"{Name} · {Phone}";
}

public sealed record HistorySessionCard(
    int Id,
    string ComputerLabel,
    string ClientName,
    string Game,
    DateTime StartedAt,
    DateTime EndedAt,
    int DurationMinutes,
    decimal AccruedAmount,
    int PointsAwarded)
{
    public string PeriodText => $"{StartedAt:dd.MM.yyyy HH:mm} — {EndedAt:HH:mm}";
    public string DurationText => $"{DurationMinutes / 60} год {DurationMinutes % 60:00} хв";
    public string AmountText => $"{AccruedAmount.ToString("0.00", CultureInfo.GetCultureInfo("uk-UA"))} ₴";
    public string PointsText => $"+{PointsAwarded} балів";
}

public sealed record ClubNotification(
    int Id,
    string Title,
    string Message,
    DateTime CreatedAt,
    bool IsRead)
{
    public string CreatedText => CreatedAt.ToString("dd.MM HH:mm");
    public string ReadText => IsRead ? "Переглянуто" : "Нове";
    public bool CanMarkRead => !IsRead;
}

public sealed record TariffCard(
    int Id,
    string Name,
    string Description,
    decimal PricePerHour,
    string Badge)
{
    public string PriceText => PricePerHour.ToString("0");
    public string ChoiceText => $"{Name} · {PriceText} ₴/год";
}

public sealed record DurationChoice(int Minutes, string DisplayText);
