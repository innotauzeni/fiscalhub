import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/api.dart';
import '../core/auth.dart';
import '../core/config.dart';
import '../core/models.dart';
import '../screens/profile.dart' show guardedValue;
import '../ui/files.dart';
import '../ui/kit.dart';
import '../ui/lists.dart';
import '../ui/premium.dart';
import 'devices.dart';
import 'lines.dart';
import 'payments.dart';
import 'resource.dart';

String _s(Json j, String k) => (j[k] ?? '').toString();
String _j(Iterable<String?> p) => p.where((e) => e != null && e.trim().isNotEmpty).join(' · ');

// ---- quotes --------------------------------------------------------------------------------------------------------

/// Add or change a quote: customer, validity and the items.
class QuoteFormScreen extends StatefulWidget {
  const QuoteFormScreen(this.existing, {super.key, this.recurring = false});
  final Json? existing;
  final bool recurring;
  @override
  State<QuoteFormScreen> createState() => _QuoteFormScreenState();
}

class _QuoteFormScreenState extends State<QuoteFormScreen> {
  late final TextEditingController _name, _tin, _email, _phone, _notes, _title, _prefix;
  final _taxes = TaxOption.standard();
  late List<LineData> _lines;
  String _currency = 'USD', _freq = 'Monthly', _pay = 'Cash';
  String _valid = '', _next = '', _deviceId = '';
  bool _incl = true, _emailCustomer = false, _busy = false;
  List<Json> _devices = [];
  List<String> _currencies = ['USD'];

  Json? get e => widget.existing;

  @override
  void initState() {
    super.initState();
    _name = TextEditingController(text: _s(e ?? {}, 'customerName'));
    _tin = TextEditingController(text: _s(e ?? {}, 'customerTin'));
    _email = TextEditingController(text: _s(e ?? {}, 'customerEmail'));
    _phone = TextEditingController(text: _s(e ?? {}, 'customerPhone'));
    _notes = TextEditingController(text: _s(e ?? {}, 'notes'));
    _title = TextEditingController(text: _s(e ?? {}, 'name'));
    _prefix = TextEditingController(text: e?['prefix']?.toString() ?? 'REC-');
    _currency = e?['currency']?.toString() ?? 'USD';
    _incl = e?['taxInclusive'] != false;
    _valid = (e?['validUntil'] ?? '').toString().split('T').first;
    _next = (e?['nextRunDate'] ?? isoDay(DateTime.now())).toString().split('T').first;
    _freq = e?['frequency']?.toString() ?? 'Monthly';
    _pay = e?['paymentMethod']?.toString() ?? 'Cash';
    _deviceId = e?['deviceId']?.toString() ?? '';
    _emailCustomer = e?['emailCustomer'] == true;
    final ls = ((e?['lines'] ?? []) as List).map((l) => LineData.fromQuote(Map<String, dynamic>.from(l), _taxes)).toList();
    _lines = ls.isEmpty ? [LineData(taxKey: 'pct:15')] : ls;
    _init();
  }

  Future<void> _init() async {
    final api = context.read<Api>();
    try { final c = await api.getList('/api/v1/currencies'); if (c.isNotEmpty && mounted) setState(() => _currencies = c.map((x) => x['code'].toString()).toList()); } catch (_) {}
    if (widget.recurring) { try { final d = await loadDevices(api); if (mounted) setState(() { _devices = d; if (_deviceId.isEmpty && d.isNotEmpty) _deviceId = d.first['id']; }); } catch (_) {} }
  }

  Future<void> _pickDate(bool valid) async {
    final cur = DateTime.tryParse(valid ? _valid : _next) ?? DateTime.now();
    final d = await showDatePicker(context: context, initialDate: cur, firstDate: DateTime(2000), lastDate: DateTime(2100));
    if (d != null) setState(() => valid ? _valid = isoDay(d) : _next = isoDay(d));
  }

  Future<void> _save() async {
    if (_name.text.trim().isEmpty) { toast(context, 'Enter the customer name.', error: true); return; }
    if (_lines.any((l) => l.name.trim().isEmpty || l.qty <= 0 || l.price <= 0)) { toast(context, 'Every item needs a name, a quantity and a price.', error: true); return; }
    if (widget.recurring && _title.text.trim().isEmpty) { toast(context, 'Give the schedule a name.', error: true); return; }
    final body = {
      'id': e?['id'], 'customerName': _name.text.trim(), 'customerTin': _tin.text.trim().isEmpty ? null : _tin.text.trim(), 'customerVat': null, 'customerEmail': _email.text.trim().isEmpty ? null : _email.text.trim(),
      'customerPhone': _phone.text.trim().isEmpty ? null : _phone.text.trim(), 'currency': _currency, 'taxInclusive': _incl, 'notes': _notes.text.trim().isEmpty ? null : _notes.text.trim(),
      'lines': [for (final l in _lines) l.toQuote(_taxes)],
      if (!widget.recurring) ...{'number': e?['number'], 'validUntil': _valid.isEmpty ? null : _valid},
      if (widget.recurring) ...{'deviceId': _deviceId, 'name': _title.text.trim(), 'paymentMethod': _pay, 'frequency': _freq, 'nextRunDate': _next, 'endDate': null, 'prefix': _prefix.text.trim(), 'emailCustomer': _emailCustomer},
    };
    setState(() => _busy = true);
    final api = context.read<Api>();
    final nav = Navigator.of(context);
    final ok = await guarded(context, () async { await api.put(widget.recurring ? '/api/v1/recurring' : '/api/v1/quotes', body: body); }, done: 'Saved.');
    if (mounted) setState(() => _busy = false);
    if (ok) nav.pop(true);
  }

