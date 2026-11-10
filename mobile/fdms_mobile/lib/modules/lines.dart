import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/api.dart';
import '../core/models.dart';
import '../ui/kit.dart';

/// One way to tax a line. For receipts these come from the device (ZIMRA's tax IDs); for quotes and recurring invoices from a plain list.
class TaxOption {
  TaxOption(this.key, this.label, {this.taxId, this.percent, this.exempt = false});
  final String key, label;
  final int? taxId;
  final double? percent;
  final bool exempt;

  static List<TaxOption> fromDevice(List<dynamic> taxes) => [
        for (final t in taxes)
          TaxOption('id:${t['taxId']}', '${t['taxName']}${t['taxPercent'] == null ? ' (exempt)' : ('${t['taxName']}'.contains('%') ? '' : ' ${t['taxPercent']}%')}', taxId: t['taxId'], percent: (t['taxPercent'] as num?)?.toDouble(), exempt: t['taxPercent'] == null),
      ];

  static List<TaxOption> standard() => [TaxOption('pct:15', 'Standard 15%', percent: 15), TaxOption('pct:0', 'Zero rated 0%', percent: 0), TaxOption('exempt', 'Exempt', exempt: true)];
}

class LineData {
  LineData({this.name = '', this.hs = '', this.qty = 1, this.price = 0, this.taxKey});
  String name, hs;
  double qty, price;
  String? taxKey;
  double get total => qty * price;

  bool get hsBad => hs.isNotEmpty && !RegExp(r'^\d{4}$|^\d{6}$|^\d{8}$').hasMatch(hs);

  Json toReceipt(List<TaxOption> taxes) {
    final t = taxes.where((x) => x.key == taxKey).firstOrNull;
    return {'name': name.trim(), if (hs.trim().isNotEmpty) 'hsCode': hs.trim(), 'quantity': qty, 'unitPrice': price, if (t?.taxId != null) 'taxId': t!.taxId, 'isExempt': t?.exempt ?? false, 'isDiscount': false};
  }

  Json toQuote(List<TaxOption> taxes) {
    final t = taxes.where((x) => x.key == taxKey).firstOrNull;
    return {'name': name.trim(), if (hs.trim().isNotEmpty) 'hsCode': hs.trim(), 'quantity': qty, 'unitPrice': price, 'taxPercent': t?.exempt == true ? null : (t?.percent ?? 15), 'isExempt': t?.exempt ?? false};
  }

  static LineData fromQuote(Json j, List<TaxOption> taxes) {
    final exempt = j['isExempt'] == true;
    final pct = (j['taxPercent'] as num?)?.toDouble();
    final key = exempt ? 'exempt' : 'pct:${pct ?? 15}';
    final fixed = taxes.any((t) => t.key == key) ? key : (taxes.firstOrNull?.key);
    return LineData(name: j['name'] ?? '', hs: j['hsCode'] ?? '', qty: (j['quantity'] as num?)?.toDouble() ?? 1, price: (j['unitPrice'] as num?)?.toDouble() ?? 0, taxKey: fixed);
  }
}

/// Edits a list of invoice lines: type or pick an item from the catalogue, set quantity, price and tax.
class LinesEditor extends StatefulWidget {
  const LinesEditor({super.key, required this.lines, required this.taxes, required this.onChanged, this.currency = ''});
  final List<LineData> lines;
  final List<TaxOption> taxes;
  final String currency;
  final VoidCallback onChanged;
  @override
  State<LinesEditor> createState() => _LinesEditorState();
}

class _LinesEditorState extends State<LinesEditor> {
  Future<void> _pick(LineData l) async {
    final api = context.read<Api>();
    final c = await showModalBottomSheet<Json>(
      context: context,
      isScrollControlled: true,
      showDragHandle: true,
      builder: (_) => _ItemSearch(api: api),
    );
    if (c != null) {
      setState(() {
        l.name = c['name'] ?? l.name;
        l.hs = (c['hsCode'] ?? l.hs).toString();
        l.price = (c['unitPrice'] as num?)?.toDouble() ?? l.price;
        if (c['isExempt'] == true) {
          l.taxKey = widget.taxes.where((t) => t.exempt).firstOrNull?.key ?? l.taxKey;
        } else if (c['taxPercent'] != null) {
          l.taxKey = widget.taxes.where((t) => t.percent == (c['taxPercent'] as num).toDouble() && !t.exempt).firstOrNull?.key ?? l.taxKey;
        }
      });
      widget.onChanged();
    }
  }

