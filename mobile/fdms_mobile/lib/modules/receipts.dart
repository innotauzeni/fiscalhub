import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/api.dart';
import '../core/auth.dart';
import '../core/models.dart';
import '../screens/profile.dart' show guardedValue;
import '../ui/files.dart';
import '../ui/kit.dart';
import '../ui/lists.dart';
import '../ui/premium.dart';
import 'devices.dart';
import 'lines.dart';

const _statuses = ['', 'Submitted', 'Pending', 'Held', 'Rejected', 'Cancelled', 'OfflineIssued'];

class ReceiptsScreen extends StatefulWidget {
  const ReceiptsScreen({super.key});
  @override
  State<ReceiptsScreen> createState() => _ReceiptsScreenState();
}

class _ReceiptsScreenState extends State<ReceiptsScreen> {
  final _items = <Json>[];
  final _search = TextEditingController();
  final _scroll = ScrollController();
  String _status = '';
  int _page = 1, _total = 0;
  bool _loading = true, _more = false;
  Object? _error;

  @override
  void initState() {
    super.initState();
    _scroll.addListener(() { if (_scroll.position.pixels > _scroll.position.maxScrollExtent - 300 && !_more && _items.length < _total) _load(more: true); });
    _load();
  }

  Future<void> _load({bool more = false}) async {
    final api = context.read<Api>();
    if (more) { setState(() => _more = true); _page++; } else { setState(() { _loading = _items.isEmpty; _page = 1; _error = null; }); }
    try {
      final r = await api.getMap('/api/v1/receipts', query: {'status': _status, 'search': _search.text.trim(), 'page': _page, 'pageSize': 25});
      final list = ((r['items'] ?? []) as List).map((e) => Map<String, dynamic>.from(e)).toList();
      if (!mounted) return;
      setState(() { if (!more) _items.clear(); _items.addAll(list); _total = (r['total'] ?? 0) as int; _loading = false; _more = false; });
    } catch (e) {
      if (mounted) setState(() { _error = e; _loading = false; _more = false; if (more) _page--; });
    }
  }

  @override
  Widget build(BuildContext context) {
    final auth = context.watch<AuthController>();
    return Scaffold(
      backgroundColor: Colors.transparent,
      floatingActionButton: auth.can('receipts.submit') ? FloatingActionButton.extended(onPressed: () async { final r = await Navigator.push<bool>(context, MaterialPageRoute(builder: (_) => const NewInvoiceScreen())); if (r == true) _load(); }, icon: const Icon(Icons.add), label: const Text('New invoice')) : null,
      body: Column(children: [
        Padding(padding: const EdgeInsets.fromLTRB(16, 12, 16, 10), child: SearchBox(controller: _search, hint: 'Search invoice number or reference', onChanged: (_) => setState(() {}), onSubmitted: (_) => _load())),
        Padding(
          padding: const EdgeInsets.only(left: 16),
          child: FilterPills([
            for (final s in _statuses)
              PillOption(s.isEmpty ? 'All' : statusText(s), _status == s, () { setState(() => _status = s); _load(); }, color: s.isEmpty ? null : toneColor(toneFor(s)), count: s == _status && !_loading ? _total : null),
          ]),
        ),
        Expanded(child: _loading ? const Center(child: CircularProgressIndicator()) : _error != null && _items.isEmpty ? ErrorState(_error!, onRetry: _load) : RefreshIndicator(
          onRefresh: _load,
          child: _items.isEmpty
              ? ListView(children: [BigEmpty(icon: Icons.receipt_long_outlined, title: _status.isEmpty && _search.text.isEmpty ? 'No receipts yet' : 'Nothing matches', message: _status.isEmpty && _search.text.isEmpty ? 'Invoices you issue, from here or from your systems, appear here.' : 'Try another status or search.')])
              : ListView(controller: _scroll, padding: const EdgeInsets.fromLTRB(16, 4, 16, 96), children: [
                  for (final g in _groups()) ...[
                    ListHeader(g.$1, trailing: g.$3),
                    ListPanel(children: [
                      for (final r in g.$2)
                        ListRow(
                          leading: Avatar(r['invoiceNo']?.toString() ?? '', color: toneColor(toneFor(r['status']?.toString())), icon: r['type'] == 'CreditNote' ? Icons.undo_rounded : r['type'] == 'DebitNote' ? Icons.add_card_rounded : Icons.receipt_long_rounded),
                          title: (r['invoiceNo'] ?? '').toString(),
                          subtitle: [_time(r['receiptDate']), if (r['fiscalDayNo'] != null) 'day ${r['fiscalDayNo']}', if ((r['buyerName'] ?? '').toString().isNotEmpty) r['buyerName'].toString()].join('  ·  '),
                          pills: [if (r['type'] != null && r['type'] != 'FiscalInvoice') (humanize(r['type'].toString()), Tone.info)],
                          figure: moneyIn(r['total'], r['currency']),
                          status: r['status']?.toString(),
                          onTap: () async { await Navigator.push(context, MaterialPageRoute(builder: (_) => ReceiptDetailScreen(receiptId: r['id']))); _load(); },
                        ),
                    ]),
                  ],
                  if (_more) const Padding(padding: EdgeInsets.all(16), child: Center(child: CircularProgressIndicator())),
                  if (!_more && _items.length < _total) Center(child: TextButton(onPressed: () => _load(more: true), child: Text('Load more (${_total - _items.length} left)'))),
                ]),
        )),
      ]),
    );
  }

