import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:provider/provider.dart';
import 'package:url_launcher/url_launcher.dart';

import '../core/api.dart';
import '../core/auth.dart';
import '../core/models.dart';
import '../ui/files.dart';
import '../ui/kit.dart';
import '../ui/lists.dart';
import '../ui/premium.dart';
import 'bridge.dart';

const _moneyTypes = ['Credit', 'Cash', 'Card', 'MobileWallet', 'BankTransfer', 'Coupon', 'Other'];

/// Zoho Books for one company: connect (the sign-in opens in the browser), choose the organisation and the fiscal device, and fiscalise what comes in from Zoho.
class ZohoScreen extends StatefulWidget {
  const ZohoScreen({super.key});
  @override
  State<ZohoScreen> createState() => _ZohoScreenState();
}

class _ZohoScreenState extends State<ZohoScreen> with WidgetsBindingObserver {
  final _loader = GlobalKey<LoaderState<Json>>();
  String _status = '';
  String _region = 'com';
  int _page = 1;
  List<Json> _docs = [];
  int _total = 0;
  List<Json> _devices = [];
  bool _busy = false;
  bool _ownMode = false;
  final _ownId = TextEditingController();
  final _ownSecret = TextEditingController();
  final _ownCode = TextEditingController();

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    for (final c in [_ownId, _ownSecret, _ownCode]) { c.dispose(); }
    super.dispose();
  }

  /// Coming back from the Zoho sign-in in the browser: show where things stand.
  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state == AppLifecycleState.resumed) _loader.currentState?.reload();
  }

  Future<Json> _load() async {
    final api = context.read<Api>();
    final c = await api.getMap('/api/v1/integrations/zoho');
    if (c['status'] == 'Connected') {
      await _loadDocs(api);
      try { _devices = await api.getList('/api/v1/devices'); } catch (_) {}
    }
    return c;
  }

  Future<void> _loadDocs(Api api) async {
    final p = await api.getMap('/api/v1/integrations/zoho/documents', query: {'status': _status, 'page': _page, 'pageSize': 25});
    _docs = ((p['items'] ?? []) as List).cast<Json>();
    _total = (p['total'] ?? 0) as int;
  }

  Future<void> _reload() async => _loader.currentState?.reload();

  Future<void> _connect() async {
    final api = context.read<Api>();
    setState(() => _busy = true);
    await guarded(context, () async {
      final r = await api.post('/api/v1/integrations/zoho/connect', body: {'region': _region}) as Map;
      await launchUrl(Uri.parse(r['authorizeUrl'].toString()), mode: LaunchMode.externalApplication);
    });
    if (mounted) setState(() => _busy = false);
  }

  Future<void> _sync() async {
    final api = context.read<Api>();
    setState(() => _busy = true);
    try {
      final r = await api.post('/api/v1/integrations/zoho/sync') as Map;
      if (!mounted) return;
      if (r['error'] != null) {
        toast(context, r['error'].toString(), error: true);
      } else {
        toast(context, 'Synced: ${r['new']} new, ${r['updated']} updated${(r['fiscalised'] ?? 0) + (r['failed'] ?? 0) > 0 ? ', ${r['fiscalised']} fiscalised, ${r['failed']} failed' : ''}.');
      }
    } catch (e) {
      if (mounted) toast(context, e, error: true);
    }
    if (mounted) setState(() => _busy = false);
    await _reload();
  }

  Future<void> _act(Json d, String action, String done) async {
    final api = context.read<Api>();
    final ok = await guarded(context, () => api.post('/api/v1/integrations/zoho/documents/${d['id']}/$action'), done: done);
    if (ok) await _reload();
  }

  Future<void> _pdf(Json d) async {
    final api = context.read<Api>();
    await guarded(context, () async {
      final bytes = await api.bytes('/api/v1/receipts/${d['receiptId']}/pdf', query: {'format': 'a4'});
      if (mounted) await openBytes(context, bytes, 'fiscal-${d['externalNumber']}.pdf');
    });
  }

  Future<void> _settings(Json c) async {
    final saved = await Navigator.of(context).push<bool>(MaterialPageRoute(builder: (_) => _ZohoSettingsScreen(c, _devices)));
    if (saved == true) await _reload();
  }

  Future<void> _disconnect() async {
    if (!await confirm(context, 'Disconnect Zoho Books? Nothing more is pulled or fiscalised. Documents already fiscalised stay.', ok: 'Disconnect', danger: true)) return;
    if (!mounted) return;
    final api = context.read<Api>();
    if (await guarded(context, () => api.delete('/api/v1/integrations/zoho'), done: 'Disconnected.')) await _reload();
  }

  @override
  Widget build(BuildContext context) {
    return Loader<Json>(
      key: _loader,
      load: _load,
      builder: (context, c, reload) {
        final status = c['status']?.toString() ?? 'NotConnected';
        return RefreshIndicator(
          onRefresh: reload,
          child: ListView(padding: const EdgeInsets.fromLTRB(16, 12, 16, 24), children: [
            if (c['available'] != true)
              InfoCard(title: 'Not available yet', icon: Icons.info_outline, children: [Padding(padding: const EdgeInsets.only(bottom: 12), child: Text('${c['unavailableReason']}'))])
            else if (status == 'NotConnected' || status == 'NeedsReconnect')
              _connectCard(c, status)
            else if (status == 'ChooseOrganization')
              _chooseCard(c)
            else ..._connected(c),
          ]),
        );
      },
    );
  }

  Widget _connectCard(Json c, String status) {
    final regions = ((c['regions'] ?? []) as List).cast<Json>();
    final p = Map<String, dynamic>.from((c['pricing'] ?? {}) as Map);
    final platform = c['platformApp'] == true;
    final useOwn = _ownMode || !platform || c['ownClientId'] != null;
    final hint = Theme.of(context).hintColor;
    if (_ownId.text.isEmpty && c['ownClientId'] != null) _ownId.text = c['ownClientId'].toString();
    return InfoCard(title: status == 'NeedsReconnect' ? 'Reconnect Zoho Books' : 'Connect Zoho Books', icon: Icons.link_rounded, children: [
      if (status == 'NeedsReconnect') Padding(padding: const EdgeInsets.only(bottom: 10), child: Text('Zoho stopped accepting this connection (${c['lastError']}). Connect again to carry on.', style: TextStyle(color: toneColor(Tone.bad)))),
      DropdownButtonFormField<String>(
        initialValue: _region,
        isExpanded: true,
        decoration: const InputDecoration(labelText: 'Zoho data centre'),
        items: [for (final r in regions) DropdownMenuItem(value: r['key'].toString(), child: Text(r['label'].toString(), overflow: TextOverflow.ellipsis))],
        onChanged: (v) => setState(() => _region = v ?? 'com'),
      ),
      const SizedBox(height: 12),
      if (platform)
        SegmentedButton<bool>(
          segments: const [ButtonSegment(value: false, label: Text('Sign in with Zoho')), ButtonSegment(value: true, label: Text('My own Zoho app'))],
          selected: {useOwn},
          onSelectionChanged: (v) => setState(() => _ownMode = v.first),
        ),
      const SizedBox(height: 12),
      if (!useOwn) ...[
        const Text('Sign in to Zoho in the browser and allow access to Zoho Books. Then come back here.'),
        const SizedBox(height: 12),
        FilledButton.icon(onPressed: _busy ? null : _connect, icon: const Icon(Icons.open_in_new_rounded, size: 18), label: Text(status == 'NeedsReconnect' ? 'Reconnect' : 'Connect')),
      ] else ...[
        Text('1. At api-console.zoho.com (as your Zoho Books admin) choose Add Client → Self Client.\n'
            '2. Copy its Client ID and Client Secret below.\n'
            '3. Generate Code with scope ZohoBooks.fullaccess.all (10 minutes), paste it and tap Connect straight away.', style: TextStyle(fontSize: 12.5, color: hint)),
        const SizedBox(height: 8),
        TextField(controller: _ownId, decoration: const InputDecoration(labelText: 'Client ID', hintText: '1000.XXXXXXXX')),
        TextField(controller: _ownSecret, obscureText: true, decoration: InputDecoration(labelText: 'Client secret', helperText: c['ownClientId'] != null ? 'Saved. Leave empty to keep it.' : null)),
        TextField(controller: _ownCode, decoration: const InputDecoration(labelText: 'Generated code')),
        const SizedBox(height: 12),
        Wrap(spacing: 8, children: [
          FilledButton.icon(onPressed: _busy ? null : _connectOwn, icon: const Icon(Icons.link_rounded, size: 18), label: const Text('Connect')),
          if (c['ownClientId'] != null) TextButton(onPressed: _forgetOwn, child: Text('Remove my app', style: TextStyle(color: toneColor(Tone.bad)))),
        ]),
      ],
      if (p['charge'] == true) Padding(padding: const EdgeInsets.only(top: 10), child: Text('Costs ${moneyIn(p['monthlyFee'] as num?, p['currency']?.toString())} a month with ${p['includedDocuments']} documents included, then ${p['pricePerExtraDocument']} per document, added to your subscription invoice.', style: TextStyle(fontSize: 12, color: hint))),
      const SizedBox(height: 12),
    ]);
  }

  Future<void> _connectOwn() async {
    final api = context.read<Api>();
    setState(() => _busy = true);
    try {
      final r = await api.post('/api/v1/integrations/zoho/own-app', body: {
        'region': _region, 'clientId': _ownId.text.trim(), 'clientSecret': _ownSecret.text.isEmpty ? null : _ownSecret.text.trim(), 'grantCode': _ownCode.text.trim().isEmpty ? null : _ownCode.text.trim(),
      }) as Map;
      if (r['authorizeUrl'] != null) {
        await launchUrl(Uri.parse(r['authorizeUrl'].toString()), mode: LaunchMode.externalApplication);
      } else {
        _ownSecret.clear(); _ownCode.clear();
        if (mounted) toast(context, 'Zoho Books is connected.');
        await _reload();
      }
    } catch (e) {
      if (mounted) toast(context, e, error: true);
    }
    if (mounted) setState(() => _busy = false);
  }

  Future<void> _forgetOwn() async {
    if (!await confirm(context, 'Remove your own Zoho app? You will need to connect again.', ok: 'Remove', danger: true)) return;
    if (!mounted) return;
    final api = context.read<Api>();
    if (await guarded(context, () => api.delete('/api/v1/integrations/zoho/own-app'), done: 'Removed.')) { _ownId.clear(); await _reload(); }
  }

  Widget _chooseCard(Json c) {
    final orgs = ((c['organizations'] ?? []) as List).cast<Json>();
    final api = context.read<Api>();
    return InfoCard(title: 'Which organisation?', icon: Icons.apartment_rounded, children: [
      for (final o in orgs)
        ListTile(
          contentPadding: EdgeInsets.zero,
          title: Text(o['name'].toString()),
          subtitle: Text('ID ${o['id']}${o['currencyCode'] != null ? ' · ${o['currencyCode']}' : ''}'),
          trailing: const Icon(Icons.chevron_right_rounded),
          onTap: () async { if (await guarded(context, () => api.post('/api/v1/integrations/zoho/organization', body: {'organizationId': o['id']}), done: 'Connected. Now choose the fiscal device.')) await _reload(); },
        ),
    ]);
  }

  List<Widget> _connected(Json c) {
    final counts = Map<String, dynamic>.from((c['counts'] ?? {}) as Map);
    final hint = Theme.of(context).hintColor;
    final problem = c['lastError'] != null;
    return [
      InfoCard(
        title: c['organizationName']?.toString() ?? 'Zoho Books',
        icon: Icons.cloud_sync_rounded,
        trailing: Badge2(problem ? 'Problem' : 'Connected', tone: problem ? Tone.bad : Tone.ok),
        children: [
          InfoRow('Last sync', c['lastSyncAt'] == null ? 'not yet' : fmtDateTime(c['lastSyncAt']), icon: Icons.sync_rounded),
          InfoRow('Token renewed', c['lastTokenRefreshAt'] == null ? '—' : fmtDateTime(c['lastTokenRefreshAt']), icon: Icons.key_rounded),
          InfoRow('Zoho calls today', '${c['apiCallsToday']} (${c['apiCallsTotal']} in all)', icon: Icons.speed_rounded),
          InfoRow('Fiscalising', c['autoFiscalise'] == true ? 'Automatic' : 'When you tap Fiscalise', icon: Icons.bolt_rounded, last: !problem),
          if (problem) Padding(padding: const EdgeInsets.only(bottom: 12), child: Text('${c['lastError']}', style: TextStyle(color: toneColor(Tone.bad), fontSize: 12.5))),
        ],
      ),
      Wrap(spacing: 8, runSpacing: 8, children: [
        FilledButton.icon(onPressed: _busy ? null : _sync, icon: const Icon(Icons.sync_rounded, size: 18), label: Text(_busy ? 'Syncing…' : 'Sync now')),
        OutlinedButton.icon(onPressed: () => _settings(c), icon: const Icon(Icons.tune_rounded, size: 18), label: const Text('Settings')),
        TextButton.icon(onPressed: _disconnect, icon: Icon(Icons.link_off_rounded, size: 18, color: toneColor(Tone.bad)), label: Text('Disconnect', style: TextStyle(color: toneColor(Tone.bad)))),
      ]),
      const SizedBox(height: 12),
      FilterPills([
        for (final f in const [('', 'All'), ('Pending', 'Waiting'), ('Failed', 'Failed'), ('Fiscalised', 'Fiscalised'), ('Changed', 'Changed after'), ('NotPostedBack', 'Not written back'), ('Skipped', 'Skipped')])
          PillOption(f.$2, _status == f.$1, () { setState(() { _status = f.$1; _page = 1; }); _reload(); },
              count: f.$1 == 'Pending' ? counts['pending'] as int? : f.$1 == 'Failed' ? counts['failed'] as int? : null),
      ]),
      const SizedBox(height: 8),
      if (_docs.isEmpty) Padding(padding: const EdgeInsets.all(24), child: Text('Nothing here yet. Raise an invoice in Zoho Books (not a draft) and tap Sync now.', textAlign: TextAlign.center, style: TextStyle(color: hint))),
      for (final d in _docs) _docTile(d),
      if (_total > _docs.length + (_page - 1) * 25 || _page > 1)
        Row(mainAxisAlignment: MainAxisAlignment.center, children: [
          IconButton(onPressed: _page > 1 ? () { setState(() => _page--); _reload(); } : null, icon: const Icon(Icons.chevron_left_rounded)),
          Text('Page $_page'),
          IconButton(onPressed: _page * 25 < _total ? () { setState(() => _page++); _reload(); } : null, icon: const Icon(Icons.chevron_right_rounded)),
        ]),
      const SizedBox(height: 8),
      OutlinedButton.icon(onPressed: () => Navigator.of(context).push(MaterialPageRoute(builder: (_) => const BridgeGrantsScreen())), icon: const Icon(Icons.share_outlined, size: 18), label: const Text('Partner access (Zoho bridge)')),
    ];
  }

  Widget _docTile(Json d) {
    final hint = Theme.of(context).hintColor;
    final status = d['status'].toString();
    return AppCard(
      padding: const EdgeInsets.all(12),
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Row(children: [
          Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text('${d['externalNumber']}', style: const TextStyle(fontWeight: FontWeight.w700)),
            Text('${d['kind'] == 'creditnote' ? 'Credit note' : 'Invoice'} · ${d['customerName'] ?? ''} · ${fmtDate(d['documentDate'])}', style: TextStyle(fontSize: 12, color: hint), maxLines: 1, overflow: TextOverflow.ellipsis),
          ])),
          Side(fraction: 0.38, children: [Amount(moneyIn(d['total'] as num?, d['currency']?.toString()), style: const TextStyle(fontWeight: FontWeight.w700)), const SizedBox(height: 4), Badge2(status, tone: status == 'Fiscalised' ? Tone.ok : toneFor(status))]),
        ]),
        if (d['fiscalInvoiceNo'] != null) Padding(padding: const EdgeInsets.only(top: 4), child: Text('Fiscal no. ${d['fiscalInvoiceNo']}', style: TextStyle(fontSize: 12, color: hint))),
        if (d['changedAfterFiscalised'] == true) Padding(padding: const EdgeInsets.only(top: 4), child: Text('Changed in Zoho after fiscalising: raise a credit note there.', style: TextStyle(fontSize: 12, color: toneColor(Tone.warn)))),
        if (d['error'] != null) Padding(padding: const EdgeInsets.only(top: 4), child: Text('${d['error']}', style: TextStyle(fontSize: 12, color: toneColor(Tone.bad)))),
        if (d['postBackError'] != null) Padding(padding: const EdgeInsets.only(top: 4), child: Text('Not all written back: ${d['postBackError']}', style: TextStyle(fontSize: 12, color: toneColor(Tone.warn)))),
        Wrap(spacing: 4, children: [
          if (status == 'Pending' || status == 'Failed') TextButton(onPressed: () => _act(d, 'fiscalise', 'Done.'), child: Text(status == 'Failed' ? 'Retry' : 'Fiscalise')),
          if (status != 'Fiscalised') TextButton(onPressed: () => _act(d, 'skip', status == 'Skipped' ? 'Brought back.' : 'Skipped.'), child: Text(status == 'Skipped' ? 'Bring back' : 'Skip')),
          if (status == 'Fiscalised') TextButton(onPressed: () => _pdf(d), child: const Text('PDF')),
          if (status == 'Fiscalised' && (d['postedBackAt'] == null || d['postBackError'] != null)) TextButton(onPressed: () => _act(d, 'post-back', 'Written back to Zoho.'), child: const Text('Write back')),
          if (d['externalUrl'] != null) TextButton(onPressed: () => launchUrl(Uri.parse(d['externalUrl'].toString()), mode: LaunchMode.externalApplication), child: const Text('Open in Zoho')),
        ]),
      ]),
    );
  }
}

