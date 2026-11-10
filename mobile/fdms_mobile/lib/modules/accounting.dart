import 'package:file_picker/file_picker.dart';
import 'package:dio/dio.dart' show FormData, MultipartFile;
import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/api.dart';
import '../core/auth.dart';
import '../core/models.dart';
import '../screens/profile.dart' show guardedValue;
import '../ui/files.dart';
import '../ui/kit.dart';
import 'resource.dart';

String _s(Json j, String k) => (j[k] ?? '').toString();

/// Currency and date choices shared by the accounting reports.
class _Params {
  _Params() : from = DateTime(DateTime.now().year, DateTime.now().month, 1), to = DateTime.now();
  String currency = 'USD';
  DateTime from, to;
}

class _ParamBar extends StatelessWidget {
  const _ParamBar({required this.p, required this.currencies, required this.range, required this.onChanged});
  final _Params p;
  final List<String> currencies;
  final bool range;
  final VoidCallback onChanged;

  Future<void> _pick(BuildContext context, bool from) async {
    final d = await showDatePicker(context: context, initialDate: from ? p.from : p.to, firstDate: DateTime(2000), lastDate: DateTime(2100));
    if (d != null) { if (from) { p.from = d; } else { p.to = d; } onChanged(); }
  }

  @override
  Widget build(BuildContext context) => Wrap(spacing: 10, runSpacing: 10, crossAxisAlignment: WrapCrossAlignment.center, children: [
        ConstrainedBox(constraints: BoxConstraints(minWidth: 110, maxWidth: MediaQuery.sizeOf(context).width * 0.5), child: DropdownButtonFormField<String>(isExpanded: true, initialValue: currencies.contains(p.currency) ? p.currency : null, decoration: const InputDecoration(labelText: 'Currency', isDense: true), items: [for (final c in currencies) DropdownMenuItem(value: c, child: Text(c))], onChanged: (v) { p.currency = v ?? p.currency; onChanged(); })),
        if (range) OutlinedButton(onPressed: () => _pick(context, true), child: Text('From ${fmtDate(p.from.toIso8601String())}')),
        OutlinedButton(onPressed: () => _pick(context, false), child: Text(range ? 'To ${fmtDate(p.to.toIso8601String())}' : 'As at ${fmtDate(p.to.toIso8601String())}')),
      ]);
}

Future<List<String>> _currencies(Api api) async {
  try { final m = await api.getMap('/api/v1/accounting/meta'); final c = ((m['currencies'] ?? []) as List).map((e) => e.toString()).toList(); return c.isEmpty ? ['USD'] : c; } catch (_) { return ['USD']; }
}

/// Financial reports: ledger, trial balance, profit and loss, balance sheet and the ageing reports.
class AccountingReportScreen extends StatefulWidget {
  const AccountingReportScreen(this.kind, {super.key});
  final String kind;
  @override
  State<AccountingReportScreen> createState() => _AccountingReportScreenState();
}

class _AccountingReportScreenState extends State<AccountingReportScreen> {
  final _p = _Params();
  List<String> _cur = ['USD'];
  List<Json> _accounts = [];
  String? _account;
  Json? _data;
  Object? _error;
  bool _loading = false;

  bool get _range => widget.kind == 'profit-loss' || widget.kind == 'ledger';

  @override
  void initState() { super.initState(); _init(); }

  Future<void> _init() async {
    final api = context.read<Api>();
    _cur = await _currencies(api);
    _p.currency = _cur.contains('USD') ? 'USD' : _cur.first;
    if (widget.kind == 'ledger') { try { _accounts = await api.getList('/api/v1/accounting/accounts'); _account = _accounts.firstOrNull?['id']; } catch (_) {} }
    if (mounted) { setState(() {}); _run(); }
  }

  Future<void> _run() async {
    if (widget.kind == 'ledger' && _account == null) return;
    setState(() { _loading = true; _error = null; });
    final q = {'currency': _p.currency, if (_range) 'from': isoDay(_p.from), if (_range) 'to': isoDay(_p.to) else 'asAt': isoDay(_p.to), if (widget.kind == 'ledger') 'accountId': _account};
    try {
      final d = await context.read<Api>().getMap('/api/v1/accounting/reports/${widget.kind}', query: q);
      if (mounted) setState(() { _data = d; _loading = false; });
    } catch (e) { if (mounted) setState(() { _error = e; _loading = false; }); }
  }