  @override
  Widget build(BuildContext context) => Scaffold(
        appBar: AppBar(title: Text(widget.recurring ? (e == null ? 'New schedule' : 'Edit schedule') : (e == null ? 'New quote' : 'Edit quote'))),
        body: ListView(padding: const EdgeInsets.all(16), children: [
          if (widget.recurring) ...[
            TextField(controller: _title, decoration: const InputDecoration(labelText: 'Schedule name')),
            const SizedBox(height: 12),
            DevicePicker(devices: _devices, value: _deviceId.isEmpty ? null : _deviceId, onChanged: (v) => setState(() => _deviceId = v ?? '')),
            const SizedBox(height: 12),
            DropdownButtonFormField<String>(isExpanded: true, initialValue: _freq, decoration: const InputDecoration(labelText: 'Repeats'), items: [for (final f in const ['Weekly', 'Monthly', 'Quarterly', 'Yearly']) DropdownMenuItem(value: f, child: Text(f))], onChanged: (v) => setState(() => _freq = v ?? 'Monthly')),
            const SizedBox(height: 12),
            OutlinedButton.icon(onPressed: () => _pickDate(false), icon: const Icon(Icons.event), label: Text('Next invoice on ${fmtDate(_next)}')),
            const SizedBox(height: 12),
            Row(children: [Expanded(child: TextField(controller: _prefix, decoration: const InputDecoration(labelText: 'Number prefix'))), const SizedBox(width: 10), Expanded(child: DropdownButtonFormField<String>(isExpanded: true, initialValue: _pay, decoration: const InputDecoration(labelText: 'Paid by'), items: [for (final m in const ['Cash', 'Card', 'MobileWallet', 'BankTransfer', 'Other']) DropdownMenuItem(value: m, child: Text(m))], onChanged: (v) => setState(() => _pay = v ?? 'Cash')))]),
            SwitchListTile(contentPadding: EdgeInsets.zero, title: const Text('Email each invoice to the customer'), value: _emailCustomer, onChanged: (v) => setState(() => _emailCustomer = v)),
            const SectionTitle('Customer'),
          ],
          TextField(controller: _name, decoration: const InputDecoration(labelText: 'Customer name')),
          const SizedBox(height: 12),
          Row(children: [Expanded(child: TextField(controller: _tin, decoration: const InputDecoration(labelText: 'TIN'))), const SizedBox(width: 10), Expanded(child: DropdownButtonFormField<String>(isExpanded: true, initialValue: _currencies.contains(_currency) ? _currency : null, decoration: const InputDecoration(labelText: 'Currency'), items: [for (final c in _currencies) DropdownMenuItem(value: c, child: Text(c))], onChanged: (v) => setState(() => _currency = v ?? 'USD')))]),
          const SizedBox(height: 12),
          TextField(controller: _email, keyboardType: TextInputType.emailAddress, decoration: const InputDecoration(labelText: 'Email')),
          const SizedBox(height: 12),
          TextField(controller: _phone, keyboardType: TextInputType.phone, decoration: const InputDecoration(labelText: 'Phone')),
          if (!widget.recurring) ...[const SizedBox(height: 12), OutlinedButton.icon(onPressed: () => _pickDate(true), icon: const Icon(Icons.event), label: Text(_valid.isEmpty ? 'Valid until (optional)' : 'Valid until ${fmtDate(_valid)}'))],
          SwitchListTile(contentPadding: EdgeInsets.zero, title: const Text('Prices include tax'), value: _incl, onChanged: (v) => setState(() => _incl = v)),
          const SectionTitle('Items'),
          LinesEditor(lines: _lines, taxes: _taxes, currency: _currency, onChanged: () => setState(() {})),
          const SizedBox(height: 8),
          TextField(controller: _notes, maxLines: 2, decoration: const InputDecoration(labelText: 'Notes')),
          const SizedBox(height: 16),
          FilledButton(onPressed: _busy ? null : _save, child: const Text('Save')),
          const SizedBox(height: 24),
        ]),
      );
}

final quotesSpec = ResourceSpec(
  title: 'Quotes', listPath: '/api/v1/quotes', itemTitle: (j) => '${_s(j, 'number')} · ${_s(j, 'customerName')}', itemSubtitle: (j) => _j([fmtDate(j['createdAt']), j['validUntil'] != null ? 'valid to ${fmtDate(j['validUntil'])}' : null]), trailing: (j) => moneyIn(j['total'], _s(j, 'currency')),
  badges: (j) => [(_s(j, 'status'), toneFor(_s(j, 'status')))], leadingIcon: Icons.request_page_outlined, emptyText: 'No quotes yet. Quote first, then turn it into a fiscal invoice.', createLabel: 'New quote', createPerm: 'quotes.manage',
  fields: const [], saveFor: null, savePath: '/api/v1/quotes', createBuilder: _quoteBuilder,
  facts: (j) => [('Customer', _s(j, 'customerName')), ('TIN', _s(j, 'customerTin')), ('Email', _s(j, 'customerEmail')), ('Total', moneyIn(j['total'], _s(j, 'currency'))), ('Valid until', fmtDate(j['validUntil'])), for (final l in (j['lines'] ?? []) as List) ('${l['quantity']} × ${l['name']}', money(l['unitPrice'])), ('Notes', _s(j, 'notes'))],
  filters: const [('Status', 'status', ['Draft', 'Sent', 'Accepted', 'Declined', 'Invoiced'])],
  deletePath: (j) => '/api/v1/quotes/${j['id']}', deletePerm: 'quotes.manage',
  canEdit: (j) => j['status'] != 'Invoiced',
  actions: [
    RowAction('Turn into invoice', 'POST', (j) => '/api/v1/quotes/${j['id']}/convert', icon: Icons.receipt_long_outlined, perm: 'quotes.manage', when: (j) => j['status'] != 'Invoiced',
        ask: const [Fld('deviceId', 'Issue from device', kind: Kind.select, required: true, optionsPath: '/api/v1/devices', optionLabel: 'zimraDeviceId'), Fld('invoiceNo', 'Invoice number (optional)'), Fld('paymentMethod', 'Paid by', kind: Kind.select, required: true, initial: 'Cash', options: [Opt('Cash', 'Cash'), Opt('Card', 'Card'), Opt('MobileWallet', 'Mobile wallet'), Opt('BankTransfer', 'Bank transfer'), Opt('Other', 'Other')])],
        body: (j, a) => {'deviceId': a['deviceId'], 'invoiceNo': a['invoiceNo'], 'paymentMethod': a['paymentMethod']}, done: 'Invoice issued.'),
    RowAction('Email quote', 'POST', (j) => '/api/v1/quotes/${j['id']}/email', icon: Icons.mail_outline, perm: 'quotes.manage', ask: const [Fld('to', 'Send to (blank = customer email)', kind: Kind.email), Fld('message', 'Message')], body: (j, a) => {'to': a['to'], 'message': a['message']}, done: 'Quote sent.'),
  ],
);
Widget _quoteBuilder(Json? e) => QuoteFormScreen(e);

