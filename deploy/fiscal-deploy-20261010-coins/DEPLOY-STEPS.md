# Deployment, 2026-10-10: referral coins and payment fixes

| File | Goes to |
|---|---|
| `fdms-update-referrals-and-coins.sql` | HeidiSQL, live database `fdms_gateway` (run first) |
| `fiscalapi-dll.zip` (8 files) | the API folder (https://fiscalapi.daraerp.co.zw) |
| `fiscalweb.zip` | the `fiscalweb` folder (https://fiscal.daraerp.co.zw) |
| `fdms-gateway.apk` | phones |

## 1. Database
Back up `fdms_gateway`, then run `fdms-update-referrals-and-coins.sql`. It contains the referral tables and the new coins table, and skips whatever is already there, so it is safe whether or not you ran the 2026-10-09 referral script.

## 2. API
1. Stop the API's application pool.
2. Copy the 8 files from `fiscalapi-dll.zip` into the API folder (replace).
3. In `appsettings.json` make sure these are there (from the last deployment):
   ```json
   "Cors": { "Origins": [ "https://fiscal.daraerp.co.zw" ] },
   "Hosting": { "AppUrl": "https://fiscal.daraerp.co.zw" }
   ```
4. Start the pool. https://fiscalapi.daraerp.co.zw/health should answer `Healthy`.

## 3. Web console
Empty the `fiscalweb` folder and unzip `fiscalweb.zip` into it. Then Ctrl+F5 in the browser.

## 4. Payment addresses (do this once)
- **Where customers come back after paying** = the web console. Sign in as the platform administrator → Settings → *Web app address* = `https://fiscal.daraerp.co.zw` (no `/app`).
- **Where the gateways tell us a payment went through** = the API (they never point at the console):
  - Stripe dashboard → Developers → Webhooks → endpoint `https://fiscalapi.daraerp.co.zw/api/v1/payments/stripe/webhook`, event `checkout.session.completed`; copy its signing secret into Settings → Payment gateways → Stripe.
  - Paynow: nothing to set; we send it `https://fiscalapi.daraerp.co.zw/api/v1/payments/paynow/result` with every payment.
  - EcoCash: notify address `https://fiscalapi.daraerp.co.zw/api/v1/payments/ecocash/notify`.
- Even if a webhook is missing or late, the API now asks the gateway itself every 2 minutes about payments still pending, and the Billing page checks again by itself, so "Pay" turns into "Paid" without the customer doing anything.

## 5. Referral rewards: coins
Administration → Referral program:
- **Reward given as: Coins** (default). Companies earn coins instead of cash: the reward % of the referred company's invoice × *coins per 1.00* (default 10 coins = 1.00). Levels can add bonus coins and free months.
- **Coins shop** (edit freely): extra days, extra devices / users / branches above the plan, or *pay an invoice with coins*. Coins are never paid out as money; invoices paid with coins earn nobody more coins.
- **Coin balances**: give or take coins by hand (a goodwill gesture or a correction). Everything is in the audit log.
- Switch to **Money** any time to pay rewards out in cash instead (you then mark each reward paid).

## 6. Manual payments (bank transfer, EcoCash Send Money, PayPal)
- The payment reference is required, and each reference can be used once (spaces, dashes and upper/lower case are ignored when comparing). A reused reference is refused with "The reference … was already used for a payment". After you reject a payment, its reference can be used again.
- Proof of payment is optional and may only be a PDF or a picture (PNG, JPEG, WebP), up to 5 MB. The file's content is checked, not just its name.
- In the mobile app: Billing → Pay now → choose Bank transfer or EcoCash (Send Money): the app shows the account details (or a ready-to-dial EcoCash code), then asks for the reference and an optional proof.