  @override
  Widget build(BuildContext context) => ListView(padding: const EdgeInsets.all(16), children: [
        _ParamBar(p: _p, currencies: _cur, range: _range, onChanged: () { setState(() {}); _run(); }),
        if (widget.kind == 'ledger') Padding(padding: const EdgeInsets.only(top: 10), child: DropdownButtonFormField<String>(initialValue: _account, isExpanded: true, decoration: const InputDecoration(labelText: 'Account', isDense: true), items: [for (final a in _accounts) DropdownMenuItem(value: a['id'] as String, child: Text('${a['code']} · ${a['name']}', overflow: TextOverflow.ellipsis))], onChanged: (v) { setState(() => _account = v); _run(); })),
        const SizedBox(height: 14),
        if (_loading) const Padding(padding: EdgeInsets.all(40), child: Center(child: CircularProgressIndicator())) else if (_error != null) ErrorState(_error!, onRetry: _run) else if (_data != null) ..._render(context, _data!),
      ]);

  List<Widget> _render(BuildContext context, Json d) {
    switch (widget.kind) {
      case 'trial-balance':
        return [
          _table(context, ['Account', 'Debit', 'Credit'], [for (final r in (d['rows'] ?? []) as List) ['${r['code']} ${r['name']}', money(r['debit']), money(r['credit'])]], foot: ['Total', money(d['totalDebit']), money(d['totalCredit'])]),
          Padding(padding: const EdgeInsets.only(top: 10), child: Badge2(d['balanced'] == true ? 'Balanced' : 'Does not balance', tone: d['balanced'] == true ? Tone.ok : Tone.bad)),
        ];
      case 'profit-loss':
      case 'balance-sheet':
        return [
          for (final sec in (d['sections'] ?? []) as List) ...[
            SectionTitle(_s(sec, 'title')),
            _table(context, ['Account', 'Amount'], [for (final r in (sec['rows'] ?? []) as List) [('${r['code'] ?? ''} ${r['name'] ?? ''}').trim(), money(r['amount'])]], foot: ['Total', money(sec['total'])]),
          ],
          const SizedBox(height: 12),
          if (widget.kind == 'profit-loss') ...[AppCard(child: Column(children: [_kv('Gross profit', money(d['grossProfit'])), _kv('Net profit', money(d['netProfit']), bold: true)]))]
          else AppCard(child: Column(children: [_kv('Total assets', money(d['totalAssets'])), _kv('Liabilities and equity', money(d['totalLiabilitiesAndEquity']), bold: true), Badge2(d['balanced'] == true ? 'Balanced' : 'Does not balance', tone: d['balanced'] == true ? Tone.ok : Tone.bad)])),
        ];
      case 'ledger':
        return [
          if (d['opening'] != null) _kv('Opening balance', money(d['opening'])),
          _table(context, ['Date / entry', 'Debit', 'Credit', 'Balance'], [for (final l in (d['lines'] ?? []) as List) ['${fmtDate(l['date'])}\n${l['number']} ${l['memo'] ?? ''}', money(l['debit']), money(l['credit']), money(l['balance'])]]),
        ];
      case 'payables':
      case 'receivables':
        final rows = (d['rows'] ?? []) as List;
        return [
          _table(context, [widget.kind == 'payables' ? 'Supplier' : 'Customer', 'Not due', '1-30', '31-60', '61-90', '90+', 'Total'], [for (final r in rows) [(r['supplier'] ?? r['customer'] ?? '').toString(), money(r['notDue']), money(r['days1To30']), money(r['days31To60']), money(r['days61To90']), money(r['over90']), money(r['total'])]],
              foot: d['totals'] is Map ? ['Total', money(d['totals']['notDue']), money(d['totals']['days1To30']), money(d['totals']['days31To60']), money(d['totals']['days61To90']), money(d['totals']['over90']), money(d['totals']['total'])] : null),
        ];
      default:
        return [AppCard(child: Text(d.entries.map((e) => '${humanize(e.key)}: ${e.value}').join('\n')))];
    }
  }

  Widget _kv(String k, String v, {bool bold = false}) => Padding(padding: const EdgeInsets.symmetric(vertical: 4), child: Row(children: [Expanded(child: Text(k, style: TextStyle(fontWeight: bold ? FontWeight.w800 : FontWeight.w500))), Text(v, style: TextStyle(fontWeight: bold ? FontWeight.w800 : FontWeight.w600, fontSize: bold ? 16 : 14))]));
}

