# TradingEngine

**A Real-Time Cryptocurrency Trading Simulation Engine**

TradingEngine is a software project focused on building a cryptocurrency trading simulation platform using real market data, backend services, and a modular architecture.

The long-term goal is to provide a realistic paper-trading experience where users can trade cryptocurrencies with virtual funds, monitor market prices, track portfolio performance, and explore trading activity without risking real money.

> **Project status:** Under active development.

## Overview

TradingEngine aims to combine market data processing, trading operations, portfolio management, and real-time updates into a modular trading simulation system.

The project is designed with separation of concerns in mind, making individual components easier to develop, test, and maintain.

## Planned Features

- **Real Market Data** — Retrieve and process cryptocurrency market prices.
- **Virtual Trading** — Simulate buy and sell orders without real-money transactions.
- **Portfolio Management** — Track virtual balances, assets, and portfolio performance.
- **Trading Engine** — Build the core components responsible for processing trading operations.
- **Real-Time Updates** — Deliver market and trading updates to connected clients.
- **Market Charts** — Visualize cryptocurrency price movements and historical data.
- **Order Management** — Track order states and trading activity.
- **Testing** — Support unit and integration testing of backend components.

*Features are planned or under development unless explicitly implemented in the current codebase.*

## Architecture

The repository is organized into separate projects to support modular development.

```text
TradingEngine/
├── src/
│   ├── TradingEngine.Domain/
│   ├── TradingEngine.Application/
│   ├── TradingEngine.Infrastructure/
│   ├── TradingEngine.MarketData/
│   ├── TradingEngine.MatchingEngine/
│   └── TradingEngine.Realtime/
├── tests/
│   ├── TradingEngine.UnitTests/
│   └── TradingEngine.IntegrationTests/
├── frontend/
├── docs/
├── docker-compose.yml
├── .dockerignore
├── .gitignore
└── TradingEngine.slnx
```

### Project Responsibilities

| Project | Responsibility |
|---|---|
| `TradingEngine.Domain` | Core domain models and business rules |
| `TradingEngine.Application` | Application use cases and orchestration |
| `TradingEngine.Infrastructure` | External integrations and persistence |
| `TradingEngine.MarketData` | Market data processing services |
| `TradingEngine.MatchingEngine` | Core matching and trading engine components |
| `TradingEngine.Realtime` | Real-time communication components |
| `TradingEngine.UnitTests` | Unit tests |
| `TradingEngine.IntegrationTests` | Integration tests |

The architecture and responsibilities may evolve as the project develops.

## Technology Stack

- **C# / .NET 9**
- **ASP.NET Core ecosystem**
- **Docker & Docker Compose**
- **PostgreSQL**
- **Real-time communication technologies**
- **Frontend application**
- **Unit and integration testing**

## Getting Started

### Prerequisites

Install the following tools:

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Git](https://git-scm.com/)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/)

A PostgreSQL instance may be required depending on the selected configuration.

### 1. Clone the repository

```bash
git clone https://github.com/abdullahyhyoglu0/TradingEngine.git
cd TradingEngine
```

### 2. Configure environment variables

Create your local environment configuration based on `.env.example`.

Use your own development secrets and database credentials. Never commit real credentials, API keys, or signing keys to version control.

Example configuration variables:

```dotenv
JWT_SIGNING_KEY=your-local-development-secret
POSTGRES_PASSWORD=your-local-database-password
Trading__StartingBalance=10000
Trading__FeeRate=0.001
```

These values are examples, not production credentials. Check the current Docker Compose and application configuration to determine which variables are required.

### 3. Start the infrastructure

If the Docker Compose configuration is ready for local development, start the configured services with:

```bash
docker compose up -d --build
```

### 4. Build the solution

```bash
dotnet restore
dotnet build
```

### 5. Run the tests

```bash
dotnet test
```

These commands provide a starting point for local development. Required configuration, service startup order, and test coverage may change as implementation progresses.

## Configuration

The project uses configuration for settings such as:

- JWT signing configuration
- Database connection details
- Initial virtual trading balance
- Trading fee rate
- Frontend CORS origins

Keep local secrets outside the repository. The committed `.env.example` file should contain placeholders only.

## Development Roadmap

- [ ] Establish the core domain and application layers
- [ ] Implement market data ingestion
- [ ] Develop trading and order-processing logic
- [ ] Add virtual wallet and portfolio management
- [ ] Integrate real-time market updates
- [ ] Build the frontend trading interface
- [ ] Expand unit and integration test coverage
- [ ] Improve deployment, monitoring, and documentation

## Contributing

This project is under active development. Suggestions, bug reports, and constructive feedback are welcome.

1. Fork the repository.
2. Create a feature branch.
3. Make your changes and add tests where appropriate.
4. Submit a pull request describing your changes.

## Disclaimer

TradingEngine is intended for educational and software development purposes. It is a trading simulation project, not a cryptocurrency exchange or financial service. Virtual trading results do not represent guaranteed real-world performance.

## License

No license has been specified yet. Until a license is added, the repository should not be assumed to grant permission to reuse, modify, or distribute its code.
