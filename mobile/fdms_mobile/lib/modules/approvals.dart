import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/api.dart';
import '../core/auth.dart';
import '../core/models.dart';
import '../ui/kit.dart';
import '../ui/lists.dart';
import '../ui/premium.dart';

/// Lets a screen inside the shell open another module ("/billing", "/approvals" ...), the way a notification's link does on the web.
class RouteOpener {
  const RouteOpener(this.open);
  final void Function(String route) open;
}

IconData noteIcon(Map n) => switch (n['kind']) {
      'application' => Icons.apartment_rounded,
      'payment' || 'billing' => Icons.payments_outlined,
      'golive' => Icons.rocket_launch_outlined,
      'approval' => Icons.fact_check_outlined,
      'backup' => Icons.backup_outlined,
      'demo' => Icons.auto_delete_outlined,
      'certificate' => Icons.verified_user_outlined,
      'email' => Icons.mail_outline,
      _ => n['severity'] == 'bad' ? Icons.error_outline_rounded : n['severity'] == 'warn' ? Icons.warning_amber_rounded : Icons.notifications_none_rounded,
    };
Tone noteTone(Map n) => n['severity'] == 'bad' ? Tone.bad : n['severity'] == 'warn' ? Tone.warn : Tone.info;

String noteAction(Map n) => switch (n['kind']) {
      'application' || 'golive' => 'Review',
      'payment' => n['link'] == '/admin/payments' ? 'Confirm' : 'Open',
      'billing' => (n['title'] ?? '').toString().contains('ready to pay') ? 'Pay now' : 'Open',
      'approval' => 'Decide',
      _ => 'Open',
    };

/// The dashboard's "needs your attention" card: the newest unread notifications (or the latest ones), each one tap away from the screen that deals with it.
class NotificationsPanel extends StatefulWidget {
  const NotificationsPanel({super.key, this.title = 'Needs your attention', this.take = 5});
  final String title;
  final int take;
  @override
  State<NotificationsPanel> createState() => _NotificationsPanelState();
}

class _NotificationsPanelState extends State<NotificationsPanel> {
  Json? _data;

  @override
  void initState() { super.initState(); _load(); }

  Future<void> _load() async {
    try { final d = await context.read<Api>().getMap('/api/v1/notifications', query: {'take': 20}); if (mounted) setState(() => _data = d); } catch (_) { /* the dashboard works without it */ }
  }

  Future<void> _open(Map n) async {
    final api = context.read<Api>();
    final opener = context.read<RouteOpener?>();
    if (n['read'] != true) { try { await api.post('/api/v1/notifications/read', body: {'ids': [n['id']]}); } catch (_) {} }
    final link = (n['link'] ?? '').toString();
    if (link.isNotEmpty && opener != null) opener.open(link.startsWith('/') ? link.substring(1) : link);
    _load();
  }

  @override
  Widget build(BuildContext context) {
    final all = ((_data?['items'] ?? []) as List).cast<Map<String, dynamic>>();
    if (all.isEmpty) return const SizedBox.shrink();
    final unread = all.where((n) => n['read'] != true).toList();
    final shown = (unread.isNotEmpty ? unread : all).take(widget.take).toList();
    final count = (_data?['unread'] ?? 0) as num;
    return InfoCard(
      title: widget.title,
      icon: Icons.notifications_active_outlined,
      trailing: count > 0 ? Pill('$count new', tone: Tone.bad) : null,
      padding: const EdgeInsets.fromLTRB(16, 14, 16, 6),
      children: [
        for (final n in shown)
          InkWell(
            borderRadius: BorderRadius.circular(12),
            onTap: () => _open(n),
            child: Padding(
              padding: const EdgeInsets.symmetric(vertical: 8),
              child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Avatar(n['title']?.toString() ?? '', icon: noteIcon(n), color: toneColor(noteTone(n))),
                const SizedBox(width: 12),
                Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  Text((n['title'] ?? '').toString(), style: TextStyle(fontWeight: n['read'] == true ? FontWeight.w500 : FontWeight.w800)),
                  if ((n['body'] ?? '').toString().isNotEmpty) Text(n['body'].toString(), maxLines: 2, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 12, color: Theme.of(context).hintColor)),
                  Text(fmtDateTime(n['createdAt']), style: TextStyle(fontSize: 11, color: Theme.of(context).hintColor)),
                ])),
                if ((n['link'] ?? '').toString().isNotEmpty) Padding(padding: const EdgeInsets.only(left: 8), child: Text(noteAction(n), style: const TextStyle(color: kBrand, fontWeight: FontWeight.w800))),
              ]),
            ),
          ),
      ],
    );
  }
}

