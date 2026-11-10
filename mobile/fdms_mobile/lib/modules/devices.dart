import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/api.dart';
import '../core/auth.dart';
import '../core/models.dart';
import '../screens/profile.dart' show guardedValue;
import '../ui/kit.dart';
import '../ui/lists.dart';
import '../ui/premium.dart';

String deviceLabel(Json d) => ((d['name'] ?? '') as String).isNotEmpty ? d['name'] : 'Device ${d['zimraDeviceId']}';

/// Loads the devices the person may use. Many screens need to pick one.
Future<List<Json>> loadDevices(Api api) => api.getList('/api/v1/devices');

/// Lets the person choose which device to work on (when there is only one it is chosen for them).
class DevicePicker extends StatelessWidget {
  const DevicePicker({super.key, required this.devices, required this.value, required this.onChanged, this.label = 'Device'});
  final List<Json> devices;
  final String? value;
  final ValueChanged<String?> onChanged;
  final String label;
  @override
  Widget build(BuildContext context) => DropdownButtonFormField<String>(
        initialValue: devices.any((d) => d['id'] == value) ? value : null,
        isExpanded: true,
        decoration: InputDecoration(labelText: label),
        items: [for (final d in devices) DropdownMenuItem(value: d['id'] as String, child: Text('${deviceLabel(d)} · ${d['environment'] ?? ''}', overflow: TextOverflow.ellipsis))],
        onChanged: onChanged,
      );
}

class DevicesScreen extends StatefulWidget {
  const DevicesScreen({super.key});
  @override
  State<DevicesScreen> createState() => _DevicesScreenState();
}

class _DevicesScreenState extends State<DevicesScreen> {
  final _key = GlobalKey<LoaderState<(List<Json>, Map<String, Json>)>>();

  Future<(List<Json>, Map<String, Json>)> _load() async {
    final api = context.read<Api>();
    final devices = await loadDevices(api);
    final byId = <String, Json>{};
    try {
      final dash = await api.getMap('/api/v1/dashboard');
      for (final d in (dash['deviceList'] ?? []) as List) { byId[(d as Map)['id']] = Map<String, dynamic>.from(d); }
    } catch (_) { /* day status just is not shown */ }
    return (devices, byId);
  }

  @override
  Widget build(BuildContext context) {
    final auth = context.watch<AuthController>();
    return Scaffold(
      backgroundColor: Colors.transparent,
      floatingActionButton: auth.can('devices.manage') ? FloatingActionButton.extended(onPressed: () async { if (await Navigator.push<bool>(context, MaterialPageRoute(builder: (_) => const RegisterDeviceScreen())) == true) _key.currentState?.reload(); }, icon: const Icon(Icons.add), label: const Text('Register device')) : null,
      body: Loader<(List<Json>, Map<String, Json>)>(
        key: _key,
        load: _load,
        builder: (context, data, reload) {
          final (devices, sum) = data;
          if (devices.isEmpty) {
            return ListView(children: [BigEmpty(icon: Icons.memory_rounded, title: 'No fiscal devices yet', message: 'Register the device ZIMRA gave you to start fiscalising.',
                action: auth.can('devices.manage') ? FilledButton.icon(onPressed: () async { if (await Navigator.push<bool>(context, MaterialPageRoute(builder: (_) => const RegisterDeviceScreen())) == true) _key.currentState?.reload(); }, icon: const Icon(Icons.add_rounded), label: const Text('Register device')) : null)]);
          }
          final hint = Theme.of(context).hintColor;
          return ListView(padding: const EdgeInsets.fromLTRB(16, 4, 16, 96), children: [
            ListHeader('Fiscal devices', trailing: '${devices.length}'),
            for (final d in devices)
              Builder(builder: (context) {
                final s = sum[d['id']];
                final st = s?['dayStatus']?.toString();
                final c = d['isActive'] == false ? toneColor(Tone.bad) : toneColor(toneFor(st));
                final pending = (s?['pendingReceipts'] ?? 0) as num;
                final cert = parseDate(d['certificateValidTill']);
                final certSoon = cert != null && cert.difference(DateTime.now()).inDays < 30;
                return Padding(
                  padding: const EdgeInsets.only(bottom: 12),
                  child: Card(
                    clipBehavior: Clip.antiAlias,
                    child: InkWell(
                      onTap: () async { await Navigator.push(context, MaterialPageRoute(builder: (_) => DeviceDetailScreen(deviceId: d['id']))); reload(); },
                      child: IntrinsicHeight(child: Row(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
                        Container(width: 5, color: c),
                        Expanded(child: Padding(
                          padding: const EdgeInsets.all(14),
                          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                            Row(children: [
                              Avatar(deviceLabel(d), icon: Icons.memory_rounded, color: c == toneColor(Tone.neutral) ? kBrand : c),
                              const SizedBox(width: 12),
                              Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                                Text(deviceLabel(d), style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w800)),
                                Text('ZIMRA ${d['zimraDeviceId']}  ·  ${d['taxPayerName'] ?? d['serialNo'] ?? ''}', style: TextStyle(fontSize: 12, color: hint), maxLines: 1, overflow: TextOverflow.ellipsis),
                              ])),
                              Icon(Icons.chevron_right_rounded, color: hint),
                            ]),
                            const SizedBox(height: 12),
                            Wrap(spacing: 6, runSpacing: 6, children: [
                              Pill(d['environment']?.toString() ?? '', tone: d['environment'] == 'Production' ? Tone.ok : Tone.info),
                              if (s != null) Pill('Day ${statusText(st)}${s['dayNo'] != null ? ' #${s['dayNo']}' : ''}', tone: toneFor(st)),
                              if (pending > 0) Pill('$pending pending', tone: Tone.warn),
                              if (d['isActive'] == false) const Pill('Inactive', tone: Tone.bad),
                              if (d['branchName'] != null) Pill(d['branchName'].toString()),
                              if (cert != null) Pill('Certificate ${fmtDate(d['certificateValidTill'])}', tone: certSoon ? Tone.warn : Tone.neutral),
                            ]),
                          ]),
                        )),
                      ])),
                    ),
                  ),
                );
              }),
          ]);
        },
      ),
    );
  }
}