  static String _time(dynamic v) { final d = parseDate(v); return d == null ? '' : '${d.hour.toString().padLeft(2, '0')}:${d.minute.toString().padLeft(2, '0')}'; }

  /// The loaded receipts by day, newest first, with each day's total per currency (credit notes subtract).
  List<(String, List<Json>, String)> _groups() {
    final out = <(String, List<Json>, String)>[];
    String? key;
    for (final r in _items) {
      final k = dayHeading(parseDate(r['receiptDate']));
      if (k != key) { out.add((k, <Json>[], '')); key = k; }
      out.last.$2.add(r);
    }
    return [
      for (final g in out)
        (g.$1, g.$2, () {
          final sums = <String, num>{};
          for (final r in g.$2) {
            if (r['status'] == 'Cancelled' || r['status'] == 'Rejected') continue;
            final sign = r['type'] == 'CreditNote' ? -1 : 1;
            sums[(r['currency'] ?? '').toString()] = (sums[(r['currency'] ?? '').toString()] ?? 0) + sign * ((r['total'] ?? 0) as num).abs();
          }
          return sums.entries.map((e) => moneyIn(e.value, e.key)).join(' + ');
        }()),
    ];
  }
}

class ReceiptDetailScreen extends StatefulWidget {
  const ReceiptDetailScreen({super.key, required this.receiptId});
  final String receiptId;
  @override
  State<ReceiptDetailScreen> createState() => _ReceiptDetailScreenState();
}

class _ReceiptDetailScreenState extends State<ReceiptDetailScreen> {
  final _key = GlobalKey<LoaderState<Json>>();
  Uint8List? _qr;
  bool _busy = false;

  Api get api => context.read<Api>();

  Future<Json> _load() async {
    final d = await api.getMap('/api/v1/receipts/${widget.receiptId}/detail');
    try { final b = await api.bytes('/api/v1/receipts/${widget.receiptId}/qr'); if (mounted) setState(() => _qr = b); } catch (_) {}
    return d;
  }

  Future<void> _act(String verb, String done) async {
    setState(() => _busy = true);
    final ok = await guarded(context, () async { await api.post('/api/v1/receipts/${widget.receiptId}/$verb'); }, done: done);
    if (mounted) setState(() => _busy = false);
    if (ok) _key.currentState?.reload();
  }

  Future<void> _pdf({bool share = false}) async {
    final name = 'invoice-${widget.receiptId.substring(0, 8)}.pdf';
    try {
      final b = await api.bytes('/api/v1/receipts/${widget.receiptId}/pdf', query: {'format': 'a4'});
      if (!mounted) return;
      share ? await shareBytes(context, b, name) : await openBytes(context, b, name);
    } catch (e) { if (mounted) toast(context, e, error: true); }
  }

