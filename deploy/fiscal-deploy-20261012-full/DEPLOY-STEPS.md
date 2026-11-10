# Full deployment, 2026-10-12: everything to date

This package replaces the whole API and the whole web console, and brings the live database up to date in one script. It includes every feature built so far:
- approvals, revenue and notifications;
- email settings and backups;
- self-service sign-up, user names and demo clean-up;
- the invoice builder and the FISCAL TAX INVOICE title rule;
- Zoho Books, the Zoho bridge for partners, and companies' own Zoho apps.

| File | Goes to |
|---|---|
| `fdms-update-full.sql` | HeidiSQL, live database `fdms_gateway`. Run it first. |
| `fiscalapi-dlls.zip` | **Use this one.** The API folder (https://fiscalapi.daraerp.co.zw): 13 DLLs and `Fdms.Api.deps.json` |
| `fiscalapi-full.zip` | Only if the API folder ever needs rebuilding from scratch. Contains all program files. |
| `fiscalweb.zip` | The `fiscalweb` folder (https://fiscal.daraerp.co.zw) |
| `FEATURE-NOTES.md` | What each feature does and how to set it up (sections 4 to 15) |

The phone app is not included; build the APK yourself.

## 1. Database (about 1 minute)
1. In HeidiSQL, **back up `fdms_gateway`**: right-click the database → Export database as SQL, with structure and data.
2. Open `fdms-update-full.sql` and run it on `fdms_gateway` (F9).
   - It checks each step against `__EFMigrationsHistory` and only adds what is missing, so it doesn't matter which earlier update scripts were run.
   - It never deletes data.
   - It is safe to run twice.
   - It was tested on an empty database and on a database from the 2026-10-09 release. Both ended with the same 83 tables, and the existing rows were kept.
3. Check the result: `SELECT COUNT(*) FROM __EFMigrationsHistory;` should give **41**.

## 2. API (DLLs only)
1. In IIS, **stop the API's application pool**.
2. Unzip `fiscalapi-dlls.zip` into the API folder and replace all 14 files.
   - The 4 `Fdms.*.dll` files are the new program.
   - The 9 library DLLs and `Fdms.Api.deps.json` are the same as in the 2026-10-10 release. Copy them again anyway: if live never got that release, the API would not start without them.
3. Start the pool. https://fiscalapi.daraerp.co.zw/health should answer `Healthy`.
4. Sign out and back in on the web and the phone.

**Developer docs:** copy `INTEGRATION.md` from this package into the API folder's `docs` folder (replace the old one). This is the new, simpler guide with examples in 7 languages and the Zoho Books sections. No restart needed; it shows within a minute.

### Rebuilding the API folder from scratch (only if ever needed)
1. In IIS, **stop the API's application pool**.
2. Copy the API folder somewhere as a backup.
3. Unzip `fiscalapi-full.zip` **into** the API folder, replacing files.
   - The zip has no `appsettings.json` and no `web.config`, so your server's own settings and IIS configuration stay as they are. Don't delete them.
   - Keep the `logs`, `backups` and DataProtection keys folders. Encrypted secrets (email password, payment gateways, Zoho keys, AI key) depend on those keys.
4. Start the pool. https://fiscalapi.daraerp.co.zw/health should answer `Healthy`.
5. Sign out and back in on the web and the phone. New menus and permissions only reach a session at sign-in.

Nothing new is required in `appsettings.json`. Optional settings, shown with their defaults:

```json
"Integrations": { "SyncMinutes": 5, "BridgeWatchMinutes": 15, "BridgePerMinute": 60 },
"Onboarding": { "SelfService": true, "DeleteUnpaidDemo": true, "DeleteUnpaidAfterDays": 7 },
"Backup": { "MirrorPath": "D:\\fdms-backups" }
```

`Cors:Origins` must still list `https://fiscal.daraerp.co.zw`, as it does today.

## 3. Web console
1. Empty the `fiscalweb` folder, then unzip `fiscalweb.zip` into it.
   - `config.js` already points to https://fiscalapi.daraerp.co.zw.
   - `web.config` is included.
2. Press Ctrl+F5 in the browser.

## 4. After deploying: Zoho
1. **Settings → Web app address** must be `https://fiscal.daraerp.co.zw`, so Zoho's sign-in comes back to the console.
2. **Shared Zoho app (optional):** at api-console.zoho.com, create a *Server-based Application* with this redirect URI:
   `https://fiscalapi.daraerp.co.zw/api/v1/integrations/zoho/callback`
   Paste its client ID (it starts with `1000.`) and secret under **Integrations monitor → Zoho app**.
3. **Or without a shared app:** each company uses **Use my own Zoho app** (Self Client: client ID, secret and a generated code).
4. Optionally, set prices under **Integrations monitor → What a connection costs**: the Zoho connection, extra documents, and the bridge fee.

Details are in `FEATURE-NOTES.md`, sections 13 to 15.

## If something goes wrong
- **The API does not start:** check that the folder still has `appsettings.json` and `web.config`, and read the newest file in `logs`.
- **Rollback:**
  1. Stop the pool.
  2. Put back the copied API folder.
  3. Restore the database export.

## 5. Mobile app download buttons
After deploying, sign in as the platform administrator and go to **Settings → Mobile app downloads**.
- Paste the app's page address for each store as soon as it's approved, for example `https://apkpure.com/fiscalhub/zw.co.daraerp.fdms_mobile`.
- Each link you fill in becomes a button on the public home page (top and "Install the app" section) and on the sign-in page.
- Empty stores stay hidden, so add Google Play and the App Store later when ready.
- Only the stores' own https addresses are accepted: apkpure.com, play.google.com and apps.apple.com.

## 6. WhatsApp assistant (fiscalise by chatting)
Company staff link their own WhatsApp number in **My profile → WhatsApp**. They then message the FiscalHub number to:
- issue fiscal invoices and quick sales;
- check today's sales and recent receipts, and get PDFs;
- open or close the fiscal day;
- issue credit notes, which go through the approval matrix;
- approve or reject requests.

Alerts can also go to their WhatsApp.

**One-time setup at Meta (platform owner, about 30 minutes)**
1. Go to https://developers.facebook.com → **My Apps → Create app** → type **Business** → add the **WhatsApp** product.
2. Under **WhatsApp → API Setup**:
   1. Add your business phone number, for example a new SIM, and verify it.
   2. Copy the **Phone number ID** and the **WhatsApp Business Account ID**.
3. Create a **permanent token**:
   1. Go to business.facebook.com → **Settings → Users → System users**.
   2. Add a system user (Admin).
   3. Assign it your app and the WhatsApp account.
   4. Click **Generate token** and tick `whatsapp_business_messaging` and `whatsapp_business_management`.
4. **App secret:** in developers.facebook.com, open your app → **App settings → Basic → App secret**.
5. In the console as platform admin, open **Settings → WhatsApp assistant**:
   1. Enter the number, phone number ID, account ID, token and app secret.
   2. Tick **Switch the WhatsApp assistant on** and save.
   3. Use **Send test** to your own phone.
6. Webhook:
   1. In Meta, go to **WhatsApp → Configuration → Webhook → Edit**.
   2. Paste the **Callback URL** and **Verify token** shown on the settings card.
   3. Click **Verify and save**.
   4. Under **Webhook fields**, subscribe to **messages**.
7. Alert template, needed for alerts sent more than 24 hours after the person last chatted:
   1. In Meta, go to **WhatsApp Manager → Message templates → Create**.
   2. Category: **Utility**. Name: `fiscalhub_alert`. Language: English.
   3. Body: `FiscalHub alert: {{1}}. {{2}}`
   4. Once Meta approves it, enter `fiscalhub_alert` and language `en` on the settings card.
8. Before going live, verify your business in Meta Business Manager and set the app to **Live**. Until then, Meta only delivers to the test numbers you add in API Setup.

**What it costs you:** Meta charges per conversation started by the business; a template sent to someone outside the 24-hour window opens one. Conversations a person starts by messaging the bot are covered by Meta's service-conversation allowance. See Meta's current WhatsApp pricing for Zimbabwe.

## 7. Zoho Books for a company's own system, and the Zoho Connector plan
**A company's own system (plug and play)**
1. On **Integration → Zoho Books**, the company connects Zoho.
2. Under **Your own system (API)**, it creates a key with the modules it needs, read only or read and write.
3. Its developers call `https://fiscalapi.daraerp.co.zw/api/v1/bridge/zoho/{path}`, in Zoho Books' own format, with header `X-Bridge-Key`.
4. Optionally, a webhook receives every change from Zoho.

The guide is in Developer docs → "Zoho Books from your own system". Each key is limited to its company and its modules, every write is in the company's audit log, and an unpaid subscription locks the key (402).

**Clients who only want the Zoho integration:** a new plan, **Zoho Connector**, is created on first start (Plans page, with the label "Zoho connector").
- Set its price under **Administration → Plans**. The starting price of 15 USD/month is a placeholder.
- In the plan editor, **What the plan is for** has two choices: Fiscalisation, or Zoho Books connector only.
- Companies on a connector plan can't register fiscal devices or branches.
- After sign-in they see only: Zoho Books (opened first), Activity, Developer docs, Billing, Users, Alerts, Refer and earn, and Approvals.
- The bridge fee under **Integrations monitor → prices** is not added for them, because their plan price already covers it.
- For companies on a fiscal plan, the bridge fee now applies while they share with a partner **or** use their own key.

## 8. Zoho Books: applications, set-up fee, suspend and revoke
Companies can no longer switch Zoho Books on by themselves. They apply; you decide.

**Companies already using Zoho keep working.** The update marks every company that already had a Zoho connection, an own-system key or a partner grant as approved.

**Set your prices once:** go to **Administration → Integrations monitor → What a connection costs**.
- **Set-up fee**: one-off, proposed on every approval. You can change it per company when you approve.
- **Monthly fee**, **documents included** and **price per extra document**: added to the company's subscription invoice at each renewal.
- **Zoho bridge monthly fee**: per company that uses own-system keys or shares with a partner.
- Tick **Charge for connections**. Only companies billed in the same currency are charged.

**Each application:** go to **Integrations monitor → Applications and access → To decide**.
- **Approve**: enter the set-up fee. With a fee above 0, an invoice is raised and the company sees it under Billing. The integration switches on when the invoice is paid (online, or when you mark it paid on the Revenue page). With 0, it switches on at once.
- **Reject**: give a reason; the company sees it and can apply again.
- **Cancel** (while awaiting payment): voids the set-up invoice.

**Stopping a company:**
- **Suspend** (on an Active row): sync, fiscalising from Zoho, API keys and partner access stop at once. Automatic fiscalising is switched off. **Reactivate** undoes it.
- **Live API keys → Revoke**: kills one key (a partner's or a company's) for good. Its owner is notified.
- To stop a whole partner, deactivate it on the **Partners** page.

The set-up invoice is separate from the subscription. Leaving it unpaid never locks the company's account or delays its renewal.

## 9. Companies' own WhatsApp numbers, and SMS
**Your SMS account (platform):** go to **Administration → Settings → SMS (platform account)**.
1. Choose the provider and fill in what it asks for:
   - **Nhume:** API key and an approved sender ID.
   - **Twilio:** Account SID, auth token, and your Twilio number or sender ID.
   - **eSolutions:** the API address they gave you, user name, password and originator.
   - Also available: **BulkSMS**, **Africa's Talking**, and **Other (HTTP)** for any HTTPS address.
2. Tick **Send SMS through this account**, save, and send a test.
3. **Companies without their own SMS account may send through this one** is on by default. Untick it if you don't want to pay for their SMS.

The platform account sends:
- sign-up codes;
- password-reset codes, for users with a phone number;
- your own platform alerts;
- when sharing is on, invoices and alerts for companies without their own account.

**A company's own account:** **Integration → WhatsApp & SMS** (company administrators).
- **Its WhatsApp number:** the page shows its own webhook address and verify token for Meta, and step-by-step help.
  - Its staff link to the company's number and get the assistant there.
  - Its alerts go out from it.
  - With **Let my customers get copies of their invoices** ticked, its customers can message the number. They get "My invoices", "Find an invoice" and "Talk to us".
  - Customers only ever see invoices whose buyer phone is their WhatsApp number, or whose TIN belongs to a saved customer with that phone.
- **Its SMS account:** used first for its invoices, staff alerts and staff password codes.

**Sending an invoice by SMS:** open a receipt → **Send it to the customer** → **SMS**. The API equivalent is `POST /api/v1/receipts/{id}/sms`.

**SMS alerts:** each person switches them on in their **Profile**. They need a phone number there. Each person gets at most 10 alert SMS a day.

**Tested only with simulated providers:** the provider calls follow the provider documentation and dara's integrations. The Nhume and eSolutions number formats (`2637…` without +) are unconfirmed. Send a test from the settings card for each account before relying on it.


## 10. Usage charges and add-ons (your prices, change any time)
**Set your prices:** go to **Administration → Plans**.
- **On each plan** (edit the plan): **Receipts / month included** and **Price per extra receipt**. Each receipt above the allowance is charged on the next invoice; 0 means never.
- **Usage and add-on prices** card, below the plans:
  - each SMS sent through *your* SMS account;
  - a company's own WhatsApp number, per month;
  - extra device, user and branch, per month.
- Tick **Charge for usage and offer add-ons** and save.

**How the charges work:**
- **Changes apply from each company's next invoice.** Only companies billed in the card's currency are charged. A price of 0 switches that item off.
- **When charges appear:** usage charges go on the subscription invoice. They cover what was used since the last invoice, so each receipt and each SMS is billed exactly once. Monthly items (WhatsApp number, add-ons) are charged for the period ahead.
- **Companies using their own SMS account** are not charged per SMS.

**Add-ons (company side):** the **Billing** page has an **Add-ons** card.
- **Adding:** the company chooses extra devices, users or branches and gets an invoice for the rest of its current period. They switch on as soon as it is paid, then renew with the subscription.
- **Removing:** allowed only if they're not in use; nothing is refunded.
- **Cancelling:** a waiting add-on invoice can be cancelled.

## 11. Modules by plan (sell higher plans)
Edit a plan under **Administration → Plans** and tick its **Modules included**:
- Accounting
- Invoice designer
- Approvals
- Quotes and recurring invoices
- Quick sale (POS)
- AI reports
- API tokens and webhooks
- WhatsApp and SMS
- Zoho Books

Fiscalising itself (devices, receipts, fiscal days, customers, items, reports, billing, users) is always included.

**What changes for companies:**
- They only see the ticked modules in their menu. The API refuses the rest with `403 PLAN_FEATURE`, and the message names their plan and tells them to upgrade.
- Without **API tokens and webhooks**, existing device tokens stop working until the company moves to a plan that includes it.
- The pricing cards (website and Billing → Change plan) list each plan's modules.

**Existing plans include everything** until you edit them. Nothing changes for current customers until you do.

**Moving a company down** to a plan without a module switches that module off at once. Its data is kept, and it comes back on upgrade.

**Menus are sent at sign-in.** People see a changed menu after signing in again. The API check applies within 30 seconds.

## 12. Go-live fee and live-device fee
Set both prices in **Administration → Plans → Usage and add-on prices**:
- **Go-live fee (one-off, on approval).**
- **Each live (Production) device, per month.** Added to the subscription invoice for each active live device.

**Approving a go-live request** (**Administration → Go-live requests → Approve**):
- The fee box is prefilled with your standard fee. Change it per company, or enter 0 to waive it.
- **With a fee:** the request becomes **Awaiting payment** and the company gets an invoice. Live devices switch on the moment it's paid. **Cancel** rejects the request and voids the invoice.
- **With 0:** live devices switch on at once, as before.

An unpaid go-live invoice never locks the company's account.

## 13. Usage pricing per plan (flexible)
Edit any plan under **Administration → Plans**. The **Usage pricing** box is added on top of the plan's price, which can be 0.

**Live devices:**
- a price **per live device per month**;
- optionally **per year**, used for yearly subscriptions (0 = 12 × monthly).

Test devices are always free.

**Receipt pricing:** choose one per plan.
- **Included in the plan price (flat):** nothing extra. The allowance and price per extra receipt above still work.
- **Per 1,000 receipts:** pay as you go, with an optional **minimum per month** (low-volume customers still pay you enough) and **cap per month** (heavy users know their maximum).
- **Monthly volume bands:** e.g. up to 1,000 receipts = 10, up to 5,000 = 30, then an open last band = 60 a month. The company pays the band its average monthly volume falls in.

**How it's charged:** usage goes on the next invoice and covers what happened since the last one. Minimums, caps and bands use the **average receipts per month** over that time, so the invoice date never changes the price.

**The Developer (API) plan** is added (hidden) when the API starts. It's set up per 1,000 receipts at 10.00, with a 10.00 monthly minimum and 5.00 per live device (50.00 a year). Edit its prices, then tick **Show on the website** when you're ready. **One exception:** if the Developer plan was already created by the previous update, it has no minimum. Set it on that plan.

## 14. User guide (with screenshots) and PDFs
- **Unzip `user-guide-docs.zip` into the API folder** so it creates `docs\guide` (and refreshes `docs\INTEGRATION.md`). No restart is needed.
- After uploading `fiscalweb.zip`, the guide is at **/app/guide**, with no sign-in needed. It's also in everyone's menu as **User guide**, and on the website under **User guide**. It has tabs for companies, the platform owner and partners, plus a **Download PDF** button.
- The three PDFs are also in `user-guide-pdf`, ready to email or print.

## 15. Invoice builder logo upload, and reseller partners
**Invoice builder:**
- Companies can now upload their **logo** right in the builder (the **Logo** tool, or **Replace logo** / **Remove logo** on the logo).
- They can **drag a picture onto the page**.
- PNG, JPEG, WebP and phone photos are converted and shrunk automatically.

**Reseller partners:** edit a partner under **Administration → Partners** and set **Kind of partner** to **Reseller** with a **discount %**.
- Its companies pay your plan prices less that discount, and see it on their Billing page.
- The reseller earns no commission from you.
- Companies linked before the switch keep their old commission %. To change that, use **Link a company**.


## 16. Accounting connectors: QuickBooks, Xero, Sage and Odoo

Four more paid products on the same model as Zoho Books: the company applies, you approve (with a set-up fee), it switches on when paid, then a monthly fee plus extra documents go on the subscription invoice. Each has its own prices and its own approval.

The database update (migration `AddAccountingConnectors`) adds the product to each application (existing ones become Zoho Books), changes the token columns to text and adds the Odoo connection details. A new menu, **Integration → Accounting connectors**, and a plan module, **Accounting connectors**, are seeded. Plans that list their modules need it ticked under **Plans** before their companies see it.

Set up each product under **Integration → Integrations monitor → QuickBooks, Xero, Sage and Odoo**:

| Product | Where to register the app | Redirect address to give it |
|---|---|---|
| QuickBooks Online | developer.intuit.com → create an app (Accounting scope). Development keys with **Sandbox** ticked first; Production keys need Intuit's production checklist. | `https://<your API host>/api/v1/integrations/connectors/quickbooks/callback` |
| Xero | developer.xero.com/app/manage → New app → **Web app**. Uncertified apps are limited to 25 connected organisations. | `https://<your API host>/api/v1/integrations/connectors/xero/callback` |
| Sage Accounting | developerselfservice.sageone.com → create an app. | `https://<your API host>/api/v1/integrations/connectors/sage/callback` |
| Odoo | Nothing to register: each company enters its own Odoo address, database, user and API key. | — |

The exact redirect address is shown (with a Copy button) on each product's tab. Then set its prices on the same tab, and keep **Charge** off until you have tested it.

Optional settings in appsettings.json:

- `Connectors:Xero:Scopes`: replaces the default Xero sign-in scopes (`offline_access accounting.transactions accounting.contacts.read accounting.settings.read accounting.attachments`). Newer Xero apps may have to use Xero's finer-grained scopes instead.
- `Connectors:Odoo:AllowPrivateAddresses`: `false` by default, so Odoo addresses on private networks are refused. Set it to `true` only if the gateway must reach an Odoo server on your own network.

**Test each one with a test company before charging.** The adapters follow each provider's published API but have not been run against live QuickBooks, Xero, Sage or Odoo accounts.

## 17. Professional chart of accounts and financial reports

The database update (migration `ProfessionalChartOfAccounts`) changes the chart of accounts and the books settings:

- **Chart of accounts:**
  - accounts can sit under a parent;
  - accounts can be **headings**, which take no postings;
  - each account has a place in the statements;
  - account codes can be up to 20 characters.
- **Books settings:** each company gets its own code format, first digits per type, code gap and financial-year start month.
- **Existing companies:**
  - their standard accounts get the roles the system now uses to find them (payable, VAT input, other income, other expenses), so a renumbered chart keeps working;
  - those accounts get their statement classification;
  - each bank-account ledger is moved under **Bank**.

Nothing else needs setting up. Things your accountants will notice:

- **Trial balance:** it now follows the financial year (default January). Income and expense accounts start each year at nil, and earlier years' profit shows in **Retained earnings**. It shows opening balance, period debits and credits, and closing balance.
- **Profit and loss:** it is classified (gross profit, operating profit, profit before tax, net profit) and can show a comparative.
- **Balance sheet:** it splits current from non-current, shows **Current year earnings**, and can show a comparative.
- **Drill-down:** any account in these reports opens its postings, and any posting opens its journal.
- **Chart of accounts screen:** it now has **Numbering**, which covers the code format, renumbering and the financial-year month. It also has **Import** and **Export** (CSV).

## 18. More accounting: cashbook, cost centres, year end, cash flow, statements, exports

The database update (migration `AccountingModules`) adds:

- cost centres;
- a cost centre on journal lines and bills;
- recurring journals;
- closed years;
- cashbook documents (spend money, receive money, transfers).

The menus are seeded. The **Cashbook** menu gets sub-pages: Bank ledger, Spend money, Receive money, Bank transfers, Post to ledger. Four new pages are added: Financial dashboard, Cost centres, Recurring journals and Year end. Two new reports are added: Cash flow and Profit by cost centre.

Nothing to configure. Two things to know:

- Recurring journals are posted by a background job about once an hour. It runs inside the API, so it needs nothing extra.
- Emailing statements uses the server's existing email settings. Without them the screen says so, and the PDF can still be downloaded.

## 19. Going live with ZIMRA: six test documents

The database update (migration `GoLiveZimraTestDocuments`) adds to each go-live request:

- its test documents;
- when it was sent to ZIMRA;
- the live device ZIMRA issued (the activation key is stored encrypted).

**How it works:**

- A company can only ask to go live once it has fiscalised six documents on a demo device: invoice, credit note and debit note in USD and in ZWG.
- Under **Go-live requests** you email them to ZIMRA (the six PDFs are attached) or download them as a zip.
- When ZIMRA issues the live device, you enter its ID and activation key; the company then registers it from its Billing page.

**Settings (optional, in appsettings.json):**

- `GoLive:ZimraEmail`: ZIMRA's address, filled in the first time. After that, the address you last used is remembered.
- `GoLive:RequireTestDocuments`: `true` by default. Set it to `false` only if you ever need to accept requests without the six documents.

Requests made before this update have no test documents; they can still be approved as before.

**After deploying, check Go-live requests → Live.** Companies that existed when go-live approval was first added, and companies you created yourself, were marked approved for live devices without going through ZIMRA. They are flagged **Without the ZIMRA process**. Click **Switch live off** for each one that ZIMRA has not yet given a live device. They keep their demo devices and go live through the six test documents like everyone else. From now on, a company you create also starts on demo.

## 20. Your own books: platform accounting

No database update is needed.

- After the API restarts, the **Accounting** menu appears for the platform administrator (SuperAdmin). This runs once on start-up; if you removed accounting from the SuperAdmin role later, it is not added back.
- The first time you open it, the chart of accounts is created and every billing invoice already issued is posted: income, VAT and payments. If a company was brought by a commission partner, the commission is posted too, and partner payouts already recorded are posted as well.
- Records dated in a period you have since closed are posted on the first open day, and their memo says the original date.

## 21. Accounting for businesses: import balances, invoices from the books, the accounting-only plan

The database update (migration `AccountingForBusinesses`) adds five tables: sales invoices and their lines, exchange rates, and prepayment schedules with their monthly releases. It also adds three settings columns: base currency, invoice footer and accounting policies. Nothing existing changes.

**After the API restarts:**

- New pages appear for everyone who keeps books (company administrators, accountants and you):
  - **Import balances**;
  - **Exchange rates**;
  - **Prepayments and deferrals**;
  - **Audit trail**;
  - two new reports, **Changes in equity** and **Financial statements**.
- **Sales invoices** appears only for companies on an accounting-only plan.
- A plan named **Books (accounting only)** is added, hidden, at 10 USD a month as a placeholder. Under **Plans**, set its price and tick **Show on the website** when you want to offer it.
- Prepayment schedules are released by the same hourly job as recurring journals, so nothing extra is needed.

## 22. Inventory: stock items, GRVs, transfers, stock takes

The database update (migration `InventoryModule`) adds six tables: categories, locations, stock levels, stock documents, their lines, and the stock movements. It also adds columns to items (category, track stock, average and last cost, reorder level and quantity, barcode) and to the accounting settings (inventory start date). Nothing existing changes.

**After the API restarts:**

- An **Inventory** menu appears for company administrators, operators and accountants (viewers see stock items and reports).
- On the plans, **Inventory** is a module like the others: plans with modules ticked need it ticked to offer it. The Books (accounting only) plan gets it by itself.
- The accounts are set up the first time a company uses inventory:
  - the standard chart's *Inventory* (1200) and *Cost of sales* (5010) take those roles;
  - a *Stock adjustments and write-offs* expense account is created.
- Nothing happens to past sales: stock counting starts on the date of a company's first GRV or opening stock. From then on, fiscal sales of stock items take stock out and post cost of sales automatically.
