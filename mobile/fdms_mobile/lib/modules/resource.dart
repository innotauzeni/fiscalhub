import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/api.dart';
import '../core/auth.dart';
import '../core/models.dart';
import '../ui/kit.dart';
import '../ui/lists.dart';
import '../ui/premium.dart';

enum Kind { text, multiline, number, integer, boolean, select, multi, date, password, email, phone }

class Opt {
  const Opt(this.value, this.label);
  final String value, label;
}

/// One input on a form. Options for a select can be fixed, or loaded from an endpoint.
class Fld {
  const Fld(this.key, this.label, {this.kind = Kind.text, this.required = false, this.options, this.optionsPath, this.optionsKey, this.optionValue = 'id', this.optionLabel = 'name', this.help, this.initial, this.createOnly = false, this.editOnly = false, this.nullIfEmpty = true});
  final String key, label;
  final Kind kind;
  final bool required, createOnly, editOnly, nullIfEmpty;
  final List<Opt>? options;
  final String? optionsPath, optionsKey, help;
  final String optionValue, optionLabel;
  final dynamic initial;
}

/// A button on an item: calls the gateway, optionally asking a few questions first.
class RowAction {
  const RowAction(this.label, this.method, this.path, {this.icon = Icons.bolt, this.body, this.ask, this.confirmText, this.when, this.perm, this.danger = false, this.done, this.openFile, this.fileName, this.screen});
  final String label, method;
  final String Function(Json item) path;
  final IconData icon;
  final Json Function(Json item, Json answers)? body;
  final List<Fld>? ask;
  final String? confirmText, perm, done;
  final bool Function(Json item)? when;
  final bool danger;
  /// For downloads: the file is fetched and opened instead of calling the gateway as an action.
  final bool? openFile;
  final String Function(Json item)? fileName;
  /// Opens a screen for the item instead of calling the gateway (for example the access editor of a role).
  final Widget Function(Json item)? screen;
}

class ResourceSpec {
  const ResourceSpec({
    required this.title, required this.listPath, required this.itemTitle, this.itemSubtitle, this.trailing, this.badges, this.facts, this.query, this.itemsKey,
    this.fields, this.savePath, this.saveMethod = 'PUT', this.createPerm, this.editPerm, this.shape, this.actions = const [], this.emptyText = 'Nothing here yet.', this.createLabel = 'Add', this.filters, this.searchable = true,
    this.idKey = 'id', this.deletePath, this.deletePerm, this.canEdit, this.leadingIcon, this.header, this.saveFor, this.onSaved, this.createBuilder,
  });
  final String title, listPath, emptyText, createLabel, idKey;
  final String? itemsKey, savePath, createPerm, editPerm, deletePerm;
  final Map<String, dynamic>? query;
  final String Function(Json) itemTitle;
  final String? Function(Json)? itemSubtitle, trailing;
  final List<(String, Tone)> Function(Json)? badges;
  final List<(String, String?)> Function(Json)? facts;
  final List<Fld>? fields;
  final String saveMethod;
  /// Turns the form's values into the request body (adds the id when editing).
  final Json Function(Json values, Json? existing)? shape;
  final List<RowAction> actions;
  final List<(String, String, List<String>)>? filters; // (label, field, values)
  final bool searchable;
  final String Function(Json)? deletePath;
  final bool Function(Json)? canEdit;
  final IconData? leadingIcon;
  final Widget Function(BuildContext, List<Json>)? header;
  /// Different request for adding and editing: returns (method, path).
  final (String, String) Function(Json? existing)? saveFor;
  /// Called with the gateway's answer after a save (for example to show a secret once).
  final Future<void> Function(BuildContext context, dynamic response)? onSaved;
  /// A custom screen instead of the plain form (invoices with lines, for example).
  final Widget Function(Json? existing)? createBuilder;
}

/// A list of things from the gateway with search, add and edit forms, and per-item buttons: the same shape for most of the console's pages.
class ResourceScreen extends StatefulWidget {
  const ResourceScreen(this.spec, {super.key});
  final ResourceSpec spec;
  @override
  State<ResourceScreen> createState() => ResourceScreenState();
}

class ResourceScreenState extends State<ResourceScreen> {
  final _key = GlobalKey<LoaderState<List<Json>>>();
  final _q = TextEditingController();
  String _filterValue = '';
  int _filterIndex = 0;

  ResourceSpec get s => widget.spec;

  Future<void> reload() async => _key.currentState?.reload();

