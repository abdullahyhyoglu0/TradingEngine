CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;
CREATE TABLE "Accounts" (
    "Id" uuid NOT NULL,
    "Email" character varying(320) NOT NULL,
    "Username" character varying(32) NOT NULL,
    "PasswordHash" text NOT NULL,
    "Role" text NOT NULL,
    "Status" integer NOT NULL,
    "UsdBalance" numeric(28,8) NOT NULL,
    "StartingCredit" numeric(28,8) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "LastLoginAt" timestamp with time zone,
    CONSTRAINT "PK_Accounts" PRIMARY KEY ("Id")
);

CREATE TABLE "Favorites" (
    "Id" uuid NOT NULL,
    "AccountId" uuid NOT NULL,
    "Symbol" character varying(32) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_Favorites" PRIMARY KEY ("Id")
);

CREATE TABLE "Assets" (
    "Id" uuid NOT NULL,
    "AccountId" uuid NOT NULL,
    "Asset" text NOT NULL,
    "Quantity" numeric(28,12) NOT NULL,
    "AverageCost" numeric(28,12) NOT NULL,
    CONSTRAINT "PK_Assets" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_Assets_Accounts_AccountId" FOREIGN KEY ("AccountId") REFERENCES "Accounts" ("Id") ON DELETE CASCADE
);

CREATE TABLE "Ledger" (
    "Id" uuid NOT NULL,
    "AccountId" uuid NOT NULL,
    "Type" integer NOT NULL,
    "Amount" numeric(28,8) NOT NULL,
    "BalanceAfter" numeric(28,8) NOT NULL,
    "Currency" character varying(12) NOT NULL,
    "Description" character varying(500) NOT NULL,
    "OrderId" uuid,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_Ledger" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_Ledger_Accounts_AccountId" FOREIGN KEY ("AccountId") REFERENCES "Accounts" ("Id") ON DELETE CASCADE
);

CREATE TABLE "Orders" (
    "Id" uuid NOT NULL,
    "AccountId" uuid NOT NULL,
    "Symbol" character varying(32) NOT NULL,
    "BaseAsset" character varying(16) NOT NULL,
    "Side" integer NOT NULL,
    "Type" integer NOT NULL,
    "Status" integer NOT NULL,
    "Quantity" numeric(28,12) NOT NULL,
    "LimitPrice" numeric(28,12),
    "FillPrice" numeric(28,12),
    "Fee" numeric(28,8) NOT NULL,
    "IdempotencyKey" character varying(128) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "FilledAt" timestamp with time zone,
    CONSTRAINT "PK_Orders" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_Orders_Accounts_AccountId" FOREIGN KEY ("AccountId") REFERENCES "Accounts" ("Id") ON DELETE CASCADE
);

CREATE UNIQUE INDEX "IX_Accounts_Email" ON "Accounts" ("Email");

CREATE UNIQUE INDEX "IX_Accounts_Username" ON "Accounts" ("Username");

CREATE UNIQUE INDEX "IX_Assets_AccountId_Asset" ON "Assets" ("AccountId", "Asset");

CREATE UNIQUE INDEX "IX_Favorites_AccountId_Symbol" ON "Favorites" ("AccountId", "Symbol");

CREATE INDEX "IX_Ledger_AccountId_CreatedAt" ON "Ledger" ("AccountId", "CreatedAt");

CREATE UNIQUE INDEX "IX_Orders_AccountId_IdempotencyKey" ON "Orders" ("AccountId", "IdempotencyKey");

CREATE INDEX "IX_Orders_Status_Symbol" ON "Orders" ("Status", "Symbol");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261027181606_InitialTradingSchema', '9.0.1');

CREATE TABLE "AdminAuditLogs" (
    "Id" uuid NOT NULL,
    "AdminAccountId" uuid NOT NULL,
    "AffectedAccountId" uuid NOT NULL,
    "Action" character varying(80) NOT NULL,
    "PreviousBalance" numeric(28,8),
    "NewBalance" numeric(28,8),
    "Amount" numeric(28,8),
    "Reason" character varying(500) NOT NULL,
    "RelatedCreditRequestId" uuid,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_AdminAuditLogs" PRIMARY KEY ("Id")
);

CREATE TABLE "CreditRequests" (
    "Id" uuid NOT NULL,
    "RequestNumber" character varying(24) NOT NULL,
    "AccountId" uuid NOT NULL,
    "Amount" numeric(28,8) NOT NULL,
    "Description" character varying(500) NOT NULL,
    "Status" integer NOT NULL,
    "AdminResponse" character varying(500),
    "ReviewedByAdminId" uuid,
    "CreatedAt" timestamp with time zone NOT NULL,
    "ReviewedAt" timestamp with time zone,
    CONSTRAINT "PK_CreditRequests" PRIMARY KEY ("Id")
);

