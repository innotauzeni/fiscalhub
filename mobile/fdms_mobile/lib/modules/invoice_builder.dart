import 'dart:convert';
import 'dart:math' as math;

import 'package:dio/dio.dart' show FormData, MultipartFile;
import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:provider/provider.dart';

import '../core/api.dart';
import '../core/models.dart';
import '../ui/files.dart';
import '../ui/kit.dart';

/// The invoice builder on the phone: the same free A4 layout as the web console. Tap an element to select it, drag to move it, pull the corner to resize it,
/// and open Properties to change what it shows and how it looks. Saving makes it the template's A4 design; Preview shows exactly how it prints.
const double kPageW = 210, kPageH = 297;
const List<String> kRequired = ['items', 'totals', 'qr', 'fiscal'];

const Map<String, String> kTypeLabels = {
  'text': 'Text', 'field': 'Invoice value', 'image': 'Picture', 'logo': 'Logo', 'shape': 'Box / circle', 'line': 'Line',
  'items': 'Items table', 'totals': 'Totals', 'qr': 'ZIMRA QR', 'fiscal': 'Fiscal details',
};
const Map<String, IconData> kTypeIcons = {
  'text': Icons.text_fields_rounded, 'field': Icons.data_object_rounded, 'image': Icons.image_outlined, 'logo': Icons.business_rounded, 'shape': Icons.crop_square_rounded,
  'line': Icons.horizontal_rule_rounded, 'items': Icons.table_rows_outlined, 'totals': Icons.functions_rounded, 'qr': Icons.qr_code_2_rounded, 'fiscal': Icons.verified_outlined,
};
const List<(String, String)> kItemColumns = [('no', '#'), ('item', 'Description'), ('hs', 'HS Code'), ('qty', 'Qty'), ('price', 'Unit price'), ('tax', 'VAT'), ('total', 'Amount')];
const List<(String, String)> kTotalRows = [('subtotal', 'Subtotal ({currency})'), ('taxes', 'VAT ({rate})'), ('total', 'TOTAL ({currency})'), ('payments', 'Payments')];
const List<String> kSwatches = [
  '#0f172a', '#334155', '#64748b', '#cbd5e1', '#ffffff', '#1d4ed8', '#2563eb', '#0ea5e9', '#059669', '#16a34a',
  '#f59e0b', '#ea580c', '#dc2626', '#9d174d', '#db2777', '#f9a8d4', '#fdf2f8', '#7c3aed', '#f1f5f9', '#fce7f3',
];

String _newId() => '${DateTime.now().microsecondsSinceEpoch.toRadixString(36)}${math.Random().nextInt(9999)}';
Color hexColor(String? hex, [Color fallback = Colors.transparent]) {
  if (hex == null || !RegExp(r'^#[0-9a-fA-F]{6}$').hasMatch(hex)) return fallback;
  return Color(int.parse('FF${hex.substring(1)}', radix: 16));
}
String _money(num v) => v.toStringAsFixed(2).replaceAllMapped(RegExp(r'\B(?=(\d{3})+(?!\d))'), (_) => ',');

Json _style([Json? extra]) => {'fontSize': 10, 'color': '#0f172a', 'align': 'left', 'vAlign': 'top', 'padding': 1, 'lineHeight': 1.2, 'opacity': 1, ...?extra};

/// A new element of a kind with a sensible size, near the middle of the page.
Json makeElement(String type) {
  Json base(double w, double h, [Json? st]) => {'id': _newId(), 'type': type, 'x': ((kPageW - w) / 2).roundToDouble(), 'y': 120.0, 'w': w, 'h': h, 'style': _style(st)};
  final e = switch (type) {
    'text' => base(80, 12, {'fontSize': 11}),
    'field' => base(70, 8),
    'image' => base(40, 30, {'align': 'center', 'vAlign': 'middle', 'padding': 0}),
    'logo' => base(45, 25, {'align': 'center', 'vAlign': 'middle', 'padding': 0}),
    'shape' => base(60, 30, {'fill': '#e0e7ff', 'padding': 0}),
    'line' => base(120, 2, {'border': '#94a3b8', 'borderWidth': 1, 'padding': 0}),
    'items' => base(186, 90, {'fontSize': 9, 'headerFill': '#1d4ed8', 'headerColor': '#ffffff', 'stripeFill': '#f1f5f9', 'lineColor': '#e2e8f0', 'rowPadding': 1.5}),
    'totals' => base(80, 28, {'fontSize': 10, 'headerFill': '#1d4ed8', 'headerColor': '#ffffff', 'lineColor': '#e2e8f0', 'rowPadding': 1.5}),
    'qr' => base(30, 34, {'fontSize': 8, 'padding': 0}),
    _ => base(130, 22, {'fontSize': 7.5, 'color': '#475569'}),
  };
  if (type == 'text') e['text'] = 'Your text';
  if (type == 'field') { e['field'] = 'invoiceNo'; e['label'] = 'Invoice No:'; }
  if (type == 'shape') e['shape'] = 'rect';
  if (type == 'items') e['columns'] = ['no', 'item', 'hs', 'qty', 'price', 'total'];
  if (type == 'totals') e['columns'] = ['subtotal', 'taxes', 'total'];
  if (type == 'qr') e['label'] = 'Scan to verify';
  return e;
}

double _d(dynamic v, [double dflt = 0]) => v is num ? v.toDouble() : dflt;
Json _st(Json e) => Map<String, dynamic>.from((e['style'] ?? {}) as Map);

class InvoiceBuilderScreen extends StatefulWidget {
  const InvoiceBuilderScreen({super.key, required this.templateId});
  final String templateId;
  @override
  State<InvoiceBuilderScreen> createState() => _InvoiceBuilderScreenState();
}

class _InvoiceBuilderScreenState extends State<InvoiceBuilderScreen> {
  Json? _tpl, _sample;
  List<Json> _fields = [], _assets = [], _presets = [];
  List<String> _fonts = [];
  final Map<String, Uint8List> _images = {};
  Uint8List? _logo;
  Json _layout = {'background': '#ffffff', 'elements': <Json>[]};
  String? _sel;
  final List<String> _past = [], _future = [];
  bool _dirty = false, _busy = false, _loading = true;
  String? _error;
  double _zoom = 1;
  Set<String> _known = {};

  Api get _api => context.read<Api>();
  List<Json> get _els => (_layout['elements'] as List).cast<Json>();
  Json? get _selected => _sel == null ? null : _els.where((e) => e['id'] == _sel).firstOrNull;
  /// What ZIMRA requires and the page lacks; 'title' = the FISCAL TAX INVOICE heading.
  List<String> get _missing => [if (!_hasTitle) 'title', ...kRequired.where((t) => !_els.any((e) => e['type'] == t))];
  bool get _hasTitle => _els.any((e) => (e['type'] == 'field' && e['field'] == 'title') ||
      (e['type'] == 'text' && ((e['text'] ?? '').toString().contains('{title}') || (e['text'] ?? '').toString().toUpperCase().contains('FISCAL TAX INVOICE'))));
  /// A text box that is just "INVOICE" (or similar) as a heading instead of ZIMRA's wording.
  Json? get _bareHeading => _els.where((e) => e['type'] == 'text' && (e['text'] ?? '').toString().split('\n').any(isBareHeading)).firstOrNull;
  String _label(String m) => m == 'title' ? 'the title (FISCAL TAX INVOICE)' : kTypeLabels[m] ?? m;
  void _addRequired(String m) => m == 'title'
      ? _add('field', {'field': 'title', 'label': null, 'x': 40.0, 'y': 40.0, 'w': 130.0, 'h': 12.0, 'style': _style({'fontSize': 18, 'bold': true, 'align': 'center', 'vAlign': 'middle', 'letterSpacing': 0.1})})
      : _add(m);