class DeviceDetailScreen extends StatefulWidget {
  const DeviceDetailScreen({super.key, required this.deviceId});
  final String deviceId;
  @override
  State<DeviceDetailScreen> createState() => _DeviceDetailScreenState();
}

class _DeviceDetailScreenState extends State<DeviceDetailScreen> {
  final _key = GlobalKey<LoaderState<(Json, Json?)>>();
  bool _busy = false;

  Future<(Json, Json?)> _load() async {
    final api = context.read<Api>();
    final d = await api.getMap('/api/v1/devices/${widget.deviceId}');
    Json? day;
    try { day = await api.getMap('/api/v1/devices/${widget.deviceId}/day'); } catch (_) {}
    return (d, day);
  }

  Future<void> _do(String label, Future<dynamic> Function() work, {String? ok, bool reload = true}) async {
    setState(() => _busy = true);
    final done = await guarded(context, () async { await work(); }, done: ok ?? label);
    if (mounted) setState(() => _busy = false);
    if (done && reload) _key.currentState?.reload();
  }

  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    final auth = context.watch<AuthController>();
    final id = widget.deviceId;
    return Loader<(Json, Json?)>(
      key: _key,
      load: _load,
      frame: (w) => Scaffold(appBar: AppBar(title: const Text('Device')), body: w),
      builder: (context, data, reload) {
        final (d, day) = data;
        final open = dayIsOpen(day?['status']?.toString());
        final taxes = ((d['taxes'] ?? []) as List).cast<Map<String, dynamic>>();
        final counters = ((day?['counters'] ?? []) as List).cast<Map<String, dynamic>>();
        final hint = Theme.of(context).hintColor;
        final dayTone = toneFor(day?['status']?.toString());
        final dayColor = toneColor(dayTone);
        final manage = auth.can('devices.manage');
        return DetailScaffold(
          barTitle: 'Device',
          title: deviceLabel(d),
          subtitle: [d['taxPayerName'], 'ZIMRA ${d['zimraDeviceId']}'].where((x) => (x ?? '').toString().isNotEmpty).join('  ·  '),
          icon: Icons.memory_rounded,
          tone: d['isActive'] == false ? Tone.bad : null,
          chips: [
            HeroChip(d['environment']?.toString() ?? '', icon: d['environment'] == 'Production' ? Icons.verified_rounded : Icons.science_outlined),
            if (d['operatingMode'] != null) HeroChip(d['operatingMode'].toString(), icon: d['operatingMode'] == 'Online' ? Icons.wifi_rounded : Icons.wifi_off_rounded),
            if (d['isActive'] == false) const HeroChip('Inactive', icon: Icons.block_rounded),
          ],
          actions: [
            if (manage) QuickAction(Icons.wifi_tethering_rounded, 'Ping ZIMRA', _busy ? null : () => _do('Ping', () async { final r = await api.post('/api/v1/devices/$id/ping'); if (!context.mounted) return; toast(context, 'ZIMRA answered${r is Map && r['operationId'] != null ? ' (operation ${r['operationId']})' : ''}.'); }, ok: 'ZIMRA answered.', reload: false), primary: true),
            if (manage) QuickAction(Icons.sync_rounded, 'Refresh config', _busy ? null : () => _do('Refresh', () => api.post('/api/v1/devices/$id/refresh-config'), ok: 'Configuration refreshed from ZIMRA.')),
            QuickAction(Icons.cloud_off_rounded, 'Offline files', () => Navigator.push(context, MaterialPageRoute(builder: (_) => _OfflineFilesScreen(deviceId: id)))),
            QuickAction(Icons.inventory_2_outlined, 'Stock', () => Navigator.push(context, MaterialPageRoute(builder: (_) => _StockScreen(deviceId: id)))),
            if (manage) QuickAction(Icons.edit_rounded, 'Rename', _busy ? null : () async { final n = await promptText(context, 'Name this device', hint: 'Till 2, front counter', initial: d['name']?.toString() ?? '', ok: 'Save'); if (n != null) _do('Name', () => api.put('/api/v1/devices/$id/name', body: {'name': n.trim().isEmpty ? null : n.trim()}), ok: 'Saved.'); }),
            if (manage) QuickAction(Icons.verified_outlined, 'Renew certificate', _busy ? null : () async { if (await confirm(context, 'Renew the device certificate with ZIMRA?', ok: 'Renew')) _do('Renew', () => api.post('/api/v1/devices/$id/renew-certificate'), ok: 'Certificate renewed.'); }),
          ],
          children: [
            InfoCard(title: 'Fiscal day', icon: Icons.today_rounded, children: [
              if (day == null)
                const Padding(padding: EdgeInsets.only(bottom: 8), child: Text('The day status could not be read.'))
              else ...[
                Container(
                  margin: const EdgeInsets.only(top: 4, bottom: 12),
                  padding: const EdgeInsets.all(14),
                  decoration: BoxDecoration(gradient: LinearGradient(colors: [dayColor.withValues(alpha: 0.14), dayColor.withValues(alpha: 0.04)]), borderRadius: BorderRadius.circular(16)),
                  child: Row(children: [
                    Container(padding: const EdgeInsets.all(10), decoration: BoxDecoration(color: dayColor, shape: BoxShape.circle), child: Icon(open ? Icons.lock_open_rounded : Icons.lock_outline_rounded, color: Colors.white, size: 22)),
                    const SizedBox(width: 14),
                    Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                      Text('Day ${day['fiscalDayNo'] ?? '-'}  ·  ${statusText(day['status']?.toString())}', style: TextStyle(fontSize: 17, fontWeight: FontWeight.w800, color: dayColor)),
                      const SizedBox(height: 2),
                      Text([if (day['openedAt'] != null) 'Opened ${fmtDateTime(day['openedAt'])}', '${day['lastReceiptCounter'] ?? 0} receipts'].join('  ·  '), style: TextStyle(fontSize: 12.5, color: hint)),
                    ])),
                  ]),
                ),
                if (day['closingErrorCode'] != null) Padding(padding: const EdgeInsets.only(bottom: 10), child: Text('Closing problem: ${day['closingErrorCode']}', style: const TextStyle(color: Color(0xFFDC2626), fontWeight: FontWeight.w600))),
                if (counters.isNotEmpty) ...[
                  Text('COUNTERS', style: TextStyle(fontSize: 11, fontWeight: FontWeight.w800, letterSpacing: 0.9, color: hint)),
                  const SizedBox(height: 4),
                  for (final c in counters.take(12))
                    Padding(padding: const EdgeInsets.symmetric(vertical: 4), child: Row(children: [
                      Expanded(child: Text('${humanize(c['type']?.toString() ?? '')} · ${c['currency']}${c['taxPercent'] != null ? ' @ ${c['taxPercent']}%' : ''}', style: const TextStyle(fontSize: 13))),
                      const SizedBox(width: 8),
                      Text(money(c['value']), style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w700)),
                    ])),
                  const SizedBox(height: 8),
                ],
                if (auth.can('day.manage'))
                  Padding(padding: const EdgeInsets.only(bottom: 8), child: Row(children: [
                    Expanded(child: FilledButton.icon(onPressed: _busy || open ? null : () => _do('Open day', () => api.post('/api/v1/devices/$id/day/open'), ok: 'Fiscal day opened.'), icon: const Icon(Icons.play_arrow_rounded), label: const Text('Open day'))),
                    const SizedBox(width: 10),
                    Expanded(child: OutlinedButton.icon(onPressed: _busy || !open ? null : () async { if (await confirm(context, 'Close the fiscal day? No more receipts can be issued on it.', ok: 'Close day')) _do('Close day', () => api.post('/api/v1/devices/$id/day/close'), ok: 'Close requested.'); }, icon: const Icon(Icons.stop_rounded), label: const Text('Close day'))),
                  ])),
              ],
            ]),
            InfoCard(title: 'Device', icon: Icons.memory_rounded, children: infoRows([
              ('ZIMRA device ID', '${d['zimraDeviceId']}'), ('Serial number', d['serialNo']?.toString()), ('Model', [d['modelName'], d['modelVersion']].where((x) => x != null).join(' ')),
              ('Branch', (d['assignedBranch'] ?? d['branchName'])?.toString()), ('Operating mode', d['operatingMode']?.toString()), ('Certificate valid until', d['certificateValidTill'] == null ? null : fmtDate(d['certificateValidTill'])),
            ])),
            InfoCard(title: 'Taxpayer', icon: Icons.apartment_rounded, children: infoRows([
              ('Taxpayer', d['taxPayerName']?.toString()), ('TIN', (d['taxPayerTin'] ?? d['taxPayerTIN'])?.toString()), ('VAT number', d['vatNumber']?.toString()),
              ('Address', d['deviceBranchAddress']?.toString()), ('Phone', d['deviceBranchPhone']?.toString()), ('Email', d['deviceBranchEmail']?.toString()),
            ])),
            if (taxes.isNotEmpty) InfoCard(title: 'Taxes set by ZIMRA', icon: Icons.percent_rounded, children: [
              for (var i = 0; i < taxes.length; i++)
                InfoRow(taxes[i]['taxName']?.toString() ?? 'Tax', taxes[i]['taxPercent'] == null ? 'Exempt' : '${taxes[i]['taxPercent']}%', icon: Icons.percent_rounded, last: i == taxes.length - 1, trailing: Text('ID ${taxes[i]['taxId']}', style: TextStyle(fontSize: 12, color: hint))),
            ]),
          ],
        );
      },
    );
  }
}