/// A scrollable table that stays readable on a narrow phone (it scrolls sideways when the columns do not fit).
Widget _table(BuildContext context, List<String> head, List<List<String>> rows, {List<String>? foot}) {
  final style = TextStyle(fontSize: 13, color: Theme.of(context).colorScheme.onSurface);
  TableRow row(List<String> cells, {bool bold = false, bool header = false}) => TableRow(
        decoration: header ? BoxDecoration(color: Theme.of(context).colorScheme.surfaceContainerHighest.withValues(alpha: 0.5)) : null,
        children: [for (var i = 0; i < cells.length; i++) Padding(padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 7), child: Text(cells[i], textAlign: i == 0 ? TextAlign.left : TextAlign.right, style: style.copyWith(fontWeight: bold || header ? FontWeight.w700 : FontWeight.w400, fontSize: header ? 12 : 13)))],
      );
  if (rows.isEmpty && foot == null) return const AppCard(child: Text('Nothing in this period.'));
  return AppCard(
    padding: const EdgeInsets.symmetric(vertical: 4),
    child: LayoutBuilder(builder: (c, box) => SingleChildScrollView(
      scrollDirection: Axis.horizontal,
      child: ConstrainedBox(
        constraints: BoxConstraints(minWidth: box.maxWidth),
        child: Table(defaultColumnWidth: const IntrinsicColumnWidth(), children: [row(head, header: true), for (final r in rows) row(r), if (foot != null) row(foot, bold: true)]),
      ),
    )),
  );
}

// ---- cashbook ------------------------------------------------------------------------------------------------------

class CashbookScreen extends StatefulWidget {
  const CashbookScreen({super.key});
  @override
  State<CashbookScreen> createState() => _CashbookScreenState();
}

class _CashbookScreenState extends State<CashbookScreen> {
  final _p = _Params();
  List<String> _cur = ['USD'];
  List<Json> _cash = [];
  List<Json> _accounts = [];
  String? _account;
  Json? _data;
  Object? _error;
  bool _loading = true;

  @override
  void initState() { super.initState(); _init(); }

  Future<void> _init() async {
    final api = context.read<Api>();
    try {
      final meta = await api.getMap('/api/v1/accounting/meta');
      _cur = ((meta['currencies'] ?? ['USD']) as List).map((e) => e.toString()).toList();
      _p.currency = (meta['defaultCurrency'] ?? _cur.first).toString();
      _cash = ((meta['cashAccounts'] ?? []) as List).map((e) => Map<String, dynamic>.from(e)).toList();
      _account = _cash.firstOrNull?['id'];
      _accounts = await api.getList('/api/v1/accounting/accounts');
      await _run();
    } catch (e) { if (mounted) setState(() { _error = e; _loading = false; }); }
  }

  Future<void> _run() async {
    if (_account == null) { if (mounted) setState(() => _loading = false); return; }
    if (mounted) setState(() { _loading = true; _error = null; });
    try {
      final d = await context.read<Api>().getMap('/api/v1/accounting/reports/cashbook', query: {'accountId': _account, 'currency': _p.currency, 'from': isoDay(_p.from), 'to': isoDay(_p.to)});
      if (mounted) setState(() { _data = d; _loading = false; });
    } catch (e) { if (mounted) setState(() { _error = e; _loading = false; }); }
  }

  Future<void> _entry(String kind) async {
    final fields = [
      const Fld('date', 'Date', kind: Kind.date, required: true),
      Fld('amount', 'Amount', kind: Kind.number, required: true),
      Fld('counterAccountId', kind == 'receipt' ? 'Received for (income or other account)' : 'Paid for (expense or other account)', kind: Kind.select, required: true, options: [for (final a in _accounts.where((a) => a['isActive'] != false && a['id'] != _account && (kind == 'receipt' ? a['type'] != 'Expense' : a['type'] != 'Income'))) Opt(a['id'], '${a['code']} · ${a['name']}')]),
      const Fld('description', 'Description', required: true),
      const Fld('reference', 'Reference'),
    ];
    final r = await showModalBottomSheet<Json>(context: context, isScrollControlled: true, showDragHandle: true, builder: (_) => AskSheet(title: kind == 'receipt' ? 'Record a receipt' : 'Record a payment', fields: fields));
    if (r == null || !mounted) return;
    final ok = await guarded(context, () async { await context.read<Api>().post('/api/v1/accounting/cashbook-entry', body: {...r, 'accountId': _account, 'kind': kind, 'currency': _p.currency}); }, done: 'Recorded.');
    if (ok) _run();
  }