  @override
  void initState() { super.initState(); _load(); }

  Future<void> _load() async {
    try {
      final list = (await _api.get('/api/v1/templates') as List).cast<Map>().map((e) => Map<String, dynamic>.from(e)).toList();
      final meta = await _api.getMap('/api/v1/templates/fields');
      final sample = await _api.getMap('/api/v1/templates/sample');
      final assets = (await _api.get('/api/v1/templates/assets') as List).cast<Map>().map((e) => Map<String, dynamic>.from(e)).toList();
      final t = list.where((x) => x['id'] == widget.templateId).firstOrNull;
      if (t == null) throw ApiException('That template no longer exists.');
      _tpl = t; _sample = sample; _assets = assets;
      _fields = ((meta['fields'] ?? []) as List).cast<Map>().map((e) => Map<String, dynamic>.from(e)).toList();
      _fonts = ((meta['fonts'] ?? []) as List).map((e) => e.toString()).toList();
      _known = _fields.map((f) => f['key'].toString()).toSet();
      final canvas = (t['settings'] as Map?)?['canvas'];
      if (canvas is Map && (canvas['elements'] as List?)?.isNotEmpty == true) _layout = jsonDecode(jsonEncode(canvas)) as Json;
      if (t['hasLogo'] == true) { try { _logo = await _api.bytes('/api/v1/templates/${t['id']}/logo'); } catch (_) {} }
      for (final a in assets) { try { _images[a['id'].toString()] = await _api.bytes('/api/v1/templates/assets/${a['id']}'); } catch (_) {} }
      if (mounted) setState(() => _loading = false);
      if (_els.isEmpty && mounted) WidgetsBinding.instance.addPostFrameCallback((_) => _choosePreset());
    } catch (e) {
      if (mounted) setState(() { _loading = false; _error = e.toString(); });
    }
  }

  // ---- history and changes ----
  void _commit() { _past.add(jsonEncode(_layout)); if (_past.length > 80) _past.removeAt(0); _future.clear(); _dirty = true; }
  void _undo() { if (_past.isEmpty) return; setState(() { _future.add(jsonEncode(_layout)); _layout = jsonDecode(_past.removeLast()) as Json; _dirty = true; }); }
  void _redo() { if (_future.isEmpty) return; setState(() { _past.add(jsonEncode(_layout)); _layout = jsonDecode(_future.removeLast()) as Json; _dirty = true; }); }
  void _change(Json Function(Json e) fn, {bool record = true}) {
    if (_sel == null) return;
    setState(() {
      if (record) _commit();
      _layout = {..._layout, 'elements': _els.map((e) => e['id'] == _sel ? fn(Map<String, dynamic>.from(e)) : e).toList()};
    });
  }
  void _add(String type, [Json? extra]) {
    final e = {...makeElement(type), ...?extra};
    setState(() { _commit(); _layout = {..._layout, 'elements': [..._els, e]}; _sel = e['id'] as String; });
  }
  void _remove() { if (_sel == null) return; setState(() { _commit(); _layout = {..._layout, 'elements': _els.where((e) => e['id'] != _sel).toList()}; _sel = null; }); }
  void _duplicate() {
    final e = _selected; if (e == null) return;
    final c = jsonDecode(jsonEncode(e)) as Json..['id'] = _newId()..['x'] = math.min(_d(e['x']) + 4, kPageW - _d(e['w']))..['y'] = math.min(_d(e['y']) + 4, kPageH - _d(e['h']))..['locked'] = false;
    setState(() { _commit(); _layout = {..._layout, 'elements': [..._els, c]}; _sel = c['id'] as String; });
  }
  void _order(bool front) {
    final e = _selected; if (e == null) return;
    setState(() { _commit(); final rest = _els.where((x) => x['id'] != _sel).toList(); _layout = {..._layout, 'elements': front ? [...rest, e] : [e, ...rest]}; });
  }

  // ---- designs ----
  Future<void> _choosePreset() async {
    if (_presets.isEmpty) {
      try { _presets = (jsonDecode(await rootBundle.loadString('assets/builder/presets.json')) as List).cast<Map>().map((e) => Map<String, dynamic>.from(e)).toList(); }
      catch (_) { /* no designs to offer */ }
      if (!mounted) return;
    }
    final p = await showModalBottomSheet<Json>(
      context: context, isScrollControlled: true, showDragHandle: true,
      builder: (ctx) => SafeArea(child: ListView(shrinkWrap: true, padding: const EdgeInsets.fromLTRB(16, 0, 16, 16), children: [
        const Text('Start from a design', style: TextStyle(fontSize: 18, fontWeight: FontWeight.w800)),
        const SizedBox(height: 4),
        Text('Everything can be changed afterwards.', style: TextStyle(color: Theme.of(ctx).hintColor)),
        const SizedBox(height: 12),
        for (final p in _presets)
          Card(child: ListTile(
            leading: _Swatch(p['key'].toString()),
            title: Text(p['name'].toString(), style: const TextStyle(fontWeight: FontWeight.w700)),
            subtitle: Text(p['hint'].toString()),
            onTap: () => Navigator.pop(ctx, p),
          )),
      ])),
    );
    if (p == null || !mounted) return;
    if (_els.isNotEmpty && !await confirm(context, 'Replace the page with the “${p['name']}” design? (Undo brings it back.)', ok: 'Replace')) return;
    setState(() { _commit(); _layout = jsonDecode(jsonEncode(p['layout'])) as Json; _sel = null; });
  }

  // ---- saving and files ----
  Json _settings() => {...Map<String, dynamic>.from(_tpl!['settings'] as Map), 'canvas': _els.isEmpty ? null : _layout};

  Future<void> _save() async {
    final bare = _bareHeading;
    if (bare != null) { setState(() => _sel = bare['id'] as String); toast(context, 'A text box says just "INVOICE" as a heading. A fiscal invoice must say FISCAL TAX INVOICE: delete it and add the Document title value.', error: true); return; }
    if (_els.isNotEmpty && _missing.isNotEmpty) { toast(context, 'Add ${_missing.map(_label).join(', ')} first: ZIMRA requires them.', error: true); return; }
    setState(() => _busy = true);
    final ok = await guarded(context, () async {
      final saved = await _api.put('/api/v1/templates', body: {'id': _tpl!['id'], 'branchId': _tpl!['branchId'], 'name': _tpl!['name'], 'settings': _settings()});
      _tpl = Map<String, dynamic>.from(saved as Map);
    }, done: _els.isEmpty ? 'Saved. Invoices print with the standard layout.' : 'Saved. A4 invoices now print with this design.');
    if (mounted) setState(() { _busy = false; if (ok) _dirty = false; });
  }