  Future<List<Json>> _load() => context.read<Api>().getList(s.listPath, query: s.query, key: s.itemsKey);

  bool _match(Json j) {
    final q = _q.text.trim().toLowerCase();
    if (q.isNotEmpty && !('${s.itemTitle(j)} ${s.itemSubtitle?.call(j) ?? ''}'.toLowerCase().contains(q))) return false;
    if (_filterValue.isNotEmpty && s.filters != null) { final f = s.filters![_filterIndex]; if ((j[f.$2] ?? '').toString() != _filterValue) return false; }
    return true;
  }

  Future<void> _edit(Json? existing) async {
    final ok = await Navigator.push<bool>(context, MaterialPageRoute(builder: (_) => s.createBuilder != null ? s.createBuilder!(existing) : ResourceFormScreen(s, existing)));
    if (ok == true) _key.currentState?.reload();
  }

  @override
  Widget build(BuildContext context) {
    final auth = context.watch<AuthController>();
    final canCreate = (s.createBuilder != null || (s.fields != null && (s.savePath != null || s.saveFor != null))) && auth.can(s.createPerm);
    return Scaffold(
      backgroundColor: Colors.transparent,
      floatingActionButton: canCreate ? FloatingActionButton.extended(onPressed: () => _edit(null), icon: const Icon(Icons.add), label: Text(s.createLabel)) : null,
      body: Loader<List<Json>>(
        key: _key,
        load: _load,
        builder: (context, all, reload) {
          final items = all.where(_match).toList();
          final initials = showsInitials(s);
          return ListView(padding: const EdgeInsets.fromLTRB(16, 12, 16, 96), children: [
            if (s.header != null) s.header!(context, all),
            if (s.searchable && all.length > 4) Padding(padding: const EdgeInsets.only(bottom: 12), child: SearchBox(controller: _q, hint: 'Search ${s.title.toLowerCase()}', onChanged: (_) => setState(() {}))),
            if (s.filters != null && all.isNotEmpty)
              FilterPills([
                for (var i = 0; i < s.filters!.length; i++)
                  for (final v in ['', ...s.filters![i].$3])
                    if (i == 0 || v.isNotEmpty)
                      PillOption(
                        v.isEmpty ? 'All' : niceLabel(statusText(v)),
                        _filterIndex == i && _filterValue == v,
                        () => setState(() { _filterIndex = i; _filterValue = v; }),
                        color: v.isEmpty ? null : toneColor(toneFor(v)),
                        count: v.isEmpty ? all.length : all.where((j) => (j[s.filters![i].$2] ?? '').toString() == v).length,
                      ),
              ]),
            if (all.isEmpty)
              BigEmpty(
                icon: s.leadingIcon ?? Icons.inbox_outlined, title: 'No ${s.title.toLowerCase()} yet', message: s.emptyText,
                action: canCreate ? FilledButton.icon(onPressed: () => _edit(null), icon: const Icon(Icons.add_rounded), label: Text(s.createLabel)) : null,
              )
            else if (items.isEmpty)
              BigEmpty(icon: Icons.search_off_rounded, title: 'Nothing matches', message: 'Try another word or filter.', action: TextButton(onPressed: () => setState(() { _q.clear(); _filterValue = ''; _filterIndex = 0; }), child: const Text('Clear search')))
            else ...[
              ListHeader(s.title, trailing: items.length == all.length ? '${all.length}' : '${items.length} of ${all.length}'),
              ListPanel(children: [
                for (final j in items)
                  ListRow(
                    leading: Avatar(s.itemTitle(j), icon: initials ? null : s.leadingIcon, color: initials ? null : kBrand),
                    title: s.itemTitle(j),
                    subtitle: s.itemSubtitle?.call(j),
                    pills: s.badges?.call(j) ?? const [],
                    figure: s.trailing?.call(j)?.split('\n').first,
                    onTap: () => _open(j, reload),
                  ),
              ]),
            ],
          ]);
        },
      ),
    );
  }

  void _open(Json j, Future<void> Function() reload) {
    Navigator.push(context, MaterialPageRoute<void>(builder: (_) => ItemSheet(spec: s, item: j, onChanged: reload, onEdit: () { Navigator.pop(context); _edit(j); })));
  }
}