// ---- approvals ---------------------------------------------------------------------------------------------------------------

Tone _statusTone(String? s) => switch (s) { 'Approved' => Tone.ok, 'Rejected' || 'Failed' => Tone.bad, 'Pending' => Tone.warn, _ => Tone.neutral };

/// The approvals inbox: what waits for my decision, what I asked for, and everything.
class ApprovalsScreen extends StatefulWidget {
  const ApprovalsScreen({super.key});
  @override
  State<ApprovalsScreen> createState() => _ApprovalsScreenState();
}

class _ApprovalsScreenState extends State<ApprovalsScreen> {
  String _scope = 'mine';
  int _tick = 0;

  Future<void> _decide(Map r, String verb) async {
    final api = context.read<Api>();
    String? note;
    if (verb == 'reject') {
      note = await promptText(context, 'Why is it rejected? The requester sees this.', hint: 'Reason', ok: 'Reject');
      if (note == null) return;
      if (note.trim().isEmpty) { if (mounted) toast(context, 'Give a reason for the rejection.', error: true); return; }
    } else if (verb == 'approve') {
      note = await promptText(context, 'Approve “${r['summary']}”? Add a note if you like.', hint: 'Note (optional)', ok: 'Approve');
      if (note == null) return;
    } else if (!await confirm(context, 'Withdraw your request “${r['summary']}”?', ok: 'Withdraw')) {
      return;
    }
    if (!mounted) return;
    Json? res;
    final ok = await guarded(context, () async { res = Map<String, dynamic>.from(await api.post('/api/v1/approvals/${r['id']}/$verb', body: verb == 'cancel' ? null : {'note': (note ?? '').trim().isEmpty ? null : note!.trim()}) as Map); });
    if (ok && mounted) {
      final s = res?['status'];
      toast(context, s == 'Approved' ? 'Approved and carried out.' : s == 'Failed' ? 'Approved, but it could not be carried out.' : verb == 'approve' ? 'Approved. Now waiting for ${res?['waitingForRole']}.' : verb == 'reject' ? 'Rejected. The requester is told.' : 'Withdrawn.');
      setState(() => _tick++);
    }
  }

  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    final auth = context.watch<AuthController>();
    return Column(children: [
      Padding(
        padding: const EdgeInsets.fromLTRB(16, 12, 16, 0),
        child: FilterPills([
          for (final (k, l) in const [('mine', 'Waiting for me'), ('requested', 'Asked by me'), ('all', 'All')]) PillOption(l, _scope == k, () => setState(() => _scope = k)),
        ]),
      ),
      Expanded(child: Loader<Json>(
        key: ValueKey('$_scope/$_tick'),
        load: () => api.getMap('/api/v1/approvals', query: {'scope': _scope}),
        builder: (context, data, reload) {
          final items = ((data['items'] ?? []) as List).cast<Map<String, dynamic>>();
          return RefreshIndicator(onRefresh: reload, child: ListView(padding: const EdgeInsets.fromLTRB(16, 12, 16, 24), children: [
            if (auth.can('approvals.manage'))
              Align(alignment: Alignment.centerRight, child: TextButton.icon(onPressed: () => Navigator.push(context, MaterialPageRoute(builder: (_) => Scaffold(appBar: AppBar(title: const Text('Approval matrix')), body: const ApprovalMatrixScreen()))), icon: const Icon(Icons.account_tree_outlined, size: 18), label: const Text('Approval matrix'))),
            if (items.isEmpty) BigEmpty(icon: Icons.fact_check_outlined, title: _scope == 'mine' ? 'Nothing waits for you' : 'No requests', message: _scope == 'mine' ? 'When something needs your approval it appears here and in the bell.' : 'Requests appear when an action is held by the approval matrix.'),
            for (final r in items) _RequestCard(r: r, onDecide: _decide),
          ]));
        },
      )),
    ]);
  }
}

class _RequestCard extends StatelessWidget {
  const _RequestCard({required this.r, required this.onDecide});
  final Map<String, dynamic> r;
  final Future<void> Function(Map r, String verb) onDecide;

