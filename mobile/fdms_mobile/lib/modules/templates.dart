import 'dart:async';
import 'dart:typed_data';

import 'package:dio/dio.dart' show FormData, MultipartFile;
import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/api.dart';
import '../core/auth.dart';
import '../core/models.dart';
import 'invoice_builder.dart';
import '../ui/kit.dart';

String _s(Json j, String k) => (j[k] ?? '').toString();

Json defaultTemplateSettings() => {
      'accent': '#2563eb', 'showLogo': true, 'logoAlign': 'left', 'showBuyer': true, 'showPayments': true, 'showHsCode': false, 'showQuantity': true, 'showUnitPrice': true, 'showLineTax': true,
      'headerStyle': 'plain', 'titleStyle': 'band', 'titleAlign': 'center', 'tableStyle': 'lines', 'partiesLayout': 'stacked', 'totalsAlign': 'full', 'qrPosition': 'center', 'qrSize': 'medium',
      'rowSpacing': 'normal', 'logoSize': 'medium', 'margins': 'normal', 'fontSize': 10,
    };

/// The company's invoice designs: pick one to change, or make a new one (also copying the look of an invoice from another system with AI).
class TemplatesScreen extends StatefulWidget {
  const TemplatesScreen({super.key});
  @override
  State<TemplatesScreen> createState() => _TemplatesScreenState();
}

class _TemplatesScreenState extends State<TemplatesScreen> {
  final _key = GlobalKey<LoaderState<List<Json>>>();

  Future<void> _open(Json? t) async {
    final changed = await Navigator.push<bool>(context, MaterialPageRoute(builder: (_) => TemplateEditorScreen(t)));
    if (changed == true) _key.currentState?.reload();
  }

  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    final can = context.watch<AuthController>().can('templates.manage');
    return Scaffold(
      backgroundColor: Colors.transparent,
      floatingActionButton: can ? FloatingActionButton.extended(onPressed: () => _open(null), icon: const Icon(Icons.add), label: const Text('New design')) : null,
      body: Loader<List<Json>>(
        key: _key,
        load: () => api.getList('/api/v1/templates'),
        builder: (context, items, _) => items.isEmpty
            ? const EmptyList('No invoice design yet. Create one, or copy the look of the invoice you already use.', icon: Icons.web_outlined)
            : ListView(padding: const EdgeInsets.fromLTRB(16, 12, 16, 90), children: [
                for (final t in items)
                  Padding(padding: const EdgeInsets.only(bottom: 10), child: AppCard(
                    onTap: () => _open(t),
                    child: Row(children: [
                      Container(width: 44, height: 44, decoration: BoxDecoration(color: _hex((t['settings'] as Map?)?['accent']?.toString()).withValues(alpha: 0.15), borderRadius: BorderRadius.circular(12)), child: Icon(Icons.receipt_long, color: _hex((t['settings'] as Map?)?['accent']?.toString()))),
                      const SizedBox(width: 12),
                      Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                        Text(_s(t, 'name'), style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 15)),
                        Text(t['branchName'] != null ? 'Branch: ${t['branchName']}' : 'Whole company', style: TextStyle(fontSize: 12, color: Theme.of(context).hintColor)),
                        Wrap(spacing: 6, runSpacing: 4, children: [Badge2('${t['deviceCount'] ?? 0} device(s)', tone: (t['deviceCount'] ?? 0) > 0 ? Tone.ok : Tone.warn), if (t['hasLogo'] == true) const Badge2('Logo', tone: Tone.info), if ((((t['settings'] as Map?)?['canvas'] as Map?)?['elements'] as List?)?.isNotEmpty == true) const Badge2('Built design', tone: Tone.info)]),
                      ])),
                      IconButton(
                        tooltip: 'Invoice builder',
                        icon: const Icon(Icons.dashboard_customize_outlined),
                        onPressed: () async { await Navigator.push(context, MaterialPageRoute(builder: (_) => InvoiceBuilderScreen(templateId: t['id'].toString()))); _key.currentState?.reload(); },
                      ),
                    ]),
                  )),
              ]),
      ),
    );
  }
}

Color _hex(String? v) {
  final h = (v ?? '').replaceAll('#', '');
  final n = int.tryParse(h.length == 6 ? 'FF$h' : h, radix: 16);
  return n == null ? kBrand : Color(n);
}


const _swatches = ['#2563eb', '#0f766e', '#7c3aed', '#be123c', '#c2410c', '#15803d', '#0369a1', '#334155', '#111827', '#a16207'];