/// Lists of people and companies show coloured initials (like a contacts app) instead of one repeated icon.
final _namedIcons = <IconData>{Icons.people_outline, Icons.person_outline, Icons.local_shipping_outlined, Icons.apartment, Icons.handshake_outlined, Icons.how_to_reg_outlined};
bool showsInitials(ResourceSpec s) => s.leadingIcon == null || _namedIcons.contains(s.leadingIcon);

/// What to show for an item that has no hand-written details: plain values only, with yes/no and dates in words and internal ids left out.
List<(String, String?)> defaultFacts(Json item) {
  final iso = RegExp(r'^\d{4}-\d{2}-\d{2}T');
  return [
    for (final e in item.entries.where((e) => e.value != null && e.value is! Map && e.value is! List && e.key != 'id' && !e.key.endsWith('Id') && e.key != 'passwordHash'))
      (humanize(e.key), e.value is bool ? (e.value ? 'Yes' : 'No') : (e.value is String && iso.hasMatch(e.value) ? fmtDateTime(e.value) : e.value.toString())),
  ];
}

/// The details of one item: a coloured header with its name, badges and main figure, round buttons for what can be done with it, and its
/// facts as icon-led rows (emails and phone numbers open, a long press copies).
class ItemSheet extends StatelessWidget {
  const ItemSheet({super.key, required this.spec, required this.item, required this.onChanged, required this.onEdit});
  final ResourceSpec spec;
  final Json item;
  final Future<void> Function() onChanged;
  final VoidCallback onEdit;

  @override
  Widget build(BuildContext context) {
    final auth = context.read<AuthController>();
    final facts = spec.facts?.call(item) ?? defaultFacts(item);
    final actions = spec.actions.where((a) => (a.perm == null || auth.can(a.perm)) && (a.when?.call(item) ?? true)).toList();
    final canEdit = (spec.createBuilder != null || (spec.fields != null && (spec.savePath != null || spec.saveFor != null))) && auth.can(spec.editPerm ?? spec.createPerm) && (spec.canEdit?.call(item) ?? true);
    final canDelete = spec.deletePath != null && auth.can(spec.deletePerm ?? spec.createPerm);
    final badges = spec.badges?.call(item) ?? const <(String, Tone)>[];
    final tone = badges.map((b) => b.$2).where((t) => t == Tone.bad || t == Tone.warn).firstOrNull;
    final title = spec.itemTitle(item);
    final figure = spec.trailing?.call(item);
    return DetailScaffold(
      barTitle: spec.title,
      title: title,
      subtitle: spec.itemSubtitle?.call(item),
      icon: spec.leadingIcon ?? Icons.description_outlined,
      initials: showsInitials(spec) ? initialsOf(title) : null,
      tone: tone,
      figure: (figure ?? '').isEmpty ? null : figure!.split('\n').first,
      chips: [for (final b in badges) HeroChip(niceLabel(b.$1))],
      actions: [
        if (canEdit) QuickAction(Icons.edit_rounded, 'Edit', onEdit, primary: true),
        for (final a in actions) QuickAction(a.icon, a.label, () => _run(context, a), danger: a.danger),
        if (canDelete) QuickAction(Icons.delete_outline_rounded, 'Delete', () => _delete(context), danger: true),
      ],
      children: [
        if (facts.any((f) => (f.$2 ?? '').trim().isNotEmpty)) InfoCard(title: 'Details', icon: Icons.info_outline_rounded, children: infoRows(facts))
        else InfoCard(children: [Padding(padding: const EdgeInsets.symmetric(vertical: 18), child: Column(children: [Icon(Icons.edit_note_rounded, size: 34, color: Theme.of(context).hintColor), const SizedBox(height: 8), Text(canEdit ? 'Nothing else recorded yet. Tap Edit to add details.' : 'Nothing else recorded yet.', textAlign: TextAlign.center, style: TextStyle(color: Theme.of(context).hintColor))]))]),
      ],
    );
  }

  Future<void> _delete(BuildContext context) async {
    final api = context.read<Api>();
    final nav = Navigator.of(context);
    if (!await confirm(context, 'Delete ${spec.itemTitle(item)}? This cannot be undone.', ok: 'Delete', danger: true)) return;
    if (!context.mounted) return;
    if (await guarded(context, () async { await api.delete(spec.deletePath!(item)); }, done: 'Deleted.')) { nav.pop(); await onChanged(); }
  }

