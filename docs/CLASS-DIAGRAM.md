# Діаграма класів LEOPARD

```mermaid
classDiagram
    class MainWindow {
      +MainWindow.axaml
      +UpdateCompactState()
    }
    class MainViewModel {
      +SelectedPage
      +ActiveSessions
      +Computers
      +Reservations
      +Clients
      +Tariffs
      +HistorySessions
      +Notifications
      +StartSession()
      +ConfirmStartSession()
      +SaveNewComputer()
      +SaveNewClient()
      +SaveNewTariff()
      +SaveNewReservation()
      +AcknowledgeNotification(id)
      +ShowHistory()
      -CheckSessionAlerts(now)
      -Refresh()
    }
    class SessionCardViewModel {
      +DurationText
      +RemainingText
      +StatusText
      +Extend()
      +End()
      +UpdateTime(now)
    }
    class ClubRepository {
      -connectionString
      +GetActiveSessions()
      +GetCompletedSessions()
      +GetAccruedAmountForDate(date)
      +GetNotifications()
      +GetComputers()
      +GetReservations()
      +GetClients()
      +GetTariffs()
      +StartSession(...)
      +ExtendSession(id, minutes)
      +EndSession(id)
      +CalculateCharge(start, end, hourlyRate)
      +AddComputer(...)
      +AddClient(...)
      +AddTariff(...)
      +AddReservation(...)
      +CancelReservation(id)
      +UpdateComputerAgentEndpoint(id, endpoint)
      +AddSessionNotification(...)
      +MarkNotificationRead(id)
    }
    class ClubSession {
      +Id
      +ComputerId
      +ClientName
      +StartedAt
      +PlannedEnd
      +HourlyRate
    }
    class ComputerCard
    class ComputerAgentClient {
      +IsAvailableAsync(endpoint)
    }
    class PcAgent {
      +GET /api/v1/health
    }
    class ReservationCard
    class ClientCard {
      +Visits
      +LoyaltyPoints
    }
    class TariffCard
    class HistorySessionCard {
      +DurationMinutes
      +AccruedAmount
      +PointsAwarded
    }
    class ClubNotification {
      +Title
      +Message
      +CreatedAt
      +IsRead
    }
    class SQLite

    MainWindow --> MainViewModel : DataContext / bindings
    MainViewModel *-- SessionCardViewModel : timer-updated cards
    MainViewModel --> ClubRepository : reads and actions
    MainViewModel --> ComputerCard : selections
    MainViewModel --> ClientCard : selections
    MainViewModel --> TariffCard : selections and preview
    MainViewModel --> HistorySessionCard : report list
    MainViewModel --> ClubNotification : in-app alerts
    MainViewModel --> ComputerAgentClient : polls configured endpoints every 20s
    ComputerAgentClient ..> PcAgent : HTTP health request
    SessionCardViewModel --> ClubSession : formats timer fields
    ClubRepository --> SQLite : parameterized statements / transactions
    ClubRepository ..> ClubSession : reads active rows
    ClubRepository ..> HistorySessionCard : reads closed rows and accrued amount
    ClubRepository ..> ComputerCard : reads inventory state
    ClubRepository ..> ReservationCard : reads and creates reservations
    ClubRepository ..> ClientCard : reads and adds points
    ClubRepository ..> TariffCard : reads and creates tariffs
    ClubRepository ..> ClubNotification : persists alerts
```

`ClubRepository` owns transactional rules and SQLite access. ViewModels format view state and dispatch commands; Views contain AXAML bindings rather than SQL or domain mutation.