class _ZohoSettingsScreen extends StatefulWidget {
  const _ZohoSettingsScreen(this.c, this.devices);
  final Json c;
  final List<Json> devices;
  @override
  State<_ZohoSettingsScreen> createState() => _ZohoSettingsScreenState();
}

class _ZohoSettingsScreenState extends State<_ZohoSettingsScreen> {
  late String? _device = widget.c['deviceId']?.toString();
  late String _money = (widget.c['defaultMoneyType'] ?? 'Credit').toString();
  late final _hs = TextEditingController(text: widget.c['defaultHsCode']?.toString() ?? '');
  late DateTime? _start = parseDate(widget.c['startDate']);
  late bool _auto = widget.c['autoFiscalise'] == true;
  late bool _post = widget.c['postBack'] != false;

  @override
  void dispose() { _hs.dispose(); super.dispose(); }

  Future<void> _save() async {
    final api = context.read<Api>();
    final ok = await guarded(context, () => api.put('/api/v1/integrations/zoho/settings', body: {
      'deviceId': _device, 'defaultMoneyType': _money, 'defaultHsCode': _hs.text.trim().isEmpty ? null : _hs.text.trim(),
      'startDate': _start == null ? null : isoDay(_start!), 'autoFiscalise': _auto, 'postBack': _post,
    }), done: 'Settings saved.');
    if (ok && mounted) Navigator.pop(context, true);
  }

