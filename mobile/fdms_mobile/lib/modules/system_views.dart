import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:provider/provider.dart';

import '../core/api.dart';
import '../core/auth.dart';
import '../core/models.dart';
import '../ui/kit.dart';

String _s(Json j, String k) => (j[k] ?? '').toString();

String _ago(dynamic iso) {
  final d = parseDate(iso);
  if (d == null) return '';
  final diff = DateTime.now().difference(d);
  if (diff.inSeconds < 60) return 'just now';
  if (diff.inMinutes < 60) return '${diff.inMinutes} min ago';
  if (diff.inHours < 24) return '${diff.inHours} h ago';
  if (diff.inDays < 7) return '${diff.inDays} d ago';
  return fmtDate(iso);
}

String _dayHeading(DateTime d) {
  final now = DateTime.now();
  final today = DateTime(now.year, now.month, now.day);
  final day = DateTime(d.year, d.month, d.day);
  final diff = today.difference(day).inDays;
  if (diff == 0) return 'Today';
  if (diff == 1) return 'Yesterday';
  return fmtDate(d.toIso8601String());
}

// ---- audit log -----------------------------------------------------------------------------------------------------

({IconData icon, Color color}) _auditStyle(String action) {
  final a = action.toLowerCase();
  if (a.contains('failed') || a.contains('blocked') || a.contains('rejected') || a.contains('delete') || a.contains('suspend') || a.contains('revoke')) return (icon: Icons.warning_amber_rounded, color: const Color(0xFFDC2626));
  if (a.startsWith('auth') || a.contains('mfa') || a.contains('passkey') || a.contains('password')) return (icon: Icons.lock_outline, color: const Color(0xFF7C3AED));
  if (a.startsWith('user') || a.startsWith('client') || a.startsWith('partner') || a.startsWith('application')) return (icon: Icons.people_outline, color: const Color(0xFF0284C7));
  if (a.startsWith('device') || a.startsWith('fiscal') || a.startsWith('receipt')) return (icon: Icons.receipt_long_outlined, color: kBrand);
  if (a.contains('payment') || a.contains('billing') || a.contains('invoice') || a.contains('subscription') || a.startsWith('referral')) return (icon: Icons.payments_outlined, color: const Color(0xFF059669));
  if (a.startsWith('backup') || a.startsWith('settings') || a.startsWith('security') || a.startsWith('golive')) return (icon: Icons.settings_outlined, color: const Color(0xFFD97706));
  return (icon: Icons.bolt, color: const Color(0xFF64748B));
}

/// Who did what, and when, across every company: a timeline grouped by day, with the person, the company and the detail on each line.
class AuditScreen extends StatefulWidget {
  const AuditScreen({super.key});
  @override
  State<AuditScreen> createState() => _AuditScreenState();
}

class _AuditScreenState extends State<AuditScreen> {
  final _items = <Json>[];
  final _search = TextEditingController();
  final _scroll = ScrollController();
  String _actor = '', _action = '';
  List<String> _actions = [];
  int _page = 1, _total = 0;
  bool _loading = true, _more = false;
  Object? _error;

  @override
  void initState() {
    super.initState();
    _scroll.addListener(() { if (_scroll.position.pixels > _scroll.position.maxScrollExtent - 300 && !_more && _items.length < _total) _load(more: true); });
    _load();
  }

  @override
  void dispose() { _scroll.dispose(); _search.dispose(); super.dispose(); }

  Future<void> _load({bool more = false}) async {
    final api = context.read<Api>();
    if (more) { setState(() => _more = true); _page++; } else { setState(() { _loading = _items.isEmpty; _page = 1; _error = null; }); }
    try {
      final r = await api.getMap('/api/v1/audit/search', query: {'search': _search.text.trim(), 'actorType': _actor, 'action': _action, 'page': _page, 'pageSize': 30});
      final list = ((r['items'] ?? []) as List).map((e) => Map<String, dynamic>.from(e)).toList();
      if (!mounted) return;
      setState(() {
        if (!more) _items.clear();
        _items.addAll(list); _total = (r['total'] ?? 0) as int; _loading = false; _more = false;
        if ((r['actions'] as List?) != null) _actions = (r['actions'] as List).map((e) => e.toString()).toList();
      });
    } catch (e) {
      if (mounted) setState(() { _error = e; _loading = false; _more = false; if (more) _page--; });
    }
  }

