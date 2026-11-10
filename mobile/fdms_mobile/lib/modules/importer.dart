import 'package:dio/dio.dart' show FormData, MultipartFile;
import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/api.dart';
import '../core/models.dart';
import '../ui/files.dart';
import '../ui/kit.dart';
import 'devices.dart';

/// Fiscalise invoices in bulk from an Excel sheet, or from a PDF or Word invoice: check them first, then send them to ZIMRA.
class ImporterScreen extends StatefulWidget {
  const ImporterScreen({super.key});
  @override
  State<ImporterScreen> createState() => _ImporterScreenState();
}

class _ImporterScreenState extends State<ImporterScreen> {
  List<Json> _devices = [];
  String? _deviceId;
  PlatformFile? _file;
  final _currency = TextEditingController(text: 'USD'), _tax = TextEditingController(text: '15'), _hs = TextEditingController(), _invoiceNo = TextEditingController();
  bool _inclusive = true, _submit = false, _busy = false, _ready = false;
  Json? _result;

  @override
  void initState() { super.initState(); _init(); }

  Future<void> _init() async {
    try {
      final d = (await loadDevices(context.read<Api>())).where((x) => x['isActive'] != false).toList();
      if (mounted) setState(() { _devices = d; _deviceId = d.length == 1 ? d.first['id'] : null; _ready = true; });
    } catch (e) { if (mounted) { setState(() => _ready = true); toast(context, e, error: true); } }
  }

  String get _kind {
    final n = (_file?.name ?? '').toLowerCase();
    return n.endsWith('.pdf') ? 'pdf' : n.endsWith('.docx') ? 'word' : 'excel';
  }

  Future<void> _pick() async {
    final r = await FilePicker.pickFiles(type: FileType.custom, allowedExtensions: ['xlsx', 'pdf', 'docx']);
    if (r.isNotEmpty && mounted) setState(() { _file = r.first; _result = null; });
  }

  Future<void> _template() async {
    try { final b = await context.read<Api>().bytes('/api/v1/import/excel-template'); if (mounted) await openBytes(context, b, 'invoice-import-template.xlsx'); } catch (e) { if (mounted) toast(context, e, error: true); }
  }

  Future<void> _run() async {
    if (_deviceId == null || _file == null) { toast(context, 'Choose the device and a file first.', error: true); return; }
    if (_submit && !await confirm(context, 'This fiscalises the invoices in the file with ZIMRA. Continue?', ok: 'Send to ZIMRA')) return;
    if (!mounted) return;
    setState(() { _busy = true; _result = null; });
    final api = context.read<Api>();
    try {
      final bytes = await _file!.readAsBytes();
      final r = await api.upload('/api/v1/devices/$_deviceId/import/$_kind', FormData.fromMap({'file': MultipartFile.fromBytes(bytes, filename: _file!.name)}), query: {
        'submit': _submit, 'taxInclusive': _inclusive, 'defaultTaxPercent': _tax.text.trim(), 'defaultCurrency': _currency.text.trim(), 'defaultHsCode': _hs.text.trim(), if (_kind != 'excel') 'invoiceNo': _invoiceNo.text.trim(),
      });
      if (mounted) setState(() => _result = Map<String, dynamic>.from(r as Map));
    } catch (e) { if (mounted) toast(context, e, error: true); } finally { if (mounted) setState(() => _busy = false); }
  }

  @override
  Widget build(BuildContext context) {
    if (!_ready) return const Center(child: CircularProgressIndicator());
    final r = _result;
    final items = ((r?['items'] ?? []) as List).cast<Map<String, dynamic>>();
    return ListView(padding: const EdgeInsets.all(16), children: [
      const Text('Import invoices from an Excel sheet, a PDF or a Word invoice. Check them first; send them to ZIMRA when they look right.'),
      const SizedBox(height: 12),
      DevicePicker(devices: _devices, value: _deviceId, onChanged: (v) => setState(() => _deviceId = v)),
      const SizedBox(height: 12),
      AppCard(onTap: _pick, child: Row(children: [const Icon(Icons.upload_file, size: 30), const SizedBox(width: 12), Expanded(child: Text(_file?.name ?? 'Choose a file (.xlsx, .pdf or .docx)', style: const TextStyle(fontWeight: FontWeight.w600))), const Icon(Icons.chevron_right)])),
      Align(alignment: Alignment.centerLeft, child: TextButton.icon(onPressed: _template, icon: const Icon(Icons.download_outlined, size: 18), label: const Text('Download the Excel template'))),
      Row(children: [
        Expanded(child: TextField(controller: _currency, textCapitalization: TextCapitalization.characters, decoration: const InputDecoration(labelText: 'Default currency'))),
        const SizedBox(width: 10),
        Expanded(child: TextField(controller: _tax, keyboardType: const TextInputType.numberWithOptions(decimal: true), decoration: const InputDecoration(labelText: 'Default tax %'))),
      ]),
      const SizedBox(height: 12),
      TextField(controller: _hs, keyboardType: TextInputType.number, decoration: const InputDecoration(labelText: 'Default HS code (when a row has none)')),
      if (_file != null && _kind != 'excel') ...[const SizedBox(height: 12), TextField(controller: _invoiceNo, decoration: const InputDecoration(labelText: 'Invoice number (if the document has none)'))],
      SwitchListTile(contentPadding: EdgeInsets.zero, title: const Text('Prices include tax'), value: _inclusive, onChanged: (v) => setState(() => _inclusive = v)),
      SwitchListTile(contentPadding: EdgeInsets.zero, title: const Text('Send to ZIMRA now'), subtitle: const Text('Off = only check the file'), value: _submit, onChanged: (v) => setState(() => _submit = v)),
      const SizedBox(height: 8),
      FilledButton.icon(onPressed: _busy ? null : _run, icon: _busy ? const SizedBox(width: 18, height: 18, child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white)) : Icon(_submit ? Icons.send : Icons.fact_check_outlined), label: Text(_submit ? 'Check and send' : 'Check the file')),
      if (r != null) ...[
        const SectionTitle('Result'),
        ResponsiveGrid(minTileWidth: 120, children: [Kpi('Invoices', '${r['total']}'), Kpi('Good', '${r['succeeded']}', color: const Color(0xFF059669)), Kpi('Problems', '${r['failed']}', color: (r['failed'] ?? 0) > 0 ? const Color(0xFFDC2626) : null)]),
        const SizedBox(height: 10),
        for (final e in ((r['errors'] ?? []) as List)) Padding(padding: const EdgeInsets.only(bottom: 6), child: Text('• $e', style: TextStyle(color: Colors.red.shade700))),
        for (final i in items) Padding(padding: const EdgeInsets.only(bottom: 8), child: AppCard(padding: const EdgeInsets.all(12), child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Row(children: [Expanded(child: Text((i['invoiceNo'] ?? 'Invoice').toString(), style: const TextStyle(fontWeight: FontWeight.w700))), if (i['total'] != null) AmountBox(money(i['total'])), const SizedBox(width: 8), StatusBadge((i['status'] ?? '').toString())]),
          if (i['error'] != null) Text('${i['errorCode'] ?? ''} ${i['error']}', style: TextStyle(fontSize: 12, color: Colors.red.shade700)),
          for (final w in ((i['warnings'] ?? []) as List)) Text('• $w', style: const TextStyle(fontSize: 12, color: Color(0xFFD97706))),
        ]))),
      ],
      const SizedBox(height: 24),
    ]);
  }
}
