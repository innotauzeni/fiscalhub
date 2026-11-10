import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:provider/provider.dart';

import '../core/api.dart';
import '../core/config.dart';
import '../core/models.dart';
import '../screens/profile.dart' show guardedValue;
import '../ui/files.dart';
import '../ui/kit.dart';
import '../ui/lists.dart';
import '../ui/premium.dart';
import 'resource.dart';

String _s(Map j, String k) => (j[k] ?? '').toString();
String _amounts(dynamic list) => ((list ?? []) as List).isEmpty ? '0.00' : (list as List).map((o) => moneyIn(o['amount'], o['currency'])).join(' + ');
String _n(num v) => v.toInt().toString().replaceAllMapped(RegExp(r'(\d)(?=(\d{3})+$)'), (m) => '${m[1]},');

const _kinds = {'free_days': 'Extra days', 'extra_device': 'Extra devices', 'extra_user': 'Extra users', 'extra_branch': 'Extra branches', 'pay_invoice': 'Pay an invoice'};
IconData _kindIcon(String k) => switch (k) { 'free_days' => Icons.event_available_rounded, 'extra_device' => Icons.memory_rounded, 'extra_user' => Icons.person_add_alt_rounded, 'extra_branch' => Icons.store_rounded, 'pay_invoice' => Icons.receipt_long_rounded, _ => Icons.card_giftcard_rounded };

/// Refer and earn: the company's code and link, its coins and what they buy (or its money rewards), the companies it brought, and its levels.
class ReferralScreen extends StatefulWidget {
  const ReferralScreen({super.key});
  @override
  State<ReferralScreen> createState() => _ReferralScreenState();
}

class _ReferralScreenState extends State<ReferralScreen> {
  final _key = GlobalKey<LoaderState<Json>>();
  bool _busy = false;