  @override
  Widget build(BuildContext context) {
    final hint = Theme.of(context).hintColor;
    final hook = widget.c['webhookUrl']?.toString();
    final devices = widget.devices;
    return Scaffold(
      appBar: AppBar(title: const Text('Zoho Books settings')),
      body: ListView(padding: const EdgeInsets.all(16), children: [
        DropdownButtonFormField<String?>(
          initialValue: devices.any((d) => d['id'].toString() == _device) ? _device : null,
          isExpanded: true,
          decoration: const InputDecoration(labelText: 'Fiscal device'),
          items: [
            const DropdownMenuItem<String?>(value: null, child: Text('Choose…')),
            for (final d in devices) DropdownMenuItem<String?>(value: d['id'].toString(), child: Text('${d['name'] ?? d['zimraDeviceId']} (${d['zimraDeviceId']})', overflow: TextOverflow.ellipsis)),
          ],
          onChanged: (v) => setState(() => _device = v),
        ),
        const SizedBox(height: 12),
        DropdownButtonFormField<String>(
          initialValue: _moneyTypes.contains(_money) ? _money : 'Credit',
          isExpanded: true,
          decoration: const InputDecoration(labelText: 'How the sales are paid'),
          items: [for (final m in _moneyTypes) DropdownMenuItem(value: m, child: Text(m == 'Credit' ? 'Credit (on account)' : m))],
          onChanged: (v) => setState(() => _money = v ?? 'Credit'),
        ),
        const SizedBox(height: 12),
        TextField(controller: _hs, keyboardType: TextInputType.number, decoration: const InputDecoration(labelText: 'HS code for lines without one', helperText: 'Items in Zoho with an HS code keep their own.')),
        const SizedBox(height: 12),
        ListTile(
          contentPadding: EdgeInsets.zero,
          title: const Text('Take documents dated from'),
          subtitle: Text(_start == null ? 'Any date' : fmtDate(_start)),
          trailing: const Icon(Icons.calendar_month_outlined),
          onTap: () async {
            final d = await showDatePicker(context: context, initialDate: _start ?? DateTime.now(), firstDate: DateTime(2020), lastDate: DateTime.now().add(const Duration(days: 30)));
            if (d != null) setState(() => _start = d);
          },
        ),
        SwitchListTile(contentPadding: EdgeInsets.zero, value: _auto, onChanged: (v) => setState(() => _auto = v), title: const Text('Fiscalise automatically'), subtitle: const Text('New invoices and credit notes are fiscalised within minutes.')),
        SwitchListTile(contentPadding: EdgeInsets.zero, value: _post, onChanged: (v) => setState(() => _post = v), title: const Text('Write the result back to Zoho'), subtitle: const Text('Comment, fiscal PDF, and the custom fields Fiscal Number, Verification Code and ZIMRA QR.')),
        if (hook != null) ...[
          const SizedBox(height: 12),
          Text('Instant sync (optional)', style: const TextStyle(fontWeight: FontWeight.w700)),
          Text('In Zoho Books add a workflow rule on invoices and credit notes with a webhook to this address. Without it the gateway checks every few minutes.', style: TextStyle(fontSize: 12, color: hint)),
          ListTile(contentPadding: EdgeInsets.zero, title: Text(hook, maxLines: 2, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 12, fontFamily: 'monospace')),
              trailing: IconButton(icon: const Icon(Icons.copy_rounded), onPressed: () { Clipboard.setData(ClipboardData(text: hook)); toast(context, 'Copied.'); })),
        ],
        const SizedBox(height: 16),
        FilledButton(onPressed: _save, child: const Text('Save')),
      ]),
    );
  }
}