  @override
  Widget build(BuildContext context) {
    final can = context.watch<AuthController>().can('accounting.manage');
    final d = _data;
    return Scaffold(
      backgroundColor: Colors.transparent,
      body: ListView(padding: const EdgeInsets.all(16), children: [
        if (_cash.isEmpty && !_loading) const AppCard(child: Text('There is no cash or bank account yet. Add one under Chart of accounts or Banks.')) else ...[
          DropdownButtonFormField<String>(initialValue: _account, isExpanded: true, decoration: const InputDecoration(labelText: 'Cash or bank account', isDense: true), items: [for (final a in _cash) DropdownMenuItem(value: a['id'] as String, child: Text('${a['code']} · ${a['name']}', overflow: TextOverflow.ellipsis))], onChanged: (v) { setState(() => _account = v); _run(); }),
          const SizedBox(height: 10),
          _ParamBar(p: _p, currencies: _cur, range: true, onChanged: () { setState(() {}); _run(); }),
        ],
        const SizedBox(height: 14),
        if (_loading) const Padding(padding: EdgeInsets.all(40), child: Center(child: CircularProgressIndicator())) else if (_error != null) ErrorState(_error!, onRetry: _run) else if (d != null) ...[
          ResponsiveGrid(minTileWidth: 140, children: [Kpi('Opening', money(d['opening'])), Kpi('Money in', money(d['totalReceipts']), color: const Color(0xFF059669)), Kpi('Money out', money(d['totalPayments']), color: const Color(0xFFDC2626)), Kpi('Closing', money(d['closing']))]),
          if (can) Padding(padding: const EdgeInsets.only(top: 12), child: Wrap(spacing: 8, runSpacing: 8, children: [FilledButton.icon(onPressed: () => _entry('receipt'), icon: const Icon(Icons.arrow_downward), label: const Text('Money in')), OutlinedButton.icon(onPressed: () => _entry('payment'), icon: const Icon(Icons.arrow_upward), label: const Text('Money out'))])),
          const SectionTitle('Movements'),
          _table(context, ['Date / description', 'In', 'Out', 'Balance'], [for (final l in (d['lines'] ?? []) as List) ['${fmtDate(l['date'])}\n${l['description'] ?? ''}', money(l['receipt']), money(l['payment']), money(l['balance'])]]),
        ],
      ]),
    );
  }
}

// ---- lists ---------------------------------------------------------------------------------------------------------

String _j(Iterable<String?> p) => p.where((e) => e != null && e.trim().isNotEmpty).join(' · ');

final journalsSpec = ResourceSpec(
  title: 'Journals', listPath: '/api/v1/accounting/journals', query: const {'take': 100}, itemTitle: (j) => '${_s(j, 'number')}  ${_s(j, 'memo')}', itemSubtitle: (j) => _j([fmtDate(j['date']), _s(j, 'source'), _s(j, 'reference')]), trailing: (j) => moneyIn(j['total'], _s(j, 'currency')),
  badges: (j) => [if (j['reversed'] == true) ('Reversed', Tone.warn), if (j['isReversal'] == true) ('Reversal', Tone.info)], leadingIcon: Icons.menu_book_outlined, emptyText: 'No journal entries yet.',
  facts: (j) => [('Number', _s(j, 'number')), ('Date', fmtDate(j['date'])), ('Memo', _s(j, 'memo')), ('Source', _s(j, 'source')), for (final l in (j['lines'] ?? []) as List) ('${l['accountCode']} ${l['accountName']}', (l['debit'] ?? 0) > 0 ? 'Dr ${money(l['debit'])}' : 'Cr ${money(l['credit'])}')],
  actions: [RowAction('Reverse', 'POST', (j) => '/api/v1/accounting/journals/${j['id']}/reverse', icon: Icons.undo, danger: true, perm: 'accounting.manage', when: (j) => j['reversed'] != true && j['isReversal'] != true && j['source'] != 'sales', confirmText: 'Reverse this entry? A matching opposite entry is posted; nothing is deleted.', done: 'Entry reversed.')],
);