  Future<void> _pickAction() async {
    final v = await showModalBottomSheet<String>(
      context: context, isScrollControlled: true, showDragHandle: true,
      builder: (c) => SafeArea(child: ConstrainedBox(constraints: BoxConstraints(maxHeight: MediaQuery.sizeOf(c).height * 0.7), child: ListView(shrinkWrap: true, children: [
        ListTile(title: const Text('All actions'), selected: _action.isEmpty, onTap: () => Navigator.pop(c, '')),
        for (final a in _actions) ListTile(leading: Icon(_auditStyle(a).icon, color: _auditStyle(a).color), title: Text(a), selected: a == _action, onTap: () => Navigator.pop(c, a)),
      ]))),
    );
    if (v != null) { setState(() => _action = v); _load(); }
  }

  @override
  Widget build(BuildContext context) {
    final rows = <Widget>[];
    String? last;
    for (final j in _items) {
      final d = parseDate(j['at']);
      final h = d == null ? '' : _dayHeading(d);
      if (h != last) { rows.add(Padding(padding: const EdgeInsets.fromLTRB(4, 14, 4, 6), child: Text(h, style: TextStyle(fontWeight: FontWeight.w800, fontSize: 13, color: Theme.of(context).hintColor)))); last = h; }
      rows.add(_AuditTile(j, onTap: () => _detail(j)));
    }
    return Column(children: [
      Padding(padding: const EdgeInsets.fromLTRB(16, 12, 16, 4), child: TextField(controller: _search, textInputAction: TextInputAction.search, onSubmitted: (_) => _load(), decoration: InputDecoration(hintText: 'Search the audit log…', prefixIcon: const Icon(Icons.search), suffixIcon: _search.text.isEmpty ? null : IconButton(icon: const Icon(Icons.clear), onPressed: () { _search.clear(); _load(); })))),
      SingleChildScrollView(scrollDirection: Axis.horizontal, padding: const EdgeInsets.symmetric(horizontal: 12), child: Row(children: [
        for (final e in const [('', 'Everyone'), ('user', 'People'), ('token', 'API tokens'), ('system', 'System')])
          Padding(padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 6), child: ChoiceChip(label: Text(e.$2), selected: _actor == e.$1, onSelected: (_) { setState(() => _actor = e.$1); _load(); })),
        Padding(padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 6), child: ActionChip(avatar: const Icon(Icons.filter_list, size: 18), label: Text(_action.isEmpty ? 'Action' : _action), onPressed: _pickAction)),
      ])),
      Expanded(child: _loading ? const Center(child: CircularProgressIndicator()) : _error != null && _items.isEmpty ? ErrorState(_error!, onRetry: _load) : RefreshIndicator(
        onRefresh: _load,
        child: _items.isEmpty ? const EmptyList('Nothing matches.', icon: Icons.fact_check_outlined) : ListView(controller: _scroll, padding: const EdgeInsets.fromLTRB(16, 0, 16, 32), children: [
          Padding(padding: const EdgeInsets.only(top: 6), child: Text('$_total entries', style: TextStyle(fontSize: 12, color: Theme.of(context).hintColor))),
          ...rows,
          if (_more) const Padding(padding: EdgeInsets.all(16), child: Center(child: CircularProgressIndicator())),
        ]),
      )),
    ]);
  }

  void _detail(Json j) {
    final st = _auditStyle(_s(j, 'action'));
    showModalBottomSheet<void>(context: context, isScrollControlled: true, showDragHandle: true, builder: (c) => SafeArea(child: SingleChildScrollView(padding: const EdgeInsets.fromLTRB(20, 0, 20, 20), child: Column(crossAxisAlignment: CrossAxisAlignment.start, mainAxisSize: MainAxisSize.min, children: [
      Row(children: [CircleAvatar(backgroundColor: st.color.withValues(alpha: 0.12), child: Icon(st.icon, color: st.color)), const SizedBox(width: 12), Expanded(child: Text(_s(j, 'action'), style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w800)))]),
      const SizedBox(height: 12),
      Facts([('When', fmtDateTime(j['at'])), ('Who', _s(j, 'userName').isEmpty ? humanize(_s(j, 'actorType')) : '${_s(j, 'userName')} (${_s(j, 'actorType')})'), ('Company', _s(j, 'clientName')), ('Detail', _s(j, 'detail')), ('Record', [_s(j, 'entity'), _s(j, 'entityId')].where((x) => x.isNotEmpty).join(' ')), ('From address', _s(j, 'ipAddress'))]),
      const SizedBox(height: 8),
      OutlinedButton.icon(onPressed: () { Clipboard.setData(ClipboardData(text: j.entries.where((e) => e.value != null).map((e) => '${e.key}: ${e.value}').join('\n'))); toast(context, 'Copied.'); }, icon: const Icon(Icons.copy, size: 18), label: const Text('Copy')),
    ]))));
  }
}