  Future<void> _link() async {
    final r = await guardedValue(context, () => api.post('/api/v1/receipts/${widget.receiptId}/share'));
    if (r != null && mounted) await shareText(context, (r['whatsAppText'] ?? r['url'] ?? '').toString());
  }

  Future<void> _email(String suggest) async {
    final to = await promptText(context, 'Email this invoice to', hint: 'customer@example.com', initial: suggest, ok: 'Send', keyboard: TextInputType.emailAddress);
    if (to == null || !mounted) return;
    final r = await guardedValue(context, () => api.post('/api/v1/receipts/${widget.receiptId}/email', body: {'email': to.trim().isEmpty ? null : to.trim()}));
    if (r != null && mounted) toast(context, (r['message'] ?? 'Sent.').toString());
  }

  Future<void> _credit(Json r) async {
    final reason = await promptText(context, 'Credit note: why is this invoice being reversed?', hint: 'Goods returned', ok: 'Issue credit note');
    if (reason == null || reason.trim().isEmpty || !mounted) return;
    final ok = await guarded(context, () async { await api.post('/api/v1/receipts/${widget.receiptId}/correct', body: {'reason': reason.trim(), 'fixes': [], 'issueReplacement': false, 'replacementInvoiceNo': null}); }, done: 'Credit note issued.');
    if (ok) _key.currentState?.reload();
  }

  Future<void> _debit(Json d) async {
    final r = d['receipt'] as Map;
    final res = await Navigator.push<bool>(context, MaterialPageRoute(builder: (_) => _DebitNoteScreen(receiptId: widget.receiptId, invoiceNo: r['invoiceNo'] ?? '', currency: r['currency'] ?? 'USD')));
    if (res == true) _key.currentState?.reload();
  }