/// The platform owner: every company's connection (token, sync, failures, usage), the Zoho app keys and the prices.
class IntegrationsMonitorScreen extends StatelessWidget {
  const IntegrationsMonitorScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    final canSettings = context.watch<AuthController>().can('settings.manage');
    return Loader<Json>(
      load: () => api.getMap('/api/v1/admin/integrations'),
      builder: (context, m, reload) {
        final rows = ((m['connections'] ?? []) as List).cast<Json>();
        final revenue = ((m['monthlyRevenue'] ?? []) as List).cast<Json>();
        final hint = Theme.of(context).hintColor;
        return RefreshIndicator(onRefresh: reload, child: ListView(padding: const EdgeInsets.fromLTRB(16, 12, 16, 24), children: [
          ResponsiveGrid(minTileWidth: 150, children: [
            Kpi('Connected', '${m['connected'] ?? 0}', icon: Icons.link_rounded),
            Kpi('Need attention', '${m['needsAttention'] ?? 0}', icon: Icons.warning_amber_rounded, color: (m['needsAttention'] ?? 0) > 0 ? toneColor(Tone.bad) : null),
            Kpi('Fiscalised this month', '${m['fiscalisedThisMonth'] ?? 0}', icon: Icons.receipt_long_outlined),
            Kpi('Earning this month', revenue.isEmpty ? 'off' : revenue.map((r) => moneyIn(r['amount'] as num?, r['currency']?.toString())).join(' · '), icon: Icons.payments_outlined),
          ]),
          SectionTitle('Connections'),
          if (rows.isEmpty) Padding(padding: const EdgeInsets.all(24), child: Text('No company has connected Zoho Books yet.', textAlign: TextAlign.center, style: TextStyle(color: hint))),
          for (final r in rows)
            AppCard(
              padding: const EdgeInsets.all(12),
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Row(children: [
                  Expanded(child: Text('${r['company']}', style: const TextStyle(fontWeight: FontWeight.w700))),
                  Badge2(r['status'] == 'Connected' ? (r['lastError'] != null ? 'Problem' : 'Connected') : r['status'] == 'NeedsReconnect' ? 'Sign-in expired' : 'Choosing', tone: r['status'] == 'Connected' && r['lastError'] == null ? Tone.ok : Tone.bad),
                ]),
                Text('${r['organizationName'] ?? ''} · last sync ${r['lastSyncAt'] == null ? 'never' : fmtDateTime(r['lastSyncAt'])} · token ${r['lastTokenRefreshAt'] == null ? '—' : fmtDateTime(r['lastTokenRefreshAt'])}', style: TextStyle(fontSize: 12, color: hint)),
                const SizedBox(height: 6),
                Wrap(spacing: 6, runSpacing: 6, children: [
                  Pill('${r['fiscalisedThisMonth']} this month', tone: Tone.ok),
                  Pill('${r['pending']} waiting', tone: Tone.warn),
                  if ((r['failed'] ?? 0) > 0) Pill('${r['failed']} failed', tone: Tone.bad),
                  Pill('${r['apiCallsToday']} calls today', tone: Tone.neutral),
                ]),
                if (r['lastError'] != null) Padding(padding: const EdgeInsets.only(top: 6), child: Text('${r['lastError']}', style: TextStyle(fontSize: 12, color: toneColor(Tone.bad)))),
              ]),
            ),
          const SizedBox(height: 12),
          OutlinedButton.icon(onPressed: () => Navigator.of(context).push(MaterialPageRoute(builder: (_) => const BridgeMonitorScreen())), icon: const Icon(Icons.swap_horiz_rounded, size: 18), label: const Text('Zoho bridge (partners)')),
          if (canSettings) ...[
            SectionTitle('Settings'),
            Wrap(spacing: 8, runSpacing: 8, children: [
              OutlinedButton.icon(onPressed: () => Navigator.of(context).push(MaterialPageRoute(builder: (_) => const _ZohoAppScreen())), icon: const Icon(Icons.vpn_key_outlined, size: 18), label: const Text('Zoho app')),
              OutlinedButton.icon(onPressed: () async { await Navigator.of(context).push(MaterialPageRoute(builder: (_) => const _PricingScreen())); await reload(); }, icon: const Icon(Icons.sell_outlined, size: 18), label: const Text('Prices')),
            ]),
          ],
        ]));
      },
    );
  }
}

