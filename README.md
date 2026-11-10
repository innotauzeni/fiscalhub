# FDMS Fiscalization Gateway

System-agnostic ZIMRA FDMS fiscalization service (.NET 8, Clean Architecture, MediatR, EF Core + MySQL).
People sign in with a user account; external systems call this API with a **per-device token** that works for one device only. The gateway calculates taxes, signs receipts, talks to FDMS over
mutual TLS, keeps the fiscal day / receipt sequence gapless, and returns the fiscalized receipt with its QR link and PDF.
Invoices can come from JSON, an Excel sheet, or a PDF. Devices in FDMS Offline mode are supported too (receipts are issued locally and uploaded as fiscal-day files).
The Angular + Tailwind console lives at `/app/` (login, dynamic menu by role, token management, administration).

Spec: `docs/fdms integration.pdf` (ZIMRA Fiscal Device Gateway API). Behaviour was cross-checked against the working
`dara` fiscalization (see "Parity with dara" below).

## Mobile app
A Flutter app in `mobile/fdms_mobile` (see `mobile/README.md`) does what the web console does, against https://fiscalapi.daraerp.co.zw, with menus and permissions taken from the sign-in answer. Every page is native (including registration, the invoice designer, roles and menus, settings, imports, audit log, system errors, referrals and the developer guide); nothing redirects to the browser.

## Layout
```
src/Fdms.Domain          entities + enums
src/Fdms.Application     MediatR commands/queries, DTOs, pure fiscal logic (signing strings, tax, counters, QR, import parsing)
src/Fdms.Infrastructure  EF Core (MySQL), FDMS mTLS client, signing, services, Excel/PDF import, receipt PDF, background jobs
src/Fdms.Api             controllers, JWT + API-token auth, permission policies, error handling, Swagger, serves the console from wwwroot/app
web/fdms-console         Angular 21 + Tailwind 4 console (builds into src/Fdms.Api/wwwroot/app)
tests/Fdms.Tests         unit tests + end-to-end API tests against a fake FDMS
```

## Run
1. Create an empty MySQL database and set `ConnectionStrings:Default` (appsettings or env `ConnectionStrings__Default`).
2. Set `Jwt:Key` (32+ random chars) and the first platform administrator: `Seed:AdminEmail` + `Seed:AdminPassword` (min length `Identity:MinPasswordLength`, default 10). The seeder creates roles, permissions, menus and that SuperAdmin on startup (idempotent; it never overwrites changes you made).
3. Set `DataProtection:KeysPath` to a **persistent, backed-up** folder. Device private keys and webhook secrets are encrypted
   with this key ring; losing it means devices must be re-registered. On Windows servers also set `DataProtection:UseDpapi=true`.
4. Apply the schema: `dotnet ef database update -p src/Fdms.Infrastructure -s src/Fdms.Api` (or `Database:AutoMigrate=true`).
5. Build the console once: `cd web/fdms-console && npm ci && npx ng build` (writes to `src/Fdms.Api/wwwroot/app`; **rebuild the API afterwards** so Development picks the new static files up). Then `dotnet run --project src/Fdms.Api`, open `/app/` (console) or `/swagger`.
   Console development: `ng serve` (proxies `/api` to the API, see `proxy.conf.json`).

Several API instances can share one database: device locks use MySQL `GET_LOCK` (set `Fdms:DistributedLocks=false` for a single instance on another provider).

IIS: app pool **Load User Profile = True**; the app-pool identity needs read/write on the key-ring folder.

Settings worth knowing:
| Setting | Default | Meaning |
|---|---|---|
| `Fdms:AcceptAnyServerCertificate` | `true` | Matches dara/ZimraEGS, which skip server-chain validation. FDMS still enforces the device (client) certificate. Set `false` once the host trusts ZIMRA's chain. |
| `Webhooks:AllowInsecureTargets` | `false` | Allow http/localhost webhook targets (dev only). |
| `RateLimit:PerMinute` | `120` | Per API token / user. |
| `RateLimit:LoginPerMinute` | `10` | Login/refresh attempts per minute per address (accounts also lock for 15 min after 5 bad passwords). |
| `Fdms:DistributedLocks` | `true` | MySQL named locks per device (multi-instance safe). |
| `Fdms:OfflineFileBytes` | `1900000` | Max raw JSON per offline file (FDMS limit is 3 MB after base64). |

