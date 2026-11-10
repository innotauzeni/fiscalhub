import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/api.dart';
import '../core/auth.dart';
import '../core/models.dart';
import '../ui/kit.dart';
import '../ui/lists.dart';
import '../ui/premium.dart';
import 'devices.dart';

/// Fiscal days: the state of each device's day with open/close buttons, and the recent history.
class DaysScreen extends StatefulWidget {
  const DaysScreen({super.key});
  @override
  State<DaysScreen> createState() => _DaysScreenState();
}

class _DaysScreenState extends State<DaysScreen> {
  final _key = GlobalKey<LoaderState<(List<Json>, Map<String, Json?>, List<Json>)>>();
  bool _busy = false;

  Future<(List<Json>, Map<String, Json?>, List<Json>)> _load() async {
    final api = context.read<Api>();
    final devices = await loadDevices(api);
    final days = <String, Json?>{};
    for (final d in devices) {
      try { days[d['id']] = await api.getMap('/api/v1/devices/${d['id']}/day'); } catch (_) { days[d['id']] = null; }
    }
    var history = <Json>[];
    try {
      final now = DateTime.now();
      final r = await api.get('/api/v1/reports/fiscal-days', query: {'from': isoDay(now.subtract(const Duration(days: 30))), 'to': isoDay(now)});
      history = (r is Map ? (r['items'] ?? r['days'] ?? r.values.whereType<List>().firstOrNull ?? []) : r) is List ? ((r is Map ? (r['items'] ?? r['days'] ?? r.values.whereType<List>().firstOrNull ?? []) : r) as List).map((e) => Map<String, dynamic>.from(e)).toList() : <Json>[];
    } catch (_) {}
    return (devices, days, history);
  }

  Future<void> _do(String id, String verb, String done) async {
    setState(() => _busy = true);
    final ok = await guarded(context, () async { await context.read<Api>().post('/api/v1/devices/$id/day/$verb'); }, done: done);
    if (mounted) setState(() => _busy = false);
    if (ok) _key.currentState?.reload();
  }

  @override
  Widget build(BuildContext context) {
    final can = context.watch<AuthController>().can('day.manage');
    return Loader<(List<Json>, Map<String, Json?>, List<Json>)>(
      key: _key,
      load: _load,
      builder: (context, data, reload) {
        final (devices, days, history) = data;
        if (devices.isEmpty) return ListView(children: const [BigEmpty(icon: Icons.calendar_month_outlined, title: 'No devices yet', message: 'Register a fiscal device first; its fiscal days appear here.')]);
        final hint = Theme.of(context).hintColor;
        final open = devices.where((d) => dayIsOpen(days[d['id']]?['status']?.toString())).length;
        return ListView(padding: const EdgeInsets.fromLTRB(16, 12, 16, 24), children: [
          ResponsiveGrid(minTileWidth: 140, children: [
            Kpi('Days open', '$open / ${devices.length}', icon: Icons.lock_open_rounded, color: const Color(0xFF059669)),
            Kpi('Receipts on open days', '${devices.fold<num>(0, (a, d) => a + ((days[d['id']]?['lastReceiptCounter'] ?? 0) as num))}', icon: Icons.receipt_long_rounded, color: kBrand),
          ]),
          const ListHeader('Today, per device'),
          for (final d in devices)
            Builder(builder: (context) {
              final day = days[d['id']];
              final st = day?['status']?.toString();
              final isOpen = dayIsOpen(st);
              final c = toneColor(toneFor(st));
              return Padding(
                padding: const EdgeInsets.only(bottom: 12),
                child: Card(
                  clipBehavior: Clip.antiAlias,
                  child: IntrinsicHeight(child: Row(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
                    Container(width: 5, color: c),
                    Expanded(child: Padding(
                      padding: const EdgeInsets.all(14),
                      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                        Row(children: [
                          Avatar(deviceLabel(d), icon: Icons.memory_rounded, color: c == toneColor(Tone.neutral) ? kBrand : c),
                          const SizedBox(width: 12),
                          Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                            Text(deviceLabel(d), style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 15.5)),
                            Text('ZIMRA ${d['zimraDeviceId']}  ·  ${d['environment'] ?? ''}', style: TextStyle(fontSize: 12, color: hint)),
                          ])),
                          StatusDot(st),
                        ]),
                        const SizedBox(height: 12),
                        Wrap(spacing: 8, runSpacing: 6, children: [
                          if (day == null) const Pill('Status unavailable', tone: Tone.warn) else ...[
                            Pill('Day ${day['fiscalDayNo'] ?? '-'}', tone: Tone.info),
                            Pill('${day['lastReceiptCounter'] ?? 0} receipts'),
                            if (day['openedAt'] != null) Pill('Opened ${fmtDateTime(day['openedAt'])}'),
                          ],
                        ]),
                        if (can) Padding(padding: const EdgeInsets.only(top: 12), child: Row(children: [
                          if (!isOpen) Expanded(child: FilledButton.icon(onPressed: _busy ? null : () => _do(d['id'], 'open', 'Fiscal day opened.'), icon: const Icon(Icons.play_arrow_rounded), label: const Text('Open day'))),
                          if (isOpen) Expanded(child: OutlinedButton.icon(onPressed: _busy ? null : () async { if (await confirm(context, 'Close the fiscal day on ${deviceLabel(d)}?', ok: 'Close day')) _do(d['id'], 'close', 'Close requested.'); }, icon: const Icon(Icons.stop_rounded), label: const Text('Close day'))),
                        ])),
                      ]),
                    )),
                  ])),
                ),
              );
            }),
          if (history.isNotEmpty) ...[
            ListHeader('Last 30 days', trailing: '${history.length}'),
            ListPanel(children: [
              for (final h in history.take(40))
                ListRow(
                  leading: Avatar('${h['fiscalDayNo']}', icon: Icons.event_note_rounded, color: toneColor(toneFor(h['status']?.toString())) == toneColor(Tone.neutral) ? const Color(0xFF64748B) : toneColor(toneFor(h['status']?.toString()))),
                  title: 'Day ${h['fiscalDayNo']}  ·  device ${h['deviceZimraId']}',
                  subtitle: '${fmtDateTime(h['openedAt'])}${h['closedAt'] != null ? '  →  ${fmtDateTime(h['closedAt'])}' : ''}',
                  pills: [('${h['receipts'] ?? 0} receipts', Tone.neutral)],
                  status: h['status']?.toString(),
                  chevron: false,
                ),
            ]),
          ],
        ]);
      },
    );
  }
}