class _AuditTile extends StatelessWidget {
  const _AuditTile(this.j, {required this.onTap});
  final Json j;
  final VoidCallback onTap;
  @override
  Widget build(BuildContext context) {
    final st = _auditStyle(_s(j, 'action'));
    final who = _s(j, 'userName').isEmpty ? humanize(_s(j, 'actorType')) : _s(j, 'userName');
    return Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: AppCard(
        onTap: onTap,
        padding: const EdgeInsets.all(12),
        child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
          CircleAvatar(radius: 18, backgroundColor: st.color.withValues(alpha: 0.12), child: Icon(st.icon, size: 18, color: st.color)),
          const SizedBox(width: 12),
          Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Row(children: [Expanded(child: Text(_s(j, 'action'), style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 14), maxLines: 1, overflow: TextOverflow.ellipsis)), const SizedBox(width: 8), Text(_ago(j['at']), style: TextStyle(fontSize: 11, color: Theme.of(context).hintColor))]),
            if (_s(j, 'detail').isNotEmpty) Padding(padding: const EdgeInsets.only(top: 2), child: Text(_s(j, 'detail'), style: TextStyle(fontSize: 12.5, color: Theme.of(context).hintColor), maxLines: 2, overflow: TextOverflow.ellipsis)),
            const SizedBox(height: 6),
            Wrap(spacing: 6, runSpacing: 4, children: [Badge2(who, tone: Tone.neutral), if (_s(j, 'clientName').isNotEmpty) Badge2(_s(j, 'clientName'), tone: Tone.info)]),
          ])),
        ]),
      ),
    );
  }
}

// ---- system errors -------------------------------------------------------------------------------------------------

Color _sevColor(String s) => switch (s) { 'Critical' => const Color(0xFFB91C1C), 'Error' => const Color(0xFFDC2626), 'Warning' => const Color(0xFFD97706), _ => const Color(0xFF0284C7) };
IconData _sevIcon(String s) => switch (s) { 'Critical' => Icons.local_fire_department_outlined, 'Error' => Icons.error_outline, 'Warning' => Icons.warning_amber_rounded, _ => Icons.info_outline };

/// Errors the system caught, grouped by cause: how many are open, how bad they are, and one tap to read the details, copy the stack trace and mark them fixed.
class ErrorLogsScreen extends StatefulWidget {
  const ErrorLogsScreen({super.key});
  @override
  State<ErrorLogsScreen> createState() => _ErrorLogsScreenState();
}

class _ErrorLogsScreenState extends State<ErrorLogsScreen> {
  final _items = <Json>[];
  final _search = TextEditingController();
  final _scroll = ScrollController();
  Json _stats = {};
  String _severity = '', _resolved = 'false';
  int _page = 1, _total = 0;
  bool _loading = true, _more = false;
  Object? _error;

  @override
  void initState() {
    super.initState();
    _scroll.addListener(() { if (_scroll.position.pixels > _scroll.position.maxScrollExtent - 300 && !_more && _items.length < _total) _load(more: true); });
    _load();
  }

  @override
  void dispose() { _scroll.dispose(); _search.dispose(); super.dispose(); }