final receivablesSpec = ResourceSpec(
  title: 'Customers owing', listPath: '/api/v1/accounting/receivables', itemTitle: (j) => '${_s(j, 'customer')} · ${_s(j, 'invoiceNo')}', itemSubtitle: (j) => _j(['Due ${fmtDate(j['dueDate'])}', if ((j['daysOverdue'] ?? 0) > 0) '${j['daysOverdue']} days late']), trailing: (j) => moneyIn(j['outstanding'], _s(j, 'currency')),
  badges: (j) => [(((j['outstanding'] ?? 0) as num) <= 0 ? 'Paid' : (j['daysOverdue'] ?? 0) > 0 ? 'Overdue' : 'Open', toneFor(((j['outstanding'] ?? 0) as num) <= 0 ? 'ok' : (j['daysOverdue'] ?? 0) > 0 ? 'bad' : 'warn'))], leadingIcon: Icons.how_to_reg_outlined, emptyText: 'Nobody owes you anything.',
  facts: (j) => [('Customer', _s(j, 'customer')), ('Invoice', _s(j, 'invoiceNo')), ('Date', fmtDate(j['date'])), ('Due', fmtDate(j['dueDate'])), ('Invoice total', money(j['total'])), ('Credited', money(j['credited'])), ('Received', money(j['received'])), ('Outstanding', moneyIn(j['outstanding'], _s(j, 'currency')))],
  actions: [
    RowAction('Receive payment', 'POST', (j) => '/api/v1/accounting/receivables/${j['receiptId']}/receive', icon: Icons.payments_outlined, perm: 'accounting.manage', when: (j) => ((j['outstanding'] ?? 0) as num) > 0,
        ask: const [Fld('date', 'Date received', kind: Kind.date, required: true), Fld('amount', 'Amount', kind: Kind.number, required: true), Fld('accountId', 'Paid into', kind: Kind.select, required: true, optionsPath: '/api/v1/accounting/accounts', optionLabel: 'name'), Fld('reference', 'Reference')],
        body: (j, a) => {'date': a['date'], 'amount': a['amount'], 'accountId': a['accountId'], 'reference': a['reference']}, done: 'Payment recorded.'),
  ],
);

final budgetsSpec = ResourceSpec(
  title: 'Budgets', listPath: '/api/v1/accounting/budgets', itemTitle: (j) => '${_s(j, 'name')} ${_s(j, 'year')}', itemSubtitle: (j) => 'Income ${money(j['totalIncome'])} · Expenses ${money(j['totalExpense'])} ${_s(j, 'currency')}', leadingIcon: Icons.savings_outlined,
  emptyText: 'No budgets yet. Create one on the web console; you can follow it here.',
  facts: (j) => [('Year', _s(j, 'year')), ('Currency', _s(j, 'currency')), ('Planned income', money(j['totalIncome'])), ('Planned expenses', money(j['totalExpense'])), for (final a in (j['accounts'] ?? []) as List) ('${a['code']} ${a['name']}', money(((a['months'] ?? []) as List).fold<num>(0, (s, m) => s + (m as num))))],
);

// ---- VAT -----------------------------------------------------------------------------------------------------------

class VatScreen extends StatefulWidget {
  const VatScreen({super.key});
  @override
  State<VatScreen> createState() => _VatScreenState();
}

class _VatScreenState extends State<VatScreen> {
  final _p = _Params();
  List<String> _cur = ['USD'];
  Json? _d;
  Object? _error;
  bool _loading = true;

  @override
  void initState() { super.initState(); _init(); }

  Future<void> _init() async { _cur = await _currencies(context.read<Api>()); if (!_cur.contains(_p.currency)) _p.currency = _cur.first; await _run(); }

  Future<void> _run() async {
    if (mounted) setState(() { _loading = true; _error = null; });
    try {
      final d = await context.read<Api>().getMap('/api/v1/accounting/vat/return', query: {'currency': _p.currency, 'from': isoDay(_p.from), 'to': isoDay(_p.to)});
      if (mounted) setState(() { _d = d; _loading = false; });
    } catch (e) { if (mounted) setState(() { _error = e; _loading = false; }); }
  }

  Future<void> _settle() async {
    final accounts = await context.read<Api>().getList('/api/v1/accounting/accounts');
    if (!mounted) return;
    final r = await showModalBottomSheet<Json>(context: context, isScrollControlled: true, showDragHandle: true, builder: (_) => AskSheet(title: 'Settle VAT', fields: [const Fld('date', 'Date', kind: Kind.date, required: true), Fld('accountId', 'Cash or bank account used', kind: Kind.select, required: true, options: [for (final a in accounts.where((a) => a['type'] == 'Asset')) Opt(a['id'], '${a['code']} · ${a['name']}')])]));
    if (r == null || !mounted) return;
    final ok = await guarded(context, () async { await context.read<Api>().post('/api/v1/accounting/vat/settle', body: {'date': r['date'], 'currency': _p.currency, 'accountId': r['accountId']}); }, done: 'VAT settled in the books.');
    if (ok) _run();
  }