final recurringSpec = ResourceSpec(
  title: 'Recurring invoices', listPath: '/api/v1/recurring', itemTitle: (j) => _s(j, 'name'), itemSubtitle: (j) => _j([_s(j, 'customerName'), _s(j, 'frequency'), 'next ${fmtDate(j['nextRunDate'])}']), trailing: (j) => moneyIn(j['total'], _s(j, 'currency')),
  badges: (j) => [(j['isActive'] == true ? 'Active' : 'Paused', toneFor(j['isActive'] == true ? 'ok' : 'warn'))], leadingIcon: Icons.repeat, emptyText: 'No schedules yet. Set up invoices that issue themselves.', createLabel: 'New schedule', createPerm: 'recurring.manage',
  fields: const [], savePath: '/api/v1/recurring', createBuilder: _recurringBuilder,
  facts: (j) => [('Customer', _s(j, 'customerName')), ('Repeats', _s(j, 'frequency')), ('Next invoice', fmtDate(j['nextRunDate'])), ('Total', moneyIn(j['total'], _s(j, 'currency'))), ('Device', _s(j, 'zimraDeviceId')), for (final r in ((j['recent'] ?? []) as List).take(5)) ('${fmtDate(r['runDate'])}', '${r['status']} ${r['invoiceNo'] ?? r['error'] ?? ''}')],
  deletePath: (j) => '/api/v1/recurring/${j['id']}', deletePerm: 'recurring.manage',
  actions: [
    RowAction('Run now', 'POST', (j) => '/api/v1/recurring/${j['id']}/run', icon: Icons.play_arrow, perm: 'recurring.manage', confirmText: 'Issue this invoice now?', done: 'Invoice issued.'),
    RowAction('Pause', 'POST', (j) => '/api/v1/recurring/${j['id']}/active', icon: Icons.pause, perm: 'recurring.manage', when: (j) => j['isActive'] == true, body: (j, a) => {'active': false}, done: 'Paused.'),
    RowAction('Resume', 'POST', (j) => '/api/v1/recurring/${j['id']}/active', icon: Icons.play_circle_outline, perm: 'recurring.manage', when: (j) => j['isActive'] != true, body: (j, a) => {'active': true}, done: 'Resumed.'),
  ],
);
Widget _recurringBuilder(Json? e) => QuoteFormScreen(e, recurring: true);

// ---- webhooks ------------------------------------------------------------------------------------------------------

class WebhooksScreen extends StatefulWidget {
  const WebhooksScreen({super.key});
  @override
  State<WebhooksScreen> createState() => _WebhooksScreenState();
}

class _WebhooksScreenState extends State<WebhooksScreen> {
  final _key = GlobalKey<LoaderState<(Json, List<Json>)>>();

  Future<(Json, List<Json>)> _load() async {
    final api = context.read<Api>();
    final cfg = await api.getMap('/api/v1/webhook');
    var deliveries = <Json>[];
    try { deliveries = await api.getList('/api/v1/webhook/deliveries'); } catch (_) {}
    return (cfg, deliveries);
  }

  Future<void> _set(Json cfg) async {
    final url = await promptText(context, 'Webhook address (https)', hint: 'https://yourserver.com/hooks/fdms', initial: _s(cfg, 'url'), ok: 'Save', keyboard: TextInputType.url);
    if (url == null || !mounted) return;
    final r = await guardedValue(context, () => context.read<Api>().put('/api/v1/webhook', body: {'url': url.trim()}));
    if (r == null || !mounted) return;
    if ((r['secret'] ?? '').toString().isNotEmpty) {
      await showDialog<void>(context: context, builder: (d) => AlertDialog(title: const Text('Your signing secret'), content: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [const Text('Shown once. Use it to check that calls really come from us.'), const SizedBox(height: 8), SelectableText(r['secret'].toString(), style: const TextStyle(fontFamily: 'monospace'))]), actions: [FilledButton(onPressed: () => Navigator.pop(d), child: const Text('Done'))]));
    }
    _key.currentState?.reload();
  }

  @override
  Widget build(BuildContext context) {
    final can = context.watch<AuthController>().can('webhooks.manage');
    return Loader<(Json, List<Json>)>(
      key: _key,
      load: _load,
      builder: (context, data, reload) {
        final (cfg, deliveries) = data;
        final url = _s(cfg, 'url');
        return ListView(padding: const EdgeInsets.all(16), children: [
          AppCard(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            const Text('Webhook', style: TextStyle(fontWeight: FontWeight.w800, fontSize: 16)),
            const SizedBox(height: 4),
            Text('We call this address when a receipt or fiscal day changes, signed with your secret.', style: TextStyle(fontSize: 12, color: Theme.of(context).hintColor)),
            const SizedBox(height: 10),
            Text(url.isEmpty ? 'Not set up' : url, style: TextStyle(fontFamily: url.isEmpty ? null : 'monospace', fontWeight: FontWeight.w600)),
            if (can) Padding(padding: const EdgeInsets.only(top: 12), child: Wrap(spacing: 8, children: [FilledButton(onPressed: () => _set(cfg), child: Text(url.isEmpty ? 'Set up' : 'Change')), if (url.isNotEmpty) OutlinedButton(style: OutlinedButton.styleFrom(foregroundColor: Colors.red.shade700), onPressed: () async { if (await confirm(context, 'Switch the webhook off?', ok: 'Remove', danger: true) && context.mounted) { if (await guarded(context, () async { await context.read<Api>().delete('/api/v1/webhook'); }, done: 'Removed.')) reload(); } }, child: const Text('Remove'))])),
          ])),
          const SectionTitle('Recent deliveries'),
          if (deliveries.isEmpty) const AppCard(child: Text('Nothing has been sent yet.')),
          for (final d in deliveries.take(30)) Padding(padding: const EdgeInsets.only(bottom: 8), child: AppCard(padding: const EdgeInsets.all(12), child: Row(children: [Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [Text(_s(d, 'eventType'), style: const TextStyle(fontWeight: FontWeight.w600)), Text('${fmtDateTime(d['createdAt'])} · ${d['attempts']} attempt(s)${d['lastError'] != null ? ' · ${d['lastError']}' : ''}', style: TextStyle(fontSize: 12, color: Theme.of(context).hintColor))])), StatusBadge(_s(d, 'status'))]))),
        ]);
      },
    );
  }
}

// ---- alerts --------------------------------------------------------------------------------------------------------

class AlertsScreen extends StatefulWidget {
  const AlertsScreen({super.key});
  @override
  State<AlertsScreen> createState() => _AlertsScreenState();
}

class _AlertsScreenState extends State<AlertsScreen> {
  final _emails = TextEditingController();
  Set<String> _on = {};
  Json? _s0;
  Object? _error;
  bool _busy = false;

  @override
  void initState() { super.initState(); _load(); }

  Future<void> _load() async {
    try {
      final s = await context.read<Api>().getMap('/api/v1/notifications/settings');
      if (!mounted) return;
      setState(() { _s0 = s; _emails.text = _s(s, 'emails'); _on = {for (final k in (s['enabledKinds'] ?? []) as List) k.toString()}; _error = null; });
    } catch (e) { if (mounted) setState(() => _error = e); }
  }

  Future<void> _save() async {
    setState(() => _busy = true);
    await guarded(context, () async { await context.read<Api>().put('/api/v1/notifications/settings', body: {'emails': _emails.text.trim().isEmpty ? null : _emails.text.trim(), 'enabledKinds': _on.toList()}); }, done: 'Saved.');
    if (mounted) setState(() => _busy = false);
  }