  Future<void> _load({bool more = false}) async {
    final api = context.read<Api>();
    if (more) { setState(() => _more = true); _page++; } else { setState(() { _loading = _items.isEmpty; _page = 1; _error = null; }); }
    try {
      final r = await api.getMap('/api/v1/system-logs', query: {'search': _search.text.trim(), 'severity': _severity, 'resolved': _resolved, 'page': _page, 'pageSize': 25});
      final list = ((r['items'] ?? []) as List).map((e) => Map<String, dynamic>.from(e)).toList();
      if (!mounted) return;
      setState(() { if (!more) _items.clear(); _items.addAll(list); _total = (r['total'] ?? 0) as int; _stats = Map<String, dynamic>.from((r['stats'] ?? {}) as Map); _loading = false; _more = false; });
    } catch (e) {
      if (mounted) setState(() { _error = e; _loading = false; _more = false; if (more) _page--; });
    }
  }

  Future<void> _bulk(String label, String method, String path, String done) async {
    if (!await confirm(context, label, ok: 'Yes', danger: method == 'DELETE')) return;
    if (!mounted) return;
    final api = context.read<Api>();
    final ok = await guarded(context, () async { method == 'DELETE' ? await api.delete(path) : await api.post(path); }, done: done);
    if (ok) _load();
  }

  @override
  Widget build(BuildContext context) {
    final can = context.watch<AuthController>().can('logs.manage');
    return Column(children: [
      Padding(padding: const EdgeInsets.fromLTRB(16, 12, 16, 0), child: ResponsiveGrid(minTileWidth: 120, spacing: 10, children: [
        Kpi('Open', '${_stats['unresolved'] ?? 0}', icon: Icons.error_outline, color: (_stats['unresolved'] ?? 0) > 0 ? const Color(0xFFDC2626) : null),
        Kpi('Critical', '${_stats['criticalUnresolved'] ?? 0}', icon: Icons.local_fire_department_outlined, color: (_stats['criticalUnresolved'] ?? 0) > 0 ? const Color(0xFFB91C1C) : null),
        Kpi('Today', '${_stats['today'] ?? 0}', icon: Icons.today_outlined),
        Kpi('All time', '${_stats['total'] ?? 0}', icon: Icons.history),
      ])),
      Padding(padding: const EdgeInsets.fromLTRB(16, 12, 16, 4), child: TextField(controller: _search, textInputAction: TextInputAction.search, onSubmitted: (_) => _load(), decoration: InputDecoration(hintText: 'Search errors…', prefixIcon: const Icon(Icons.search), suffixIcon: _search.text.isEmpty ? null : IconButton(icon: const Icon(Icons.clear), onPressed: () { _search.clear(); _load(); })))),
      SingleChildScrollView(scrollDirection: Axis.horizontal, padding: const EdgeInsets.symmetric(horizontal: 12), child: Row(children: [
        for (final e in const [('false', 'Open'), ('true', 'Fixed'), ('', 'All')]) Padding(padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 6), child: ChoiceChip(label: Text(e.$2), selected: _resolved == e.$1, onSelected: (_) { setState(() => _resolved = e.$1); _load(); })),
        const Padding(padding: EdgeInsets.symmetric(horizontal: 6, vertical: 12), child: VerticalDivider(width: 1)),
        for (final s in const ['Critical', 'Error', 'Warning', 'Info']) Padding(padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 6), child: FilterChip(avatar: Icon(_sevIcon(s), size: 16, color: _sevColor(s)), label: Text(s), selected: _severity == s, onSelected: (on) { setState(() => _severity = on ? s : ''); _load(); })),
      ])),
      if (can) Padding(padding: const EdgeInsets.fromLTRB(16, 0, 16, 0), child: Wrap(spacing: 8, children: [
        TextButton.icon(onPressed: () => _bulk('Mark every open error as fixed?', 'POST', '/api/v1/system-logs/resolve-all', 'All marked as fixed.'), icon: const Icon(Icons.done_all, size: 18), label: const Text('Mark all fixed')),
        TextButton.icon(onPressed: () => _bulk('Delete every error already marked fixed?', 'DELETE', '/api/v1/system-logs/resolved', 'Deleted.'), icon: const Icon(Icons.delete_sweep_outlined, size: 18), label: const Text('Clear fixed')),
      ])),
      Expanded(child: _loading ? const Center(child: CircularProgressIndicator()) : _error != null && _items.isEmpty ? ErrorState(_error!, onRetry: _load) : RefreshIndicator(
        onRefresh: _load,
        child: _items.isEmpty ? const EmptyList('No errors here. All clear.', icon: Icons.verified_outlined) : ListView.builder(
          controller: _scroll, padding: const EdgeInsets.fromLTRB(16, 4, 16, 32), itemCount: _items.length + (_more ? 1 : 0),
          itemBuilder: (c, i) {
            if (i >= _items.length) return const Padding(padding: EdgeInsets.all(16), child: Center(child: CircularProgressIndicator()));
            return _ErrorTile(_items[i], onTap: () async { final changed = await Navigator.push<bool>(context, MaterialPageRoute(builder: (_) => ErrorDetailScreen(_items[i]))); if (changed == true) _load(); });
          },
        ),
      )),
    ]);
  }
}