## Access model (same shape as dara)
* **Users** sign in: `POST /api/v1/auth/login {identifier, password}` returns a JWT, a rotating refresh token, the user's `permissions` and the menu tree
  (`sectionMenus` / `menus`), built from the tables `SectionModule → SystemModule → Submodule`, `Role`, `Permission`, `RolePermission`, `RoleMenu`.
  Also `/auth/refresh`, `/auth/logout`, `/auth/change-password`, `GET /auth/me`. Controllers use `[HasPermission("x.y")]`.
* **Roles** (seeded, editable): `SuperAdmin` (platform operator: clients, users, roles, menus, audit), `ClientAdmin`, `Operator`, `Viewer`, `Accountant` (keeps the books, reads receipts; cannot issue receipts, run fiscal days or manage users and devices). Create more under *Roles & access*; tick permissions (what they may do) and menus (what they see).
* **Two-step sign-in and passkeys** (My profile → Extra protection). *Authenticator app*: scan the QR (or type the key), confirm with a 6-digit code, and keep the 10 one-time recovery codes. From then on `POST /auth/login` answers `{ mfaRequired: true, mfaToken }` (no session yet) and the code goes to `POST /auth/login/mfa`; wrong codes count toward the account lockout. *Passkeys* (WebAuthn, via Fido2NetLib): `POST /auth/passkey/options` then `/auth/passkey/login` sign in with no username, password or code (the device's fingerprint/face/PIN is required, so it counts as both factors). Management is under `/api/v1/security`; turning things off, new recovery codes and removing a passkey ask for the password. A company administrator can reset a colleague who lost their phone (`POST /users/{id}/reset-mfa`: MFA off, passkeys deleted, signed out everywhere). Passkeys are bound to the site's host, taken from the request; behind a proxy set `Passkeys:RpId` and `Passkeys:Origin`. They need https (localhost is fine). API tokens are unaffected.
* **Clients** are companies; every user except SuperAdmin belongs to one and only sees its data. SuperAdmin creates a client together with its first administrator (who must change the temporary password at first login).
* **Per-device API tokens**: a ClientAdmin opens *API tokens*, picks a **device**, a name, the scopes and an optional expiry, and gets `fdms_...` **once**.
  The token can only act on that device (other devices of the same company are invisible to it), only with its scopes (receipts, day, offline, stock - never registering devices, users, tokens or webhooks), and can be revoked at any time.
  Send it as `Authorization: Bearer fdms_...` or `X-Api-Key: fdms_...`.
```
POST   /api/v1/devices/{deviceId}/tokens   { "name": "POS till 1", "scopes": ["receipts.submit","receipts.view"], "expiresAt": null } -> { token, secret }
GET    /api/v1/tokens?deviceId=            DELETE /api/v1/tokens/{id}        GET /api/v1/tokens/scopes
GET|POST /api/v1/clients  (+PUT {id}/active)   /users   /roles (+{id}/access, PUT {id}/permissions|menus)   /modules   /audit
```

## Using it (a signed-in user, or a device token)
```
POST /api/v1/devices/verify | register         activation + certificate (keys are generated and stored encrypted by the gateway)
POST /api/v1/devices/{id}/day/open | close     (receipts auto-open a day)   GET .../day = status straight from FDMS
POST /api/v1/devices/{id}/receipts             fiscal invoice / credit note / debit note
POST /api/v1/devices/{id}/receipts/preview     same calculation, no number consumed, nothing sent
GET  /api/v1/receipts?status=&search=&from=&to=&deviceId=&page=
GET  /api/v1/receipts/{id}   /qr (PNG)   /pdf?format=receipt|a4     POST /retry
POST /api/v1/devices/{id}/import/excel         multipart "file" (.xlsx); GET /api/v1/import/excel-template
POST /api/v1/devices/{id}/import/pdf           multipart "file" (.pdf, digital text)
POST /api/v1/receipts/{id}/release | cancel   end / void a hold (see below)
POST /api/v1/devices/{id}/ping                 GET .../stock?hsCode=&goodName=   GET .../server-certificate
GET  /api/v1/devices/{id}/offline-files        POST .../offline-files/sync   (Offline-mode devices)
PUT|GET|DELETE /api/v1/webhook                 GET /api/v1/webhook/deliveries
```
Receipt body (prices positive; credit notes are negated by the gateway):
```json
{
  "externalRef": "ERP-INV-1001", "invoiceNo": "INV-1001", "receiptType": "FiscalInvoice",
  "currency": "USD", "linesTaxInclusive": true,
  "lines": [
    { "name": "Shoes", "hsCode": "85456852", "quantity": 1, "unitPrice": 115, "taxPercent": 15 },
    { "name": "Bread", "hsCode": "19059010", "quantity": 2, "unitPrice": 1, "isExempt": true }
  ],
  "payments": [{ "moneyType": "Cash", "amount": 117 }]
}
```
* A line names its tax with **`taxId`** (FDMS id), **`taxPercent`** (15, 15.5, 0) or **`isExempt`** - the gateway maps it to the device's configured taxes.
* `externalRef` makes retries safe: the same ref returns the original receipt and consumes no new number.
* `201` = accepted by FDMS, `202` = numbered and queued because FDMS is unreachable (delivered automatically, in order; a webhook tells you).
* Credit/debit notes need `originalReceiptId` (gateway receipt id) and `notes`; the remaining value of the original is enforced.
* Errors are RFC 7807 with a stable `errorCode` (e.g. `RCPT025`, `RCPT035`, `DEV02`).

### Excel import
Download the template, one row per line item; rows with the same **Invoice No** form one invoice (blank = continue the invoice above).
Credit/debit notes use `Receipt Type` + `Original Invoice No` + `Notes`. `submit=false` (default) only previews; `submit=true` fiscalises.
A file with any unreadable row submits nothing. Re-uploading a file never double-fiscalises (idempotent per invoice number).
Options: `taxInclusive` (default true), `defaultTaxPercent`, `defaultCurrency`, `defaultHsCode`.

### PDF import
Best effort for **digital** PDFs (no OCR for scans). It returns a draft (a ready-to-send receipt request) with warnings for everything
it guessed; review/edit it and POST it to `/receipts`, or repeat with `submit=true`. VAT-registered taxpayers need HS codes: pass
`defaultHsCode=` explicitly (it is validated and echoed as a warning; nothing is defaulted silently). Pass `invoiceNo=` when the PDF has none.

### Hold window (grace period)
Send `"holdMinutes": 10` (0-60) with a receipt: it is fully validated now but **not numbered, signed or sent**, so it can be cancelled
(`POST /receipts/{id}/cancel`) with no trace at ZIMRA and no number used. It is issued automatically when the hold ends (or
`POST /receipts/{id}/release`). A cancelled receipt frees its reference/invoice number for a corrected resubmission. A held receipt
that ZIMRA later refuses is marked `Rejected` with the reason and announced by webhook. Once issued, only a credit note can correct it.

### Offline mode
When FDMS has set a device to **Offline** (`GET /devices/{id}` shows `operatingMode`), the gateway issues receipts locally: signed,
numbered, QR ready (status `OfflineIssued`), with fiscal days opened/closed locally. At close (or `offline-files/sync`) the day is packed
into fiscal-day files (spec 4.9: header, receipts, footer with the signed Z report on the last file; split below ~1.9 MB), uploaded in sequence
order with `submitFile`, and polled with `getFileStatus` until FDMS reports them processed (receipts then become `Submitted`;
errors raise the `offlinefile.failed` webhook). Credit/debit notes against an offline invoice reference it by device + global number + fiscal day.

### Webhooks
`PUT /api/v1/webhook {"url":"https://..."}` returns a signing secret **once**. Events: `receipt.fiscalised`, `receipt.failed`, `receipt.cancelled`,
`fiscalday.closed`, `fiscalday.close_failed`, `offlinefile.failed`. Each POST has `X-Fdms-Event`, `X-Fdms-Delivery`, `X-Fdms-Timestamp`,
`X-Fdms-Signature: sha256=<hex HMAC-SHA256(secret, timestamp + "." + body)>`. Failures are retried (1m, 5m, 30m, 2h, 6h, 12h, 24h).
Targets must be public https hosts (checked at set time and again at connect time to block internal addresses).

## Console (`/app/`)
Angular standalone components + signals, Tailwind 4, light/dark. Login, then a sidebar built from the menu tree the server returns for your role (colour-coded sections, modules, submodules);
route guards use the same permission names as the API (the server enforces everything again). Pages: dashboard, devices (verify/register, config, certificate, ping), receipts (filters, PDF/QR, release/cancel/retry),
new invoice (preview, hold), Excel/PDF import, fiscal days, offline files, API tokens, webhooks, and for administrators clients, users, roles & access, menus & modules, audit log.
The access token lives in memory only, the refresh token in `sessionStorage`; the page is served with a strict CSP (`script-src 'self'`).

## Branches, user access and invoice templates
* **Branches** (`/app/admin/branches`): group a company's devices by shop. A user is either unrestricted or limited to chosen branches and/or devices
  (Users → building icon). The limit uses the same allowed-device filter a device token uses, so a limited person cannot see, fiscalise on or list
  anything of another device; it applies within ~20 s without signing out.
* **Invoice templates** (`/app/admin/templates`): logo, accent colour, header, columns, bank details, terms and footer, for the company and optionally per
  branch, with a live preview. The fiscal elements (numbers, device id, tax summary, QR, signature) are always printed.

## AI (Gemini), My profile and the public sign-up assistant
* **Setup** (platform operator, `/app/admin/ai`): paste a Google Gemini API key. It is stored **encrypted in the database** (Data Protection, like the payment-gateway secrets), never returned
  by any endpoint (only the last four characters), and used only by the server. Choose the model (default `gemini-2.5-flash`), switch the chat / reports / comments on or off, and set daily limits.
  Every use is counted in `fdms_ai_usage`; a spent limit answers 429. `Gemini:Model` / `Gemini:BaseUrl` in configuration are optional overrides.
* **AI reports** (`/app/ai-reports`, permission `reports.ai`): ask in plain words ("top 10 items this month"). The AI only **chooses** a measure, a grouping, a period and filters from closed lists;
  the spec is validated (unknown words are refused, ranges capped at 400 days) and **the gateway works out every number** from the company's receipts within the person's device access, with credit notes netted off.
  Only the question and machine names go to Google; the optional comment receives just the grouped totals and is never sent for per-customer reports. Reports can also be built by hand
  (`POST /api/v1/ai/reports/run`, free), saved and re-run with fresh dates (`/saved`).
* **Public sign-up assistant** (`/assistant`, plus a launcher on the landing and registration pages): a chat that collects company name, TIN, contact, email, phone, plan and billing cycle.
  The AI only proposes values for those fields; the server re-validates each one, **a password is never part of the chat** (it is typed in a private box and sent with the ordinary
  `POST /api/v1/public/register`, so the chat can do nothing the form cannot). Limited per IP (`RateLimit:AssistantPerMinute`, 30), per message (600 characters, 40 messages) and per day.
* **My profile** (`/app/profile`, `GET/PUT /api/v1/profile`): details, a cropped picture (PNG/JPEG up to 100 KB, shown in the header), password link, signed-in devices
  (browser, address; sign one or all others out) and the machines you may use. Signing a device out deletes its refresh token.

## Invoice design: copy an existing invoice, or keep your own
* **Layout** (`/app/admin/templates`): beyond logo, colour and wording, a template now chooses the company block (plain or on a coloured bar), title style and position, table style (lines, striped, grid, coloured header), the order and names of the table columns (item, HS code, quantity, price, tax rate, total), invoice/customer details as one list or two columns, totals across the page or compact on the right, QR position, logo size, margins and text size, and the company's own words for the labels. Every value comes from a closed list and the defaults reproduce the standard look. The fiscal items (numbers, device ID, tax summary, QR, signature) are always printed, and the 80 mm roll keeps its compact form.
* **Copy your current invoice** (`POST /api/v1/templates/analyze`, `templates.manage`, needs the Gemini key): upload a PDF, PNG or JPEG of an invoice from SAP, Sage, Pastel or any POS; the AI describes its design and the answer is the same settings the editor uses, so AI and manual are one thing. Nothing is saved or kept; the editor shows what was matched, a live preview and every control for adjusting it. The AI is told never to copy the sample's customer data. It counts toward the company's daily AI allowance. Logos are uploaded (or cut from an image sample) separately.
* **Keep the ERP's own invoice**: every fiscal result (`/receipts/{id}/fiscal`, the `fiscalise` response and `lookup`) carries a `printBlock` (`ready`, the lines ZIMRA expects at the foot of the invoice, and `qrUrl`; `links.qr` is the QR as a PNG). The ERP prints those, plus its own device ID, on its own design.

## Accounting (books, reports and bank reconciliation)
Permissions `accounting.view` (see) and `accounting.manage` (keep the books); ClientAdmin has both, other roles get them through Roles & access. Menu section **Accounting**.
* **Double-entry books, per currency.** Each company gets a standard chart of accounts (`/app/accounting/accounts`; add your own, standard and bank accounts can be renamed or switched off, not deleted). Every bank account listed under **Banks** gets its own ledger account. Nothing is converted between currencies: each report is for one currency.
* **Fiscal receipts post themselves** (`AccountingService.SyncSalesAsync`): a confirmed invoice or debit note debits cash, card, mobile wallet, the bank account (for the invoice's currency) or receivables, according to how it was paid, and credits Sales and VAT output; a credit note does the reverse against Sales returns. It runs whenever a journal list or report is opened (and from "Post new sales"), is idempotent (one entry per receipt, unique `(ClientId, SourceId)`), and never touches the fiscalisation path.
* **Journals** (`/app/accounting/journals`): manual entries must balance; a posted entry is never edited, only **reversed** (a new entry with debits and credits swapped; a reversal cannot be reversed).
* **Cashbook** (`/app/accounting/cashbook`): receipts, payments and a running balance for any cash, bank or wallet account, with quick "record receipt / payment" entries.
* **Reports** (`/app/accounting/reports/{ledger|trial-balance|profit-loss|balance-sheet}`): general ledger with running balance, trial balance, profit and loss (income, cost of sales, expenses) and balance sheet (profit to date included), each with print and CSV. The trial balance and balance sheet say whether they balance.
* **Bank reconciliation** (`/app/accounting/reconcile`): import a CSV statement (Date, Description, Amount or Debit/Credit columns; `yyyy-MM-dd` or day-first dates; re-importing adds nothing), match each line with the books (suggestions by identical amount within 7 days, one click to match all), turn what the books missed (fees, interest) into entries that match automatically, and read the difference against the closing balance you type. Matching requires identical amounts.

### Suppliers, bills and closing a period
* **Suppliers and bills** (`/app/accounting/suppliers`, `/app/accounting/bills`): enter a supplier's invoice (amount before VAT, VAT, what it is for, due date or the supplier's terms) and it posts itself: expense and VAT input debited, **accounts payable (2010)** credited. Paying it (part or full, from any cash, bank or wallet account) debits payables and credits that account. Overpaying, duplicate supplier invoice numbers and non-expense accounts are refused. A payment can be voided, and a bill can be voided once it has no payments; both are reversed in the books, never deleted.
* **Payables ageing** (`/app/accounting/reports/payables`): what is owed to each supplier as not due, 1-30, 31-60, 61-90 and over 90 days past due, per currency, with CSV.
* **Closing a period** (Journals page, `GET/PUT /api/v1/accounting/period`): everything dated on or before the closing date is final. Manual journals, cashbook entries, bills, payments and reversals dated in it are refused (`PERIOD_LOCKED`); a fiscal receipt confirmed after its day was closed is posted on the first open day and says "(dated yyyy-MM-dd)" in its memo, so signed-off figures never move. Reopening is allowed and goes in the audit log.

### Customers owing and fixed assets
* **Customers owing** (`/app/accounting/receivables`): every fiscal invoice (or part of one) that was left on account, followed until it is settled. It shows what was credited (credit notes against that invoice reduce it automatically) and received (**Receive payment** posts debit cash/bank, credit receivables; a payment can be voided and is reversed), how many days late it is (payment terms default to 30 days and are changed on the page), and a **customer statement** (invoices, credit notes and payments with a running balance, printable). Customers are told apart by the buyer's TIN, else name. Invoices paid in full at the till never appear. **Receivables ageing** (`/app/accounting/reports/receivables`) is the same breakdown as payables: not due, 1-30, 31-60, 61-90, over 90.
* **Fixed assets** (`/app/accounting/assets`): register an asset (cost, value left at the end of its life, straight-line life in months or a reducing-balance yearly rate, and its asset, accumulated-depreciation and expense accounts; optionally post the purchase from a cash or bank account). **Run depreciation** charges every completed month for every asset exactly once (`(AssetId, Period)` is unique) as one journal per month and currency, and refuses months inside a closed period. **Sell or scrap** brings depreciation up to the end of that month, removes cost and accumulated depreciation, and books the difference between the proceeds and the book value to Other income (4090) or Other expenses (5990). The register shows cost, accumulated depreciation and book value per currency.

### VAT return and budgets
* **VAT return** (`/app/accounting/vat`, `GET /api/v1/accounting/vat/return`): worked out from the books for any period and currency. Sales before VAT and sales returned come from the Sales and Sales-returns accounts, **VAT charged** from the VAT output account (credit notes reduce it), **purchases and input VAT** from your supplier bills, and the result is **VAT payable (or refundable)**. It also shows what is currently held in the two VAT accounts and offers **Record VAT payment / refund**, which posts one entry clearing both accounts against the cash or bank account used (`POST /accounting/vat/settle`; refused when nothing is held). Settlement entries are left out of the period's return, so a settled period still reads the same. This is the books' view; the receipt-based VAT export under Reports remains the per-device ZIMRA submission figure.
* **Budgets** (`/app/accounting/budgets`): a budget is a year in one currency with a monthly amount for each income and expense account (Spread repeats one figure). **Budget vs actual** compares any range of months with the books: per account budget, actual, variance and percent, with unbudgeted spending shown as such, and planned versus actual profit. Variance is signed so that positive is always good: more income than planned, or less spent than planned.


## Partners, documents, usage analytics and required MFA
* **Required extra protection.** Users page → "Require extra protection for everyone" (`GET/PUT /api/v1/security-policy`, company administrators). While it is on, a person who has neither the authenticator app nor signed in with a passkey gets `mfaSetupRequired` at sign-in and a token that may only reach `/auth`, `/security`, `/profile` (the console sends them to My profile; every other call answers 403 `MFA_SETUP_REQUIRED`). Turning on the app and refreshing the session lifts it; a passkey must be used to sign in (the sign-in method is remembered on the refresh token). API tokens are not affected.
* **Documents** (Accounting → Documents, and a Documents button on bills, assets and suppliers). PDF, PNG/JPG, Excel, Word, CSV and text only, up to 8 MB; the first bytes must match the type; stored in the database (`fdms_documents`), 500 MB per company (`Documents:CompanyLimitMb`). Every file is kept for 7 years (`Documents:RetentionYears`): it cannot be deleted before its keep-until date, which can be extended but never shortened. Downloads are sent with a fixed type, `nosniff` and a sandbox CSP.
* **Partners (resellers / accounting firms).** Platform operator: Partners page (`/api/v1/partners`): create a partner (referral code, commission %), add its staff (role `Partner`), link a company, record payouts (never more than is owed). A company that registers with `/register?ref=CODE` is attached when its application is approved; the rate is copied to the company, so changing the partner's rate later does not rewrite history. Commission = rate × the subtotal (before tax) of each *paid* subscription invoice of the partner's companies, per currency. Partner staff belong to no company: their menu is only Partner → Overview, Our companies, Commission (`/api/v1/partner/*`), and every company, accounting and platform endpoint answers 403.
* **Usage analytics** (operator, Administration → Usage analytics, `GET /api/v1/analytics/usage?days=`). Receipts per day and per company, rejections, pending, plans, and an at-risk verdict with the reasons: unpaid/expired subscription, no receipts after earlier activity, falling receipts, many rejections, receipts stuck pending, trial ending, no device, nobody signing in for 30 days.

## Refer and earn, terms, privacy and developer details
* **Refer and earn** (companies, menu "Refer & earn", `GET /api/v1/referrals`, perm `referrals.view`). Every company gets a code (`REF` + 6 characters) and a link `/app/register?ref=CODE`; the code can also be typed on the registration page (web and mobile). A partner code wins over a company code. When the referred company is approved it is linked to the referrer; the referrer earns the program's % of the subtotal of the first N *paid* subscription invoices of that company, and reaching a level (e.g. 3 paying companies) adds its free months to the referrer's own subscription once.
* **Referral program** (operator, Administration → Referral program, `/api/v1/referral-program`): switch it on/off, set the %, the number of invoices rewarded, the levels, and mark rewards as paid out.
* **Terms, privacy, developer details**: public `GET /api/v1/public/legal`, pages `/app/terms`, `/app/privacy`, `/app/developer`, linked from the footer, the registration page and the mobile app; edited in Settings (`PUT /api/v1/settings/legal`, `settings.manage`). Clearing a document restores the starting text.

## Audit trail and system logs
* **Audit log** (`/app/admin/audit`, `GET /api/v1/audit/search`): every entry says **who** (a person, an integrating API token by name, or the system), **what**,
  **which record**, **from which address**, and when. Covers sign-ins (including failures), receipts issued/held/retried/cancelled, fiscal days, devices, tokens,
  users/roles/access, templates and log actions. Filter by text, action, person, type, client and date. A company sees only its own trail.
  Behind a reverse proxy set `Hosting:BehindProxy=true` so the real client address is recorded.
* **System logs** (`/app/admin/logs`, platform operator, `logs.view` / `logs.manage`): every Error/Fatal the gateway logs - unhandled exceptions, ZIMRA unreachable,
  background-job crashes - with request, person, machine, trace id and stack trace. Repeats of the same unresolved error are counted on one row. Mark resolved with a note,
  resolve all, delete resolved. Entries older than `Logs:ErrorRetentionDays` (90) are removed. Logging never blocks or fails a fiscal request (it only queues).

## Time and timezones
Fiscal timestamps come from one clock, `FdmsClock.CatNow()`: **UTC+2 computed from the UTC clock**, so the host's timezone cannot change what is signed or sent. A test fails the
build if any code uses `DateTime.Now`, `ToLocalTime` or `TimeZoneInfo.Local`. Receipt and fiscal-day dates are Zimbabwe wall-clock (sent without a zone); everything else
(created, last login, audit, logs, hold deadlines) is UTC and sent with `Z` so browsers show the viewer's local time.

## Integrating another system
The guide lives in [docs/INTEGRATION.md](docs/INTEGRATION.md) (per-device token, the receipt call, statuses, corrections, webhooks with signature verification, error codes, timezones,
examples in curl/C#/JavaScript/PHP/Python, go-live checklist). It is **public**, no sign-in needed:
* readable page: `/app/docs` (also linked from the sign-in page and, for company administrators, the *Developer docs* menu entry; signed-in users also see their device ids);
* raw Markdown: `GET /api/v1/docs/integration`;
* interactive API reference: `/swagger`, OpenAPI document: `/swagger/v1/swagger.json`.

Edit only `docs/INTEGRATION.md`: the API copies it next to the application at build time and the page renders it, so there is a single source. A test fails if a webhook event the gateway can
send is missing from the guide.

## Operational rules baked in
* ZIMRA is the source of truth for fiscal-day state; every day operation reconciles with `getStatus` first.
* Receipt counter / global number / hash chain are issued under a per-device lock (process semaphore + MySQL `GET_LOCK`, so several API
  instances can share a database) and rolled back if FDMS permanently rejects the receipt, so the sequence never has holes.
* All FDMS timestamps use Zimbabwe time (UTC+2) regardless of server clock.
* One mTLS client per device is cached (no per-request key import). Every signature is self-verified against the device certificate.
* Background jobs: re-deliver queued receipts (30 s), auto-close days before the max duration, renew certificates 30 days before expiry, deliver webhooks (10 s).

## Parity with dara (the working system)
Matched on purpose: header names (`DeviceModelName`/`DeviceModelVersion`), endpoint paths, `{ receipt }` envelope with no `deviceID`, no
`taxCode`, `taxPercent` omitted for exempt, `taxID`/`receiptLineHSCode` field names, credit/debit note reference (receiptID + global no +
fiscal day + date), CloseDay payload and counter order, CAT time, RSA-2048 CSR with `CN=ZIMRA-<serial>-<10 digit id>`, SignHash + self-verify,
server-certificate validation off by default. Done differently: numbering is serialised per device with rollback (dara computes it from stored
rows), counters include credit/debit notes and every currency, no silent default HS code or tax rate, idempotent submissions, webhooks outbox.

## Tests
`dotnet test` runs 85 tests (unit + end-to-end through the real API with a fake FDMS that verifies signatures, receipt chains, offline files and footer counters like ZIMRA does).
Set `FDMS_TEST_MYSQL="Server=127.0.0.1;Port=3306;User=root;Password=;"` to run the same suite against real MySQL (one database per fixture).

## Verified vs assumed
Verified against the spec and your working dara code: signing, payload shapes, `getConfig/getStatus/openDay/submitReceipt/closeDay/registerDevice/verifyTaxpayerInformation` paths and headers.
**Assumed from the spec text, not yet seen working live** (check on the first test-environment run): the HTTP paths of `GetStockList`, `GetServerCertificate`
(`/Public/v1/GetServerCertificate`), `Ping`, `SubmitFile`/`GetFileStatus` and the multipart encoding of the file (field `file`, base64 text), plus the exact
status strings of `GetFileStatus`. They are isolated in `FdmsGateway`; a mismatch is a one-line fix.
The console was exercised with jsdom against the live API; it has not been clicked through in a real browser.

## Still needed
A registered ZIMRA **test** device (id, activation key, serial, model name/version) for the first live run.


## Database backups

The platform operator has **Administration → Backups**. A backup is a consistent (single-snapshot), gzip-compressed SQL dump of every table, written by the gateway itself (no `mysqldump` needed) to `Backup:Path` (default `backups` next to the app, outside the web root). It can be made on demand, runs automatically on a schedule, and the newest `Backup:KeepLast` are kept. Downloads and deletions are recorded in the audit log; only the operator can reach them.

```json
"Backup": { "Enabled": true, "IntervalHours": 24, "KeepLast": 14, "Path": "backups" }
```

On Windows (no `gunzip`) use the **.sql** download button next to a backup, or the **Structure script** button for an empty new server (`deploy/DEPLOY-WINDOWS.md` has the full go-live steps).

Restore on purpose, from the server, with the gateway stopped: `gunzip -c fdms-YYYYMMDD-HHMMSS-xxxxxx.sql.gz | mysql -u USER -p DATABASE`. The DataProtection key folder (`DataProtection:KeysPath`) is not in the database; back it up too or saved gateway credentials cannot be decrypted after a restore. Copy backups off the server regularly.
