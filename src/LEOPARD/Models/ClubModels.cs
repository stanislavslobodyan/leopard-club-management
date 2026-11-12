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
    string Status)
{
    public string Note { get; init; } = "";
    public string EndText => StartsAt.AddMinutes(DurationMinutes).ToString("HH:mm");
    public string TimeRangeText => $"{StartsAt:HH:mm}–{EndText}";
    public string DateText => StartsAt.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("uk-UA"));
    public int TimelineColumn => Math.Clamp(StartsAt.Hour - 18, 0, 5);
    public int TimelineColumnSpan => Math.Clamp((DurationMinutes + 59) / 60, 1, 6 - TimelineColumn);
    public string TimelineText => $"{ClientName} · {StartsAt:HH:mm}–{StartsAt.AddMinutes(DurationMinutes):HH:mm}";
}

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
    public string NetworkName { get; init; } = "";
    public string AgentClubServer { get; init; } = "";
    public int AgentPort { get; init; } = 47831;
    public string AgentConnectionKey { get; init; } = "";
    public int AgentPollSeconds { get; init; } = 15;
    public bool IsArchived { get; init; }
    public bool CanEdit => !IsArchived;
    public string ArchiveActionText => IsArchived ? "↶" : "▱";
    public string ArchiveActionTooltip => IsArchived ? "Повернути з архіву" : "Архівувати ПК";
    public string ManagementStatusText => IsArchived ? "В архіві" : Status;
    public string ManagementStatusColor => IsArchived ? "#FFB454" : StatusColor;
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
    public string Email { get; init; } = "";
    public string? BirthDate { get; init; }
    public decimal AccountBalance { get; init; }
    public string Note { get; init; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.MinValue;
    public DateTime? LastVisit { get; init; }
    public bool IsArchived { get; init; }
    public bool CanEdit => !IsArchived;
    public string ArchiveActionText => IsArchived ? "↶" : "▱";
    public string ArchiveActionTooltip => IsArchived ? "Повернути з архіву" : "Архівувати клієнта";
    public string ManagementStatusText => IsArchived ? "В архіві" : StatusText;
    public string ManagementStatusColor => IsArchived ? "#FFB454" : "#9FE2B2";
    public string VisitsText => $"Візитів: {Visits}";
    public string LoyaltyText => $"Бали: {LoyaltyPoints}";
    public string AccountBalanceText => AccountBalance.ToString("N0", CultureInfo.GetCultureInfo("uk-UA")) + " ₴";
    public string LastVisitText => LastVisit?.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("uk-UA")) ?? "Ще без сеансів";
    public string StatusText => Visits > 0 ? "Постійний" : "Новий";
    public string ChoiceText => $"{Name} · {Phone}";
}

public sealed record HistorySessionCard(
    int Id,
    string ComputerLabel,
    string ClientName,
    string Game,
    string TariffName,
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
    public int DurationMinutes { get; init; } = 60;
    public decimal PackagePrice { get; init; }
    public string Availability { get; init; } = "Увесь день · усі комп’ютери";
    public bool IsArchived { get; init; }
    public bool CanEdit => !IsArchived;
    public string ArchiveActionText => IsArchived ? "↶" : "▱";
    public string ArchiveActionTooltip => IsArchived ? "Повернути з архіву" : "Архівувати тариф";
    public string PriceText => (PackagePrice > 0 ? PackagePrice : PricePerHour).ToString("0", CultureInfo.GetCultureInfo("uk-UA"));
    public string DurationText
    {
        get
        {
            if (DurationMinutes % 60 != 0) return $"{DurationMinutes} хв";
            var hours = DurationMinutes / 60;
            var unit = hours switch { 1 => "година", 2 or 3 or 4 => "години", _ => "годин" };
            return $"{hours} {unit}";
        }
    }
    public string HourlyPriceText => PricePerHour.ToString("0.00", CultureInfo.GetCultureInfo("uk-UA"));
    public string ChoiceText => $"{Name} · {DurationText} · {PriceText} ₴";
}

public sealed record DurationChoice(int Minutes, string DisplayText);