  @override
  Widget build(BuildContext context) {
    final auth = context.watch<AuthController>();
    return Loader<Json>(
      key: _key,
      load: _load,
      frame: (w) => Scaffold(appBar: AppBar(title: const Text('Receipt')), body: w),
      builder: (context, d, reload) {
        final r = Map<String, dynamic>.from(d['receipt'] as Map);
        final lines = ((d['lines'] ?? []) as List).cast<Map<String, dynamic>>();
        final pays = ((d['payments'] ?? []) as List).cast<Map<String, dynamic>>();
        final errs = ((r['validationErrors'] ?? []) as List).cast<Map<String, dynamic>>();
        final buyer = d['buyer'] as Map?;
        final status = r['status']?.toString();
        final canManage = auth.can('receipts.submit');
        final hint = Theme.of(context).hintColor;
        final type = humanize(r['type'] ?? '');
        final manage = <QuickAction>[
          if (status == 'Held') QuickAction(Icons.send_rounded, 'Release now', _busy ? null : () => _act('release', 'Released to ZIMRA.'), primary: true),
          if (status == 'Pending' || status == 'Rejected') QuickAction(Icons.refresh_rounded, 'Send again', _busy ? null : () => _act('retry', 'Sent again.'), primary: true),
          if (status == 'Submitted' && r['type'] == 'FiscalInvoice' && d['creditNoteId'] == null) QuickAction(Icons.undo_rounded, 'Credit note', _busy ? null : () => _credit(r)),
          if (status == 'Submitted' && r['type'] == 'FiscalInvoice') QuickAction(Icons.add_card_rounded, 'Debit note', _busy ? null : () => _debit(d)),
          if (status == 'Held' || status == 'Pending') QuickAction(Icons.block_rounded, 'Cancel', _busy ? null : () async { if (await confirm(context, 'Cancel this receipt? It will not be sent to ZIMRA.', ok: 'Cancel receipt', danger: true)) _act('cancel', 'Cancelled.'); }, danger: true),
        ];
        return DetailScaffold(
          barTitle: type,
          title: (r['invoiceNo'] ?? '').toString(),
          subtitle: '${fmtDateTime(r['receiptDate'])}${r['fiscalDayNo'] != null ? '  ·  fiscal day ${r['fiscalDayNo']}' : ''}',
          icon: r['type'] == 'CreditNote' ? Icons.undo_rounded : r['type'] == 'DebitNote' ? Icons.add_card_rounded : Icons.receipt_long_rounded,
          tone: switch (toneFor(status)) { Tone.bad => Tone.bad, Tone.warn => Tone.warn, Tone.ok => Tone.ok, _ => null },
          figureLabel: 'Total',
          figure: moneyIn(r['total'], r['currency']),
          chips: [HeroChip(statusText(status), icon: Icons.circle), if (r['type'] != 'FiscalInvoice') HeroChip(type), if (r['holdUntil'] != null) HeroChip('Held until ${fmtDateTime(r['holdUntil'])}', icon: Icons.schedule_rounded)],
          actions: [
            QuickAction(Icons.picture_as_pdf_rounded, 'Open PDF', _pdf, primary: true),
            QuickAction(Icons.ios_share_rounded, 'Share PDF', () => _pdf(share: true)),
            QuickAction(Icons.link_rounded, 'Share link', _link),
            QuickAction(Icons.mail_outline_rounded, 'Email', () => _email(buyer?['buyerEmail']?.toString() ?? '')),
          ],
          children: [
            if (r['errorMessage'] != null)
              Container(
                margin: const EdgeInsets.only(bottom: 14),
                padding: const EdgeInsets.all(14),
                decoration: BoxDecoration(color: const Color(0xFFDC2626).withValues(alpha: 0.08), borderRadius: BorderRadius.circular(16), border: Border.all(color: const Color(0xFFDC2626).withValues(alpha: 0.25))),
                child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  const Icon(Icons.error_outline_rounded, color: Color(0xFFDC2626)),
                  const SizedBox(width: 10),
                  Expanded(child: Text('${r['errorCode'] ?? 'Error'}: ${r['errorMessage']}', style: const TextStyle(color: Color(0xFFB91C1C), fontWeight: FontWeight.w600))),
                ]),
              ),
            if (canManage && manage.isNotEmpty) InfoCard(title: 'Manage', icon: Icons.tune_rounded, padding: const EdgeInsets.fromLTRB(8, 14, 8, 8), children: [
              Padding(padding: const EdgeInsets.symmetric(horizontal: 8), child: Wrap(spacing: 8, runSpacing: 8, children: [
                for (final a in manage) a.primary
                    ? FilledButton.icon(onPressed: a.onTap, icon: Icon(a.icon, size: 18), label: Text(a.label))
                    : OutlinedButton.icon(onPressed: a.onTap, style: a.danger ? OutlinedButton.styleFrom(foregroundColor: const Color(0xFFDC2626)) : null, icon: Icon(a.icon, size: 18), label: Text(a.label)),
              ])),
              if (d['creditNoteId'] != null) Padding(padding: const EdgeInsets.fromLTRB(8, 10, 8, 0), child: Text('This invoice already has a credit note.', style: TextStyle(fontSize: 12, color: hint))),
            ]),
            InfoCard(title: 'Items', icon: Icons.shopping_bag_outlined, children: [
              for (var i = 0; i < lines.length; i++)
                Container(
                  padding: const EdgeInsets.symmetric(vertical: 10),
                  decoration: BoxDecoration(border: i == lines.length - 1 ? null : Border(bottom: BorderSide(color: Theme.of(context).dividerColor.withValues(alpha: 0.6)))),
                  child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    Container(
                      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                      decoration: BoxDecoration(color: kBrand.withValues(alpha: 0.08), borderRadius: BorderRadius.circular(8)),
                      child: Text('${lines[i]['receiptLineQuantity']}×', style: const TextStyle(color: kBrand, fontWeight: FontWeight.w800, fontSize: 12)),
                    ),
                    const SizedBox(width: 12),
                    Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                      Text((lines[i]['receiptLineName'] ?? '').toString(), style: const TextStyle(fontWeight: FontWeight.w700)),
                      const SizedBox(height: 2),
                      Text([
                        '@ ${money(lines[i]['receiptLinePrice'])}',
                        if (lines[i]['taxPercent'] != null) '${lines[i]['taxPercent']}% VAT' else 'Exempt',
                        if ((lines[i]['receiptLineHSCode'] ?? '').toString().isNotEmpty) 'HS ${lines[i]['receiptLineHSCode']}',
                      ].join('  ·  '), style: TextStyle(fontSize: 12, color: hint)),
                    ])),
                    const SizedBox(width: 8),
                    Side(fraction: 0.3, children: [AmountBox(money(lines[i]['receiptLineTotal']), style: const TextStyle(fontWeight: FontWeight.w800))]),
                  ]),
                ),
              Container(
                margin: const EdgeInsets.only(top: 6, bottom: 6),
                padding: const EdgeInsets.all(12),
                decoration: BoxDecoration(color: kBrand.withValues(alpha: 0.06), borderRadius: BorderRadius.circular(12)),
                child: Column(children: [
                  for (final p in pays) Padding(padding: const EdgeInsets.only(bottom: 4), child: Row(children: [Icon(Icons.payments_outlined, size: 16, color: hint), const SizedBox(width: 8), Expanded(child: Text('Paid by ${humanize(p['moneyTypeCode']?.toString() ?? '')}', style: TextStyle(color: hint))), Text(money(p['paymentAmount']), style: const TextStyle(fontWeight: FontWeight.w600))])),
                  Row(children: [const Expanded(child: Text('Total', style: TextStyle(fontWeight: FontWeight.w800, fontSize: 15))), Text(moneyIn(r['total'], r['currency']), style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 16, color: kBrand))]),
                ]),
              ),
            ]),
            if (buyer != null) InfoCard(title: 'Buyer', icon: Icons.person_outline_rounded, children: infoRows([
              ('Name', buyer['buyerRegisterName']?.toString()), ('Trade name', buyer['buyerTradeName']?.toString()), ('TIN', (buyer['buyerTIN'] ?? buyer['buyerTin'])?.toString()),
              ('VAT number', buyer['vatNumber']?.toString()), ('Email', buyer['buyerEmail']?.toString()), ('Phone', buyer['buyerPhone']?.toString()),
            ])),
            InfoCard(title: 'Fiscal details', icon: Icons.verified_outlined, children: infoRows([
              ('Type', type), ('Fiscal day', r['fiscalDayNo'] == null ? null : 'Day ${r['fiscalDayNo']}  ·  counter ${r['receiptCounter']}'), ('Global number', r['receiptGlobalNo']?.toString()),
              ('ZIMRA receipt ID', r['fdmsReceiptId']?.toString()), ('Your reference', r['externalRef']?.toString()), ('Currency', r['currency']?.toString()),
            ])),
            if (errs.isNotEmpty) InfoCard(title: 'ZIMRA validation', icon: Icons.rule_rounded, children: [
              Padding(padding: const EdgeInsets.only(bottom: 8), child: Wrap(spacing: 6, runSpacing: 6, children: [for (final e in errs) Badge2(e['code']?.toString() ?? '', tone: (e['color'] ?? '').toString().toLowerCase() == 'red' ? Tone.bad : Tone.warn)])),
            ]),
            if (_qr != null) InfoCard(title: 'Verification QR', icon: Icons.qr_code_2_rounded, children: [
              Center(child: Container(margin: const EdgeInsets.only(bottom: 8), padding: const EdgeInsets.all(10), decoration: BoxDecoration(color: Colors.white, borderRadius: BorderRadius.circular(14), border: Border.all(color: Theme.of(context).dividerColor)), child: Image.memory(_qr!, width: 180, height: 180))),
              Padding(padding: const EdgeInsets.only(bottom: 8), child: Text('Customers scan this to check the invoice with ZIMRA.', textAlign: TextAlign.center, style: TextStyle(fontSize: 12, color: hint))),
            ]),
          ],
        );
      },
    );
  }
}

