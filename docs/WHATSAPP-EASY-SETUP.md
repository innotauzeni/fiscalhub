# Easy WhatsApp bot setup for companies: is it possible?

**Short answer: mostly yes, but not with *only* a company name and phone number.**
Meta does not let any system take over a company's Facebook page or WhatsApp
number just because it knows the name. The company has to **log in with Facebook
once and type a 6-digit code** that Meta sends to the phone. That is the least
setup Meta allows, and a non-technical person can still do it in about 3 minutes.

Meta calls this **WhatsApp Embedded Signup**. Most WhatsApp bot platforms
(Twilio, 360dialog, WATI, Interakt and others) use it.

---

## 1. What the company does (the "naive user" flow)

1. Open **Integration → WhatsApp** and tap **Connect WhatsApp**.
2. A Facebook pop-up opens. They log in with the Facebook account that manages
   the company's page.
3. They pick their business. If they don't have one, the pop-up creates it from
   the company name.
4. They type the phone number they want to use for the bot.
5. Meta sends a code by SMS or voice call. They type it in.
6. Done. The pop-up closes and our system does everything else.

They never see an access token, webhook URL, verify token, app secret or the
Meta developer console.

## 2. What the system does automatically after the pop-up closes

| Step | What happens |
|---|---|
| Receive result | The pop-up returns a one-time `code`, the `waba_id` and the `phone_number_id` |
| Get token | Server swaps the `code` for a business access token (Graph API `oauth/access_token`) |
| Subscribe webhooks | `POST /{waba_id}/subscribed_apps`, so messages arrive at **our one shared webhook** |
| Register number | `POST /{phone_number_id}/register` with a PIN we generate |
| Save | Fill in `WhatsAppAccount` (PhoneNumberId, BusinessAccountId, DisplayNumber, AccessToken) and set `Enabled = true` |
| Test | Send a welcome message to the admin's phone |
| Bot live | Staff assistant + customer invoice copies work straight away |

## 3. How this fits the current setup

**What we already have** (in `WhatsAppAccount`, `WhatsAppControllers.cs` and the messaging services):

- Each company has its own WhatsApp number record, with its token and app secret stored encrypted.
- Webhook endpoints: a shared one (`/api/v1/whatsapp/webhook`) and one per company (`/{accountId}`).
- The bot logic: the staff assistant, alerts, customers asking for invoices, and the greeting.
- Test send.

**What the company has to do today (the "too technical" part):**

- Create a Meta developer app.
- Copy the Phone Number ID, WABA ID and access token by hand.
- Paste our webhook URL and verify token into Meta.

**What needs to change:**

| # | Work | Size |
|---|---|---|
| 1 | **One-time platform setup (you, not the companies):** become a Meta **Tech Provider**, finish Business Verification, create one Meta app with WhatsApp + Facebook Login for Business, configure Embedded Signup and get a `config_id` | Paperwork, about 1–3 weeks for Meta review |
| 2 | Point the Meta app's webhook at the shared `/api/v1/whatsapp/webhook` once. Route incoming messages by `metadata.phone_number_id` to the right `WhatsAppAccount` | Small (the shared endpoint already exists) |
| 3 | Console: a **Connect WhatsApp** button that loads the Facebook JS SDK and calls `FB.login` with the `config_id` | Small |
| 4 | API: `POST /api/v1/messaging/whatsapp/connect` that takes `code`, `waba_id` and `phone_number_id`, then runs the steps in section 2 | Medium |
| 5 | Use the **platform app secret** for the signature check on these accounts (no app secret per company) | Small |
| 6 | Keep today's manual form as an **"Advanced"** option for companies that already have their own Meta app | Already built |
| 7 | Status page: show number status, quality rating, display-name approval and messaging limit (read from the Graph API) | Small–medium |

So **most of the bot already exists**. The new work is the one-click connect flow,
plus the Meta paperwork on your side.

## 4. Limits the company should know about (Meta's rules, not ours)

- **The number must not already be in use on the normal WhatsApp or WhatsApp Business app.**
  They either use a new number or delete the WhatsApp account on that number first.
  (Meta now allows *coexistence* for WhatsApp Business app numbers in some countries.
  Check whether Zimbabwe is supported before promising it.)
- The number must be able to receive an SMS or voice call for the code.
- The **display name** shown to customers is reviewed by Meta. It must match the
  business or page name, which is why "the company name from the Facebook page" matters.
- New numbers start with limited messaging (about 250 new customers a day for
  business-started chats) until Meta verifies the business and quality stays good.
- Meta charges per conversation or template message started by the business.
  Replies within 24 hours of a customer's message are free or cheap. Decide
  whether the company pays Meta directly (they add a card in the pop-up) or you
  resell credit through your plan billing.
- Alerts sent outside the 24-hour window need an **approved template**. We can
  submit our standard alert template for them automatically through the API.

## 5. Verdict

| Question | Answer |
|---|---|
| Can a non-technical person set up the bot? | **Yes**, with Embedded Signup |
| Can it be *only* company name + phone? | **No.** Meta requires a Facebook login and an SMS code. That's the minimum, and it's about 3 minutes |
| Can our current system support it? | **Yes.** The data model, webhooks and bot already exist; we need the connect flow (rows 3–5 above) |
| Biggest blocker | Becoming a Meta Tech Provider / Business Verification (one-time, on your side) |
