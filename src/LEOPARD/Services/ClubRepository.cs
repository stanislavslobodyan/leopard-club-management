using LEOPARD.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace LEOPARD.Services;

public sealed class ClubRepository
{
    private readonly string _connectionString;

    public ClubRepository()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LEOPARD");
        Directory.CreateDirectory(folder);
        // The production store is separate from the old development database. That
        // file is intentionally left untouched so a user can recover its contents.
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(folder, "club.sqlite3") }.ToString();
        Initialize();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Computers(
                Id INTEGER PRIMARY KEY, Label TEXT NOT NULL, Zone TEXT NOT NULL,
                Processor TEXT NOT NULL DEFAULT '', GraphicsCard TEXT NOT NULL DEFAULT '',
                Memory TEXT NOT NULL DEFAULT '', Monitor TEXT NOT NULL DEFAULT '', Storage TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS Clients(Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Phone TEXT NOT NULL, Visits INTEGER NOT NULL DEFAULT 0, FavoriteGame TEXT NOT NULL, LoyaltyPoints INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS Tariffs(Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Description TEXT NOT NULL, PricePerHour REAL NOT NULL, Badge TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Sessions(Id INTEGER PRIMARY KEY, ComputerId INTEGER NOT NULL REFERENCES Computers, ClientId INTEGER NOT NULL REFERENCES Clients, Game TEXT NOT NULL, TariffId INTEGER NOT NULL REFERENCES Tariffs, StartedAt TEXT NOT NULL, PlannedEnd TEXT NOT NULL, EndedAt TEXT NULL, PointsAwarded INTEGER NOT NULL DEFAULT 0, ChargeAmount REAL NULL);
            CREATE TABLE IF NOT EXISTS Reservations(Id INTEGER PRIMARY KEY, ComputerId INTEGER NOT NULL REFERENCES Computers, ClientId INTEGER NOT NULL REFERENCES Clients, StartsAt TEXT NOT NULL, DurationMinutes INTEGER NOT NULL, Status TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Notifications(Id INTEGER PRIMARY KEY, SessionId INTEGER NOT NULL REFERENCES Sessions, Kind TEXT NOT NULL, Title TEXT NOT NULL, Message TEXT NOT NULL, CreatedAt TEXT NOT NULL, IsRead INTEGER NOT NULL DEFAULT 0, UNIQUE(SessionId,Kind));
            """;
        command.ExecuteNonQuery();
        EnsureComputerSpecColumn(connection, "Processor");
        EnsureComputerSpecColumn(connection, "GraphicsCard");
        EnsureComputerSpecColumn(connection, "Memory");
        EnsureComputerSpecColumn(connection, "Monitor");
        EnsureComputerSpecColumn(connection, "Storage");
        EnsureColumn(connection, "Clients", "LoyaltyPoints", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "Sessions", "PointsAwarded", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "Sessions", "ChargeAmount", "REAL NULL");
        EnsureColumn(connection, "Computers", "AgentEndpoint", "TEXT NOT NULL DEFAULT ''");
        using var index = connection.CreateCommand();
        index.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS IX_Computers_Label ON Computers(Label COLLATE NOCASE); CREATE UNIQUE INDEX IF NOT EXISTS IX_Tariffs_Name ON Tariffs(Name COLLATE NOCASE); CREATE UNIQUE INDEX IF NOT EXISTS IX_Clients_Phone ON Clients(Phone); CREATE INDEX IF NOT EXISTS IX_Sessions_ActiveComputer ON Sessions(ComputerId,EndedAt); CREATE INDEX IF NOT EXISTS IX_Reservations_ComputerStart ON Reservations(ComputerId,StartsAt);";
        index.ExecuteNonQuery();
    }

    private static void EnsureComputerSpecColumn(SqliteConnection connection, string column)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA table_info(Computers);";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
                return;
        }
        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE Computers ADD COLUMN {column} TEXT NOT NULL DEFAULT '';";
        alter.ExecuteNonQuery();
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table});";
        using var reader = check.ExecuteReader();
        while (reader.Read())
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return;
        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection db, SqliteTransaction tx, string sql, params (string Name, object Value)[] args)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var arg in args) command.Parameters.AddWithValue(arg.Name, arg.Value);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ClubSession> GetActiveSessions()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT s.Id,s.ComputerId,c.Label,cl.Name,s.Game,t.Name,s.StartedAt,s.PlannedEnd,t.PricePerHour
            FROM Sessions s JOIN Computers c ON c.Id=s.ComputerId JOIN Clients cl ON cl.Id=s.ClientId
            JOIN Tariffs t ON t.Id=s.TariffId WHERE s.EndedAt IS NULL ORDER BY s.StartedAt;
            """;
        using var reader = cmd.ExecuteReader();
        var list = new List<ClubSession>();
        while (reader.Read())
            list.Add(new ClubSession(reader.GetInt32(0),reader.GetInt32(1),reader.GetString(2),reader.GetString(3),
                reader.GetString(4),reader.GetString(5),DateTime.Parse(reader.GetString(6)),DateTime.Parse(reader.GetString(7)),
                Convert.ToDecimal(reader.GetDouble(8))));
        return list;
    }

    public bool StartSession(int computerId, int clientId, int tariffId, string game, int durationMinutes)
    {
        if (durationMinutes <= 0 || string.IsNullOrWhiteSpace(game)) return false;
        using var db = Open();
        using var tx = db.BeginTransaction();
        var now = DateTime.Now;
        using (var active = db.CreateCommand())
        {
            active.Transaction = tx;
            active.CommandText = "SELECT COUNT(*) FROM Sessions WHERE ComputerId=$pc AND EndedAt IS NULL;";
            active.Parameters.AddWithValue("$pc", computerId);
            if (Convert.ToInt32(active.ExecuteScalar()) > 0) { tx.Rollback(); return false; }
        }

        var sessionEnd = now.AddMinutes(durationMinutes);
        var hasReservationConflict = false;
        using (var reservations = db.CreateCommand())
        {
            reservations.Transaction = tx;
            reservations.CommandText = "SELECT StartsAt,DurationMinutes FROM Reservations WHERE ComputerId=$pc AND Status<>'Скасовано';";
            reservations.Parameters.AddWithValue("$pc", computerId);
            using var reader = reservations.ExecuteReader();
            while (reader.Read())
            {
                var startsAt = DateTime.Parse(reader.GetString(0));
                var endsAt = startsAt.AddMinutes(reader.GetInt32(1));
                if (startsAt < sessionEnd && endsAt > now)
                {
                    hasReservationConflict = true;
                    break;
                }
            }
        }
        if (hasReservationConflict) { tx.Rollback(); return false; }

        Execute(db,tx,"INSERT INTO Sessions(ComputerId,ClientId,Game,TariffId,StartedAt,PlannedEnd) VALUES($pc,$client,$game,$tariff,$start,$end);",
            ("$pc",computerId),("$client",clientId),("$game",game.Trim()),("$tariff",tariffId),
            ("$start",now.ToString("O")),("$end",sessionEnd.ToString("O")));
        tx.Commit();
        return true;
    }

    public void ExtendSession(int id, int minutes)
    {
        using var db=Open();
        using var cmd=db.CreateCommand();
        cmd.CommandText="UPDATE Sessions SET PlannedEnd=$end WHERE Id=$id AND EndedAt IS NULL;";
        var current=GetActiveSessions().FirstOrDefault(s=>s.Id==id);
        if(current is null) return;
        cmd.Parameters.AddWithValue("$end",current.PlannedEnd.AddMinutes(minutes).ToString("O"));
        cmd.Parameters.AddWithValue("$id",id); cmd.ExecuteNonQuery();
    }

    public decimal? EndSession(int id)
    {
        using var db=Open(); using var tx=db.BeginTransaction();
        DateTime startedAt; int clientId; string computerLabel; decimal hourlyRate;
        using (var lookup=db.CreateCommand())
        {
            lookup.Transaction=tx;
            lookup.CommandText="SELECT s.StartedAt,s.ClientId,c.Label,t.PricePerHour FROM Sessions s JOIN Computers c ON c.Id=s.ComputerId JOIN Tariffs t ON t.Id=s.TariffId WHERE s.Id=$id AND s.EndedAt IS NULL;";
            lookup.Parameters.AddWithValue("$id",id);
            using var reader=lookup.ExecuteReader();
            if(!reader.Read()){tx.Rollback();return null;}
            startedAt=DateTime.Parse(reader.GetString(0)); clientId=reader.GetInt32(1); computerLabel=reader.GetString(2);
            hourlyRate=Convert.ToDecimal(reader.GetDouble(3));
        }
        var endedAt=DateTime.Now;
        var elapsed=endedAt>startedAt ? endedAt-startedAt : TimeSpan.Zero;
        var fullHours=(int)(elapsed.Ticks/TimeSpan.TicksPerHour);
        var chargeAmount=CalculateCharge(startedAt,endedAt,hourlyRate);
        using(var update=db.CreateCommand())
        {
            update.Transaction=tx;
            update.CommandText="UPDATE Sessions SET EndedAt=$now,PointsAwarded=$points,ChargeAmount=$charge WHERE Id=$id AND EndedAt IS NULL; UPDATE Clients SET Visits=Visits+1,LoyaltyPoints=LoyaltyPoints+$points WHERE Id=$client;";
            update.Parameters.AddWithValue("$now",endedAt.ToString("O")); update.Parameters.AddWithValue("$points",fullHours);
            update.Parameters.AddWithValue("$charge",chargeAmount);
            update.Parameters.AddWithValue("$id",id); update.Parameters.AddWithValue("$client",clientId); update.ExecuteNonQuery();
        }
        using(var alert=db.CreateCommand())
        {
            alert.Transaction=tx;
            alert.CommandText="INSERT OR IGNORE INTO Notifications(SessionId,Kind,Title,Message,CreatedAt) VALUES($session,'ended','Сеанс завершено',$message,$now);";
            var formattedCharge = chargeAmount.ToString("0.00", CultureInfo.GetCultureInfo("uk-UA"));
            alert.Parameters.AddWithValue("$session",id); alert.Parameters.AddWithValue("$message",$"Сеанс на {computerLabel} завершено о {endedAt:HH:mm}. Нараховано за тарифом: {formattedCharge} ₴. Бонусних балів: {fullHours}.");
            alert.Parameters.AddWithValue("$now",endedAt.ToString("O")); alert.ExecuteNonQuery();
        }
        tx.Commit();
        return chargeAmount;
    }

    private static decimal CalculateCharge(DateTime startedAt, DateTime endedAt, decimal hourlyRate)
    {
        var elapsedTicks = Math.Max(0L, (endedAt - startedAt).Ticks);
        var unroundedAmount = hourlyRate * elapsedTicks / TimeSpan.TicksPerHour;
        return decimal.Round(unroundedAmount, 2, MidpointRounding.AwayFromZero);
    }

    public IReadOnlyList<ComputerCard> GetComputers()
    {
        var sessions=GetActiveSessions();
        using var db=Open(); using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT Id,Label,Zone,Processor,GraphicsCard,Memory,Monitor,Storage,AgentEndpoint FROM Computers ORDER BY Id;";
        using var reader=cmd.ExecuteReader(); var result=new List<ComputerCard>();
        while(reader.Read())
        {
            var id=reader.GetInt32(0); var label=reader.GetString(1); var zone=reader.GetString(2);
            var session=sessions.FirstOrDefault(s=>s.ComputerId==id);
            var status = session is not null ? "Активний" : IsReserved(id) ? "Заброньований" : "Вільний";
            var details = session is not null ? $"{session.ClientName} · {session.Game}"
                : status == "Заброньований" ? "Є майбутнє бронювання" : "Готовий до сеансу";
            result.Add(new(id,label,zone,status,details,reader.GetString(3),reader.GetString(4),
                reader.GetString(5),reader.GetString(6),reader.GetString(7),reader.GetString(8),"Unknown"));
        }
        return result;
    }

    private bool IsReserved(int computerId)
    {
        using var db=Open(); using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT COUNT(*) FROM Reservations WHERE ComputerId=$pc AND Status<>'Скасовано' AND StartsAt>$now;";
        cmd.Parameters.AddWithValue("$pc",computerId); cmd.Parameters.AddWithValue("$now",DateTime.Now.ToString("O"));
        return Convert.ToInt32(cmd.ExecuteScalar())>0;
    }

    public IReadOnlyList<ReservationCard> GetReservations()
    {
        using var db=Open(); using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT r.Id,c.Label,cl.Name,r.StartsAt,r.DurationMinutes,r.Status FROM Reservations r JOIN Computers c ON c.Id=r.ComputerId JOIN Clients cl ON cl.Id=r.ClientId WHERE r.Status<>'Скасовано' ORDER BY r.StartsAt;";
        using var reader=cmd.ExecuteReader(); var list=new List<ReservationCard>();
        while(reader.Read())
        {
            var startsAt = DateTime.Parse(reader.GetString(3));
            var minutes = reader.GetInt32(4);
            list.Add(new(reader.GetInt32(0),reader.GetString(1),reader.GetString(2),startsAt,minutes,
                startsAt.ToString("dd.MM · HH:mm"),$"{minutes / 60} год {minutes % 60:00} хв",reader.GetString(5)));
        }
        return list;
    }

    public int AddComputer(string label, string zone, string processor, string graphicsCard, string memory, string monitor, string storage, string agentEndpoint)
    {
        label = label.Trim(); zone = zone.Trim();
        var normalizedAgentEndpoint = NormalizeAgentEndpoint(agentEndpoint);
        if (label.Length < 2 || string.IsNullOrWhiteSpace(zone) || normalizedAgentEndpoint is null) return 0;
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO Computers(Label,Zone,Processor,GraphicsCard,Memory,Monitor,Storage,AgentEndpoint) VALUES($label,$zone,$cpu,$gpu,$ram,$monitor,$storage,$agent); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$label", label); cmd.Parameters.AddWithValue("$zone", zone);
        cmd.Parameters.AddWithValue("$cpu", processor.Trim()); cmd.Parameters.AddWithValue("$gpu", graphicsCard.Trim());
        cmd.Parameters.AddWithValue("$ram", memory.Trim()); cmd.Parameters.AddWithValue("$monitor", monitor.Trim());
        cmd.Parameters.AddWithValue("$storage", storage.Trim()); cmd.Parameters.AddWithValue("$agent", normalizedAgentEndpoint);
        try { return Convert.ToInt32(cmd.ExecuteScalar()); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { return 0; }
    }

    public bool UpdateComputerAgentEndpoint(int computerId, string agentEndpoint)
    {
        var normalizedAgentEndpoint = NormalizeAgentEndpoint(agentEndpoint);
        if (normalizedAgentEndpoint is null) return false;
        using var db=Open(); using var cmd=db.CreateCommand();
        cmd.CommandText="UPDATE Computers SET AgentEndpoint=$endpoint WHERE Id=$id;";
        cmd.Parameters.AddWithValue("$endpoint",normalizedAgentEndpoint); cmd.Parameters.AddWithValue("$id",computerId);
        return cmd.ExecuteNonQuery()==1;
    }

    private static string? NormalizeAgentEndpoint(string endpoint)
    {
        endpoint = endpoint.Trim().TrimEnd('/');
        if (endpoint.Length == 0) return "";
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath is not ("" or "/")) return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }

    public int AddTariff(string name, string description, decimal hourlyRate, string badge)
    {
        name = name.Trim();
        if (name.Length < 2 || hourlyRate <= 0) return 0;
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO Tariffs(Name,Description,PricePerHour,Badge) VALUES($name,$description,$rate,$badge); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$name", name); cmd.Parameters.AddWithValue("$description", description.Trim());
        cmd.Parameters.AddWithValue("$rate", hourlyRate); cmd.Parameters.AddWithValue("$badge", badge.Trim());
        try { return Convert.ToInt32(cmd.ExecuteScalar()); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { return 0; }
    }

    public bool AddReservation(int computerId, int clientId, DateTime startsAt, int durationMinutes)
    {
        if (startsAt <= DateTime.Now || durationMinutes <= 0) return false;
        var endsAt = startsAt.AddMinutes(durationMinutes);
        using var db = Open(); using var tx = db.BeginTransaction();
        using (var active = db.CreateCommand())
        {
            active.Transaction = tx;
            active.CommandText = "SELECT PlannedEnd FROM Sessions WHERE ComputerId=$pc AND EndedAt IS NULL;";
            active.Parameters.AddWithValue("$pc", computerId);
            using var reader = active.ExecuteReader();
            while (reader.Read())
                if (DateTime.Parse(reader.GetString(0)) > startsAt) { tx.Rollback(); return false; }
        }
        using (var existing = db.CreateCommand())
        {
            existing.Transaction = tx;
            existing.CommandText = "SELECT StartsAt,DurationMinutes FROM Reservations WHERE ComputerId=$pc AND Status<>'Скасовано';";
            existing.Parameters.AddWithValue("$pc", computerId);
            using var reader = existing.ExecuteReader();
            while (reader.Read())
            {
                var existingStart = DateTime.Parse(reader.GetString(0));
                var existingEnd = existingStart.AddMinutes(reader.GetInt32(1));
                if (existingStart < endsAt && existingEnd > startsAt) { tx.Rollback(); return false; }
            }
        }
        Execute(db, tx, "INSERT INTO Reservations(ComputerId,ClientId,StartsAt,DurationMinutes,Status) VALUES($pc,$client,$start,$minutes,'Підтверджено');",
            ("$pc", computerId), ("$client", clientId), ("$start", startsAt.ToString("O")), ("$minutes", durationMinutes));
        tx.Commit();
        return true;
    }

    public void CancelReservation(int id)
    {
        using var db=Open(); using var cmd=db.CreateCommand();
        cmd.CommandText="UPDATE Reservations SET Status='Скасовано' WHERE Id=$id AND Status<>'Скасовано';";
        cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<ClientCard> GetClients()
    {
        using var db=Open(); using var cmd=db.CreateCommand(); cmd.CommandText="SELECT Id,Name,Phone,Visits,FavoriteGame,LoyaltyPoints FROM Clients ORDER BY Name;";
        using var r=cmd.ExecuteReader(); var list=new List<ClientCard>();
        while(r.Read()) list.Add(new(r.GetInt32(0),r.GetString(1),r.GetString(2),r.GetInt32(3),r.GetString(4),r.GetInt32(5)));
        return list;
    }

    public IReadOnlyList<HistorySessionCard> GetCompletedSessions()
    {
        using var db=Open(); using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT s.Id,c.Label,cl.Name,s.Game,t.Name,s.StartedAt,s.EndedAt,t.PricePerHour,s.ChargeAmount,s.PointsAwarded FROM Sessions s JOIN Computers c ON c.Id=s.ComputerId JOIN Clients cl ON cl.Id=s.ClientId JOIN Tariffs t ON t.Id=s.TariffId WHERE s.EndedAt IS NOT NULL ORDER BY s.EndedAt DESC LIMIT 300;";
        using var reader=cmd.ExecuteReader(); var result=new List<HistorySessionCard>();
        while(reader.Read())
        {
            var start=DateTime.Parse(reader.GetString(5)); var end=DateTime.Parse(reader.GetString(6));
            var minutes=Math.Max(0,(int)(end-start).TotalMinutes);
            var hourlyRate=Convert.ToDecimal(reader.GetDouble(7));
            var chargeAmount=reader.IsDBNull(8) ? CalculateCharge(start,end,hourlyRate) : Convert.ToDecimal(reader.GetDouble(8));
            result.Add(new(reader.GetInt32(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),start,end,minutes,chargeAmount,reader.GetInt32(9)));
        }
        return result;
    }

    public decimal GetAccruedAmountForDate(DateTime date)
    {
        using var db=Open(); using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT s.StartedAt,s.EndedAt,t.PricePerHour,s.ChargeAmount FROM Sessions s JOIN Tariffs t ON t.Id=s.TariffId WHERE s.EndedAt IS NOT NULL AND substr(s.EndedAt,1,10)=$day;";
        cmd.Parameters.AddWithValue("$day",date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));
        using var reader=cmd.ExecuteReader(); var total=0m;
        while(reader.Read())
        {
            var start=DateTime.Parse(reader.GetString(0)); var end=DateTime.Parse(reader.GetString(1));
            var hourlyRate=Convert.ToDecimal(reader.GetDouble(2));
            total+=reader.IsDBNull(3) ? CalculateCharge(start,end,hourlyRate) : Convert.ToDecimal(reader.GetDouble(3));
        }
        return total;
    }

    public IReadOnlyList<ClubNotification> GetNotifications()
    {
        using var db=Open(); using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT Id,Title,Message,CreatedAt,IsRead FROM Notifications ORDER BY CreatedAt DESC LIMIT 100;";
        using var reader=cmd.ExecuteReader(); var result=new List<ClubNotification>();
        while(reader.Read()) result.Add(new(reader.GetInt32(0),reader.GetString(1),reader.GetString(2),DateTime.Parse(reader.GetString(3)),reader.GetInt32(4)!=0));
        return result;
    }

    public bool AddSessionNotification(int sessionId, string kind, string title, string message)
    {
        using var db=Open(); using var cmd=db.CreateCommand();
        cmd.CommandText="INSERT OR IGNORE INTO Notifications(SessionId,Kind,Title,Message,CreatedAt) VALUES($session,$kind,$title,$message,$now);";
        cmd.Parameters.AddWithValue("$session",sessionId); cmd.Parameters.AddWithValue("$kind",kind);
        cmd.Parameters.AddWithValue("$title",title); cmd.Parameters.AddWithValue("$message",message); cmd.Parameters.AddWithValue("$now",DateTime.Now.ToString("O"));
        return cmd.ExecuteNonQuery()>0;
    }

    public void MarkNotificationRead(int id)
    {
        using var db=Open(); using var cmd=db.CreateCommand(); cmd.CommandText="UPDATE Notifications SET IsRead=1 WHERE Id=$id;";
        cmd.Parameters.AddWithValue("$id",id); cmd.ExecuteNonQuery();
    }

    public int AddClient(string name, string phone, string favoriteGame)
    {
        name = name.Trim();
        phone = phone.Trim();
        favoriteGame = favoriteGame.Trim();
        var phoneDigits = new string(phone.Where(char.IsDigit).ToArray());
        if (name.Length < 2 || phoneDigits.Length is < 7 or > 15 || string.IsNullOrWhiteSpace(favoriteGame))
            return 0;

        using var db = Open();
        using (var find = db.CreateCommand())
        {
            find.CommandText = "SELECT Phone FROM Clients;";
            using var reader = find.ExecuteReader();
            while (reader.Read())
            {
                var existingDigits = new string(reader.GetString(0).Where(char.IsDigit).ToArray());
                if (existingDigits == phoneDigits)
                    return 0;
            }
        }

        using var insert = db.CreateCommand();
        insert.CommandText = "INSERT INTO Clients(Name,Phone,Visits,FavoriteGame) VALUES($name,$phone,0,$game);";
        insert.Parameters.AddWithValue("$name", name);
        insert.Parameters.AddWithValue("$phone", phone);
        insert.Parameters.AddWithValue("$game", favoriteGame);
        insert.ExecuteNonQuery();
        using var id = db.CreateCommand();
        id.CommandText = "SELECT last_insert_rowid();";
        return Convert.ToInt32(id.ExecuteScalar());
    }

    public IReadOnlyList<TariffCard> GetTariffs()
    {
        using var db=Open(); using var cmd=db.CreateCommand(); cmd.CommandText="SELECT Id,Name,Description,PricePerHour,Badge FROM Tariffs ORDER BY PricePerHour;";
        using var r=cmd.ExecuteReader(); var list=new List<TariffCard>();
        while(r.Read()) list.Add(new(r.GetInt32(0),r.GetString(1),r.GetString(2),Convert.ToDecimal(r.GetDouble(3)),r.GetString(4)));
        return list;
    }
}