  @override
  Widget build(BuildContext context) {
    final hint = Theme.of(context).hintColor;
    final levels = ((r['levels'] ?? []) as List).map((e) => e.toString()).toList();
    final decisions = ((r['decisions'] ?? []) as List).cast<Map<String, dynamic>>();
    Map? dec(int level) => decisions.where((d) => d['level'] == level).firstOrNull;
    return InfoCard(
      padding: const EdgeInsets.fromLTRB(16, 14, 16, 12),
      children: [
        Wrap(spacing: 6, runSpacing: 6, children: [Pill((r['actionLabel'] ?? '').toString(), tone: Tone.neutral), Pill((r['status'] ?? '').toString(), tone: _statusTone(r['status']))]),
        const SizedBox(height: 8),
        Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text((r['summary'] ?? '').toString(), style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w800)),
            Text('Asked by ${r['requestedBy']} · ${fmtDateTime(r['createdAt'])}', style: TextStyle(fontSize: 12, color: hint)),
          ])),
          const SizedBox(width: 8),
          Flexible(child: FittedBox(fit: BoxFit.scaleDown, alignment: Alignment.centerRight, child: Text(moneyIn(r['amount'] as num?, r['currency']?.toString()), style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w900)))),
        ]),
        const SizedBox(height: 10),
        Wrap(spacing: 6, runSpacing: 6, crossAxisAlignment: WrapCrossAlignment.center, children: [
          for (var i = 0; i < levels.length; i++) ...[
            Builder(builder: (_) {
              final d = dec(i + 1);
              final waiting = r['status'] == 'Pending' && d == null && r['waitingForRole'] == levels[i] && decisions.length == i;
              final tone = d?['decision'] == 'Approved' ? Tone.ok : d?['decision'] == 'Rejected' ? Tone.bad : waiting ? Tone.warn : Tone.neutral;
              return Pill('${i + 1}. ${levels[i]}${d != null ? ' · ${d['userName']}' : ''}', tone: tone);
            }),
            if (i < levels.length - 1) Icon(Icons.chevron_right_rounded, size: 16, color: hint),
          ],
        ]),
        for (final d in decisions) if ((d['note'] ?? '').toString().isNotEmpty) Padding(padding: const EdgeInsets.only(top: 6), child: Text('${d['userName']}: “${d['note']}”', style: const TextStyle(fontSize: 13))),
        if ((r['error'] ?? '').toString().isNotEmpty) Padding(padding: const EdgeInsets.only(top: 6), child: Text('Approved but not carried out: ${r['error']}', style: TextStyle(color: toneColor(Tone.bad), fontSize: 13))),
        if (r['canDecide'] == true || r['canCancel'] == true) ...[
          const Divider(height: 22),
          Wrap(alignment: WrapAlignment.end, spacing: 8, runSpacing: 8, children: [
            if (r['canCancel'] == true) OutlinedButton(onPressed: () => onDecide(r, 'cancel'), child: const Text('Withdraw')),
            if (r['canDecide'] == true) ...[
              OutlinedButton.icon(style: OutlinedButton.styleFrom(foregroundColor: toneColor(Tone.bad)), onPressed: () => onDecide(r, 'reject'), icon: const Icon(Icons.close_rounded, size: 18), label: const Text('Reject')),
              FilledButton.icon(onPressed: () => onDecide(r, 'approve'), icon: const Icon(Icons.check_rounded, size: 18), label: const Text('Approve')),
            ],
          ]),
        ],
      ],
    );
  }
}

/// The approval matrix: for each action, from which amount (and currency) it needs approval, and which roles approve in which order. Administrators edit it here too.
class ApprovalMatrixScreen extends StatefulWidget {
  const ApprovalMatrixScreen({super.key});
  @override
  State<ApprovalMatrixScreen> createState() => _ApprovalMatrixScreenState();
}

class _ApprovalMatrixScreenState extends State<ApprovalMatrixScreen> {
  final _key = GlobalKey<LoaderState<Json>>();

  Future<void> _edit(Map action, List<String> roles, [Map? rule]) async {
    final saved = await Navigator.push<bool>(context, MaterialPageRoute(builder: (_) => _RuleEditor(action: action, roles: roles, rule: rule)));
    if (saved == true) _key.currentState?.reload();
  }