  Future<void> _redeem(Json d, Map item) async {
    final invoices = ((d['payableInvoices'] ?? []) as List).cast<Map>();
    Map? invoice;
    if (item['kind'] == 'pay_invoice') {
      if (invoices.isEmpty) { toast(context, 'You have no open invoice to pay.'); return; }
      invoice = invoices.length == 1 ? invoices.first : await showModalBottomSheet<Map>(context: context, showDragHandle: true, builder: (c) => SafeArea(child: ListView(shrinkWrap: true, padding: const EdgeInsets.fromLTRB(16, 0, 16, 16), children: [
            const Text('Which invoice?', style: TextStyle(fontSize: 18, fontWeight: FontWeight.w800)),
            const SizedBox(height: 10),
            for (final i in invoices) ListTile(contentPadding: EdgeInsets.zero, leading: const Icon(Icons.receipt_long_rounded, color: kBrand), title: Text(_s(i, 'number')), subtitle: Text(moneyIn(i['total'], _s(i, 'currency'))), trailing: Text('${_n(i['coins'] ?? 0)} coins', style: const TextStyle(fontWeight: FontWeight.w800)), onTap: () => Navigator.pop(c, i)),
          ])));
      if (invoice == null) return;
    }
    final cost = invoice != null ? (invoice['coins'] ?? 0) as num : (item['cost'] ?? 0) as num;
    if (!mounted || !await confirm(context, '${_s(item, 'title')}${invoice != null ? ' ${_s(invoice, 'number')}' : ''} for ${_n(cost)} coins?', ok: 'Spend coins')) return;
    if (!mounted) return;
    setState(() => _busy = true);
    final r = await guardedValue(context, () => context.read<Api>().post('/api/v1/referrals/redeem', body: {'key': item['key'], 'invoiceId': invoice?['id']}));
    if (mounted) setState(() => _busy = false);
    if (r != null && mounted) { toast(context, _s(r, 'message')); _key.currentState?.reload(); }
  }

  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    final base = context.read<AppConfig>().baseUrl;
    return Loader<Json>(
      key: _key,
      load: () => api.getMap('/api/v1/referrals'),
      builder: (context, d, reload) {
        final coins = d['rewardType'] != 'money';
        final balance = (d['coins'] ?? 0) as num;
        final tiers = ((d['tiers'] ?? []) as List).cast<Map<String, dynamic>>();
        final companies = ((d['companies'] ?? []) as List).cast<Map<String, dynamic>>();
        final rewards = ((d['rewards'] ?? []) as List).cast<Map<String, dynamic>>();
        final shop = ((d['shop'] ?? []) as List).cast<Map<String, dynamic>>();
        final history = ((d['coinHistory'] ?? []) as List).cast<Map<String, dynamic>>();
        final invoices = ((d['payableInvoices'] ?? []) as List).cast<Map>();
        final bonus = (d['bonus'] ?? {}) as Map;
        final link = _s(d, 'shareLink').isNotEmpty ? _s(d, 'shareLink') : '$base/app${d['referralPath']}';
        final paying = (d['paying'] ?? 0) as int;
        Map<String, dynamic>? next;
        for (final t in tiers) { if (t['reached'] != true) { next = t; break; } }
        final hint = Theme.of(context).hintColor;
        return ListView(padding: const EdgeInsets.fromLTRB(16, 12, 16, 28), children: [
          if (d['enabled'] != true) Container(margin: const EdgeInsets.only(bottom: 12), padding: const EdgeInsets.all(14), decoration: BoxDecoration(color: Colors.amber.withValues(alpha: 0.15), borderRadius: BorderRadius.circular(14)), child: const Text('The referral program is switched off at the moment.')),
          Container(
            padding: const EdgeInsets.all(20),
            decoration: BoxDecoration(gradient: heroGradient(), borderRadius: BorderRadius.circular(24), boxShadow: [BoxShadow(color: const Color(0xFF4F46E5).withValues(alpha: 0.28), blurRadius: 22, offset: const Offset(0, 10))]),
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  Text(coins ? 'YOUR COINS' : 'REFER A COMPANY, EARN', style: TextStyle(color: Colors.white.withValues(alpha: 0.75), fontSize: 11, fontWeight: FontWeight.w800, letterSpacing: 1)),
                  FittedBox(fit: BoxFit.scaleDown, alignment: Alignment.centerLeft, child: Text(coins ? _n(balance) : '${d['percent']}% of what they pay', style: const TextStyle(color: Colors.white, fontSize: 32, fontWeight: FontWeight.w900, letterSpacing: -0.5))),
                  if (coins) Text('${d['percent']}% back in coins · ${d['coinsPerUnit']} coins = 1.00', style: TextStyle(color: Colors.white.withValues(alpha: 0.82), fontSize: 13)),
                ])),
                Container(padding: const EdgeInsets.all(12), decoration: BoxDecoration(color: Colors.white.withValues(alpha: 0.16), borderRadius: BorderRadius.circular(16)), child: Icon(coins ? Icons.toll_rounded : Icons.card_giftcard_rounded, color: Colors.white, size: 28)),
              ]),
              if ((d['description'] ?? '').toString().isNotEmpty) Padding(padding: const EdgeInsets.only(top: 10), child: Text(d['description'], style: TextStyle(color: Colors.white.withValues(alpha: 0.8), fontSize: 13, height: 1.35))),
              const SizedBox(height: 16),
              Container(
                padding: const EdgeInsets.fromLTRB(14, 10, 6, 10),
                decoration: BoxDecoration(color: Colors.white.withValues(alpha: 0.16), borderRadius: BorderRadius.circular(16)),
                child: Row(children: [
                  Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [Text('Your code', style: TextStyle(color: Colors.white.withValues(alpha: 0.75), fontSize: 12)), SelectableText(_s(d, 'code'), style: const TextStyle(color: Colors.white, fontSize: 22, fontWeight: FontWeight.w900, letterSpacing: 2))])),
                  IconButton(tooltip: 'Copy', onPressed: () { Clipboard.setData(ClipboardData(text: _s(d, 'code'))); toast(context, 'Code copied.'); }, icon: const Icon(Icons.copy_rounded, color: Colors.white)),
                ]),
              ),
              const SizedBox(height: 12),
              SizedBox(width: double.infinity, child: FilledButton.icon(style: FilledButton.styleFrom(backgroundColor: Colors.white, foregroundColor: const Color(0xFF3730A3)), onPressed: () => shareText(context, 'Fiscalise your invoices with FDMS Gateway. Register with my code ${_s(d, 'code')}: $link'), icon: const Icon(Icons.share_rounded), label: const Text('Share my link'))),
            ]),
          ),
          const SizedBox(height: 14),
          ResponsiveGrid(minTileWidth: 140, children: [
            Kpi('Referred', '${d['referred']}', icon: Icons.group_add_rounded, sub: '${d['pending']} waiting'),
            Kpi('Paying', '$paying', icon: Icons.verified_rounded, color: const Color(0xFF059669)),
            if (coins) ...[
              Kpi('Coins earned', _n(history.where((h) => (h['amount'] ?? 0) > 0).fold<num>(0, (a, h) => a + (h['amount'] as num))), icon: Icons.toll_rounded, color: const Color(0xFFD97706)),
              Kpi('Extra slots', '${bonus['devices'] ?? 0} · ${bonus['users'] ?? 0} · ${bonus['branches'] ?? 0}', icon: Icons.add_box_outlined, sub: 'devices · users · branches', color: const Color(0xFF7C3AED)),
            ] else ...[
              Kpi('Earned', _amounts(d['earned']), icon: Icons.savings_outlined),
              Kpi('To be paid', _amounts(d['owed']), icon: Icons.hourglass_bottom, color: const Color(0xFFD97706), sub: 'paid out ${_amounts(d['paid'])}'),
            ],
          ]),
          if (coins && shop.isNotEmpty) ...[
            ListHeader('Spend your coins', trailing: '${_n(balance)} coins'),
            ListPanel(children: [
              for (final it in shop)
                Builder(builder: (context) {
                  final isInvoice = it['kind'] == 'pay_invoice';
                  final cost = isInvoice ? (invoices.isEmpty ? null : (invoices.first['coins'] ?? 0) as num) : (it['cost'] ?? 0) as num;
                  final can = !_busy && cost != null && balance >= cost;
                  return InkWell(
                    onTap: can ? () => _redeem(d, it) : null,
                    child: Padding(
                      padding: const EdgeInsets.fromLTRB(14, 12, 12, 12),
                      child: Row(children: [
                        Avatar(_s(it, 'title'), icon: _kindIcon(_s(it, 'kind')), color: kBrand),
                        const SizedBox(width: 12),
                        Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                          Text(_s(it, 'title'), style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 15)),
                          if (_s(it, 'description').isNotEmpty) Text(_s(it, 'description'), style: TextStyle(fontSize: 12.5, color: hint)),
                          const SizedBox(height: 4),
                          Text(cost == null ? 'No open invoice' : isInvoice && invoices.length > 1 ? 'From ${_n(cost)} coins' : '${_n(cost)} coins', style: TextStyle(fontWeight: FontWeight.w800, color: can ? const Color(0xFFD97706) : hint)),
                        ])),
                        const SizedBox(width: 8),
                        FilledButton(onPressed: can ? () => _redeem(d, it) : null, child: const Text('Get')),
                      ]),
                    ),
                  );
                }),
            ]),
          ],
          if (tiers.isNotEmpty) ...[
            const ListHeader('Your level'),
            if (next != null) Padding(padding: const EdgeInsets.only(bottom: 10, left: 4), child: Text('${((next['referrals'] ?? 1) as int) - paying} more paying ${((next['referrals'] ?? 1) as int) - paying == 1 ? 'company' : 'companies'} to reach ${next['label']}', style: const TextStyle(color: kBrand, fontWeight: FontWeight.w700))),
            ListPanel(children: [
              for (final t in tiers)
                ListRow(
                  leading: Avatar(_s(t, 'label'), icon: t['reached'] == true ? Icons.emoji_events_rounded : Icons.lock_outline_rounded, color: t['reached'] == true ? const Color(0xFF059669) : const Color(0xFF94A3B8)),
                  title: '${t['label']} · ${t['referrals']} paying',
                  subtitle: [if (_s(t, 'perk').isNotEmpty) t['perk'], if ((t['freeMonths'] ?? 0) > 0) '${t['freeMonths']} free month(s)', if ((t['bonusCoins'] ?? 0) > 0) '+${_n(t['bonusCoins'])} coins', if (t['reached'] == true && t['grantedAt'] != null) 'unlocked ${fmtDate(t['grantedAt'])}'].join(' · '),
                  chevron: false,
                ),
            ]),
          ],
          ListHeader('Companies you referred', trailing: '${companies.length}'),
          if (companies.isEmpty) const AppCard(child: Text('Nobody yet. Share your link or code with a company that needs to fiscalise.'))
          else ListPanel(children: [
            for (final c in companies) ListRow(leading: Avatar(_s(c, 'name')), title: _s(c, 'name'), subtitle: 'since ${fmtDate(c['since'])}', pills: [(_s(c, 'status'), c['paying'] == true ? Tone.ok : c['status'] == 'Suspended' ? Tone.bad : Tone.warn)], chevron: false),
          ]),
          if (coins) ...[
            ListHeader('Coin history', trailing: '${history.length}'),
            if (history.isEmpty) const AppCard(child: Text('Coins appear here when a company you referred pays its invoice.'))
            else ListPanel(children: [
              for (final h in history)
                Padding(
                  padding: const EdgeInsets.fromLTRB(14, 12, 14, 12),
                  child: Row(children: [
                    Avatar(_s(h, 'description'), icon: (h['amount'] ?? 0) > 0 ? Icons.add_rounded : Icons.remove_rounded, color: (h['amount'] ?? 0) > 0 ? const Color(0xFF059669) : const Color(0xFF64748B)),
                    const SizedBox(width: 12),
                    Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [Text(_s(h, 'description'), style: const TextStyle(fontWeight: FontWeight.w600)), Text(fmtDateTime(h['createdAt']), style: TextStyle(fontSize: 12, color: hint))])),
                    Text('${(h['amount'] ?? 0) > 0 ? '+' : ''}${_n(h['amount'] ?? 0)}', style: TextStyle(fontWeight: FontWeight.w800, color: (h['amount'] ?? 0) > 0 ? const Color(0xFF059669) : null)),
                  ]),
                ),
            ]),
          ] else ...[
            ListHeader('Rewards', trailing: '${rewards.length}'),
            if (rewards.isEmpty) const AppCard(child: Text('Rewards appear here when a company you referred pays its invoice.'))
            else ListPanel(children: [
              for (final r in rewards) ListRow(leading: Avatar(_s(r, 'company')), title: _s(r, 'company'), subtitle: '${r['percent']}% · ${fmtDate(r['createdAt'])}', figure: (r['coins'] ?? 0) > 0 ? '${_n(r['coins'])} coins' : moneyIn(r['amount'], _s(r, 'currency')), status: _s(r, 'status') == 'Earned' ? 'Pending' : _s(r, 'status'), chevron: false),
            ]),
          ],
        ]);
      },
    );
  }
}

