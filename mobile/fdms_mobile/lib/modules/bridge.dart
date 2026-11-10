import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:provider/provider.dart';

import '../core/api.dart';
import '../core/models.dart';
import '../ui/kit.dart';
import '../ui/lists.dart';
import '../ui/premium.dart';

void _copy(BuildContext context, String text) {
  Clipboard.setData(ClipboardData(text: text));
  toast(context, 'Copied.');
}

/// A one-time secret (a key or a signing secret): shown once with a copy button.
Future<void> _showSecret(BuildContext context, String title, String secret) => showDialog<void>(
      context: context,
      builder: (d) => AlertDialog(
        title: Text(title),
        content: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
          const Text('Copy it now. It is not shown again.'),
          const SizedBox(height: 10),
          SelectableText(secret, style: const TextStyle(fontFamily: 'monospace', fontSize: 12)),
        ]),
        actions: [TextButton(onPressed: () => _copy(d, secret), child: const Text('Copy')), FilledButton(onPressed: () => Navigator.pop(d), child: const Text('Done'))],
      ),
    );

/// The company: which partners may use its Zoho Books through FiscalHub, which modules, read only or read and write.
class BridgeGrantsScreen extends StatefulWidget {
  const BridgeGrantsScreen({super.key});
  @override
  State<BridgeGrantsScreen> createState() => _BridgeGrantsScreenState();
}

class _BridgeGrantsScreenState extends State<BridgeGrantsScreen> {
  final _loader = GlobalKey<LoaderState<Json>>();

  Future<void> _edit(Json g, Json? grant) async {
    final saved = await Navigator.of(context).push<bool>(MaterialPageRoute(builder: (_) => _GrantForm(((g['modules'] ?? []) as List).cast<Json>(), grant)));
    if (saved == true) _loader.currentState?.reload();
  }

  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    return Scaffold(
      appBar: AppBar(title: const Text('Partner access')),
      body: Loader<Json>(
        key: _loader,
        load: () => api.getMap('/api/v1/integrations/zoho/bridge'),
        builder: (context, g, reload) {
          final grants = ((g['grants'] ?? []) as List).cast<Json>();
          final modules = ((g['modules'] ?? []) as List).cast<Json>();
          final hint = Theme.of(context).hintColor;
          String labels(Json x) => ((x['modules'] ?? []) as List).map((k) => modules.firstWhere((m) => m['key'] == k, orElse: () => {'label': k})['label']).join(', ');
          return RefreshIndicator(onRefresh: reload, child: ListView(padding: const EdgeInsets.all(16), children: [
            Text('Let a partner\'s system read or update your Zoho Books through FiscalHub. They never get your Zoho sign-in, only what you allow, and you can withdraw it at any time.', style: TextStyle(color: hint)),
            if (g['zohoFullAccess'] != true) Padding(padding: const EdgeInsets.only(top: 10), child: Text('Your Zoho connection was made before the bridge existed. Disconnect and connect Zoho Books once more so partners can reach what you share.', style: TextStyle(color: toneColor(Tone.warn)))),
            if (g['charged'] == true) Padding(padding: const EdgeInsets.only(top: 10), child: Text('Sharing costs ${moneyIn(g['monthlyFee'] as num?, g['currency']?.toString())} a month, added to your subscription invoice.', style: TextStyle(fontSize: 12, color: hint))),
            const SizedBox(height: 12),
            for (final x in grants)
              AppCard(
                padding: const EdgeInsets.all(12),
                child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  Text('${x['partnerName']}  ·  ${x['partnerCode']}', style: const TextStyle(fontWeight: FontWeight.w700)),
                  Text('${labels(x)} · ${x['canWrite'] == true ? 'read and write' : 'read only'} · ${x['callsThisMonth']} calls this month', style: TextStyle(fontSize: 12, color: hint)),
                  Wrap(spacing: 4, children: [
                    TextButton(onPressed: () => _edit(g, x), child: const Text('Change')),
                    TextButton(
                      onPressed: () async {
                        if (!await confirm(context, 'Withdraw ${x['partnerName']}\'s access? Their system stops at once.', ok: 'Withdraw', danger: true)) return;
                        if (!context.mounted) return;
                        if (await guarded(context, () => api.delete('/api/v1/integrations/zoho/bridge/${x['id']}'), done: 'Access withdrawn.')) await reload();
                      },
                      child: Text('Withdraw', style: TextStyle(color: toneColor(Tone.bad))),
                    ),
                  ]),
                ]),
              ),
            if (grants.isEmpty) Padding(padding: const EdgeInsets.all(16), child: Text('No partner has access.', textAlign: TextAlign.center, style: TextStyle(color: hint))),
            const SizedBox(height: 8),
            FilledButton.icon(onPressed: () => _edit(g, null), icon: const Icon(Icons.add_rounded, size: 18), label: const Text('Share with a partner')),
          ]));
        },
      ),
    );
  }
}

