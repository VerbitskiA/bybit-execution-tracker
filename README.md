# Bybit Execution Tracker

Консольное приложение на .NET 8 для отслеживания пользовательских сделок (execution events) с Bybit Linear Futures через WebSocket API v5.

## Возможности

- ✅ Авторизованное WebSocket подключение к Bybit Private Channel
- ✅ Получение потока execution events в реальном времени
- ✅ Автоматическое переподключение при разрыве соединения
- ✅ Дедупликация сделок по executionId
- ✅ Сохранение хронологического порядка получения сделок
- ✅ Graceful shutdown (Ctrl+C)
- ✅ Потокобезопасная обработка данных через Channel<T>

## Требования

- .NET 8 SDK
- API ключ и секрет от Bybit (с правами на чтение данных)

## Настройка

### 1. Клонирование репозитория

```bash
git clone <repository-url>
cd bybit-execution-tracker
```

### 2. Настройка API ключей

Создайте файл `appsettings.json` или используйте переменные окружения:

**Вариант 1: appsettings.json**
```json
{
  "Bybit": {
    "ApiKey": "your-api-key",
    "ApiSecret": "your-api-secret",
    "WebSocketUrl": "wss://stream.bybit.com/v5/private",
    "ReconnectDelayMs": 1000,
    "MaxReconnectDelayMs": 30000,
    "HeartbeatIntervalSeconds": 20
  }
}
```

**Вариант 2: Переменные окружения**
```bash
export BYBIT_APIKEY="your-api-key"
export BYBIT_APISECRET="your-api-secret"
```

### 3. Сборка и запуск

```bash
dotnet restore
dotnet build
dotnet run
```

## Использование

После запуска приложение:
1. Подключается к Bybit WebSocket
2. Авторизуется с использованием ваших API ключей
3. Подписывается на канал execution
4. Выводит информацию о каждой новой сделке в консоль

### Пример вывода

```
[2024-01-15 10:30:15] Execution ID: 123456789, Symbol: BTCUSDT, Side: Buy, Price: 42500.50, Qty: 0.1, Time: 2024-01-15T10:30:15.123Z
[2024-01-15 10:30:16] Execution ID: 123456790, Symbol: ETHUSDT, Side: Sell, Price: 2500.75, Qty: 2.5, Time: 2024-01-15T10:30:16.456Z
```

### Остановка

Нажмите `Ctrl+C` для graceful shutdown. Приложение корректно завершит все соединения и выведет оставшиеся события.

## Архитектура

Приложение использует следующие компоненты:

- **BybitWebSocketService** - управление WebSocket соединением, авторизация, подписка
- **ExecutionProcessor** - обработка execution events через Channel<T>, дедупликация, сортировка
- **Models** - модели данных для execution events и конфигурации

### Поток данных

```
WebSocket → BybitWebSocketService → Channel<ExecutionEvent> → ExecutionProcessor → Console
```

## Технические детали

- Использует только стандартные библиотеки .NET 8
- Потокобезопасность через `System.Threading.Channels`
- Дедупликация через `HashSet<string>`
- Сохранение порядка через `SortedSet<ExecutionEvent>`
- Экспоненциальная задержка при переподключении
- Обработка ping/pong для поддержания соединения

## Лицензия

MIT
