# FDMS Gateway mobile app (Flutter)

The web console, on a phone. It talks to the live gateway at **https://fiscalapi.daraerp.co.zw** (API reference: `/swagger/index.html`).
Change the address for a build with `--dart-define=API_BASE_URL=https://...`, or at run time by long-pressing the logo on the sign-in screen.

## What it does
Everything is driven by the same menu tree the web console gets at sign-in, so each role sees only its own modules, and a module added on the server shows up in the drawer.

* **Sign-in and sign-up**: register a company (with a referral code) right in the app; password, then the 6-digit code when two-step sign-in is on; forced password change; forced security set-up when the company requires extra protection; session kept in the phone's secure storage and refreshed automatically.
* **Fiscal work**: dashboard, devices (register, rename, ping, refresh, renew certificate, offline files, stock), fiscal days (open/close), bulk import from Excel, PDF or Word (check, then send to ZIMRA), receipts (search, detail with QR, PDF open/share, share link, email, release/cancel/retry, credit and debit notes), new invoice and quick sale with preview, customers, items, quotes (and turn into invoice), recurring invoices, reports, AI report builder, compliance, health, alerts, activity, API tokens, webhooks.
* **Accounting**: chart of accounts, journals, cashbook, bank reconciliation (summary, auto-match, CSV import), reports (ledger, trial balance, profit and loss, balance sheet, ageing), suppliers, bills (enter, pay, void), customers owing (receive payment), fixed assets, VAT return and settlement, budgets, documents (upload, open, share, delete).
* **Administration**: users, roles, branches, banks, billing (invoices, PDF, pay with EcoCash/card), notifications, My profile (details, password, authenticator app, sessions), invoice template designer (colours, logo, columns, words, live A4 / 80 mm preview, copy an existing invoice with AI), roles & access (permissions and menus per role), menus & modules, **Refer and earn** (your code, share link, levels, rewards).
* **Platform operator**: overview, clients, applications, plans, subscriptions, payments, go-live, audit log (timeline with filters), system errors (severity counts, filters, stack trace, mark fixed), backups, currencies, usage analytics, partners, referral program (rules, levels, pay out rewards), settings (public addresses, billing details, payment gateways, AI assistant, terms / privacy / developer details).
* **Partner portal**: overview with the referral link, companies, commission.
* **Everywhere**: terms and conditions, privacy policy and developer details (sign-in page, drawer, account menu), the developer documentation as a searchable in-app reader with copyable code.

Nothing sends you to the browser: every page of the web console has a native screen. Only adding a passkey needs the browser, because passkeys are tied to a website.

## Fits any screen
No fixed sizes: layouts use the available width and the user's text size (grids reflow, rows shrink or wrap, charts scale). `test/responsive_test.dart` draws every main screen and form (including the template designer, audit log, system errors, referrals, registration, legal pages and docs) at 320x480, 360x640 with 160% text, 375x667 with 200% text, a large phone and a tablet, and fails on any overflow.

## Run, test, build
```
flutter pub get
flutter run                      # on a connected phone or emulator
flutter test                     # responsive and API-mapping tests
flutter build apk --release      # Android (accept licences once: flutter doctor --android-licenses)
flutter build appbundle          # for the Play Store
flutter build ios                # on a Mac
```
Android needs the INTERNET permission (already added) and `minSdk 24`. For a release build create a signing key and reference it from `android/app/build.gradle.kts`.