  Future<void> _delete(Map rule) async {
    if (!await confirm(context, 'Delete this rule? Requests already waiting are not affected.', ok: 'Delete', danger: true) || !mounted) return;
    if (await guarded(context, () async { await context.read<Api>().delete('/api/v1/approvals/rules/${rule['id']}'); }, done: 'Rule deleted.')) _key.currentState?.reload();
  }

  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    final manage = context.watch<AuthController>().can('approvals.manage');
    return Loader<Json>(
      key: _key,
      load: () => api.getMap('/api/v1/approvals/matrix'),
      builder: (context, m, reload) {
        final rows = ((m['rows'] ?? []) as List).cast<Map<String, dynamic>>();
        final roles = ((m['roles'] ?? []) as List).map((e) => e.toString()).toList();
        final hint = Theme.of(context).hintColor;
        return ListView(padding: const EdgeInsets.fromLTRB(16, 12, 16, 24), children: [
          Padding(padding: const EdgeInsets.only(bottom: 12), child: Text('Which actions need approval, from which amount, and who approves in which order. The most specific rule wins.', style: TextStyle(color: hint))),
          for (final row in rows)
            Builder(builder: (context) {
              final a = Map<String, dynamic>.from(row['action'] as Map);
              final rules = ((row['rules'] ?? []) as List).cast<Map<String, dynamic>>();
              return InfoCard(
                title: (a['label'] ?? '').toString(),
                icon: Icons.rule_rounded,
                trailing: manage ? IconButton(tooltip: 'Add a rule', visualDensity: VisualDensity.compact, onPressed: () => _edit(a, roles), icon: const Icon(Icons.add_circle_outline, color: kBrand)) : null,
                children: [
                  Text((a['description'] ?? '').toString(), style: TextStyle(fontSize: 13, color: hint)),
                  const SizedBox(height: 8),
                  if (rules.isEmpty) Padding(padding: const EdgeInsets.only(bottom: 8), child: Text('No rule: it happens straight away.', style: TextStyle(fontSize: 13, color: hint, fontStyle: FontStyle.italic))),
                  for (final r in rules)
                    InkWell(
                      onTap: manage ? () => _edit(a, roles, r) : null,
                      onLongPress: manage ? () => _delete(r) : null,
                      child: Opacity(
                        opacity: r['isActive'] == true ? 1 : 0.5,
                        child: Padding(
                          padding: const EdgeInsets.symmetric(vertical: 8),
                          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                            Text('From ${money(r['minAmount'] as num?)} ${r['currency'] ?? '(any currency)'}${r['isActive'] == true ? '' : ' · off'}', style: const TextStyle(fontWeight: FontWeight.w700)),
                            const SizedBox(height: 4),
                            Wrap(spacing: 6, runSpacing: 6, crossAxisAlignment: WrapCrossAlignment.center, children: [
                              for (var i = 0; i < (r['levels'] as List).length; i++) ...[
                                Pill('${i + 1}. ${(r['levels'] as List)[i]}', tone: Tone.info),
                                if (i < (r['levels'] as List).length - 1) Icon(Icons.chevron_right_rounded, size: 16, color: hint),
                              ],
                            ]),
                          ]),
                        ),
                      ),
                    ),
                  if (manage && rules.isNotEmpty) Padding(padding: const EdgeInsets.only(bottom: 6), child: Text('Tap a rule to change it, hold to delete it.', style: TextStyle(fontSize: 11, color: hint))),
                ],
              );
            }),
        ]);
      },
    );
  }
}

class _RuleEditor extends StatefulWidget {
  const _RuleEditor({required this.action, required this.roles, this.rule});
  final Map action;
  final List<String> roles;
  final Map? rule;
  @override
  State<_RuleEditor> createState() => _RuleEditorState();
}

class _RuleEditorState extends State<_RuleEditor> {
  late final _min = TextEditingController(text: widget.rule == null ? '0' : '${widget.rule!['minAmount']}');
  late final _cur = TextEditingController(text: (widget.rule?['currency'] ?? '').toString());
  late final List<String> _levels = [...((widget.rule?['levels'] ?? []) as List).map((e) => e.toString())];
  late bool _active = widget.rule?['isActive'] ?? true;
  bool _busy = false;

  @override
  void dispose() { _min.dispose(); _cur.dispose(); super.dispose(); }