  @override
  Widget build(BuildContext context) {
    final taxes = widget.taxes;
    return Column(children: [
      for (var i = 0; i < widget.lines.length; i++)
        Padding(
          padding: const EdgeInsets.only(bottom: 10),
          child: AppCard(
            padding: const EdgeInsets.all(12),
            child: Column(children: [
              Row(children: [
                Expanded(child: TextFormField(
                  key: ValueKey('n${widget.lines[i].hashCode}${widget.lines[i].name.isEmpty}'), initialValue: widget.lines[i].name, decoration: InputDecoration(labelText: 'Item ${i + 1}', isDense: true, suffixIcon: IconButton(tooltip: 'Pick from your items', icon: const Icon(Icons.search), onPressed: () => _pick(widget.lines[i]))),
                  onChanged: (v) { widget.lines[i].name = v; widget.onChanged(); },
                )),
                if (widget.lines.length > 1) IconButton(icon: const Icon(Icons.delete_outline), onPressed: () { setState(() => widget.lines.removeAt(i)); widget.onChanged(); }),
              ]),
              const SizedBox(height: 10),
              Row(children: [
                Expanded(child: TextFormField(key: ValueKey('q${widget.lines[i].hashCode}'), initialValue: _n(widget.lines[i].qty), keyboardType: const TextInputType.numberWithOptions(decimal: true), decoration: const InputDecoration(labelText: 'Qty', isDense: true), onChanged: (v) { widget.lines[i].qty = double.tryParse(v) ?? 0; setState(() {}); widget.onChanged(); })),
                const SizedBox(width: 10),
                Expanded(flex: 2, child: TextFormField(key: ValueKey('p${widget.lines[i].hashCode}${widget.lines[i].price}'), initialValue: _n(widget.lines[i].price), keyboardType: const TextInputType.numberWithOptions(decimal: true), decoration: InputDecoration(labelText: 'Unit price ${widget.currency}', isDense: true), onChanged: (v) { widget.lines[i].price = double.tryParse(v) ?? 0; setState(() {}); widget.onChanged(); })),
              ]),
              const SizedBox(height: 10),
              Row(children: [
                Expanded(child: TextFormField(key: ValueKey('h${widget.lines[i].hashCode}${widget.lines[i].hs}'), initialValue: widget.lines[i].hs, keyboardType: TextInputType.number, decoration: InputDecoration(labelText: 'HS code', isDense: true, errorText: widget.lines[i].hsBad ? '4, 6 or 8 digits' : null), onChanged: (v) { widget.lines[i].hs = v; setState(() {}); widget.onChanged(); })),
                const SizedBox(width: 10),
                Expanded(child: DropdownButtonFormField<String>(
                  initialValue: taxes.any((t) => t.key == widget.lines[i].taxKey) ? widget.lines[i].taxKey : null, isExpanded: true, decoration: const InputDecoration(labelText: 'Tax', isDense: true),
                  items: [for (final t in taxes) DropdownMenuItem(value: t.key, child: Text(t.label, overflow: TextOverflow.ellipsis))],
                  onChanged: (v) { setState(() => widget.lines[i].taxKey = v); widget.onChanged(); },
                )),
              ]),
              Align(alignment: Alignment.centerRight, child: Padding(padding: const EdgeInsets.only(top: 8), child: Text('Line total ${money(widget.lines[i].total)}', style: const TextStyle(fontWeight: FontWeight.w700)))),
            ]),
          ),
        ),
      Align(alignment: Alignment.centerLeft, child: TextButton.icon(onPressed: () { setState(() => widget.lines.add(LineData(taxKey: taxes.firstOrNull?.key))); widget.onChanged(); }, icon: const Icon(Icons.add), label: const Text('Add another item'))),
    ]);
  }

  static String _n(double v) => v == 0 ? '' : (v == v.roundToDouble() ? v.toInt().toString() : v.toString());
}

class _ItemSearch extends StatefulWidget {
  const _ItemSearch({required this.api});
  final Api api;
  @override
  State<_ItemSearch> createState() => _ItemSearchState();
}

class _ItemSearchState extends State<_ItemSearch> {
  final _q = TextEditingController();
  List<Json> _items = [];
  bool _loading = true;

  @override
  void initState() { super.initState(); _run(''); }

  Future<void> _run(String q) async {
    try {
      final r = await widget.api.getList('/api/v1/suggest/items', query: {'q': q});
      if (mounted) setState(() { _items = r; _loading = false; });
    } catch (_) { if (mounted) setState(() => _loading = false); }
  }

  @override
  Widget build(BuildContext context) => Padding(
        padding: EdgeInsets.only(bottom: MediaQuery.of(context).viewInsets.bottom),
        child: SizedBox(height: MediaQuery.of(context).size.height * 0.7, child: Column(children: [
          Padding(padding: const EdgeInsets.fromLTRB(16, 0, 16, 8), child: TextField(controller: _q, autofocus: true, decoration: const InputDecoration(hintText: 'Search your items…', prefixIcon: Icon(Icons.search)), onChanged: _run)),
          Expanded(child: _loading ? const Center(child: CircularProgressIndicator()) : _items.isEmpty ? const EmptyState('No matching items. Type the item name on the line instead.') : ListView(children: [
            for (final i in _items) ListTile(title: Text(i['name'] ?? ''), subtitle: Text('${i['hsCode'] ?? 'no HS code'} · ${i['source'] ?? ''}'), trailing: Text(moneyIn(i['unitPrice'], i['currency'])), onTap: () => Navigator.pop(context, i)),
          ])),
        ])),
      );
}