CREATE TABLE "Notifications" (
    "Id" uuid NOT NULL,
    "AccountId" uuid NOT NULL,
    "Type" character varying(80) NOT NULL,
    "Message" character varying(500) NOT NULL,
    "IsRead" boolean NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_Notifications" PRIMARY KEY ("Id")
);

CREATE INDEX "IX_AdminAuditLogs_AffectedAccountId_CreatedAt" ON "AdminAuditLogs" ("AffectedAccountId", "CreatedAt");

CREATE INDEX "IX_CreditRequests_AccountId_CreatedAt" ON "CreditRequests" ("AccountId", "CreatedAt");

CREATE UNIQUE INDEX "IX_CreditRequests_RequestNumber" ON "CreditRequests" ("RequestNumber");

CREATE INDEX "IX_Notifications_AccountId_CreatedAt" ON "Notifications" ("AccountId", "CreatedAt");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261027182247_AddVirtualCreditAndAdminAudit', '9.0.1');

CREATE INDEX "IX_AdminAuditLogs_AdminAccountId" ON "AdminAuditLogs" ("AdminAccountId");

ALTER TABLE "AdminAuditLogs" ADD CONSTRAINT "FK_AdminAuditLogs_Accounts_AdminAccountId" FOREIGN KEY ("AdminAccountId") REFERENCES "Accounts" ("Id") ON DELETE RESTRICT;

ALTER TABLE "AdminAuditLogs" ADD CONSTRAINT "FK_AdminAuditLogs_Accounts_AffectedAccountId" FOREIGN KEY ("AffectedAccountId") REFERENCES "Accounts" ("Id") ON DELETE RESTRICT;

ALTER TABLE "CreditRequests" ADD CONSTRAINT "FK_CreditRequests_Accounts_AccountId" FOREIGN KEY ("AccountId") REFERENCES "Accounts" ("Id") ON DELETE RESTRICT;

ALTER TABLE "Favorites" ADD CONSTRAINT "FK_Favorites_Accounts_AccountId" FOREIGN KEY ("AccountId") REFERENCES "Accounts" ("Id") ON DELETE CASCADE;

ALTER TABLE "Notifications" ADD CONSTRAINT "FK_Notifications_Accounts_AccountId" FOREIGN KEY ("AccountId") REFERENCES "Accounts" ("Id") ON DELETE CASCADE;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261027182535_AddAuditForeignKeys', '9.0.1');

ALTER TABLE "Orders" ADD "RealizedPnl" numeric(28,8) NOT NULL DEFAULT 0.0;

ALTER TABLE "Orders" ADD "ReservedAmount" numeric(28,8) NOT NULL DEFAULT 0.0;

ALTER TABLE "Assets" ADD "ReservedQuantity" numeric(28,12) NOT NULL DEFAULT 0.0;

CREATE TABLE "PriceAlerts" (
    "Id" uuid NOT NULL,
    "AccountId" uuid NOT NULL,
    "Symbol" character varying(32) NOT NULL,
    "Condition" integer NOT NULL,
    "TargetPrice" numeric(28,12) NOT NULL,
    "IsEnabled" boolean NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "TriggeredAt" timestamp with time zone,
    CONSTRAINT "PK_PriceAlerts" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_PriceAlerts_Accounts_AccountId" FOREIGN KEY ("AccountId") REFERENCES "Accounts" ("Id") ON DELETE CASCADE
);

CREATE TABLE "RefreshTokens" (
    "Id" uuid NOT NULL,
    "AccountId" uuid NOT NULL,
    "TokenHash" character varying(64) NOT NULL,
    "FamilyId" uuid NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "ExpiresAt" timestamp with time zone NOT NULL,
    "RevokedAt" timestamp with time zone,
    "ReplacedById" uuid,
    CONSTRAINT "PK_RefreshTokens" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_RefreshTokens_Accounts_AccountId" FOREIGN KEY ("AccountId") REFERENCES "Accounts" ("Id") ON DELETE CASCADE
);

CREATE INDEX "IX_PriceAlerts_AccountId_CreatedAt" ON "PriceAlerts" ("AccountId", "CreatedAt");

CREATE INDEX "IX_PriceAlerts_Symbol_IsEnabled_TriggeredAt" ON "PriceAlerts" ("Symbol", "IsEnabled", "TriggeredAt");

CREATE INDEX "IX_RefreshTokens_AccountId_FamilyId" ON "RefreshTokens" ("AccountId", "FamilyId");

CREATE UNIQUE INDEX "IX_RefreshTokens_TokenHash" ON "RefreshTokens" ("TokenHash");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261027183336_AddAdvancedTradingRealtime', '9.0.1');

COMMIT;