class _OfflineFilesScreen extends StatelessWidget {
  const _OfflineFilesScreen({required this.deviceId});
  final String deviceId;
  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    final key = GlobalKey<LoaderState<List<Json>>>();
    return Scaffold(
      appBar: AppBar(title: const Text('Offline files'), actions: [IconButton(tooltip: 'Send to ZIMRA now', icon: const Icon(Icons.cloud_sync_outlined), onPressed: () async { if (await guarded(context, () => api.post('/api/v1/devices/$deviceId/offline-files/sync'), done: 'Sent to ZIMRA.')) key.currentState?.reload(); })]),
      body: Loader<List<Json>>(key: key, load: () => api.getList('/api/v1/devices/$deviceId/offline-files'), builder: (context, items, _) => items.isEmpty
          ? const EmptyList('No offline files. They appear when receipts are issued while ZIMRA is unreachable.')
          : ListView(padding: const EdgeInsets.all(16), children: [
              for (final f in items)
                Padding(padding: const EdgeInsets.only(bottom: 10), child: AppCard(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  Row(children: [Text('Day ${f['fiscalDayNo']} · file ${f['sequence']}', style: const TextStyle(fontWeight: FontWeight.w700)), const Spacer(), StatusBadge(f['status']?.toString())]),
                  Text('${f['receiptCount']} receipts · ${f['attempts']} attempt(s)', style: TextStyle(fontSize: 12, color: Theme.of(context).hintColor)),
                  if (f['lastError'] != null) Text(f['lastError'].toString(), style: TextStyle(fontSize: 12, color: Colors.red.shade700)),
                ]))),
            ])),
    );
  }
}

