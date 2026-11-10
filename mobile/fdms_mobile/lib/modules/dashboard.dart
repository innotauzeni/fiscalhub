import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/api.dart';
import '../core/auth.dart';
import '../core/models.dart';
import '../ui/kit.dart';
import '../ui/premium.dart';
import 'approvals.dart';
import 'devices.dart';
import 'receipts.dart';

/// A small bar chart of receipts per day (rejected ones in red). Its height follows its width, and every bar is sized by flex, so it fits any screen.
class TrendChart extends StatelessWidget {
  const TrendChart(this.points, {super.key});
  final List<Map<String, dynamic>> points;
  @override
  Widget build(BuildContext context) {
    if (points.isEmpty) return const SizedBox.shrink();
    final max = points.map((p) => ((p['count'] as num?) ?? 0).toDouble()).fold<double>(1, (a, b) => b > a ? b : a);
    int flex(num v) => (v / max * 1000).round().clamp(0, 1000);
    return AspectRatio(
      aspectRatio: 3.2,
      child: Row(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
        for (final p in points)
          Expanded(
            child: Tooltip(
              message: '${fmtDate(p['date'])}: ${p['count']} receipts${((p['rejected'] ?? 0) as num) > 0 ? ', ${p['rejected']} rejected' : ''}',
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 3),
                child: Builder(builder: (context) {
                  final count = ((p['count'] ?? 0) as num), rej = ((p['rejected'] ?? 0) as num).clamp(0, count);
                  final good = flex(count - rej), bad = flex(rej);
                  return Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
                    Expanded(flex: (1000 - good - bad).clamp(1, 1000), child: const SizedBox.shrink()),
                    if (bad > 0) Expanded(flex: bad, child: const DecoratedBox(decoration: BoxDecoration(color: Color(0xFFF87171), borderRadius: BorderRadius.vertical(top: Radius.circular(3))))),
                    Expanded(flex: good < 3 ? 3 : good, child: DecoratedBox(decoration: BoxDecoration(gradient: const LinearGradient(colors: [Color(0xFF6366F1), Color(0xFF2563EB)], begin: Alignment.topCenter, end: Alignment.bottomCenter), borderRadius: BorderRadius.vertical(top: Radius.circular(bad > 0 ? 0 : 5))))),
                  ]);
                }),
              ),
            ),
          ),
      ]),
    );
  }
}

/// Home for a company: a greeting with today's sales, what needs attention, the key numbers, the last days, devices and the latest receipts.
class DashboardScreen extends StatelessWidget {
  const DashboardScreen({super.key});

