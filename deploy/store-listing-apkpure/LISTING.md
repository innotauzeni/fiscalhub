# FiscalHub: APKPure store listing

## Quick guide: Add Application, field by field

Open the matching file in `paste/`, select everything (Ctrl+A), copy, and paste it into the field.

| Form field | Paste from / upload |
|---|---|
| App or game | `paste/01-app-type.txt` (App) |
| App name | `paste/02-app-name.txt` |
| Package name | `paste/03-package-name.txt` (must match the APK exactly) |
| Category | `paste/04-category.txt` |
| Tags / keywords | `paste/05-tags.txt` |
| Short description | `paste/06-short-description.txt` |
| Long description / about | `paste/07-long-description.txt` |
| What's new / release notes | `paste/08-whats-new.txt` |
| Content rating / audience | `paste/09-content-rating.txt` |
| Privacy policy URL | `paste/10-privacy-policy-url.txt` |
| Website | `paste/11-website-and-terms.txt` |
| Developer name, email, website | `paste/12-developer-info.txt` (put in your support email) |
| Version, price, ads, Android version, languages | `paste/13-app-details.txt` |
| Permissions explanation (if asked) | `paste/14-permissions.txt` |
| Data collection / privacy questions (if asked) | `paste/15-data-safety.txt` |
| App icon (512 × 512) | `icon-512x512.png` |
| Banner / feature graphic (1024 × 500) | `feature-graphic-1024x500.png` |
| Screenshots | everything in `screenshots/`, in number order 01 to 07 |
| APK (Manage Versions → Select files) | `mobile/fdms_mobile/build/app/outputs/flutter-apk/app-release.apk`, signed with your own key (see the end of this file) |

Then click **Submit for review**. APKPure reviews every app by hand; if it is not approved within about 3 business days, check your email for their reasons.

---


Copy each block into the matching field of the APKPure developer console (developer.apkpure.com). The pictures are in this folder.

---

## App details

| Field | Value |
|---|---|
| App name | **FiscalHub** |
| Package name | `zw.co.daraerp.fdms_mobile` |
| Version | 1.0.0 (version code 1) |
| Category | **Business** (if asked for a second: Finance) |
| Price | Free |
| Content rating | Everyone / 3+ (no violence, no user-generated public content, no ads) |
| Languages | English |
| Website | https://fiscal.daraerp.co.zw |
| Privacy policy | https://fiscal.daraerp.co.zw/privacy |
| Terms | https://fiscal.daraerp.co.zw/terms |
| Developer / company | Daraerp Private Limited |
| Support email | *(your support address, e.g. support@daraerp.co.zw)* |
| Country | Zimbabwe |

Before publishing, open https://fiscal.daraerp.co.zw/privacy and check it shows your privacy policy. Stores reject an empty page. You can edit it in the console under **Settings → Legal**.

---

## Short description (max 80 characters)

```
ZIMRA fiscal invoices from your phone: FDMS compliant, Zoho Books ready.
```

## Tags / keywords

```
ZIMRA, FDMS, fiscalisation, fiscal invoice, tax invoice, VAT, Zimbabwe, invoicing, receipts, point of sale, Zoho Books, accounting, credit note, fiscal device, business
```

## Full description