class TemplateEditorScreen extends StatefulWidget {
  const TemplateEditorScreen(this.existing, {super.key});
  final Json? existing;
  @override
  State<TemplateEditorScreen> createState() => _TemplateEditorScreenState();
}

class _TemplateEditorScreenState extends State<TemplateEditorScreen> with SingleTickerProviderStateMixin {
  late final TabController _tabs = TabController(length: 4, vsync: this);
  late Json _settings;
  final _name = TextEditingController();
  final _ctl = <String, TextEditingController>{};
  String? _id, _branchId;
  List<Json> _branches = [];
  Uint8List? _preview;
  String _format = 'a4';
  bool _previewBusy = false, _saving = false, _analysing = false, _hasLogo = false;
  Object? _previewError;
  Timer? _debounce;
  int _previewGen = 0;
  List<String> _notes = [];

  Api get api => context.read<Api>();

  @override
  void initState() {
    super.initState();
    final e = widget.existing;
    _id = e?['id']?.toString();
    _branchId = e?['branchId']?.toString();
    _hasLogo = e?['hasLogo'] == true;
    _name.text = e == null ? 'Company default' : _s(e, 'name');
    _settings = {...defaultTemplateSettings(), if (e?['settings'] is Map) ...Map<String, dynamic>.from(e!['settings'] as Map)};
    _tabs.addListener(() { if (_tabs.index == 0 && !_tabs.indexIsChanging) _refresh(); });
    _loadBranches();
    WidgetsBinding.instance.addPostFrameCallback((_) => _refresh());
  }

  @override
  void dispose() { _debounce?.cancel(); _tabs.dispose(); super.dispose(); }

  Future<void> _loadBranches() async {
    try { final b = await api.getList('/api/v1/branches'); if (mounted) setState(() => _branches = b); } catch (_) {}
  }

  TextEditingController _c(String key) => _ctl.putIfAbsent(key, () => TextEditingController(text: _settings[key]?.toString() ?? ''));

  Json _clean() {
    final out = <String, dynamic>{};
    _settings.forEach((k, v) {
      if (v is String && v.trim().isEmpty) { out[k] = null; } else { out[k] = v; }
    });
    return out;
  }

  void _set(String k, dynamic v, {bool refresh = true}) {
    setState(() => _settings[k] = v);
    if (refresh) _later();
  }

  void _later() { _debounce?.cancel(); _debounce = Timer(const Duration(milliseconds: 700), () { if (_tabs.index == 0) _refresh(); }); }

  Future<void> _refresh() async {
    final gen = ++_previewGen;
    setState(() { _previewBusy = true; _previewError = null; });
    try {
      final b = await api.bytes('/api/v1/templates/preview', method: 'POST', body: {'settings': _clean(), 'format': _format, 'templateId': _id});
      if (mounted && gen == _previewGen) setState(() { _preview = b; _previewBusy = false; });
    } catch (e) { if (mounted && gen == _previewGen) setState(() { _previewError = e; _previewBusy = false; }); }
  }

  Future<void> _save() async {
    if (_name.text.trim().isEmpty) { toast(context, 'Give the design a name.', error: true); return; }
    setState(() => _saving = true);
    final r = await guarded(context, () async {
      final saved = await api.put('/api/v1/templates', body: {'id': _id, 'branchId': _branchId, 'name': _name.text.trim(), 'settings': _clean()});
      if (saved is Map) { _id = saved['id']?.toString(); _hasLogo = saved['hasLogo'] == true; }
    }, done: 'Saved. Invoices from the devices on this design use it now.');
    if (mounted) setState(() => _saving = false);
    if (r && mounted && !_logoPending) Navigator.pop(context, true);
  }

  bool get _logoPending => false;

  Future<void> _delete() async {
    if (_id == null) return;
    if (!await confirm(context, 'Delete this design? Devices fall back to the company default.', ok: 'Delete', danger: true) || !mounted) return;
    final nav = Navigator.of(context);
    if (await guarded(context, () async { await api.delete('/api/v1/templates/$_id'); }, done: 'Deleted.')) nav.pop(true);
  }

  Future<void> _logo() async {
    if (_id == null) { toast(context, 'Save the design first, then add the logo.', error: true); return; }
    final pick = await FilePicker.pickFiles(type: FileType.image);
    final f = pick.firstOrNull;
    if (f == null || !mounted) return;
    final bytes = await f.readAsBytes();
    if (!mounted) return;
    final ok = await guarded(context, () async { await api.upload('/api/v1/templates/$_id/logo', FormData.fromMap({'file': MultipartFile.fromBytes(bytes, filename: f.name)})); }, done: 'Logo saved.');
    if (ok) { setState(() => _hasLogo = true); _refresh(); }
  }