  static String greeting() {
    final h = DateTime.now().hour;
    return h < 12 ? 'Good morning' : h < 17 ? 'Good afternoon' : 'Good evening';
  }

  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    final auth = context.watch<AuthController>();
    return Loader<Json>(
      load: () => api.getMap('/api/v1/dashboard'),
      builder: (context, d, reload) {
        final today = ((d['todayTotals'] ?? []) as List).cast<Map<String, dynamic>>();
        final devices = ((d['deviceList'] ?? []) as List).cast<Map<String, dynamic>>();
        final recent = ((d['recent'] ?? []) as List).cast<Map<String, dynamic>>();
        final alerts = ((d['alerts'] ?? []) as List).map((e) => e.toString()).toList();
        final trend = ((d['trend'] ?? []) as List).cast<Map<String, dynamic>>();
        final check = d['checklist'] as Map?;
        final usage = d['usage'] as Map?;
        final hint = Theme.of(context).hintColor;
        final first = today.isEmpty ? null : today.first;
        final trendTotal = trend.fold<num>(0, (a, p) => a + ((p['count'] ?? 0) as num));
        return ListView(padding: const EdgeInsets.fromLTRB(16, 12, 16, 24), children: [
          // hero
          Container(
            padding: const EdgeInsets.all(20),
            decoration: BoxDecoration(
              gradient: heroGradient(),
              borderRadius: BorderRadius.circular(24),
              boxShadow: [BoxShadow(color: const Color(0xFF4F46E5).withValues(alpha: 0.3), blurRadius: 24, offset: const Offset(0, 10))],
            ),
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Text('${greeting()}, ${auth.session?.user.name ?? ''}', style: TextStyle(color: Colors.white.withValues(alpha: 0.85), fontSize: 14, fontWeight: FontWeight.w600)),
              const SizedBox(height: 14),
              Text('SALES TODAY', style: TextStyle(color: Colors.white.withValues(alpha: 0.7), fontSize: 11, fontWeight: FontWeight.w800, letterSpacing: 1)),
              FittedBox(fit: BoxFit.scaleDown, alignment: Alignment.centerLeft, child: Text(first == null ? '0.00' : moneyIn(first['total'], first['currency']), style: const TextStyle(color: Colors.white, fontSize: 34, fontWeight: FontWeight.w900, letterSpacing: -1))),
              for (final t in today.skip(1)) Text('+ ${moneyIn(t['total'], t['currency'])}', style: TextStyle(color: Colors.white.withValues(alpha: 0.85), fontWeight: FontWeight.w700)),
              const SizedBox(height: 12),
              Wrap(spacing: 8, runSpacing: 8, children: [
                HeroChip('${d['receiptsToday'] ?? 0} receipts today', icon: Icons.receipt_long_rounded),
                HeroChip('${d['openDays'] ?? 0} of ${d['devices'] ?? 0} days open', icon: Icons.lock_open_rounded),
                if (d['autoCloseTime'] != null) HeroChip('Auto-close ${d['autoCloseTime']}', icon: Icons.schedule_rounded),
              ]),
              if (auth.can('receipts.submit')) ...[
                const SizedBox(height: 16),
                Row(children: [
                  Expanded(child: FilledButton.icon(
                    style: FilledButton.styleFrom(backgroundColor: Colors.white, foregroundColor: const Color(0xFF3730A3)),
                    onPressed: () async { final r = await Navigator.push<bool>(context, MaterialPageRoute(builder: (_) => const NewInvoiceScreen())); if (r == true) reload(); },
                    icon: const Icon(Icons.add_rounded), label: const Text('New invoice'),
                  )),
                ]),
              ],
            ]),
          ),
          const SizedBox(height: 16),
          const NotificationsPanel(),
          for (final a in alerts)
            Container(
              margin: const EdgeInsets.only(bottom: 10),
              padding: const EdgeInsets.all(14),
              decoration: BoxDecoration(color: const Color(0xFFF59E0B).withValues(alpha: 0.1), borderRadius: BorderRadius.circular(16), border: Border.all(color: const Color(0xFFF59E0B).withValues(alpha: 0.3))),
              child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                const Icon(Icons.warning_amber_rounded, size: 20, color: Color(0xFFD97706)),
                const SizedBox(width: 10),
                Expanded(child: Text(a, style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w600))),
              ]),
            ),
          ResponsiveGrid(minTileWidth: 150, children: [
            Kpi('Accepted by ZIMRA', '${d['submitted'] ?? 0}', icon: Icons.verified_rounded, color: const Color(0xFF059669), sub: 'all time'),
            Kpi('Pending', '${d['pending'] ?? 0}', icon: Icons.hourglass_top_rounded, color: (d['pending'] ?? 0) > 0 ? const Color(0xFFD97706) : const Color(0xFF64748B), sub: '${d['held'] ?? 0} held · ${d['offlineIssued'] ?? 0} offline'),
            Kpi('Rejected', '${d['rejected'] ?? 0}', icon: Icons.error_outline_rounded, color: (d['rejected'] ?? 0) > 0 ? const Color(0xFFDC2626) : const Color(0xFF64748B), sub: (d['rejected'] ?? 0) > 0 ? 'needs your attention' : 'all clear'),
            Kpi('Devices', '${d['devices'] ?? 0}', icon: Icons.memory_rounded, color: const Color(0xFF7C3AED), sub: '${d['openDays'] ?? 0} open fiscal days'),
          ]),
          if (trend.isNotEmpty) ...[
            const SizedBox(height: 14),
            InfoCard(
              title: 'Receipts, last ${trend.length} days', icon: Icons.bar_chart_rounded,
              trailing: Text('$trendTotal', style: const TextStyle(fontWeight: FontWeight.w800, color: kBrand)),
              children: [Padding(padding: const EdgeInsets.only(top: 8, bottom: 8), child: TrendChart(trend))],
            ),
          ],
          if (check != null && (check['done'] ?? 0) < (check['total'] ?? 0))
            InfoCard(title: 'Getting started', icon: Icons.rocket_launch_outlined, trailing: Text('${check['done']}/${check['total']}', style: const TextStyle(fontWeight: FontWeight.w800, color: kBrand)), children: [
              Padding(padding: const EdgeInsets.only(top: 4, bottom: 10), child: LinearProgressIndicator(value: ((check['done'] ?? 0) as num) / ((check['total'] ?? 1) as num), minHeight: 8, borderRadius: BorderRadius.circular(8), color: kBrand, backgroundColor: kBrand.withValues(alpha: 0.12))),
              for (final s in (check['steps'] as List))
                Padding(padding: const EdgeInsets.only(bottom: 10), child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  Icon(s['done'] == true ? Icons.check_circle_rounded : Icons.radio_button_unchecked_rounded, size: 22, color: s['done'] == true ? const Color(0xFF059669) : hint),
                  const SizedBox(width: 10),
                  Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    Text(s['title'] ?? '', style: TextStyle(fontWeight: FontWeight.w700, decoration: s['done'] == true ? TextDecoration.lineThrough : null, color: s['done'] == true ? hint : null)),
                    if (s['done'] != true) Text(s['help'] ?? '', style: TextStyle(fontSize: 12, color: hint)),
                  ])),
                ])),
            ]),
          if (usage != null && usage['limited'] == true)
            InfoCard(title: 'Plan usage · ${usage['plan'] ?? ''}', icon: Icons.data_usage_rounded, children: [
              for (final i in (usage['items'] as List))
                Padding(padding: const EdgeInsets.only(bottom: 10), child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  Row(children: [Expanded(child: Text(i['label'] ?? '', style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w600))), Text('${i['used']} / ${i['limit']}', style: TextStyle(fontSize: 12, color: hint))]),
                  const SizedBox(height: 6),
                  LinearProgressIndicator(value: ((i['percent'] ?? 0) as num).toDouble().clamp(0, 100) / 100, color: toneColor(i['level'] == 'over' ? Tone.bad : i['level'] == 'warn' ? Tone.warn : Tone.ok), minHeight: 7, borderRadius: BorderRadius.circular(6)),
                ])),
            ]),
          SectionTitle('Devices', trailing: Text('${devices.length}', style: TextStyle(color: hint, fontWeight: FontWeight.w700))),
          if (devices.isEmpty) const AppCard(child: Text('No devices yet. Register your first fiscal device from the Devices tab.')),
          for (final x in devices)
            Padding(padding: const EdgeInsets.only(bottom: 10), child: AppCard(
              onTap: () => Navigator.push(context, MaterialPageRoute(builder: (_) => DeviceDetailScreen(deviceId: x['id']))),
              padding: const EdgeInsets.all(14),
              child: Row(children: [
                Avatar(x['name']?.toString() ?? '', icon: Icons.memory_rounded, color: toneColor(toneFor(x['dayStatus']?.toString())) == toneColor(Tone.neutral) ? kBrand : toneColor(toneFor(x['dayStatus']?.toString()))),
                const SizedBox(width: 12),
                Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  Text(((x['name'] ?? '') as String).isNotEmpty ? x['name'] : 'Device ${x['zimraDeviceId']}', style: const TextStyle(fontWeight: FontWeight.w700)),
                  Text('${x['taxPayerName'] ?? ''}${x['branchName'] != null ? ' · ${x['branchName']}' : ''}', style: TextStyle(fontSize: 12, color: hint), overflow: TextOverflow.ellipsis),
                ])),
                Side(fraction: 0.36, children: [StatusDot(x['dayStatus']?.toString()), if ((x['pendingReceipts'] ?? 0) > 0) Padding(padding: const EdgeInsets.only(top: 4), child: Text('${x['pendingReceipts']} pending', style: const TextStyle(fontSize: 11, color: Color(0xFFD97706), fontWeight: FontWeight.w700)))]),
              ]),
            )),
          SectionTitle('Latest receipts', trailing: Text('${recent.length}', style: TextStyle(color: hint, fontWeight: FontWeight.w700))),
          if (recent.isEmpty) const AppCard(child: Text('No receipts yet.')),
          for (final r in recent.take(8))
            Padding(padding: const EdgeInsets.only(bottom: 10), child: AppCard(
              onTap: () => Navigator.push(context, MaterialPageRoute(builder: (_) => ReceiptDetailScreen(receiptId: r['id']))),
              padding: const EdgeInsets.all(14),
              child: Row(children: [
                Avatar(r['invoiceNo']?.toString() ?? '', icon: Icons.receipt_long_rounded, color: toneColor(toneFor(r['status']?.toString()))),
                const SizedBox(width: 12),
                Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [Text(r['invoiceNo'] ?? '', style: const TextStyle(fontWeight: FontWeight.w700), overflow: TextOverflow.ellipsis), Text(fmtDateTime(r['receiptDate']), style: TextStyle(fontSize: 12, color: hint))])),
                Side(children: [AmountBox(moneyIn(r['total'], r['currency']), style: const TextStyle(fontWeight: FontWeight.w800)), const SizedBox(height: 4), StatusDot(r['status']?.toString())]),
              ]),
            )),
        ]);
      },
    );
  }
}