  Future<void> _save() async {
    if (_levels.isEmpty) { toast(context, 'Add at least one approval level.', error: true); return; }
    setState(() => _busy = true);
    final ok = await guarded(context, () async {
      await context.read<Api>().put('/api/v1/approvals/rules', body: {
        'id': widget.rule?['id'], 'action': widget.action['key'], 'minAmount': double.tryParse(_min.text.trim()) ?? 0,
        'currency': _cur.text.trim().isEmpty ? null : _cur.text.trim().toUpperCase(), 'levels': _levels, 'isActive': _active,
      });
    }, done: 'Rule saved. It applies from now on.');
    if (mounted) { setState(() => _busy = false); if (ok) Navigator.pop(context, true); }
  }

  @override
  Widget build(BuildContext context) {
    final hint = Theme.of(context).hintColor;
    return Scaffold(
      appBar: AppBar(title: Text(widget.rule == null ? 'New rule' : 'Edit rule')),
      body: SafeArea(child: ListView(padding: const EdgeInsets.all(16), children: [
        Text((widget.action['label'] ?? '').toString(), style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w800)),
        Text('Amount: ${widget.action['amountMeaning'] ?? ''}', style: TextStyle(color: hint)),
        const SizedBox(height: 16),
        TextField(controller: _min, keyboardType: const TextInputType.numberWithOptions(decimal: true), decoration: const InputDecoration(labelText: 'From amount')),
        const SizedBox(height: 12),
        TextField(controller: _cur, maxLength: 3, textCapitalization: TextCapitalization.characters, decoration: const InputDecoration(labelText: 'Currency (empty = any)', hintText: 'USD')),
        const SizedBox(height: 8),
        const Text('Approved by, in this order', style: TextStyle(fontWeight: FontWeight.w700)),
        Text('Tap roles to add them as levels (1 to 5). Each level is decided by a different person, never the one who asked.', style: TextStyle(fontSize: 12, color: hint)),
        const SizedBox(height: 8),
        if (_levels.isNotEmpty)
          Wrap(spacing: 6, runSpacing: 6, children: [
            for (var i = 0; i < _levels.length; i++) InputChip(label: Text('${i + 1}. ${_levels[i]}'), onDeleted: () => setState(() => _levels.removeAt(i))),
          ]),
        const SizedBox(height: 8),
        Wrap(spacing: 6, runSpacing: 6, children: [
          for (final role in widget.roles) ActionChip(avatar: const Icon(Icons.add, size: 16), label: Text(role), onPressed: _levels.length >= 5 ? null : () => setState(() => _levels.add(role))),
        ]),
        const SizedBox(height: 8),
        SwitchListTile(contentPadding: EdgeInsets.zero, value: _active, onChanged: (v) => setState(() => _active = v), title: const Text('Active')),
        const SizedBox(height: 12),
        FilledButton(onPressed: _busy ? null : _save, child: Text(_busy ? 'Saving…' : 'Save rule')),
      ])),
    );
  }
}

// ---- revenue (the platform owner) ---------------------------------------------------------------------------------------------

String moneyList(dynamic list) {
  final l = ((list ?? []) as List).cast<Map>();
  if (l.isEmpty) return '0.00';
  return l.map((a) => moneyIn(a['amount'] as num?, a['currency']?.toString())).join(' + ');
}