  Future<void> _removeLogo() async {
    if (_id == null) return;
    if (await guarded(context, () async { await api.delete('/api/v1/templates/$_id/logo'); }, done: 'Logo removed.')) { setState(() => _hasLogo = false); _refresh(); }
  }

  Future<void> _analyse() async {
    final pick = await FilePicker.pickFiles(type: FileType.custom, allowedExtensions: ['pdf', 'png', 'jpg', 'jpeg', 'webp']);
    final f = pick.firstOrNull;
    if (f == null || !mounted) return;
    final bytes = await f.readAsBytes();
    if (bytes.length > 4000000) { if (mounted) toast(context, 'The file must be at most 4 MB.', error: true); return; }
    if (!mounted) return;
    setState(() => _analysing = true);
    try {
      final r = await api.upload('/api/v1/templates/analyze', FormData.fromMap({'file': MultipartFile.fromBytes(bytes, filename: f.name)}));
      final s = Map<String, dynamic>.from((r as Map)['settings'] as Map);
      if (!mounted) return;
      setState(() {
        _settings = {...defaultTemplateSettings(), ...s};
        _ctl.clear();
        _notes = [if ((r['summary'] ?? '').toString().isNotEmpty) r['summary'].toString(), ...((r['notes'] ?? []) as List).map((e) => e.toString())];
      });
      toast(context, 'Your invoice was read. Check the preview, adjust, then save.');
      _tabs.animateTo(0);
      _refresh();
    } catch (e) { if (mounted) toast(context, e, error: true); } finally { if (mounted) setState(() => _analysing = false); }
  }

  // ---- small builders ------------------------------------------------------------------------------------------------

  Widget _choice(String label, String key, List<(String, String)> options) => Padding(
        padding: const EdgeInsets.only(bottom: 14),
        child: DropdownButtonFormField<String>(
          initialValue: options.any((o) => o.$1 == _settings[key]) ? _settings[key] as String : options.first.$1,
          isExpanded: true,
          decoration: InputDecoration(labelText: label),
          items: [for (final o in options) DropdownMenuItem(value: o.$1, child: Text(o.$2))],
          onChanged: (v) => _set(key, v),
        ),
      );

  Widget _text(String label, String key, {int lines = 1, String? hint, TextInputType? kb}) => Padding(
        padding: const EdgeInsets.only(bottom: 14),
        child: TextField(controller: _c(key), minLines: lines, maxLines: lines, keyboardType: kb, decoration: InputDecoration(labelText: label, hintText: hint), onChanged: (v) => _set(key, v)),
      );

  Widget _toggle(String label, String key) => SwitchListTile(contentPadding: EdgeInsets.zero, title: Text(label), value: _settings[key] == true, onChanged: (v) => _set(key, v));