  @override
  Widget build(BuildContext context) {
    if (_error != null && _s0 == null) return ErrorState(_error!, onRetry: _load);
    final s = _s0;
    if (s == null) return const Center(child: CircularProgressIndicator());
    final can = context.watch<AuthController>().can('alerts.manage');
    return ListView(padding: const EdgeInsets.all(16), children: [
      TextField(controller: _emails, enabled: can, decoration: InputDecoration(labelText: 'Email alerts to (comma separated)', helperText: _s(s, 'fallbackNote'))),
      const SectionTitle('What to tell you about'),
      for (final k in (s['kinds'] ?? []) as List)
        SwitchListTile(contentPadding: EdgeInsets.zero, title: Text(_s(k, 'label')), subtitle: Text(_s(k, 'help'), style: const TextStyle(fontSize: 12)), value: _on.contains(k['id']), onChanged: can ? (v) => setState(() => v ? _on.add(k['id']) : _on.remove(k['id'])) : null),
      if (can) FilledButton(onPressed: _busy ? null : _save, child: const Text('Save')),
    ]);
  }
}

// ---- billing -------------------------------------------------------------------------------------------------------

class BillingScreen extends StatelessWidget {
  const BillingScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    return Loader<Json>(
      load: () => api.getMap('/api/v1/billing'),
      builder: (context, o, reload) {
        final sub = o['subscription'] as Map?;
        final plan = o['plan'] as Map?;
        final usage = o['usage'] as Map?;
        final invoices = ((o['invoices'] ?? []) as List).cast<Map<String, dynamic>>();
        final access = o['access'] as Map?;
        final open = invoices.where((i) => i['status'] == 'Open').toList();
        return ListView(padding: const EdgeInsets.fromLTRB(16, 12, 16, 24), children: [
          if (access?['locked'] == true)
            Container(
              margin: const EdgeInsets.only(bottom: 12), padding: const EdgeInsets.all(14),
              decoration: BoxDecoration(color: const Color(0xFFDC2626).withValues(alpha: 0.08), borderRadius: BorderRadius.circular(16), border: Border.all(color: const Color(0xFFDC2626).withValues(alpha: 0.25))),
              child: const Row(children: [Icon(Icons.lock_outline_rounded, color: Color(0xFFDC2626)), SizedBox(width: 10), Expanded(child: Text('Your account is waiting for payment. Pay the open invoice below to unlock everything.', style: TextStyle(fontWeight: FontWeight.w600)))]),
            ),
          Container(
            padding: const EdgeInsets.all(20),
            decoration: BoxDecoration(gradient: heroGradient(), borderRadius: BorderRadius.circular(24), boxShadow: [BoxShadow(color: const Color(0xFF4F46E5).withValues(alpha: 0.28), blurRadius: 22, offset: const Offset(0, 10))]),
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Text('YOUR PLAN', style: TextStyle(color: Colors.white.withValues(alpha: 0.7), fontSize: 11, fontWeight: FontWeight.w800, letterSpacing: 1)),
              const SizedBox(height: 4),
              Text(plan?['name']?.toString() ?? 'No plan', style: const TextStyle(color: Colors.white, fontSize: 26, fontWeight: FontWeight.w900, letterSpacing: -0.5)),
              if (sub != null) Padding(padding: const EdgeInsets.only(top: 4), child: Text('${niceLabel(sub['billingCycle']?.toString() ?? '')} · current period until ${fmtDate(sub['currentPeriodEnd'])}', style: TextStyle(color: Colors.white.withValues(alpha: 0.82)))),
              const SizedBox(height: 14),
              Wrap(spacing: 8, runSpacing: 8, children: [
                if (sub != null) HeroChip(statusText(sub['status']?.toString()), icon: Icons.circle),
                if (sub?['trialEndsAt'] != null) HeroChip('Trial ends ${fmtDate(sub!['trialEndsAt'])}', icon: Icons.hourglass_bottom_rounded),
                if (usage != null) ...[
                  HeroChip('${usage['devices']} devices', icon: Icons.memory_rounded),
                  HeroChip('${usage['users']} users', icon: Icons.people_outline),
                  HeroChip('${usage['receiptsThisMonth']} receipts this month', icon: Icons.receipt_long_rounded),
                ],
              ]),
            ]),
          ),
          if (open.isNotEmpty) ...[
            const ListHeader('To pay'),
            for (final i in open)
              Card(child: Padding(padding: const EdgeInsets.all(16), child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Row(children: [Expanded(child: Text(_s(i, 'number'), style: const TextStyle(fontWeight: FontWeight.w800))), Text('due ${fmtDate(i['dueAt'])}', style: TextStyle(fontSize: 12, color: Theme.of(context).hintColor))]),
                if (_pendingOf(i) != null) Padding(padding: const EdgeInsets.only(top: 6), child: Row(children: [const SizedBox.square(dimension: 14, child: CircularProgressIndicator(strokeWidth: 2)), const SizedBox(width: 8), Expanded(child: Text('Checking your payment… it turns to Paid by itself.', style: TextStyle(fontSize: 12.5, color: Colors.blue.shade700, fontWeight: FontWeight.w600)))])),
                if ((i['payments'] as List? ?? []).any((p) => p['status'] == 'AwaitingConfirmation')) Padding(padding: const EdgeInsets.only(top: 6), child: Text('Payment sent for confirmation. We confirm it within one working day.', style: TextStyle(fontSize: 12.5, color: Colors.blue.shade700, fontWeight: FontWeight.w600))),
                const SizedBox(height: 4),
                FittedBox(fit: BoxFit.scaleDown, alignment: Alignment.centerLeft, child: Text(moneyIn(i['total'], _s(i, 'currency')), style: const TextStyle(fontSize: 28, fontWeight: FontWeight.w900, letterSpacing: -0.5))),
                if (_s(i, 'description').isNotEmpty) Text(_s(i, 'description'), style: TextStyle(fontSize: 12, color: Theme.of(context).hintColor)),
                const SizedBox(height: 12),
                Row(children: [
                  Expanded(child: _pendingOf(i) != null
                      ? FilledButton.icon(onPressed: () => waitForPayment(context, (_pendingOf(i)!['id'] ?? '').toString(), 'Checking with the payment provider…', reload), icon: const Icon(Icons.refresh_rounded, size: 18), label: const Text('Check now'))
                      : FilledButton.icon(onPressed: () => payInvoice(context, o, i, reload), icon: const Icon(Icons.credit_card_rounded, size: 18), label: Text((i['payments'] as List? ?? []).any((p) => p['status'] == 'AwaitingConfirmation') ? 'Pay another way' : 'Pay now'))),
                  const SizedBox(width: 10),
                  OutlinedButton.icon(onPressed: () => _pdf(context, api, i), icon: const Icon(Icons.picture_as_pdf_outlined, size: 18), label: const Text('PDF')),
                ]),
              ]))),
          ],
          ListHeader('Invoices', trailing: '${invoices.length}'),
          if (invoices.isEmpty) const BigEmpty(icon: Icons.receipt_outlined, title: 'No invoices yet', message: 'Your subscription invoices appear here.')
          else ListPanel(children: [
            for (final i in invoices)
              ListRow(
                leading: Avatar(_s(i, 'number'), icon: Icons.receipt_outlined, color: toneColor(toneFor(_s(i, 'status')))),
                title: _s(i, 'number'),
                subtitle: [_s(i, 'description'), i['status'] == 'Paid' ? 'paid ${fmtDate(i['paidAt'])}' : 'due ${fmtDate(i['dueAt'])}'].where((x) => x.isNotEmpty).join('  ·  '),
                figure: moneyIn(i['total'], _s(i, 'currency')),
                status: _s(i, 'status'),
                onTap: () => showModalBottomSheet<void>(context: context, showDragHandle: true, builder: (c) => SafeArea(child: Padding(
                  padding: const EdgeInsets.fromLTRB(20, 0, 20, 16),
                  child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.stretch, children: [
                    Text(_s(i, 'number'), style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w800)),
                    Text(moneyIn(i['total'], _s(i, 'currency')), style: const TextStyle(fontSize: 24, fontWeight: FontWeight.w900)),
                    const SizedBox(height: 14),
                    OutlinedButton.icon(onPressed: () { Navigator.pop(c); _pdf(context, api, i); }, icon: const Icon(Icons.picture_as_pdf_outlined), label: const Text('Open PDF')),
                    if (i['status'] == 'Open') ...[const SizedBox(height: 10), FilledButton.icon(onPressed: () { Navigator.pop(c); payInvoice(context, o, i, reload); }, icon: const Icon(Icons.credit_card_rounded), label: const Text('Pay now'))],
                  ]),
                ))),
              ),
          ]),
        ]);
      },
    );
  }

  /// A card, Paynow or EcoCash payment started in the last 30 minutes and not decided yet.
  static Map? _pendingOf(Map i) => ((i['payments'] ?? []) as List).cast<Map>().where((p) => p['status'] == 'Pending' && ['Paynow', 'EcoCash', 'Stripe'].contains(p['method']) && (parseDate(p['createdAt'])?.isAfter(DateTime.now().subtract(const Duration(minutes: 30))) ?? false)).firstOrNull;

  Future<void> _pdf(BuildContext context, Api api, Map<String, dynamic> i) async {
    try { final b = await api.bytes('/api/v1/billing/invoices/${i['id']}/pdf'); if (context.mounted) await openBytes(context, b, '${i['number']}.pdf'); } catch (e) { if (context.mounted) toast(context, e, error: true); }
  }

}