class _ErrorTile extends StatelessWidget {
  const _ErrorTile(this.j, {required this.onTap});
  final Json j;
  final VoidCallback onTap;
  @override
  Widget build(BuildContext context) {
    final sev = _s(j, 'severity');
    final c = _sevColor(sev);
    final fixed = j['isResolved'] == true;
    return Padding(
      padding: const EdgeInsets.only(bottom: 10),
      child: Card(
        clipBehavior: Clip.antiAlias,
        child: InkWell(
          onTap: onTap,
          child: IntrinsicHeight(child: Row(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
            Container(width: 5, color: fixed ? Colors.grey : c),
            Expanded(child: Padding(padding: const EdgeInsets.all(12), child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Wrap(spacing: 6, runSpacing: 4, crossAxisAlignment: WrapCrossAlignment.center, children: [Icon(_sevIcon(sev), size: 18, color: c), Text(sev, style: TextStyle(color: c, fontWeight: FontWeight.w800, fontSize: 12)), if (fixed) const Badge2('Fixed', tone: Tone.ok) else if ((j['occurrences'] ?? 1) > 1) Badge2('${j['occurrences']}× seen', tone: Tone.warn)]),
              const SizedBox(height: 6),
              Text(_s(j, 'message'), maxLines: 3, overflow: TextOverflow.ellipsis, style: const TextStyle(fontWeight: FontWeight.w600, fontSize: 14)),
              const SizedBox(height: 6),
              Text([_s(j, 'method'), _s(j, 'path')].where((x) => x.isNotEmpty).join(' '), maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 11.5, fontFamily: 'monospace', color: Theme.of(context).hintColor)),
              Text('${_ago(j['lastSeenAt'])}${_s(j, 'clientName').isNotEmpty ? ' · ${_s(j, 'clientName')}' : ''}', style: TextStyle(fontSize: 11.5, color: Theme.of(context).hintColor)),
            ]))),
          ])),
        ),
      ),
    );
  }
}

class ErrorDetailScreen extends StatefulWidget {
  const ErrorDetailScreen(this.e, {super.key});
  final Json e;
  @override
  State<ErrorDetailScreen> createState() => _ErrorDetailScreenState();
}

class _ErrorDetailScreenState extends State<ErrorDetailScreen> {
  Json? _full;
  bool _trace = false;

  @override
  void initState() { super.initState(); _loadFull(); }

  Future<void> _loadFull() async {
    try { final f = await context.read<Api>().getMap('/api/v1/system-logs/${widget.e['id']}'); if (mounted) setState(() => _full = f); } catch (_) { /* the list row is shown */ }
  }

  Json get e => _full ?? widget.e;

  Future<void> _resolve() async {
    final note = await promptText(context, 'What fixed it? (optional)', hint: 'Restarted the service', ok: 'Mark as fixed');
    if (note == null || !mounted) return;
    final api = context.read<Api>();
    final nav = Navigator.of(context);
    final ok = await guarded(context, () async { await api.put('/api/v1/system-logs/${e['id']}/resolve', body: {'resolution': note.trim().isEmpty ? null : note.trim()}); }, done: 'Marked as fixed.');
    if (ok) nav.pop(true);
  }

