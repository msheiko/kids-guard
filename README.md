# KidGuard

Служба Windows для родительского контроля доступа к ПК с управлением через Telegram-бота.
Спецификация — [SPEC-telegram.md](SPEC-telegram.md).

## Структура

| Проект | Назначение |
|---|---|
| `src/KidGuard.Core` | Логика без Windows и сети: правило доступа, таймер, расписание, лимиты, доверенное время, хранение |
| `src/KidGuard.Win32` | P/Invoke: учётная запись, сеансы WTS, запуск процесса в сеансе, права LSA, DPAPI |
| `src/KidGuard.Telegram` | Бот: команды, панель, авторизация и привязка, уведомления, команды с задержкой |
| `src/KidGuard.Service` | Служба Windows (.NET Worker Service) и CLI установки/обслуживания |
| `src/KidGuard.Tray` | Значок в сеансе ребёнка: предупреждения, сообщения, запрос времени |
| `tests/*` | Модульные тесты (xUnit) |

## Сборка и тесты

Нужен .NET 10 SDK.

```
dotnet test KidGuard.slnx
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```

`publish.ps1` кладёт `KidGuard.Service.exe` и `KidGuard.Tray.exe` в папку `publish\`.
Установка, привязка родителей, настройка и чек-лист приёмки — [docs/INSTALL.md](docs/INSTALL.md).
