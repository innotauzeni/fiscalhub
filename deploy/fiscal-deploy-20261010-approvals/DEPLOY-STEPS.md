# Deployment, 2026-10-11: approvals, revenue, notifications, email settings, backups, security headers, invoice builder, Zoho Books, Zoho bridge for partners

| File | Goes to |
|---|---|
| `fdms-update-approvals.sql` | HeidiSQL, live database `fdms_gateway` (run first) |
| `fiscalapi-files.zip` (18 files + `docs/INTEGRATION.md`) | the API folder (https://fiscalapi.daraerp.co.zw), keeping the `docs` folder |
| `fiscalweb.zip` | the `fiscalweb` folder (https://fiscal.daraerp.co.zw) |
| `fdms-gateway.apk` | phones |

## 1. Database
Back up `fdms_gateway`, then run `fdms-update-approvals.sql`.
- It adds the approval, sign-up, template picture and Zoho Books tables (`fdms_integration_connections`, `fdms_integration_documents`) and the bridge tables (`fdms_bridge_keys`, `fdms_bridge_grants`, `fdms_bridge_endpoints`, `fdms_bridge_events`). It also contains the referral and coins tables from the last two releases.
- It skips anything already there, so it is safe whichever earlier scripts you ran, and safe to run twice.

## 2. API
1. Stop the API's application pool.
2. Copy all 18 files from `fiscalapi-files.zip` into the API folder, replacing the old ones.
   - This time it is not only the Fdms DLLs. The mail and security libraries were updated to versions with no known vulnerabilities (MailKit, MimeKit, BouncyCastle, System.Security.Cryptography.\*), together with `Fdms.Api.deps.json`.
   - Copy every file. If you copy only the DLLs, the API will not start.
3. Optional: add a second backup location to `appsettings.json` (see section 6).
4. Start the pool. https://fiscalapi.daraerp.co.zw/health should answer `Healthy`.
5. **Sign out and back in** on the web and the phone. New permissions (`notifications.view`, `approvals.*`, `revenue.view`, `integrations.manage`, `integrations.monitor`) only reach a session at sign-in.

## 3. Web console
Empty the `fiscalweb` folder and unzip `fiscalweb.zip` into it. Then press Ctrl+F5 in the browser.

## 4. Email: your address for platform alerts
Sign in as the platform administrator, then go to **Settings → Email**.
- **Mail server, port, user, password, sender.** What you save here replaces the mail settings in appsettings.json. The password is stored encrypted and is never shown again.
- **Alert addresses.** Put your own email here. You are emailed when:
  - a company registers,
  - a bank, EcoCash or PayPal payment is waiting for you to confirm,
  - a company asks to go live,
  - a payment is received,
  - a backup fails.
- **Send test.** Sends a test message using the saved settings.

## 5. What shows where

**Platform (you):**
- The bell at the top shows the platform alerts listed in section 4.
- The Overview page has four cards: *New applications*, *Payments to confirm*, *Recurring / month* and *Collected this month*. Below them is a *Platform notifications* panel; each item has a Review / Confirm button that opens the right page.
- **Revenue** (new menu item): money per month and per year, collected and outstanding, and plans. It also lists who pays, who owes, payment methods, and what the platform owes in coins and commission.

**Companies:**
- The bell and a *Needs your attention* panel on the dashboard show:
  - an invoice ready to pay (Pay now),
  - a payment received and being checked,
  - a payment approved, or a payment rejected with the reason,
  - approvals waiting for them,
  - device alerts.
- The bell still works while an account is locked for an unpaid invoice, so the company can see what it owes.

**Phone app:** the same panels on the home screens, a tappable bell list, and Approvals, Approval matrix and Revenue screens. Email settings are under Settings.

## 6. Backups: never lose data
Backups run every 24 hours and keep the last 14. Each one is now:
- **checked**: read back after writing; a cut-off or corrupt file is marked Failed, not trusted;
- **copied to a second place**, if you set one. Add this to `appsettings.json`:
  ```json
  "Backup": { "MirrorPath": "D:\\fdms-backups" }
  ```
  Use another disk, a network share, or a folder synced to OneDrive or Google Drive. The service account of the API's application pool must be able to write there.
- **alerted**: if a backup or its copy fails, you get a bell notification and an email.

Also have your hosting provider snapshot the server, or MySQL, daily. Two independent copies beat one.

## 7. Approval workflow (maker-checker)
Each company sets up its own rules under **Administration → Approvals → Approval matrix**:
- **What it covers:** credit notes, debit notes, cancelling a held receipt, entering a bill, paying a bill, and manual journals.
- **When it applies:** from an amount you choose (optionally per currency).
- **Who approves:** up to 5 levels of roles, in order, for example Accountant, then ClientAdmin.

When an action is held:
- The person who asked sees "Sent for approval". The approvers get a notification.
- When the last level approves, the action runs by itself. Every decision and note is kept.
- Nobody can approve their own request, and one person cannot approve two levels of the same request.

## 8. Keep it up all the time (IIS)
On the API's application pool:
- **Start Mode** = *AlwaysRunning*, **Idle Time-out** = 0, **Regular Time Interval (recycling)** = 0, with one fixed recycle at 03:00.
- On the site: **Preload Enabled** = True. Install the IIS feature *Application Initialization*.

Watch it from outside:
- Add https://fiscalapi.daraerp.co.zw/health to a free uptime monitor (UptimeRobot, Better Stack) that emails or SMSes you.

Release safely:
- Back up first (section 6), and deploy outside business hours.
- The app pool stops for under a minute.
- Receipts queued during that minute are sent automatically afterwards.

## 9. Security checklist (before a penetration test)
Already in place:
- **SQL injection.** Every query is parameterised by Entity Framework or MySqlConnector, with no hand-built SQL from user input. Backup table names come from MySQL itself and are quoted.
- **Sign-in protection:**
  - accounts lock for 15 minutes after 5 failed sign-ins;
  - sign-in and API calls are rate-limited;
  - two-step sign-in and passkeys are available, and companies can require them;
  - refresh tokens are rotated and stored only as hashes.
- **Access control:**
  - every endpoint has a permission check, enforced by an automated test;
  - companies can never see each other's data (tested);
  - API tokens work for one device only.
- **Secrets:** passwords are hashed with ASP.NET Identity. API tokens, activation codes and refresh tokens are stored as hashes. The mail password and gateway keys are encrypted.
- **Uploads:** proofs of payment are checked by their content (PDF, PNG, JPEG or WebP, at most 5 MB), not by their file name.
- **Security headers (new)**, on every API answer:
  - `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`;
  - a `Permissions-Policy`, and `Cache-Control: no-store` on API data;
  - HSTS over HTTPS.
  The web console also has a strict Content-Security-Policy.
- **Packages:** `dotnet list package --vulnerable` reports none.

Your part on the server:
- **HTTPS:** redirect HTTP to HTTPS on both sites, and disable TLS 1.0/1.1 (IIS Crypto, "Best Practices" template).
- **Firewall:** do not expose MySQL (3306) to the internet. Only ports 443 and 80 should be open.
- **Database user:** the API should use its own MySQL user with rights on `fdms_gateway` only, not root.
- **Files:** keep `appsettings.json` readable only by the app pool identity.
- **Production setting:** `ASPNETCORE_ENVIRONMENT` must be `Production`. That turns off detailed error pages.
- **API reference (Swagger):** it is public on purpose, so integrators can read it. It lists endpoints but gives no access: every call still needs a token. Tell the testers it is intentional.

## 10. FiscalHub logo and icons
- **Web console:** the logo is in the sidebar, the sign-in page and the public site header and footer. The browser tab shows the FiscalHub favicon, and the installed app (PWA) uses the FiscalHub icon and name.
  - Browsers keep old icons for a while: press Ctrl+F5.
  - An already-installed web app picks up the new icon the next time it updates itself, within about a day.
- **Phone:** the app is now called *FiscalHub*, with the new launcher icon and the logo on the sign-in screens and in the menu.
  - Install the new `fdms-gateway.apk` over the old one. Phones keep their sign-in.
- Companies that uploaded their own logo still see theirs in the sidebar.

## 11. Sign-up without waiting, user names, and unpaid demos
**New companies open at once.** Registering now works like this:
1. The company fills in the form. They can also choose a **user name** (for example *Inno*) to sign in with instead of their email.
2. We email a **6-digit code**. It works for 15 minutes, allows 5 wrong tries, and can be resent once a minute.
3. When the code is entered, the company, its administrator and a **free demo** are created at once, with no approval needed.
   - The **first invoice is raised straight away**. Paying early never loses demo days: the paid period starts when the demo ends.
   - You get a bell notification and an email: *New company signed up*.

**User names and email:**
- Anyone can set or change their user name and email under **My profile → How you sign in**.
- Changing the email asks for the password, then a code sent to the *new* address. The old address is told about the change.
- Administrators can edit a colleague's email and user name in **Users**.
- *Forgot password* accepts the user name too.

**Unpaid demos are deleted**, with all their data, **7 days after the demo ends**:
- The company is warned by bell and email when the demo ends, and again 2 days before deletion.
- Never deleted: a company that ever paid, one approved for live devices, or one you mark **Keep**.
- See and control them under **Subscriptions → Unpaid demo accounts** (web) or the **Unpaid demos** tab (phone).
- Registrations whose code was never entered are removed after 7 days.
- A daily backup is still taken, so a deleted demo can be recovered from the last backup if really needed.

Optional settings in `appsettings.json` (the defaults are shown):
```json
"Onboarding": {
  "SelfService": true,          // false = every registration waits for your approval, as before
  "TrialDays": 14,              // demo length; leave out to use each plan's trial days
  "DeleteUnpaidDemo": true,     // false = never delete unpaid demos
  "DeleteUnpaidAfterDays": 7
}
```

## 12. Invoice builder (design your own invoice)
**Templates → Open the invoice builder** on the web, or the builder button next to a template on the phone. Then:

- **Start from a design:** Classic, Elegant pink (like a beauty brand's invoice), Modern dark, Minimal, or a blank page.
- **Place anything anywhere** on the A4 page:
  - text with invoice values, written `{invoiceNo}`, `{buyerName}`, `{total}` and so on;
  - single values such as invoice number, date, customer or bank;
  - the logo and pictures you upload (a full-page background, a signature or a stamp, PNG or JPEG up to 2 MB);
  - boxes and circles with colours, gradients and rounded corners, and lines;
  - the items table, with columns of your choice and your own headings;
  - the totals, the ZIMRA QR code and the fiscal details.
- **Web:** drag to move, pull the handles to resize. It snaps to a grid and lines up with other elements. Undo/redo, copy, duplicate, lock, front/back and alignment tools are all there.
- **Phone:** tap an element, drag it, pull its corner to resize, and open **Properties** to change it.
- **Required by ZIMRA:** the title, the items table, the totals, the QR code and the fiscal details must be on the page; saving is refused without them.
- **The title always says FISCAL TAX INVOICE** (FISCAL TAX CREDIT NOTE / DEBIT NOTE for notes). A custom title must contain those words, for example "FISCAL TAX INVOICE - COPY". A heading that says only "INVOICE" or "TAX INVOICE" is refused.
- **Save** makes it the look of every A4 invoice of the company, or of a branch if it's a branch template. The 80 mm roll keeps the standard layout.
- If a design ever cannot be drawn, the invoice still prints, in the standard layout.

**Exports:**
- **Sample invoice** as PDF, Word (.docx) or JSON, from the builder's Export menu.
- **Template file** (.json, with its logo and pictures): download it, then load it into another template or another company with **Import a template file**.
- **Every real invoice** can also be downloaded as Word (`GET /api/v1/receipts/{id}/docx`) or as JSON data for other systems (`GET /api/v1/receipts/{id}/document`). See *The invoice as data* in the integration guide.

**Note:** the Word file passes the Office schema check. Open one in your version of Word once before relying on it, because I could not test it in Word itself.

## 13. Zoho Books (plug and play, charged per connection)
Companies that keep their books in Zoho Books connect once. After that, their invoices and credit notes are fiscalised here and the ZIMRA result is written back into Zoho.

**A. One-time setup, by you (platform administrator)**
1. Go to https://api-console.zoho.com and choose **Add client → Server-based Applications**.
   - Name: FiscalHub. Homepage: https://fiscal.daraerp.co.zw.
   - Redirect URI: `https://fiscalapi.daraerp.co.zw/api/v1/integrations/zoho/callback`. The exact value is shown on the monitor page; copy it from there.
2. Open the app's **Settings** tab and switch on **multi-DC**. Without it, only accounts on zoho.com can connect.
3. In the console, open **Administration → Integrations monitor → Zoho app**. Paste the client ID and client secret, then save. The secret is stored encrypted and never shown again.
4. In the same page, under **What a connection costs**, tick **Charge for connections** and set the monthly fee, the documents included and the price per extra document.
   - These charges are added to the company's next subscription invoice, and only when its plan is billed in the same currency.

**B. What a company does (Integration → Zoho Books)**
1. Choose the Zoho data centre and click **Connect**. They sign in to Zoho and allow access.
2. Choose the organisation, if the Zoho account has several.
3. Fill in the settings:
   - the fiscal device;
   - how sales are paid (default: Credit, on account);
   - an HS code for lines without one;
   - the date to start from (default: today, so old invoices are left alone).
4. Turn on **Fiscalise automatically** if they want it.
5. Optional, for custom fields: in Zoho Books, add invoice custom fields named **Fiscal Number**, **Verification Code** and **ZIMRA QR**. They are filled in automatically.
6. Optional, for instant sync: in Zoho Books, add a workflow rule on invoices and credit notes that calls a webhook. The webhook address is shown on the page. Without it, the gateway checks every 5 minutes.

**What happens to each document**
- Drafts and voided documents are not taken.
- An invoice is fiscalised once only. A repeat returns the same fiscal receipt.
- A credit note needs its invoice fiscalised first.
- If the fiscal total differs from Zoho's total by more than 0.05, the document is held as **Failed**, with the reason. The usual cause is a wrong VAT rate.
- If an invoice is edited in Zoho after it was fiscalised, it is flagged **Changed after**. The fiscal record cannot change, so the company issues a credit note.
- After fiscalising, the gateway writes back to the Zoho document:
  - a comment with the fiscal number, verification code and link;
  - the fiscal PDF, as an attachment;
  - the custom fields, if the company created them.

**Token health**
- Zoho's access token is renewed automatically about every hour.
- If a company revokes access, or Zoho stops accepting the token:
  - the connection turns **Needs reconnect**;
  - the company gets a bell alert and an email;
  - it appears under **Need attention** on your monitor page.

**Monitor**
**Administration → Integrations monitor** shows every connection with:
- token renewals;
- the last sync;
- Zoho calls today;
- documents fiscalised this month, waiting and failed;
- what connections are earning this month.

**Not tested against live Zoho:** the Zoho side was tested with a simulated Zoho. Before charging anyone, connect a real Zoho Books trial organisation and fiscalise one test invoice on a **test** device. That confirms the Zoho app setup and your region.

## 14. Zoho bridge: the gateway as middleman for partners
A partner (the other team, an ERP vendor, an accounting firm) can read and write the Zoho Books of the companies they serve **through your gateway**. FiscalHub keeps the Zoho sign-in, and you control access, see the traffic and charge for it.

**Your part (platform)**
1. Create the partner under **Administration → Partners**, and a staff login for them.
2. Their code is the partner code shown there.
3. Set the price in **Integrations monitor → What a connection costs → Zoho bridge: monthly fee per company that shares**, and tick **Charge for connections**. The fee is added to the subscription invoice of each company that shares with a partner. Partners have no invoices of their own.
4. Watch the traffic in **Integrations monitor → Zoho bridge (partners)**: keys, companies, calls today, changes waiting or failed.

**The company's part** (Integration → Zoho Books → Partner access)
1. Enter the partner's code.
2. Tick the modules, for example contacts, items and invoices.
3. Choose read only, or read and write. Usage is shown, and access can be withdrawn at any time.
4. **Companies that connected Zoho before this update must disconnect and connect again once.** The bridge needs full Zoho Books access, and the older connection only asked for invoices and contacts. The page says so.

**The partner's part** (Partner → Zoho bridge)
1. Create a key for their system.
2. Optionally set a webhook address and the modules to watch.
3. Follow the integration guide, section "Zoho bridge (for partners)": `docs/INTEGRATION.md`, also in the console under Developer docs.

**Limits:**
- 60 calls a minute per key. Change it with `Integrations:BridgePerMinute`.
- Changes are checked every 15 minutes. Change it with `Integrations:BridgeWatchMinutes`.
- Deleting in Zoho is not offered, and deletions are not detected.
- Zoho's own daily call limit for each organisation still applies; it depends on the company's Zoho plan.

## 15. A company can use its own Zoho app (Self Client)
A company doesn't need your shared Zoho app. On **Integration → Zoho Books**, it picks **Use my own Zoho app**:
1. At https://api-console.zoho.com, signed in as its Zoho Books admin, it chooses **Add Client → Self Client**.
2. It copies the **Client ID** and **Client Secret** into the page.
3. Under **Generate Code**, it creates a code with scope `ZohoBooks.fullaccess.all` and a 10-minute duration, pastes it, and clicks **Connect** straight away. A code works once.

How it behaves:
- No redirect address is needed, so it also works on localhost.
- The secret is stored encrypted, and token renewal uses the company's own app.
- **Remove my app** goes back to the shared app.
- A company with a server-based app can leave the code empty. It then signs in through Zoho with the redirect address shown on the page.

**Check your shared app's client ID:** it must start with `1000.`. A stray character in front (for example `V1000.…`) makes Zoho show "General Error". Such IDs are now refused when saving, and the app shows as "Not set up".
