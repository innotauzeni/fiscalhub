-- FDMS Gateway database update (2026-10-10): referral program (AddReferrals) and referral coins (AddReferralCoins).
-- Adds: fdms_clients.ReferralCode, ReferredByClientId, BonusDevices, BonusUsers, BonusBranches; fdms_applications.ReferrerClientId;
--       fdms_referral_rewards (+ Coins), fdms_referral_perks, fdms_coin_transactions.
-- Safe to run more than once (each step checks __EFMigrationsHistory); it skips whatever is already applied. Back up first.
-- Run it in HeidiSQL on the live database (fdms_gateway) BEFORE installing the new API DLLs.

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