  @override
  Widget build(BuildContext context) {
    final d = _d;
    final can = context.watch<AuthController>().can('accounting.manage');
    return ListView(padding: const EdgeInsets.all(16), children: [
      _ParamBar(p: _p, currencies: _cur, range: true, onChanged: () { setState(() {}); _run(); }),
      const SizedBox(height: 14),
      if (_loading) const Padding(padding: EdgeInsets.all(40), child: Center(child: CircularProgressIndicator())) else if (_error != null) ErrorState(_error!, onRetry: _run) else if (d != null) ...[
        ResponsiveGrid(minTileWidth: 150, children: [Kpi('Net sales', money(d['netSales'])), Kpi('Output VAT', money(d['netOutputVat'])), Kpi('Input VAT', money(d['inputVat'])), Kpi('VAT payable', money(d['vatPayable']), color: (d['vatPayable'] ?? 0) > 0 ? const Color(0xFFD97706) : const Color(0xFF059669))]),
        const SectionTitle('Return'),
        AppCard(child: Column(children: [
          for (final e in [('Taxable sales', 'taxableSales'), ('Sales returns', 'salesReturns'), ('Output VAT', 'outputVat'), ('Credit note VAT', 'creditNoteVat'), ('Purchases', 'purchases'), ('Input VAT', 'inputVat'), ('VAT payable', 'vatPayable'), ('Held for VAT now', 'outputBalance'), ('Settlement due', 'settlementDue')])
            Padding(padding: const EdgeInsets.symmetric(vertical: 4), child: Row(children: [Expanded(child: Text(e.$1)), Text(money(d[e.$2]), style: const TextStyle(fontWeight: FontWeight.w600))])),
        ])),
        if (can) Padding(padding: const EdgeInsets.only(top: 12), child: FilledButton.icon(onPressed: _settle, icon: const Icon(Icons.account_balance_outlined), label: const Text('Settle VAT in the books'))),
      ],
    ]);
  }
}

// ---- bank reconciliation (summary) ---------------------------------------------------------------------------------

class ReconcileScreen extends StatefulWidget {
  const ReconcileScreen({super.key});
  @override
  State<ReconcileScreen> createState() => _ReconcileScreenState();
}

class _ReconcileScreenState extends State<ReconcileScreen> {
  List<Json> _banks = [];
  String? _account;
  Json? _d;
  Object? _error;
  bool _loading = true;
  final _balance = TextEditingController();

  @override
  void initState() { super.initState(); _init(); }

  Future<void> _init() async {
    try {
      final api = context.read<Api>();
      final accounts = await api.getList('/api/v1/accounting/accounts');
      _banks = accounts.where((a) => a['role'] == 'bank' || a['bankAccountId'] != null).toList();
      _account = _banks.firstOrNull?['id'];
      await _run();
    } catch (e) { if (mounted) setState(() { _error = e; _loading = false; }); }
  }

  Future<void> _run() async {
    if (_account == null) { if (mounted) setState(() => _loading = false); return; }
    if (mounted) setState(() { _loading = true; _error = null; });
    try {
      final d = await context.read<Api>().getMap('/api/v1/accounting/bank/reconciliation', query: {'accountId': _account, 'asAt': isoDay(DateTime.now()), 'statementBalance': _balance.text.trim()});
      if (mounted) setState(() { _d = d; _loading = false; });
    } catch (e) { if (mounted) setState(() { _error = e; _loading = false; }); }
  }

  Future<void> _auto() async {
    final api = context.read<Api>();
    final r = await guardedValue(context, () => api.post('/api/v1/accounting/bank/auto-match', body: {'accountId': _account}));
    if (r != null && mounted) { toast(context, '${r['matched'] ?? 0} line(s) matched.'); _run(); }
  }

  Future<void> _import() async {
    final pick = await FilePicker.pickFiles(type: FileType.custom, allowedExtensions: ['csv', 'txt']);
    final f = pick.firstOrNull;
    if (f == null || !mounted) return;
    final bytes = await f.readAsBytes();
    if (!mounted) return;
    final api = context.read<Api>();
    final r = await guardedValue(context, () => api.post('/api/v1/accounting/bank/statement/import', body: {'accountId': _account, 'csv': String.fromCharCodes(bytes)}));
    if (r != null && mounted) { toast(context, '${r['imported'] ?? 0} imported, ${r['duplicates'] ?? 0} already there.'); _run(); }
  }

