# TradingEngine

Virtual spot trading simulator using public Binance market prices. Balances and orders are simulated; the application does not submit exchange orders or handle real funds.

## Current implementation

- .NET 9 API with separate Domain, Application, Infrastructure, and API projects.
- PostgreSQL schema/migrations for accounts, virtual assets, market/limit orders, ledger entries, refresh tokens, price alerts, notifications, and favorites.
- Email/password registration and login, password hashing, short-lived JWT access tokens, HttpOnly refresh cookies with rotation and reuse detection, and account roles/status.
- Configurable virtual starting credit and commission. Market orders use server-fetched prices; limit orders reserve funds/assets, match against the common live ticker feed, and release reserves on cancellation/fill.
- One shared Binance public WebSocket feed, published to authenticated SignalR market groups. Redis backs ticker cache and SignalR scale-out when `ConnectionStrings:Redis` is configured; local development uses in-memory cache/backplane.
- REST candle history, cached market tickers, wallet, portfolio, orders, trades, favorites, alerts, and notifications.
- Responsive React/TypeScript terminal with WebSocket-updated candles, SMA/EMA/RSI/MACD/Bollinger overlays/panes, limit orders/cancellation, price alerts, notifications, portfolio, and wallet.

Implemented admin scope includes user search, virtual credits, account suspension/reactivation, credit request review, notifications, and immutable-from-API audit records. Admin role assignment is deliberately not available through public registration: after creating the intended account, an operator must use controlled database access to set that account's `Role` to `Admin`. Example SQL: `UPDATE "Accounts" SET "Role" = 'Admin' WHERE "Email" = 'operator@example.com';` then sign in again to receive the Admin claim.

Email verification, password reset, alert edit/re-enable, richer admin reports/settings, and external push/email notifications are not implemented. REST polling remains for the broad 24-hour market list and historical candles; selected chart prices and execution matching use the shared WebSocket feed. This is a development foundation, not a production financial service.

## Run locally

Requirements: .NET 9 SDK, Node 22+, PostgreSQL 16+. Redis is optional locally; set `ConnectionStrings__Redis=localhost:6379` to use the Redis cache and SignalR backplane.

1. Start PostgreSQL and configure `ConnectionStrings__TradingDb` or update `src/TradingEngine.Api/appsettings.json`.
2. Set a local-only signing secret: `dotnet user-secrets --project src/TradingEngine.Api set "Jwt:SigningKey" "your-local-random-secret-at-least-32-bytes"`.
3. Apply the schema: `dotnet ef database update --project src/TradingEngine.Infrastructure --startup-project src/TradingEngine.Api`.
4. Start the API: `dotnet run --project src/TradingEngine.Api --urls http://localhost:5000`.
5. In another terminal: `cd frontend`, `npm ci`, `npm run dev`.

Set `VITE_API_URL` in `frontend/.env.local` if the API uses another address. OpenAPI JSON is available at `/openapi/v1.json` in Development.

## Docker

Copy `.env.example` to `.env`, replace the example secrets, then run `docker compose up --build`. The compose database is exposed on host port `55432`; apply the schema with `dotnet ef database update --project src/TradingEngine.Infrastructure --startup-project src/TradingEngine.Api --connection "Host=localhost;Port=55432;Database=tradingengine;Username=postgres;Password=replace-with-a-local-database-password"` (use the value in `POSTGRES_PASSWORD`). Redis is started alongside the API.

## Build and tests

```powershell
dotnet build TradingEngine.slnx
dotnet test TradingEngine.slnx
cd frontend; npm ci; npm run build
```

## Financial behavior

Only the API reads market prices for execution. Financial values use `decimal`; USD balances, asset quantities, orders, and append-only ledger rows are saved in a PostgreSQL transaction. Reusing an account-scoped idempotency key returns the recorded order. Every account receives the configured virtual credit once at registration. No endpoint lets a user directly edit their balance.