class _DebitNoteScreen extends StatefulWidget {
  const _DebitNoteScreen({required this.receiptId, required this.invoiceNo, required this.currency});
  final String receiptId, invoiceNo, currency;
  @override
  State<_DebitNoteScreen> createState() => _DebitNoteScreenState();
}

class _DebitNoteScreenState extends State<_DebitNoteScreen> {
  final _reason = TextEditingController();
  final _lines = [LineData(taxKey: 'pct:15')];
  final _taxes = TaxOption.standard();
  String _pay = 'Cash';
  bool _busy = false;

  Future<void> _submit() async {
    if (_reason.text.trim().isEmpty || _lines.any((l) => l.name.trim().isEmpty || l.price <= 0 || l.hsBad)) { toast(context, 'Give the reason and complete every item (name and price).', error: true); return; }
    setState(() => _busy = true);
    final ok = await guarded(context, () async {
      await context.read<Api>().post('/api/v1/receipts/${widget.receiptId}/debit-note', body: {
        'reason': _reason.text.trim(), 'moneyType': _pay, 'invoiceNo': null,
        'lines': [for (final l in _lines) {'name': l.name.trim(), 'hsCode': l.hs.trim().isEmpty ? null : l.hs.trim(), 'quantity': l.qty, 'unitPrice': l.price, ..._taxFor(l)}],
      });
    }, done: 'Debit note issued.');
    if (mounted) { setState(() => _busy = false); if (ok) Navigator.pop(context, true); }
  }