/// The platform owner's money: recurring revenue, what came in, what is owed, which plans sell, who pays, and what the platform owes in rewards.
class RevenueScreen extends StatelessWidget {
  const RevenueScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    return Loader<Json>(
      load: () => api.getMap('/api/v1/admin/revenue'),
      builder: (context, r, reload) {
        final c = Map<String, dynamic>.from((r['counts'] ?? {}) as Map);
        final months = ((r['months'] ?? []) as List).cast<Map<String, dynamic>>();
        final plans = ((r['plans'] ?? []) as List).cast<Map<String, dynamic>>();
        final methods = ((r['methods'] ?? []) as List).cast<Map<String, dynamic>>();
        final top = ((r['topClients'] ?? []) as List).cast<Map<String, dynamic>>();
        final owing = ((r['owing'] ?? []) as List).cast<Map<String, dynamic>>();
        final hint = Theme.of(context).hintColor;
        // the chart shows the currency most money comes in
        final l12 = ((r['last12Months'] ?? []) as List).cast<Map>()..sort((a, b) => ((b['amount'] ?? 0) as num).compareTo((a['amount'] ?? 0) as num));
        final cur = l12.isEmpty ? 'USD' : l12.first['currency'].toString();
        final points = [for (final m in months) {'date': '${m['month']}-01', 'count': ((m['collected'] as List).cast<Map>().where((a) => a['currency'] == cur).firstOrNull?['amount'] ?? 0) as num, 'rejected': 0}];
        return RefreshIndicator(onRefresh: reload, child: ListView(padding: const EdgeInsets.fromLTRB(16, 12, 16, 24), children: [
          Container(
            padding: const EdgeInsets.all(20),
            decoration: BoxDecoration(
              gradient: const LinearGradient(colors: [Color(0xFF064E3B), Color(0xFF0F766E), Color(0xFF0369A1)], begin: Alignment.topLeft, end: Alignment.bottomRight),
              borderRadius: BorderRadius.circular(24),
            ),
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Text('RECURRING REVENUE / MONTH', style: TextStyle(color: Colors.white.withValues(alpha: 0.75), fontSize: 11, fontWeight: FontWeight.w800, letterSpacing: 1)),
              FittedBox(fit: BoxFit.scaleDown, alignment: Alignment.centerLeft, child: Text(moneyList(r['mrr']), style: const TextStyle(color: Colors.white, fontSize: 32, fontWeight: FontWeight.w900, letterSpacing: -1))),
              const SizedBox(height: 10),
              Wrap(spacing: 8, runSpacing: 8, children: [
                HeroChip('${c['paying'] ?? 0} paying', icon: Icons.verified_rounded),
                HeroChip('${c['trial'] ?? 0} on trial', icon: Icons.hourglass_top_rounded),
                HeroChip('${moneyList(r['arr'])} / year', icon: Icons.trending_up_rounded),
              ]),
            ]),
          ),
          const SizedBox(height: 14),
          ResponsiveGrid(minTileWidth: 150, children: [
            Kpi('This month', moneyList(r['thisMonth']), icon: Icons.payments_outlined, color: const Color(0xFF059669), sub: 'Last month ${moneyList(r['lastMonth'])}'),
            Kpi('Last 12 months', moneyList(r['last12Months']), icon: Icons.calendar_month_outlined, sub: 'All time ${moneyList(r['allTime'])}'),
            Kpi('Outstanding', moneyList(r['outstanding']), icon: Icons.receipt_long_outlined, color: const Color(0xFFD97706), sub: 'Overdue ${moneyList(r['overdue'])}'),
            Kpi('Payments to confirm', '${c['awaitingConfirmation'] ?? 0}', icon: Icons.fact_check_outlined, color: (c['awaitingConfirmation'] ?? 0) > 0 ? const Color(0xFFD97706) : null, sub: 'Trial to paid ${r['trialConversionPercent'] ?? 0}%'),
          ]),
          const SizedBox(height: 4),
          InfoCard(title: 'Companies', icon: Icons.apartment_rounded, children: [
            Wrap(spacing: 8, runSpacing: 8, children: [
              Pill('${c['companies'] ?? 0} in total', tone: Tone.neutral),
              Pill('${c['newThisMonth'] ?? 0} new this month', tone: Tone.info),
              Pill('${c['trialsEndingIn7Days'] ?? 0} trials ending in 7 days', tone: Tone.info),
              Pill('${c['pastDue'] ?? 0} past due', tone: Tone.warn),
              Pill('${c['suspended'] ?? 0} suspended', tone: Tone.bad),
            ]),
            const SizedBox(height: 8),
          ]),
          if (months.isNotEmpty) InfoCard(title: 'Collected per month · $cur', icon: Icons.bar_chart_rounded, children: [Padding(padding: const EdgeInsets.only(bottom: 8), child: _MoneyBars(points))]),
          InfoCard(title: 'Plans', icon: Icons.layers_outlined, children: [
            for (final p in plans)
              Padding(padding: const EdgeInsets.symmetric(vertical: 6), child: Row(children: [
                Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  Text('${p['plan']}', style: const TextStyle(fontWeight: FontWeight.w700)),
                  Text('${moneyIn(p['priceMonthly'] as num?, p['currency']?.toString())} / month · ${p['paying']} paying · ${p['trial']} on trial', style: TextStyle(fontSize: 12, color: hint)),
                ])),
                Side(fraction: 0.36, children: [Text(moneyIn(p['mrr'] as num?, p['currency']?.toString()), style: const TextStyle(fontWeight: FontWeight.w800))]),
              ])),
          ]),
          if (methods.isNotEmpty) InfoCard(title: 'How companies pay', icon: Icons.account_balance_wallet_outlined, children: [
            for (final m in methods) Padding(padding: const EdgeInsets.symmetric(vertical: 5), child: Row(children: [Expanded(child: Text('${m['method']} · ${m['payments']}')), Side(fraction: 0.45, children: [Text(moneyList(m['collected']), style: const TextStyle(fontWeight: FontWeight.w700))])])),
          ]),
          InfoCard(title: 'Best paying companies', icon: Icons.emoji_events_outlined, children: [
            if (top.isEmpty) Text('No paid invoices yet.', style: TextStyle(color: hint)),
            for (final t in top) Padding(padding: const EdgeInsets.symmetric(vertical: 5), child: Row(children: [Expanded(child: Text('${t['company']}', style: const TextStyle(fontWeight: FontWeight.w600))), Side(fraction: 0.45, children: [Text(moneyList(t['paidAllTime']))])])),
          ]),
          InfoCard(title: 'Companies that owe', icon: Icons.warning_amber_rounded, children: [
            if (owing.isEmpty) Text('Nobody owes anything.', style: TextStyle(color: hint)),
            for (final o in owing) Padding(padding: const EdgeInsets.symmetric(vertical: 5), child: Row(children: [Expanded(child: Text('${o['company']} · ${o['status']}')), Side(fraction: 0.45, children: [Text(moneyList(o['outstanding']), style: TextStyle(color: toneColor(Tone.warn), fontWeight: FontWeight.w700))])])),
          ]),
          InfoCard(title: 'What the platform owes', icon: Icons.savings_outlined, children: [
            InfoRow(icon: Icons.toll_outlined, 'Referral coins held', '${r['coinsOutstanding'] ?? 0} coins (${moneyList(r['coinsLiability'])})'),
            InfoRow(icon: Icons.card_giftcard, 'Referral money not paid out', moneyList(r['referralMoneyOwed'])),
            InfoRow(icon: Icons.handshake_outlined, last: true, 'Partner commission not paid out', moneyList(r['partnerCommissionOwed'])),
          ]),
          Text('Worked out ${fmtDateTime(r['generatedAt'])}. Invoices paid with coins are not counted as revenue.', style: TextStyle(fontSize: 11, color: hint), textAlign: TextAlign.center),
        ]));
      },
    );
  }
}