  Future<void> _preview() async {
    setState(() => _busy = true);
    try {
      final png = await _api.bytes('/api/v1/templates/preview', method: 'POST', body: {'settings': _settings(), 'format': 'a4', 'templateId': _tpl!['id'], 'large': true});
      if (!mounted) return;
      await Navigator.push(context, MaterialPageRoute(builder: (_) => Scaffold(
        appBar: AppBar(title: const Text('Preview')),
        backgroundColor: const Color(0xFF334155),
        body: InteractiveViewer(maxScale: 5, child: Center(child: Image.memory(png))),
      )));
    } catch (e) {
      if (mounted) toast(context, e, error: true);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _export(String format) async {
    try {
      final bytes = await _api.bytes('/api/v1/templates/render', method: 'POST', body: {'settings': _settings(), 'format': format, 'templateId': _tpl!['id']});
      if (mounted) await shareBytes(context, bytes, 'sample-invoice.$format');
    } catch (e) { if (mounted) toast(context, e, error: true); }
  }

  Future<void> _exportTemplate() async {
    if (_dirty) { toast(context, 'Save first: the file holds the saved template.', error: true); return; }
    try {
      final bytes = await _api.bytes('/api/v1/templates/${_tpl!['id']}/export');
      if (mounted) await shareBytes(context, bytes, 'invoice-template.json');
    } catch (e) { if (mounted) toast(context, e, error: true); }
  }

  Future<void> _importTemplate() async {
    final pick = await FilePicker.pickFiles(type: FileType.custom, allowedExtensions: ['json']);
    final f = pick.firstOrNull;
    if (f == null || !mounted) return;
    final data = await f.readAsBytes();
    if (!mounted || !await confirm(context, 'Load “${f.name}” into this template? Its layout, logo and pictures replace what is here.', ok: 'Load it') || !mounted) return;
    await guarded(context, () async {
      final saved = Map<String, dynamic>.from(await _api.post('/api/v1/templates/${_tpl!['id']}/import', body: jsonDecode(utf8.decode(data))) as Map);
      _tpl = saved;
      _commit();
      final canvas = (saved['settings'] as Map?)?['canvas'];
      _layout = canvas is Map ? jsonDecode(jsonEncode(canvas)) as Json : {'background': '#ffffff', 'elements': <Json>[]};
      _dirty = false;
      _assets = (await _api.get('/api/v1/templates/assets') as List).cast<Map>().map((e) => Map<String, dynamic>.from(e)).toList();
      for (final a in _assets) { if (!_images.containsKey(a['id'])) { try { _images[a['id'].toString()] = await _api.bytes('/api/v1/templates/assets/${a['id']}'); } catch (_) {} } }
      if (saved['hasLogo'] == true) { try { _logo = await _api.bytes('/api/v1/templates/${saved['id']}/logo'); } catch (_) {} }
    }, done: 'Template loaded.');
    if (mounted) setState(() {});
  }

  // ---- pictures ----
  Future<void> _pictures() async {
    final picked = await showModalBottomSheet<String>(
      context: context, isScrollControlled: true, showDragHandle: true,
      builder: (ctx) => SafeArea(child: Padding(
        padding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
        child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.stretch, children: [
          const Text('Pictures', style: TextStyle(fontSize: 18, fontWeight: FontWeight.w800)),
          const SizedBox(height: 8),
          FilledButton.icon(onPressed: () => Navigator.pop(ctx, '__upload'), icon: const Icon(Icons.upload_rounded), label: const Text('Upload a picture (PNG or JPEG, up to 2 MB)')),
          const SizedBox(height: 12),
          if (_assets.isEmpty) Padding(padding: const EdgeInsets.all(16), child: Text('No pictures yet.', textAlign: TextAlign.center, style: TextStyle(color: Theme.of(ctx).hintColor))),
          Flexible(child: GridView.count(shrinkWrap: true, crossAxisCount: 3, mainAxisSpacing: 8, crossAxisSpacing: 8, children: [
            for (final a in _assets)
              InkWell(
                onTap: () => Navigator.pop(ctx, a['id'].toString()),
                child: Container(
                  decoration: BoxDecoration(border: Border.all(color: Theme.of(ctx).dividerColor), borderRadius: BorderRadius.circular(10)),
                  padding: const EdgeInsets.all(4),
                  child: _images[a['id']] != null ? Image.memory(_images[a['id']]!, fit: BoxFit.contain) : const Icon(Icons.image_outlined),
                ),
              ),
          ])),
        ]),
      )),
    );
    if (picked == null || !mounted) return;
    var id = picked;
    if (picked == '__upload') {
      final pick = await FilePicker.pickFiles(type: FileType.custom, allowedExtensions: ['png', 'jpg', 'jpeg']);
      final f = pick.firstOrNull;
      if (f == null || !mounted) return;
      final data = await f.readAsBytes();
      if (!mounted) return;
      if (data.length > 2000000) { toast(context, 'The picture must be at most 2 MB.', error: true); return; }
      Json? a;
      final ok = await guarded(context, () async {
        a = Map<String, dynamic>.from(await _api.upload('/api/v1/templates/assets', FormData.fromMap({'file': MultipartFile.fromBytes(data, filename: f.name)})) as Map);
      });
      if (!ok || a == null || !mounted) return;
      id = a!['id'].toString();
      _assets = [a!, ..._assets];
      _images[id] = data;
    }
    final sel = _selected;
    if (sel != null && sel['type'] == 'image') { _change((e) => e..['assetId'] = id); return; }
    _add('image', {'assetId': id, 'y': 60.0});
  }

  // ---- adding ----
  Future<void> _addSheet() async {
    final type = await showModalBottomSheet<String>(
      context: context, showDragHandle: true,
      builder: (ctx) => SafeArea(child: GridView.count(
        shrinkWrap: true, crossAxisCount: 3, padding: const EdgeInsets.fromLTRB(12, 0, 12, 16), childAspectRatio: 1.25,
        children: [
          for (final t in kTypeLabels.keys)
            InkWell(
              borderRadius: BorderRadius.circular(12),
              onTap: () => Navigator.pop(ctx, t),
              child: Column(mainAxisAlignment: MainAxisAlignment.center, children: [
                Icon(kTypeIcons[t], color: kRequired.contains(t) ? const Color(0xFFD97706) : null),
                const SizedBox(height: 6),
                Text(kTypeLabels[t]!, textAlign: TextAlign.center, style: const TextStyle(fontSize: 12, fontWeight: FontWeight.w600)),
              ]),
            ),
        ],
      )),
    );
    if (type == null || !mounted) return;
    if (type == 'image') { await _pictures(); return; }
    _add(type);
  }

  // ---- properties ----
  Future<void> _properties() async {
    if (_selected == null) return;
    await showModalBottomSheet(
      context: context, isScrollControlled: true, showDragHandle: true,
      builder: (ctx) => DraggableScrollableSheet(
        expand: false, initialChildSize: 0.6, minChildSize: 0.3, maxChildSize: 0.95,
        builder: (ctx, scroll) => _Inspector(
          scroll: scroll, element: () => _selected, fields: _fields, fonts: _fonts,
          onChange: (fn) => _change(fn), onPicture: () { Navigator.pop(ctx); _pictures(); },
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final hint = Theme.of(context).hintColor;
    final sel = _selected;
    return PopScope(
      canPop: !_dirty,
      onPopInvokedWithResult: (didPop, _) async {
        if (didPop) return;
        if (await confirm(context, 'Leave without saving your changes?', ok: 'Leave', danger: true) && context.mounted) { _dirty = false; Navigator.pop(context); }
      },
      child: Scaffold(
        appBar: AppBar(
          title: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text((_tpl?['name'] ?? 'Invoice builder').toString(), overflow: TextOverflow.ellipsis),
            Text(_dirty ? 'Not saved' : (_tpl?['branchId'] == null ? 'Company default' : 'Branch: ${_tpl?['branchName'] ?? ''}'), style: TextStyle(fontSize: 12, color: _dirty ? const Color(0xFFD97706) : hint)),
          ]),
          actions: [
            IconButton(tooltip: 'Preview', onPressed: _busy || _tpl == null ? null : _preview, icon: const Icon(Icons.visibility_outlined)),
            PopupMenuButton<String>(
              tooltip: 'More',
              onSelected: (v) => switch (v) { 'designs' => _choosePreset(), 'pdf' || 'docx' || 'json' => _export(v), 'tpl' => _exportTemplate(), 'import' => _importTemplate(), _ => null },
              itemBuilder: (_) => const [
                PopupMenuItem(value: 'designs', child: ListTile(dense: true, leading: Icon(Icons.dashboard_customize_outlined), title: Text('Start from a design'))),
                PopupMenuDivider(),
                PopupMenuItem(value: 'pdf', child: ListTile(dense: true, leading: Icon(Icons.picture_as_pdf_outlined), title: Text('Sample as PDF'))),
                PopupMenuItem(value: 'docx', child: ListTile(dense: true, leading: Icon(Icons.description_outlined), title: Text('Sample as Word'))),
                PopupMenuItem(value: 'json', child: ListTile(dense: true, leading: Icon(Icons.data_object_rounded), title: Text('Sample as JSON'))),
                PopupMenuDivider(),
                PopupMenuItem(value: 'tpl', child: ListTile(dense: true, leading: Icon(Icons.file_download_outlined), title: Text('Template file (.json)'))),
                PopupMenuItem(value: 'import', child: ListTile(dense: true, leading: Icon(Icons.file_upload_outlined), title: Text('Import a template file'))),
              ],
            ),
            Padding(padding: const EdgeInsets.only(right: 8), child: FilledButton(onPressed: _busy || _tpl == null ? null : _save, child: Text(_busy ? '…' : 'Save'))),
          ],
        ),
        body: _loading ? const Center(child: CircularProgressIndicator()) : _error != null ? ErrorState(_error!, onRetry: () { setState(() { _loading = true; _error = null; }); _load(); }) : Column(children: [
          if (_missing.isNotEmpty)
            Material(color: const Color(0xFFFEF3C7), child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
              child: Wrap(crossAxisAlignment: WrapCrossAlignment.center, spacing: 6, children: [
                const Text('ZIMRA requires:', style: TextStyle(color: Color(0xFF92400E), fontSize: 13)),
                for (final m in _missing) ActionChip(visualDensity: VisualDensity.compact, label: Text('add ${_label(m)}'), onPressed: () => _addRequired(m)),
              ]),
            )),
          Expanded(child: LayoutBuilder(builder: (context, c) {
            final s = (c.maxWidth - 24) / kPageW * _zoom;   // screen pixels per millimetre
            return GestureDetector(
              onTap: () => setState(() => _sel = null),
              child: Container(
                color: const Color(0xFFCBD5E1),
                child: SingleChildScrollView(
                  padding: const EdgeInsets.all(12),
                  child: SingleChildScrollView(
                    scrollDirection: Axis.horizontal,
                    child: Container(
                      width: kPageW * s, height: kPageH * s,
                      decoration: BoxDecoration(color: hexColor(_layout['background']?.toString(), Colors.white), boxShadow: const [BoxShadow(color: Color(0x33000000), blurRadius: 12, offset: Offset(0, 4))]),
                      child: Stack(clipBehavior: Clip.none, children: [
                        for (final e in _els) _positioned(e, s),
                        if (sel != null) ..._handles(sel, s),
                      ]),
                    ),
                  ),
                ),
              ),
            );
          })),
        ]),
        bottomNavigationBar: _loading || _error != null ? null : SafeArea(child: Material(
          elevation: 8,
          child: SingleChildScrollView(
            scrollDirection: Axis.horizontal,
            padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 4),
            child: Row(children: [
              _Tool(Icons.add_circle_outline_rounded, 'Add', _addSheet),
              _Tool(Icons.undo_rounded, 'Undo', _past.isEmpty ? null : _undo),
              _Tool(Icons.redo_rounded, 'Redo', _future.isEmpty ? null : _redo),
              _Tool(Icons.zoom_out_rounded, 'Smaller', _zoom <= 0.6 ? null : () => setState(() => _zoom = math.max(0.6, _zoom - 0.25))),
              _Tool(Icons.zoom_in_rounded, 'Bigger', _zoom >= 3 ? null : () => setState(() => _zoom = math.min(3, _zoom + 0.25))),
              _Tool(Icons.tune_rounded, 'Properties', sel == null ? null : _properties),
              _Tool(Icons.copy_rounded, 'Duplicate', sel == null ? null : _duplicate),
              _Tool(Icons.flip_to_front_rounded, 'Front', sel == null ? null : () => _order(true)),
              _Tool(Icons.flip_to_back_rounded, 'Back', sel == null ? null : () => _order(false)),
              _Tool(sel?['locked'] == true ? Icons.lock_rounded : Icons.lock_open_rounded, sel?['locked'] == true ? 'Unlock' : 'Lock', sel == null ? null : () => _change((e) => e..['locked'] = !(e['locked'] == true))),
              _Tool(Icons.delete_outline_rounded, 'Delete', sel == null ? null : _remove, danger: true),
            ]),
          ),
        )),
      ),
    );
  }