  @override
  Widget build(BuildContext context) {
    final can = context.watch<AuthController>().can('accounting.manage');
    final d = _d;
    return ListView(padding: const EdgeInsets.all(16), children: [
      if (_banks.isEmpty && !_loading) const AppCard(child: Text('Add a bank account under Banks first. Each one gets its own ledger account to reconcile.')) else ...[
        DropdownButtonFormField<String>(initialValue: _account, isExpanded: true, decoration: const InputDecoration(labelText: 'Bank account', isDense: true), items: [for (final a in _banks) DropdownMenuItem(value: a['id'] as String, child: Text('${a['code']} · ${a['name']}', overflow: TextOverflow.ellipsis))], onChanged: (v) { setState(() => _account = v); _run(); }),
        const SizedBox(height: 10),
        TextField(controller: _balance, keyboardType: const TextInputType.numberWithOptions(decimal: true, signed: true), decoration: InputDecoration(labelText: 'Closing balance on your bank statement', isDense: true, suffixIcon: IconButton(icon: const Icon(Icons.refresh), onPressed: _run)), onSubmitted: (_) => _run()),
      ],
      const SizedBox(height: 14),
      if (_loading) const Padding(padding: EdgeInsets.all(40), child: Center(child: CircularProgressIndicator())) else if (_error != null) ErrorState(_error!, onRetry: _run) else if (d != null) ...[
        ResponsiveGrid(minTileWidth: 150, children: [Kpi('Ledger balance', money(d['ledgerBalance'])), Kpi('Statement balance', d['statementBalance'] == null ? '-' : money(d['statementBalance'])), Kpi('Not on statement', money(d['unmatchedLedger'])), Kpi('Not in books', money(d['unmatchedStatement']))]),
        if (d['difference'] != null) Padding(padding: const EdgeInsets.only(top: 10), child: Badge2((d['difference'] as num) == 0 ? 'Reconciled' : 'Difference ${money(d['difference'])}', tone: (d['difference'] as num) == 0 ? Tone.ok : Tone.warn)),
        if (can) Padding(padding: const EdgeInsets.only(top: 12), child: Wrap(spacing: 8, runSpacing: 8, children: [FilledButton.icon(onPressed: _auto, icon: const Icon(Icons.auto_fix_high), label: const Text('Match automatically')), OutlinedButton.icon(onPressed: _import, icon: const Icon(Icons.upload_file), label: const Text('Import statement (CSV)'))])),
        const SectionTitle('Statement lines not yet matched'),
        for (final l in ((d['statementLines'] ?? []) as List).where((l) => l['matched'] != true)) Padding(padding: const EdgeInsets.only(bottom: 8), child: AppCard(padding: const EdgeInsets.all(12), child: Row(children: [Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [Text(_s(l, 'description')), Text(fmtDate(l['date']), style: TextStyle(fontSize: 12, color: Theme.of(context).hintColor))])), Text(money(l['amount']), style: const TextStyle(fontWeight: FontWeight.w700))]))),
        if (((d['statementLines'] ?? []) as List).where((l) => l['matched'] != true).isEmpty) const AppCard(child: Text('Everything on the statement is matched.')),
        const Padding(padding: EdgeInsets.only(top: 8), child: Text('To match lines one by one or create entries from them, use the web console.', style: TextStyle(fontSize: 12))),
      ],
    ]);
  }
}

// ---- documents -----------------------------------------------------------------------------------------------------

class DocumentsScreen extends StatefulWidget {
  const DocumentsScreen({super.key});
  @override
  State<DocumentsScreen> createState() => _DocumentsScreenState();
}

const _docKinds = ['Supplier invoice', 'Contract', 'Tax', 'Bank', 'Payroll', 'Asset', 'Other'];

class _DocumentsScreenState extends State<DocumentsScreen> {
  final _key = GlobalKey<LoaderState<(List<Json>, Json)>>();
  final _q = TextEditingController();

  Future<(List<Json>, Json)> _load() async {
    final api = context.read<Api>();
    final items = await api.getList('/api/v1/accounting/documents', query: {'q': _q.text.trim()});
    final sum = await api.getMap('/api/v1/accounting/documents/summary');
    return (items, sum);
  }

