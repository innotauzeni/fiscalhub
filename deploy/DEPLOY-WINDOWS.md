# Putting the gateway on a live Windows server

You need three things on the server: MySQL (8.x or MariaDB 10.6+), the .NET 8 **ASP.NET Core Hosting Bundle** (for IIS) or the .NET 8 runtime, and the published gateway.

## 1. The database

**A brand new, empty database (normal for a first go-live)**

1. Create it: `CREATE DATABASE fdms_gateway CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;` and a user that owns only that database.
2. Load the structure: run `fdms-structure.sql` (in this folder, or download a fresh one from **Administration → Backups → Structure script**).
   `mysql -u USER -p fdms_gateway < fdms-structure.sql` (in MySQL Workbench: File → Open SQL Script → run).
   It is safe to run twice. It also records the migrations, so the gateway will not try to build the tables again.
3. Start the gateway once. It creates the roles, menus and permissions and the first platform administrator from `Seed:AdminEmail` / `Seed:AdminPassword`.

**Moving real data from another server**

1. On the old gateway: **Administration → Backups → Back up now**, then download it. Use the `.sql` button (plain file) when the new server is Windows; the other button gives the compressed `.gz` (open it with 7-Zip).
2. On the new server, with the gateway **stopped**: `mysql -u USER -p fdms_gateway < fdms-YYYYMMDD-HHMMSS-xxxxxx.sql`. The backup already contains the structure, so do not run `fdms-structure.sql` first.
3. Copy the old server's **DataProtection key folder** (`DataProtection:KeysPath`) to the new one. It is not in the database: without it saved ZIMRA/Paynow/Gemini credentials and every device certificate cannot be decrypted.
4. Start the gateway.

> The development database holds test companies. Do not load it on a live server; start empty and use the structure script.

## 2. Settings (`appsettings.Production.json` next to the app, or environment variables)

```json
{
  "ConnectionStrings": { "Default": "Server=localhost;Port=3306;Database=fdms_gateway;User=fdms;Password=CHANGE-ME;" },
  "Jwt": { "Key": "a-long-random-secret-of-at-least-32-characters" },
  "Seed": { "AdminEmail": "you@yourcompany.com", "AdminPassword": "a-strong-password" },
  "DataProtection": { "KeysPath": "C:\\fdms\\keys" },
  "Backup": { "Enabled": true, "IntervalHours": 24, "KeepLast": 14, "Path": "D:\\fdms-backups" },
  "Database": { "AutoMigrate": false }
}
```

Set `Database:AutoMigrate` to `true` instead if you prefer the gateway to build and update the tables itself (then you can skip `fdms-structure.sql`).
Passkeys need https and the real host name. Behind a proxy also set `Passkeys:RpId` (e.g. `gateway.example.com`) and `Passkeys:Origin` (`https://gateway.example.com`).

## 3. Publish and host

```
dotnet publish src/Fdms.Api -c Release -o C:\inetpub\fdms
```

(Build the console first with `npx ng build` in `web/fdms-console`; it writes into `src/Fdms.Api/wwwroot/app`.)
In IIS: create a site pointing at `C:\inetpub\fdms` with an **No Managed Code** application pool, bind https with your certificate. The folders for `DataProtection:KeysPath` and `Backup:Path` must be writable by the application pool identity (`IIS AppPool\<pool name>`).

## 4. Backups on the live server

* The gateway makes a full SQL backup every `Backup:IntervalHours` and keeps the newest `Backup:KeepLast` in `Backup:Path`. Make a backup at any time from **Administration → Backups**.
* Copy the backup folder **and** the DataProtection key folder off the server regularly (another disk or machine). A backup without the keys cannot restore saved credentials.
* Restore: stop the site, `mysql -u USER -p fdms_gateway < backup.sql`, start the site.

## 5. "HTTP Error 500.30 - ASP.NET Core app failed to start"

The page hides the real reason. Find it first:

1. In `web.config` set `stdoutLogEnabled="true"` and create the folder `logs` next to `Fdms.Api.dll` (the app pool identity needs write access to it). Reload the site, then open the newest `logs\stdout_*.log`. Turn it off again afterwards.
2. The usual causes, in the order to check them:
   * **`Jwt:Key` is empty.** `appsettings.json` ships with `"Key": ""` and in Production the app refuses to start without a secret of at least 32 characters. Put it in `appsettings.Production.json` (sample in this folder) or an environment variable `Jwt__Key`.
   * **The connection string still says `localhost` / `root`** while MySQL on the server uses another user, or the database `fdms_gateway` does not exist yet (run `fdms-structure.sql` first).
   * **The ASP.NET Core 8 Hosting Bundle is not installed** (or IIS was not restarted after installing it). The runtime must be 8.x: `dotnet --list-runtimes` should list `Microsoft.AspNetCore.App 8.0.x`.
   * **`DataProtection:KeysPath` or `Backup:Path` is not writable** by `IIS AppPool\<your pool>` (give it Modify rights).
   * The application pool must be **No Managed Code**, and the site must run **in-process** (the `web.config` you have is correct).
3. In Event Viewer → Windows Logs → Application, the source `IIS AspNetCore Module V2` also prints the exception.

## 6. Registering a device fails with "CryptographicException: Access denied"

The device certificate has to be loaded into Windows' key store so TLS can use it. The IIS application pool is not allowed to write to the machine key store by default. Fix it in IIS (no redeploy needed):

1. IIS Manager → **Application Pools** → your pool → **Advanced Settings** → set **Load User Profile = True**. Keep the identity as `ApplicationPoolIdentity` (or a real account). Recycle the pool.
2. If it still fails, give the pool identity rights on the machine keys folder: `icacls "C:\ProgramData\Microsoft\Crypto\RSA\MachineKeys" /grant "IIS AppPool\<your pool name>:(M)"`, then recycle the pool.

Newer builds of the gateway also fall back to the pool's own user profile automatically when the machine store is refused, but that fallback still needs **Load User Profile = True**.