  Widget _positioned(Json e, double s) {
    final selected = e['id'] == _sel;
    final locked = e['locked'] == true;
    return Positioned(
      left: _d(e['x']) * s, top: _d(e['y']) * s, width: math.max(_d(e['w']) * s, 1), height: math.max(_d(e['h']) * s, 1),
      child: GestureDetector(
        behavior: HitTestBehavior.opaque,
        onTap: () => setState(() => _sel = e['id'] as String),
        onDoubleTap: () { setState(() => _sel = e['id'] as String); _properties(); },
        // only a selected element moves, so the page still scrolls under the others
        onPanStart: selected && !locked ? (_) => setState(_commit) : null,
        onPanUpdate: selected && !locked ? (d) => _change((x) => x
          ..['x'] = (_d(x['x']) + d.delta.dx / s).clamp(0, kPageW - _d(x['w'])).toDouble()
          ..['y'] = (_d(x['y']) + d.delta.dy / s).clamp(0, kPageH - _d(x['h'])).toDouble(), record: false) : null,
        onPanEnd: selected && !locked ? (_) => _change((x) => x..['x'] = (_d(x['x']) * 2).round() / 2..['y'] = (_d(x['y']) * 2).round() / 2, record: false) : null,
        child: Container(
          foregroundDecoration: selected ? BoxDecoration(border: Border.all(color: const Color(0xFF2563EB), width: 1.5)) : null,
          child: ClipRect(child: ElementView(element: e, scale: s, sample: _sample, known: _known, logo: _logo, images: _images)),
        ),
      ),
    );
  }