// ---- compliance, health, activity, reports ------------------------------------------------------------------------

class ComplianceScreen extends StatelessWidget {
  const ComplianceScreen({super.key});
  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    return Loader<Json>(load: () => api.getMap('/api/v1/compliance'), builder: (context, d, _) {
      final checks = ((d['checks'] ?? []) as List).cast<Map<String, dynamic>>();
      final tone = toneFor(_s(d, 'status'));
      Tone toneOf(Map c) => c['status'] == 'ok' ? Tone.ok : c['status'] == 'critical' ? Tone.bad : c['status'] == 'warn' ? Tone.warn : Tone.info;
      IconData iconOf(Map c) => c['status'] == 'ok' ? Icons.check_circle_rounded : c['status'] == 'critical' ? Icons.error_rounded : c['status'] == 'warn' ? Icons.warning_amber_rounded : Icons.info_rounded;
      final problems = checks.where((c) => c['status'] != 'ok').toList();
      final fine = checks.where((c) => c['status'] == 'ok').toList();
      Widget panel(List<Map<String, dynamic>> list) => ListPanel(children: [
            for (final c in list)
              ListRow(
                leading: Avatar(_s(c, 'title'), icon: iconOf(c), color: toneColor(toneOf(c))),
                title: _s(c, 'title'),
                subtitle: [_s(c, 'detail'), _s(c, 'device')].where((x) => x.isNotEmpty).join('  ·  '),
                chevron: false,
              ),
          ]);
      return ListView(padding: const EdgeInsets.fromLTRB(16, 12, 16, 24), children: [
        Container(
          padding: const EdgeInsets.all(20),
          decoration: BoxDecoration(gradient: heroGradient(tone == Tone.neutral || tone == Tone.info ? null : tone), borderRadius: BorderRadius.circular(24)),
          child: Row(children: [
            SizedBox.square(
              dimension: 84,
              child: Stack(alignment: Alignment.center, children: [
                SizedBox.expand(child: CircularProgressIndicator(value: ((d['score'] ?? 0) as num) / 100, strokeWidth: 8, strokeCap: StrokeCap.round, backgroundColor: Colors.white.withValues(alpha: 0.2), color: Colors.white)),
                FittedBox(child: Text('${d['score'] ?? 0}', style: const TextStyle(color: Colors.white, fontWeight: FontWeight.w900, fontSize: 24))),
              ]),
            ),
            const SizedBox(width: 18),
            Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              const Text('Compliance score', style: TextStyle(color: Colors.white, fontWeight: FontWeight.w800, fontSize: 18)),
              const SizedBox(height: 8),
              Wrap(spacing: 6, runSpacing: 6, children: [
                HeroChip('${d['ok'] ?? 0} ok', icon: Icons.check_rounded),
                HeroChip('${d['warnings'] ?? 0} warnings', icon: Icons.warning_amber_rounded),
                HeroChip('${d['critical'] ?? 0} critical', icon: Icons.error_outline_rounded),
              ]),
            ])),
          ]),
        ),
        if (problems.isNotEmpty) ...[ListHeader('Needs attention', trailing: '${problems.length}'), panel(problems)],
        if (fine.isNotEmpty) ...[ListHeader('All good', trailing: '${fine.length}'), panel(fine)],
      ]);
    });
  }
}

