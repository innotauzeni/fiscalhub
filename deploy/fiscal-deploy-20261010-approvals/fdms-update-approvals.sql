START TRANSACTION;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261009012815_AddReferrals') THEN

    ALTER TABLE `fdms_clients` ADD `ReferralCode` varchar(20) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261009012815_AddReferrals') THEN

    ALTER TABLE `fdms_clients` ADD `ReferredByClientId` char(36) COLLATE ascii_general_ci NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261009012815_AddReferrals') THEN

    ALTER TABLE `fdms_applications` ADD `ReferrerClientId` char(36) COLLATE ascii_general_ci NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261009012815_AddReferrals') THEN

    CREATE TABLE `fdms_referral_perks` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `TierKey` varchar(40) CHARACTER SET utf8mb4 NOT NULL,
        `Label` varchar(60) CHARACTER SET utf8mb4 NOT NULL,
        `FreeMonths` int NOT NULL,
        `GrantedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_referral_perks` PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261009012815_AddReferrals') THEN

    CREATE TABLE `fdms_referral_rewards` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ReferrerClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `ReferredClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `InvoiceId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Amount` decimal(18,2) NOT NULL,
        `Currency` varchar(3) CHARACTER SET utf8mb4 NOT NULL,
        `Percent` decimal(5,2) NOT NULL,
        `Status` varchar(10) CHARACTER SET utf8mb4 NOT NULL,
        `Reference` varchar(100) CHARACTER SET utf8mb4 NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `PaidAt` datetime(6) NULL,
        `PaidBy` varchar(200) CHARACTER SET utf8mb4 NULL,
        CONSTRAINT `PK_fdms_referral_rewards` PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261009012815_AddReferrals') THEN

    CREATE UNIQUE INDEX `IX_fdms_clients_ReferralCode` ON `fdms_clients` (`ReferralCode`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261009012815_AddReferrals') THEN

    CREATE UNIQUE INDEX `IX_fdms_referral_perks_ClientId_TierKey` ON `fdms_referral_perks` (`ClientId`, `TierKey`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261009012815_AddReferrals') THEN

    CREATE UNIQUE INDEX `IX_fdms_referral_rewards_ReferredClientId_InvoiceId` ON `fdms_referral_rewards` (`ReferredClientId`, `InvoiceId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261009012815_AddReferrals') THEN

    CREATE INDEX `IX_fdms_referral_rewards_ReferrerClientId` ON `fdms_referral_rewards` (`ReferrerClientId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261009012815_AddReferrals') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261009012815_AddReferrals', '8.0.11');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

COMMIT;

START TRANSACTION;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261010024633_AddReferralCoins') THEN

    ALTER TABLE `fdms_referral_rewards` ADD `Coins` int NOT NULL DEFAULT 0;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261010024633_AddReferralCoins') THEN

    ALTER TABLE `fdms_clients` ADD `BonusBranches` int NOT NULL DEFAULT 0;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261010024633_AddReferralCoins') THEN

    ALTER TABLE `fdms_clients` ADD `BonusDevices` int NOT NULL DEFAULT 0;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261010024633_AddReferralCoins') THEN

    ALTER TABLE `fdms_clients` ADD `BonusUsers` int NOT NULL DEFAULT 0;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261010024633_AddReferralCoins') THEN

    CREATE TABLE `fdms_coin_transactions` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Amount` int NOT NULL,
        `Kind` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
        `Description` varchar(300) CHARACTER SET utf8mb4 NOT NULL,
        `Reference` varchar(100) CHARACTER SET utf8mb4 NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `CreatedBy` varchar(200) CHARACTER SET utf8mb4 NULL,
        CONSTRAINT `PK_fdms_coin_transactions` PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261010024633_AddReferralCoins') THEN

    CREATE INDEX `IX_fdms_coin_transactions_ClientId_CreatedAt` ON `fdms_coin_transactions` (`ClientId`, `CreatedAt`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261010024633_AddReferralCoins') THEN

    CREATE UNIQUE INDEX `IX_fdms_coin_transactions_ClientId_Kind_Reference` ON `fdms_coin_transactions` (`ClientId`, `Kind`, `Reference`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261010024633_AddReferralCoins') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261010024633_AddReferralCoins', '8.0.11');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

COMMIT;

START TRANSACTION;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261010060607_AddApprovals') THEN

    CREATE TABLE `fdms_approval_decisions` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `RequestId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Level` int NOT NULL,
        `Role` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
        `UserId` varchar(64) CHARACTER SET utf8mb4 NOT NULL,
        `UserName` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
        `Decision` varchar(10) CHARACTER SET utf8mb4 NOT NULL,
        `Note` varchar(500) CHARACTER SET utf8mb4 NULL,
        `At` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_approval_decisions` PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261010060607_AddApprovals') THEN

    CREATE TABLE `fdms_approval_requests` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Action` varchar(30) CHARACTER SET utf8mb4 NOT NULL,
        `RuleId` char(36) COLLATE ascii_general_ci NULL,
        `LevelsJson` varchar(1000) CHARACTER SET utf8mb4 NOT NULL,
        `CurrentLevel` int NOT NULL,
        `Status` varchar(12) CHARACTER SET utf8mb4 NOT NULL,
        `Summary` varchar(300) CHARACTER SET utf8mb4 NOT NULL,
        `Amount` decimal(18,2) NOT NULL,
        `Currency` varchar(3) CHARACTER SET utf8mb4 NULL,
        `EntityId` varchar(64) CHARACTER SET utf8mb4 NULL,
        `PayloadJson` longtext CHARACTER SET utf8mb4 NOT NULL,
        `RequestedById` varchar(64) CHARACTER SET utf8mb4 NOT NULL,
        `RequestedByName` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `DecidedAt` datetime(6) NULL,
        `ResultJson` longtext CHARACTER SET utf8mb4 NULL,
        `Error` varchar(1000) CHARACTER SET utf8mb4 NULL,
        CONSTRAINT `PK_fdms_approval_requests` PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261010060607_AddApprovals') THEN

    CREATE TABLE `fdms_approval_rules` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Action` varchar(30) CHARACTER SET utf8mb4 NOT NULL,
        `MinAmount` decimal(18,2) NOT NULL,
        `Currency` varchar(3) CHARACTER SET utf8mb4 NULL,
        `LevelsJson` varchar(1000) CHARACTER SET utf8mb4 NOT NULL,
        `IsActive` tinyint(1) NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_approval_rules` PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261010060607_AddApprovals') THEN

    CREATE INDEX `IX_fdms_approval_decisions_RequestId` ON `fdms_approval_decisions` (`RequestId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261010060607_AddApprovals') THEN

    CREATE INDEX `IX_fdms_approval_requests_ClientId_Status_CreatedAt` ON `fdms_approval_requests` (`ClientId`, `Status`, `CreatedAt`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261010060607_AddApprovals') THEN

    CREATE INDEX `IX_fdms_approval_rules_ClientId_Action` ON `fdms_approval_rules` (`ClientId`, `Action`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261010060607_AddApprovals') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261010060607_AddApprovals', '8.0.11');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

COMMIT;

START TRANSACTION;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261010075853_AddSignupVerification') THEN

    ALTER TABLE `fdms_clients` ADD `KeepWhenUnpaid` tinyint(1) NOT NULL DEFAULT FALSE;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261010075853_AddSignupVerification') THEN

    ALTER TABLE `fdms_applications` ADD `UserName` varchar(64) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261010075853_AddSignupVerification') THEN

    ALTER TABLE `fdms_applications` ADD `VerifyAttempts` int NOT NULL DEFAULT 0;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261010075853_AddSignupVerification') THEN

    ALTER TABLE `fdms_applications` ADD `VerifyCodeHash` varchar(100) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261010075853_AddSignupVerification') THEN

    ALTER TABLE `fdms_applications` ADD `VerifyExpiresAt` datetime(6) NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261010075853_AddSignupVerification') THEN

    ALTER TABLE `fdms_applications` ADD `VerifySentAt` datetime(6) NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261010075853_AddSignupVerification') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261010075853_AddSignupVerification', '8.0.11');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

COMMIT;

START TRANSACTION;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261010164435_AddTemplateAssets') THEN

    CREATE TABLE `fdms_template_assets` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Name` varchar(120) CHARACTER SET utf8mb4 NULL,
        `ContentType` varchar(40) CHARACTER SET utf8mb4 NOT NULL,
        `Bytes` longblob NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_template_assets` PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261010164435_AddTemplateAssets') THEN

    CREATE INDEX `IX_fdms_template_assets_ClientId` ON `fdms_template_assets` (`ClientId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261010164435_AddTemplateAssets') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261010164435_AddTemplateAssets', '8.0.11');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

COMMIT;

START TRANSACTION;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261011012645_AddIntegrations') THEN

    CREATE TABLE `fdms_integration_connections` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Provider` varchar(30) CHARACTER SET utf8mb4 NOT NULL,
        `Status` varchar(30) CHARACTER SET utf8mb4 NOT NULL,
        `AccountsServer` varchar(120) CHARACTER SET utf8mb4 NOT NULL,
        `ApiDomain` varchar(120) CHARACTER SET utf8mb4 NOT NULL,
        `OrganizationId` varchar(40) CHARACTER SET utf8mb4 NULL,
        `OrganizationName` varchar(200) CHARACTER SET utf8mb4 NULL,
        `OrganizationsJson` longtext CHARACTER SET utf8mb4 NULL,
        `RefreshTokenProtected` varchar(2000) CHARACTER SET utf8mb4 NULL,
        `AccessTokenProtected` varchar(2000) CHARACTER SET utf8mb4 NULL,
        `AccessTokenExpiresAt` datetime(6) NULL,
        `LastTokenRefreshAt` datetime(6) NULL,
        `ConnectedBy` varchar(200) CHARACTER SET utf8mb4 NULL,
        `ConnectedAt` datetime(6) NOT NULL,
        `LastSyncAt` datetime(6) NULL,
        `SyncRequestedAt` datetime(6) NULL,
        `LastError` varchar(1000) CHARACTER SET utf8mb4 NULL,
        `LastErrorAt` datetime(6) NULL,
        `SyncCursor` datetime(6) NULL,
        `AutoFiscalise` tinyint(1) NOT NULL,
        `DeviceId` char(36) COLLATE ascii_general_ci NULL,
        `DefaultHsCode` varchar(20) CHARACTER SET utf8mb4 NULL,
        `DefaultMoneyType` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
        `PostBack` tinyint(1) NOT NULL,
        `StartDate` datetime(6) NULL,
        `WebhookKey` varchar(64) CHARACTER SET utf8mb4 NOT NULL,
        `ApiCallsDay` datetime(6) NULL,
        `ApiCallsToday` int NOT NULL,
        `ApiCallsTotal` bigint NOT NULL,
        CONSTRAINT `PK_fdms_integration_connections` PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261011012645_AddIntegrations') THEN

    CREATE TABLE `fdms_integration_documents` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `ConnectionId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Kind` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
        `ExternalId` varchar(60) CHARACTER SET utf8mb4 NOT NULL,
        `ExternalNumber` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
        `CustomerName` varchar(200) CHARACTER SET utf8mb4 NULL,
        `DocumentDate` datetime(6) NULL,
        `Total` decimal(19,4) NOT NULL,
        `Currency` varchar(10) CHARACTER SET utf8mb4 NOT NULL,
        `ExternalStatus` varchar(40) CHARACTER SET utf8mb4 NULL,
        `ExternalModifiedAt` datetime(6) NULL,
        `Status` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
        `ReceiptId` char(36) COLLATE ascii_general_ci NULL,
        `Error` varchar(1000) CHARACTER SET utf8mb4 NULL,
        `Attempts` int NOT NULL,
        `PostedBackAt` datetime(6) NULL,
        `PostBackError` varchar(1000) CHARACTER SET utf8mb4 NULL,
        `ChangedAfterFiscalised` tinyint(1) NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `FiscalisedAt` datetime(6) NULL,
        CONSTRAINT `PK_fdms_integration_documents` PRIMARY KEY (`Id`),
        CONSTRAINT `FK_fdms_integration_documents_fdms_integration_connections_Conn~` FOREIGN KEY (`ConnectionId`) REFERENCES `fdms_integration_connections` (`Id`) ON DELETE CASCADE
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261011012645_AddIntegrations') THEN

    CREATE UNIQUE INDEX `IX_fdms_integration_connections_ClientId_Provider` ON `fdms_integration_connections` (`ClientId`, `Provider`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261011012645_AddIntegrations') THEN

    CREATE INDEX `IX_fdms_integration_documents_ClientId_Status` ON `fdms_integration_documents` (`ClientId`, `Status`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261011012645_AddIntegrations') THEN

    CREATE UNIQUE INDEX `IX_fdms_integration_documents_ConnectionId_Kind_ExternalId` ON `fdms_integration_documents` (`ConnectionId`, `Kind`, `ExternalId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261011012645_AddIntegrations') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261011012645_AddIntegrations', '8.0.11');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

COMMIT;

START TRANSACTION;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261011021131_AddZohoBridge') THEN

    ALTER TABLE `fdms_integration_connections` ADD `BridgeCursorsJson` varchar(4000) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261011021131_AddZohoBridge') THEN

    ALTER TABLE `fdms_integration_connections` ADD `BridgeWatchAt` datetime(6) NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261011021131_AddZohoBridge') THEN

    ALTER TABLE `fdms_integration_connections` ADD `BridgeWatchRequested` tinyint(1) NOT NULL DEFAULT FALSE;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261011021131_AddZohoBridge') THEN

    ALTER TABLE `fdms_integration_connections` ADD `FullAccess` tinyint(1) NOT NULL DEFAULT FALSE;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261011021131_AddZohoBridge') THEN

    CREATE TABLE `fdms_bridge_endpoints` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `PartnerId` char(36) COLLATE ascii_general_ci NOT NULL,
        `WebhookUrl` varchar(500) CHARACTER SET utf8mb4 NULL,
        `SecretProtected` varchar(1000) CHARACTER SET utf8mb4 NULL,
        `WatchModules` varchar(500) CHARACTER SET utf8mb4 NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_bridge_endpoints` PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261011021131_AddZohoBridge') THEN

    CREATE TABLE `fdms_bridge_events` (
        `Id` bigint NOT NULL AUTO_INCREMENT,
        `PartnerId` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Module` varchar(40) CHARACTER SET utf8mb4 NOT NULL,
        `RecordId` varchar(60) CHARACTER SET utf8mb4 NOT NULL,
        `EventType` varchar(80) CHARACTER SET utf8mb4 NOT NULL,
        `PayloadJson` longtext CHARACTER SET utf8mb4 NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `Status` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
        `Attempts` int NOT NULL,
        `NextAttemptAt` datetime(6) NOT NULL,
        `DeliveredAt` datetime(6) NULL,
        `LastError` varchar(500) CHARACTER SET utf8mb4 NULL,
        CONSTRAINT `PK_fdms_bridge_events` PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261011021131_AddZohoBridge') THEN

    CREATE TABLE `fdms_bridge_grants` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `PartnerId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Modules` varchar(500) CHARACTER SET utf8mb4 NOT NULL,
        `CanWrite` tinyint(1) NOT NULL,
        `IsActive` tinyint(1) NOT NULL,
        `CallsMonth` datetime(6) NULL,
        `CallsThisMonth` bigint NOT NULL,
        `CreatedBy` varchar(200) CHARACTER SET utf8mb4 NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `RevokedAt` datetime(6) NULL,
        CONSTRAINT `PK_fdms_bridge_grants` PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261011021131_AddZohoBridge') THEN

    CREATE TABLE `fdms_bridge_keys` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `PartnerId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Name` varchar(80) CHARACTER SET utf8mb4 NOT NULL,
        `Prefix` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
        `KeyHash` varchar(64) CHARACTER SET utf8mb4 NOT NULL,
        `CreatedBy` varchar(200) CHARACTER SET utf8mb4 NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `LastUsedAt` datetime(6) NULL,
        `RevokedAt` datetime(6) NULL,
        `CallsDay` datetime(6) NULL,
        `CallsToday` int NOT NULL,
        `CallsTotal` bigint NOT NULL,
        CONSTRAINT `PK_fdms_bridge_keys` PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261011021131_AddZohoBridge') THEN

    CREATE UNIQUE INDEX `IX_fdms_bridge_endpoints_PartnerId` ON `fdms_bridge_endpoints` (`PartnerId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261011021131_AddZohoBridge') THEN

    CREATE INDEX `IX_fdms_bridge_events_ClientId` ON `fdms_bridge_events` (`ClientId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261011021131_AddZohoBridge') THEN

    CREATE INDEX `IX_fdms_bridge_events_PartnerId_Id` ON `fdms_bridge_events` (`PartnerId`, `Id`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261011021131_AddZohoBridge') THEN

    CREATE INDEX `IX_fdms_bridge_events_Status_NextAttemptAt` ON `fdms_bridge_events` (`Status`, `NextAttemptAt`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261011021131_AddZohoBridge') THEN

    CREATE INDEX `IX_fdms_bridge_grants_ClientId_PartnerId` ON `fdms_bridge_grants` (`ClientId`, `PartnerId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261011021131_AddZohoBridge') THEN

    CREATE INDEX `IX_fdms_bridge_grants_PartnerId` ON `fdms_bridge_grants` (`PartnerId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261011021131_AddZohoBridge') THEN

    CREATE UNIQUE INDEX `IX_fdms_bridge_keys_KeyHash` ON `fdms_bridge_keys` (`KeyHash`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261011021131_AddZohoBridge') THEN

    CREATE INDEX `IX_fdms_bridge_keys_PartnerId` ON `fdms_bridge_keys` (`PartnerId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261011021131_AddZohoBridge') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261011021131_AddZohoBridge', '8.0.11');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

COMMIT;

START TRANSACTION;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261011025356_AddZohoOwnApp') THEN

    ALTER TABLE `fdms_integration_connections` ADD `OwnClientId` longtext CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261011025356_AddZohoOwnApp') THEN

    ALTER TABLE `fdms_integration_connections` ADD `OwnClientSecretProtected` longtext CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261011025356_AddZohoOwnApp') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261011025356_AddZohoOwnApp', '8.0.11');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

COMMIT;