  Future<void> _run(BuildContext context, RowAction a) async {
    final api = context.read<Api>();
    final nav = Navigator.of(context);
    if (a.screen != null) { nav.pop(); await nav.push(MaterialPageRoute<void>(builder: (_) => a.screen!(item))); await onChanged(); return; }
    Json answers = {};
    if (a.ask != null) {
      final r = await showModalBottomSheet<Json>(context: context, isScrollControlled: true, showDragHandle: true, builder: (_) => AskSheet(title: a.label, fields: a.ask!));
      if (r == null) return;
      answers = r;
    } else if (a.confirmText != null) {
      if (!await confirm(context, a.confirmText!, ok: a.label, danger: a.danger)) return;
    }
    if (!context.mounted) return;
    final ok = await guarded(context, () async {
      final path = a.path(item);
      final body = a.body?.call(item, answers) ?? (a.method == 'POST' || a.method == 'PUT' ? <String, dynamic>{} : null);
      switch (a.method) {
        case 'POST': await api.post(path, body: body); break;
        case 'PUT': await api.put(path, body: body); break;
        case 'DELETE': await api.delete(path); break;
        default: await api.get(path);
      }
    }, done: a.done ?? '${a.label}: done.');
    if (ok) { nav.pop(); await onChanged(); }
  }
}

/// A few questions in a bottom sheet (a reason, an amount, a reference).
class AskSheet extends StatefulWidget {
  const AskSheet({super.key, required this.title, required this.fields});
  final String title;
  final List<Fld> fields;
  @override
  State<AskSheet> createState() => _AskSheetState();
}

class _AskSheetState extends State<AskSheet> {
  late final FormState2 _form = FormState2(widget.fields, null);

  @override
  Widget build(BuildContext context) => Padding(
        padding: EdgeInsets.fromLTRB(20, 0, 20, MediaQuery.of(context).viewInsets.bottom + 20),
        child: SingleChildScrollView(child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text(widget.title, style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w800)),
          const SizedBox(height: 14),
          FieldsView(_form, onChanged: () => setState(() {})),
          const SizedBox(height: 16),
          SizedBox(width: double.infinity, child: FilledButton(onPressed: () { final e = _form.validate(); if (e != null) { toast(context, e, error: true); return; } Navigator.pop(context, _form.values()); }, child: Text(widget.title))),
        ])),
      );
}

// ---- forms ---------------------------------------------------------------------------------------------------------

/// The values of a form, with simple checks. Used by both the add/edit screens and the small question sheets.
class FormState2 {
  FormState2(this.fields, Json? existing) {
    for (final f in fields) {
      final v = existing?[f.key] ?? f.initial;
      switch (f.kind) {
        case Kind.boolean: _v[f.key] = v == null ? false : v == true; break;
        case Kind.multi: _v[f.key] = <String>[...((v is List ? v : const []).map((e) => e.toString()))]; break;
        case Kind.date: _v[f.key] = v == null ? '' : v.toString().split('T').first; break;
        default: _v[f.key] = v?.toString() ?? '';
      }
    }
    _ctl = {for (final f in fields) if (f.kind != Kind.boolean && f.kind != Kind.select && f.kind != Kind.multi && f.kind != Kind.date) f.key: TextEditingController(text: _v[f.key] as String)};
  }
  final List<Fld> fields;
  final Map<String, dynamic> _v = {};
  late final Map<String, TextEditingController> _ctl;
  Map<String, List<Opt>> loaded = {};

  dynamic get(String k) => _ctl[k]?.text ?? _v[k];
  void set(String k, dynamic v) { _v[k] = v; _ctl[k]?.text = v?.toString() ?? ''; }

  String? validate() {
    for (final f in fields) {
      final raw = get(f.key);
      if (f.required && f.kind == Kind.multi && (raw as List).isEmpty) return 'Choose at least one ${f.label.toLowerCase()}.';
      if (f.required && f.kind != Kind.boolean && f.kind != Kind.multi && (raw ?? '').toString().trim().isEmpty) return '${f.label} is required.';
      if ((f.kind == Kind.number || f.kind == Kind.integer) && (raw ?? '').toString().trim().isNotEmpty && num.tryParse(raw.toString().trim()) == null) return '${f.label} must be a number.';
    }
    return null;
  }