class HealthScreen extends StatelessWidget {
  const HealthScreen({super.key});
  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    return Loader<List<Json>>(load: () => api.getList('/api/v1/health'), builder: (context, devs, reload) {
      if (devs.isEmpty) return const EmptyList('No devices to check yet.');
      return ListView(padding: const EdgeInsets.all(16), children: [
        for (final d in devs) Padding(padding: const EdgeInsets.only(bottom: 12), child: AppCard(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Row(children: [Expanded(child: Text(((d['name'] ?? '') as String).isNotEmpty ? d['name'] : 'Device ${d['zimraDeviceId']}', style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 16))), StatusBadge(_s(d, 'status'))]),
          const SizedBox(height: 6),
          for (final c in (d['checks'] ?? []) as List) Padding(padding: const EdgeInsets.symmetric(vertical: 4), child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [Icon(c['status'] == 'ok' ? Icons.check_circle_outline : Icons.warning_amber, size: 18, color: toneColor(toneFor(_s(c, 'status')))), const SizedBox(width: 8), Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [Text(_s(c, 'title'), style: const TextStyle(fontWeight: FontWeight.w600, fontSize: 13)), Text(_s(c, 'detail'), style: TextStyle(fontSize: 12, color: Theme.of(context).hintColor)), if (c['fixAction'] != null && context.read<AuthController>().can('receipts.submit')) TextButton(onPressed: () async { if (await guarded(context, () async { await api.post('/api/v1/health/devices/${d['deviceId']}/retry-pending'); }, done: 'Sent again.')) reload(); }, child: Text(_s(c, 'fixLabel')))]))]))
        ]))),
      ]);
    });
  }
}

class ActivityScreen extends StatelessWidget {
  const ActivityScreen({super.key});
  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    return Loader<(List<Json>, List<Json>)>(
      load: () async => (await api.getList('/api/v1/activity/devices'), await api.getList('/api/v1/activity', query: {'pageSize': 50})),
      builder: (context, d, _) {
        final (devices, calls) = d;
        return ListView(padding: const EdgeInsets.all(16), children: [
          const Text('Which systems and people are using each device', style: TextStyle(fontWeight: FontWeight.w700)),
          const SizedBox(height: 8),
          for (final x in devices) Padding(padding: const EdgeInsets.only(bottom: 8), child: AppCard(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(_j([x['name']?.toString(), 'Device ${x['zimraDeviceId']}']), style: const TextStyle(fontWeight: FontWeight.w700)),
            Text('${x['calls24h']} calls · ${x['errors24h']} errors · ${x['receipts24h']} receipts (24h) · ${x['tokenCount']} token(s)', style: TextStyle(fontSize: 12, color: Theme.of(context).hintColor)),
            if (x['lastSeenAt'] != null) Text('Last seen ${fmtDateTime(x['lastSeenAt'])}', style: const TextStyle(fontSize: 12)),
          ]))),
          const SectionTitle('Recent calls'),
          for (final c in calls) Padding(padding: const EdgeInsets.only(bottom: 6), child: AppCard(padding: const EdgeInsets.all(10), child: Row(children: [Badge2(_s(c, 'method')), const SizedBox(width: 8), Expanded(child: Text(_s(c, 'path'), style: const TextStyle(fontSize: 12, fontFamily: 'monospace'), overflow: TextOverflow.ellipsis)), const SizedBox(width: 8), Badge2('${c['statusCode']}', tone: (c['statusCode'] as int) >= 400 ? Tone.bad : Tone.ok)]))),
        ]);
      },
    );
  }
}

class SalesReportScreen extends StatefulWidget {
  const SalesReportScreen({super.key});
  @override
  State<SalesReportScreen> createState() => _SalesReportScreenState();
}

class _SalesReportScreenState extends State<SalesReportScreen> {
  DateTime _from = DateTime(DateTime.now().year, DateTime.now().month, 1), _to = DateTime.now();
  Json? _d;
  Object? _error;
  bool _loading = true;

  @override
  void initState() { super.initState(); _run(); }

  Future<void> _run() async {
    setState(() { _loading = true; _error = null; });
    try {
      final d = await context.read<Api>().getMap('/api/v1/reports/sales', query: {'from': isoDay(_from), 'to': isoDay(_to)});
      if (mounted) setState(() { _d = d; _loading = false; });
    } catch (e) { if (mounted) setState(() { _error = e; _loading = false; }); }
  }

  Future<void> _pick(bool from) async {
    final d = await showDatePicker(context: context, initialDate: from ? _from : _to, firstDate: DateTime(2000), lastDate: DateTime(2100));
    if (d != null) { setState(() => from ? _from = d : _to = d); _run(); }
  }

  @override
  Widget build(BuildContext context) {
    final d = _d;
    return ListView(padding: const EdgeInsets.all(16), children: [
      Wrap(spacing: 10, runSpacing: 10, children: [OutlinedButton(onPressed: () => _pick(true), child: Text('From ${fmtDate(_from.toIso8601String())}')), OutlinedButton(onPressed: () => _pick(false), child: Text('To ${fmtDate(_to.toIso8601String())}'))]),
      const SizedBox(height: 12),
      if (_loading) const Padding(padding: EdgeInsets.all(40), child: Center(child: CircularProgressIndicator())) else if (_error != null) ErrorState(_error!, onRetry: _run) else if (d != null) ...[
        if ((d['counts'] as Map?) != null) AppCard(child: Wrap(spacing: 14, runSpacing: 6, children: [for (final e in (d['counts'] as Map).entries) Text('${humanize(e.key.toString())}: ${e.value}', style: const TextStyle(fontSize: 12))])),
        for (final c in (d['currencies'] ?? []) as List) ...[
          SectionTitle('${c['currency']}'),
          ResponsiveGrid(minTileWidth: 150, children: [Kpi('Net sales', money(c['netSales'])), Kpi('Net tax', money(c['netTax'])), Kpi('Invoices', '${c['invoices']}', sub: money(c['invoicesTotal'])), Kpi('Credit notes', '${c['creditNotes']}', sub: money(c['creditNotesTotal']))]),
          const SizedBox(height: 10),
          AppCard(child: Column(children: [for (final t in (c['taxes'] ?? []) as List) Padding(padding: const EdgeInsets.symmetric(vertical: 3), child: Row(children: [Expanded(child: Text('${t['label']}${t['taxPercent'] != null && !'${t['label']}'.contains('%') ? ' ${t['taxPercent']}%' : ''}')), Text('${money(t['sales'])}  tax ${money(t['tax'])}', style: const TextStyle(fontSize: 12))])), for (final p in (c['payments'] ?? []) as List) Padding(padding: const EdgeInsets.symmetric(vertical: 3), child: Row(children: [Expanded(child: Text('Paid by ${p['moneyType']}')), Text(money(p['amount']), style: const TextStyle(fontSize: 12))]))])),
        ],
      ],
    ]);
  }
}

// ---- AI reports ----------------------------------------------------------------------------------------------------

class AiReportsScreen extends StatefulWidget {
  const AiReportsScreen({super.key});
  @override
  State<AiReportsScreen> createState() => _AiReportsScreenState();
}