  List<Widget> _handles(Json e, double s) {
    if (e['locked'] == true) return [];
    final size = 26.0;
    return [
      Positioned(
        left: (_d(e['x']) + _d(e['w'])) * s - size / 2, top: (_d(e['y']) + _d(e['h'])) * s - size / 2, width: size, height: size,
        child: GestureDetector(
          onPanStart: (_) => setState(_commit),
          onPanUpdate: (d) => _change((x) {
            final minSize = x['type'] == 'line' ? 0.5 : 3.0;
            x['w'] = (_d(x['w']) + d.delta.dx / s).clamp(minSize, kPageW - _d(x['x'])).toDouble();
            x['h'] = (_d(x['h']) + d.delta.dy / s).clamp(minSize, kPageH - _d(x['y'])).toDouble();
            return x;
          }, record: false),
          child: Center(child: Container(width: size * 0.55, height: size * 0.55, decoration: BoxDecoration(color: Colors.white, border: Border.all(color: const Color(0xFF2563EB), width: 2), borderRadius: BorderRadius.circular(3)))),
        ),
      ),
    ];
  }
}

class _Tool extends StatelessWidget {
  const _Tool(this.icon, this.label, this.onTap, {this.danger = false});
  final IconData icon;
  final String label;
  final VoidCallback? onTap;
  final bool danger;
  @override
  Widget build(BuildContext context) {
    final color = onTap == null ? Theme.of(context).disabledColor : danger ? toneColor(Tone.bad) : null;
    return InkWell(
      borderRadius: BorderRadius.circular(10),
      onTap: onTap,
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
        child: Column(mainAxisSize: MainAxisSize.min, children: [Icon(icon, color: color), Text(label, style: TextStyle(fontSize: 11, color: color))]),
      ),
    );
  }
}

class _Swatch extends StatelessWidget {
  const _Swatch(this.preset);
  final String preset;
  @override
  Widget build(BuildContext context) {
    final colors = switch (preset) {
      'classic' => [const Color(0xFF1D4ED8), const Color(0xFF60A5FA)],
      'elegant' => [const Color(0xFF9D174D), const Color(0xFFF9A8D4)],
      'modern' => [const Color(0xFF0F172A), const Color(0xFF334155)],
      'minimal' => [const Color(0xFFE5E7EB), Colors.white],
      _ => [Colors.white, const Color(0xFFF1F5F9)],
    };
    return AspectRatio(aspectRatio: 0.75, child: DecoratedBox(decoration: BoxDecoration(borderRadius: BorderRadius.circular(6), border: Border.all(color: Theme.of(context).dividerColor), gradient: LinearGradient(colors: colors, begin: Alignment.topLeft, end: Alignment.bottomRight))));
  }
}

/// One element, drawn the way the PDF draws it (Preview shows the exact print).
class ElementView extends StatelessWidget {
  const ElementView({super.key, required this.element, required this.scale, required this.sample, required this.known, this.logo, this.images = const {}});
  final Json element;
  final double scale;
  final Json? sample;
  final Set<String> known;
  final Uint8List? logo;
  final Map<String, Uint8List> images;

  Json get _f => Map<String, dynamic>.from((sample?['fields'] ?? {}) as Map);
  String _fill(String? text) => (text ?? '').replaceAllMapped(RegExp(r'\{\s*([A-Za-z]+)\s*\}'), (m) => known.contains(m[1]) ? (_f[m[1]]?.toString() ?? '') : m[0]!);
  double _pt(num pt) => pt * 0.3528 * scale;

  BoxDecoration _box(Json st, {bool ellipse = false}) {
    final fill = st['fill'] as String?, fill2 = st['fill2'] as String?;
    final opacity = _d(st['opacity'], 1).clamp(0.05, 1).toDouble();
    Gradient? gradient;
    if (fill != null && fill2 != null) {
      final (b, e) = switch (st['gradientDir']) { 'vertical' => (Alignment.topCenter, Alignment.bottomCenter), 'diagonal' => (Alignment.topLeft, Alignment.bottomRight), _ => (Alignment.centerLeft, Alignment.centerRight) };
      gradient = LinearGradient(colors: [hexColor(fill).withValues(alpha: opacity), hexColor(fill2).withValues(alpha: opacity)], begin: b, end: e);
    }
    final bw = _d(st['borderWidth']);
    return BoxDecoration(
      color: gradient == null && fill != null ? hexColor(fill).withValues(alpha: opacity) : null,
      gradient: gradient,
      border: bw > 0 && st['border'] != null ? Border.all(color: hexColor(st['border'] as String?), width: math.max(_pt(bw), 0.5)) : null,
      shape: ellipse ? BoxShape.circle : BoxShape.rectangle,
      borderRadius: ellipse ? null : BorderRadius.circular(_d(st['radius']) * scale),
    );
  }

  TextStyle _text(Json st, {double? size, String? color, bool? bold}) => TextStyle(
        fontSize: math.max(_pt(size ?? _d(st['fontSize'], 10)), 1),
        color: hexColor(color ?? st['color'] as String?, const Color(0xFF0F172A)),
        fontWeight: (bold ?? st['bold'] == true) ? FontWeight.w700 : FontWeight.w400,
        fontStyle: st['italic'] == true ? FontStyle.italic : FontStyle.normal,
        decoration: st['underline'] == true ? TextDecoration.underline : null,
        letterSpacing: _d(st['letterSpacing']) * _pt(_d(st['fontSize'], 10)),
        height: _d(st['lineHeight'], 1.2),
        fontFamily: (st['font'] as String?) == 'Georgia' || (st['font'] as String?) == 'Times New Roman' ? 'serif' : null,
      );

  Alignment _align(Json st) {
    final x = switch (st['align']) { 'center' => 0.0, 'right' => 1.0, _ => -1.0 };
    final y = switch (st['vAlign']) { 'middle' => 0.0, 'bottom' => 1.0, _ => -1.0 };
    return Alignment(x, y);
  }

