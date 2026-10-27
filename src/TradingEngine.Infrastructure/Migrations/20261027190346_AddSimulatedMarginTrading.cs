using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSimulatedMarginTrading : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MarginPositions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Side = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: false),
                    Leverage = table.Column<int>(type: "integer", nullable: false),
                    EntryPrice = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: false),
                    Collateral = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    EntryFee = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    StopLoss = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: true),
                    TakeProfit = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: true),
                    LiquidationPrice = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ExitPrice = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: true),
                    RealizedPnl = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: true),
                    OpenedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarginPositions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MarginPositions_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MarginPositions_AccountId_IdempotencyKey",
                table: "MarginPositions",
                columns: new[] { "AccountId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarginPositions_AccountId_Status_OpenedAt",
                table: "MarginPositions",
                columns: new[] { "AccountId", "Status", "OpenedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MarginPositions_Symbol_Status",
                table: "MarginPositions",
                columns: new[] { "Symbol", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MarginPositions");
        }
    }
}
