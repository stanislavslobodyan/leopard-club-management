# Встановлення та запуск LEOPARD у Windows

## Збірка з вихідного коду

Для розробки потрібні Windows 10/11, Git (якщо репозиторій ще не завантажено), .NET 10 SDK і доступ до NuGet під час першого відновлення залежностей. Avalonia окремо встановлювати не потрібно: це NuGet-залежності проєкту.

У **CMD**:

```cmd
git clone https://github.com/stanislavslobodyan/leopard-club-management.git
cd /d leopard-club-management
dotnet --version
dotnet restore LEOPARD.slnx
dotnet run --project src\LEOPARD\LEOPARD.csproj
```

Репозиторій приватний, тож для `git clone` потрібен доступ до GitHub-акаунта, якому його надали. Якщо проєкт уже є на комп’ютері, починайте з `cd /d "шлях-до-проєкту"`.

Щоб зібрати без запуску:

```cmd
dotnet build LEOPARD.slnx -c Release
```

## Запуск на ПК адміністратора без .NET

На комп’ютері розробки з .NET 10 SDK виконайте:

```cmd
dotnet publish src\LEOPARD\LEOPARD.csproj -c Release -r win-x64 --self-contained true -o publish\LEOPARD
```

Скопіюйте **всю** папку `publish\LEOPARD` на другий Windows x64 ПК, наприклад у `C:\LEOPARD`. На цільовому ПК не потрібні .NET SDK, .NET Runtime або встановлена Avalonia. Запустіть `LEOPARD.exe` у скопійованій папці:

```cmd
cd /d C:\LEOPARD
LEOPARD.exe
```

Не переносіть лише `.exe`: поруч є файли, потрібні застосунку. Для оновлення повторіть `dotnet publish` і замініть файли програми, залишивши файл бази даних цільового ПК недоторканим.

## Розгортання PC Agent на клубному ПК

На комп’ютері збірки створіть автономну збірку агента:

```cmd
dotnet publish src\LEOPARD.PcAgent\LEOPARD.PcAgent.csproj -c Release -r win-x64 --self-contained true -o publish\PC-Agent
```

Перенесіть усю папку `publish\PC-Agent` на клубний ПК і запустіть `LEOPARD.PcAgent.exe`. Після цього в картці відповідного комп’ютера вкажіть адресу `http://IP-АДРЕСА:47831`. Для постійної роботи агента його запуск після входу Windows треба налаштувати окремо через Планувальник завдань або службу.

Agent використовує HTTP без автентифікації. Дозволяйте доступ до порту 47831 тільки з довіреної приватної LAN; не відкривайте порт у публічний інтернет. Деталі — у [документації PC Agent](PC-AGENT-INTEGRATION.md).

## Файл даних

LEOPARD створює SQLite-базу в `%LOCALAPPDATA%\LEOPARD\club.sqlite3` під Windows-профілем, який запускає застосунок. На різних комп’ютерах це окремі бази; серверної синхронізації немає. Для резервного копіювання закрийте застосунок і скопіюйте файл. Повна процедура описана в [посібнику адміністратора](USER-GUIDE.md#резервна-копія-та-перенесення).
