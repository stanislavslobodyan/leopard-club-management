using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LEOPARD.Models;
using LEOPARD.Services;
using System.Collections.ObjectModel;
using System.Globalization;

namespace LEOPARD.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly ClubRepository _repository = new();
    private readonly ComputerAgentClient _agentClient = new();
    private readonly DispatcherTimer _clock;
    private IReadOnlyList<ClubSession> _sessionCache = [];
    private bool _returnToStartAfterClient;
    private readonly HashSet<(int SessionId, string Kind)> _checkedAlerts = [];
    private readonly Dictionary<int, DateTime> _agentLastPolls = [];
    private DateTime _accrualDate = DateTime.Today;
    private decimal _todayAccruedAmount;
    private int _visitsThisMonth;
    private bool _agentPollRunning;

    public MainViewModel()
    {
        FilterOptions = new ObservableCollection<string> { "Усі", "Активні", "Менше 15 хв", "Потрібна увага" };
        Refresh();
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) =>
        {
            var now = DateTime.Now;
            if (DateTime.Today != _accrualDate)
                RefreshAccruedAmount();
            foreach (var session in ActiveSessions)
                session.UpdateTime(now);
            CheckSessionAlerts(now);
            if (Computers.Any(c => !string.IsNullOrWhiteSpace(c.AgentEndpoint) &&
                now - _agentLastPolls.GetValueOrDefault(c.Id, DateTime.MinValue) >= TimeSpan.FromSeconds(Math.Clamp(c.AgentPollSeconds, 5, 300))))
            {
                _ = RefreshAgentStatusesAsync();
            }
            if (SelectedFilter is "Менше 15 хв" or "Потрібна увага")
                ApplySessionFilter();
            OnPropertyChanged(nameof(CurrentTimeText));
            OnPropertyChanged(nameof(CurrentDateText));
            OnPropertyChanged(nameof(ActiveAccruedAmount));
            OnPropertyChanged(nameof(ActiveRunningCount));
            OnPropertyChanged(nameof(SessionsUnder15Count));
            OnPropertyChanged(nameof(OverdueSessionCount));
            OnPropertyChanged(nameof(HasUrgentSessions));
            OnPropertyChanged(nameof(SessionAttentionText));
            OnPropertyChanged(nameof(SessionStartTimeText));
            OnPropertyChanged(nameof(SessionEndTimeText));
            RefreshHourlyOccupancy();
        };
        _clock.Start();
    }

    public ObservableCollection<SessionCardViewModel> ActiveSessions { get; } = [];
    public ObservableCollection<ComputerCard> Computers { get; } = [];
    public ObservableCollection<ComputerCard> ComputerRecords { get; } = [];
    public ObservableCollection<ComputerCard> VisibleComputers { get; } = [];
    public ObservableCollection<ComputerCard> AvailableComputers { get; } = [];
    public ObservableCollection<ReservationCard> Reservations { get; } = [];
    public ObservableCollection<ClientCard> Clients { get; } = [];
    public ObservableCollection<ClientCard> ClientRecords { get; } = [];
    public ObservableCollection<ClientCard> VisibleClients { get; } = [];
    public ObservableCollection<TariffCard> Tariffs { get; } = [];
    public ObservableCollection<TariffCard> TariffRecords { get; } = [];
    public ObservableCollection<TariffCard> VisibleTariffs { get; } = [];
    public ObservableCollection<HistorySessionCard> HistorySessions { get; } = [];
    public ObservableCollection<ClubNotification> Notifications { get; } = [];
    public ObservableCollection<OccupancyBarViewModel> HourlyOccupancy { get; } = [];
    public ObservableCollection<string> FilterOptions { get; }
    public ObservableCollection<string> GameOptions { get; } =
        ["Counter-Strike 2", "Valorant", "Dota 2", "Apex Legends", "Fortnite", "Інша гра"];
    public ObservableCollection<DurationChoice> DurationOptions { get; } =
        [new(15, "15 хвилин"), new(30, "30 хвилин"), new(60, "1 година"), new(90, "1 год 30 хв"),
         new(120, "2 години"), new(180, "3 години")];
    public ObservableCollection<string> TariffAvailabilityOptions { get; } =
        ["Увесь день · усі комп’ютери", "Денний час · усі комп’ютери", "Увесь день · основна зала"];
    public ObservableCollection<string> ZoneOptions { get; } = ["Стандарт · Ряд А", "Стандарт · Ряд B", "VIP"];

    public string CurrentTimeText => DateTime.Now.ToString("HH:mm");
    public string CurrentDateText => DateTime.Now.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("uk-UA"));
    public string CurrentClubText => $"Київ · Поділ · {DateTime.Now.ToString("d MMMM", CultureInfo.GetCultureInfo("uk-UA"))}";
    public int ActiveCount => _sessionCache.Count;
    public int ActiveRunningCount => _sessionCache.Count(s => s.PlannedEnd > DateTime.Now);
    public int SessionsUnder15Count => _sessionCache.Count(s =>
        s.PlannedEnd > DateTime.Now && s.PlannedEnd - DateTime.Now <= TimeSpan.FromMinutes(15));
    public int OverdueSessionCount => _sessionCache.Count(s => s.PlannedEnd <= DateTime.Now);
    public int FreeCount => Computers.Count(c => c.Status == "Вільний");
    public int ReservedCount => Computers.Count(c => c.Status == "Заброньований");
    public int OfflineCount => Computers.Count(c => c.AgentStatus == "Offline");
    public GridLength ActiveSegmentWidth => new(ActiveCount, GridUnitType.Star);
    public GridLength FreeSegmentWidth => new(FreeCount, GridUnitType.Star);
    public GridLength ReservedSegmentWidth => new(ReservedCount, GridUnitType.Star);
    public GridLength OfflineSegmentWidth => new(OfflineCount, GridUnitType.Star);
    public int ComputerCount => Computers.Count;
    public int ResponsiveCardColumns => IsCompact ? 1 : 4;
    public int ResponsiveMetricColumns => IsCompact ? 2 : 4;
    public bool IsMobileStartAction => IsCompact && (IsDashboard || IsSessions);
    public bool IsMobileBookingAction => IsCompact && IsBookings;
    public bool IsMobileComputerAction => IsCompact && IsComputers;
    public bool IsMobileClientAction => IsCompact && IsClients;
    public bool IsMobileTariffAction => IsCompact && IsTariffs;
    public string ActiveComputerRatio => $"{ActiveCount} / {ComputerCount}";
    public double OccupancyPercent => ComputerCount == 0 ? 0 : Math.Round(ActiveCount * 100d / ComputerCount);
    public string OccupancyText => $"{OccupancyPercent:0}% завантаженості";
    public string ActiveAccruedAmount => _sessionCache.Sum(s => s.HourlyRate * (decimal)Math.Max(0, (DateTime.Now - s.StartedAt).TotalHours))
        .ToString("0.00", CultureInfo.GetCultureInfo("uk-UA")) + " ₴";
    public int ActiveClientCount => _sessionCache.Select(s => s.ClientName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    public int TotalVisits => Clients.Sum(c => c.Visits);
    public int NewClientsThisMonth => Clients.Count(c => c.CreatedAt.Year == DateTime.Today.Year && c.CreatedAt.Month == DateTime.Today.Month);
    public int VisitsThisMonth => _visitsThisMonth;
    public string TotalClientBalance => Clients.Sum(c => c.AccountBalance).ToString("N0", CultureInfo.GetCultureInfo("uk-UA")) + " ₴";
    public int TotalLoyaltyPoints => Clients.Sum(c => c.LoyaltyPoints);
    public int HistoryTodayCount => HistorySessions.Count(s => s.EndedAt.Date == DateTime.Today);
    public string HistoryHoursToday => FormatHours(HistorySessions.Where(s => s.EndedAt.Date == DateTime.Today).Sum(s => s.DurationMinutes)) + " год";
    public ReservationCard? NextReservation => Reservations.Where(r => r.StartsAt >= DateTime.Now).OrderBy(r => r.StartsAt).FirstOrDefault();
    public IReadOnlyList<ReservationCard> ReservationsForSelectedDay => Reservations.Where(r => r.StartsAt.Date == SelectedBookingDate.Date).OrderBy(r => r.StartsAt).ToList();
    public string SelectedBookingDateText => SelectedBookingDate.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("uk-UA"));
    public int SelectedDayReservationCount => ReservationsForSelectedDay.Count;
    public bool HasNoReservationsForSelectedDay => ReservationsForSelectedDay.Count == 0;
    public string ReservationEndTimeText => TimeSpan.TryParseExact(NewReservationTime.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var start) && SelectedReservationDuration is not null
        ? start.Add(TimeSpan.FromMinutes(SelectedReservationDuration.Minutes)).ToString(@"hh\:mm") : "—";
    public string SessionStartTimeText => DateTime.Now.ToString("HH:mm");
    public string SessionEndTimeText => SelectedDuration is null ? "—" : DateTime.Now.AddMinutes(SelectedDuration.Minutes).ToString("HH:mm");
    public string TariffPackageDescription => $"{NewTariffPrice} ₴ за {NewTariffDurationMinutes} хв · нарахування за використаний час";
    public string TariffHourlyEquivalentText
    {
        get
        {
            if (!int.TryParse(NewTariffDurationMinutes, out var minutes) || minutes <= 0 ||
                !decimal.TryParse(NewTariffPrice.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var price) || price <= 0)
                return "Вкажіть тривалість і вартість";
            return $"{(price * 60m / minutes).ToString("0.00", CultureInfo.GetCultureInfo("uk-UA"))} ₴";
        }
    }
    public bool HasUpcomingReservation => NextReservation is not null;
    public bool HasNoUpcomingReservation => NextReservation is null;
    public bool HasUrgentSessions => _sessionCache.Any(s => s.PlannedEnd - DateTime.Now <= TimeSpan.FromMinutes(30));
    public string SessionAttentionText
    {
        get
        {
            var urgent = _sessionCache.Where(s => s.PlannedEnd - DateTime.Now <= TimeSpan.FromMinutes(30)).ToArray();
            if (urgent.Length == 0) return "";
            var first = urgent[0];
            var remaining = first.PlannedEnd - DateTime.Now;
            return remaining <= TimeSpan.Zero
                ? $"{first.ComputerLabel} · {first.ClientName}: запланований час завершився."
                : $"{first.ComputerLabel} · {first.ClientName}: до завершення залишилося близько {Math.Ceiling(remaining.TotalMinutes)} хв.";
        }
    }
    public int OnlineAgentCount => Computers.Count(c => c.AgentStatus == "Online");
    public int ConfiguredAgentCount => Computers.Count(c => !string.IsNullOrWhiteSpace(c.AgentEndpoint));
    public string AgentSummaryText => ConfiguredAgentCount == 0
        ? "PC-агенти не налаштовані"
        : $"{OnlineAgentCount} / {ConfiguredAgentCount} агентів онлайн";
    public string AgentStatusColor => AgentStatusText == "Підключено" ? "#9FE2B2" : "#FFB454";
    public string AgentDetailText => ConfiguredAgentCount == 0
        ? "Стан ПК доступний після налаштування агента"
        : $"Перевірено ПК: {ComputerCount}";
    public int UnreadNotificationCount => Notifications.Count(n => !n.IsRead);
    public bool HasNotifications => Notifications.Count > 0;
    public bool HasHistory => HistorySessions.Count > 0;
    public bool HasNoHistory => HistorySessions.Count == 0;
    public string TodayAccruedAmount => _todayAccruedAmount.ToString("0.00", CultureInfo.GetCultureInfo("uk-UA")) + " ₴";
    public bool HasNoSessions => ActiveSessions.Count == 0;
    public bool HasVisibleSessions => ActiveSessions.Count > 0;
    public bool ShowWideSessionTable => IsWide && HasVisibleSessions;
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool HasNoClients => VisibleClients.Count == 0;
    public bool HasNoComputers => VisibleComputers.Count == 0;
    public bool HasNoTariffs => VisibleTariffs.Count == 0;
    public string ComputerArchiveToggleText => ShowArchivedComputers ? "Активні ПК" : $"Архів ПК · {ComputerRecords.Count(c => c.IsArchived)}";
    public string ClientArchiveToggleText => ShowArchivedClients ? "Активні клієнти" : $"Архів · {ClientRecords.Count(c => c.IsArchived)}";
    public string TariffArchiveToggleText => ShowArchivedTariffs ? "Активні тарифи" : $"Архів · {TariffRecords.Count(t => t.IsArchived)}";
    public string ComputerEmptyStateText => ShowArchivedComputers ? "Архів робочих місць порожній." : "У базі ще немає робочих місць. Додайте ПК та внесіть їхні характеристики.";
    public string ClientEmptyStateText => ShowArchivedClients ? "Архів клієнтів порожній." : "Клієнтів за цим запитом не знайдено.";
    public string TariffEmptyStateText => ShowArchivedTariffs ? "Архів тарифів порожній." : "Активних тарифів поки немає.";
    public bool HasNoReservations => Reservations.Count == 0;
    public bool HasClientFormError => !string.IsNullOrWhiteSpace(NewClientErrorMessage);
    public bool CanSaveClient
    {
        get
        {
            var digits = new string(NewClientPhone.Where(char.IsDigit).ToArray());
            var balanceValid = string.IsNullOrWhiteSpace(NewClientBalance) ||
                decimal.TryParse(NewClientBalance.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var balance) && balance >= 0;
            var emailValid = string.IsNullOrWhiteSpace(NewClientEmail) || NewClientEmail.Contains('@', StringComparison.Ordinal);
            return NewClientName.Trim().Length >= 2 && digits.Length is >= 7 and <= 15 && balanceValid && emailValid;
        }
    }
    public bool CanConfirmStart => SelectedComputer is not null && SelectedClient is not null &&
                                   SelectedTariff is not null && SelectedDuration is not null &&
                                   !string.IsNullOrWhiteSpace(SelectedGame);
    public string SelectedComputerSpecText => SelectedComputer is null
        ? "Оберіть комп’ютер, щоб переглянути характеристики."
        : $"{SelectedComputer.Processor}  ·  {SelectedComputer.GraphicsCard}  ·  {SelectedComputer.Memory}  ·  {SelectedComputer.Monitor}  ·  {SelectedComputer.Storage}";
    public string EstimatedCostText => SelectedTariff is null || SelectedDuration is null
        ? "Оберіть тариф і тривалість"
        : $"Орієнтовно {(SelectedTariff.PricePerHour * SelectedDuration.Minutes / 60m).ToString("0.00", CultureInfo.GetCultureInfo("uk-UA"))} ₴";
    public GridLength SidebarColumnWidth => IsCompact ? new GridLength(0) : new GridLength(232);
    public Thickness ContentMargin => IsCompact
        ? new Thickness(14, 16, 14, 24)
        : new Thickness(26, 22, 26, 32);
    public string PageTitle => SelectedPage switch
    {
        "Бронювання" => "Бронювання",
        "Комп’ютери" => "Комп’ютери",
        "Клієнти" => "Клієнти",
        "Тарифи" => "Тарифи",
        "Історія" => "Історія",
        "Сеанси" => "Активні сеанси",
        "Налаштування" => "Налаштування",
        _ => "Огляд"
    };

    public string PageSubtitle => SelectedPage switch
    {
        "Бронювання" => "Розклад залу та майбутні слоти клубу.",
        "Комп’ютери" => "Статус робочих місць і конфігурації обладнання.",
        "Клієнти" => "База клієнтів та історія відвідувань.",
        "Тарифи" => "Правила вартості та погодинні пропозиції клубу.",
        "Історія" => "Завершені сеанси та нарахування за використаний час.",
        "Сеанси" => "Керуйте активними сеансами та контролюйте залишок часу.",
        "Налаштування" => "Локальне збереження та підключення робочих місць.",
        _ => "Усе, що відбувається у клубі зараз."
    };

    public bool IsDashboard => SelectedPage == "Огляд";
    public bool IsDashboardWide => IsDashboard && IsWide;
    public bool IsDashboardCompact => IsDashboard && IsCompact;
    public bool IsSessions => SelectedPage == "Сеанси";
    public bool IsBookings => SelectedPage == "Бронювання";
    public bool IsComputers => SelectedPage == "Комп’ютери";
    public bool IsClients => SelectedPage == "Клієнти";
    public bool IsTariffs => SelectedPage == "Тарифи";
    public bool IsHistory => SelectedPage == "Історія";
    public bool IsSettings => SelectedPage == "Налаштування";
    public bool IsAllSessionFilter => SelectedFilter == "Усі";
    public bool IsActiveSessionFilter => SelectedFilter == "Активні";
    public bool IsExpiringSessionFilter => SelectedFilter == "Менше 15 хв";
    public bool IsOverdueSessionFilter => SelectedFilter == "Потрібна увага";
    public bool IsWide => !IsCompact;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageTitle))]
    [NotifyPropertyChangedFor(nameof(IsDashboard))]
    [NotifyPropertyChangedFor(nameof(IsDashboardWide))]
    [NotifyPropertyChangedFor(nameof(IsDashboardCompact))]
    [NotifyPropertyChangedFor(nameof(IsSessions))]
    [NotifyPropertyChangedFor(nameof(IsBookings))]
    [NotifyPropertyChangedFor(nameof(IsComputers))]
    [NotifyPropertyChangedFor(nameof(IsClients))]
    [NotifyPropertyChangedFor(nameof(IsTariffs))]
    [NotifyPropertyChangedFor(nameof(IsHistory))]
    [NotifyPropertyChangedFor(nameof(IsSettings))]
    [NotifyPropertyChangedFor(nameof(IsMobileStartAction))]
    [NotifyPropertyChangedFor(nameof(IsMobileBookingAction))]
    [NotifyPropertyChangedFor(nameof(IsMobileComputerAction))]
    [NotifyPropertyChangedFor(nameof(IsMobileClientAction))]
    [NotifyPropertyChangedFor(nameof(IsMobileTariffAction))]
    [NotifyPropertyChangedFor(nameof(PageSubtitle))]
    private string selectedPage = "Огляд";

    [ObservableProperty]
    private string filterText = "";

    [ObservableProperty]
    private string computerFilterText = "";

    [ObservableProperty]
    private bool isNotificationPanelOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllSessionFilter))]
    [NotifyPropertyChangedFor(nameof(IsActiveSessionFilter))]
    [NotifyPropertyChangedFor(nameof(IsExpiringSessionFilter))]
    [NotifyPropertyChangedFor(nameof(IsOverdueSessionFilter))]
    private string selectedFilter = "Усі";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWide))]
    [NotifyPropertyChangedFor(nameof(SidebarColumnWidth))]
    [NotifyPropertyChangedFor(nameof(ContentMargin))]
    [NotifyPropertyChangedFor(nameof(IsDashboardWide))]
    [NotifyPropertyChangedFor(nameof(IsDashboardCompact))]
    [NotifyPropertyChangedFor(nameof(ResponsiveCardColumns))]
    [NotifyPropertyChangedFor(nameof(ResponsiveMetricColumns))]
    [NotifyPropertyChangedFor(nameof(IsMobileStartAction))]
    [NotifyPropertyChangedFor(nameof(IsMobileBookingAction))]
    [NotifyPropertyChangedFor(nameof(IsMobileComputerAction))]
    [NotifyPropertyChangedFor(nameof(IsMobileClientAction))]
    [NotifyPropertyChangedFor(nameof(IsMobileTariffAction))]
    [NotifyPropertyChangedFor(nameof(ShowWideSessionTable))]
    private bool isCompact;

    [ObservableProperty]
    private string statusMessage = "";

    [ObservableProperty]
    private bool isStartDialogOpen;

    [ObservableProperty]
    private ComputerCard? selectedComputer;

    [ObservableProperty]
    private ClientCard? selectedClient;

    [ObservableProperty]
    private TariffCard? selectedTariff;

    [ObservableProperty]
    private DurationChoice? selectedDuration;

    [ObservableProperty]
    private string selectedGame = "Counter-Strike 2";

    [ObservableProperty]
    private bool isNewClientDialogOpen;

    [ObservableProperty]
    private string newClientName = "";

    [ObservableProperty]
    private string newClientPhone = "";

    [ObservableProperty]
    private string newClientEmail = "";

    [ObservableProperty]
    private DateTimeOffset? newClientBirthDate;

    [ObservableProperty]
    private string newClientBalance = "0";

    [ObservableProperty]
    private string newClientNote = "";

    [ObservableProperty]
    private string newClientFavoriteGame = "Counter-Strike 2";

    [ObservableProperty]
    private string newClientErrorMessage = "";

    [ObservableProperty]
    private string clientFilterText = "";

    [ObservableProperty] private bool showArchivedComputers;
    [ObservableProperty] private bool showArchivedClients;
    [ObservableProperty] private bool showArchivedTariffs;
    [ObservableProperty] private int? editingComputerId;
    [ObservableProperty] private int? editingClientId;
    [ObservableProperty] private int? editingTariffId;

    [ObservableProperty] private bool isComputerDialogOpen;
    [ObservableProperty] private string newComputerLabel = "";
    [ObservableProperty] private string newComputerZone = "Стандарт · Ряд А";
    [ObservableProperty] private string newComputerNetworkName = "";
    [ObservableProperty] private string newComputerProcessor = "";
    [ObservableProperty] private string newComputerGraphicsCard = "";
    [ObservableProperty] private string newComputerMemory = "";
    [ObservableProperty] private string newComputerMonitor = "";
    [ObservableProperty] private string newComputerStorage = "";
    [ObservableProperty] private string newComputerAgentEndpoint = "";
    [ObservableProperty] private string computerFormError = "";

    [ObservableProperty] private bool isAgentDialogOpen;
    [ObservableProperty] private ComputerCard? selectedAgentComputer;
    [ObservableProperty] private string editAgentAddress = "";
    [ObservableProperty] private string editAgentPort = "47831";
    [ObservableProperty] private string editAgentClubServer = "";
    [ObservableProperty] private string editAgentConnectionKey = "";
    [ObservableProperty] private string editAgentPollSeconds = "15";
    [ObservableProperty] private string agentStatusText = "Не підключено";
    [ObservableProperty] private string agentLastCheckText = "Поки не перевірено";

    [ObservableProperty] private bool isTariffDialogOpen;
    [ObservableProperty] private string newTariffName = "";
    [ObservableProperty] private string newTariffDescription = "";
    [ObservableProperty] private string newTariffPrice = "";
    [ObservableProperty] private string newTariffDurationMinutes = "60";
    [ObservableProperty] private string newTariffAvailability = "Увесь день · усі комп’ютери";
    [ObservableProperty] private string newTariffBadge = "";
    [ObservableProperty] private string tariffFormError = "";

    [ObservableProperty] private bool isReservationDialogOpen;
    [ObservableProperty] private ComputerCard? selectedReservationComputer;
    [ObservableProperty] private ClientCard? selectedReservationClient;
    [ObservableProperty] private DateTimeOffset? newReservationDate = DateTimeOffset.Now.AddDays(1);
    [ObservableProperty] private string newReservationTime = "18:00";
    [ObservableProperty] private string newReservationNote = "";
    [ObservableProperty] private int? editingReservationId;
    [ObservableProperty] private DurationChoice? selectedReservationDuration;
    [ObservableProperty] private DateTime selectedBookingDate = DateTime.Today;
    [ObservableProperty] private string reservationFormError = "";

    partial void OnFilterTextChanged(string value) => ApplySessionFilter();
    partial void OnSelectedFilterChanged(string value) => ApplySessionFilter();
    partial void OnComputerFilterTextChanged(string value) => ApplyComputerFilter();
    partial void OnSelectedComputerChanged(ComputerCard? value)
    {
        OnPropertyChanged(nameof(CanConfirmStart));
        OnPropertyChanged(nameof(SelectedComputerSpecText));
    }
    partial void OnSelectedClientChanged(ClientCard? value)
    {
        if (value is not null && !string.IsNullOrWhiteSpace(value.FavoriteGame))
            SelectedGame = value.FavoriteGame;
        OnPropertyChanged(nameof(CanConfirmStart));
    }
    partial void OnSelectedTariffChanged(TariffCard? value)
    {
        OnPropertyChanged(nameof(CanConfirmStart));
        OnPropertyChanged(nameof(EstimatedCostText));
    }
    partial void OnSelectedDurationChanged(DurationChoice? value)
    {
        OnPropertyChanged(nameof(CanConfirmStart));
        OnPropertyChanged(nameof(EstimatedCostText));
        OnPropertyChanged(nameof(SessionEndTimeText));
    }
    partial void OnSelectedGameChanged(string value) => OnPropertyChanged(nameof(CanConfirmStart));
    partial void OnNewClientNameChanged(string value) => OnClientFormValueChanged();
    partial void OnNewClientPhoneChanged(string value) => OnClientFormValueChanged();
    partial void OnNewClientFavoriteGameChanged(string value) => OnClientFormValueChanged();
    partial void OnNewClientErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasClientFormError));
    partial void OnClientFilterTextChanged(string value) => ApplyClientFilter();
    partial void OnShowArchivedComputersChanged(bool value) => ApplyComputerFilter();
    partial void OnShowArchivedClientsChanged(bool value) => ApplyClientFilter();
    partial void OnShowArchivedTariffsChanged(bool value) => ApplyTariffFilter();
    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatusMessage));
    partial void OnEditingComputerIdChanged(int? value)
    {
        OnPropertyChanged(nameof(ComputerDialogTitle));
        OnPropertyChanged(nameof(ComputerDialogAction));
    }
    partial void OnEditingClientIdChanged(int? value)
    {
        OnPropertyChanged(nameof(ClientDialogTitle));
        OnPropertyChanged(nameof(ClientDialogAction));
    }
    partial void OnEditingTariffIdChanged(int? value)
    {
        OnPropertyChanged(nameof(TariffDialogTitle));
        OnPropertyChanged(nameof(TariffDialogAction));
    }
    partial void OnNewComputerLabelChanged(string value) => ComputerFormError = "";
    partial void OnNewComputerZoneChanged(string value) => ComputerFormError = "";
    partial void OnNewTariffNameChanged(string value) => TariffFormError = "";
    partial void OnNewTariffPriceChanged(string value) { TariffFormError = ""; OnPropertyChanged(nameof(TariffHourlyEquivalentText)); OnPropertyChanged(nameof(TariffPackageDescription)); }
    partial void OnNewTariffDurationMinutesChanged(string value) { TariffFormError = ""; OnPropertyChanged(nameof(TariffHourlyEquivalentText)); OnPropertyChanged(nameof(TariffPackageDescription)); }
    partial void OnSelectedReservationDurationChanged(DurationChoice? value) => OnReservationFormChanged();
    partial void OnNewReservationTimeChanged(string value) => OnReservationFormChanged();
    partial void OnNewReservationDateChanged(DateTimeOffset? value) => OnReservationFormChanged();
    partial void OnSelectedReservationComputerChanged(ComputerCard? value) => OnReservationFormChanged();
    partial void OnSelectedReservationClientChanged(ClientCard? value) => OnReservationFormChanged();
    partial void OnEditingReservationIdChanged(int? value)
    {
        OnPropertyChanged(nameof(ReservationDialogTitle));
        OnPropertyChanged(nameof(ReservationDialogAction));
    }
    partial void OnSelectedBookingDateChanged(DateTime value)
    {
        OnPropertyChanged(nameof(SelectedBookingDateText));
        OnPropertyChanged(nameof(ReservationsForSelectedDay));
        OnPropertyChanged(nameof(SelectedDayReservationCount));
        OnPropertyChanged(nameof(HasNoReservationsForSelectedDay));
    }
    partial void OnNewClientEmailChanged(string value) => OnClientFormValueChanged();
    partial void OnNewClientBalanceChanged(string value) => OnClientFormValueChanged();
    partial void OnAgentStatusTextChanged(string value) => OnPropertyChanged(nameof(AgentStatusColor));

    public string ComputerDialogTitle => EditingComputerId is null ? "Додати ПК" : "Редагувати ПК";
    public string ComputerDialogAction => EditingComputerId is null ? "Додати ПК" : "Зберегти зміни";
    public string ClientDialogTitle => EditingClientId is null ? "Додати клієнта" : "Редагувати клієнта";
    public string ClientDialogAction => EditingClientId is null ? "Додати клієнта" : "Зберегти зміни";
    public string TariffDialogTitle => EditingTariffId is null ? "Додати тариф" : "Редагувати тариф";
    public string TariffDialogAction => EditingTariffId is null ? "Зберегти тариф" : "Зберегти зміни";
    private void OnReservationFormChanged()
    {
        OnPropertyChanged(nameof(ReservationEndTimeText));
        OnPropertyChanged(nameof(CanSaveReservation));
        OnPropertyChanged(nameof(ReservationAvailabilityText));
    }

    public bool CanSaveReservation => SelectedReservationComputer is not null && SelectedReservationClient is not null &&
        NewReservationDate is not null && SelectedReservationDuration is not null &&
        TimeSpan.TryParseExact(NewReservationTime.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out _);
    public string ReservationDialogTitle => EditingReservationId is null ? "Нове бронювання" : "Змінити бронювання";
    public string ReservationDialogAction => EditingReservationId is null ? "Створити" : "Зберегти зміни";
    public string ReservationAvailabilityText => SelectedReservationComputer is null || NewReservationDate is null
        ? "Оберіть ПК, дату та час бронювання."
        : $"{SelectedReservationComputer.ComputerLabel} · слот {NewReservationTime}–{ReservationEndTimeText}. Бронювання не запускає сеанс і не створює нарахування.";

    private void OnClientFormValueChanged()
    {
        NewClientErrorMessage = "";
        OnPropertyChanged(nameof(CanSaveClient));
    }

    public void SetCompact(bool compact)
    {
        if (IsCompact != compact)
            IsCompact = compact;
    }

    private void Refresh()
    {
        ComputerRecords.ReplaceWith(_repository.GetComputers(includeArchived: true));
        Computers.ReplaceWith(ComputerRecords.Where(c => !c.IsArchived));
        AvailableComputers.ReplaceWith(Computers.Where(c => c.Status == "Вільний"));
        Reservations.ReplaceWith(_repository.GetReservations());
        OnPropertyChanged(nameof(ReservationsForSelectedDay));
        OnPropertyChanged(nameof(SelectedDayReservationCount));
        OnPropertyChanged(nameof(HasNoReservationsForSelectedDay));
        OnPropertyChanged(nameof(NextReservation));
        OnPropertyChanged(nameof(HasUpcomingReservation));
        OnPropertyChanged(nameof(HasNoUpcomingReservation));
        ClientRecords.ReplaceWith(_repository.GetClients(includeArchived: true));
        Clients.ReplaceWith(ClientRecords.Where(c => !c.IsArchived));
        _visitsThisMonth = _repository.GetVisitsForMonth(DateTime.Today);
        ApplyClientFilter();
        TariffRecords.ReplaceWith(_repository.GetTariffs(includeArchived: true));
        Tariffs.ReplaceWith(TariffRecords.Where(t => !t.IsArchived));
        ApplyTariffFilter();
        _sessionCache = _repository.GetActiveSessions();
        HistorySessions.ReplaceWith(_repository.GetCompletedSessions());
        RefreshAccruedAmount();
        Notifications.ReplaceWith(_repository.GetNotifications());
        ApplyComputerFilter();
        ApplySessionFilter();
        RefreshHourlyOccupancy();
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(FreeCount));
        OnPropertyChanged(nameof(ReservedCount));
        OnPropertyChanged(nameof(OfflineCount));
        OnPropertyChanged(nameof(ActiveSegmentWidth));
        OnPropertyChanged(nameof(FreeSegmentWidth));
        OnPropertyChanged(nameof(ReservedSegmentWidth));
        OnPropertyChanged(nameof(OfflineSegmentWidth));
        OnPropertyChanged(nameof(ComputerCount));
        OnPropertyChanged(nameof(HasNoComputers));
        OnPropertyChanged(nameof(ComputerArchiveToggleText));
        OnPropertyChanged(nameof(ClientArchiveToggleText));
        OnPropertyChanged(nameof(TariffArchiveToggleText));
        OnPropertyChanged(nameof(ComputerEmptyStateText));
        OnPropertyChanged(nameof(ClientEmptyStateText));
        OnPropertyChanged(nameof(TariffEmptyStateText));
        OnPropertyChanged(nameof(HasNoTariffs));
        OnPropertyChanged(nameof(HasNoReservations));
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HasNoHistory));
        OnPropertyChanged(nameof(UnreadNotificationCount));
        OnPropertyChanged(nameof(HasNotifications));
        OnPropertyChanged(nameof(ActiveComputerRatio));
        OnPropertyChanged(nameof(OccupancyPercent));
        OnPropertyChanged(nameof(OccupancyText));
        OnPropertyChanged(nameof(ActiveAccruedAmount));
        OnPropertyChanged(nameof(ActiveClientCount));
        OnPropertyChanged(nameof(TotalVisits));
        OnPropertyChanged(nameof(NewClientsThisMonth));
        OnPropertyChanged(nameof(VisitsThisMonth));
        OnPropertyChanged(nameof(TotalClientBalance));
        OnPropertyChanged(nameof(TotalLoyaltyPoints));
        OnPropertyChanged(nameof(HistoryTodayCount));
        OnPropertyChanged(nameof(HistoryHoursToday));
        OnPropertyChanged(nameof(NextReservation));
        OnPropertyChanged(nameof(HasUpcomingReservation));
        OnPropertyChanged(nameof(HasNoUpcomingReservation));
        OnPropertyChanged(nameof(HasUrgentSessions));
        OnPropertyChanged(nameof(SessionAttentionText));
        OnPropertyChanged(nameof(OnlineAgentCount));
        OnPropertyChanged(nameof(ConfiguredAgentCount));
        OnPropertyChanged(nameof(AgentSummaryText));
        OnPropertyChanged(nameof(AgentDetailText));
    }

    private void RefreshAccruedAmount()
    {
        _accrualDate = DateTime.Today;
        _todayAccruedAmount = _repository.GetAccruedAmountForDate(_accrualDate);
        OnPropertyChanged(nameof(TodayAccruedAmount));
    }

    private void ApplyClientFilter()
    {
        var query = ClientFilterText.Trim();
        VisibleClients.ReplaceWith(ClientRecords.Where(client => client.IsArchived == ShowArchivedClients &&
            (string.IsNullOrWhiteSpace(query) ||
             client.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             client.Phone.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             client.Email.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             client.FavoriteGame.Contains(query, StringComparison.OrdinalIgnoreCase))));
        OnPropertyChanged(nameof(HasNoClients));
        OnPropertyChanged(nameof(ClientArchiveToggleText));
        OnPropertyChanged(nameof(ClientEmptyStateText));
    }

    private void ApplyComputerFilter()
    {
        var query = ComputerFilterText.Trim();
        VisibleComputers.ReplaceWith(ComputerRecords.Where(computer => computer.IsArchived == ShowArchivedComputers &&
            (string.IsNullOrWhiteSpace(query) ||
             computer.ComputerLabel.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             computer.NetworkName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             computer.Zone.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             computer.Processor.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             computer.GraphicsCard.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             computer.Memory.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             computer.Status.Contains(query, StringComparison.OrdinalIgnoreCase))));
        OnPropertyChanged(nameof(HasNoComputers));
        OnPropertyChanged(nameof(ComputerArchiveToggleText));
        OnPropertyChanged(nameof(ComputerEmptyStateText));
    }

    private void ApplyTariffFilter()
    {
        VisibleTariffs.ReplaceWith(TariffRecords.Where(t => t.IsArchived == ShowArchivedTariffs));
        OnPropertyChanged(nameof(HasNoTariffs));
        OnPropertyChanged(nameof(TariffArchiveToggleText));
        OnPropertyChanged(nameof(TariffEmptyStateText));
    }

    private void RefreshHourlyOccupancy()
    {
        HourlyOccupancy.Clear();
        const int startHour = 10;
        for (var i = 0; i < 8; i++)
        {
            var start = DateTime.Today.AddHours(startHour + i);
            var end = start.AddHours(1);
            var activeSessions = _sessionCache.Count(s => s.StartedAt < end && s.PlannedEnd > start);
            var completedSessions = HistorySessions.Count(s => s.StartedAt < end && s.EndedAt > start);
            var reservations = Reservations.Count(r => r.StartsAt < end && r.StartsAt.AddMinutes(r.DurationMinutes) > start);
            var occupied = Math.Min(ComputerCount, activeSessions + completedSessions + reservations);
            var height = ComputerCount == 0 ? 6 : Math.Max(6, Math.Round(occupied * 60d / ComputerCount));
            HourlyOccupancy.Add(new OccupancyBarViewModel(start.ToString("HH:mm"), occupied, height, "#252D39"));
        }
        var busiestHour = HourlyOccupancy.Where(bar => bar.Count > 0).MaxBy(bar => bar.Count);
        if (busiestHour is not null)
        {
            var index = HourlyOccupancy.IndexOf(busiestHour);
            HourlyOccupancy[index] = busiestHour with { BarColor = "#C7F36B" };
        }
    }

    private static string FormatHours(int minutes) => (minutes / 60d).ToString("0.#", CultureInfo.GetCultureInfo("uk-UA"));

    private void ApplySessionFilter()
    {
        var query = FilterText.Trim();
        var now = DateTime.Now;
        var sessions = _sessionCache.Where(s =>
        {
            var matchesText = string.IsNullOrWhiteSpace(query) ||
                s.ComputerLabel.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.ClientName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.Game.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.TariffName.Contains(query, StringComparison.OrdinalIgnoreCase);
            var minutesLeft = (s.PlannedEnd - now).TotalMinutes;
            var matchesFilter = SelectedFilter switch
            {
                "Активні" => minutesLeft > 0,
                "Менше 15 хв" => minutesLeft is > 0 and <= 15,
                "Потрібна увага" => minutesLeft <= 0,
                _ => true
            };
            return matchesText && matchesFilter;
        }).ToList();

        ActiveSessions.Clear();
        foreach (var session in sessions)
            ActiveSessions.Add(new SessionCardViewModel(session, ExtendSession, EndSession));
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(HasNoSessions));
        OnPropertyChanged(nameof(HasVisibleSessions));
        OnPropertyChanged(nameof(ShowWideSessionTable));
        OnPropertyChanged(nameof(ActiveRunningCount));
        OnPropertyChanged(nameof(SessionsUnder15Count));
        OnPropertyChanged(nameof(OverdueSessionCount));
    }

    private void ExtendSession(int id, int minutes)
    {
        _repository.ExtendSession(id, minutes);
        StatusMessage = "Сеанс подовжено на 30 хвилин.";
        Refresh();
    }

    private void EndSession(int id)
    {
        var amount = _repository.EndSession(id);
        StatusMessage = amount is null
            ? "Сеанс уже завершено або його не знайдено."
            : $"Сеанс завершено. Нараховано за тарифом {amount.Value.ToString("0.00", CultureInfo.GetCultureInfo("uk-UA"))} ₴; оплату не фіксували.";
        Refresh();
    }

    private void CheckSessionAlerts(DateTime now)
    {
        foreach (var session in _sessionCache)
        {
            var remaining = session.PlannedEnd - now;
            if (remaining <= TimeSpan.FromMinutes(15) && remaining > TimeSpan.Zero && _checkedAlerts.Add((session.Id, "soon")))
            {
                if (_repository.AddSessionNotification(session.Id, "soon", "Сеанс завершується",
                        $"{session.ComputerLabel} · {session.ClientName}: залишилося близько {(int)remaining.TotalMinutes} хв."))
                {
                    StatusMessage = $"Нагадування: сеанс на {session.ComputerLabel} скоро завершиться.";
                    RefreshNotifications();
                }
            }
            if (remaining <= TimeSpan.Zero && _checkedAlerts.Add((session.Id, "overdue")))
            {
                if (_repository.AddSessionNotification(session.Id, "overdue", "Час сеансу вичерпано",
                        $"{session.ComputerLabel} · {session.ClientName}: запланований час завершився."))
                {
                    StatusMessage = $"Увага: час сеансу на {session.ComputerLabel} вичерпано.";
                    RefreshNotifications();
                }
            }
        }
    }

    private void RefreshNotifications()
    {
        Notifications.ReplaceWith(_repository.GetNotifications());
        OnPropertyChanged(nameof(UnreadNotificationCount));
        OnPropertyChanged(nameof(HasNotifications));
    }

    private async Task RefreshAgentStatusesAsync()
    {
        if (_agentPollRunning) return;
        var now = DateTime.Now;
        var configured = Computers.Where(c => !string.IsNullOrWhiteSpace(c.AgentEndpoint) &&
            now - _agentLastPolls.GetValueOrDefault(c.Id, DateTime.MinValue) >= TimeSpan.FromSeconds(Math.Clamp(c.AgentPollSeconds, 5, 300))).ToArray();
        if (configured.Length == 0) return;
        _agentPollRunning = true;
        foreach (var computer in configured) _agentLastPolls[computer.Id] = now;
        try
        {
            var results = await Task.WhenAll(configured.Select(async computer =>
                (computer.Id, Status: await _agentClient.IsAvailableAsync(computer.AgentEndpoint, computer.AgentConnectionKey))));
            foreach (var result in results)
            {
                var index = -1;
                for (var i = 0; i < Computers.Count; i++)
                    if (Computers[i].Id == result.Id) { index = i; break; }
                if (index < 0) continue;
                var updated = Computers[index] with { AgentStatus = result.Status ? "Online" : "Offline" };
                Computers[index] = updated;
                OnPropertyChanged(nameof(OfflineCount));
                OnPropertyChanged(nameof(OfflineSegmentWidth));
                if (SelectedAgentComputer?.Id == result.Id)
                {
                    AgentStatusText = result.Status ? "Підключено" : "Не підключено";
                    AgentLastCheckText = $"Остання перевірка: {DateTime.Now:HH:mm}";
                }
                for (var i = 0; i < AvailableComputers.Count; i++)
                    if (AvailableComputers[i].Id == result.Id) { AvailableComputers[i] = updated; break; }
            }
            OnPropertyChanged(nameof(OnlineAgentCount));
            OnPropertyChanged(nameof(AgentSummaryText));
            OnPropertyChanged(nameof(AgentDetailText));
        }
        finally { _agentPollRunning = false; }
    }

    [RelayCommand]
    private void AcknowledgeNotification(int id)
    {
        _repository.MarkNotificationRead(id);
        RefreshNotifications();
    }

    [RelayCommand] private void ToggleArchivedComputers() => ShowArchivedComputers = !ShowArchivedComputers;
    [RelayCommand] private void ToggleArchivedClients() => ShowArchivedClients = !ShowArchivedClients;
    [RelayCommand] private void ToggleArchivedTariffs() => ShowArchivedTariffs = !ShowArchivedTariffs;

    [RelayCommand]
    private void ToggleComputerArchive(int id)
    {
        var computer = ComputerRecords.FirstOrDefault(item => item.Id == id);
        if (computer is null) return;
        if (!_repository.SetComputerArchived(id, !computer.IsArchived))
        {
            StatusMessage = computer.IsArchived
                ? "Не вдалося повернути ПК з архіву. Перевірте, чи назва не зайнята."
                : "ПК має активний сеанс або майбутнє бронювання. Спершу завершіть їх.";
            return;
        }
        Refresh();
        StatusMessage = computer.IsArchived ? $"{computer.ComputerLabel} повернуто з архіву." : $"{computer.ComputerLabel} переміщено до архіву.";
    }

    [RelayCommand]
    private void ToggleClientArchive(int id)
    {
        var client = ClientRecords.FirstOrDefault(item => item.Id == id);
        if (client is null) return;
        if (!_repository.SetClientArchived(id, !client.IsArchived))
        {
            StatusMessage = client.IsArchived
                ? "Не вдалося повернути клієнта з архіву. Перевірте, чи номер телефону не зайнятий."
                : "У клієнта є активний сеанс або майбутнє бронювання. Спершу завершіть їх.";
            return;
        }
        Refresh();
        StatusMessage = client.IsArchived ? $"Клієнта «{client.Name}» повернуто з архіву." : $"Клієнта «{client.Name}» переміщено до архіву.";
    }

    [RelayCommand]
    private void ToggleTariffArchive(int id)
    {
        var tariff = TariffRecords.FirstOrDefault(item => item.Id == id);
        if (tariff is null) return;
        if (!_repository.SetTariffArchived(id, !tariff.IsArchived))
        {
            StatusMessage = tariff.IsArchived
                ? "Не вдалося повернути тариф з архіву. Перевірте, чи назва не зайнята."
                : "Цей тариф використовується активним сеансом. Його можна архівувати після завершення.";
            return;
        }
        Refresh();
        StatusMessage = tariff.IsArchived ? $"Тариф «{tariff.Name}» повернуто з архіву." : $"Тариф «{tariff.Name}» переміщено до архіву.";
    }

    [RelayCommand]
    private void EditComputer(int id)
    {
        var item = ComputerRecords.FirstOrDefault(computer => computer.Id == id && !computer.IsArchived);
        if (item is null) return;
        EditingComputerId = item.Id;
        NewComputerLabel = item.ComputerLabel;
        NewComputerZone = ZoneOptions.Contains(item.Zone) ? item.Zone : ZoneOptions.First();
        NewComputerNetworkName = item.NetworkName;
        NewComputerProcessor = item.Processor;
        NewComputerGraphicsCard = item.GraphicsCard;
        NewComputerMemory = item.Memory;
        NewComputerMonitor = item.Monitor;
        NewComputerStorage = item.Storage;
        NewComputerAgentEndpoint = "";
        ComputerFormError = "";
        IsComputerDialogOpen = true;
    }

    [RelayCommand]
    private void ShowNewComputerDialog()
    {
        EditingComputerId = null;
        NewComputerLabel = ""; NewComputerZone = ZoneOptions.First(); NewComputerNetworkName = ""; NewComputerProcessor = ""; NewComputerGraphicsCard = "";
        NewComputerMemory = ""; NewComputerMonitor = ""; NewComputerStorage = ""; ComputerFormError = "";
        NewComputerAgentEndpoint = "";
        IsComputerDialogOpen = true;
    }

    [RelayCommand]
    private void SaveNewComputer()
    {
        if (NewComputerLabel.Trim().Length < 2 || string.IsNullOrWhiteSpace(NewComputerZone))
        {
            ComputerFormError = "Вкажіть назву робочого місця та зону.";
            return;
        }
        var editing = EditingComputerId;
        var saved = editing is int computerId
            ? _repository.UpdateComputer(computerId, NewComputerLabel, NewComputerZone, NewComputerNetworkName,
                NewComputerProcessor, NewComputerGraphicsCard, NewComputerMemory, NewComputerMonitor, NewComputerStorage)
            : _repository.AddComputer(NewComputerLabel, NewComputerZone, NewComputerNetworkName, NewComputerProcessor,
                NewComputerGraphicsCard, NewComputerMemory, NewComputerMonitor, NewComputerStorage, NewComputerAgentEndpoint) != 0;
        if (!saved) { ComputerFormError = "Перевірте поля. Назва ПК має бути унікальною."; return; }
        IsComputerDialogOpen = false;
        EditingComputerId = null;
        Refresh();
        StatusMessage = editing is null
            ? $"Робоче місце {NewComputerLabel.Trim()} додано до обліку."
            : $"Зміни для {NewComputerLabel.Trim()} збережено.";
    }

    [RelayCommand]
    private void CancelNewComputerDialog()
    {
        IsComputerDialogOpen = false;
        EditingComputerId = null;
    }

    [RelayCommand]
    private void ConfigureComputerAgent(int computerId)
    {
        SelectedAgentComputer = Computers.FirstOrDefault(c => c.Id == computerId);
        if (SelectedAgentComputer is null) return;
        EditAgentAddress = Uri.TryCreate(SelectedAgentComputer.AgentEndpoint, UriKind.Absolute, out var endpointUri)
            ? endpointUri.Host
            : SelectedAgentComputer.NetworkName;
        EditAgentPort = SelectedAgentComputer.AgentPort.ToString(CultureInfo.InvariantCulture);
        EditAgentClubServer = SelectedAgentComputer.AgentClubServer;
        EditAgentConnectionKey = SelectedAgentComputer.AgentConnectionKey;
        EditAgentPollSeconds = SelectedAgentComputer.AgentPollSeconds.ToString(CultureInfo.InvariantCulture);
        AgentStatusText = SelectedAgentComputer.AgentStatus == "Online" ? "Підключено" : "Не підключено";
        AgentLastCheckText = "Остання перевірка: —";
        ComputerFormError = "";
        IsAgentDialogOpen = true;
    }

    [RelayCommand]
    private void SaveComputerAgentEndpoint()
    {
        if (!int.TryParse(EditAgentPort, out var port) || !int.TryParse(EditAgentPollSeconds, out var pollSeconds) ||
            SelectedAgentComputer is null || !_repository.UpdateComputerAgentConfiguration(SelectedAgentComputer.Id,
                EditAgentAddress, port, EditAgentClubServer, EditAgentConnectionKey, pollSeconds))
        {
            ComputerFormError = "Вкажіть адресу ПК, порт 1024–65535 і інтервал 5–300 секунд.";
            return;
        }
        IsAgentDialogOpen = false;
        Refresh();
        _agentLastPolls.Remove(SelectedAgentComputer.Id);
        StatusMessage = $"Налаштування агента для {SelectedAgentComputer.ComputerLabel} оновлено.";
    }

    [RelayCommand] private void CancelAgentDialog() => IsAgentDialogOpen = false;

    [RelayCommand]
    private void ShowNewTariffDialog()
    {
        EditingTariffId = null;
        NewTariffName = ""; NewTariffDescription = ""; NewTariffPrice = ""; NewTariffDurationMinutes = "60";
        NewTariffAvailability = TariffAvailabilityOptions.First(); NewTariffBadge = ""; TariffFormError = "";
        IsTariffDialogOpen = true;
    }

    [RelayCommand]
    private void EditTariff(int id)
    {
        var item = TariffRecords.FirstOrDefault(tariff => tariff.Id == id && !tariff.IsArchived);
        if (item is null) return;
        EditingTariffId = item.Id;
        NewTariffName = item.Name;
        NewTariffDescription = item.Description;
        NewTariffPrice = (item.PackagePrice > 0 ? item.PackagePrice : item.PricePerHour).ToString("0.##", CultureInfo.InvariantCulture);
        NewTariffDurationMinutes = item.DurationMinutes.ToString(CultureInfo.InvariantCulture);
        NewTariffAvailability = TariffAvailabilityOptions.Contains(item.Availability) ? item.Availability : TariffAvailabilityOptions.First();
        NewTariffBadge = item.Badge;
        TariffFormError = "";
        IsTariffDialogOpen = true;
    }

    [RelayCommand]
    private void SaveNewTariff()
    {
        if (!int.TryParse(NewTariffDurationMinutes, out var durationMinutes) || durationMinutes <= 0 ||
            !decimal.TryParse(NewTariffPrice.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var price) || price <= 0)
        {
            TariffFormError = "Вкажіть тривалість у хвилинах і вартість пакета, більшу за нуль.";
            return;
        }
        var editing = EditingTariffId;
        var saved = editing is int tariffId
            ? _repository.UpdateTariff(tariffId, NewTariffName, NewTariffDescription, durationMinutes, price, NewTariffAvailability, NewTariffBadge)
            : _repository.AddTariff(NewTariffName, NewTariffDescription, durationMinutes, price, NewTariffAvailability, NewTariffBadge) != 0;
        if (!saved) { TariffFormError = "Вкажіть назву тарифу; тариф із такою назвою вже існує."; return; }
        IsTariffDialogOpen = false;
        EditingTariffId = null;
        Refresh();
        StatusMessage = editing is null ? $"Тариф «{NewTariffName.Trim()}» додано." : $"Зміни тарифу «{NewTariffName.Trim()}» збережено.";
    }

    [RelayCommand]
    private void CancelNewTariffDialog()
    {
        IsTariffDialogOpen = false;
        EditingTariffId = null;
    }

    [RelayCommand]
    private void ShowNewReservationDialog()
    {
        if (Computers.Count == 0 || Clients.Count == 0)
        {
            StatusMessage = "Для бронювання додайте комп’ютер і клієнта.";
            return;
        }
        SelectedReservationComputer = Computers.FirstOrDefault();
        SelectedReservationClient = Clients.FirstOrDefault();
        NewReservationDate = DateTimeOffset.Now.AddDays(1);
        NewReservationTime = "18:00";
        NewReservationNote = "";
        EditingReservationId = null;
        SelectedReservationDuration = DurationOptions.FirstOrDefault(d => d.Minutes == 120);
        ReservationFormError = "";
        IsReservationDialogOpen = true;
    }

    [RelayCommand]
    private void SaveNewReservation()
    {
        if (SelectedReservationComputer is null || SelectedReservationClient is null || NewReservationDate is null || SelectedReservationDuration is null)
        {
            ReservationFormError = "Оберіть комп’ютер, клієнта, дату та тривалість.";
            return;
        }
        if (!TimeSpan.TryParseExact(NewReservationTime.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var time))
        {
            ReservationFormError = "Введіть час у форматі 18:30.";
            return;
        }
        var startsAt = NewReservationDate.Value.Date.Add(time);
        var editing = EditingReservationId is not null;
        if (!_repository.AddReservation(SelectedReservationComputer.Id, SelectedReservationClient.Id, startsAt,
                SelectedReservationDuration.Minutes, NewReservationNote, EditingReservationId))
        {
            ReservationFormError = "Час минув або слот перетинається із сеансом чи іншим бронюванням.";
            return;
        }
        IsReservationDialogOpen = false;
        Refresh();
        StatusMessage = editing ? "Бронювання оновлено." : $"Бронювання для {SelectedReservationClient.Name} створено.";
        EditingReservationId = null;
    }

    [RelayCommand] private void CancelNewReservationDialog() { IsReservationDialogOpen = false; EditingReservationId = null; }

    [RelayCommand]
    private void EditReservation(int id)
    {
        var reservation = Reservations.FirstOrDefault(r => r.Id == id);
        if (reservation is null) return;
        var computer = Computers.FirstOrDefault(c => c.ComputerLabel == reservation.ComputerLabel);
        var client = Clients.FirstOrDefault(c => c.Name == reservation.ClientName);
        if (computer is null || client is null) return;
        SelectedReservationComputer = computer;
        SelectedReservationClient = client;
        NewReservationDate = reservation.StartsAt;
        NewReservationTime = reservation.StartsAt.ToString("HH:mm");
        SelectedReservationDuration = DurationOptions.FirstOrDefault(d => d.Minutes == reservation.DurationMinutes)
                                     ?? new DurationChoice(reservation.DurationMinutes, $"{reservation.DurationMinutes} хв");
        NewReservationNote = reservation.Note;
        EditingReservationId = reservation.Id;
        ReservationFormError = "";
        IsReservationDialogOpen = true;
    }

    [RelayCommand] private void PreviousBookingDay() => SelectedBookingDate = SelectedBookingDate.AddDays(-1);
    [RelayCommand] private void NextBookingDay() => SelectedBookingDate = SelectedBookingDate.AddDays(1);

    [RelayCommand]
    private void CancelReservation(int id)
    {
        _repository.CancelReservation(id);
        Refresh();
        StatusMessage = "Бронювання скасовано.";
    }

    [RelayCommand]
    private void StartSession()
    {
        if (Computers.Count == 0 || Clients.Count == 0 || Tariffs.Count == 0)
        {
            StatusMessage = "Для старту сеансу додайте щонайменше один комп’ютер, клієнта й тариф.";
            SelectedPage = Computers.Count == 0 ? "Комп’ютери" : Clients.Count == 0 ? "Клієнти" : "Тарифи";
            return;
        }
        AvailableComputers.ReplaceWith(Computers.Where(c => c.Status == "Вільний"));
        if (AvailableComputers.Count == 0)
        {
            StatusMessage = "Немає вільного комп’ютера для нового сеансу.";
            return;
        }

        SelectedComputer = AvailableComputers.FirstOrDefault();
        var previousClientId = SelectedClient?.Id;
        SelectedClient = (previousClientId is null ? null : Clients.FirstOrDefault(c => c.Id == previousClientId))
                         ?? Clients.FirstOrDefault();
        SelectedTariff = Tariffs.FirstOrDefault(t => t.Name == "Стандарт") ?? Tariffs.FirstOrDefault();
        SelectedGame = GameOptions.FirstOrDefault() ?? "";
        SelectedDuration = DurationOptions.FirstOrDefault(d => d.Minutes == 60) ?? DurationOptions.FirstOrDefault();
        IsStartDialogOpen = true;
    }

    [RelayCommand]
    private void ConfirmStartSession()
    {
        if (!CanConfirmStart || SelectedComputer is null || SelectedClient is null ||
            SelectedTariff is null || SelectedDuration is null)
            return;

        var created = _repository.StartSession(
            SelectedComputer.Id, SelectedClient.Id, SelectedTariff.Id, SelectedGame, SelectedDuration.Minutes);
        if (!created)
        {
            StatusMessage = "Цей комп’ютер уже зайнятий або має бронювання на цей час. Оберіть інший ПК.";
            Refresh();
            SelectedComputer = AvailableComputers.FirstOrDefault();
            return;
        }

        IsStartDialogOpen = false;
        StatusMessage = $"Сеанс на {SelectedComputer.ComputerLabel} запущено на {SelectedDuration.DisplayText.ToLowerInvariant()}.";
        SelectedPage = "Огляд";
        Refresh();
    }

    [RelayCommand]
    private void CancelStartSession() => IsStartDialogOpen = false;

    [RelayCommand]
    private void ShowNewClientDialog()
    {
        EditingClientId = null;
        _returnToStartAfterClient = IsStartDialogOpen;
        if (_returnToStartAfterClient)
            IsStartDialogOpen = false;
        NewClientName = "";
        NewClientPhone = "";
        NewClientEmail = "";
        NewClientBirthDate = null;
        NewClientBalance = "0";
        NewClientNote = "";
        NewClientFavoriteGame = "Інша гра";
        NewClientErrorMessage = "";
        IsNewClientDialogOpen = true;
    }

    [RelayCommand]
    private void EditClient(int id)
    {
        var client = ClientRecords.FirstOrDefault(item => item.Id == id && !item.IsArchived);
        if (client is null) return;
        _returnToStartAfterClient = false;
        EditingClientId = client.Id;
        NewClientName = client.Name;
        NewClientPhone = client.Phone;
        NewClientEmail = client.Email;
        NewClientBirthDate = DateTimeOffset.TryParse(client.BirthDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var birthDate)
            ? birthDate : null;
        NewClientBalance = client.AccountBalance.ToString("0.##", CultureInfo.InvariantCulture);
        NewClientNote = client.Note;
        NewClientFavoriteGame = client.FavoriteGame;
        NewClientErrorMessage = "";
        IsNewClientDialogOpen = true;
    }

    [RelayCommand]
    private void SaveNewClient()
    {
        if (!CanSaveClient)
        {
            NewClientErrorMessage = "Перевірте ім’я, телефон, формат електронної пошти та початковий баланс.";
            return;
        }

        decimal.TryParse(NewClientBalance.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var initialBalance);
        var editing = EditingClientId;
        var id = editing is int clientId
            ? (_repository.UpdateClient(clientId, NewClientName, NewClientPhone, NewClientEmail,
                NewClientBirthDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), initialBalance, NewClientNote) ? clientId : 0)
            : _repository.AddClient(NewClientName, NewClientPhone, NewClientEmail,
                NewClientBirthDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), initialBalance, NewClientNote);
        if (id == 0)
        {
            NewClientErrorMessage = "Клієнт із таким номером телефону вже існує.";
            return;
        }

        IsNewClientDialogOpen = false;
        EditingClientId = null;
        ClientFilterText = "";
        Refresh();
        SelectedClient = Clients.FirstOrDefault(client => client.Id == id);
        if (editing is null && _returnToStartAfterClient)
            IsStartDialogOpen = true;
        else if (editing is null)
            SelectedPage = "Клієнти";
        _returnToStartAfterClient = false;
        StatusMessage = editing is null
            ? $"Клієнта «{NewClientName.Trim()}» додано. Його вже можна вибрати для нового сеансу."
            : $"Профіль клієнта «{NewClientName.Trim()}» оновлено.";
    }

    [RelayCommand]
    private void CancelNewClientDialog()
    {
        IsNewClientDialogOpen = false;
        NewClientErrorMessage = "";
        EditingClientId = null;
        if (_returnToStartAfterClient)
            IsStartDialogOpen = true;
        _returnToStartAfterClient = false;
    }

    [RelayCommand] private void ShowDashboard() => SelectedPage = "Огляд";
    [RelayCommand]
    private void SetSessionFilter(string filter)
    {
        if (FilterOptions.Contains(filter))
            SelectedFilter = filter;
    }

    [RelayCommand] private void ShowSessions() => SelectedPage = "Сеанси";
    [RelayCommand] private void ShowBookings() => SelectedPage = "Бронювання";
    [RelayCommand] private void ShowComputers() => SelectedPage = "Комп’ютери";
    [RelayCommand] private void ShowClients() => SelectedPage = "Клієнти";
    [RelayCommand] private void ShowTariffs() => SelectedPage = "Тарифи";
    [RelayCommand] private void ShowHistory() => SelectedPage = "Історія";
    [RelayCommand] private void ShowSettings() => SelectedPage = "Налаштування";
    [RelayCommand] private void ToggleNotifications() => IsNotificationPanelOpen = !IsNotificationPanelOpen;
    [RelayCommand] private void RefreshData() { Refresh(); StatusMessage = "Дані оновлено."; }
}

internal static class ObservableCollectionExtensions
{
    public static void ReplaceWith<T>(this ObservableCollection<T> collection, IEnumerable<T> values)
    {
        collection.Clear();
        foreach (var value in values)
            collection.Add(value);
    }
}