class _GrantForm extends StatefulWidget {
  const _GrantForm(this.modules, this.grant);
  final List<Json> modules;
  final Json? grant;
  @override
  State<_GrantForm> createState() => _GrantFormState();
}

class _GrantFormState extends State<_GrantForm> {
  late final _code = TextEditingController(text: widget.grant?['partnerCode']?.toString() ?? '');
  late bool _write = widget.grant?['canWrite'] == true;
  late final Set<String> _chosen = {...((widget.grant?['modules'] ?? const ['contacts', 'items']) as List).map((e) => e.toString())};

  @override
  void dispose() { _code.dispose(); super.dispose(); }

  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    return Scaffold(
      appBar: AppBar(title: const Text('Share Zoho Books')),
      body: ListView(padding: const EdgeInsets.all(16), children: [
        TextField(controller: _code, textCapitalization: TextCapitalization.characters, decoration: const InputDecoration(labelText: 'Partner code', helperText: 'The code the partner gave you.')),
        SwitchListTile(contentPadding: EdgeInsets.zero, value: _write, onChanged: (v) => setState(() => _write = v), title: const Text('May also create and update records')),
        SectionTitle('Modules'),
        for (final m in widget.modules)
          CheckboxListTile(
            contentPadding: EdgeInsets.zero,
            value: _chosen.contains(m['key']),
            onChanged: (v) => setState(() => v == true ? _chosen.add(m['key'].toString()) : _chosen.remove(m['key'])),
            title: Text(m['label'].toString()),
          ),
        const SizedBox(height: 12),
        FilledButton(
          onPressed: () async {
            final ok = await guarded(context, () => api.put('/api/v1/integrations/zoho/bridge', body: {'partnerCode': _code.text.trim(), 'modules': _chosen.toList(), 'canWrite': _write}), done: 'Shared.');
            if (ok && context.mounted) Navigator.pop(context, true);
          },
          child: const Text('Share'),
        ),
      ]),
    );
  }
}