/// The operator's side: coins or money, the levels, the coins shop, coin balances (give or take), and paying out money rewards.
class ReferralProgramScreen extends StatefulWidget {
  const ReferralProgramScreen({super.key});
  @override
  State<ReferralProgramScreen> createState() => _ReferralProgramScreenState();
}

class _ReferralProgramScreenState extends State<ReferralProgramScreen> {
  final _key = GlobalKey<LoaderState<Json>>();
  String _status = '';

  Map<String, dynamic> _body(Json program, {List<Map>? tiers, List<Map>? shop, Json? override}) => {
        'enabled': program['enabled'], 'percent': program['percent'], 'invoicesRewarded': program['invoicesRewarded'], 'description': program['description'],
        'rewardType': program['rewardType'] ?? 'coins', 'coinsPerUnit': program['coinsPerUnit'] ?? 10,
        'tiers': tiers ?? program['tiers'], 'shop': shop ?? program['shop'],
        ...?override,
      };

  Future<void> _editRules(Json program) async {
    final tiers = ((program['tiers'] ?? []) as List).map((t) => Map<String, dynamic>.from(t)).toList();
    final fields = <Fld>[
      const Fld('enabled', 'Program is on', kind: Kind.boolean),
      const Fld('rewardType', 'Reward given as', kind: Kind.select, required: true, options: [Opt('coins', 'Coins (spent in the gateway, no cash)'), Opt('money', 'Money (you pay it out)')]),
      const Fld('percent', 'Reward (% of the invoice before tax)', kind: Kind.number, required: true),
      const Fld('invoicesRewarded', 'Invoices rewarded per company', kind: Kind.integer, required: true),
      const Fld('coinsPerUnit', 'Coins per 1.00 (coins mode)', kind: Kind.integer, required: true),
      const Fld('description', 'Message shown to companies', kind: Kind.multiline),
      for (var i = 0; i < 3; i++) ...[
        Fld('t${i}label', 'Level ${i + 1} name', initial: i < tiers.length ? tiers[i]['label'] : null),
        Fld('t${i}referrals', 'Level ${i + 1}: paying companies needed', kind: Kind.integer, initial: i < tiers.length ? tiers[i]['referrals'] : null),
        Fld('t${i}months', 'Level ${i + 1}: free months', kind: Kind.integer, initial: i < tiers.length ? tiers[i]['freeMonths'] : null),
        Fld('t${i}coins', 'Level ${i + 1}: bonus coins', kind: Kind.integer, initial: i < tiers.length ? tiers[i]['bonusCoins'] : null),
        Fld('t${i}perk', 'Level ${i + 1}: perk shown', initial: i < tiers.length ? tiers[i]['perk'] : null),
      ],
    ];
    final api = context.read<Api>();
    final saved = await Navigator.push<bool>(context, MaterialPageRoute(builder: (_) => FormPage(
      title: 'Program rules', intro: 'Leave a level name empty to remove that level.', fields: fields, initial: {...program},
      onSave: (v) async {
        final out = <Map<String, dynamic>>[];
        for (var i = 0; i < 3; i++) {
          final label = (v['t${i}label'] ?? '').toString().trim();
          if (label.isEmpty) continue;
          out.add({'key': label.toLowerCase().replaceAll(RegExp(r'[^a-z0-9]'), ''), 'label': label, 'referrals': v['t${i}referrals'] ?? 1, 'freeMonths': v['t${i}months'] ?? 0, 'bonusCoins': v['t${i}coins'] ?? 0, 'perk': v['t${i}perk']});
        }
        await api.put('/api/v1/referral-program', body: _body(program, tiers: out, override: {
          'enabled': v['enabled'], 'percent': v['percent'] ?? 0, 'invoicesRewarded': v['invoicesRewarded'] ?? 1, 'description': v['description'], 'rewardType': v['rewardType'] ?? 'coins', 'coinsPerUnit': v['coinsPerUnit'] ?? 10,
        }));
      },
    )));
    if (saved == true) _key.currentState?.reload();
  }