  Json values() {
    final out = <String, dynamic>{};
    for (final f in fields) {
      final raw = get(f.key);
      switch (f.kind) {
        case Kind.boolean: out[f.key] = raw == true; break;
        case Kind.multi: out[f.key] = List<String>.from(raw as List); break;
        case Kind.number: out[f.key] = raw.toString().trim().isEmpty ? (f.nullIfEmpty ? null : 0) : double.parse(raw.toString().trim()); break;
        case Kind.integer: out[f.key] = raw.toString().trim().isEmpty ? (f.nullIfEmpty ? null : 0) : int.parse(raw.toString().trim().split('.').first); break;
        default:
          final t = raw.toString().trim();
          out[f.key] = t.isEmpty && f.nullIfEmpty ? null : (f.kind == Kind.password ? raw.toString() : t);
      }
    }
    return out;
  }
}

class FieldsView extends StatefulWidget {
  const FieldsView(this.form, {super.key, required this.onChanged});
  final FormState2 form;
  final VoidCallback onChanged;
  @override
  State<FieldsView> createState() => _FieldsViewState();
}

class _FieldsViewState extends State<FieldsView> {
  @override
  void initState() {
    super.initState();
    for (final f in widget.form.fields) {
      if (f.optionsPath != null) _loadOptions(f);
    }
  }

  Future<void> _loadOptions(Fld f) async {
    try {
      final r = await context.read<Api>().getList(f.optionsPath!, key: f.optionsKey);
      if (mounted) setState(() => widget.form.loaded[f.key] = [for (final j in r) Opt('${dig(j, f.optionValue)}', '${dig(j, f.optionLabel)}')]);
    } catch (_) { if (mounted) setState(() => widget.form.loaded[f.key] = const []); }
  }

  @override
  Widget build(BuildContext context) {
    final form = widget.form;
    return Column(children: [
      for (final f in form.fields)
        Padding(padding: const EdgeInsets.only(bottom: 14), child: switch (f.kind) {
          Kind.boolean => SwitchListTile(contentPadding: EdgeInsets.zero, title: Text(f.label), subtitle: f.help == null ? null : Text(f.help!), value: form.get(f.key) == true, onChanged: (v) { setState(() => form.set(f.key, v)); widget.onChanged(); }),
          Kind.multi => Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Text(f.label + (f.required ? ' *' : ''), style: const TextStyle(fontWeight: FontWeight.w600)),
              if (f.help != null) Text(f.help!, style: TextStyle(fontSize: 12, color: Theme.of(context).hintColor)),
              const SizedBox(height: 6),
              Wrap(spacing: 8, runSpacing: 4, children: [
                for (final o in (f.options ?? form.loaded[f.key] ?? const <Opt>[]))
                  FilterChip(label: Text(o.label), selected: (form.get(f.key) as List).contains(o.value), onSelected: (on) { setState(() { final l = form.get(f.key) as List; on ? l.add(o.value) : l.remove(o.value); }); widget.onChanged(); }),
              ]),
            ]),
          Kind.select => DropdownButtonFormField<String>(
              initialValue: (form.get(f.key) ?? '').toString().isEmpty ? null : form.get(f.key).toString(),
              isExpanded: true,
              decoration: InputDecoration(labelText: f.label + (f.required ? ' *' : ''), helperText: f.help),
              items: [if (!f.required) const DropdownMenuItem(value: '', child: Text('None')), for (final o in (f.options ?? form.loaded[f.key] ?? const <Opt>[])) DropdownMenuItem(value: o.value, child: Text(o.label, overflow: TextOverflow.ellipsis))],
              onChanged: (v) { setState(() => form.set(f.key, v ?? '')); widget.onChanged(); },
            ),
          Kind.date => InkWell(
              onTap: () async {
                final cur = DateTime.tryParse(form.get(f.key).toString()) ?? DateTime.now();
                final d = await showDatePicker(context: context, initialDate: cur, firstDate: DateTime(2000), lastDate: DateTime(2100));
                if (d != null) { setState(() => form.set(f.key, isoDay(d))); widget.onChanged(); }
              },
              child: InputDecorator(decoration: InputDecoration(labelText: f.label + (f.required ? ' *' : ''), suffixIcon: const Icon(Icons.calendar_today_outlined, size: 18), helperText: f.help), child: Text(form.get(f.key).toString().isEmpty ? 'Choose a date' : fmtDate(form.get(f.key)))),
            ),
          _ => TextField(
              controller: form._ctl[f.key],
              obscureText: f.kind == Kind.password,
              maxLines: f.kind == Kind.multiline ? 3 : 1,
              keyboardType: switch (f.kind) { Kind.number => const TextInputType.numberWithOptions(decimal: true), Kind.integer => TextInputType.number, Kind.email => TextInputType.emailAddress, Kind.phone => TextInputType.phone, Kind.multiline => TextInputType.multiline, _ => TextInputType.text },
              decoration: InputDecoration(labelText: f.label + (f.required ? ' *' : ''), helperText: f.help),
              onChanged: (_) => widget.onChanged(),
            ),
        }),
    ]);
  }
}