/// Bars of money per month, sized by flex so they fit any width.
class _MoneyBars extends StatelessWidget {
  const _MoneyBars(this.points);
  final List<Map<String, dynamic>> points;
  @override
  Widget build(BuildContext context) {
    final max = points.map((p) => (p['count'] as num).toDouble()).fold<double>(1, (a, b) => b > a ? b : a);
    const names = ['J', 'F', 'M', 'A', 'M', 'J', 'J', 'A', 'S', 'O', 'N', 'D'];
    return AspectRatio(
      aspectRatio: 2.6,
      child: Row(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
        for (final p in points)
          Expanded(child: Tooltip(
            message: '${p['date'].toString().substring(0, 7)}: ${money(p['count'] as num)}',
            child: Padding(padding: const EdgeInsets.symmetric(horizontal: 2), child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
              Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
                Expanded(flex: (1000 - ((p['count'] as num) / max * 1000).round()).clamp(1, 1000), child: const SizedBox.shrink()),
                Expanded(flex: ((p['count'] as num) / max * 1000).round().clamp(8, 1000), child: const DecoratedBox(decoration: BoxDecoration(gradient: LinearGradient(colors: [Color(0xFF2DD4BF), Color(0xFF059669)], begin: Alignment.topCenter, end: Alignment.bottomCenter), borderRadius: BorderRadius.vertical(top: Radius.circular(4))))),
              ])),
              const SizedBox(height: 4),
              FittedBox(fit: BoxFit.scaleDown, child: Text(names[(int.tryParse(p['date'].toString().substring(5, 7)) ?? 1) - 1], style: TextStyle(fontSize: 10, color: Theme.of(context).hintColor))),
            ])),
          )),
      ]),
    );
  }
}