  Map<String, dynamic> _taxFor(LineData l) {
    final t = _taxes.where((x) => x.key == l.taxKey).firstOrNull;
    return t?.exempt == true ? {'isExempt': true} : {'taxPercent': t?.percent ?? 15};
  }

  @override
  Widget build(BuildContext context) => Scaffold(
        appBar: AppBar(title: Text('Debit note for ${widget.invoiceNo}')),
        body: ListView(padding: const EdgeInsets.all(16), children: [
          const Text('A debit note adds charges to an invoice that is already fiscalised.'),
          const SizedBox(height: 12),
          TextField(controller: _reason, decoration: const InputDecoration(labelText: 'Reason')),
          const SizedBox(height: 12),
          DropdownButtonFormField<String>(isExpanded: true, initialValue: _pay, decoration: const InputDecoration(labelText: 'Paid by'), items: [for (final m in _payMethods) DropdownMenuItem(value: m, child: Text(m))], onChanged: (v) => setState(() => _pay = v ?? 'Cash')),
          const SectionTitle('Items to add'),
          LinesEditor(lines: _lines, taxes: _taxes, currency: widget.currency, onChanged: () => setState(() {})),
          const SizedBox(height: 12),
          FilledButton(onPressed: _busy ? null : _submit, child: const Text('Issue debit note')),
        ]),
      );
}

const _payMethods = ['Cash', 'Card', 'MobileWallet', 'BankTransfer', 'Coupon', 'Credit', 'Other'];

/// Issues a fiscal invoice from the phone: pick the device, add the items, preview what ZIMRA will see, and submit.
class NewInvoiceScreen extends StatefulWidget {
  const NewInvoiceScreen({super.key, this.embedded = false});
  /// Shown inside the app frame (Quick sale) instead of as its own page.
  final bool embedded;
  @override
  State<NewInvoiceScreen> createState() => _NewInvoiceScreenState();
}

class _NewInvoiceScreenState extends State<NewInvoiceScreen> {
  List<Json> _devices = [];
  List<String> _currencies = ['USD'];
  String? _deviceId;
  final _invoiceNo = TextEditingController(), _buyer = TextEditingController(), _tin = TextEditingController();
  String _currency = 'USD', _pay = 'Cash';
  bool _inclusive = true, _busy = false, _ready = false;
  final _lines = [LineData()];
  Json? _preview;
  String _ref = _newRef();
  Object? _error;