/// Home for the platform operator: every company at a glance, and what needs attention.
class PlatformDashboardScreen extends StatelessWidget {
  const PlatformDashboardScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    final auth = context.watch<AuthController>();
    if (!auth.can('clients.manage')) return const EmptyState('Nothing to show here.');
    return Loader<Json>(
      load: () => api.getMap('/api/v1/dashboard/platform'),
      builder: (context, d, reload) {
        final top = ((d['topClients'] ?? []) as List).cast<Map<String, dynamic>>();
        final att = ((d['attention'] ?? []) as List).cast<Map<String, dynamic>>();
        final recent = ((d['recentActivity'] ?? []) as List).cast<Map<String, dynamic>>();
        final trend = ((d['trend'] ?? []) as List).cast<Map<String, dynamic>>();
        return ListView(padding: const EdgeInsets.all(16), children: [
          if (auth.can('revenue.view')) const _MoneyStrip(),
          const NotificationsPanel(title: 'Platform notifications', take: 6),
          ResponsiveGrid(minTileWidth: 150, children: [
            Kpi('Companies', '${d['activeClients'] ?? 0} / ${d['clients'] ?? 0}', icon: Icons.apartment, sub: '${d['users'] ?? 0} users'),
            Kpi('Devices', '${d['devices'] ?? 0}', icon: Icons.memory, sub: '${d['openDays'] ?? 0} open days'),
            Kpi('Receipts today', '${d['receiptsToday'] ?? 0}', icon: Icons.receipt_long_outlined),
            Kpi('Rejected / pending', '${d['rejected'] ?? 0} / ${d['pending'] ?? 0}', icon: Icons.error_outline, color: (d['rejected'] ?? 0) > 0 ? const Color(0xFFDC2626) : null),
          ]),
          if (att.isNotEmpty) ...[
            SectionTitle('Needs attention (${att.length})'),
            for (final a in att) Padding(padding: const EdgeInsets.only(bottom: 8), child: AppCard(child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [Icon(a['severity'] == 'bad' ? Icons.error_outline : Icons.warning_amber_outlined, color: toneColor(a['severity'] == 'bad' ? Tone.bad : Tone.warn)), const SizedBox(width: 10), Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [Text(a['title'] ?? '', style: const TextStyle(fontWeight: FontWeight.w700)), Text('${a['clientName']} · ${a['detail'] ?? ''}', style: TextStyle(fontSize: 12, color: Theme.of(context).hintColor))]))]))),
          ],
          if (trend.isNotEmpty) ...[const SectionTitle('Receipts per day'), AppCard(child: TrendChart(trend))],
          const SectionTitle('Busiest companies this week'),
          AppCard(child: Column(children: [for (final t in top) Padding(padding: const EdgeInsets.symmetric(vertical: 4), child: Row(children: [Expanded(child: Text(t['name'] ?? '')), Text('${t['receiptsWeek']} receipts · ${t['devices']} dev', style: const TextStyle(fontSize: 12))])), if (top.isEmpty) const Text('No activity yet.')])),
          const SectionTitle('Recent activity'),
          AppCard(child: Column(children: [for (final a in recent.take(10)) Padding(padding: const EdgeInsets.symmetric(vertical: 4), child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [Expanded(child: Text('${a['action']}${a['clientName'] != null ? ' · ${a['clientName']}' : ''}', style: const TextStyle(fontSize: 13))), Text(fmtDateTime(a['at']), style: TextStyle(fontSize: 11, color: Theme.of(context).hintColor))])), if (recent.isEmpty) const Text('Nothing yet.')])),
          const SizedBox(height: 24),
        ]);
      },
    );
  }
}

