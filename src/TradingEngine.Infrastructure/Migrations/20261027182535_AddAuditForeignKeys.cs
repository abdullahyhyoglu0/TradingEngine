using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_AdminAuditLogs_AdminAccountId",
                table: "AdminAuditLogs",
                column: "AdminAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_AdminAuditLogs_Accounts_AdminAccountId",
                table: "AdminAuditLogs",
                column: "AdminAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AdminAuditLogs_Accounts_AffectedAccountId",
                table: "AdminAuditLogs",
                column: "AffectedAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CreditRequests_Accounts_AccountId",
                table: "CreditRequests",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Favorites_Accounts_AccountId",
                table: "Favorites",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Notifications_Accounts_AccountId",
                table: "Notifications",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AdminAuditLogs_Accounts_AdminAccountId",
                table: "AdminAuditLogs");

            migrationBuilder.DropForeignKey(
                name: "FK_AdminAuditLogs_Accounts_AffectedAccountId",
                table: "AdminAuditLogs");

            migrationBuilder.DropForeignKey(
                name: "FK_CreditRequests_Accounts_AccountId",
                table: "CreditRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_Favorites_Accounts_AccountId",
                table: "Favorites");

            migrationBuilder.DropForeignKey(
                name: "FK_Notifications_Accounts_AccountId",
                table: "Notifications");

            migrationBuilder.DropIndex(
                name: "IX_AdminAuditLogs_AdminAccountId",
                table: "AdminAuditLogs");
        }
    }
}