class _StockScreen extends StatelessWidget {
  const _StockScreen({required this.deviceId});
  final String deviceId;
  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    return Scaffold(
      appBar: AppBar(title: const Text('Stock held at ZIMRA')),
      body: Loader<dynamic>(load: () => api.get('/api/v1/devices/$deviceId/stock'), builder: (context, d, _) {
        final items = d is Map ? (d['items'] ?? d['stock'] ?? d.values.whereType<List>().firstOrNull ?? []) as List : (d is List ? d : []);
        if (items.isEmpty) return const EmptyList('No stock is recorded at ZIMRA for this device.');
        return ListView(padding: const EdgeInsets.all(16), children: [
          for (final i in items) Padding(padding: const EdgeInsets.only(bottom: 8), child: AppCard(child: Text(i is Map ? i.entries.map((e) => '${humanize(e.key.toString())}: ${e.value}').join('\n') : i.toString()))),
        ]);
      }),
    );
  }
}

class RegisterDeviceScreen extends StatefulWidget {
  const RegisterDeviceScreen({super.key, this.embedded = false});
  /// Shown as the drawer page itself (no own app bar; stays on the page after registering).
  final bool embedded;
  @override
  State<RegisterDeviceScreen> createState() => _RegisterDeviceScreenState();
}

class _RegisterDeviceScreenState extends State<RegisterDeviceScreen> {
  final _id = TextEditingController(), _key = TextEditingController(), _serial = TextEditingController(), _model = TextEditingController(text: 'Server'), _ver = TextEditingController(text: 'v1');
  String _env = 'Test';
  Json? _taxpayer;
  bool _busy = false;