  @override
  Widget build(BuildContext context) {
    final e = element;
    final st = _st(e);
    final type = e['type'];
    final pad = EdgeInsets.all(_d(st['padding'], 1) * scale);
    switch (type) {
      case 'shape':
        return DecoratedBox(decoration: _box(st, ellipse: e['shape'] == 'ellipse'), child: const SizedBox.expand());
      case 'line':
        final w = _d(e['w']), h = _d(e['h']);
        final stroke = math.max(_pt(_d(st['borderWidth'], 1)), 1.0);
        final color = hexColor((st['border'] ?? st['color']) as String?, const Color(0xFF0F172A));
        return Center(child: w >= h ? Container(height: stroke, color: color) : Container(width: stroke, color: color));
      case 'logo':
      case 'image':
        final bytes = type == 'logo' ? logo : images[e['assetId']?.toString()];
        if (bytes == null) {
          return DecoratedBox(decoration: BoxDecoration(border: Border.all(color: const Color(0xFFCBD5E1))), child: Center(child: Text(type == 'logo' ? 'LOGO' : 'PICTURE', style: TextStyle(fontSize: math.max(_pt(7), 6), color: const Color(0xFF94A3B8)))));
        }
        return Padding(padding: pad, child: Image.memory(bytes, fit: st['fit'] == 'stretch' ? BoxFit.fill : BoxFit.contain, alignment: _align(st)));
      case 'qr':
        final caption = (e['label'] ?? '').toString();
        final captionH = caption.isEmpty ? 0.0 : _pt(_d(st['fontSize'], 8) * 0.75) * 1.4;
        return Padding(padding: pad, child: LayoutBuilder(builder: (context, c) => Stack(children: [
          Positioned(left: 0, right: 0, top: 0, height: math.max(c.maxHeight - captionH, 0), child: FittedBox(child: Icon(Icons.qr_code_2_rounded, color: Colors.black.withValues(alpha: 0.85)))),
          if (caption.isNotEmpty) Positioned(left: 0, right: 0, bottom: 0, height: math.min(captionH, c.maxHeight), child: Text(caption, textAlign: TextAlign.center, style: _text(st, size: _d(st['fontSize'], 8) * 0.75), maxLines: 1, overflow: TextOverflow.clip)),
        ])));
      case 'items':
        return Container(decoration: _box(st), padding: pad, child: _clip(_items(e, st)));
      case 'totals':
        return Container(decoration: _box(st), padding: pad, child: _clip(_totals(e, st)));
    }
    var text = '';
    if (type == 'text') text = _fill(e['text'] as String?);
    if (type == 'field') { final v = _f[e['field']]?.toString() ?? ''; final l = (e['label'] ?? '').toString(); text = l.isEmpty ? v : '${_fill(l)} $v'.trim(); }
    if (type == 'fiscal') {
      final f = Map<String, dynamic>.from((sample?['fiscal'] ?? {}) as Map);
      text = '${(e['label'] ?? '').toString().isEmpty ? '' : '${e['label']}\n'}Receipt ${f['receiptCounter']} · Global ${f['globalNo']} · Fiscal day ${f['fiscalDay']} · Device ${f['deviceId']}\nZIMRA receipt ID: ${f['zimraReceiptId'] ?? ''}\nVerify: ${f['verificationUrl'] ?? ''}\nDevice signature: ${f['signature'] ?? ''}';
    }
    if (st['uppercase'] == true) text = text.toUpperCase();
    final align = switch (st['align']) { 'center' => TextAlign.center, 'right' => TextAlign.right, _ => TextAlign.left };
    return Container(decoration: _box(st), padding: pad, alignment: _align(st), child: _clip(Text(text, style: _text(st), textAlign: align), alignment: _align(st)));
  }

  /// Content taller than its box is cut off here, like paper (the PDF shrinks it to fit instead).
  Widget _clip(Widget child, {Alignment alignment = Alignment.topLeft}) => ClipRect(child: OverflowBox(alignment: alignment, minHeight: 0, maxHeight: double.infinity, child: child));

  List<String> _cols(Json e, List<(String, String)> all, List<String> dflt) {
    final c = ((e['columns'] ?? []) as List).map((x) => x.toString()).where((k) => all.any((a) => a.$1 == k)).toList();
    return c.isEmpty ? dflt : c;
  }

  String _label(Json e, String key, String dflt) {
    final v = (e['labels'] as Map?)?[key]?.toString();
    return _fill(v == null || v.trim().isEmpty ? dflt : v);
  }

  Widget _items(Json e, Json st) {
    final cols = _cols(e, kItemColumns, ['no', 'item', 'qty', 'price', 'total']);
    const weights = {'no': 55, 'item': 460, 'hs': 160, 'qty': 100, 'price': 170, 'tax': 110, 'total': 190};
    final lines = ((sample?['lines'] ?? []) as List).cast<Map>();
    final rowPad = EdgeInsets.symmetric(vertical: _d(st['rowPadding'], 1.5) * scale, horizontal: _pt(4));
    TextAlign alignFor(String k) => k == 'item' ? TextAlign.left : ['no', 'hs', 'qty', 'tax'].contains(k) ? TextAlign.center : TextAlign.right;
    String cell(Map l, String k) => switch (k) {
          'no' => '${l['no']}', 'item' => '${l['description']}', 'hs' => '${l['hsCode'] ?? ''}', 'qty' => '${l['quantity']}',
          'price' => _money(_d(l['unitPrice'])), 'tax' => l['taxPercent'] == null ? 'Exempt' : '${l['taxPercent']}%', _ => _money(_d(l['amount'])),
        };
    final dflt = {for (final c in kItemColumns) c.$1: c.$2};
    return Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.stretch, children: [
      Container(
        color: st['headerFill'] == null ? null : hexColor(st['headerFill'] as String?),
        padding: rowPad,
        child: Row(children: [for (final k in cols) Expanded(flex: weights[k]!, child: Text(_label(e, k, dflt[k]!), textAlign: alignFor(k), maxLines: 1, overflow: TextOverflow.clip, style: _text(st, color: (st['headerColor'] ?? st['color']) as String?, bold: true)))]),
      ),
      for (var i = 0; i < lines.length; i++)
        Container(
          decoration: BoxDecoration(
            color: i.isOdd && st['stripeFill'] != null ? hexColor(st['stripeFill'] as String?) : null,
            border: Border(bottom: BorderSide(color: hexColor(st['lineColor'] as String?, const Color(0xFFE2E8F0)), width: 0.5)),
          ),
          padding: rowPad,
          child: Row(children: [for (final k in cols) Expanded(flex: weights[k]!, child: Text(cell(lines[i], k), textAlign: alignFor(k), maxLines: 1, overflow: TextOverflow.clip, style: _text(st, bold: false)))]),
        ),
    ]);
  }

  Widget _totals(Json e, Json st) {
    final rows = <(String, String, bool)>[];
    final s = sample ?? {};
    for (final k in _cols(e, kTotalRows, ['subtotal', 'taxes', 'total'])) {
      if (k == 'subtotal') rows.add((_label(e, 'subtotal', 'Subtotal ({currency})'), _money(_d(s['subtotal'])), false));
      if (k == 'taxes') {
        for (final t in ((s['taxes'] ?? []) as List).cast<Map>()) {
          rows.add((t['percent'] == null ? 'Exempt' : _label(e, 'taxes', 'VAT ({rate})').replaceAll('{rate}', '${t['percent']}%'), _money(_d(t['tax'])), false));
        }
      }
      if (k == 'total') rows.add((_label(e, 'total', 'TOTAL ({currency})'), _money(_d(s['total'])), true));
      if (k == 'payments') { for (final p in ((s['payments'] ?? []) as List).cast<Map>()) { rows.add(('${p['method']}', _money(_d(p['amount'])), false)); } }
    }
    final size = _d(st['fontSize'], 10);
    return Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.stretch, children: [
      for (final r in rows)
        Container(
          decoration: BoxDecoration(
            color: r.$3 && st['headerFill'] != null ? hexColor(st['headerFill'] as String?) : null,
            border: r.$3 && st['headerFill'] != null ? null : Border(bottom: BorderSide(color: hexColor(st['lineColor'] as String?, const Color(0xFFE2E8F0)), width: 0.5)),
          ),
          padding: EdgeInsets.symmetric(vertical: _d(st['rowPadding'], 1.5) * scale, horizontal: _pt(4)),
          child: Row(children: [
            Expanded(child: Text(r.$1, maxLines: 1, overflow: TextOverflow.clip, style: _text(st, size: r.$3 ? size * 1.15 : size, color: r.$3 ? (st['headerColor'] ?? st['color']) as String? : null, bold: r.$3 || st['bold'] == true))),
            Text(r.$2, style: _text(st, size: r.$3 ? size * 1.15 : size, color: r.$3 ? (st['headerColor'] ?? st['color']) as String? : null, bold: r.$3 || st['bold'] == true)),
          ]),
        ),
    ]);
  }
}