class ResourceFormScreen extends StatefulWidget {
  const ResourceFormScreen(this.spec, this.existing, {super.key});
  final ResourceSpec spec;
  final Json? existing;
  @override
  State<ResourceFormScreen> createState() => _ResourceFormScreenState();
}

class _ResourceFormScreenState extends State<ResourceFormScreen> {
  late final FormState2 _form;
  bool _busy = false;

  @override
  void initState() {
    super.initState();
    final editing = widget.existing != null;
    _form = FormState2(widget.spec.fields!.where((f) => editing ? !f.createOnly : !f.editOnly).toList(), widget.existing);
  }

  Future<void> _save() async {
    final e = _form.validate();
    if (e != null) { toast(context, e, error: true); return; }
    final s = widget.spec;
    final values = _form.values();
    final body = s.shape != null ? s.shape!(values, widget.existing) : {if (widget.existing != null) s.idKey: widget.existing![s.idKey] else s.idKey: null, ...values};
    setState(() => _busy = true);
    final api = context.read<Api>();
    final nav = Navigator.of(context);
    final (method, path) = s.saveFor?.call(widget.existing) ?? (s.saveMethod, s.savePath!);
    dynamic answer;
    final ok = await guarded(context, () async { answer = method == 'POST' ? await api.post(path, body: body) : await api.put(path, body: body); }, done: s.onSaved == null ? 'Saved.' : null);
    if (ok && s.onSaved != null && mounted) await s.onSaved!(context, answer);
    if (mounted) setState(() => _busy = false);
    if (ok) nav.pop(true);
  }

  @override
  Widget build(BuildContext context) => Scaffold(
        appBar: AppBar(title: Text(widget.existing == null ? widget.spec.createLabel : 'Edit')),
        body: ListView(padding: const EdgeInsets.all(16), children: [
          FieldsView(_form, onChanged: () => setState(() {})),
          const SizedBox(height: 8),
          FilledButton(onPressed: _busy ? null : _save, child: _busy ? const SizedBox(height: 20, width: 20, child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white)) : const Text('Save')),
          const SizedBox(height: 24),
        ]),
      );
}

/// A one-off form page: show [fields] (with starting [initial] values), and hand the values to [onSave]. For settings pages that are not a list of items.
class FormPage extends StatefulWidget {
  const FormPage({super.key, required this.title, required this.fields, required this.onSave, this.initial, this.intro, this.saveLabel = 'Save', this.popOnSave = true, this.embedded = false});
  /// Shown inside the app frame (no own app bar).
  final bool embedded;
  final String title;
  final String? intro, saveLabel;
  final List<Fld> fields;
  final Json? initial;
  final bool popOnSave;
  final Future<void> Function(Json values) onSave;
  @override
  State<FormPage> createState() => _FormPageState();
}

class _FormPageState extends State<FormPage> {
  late final FormState2 _form = FormState2(widget.fields, widget.initial);
  bool _busy = false;

  Future<void> _save() async {
    final e = _form.validate();
    if (e != null) { toast(context, e, error: true); return; }
    setState(() => _busy = true);
    final nav = Navigator.of(context);
    final ok = await guarded(context, () => widget.onSave(_form.values()), done: 'Saved.');
    if (mounted) setState(() => _busy = false);
    if (ok && widget.popOnSave) nav.pop(true);
  }

  @override
  Widget build(BuildContext context) {
    final body = ListView(padding: const EdgeInsets.all(16), children: [
          if (widget.intro != null) Padding(padding: const EdgeInsets.only(bottom: 14), child: Text(widget.intro!, style: TextStyle(color: Theme.of(context).hintColor))),
          FieldsView(_form, onChanged: () => setState(() {})),
          const SizedBox(height: 8),
          FilledButton(onPressed: _busy ? null : _save, child: _busy ? const SizedBox(height: 20, width: 20, child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white)) : Text(widget.saveLabel ?? 'Save')),
          const SizedBox(height: 24),
        ]);
    return widget.embedded ? body : Scaffold(appBar: AppBar(title: Text(widget.title)), body: body);
  }
}
