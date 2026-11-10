# Deployment, 2026-10-10: web console on its own site

| Site | Address | IIS folder | Package |
|---|---|---|---|
| Web console | https://fiscal.daraerp.co.zw | `fiscalweb` | `fiscalweb.zip` (or the `fiscalweb` folder) |
| API | https://fiscalapi.daraerp.co.zw | the API folder | `fiscalapi-dll.zip` |

Neither package contains `appsettings.json`. Only the console package has a `web.config`, and that one is for the static site.

## 1. Database (once)
Back up `fdms_gateway`, then run `fdms-update-20261009-referrals.sql` in HeidiSQL. It is safe to run twice.

## 2. API: allow the console's address
In the API folder's **appsettings.json**, add (or merge) these two sections. Keep everything else as it is:

```json
"Cors": { "Origins": [ "https://fiscal.daraerp.co.zw" ] },
"Hosting": { "AppUrl": "https://fiscal.daraerp.co.zw" }
```

`Hosting:AppUrl` makes https://fiscalapi.daraerp.co.zw/ send people to the console, and puts the right address in emails.
If you already have a `"Hosting"` section, add the `AppUrl` line to it instead of adding a second section.

## 3. API: update the program
1. Back up the API folder.
2. IIS Manager → Application Pools → the API's pool → **Stop**.
3. Unzip `fiscalapi-dll.zip` (8 files: Fdms.Api / Application / Domain / Infrastructure .dll and .pdb) into the API folder and **Replace** them.
4. **Start** the pool. Check that https://fiscalapi.daraerp.co.zw/health answers `Healthy`.

## 4. Web console: replace the fiscalweb folder
1. Install **URL Rewrite** for IIS once, if it is not installed yet: https://www.iis.net/downloads/microsoft/url-rewrite
   (Without it the site shows *HTTP Error 500.19* because web.config uses `<rewrite>`.)
2. Empty the `fiscalweb` folder (back it up first), then unzip `fiscalweb.zip` into it.
   `index.html`, `config.js` and `web.config` must sit directly in `fiscalweb`, not in a sub-folder.
3. The site needs no application pool code (it is static files); "No Managed Code" is fine.

## 5. Point emails at the new address
Sign in as the platform administrator → **Settings** → *Web app address* → `https://fiscal.daraerp.co.zw` → Save.
(A value saved here wins over `Hosting:AppUrl`, so if it still says `.../app`, change it.)

## 6. Check
- Open https://fiscal.daraerp.co.zw and press **Ctrl+F5**. Sign in.
- Open https://fiscal.daraerp.co.zw/dashboard directly. It must load, not show a 404 (the URL Rewrite rule handles this).
- If sign-in says *"Cannot reach the gateway"*: step 2 is missing or the API pool was not restarted. In the browser press F12 → Console; a "CORS" message confirms it.

## Later updates
- Console only: empty `fiscalweb`, unzip the new `fiscalweb.zip`. Keep your `config.js` if you changed it.
- If the API address ever changes, edit `fiscalweb\config.js` only. No rebuild is needed.
- The mobile app talks to the API directly and needs no change.
