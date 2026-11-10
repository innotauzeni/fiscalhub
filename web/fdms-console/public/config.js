// Where this web console finds the gateway API. Edit on the server, no rebuild needed.
//   ''                                   -> the API is on the same site (the console is served by the API at /app/)
//   'https://fiscalapi.daraerp.co.zw'    -> the console has its own site and calls the API there
// The API must list this console's address in Cors:Origins (appsettings.json).
window.FDMS_CONFIG = { apiBase: '' };