/// The partner: keys for its system, the webhook for changes, the companies that shared, recent changes.
class PartnerBridgeScreen extends StatelessWidget {
  const PartnerBridgeScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    return Loader<Json>(
      load: () => api.getMap('/api/v1/partner/bridge'),
      builder: (context, p, reload) {
        final keys = ((p['keys'] ?? []) as List).cast<Json>();
        final companies = ((p['companies'] ?? []) as List).cast<Json>();
        final events = ((p['recentEvents'] ?? []) as List).cast<Json>();
        final ep = Map<String, dynamic>.from((p['endpoint'] ?? {}) as Map);
        final hint = Theme.of(context).hintColor;
        final base = p['baseUrl']?.toString() ?? '';
        return RefreshIndicator(onRefresh: reload, child: ListView(padding: const EdgeInsets.fromLTRB(16, 12, 16, 24), children: [
          InfoCard(title: 'How it works', icon: Icons.info_outline, children: [
            Text('1. Give your partner code ${p['partnerCode']} to the company; it shares its Zoho Books with you in FiscalHub.\n'
                '2. Create a key and send it from your system as X-Bridge-Key.\n'
                '3. Call $base/companies, then $base/companies/{companyId}/zoho/contacts and so on, as in Zoho Books\' API v3.\n'
                '4. Set a webhook for changes, or read $base/events?after=0.', style: TextStyle(fontSize: 12.5, color: hint)),
            const SizedBox(height: 12),
          ]),
          SectionTitle('Keys', trailing: TextButton.icon(
            onPressed: () async {
              final name = await promptText(context, 'Name the key', hint: 'e.g. Production server', ok: 'Create');
              if (name == null || name.trim().isEmpty || !context.mounted) return;
              try {
                final r = await api.post('/api/v1/partner/bridge/keys', body: {'name': name.trim()}) as Map;
                if (context.mounted) await _showSecret(context, 'Your bridge key', r['secret'].toString());
                await reload();
              } catch (e) { if (context.mounted) toast(context, e, error: true); }
            },
            icon: const Icon(Icons.add_rounded, size: 18), label: const Text('New key'))),
          if (keys.isEmpty) Text('No keys yet.', style: TextStyle(color: hint)),
          for (final k in keys)
            ListTile(
              contentPadding: EdgeInsets.zero,
              title: Text('${k['name']}  ${k['prefix']}…'),
              subtitle: Text('${k['callsToday']} calls today · last used ${k['lastUsedAt'] == null ? 'never' : fmtDateTime(k['lastUsedAt'])}'),
              trailing: k['revokedAt'] != null ? const Badge2('Revoked', tone: Tone.bad) : TextButton(
                onPressed: () async {
                  if (!await confirm(context, 'Revoke this key? Your system stops working with it at once.', ok: 'Revoke', danger: true)) return;
                  if (!context.mounted) return;
                  if (await guarded(context, () => api.delete('/api/v1/partner/bridge/keys/${k['id']}'), done: 'Revoked.')) await reload();
                },
                child: Text('Revoke', style: TextStyle(color: toneColor(Tone.bad)))),
            ),
          SectionTitle('Changes (webhook)', trailing: TextButton(
            onPressed: () async {
              await Navigator.of(context).push(MaterialPageRoute(builder: (_) => _EndpointForm(ep, ((p['modules'] ?? []) as List).cast<Json>().where((m) => m['watchable'] == true).toList())));
              await reload();
            },
            child: const Text('Edit'))),
          Text(ep['webhookUrl'] == null ? 'No webhook: read the feed instead.' : '${ep['webhookUrl']}\nWatching: ${((ep['watchModules'] ?? []) as List).join(', ')}', style: TextStyle(color: hint)),
          SectionTitle('Companies that shared'),
          if (companies.isEmpty) Text('None yet. Give them your code ${p['partnerCode']}.', style: TextStyle(color: hint)),
          for (final c in companies)
            AppCard(
              padding: const EdgeInsets.all(12),
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Row(children: [Expanded(child: Text('${c['name']}', style: const TextStyle(fontWeight: FontWeight.w700))), Badge2(c['ready'] == true ? 'Ready' : 'Not ready', tone: c['ready'] == true ? Tone.ok : Tone.warn)]),
                Text('${((c['modules'] ?? []) as List).join(', ')} · ${c['canWrite'] == true ? 'read and write' : 'read only'}', style: TextStyle(fontSize: 12, color: hint)),
                if (c['notReadyReason'] != null) Text('${c['notReadyReason']}', style: TextStyle(fontSize: 12, color: toneColor(Tone.warn))),
                Row(children: [Expanded(child: Text('ID ${c['companyId']}', style: const TextStyle(fontSize: 11, fontFamily: 'monospace'), overflow: TextOverflow.ellipsis)),
                  IconButton(icon: const Icon(Icons.copy_rounded, size: 18), onPressed: () => _copy(context, '${c['companyId']}'))]),
              ]),
            ),
          SectionTitle('Recent changes'),
          if (events.isEmpty) Text('No changes yet.', style: TextStyle(color: hint)),
          for (final e in events)
            ListTile(
              contentPadding: EdgeInsets.zero,
              dense: true,
              title: Text('${e['type']} · ${e['recordId']}', style: const TextStyle(fontSize: 13)),
              subtitle: Text('${fmtDateTime(e['createdAt'])}${e['lastError'] != null ? ' · ${e['lastError']}' : ''}'),
              trailing: Badge2('${e['status']}', tone: e['status'] == 'Delivered' ? Tone.ok : e['status'] == 'Failed' ? Tone.bad : Tone.warn),
            ),
        ]));
      },
    );
  }
}