/// The platform owner's money at a glance on the home screen: recurring revenue, this month's takings and payments to confirm. Tapping opens Revenue.
class _MoneyStrip extends StatefulWidget {
  const _MoneyStrip();
  @override
  State<_MoneyStrip> createState() => _MoneyStripState();
}

class _MoneyStripState extends State<_MoneyStrip> {
  late final Future<Json> _load = context.read<Api>().getMap('/api/v1/admin/revenue');

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<Json>(
      future: _load,
      builder: (context, snap) {
        final r = snap.data;
        if (r == null) return const SizedBox.shrink();
        final c = Map<String, dynamic>.from((r['counts'] ?? {}) as Map);
        final opener = context.read<RouteOpener?>();
        return Padding(
          padding: const EdgeInsets.only(bottom: 14),
          child: Material(
            color: Colors.transparent,
            child: InkWell(
              borderRadius: BorderRadius.circular(24),
              onTap: opener == null ? null : () => opener.open('admin/revenue'),
              child: Ink(
                padding: const EdgeInsets.all(18),
                decoration: BoxDecoration(
                  gradient: const LinearGradient(colors: [Color(0xFF064E3B), Color(0xFF0F766E), Color(0xFF0369A1)], begin: Alignment.topLeft, end: Alignment.bottomRight),
                  borderRadius: BorderRadius.circular(24),
                ),
                child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  Text('RECURRING REVENUE / MONTH', style: TextStyle(color: Colors.white.withValues(alpha: 0.75), fontSize: 11, fontWeight: FontWeight.w800, letterSpacing: 1)),
                  FittedBox(fit: BoxFit.scaleDown, alignment: Alignment.centerLeft, child: Text(moneyList(r['mrr']), style: const TextStyle(color: Colors.white, fontSize: 28, fontWeight: FontWeight.w900))),
                  const SizedBox(height: 10),
                  Wrap(spacing: 8, runSpacing: 8, children: [
                    HeroChip('This month ${moneyList(r['thisMonth'])}', icon: Icons.payments_outlined),
                    HeroChip('${c['awaitingConfirmation'] ?? 0} payments to confirm', icon: Icons.fact_check_outlined),
                    HeroChip('${c['paying'] ?? 0} paying · ${c['trial'] ?? 0} on trial', icon: Icons.apartment_rounded),
                  ]),
                ]),
              ),
            ),
          ),
        );
      },
    );
  }
}
