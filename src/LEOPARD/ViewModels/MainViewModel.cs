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
    private DateTime _lastAgentPoll = DateTime.MinValue;
    private bool _agentPollRunning;

    public MainViewModel()
    {
        FilterOptions = new ObservableCollection<string> { "Усі", "Активні", "Менше 15 хв", "Потрібна увага" };
        Refresh();
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) =>
        {
            var now = DateTime.Now;
            foreach (var session in ActiveSessions)
                session.UpdateTime(now);
            CheckSessionAlerts(now);
            if (now - _lastAgentPoll >= TimeSpan.FromSeconds(20))
            {
                _lastAgentPoll = now;
                _ = RefreshAgentStatusesAsync();
            }
            if (SelectedFilter is "Менше 15 хв" or "Потрібна увага")
                ApplySessionFilter();
            OnPropertyChanged(nameof(CurrentTimeText));
            OnPropertyChanged(nameof(CurrentDateText));
        };
        _clock.Start();
    }

    public ObservableCollection<SessionCardViewModel> ActiveSessions { get; } = [];
    public ObservableCollection<ComputerCard> Computers { get; } = [];
    public ObservableCollection<ComputerCard> AvailableComputers { get; } = [];
    public ObservableCollection<ReservationCard> Reservations { get; } = [];
    public ObservableCollection<ClientCard> Clients { get; } = [];
    public ObservableCollection<ClientCard> VisibleClients { get; } = [];
    public ObservableCollection<TariffCard> Tariffs { get; } = [];
    public ObservableCollection<HistorySessionCard> HistorySessions { get; } = [];
    public ObservableCollection<ClubNotification> Notifications { get; } = [];
    public ObservableCollection<string> FilterOptions { get; }
    public ObservableCollection<string> GameOptions { get; } =
        ["Counter-Strike 2", "Valorant", "Dota 2", "Apex Legends", "Fortnite", "Інша гра"];
    public ObservableCollection<DurationChoice> DurationOptions { get; } =
        [new(15, "15 хвилин"), new(30, "30 хвилин"), new(60, "1 година"), new(90, "1 год 30 хв"),
         new(120, "2 години"), new(180, "3 години")];

    public string CurrentTimeText => DateTime.Now.ToString("HH:mm");
    public string CurrentDateText => DateTime.Now.ToString("dddd, dd MMMM yyyy", CultureInfo.GetCultureInfo("uk-UA"));
    public int ActiveCount => _sessionCache.Count;
    public int FreeCount => Computers.Count(c => c.Status == "Вільний");
    public int ReservedCount => Computers.Count(c => c.Status == "Заброньований");
    public int ComputerCount => Computers.Count;
    public int UnreadNotificationCount => Notifications.Count(n => !n.IsRead);
    public bool HasNotifications => Notifications.Count > 0;
    public bool HasHistory => HistorySessions.Count > 0;
    public bool HasNoHistory => HistorySessions.Count == 0;
    public string TodayEstimatedRevenue => HistorySessions.Where(s => s.EndedAt.Date == DateTime.Today).Sum(s => s.EstimatedAmount).ToString("0.00") + " ₴";
    public bool HasNoSessions => ActiveSessions.Count == 0;
    public bool HasNoClients => VisibleClients.Count == 0;
    public bool HasNoComputers => Computers.Count == 0;
    public bool HasNoTariffs => Tariffs.Count == 0;
    public bool HasNoReservations => Reservations.Count == 0;
    public bool HasClientFormError => !string.IsNullOrWhiteSpace(NewClientErrorMessage);
    public bool CanSaveClient
    {
        get
        {
            var digits = new string(NewClientPhone.Where(char.IsDigit).ToArray());
            return NewClientName.Trim().Length >= 2 && digits.Length is >= 7 and <= 15 &&
                   !string.IsNullOrWhiteSpace(NewClientFavoriteGame);
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
        : $"Орієнтовно {SelectedTariff.PricePerHour * SelectedDuration.Minutes / 60m:0.00} ₴";
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
        "Історія" => "Історія сеансів",
        "Сеанси" => "Активні сеанси",
        _ => "Огляд клубу"
    };

    public bool IsDashboard => SelectedPage == "Огляд";
    public bool IsSessions => SelectedPage == "Сеанси";
    public bool IsBookings => SelectedPage == "Бронювання";
    public bool IsComputers => SelectedPage == "Комп’ютери";
    public bool IsClients => SelectedPage == "Клієнти";
    public bool IsTariffs => SelectedPage == "Тарифи";
    public bool IsHistory => SelectedPage == "Історія";
    public bool IsWide => !IsCompact;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageTitle))]
    [NotifyPropertyChangedFor(nameof(IsDashboard))]
    [NotifyPropertyChangedFor(nameof(IsSessions))]
    [NotifyPropertyChangedFor(nameof(IsBookings))]
    [NotifyPropertyChangedFor(nameof(IsComputers))]
    [NotifyPropertyChangedFor(nameof(IsClients))]
    [NotifyPropertyChangedFor(nameof(IsTariffs))]
    [NotifyPropertyChangedFor(nameof(IsHistory))]
    private string selectedPage = "Огляд";

    [ObservableProperty]
    private string filterText = "";

    [ObservableProperty]
    private string selectedFilter = "Усі";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWide))]
    [NotifyPropertyChangedFor(nameof(SidebarColumnWidth))]
    [NotifyPropertyChangedFor(nameof(ContentMargin))]
    private bool isCompact;

    [ObservableProperty]
    private string statusMessage = "Дані клубу зберігаються локально у SQLite.";

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
    private string newClientFavoriteGame = "Counter-Strike 2";

    [ObservableProperty]
    private string newClientErrorMessage = "";

    [ObservableProperty]
    private string clientFilterText = "";

    [ObservableProperty] private bool isComputerDialogOpen;
    [ObservableProperty] private string newComputerLabel = "";
    [ObservableProperty] private string newComputerZone = "Основна зала";
    [ObservableProperty] private string newComputerProcessor = "";
    [ObservableProperty] private string newComputerGraphicsCard = "";
    [ObservableProperty] private string newComputerMemory = "";
    [ObservableProperty] private string newComputerMonitor = "";
    [ObservableProperty] private string newComputerStorage = "";
    [ObservableProperty] private string newComputerAgentEndpoint = "";
    [ObservableProperty] private string computerFormError = "";

    [ObservableProperty] private bool isAgentDialogOpen;
    [ObservableProperty] private ComputerCard? selectedAgentComputer;
    [ObservableProperty] private string editAgentEndpoint = "";

    [ObservableProperty] private bool isTariffDialogOpen;
    [ObservableProperty] private string newTariffName = "";
    [ObservableProperty] private string newTariffDescription = "";
    [ObservableProperty] private string newTariffPrice = "";
    [ObservableProperty] private string newTariffBadge = "";
    [ObservableProperty] private string tariffFormError = "";

    [ObservableProperty] private bool isReservationDialogOpen;
    [ObservableProperty] private ComputerCard? selectedReservationComputer;
    [ObservableProperty] private ClientCard? selectedReservationClient;
    [ObservableProperty] private DateTimeOffset? newReservationDate = DateTimeOffset.Now.AddDays(1);
    [ObservableProperty] private string newReservationTime = "18:00";
    [ObservableProperty] private DurationChoice? selectedReservationDuration;
    [ObservableProperty] private string reservationFormError = "";

    partial void OnFilterTextChanged(string value) => ApplySessionFilter();
    partial void OnSelectedFilterChanged(string value) => ApplySessionFilter();
    partial void OnSelectedComputerChanged(ComputerCard? value)
    {
        OnPropertyChanged(nameof(CanConfirmStart));
        OnPropertyChanged(nameof(SelectedComputerSpecText));
    }
    partial void OnSelectedClientChanged(ClientCard? value) => OnPropertyChanged(nameof(CanConfirmStart));
    partial void OnSelectedTariffChanged(TariffCard? value)
    {
        OnPropertyChanged(nameof(CanConfirmStart));
        OnPropertyChanged(nameof(EstimatedCostText));
    }
    partial void OnSelectedDurationChanged(DurationChoice? value)
    {
        OnPropertyChanged(nameof(CanConfirmStart));
        OnPropertyChanged(nameof(EstimatedCostText));
    }
    partial void OnSelectedGameChanged(string value) => OnPropertyChanged(nameof(CanConfirmStart));
    partial void OnNewClientNameChanged(string value) => OnClientFormValueChanged();
    partial void OnNewClientPhoneChanged(string value) => OnClientFormValueChanged();
    partial void OnNewClientFavoriteGameChanged(string value) => OnClientFormValueChanged();
    partial void OnNewClientErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasClientFormError));
    partial void OnClientFilterTextChanged(string value) => ApplyClientFilter();
    partial void OnNewComputerLabelChanged(string value) => ComputerFormError = "";
    partial void OnNewComputerZoneChanged(string value) => ComputerFormError = "";
    partial void OnNewTariffNameChanged(string value) => TariffFormError = "";
    partial void OnNewTariffPriceChanged(string value) => TariffFormError = "";

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
        Computers.ReplaceWith(_repository.GetComputers());
        AvailableComputers.ReplaceWith(Computers.Where(c => c.Status == "Вільний"));
        Reservations.ReplaceWith(_repository.GetReservations());
        Clients.ReplaceWith(_repository.GetClients());
        ApplyClientFilter();
        Tariffs.ReplaceWith(_repository.GetTariffs());
        _sessionCache = _repository.GetActiveSessions();
        HistorySessions.ReplaceWith(_repository.GetCompletedSessions());
        Notifications.ReplaceWith(_repository.GetNotifications());
        ApplySessionFilter();
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(FreeCount));
        OnPropertyChanged(nameof(ReservedCount));
        OnPropertyChanged(nameof(ComputerCount));
        OnPropertyChanged(nameof(HasNoComputers));
        OnPropertyChanged(nameof(HasNoTariffs));
        OnPropertyChanged(nameof(HasNoReservations));
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HasNoHistory));
        OnPropertyChanged(nameof(TodayEstimatedRevenue));
        OnPropertyChanged(nameof(UnreadNotificationCount));
        OnPropertyChanged(nameof(HasNotifications));
    }

    private void ApplyClientFilter()
    {
        var query = ClientFilterText.Trim();
        VisibleClients.ReplaceWith(Clients.Where(client =>
            string.IsNullOrWhiteSpace(query) ||
            client.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            client.Phone.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            client.FavoriteGame.Contains(query, StringComparison.OrdinalIgnoreCase)));
        OnPropertyChanged(nameof(HasNoClients));
    }

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
                "Менше 15 хв" => minutesLeft <= 15,
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
    }

    private void ExtendSession(int id, int minutes)
    {
        _repository.ExtendSession(id, minutes);
        StatusMessage = "Сеанс подовжено на 30 хвилин.";
        Refresh();
    }

    private void EndSession(int id)
    {
        _repository.EndSession(id);
        StatusMessage = "Сеанс завершено. Комп’ютер тепер доступний.";
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
        var configured = Computers.Where(c => !string.IsNullOrWhiteSpace(c.AgentEndpoint)).ToArray();
        if (configured.Length == 0) return;
        _agentPollRunning = true;
        try
        {
            var results = await Task.WhenAll(configured.Select(async computer =>
                (computer.Id, Status: await _agentClient.IsAvailableAsync(computer.AgentEndpoint))));
            foreach (var result in results)
            {
                var index = -1;
                for (var i = 0; i < Computers.Count; i++)
                    if (Computers[i].Id == result.Id) { index = i; break; }
                if (index < 0) continue;
                var updated = Computers[index] with { AgentStatus = result.Status ? "Online" : "Offline" };
                Computers[index] = updated;
                for (var i = 0; i < AvailableComputers.Count; i++)
                    if (AvailableComputers[i].Id == result.Id) { AvailableComputers[i] = updated; break; }
            }
        }
        finally { _agentPollRunning = false; }
    }

    [RelayCommand]
    private void AcknowledgeNotification(int id)
    {
        _repository.MarkNotificationRead(id);
        RefreshNotifications();
    }

    [RelayCommand]
    private void ShowNewComputerDialog()
    {
        NewComputerLabel = ""; NewComputerProcessor = ""; NewComputerGraphicsCard = "";
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
        var id = _repository.AddComputer(NewComputerLabel, NewComputerZone, NewComputerProcessor,
            NewComputerGraphicsCard, NewComputerMemory, NewComputerMonitor, NewComputerStorage, NewComputerAgentEndpoint);
        if (id == 0) { ComputerFormError = "Перевірте назву ПК та адресу агента (http://хост:47831); назва має бути унікальною."; return; }
        IsComputerDialogOpen = false;
        Refresh();
        StatusMessage = $"Робоче місце {NewComputerLabel.Trim()} додано до обліку.";
    }

    [RelayCommand] private void CancelNewComputerDialog() => IsComputerDialogOpen = false;

    [RelayCommand]
    private void ConfigureComputerAgent(int computerId)
    {
        SelectedAgentComputer = Computers.FirstOrDefault(c => c.Id == computerId);
        if (SelectedAgentComputer is null) return;
        EditAgentEndpoint = SelectedAgentComputer.AgentEndpoint;
        ComputerFormError = "";
        IsAgentDialogOpen = true;
    }

    [RelayCommand]
    private void SaveComputerAgentEndpoint()
    {
        if (SelectedAgentComputer is null || !_repository.UpdateComputerAgentEndpoint(SelectedAgentComputer.Id, EditAgentEndpoint))
        {
            ComputerFormError = "Введіть базову адресу http://хост:47831 або https://хост; поле можна залишити порожнім.";
            return;
        }
        IsAgentDialogOpen = false;
        Refresh();
        _lastAgentPoll = DateTime.MinValue;
        StatusMessage = $"Налаштування агента для {SelectedAgentComputer.ComputerLabel} оновлено.";
    }

    [RelayCommand] private void CancelAgentDialog() => IsAgentDialogOpen = false;

    [RelayCommand]
    private void ShowNewTariffDialog()
    {
        NewTariffName = ""; NewTariffDescription = ""; NewTariffPrice = ""; NewTariffBadge = ""; TariffFormError = "";
        IsTariffDialogOpen = true;
    }

    [RelayCommand]
    private void SaveNewTariff()
    {
        if (!decimal.TryParse(NewTariffPrice.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var price) || price <= 0)
        {
            TariffFormError = "Вкажіть ціну за годину, більшу за нуль.";
            return;
        }
        var id = _repository.AddTariff(NewTariffName, NewTariffDescription, price, NewTariffBadge);
        if (id == 0) { TariffFormError = "Вкажіть назву тарифу; тариф із такою назвою вже існує."; return; }
        IsTariffDialogOpen = false;
        Refresh();
        StatusMessage = $"Тариф «{NewTariffName.Trim()}» додано.";
    }

    [RelayCommand] private void CancelNewTariffDialog() => IsTariffDialogOpen = false;

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
        if (!_repository.AddReservation(SelectedReservationComputer.Id, SelectedReservationClient.Id, startsAt, SelectedReservationDuration.Minutes))
        {
            ReservationFormError = "Час минув або слот перетинається із сеансом чи іншим бронюванням.";
            return;
        }
        IsReservationDialogOpen = false;
        Refresh();
        StatusMessage = $"Бронювання для {SelectedReservationClient.Name} створено.";
    }

    [RelayCommand] private void CancelNewReservationDialog() => IsReservationDialogOpen = false;

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
        _returnToStartAfterClient = IsStartDialogOpen;
        if (_returnToStartAfterClient)
            IsStartDialogOpen = false;
        NewClientName = "";
        NewClientPhone = "";
        NewClientFavoriteGame = GameOptions.FirstOrDefault() ?? "Інша гра";
        NewClientErrorMessage = "";
        IsNewClientDialogOpen = true;
    }

    [RelayCommand]
    private void SaveNewClient()
    {
        if (!CanSaveClient)
        {
            NewClientErrorMessage = "Вкажіть ім’я, телефон (7–15 цифр) і улюблену гру.";
            return;
        }

        var id = _repository.AddClient(NewClientName, NewClientPhone, NewClientFavoriteGame);
        if (id == 0)
        {
            NewClientErrorMessage = "Клієнт із таким номером телефону вже існує.";
            return;
        }

        IsNewClientDialogOpen = false;
        ClientFilterText = "";
        Refresh();
        SelectedClient = Clients.FirstOrDefault(client => client.Id == id);
        if (_returnToStartAfterClient)
            IsStartDialogOpen = true;
        else
            SelectedPage = "Клієнти";
        _returnToStartAfterClient = false;
        StatusMessage = $"Клієнта «{NewClientName.Trim()}» додано. Його вже можна вибрати для нового сеансу.";
    }

    [RelayCommand]
    private void CancelNewClientDialog()
    {
        IsNewClientDialogOpen = false;
        NewClientErrorMessage = "";
        if (_returnToStartAfterClient)
            IsStartDialogOpen = true;
        _returnToStartAfterClient = false;
    }

    [RelayCommand] private void ShowDashboard() => SelectedPage = "Огляд";
    [RelayCommand] private void ShowSessions() => SelectedPage = "Сеанси";
    [RelayCommand] private void ShowBookings() => SelectedPage = "Бронювання";
    [RelayCommand] private void ShowComputers() => SelectedPage = "Комп’ютери";
    [RelayCommand] private void ShowClients() => SelectedPage = "Клієнти";
    [RelayCommand] private void ShowTariffs() => SelectedPage = "Тарифи";
    [RelayCommand] private void ShowHistory() => SelectedPage = "Історія";
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