class _EndpointForm extends StatefulWidget {
  const _EndpointForm(this.endpoint, this.watchable);
  final Json endpoint;
  final List<Json> watchable;
  @override
  State<_EndpointForm> createState() => _EndpointFormState();
}

class _EndpointFormState extends State<_EndpointForm> {
  late final _url = TextEditingController(text: widget.endpoint['webhookUrl']?.toString() ?? '');
  late final Set<String> _watch = {...((widget.endpoint['watchModules'] ?? const []) as List).map((e) => e.toString())};
  bool _rotate = false;

  @override
  void dispose() { _url.dispose(); super.dispose(); }

  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    return Scaffold(
      appBar: AppBar(title: const Text('Webhook for changes')),
      body: ListView(padding: const EdgeInsets.all(16), children: [
        TextField(controller: _url, keyboardType: TextInputType.url, decoration: const InputDecoration(labelText: 'Your webhook address (https)')),
        if (widget.endpoint['hasSecret'] == true) CheckboxListTile(contentPadding: EdgeInsets.zero, value: _rotate, onChanged: (v) => setState(() => _rotate = v ?? false), title: const Text('Make a new signing secret')),
        SectionTitle('Watch these modules'),
        for (final m in widget.watchable)
          CheckboxListTile(
            contentPadding: EdgeInsets.zero,
            value: _watch.contains(m['key']),
            onChanged: (v) => setState(() => v == true ? _watch.add(m['key'].toString()) : _watch.remove(m['key'])),
            title: Text(m['label'].toString()),
          ),
        const SizedBox(height: 12),
        FilledButton(
          onPressed: () async {
            try {
              final r = await api.put('/api/v1/partner/bridge/endpoint', body: {'webhookUrl': _url.text.trim().isEmpty ? null : _url.text.trim(), 'watchModules': _watch.toList(), 'rotateSecret': _rotate}) as Map;
              if (r['secret'] != null && context.mounted) await _showSecret(context, 'Signing secret', r['secret'].toString());
              if (context.mounted) Navigator.pop(context);
            } catch (e) { if (context.mounted) toast(context, e, error: true); }
          },
          child: const Text('Save'),
        ),
      ]),
    );
  }
}

/// The platform: partners using the bridge, their traffic and deliveries.
class BridgeMonitorScreen extends StatelessWidget {
  const BridgeMonitorScreen({super.key});
  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    return Scaffold(
      appBar: AppBar(title: const Text('Zoho bridge')),
      body: Loader<Json>(
        load: () => api.getMap('/api/v1/admin/integrations/bridge'),
        builder: (context, b, reload) {
          final rows = ((b['partners'] ?? []) as List).cast<Json>();
          final hint = Theme.of(context).hintColor;
          return RefreshIndicator(onRefresh: reload, child: ListView(padding: const EdgeInsets.all(16), children: [
            ResponsiveGrid(minTileWidth: 150, children: [
              Kpi('Companies sharing', '${b['companiesShared'] ?? 0}', icon: Icons.apartment_rounded),
              Kpi('Calls today', '${b['callsToday'] ?? 0}', icon: Icons.swap_horiz_rounded),
            ]),
            if (rows.isEmpty) Padding(padding: const EdgeInsets.all(24), child: Text('No partner uses the bridge yet.', textAlign: TextAlign.center, style: TextStyle(color: hint))),
            for (final r in rows)
              AppCard(
                padding: const EdgeInsets.all(12),
                child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  Text('${r['partner']}', style: const TextStyle(fontWeight: FontWeight.w700)),
                  Text('${r['companies']} companies · ${r['activeKeys']} keys · webhook ${r['webhookHost'] ?? 'none (feed)'}', style: TextStyle(fontSize: 12, color: hint)),
                  const SizedBox(height: 6),
                  Wrap(spacing: 6, runSpacing: 6, children: [
                    Pill('${r['callsToday']} calls today', tone: Tone.info),
                    Pill('${r['eventsPending']} waiting', tone: Tone.warn),
                    if ((r['eventsFailed'] ?? 0) > 0) Pill('${r['eventsFailed']} failed', tone: Tone.bad),
                  ]),
                ]),
              ),
          ]));
        },
      ),
    );
  }
}