```
FiscalHub puts ZIMRA fiscalisation in your pocket. Issue fiscal tax invoices, credit notes and debit notes that are signed and sent to ZIMRA's FDMS in seconds, wherever you are, without a fiscal printer on every counter.

WHY FISCALHUB
• ZIMRA FDMS compliant: every invoice is signed by your registered fiscal device and accepted by ZIMRA, with the QR code and verification code your customers can check.
• Works the way you sell: quick sale at the counter or a full invoice with customer TIN and VAT number.
• Fiscal days handled for you: days open automatically with your first sale and close at the end of the day, with ZIMRA's counters checked.
• Never lose a sale: if the internet drops, receipts wait and are sent the moment you are back online.

WHAT YOU CAN DO
• Fiscal tax invoices, credit notes and debit notes, in USD, ZWG and other currencies
• Share the fiscal PDF by WhatsApp or email, or send a link your customer opens in the browser
• Design your own A4 invoice: logo, colours, fonts and layout, always titled FISCAL TAX INVOICE
• Customers and items with HS codes and VAT rates, ready to pick
• Several devices and branches, with each user limited to their own tills
• Approvals: credit notes and other sensitive actions can wait for a manager's OK
• Dashboard of today's sales, pending receipts and what needs your attention
• Notifications for rejected receipts, expiring certificates, approvals and payments
• Reports and VAT summaries for your returns

CONNECTED
• Zoho Books: connect once and the invoices and credit notes you raise in Zoho are fiscalised automatically, with the fiscal number, verification code and PDF written back to Zoho.
• API and webhooks for your own ERP, shop or POS system.

SECURE
• Two-step sign-in and passkeys
• Your company's data stays separate from everyone else's
• Encrypted connections and an audit trail of who did what

GETTING STARTED
Create your company in the app, confirm your email, and use the free trial. Register your ZIMRA fiscal device with the activation key ZIMRA gave you, then issue your first fiscal invoice. Start on a ZIMRA test device and switch to production when you are approved.

FiscalHub is an independent product by Daraerp Private Limited and is not affiliated with or endorsed by the Zimbabwe Revenue Authority (ZIMRA).
```

## What's new (release notes for 1.0.0)

```
First release of FiscalHub:
• Fiscal tax invoices, credit and debit notes signed by ZIMRA FDMS
• Automatic fiscal days and offline queue
• Invoice designer with your own A4 layout
• Zoho Books integration
• Notifications, approvals, reports and VAT summaries
```

---

## Pictures

| Store field | File | Size |
|---|---|---|
| Icon | `icon-512x512.png` | 512 × 512 |
| Feature graphic / banner | `feature-graphic-1024x500.png` | 1024 × 500 |
| Screenshot 1 | `screenshots/01-welcome.png` | 1080 × 2160 |
| Screenshot 2 | `screenshots/02-dashboard.png` | 1080 × 2160 |
| Screenshot 3 | `screenshots/03-receipts.png` | 1080 × 2160 |
| Screenshot 4 | `screenshots/04-fiscal-invoice.png` | 1080 × 2160 |
| Screenshot 5 | `screenshots/05-zoho-books.png` | 1080 × 2160 |
| Screenshot 6 | `screenshots/06-invoice-designer.png` | 1080 × 2160 |
| Screenshot 7 | `screenshots/07-notifications.png` | 1080 × 2160 |

The screenshots show the real app screens with demo data (a sample shop, "Moyo Retail"), not anyone's real records.

To make them again after changing the app:

```
cd mobile/fdms_mobile
flutter test test/store_shots_test.dart --update-goldens --dart-define=STORE_SHOTS=true
```

They are written to `test/store/`.

---

## Before you build the APK: your release key (one time only)

Stores accept an APK signed with any key. **Every later update must be signed with the same key**, so make your own now and keep it safe. If you lose it, you can't update the app; you'd have to publish a new one.

1. Create the key. Run this once in a terminal and choose two passwords:

   ```
   keytool -genkey -v -keystore C:\keys\fiscalhub-upload.jks -keyalg RSA -keysize 2048 -validity 10000 -alias fiscalhub
   ```

   `keytool` comes with Java and Android Studio. If it isn't found, use `"C:\Program Files\Android\Android Studio\jbr\bin\keytool.exe"`.
2. Create `mobile/fdms_mobile/android/key.properties` with:

   ```
   storePassword=YOUR_STORE_PASSWORD
   keyPassword=YOUR_KEY_PASSWORD
   keyAlias=fiscalhub
   storeFile=C:/keys/fiscalhub-upload.jks
   ```

   This file and the `.jks` are already excluded from git.
3. Build as usual:

   ```
   flutter build apk --release
   ```

   The project now signs release builds with your key whenever `key.properties` exists. Without the file it still uses the debug key.
4. Back up `fiscalhub-upload.jks` and both passwords somewhere safe, outside this computer.

For every new upload, raise `version:` in `pubspec.yaml` (for example `1.0.1+2`). The number after `+` must always go up.

The APK to upload is `mobile/fdms_mobile/build/app/outputs/flutter-apk/app-release.apk`.