  Future<void> _upload() async {
    final pick = await FilePicker.pickFiles(type: FileType.custom, allowedExtensions: ['pdf', 'png', 'jpg', 'jpeg', 'xlsx', 'docx', 'csv', 'txt']);
    final f = pick.firstOrNull;
    if (f == null || !mounted) return;
    final bytes = await f.readAsBytes();
    if (!mounted) return;
    final r = await showModalBottomSheet<Json>(context: context, isScrollControlled: true, showDragHandle: true, builder: (_) => AskSheet(title: 'Store ${f.name}', fields: [Fld('title', 'Title', initial: f.name.replaceFirst(RegExp(r'\.[^.]+$'), '')), Fld('category', 'Kind', kind: Kind.select, required: true, initial: 'Supplier invoice', options: [for (final c in _docKinds) Opt(c, c)])]));
    if (r == null || !mounted) return;
    final api = context.read<Api>();
    final ok = await guarded(context, () async {
      final form = FormData.fromMap({'File': MultipartFile.fromBytes(bytes, filename: f.name), 'Category': r['category'], if (r['title'] != null) 'Title': r['title']});
      await api.upload('/api/v1/accounting/documents', form);
    }, done: 'Document stored.');
    if (ok) _key.currentState?.reload();
  }

  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    final auth = context.watch<AuthController>();
    return Scaffold(
      backgroundColor: Colors.transparent,
      floatingActionButton: auth.can('accounting.manage') ? FloatingActionButton.extended(onPressed: _upload, icon: const Icon(Icons.upload_file), label: const Text('Add document')) : null,
      body: Loader<(List<Json>, Json)>(
        key: _key,
        load: _load,
        builder: (context, data, reload) {
          final (items, sum) = data;
          return ListView(padding: const EdgeInsets.fromLTRB(16, 12, 16, 90), children: [
            TextField(controller: _q, textInputAction: TextInputAction.search, onSubmitted: (_) => reload(), decoration: const InputDecoration(hintText: 'Search documents…', prefixIcon: Icon(Icons.search))),
            Padding(padding: const EdgeInsets.symmetric(vertical: 8), child: Text('${sum['files'] ?? 0} file(s) · ${size((sum['totalBytes'] ?? 0) as num)} of ${size((sum['limitBytes'] ?? 0) as num)} · kept ${sum['yearsKept'] ?? 7} years', style: TextStyle(fontSize: 12, color: Theme.of(context).hintColor))),
            if (items.isEmpty) const Padding(padding: EdgeInsets.all(40), child: EmptyState('No documents yet. Keep supplier invoices, contracts and tax papers here.', icon: Icons.folder_open)),
            for (final d in items)
              Padding(padding: const EdgeInsets.only(bottom: 10), child: AppCard(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Row(children: [const Icon(Icons.description_outlined), const SizedBox(width: 10), Expanded(child: Text(_s(d, 'title'), style: const TextStyle(fontWeight: FontWeight.w700)))]),
                Padding(padding: const EdgeInsets.only(top: 4), child: Text(_j([_s(d, 'category'), size((d['sizeBytes'] ?? 0) as num), _s(d, 'linkLabel')]), style: TextStyle(fontSize: 12, color: Theme.of(context).hintColor))),
                Text('Added ${fmtDate(d['uploadedAt'])} · keep until ${fmtDate(d['retainUntil'])}', style: TextStyle(fontSize: 12, color: Theme.of(context).hintColor)),
                const SizedBox(height: 8),
                Wrap(spacing: 8, children: [
                  OutlinedButton.icon(onPressed: () async { try { final b = await api.bytes('/api/v1/accounting/documents/${d['id']}/file'); if (context.mounted) await openBytes(context, b, _s(d, 'fileName')); } catch (e) { if (context.mounted) toast(context, e, error: true); } }, icon: const Icon(Icons.open_in_new, size: 18), label: const Text('Open')),
                  OutlinedButton.icon(onPressed: () async { try { final b = await api.bytes('/api/v1/accounting/documents/${d['id']}/file'); if (context.mounted) await shareBytes(context, b, _s(d, 'fileName')); } catch (e) { if (context.mounted) toast(context, e, error: true); } }, icon: const Icon(Icons.share_outlined, size: 18), label: const Text('Share')),
                  if (auth.can('accounting.manage') && d['canDelete'] == true) OutlinedButton.icon(style: OutlinedButton.styleFrom(foregroundColor: Colors.red.shade700), onPressed: () async { if (await confirm(context, 'Delete "${d['title']}"?', ok: 'Delete', danger: true) && context.mounted) { if (await guarded(context, () async { await api.delete('/api/v1/accounting/documents/${d['id']}'); }, done: 'Deleted.')) reload(); } }, icon: const Icon(Icons.delete_outline, size: 18), label: const Text('Delete')),
                ]),
              ]))),
          ]);
        },
      ),
    );
  }
}