/// The properties of the selected element: what it shows and how it looks. Every change applies at once (Undo takes it back).
class _Inspector extends StatefulWidget {
  const _Inspector({required this.scroll, required this.element, required this.fields, required this.fonts, required this.onChange, required this.onPicture});
  final ScrollController scroll;
  final Json? Function() element;
  final List<Json> fields;
  final List<String> fonts;
  final void Function(Json Function(Json e) fn) onChange;
  final VoidCallback onPicture;
  @override
  State<_Inspector> createState() => _InspectorState();
}

class _InspectorState extends State<_Inspector> {
  void _set(String key, dynamic v) { widget.onChange((e) => e..[key] = v); setState(() {}); }
  void _style(String key, dynamic v) { widget.onChange((e) => e..['style'] = {..._st(e), key: v}); setState(() {}); }

  Widget _num(String label, num value, void Function(double v) onSet, {double step = 0.5}) => Expanded(child: TextFormField(
        key: ValueKey('$label-$value'),
        initialValue: (value is double && value == value.roundToDouble() ? value.toInt() : value).toString(),
        keyboardType: const TextInputType.numberWithOptions(decimal: true),
        decoration: InputDecoration(labelText: label, isDense: true),
        onFieldSubmitted: (s) { final v = double.tryParse(s); if (v != null) onSet(v); },
      ));

  Future<String?> _pickColor(String? current) => showDialog<String>(
        context: context,
        builder: (ctx) {
          final hex = TextEditingController(text: current ?? '#');
          return AlertDialog(
            title: const Text('Colour'),
            content: Column(mainAxisSize: MainAxisSize.min, children: [
              Wrap(spacing: 8, runSpacing: 8, children: [
                for (final c in kSwatches)
                  InkWell(onTap: () => Navigator.pop(ctx, c), child: Container(width: 30, height: 30, decoration: BoxDecoration(color: hexColor(c), shape: BoxShape.circle, border: Border.all(color: Theme.of(ctx).dividerColor, width: c == current ? 3 : 1)))),
              ]),
              const SizedBox(height: 12),
              TextField(controller: hex, decoration: const InputDecoration(labelText: 'Or type it, like #9d174d')),
            ]),
            actions: [
              TextButton(onPressed: () => Navigator.pop(ctx), child: const Text('Cancel')),
              FilledButton(onPressed: () { final v = hex.text.trim(); if (RegExp(r'^#[0-9a-fA-F]{6}$').hasMatch(v)) Navigator.pop(ctx, v.toLowerCase()); }, child: const Text('Use')),
            ],
          );
        },
      );

  Widget _colorRow(String label, String? value, void Function(String? v) onSet, {bool canClear = true}) => ListTile(
        contentPadding: EdgeInsets.zero, dense: true, title: Text(label),
        trailing: Row(mainAxisSize: MainAxisSize.min, children: [
          if (canClear && value != null) IconButton(tooltip: 'None', onPressed: () => onSet(null), icon: const Icon(Icons.format_color_reset_outlined)),
          InkWell(
            onTap: () async { final c = await _pickColor(value); if (c != null) onSet(c); },
            child: Container(width: 34, height: 26, decoration: BoxDecoration(color: value == null ? null : hexColor(value), borderRadius: BorderRadius.circular(6), border: Border.all(color: Theme.of(context).dividerColor)), child: value == null ? const Icon(Icons.add, size: 16) : null),
          ),
        ]),
      );