  @override
  Widget build(BuildContext context) {
    final accent = _hex(_settings['accent']?.toString());
    return Scaffold(
      appBar: AppBar(
        title: Text(widget.existing == null ? 'New invoice design' : 'Edit design'),
        actions: [
          if (_id != null) IconButton(tooltip: 'Delete', icon: const Icon(Icons.delete_outline), onPressed: _delete),
          TextButton(onPressed: _saving ? null : _save, child: const Text('Save')),
        ],
        bottom: TabBar(controller: _tabs, isScrollable: true, tabAlignment: TabAlignment.start, tabs: const [Tab(text: 'Preview'), Tab(text: 'Look'), Tab(text: 'Content'), Tab(text: 'Words')]),
      ),
      body: TabBarView(controller: _tabs, children: [
        // ---- preview ----
        Column(children: [
          Padding(padding: const EdgeInsets.fromLTRB(16, 12, 16, 4), child: Row(children: [
            Expanded(child: SegmentedButton<String>(segments: const [ButtonSegment(value: 'a4', label: Text('A4')), ButtonSegment(value: 'receipt', label: Text('80 mm'))], selected: {_format}, onSelectionChanged: (s) { setState(() => _format = s.first); _refresh(); })),
            IconButton(tooltip: 'Refresh', onPressed: _refresh, icon: const Icon(Icons.refresh)),
          ])),
          if (_notes.isNotEmpty) Padding(padding: const EdgeInsets.fromLTRB(16, 4, 16, 4), child: Container(width: double.infinity, padding: const EdgeInsets.all(10), decoration: BoxDecoration(color: Colors.amber.withValues(alpha: 0.14), borderRadius: BorderRadius.circular(12)), child: Text(_notes.join('\n'), style: const TextStyle(fontSize: 12)))),
          Expanded(child: Padding(padding: const EdgeInsets.all(12), child: Stack(children: [
            Positioned.fill(child: DecoratedBox(decoration: BoxDecoration(color: Colors.grey.withValues(alpha: 0.12), borderRadius: BorderRadius.circular(16)), child: ClipRRect(borderRadius: BorderRadius.circular(16), child: _preview == null
                ? (_previewError != null ? ErrorState(_previewError!, onRetry: _refresh) : const Center(child: CircularProgressIndicator()))
                : InteractiveViewer(minScale: 0.8, maxScale: 5, child: Center(child: Image.memory(_preview!, fit: BoxFit.contain, gaplessPlayback: true)))))),
            if (_previewBusy && _preview != null) const Positioned(top: 12, right: 12, child: SizedBox(width: 22, height: 22, child: CircularProgressIndicator(strokeWidth: 2.5))),
            const Positioned(left: 0, right: 0, bottom: 6, child: Text('Pinch to zoom. The preview uses sample data.', textAlign: TextAlign.center, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 11, color: Colors.grey))),
          ]))),
        ]),
        // ---- look ----
        ListView(padding: const EdgeInsets.all(16), children: [
          AppCard(onTap: _analysing ? null : _analyse, child: Row(children: [
            const CircleAvatar(backgroundColor: Color(0x1F7C3AED), child: Icon(Icons.auto_awesome, color: Color(0xFF7C3AED))),
            const SizedBox(width: 12),
            const Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [Text('Copy my invoice', style: TextStyle(fontWeight: FontWeight.w800)), Text('Pick a PDF or photo of the invoice you use now (SAP, Sage, Pastel, POS…) and the look is copied.', style: TextStyle(fontSize: 12))])),
            if (_analysing) const SizedBox(width: 20, height: 20, child: CircularProgressIndicator(strokeWidth: 2)) else const Icon(Icons.chevron_right),
          ])),
          const SizedBox(height: 16),
          TextField(controller: _name, decoration: const InputDecoration(labelText: 'Design name')),
          const SizedBox(height: 14),
          DropdownButtonFormField<String>(initialValue: _branches.any((b) => b['id'] == _branchId) ? _branchId : '', isExpanded: true, decoration: const InputDecoration(labelText: 'Used by'), items: [const DropdownMenuItem(value: '', child: Text('The whole company')), for (final b in _branches) DropdownMenuItem(value: b['id'] as String, child: Text('Branch: ${b['name']}', overflow: TextOverflow.ellipsis))], onChanged: (v) => setState(() => _branchId = (v ?? '').isEmpty ? null : v)),
          const SectionTitle('Colour'),
          Wrap(spacing: 10, runSpacing: 10, children: [
            for (final h in _swatches) GestureDetector(onTap: () => _set('accent', h), child: Container(width: 38, height: 38, decoration: BoxDecoration(color: _hex(h), shape: BoxShape.circle, border: Border.all(color: accent == _hex(h) ? Theme.of(context).colorScheme.onSurface : Colors.transparent, width: 3)))),
          ]),
          const SizedBox(height: 10),
          TextField(controller: _c('accent'), decoration: InputDecoration(labelText: 'Or type a colour', hintText: '#2563eb', prefixIcon: Icon(Icons.circle, color: accent)), onChanged: (v) { if (RegExp(r'^#[0-9a-fA-F]{6}$').hasMatch(v)) _set('accent', v.toLowerCase()); }),
          const SectionTitle('Logo'),
          AppCard(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(_hasLogo ? 'A logo is set.' : 'No logo yet.'),
            const SizedBox(height: 8),
            Wrap(spacing: 8, children: [OutlinedButton.icon(onPressed: _logo, icon: const Icon(Icons.image_outlined), label: Text(_hasLogo ? 'Change logo' : 'Add logo')), if (_hasLogo) TextButton(onPressed: _removeLogo, child: const Text('Remove'))]),
            _toggle('Show the logo', 'showLogo'),
          ])),
          const SizedBox(height: 14),
          _choice('Logo position', 'logoAlign', const [('left', 'Left'), ('center', 'Centre'), ('right', 'Right')]),
          _choice('Logo size', 'logoSize', const [('small', 'Small'), ('medium', 'Medium'), ('large', 'Large')]),
          const SectionTitle('Layout'),
          _choice('Company header', 'headerStyle', const [('plain', 'Plain'), ('band', 'On a coloured band')]),
          _choice('Invoice title', 'titleStyle', const [('band', 'Coloured band'), ('plain', 'Plain'), ('underline', 'Underlined')]),
          _choice('Title position', 'titleAlign', const [('left', 'Left'), ('center', 'Centre'), ('right', 'Right')]),
          _choice('Table', 'tableStyle', const [('lines', 'Lines'), ('striped', 'Striped'), ('grid', 'Grid'), ('solid', 'Solid header')]),
          _choice('Invoice and buyer details', 'partiesLayout', const [('stacked', 'One under the other'), ('columns', 'Side by side')]),
          _choice('Totals', 'totalsAlign', const [('full', 'Across the page'), ('right', 'Compact, on the right')]),
          _choice('QR code position', 'qrPosition', const [('center', 'Bottom centre'), ('left', 'Bottom left'), ('right', 'Bottom right'), ('top-left', 'Top left'), ('top-right', 'Top right'), ('with-totals', 'Beside the totals')]),
          _choice('QR code size', 'qrSize', const [('small', 'Small'), ('medium', 'Medium'), ('large', 'Large')]),
          _choice('Row spacing', 'rowSpacing', const [('compact', 'Compact'), ('normal', 'Normal'), ('relaxed', 'Relaxed')]),
          _choice('Margins', 'margins', const [('narrow', 'Narrow'), ('normal', 'Normal'), ('wide', 'Wide')]),
          Text('Text size: ${_settings['fontSize'] ?? 10} pt', style: const TextStyle(fontWeight: FontWeight.w600)),
          Slider(min: 8, max: 13, divisions: 5, value: ((_settings['fontSize'] ?? 10) as num).toDouble().clamp(8, 13), label: '${_settings['fontSize'] ?? 10}', onChanged: (v) => _set('fontSize', v.round())),
          const SizedBox(height: 24),
        ]),
        // ---- content ----
        ListView(padding: const EdgeInsets.all(16), children: [
          _text('Name printed at the top', 'displayName', hint: 'Your trading name (blank = the name ZIMRA holds)'),
          _text('Invoice title (must contain FISCAL TAX INVOICE)', 'invoiceTitle', hint: 'FISCAL TAX INVOICE'),
          _text('Company details under the name', 'headerText', lines: 3, hint: 'One line per row'),
          _text('Address', 'address', lines: 2), _text('Telephone', 'phone', kb: TextInputType.phone), _text('Email', 'email', kb: TextInputType.emailAddress), _text('Website', 'website', kb: TextInputType.url),
          const SectionTitle('What to show'),
          _toggle('Buyer details', 'showBuyer'), _toggle('How it was paid', 'showPayments'), _toggle('HS code column', 'showHsCode'), _toggle('Quantity column', 'showQuantity'), _toggle('Unit price column', 'showUnitPrice'), _toggle('Tax rate on each line', 'showLineTax'),
          const SizedBox(height: 14),
          _text('Bank details', 'bankDetails', lines: 3, hint: 'Bank: [BANK NAME]\nAccount: [ACCOUNT NUMBER]'),
          _text('Terms', 'terms', lines: 3), _text('Footer', 'footerText', lines: 2),
          const Text('Words in [square brackets] such as [Company Address], [Bank Name] and [Account Number] are filled in from your company and bank details.', style: TextStyle(fontSize: 12, color: Colors.grey)),
          const SizedBox(height: 24),
        ]),
        // ---- words ----
        ListView(padding: const EdgeInsets.all(16), children: [
          const Padding(padding: EdgeInsets.only(bottom: 12), child: Text('Use your own words for the labels on the invoice. Leave a box empty to keep ours.', style: TextStyle(color: Colors.grey))),
          _text('Invoice number', 'labelInvoiceNo'), _text('Date', 'labelDate'), _text('Buyer', 'labelBuyer'), _text('Item column', 'labelItem'), _text('HS code column', 'labelHsCode'), _text('Quantity column', 'labelQuantity'),
          _text('Price column', 'labelPrice'), _text('Tax column', 'labelTax'), _text('Total column', 'labelTotal'), _text('Grand total line', 'labelGrandTotal', hint: 'TOTAL'),
          const SizedBox(height: 24),
        ]),
      ]),
    );
  }
}