class _AiReportsScreenState extends State<AiReportsScreen> {
  final _q = TextEditingController();
  Json? _res;
  String? _summary;
  List<String> _examples = [];
  bool _busy = false;

  @override
  void initState() { super.initState(); _meta(); }

  Future<void> _meta() async {
    try { final m = await context.read<Api>().getMap('/api/v1/ai/reports/meta'); if (mounted) setState(() => _examples = ((m['examples'] ?? []) as List).map((e) => e.toString()).toList()); } catch (_) {}
  }

  Future<void> _ask([String? q]) async {
    final question = (q ?? _q.text).trim();
    if (question.isEmpty) return;
    _q.text = question;
    setState(() => _busy = true);
    final r = await guardedValue(context, () => context.read<Api>().post('/api/v1/ai/reports/ask', body: {'question': question}));
    if (mounted) setState(() { _busy = false; if (r != null) { _res = Map<String, dynamic>.from(r['result'] ?? {}); _summary = r['summary']?.toString() ?? r['notice']?.toString(); } });
  }

  @override
  Widget build(BuildContext context) {
    final r = _res;
    final cols = ((r?['columns'] ?? []) as List).map((c) => _s(c, 'label')).toList();
    final rows = ((r?['rows'] ?? []) as List).map((x) => (x as List).map((c) => c is num ? money(c) : (c ?? '').toString()).toList()).toList();
    return ListView(padding: const EdgeInsets.all(16), children: [
      TextField(controller: _q, minLines: 1, maxLines: 3, textInputAction: TextInputAction.send, onSubmitted: (_) => _ask(), decoration: InputDecoration(labelText: 'Ask about your sales', hintText: 'Sales by day this month', suffixIcon: IconButton(icon: _busy ? const SizedBox(width: 18, height: 18, child: CircularProgressIndicator(strokeWidth: 2)) : const Icon(Icons.send), onPressed: _busy ? null : _ask))),
      if (_examples.isNotEmpty && r == null) Padding(padding: const EdgeInsets.only(top: 12), child: Wrap(spacing: 8, runSpacing: 8, children: [for (final e in _examples.take(6)) ActionChip(label: Text(e), onPressed: () => _ask(e))])),
      if (_summary != null) Padding(padding: const EdgeInsets.only(top: 12), child: AppCard(child: Text(_summary!))),
      if (r != null) ...[
        SectionTitle(_s(r, 'title')),
        if (rows.isEmpty) const AppCard(child: Text('No sales match that.')) else AppCard(padding: const EdgeInsets.symmetric(vertical: 4), child: LayoutBuilder(builder: (c, box) => SingleChildScrollView(scrollDirection: Axis.horizontal, child: ConstrainedBox(constraints: BoxConstraints(minWidth: box.maxWidth), child: DataTable(columnSpacing: 20, headingRowHeight: 38, dataRowMinHeight: 34, dataRowMaxHeight: 44, columns: [for (final c in cols) DataColumn(label: Text(c, style: const TextStyle(fontSize: 12, fontWeight: FontWeight.w700)))], rows: [for (final x in rows) DataRow(cells: [for (final c in x) DataCell(Text(c, style: const TextStyle(fontSize: 13)))])]))))),
      ],
    ]);
  }
}

// ---- offline files across devices ----------------------------------------------------------------------------------

class OfflineScreen extends StatelessWidget {
  const OfflineScreen({super.key});
  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    return Loader<List<(Json, List<Json>)>>(
      load: () async {
        final devs = await loadDevices(api);
        final out = <(Json, List<Json>)>[];
        for (final d in devs) { try { out.add((d, await api.getList('/api/v1/devices/${d['id']}/offline-files'))); } catch (_) { out.add((d, [])); } }
        return out;
      },
      builder: (context, all, reload) => all.isEmpty ? const EmptyList('No devices yet.') : ListView(padding: const EdgeInsets.all(16), children: [
        for (final (d, files) in all) ...[
          Row(children: [Expanded(child: SectionTitle(deviceLabel(d))), if (context.read<AuthController>().can('offline.manage')) TextButton.icon(onPressed: () async { if (await guarded(context, () async { await api.post('/api/v1/devices/${d['id']}/offline-files/sync'); }, done: 'Sent to ZIMRA.')) reload(); }, icon: const Icon(Icons.cloud_sync_outlined, size: 18), label: const Text('Send now'))]),
          if (files.isEmpty) const AppCard(child: Text('No offline files.')),
          for (final f in files) Padding(padding: const EdgeInsets.only(bottom: 8), child: AppCard(padding: const EdgeInsets.all(12), child: Row(children: [Expanded(child: Text('Day ${f['fiscalDayNo']} · file ${f['sequence']} · ${f['receiptCount']} receipts')), StatusBadge(_s(f, 'status'))]))),
        ],
      ]),
    );
  }
}

// ---- operator analytics --------------------------------------------------------------------------------------------

class AnalyticsScreen extends StatefulWidget {
  const AnalyticsScreen({super.key});
  @override
  State<AnalyticsScreen> createState() => _AnalyticsScreenState();
}

class _AnalyticsScreenState extends State<AnalyticsScreen> {
  int _days = 30;

  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    return Loader<Json>(
      key: ValueKey(_days),
      load: () => api.getMap('/api/v1/analytics/usage', query: {'days': _days}),
      builder: (context, d, _) {
        final t = (d['totals'] ?? {}) as Map;
        final companies = ((d['companies'] ?? []) as List).cast<Map<String, dynamic>>();
        return ListView(padding: const EdgeInsets.all(16), children: [
          SegmentedButton<int>(segments: const [ButtonSegment(value: 7, label: Text('7 days')), ButtonSegment(value: 30, label: Text('30 days')), ButtonSegment(value: 90, label: Text('90 days'))], selected: {_days}, onSelectionChanged: (s) => setState(() => _days = s.first)),
          const SizedBox(height: 12),
          ResponsiveGrid(minTileWidth: 150, children: [Kpi('Companies', '${t['activeCompanies']} / ${t['companies']}', sub: '${t['onTrial']} trial · ${t['locked']} locked'), Kpi('At risk', '${t['atRisk']}', color: (t['atRisk'] ?? 0) > 0 ? const Color(0xFFD97706) : null), Kpi('Receipts', '${t['receipts']}', sub: 'before: ${t['receiptsPrevious']}'), Kpi('Rejected', '${t['rejectedPercent']}%', sub: '${t['rejected']} receipts')]),
          const SectionTitle('Companies'),
          for (final c in companies) Padding(padding: const EdgeInsets.only(bottom: 10), child: AppCard(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Row(children: [Expanded(child: Text(_s(c, 'name'), style: const TextStyle(fontWeight: FontWeight.w800))), Badge2(_s(c, 'risk'), tone: toneFor(_s(c, 'risk')))]),
            Text('${_s(c, 'plan').isEmpty ? 'No plan' : c['plan']} · ${c['devices']} devices · ${c['receipts']} receipts (before ${c['receiptsPrevious']})', style: TextStyle(fontSize: 12, color: Theme.of(context).hintColor)),
            for (final why in (c['reasons'] ?? []) as List) Padding(padding: const EdgeInsets.only(top: 3), child: Text('• $why', style: const TextStyle(fontSize: 12))),
          ]))),
        ]);
      },
    );
  }
}