  Future<void> _editItem(Json program, Map<String, dynamic>? item) async {
    final shop = ((program['shop'] ?? []) as List).map((t) => Map<String, dynamic>.from(t)).toList();
    final api = context.read<Api>();
    final saved = await Navigator.push<bool>(context, MaterialPageRoute(builder: (_) => FormPage(
      title: item == null ? 'New reward' : 'Edit reward',
      intro: '"Pay an invoice" is priced by the invoice itself (total × coins per 1.00), so its price and amount are ignored.',
      initial: item ?? {'kind': 'free_days', 'value': 7, 'cost': 100, 'enabled': true},
      fields: [
        const Fld('title', 'Name', required: true), const Fld('description', 'Description'),
        Fld('kind', 'What it gives', kind: Kind.select, required: true, options: [for (final e in _kinds.entries) Opt(e.key, e.value)]),
        const Fld('value', 'How many (days, devices, users or branches)', kind: Kind.integer, nullIfEmpty: false), const Fld('cost', 'Price in coins', kind: Kind.integer, nullIfEmpty: false),
        const Fld('enabled', 'On', kind: Kind.boolean),
      ],
      onSave: (v) async {
        final isInvoice = v['kind'] == 'pay_invoice';
        final next = {'key': item?['key'] ?? (v['title'] ?? '').toString().toLowerCase().replaceAll(RegExp(r'[^a-z0-9]'), ''), 'title': v['title'], 'description': v['description'], 'kind': v['kind'], 'value': isInvoice ? 0 : (v['value'] ?? 1), 'cost': isInvoice ? 0 : (v['cost'] ?? 1), 'enabled': v['enabled'] ?? true};
        final list = [for (final s in shop) if (item != null && s['key'] == item['key']) next else s, if (item == null) next];
        await api.put('/api/v1/referral-program', body: _body(program, shop: list));
      },
    )));
    if (saved == true) _key.currentState?.reload();
  }