  @override
  Widget build(BuildContext context) {
    final e = widget.element();
    if (e == null) return const SizedBox.shrink();
    final st = _st(e);
    final type = e['type'] as String;
    final hint = Theme.of(context).hintColor;
    final textual = !['line', 'shape', 'image', 'logo'].contains(type);
    final groups = <String, List<Json>>{};
    for (final f in widget.fields) { (groups[f['group'].toString()] ??= []).add(f); }
    final cols = ((e['columns'] ?? []) as List).map((x) => x.toString()).toList();
    final options = type == 'items' ? kItemColumns : kTotalRows;
    return ListView(controller: widget.scroll, padding: const EdgeInsets.fromLTRB(16, 0, 16, 32), children: [
      Row(children: [Icon(kTypeIcons[type]), const SizedBox(width: 8), Text(kTypeLabels[type] ?? type, style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w800))]),
      const SizedBox(height: 12),
      Row(children: [
        _num('X (mm)', _d(e['x']), (v) => _set('x', v.clamp(0, kPageW - 1))), const SizedBox(width: 8),
        _num('Y (mm)', _d(e['y']), (v) => _set('y', v.clamp(0, kPageH - 1))), const SizedBox(width: 8),
        _num('Width', _d(e['w']), (v) => _set('w', v.clamp(0.5, kPageW - _d(e['x'])))), const SizedBox(width: 8),
        _num('Height', _d(e['h']), (v) => _set('h', v.clamp(0.5, kPageH - _d(e['y'])))),
      ]),
      Wrap(spacing: 6, children: [
        TextButton(onPressed: () => _set('x', ((kPageW - _d(e['w'])) / 2 * 2).round() / 2), child: const Text('Centre on page')),
        TextButton(onPressed: () { _set('x', 0.0); _set('w', kPageW); }, child: const Text('Full width')),
      ]),
      const Divider(),
      if (type == 'text') ...[
        TextFormField(key: ValueKey('t-${e['id']}'), initialValue: (e['text'] ?? '').toString(), minLines: 2, maxLines: 6, decoration: const InputDecoration(labelText: 'Text', helperText: 'Invoice values go in {braces}, e.g. {invoiceNo}'), onChanged: (v) => _set('text', v)),
        const SizedBox(height: 8),
        DropdownButtonFormField<String>(
          isExpanded: true, decoration: const InputDecoration(labelText: 'Insert an invoice value'),
          items: [for (final f in widget.fields) DropdownMenuItem(value: f['key'].toString(), child: Text('${f['group']}: ${f['label']}', overflow: TextOverflow.ellipsis))],
          onChanged: (k) { if (k != null) { _set('text', '${e['text'] ?? ''}{$k}'); setState(() {}); } },
        ),
      ],
      if (type == 'field') ...[
        DropdownButtonFormField<String>(
          isExpanded: true, initialValue: e['field']?.toString(), decoration: const InputDecoration(labelText: 'Shows'),
          items: [for (final g in groups.entries) for (final f in g.value) DropdownMenuItem(value: f['key'].toString(), child: Text('${g.key}: ${f['label']}', overflow: TextOverflow.ellipsis))],
          onChanged: (k) => _set('field', k),
        ),
        TextFormField(key: ValueKey('l-${e['id']}'), initialValue: (e['label'] ?? '').toString(), decoration: const InputDecoration(labelText: 'Words before it'), onChanged: (v) => _set('label', v)),
      ],
      if (type == 'qr' || type == 'fiscal')
        TextFormField(key: ValueKey('c-${e['id']}'), initialValue: (e['label'] ?? '').toString(), decoration: InputDecoration(labelText: type == 'qr' ? 'Caption' : 'Heading (optional)'), onChanged: (v) => _set('label', v)),
      if (type == 'image') ...[
        OutlinedButton.icon(onPressed: widget.onPicture, icon: const Icon(Icons.image_outlined), label: const Text('Change picture')),
        SegmentedButton<String>(segments: const [ButtonSegment(value: 'contain', label: Text('Keep shape')), ButtonSegment(value: 'stretch', label: Text('Stretch'))], selected: {st['fit'] == 'stretch' ? 'stretch' : 'contain'}, onSelectionChanged: (v) => _style('fit', v.first)),
      ],
      if (type == 'logo') Text('Shows the template\'s logo (upload it on the Templates screen).', style: TextStyle(color: hint)),
      if (type == 'shape')
        SegmentedButton<String>(segments: const [ButtonSegment(value: 'rect', label: Text('Box')), ButtonSegment(value: 'ellipse', label: Text('Circle'))], selected: {e['shape'] == 'ellipse' ? 'ellipse' : 'rect'}, onSelectionChanged: (v) => _set('shape', v.first)),
      if (type == 'items' || type == 'totals') ...[
        Text(type == 'items' ? 'Columns' : 'Rows', style: const TextStyle(fontWeight: FontWeight.w700)),
        for (final o in options)
          CheckboxListTile(
            contentPadding: EdgeInsets.zero, dense: true, value: cols.contains(o.$1),
            title: TextFormField(key: ValueKey('h-${e['id']}-${o.$1}'), initialValue: ((e['labels'] as Map?)?[o.$1] ?? '').toString(), decoration: InputDecoration(hintText: o.$2, isDense: true),
                onChanged: (v) { final labels = Map<String, dynamic>.from((e['labels'] ?? {}) as Map); if (v.trim().isEmpty) { labels.remove(o.$1); } else { labels[o.$1] = v; } _set('labels', labels); }),
            onChanged: (on) {
              final next = [...cols]; if (on == true) { next.add(o.$1); } else { next.remove(o.$1); }
              if (next.isNotEmpty) _set('columns', options.map((x) => x.$1).where(next.contains).toList());
            },
          ),
        _colorRow(type == 'items' ? 'Heading fill' : 'Total row fill', st['headerFill'] as String?, (v) => _style('headerFill', v)),
        _colorRow(type == 'items' ? 'Heading text' : 'Total row text', st['headerColor'] as String?, (v) => _style('headerColor', v)),
        if (type == 'items') _colorRow('Stripes', st['stripeFill'] as String?, (v) => _style('stripeFill', v)),
        _colorRow('Lines', st['lineColor'] as String?, (v) => _style('lineColor', v)),
      ],
      if (textual) ...[
        const Divider(),
        const Text('Text', style: TextStyle(fontWeight: FontWeight.w700)),
        Row(children: [
          Expanded(child: DropdownButtonFormField<String>(
            isExpanded: true, initialValue: (st['font'] as String?) ?? '', decoration: const InputDecoration(labelText: 'Font', isDense: true),
            items: [const DropdownMenuItem(value: '', child: Text('Lato (default)')), for (final f in widget.fonts.where((f) => f != 'Lato')) DropdownMenuItem(value: f, child: Text(f))],
            onChanged: (v) => _style('font', v == null || v.isEmpty ? null : v),
          )),
          const SizedBox(width: 8),
          _num('Size (pt)', _d(st['fontSize'], 10), (v) => _style('fontSize', v.clamp(4, 72))),
        ]),
        const SizedBox(height: 8),
        Wrap(spacing: 6, runSpacing: 6, children: [
          for (final (k, icon) in [('bold', Icons.format_bold), ('italic', Icons.format_italic), ('underline', Icons.format_underline), ('uppercase', Icons.text_fields)])
            FilterChip(label: Icon(icon, size: 18), selected: st[k] == true, onSelected: (on) => _style(k, on)),
        ]),
        const SizedBox(height: 8),
        SegmentedButton<String>(
          segments: const [ButtonSegment(value: 'left', icon: Icon(Icons.format_align_left)), ButtonSegment(value: 'center', icon: Icon(Icons.format_align_center)), ButtonSegment(value: 'right', icon: Icon(Icons.format_align_right))],
          selected: {(st['align'] ?? 'left').toString()}, onSelectionChanged: (v) => _style('align', v.first),
        ),
        const SizedBox(height: 6),
        SegmentedButton<String>(
          segments: const [ButtonSegment(value: 'top', icon: Icon(Icons.vertical_align_top)), ButtonSegment(value: 'middle', icon: Icon(Icons.vertical_align_center)), ButtonSegment(value: 'bottom', icon: Icon(Icons.vertical_align_bottom))],
          selected: {(st['vAlign'] ?? 'top').toString()}, onSelectionChanged: (v) => _style('vAlign', v.first),
        ),
        _colorRow('Text colour', st['color'] as String?, (v) => _style('color', v ?? '#0f172a'), canClear: false),
        Row(children: [
          _num('Letter spacing', _d(st['letterSpacing']), (v) => _style('letterSpacing', v.clamp(-0.1, 1)), step: 0.02), const SizedBox(width: 8),
          _num('Line height', _d(st['lineHeight'], 1.2), (v) => _style('lineHeight', v.clamp(0.8, 3))),
        ]),
      ],
      if (type == 'line') ...[
        const Divider(),
        _colorRow('Colour', st['border'] as String?, (v) => _style('border', v ?? '#94a3b8'), canClear: false),
        Row(children: [_num('Thickness (pt)', _d(st['borderWidth'], 1), (v) => _style('borderWidth', v.clamp(0.25, 20)))]),
      ] else ...[
        const Divider(),
        const Text('Box', style: TextStyle(fontWeight: FontWeight.w700)),
        _colorRow('Fill', st['fill'] as String?, (v) => _style('fill', v)),
        if (st['fill'] != null) ...[
          _colorRow('Gradient to', st['fill2'] as String?, (v) => _style('fill2', v)),
          if (st['fill2'] != null)
            SegmentedButton<String>(
              segments: const [ButtonSegment(value: 'horizontal', icon: Icon(Icons.arrow_forward)), ButtonSegment(value: 'vertical', icon: Icon(Icons.arrow_downward)), ButtonSegment(value: 'diagonal', icon: Icon(Icons.south_east))],
              selected: {(st['gradientDir'] ?? 'horizontal').toString()}, onSelectionChanged: (v) => _style('gradientDir', v.first),
            ),
        ],
        _colorRow('Border', _d(st['borderWidth']) > 0 ? st['border'] as String? : null, (v) { _style('border', v); _style('borderWidth', v == null ? 0 : math.max(_d(st['borderWidth']), 1)); }),
        Row(children: [
          if (_d(st['borderWidth']) > 0) ...[_num('Border (pt)', _d(st['borderWidth']), (v) => _style('borderWidth', v.clamp(0, 20))), const SizedBox(width: 8)],
          _num('Corners (mm)', _d(st['radius']), (v) => _style('radius', v.clamp(0, 100))), const SizedBox(width: 8),
          _num('Inside (mm)', _d(st['padding'], 1), (v) => _style('padding', v.clamp(0, 20))), const SizedBox(width: 8),
          _num('Opacity', _d(st['opacity'], 1), (v) => _style('opacity', v.clamp(0.05, 1))),
        ]),
      ],
    ]);
  }
}

/// A line that is a document heading on its own ("INVOICE", "Tax invoice") rather than ZIMRA's FISCAL TAX INVOICE.
bool isBareHeading(String line) {
  final l = line.replaceAll(RegExp(r'[^A-Za-z ]'), ' ').replaceAll(RegExp(r'\s+'), ' ').trim().toUpperCase();
  return const {'INVOICE', 'TAX INVOICE', 'SALES INVOICE', 'TAX INVOICE RECEIPT', 'INVOICE RECEIPT', 'RECEIPT', 'BILL', 'FISCAL INVOICE'}.contains(l);
}