class _ZohoAppScreen extends StatefulWidget {
  const _ZohoAppScreen();
  @override
  State<_ZohoAppScreen> createState() => _ZohoAppScreenState();
}

class _ZohoAppScreenState extends State<_ZohoAppScreen> {
  final _id = TextEditingController();
  final _secret = TextEditingController();
  bool _clear = false;
  bool _filled = false;

  @override
  void dispose() { _id.dispose(); _secret.dispose(); super.dispose(); }

  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    return Scaffold(
      appBar: AppBar(title: const Text('Zoho app')),
      body: Loader<Json>(
        load: () => api.getMap('/api/v1/admin/integrations/zoho-app'),
        builder: (context, a, reload) {
          if (!_filled) { _id.text = a['clientId']?.toString() ?? ''; _filled = true; }
          return ListView(padding: const EdgeInsets.all(16), children: [
            Text('Register a Server-based application at api-console.zoho.com with this redirect address (switch on multi-DC), then enter its client ID and secret.', style: TextStyle(color: Theme.of(context).hintColor)),
            ListTile(contentPadding: EdgeInsets.zero, title: Text('${a['redirectUri']}', style: const TextStyle(fontSize: 12, fontFamily: 'monospace')),
                trailing: IconButton(icon: const Icon(Icons.copy_rounded), onPressed: () { Clipboard.setData(ClipboardData(text: '${a['redirectUri']}')); toast(context, 'Copied.'); })),
            TextField(controller: _id, decoration: const InputDecoration(labelText: 'Client ID')),
            const SizedBox(height: 12),
            TextField(controller: _secret, obscureText: true, decoration: InputDecoration(labelText: 'Client secret', helperText: a['hasSecret'] == true ? 'Saved. Leave empty to keep it.' : null)),
            if (a['hasSecret'] == true) CheckboxListTile(contentPadding: EdgeInsets.zero, value: _clear, onChanged: (v) => setState(() => _clear = v ?? false), title: const Text('Remove the saved secret')),
            const SizedBox(height: 16),
            FilledButton(
              onPressed: () async {
                final ok = await guarded(context, () => api.put('/api/v1/admin/integrations/zoho-app', body: {'clientId': _id.text.trim().isEmpty ? null : _id.text.trim(), 'clientSecret': _secret.text.isEmpty ? null : _secret.text, 'clearSecret': _clear}), done: 'Zoho app saved.');
                if (ok) { _secret.clear(); await reload(); }
              },
              child: const Text('Save'),
            ),
          ]);
        },
      ),
    );
  }
}

