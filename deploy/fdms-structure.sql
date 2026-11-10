CREATE TABLE IF NOT EXISTS `__EFMigrationsHistory` (
    `MigrationId` varchar(150) CHARACTER SET utf8mb4 NOT NULL,
    `ProductVersion` varchar(32) CHARACTER SET utf8mb4 NOT NULL,
    CONSTRAINT `PK___EFMigrationsHistory` PRIMARY KEY (`MigrationId`)
) CHARACTER SET=utf8mb4;

START TRANSACTION;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    ALTER DATABASE CHARACTER SET utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE TABLE `fdms_apikeys` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Name` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
        `KeyPrefix` varchar(16) CHARACTER SET utf8mb4 NOT NULL,
        `KeyHash` varchar(64) CHARACTER SET utf8mb4 NOT NULL,
        `DeviceId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Scopes` varchar(500) CHARACTER SET utf8mb4 NOT NULL,
        `CreatedByUserId` varchar(450) CHARACTER SET utf8mb4 NULL,
        `ExpiresAt` datetime(6) NULL,
        `RevokedAt` datetime(6) NULL,
        `LastUsedAt` datetime(6) NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_apikeys` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE TABLE `fdms_auditlogs` (
        `Id` bigint NOT NULL AUTO_INCREMENT,
        `ClientId` char(36) COLLATE ascii_general_ci NULL,
        `ApiKeyId` char(36) COLLATE ascii_general_ci NULL,
        `UserId` varchar(450) CHARACTER SET utf8mb4 NULL,
        `UserName` varchar(200) CHARACTER SET utf8mb4 NULL,
        `Action` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
        `Detail` varchar(2000) CHARACTER SET utf8mb4 NULL,
        `At` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_auditlogs` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE TABLE `fdms_clients` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `Name` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
        `Email` varchar(200) CHARACTER SET utf8mb4 NULL,
        `IsActive` tinyint(1) NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `WebhookUrl` varchar(500) CHARACTER SET utf8mb4 NULL,
        `WebhookSecretProtected` text CHARACTER SET utf8mb4 NULL,
        CONSTRAINT `PK_fdms_clients` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE TABLE `fdms_devices` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `ZimraDeviceId` int NOT NULL,
        `Environment` int NOT NULL,
        `SerialNo` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
        `ModelName` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
        `ModelVersion` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
        `CertificatePem` text CHARACTER SET utf8mb4 NOT NULL,
        `PrivateKeyProtected` text CHARACTER SET utf8mb4 NOT NULL,
        `CertificateValidTill` datetime(6) NULL,
        `OperatingMode` int NOT NULL,
        `QrUrl` varchar(200) CHARACTER SET utf8mb4 NULL,
        `TaxPayerName` varchar(250) CHARACTER SET utf8mb4 NULL,
        `TaxPayerTin` varchar(20) CHARACTER SET utf8mb4 NULL,
        `VatNumber` varchar(20) CHARACTER SET utf8mb4 NULL,
        `BranchName` varchar(250) CHARACTER SET utf8mb4 NULL,
        `DayMaxHrs` int NOT NULL,
        `TaxesJson` text CHARACTER SET utf8mb4 NULL,
        `ConfigRefreshedAt` datetime(6) NULL,
        `LastReceiptGlobalNo` bigint NOT NULL,
        `IsActive` tinyint(1) NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_devices` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE TABLE `fdms_fiscaldays` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `DeviceId` char(36) COLLATE ascii_general_ci NOT NULL,
        `FiscalDayNo` int NOT NULL,
        `Status` int NOT NULL,
        `OpenedAt` datetime(6) NOT NULL,
        `ClosedAt` datetime(6) NULL,
        `LastReceiptCounter` int NOT NULL,
        `LastReceiptHash` varchar(64) CHARACTER SET utf8mb4 NULL,
        `LastReceiptDate` datetime(6) NULL,
        `ClosingErrorCode` varchar(50) CHARACTER SET utf8mb4 NULL,
        `IsOffline` tinyint(1) NOT NULL,
        CONSTRAINT `PK_fdms_fiscaldays` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE TABLE `fdms_offlinefiles` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `DeviceId` char(36) COLLATE ascii_general_ci NOT NULL,
        `FiscalDayNo` int NOT NULL,
        `Sequence` int NOT NULL,
        `ReceiptCount` int NOT NULL,
        `HasFooter` tinyint(1) NOT NULL,
        `Status` int NOT NULL,
        `ContentBase64` longtext CHARACTER SET utf8mb4 NOT NULL,
        `OperationId` varchar(80) CHARACTER SET utf8mb4 NULL,
        `ErrorsJson` text CHARACTER SET utf8mb4 NULL,
        `LastError` varchar(500) CHARACTER SET utf8mb4 NULL,
        `Attempts` int NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `UploadedAt` datetime(6) NULL,
        `ProcessedAt` datetime(6) NULL,
        CONSTRAINT `PK_fdms_offlinefiles` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE TABLE `fdms_permissions` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `Name` varchar(80) CHARACTER SET utf8mb4 NOT NULL,
        `DisplayName` varchar(150) CHARACTER SET utf8mb4 NOT NULL,
        `Module` varchar(60) CHARACTER SET utf8mb4 NOT NULL,
        `IsActive` tinyint(1) NOT NULL,
        CONSTRAINT `PK_fdms_permissions` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE TABLE `fdms_receipts` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `DeviceId` char(36) COLLATE ascii_general_ci NOT NULL,
        `FiscalDayId` int NOT NULL,
        `FiscalDayNo` int NOT NULL,
        `ExternalRef` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
        `InvoiceNo` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
        `Type` int NOT NULL,
        `Currency` varchar(3) CHARACTER SET utf8mb4 NOT NULL,
        `ReceiptCounter` int NOT NULL,
        `ReceiptGlobalNo` bigint NOT NULL,
        `ReceiptDate` datetime(6) NOT NULL,
        `Total` decimal(21,2) NOT NULL,
        `OriginalReceiptId` char(36) COLLATE ascii_general_ci NULL,
        `RequestJson` longtext CHARACTER SET utf8mb4 NULL,
        `HoldUntil` datetime(6) NULL,
        `OfflineFileId` int NULL,
        `Status` int NOT NULL,
        `Hash` varchar(64) CHARACTER SET utf8mb4 NOT NULL,
        `Signature` text CHARACTER SET utf8mb4 NOT NULL,
        `PayloadJson` longtext CHARACTER SET utf8mb4 NOT NULL,
        `FdmsReceiptId` bigint NULL,
        `ServerDate` datetime(6) NULL,
        `ServerSignatureJson` text CHARACTER SET utf8mb4 NULL,
        `ValidationErrorsJson` text CHARACTER SET utf8mb4 NULL,
        `QrUrl` varchar(300) CHARACTER SET utf8mb4 NULL,
        `ErrorCode` varchar(50) CHARACTER SET utf8mb4 NULL,
        `ErrorMessage` varchar(1000) CHARACTER SET utf8mb4 NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_receipts` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE TABLE `fdms_refreshtokens` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `UserId` varchar(450) CHARACTER SET utf8mb4 NOT NULL,
        `TokenHash` varchar(64) CHARACTER SET utf8mb4 NOT NULL,
        `ExpiresAt` datetime(6) NOT NULL,
        `IsRevoked` tinyint(1) NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_refreshtokens` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE TABLE `fdms_roles` (
        `Id` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
        `Type` varchar(20) CHARACTER SET utf8mb4 NULL,
        `Level` varchar(20) CHARACTER SET utf8mb4 NULL,
        `Description` varchar(300) CHARACTER SET utf8mb4 NULL,
        `Name` varchar(256) CHARACTER SET utf8mb4 NULL,
        `NormalizedName` varchar(256) CHARACTER SET utf8mb4 NULL,
        `ConcurrencyStamp` longtext CHARACTER SET utf8mb4 NULL,
        CONSTRAINT `PK_fdms_roles` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE TABLE `fdms_sectionmodules` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `Name` varchar(80) CHARACTER SET utf8mb4 NOT NULL,
        `Color` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
        `SortOrder` int NOT NULL,
        `Status` tinyint(1) NOT NULL,
        CONSTRAINT `PK_fdms_sectionmodules` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE TABLE `fdms_users` (
        `Id` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
        `Name` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
        `Surname` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NULL,
        `Status` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
        `MustChangePassword` tinyint(1) NOT NULL,
        `LastLoginDate` datetime(6) NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `UserName` varchar(256) CHARACTER SET utf8mb4 NULL,
        `NormalizedUserName` varchar(256) CHARACTER SET utf8mb4 NULL,
        `Email` varchar(256) CHARACTER SET utf8mb4 NULL,
        `NormalizedEmail` varchar(256) CHARACTER SET utf8mb4 NULL,
        `EmailConfirmed` tinyint(1) NOT NULL,
        `PasswordHash` longtext CHARACTER SET utf8mb4 NULL,
        `SecurityStamp` longtext CHARACTER SET utf8mb4 NULL,
        `ConcurrencyStamp` longtext CHARACTER SET utf8mb4 NULL,
        `PhoneNumber` longtext CHARACTER SET utf8mb4 NULL,
        `PhoneNumberConfirmed` tinyint(1) NOT NULL,
        `TwoFactorEnabled` tinyint(1) NOT NULL,
        `LockoutEnd` datetime(6) NULL,
        `LockoutEnabled` tinyint(1) NOT NULL,
        `AccessFailedCount` int NOT NULL,
        CONSTRAINT `PK_fdms_users` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE TABLE `fdms_webhookdeliveries` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `EventType` varchar(60) CHARACTER SET utf8mb4 NOT NULL,
        `PayloadJson` longtext CHARACTER SET utf8mb4 NOT NULL,
        `Status` int NOT NULL,
        `Attempts` int NOT NULL,
        `NextAttemptAt` datetime(6) NOT NULL,
        `DeliveredAt` datetime(6) NULL,
        `LastError` varchar(500) CHARACTER SET utf8mb4 NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_webhookdeliveries` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE TABLE `fdms_fiscaldaycounters` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `FiscalDayId` int NOT NULL,
        `Type` int NOT NULL,
        `Currency` varchar(3) CHARACTER SET utf8mb4 NOT NULL,
        `TaxId` int NULL,
        `TaxPercent` decimal(5,2) NULL,
        `MoneyType` int NULL,
        `Value` decimal(21,2) NOT NULL,
        CONSTRAINT `PK_fdms_fiscaldaycounters` PRIMARY KEY (`Id`),
        CONSTRAINT `FK_fdms_fiscaldaycounters_fdms_fiscaldays_FiscalDayId` FOREIGN KEY (`FiscalDayId`) REFERENCES `fdms_fiscaldays` (`Id`) ON DELETE CASCADE
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE TABLE `fdms_roleclaims` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `RoleId` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
        `ClaimType` longtext CHARACTER SET utf8mb4 NULL,
        `ClaimValue` longtext CHARACTER SET utf8mb4 NULL,
        CONSTRAINT `PK_fdms_roleclaims` PRIMARY KEY (`Id`),
        CONSTRAINT `FK_fdms_roleclaims_fdms_roles_RoleId` FOREIGN KEY (`RoleId`) REFERENCES `fdms_roles` (`Id`) ON DELETE CASCADE
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE TABLE `fdms_rolepermissions` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `RoleId` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
        `PermissionId` int NOT NULL,
        CONSTRAINT `PK_fdms_rolepermissions` PRIMARY KEY (`Id`),
        CONSTRAINT `FK_fdms_rolepermissions_fdms_permissions_PermissionId` FOREIGN KEY (`PermissionId`) REFERENCES `fdms_permissions` (`Id`) ON DELETE CASCADE,
        CONSTRAINT `FK_fdms_rolepermissions_fdms_roles_RoleId` FOREIGN KEY (`RoleId`) REFERENCES `fdms_roles` (`Id`) ON DELETE CASCADE
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE TABLE `fdms_systemmodules` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `SectionModuleId` int NOT NULL,
        `Name` varchar(80) CHARACTER SET utf8mb4 NOT NULL,
        `Slugname` varchar(80) CHARACTER SET utf8mb4 NOT NULL,
        `Icon` varchar(40) CHARACTER SET utf8mb4 NOT NULL,
        `Description` varchar(200) CHARACTER SET utf8mb4 NULL,
        `AccessName` varchar(80) CHARACTER SET utf8mb4 NULL,
        `SortOrder` int NOT NULL,
        `IsActive` tinyint(1) NOT NULL,
        CONSTRAINT `PK_fdms_systemmodules` PRIMARY KEY (`Id`),
        CONSTRAINT `FK_fdms_systemmodules_fdms_sectionmodules_SectionModuleId` FOREIGN KEY (`SectionModuleId`) REFERENCES `fdms_sectionmodules` (`Id`) ON DELETE RESTRICT
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE TABLE `fdms_userclaims` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `UserId` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
        `ClaimType` longtext CHARACTER SET utf8mb4 NULL,
        `ClaimValue` longtext CHARACTER SET utf8mb4 NULL,
        CONSTRAINT `PK_fdms_userclaims` PRIMARY KEY (`Id`),
        CONSTRAINT `FK_fdms_userclaims_fdms_users_UserId` FOREIGN KEY (`UserId`) REFERENCES `fdms_users` (`Id`) ON DELETE CASCADE
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE TABLE `fdms_userlogins` (
        `LoginProvider` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
        `ProviderKey` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
        `ProviderDisplayName` longtext CHARACTER SET utf8mb4 NULL,
        `UserId` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
        CONSTRAINT `PK_fdms_userlogins` PRIMARY KEY (`LoginProvider`, `ProviderKey`),
        CONSTRAINT `FK_fdms_userlogins_fdms_users_UserId` FOREIGN KEY (`UserId`) REFERENCES `fdms_users` (`Id`) ON DELETE CASCADE
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE TABLE `fdms_userroles` (
        `UserId` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
        `RoleId` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
        CONSTRAINT `PK_fdms_userroles` PRIMARY KEY (`UserId`, `RoleId`),
        CONSTRAINT `FK_fdms_userroles_fdms_roles_RoleId` FOREIGN KEY (`RoleId`) REFERENCES `fdms_roles` (`Id`) ON DELETE CASCADE,
        CONSTRAINT `FK_fdms_userroles_fdms_users_UserId` FOREIGN KEY (`UserId`) REFERENCES `fdms_users` (`Id`) ON DELETE CASCADE
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE TABLE `fdms_usertokens` (
        `UserId` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
        `LoginProvider` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
        `Name` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
        `Value` longtext CHARACTER SET utf8mb4 NULL,
        CONSTRAINT `PK_fdms_usertokens` PRIMARY KEY (`UserId`, `LoginProvider`, `Name`),
        CONSTRAINT `FK_fdms_usertokens_fdms_users_UserId` FOREIGN KEY (`UserId`) REFERENCES `fdms_users` (`Id`) ON DELETE CASCADE
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE TABLE `fdms_rolemenus` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `RoleId` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
        `SystemModuleId` int NOT NULL,
        CONSTRAINT `PK_fdms_rolemenus` PRIMARY KEY (`Id`),
        CONSTRAINT `FK_fdms_rolemenus_fdms_roles_RoleId` FOREIGN KEY (`RoleId`) REFERENCES `fdms_roles` (`Id`) ON DELETE CASCADE,
        CONSTRAINT `FK_fdms_rolemenus_fdms_systemmodules_SystemModuleId` FOREIGN KEY (`SystemModuleId`) REFERENCES `fdms_systemmodules` (`Id`) ON DELETE CASCADE
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE TABLE `fdms_submodules` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `ModuleId` int NOT NULL,
        `Name` varchar(80) CHARACTER SET utf8mb4 NOT NULL,
        `Icon` varchar(40) CHARACTER SET utf8mb4 NOT NULL,
        `Route` varchar(120) CHARACTER SET utf8mb4 NOT NULL,
        `AccessName` varchar(80) CHARACTER SET utf8mb4 NULL,
        `Order` int NOT NULL,
        `IsActive` tinyint(1) NOT NULL,
        CONSTRAINT `PK_fdms_submodules` PRIMARY KEY (`Id`),
        CONSTRAINT `FK_fdms_submodules_fdms_systemmodules_ModuleId` FOREIGN KEY (`ModuleId`) REFERENCES `fdms_systemmodules` (`Id`) ON DELETE CASCADE
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE INDEX `IX_fdms_apikeys_ClientId` ON `fdms_apikeys` (`ClientId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE INDEX `IX_fdms_apikeys_DeviceId` ON `fdms_apikeys` (`DeviceId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE UNIQUE INDEX `IX_fdms_apikeys_KeyHash` ON `fdms_apikeys` (`KeyHash`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE INDEX `IX_fdms_auditlogs_ClientId_At` ON `fdms_auditlogs` (`ClientId`, `At`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE INDEX `IX_fdms_devices_ClientId` ON `fdms_devices` (`ClientId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE UNIQUE INDEX `IX_fdms_devices_ZimraDeviceId_Environment` ON `fdms_devices` (`ZimraDeviceId`, `Environment`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE INDEX `IX_fdms_fiscaldaycounters_FiscalDayId` ON `fdms_fiscaldaycounters` (`FiscalDayId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE UNIQUE INDEX `IX_fdms_fiscaldays_DeviceId_FiscalDayNo` ON `fdms_fiscaldays` (`DeviceId`, `FiscalDayNo`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE UNIQUE INDEX `IX_fdms_offlinefiles_DeviceId_FiscalDayNo_Sequence` ON `fdms_offlinefiles` (`DeviceId`, `FiscalDayNo`, `Sequence`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE INDEX `IX_fdms_offlinefiles_Status_DeviceId` ON `fdms_offlinefiles` (`Status`, `DeviceId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE UNIQUE INDEX `IX_fdms_permissions_Name` ON `fdms_permissions` (`Name`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE UNIQUE INDEX `IX_fdms_receipts_ClientId_DeviceId_ExternalRef` ON `fdms_receipts` (`ClientId`, `DeviceId`, `ExternalRef`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE INDEX `IX_fdms_receipts_ClientId_InvoiceNo` ON `fdms_receipts` (`ClientId`, `InvoiceNo`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE INDEX `IX_fdms_receipts_DeviceId_ReceiptGlobalNo` ON `fdms_receipts` (`DeviceId`, `ReceiptGlobalNo`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE INDEX `IX_fdms_receipts_Status_HoldUntil` ON `fdms_receipts` (`Status`, `HoldUntil`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE UNIQUE INDEX `IX_fdms_refreshtokens_TokenHash` ON `fdms_refreshtokens` (`TokenHash`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE INDEX `IX_fdms_refreshtokens_UserId` ON `fdms_refreshtokens` (`UserId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE INDEX `IX_fdms_roleclaims_RoleId` ON `fdms_roleclaims` (`RoleId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE UNIQUE INDEX `IX_fdms_rolemenus_RoleId_SystemModuleId` ON `fdms_rolemenus` (`RoleId`, `SystemModuleId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE INDEX `IX_fdms_rolemenus_SystemModuleId` ON `fdms_rolemenus` (`SystemModuleId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE INDEX `IX_fdms_rolepermissions_PermissionId` ON `fdms_rolepermissions` (`PermissionId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE UNIQUE INDEX `IX_fdms_rolepermissions_RoleId_PermissionId` ON `fdms_rolepermissions` (`RoleId`, `PermissionId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE UNIQUE INDEX `RoleNameIndex` ON `fdms_roles` (`NormalizedName`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE INDEX `IX_fdms_submodules_ModuleId` ON `fdms_submodules` (`ModuleId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE INDEX `IX_fdms_systemmodules_SectionModuleId` ON `fdms_systemmodules` (`SectionModuleId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE UNIQUE INDEX `IX_fdms_systemmodules_Slugname` ON `fdms_systemmodules` (`Slugname`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE INDEX `IX_fdms_userclaims_UserId` ON `fdms_userclaims` (`UserId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE INDEX `IX_fdms_userlogins_UserId` ON `fdms_userlogins` (`UserId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE INDEX `IX_fdms_userroles_RoleId` ON `fdms_userroles` (`RoleId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE INDEX `EmailIndex` ON `fdms_users` (`NormalizedEmail`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE INDEX `IX_fdms_users_ClientId` ON `fdms_users` (`ClientId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE UNIQUE INDEX `UserNameIndex` ON `fdms_users` (`NormalizedUserName`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE INDEX `IX_fdms_webhookdeliveries_ClientId_CreatedAt` ON `fdms_webhookdeliveries` (`ClientId`, `CreatedAt`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    CREATE INDEX `IX_fdms_webhookdeliveries_Status_NextAttemptAt` ON `fdms_webhookdeliveries` (`Status`, `NextAttemptAt`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005132230_InitialCreate') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261005132230_InitialCreate', '8.0.11');

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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005200424_AddBranches') THEN

    ALTER TABLE `fdms_users` ADD `AccessScope` longtext CHARACTER SET utf8mb4 NOT NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005200424_AddBranches') THEN

    UPDATE fdms_users SET AccessScope = 'all' WHERE AccessScope = '';

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005200424_AddBranches') THEN

    ALTER TABLE `fdms_devices` ADD `BranchId` char(36) COLLATE ascii_general_ci NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005200424_AddBranches') THEN

    CREATE TABLE `fdms_branches` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Code` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
        `Name` varchar(120) CHARACTER SET utf8mb4 NOT NULL,
        `Address` varchar(250) CHARACTER SET utf8mb4 NULL,
        `Phone` varchar(40) CHARACTER SET utf8mb4 NULL,
        `IsActive` tinyint(1) NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_branches` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005200424_AddBranches') THEN

    CREATE TABLE `fdms_userdevices` (
        `UserId` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
        `DeviceId` char(36) COLLATE ascii_general_ci NOT NULL,
        CONSTRAINT `PK_fdms_userdevices` PRIMARY KEY (`UserId`, `DeviceId`),
        CONSTRAINT `FK_fdms_userdevices_fdms_devices_DeviceId` FOREIGN KEY (`DeviceId`) REFERENCES `fdms_devices` (`Id`) ON DELETE CASCADE,
        CONSTRAINT `FK_fdms_userdevices_fdms_users_UserId` FOREIGN KEY (`UserId`) REFERENCES `fdms_users` (`Id`) ON DELETE CASCADE
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005200424_AddBranches') THEN

    CREATE TABLE `fdms_userbranches` (
        `UserId` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
        `BranchId` char(36) COLLATE ascii_general_ci NOT NULL,
        CONSTRAINT `PK_fdms_userbranches` PRIMARY KEY (`UserId`, `BranchId`),
        CONSTRAINT `FK_fdms_userbranches_fdms_branches_BranchId` FOREIGN KEY (`BranchId`) REFERENCES `fdms_branches` (`Id`) ON DELETE CASCADE,
        CONSTRAINT `FK_fdms_userbranches_fdms_users_UserId` FOREIGN KEY (`UserId`) REFERENCES `fdms_users` (`Id`) ON DELETE CASCADE
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005200424_AddBranches') THEN

    CREATE INDEX `IX_fdms_devices_BranchId` ON `fdms_devices` (`BranchId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005200424_AddBranches') THEN

    CREATE UNIQUE INDEX `IX_fdms_branches_ClientId_Code` ON `fdms_branches` (`ClientId`, `Code`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005200424_AddBranches') THEN

    CREATE INDEX `IX_fdms_userbranches_BranchId` ON `fdms_userbranches` (`BranchId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005200424_AddBranches') THEN

    CREATE INDEX `IX_fdms_userdevices_DeviceId` ON `fdms_userdevices` (`DeviceId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005200424_AddBranches') THEN

    ALTER TABLE `fdms_devices` ADD CONSTRAINT `FK_fdms_devices_fdms_branches_BranchId` FOREIGN KEY (`BranchId`) REFERENCES `fdms_branches` (`Id`) ON DELETE SET NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005200424_AddBranches') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261005200424_AddBranches', '8.0.11');

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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005202358_AddInvoiceTemplates') THEN

    CREATE TABLE `fdms_invoicetemplates` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `BranchId` char(36) COLLATE ascii_general_ci NULL,
        `Name` varchar(80) CHARACTER SET utf8mb4 NOT NULL,
        `SettingsJson` longtext CHARACTER SET utf8mb4 NOT NULL,
        `Logo` longblob NULL,
        `LogoContentType` varchar(40) CHARACTER SET utf8mb4 NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_invoicetemplates` PRIMARY KEY (`Id`),
        CONSTRAINT `FK_fdms_invoicetemplates_fdms_branches_BranchId` FOREIGN KEY (`BranchId`) REFERENCES `fdms_branches` (`Id`) ON DELETE CASCADE
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005202358_AddInvoiceTemplates') THEN

    CREATE INDEX `IX_fdms_invoicetemplates_BranchId` ON `fdms_invoicetemplates` (`BranchId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005202358_AddInvoiceTemplates') THEN

    CREATE INDEX `IX_fdms_invoicetemplates_ClientId_BranchId` ON `fdms_invoicetemplates` (`ClientId`, `BranchId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005202358_AddInvoiceTemplates') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261005202358_AddInvoiceTemplates', '8.0.11');

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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005205212_AddErrorLogsAndAuditDetail') THEN

    ALTER TABLE `fdms_auditlogs` ADD `ActorType` varchar(12) CHARACTER SET utf8mb4 NOT NULL DEFAULT '';

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005205212_AddErrorLogsAndAuditDetail') THEN

    UPDATE fdms_auditlogs SET ActorType = CASE WHEN UserId IS NOT NULL THEN 'user' WHEN ApiKeyId IS NOT NULL THEN 'token' ELSE 'system' END WHERE ActorType = '';

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005205212_AddErrorLogsAndAuditDetail') THEN

    ALTER TABLE `fdms_auditlogs` ADD `Entity` varchar(60) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005205212_AddErrorLogsAndAuditDetail') THEN

    ALTER TABLE `fdms_auditlogs` ADD `EntityId` varchar(64) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005205212_AddErrorLogsAndAuditDetail') THEN

    ALTER TABLE `fdms_auditlogs` ADD `IpAddress` varchar(64) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005205212_AddErrorLogsAndAuditDetail') THEN

    CREATE TABLE `fdms_errorlogs` (
        `Id` bigint NOT NULL AUTO_INCREMENT,
        `Uuid` varchar(16) CHARACTER SET utf8mb4 NOT NULL,
        `Severity` varchar(12) CHARACTER SET utf8mb4 NOT NULL,
        `Source` varchar(200) CHARACTER SET utf8mb4 NULL,
        `Message` varchar(2000) CHARACTER SET utf8mb4 NOT NULL,
        `ExceptionType` varchar(200) CHARACTER SET utf8mb4 NULL,
        `StackTrace` longtext CHARACTER SET utf8mb4 NULL,
        `Path` varchar(300) CHARACTER SET utf8mb4 NULL,
        `Method` varchar(10) CHARACTER SET utf8mb4 NULL,
        `StatusCode` int NULL,
        `IpAddress` varchar(64) CHARACTER SET utf8mb4 NULL,
        `UserName` varchar(200) CHARACTER SET utf8mb4 NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NULL,
        `TraceId` varchar(64) CHARACTER SET utf8mb4 NULL,
        `MachineName` varchar(100) CHARACTER SET utf8mb4 NULL,
        `Environment` varchar(30) CHARACTER SET utf8mb4 NULL,
        `Fingerprint` varchar(64) CHARACTER SET utf8mb4 NOT NULL,
        `Occurrences` int NOT NULL,
        `IsResolved` tinyint(1) NOT NULL,
        `Resolution` varchar(1000) CHARACTER SET utf8mb4 NULL,
        `ResolvedBy` varchar(200) CHARACTER SET utf8mb4 NULL,
        `ResolvedAt` datetime(6) NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `LastSeenAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_errorlogs` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005205212_AddErrorLogsAndAuditDetail') THEN

    CREATE INDEX `IX_fdms_auditlogs_At` ON `fdms_auditlogs` (`At`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005205212_AddErrorLogsAndAuditDetail') THEN

    CREATE INDEX `IX_fdms_errorlogs_CreatedAt` ON `fdms_errorlogs` (`CreatedAt`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005205212_AddErrorLogsAndAuditDetail') THEN

    CREATE INDEX `IX_fdms_errorlogs_Fingerprint` ON `fdms_errorlogs` (`Fingerprint`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005205212_AddErrorLogsAndAuditDetail') THEN

    CREATE INDEX `IX_fdms_errorlogs_IsResolved_CreatedAt` ON `fdms_errorlogs` (`IsResolved`, `CreatedAt`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261005205212_AddErrorLogsAndAuditDetail') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261005205212_AddErrorLogsAndAuditDetail', '8.0.11');

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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006085916_AddSystemSettings') THEN

    CREATE TABLE `fdms_systemsettings` (
        `Key` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
        `Value` varchar(1000) CHARACTER SET utf8mb4 NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        `UpdatedBy` varchar(200) CHARACTER SET utf8mb4 NULL,
        CONSTRAINT `PK_fdms_systemsettings` PRIMARY KEY (`Key`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006085916_AddSystemSettings') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261006085916_AddSystemSettings', '8.0.11');

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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006092655_AddBilling') THEN

    ALTER TABLE `fdms_systemsettings` MODIFY COLUMN `Value` longtext CHARACTER SET utf8mb4 NOT NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006092655_AddBilling') THEN

    CREATE TABLE `fdms_applications` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `CompanyName` varchar(150) CHARACTER SET utf8mb4 NOT NULL,
        `Tin` varchar(30) CHARACTER SET utf8mb4 NULL,
        `ContactName` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
        `ContactSurname` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
        `Email` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
        `Phone` varchar(40) CHARACTER SET utf8mb4 NULL,
        `PasswordHash` varchar(500) CHARACTER SET utf8mb4 NOT NULL,
        `PlanId` int NULL,
        `BillingCycle` varchar(10) CHARACTER SET utf8mb4 NOT NULL,
        `Status` varchar(12) CHARACTER SET utf8mb4 NOT NULL,
        `Message` varchar(1000) CHARACTER SET utf8mb4 NULL,
        `DecisionReason` varchar(500) CHARACTER SET utf8mb4 NULL,
        `DecidedBy` varchar(200) CHARACTER SET utf8mb4 NULL,
        `DecidedAt` datetime(6) NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NULL,
        `IpAddress` varchar(64) CHARACTER SET utf8mb4 NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_applications` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006092655_AddBilling') THEN

    CREATE TABLE `fdms_billinginvoices` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `Number` varchar(40) CHARACTER SET utf8mb4 NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `SubscriptionId` char(36) COLLATE ascii_general_ci NULL,
        `PlanId` int NULL,
        `Description` varchar(300) CHARACTER SET utf8mb4 NOT NULL,
        `Subtotal` decimal(18,2) NOT NULL,
        `TaxPercent` decimal(5,2) NOT NULL,
        `TaxAmount` decimal(18,2) NOT NULL,
        `Total` decimal(18,2) NOT NULL,
        `Currency` varchar(3) CHARACTER SET utf8mb4 NOT NULL,
        `Status` varchar(10) CHARACTER SET utf8mb4 NOT NULL,
        `IssuedAt` datetime(6) NOT NULL,
        `DueAt` datetime(6) NOT NULL,
        `PaidAt` datetime(6) NULL,
        `PeriodStart` datetime(6) NOT NULL,
        `PeriodEnd` datetime(6) NOT NULL,
        `PaymentMethod` varchar(30) CHARACTER SET utf8mb4 NULL,
        `PaymentReference` varchar(150) CHARACTER SET utf8mb4 NULL,
        `ChangeToPlanId` int NULL,
        `ChangeToCycle` varchar(10) CHARACTER SET utf8mb4 NULL,
        CONSTRAINT `PK_fdms_billinginvoices` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006092655_AddBilling') THEN

    CREATE TABLE `fdms_payments` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `InvoiceId` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Method` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
        `Status` varchar(24) CHARACTER SET utf8mb4 NOT NULL,
        `Amount` decimal(18,2) NOT NULL,
        `Currency` varchar(3) CHARACTER SET utf8mb4 NOT NULL,
        `Reference` varchar(150) CHARACTER SET utf8mb4 NULL,
        `ProviderReference` varchar(150) CHARACTER SET utf8mb4 NULL,
        `PollUrl` varchar(500) CHARACTER SET utf8mb4 NULL,
        `RedirectUrl` varchar(500) CHARACTER SET utf8mb4 NULL,
        `Instructions` varchar(1000) CHARACTER SET utf8mb4 NULL,
        `PayerPhone` varchar(40) CHARACTER SET utf8mb4 NULL,
        `ProofName` varchar(200) CHARACTER SET utf8mb4 NULL,
        `ProofContentType` varchar(60) CHARACTER SET utf8mb4 NULL,
        `Proof` longblob NULL,
        `Notes` varchar(1000) CHARACTER SET utf8mb4 NULL,
        `ConfirmedBy` varchar(200) CHARACTER SET utf8mb4 NULL,
        `ProviderStatus` varchar(40) CHARACTER SET utf8mb4 NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `ConfirmedAt` datetime(6) NULL,
        CONSTRAINT `PK_fdms_payments` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006092655_AddBilling') THEN

    CREATE TABLE `fdms_plans` (
        `Id` int NOT NULL AUTO_INCREMENT,
        `Code` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
        `Name` varchar(80) CHARACTER SET utf8mb4 NOT NULL,
        `Description` varchar(500) CHARACTER SET utf8mb4 NULL,
        `PriceMonthly` decimal(18,2) NOT NULL,
        `PriceYearly` decimal(18,2) NOT NULL,
        `Currency` varchar(3) CHARACTER SET utf8mb4 NOT NULL,
        `MaxDevices` int NOT NULL,
        `MaxUsers` int NOT NULL,
        `MaxBranches` int NOT NULL,
        `MaxReceiptsPerMonth` int NOT NULL,
        `Features` varchar(2000) CHARACTER SET utf8mb4 NOT NULL,
        `TrialDays` int NOT NULL,
        `IsActive` tinyint(1) NOT NULL,
        `IsPublic` tinyint(1) NOT NULL,
        `SortOrder` int NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_plans` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006092655_AddBilling') THEN

    CREATE TABLE `fdms_subscriptions` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `PlanId` int NOT NULL,
        `Status` varchar(12) CHARACTER SET utf8mb4 NOT NULL,
        `BillingCycle` varchar(10) CHARACTER SET utf8mb4 NOT NULL,
        `StartedAt` datetime(6) NOT NULL,
        `CurrentPeriodStart` datetime(6) NOT NULL,
        `CurrentPeriodEnd` datetime(6) NOT NULL,
        `TrialEndsAt` datetime(6) NULL,
        `CancelledAt` datetime(6) NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_subscriptions` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006092655_AddBilling') THEN

    CREATE INDEX `IX_fdms_applications_Email` ON `fdms_applications` (`Email`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006092655_AddBilling') THEN

    CREATE INDEX `IX_fdms_applications_Status` ON `fdms_applications` (`Status`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006092655_AddBilling') THEN

    CREATE INDEX `IX_fdms_billinginvoices_ClientId_Status` ON `fdms_billinginvoices` (`ClientId`, `Status`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006092655_AddBilling') THEN

    CREATE UNIQUE INDEX `IX_fdms_billinginvoices_Number` ON `fdms_billinginvoices` (`Number`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006092655_AddBilling') THEN

    CREATE INDEX `IX_fdms_payments_ClientId_Status` ON `fdms_payments` (`ClientId`, `Status`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006092655_AddBilling') THEN

    CREATE INDEX `IX_fdms_payments_InvoiceId` ON `fdms_payments` (`InvoiceId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006092655_AddBilling') THEN

    CREATE UNIQUE INDEX `IX_fdms_plans_Code` ON `fdms_plans` (`Code`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006092655_AddBilling') THEN

    CREATE UNIQUE INDEX `IX_fdms_subscriptions_ClientId` ON `fdms_subscriptions` (`ClientId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006092655_AddBilling') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261006092655_AddBilling', '8.0.11');

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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006131248_AddActivationAndGoLive') THEN

    ALTER TABLE `fdms_clients` ADD `LiveApproved` tinyint(1) NOT NULL DEFAULT FALSE;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006131248_AddActivationAndGoLive') THEN

    ALTER TABLE `fdms_clients` ADD `LiveApprovedAt` datetime(6) NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006131248_AddActivationAndGoLive') THEN

    UPDATE fdms_clients SET LiveApproved = 1, LiveApprovedAt = UTC_TIMESTAMP(6);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006131248_AddActivationAndGoLive') THEN

    CREATE TABLE `fdms_activationcodes` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `InvoiceId` char(36) COLLATE ascii_general_ci NOT NULL,
        `PaymentId` char(36) COLLATE ascii_general_ci NULL,
        `Salt` varchar(64) CHARACTER SET utf8mb4 NOT NULL,
        `CodeHash` varchar(128) CHARACTER SET utf8mb4 NOT NULL,
        `ExpiresAt` datetime(6) NOT NULL,
        `UsedAt` datetime(6) NULL,
        `UsedBy` varchar(200) CHARACTER SET utf8mb4 NULL,
        `RevokedAt` datetime(6) NULL,
        `IssuedBy` varchar(200) CHARACTER SET utf8mb4 NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_activationcodes` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006131248_AddActivationAndGoLive') THEN

    CREATE TABLE `fdms_golive` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Status` varchar(12) CHARACTER SET utf8mb4 NOT NULL,
        `ContactName` varchar(120) CHARACTER SET utf8mb4 NOT NULL,
        `ContactPhone` varchar(40) CHARACTER SET utf8mb4 NOT NULL,
        `ZimraTin` varchar(30) CHARACTER SET utf8mb4 NULL,
        `Notes` varchar(1000) CHARACTER SET utf8mb4 NULL,
        `DecisionReason` varchar(500) CHARACTER SET utf8mb4 NULL,
        `DecidedBy` varchar(200) CHARACTER SET utf8mb4 NULL,
        `DecidedAt` datetime(6) NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_golive` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006131248_AddActivationAndGoLive') THEN

    CREATE INDEX `IX_fdms_activationcodes_ClientId_InvoiceId` ON `fdms_activationcodes` (`ClientId`, `InvoiceId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006131248_AddActivationAndGoLive') THEN

    CREATE INDEX `IX_fdms_golive_ClientId_Status` ON `fdms_golive` (`ClientId`, `Status`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006131248_AddActivationAndGoLive') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261006131248_AddActivationAndGoLive', '8.0.11');

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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006140611_AddCustomersAndProducts') THEN

    CREATE TABLE `fdms_customers` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Name` varchar(150) CHARACTER SET utf8mb4 NOT NULL,
        `TradeName` varchar(150) CHARACTER SET utf8mb4 NULL,
        `Tin` varchar(20) CHARACTER SET utf8mb4 NULL,
        `VatNumber` varchar(20) CHARACTER SET utf8mb4 NULL,
        `Email` varchar(200) CHARACTER SET utf8mb4 NULL,
        `Phone` varchar(40) CHARACTER SET utf8mb4 NULL,
        `Address` varchar(300) CHARACTER SET utf8mb4 NULL,
        `Notes` varchar(500) CHARACTER SET utf8mb4 NULL,
        `IsActive` tinyint(1) NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_customers` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006140611_AddCustomersAndProducts') THEN

    CREATE TABLE `fdms_products` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Code` varchar(40) CHARACTER SET utf8mb4 NULL,
        `Name` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
        `HsCode` varchar(8) CHARACTER SET utf8mb4 NULL,
        `Unit` varchar(20) CHARACTER SET utf8mb4 NULL,
        `UnitPrice` decimal(18,4) NOT NULL,
        `Currency` varchar(3) CHARACTER SET utf8mb4 NOT NULL,
        `TaxPercent` decimal(5,2) NULL,
        `IsExempt` tinyint(1) NOT NULL,
        `IsActive` tinyint(1) NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_products` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006140611_AddCustomersAndProducts') THEN

    CREATE INDEX `IX_fdms_customers_ClientId_Name` ON `fdms_customers` (`ClientId`, `Name`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006140611_AddCustomersAndProducts') THEN

    CREATE INDEX `IX_fdms_customers_ClientId_Tin` ON `fdms_customers` (`ClientId`, `Tin`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006140611_AddCustomersAndProducts') THEN

    CREATE INDEX `IX_fdms_products_ClientId_Code` ON `fdms_products` (`ClientId`, `Code`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006140611_AddCustomersAndProducts') THEN

    CREATE INDEX `IX_fdms_products_ClientId_Name` ON `fdms_products` (`ClientId`, `Name`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006140611_AddCustomersAndProducts') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261006140611_AddCustomersAndProducts', '8.0.11');

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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006143157_AddApiActivity') THEN

    CREATE TABLE `fdms_apiactivity` (
        `Id` bigint NOT NULL AUTO_INCREMENT,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `DeviceId` char(36) COLLATE ascii_general_ci NULL,
        `ApiKeyId` char(36) COLLATE ascii_general_ci NULL,
        `UserId` varchar(450) CHARACTER SET utf8mb4 NULL,
        `ActorName` varchar(200) CHARACTER SET utf8mb4 NULL,
        `ActorType` varchar(12) CHARACTER SET utf8mb4 NOT NULL,
        `Method` varchar(10) CHARACTER SET utf8mb4 NOT NULL,
        `Path` varchar(300) CHARACTER SET utf8mb4 NOT NULL,
        `StatusCode` int NOT NULL,
        `DurationMs` int NOT NULL,
        `ErrorCode` varchar(60) CHARACTER SET utf8mb4 NULL,
        `IpAddress` varchar(64) CHARACTER SET utf8mb4 NULL,
        `UserAgent` varchar(200) CHARACTER SET utf8mb4 NULL,
        `At` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_apiactivity` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006143157_AddApiActivity') THEN

    CREATE INDEX `IX_fdms_apiactivity_At` ON `fdms_apiactivity` (`At`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006143157_AddApiActivity') THEN

    CREATE INDEX `IX_fdms_apiactivity_ClientId_At` ON `fdms_apiactivity` (`ClientId`, `At`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006143157_AddApiActivity') THEN

    CREATE INDEX `IX_fdms_apiactivity_DeviceId_At` ON `fdms_apiactivity` (`DeviceId`, `At`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006143157_AddApiActivity') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261006143157_AddApiActivity', '8.0.11');

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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006144826_AddDatabaseBackups') THEN

    CREATE TABLE `fdms_backups` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `FileName` varchar(120) CHARACTER SET utf8mb4 NOT NULL,
        `SizeBytes` bigint NOT NULL,
        `Kind` varchar(12) CHARACTER SET utf8mb4 NOT NULL,
        `Status` varchar(12) CHARACTER SET utf8mb4 NOT NULL,
        `Error` varchar(1000) CHARACTER SET utf8mb4 NULL,
        `Tables` int NOT NULL,
        `Rows` bigint NOT NULL,
        `Sha256` varchar(64) CHARACTER SET utf8mb4 NULL,
        `CreatedBy` varchar(200) CHARACTER SET utf8mb4 NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `FinishedAt` datetime(6) NULL,
        CONSTRAINT `PK_fdms_backups` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006144826_AddDatabaseBackups') THEN

    CREATE INDEX `IX_fdms_backups_CreatedAt` ON `fdms_backups` (`CreatedAt`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006144826_AddDatabaseBackups') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261006144826_AddDatabaseBackups', '8.0.11');

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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006205835_AddApplicationLogo') THEN

    ALTER TABLE `fdms_applications` ADD `Logo` longblob NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006205835_AddApplicationLogo') THEN

    ALTER TABLE `fdms_applications` ADD `LogoContentType` longtext CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006205835_AddApplicationLogo') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261006205835_AddApplicationLogo', '8.0.11');

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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006230757_AddNotificationsQuotesRecurring') THEN

    ALTER TABLE `fdms_clients` ADD `AlertEmails` varchar(500) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006230757_AddNotificationsQuotesRecurring') THEN

    ALTER TABLE `fdms_clients` ADD `AlertKinds` varchar(200) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006230757_AddNotificationsQuotesRecurring') THEN

    ALTER TABLE `fdms_clients` ADD `Slug` varchar(40) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006230757_AddNotificationsQuotesRecurring') THEN

    CREATE TABLE `fdms_notifications` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Kind` varchar(30) CHARACTER SET utf8mb4 NOT NULL,
        `Severity` varchar(8) CHARACTER SET utf8mb4 NOT NULL,
        `Title` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
        `Body` varchar(1000) CHARACTER SET utf8mb4 NULL,
        `Link` varchar(200) CHARACTER SET utf8mb4 NULL,
        `Key` varchar(120) CHARACTER SET utf8mb4 NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `ReadAt` datetime(6) NULL,
        `EmailedAt` datetime(6) NULL,
        CONSTRAINT `PK_fdms_notifications` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006230757_AddNotificationsQuotesRecurring') THEN

    CREATE TABLE `fdms_quotes` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Number` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
        `CustomerName` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
        `CustomerTin` longtext CHARACTER SET utf8mb4 NULL,
        `CustomerVat` longtext CHARACTER SET utf8mb4 NULL,
        `CustomerEmail` longtext CHARACTER SET utf8mb4 NULL,
        `CustomerPhone` longtext CHARACTER SET utf8mb4 NULL,
        `Currency` varchar(3) CHARACTER SET utf8mb4 NOT NULL,
        `TaxInclusive` tinyint(1) NOT NULL,
        `ValidUntil` datetime(6) NULL,
        `Notes` varchar(1000) CHARACTER SET utf8mb4 NULL,
        `Status` varchar(12) CHARACTER SET utf8mb4 NOT NULL,
        `LinesJson` longtext CHARACTER SET utf8mb4 NOT NULL,
        `InvoiceReceiptId` char(36) COLLATE ascii_general_ci NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_quotes` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006230757_AddNotificationsQuotesRecurring') THEN

    CREATE TABLE `fdms_recurring` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `DeviceId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Name` varchar(150) CHARACTER SET utf8mb4 NOT NULL,
        `CustomerName` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
        `CustomerTin` longtext CHARACTER SET utf8mb4 NULL,
        `CustomerVat` longtext CHARACTER SET utf8mb4 NULL,
        `CustomerEmail` longtext CHARACTER SET utf8mb4 NULL,
        `CustomerPhone` longtext CHARACTER SET utf8mb4 NULL,
        `Currency` varchar(3) CHARACTER SET utf8mb4 NOT NULL,
        `TaxInclusive` tinyint(1) NOT NULL,
        `PaymentMethod` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
        `Notes` varchar(1000) CHARACTER SET utf8mb4 NULL,
        `LinesJson` longtext CHARACTER SET utf8mb4 NOT NULL,
        `Frequency` varchar(12) CHARACTER SET utf8mb4 NOT NULL,
        `NextRunDate` datetime(6) NOT NULL,
        `EndDate` datetime(6) NULL,
        `IsActive` tinyint(1) NOT NULL,
        `Prefix` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
        `NextNumber` int NOT NULL,
        `EmailCustomer` tinyint(1) NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_recurring` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006230757_AddNotificationsQuotesRecurring') THEN

    CREATE TABLE `fdms_recurring_runs` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `RecurringInvoiceId` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `RunDate` datetime(6) NOT NULL,
        `ReceiptId` char(36) COLLATE ascii_general_ci NULL,
        `InvoiceNo` varchar(50) CHARACTER SET utf8mb4 NULL,
        `Status` varchar(8) CHARACTER SET utf8mb4 NOT NULL,
        `Error` varchar(500) CHARACTER SET utf8mb4 NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_recurring_runs` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006230757_AddNotificationsQuotesRecurring') THEN

    CREATE UNIQUE INDEX `IX_fdms_clients_Slug` ON `fdms_clients` (`Slug`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006230757_AddNotificationsQuotesRecurring') THEN

    CREATE INDEX `IX_fdms_notifications_ClientId_CreatedAt` ON `fdms_notifications` (`ClientId`, `CreatedAt`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006230757_AddNotificationsQuotesRecurring') THEN

    CREATE UNIQUE INDEX `IX_fdms_notifications_ClientId_Key` ON `fdms_notifications` (`ClientId`, `Key`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006230757_AddNotificationsQuotesRecurring') THEN

    CREATE UNIQUE INDEX `IX_fdms_quotes_ClientId_Number` ON `fdms_quotes` (`ClientId`, `Number`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006230757_AddNotificationsQuotesRecurring') THEN

    CREATE INDEX `IX_fdms_recurring_IsActive_NextRunDate` ON `fdms_recurring` (`IsActive`, `NextRunDate`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006230757_AddNotificationsQuotesRecurring') THEN

    CREATE UNIQUE INDEX `IX_fdms_recurring_runs_RecurringInvoiceId_RunDate` ON `fdms_recurring_runs` (`RecurringInvoiceId`, `RunDate`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261006230757_AddNotificationsQuotesRecurring') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261006230757_AddNotificationsQuotesRecurring', '8.0.11');

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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007002728_AddEmailOutbox') THEN

    CREATE TABLE `fdms_email_outbox` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NULL,
        `To` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
        `Subject` varchar(300) CHARACTER SET utf8mb4 NOT NULL,
        `Html` longtext CHARACTER SET utf8mb4 NOT NULL,
        `AttachmentsJson` longtext CHARACTER SET utf8mb4 NULL,
        `Status` varchar(10) CHARACTER SET utf8mb4 NOT NULL,
        `Attempts` int NOT NULL,
        `NextAttemptAt` datetime(6) NOT NULL,
        `LastError` varchar(600) CHARACTER SET utf8mb4 NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `SentAt` datetime(6) NULL,
        CONSTRAINT `PK_fdms_email_outbox` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007002728_AddEmailOutbox') THEN

    CREATE INDEX `IX_fdms_email_outbox_ClientId_CreatedAt` ON `fdms_email_outbox` (`ClientId`, `CreatedAt`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007002728_AddEmailOutbox') THEN

    CREATE INDEX `IX_fdms_email_outbox_Status_NextAttemptAt` ON `fdms_email_outbox` (`Status`, `NextAttemptAt`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007002728_AddEmailOutbox') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261007002728_AddEmailOutbox', '8.0.11');

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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007083654_AddDeviceName') THEN

    ALTER TABLE `fdms_devices` ADD `Name` varchar(80) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007083654_AddDeviceName') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261007083654_AddDeviceName', '8.0.11');

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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007090429_AddAiAndProfile') THEN

    ALTER TABLE `fdms_users` ADD `Avatar` longblob NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007090429_AddAiAndProfile') THEN

    ALTER TABLE `fdms_users` ADD `AvatarContentType` varchar(20) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007090429_AddAiAndProfile') THEN

    ALTER TABLE `fdms_refreshtokens` ADD `IpAddress` varchar(64) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007090429_AddAiAndProfile') THEN

    ALTER TABLE `fdms_refreshtokens` ADD `UserAgent` varchar(200) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007090429_AddAiAndProfile') THEN

    CREATE TABLE `fdms_ai_usage` (
        `Id` bigint NOT NULL AUTO_INCREMENT,
        `Kind` varchar(10) CHARACTER SET utf8mb4 NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NULL,
        `IpAddress` varchar(64) CHARACTER SET utf8mb4 NULL,
        `Ok` tinyint(1) NOT NULL,
        `At` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_ai_usage` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007090429_AddAiAndProfile') THEN

    CREATE TABLE `fdms_saved_reports` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Name` varchar(120) CHARACTER SET utf8mb4 NOT NULL,
        `Question` varchar(600) CHARACTER SET utf8mb4 NULL,
        `SpecJson` longtext CHARACTER SET utf8mb4 NOT NULL,
        `CreatedBy` varchar(200) CHARACTER SET utf8mb4 NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_saved_reports` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007090429_AddAiAndProfile') THEN

    CREATE INDEX `IX_fdms_ai_usage_At` ON `fdms_ai_usage` (`At`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007090429_AddAiAndProfile') THEN

    CREATE INDEX `IX_fdms_ai_usage_ClientId_Kind_At` ON `fdms_ai_usage` (`ClientId`, `Kind`, `At`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007090429_AddAiAndProfile') THEN

    CREATE INDEX `IX_fdms_saved_reports_ClientId_CreatedAt` ON `fdms_saved_reports` (`ClientId`, `CreatedAt`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007090429_AddAiAndProfile') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261007090429_AddAiAndProfile', '8.0.11');

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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007120731_AddBanksAndCurrencies') THEN

    CREATE TABLE `fdms_bank_accounts` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `BankId` char(36) COLLATE ascii_general_ci NOT NULL,
        `AccountName` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
        `AccountNumber` varchar(34) CHARACTER SET utf8mb4 NOT NULL,
        `BranchName` varchar(100) CHARACTER SET utf8mb4 NULL,
        `BranchCode` varchar(20) CHARACTER SET utf8mb4 NULL,
        `CurrencyCode` varchar(3) CHARACTER SET utf8mb4 NOT NULL,
        `IsDefault` tinyint(1) NOT NULL,
        `IsActive` tinyint(1) NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_bank_accounts` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007120731_AddBanksAndCurrencies') THEN

    CREATE TABLE `fdms_banks` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Name` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
        `Code` varchar(20) CHARACTER SET utf8mb4 NULL,
        `IsActive` tinyint(1) NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_banks` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007120731_AddBanksAndCurrencies') THEN

    CREATE TABLE `fdms_currencies` (
        `Code` varchar(3) CHARACTER SET utf8mb4 NOT NULL,
        `Name` varchar(60) CHARACTER SET utf8mb4 NOT NULL,
        `Symbol` varchar(6) CHARACTER SET utf8mb4 NOT NULL,
        `IsActive` tinyint(1) NOT NULL,
        `SortOrder` int NOT NULL,
        CONSTRAINT `PK_fdms_currencies` PRIMARY KEY (`Code`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007120731_AddBanksAndCurrencies') THEN

    CREATE INDEX `IX_fdms_bank_accounts_ClientId_BankId` ON `fdms_bank_accounts` (`ClientId`, `BankId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007120731_AddBanksAndCurrencies') THEN

    CREATE UNIQUE INDEX `IX_fdms_banks_ClientId_Name` ON `fdms_banks` (`ClientId`, `Name`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007120731_AddBanksAndCurrencies') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261007120731_AddBanksAndCurrencies', '8.0.11');

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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007135945_AddDeviceContacts') THEN

    ALTER TABLE `fdms_devices` ADD `BranchAddress` varchar(500) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007135945_AddDeviceContacts') THEN

    ALTER TABLE `fdms_devices` ADD `BranchEmail` varchar(200) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007135945_AddDeviceContacts') THEN

    ALTER TABLE `fdms_devices` ADD `BranchPhone` varchar(50) CHARACTER SET utf8mb4 NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007135945_AddDeviceContacts') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261007135945_AddDeviceContacts', '8.0.11');

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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007143622_AddAccounting') THEN

    CREATE TABLE `fdms_bank_statement_lines` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `AccountId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Date` datetime(6) NOT NULL,
        `Description` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
        `Reference` varchar(60) CHARACTER SET utf8mb4 NULL,
        `Amount` decimal(18,2) NOT NULL,
        `JournalLineId` char(36) COLLATE ascii_general_ci NULL,
        `MatchedAt` datetime(6) NULL,
        `Fingerprint` varchar(64) CHARACTER SET utf8mb4 NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_bank_statement_lines` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007143622_AddAccounting') THEN

    CREATE TABLE `fdms_journal_entries` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Seq` int NOT NULL,
        `Number` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
        `Date` datetime(6) NOT NULL,
        `Currency` varchar(3) CHARACTER SET utf8mb4 NOT NULL,
        `Memo` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
        `Reference` varchar(60) CHARACTER SET utf8mb4 NULL,
        `Source` varchar(12) CHARACTER SET utf8mb4 NOT NULL,
        `SourceId` char(36) COLLATE ascii_general_ci NULL,
        `ReversesId` char(36) COLLATE ascii_general_ci NULL,
        `ReversedById` char(36) COLLATE ascii_general_ci NULL,
        `CreatedBy` varchar(200) CHARACTER SET utf8mb4 NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_journal_entries` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007143622_AddAccounting') THEN

    CREATE TABLE `fdms_ledger_accounts` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Code` varchar(10) CHARACTER SET utf8mb4 NOT NULL,
        `Name` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
        `Type` varchar(10) CHARACTER SET utf8mb4 NOT NULL,
        `Role` varchar(20) CHARACTER SET utf8mb4 NULL,
        `IsActive` tinyint(1) NOT NULL,
        `IsSystem` tinyint(1) NOT NULL,
        `BankAccountId` char(36) COLLATE ascii_general_ci NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_ledger_accounts` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007143622_AddAccounting') THEN

    CREATE TABLE `fdms_journal_lines` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `EntryId` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `AccountId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Debit` decimal(18,2) NOT NULL,
        `Credit` decimal(18,2) NOT NULL,
        `Description` varchar(200) CHARACTER SET utf8mb4 NULL,
        `Date` datetime(6) NOT NULL,
        `Currency` varchar(3) CHARACTER SET utf8mb4 NOT NULL,
        CONSTRAINT `PK_fdms_journal_lines` PRIMARY KEY (`Id`),
        CONSTRAINT `FK_fdms_journal_lines_fdms_journal_entries_EntryId` FOREIGN KEY (`EntryId`) REFERENCES `fdms_journal_entries` (`Id`) ON DELETE CASCADE
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007143622_AddAccounting') THEN

    CREATE INDEX `IX_fdms_bank_statement_lines_ClientId_AccountId_Date` ON `fdms_bank_statement_lines` (`ClientId`, `AccountId`, `Date`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007143622_AddAccounting') THEN

    CREATE UNIQUE INDEX `IX_fdms_bank_statement_lines_ClientId_AccountId_Fingerprint` ON `fdms_bank_statement_lines` (`ClientId`, `AccountId`, `Fingerprint`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007143622_AddAccounting') THEN

    CREATE INDEX `IX_fdms_journal_entries_ClientId_Date` ON `fdms_journal_entries` (`ClientId`, `Date`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007143622_AddAccounting') THEN

    CREATE UNIQUE INDEX `IX_fdms_journal_entries_ClientId_Seq` ON `fdms_journal_entries` (`ClientId`, `Seq`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007143622_AddAccounting') THEN

    CREATE UNIQUE INDEX `IX_fdms_journal_entries_ClientId_SourceId` ON `fdms_journal_entries` (`ClientId`, `SourceId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007143622_AddAccounting') THEN

    CREATE INDEX `IX_fdms_journal_lines_ClientId_AccountId_Currency_Date` ON `fdms_journal_lines` (`ClientId`, `AccountId`, `Currency`, `Date`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007143622_AddAccounting') THEN

    CREATE INDEX `IX_fdms_journal_lines_EntryId` ON `fdms_journal_lines` (`EntryId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007143622_AddAccounting') THEN

    CREATE UNIQUE INDEX `IX_fdms_ledger_accounts_ClientId_Code` ON `fdms_ledger_accounts` (`ClientId`, `Code`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261007143622_AddAccounting') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261007143622_AddAccounting', '8.0.11');

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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008084438_AddPayablesAndPeriodLock') THEN

    CREATE TABLE `fdms_accounting_settings` (
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `LockedUntil` datetime(6) NULL,
        `UpdatedAt` datetime(6) NOT NULL,
        `UpdatedBy` varchar(200) CHARACTER SET utf8mb4 NULL,
        CONSTRAINT `PK_fdms_accounting_settings` PRIMARY KEY (`ClientId`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008084438_AddPayablesAndPeriodLock') THEN

    CREATE TABLE `fdms_bill_payments` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `BillId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Date` datetime(6) NOT NULL,
        `Amount` decimal(18,2) NOT NULL,
        `AccountId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Reference` varchar(50) CHARACTER SET utf8mb4 NULL,
        `JournalEntryId` char(36) COLLATE ascii_general_ci NULL,
        `IsVoid` tinyint(1) NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_bill_payments` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008084438_AddPayablesAndPeriodLock') THEN

    CREATE TABLE `fdms_bills` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `SupplierId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Number` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
        `Date` datetime(6) NOT NULL,
        `DueDate` datetime(6) NOT NULL,
        `Currency` varchar(3) CHARACTER SET utf8mb4 NOT NULL,
        `Memo` varchar(200) CHARACTER SET utf8mb4 NULL,
        `Subtotal` decimal(18,2) NOT NULL,
        `Vat` decimal(18,2) NOT NULL,
        `Total` decimal(18,2) NOT NULL,
        `ExpenseAccountId` char(36) COLLATE ascii_general_ci NOT NULL,
        `JournalEntryId` char(36) COLLATE ascii_general_ci NULL,
        `IsVoid` tinyint(1) NOT NULL,
        `CreatedBy` varchar(200) CHARACTER SET utf8mb4 NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_bills` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008084438_AddPayablesAndPeriodLock') THEN

    CREATE TABLE `fdms_suppliers` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Name` varchar(120) CHARACTER SET utf8mb4 NOT NULL,
        `Tin` varchar(20) CHARACTER SET utf8mb4 NULL,
        `Email` varchar(120) CHARACTER SET utf8mb4 NULL,
        `Phone` varchar(40) CHARACTER SET utf8mb4 NULL,
        `Address` varchar(300) CHARACTER SET utf8mb4 NULL,
        `TermsDays` int NOT NULL,
        `IsActive` tinyint(1) NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_suppliers` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008084438_AddPayablesAndPeriodLock') THEN

    CREATE INDEX `IX_fdms_bill_payments_ClientId_BillId` ON `fdms_bill_payments` (`ClientId`, `BillId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008084438_AddPayablesAndPeriodLock') THEN

    CREATE INDEX `IX_fdms_bills_ClientId_SupplierId_Date` ON `fdms_bills` (`ClientId`, `SupplierId`, `Date`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008084438_AddPayablesAndPeriodLock') THEN

    CREATE UNIQUE INDEX `IX_fdms_suppliers_ClientId_Name` ON `fdms_suppliers` (`ClientId`, `Name`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008084438_AddPayablesAndPeriodLock') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261008084438_AddPayablesAndPeriodLock', '8.0.11');

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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008090306_AddReceivablesAndAssets') THEN

    ALTER TABLE `fdms_accounting_settings` ADD `CustomerTermsDays` int NOT NULL DEFAULT 0;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008090306_AddReceivablesAndAssets') THEN

    CREATE TABLE `fdms_ar_payments` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `ReceiptId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Date` datetime(6) NOT NULL,
        `Amount` decimal(18,2) NOT NULL,
        `AccountId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Reference` varchar(50) CHARACTER SET utf8mb4 NULL,
        `JournalEntryId` char(36) COLLATE ascii_general_ci NULL,
        `IsVoid` tinyint(1) NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_ar_payments` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008090306_AddReceivablesAndAssets') THEN

    CREATE TABLE `fdms_depreciation_charges` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `AssetId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Period` datetime(6) NOT NULL,
        `Amount` decimal(18,2) NOT NULL,
        `JournalEntryId` char(36) COLLATE ascii_general_ci NULL,
        CONSTRAINT `PK_fdms_depreciation_charges` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008090306_AddReceivablesAndAssets') THEN

    CREATE TABLE `fdms_fixed_assets` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Name` varchar(120) CHARACTER SET utf8mb4 NOT NULL,
        `Code` varchar(30) CHARACTER SET utf8mb4 NULL,
        `PurchaseDate` datetime(6) NOT NULL,
        `Currency` varchar(3) CHARACTER SET utf8mb4 NOT NULL,
        `Cost` decimal(18,2) NOT NULL,
        `ResidualValue` decimal(18,2) NOT NULL,
        `UsefulLifeMonths` int NOT NULL,
        `Method` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
        `RatePercent` decimal(5,2) NOT NULL,
        `AssetAccountId` char(36) COLLATE ascii_general_ci NOT NULL,
        `AccumulatedAccountId` char(36) COLLATE ascii_general_ci NOT NULL,
        `ExpenseAccountId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Status` varchar(10) CHARACTER SET utf8mb4 NOT NULL,
        `DisposedOn` datetime(6) NULL,
        `DisposalProceeds` decimal(18,2) NULL,
        `Notes` varchar(300) CHARACTER SET utf8mb4 NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_fixed_assets` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008090306_AddReceivablesAndAssets') THEN

    CREATE INDEX `IX_fdms_ar_payments_ClientId_ReceiptId` ON `fdms_ar_payments` (`ClientId`, `ReceiptId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008090306_AddReceivablesAndAssets') THEN

    CREATE UNIQUE INDEX `IX_fdms_depreciation_charges_AssetId_Period` ON `fdms_depreciation_charges` (`AssetId`, `Period`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008090306_AddReceivablesAndAssets') THEN

    CREATE INDEX `IX_fdms_fixed_assets_ClientId_Code` ON `fdms_fixed_assets` (`ClientId`, `Code`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008090306_AddReceivablesAndAssets') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261008090306_AddReceivablesAndAssets', '8.0.11');

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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008093142_AddBudgets') THEN

    CREATE TABLE `fdms_budget_lines` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `BudgetId` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `AccountId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Month` int NOT NULL,
        `Amount` decimal(18,2) NOT NULL,
        CONSTRAINT `PK_fdms_budget_lines` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008093142_AddBudgets') THEN

    CREATE TABLE `fdms_budgets` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Name` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
        `Year` int NOT NULL,
        `Currency` varchar(3) CHARACTER SET utf8mb4 NOT NULL,
        `CreatedBy` varchar(200) CHARACTER SET utf8mb4 NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_budgets` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008093142_AddBudgets') THEN

    CREATE UNIQUE INDEX `IX_fdms_budget_lines_BudgetId_AccountId_Month` ON `fdms_budget_lines` (`BudgetId`, `AccountId`, `Month`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008093142_AddBudgets') THEN

    CREATE UNIQUE INDEX `IX_fdms_budgets_ClientId_Year_Name` ON `fdms_budgets` (`ClientId`, `Year`, `Name`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008093142_AddBudgets') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261008093142_AddBudgets', '8.0.11');

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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008104328_AddPasskeys') THEN

    CREATE TABLE `fdms_passkeys` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `UserId` varchar(450) CHARACTER SET utf8mb4 NOT NULL,
        `CredentialId` varchar(512) CHARACTER SET utf8mb4 NOT NULL,
        `PublicKey` longblob NOT NULL,
        `SignCount` bigint NOT NULL,
        `Name` varchar(80) CHARACTER SET utf8mb4 NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `LastUsedAt` datetime(6) NULL,
        CONSTRAINT `PK_fdms_passkeys` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008104328_AddPasskeys') THEN

    CREATE UNIQUE INDEX `IX_fdms_passkeys_CredentialId` ON `fdms_passkeys` (`CredentialId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008104328_AddPasskeys') THEN

    CREATE INDEX `IX_fdms_passkeys_UserId` ON `fdms_passkeys` (`UserId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008104328_AddPasskeys') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261008104328_AddPasskeys', '8.0.11');

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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008111807_AddPartnersDocumentsMfaPolicy') THEN

    ALTER TABLE `fdms_users` ADD `PartnerId` char(36) COLLATE ascii_general_ci NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008111807_AddPartnersDocumentsMfaPolicy') THEN

    ALTER TABLE `fdms_refreshtokens` ADD `AuthMethod` varchar(12) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'password';

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008111807_AddPartnersDocumentsMfaPolicy') THEN

    ALTER TABLE `fdms_clients` ADD `PartnerId` char(36) COLLATE ascii_general_ci NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008111807_AddPartnersDocumentsMfaPolicy') THEN

    ALTER TABLE `fdms_clients` ADD `PartnerPercent` decimal(5,2) NOT NULL DEFAULT 0.0;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008111807_AddPartnersDocumentsMfaPolicy') THEN

    ALTER TABLE `fdms_clients` ADD `RequireMfa` tinyint(1) NOT NULL DEFAULT FALSE;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008111807_AddPartnersDocumentsMfaPolicy') THEN

    ALTER TABLE `fdms_applications` ADD `PartnerId` char(36) COLLATE ascii_general_ci NULL;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008111807_AddPartnersDocumentsMfaPolicy') THEN

    CREATE TABLE `fdms_documents` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `ClientId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Title` varchar(150) CHARACTER SET utf8mb4 NOT NULL,
        `FileName` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
        `ContentType` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
        `SizeBytes` bigint NOT NULL,
        `Sha256` varchar(64) CHARACTER SET utf8mb4 NOT NULL,
        `Data` longblob NOT NULL,
        `Category` varchar(30) CHARACTER SET utf8mb4 NOT NULL,
        `LinkType` varchar(10) CHARACTER SET utf8mb4 NOT NULL,
        `LinkId` char(36) COLLATE ascii_general_ci NULL,
        `Notes` varchar(500) CHARACTER SET utf8mb4 NULL,
        `RetainUntil` datetime(6) NOT NULL,
        `UploadedBy` varchar(200) CHARACTER SET utf8mb4 NULL,
        `UploadedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_documents` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008111807_AddPartnersDocumentsMfaPolicy') THEN

    CREATE TABLE `fdms_partner_payouts` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `PartnerId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Date` datetime(6) NOT NULL,
        `Amount` decimal(18,2) NOT NULL,
        `Currency` varchar(3) CHARACTER SET utf8mb4 NOT NULL,
        `Reference` varchar(100) CHARACTER SET utf8mb4 NULL,
        `Notes` varchar(500) CHARACTER SET utf8mb4 NULL,
        `RecordedBy` varchar(200) CHARACTER SET utf8mb4 NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_partner_payouts` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008111807_AddPartnersDocumentsMfaPolicy') THEN

    CREATE TABLE `fdms_partners` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `Name` varchar(150) CHARACTER SET utf8mb4 NOT NULL,
        `Code` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
        `ContactName` varchar(120) CHARACTER SET utf8mb4 NULL,
        `Email` varchar(200) CHARACTER SET utf8mb4 NULL,
        `Phone` varchar(40) CHARACTER SET utf8mb4 NULL,
        `CommissionPercent` decimal(5,2) NOT NULL,
        `IsActive` tinyint(1) NOT NULL,
        `Notes` varchar(1000) CHARACTER SET utf8mb4 NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_fdms_partners` PRIMARY KEY (`Id`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008111807_AddPartnersDocumentsMfaPolicy') THEN

    CREATE INDEX `IX_fdms_documents_ClientId_LinkType_LinkId` ON `fdms_documents` (`ClientId`, `LinkType`, `LinkId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008111807_AddPartnersDocumentsMfaPolicy') THEN

    CREATE INDEX `IX_fdms_partner_payouts_PartnerId` ON `fdms_partner_payouts` (`PartnerId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008111807_AddPartnersDocumentsMfaPolicy') THEN

    CREATE UNIQUE INDEX `IX_fdms_partners_Code` ON `fdms_partners` (`Code`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261008111807_AddPartnersDocumentsMfaPolicy') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20261008111807_AddPartnersDocumentsMfaPolicy', '8.0.11');

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