  Future<void> _adjust(Map h) async {
    final v = await promptText(context, 'Give coins to ${_s(h, 'company')} (a minus number takes them back). They have ${_n(h['balance'] ?? 0)}.', hint: '100 or -50', ok: 'Apply', keyboard: const TextInputType.numberWithOptions(signed: true));
    final n = int.tryParse((v ?? '').trim());
    if (n == null || n == 0 || !mounted) return;
    final r = await guardedValue(context, () => context.read<Api>().post('/api/v1/referral-program/coins', body: {'clientId': h['clientId'], 'amount': n, 'note': n > 0 ? 'Coins from the operator' : 'Coins taken by the operator'}));
    if (r != null && mounted) { toast(context, _s(r, 'message')); _key.currentState?.reload(); }
  }

  Future<void> _pay(Json r) async {
    final ref = await promptText(context, 'Pay ${moneyIn(r['amount'], _s(r, 'currency'))} to ${_s(r, 'referrer')}', hint: 'Payment reference (optional)', ok: 'Mark as paid');
    if (ref == null || !mounted) return;
    final res = await guardedValue(context, () => context.read<Api>().post('/api/v1/referral-program/rewards/${r['id']}/pay', body: {'reference': ref.trim().isEmpty ? null : ref.trim()}));
    if (res != null) _key.currentState?.reload();
  }

  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    return Loader<Json>(
      key: _key,
      load: () => api.getMap('/api/v1/referral-program', query: {'status': _status}),
      builder: (context, d, reload) {
        final program = Map<String, dynamic>.from((d['program'] ?? {}) as Map);
        final rewards = ((d['rewards'] ?? []) as List).cast<Map<String, dynamic>>();
        final tiers = ((program['tiers'] ?? []) as List).cast<Map<String, dynamic>>();
        final shop = ((program['shop'] ?? []) as List).cast<Map<String, dynamic>>();
        final holders = ((d['holders'] ?? []) as List).cast<Map<String, dynamic>>();
        final coins = program['rewardType'] != 'money';
        final hint = Theme.of(context).hintColor;
        return ListView(padding: const EdgeInsets.fromLTRB(16, 12, 16, 28), children: [
          AppCard(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Row(children: [
              const Expanded(child: Text('Refer and earn', style: TextStyle(fontSize: 18, fontWeight: FontWeight.w800))),
              Pill(program['enabled'] == true ? 'On' : 'Off', tone: program['enabled'] == true ? Tone.ok : Tone.warn),
              const SizedBox(width: 6),
              Pill(coins ? 'Coins' : 'Money', tone: Tone.info),
            ]),
            const SizedBox(height: 6),
            Text('${program['percent']}% of ${program['invoicesRewarded'] == 1 ? 'the first paid invoice' : 'the first ${program['invoicesRewarded']} paid invoices'} of each referred company${coins ? ', as coins (${program['coinsPerUnit']} per 1.00)' : ', paid out in money'}', style: const TextStyle(fontWeight: FontWeight.w600)),
            for (final t in tiers) Text('• ${t['label']}: ${t['referrals']} paying${(t['freeMonths'] ?? 0) > 0 ? ' → ${t['freeMonths']} free month(s)' : ''}${(t['bonusCoins'] ?? 0) > 0 ? ' + ${t['bonusCoins']} coins' : ''}', style: TextStyle(fontSize: 13, color: hint)),
            const SizedBox(height: 10),
            FilledButton.icon(onPressed: () => _editRules(program), icon: const Icon(Icons.tune_rounded), label: const Text('Change the rules')),
          ])),
          const SizedBox(height: 12),
          ResponsiveGrid(minTileWidth: 140, children: [
            Kpi('Referrers', '${d['referrers']}', icon: Icons.campaign_outlined),
            Kpi('Companies brought', '${d['referredCompanies']}', icon: Icons.apartment),
            Kpi('Coins issued / spent', '${_n(d['coinsIssued'] ?? 0)} / ${_n(d['coinsSpent'] ?? 0)}', icon: Icons.toll_rounded, color: const Color(0xFFD97706)),
            Kpi('Money to pay out', _amounts(d['owed']), icon: Icons.hourglass_bottom, sub: 'paid ${_amounts(d['paid'])}'),
          ]),
          ListHeader('Coins shop', trailing: '${shop.length}'),
          ListPanel(children: [
            for (final it in shop)
              ListRow(
                leading: Avatar(_s(it, 'title'), icon: _kindIcon(_s(it, 'kind')), color: it['enabled'] == false ? const Color(0xFF94A3B8) : kBrand),
                title: _s(it, 'title'),
                subtitle: [_kinds[_s(it, 'kind')] ?? '', if (it['kind'] != 'pay_invoice') '${it['value']}', _s(it, 'description')].where((x) => x.isNotEmpty).join(' · '),
                figure: it['kind'] == 'pay_invoice' ? 'by invoice' : '${_n(it['cost'] ?? 0)} coins',
                pills: [if (it['enabled'] == false) ('Off', Tone.warn)],
                onTap: () => _editItem(program, it),
              ),
          ]),
          Align(alignment: Alignment.centerLeft, child: TextButton.icon(onPressed: () => _editItem(program, null), icon: const Icon(Icons.add_rounded), label: const Text('Add a reward'))),
          ListHeader('Coin balances', trailing: '${holders.length}'),
          if (holders.isEmpty) const AppCard(child: Text('No company has coins yet.'))
          else ListPanel(children: [
            for (final h in holders) ListRow(leading: Avatar(_s(h, 'company')), title: _s(h, 'company'), figure: '${_n(h['balance'] ?? 0)} coins', onTap: () => _adjust(h)),
          ]),
          const ListHeader('Rewards'),
          FilterPills([for (final e in const [('', 'All'), ('Earned', 'To pay'), ('Credited', 'As coins'), ('Paid', 'Paid')]) PillOption(e.$2, _status == e.$1, () { setState(() => _status = e.$1); _key.currentState?.reload(); })]),
          const SizedBox(height: 10),
          if (rewards.isEmpty) const AppCard(child: Text('No rewards yet.'))
          else ListPanel(children: [
            for (final r in rewards)
              ListRow(
                leading: Avatar(_s(r, 'referrer')),
                title: '${_s(r, 'referrer')} → ${_s(r, 'referred')}',
                subtitle: '${fmtDate(r['createdAt'])}${r['reference'] != null ? ' · ${r['reference']}' : ''}',
                figure: (r['coins'] ?? 0) > 0 ? '${_n(r['coins'])} coins' : moneyIn(r['amount'], _s(r, 'currency')),
                status: r['status'] == 'Earned' ? 'Pending' : r['status'] == 'Credited' ? 'Credited' : _s(r, 'status'),
                onTap: r['status'] == 'Earned' ? () => _pay(r) : null,
                chevron: r['status'] == 'Earned',
              ),
          ]),
        ]);
      },
    );
  }
}