// ---- partner portal ------------------------------------------------------------------------------------------------

String _amounts(dynamic list) => ((list ?? []) as List).isEmpty ? '0.00' : ((list as List).map((o) => moneyIn(o['amount'], o['currency'])).join(' + '));

class PartnerOverviewScreen extends StatelessWidget {
  const PartnerOverviewScreen({super.key});
  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    final base = context.read<AppConfig>().baseUrl;
    return Loader<(Json, List<Json>)>(
      load: () async => (await api.getMap('/api/v1/partner/summary'), await api.getList('/api/v1/partner/referrals')),
      builder: (context, d, _) {
        final (s, refs) = d;
        final link = '$base/app${s['referralPath']}';
        return ListView(padding: const EdgeInsets.all(16), children: [
          Text(_s(s, 'name'), style: const TextStyle(fontSize: 22, fontWeight: FontWeight.w800)),
          const SizedBox(height: 12),
          ResponsiveGrid(minTileWidth: 150, children: [Kpi('Companies', '${s['clients']}', sub: '${s['activeClients']} active'), Kpi('Waiting', '${s['pendingReferrals']}', sub: 'registered with your link'), Kpi('Earned', _amounts(s['earned']), sub: '${s['commissionPercent']}% of paid invoices'), Kpi('Owed to you', _amounts(s['owed']), color: const Color(0xFFD97706), sub: 'paid ${_amounts(s['paid'])}')]),
          const SectionTitle('Your referral link'),
          AppCard(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            SelectableText(link, style: const TextStyle(fontFamily: 'monospace', fontSize: 13)),
            const SizedBox(height: 10),
            Wrap(spacing: 8, children: [FilledButton.icon(onPressed: () => shareText(context, 'Register your company on FDMS Gateway: $link'), icon: const Icon(Icons.share), label: const Text('Share link')), Text('Code ${s['code']}', style: const TextStyle(fontWeight: FontWeight.w700))]),
          ])),
          const SectionTitle('Registrations from your link'),
          if (refs.isEmpty) const AppCard(child: Text('No registrations yet.')),
          for (final r in refs) Padding(padding: const EdgeInsets.only(bottom: 8), child: AppCard(padding: const EdgeInsets.all(12), child: Row(children: [Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [Text(_s(r, 'companyName'), style: const TextStyle(fontWeight: FontWeight.w700)), Text('${_s(r, 'contactName')} · ${fmtDate(r['createdAt'])}', style: TextStyle(fontSize: 12, color: Theme.of(context).hintColor))])), StatusBadge(_s(r, 'status'))]))),
        ]);
      },
    );
  }
}

final partnerClientsSpec = ResourceSpec(
  title: 'Our companies', listPath: '/api/v1/partner/clients', itemTitle: (j) => _s(j, 'name'), itemSubtitle: (j) => _j([_s(j, 'plan'), '${j['devices']} devices', '${j['receipts30Days']} receipts (30 days)']), badges: (j) => [(_s(j, 'status'), toneFor(_s(j, 'status')))],
  leadingIcon: Icons.apartment, emptyText: 'No companies yet. Share your referral link from Overview.', trailing: (j) => _amounts(j['earned']),
  facts: (j) => [('Status', _s(j, 'status')), ('Plan', _s(j, 'plan')), ('With you since', fmtDate(j['since'])), ('Your rate', '${j['percent']}%'), ('Devices', _s(j, 'devices')), ('Receipts, 30 days', _s(j, 'receipts30Days')), ('Last receipt', fmtDate(j['lastReceiptAt'])), ('Commission earned', _amounts(j['earned']))],
);

class PartnerCommissionScreen extends StatelessWidget {
  const PartnerCommissionScreen({super.key});
  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    return Loader<Json>(load: () => api.getMap('/api/v1/partner/statement'), builder: (context, s, _) {
      final lines = ((s['lines'] ?? []) as List).cast<Map<String, dynamic>>();
      final payouts = ((s['payouts'] ?? []) as List).cast<Map<String, dynamic>>();
      return ListView(padding: const EdgeInsets.all(16), children: [
        ResponsiveGrid(minTileWidth: 150, children: [Kpi('Earned', _amounts(s['earned'])), Kpi('Paid out', _amounts(s['paid'])), Kpi('Owed to you', _amounts(s['owed']), color: const Color(0xFFD97706))]),
        const SectionTitle('Invoices paid'),
        if (lines.isEmpty) const AppCard(child: Text('Nothing earned yet. Commission appears when a company pays its invoice.')),
        for (final l in lines) Padding(padding: const EdgeInsets.only(bottom: 8), child: AppCard(padding: const EdgeInsets.all(12), child: Row(children: [Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [Text(_s(l, 'company'), style: const TextStyle(fontWeight: FontWeight.w700)), Text('${_s(l, 'invoice')} · ${fmtDate(l['date'])} · ${l['percent']}% of ${money(l['invoiceAmount'])}', style: TextStyle(fontSize: 12, color: Theme.of(context).hintColor))])), Amount(moneyIn(l['commission'], _s(l, 'currency')), style: const TextStyle(fontWeight: FontWeight.w800))]))),
        const SectionTitle('Paid to you'),
        if (payouts.isEmpty) const AppCard(child: Text('No payouts yet.')),
        for (final p in payouts) Padding(padding: const EdgeInsets.only(bottom: 8), child: AppCard(padding: const EdgeInsets.all(12), child: Row(children: [Expanded(child: Text('${fmtDate(p['date'])} · ${_s(p, 'reference').isEmpty ? 'no reference' : p['reference']}')), Amount(moneyIn(p['amount'], _s(p, 'currency')), style: const TextStyle(fontWeight: FontWeight.w700))]))),
      ]);
    });
  }
}

/// Shown only for a menu entry the gateway added after this version of the app was built.
class UnknownModuleScreen extends StatelessWidget {
  const UnknownModuleScreen(this.title, {super.key});
  final String title;
  @override
  Widget build(BuildContext context) => EmptyState(' arrives in the next app update. Update the app to use it.', icon: Icons.system_update_alt);
}