class _PricingScreen extends StatefulWidget {
  const _PricingScreen();
  @override
  State<_PricingScreen> createState() => _PricingScreenState();
}

class _PricingScreenState extends State<_PricingScreen> {
  final _fee = TextEditingController();
  final _cur = TextEditingController();
  final _inc = TextEditingController();
  final _per = TextEditingController();
  final _bridge = TextEditingController();
  bool _charge = false;
  bool _filled = false;

  @override
  void dispose() { for (final c in [_fee, _cur, _inc, _per, _bridge]) { c.dispose(); } super.dispose(); }

  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    const number = TextInputType.numberWithOptions(decimal: true);
    return Scaffold(
      appBar: AppBar(title: const Text('Connection prices')),
      body: Loader<Json>(
        load: () => api.getMap('/api/v1/admin/integrations/pricing'),
        builder: (context, p, reload) {
          if (!_filled) {
            _charge = p['charge'] == true; _fee.text = '${p['monthlyFee']}'; _cur.text = '${p['currency']}'; _inc.text = '${p['includedDocuments']}'; _per.text = '${p['pricePerExtraDocument']}'; _bridge.text = '${p['bridgeMonthlyFee'] ?? 0}';
            _filled = true;
          }
          return ListView(padding: const EdgeInsets.all(16), children: [
            Text('Added to each connected company\'s subscription invoice: the monthly fee, and every document above the included number.', style: TextStyle(color: Theme.of(context).hintColor)),
            SwitchListTile(contentPadding: EdgeInsets.zero, value: _charge, onChanged: (v) => setState(() => _charge = v), title: const Text('Charge for connections')),
            TextField(controller: _fee, keyboardType: number, decoration: const InputDecoration(labelText: 'Monthly fee')),
            const SizedBox(height: 12),
            TextField(controller: _cur, textCapitalization: TextCapitalization.characters, maxLength: 3, decoration: const InputDecoration(labelText: 'Currency')),
            TextField(controller: _inc, keyboardType: TextInputType.number, decoration: const InputDecoration(labelText: 'Documents included a month')),
            const SizedBox(height: 12),
            TextField(controller: _per, keyboardType: number, decoration: const InputDecoration(labelText: 'Price per extra document')),
            const SizedBox(height: 12),
            TextField(controller: _bridge, keyboardType: number, decoration: const InputDecoration(labelText: 'Zoho bridge: monthly fee per sharing company')),
            const SizedBox(height: 16),
            FilledButton(
              onPressed: () async {
                final ok = await guarded(context, () => api.put('/api/v1/admin/integrations/pricing', body: {
                  'charge': _charge, 'monthlyFee': num.tryParse(_fee.text) ?? 0, 'currency': _cur.text.trim(), 'includedDocuments': int.tryParse(_inc.text) ?? 0, 'pricePerExtraDocument': num.tryParse(_per.text) ?? 0, 'bridgeMonthlyFee': num.tryParse(_bridge.text) ?? 0,
                }), done: 'Prices saved.');
                if (ok && context.mounted) Navigator.pop(context);
              },
              child: const Text('Save'),
            ),
          ]);
        },
      ),
    );
  }
}