  Json _body() => {'zimraDeviceId': int.tryParse(_id.text.trim()) ?? 0, 'activationKey': _key.text.trim(), 'serialNo': _serial.text.trim(), 'modelName': _model.text.trim(), 'modelVersion': _ver.text.trim(), 'environment': _env};
  bool get _ready => (int.tryParse(_id.text.trim()) ?? 0) > 0 && _key.text.trim().length == 8 && _serial.text.trim().isNotEmpty;

  Future<void> _verify() async {
    setState(() => _busy = true);
    final r = await guardedValue(context, () => context.read<Api>().post('/api/v1/devices/verify', body: _body()));
    if (mounted) setState(() { _busy = false; _taxpayer = r; });
  }

  Future<void> _register() async {
    setState(() => _busy = true);
    final r = await guardedValue(context, () => context.read<Api>().post('/api/v1/devices/register', body: _body()));
    if (!mounted) return;
    setState(() => _busy = false);
    if (r != null) { toast(context, 'Device registered.'); if (widget.embedded) { for (final c in [_id, _key, _serial]) { c.clear(); } setState(() => _taxpayer = null); } else { Navigator.pop(context, true); } }
  }

  @override
  Widget build(BuildContext context) {
    final demoOnly = context.watch<AuthController>().session?.demoOnly ?? false;
    final body = ListView(padding: const EdgeInsets.all(16), children: [
        const Text('Enter the details ZIMRA gave you for this fiscal device. We check them with ZIMRA before registering.'),
        const SizedBox(height: 16),
        TextField(controller: _id, keyboardType: TextInputType.number, onChanged: (_) => setState(() {}), decoration: const InputDecoration(labelText: 'ZIMRA device ID')),
        const SizedBox(height: 12),
        TextField(controller: _key, maxLength: 8, textCapitalization: TextCapitalization.characters, onChanged: (_) => setState(() {}), decoration: const InputDecoration(labelText: 'Activation key (8 characters)')),
        const SizedBox(height: 4),
        TextField(controller: _serial, onChanged: (_) => setState(() {}), decoration: const InputDecoration(labelText: 'Device serial number')),
        const SizedBox(height: 12),
        Row(children: [Expanded(child: TextField(controller: _model, decoration: const InputDecoration(labelText: 'Model name'))), const SizedBox(width: 12), Expanded(child: TextField(controller: _ver, decoration: const InputDecoration(labelText: 'Model version')))]),
        const SizedBox(height: 12),
        DropdownButtonFormField<String>(isExpanded: true, initialValue: _env, decoration: const InputDecoration(labelText: 'Environment'), items: [const DropdownMenuItem(value: 'Test', child: Text('Test')), DropdownMenuItem(value: 'Production', enabled: !demoOnly, child: Text(demoOnly ? 'Production (after you pay)' : 'Production'))], onChanged: (v) => setState(() => _env = v ?? 'Test')),
        if (_taxpayer != null) Padding(padding: const EdgeInsets.only(top: 14), child: AppCard(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [const Text('ZIMRA knows this device as', style: TextStyle(fontWeight: FontWeight.w700)), const SizedBox(height: 6), Facts([('Taxpayer', _taxpayer!['taxPayerName']?.toString()), ('TIN', _taxpayer!['taxPayerTIN']?.toString() ?? _taxpayer!['taxPayerTin']?.toString()), ('VAT', _taxpayer!['vatNumber']?.toString()), ('Branch', _taxpayer!['deviceBranchName']?.toString())])]))),
        const SizedBox(height: 20),
        Row(children: [
          Expanded(child: OutlinedButton(onPressed: _busy || !_ready ? null : _verify, child: const Text('Check with ZIMRA'))),
          const SizedBox(width: 12),
          Expanded(child: FilledButton(onPressed: _busy || !_ready ? null : _register, child: const Text('Register'))),
        ]),
      ]);
    return widget.embedded ? body : Scaffold(appBar: AppBar(title: const Text('Register a device')), body: body);
  }
}