  static String _newRef() => 'app-${DateTime.now().millisecondsSinceEpoch.toRadixString(36)}';

  @override
  void initState() { super.initState(); _init(); }

  Future<void> _init() async {
    final api = context.read<Api>();
    try {
      final devs = (await loadDevices(api)).where((d) => d['isActive'] != false).toList();
      var cur = <String>['USD'];
      try { final c = await api.getList('/api/v1/currencies'); if (c.isNotEmpty) cur = c.map((e) => e['code'].toString()).toList(); } catch (_) {}
      if (!mounted) return;
      setState(() {
        _devices = devs; _currencies = cur; _currency = cur.contains('USD') ? 'USD' : cur.first;
        _deviceId = devs.length == 1 ? devs.first['id'] : null; _ready = true;
        _fixTaxes();
      });
    } catch (e) { if (mounted) setState(() { _error = e; _ready = true; }); }
  }

  Json? get _device => _devices.where((d) => d['id'] == _deviceId).firstOrNull;
  List<TaxOption> get _taxes => TaxOption.fromDevice((_device?['taxes'] ?? []) as List);

  void _fixTaxes() {
    final t = _taxes;
    for (final l in _lines) { if (!t.any((x) => x.key == l.taxKey)) l.taxKey = (t.where((x) => !x.exempt && (x.percent ?? 0) > 0).firstOrNull ?? t.firstOrNull)?.key; }
  }

  String? _problem() {
    if (_deviceId == null) return 'Choose the device to issue from.';
    if (_invoiceNo.text.trim().isEmpty) return 'Give the invoice a number.';
    if (_lines.any((l) => l.name.trim().isEmpty)) return 'Every item needs a name.';
    if (_lines.any((l) => l.qty <= 0 || l.price <= 0)) return 'Every item needs a quantity and a price above zero.';
    if (_lines.any((l) => l.hsBad)) return 'An HS code must be 4, 6 or 8 digits.';
    if (_buyer.text.trim().isNotEmpty != _tin.text.trim().isNotEmpty) return 'Enter both the buyer name and TIN, or neither.';
    return null;
  }

  Json _body() => {
        'externalRef': _ref, 'invoiceNo': _invoiceNo.text.trim(), 'receiptType': 'FiscalInvoice', 'currency': _currency, 'linesTaxInclusive': _inclusive, 'autoOpenDay': true, 'holdMinutes': 0,
        'lines': [for (final l in _lines) l.toReceipt(_taxes)],
        if (_buyer.text.trim().isNotEmpty && _tin.text.trim().isNotEmpty) 'buyer': {'registerName': _buyer.text.trim(), 'tin': _tin.text.trim()},
      };

  Future<void> _doPreview() async {
    final p = _problem();
    if (p != null) { toast(context, p, error: true); return; }
    setState(() => _busy = true);
    final r = await guardedValue(context, () => context.read<Api>().post('/api/v1/devices/$_deviceId/receipts/preview', body: _body()));
    if (mounted) setState(() { _busy = false; _preview = r; });
  }