  Future<void> _delete() async {
    if (!await confirm(context, 'Delete this entry?', ok: 'Delete', danger: true) || !mounted) return;
    final api = context.read<Api>();
    final nav = Navigator.of(context);
    if (await guarded(context, () async { await api.delete('/api/v1/system-logs/${e['id']}'); }, done: 'Deleted.')) nav.pop(true);
  }

  @override
  Widget build(BuildContext context) {
    final can = context.watch<AuthController>().can('logs.manage');
    final sev = _s(e, 'severity');
    final c = _sevColor(sev);
    final trace = _s(e, 'stackTrace');
    return Scaffold(
      appBar: AppBar(title: const Text('Error'), actions: [IconButton(tooltip: 'Copy everything', icon: const Icon(Icons.copy), onPressed: () { Clipboard.setData(ClipboardData(text: e.entries.where((x) => x.value != null).map((x) => '${x.key}: ${x.value}').join('\n'))); toast(context, 'Copied.'); })]),
      body: ListView(padding: const EdgeInsets.all(16), children: [
        Container(
          padding: const EdgeInsets.all(16),
          decoration: BoxDecoration(color: c.withValues(alpha: 0.1), borderRadius: BorderRadius.circular(16), border: Border.all(color: c.withValues(alpha: 0.3))),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Row(children: [Icon(_sevIcon(sev), color: c), const SizedBox(width: 8), Text(sev, style: TextStyle(color: c, fontWeight: FontWeight.w800)), const Spacer(), if (e['isResolved'] == true) const Badge2('Fixed', tone: Tone.ok)]),
            const SizedBox(height: 8),
            SelectableText(_s(e, 'message'), style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w700)),
            if (_s(e, 'exceptionType').isNotEmpty) Padding(padding: const EdgeInsets.only(top: 6), child: Text(_s(e, 'exceptionType'), style: TextStyle(fontFamily: 'monospace', fontSize: 12, color: Theme.of(context).hintColor))),
          ]),
        ),
        const SectionTitle('Where and who'),
        AppCard(child: Facts([
          ('Request', [_s(e, 'method'), _s(e, 'path')].where((x) => x.isNotEmpty).join(' ')), ('Status', _s(e, 'statusCode')), ('Company', _s(e, 'clientName')), ('User', _s(e, 'userName')), ('From', _s(e, 'ipAddress')),
          ('Seen', '${e['occurrences'] ?? 1} time(s)'), ('First', fmtDateTime(e['createdAt'])), ('Last', fmtDateTime(e['lastSeenAt'])), ('Reference', _s(e, 'uuid')), ('Trace', _s(e, 'traceId')), ('Machine', _s(e, 'machineName')), ('Environment', _s(e, 'environment')),
        ])),
        if (e['isResolved'] == true) ...[const SectionTitle('Resolution'), AppCard(child: Facts([('Fixed by', _s(e, 'resolvedBy')), ('When', fmtDateTime(e['resolvedAt'])), ('Note', _s(e, 'resolution'))]))],
        if (trace.isNotEmpty) ...[
          SectionTitle('Stack trace', trailing: TextButton(onPressed: () => setState(() => _trace = !_trace), child: Text(_trace ? 'Hide' : 'Show'))),
          if (_trace) AppCard(padding: const EdgeInsets.all(12), child: SelectableText(trace, style: const TextStyle(fontFamily: 'monospace', fontSize: 11.5, height: 1.4))),
        ],
        const SizedBox(height: 16),
        if (can) Wrap(spacing: 8, runSpacing: 8, children: [
          if (e['isResolved'] != true) FilledButton.icon(onPressed: _resolve, icon: const Icon(Icons.check), label: const Text('Mark as fixed')),
          OutlinedButton.icon(style: OutlinedButton.styleFrom(foregroundColor: Colors.red.shade700), onPressed: _delete, icon: const Icon(Icons.delete_outline), label: const Text('Delete')),
        ]),
        const SizedBox(height: 24),
      ]),
    );
  }
}
