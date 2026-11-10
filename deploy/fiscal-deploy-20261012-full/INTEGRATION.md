# Integration guide

FiscalHub fiscalises your sales with ZIMRA. Your system sends a sale over HTTPS. FiscalHub works out the tax, numbers and signs the receipt, sends it to ZIMRA, and gives you back the fiscal number, the verification QR code and a printable PDF. Your system never talks to ZIMRA and never handles certificates.

```
 your system  ──HTTPS + token──▶  FiscalHub  ──signed receipt──▶  ZIMRA FDMS
   (the sale)                     (tax, numbering, signing,       (fiscal number,
        ▲                          fiscal day, retries, PDF, QR)    validation)
        └──────── webhook (optional): "ZIMRA accepted / rejected it" ◀───────┘
```

**It works with any programming language.** The API is plain HTTPS with JSON, so anything that can make a web request can use it: PHP, Python, JavaScript/Node, C#/.NET, Java, Go, Ruby, Delphi, VB, even Excel or Postman. Every example below has a tab for each common language. Pick yours once and the page remembers it.

Base URL used below: `https://your-gateway.example.com`. When you read this from the gateway itself, that is replaced with the real address, so the examples work as written.
Full reference of every endpoint: [API reference](https://your-gateway.example.com/swagger/index.html) (interactive) and [OpenAPI document](https://your-gateway.example.com/swagger/v1/swagger.json). Most languages can generate a ready-made client from the OpenAPI document.

> **Tip:** using Zoho Books? You may not need to write any code. Jump to *Zoho Books: which option is for you*.

## Quick start

You need three things:

| What | Where to get it |
|---|---|
| **Base URL** | `https://your-gateway.example.com` |
| **Token** (starts with `fdms_`) | Console → *API tokens* → *New token*. Pick the device and tick what the token may do. The token is shown **once**: copy it and keep it on your server. |
| **Device id** | Shown next to the token, or returned by step 1 below. |

That's all. Three calls take you from nothing to a printed fiscal invoice.

**Step 1. Check the token works and find your device id.**

```bash
curl "https://your-gateway.example.com/api/v1/devices" -H "Authorization: Bearer fdms_your_token"
```

```python
import requests
r = requests.get("https://your-gateway.example.com/api/v1/devices", headers={"Authorization": "Bearer fdms_your_token"})
print(r.status_code, r.json())   # [ { "id": "9e840039-...", "zimraDeviceId": 12345, ... } ]
```

```js
const r = await fetch('https://your-gateway.example.com/api/v1/devices', { headers: { Authorization: 'Bearer fdms_your_token' } });
console.log(r.status, await r.json());   // [ { id: '9e840039-...', zimraDeviceId: 12345, ... } ]
```

```php
<?php
$ch = curl_init('https://your-gateway.example.com/api/v1/devices');
curl_setopt_array($ch, [CURLOPT_RETURNTRANSFER => true, CURLOPT_HTTPHEADER => ['Authorization: Bearer fdms_your_token']]);
$devices = json_decode(curl_exec($ch), true);
echo $devices[0]['id'];
```

```csharp
using var http = new HttpClient { BaseAddress = new Uri("https://your-gateway.example.com/") };
http.DefaultRequestHeaders.Add("Authorization", "Bearer fdms_your_token");
Console.WriteLine(await http.GetStringAsync("api/v1/devices"));
```

```java
// Java 11+
var http = java.net.http.HttpClient.newHttpClient();
var req = java.net.http.HttpRequest.newBuilder(java.net.URI.create("https://your-gateway.example.com/api/v1/devices"))
    .header("Authorization", "Bearer fdms_your_token").build();
System.out.println(http.send(req, java.net.http.HttpResponse.BodyHandlers.ofString()).body());
```

```go
req, _ := http.NewRequest("GET", "https://your-gateway.example.com/api/v1/devices", nil)
req.Header.Set("Authorization", "Bearer fdms_your_token")
res, err := http.DefaultClient.Do(req)
if err != nil { log.Fatal(err) }
body, _ := io.ReadAll(res.Body)
fmt.Println(res.StatusCode, string(body))
```

You get a list with one device. Its `id` is your **device id**. A `401` means the token is wrong.

**Step 2. Send a sale.** This is the main call. The example sells one pair of shoes at 115.00 including 15% VAT.

```bash
curl -X POST "https://your-gateway.example.com/api/v1/devices/YOUR_DEVICE_ID/receipts" \
  -H "Authorization: Bearer fdms_your_token" -H "Content-Type: application/json" \
  -d '{
    "externalRef": "SALE-1001",
    "invoiceNo": "INV-1001",
    "receiptType": "FiscalInvoice",
    "currency": "USD",
    "linesTaxInclusive": true,
    "autoOpenDay": true,
    "lines": [ { "name": "Shoes", "hsCode": "64039900", "quantity": 1, "unitPrice": 115, "taxPercent": 15 } ]
  }'
```

```python
import requests

sale = {
    "externalRef": "SALE-1001",          # your own id for the sale: makes retries safe
    "invoiceNo": "INV-1001",
    "receiptType": "FiscalInvoice",
    "currency": "USD",
    "linesTaxInclusive": True,
    "autoOpenDay": True,
    "lines": [{"name": "Shoes", "hsCode": "64039900", "quantity": 1, "unitPrice": 115, "taxPercent": 15}],
}
r = requests.post("https://your-gateway.example.com/api/v1/devices/YOUR_DEVICE_ID/receipts",
                  headers={"Authorization": "Bearer fdms_your_token"}, json=sale, timeout=60)
if r.status_code in (201, 202):
    receipt = r.json()
    print("Fiscalised:", receipt["receiptGlobalNo"], receipt["qrUrl"])
else:
    err = r.json()
    print("Refused:", err.get("errorCode"), err.get("title"))
```

```js
// Node 18+ or any browser-free JavaScript runtime
const sale = {
  externalRef: 'SALE-1001',            // your own id for the sale: makes retries safe
  invoiceNo: 'INV-1001',
  receiptType: 'FiscalInvoice',
  currency: 'USD',
  linesTaxInclusive: true,
  autoOpenDay: true,
  lines: [{ name: 'Shoes', hsCode: '64039900', quantity: 1, unitPrice: 115, taxPercent: 15 }],
};
const r = await fetch('https://your-gateway.example.com/api/v1/devices/YOUR_DEVICE_ID/receipts', {
  method: 'POST',
  headers: { Authorization: 'Bearer fdms_your_token', 'Content-Type': 'application/json' },
  body: JSON.stringify(sale),
});
const data = await r.json();
if (r.status === 201 || r.status === 202) console.log('Fiscalised:', data.receiptGlobalNo, data.qrUrl);
else console.log('Refused:', data.errorCode, data.title);
```

```php
<?php
$sale = [
  'externalRef' => 'SALE-1001',        // your own id for the sale: makes retries safe
  'invoiceNo' => 'INV-1001',
  'receiptType' => 'FiscalInvoice',
  'currency' => 'USD',
  'linesTaxInclusive' => true,
  'autoOpenDay' => true,
  'lines' => [ ['name' => 'Shoes', 'hsCode' => '64039900', 'quantity' => 1, 'unitPrice' => 115, 'taxPercent' => 15] ],
];
$ch = curl_init('https://your-gateway.example.com/api/v1/devices/YOUR_DEVICE_ID/receipts');
curl_setopt_array($ch, [
  CURLOPT_POST => true, CURLOPT_RETURNTRANSFER => true, CURLOPT_TIMEOUT => 60,
  CURLOPT_HTTPHEADER => ['Authorization: Bearer fdms_your_token', 'Content-Type: application/json'],
  CURLOPT_POSTFIELDS => json_encode($sale),
]);
$data = json_decode(curl_exec($ch), true);
$status = curl_getinfo($ch, CURLINFO_HTTP_CODE);
if ($status == 201 || $status == 202) echo "Fiscalised: {$data['receiptGlobalNo']} {$data['qrUrl']}";
else echo "Refused: {$data['errorCode']} {$data['title']}";
```

```csharp
using System.Net.Http.Json;
using System.Text.Json;

using var http = new HttpClient { BaseAddress = new Uri("https://your-gateway.example.com/") };
http.DefaultRequestHeaders.Add("Authorization", "Bearer fdms_your_token");

var sale = new
{
    externalRef = "SALE-1001",           // your own id for the sale: makes retries safe
    invoiceNo = "INV-1001",
    receiptType = "FiscalInvoice",
    currency = "USD",
    linesTaxInclusive = true,
    autoOpenDay = true,
    lines = new[] { new { name = "Shoes", hsCode = "64039900", quantity = 1, unitPrice = 115m, taxPercent = 15m } },
};
var res = await http.PostAsJsonAsync("api/v1/devices/YOUR_DEVICE_ID/receipts", sale);
var data = await res.Content.ReadFromJsonAsync<JsonElement>();
if ((int)res.StatusCode is 201 or 202) Console.WriteLine($"Fiscalised: {data.GetProperty("receiptGlobalNo")} {data.GetProperty("qrUrl")}");
else Console.WriteLine($"Refused: {data.GetProperty("errorCode")} {data.GetProperty("title")}");
```

```java
// Java 17+
import java.net.URI;
import java.net.http.*;

var sale = """
    { "externalRef": "SALE-1001", "invoiceNo": "INV-1001", "receiptType": "FiscalInvoice", "currency": "USD",
      "linesTaxInclusive": true, "autoOpenDay": true,
      "lines": [ { "name": "Shoes", "hsCode": "64039900", "quantity": 1, "unitPrice": 115, "taxPercent": 15 } ] }
    """;
var req = HttpRequest.newBuilder(URI.create("https://your-gateway.example.com/api/v1/devices/YOUR_DEVICE_ID/receipts"))
    .header("Authorization", "Bearer fdms_your_token")
    .header("Content-Type", "application/json")
    .POST(HttpRequest.BodyPublishers.ofString(sale)).build();
var res = HttpClient.newHttpClient().send(req, HttpResponse.BodyHandlers.ofString());
// 201 or 202 = fiscalised; anything else: the body says why (errorCode, title)
System.out.println(res.statusCode() + " " + res.body());
```

```go
sale := []byte(`{ "externalRef": "SALE-1001", "invoiceNo": "INV-1001", "receiptType": "FiscalInvoice", "currency": "USD",
  "linesTaxInclusive": true, "autoOpenDay": true,
  "lines": [ { "name": "Shoes", "hsCode": "64039900", "quantity": 1, "unitPrice": 115, "taxPercent": 15 } ] }`)
req, _ := http.NewRequest("POST", "https://your-gateway.example.com/api/v1/devices/YOUR_DEVICE_ID/receipts", bytes.NewReader(sale))
req.Header.Set("Authorization", "Bearer fdms_your_token")
req.Header.Set("Content-Type", "application/json")
res, err := http.DefaultClient.Do(req)
if err != nil { log.Fatal(err) }
defer res.Body.Close()
body, _ := io.ReadAll(res.Body)
// 201 or 202 = fiscalised; anything else: the body says why (errorCode, title)
fmt.Println(res.StatusCode, string(body))
```

The answer contains the fiscal result. Save at least `id`, `receiptGlobalNo` and `qrUrl` with your invoice:

```json
{ "id": "8af959c9-5c2b-4950-ae4b-a89f7ab3432c", "invoiceNo": "INV-1001", "status": "Submitted",
  "fiscalDayNo": 1, "receiptGlobalNo": 1, "total": 115.00, "qrUrl": "https://fdmsapitest.zimra.co.zw/…" }
```

**Step 3. Get the printable invoice** (with the ZIMRA QR code on it). Use the `id` from step 2:

```bash
curl "https://your-gateway.example.com/api/v1/receipts/RECEIPT_ID/pdf?format=a4" -H "Authorization: Bearer fdms_your_token" -o invoice.pdf
```

```python
pdf = requests.get(f"https://your-gateway.example.com/api/v1/receipts/{receipt['id']}/pdf?format=a4",
                   headers={"Authorization": "Bearer fdms_your_token"})
open("invoice.pdf", "wb").write(pdf.content)
```

```js
const pdf = await fetch(`https://your-gateway.example.com/api/v1/receipts/${data.id}/pdf?format=a4`, { headers: { Authorization: 'Bearer fdms_your_token' } });
require('fs').writeFileSync('invoice.pdf', Buffer.from(await pdf.arrayBuffer()));
```

```php
<?php
$ch = curl_init("https://your-gateway.example.com/api/v1/receipts/{$data['id']}/pdf?format=a4");
curl_setopt_array($ch, [CURLOPT_RETURNTRANSFER => true, CURLOPT_HTTPHEADER => ['Authorization: Bearer fdms_your_token']]);
file_put_contents('invoice.pdf', curl_exec($ch));
```

```csharp
var pdf = await http.GetByteArrayAsync($"api/v1/receipts/{data.GetProperty("id").GetString()}/pdf?format=a4");
await File.WriteAllBytesAsync("invoice.pdf", pdf);
```

```java
var pdfReq = HttpRequest.newBuilder(URI.create("https://your-gateway.example.com/api/v1/receipts/" + receiptId + "/pdf?format=a4"))
    .header("Authorization", "Bearer fdms_your_token").build();
HttpClient.newHttpClient().send(pdfReq, HttpResponse.BodyHandlers.ofFile(java.nio.file.Path.of("invoice.pdf")));
```

```go
req, _ := http.NewRequest("GET", "https://your-gateway.example.com/api/v1/receipts/"+receiptID+"/pdf?format=a4", nil)
req.Header.Set("Authorization", "Bearer fdms_your_token")
res, _ := http.DefaultClient.Do(req)
defer res.Body.Close()
f, _ := os.Create("invoice.pdf")
defer f.Close()
io.Copy(f, res.Body)
```

Use `format=receipt` instead of `format=a4` for an 80 mm till slip.

**You are done.** Everything else in this guide is optional: credit notes, webhooks, imports, Zoho Books.

## Any language: the 5 rules

If your language is not shown above, these five rules are all you need:

1. **Address:** `https://your-gateway.example.com` + the path, for example `/api/v1/devices/{deviceId}/receipts`.
2. **Header on every call:** `Authorization: Bearer fdms_your_token` (or `X-Api-Key: fdms_your_token`).
3. **Sending data:** the body is JSON, with the header `Content-Type: application/json`. Field names are written exactly as shown (camelCase).
4. **Success:** any `2xx` status. For a sale, `201` (ZIMRA accepted it) and `202` (queued, delivered automatically) both mean "fiscalised".
5. **Failure:** any other status. The body is JSON with `errorCode` (stable, for your code) and `title` (a sentence for people). See *Errors*.

Testing without code? Import the [OpenAPI document](https://your-gateway.example.com/swagger/v1/swagger.json) into Postman or Insomnia, or try calls in the browser under the [API reference](https://your-gateway.example.com/swagger/index.html) (click *Authorize* and paste your token).

## Concepts

| Term | What it is |
|---|---|
| **Company** | Your organisation on FiscalHub. Its users, devices and tokens are kept apart from every other company. |
| **Device** | A fiscal device registered with ZIMRA. Every sale is fiscalised *on* one device. A shop with three tills has three devices. |
| **Token** | A secret `fdms_…` that lets your software act for **one** device, with only the rights you ticked. |
| **Fiscal day** | ZIMRA's daily work period for a device: open, issue receipts, close. Send `autoOpenDay: true` and FiscalHub opens it for you; it also closes it at 23:59. |
| **Receipt** | One fiscal document: `FiscalInvoice`, `CreditNote` or `DebitNote`. It gets a global number, a signature and a QR code. |
| **externalRef** | *Your* id for the sale. Sending it again never creates a second fiscal document, so retries are always safe. |

## Authentication

Send the token on every call, in either header:

```
Authorization: Bearer fdms_xxxxxxxxxxxxxxxxxxxxxxxx
X-Api-Key: fdms_xxxxxxxxxxxxxxxxxxxxxxxx
```

- A token works for **one device only**. Three tills means three tokens.
- A token can only do what its creator ticked: `devices.view`, `day.view`, `day.manage`, `receipts.view`, `receipts.submit`, `receipts.import`, `offline.manage`, `stock.view`. It can never manage users, tokens or webhooks.
- It can expire, can be revoked at any time, and stops at once if the company is suspended or the device disabled.
- `401`: the token is missing or wrong. `403`: the token may not do that. `404`: that device or receipt is not yours.
- Limit: 120 calls a minute per token. `429` means wait a moment and retry.

> **Warning:** keep the token on your server. Never put it in a web page or a mobile app, where anyone could read it. It is shown only once; if you lose it, create a new one and revoke the old one.

## Accounts, plans and billing

Most developers integrate for a company that already has an account. To set one up yourself:

1. **Register** the company on the website and choose a plan. Confirm your email with the code you receive.
2. **Sign in** and **pay the first invoice** under *Billing* (unless your plan starts with a free trial).
3. Add your device under *Devices*, then create a token under *API tokens*.

| Situation | What happens |
|---|---|
| **First invoice unpaid** | Only *Billing*, sign-in and these docs work. Other calls answer `402 PAYMENT_REQUIRED` until it is paid. |
| **Free trial** | Everything works with **demo (Test) devices**. A live (Production) device answers `403 TRIAL_DEMO_ONLY` until the plan is paid. |
| **Subscription expired** | Everything except Billing answers `402` until the renewal invoice is paid. Your data is kept. |
| **Going live** | Demo devices need no approval. For a live device, pay for your plan and send a go-live request under *Billing*. Until it is approved: `403 LIVE_NOT_APPROVED`. |
| **Plan limits** | More devices, users or branches than the plan allows: `403 PLAN_LIMIT`. Upgrade under *Billing*. |

**Paying:** Paynow (Visa, Mastercard, ZimSwitch, EcoCash, OneMoney), EcoCash direct, Stripe, or a manual payment (EcoCash Send Money, PayPal, bank transfer) you confirm with its reference. After a cash or bank payment you may get a one-time **activation code** to type under *Billing*.

The price list is public: `GET /api/v1/public/plans` (no token needed).

## Fiscalise a sale

```
POST /api/v1/devices/{deviceId}/receipts
```

**The smallest valid sale** (everything paid in cash, no named buyer):

```json
{
  "externalRef": "SALE-1001",
  "invoiceNo": "INV-1001",
  "receiptType": "FiscalInvoice",
  "currency": "USD",
  "linesTaxInclusive": true,
  "autoOpenDay": true,
  "lines": [ { "name": "Shoes", "hsCode": "64039900", "quantity": 1, "unitPrice": 115, "taxPercent": 15 } ]
}
```

**A full sale** (several lines, an exempt item, a payment method and a named buyer):

```json
{
  "externalRef": "SALE-1002",
  "invoiceNo": "INV-1002",
  "receiptType": "FiscalInvoice",
  "currency": "USD",
  "linesTaxInclusive": true,
  "autoOpenDay": true,
  "lines": [
    { "name": "Shoes", "hsCode": "64039900", "quantity": 1, "unitPrice": 115, "taxPercent": 15 },
    { "name": "Bread", "hsCode": "19059010", "quantity": 2, "unitPrice": 1, "isExempt": true }
  ],
  "payments": [ { "moneyType": "Card", "amount": 117 } ],
  "buyer": { "registerName": "Acme (Pvt) Ltd", "tin": "1234567890", "vatNumber": "220012345" },
  "notes": "Thank you"
}
```

| Field | Required? | What to put |
|---|---|---|
| `externalRef` | **Yes** | Your own unique id for the sale (up to 100 characters), for example your sale id. Sending it again returns the first receipt instead of fiscalising twice. |
| `invoiceNo` | **Yes** | The number printed on your invoice (up to 50 characters). |
| `receiptType` | **Yes** | `FiscalInvoice`, `CreditNote` or `DebitNote`. |
| `currency` | **Yes** | 3 letters, for example `USD` or `ZWG`. |
| `lines` | **Yes** | At least one line, see below. |
| `linesTaxInclusive` | Recommended | `true` if your prices already include VAT (most shops), `false` if VAT is added on top. |
| `autoOpenDay` | Recommended | `true`: opens the fiscal day for you when needed. |
| `payments` | No | Leave out and the full total counts as cash. If given, the amounts must add up to the total. `moneyType`: `Cash`, `Card`, `MobileWallet`, `Coupon`, `Credit`, `BankTransfer`, `Other`. |
| `buyer` | No | `registerName`, `tin` (10 digits), and optionally `vatNumber`, `tradeName`, `email`, `phone`. |
| `notes` | No | Free text. **Required** for credit and debit notes (the reason). |
| `holdMinutes` | No | 1 to 60. Checks the sale now but sends it later, so a mistake can still be cancelled. |

**Each line:**

| Field | Required? | What to put |
|---|---|---|
| `name` | **Yes** | Product or service name (up to 200 characters). |
| `quantity` | **Yes** | More than 0. |
| `unitPrice` | **Yes** | Price of one unit (positive). |
| `hsCode` | For VAT-registered sellers | ZIMRA tariff code: 4, 6 or 8 digits (normally 8). |
| tax | **Yes, one of** | `taxPercent` (for example `15`, `0`), or `isExempt: true`, or `taxId` (ZIMRA's tax id). It must be a tax ZIMRA set up for the device: `GET /api/v1/devices` lists them under `taxes`. |
| `isDiscount` | No | `true` for a discount line (give a positive price; FiscalHub applies the minus). |

**What you get back:**

| Status | Meaning | What to do |
|---|---|---|
| `201` | ZIMRA accepted it. | Save the result. Done. |
| `202` | Numbered and queued because ZIMRA could not be reached. FiscalHub delivers it automatically. | Treat it as fiscalised. |
| `400` / `422` | Refused: something in the data is wrong. **No number was used.** | Read `title`, fix the data, send again with the same `externalRef`. |
| `503` | ZIMRA unavailable and the sale could not be queued. | Retry later with the same `externalRef`. |

```json
{
  "id": "8af959c9-5c2b-4950-ae4b-a89f7ab3432c",
  "deviceId": "9e840039-582c-4e7b-a96c-8de8de9c072f",
  "externalRef": "SALE-1001",
  "invoiceNo": "INV-1001",
  "type": "FiscalInvoice",
  "currency": "USD",
  "status": "Submitted",
  "fiscalDayNo": 1,
  "receiptCounter": 1,
  "receiptGlobalNo": 1,
  "receiptDate": "2026-10-05T20:52:40",
  "total": 115.00,
  "fdmsReceiptId": 1234567,
  "qrUrl": "https://fdmsapitest.zimra.co.zw/…",
  "validationErrors": []
}
```

`validationErrors` lists anything ZIMRA flagged. A **Red** one must be corrected before the day can close (see *Credit notes and corrections*).

**Try it without fiscalising:** `POST /api/v1/devices/{deviceId}/receipts/preview` with the same body calculates everything and uses no number. Nothing is sent to ZIMRA.

## Receipt statuses

| Status | Meaning |
|---|---|
| `Submitted` | Accepted by ZIMRA. |
| `Pending` | Numbered, waiting to reach ZIMRA. Delivered automatically (or `POST /api/v1/receipts/{id}/retry`). |
| `Held` | Inside its hold window, not sent yet. `POST /api/v1/receipts/{id}/release` sends it now; `…/cancel` voids it. |
| `OfflineIssued` | Issued while the device is in ZIMRA *Offline* mode; uploaded in the day's file. |
| `Rejected` | ZIMRA permanently refused it (see `errorCode`). |
| `Cancelled` | A held receipt that was voided. |

## Read, search and print

```
GET /api/v1/receipts/{id}                       the receipt
GET /api/v1/receipts/{id}/detail                with its lines, payments and buyer as sent to ZIMRA
GET /api/v1/receipts/lookup?externalRef=SALE-1001   find a receipt by your own reference (or ?invoiceNo=INV-1001)
GET /api/v1/receipts?search=INV-1001            search; filters: deviceId, status, from, to, fiscalDayNo, hasErrors, page, pageSize
GET /api/v1/receipts/{id}/pdf?format=a4         printable A4 invoice (format=receipt for an 80 mm slip)
GET /api/v1/receipts/{id}/qr                    the ZIMRA QR code as a PNG image
GET /api/v1/receipts/{id}/docx                  the invoice as a Word document
GET /api/v1/receipts/{id}/document              the invoice as JSON, to draw it your own way
GET /api/v1/receipts/{id}/fiscal                just the fiscal result, compact
```

The PDF and Word files use the company's own invoice design (logo, colours, layout) when one is set up in the console.

**Send it to the customer:**

```
POST /api/v1/receipts/{id}/share       a link anyone can open without signing in (invoice, QR, PDF, ZIMRA check); expires after 90 days
POST /api/v1/receipts/{id}/email       { "email": "customer@example.co.zw", "message": "Thank you" }
POST /api/v1/receipts/{id}/sms         { "phone": "+263771234567", "message": "Thank you" }
```

- **SMS:** both fields are optional. Without `phone`, the buyer's phone on the invoice is used. The text carries the invoice number, the total and the link to view and verify it.
- **Which account sends it:** the company's own SMS account if it has one (console → *Integration → WhatsApp & SMS*), otherwise the platform's.
- **No SMS set up:** the answer is `"sent": false, "configured": false`, and nothing is sent.

## Credit notes and corrections

A fiscalised invoice can never be edited or deleted. To refund or fix one, issue a credit note that points to it.

**Refund (credit note):** same call as a sale, with `receiptType: "CreditNote"`, the original's `id` in `originalReceiptId`, and the reason in `notes`. Use positive prices; FiscalHub applies the minus.

```json
{
  "externalRef": "REFUND-1001",
  "invoiceNo": "CN-1001",
  "receiptType": "CreditNote",
  "originalReceiptId": "8af959c9-5c2b-4950-ae4b-a89f7ab3432c",
  "notes": "Customer returned the shoes",
  "currency": "USD",
  "linesTaxInclusive": true,
  "autoOpenDay": true,
  "lines": [ { "name": "Shoes", "hsCode": "64039900", "quantity": 1, "unitPrice": 115, "taxPercent": 15 } ]
}
```

- You cannot credit more than what is left of the original, use a different currency, or credit an invoice older than 12 months.
- **Debit note** (charge more): the same, with `receiptType: "DebitNote"`.

**Fix a flagged invoice in one call.** For example ZIMRA flagged a wrong HS code, which stops the day from closing:

```
POST /api/v1/receipts/{id}/correct
```

```json
{ "reason": "HS code had the wrong length",
  "fixes": [ { "lineNo": 1, "hsCode": "01019100" } ],
  "issueReplacement": true,
  "replacementInvoiceNo": "INV-1001-R" }
```

This issues a credit note that cancels the invoice and, with `issueReplacement`, a corrected copy. It is safe to repeat.

## Fiscal day

Usually you do nothing:
- `autoOpenDay: true` opens the day with the first sale.
- FiscalHub **closes any open day at 23:59** Zimbabwe time by itself, and retries if ZIMRA refuses.

To close it yourself (the Z report) or check it:

```
GET  /api/v1/devices/{deviceId}/day            status and counters
POST /api/v1/devices/{deviceId}/day/close
```

If a close fails, the answer says why. The usual cause is `ReceiptsWithValidationErrors` (a receipt flagged red): correct it, then close again.

## Bulk and file imports

Already producing invoices as files? Send the file and FiscalHub reads it.

```
POST /api/v1/devices/{deviceId}/fiscalise            multipart field "file": .xlsx, .docx or a digital .pdf; fiscalises every invoice in it
POST /api/v1/devices/{deviceId}/fiscalise?submit=false    dry run: shows what it read, fiscalises nothing
```

- Excel template: `GET /api/v1/import/excel-template`.
- No invoicing system at all? Use the free Word template `GET /api/v1/public/templates/tax-invoice.docx` (add `?sample=true` for a filled example), fill it in, and upload it.
- Re-sending the same file never fiscalises an invoice twice.
- Word and PDF files are read best-effort, so check the dry run first.

```bash
curl -X POST "https://your-gateway.example.com/api/v1/devices/YOUR_DEVICE_ID/fiscalise" \
  -H "Authorization: Bearer fdms_your_token" -F "file=@invoices.xlsx"
```

```python
r = requests.post("https://your-gateway.example.com/api/v1/devices/YOUR_DEVICE_ID/fiscalise",
                  headers={"Authorization": "Bearer fdms_your_token"}, files={"file": open("invoices.xlsx", "rb")})
print(r.json())   # { "total": 1, "succeeded": 1, "failed": 0, "items": [ { "invoiceNo": ..., "status": ..., "fiscal": {...} } ] }
```

## Zoho Books: which option is for you

FiscalHub connects to Zoho Books in three ways. All three need the integration switched on for your company first (see *Zoho Books: switch it on*).

| You want to… | Option | Code needed? |
|---|---|---|
| Fiscalise the invoices and credit notes you raise **in Zoho Books** | *Zoho Books: fiscalise your Zoho invoices* | **None.** Set it up in the console. |
| Let **your own system** (ERP, shop, POS, payroll) read and write your Zoho Books, without dealing with Zoho sign-in | *Zoho Books from your own system* | A few HTTPS calls, any language |
| Read and write the Zoho Books of **companies you work for** (you are an accountant or software vendor) | *Zoho bridge for partners* | A few HTTPS calls, any language |

You never need a Zoho developer account, OAuth code or Zoho tokens in your system: FiscalHub keeps the Zoho sign-in and renews it.

## Zoho Books: switch it on

The Zoho Books integration is switched on per company by the platform operator.

1. Sign in to the console as a company administrator and open **Integration → Zoho Books**.
2. Tick what you need it for (fiscalise Zoho invoices, connect your own system, share with a partner), add a short note about your system, and click **Apply**.
3. You get a notification and an email when it is decided.
4. If there is a set-up fee, an invoice appears under **Billing**. While it is unpaid you can already connect Zoho Books, choose your settings and create API keys; syncing, fiscalising and the keys start working as soon as it is paid.

Until then, Zoho calls answer `403 INTEGRATION_NOT_ACTIVE`. If the operator suspends it, the same code comes back with the reason.

## Zoho Books: fiscalise your Zoho invoices

No code needed. Every invoice and credit note you raise in Zoho Books gets fiscalised, and the ZIMRA result is written back onto the Zoho document.

**1. Prepare Zoho Books (once, about 5 minutes).**
- You need a Zoho Books **organisation**. If you have none yet, sign in at books.zoho.com and create one.
- Create three invoice custom fields: in Zoho Books **Settings**, open **Invoices → Field Customization**, click **New Custom Field**, choose *Text Box (Single Line)*, and name them exactly:
  - `Fiscal Number`
  - `Verification Code`
  - `ZIMRA QR`
- Put an **HS code** on each item, in the item's HSN/SAC field or in an item custom field named `HS code`. Without one, the default HS code you choose in step 3 is used.
- For a customer's name and TIN to appear on the fiscal invoice, add a contact custom field named `TIN` and fill in their 10-digit TIN.

**2. Connect Zoho Books** on **Integration → Zoho Books**. Choose your Zoho region (where your Zoho account lives: `.com`, `.eu`, `.in`, `.com.au`…), then use one of:

- **Sign in with Zoho** (when the platform offers it): click *Connect*, sign in to Zoho and click *Accept*. Done.
- **Your own Zoho app (Self Client)**, which works in every case:
  1. Open [api-console.zoho.com](https://api-console.zoho.com) with the same Zoho account, click **Add Client → Self Client → Create**.
  2. Copy the **Client ID** (starts with `1000.`) and **Client Secret** into FiscalHub.
  3. In the Zoho console, open the **Generate Code** tab. Scope: `ZohoBooks.fullaccess.all`. Time duration: 10 minutes. Description: FiscalHub. Click **Create** and copy the code.
  4. Paste the code into FiscalHub within the 10 minutes and click **Connect**.

  The code works once only. If you see `ZOHO_CODE_REFUSED`, generate a new code **with the same Self Client** whose ID and secret you entered.

**3. Choose the settings.**
- The **organisation** to use (if your Zoho account has several).
- The **fiscal device** that fiscalises Zoho documents.
- A **default HS code** for items without one, and the **payment type** (for example `Credit` for invoices paid later).
- A **start date**: documents dated before it are ignored.
- **Fiscalise automatically**: on, every new invoice is fiscalised within a few minutes; off, you click *Fiscalise* on each one.
- **Write back to Zoho**: fills the custom fields above and attaches the fiscal PDF.

**4. Use Zoho as normal.** Raise an invoice in Zoho Books (not a draft). FiscalHub picks it up about every 5 minutes, or at once with **Sync now**. For instant pick-up, the page shows a *webhook address*: add it in Zoho under **Settings → Automation → Workflow Rules** as a webhook on invoice creation.

**What happens**
- Each Zoho document is fiscalised **once only**. Its reference is `zoho:{organisationId}:{invoice|creditnote}:{zohoId}`.
- A credit note is fiscalised when the invoice it belongs to was fiscalised first.
- Drafts are skipped. A document whose totals do not match is held as *Failed* with the reason, so you can fix it in Zoho and click *Fiscalise* again.
- The fiscal number, verification code and QR link appear on the Zoho invoice, so the invoice you send from Zoho carries them.

## QuickBooks, Xero, Sage and Odoo

The same thing for **QuickBooks Online**, **Xero**, **Sage Accounting** and **Odoo**, with no code needed. Each is applied for and switched on separately: on **Integration → Accounting connectors**, click the product and **Apply** (the same `403 INTEGRATION_NOT_ACTIVE` applies until it is on). Then connect it, choose the company in it and the fiscal device, and use your books as normal.

| | QuickBooks Online | Xero | Sage Accounting | Odoo (14 or later) |
|---|---|---|---|---|
| Connect | Sign in | Sign in | Sign in | Address, database, email and an API key (**My Profile → Account Security → New API Key**) |
| HS code on a line | Item **SKU** | **Item code** | Product **item code** | Product **internal reference** |
| Buyer's TIN | Invoice custom field `TIN` (10 digits) | Contact **tax number** | Contact **tax number** | Contact **Tax ID** |
| Credit note → invoice | Credit memo custom field `Invoice` = invoice number | Allocate it to the invoice | **Original invoice number** | **Add credit note** on the invoice |
| Written back | Private note + PDF attached | History note + PDF attached | Notes + PDF attached | Chatter note + PDF attached |

Each document is fiscalised **once only**. Its reference is `{quickbooks|xero|sage|odoo}:{companyId}:{invoice|creditnote}:{documentId}`. Documents are picked up about every 5 minutes, or at once with **Sync now**. Drafts are skipped; voided ones are skipped too. A document whose total does not match the VAT on its lines is held as *Failed* with the reason.

The console uses these endpoints (signed in, permission `integrations.manage`), where `{product}` is `quickbooks`, `xero`, `sage` or `odoo`:

| Method and path | What it does |
|---|---|
| `GET /api/v1/integrations/connectors` | All four, with access status, connection and counts |
| `POST /api/v1/integrations/access` `{ "provider": "{product}", "note": "…" }` | Apply |
| `POST /api/v1/integrations/connectors/{product}/connect` | The sign-in address to open (QuickBooks, Xero, Sage) |
| `POST /api/v1/integrations/connectors/odoo/key` `{ "url", "database", "username", "apiKey" }` | Connect Odoo |
| `POST /api/v1/integrations/connectors/{product}/organization` `{ "organizationId" }` | Choose the company |
| `PUT /api/v1/integrations/connectors/{product}/settings` | Device, HS code, payment type, start date, automatic, write back |
| `POST /api/v1/integrations/connectors/{product}/sync` | Sync now |
| `GET /api/v1/integrations/connectors/{product}/documents?status=` | Documents (`Pending`, `Fiscalised`, `Failed`, `Skipped`, `Changed`, `NotPostedBack`) |
| `POST /api/v1/integrations/connectors/{product}/documents/{id}/fiscalise` · `/skip` · `/post-back` | Act on one document |
| `DELETE /api/v1/integrations/connectors/{product}` | Disconnect |

## Zoho Books from your own system

Your ERP, shop, POS or payroll system can **push data into your Zoho Books and pull it back** through FiscalHub with one key. FiscalHub keeps your Zoho sign-in, so your system never deals with Zoho's OAuth or expiring tokens. Anything you push can then be fiscalised automatically (see the previous section).

**Setup (about 5 minutes)**
1. Switch the integration on and connect Zoho Books (the two sections above).
2. On **Integration → Zoho Books → Your own system (API)**, click **New key**. Give it a name, tick the modules it may use, and choose *read only* or *read and write*.
3. Copy the key. It starts with `fhb_` and is shown **once**.

**Calls.** Send the key in the header `X-Bridge-Key: fhb_your_key` (or `Authorization: Bearer fhb_your_key`).

| Call | What it does |
|---|---|
| `GET /api/v1/bridge/zoho/{path}` | Read from your Zoho Books |
| `POST /api/v1/bridge/zoho/{path}` | Create a record (key must allow writing) |
| `PUT /api/v1/bridge/zoho/{path}` | Update a record (key must allow writing) |
| `GET /api/v1/bridge/events?after={lastId}` | What changed in Zoho since you last asked |
| `GET /api/v1/bridge/modules` | The modules available |

`{path}` and the JSON are **exactly Zoho Books API v3** ([Zoho's reference](https://www.zoho.com/books/api/v3/)): `contacts`, `contacts/{contact_id}`, `items?page=2`, `invoices?status=unpaid`, `settings/taxes`… Leave out `organization_id`; your organisation is filled in for you. Answers are Zoho's own JSON, unchanged. The fields each record needs (customers, items, invoices, credit notes, payments…) are listed in *Zoho Books: fields for each record* below.

**Example: list customers, then create an invoice in Zoho.**

```bash
# list customers
curl "https://your-gateway.example.com/api/v1/bridge/zoho/contacts?contact_type=customer" -H "X-Bridge-Key: fhb_your_key"

# create an invoice
curl -X POST "https://your-gateway.example.com/api/v1/bridge/zoho/invoices" \
  -H "X-Bridge-Key: fhb_your_key" -H "Content-Type: application/json" \
  -d '{"customer_id":"4600000000123","line_items":[{"name":"Consulting","rate":120,"quantity":1}]}'
```

```python
import requests
BASE = "https://your-gateway.example.com/api/v1/bridge/zoho"
H = {"X-Bridge-Key": "fhb_your_key"}

customers = requests.get(f"{BASE}/contacts", headers=H, params={"contact_type": "customer"}).json()["contacts"]
print([c["contact_name"] for c in customers])

r = requests.post(f"{BASE}/invoices", headers=H, json={
    "customer_id": customers[0]["contact_id"],
    "line_items": [{"name": "Consulting", "rate": 120, "quantity": 1}],
})
print(r.status_code, r.json()["invoice"]["invoice_number"])
```

```js
const BASE = 'https://your-gateway.example.com/api/v1/bridge/zoho';
const H = { 'X-Bridge-Key': 'fhb_your_key', 'Content-Type': 'application/json' };

const { contacts } = await (await fetch(`${BASE}/contacts?contact_type=customer`, { headers: H })).json();
console.log(contacts.map(c => c.contact_name));

const r = await fetch(`${BASE}/invoices`, { method: 'POST', headers: H,
  body: JSON.stringify({ customer_id: contacts[0].contact_id, line_items: [{ name: 'Consulting', rate: 120, quantity: 1 }] }) });
console.log(r.status, (await r.json()).invoice.invoice_number);
```

```php
<?php
$base = 'https://your-gateway.example.com/api/v1/bridge/zoho';
function zoho($method, $path, $body = null) {
  global $base;
  $ch = curl_init("$base/$path");
  curl_setopt_array($ch, [CURLOPT_RETURNTRANSFER => true, CURLOPT_CUSTOMREQUEST => $method,
    CURLOPT_HTTPHEADER => ['X-Bridge-Key: fhb_your_key', 'Content-Type: application/json']]);
  if ($body !== null) curl_setopt($ch, CURLOPT_POSTFIELDS, json_encode($body));
  return json_decode(curl_exec($ch), true);
}
$customers = zoho('GET', 'contacts?contact_type=customer')['contacts'];
$invoice = zoho('POST', 'invoices', [
  'customer_id' => $customers[0]['contact_id'],
  'line_items' => [ ['name' => 'Consulting', 'rate' => 120, 'quantity' => 1] ],
]);
echo $invoice['invoice']['invoice_number'];
```

```csharp
using var zoho = new HttpClient { BaseAddress = new Uri("https://your-gateway.example.com/api/v1/bridge/zoho/") };
zoho.DefaultRequestHeaders.Add("X-Bridge-Key", "fhb_your_key");

var list = await zoho.GetFromJsonAsync<JsonElement>("contacts?contact_type=customer");
var customerId = list.GetProperty("contacts")[0].GetProperty("contact_id").GetString();

var res = await zoho.PostAsJsonAsync("invoices", new { customer_id = customerId, line_items = new[] { new { name = "Consulting", rate = 120, quantity = 1 } } });
Console.WriteLine(await res.Content.ReadAsStringAsync());
```

```java
var http = HttpClient.newHttpClient();
var base = "https://your-gateway.example.com/api/v1/bridge/zoho/";

var list = HttpRequest.newBuilder(URI.create(base + "contacts?contact_type=customer")).header("X-Bridge-Key", "fhb_your_key").build();
System.out.println(http.send(list, HttpResponse.BodyHandlers.ofString()).body());

var invoice = """
    { "customer_id": "4600000000123", "line_items": [ { "name": "Consulting", "rate": 120, "quantity": 1 } ] }
    """;
var create = HttpRequest.newBuilder(URI.create(base + "invoices"))
    .header("X-Bridge-Key", "fhb_your_key").header("Content-Type", "application/json")
    .POST(HttpRequest.BodyPublishers.ofString(invoice)).build();
System.out.println(http.send(create, HttpResponse.BodyHandlers.ofString()).body());
```

```go
base := "https://your-gateway.example.com/api/v1/bridge/zoho/"
call := func(method, path string, body []byte) string {
  req, _ := http.NewRequest(method, base+path, bytes.NewReader(body))
  req.Header.Set("X-Bridge-Key", "fhb_your_key")
  req.Header.Set("Content-Type", "application/json")
  res, err := http.DefaultClient.Do(req)
  if err != nil { log.Fatal(err) }
  defer res.Body.Close()
  out, _ := io.ReadAll(res.Body)
  return string(out)
}
fmt.Println(call("GET", "contacts?contact_type=customer", nil))
fmt.Println(call("POST", "invoices", []byte(`{"customer_id":"4600000000123","line_items":[{"name":"Consulting","rate":120,"quantity":1}]}`)))
```

**Find out what changed in Zoho.** Two ways; use either or both.

**Option A: poll the feed** (simplest). Keep the last `id` you processed and ask for newer events:

```
GET /api/v1/bridge/events?after=0&take=100
```

```json
{ "events": [ { "id": 1042, "type": "zoho.contacts.updated", "companyId": "3fa8…", "module": "contacts", "recordId": "4600000000123",
                "createdAt": "2026-10-11T09:30:00Z", "data": { "contact_id": "4600000000123", "contact_name": "Glow Salon" } } ],
  "lastId": 1042, "more": false }
```

Process the events, store `lastId`, and next time call with `after=<lastId>`. When `more` is `true`, call again straight away.

**Option B: webhook** (instant). On the same page, set an https address and tick the modules to watch. Each change is POSTed to you, signed like FiscalHub's other webhooks (see *Checking a webhook signature*), with headers `X-FiscalHub-Event`, `X-FiscalHub-Delivery`, `X-FiscalHub-Timestamp` and `X-FiscalHub-Signature`, and secret `fhbsec_…`.

- Event types are `zoho.{module}.created` and `zoho.{module}.updated`. Watched modules are checked about every 15 minutes (at once when Zoho's workflow webhook calls).
- `data` is the record as Zoho lists it. For every field, call `GET /api/v1/bridge/zoho/{module}/{recordId}`.
- Events stay in the feed for 30 days. Deletions in Zoho are not reported.

**Only need the Zoho connection, no fiscal device?** Choose the **Zoho Connector** plan when you register.

## Zoho Books: fields for each record

When you create or update records through the bridge, the body is Zoho Books' own JSON. This section lists the fields you need for the common records, and how to get the IDs they refer to.

**General rules**
- Field names are lowercase with underscores (`customer_id`, `line_items`), exactly as below.
- Dates are text `YYYY-MM-DD` (for example `"2026-10-12"`). Amounts are numbers (`120.5`), not text.
- `POST /api/v1/bridge/zoho/{module}` creates a record. `PUT /api/v1/bridge/zoho/{module}/{id}` updates one; send only the fields that change.
- Save the ID Zoho returns (`contact_id`, `item_id`, `invoice_id`…). You need it to update the record or to point other records at it.
- If a required field is missing, Zoho answers with its own `code` and `message` (for example `"code": 4, "message": "Invalid value passed for customer_id"`). FiscalHub passes it to you unchanged.
- Zoho decides which fields are required, and an organisation's own settings can add some (for example a mandatory custom field, or invoice numbers typed by hand). The complete list for each record is in [Zoho Books API v3](https://www.zoho.com/books/api/v3/).

**Where to get the IDs**

| You need | Get it from |
|---|---|
| `customer_id` / `vendor_id` | `GET /api/v1/bridge/zoho/contacts?search_text=…` → `contacts[].contact_id`, or the `contact_id` returned when you created the contact |
| `item_id` | `GET /api/v1/bridge/zoho/items?search_text=…` → `items[].item_id` |
| `tax_id` | `GET /api/v1/bridge/zoho/settings/taxes` → `taxes[].tax_id` (for example the 15% VAT rate) |
| `account_id` | `GET /api/v1/bridge/zoho/chartofaccounts` → `chartofaccounts[].account_id` |
| `invoice_id` | `GET /api/v1/bridge/zoho/invoices?invoice_number=…` → `invoices[].invoice_id` |
| `customfield_id` | Open one existing record of that kind, for example `GET /api/v1/bridge/zoho/contacts/{contact_id}`. Its `custom_fields[]` lists each field's `customfield_id` and `label`. |

### Customer or vendor (contacts)

| Field | Required? | What to put |
|---|---|---|
| `contact_name` | **Yes** | The name shown in Zoho. It must be unique, so search first and update if it exists. |
| `contact_type` | Recommended | `customer` or `vendor`. |
| `company_name` | No | The registered company name. |
| `currency_id` | No | Only for a customer billed in another currency (`GET …/settings/currencies`). |
| `billing_address` | No | `{ "address", "city", "state", "zip", "country" }` |
| `contact_persons` | No | `[{ "first_name", "last_name", "email", "phone", "is_primary_contact": true }]` |
| `custom_fields` | No | `[{ "customfield_id": "…", "value": "…" }]`. Put the 10-digit **TIN** in the custom field labelled `TIN` (or Zoho's `tax_reg_no`) and the VAT number in `vat_reg_no` or a custom field labelled `VAT`; FiscalHub prints them on the fiscal invoice. |

```json
{ "contact_name": "Moyo Traders", "contact_type": "customer", "company_name": "Moyo Traders (Pvt) Ltd",
  "billing_address": { "address": "12 Samora Machel Ave", "city": "Harare", "country": "Zimbabwe" },
  "contact_persons": [ { "first_name": "Tendai", "last_name": "Moyo", "email": "accounts@moyo.co.zw", "is_primary_contact": true } ],
  "custom_fields": [ { "customfield_id": "4600000000999", "value": "1234567890" } ] }
```

### Product or service (items)

| Field | Required? | What to put |
|---|---|---|
| `name` | **Yes** | Item name (unique). |
| `rate` | **Yes** | Selling price. |
| `description` | No | Default line description. |
| `sku` | No | Your product code. Useful for matching items with your system. |
| `tax_id` | No | Default tax (for example 15% VAT). |
| `product_type` | No | `goods` or `service`. |
| `hsn_or_sac` | For fiscalising | The 8-digit ZIMRA **HS code**. FiscalHub also reads an item custom field labelled `HS code`. Without either, the default HS code from the Zoho settings page is used. |

```json
{ "name": "Shoes", "rate": 115, "sku": "SH-001", "product_type": "goods", "tax_id": "4600000000555", "hsn_or_sac": "64039900" }
```

### Invoice

| Field | Required? | What to put |
|---|---|---|
| `customer_id` | **Yes** | From contacts. |
| `line_items` | **Yes** | At least one line, see below. |
| `date` | No | Defaults to today. |
| `invoice_number` | Only if auto-numbering is off in Zoho | Otherwise leave it out and Zoho numbers it. |
| `reference_number` | Recommended | Your own sale number, to match the invoice with your system. |
| `due_date` / `payment_terms` | No | When it must be paid. |
| `is_inclusive_tax` | No | `true` if your rates already include VAT. |
| `notes` | No | Shown on the invoice. |

**Each line** (`line_items[]`):

| Field | Required? | What to put |
|---|---|---|
| `item_id` **or** `name` | **Yes** | An existing item (recommended, carries the HS code), or a free-text line name. |
| `rate` | **Yes** | Unit price. |
| `quantity` | **Yes** | More than 0. |
| `tax_id` | For VAT | The tax for the line. Leave it out on exempt lines. |
| `description` | No | Extra text. |
| `discount` | No | Per-line discount, for example `10` or `"10%"`. |

```json
{ "customer_id": "4600000000123", "reference_number": "SALE-1001", "date": "2026-10-12", "is_inclusive_tax": true,
  "line_items": [ { "item_id": "4600000000777", "rate": 115, "quantity": 1, "tax_id": "4600000000555" },
                  { "name": "Delivery", "rate": 10, "quantity": 1, "tax_id": "4600000000555" } ] }
```

> **Important:** Zoho creates invoices as **drafts**, and FiscalHub does not fiscalise drafts. To have the invoice fiscalised (when *Fiscalise automatically* is on), mark it as sent: `POST /api/v1/bridge/zoho/invoices/{invoice_id}/status/sent` with an empty body. The fiscal number, verification code and QR then appear on the invoice within a few minutes.

### Credit note

The same as an invoice, but with these fields:

| Field | Required? | What to put |
|---|---|---|
| `customer_id` | **Yes** | The customer of the original invoice. |
| `date` | **Yes** | `YYYY-MM-DD`. |
| `line_items` | **Yes** | The lines being credited (same format as invoice lines). |
| `reference_invoice_id` | **Yes, for fiscalising** | The `invoice_id` of the invoice being credited. FiscalHub uses it to point the fiscal credit note at the original fiscal invoice. (Applying the credit to that invoice afterwards, `POST …/creditnotes/{id}/invoices` with `[{ "invoice_id", "amount_applied" }]`, links it too.) |
| `creditnote_number` | Only if auto-numbering is off | |
| `notes` | Recommended | Why it is credited. ZIMRA requires a reason: FiscalHub sends this text, or a standard reason when it is empty. |

To fiscalise a credit note, its invoice must already be fiscalised. Open (non-draft) credit notes are fiscalised the same way as invoices.

### Customer payment

| Field | Required? | What to put |
|---|---|---|
| `customer_id` | **Yes** | |
| `payment_mode` | **Yes** | For example `cash`, `banktransfer`, `creditcard`, or the name of a payment mode set up in Zoho. |
| `amount` | **Yes** | Total received. |
| `date` | **Yes** | `YYYY-MM-DD`. |
| `invoices` | To settle invoices | `[{ "invoice_id": "…", "amount_applied": 125 }]` |
| `reference_number` | No | Receipt or bank reference. |
| `account_id` | No | The bank or cash account it went into. |

```json
{ "customer_id": "4600000000123", "payment_mode": "banktransfer", "amount": 125, "date": "2026-10-12", "reference_number": "FT26101200123",
  "invoices": [ { "invoice_id": "4600000000888", "amount_applied": 125 } ] }
```

### Other records (main fields)

| Record (module) | Main required fields |
|---|---|
| Quote (`estimates`) | `customer_id`, `line_items` |
| Sales order (`salesorders`) | `customer_id`, `line_items` |
| Purchase order (`purchaseorders`) | `vendor_id`, `line_items` |
| Bill (`bills`) | `vendor_id`, `bill_number`, `date`, `line_items` (lines use `account_id` or `item_id`, `rate`, `quantity`) |
| Vendor payment (`vendorpayments`) | `vendor_id`, `amount`, `date`, `bills: [{ "bill_id", "amount_applied" }]` to settle bills |
| Expense (`expenses`) | `account_id` (expense account), `paid_through_account_id`, `amount`, `date` |
| Manual journal (`journals`) | `journal_date`, `line_items: [{ "account_id", "debit_or_credit": "debit" or "credit", "amount" }]` (debits must equal credits) |

For these, check the record's page in [Zoho Books API v3](https://www.zoho.com/books/api/v3/) for the optional fields. To see a valid example, `GET` an existing record of that kind and copy its shape.

## Zoho bridge for partners

For accountants and software vendors who serve **several companies**: read and write each company's Zoho Books with one key, seeing only what each company chose to share.

**Setup**
1. Ask the platform for a partner account. Your partner code is shown under **Partner → Zoho bridge**.
2. Each company enters your code under **Integration → Zoho Books → Partner access**, and picks the modules you may use and whether you may write.
3. Under **Partner → Zoho bridge**, create a key (`fhb_…`, shown once).

**Calls** (header `X-Bridge-Key: fhb_your_key`):

| Call | What it does |
|---|---|
| `GET /api/v1/bridge/companies` | Companies that shared with you: `companyId`, Zoho organisation, what they shared, and whether they are ready |
| `GET /api/v1/bridge/companies/{companyId}/zoho/{path}` | Read that company's Zoho Books |
| `POST` / `PUT /api/v1/bridge/companies/{companyId}/zoho/{path}` | Create or update in that company's Zoho Books |
| `GET /api/v1/bridge/events?after={lastId}` | Changes across all your companies (each event carries its `companyId`) |
| `GET /api/v1/bridge/modules` | The modules available |

It works exactly like *Zoho Books from your own system*; only the address has `companies/{companyId}/` in it.

```bash
curl "https://your-gateway.example.com/api/v1/bridge/companies" -H "X-Bridge-Key: fhb_your_key"
curl "https://your-gateway.example.com/api/v1/bridge/companies/COMPANY_ID/zoho/invoices?status=unpaid" -H "X-Bridge-Key: fhb_your_key"
```

```python
H = {"X-Bridge-Key": "fhb_your_key"}
BASE = "https://your-gateway.example.com/api/v1/bridge"
for co in requests.get(f"{BASE}/companies", headers=H).json():
    if co["ready"]:
        unpaid = requests.get(f"{BASE}/companies/{co['companyId']}/zoho/invoices", headers=H, params={"status": "unpaid"}).json()
        print(co["name"], len(unpaid["invoices"]))
```

```js
const H = { 'X-Bridge-Key': 'fhb_your_key' };
const BASE = 'https://your-gateway.example.com/api/v1/bridge';
for (const co of await (await fetch(`${BASE}/companies`, { headers: H })).json()) {
  if (!co.ready) continue;
  const unpaid = await (await fetch(`${BASE}/companies/${co.companyId}/zoho/invoices?status=unpaid`, { headers: H })).json();
  console.log(co.name, unpaid.invoices.length);
}
```

**Modules**
- Read and write: contacts, items, invoices, credit notes, quotes (estimates), sales orders, customer payments, purchase orders, bills, vendor payments, expenses, recurring invoices, projects, manual journals.
- Read only: chart of accounts, bank accounts, tax rates, currencies.
- Deleting is not offered.

## Zoho errors

Errors from FiscalHub have an `errorCode`. Errors from Zoho itself are passed through unchanged (Zoho's `code` and `message`).

| Status | Code | Meaning and fix |
|---|---|---|
| 401 | | The `fhb_` key is missing, wrong or revoked. |
| 402 | `PAYMENT_REQUIRED` | The company's subscription is unpaid. |
| 403 | `INTEGRATION_NOT_ACTIVE` | The Zoho integration is not switched on, or is suspended (see *Zoho Books: switch it on*). |
| 403 | `MODULE_NOT_SHARED` | The key or partner grant does not include this module. |
| 403 | `WRITE_NOT_SHARED` | The key or grant is read only. |
| 403 | `MODULE_READ_ONLY` | This module can only be read. |
| 404 | `COMPANY_NOT_SHARED` | (Partners) That company has not shared with you. |
| 409 | `ZOHO_NOT_READY` | Zoho Books is not connected, or must be reconnected. The message says which. |
| 400 | `ZOHO_CODE_REFUSED` | Zoho refused the Self Client code: it expired, was used already, or belongs to another client. Generate a new one. |
| 400 | `CLIENT_ID_INVALID` | The Zoho client ID must look like `1000.XXXXXXXX` (check for extra characters). |
| 400 | `BODY_TOO_LARGE` | The JSON body is over 1 MB. |
| 429 | `RATE_LIMITED` | More than 60 calls a minute on one key. Wait and retry. |
| 429 | `ZOHO_RATE_LIMITED` | Zoho's own daily or per-minute limit for the organisation. Wait and retry. |

## Webhooks

Instead of asking "has ZIMRA accepted it yet?", let FiscalHub call your system.

**Set it up** (one address per company): console → *Webhooks*, or `PUT /api/v1/webhook` with `{ "url": "https://erp.example.com/fiscal-events" }` (signed in as a company administrator).
The answer contains the signing secret `whsec_…`, shown **once**. The address must be public `https`.

**Events**

| Event | Sent when |
|---|---|
| `receipt.fiscalised` | ZIMRA accepted a receipt (including one queued and delivered later) |
| `receipt.failed` | ZIMRA permanently rejected a receipt |
| `receipt.cancelled` | A held receipt was cancelled |
| `fiscalday.closed` | The fiscal day closed |
| `fiscalday.close_failed` | ZIMRA refused to close the day |
| `offlinefile.failed` | An offline-mode day file had errors |

**What you receive:** a `POST` with JSON and these headers:

```
X-Fdms-Event: receipt.fiscalised
X-Fdms-Delivery: 5c3b7a0e-6d8f-4d57-8f5e-0d2d7b1b9a11
X-Fdms-Timestamp: 1759750000
X-Fdms-Signature: sha256=3f1c…
```

```json
{ "id": "0b2e9c1c-…", "type": "receipt.fiscalised", "createdAt": "2026-10-05T20:52:41Z",
  "data": { "id": "8af959c9-…", "invoiceNo": "INV-1001", "status": "Submitted", "receiptGlobalNo": 1, "total": 115.00, "qrUrl": "https://…" } }
```

**Rules**
- Answer with any `2xx` quickly, then do your work. Otherwise it is retried after 1 min, 5 min, 30 min, 2 h, 6 h, 12 h and 24 h.
- The same event can arrive twice. Ignore a `X-Fdms-Delivery` you have already handled.
- Events can arrive out of order. Trust the `status` inside `data`.
- No webhook? Poll `GET /api/v1/receipts/lookup?externalRef=…` instead.

### Checking a webhook signature

Check every webhook so nobody can fake one. The same recipe works for FiscalHub webhooks (`X-Fdms-…` headers, `whsec_` secret) and Zoho bridge webhooks (`X-FiscalHub-…` headers, `fhbsec_` secret):

1. Take the timestamp header and the **raw** request body (exactly as received, before any JSON parsing).
2. Compute HMAC-SHA256 of `timestamp + "." + body`, keyed with your secret, as lowercase hex.
3. It must equal the signature header without its `sha256=` prefix (compare in constant time), and the timestamp must be less than 5 minutes old.

```js
// Node / Express: app.post('/fiscal-events', express.raw({ type: 'application/json' }), handler)
const crypto = require('crypto');
function verify(req, secret) {
  const ts = req.header('X-Fdms-Timestamp');
  const expected = 'sha256=' + crypto.createHmac('sha256', secret).update(`${ts}.${req.body}`).digest('hex');
  const given = req.header('X-Fdms-Signature') || '';
  const ok = given.length === expected.length && crypto.timingSafeEqual(Buffer.from(given), Buffer.from(expected));
  return ok && Math.abs(Date.now() / 1000 - Number(ts)) < 300;
}
```

```python
import hmac, hashlib, time
def verify(secret: str, ts: str, raw: bytes, sig: str) -> bool:
    expected = "sha256=" + hmac.new(secret.encode(), ts.encode() + b"." + raw, hashlib.sha256).hexdigest()
    return hmac.compare_digest(expected, sig) and abs(time.time() - int(ts)) < 300
# Flask: verify(SECRET, request.headers["X-Fdms-Timestamp"], request.get_data(), request.headers["X-Fdms-Signature"])
```

```php
<?php
$raw = file_get_contents('php://input');
$ts  = $_SERVER['HTTP_X_FDMS_TIMESTAMP'];
$expected = 'sha256=' . hash_hmac('sha256', $ts . '.' . $raw, $secret);
$ok = hash_equals($expected, $_SERVER['HTTP_X_FDMS_SIGNATURE'] ?? '') && abs(time() - (int)$ts) < 300;
if (!$ok) { http_response_code(401); exit; }
http_response_code(200);
```

```csharp
static bool Verify(string secret, string timestamp, string rawBody, string signatureHeader)
{
    var hash = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{rawBody}"))).ToLowerInvariant();
    var fresh = Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - long.Parse(timestamp)) < 300;
    return fresh && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes("sha256=" + hash), Encoding.UTF8.GetBytes(signatureHeader));
}
```

```java
// Java 17+
static boolean verify(String secret, String ts, byte[] rawBody, String sig) throws Exception {
    var mac = javax.crypto.Mac.getInstance("HmacSHA256");
    mac.init(new javax.crypto.spec.SecretKeySpec(secret.getBytes(java.nio.charset.StandardCharsets.UTF_8), "HmacSHA256"));
    mac.update((ts + ".").getBytes(java.nio.charset.StandardCharsets.UTF_8));
    var expected = "sha256=" + java.util.HexFormat.of().formatHex(mac.doFinal(rawBody));
    var fresh = Math.abs(System.currentTimeMillis() / 1000 - Long.parseLong(ts)) < 300;
    return fresh && java.security.MessageDigest.isEqual(expected.getBytes(), sig.getBytes());
}
```

```go
func verify(secret, ts string, raw []byte, sig string) bool {
  m := hmac.New(sha256.New, []byte(secret))
  m.Write([]byte(ts + "."))
  m.Write(raw)
  expected := "sha256=" + hex.EncodeToString(m.Sum(nil))
  t, err := strconv.ParseInt(ts, 10, 64)
  if err != nil { return false }
  age := time.Now().Unix() - t
  return age < 300 && age > -300 && hmac.Equal([]byte(expected), []byte(sig))
}
```

## Retries: safe by design

Networks fail. Repeating a call is always safe if you follow one rule: **reuse the same `externalRef`** for the same sale. Build it from your own sale id, never from a random value.

| You got | Do this |
|---|---|
| No answer / timeout | Send the same sale again with the same `externalRef`. If it went through the first time, you get that receipt back; nothing is fiscalised twice. |
| `409 DEVICE_BUSY` | Another call is using the device. Wait a second and retry. |
| `429` | Too many calls. Wait a few seconds and retry. |
| `503 FDMS_UNAVAILABLE` | ZIMRA unreachable. Retry later with the same `externalRef`. |
| `400` / `422` | The data is wrong. Retrying will not help: fix the data, then resend (same `externalRef` is fine). |

Wait a little longer each time (1 s, 2 s, 4 s… up to a minute). If FiscalHub stays unreachable, keep the sale in a queue on your side and send it later.

## Errors

Every error has the same JSON shape. Use `errorCode` in your code and show `title` to people:

```json
{ "status": 422, "title": "Line 1: HS code \"0199199\" has the wrong length - use 4, 6 or 8 digits (ZIMRA tariff codes are 8).", "errorCode": "RCPT048", "traceId": "0HN…" }
```

| Status | Meaning |
|---|---|
| `400` | Missing or wrong field (`EXTERNAL_REF_REQUIRED`, `INVOICE_NO_REQUIRED`, `CURRENCY_INVALID`, `TAX_REQUIRED`…). |
| `401` | Token missing or wrong. |
| `402` | Account unpaid (`PAYMENT_REQUIRED`). Pay under *Billing*. |
| `403` | Not allowed (`SCOPE_RESTRICTED`, `PLAN_LIMIT`, `TRIAL_DEMO_ONLY`, `INTEGRATION_NOT_ACTIVE`…). |
| `404` | That device or receipt does not exist for you. |
| `409` | Wrong moment: `FISCAL_DAY_CLOSED`, `DEVICE_BUSY` (retry), `SEQUENCE_BROKEN`, `ALREADY_CORRECTED`… |
| `422` | ZIMRA rules refused the receipt. Common codes below. |
| `429` | Too many calls. |
| `503` | ZIMRA unreachable (`FDMS_UNAVAILABLE`). Retry with the same `externalRef`. |

| Code | Meaning |
|---|---|
| `RCPT016` | No lines. |
| `RCPT023` | A quantity is zero or negative. |
| `RCPT025` | The line's tax is not set up for this device. Use one listed under `taxes` in `GET /api/v1/devices`. |
| `RCPT015` / `RCPT029` / `RCPT034` | Credit/debit note: `notes` or `originalReceiptId` missing or wrong. |
| `RCPT032` / `RCPT033` / `RCPT036` | The original invoice is missing, not accepted, too old, or a tax differs. |
| `RCPT035` | The credit note is more than what is left of the original. |
| `RCPT039` | Payments do not add up to the total. |
| `RCPT040` | Negative total on an invoice or debit note. |
| `RCPT043` | A credit/debit note must use the original's currency. |
| `RCPT047` | HS code needed on every line (VAT-registered seller). |
| `RCPT048` | HS code has the wrong length (use 4, 6 or 8 digits). |

## Time and timezones

- **You never send a date.** FiscalHub stamps each receipt with Zimbabwe time (CAT, UTC+2) itself, so your server's clock or timezone can never cause a rejection.
- `receiptDate`, `serverDate` and fiscal-day times are **Zimbabwe local time without a zone** (`2026-10-05T10:30:00`). Show them as they are.
- Everything else (created, last used, webhook `createdAt`) is **UTC with a trailing `Z`**. Convert it to the viewer's zone.

## Other features

- **Offline mode:** if ZIMRA cannot be reached for a long time, a device can work in ZIMRA *Offline* mode; receipts are uploaded in order when the connection returns.
- **Activity:** every call with a token is logged per device (who, what, result). Console → *Activity*, or `GET /api/v1/activity` (administrators).
- **Alerts:** certificate expiry, a day that would not close, rejected or stuck receipts. Shown under the bell and emailed. `GET /api/v1/notifications`.
- **Device health:** `GET /api/v1/health?live=true`.
- **Quotes** (`/api/v1/quotes`) convert into a fiscal invoice once; **recurring invoices** (`/api/v1/recurring`) issue on a schedule.
- **VAT summary:** `GET /api/v1/reports/vat-return?from=…&to=…` (also `/xlsx` and `/pdf`).

## Going live checklist

1. Build and test against a ZIMRA **test** device. For production, register the live device and create a **new token** for it.
2. Use your own sale id as `externalRef`, and reuse it on every retry.
3. Treat `201` and `202` as fiscalised. On `400`/`422`, fix the data and resend.
4. Save `id`, `receiptGlobalNo` and `qrUrl` with each invoice, and print the QR code.
5. Refund with a `CreditNote` that names `originalReceiptId`.
6. If you use webhooks, check the signature and ignore duplicates.
7. Keep tokens and `fhb_` keys on your server only. Revoke and replace one that leaks.
8. If FiscalHub is unreachable, queue the sale and resend it later.

## Troubleshooting

| You see | Likely cause and fix |
|---|---|
| `401` | Token missing, mistyped, expired or revoked. Send `Authorization: Bearer fdms_…`, or create a new token. |
| `402 PAYMENT_REQUIRED` | The account has an unpaid invoice. Sign in and pay under *Billing*. |
| `403 TRIAL_DEMO_ONLY` | Trial account: use a Test device, or pay for the plan. |
| `404` on your own device | The token belongs to another device. A token only sees its own device. |
| `409 FISCAL_DAY_CLOSED` | No open fiscal day. Send `"autoOpenDay": true`. |
| `409 SEQUENCE_BROKEN` | An earlier receipt was rejected by ZIMRA. Correct it first (*Credit notes and corrections*). |
| `422 RCPT048` | HS code has the wrong length. Use a 4, 6 or 8 digit code. |
| `422 RCPT025` | Tax not set up for the device. Use a tax from `GET /api/v1/devices`. |
| `503 FDMS_UNAVAILABLE` | ZIMRA unreachable. Retry later with the same `externalRef`. |
| Zoho invoice not picked up | It is a draft, dated before the start date, or the integration is not active. Click *Sync now* on the Zoho page and read the reason shown. |
| `403 INTEGRATION_NOT_ACTIVE` | Apply for the Zoho integration and pay the set-up invoice, if any. |
| Webhook not arriving | The address must be public HTTPS and answer `2xx` quickly. Check the delivery log under *Webhooks*. |

Still stuck? Quote the `traceId` from the error when you contact support.