  Future<void> _submit() async {
    final p = _problem();
    if (p != null) { toast(context, p, error: true); return; }
    final api = context.read<Api>();
    setState(() => _busy = true);
    Json? done;
    final ok = await guarded(context, () async {
      final pv = await api.post('/api/v1/devices/$_deviceId/receipts/preview', body: _body()) as Map;
      final body = _body()..['payments'] = [{'moneyType': _pay, 'amount': (pv['total'] as num).abs()}];
      done = Map<String, dynamic>.from(await api.post('/api/v1/devices/$_deviceId/receipts', body: body) as Map);
    });
    if (!mounted) return;
    setState(() => _busy = false);
    if (ok && done != null) {
      toast(context, 'Receipt ${done!['status']}.');
      final id = done!['id'];
      if (widget.embedded) {
        setState(() { _lines..clear()..add(LineData(taxKey: _taxes.firstOrNull?.key)); _invoiceNo.clear(); _buyer.clear(); _tin.clear(); _preview = null; _ref = _newRef(); });
        await Navigator.push(context, MaterialPageRoute(builder: (_) => ReceiptDetailScreen(receiptId: id)));
      } else {
        await Navigator.pushReplacement(context, MaterialPageRoute(builder: (_) => ReceiptDetailScreen(receiptId: id)));
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final pv = _preview;
    return Scaffold(
      appBar: widget.embedded ? null : AppBar(title: const Text('New invoice')),
      body: !_ready ? const Center(child: CircularProgressIndicator()) : _error != null ? ErrorState(_error!, onRetry: () { setState(() => _ready = false); _init(); }) : ListView(padding: const EdgeInsets.all(16), children: [
        DevicePicker(devices: _devices, value: _deviceId, onChanged: (v) => setState(() { _deviceId = v; _preview = null; _fixTaxes(); })),
        const SizedBox(height: 12),
        Row(children: [
          Expanded(flex: 3, child: TextField(controller: _invoiceNo, decoration: const InputDecoration(labelText: 'Invoice number'), onChanged: (_) => _preview = null)),
          const SizedBox(width: 10),
          Expanded(flex: 2, child: DropdownButtonFormField<String>(isExpanded: true, initialValue: _currency, decoration: const InputDecoration(labelText: 'Currency'), items: [for (final c in _currencies) DropdownMenuItem(value: c, child: Text(c))], onChanged: (v) => setState(() { _currency = v ?? 'USD'; _preview = null; }))),
        ]),
        SwitchListTile(contentPadding: EdgeInsets.zero, title: const Text('Prices include tax'), value: _inclusive, onChanged: (v) => setState(() { _inclusive = v; _preview = null; })),
        const SectionTitle('Buyer (optional)'),
        Row(children: [Expanded(child: TextField(controller: _buyer, decoration: const InputDecoration(labelText: 'Buyer name'))), const SizedBox(width: 10), Expanded(child: TextField(controller: _tin, keyboardType: TextInputType.number, decoration: const InputDecoration(labelText: 'Buyer TIN')))]),
        const SectionTitle('Items'),
        LinesEditor(lines: _lines, taxes: _taxes, currency: _currency, onChanged: () => setState(() => _preview = null)),
        const SectionTitle('Payment'),
        DropdownButtonFormField<String>(isExpanded: true, initialValue: _pay, decoration: const InputDecoration(labelText: 'Paid by'), items: [for (final m in _payMethods) DropdownMenuItem(value: m, child: Text(m))], onChanged: (v) => setState(() => _pay = v ?? 'Cash')),
        if (pv != null) ...[
          const SectionTitle('What ZIMRA will receive'),
          AppCard(child: Column(children: [
            for (final l in (pv['lines'] ?? []) as List) Padding(padding: const EdgeInsets.symmetric(vertical: 3), child: Row(children: [Expanded(child: Text('${l['name']}  × ${l['quantity']}', overflow: TextOverflow.ellipsis)), Text(money(l['total']))])),
            const Divider(),
            for (final t in (pv['taxes'] ?? []) as List) Row(children: [Expanded(child: Text('Tax ${t['taxPercent'] ?? 'exempt'}${t['taxPercent'] != null ? '%' : ''}', style: const TextStyle(fontSize: 12))), Text(money(t['taxAmount']), style: const TextStyle(fontSize: 12))]),
            const SizedBox(height: 4),
            Row(children: [const Expanded(child: Text('Total', style: TextStyle(fontWeight: FontWeight.w800, fontSize: 16))), Amount(moneyIn(pv['total'], _currency), style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 18))]),
          ])),
        ],
        const SizedBox(height: 16),
        Row(children: [
          Expanded(child: OutlinedButton(onPressed: _busy ? null : _doPreview, child: const Text('Preview'))),
          const SizedBox(width: 12),
          Expanded(child: FilledButton(onPressed: _busy ? null : _submit, child: _busy ? const SizedBox(height: 20, width: 20, child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white)) : const Text('Issue invoice'))),
        ]),
        const SizedBox(height: 30),
      ]),
    );
  }
}
