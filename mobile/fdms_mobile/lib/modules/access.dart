import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/api.dart';
import '../core/models.dart';
import '../ui/kit.dart';
import 'resource.dart';

String _s(Json j, String k) => (j[k] ?? '').toString();

/// Roles: who can do what. Open a role to change its name, or to choose which permissions it has and which menus it sees.
final rolesAdminSpec = ResourceSpec(
  title: 'Roles', listPath: '/api/v1/roles', itemTitle: (j) => _s(j, 'name'), itemSubtitle: (j) => _s(j, 'description'),
  badges: (j) => [('${j['permissionCount']} permissions', Tone.info), ('${j['menuCount']} menus', Tone.neutral), ('${j['userCount']} users', Tone.neutral)], leadingIcon: Icons.shield_outlined, emptyText: 'No roles.',
  createLabel: 'New role', createPerm: 'roles.manage', savePath: '/api/v1/roles',
  saveFor: (e) => e == null ? ('POST', '/api/v1/roles') : ('PUT', '/api/v1/roles/${e['id']}'),
  fields: const [
    Fld('name', 'Role name', required: true),
    Fld('level', 'Belongs to', kind: Kind.select, required: true, initial: 'client', options: [Opt('client', 'A company (its people)'), Opt('platform', 'The platform operator')]),
    Fld('description', 'What the role is for'),
  ],
  shape: (v, e) => {'name': v['name'], 'level': v['level'] ?? e?['level'] ?? 'client', 'description': v['description']},
  facts: (j) => [('Level', _s(j, 'level')), ('Type', _s(j, 'type')), ('Permissions', _s(j, 'permissionCount')), ('Menus', _s(j, 'menuCount')), ('People', _s(j, 'userCount')), ('About', _s(j, 'description'))],
  actions: [RowAction('Permissions and menus', 'GET', (j) => '', icon: Icons.tune, perm: 'roles.manage', screen: _accessScreen)],
  deletePath: (j) => '/api/v1/roles/${j['id']}', deletePerm: 'roles.manage',
);
Widget _accessScreen(Json role) => RoleAccessScreen(roleId: _s(role, 'id'), roleName: _s(role, 'name'));

/// Tick what a role may do (permissions) and which menu entries it sees, then save.
class RoleAccessScreen extends StatefulWidget {
  const RoleAccessScreen({super.key, required this.roleId, required this.roleName});
  final String roleId, roleName;
  @override
  State<RoleAccessScreen> createState() => _RoleAccessScreenState();
}

class _RoleAccessScreenState extends State<RoleAccessScreen> with SingleTickerProviderStateMixin {
  late final TabController _tabs = TabController(length: 2, vsync: this);
  final _q = TextEditingController();
  List<Json> _perms = [], _modules = [], _sections = [];
  Set<int> _pOn = {}, _mOn = {};
  Object? _error;
  bool _loading = true, _busy = false;

  @override
  void initState() { super.initState(); _load(); }

  Future<void> _load() async {
    final api = context.read<Api>();
    try {
      final access = await api.getMap('/api/v1/roles/${widget.roleId}/access');
      final perms = await api.getList('/api/v1/roles/permissions');
      final tree = await api.getMap('/api/v1/modules');
      if (!mounted) return;
      setState(() {
        _perms = perms; _pOn = {for (final i in (access['permissionIds'] ?? []) as List) i as int}; _mOn = {for (final i in (access['moduleIds'] ?? []) as List) i as int};
        _sections = ((tree['sections'] ?? []) as List).map((e) => Map<String, dynamic>.from(e)).toList(); _modules = ((tree['modules'] ?? []) as List).map((e) => Map<String, dynamic>.from(e)).toList();
        _loading = false; _error = null;
      });
    } catch (e) { if (mounted) setState(() { _error = e; _loading = false; }); }
  }

  Future<void> _save() async {
    setState(() => _busy = true);
    final api = context.read<Api>();
    final nav = Navigator.of(context);
    final ok = await guarded(context, () async {
      await api.put('/api/v1/roles/${widget.roleId}/permissions', body: {'ids': _pOn.toList()});
      await api.put('/api/v1/roles/${widget.roleId}/menus', body: {'ids': _mOn.toList()});
    }, done: 'Access saved. People with this role get it the next time they sign in.');
    if (mounted) setState(() => _busy = false);
    if (ok) nav.pop(true);
  }

  Widget _group(String title, List<Json> items, Set<int> on, String label) {
    final ids = items.map((i) => i['id'] as int).toList();
    final all = ids.every(on.contains);
    return Padding(padding: const EdgeInsets.only(bottom: 10), child: AppCard(padding: EdgeInsets.zero, child: Column(children: [
      CheckboxListTile(
        value: all ? true : (ids.any(on.contains) ? null : false), tristate: true, controlAffinity: ListTileControlAffinity.leading,
        title: Text(title, style: const TextStyle(fontWeight: FontWeight.w800)), subtitle: Text('${ids.where(on.contains).length} of ${ids.length}'),
        onChanged: (_) => setState(() { if (all) { on.removeAll(ids); } else { on.addAll(ids); } }),
      ),
      const Divider(height: 1),
      for (final i in items) CheckboxListTile(dense: true, controlAffinity: ListTileControlAffinity.leading, value: on.contains(i['id']), title: Text(_s(i, label)), subtitle: i['name'] != null && label != 'name' ? Text(_s(i, 'name'), style: const TextStyle(fontSize: 11)) : null, onChanged: (v) => setState(() => v == true ? on.add(i['id'] as int) : on.remove(i['id']))),
    ])));
  }

  @override
  Widget build(BuildContext context) {
    final q = _q.text.trim().toLowerCase();
    final byModule = <String, List<Json>>{};
    for (final p in _perms.where((p) => q.isEmpty || '${p['displayName']} ${p['name']}'.toLowerCase().contains(q))) { byModule.putIfAbsent(_s(p, 'module'), () => []).add(p); }
    return Scaffold(
      appBar: AppBar(title: Text('Access: ${widget.roleName}'), actions: [TextButton(onPressed: _busy || _loading ? null : _save, child: const Text('Save'))], bottom: TabBar(controller: _tabs, tabs: const [Tab(text: 'What it can do'), Tab(text: 'Menus it sees')])),
      body: _loading ? const Center(child: CircularProgressIndicator()) : _error != null ? ErrorState(_error!, onRetry: _load) : Column(children: [
        Padding(padding: const EdgeInsets.fromLTRB(16, 12, 16, 4), child: TextField(controller: _q, onChanged: (_) => setState(() {}), decoration: const InputDecoration(hintText: 'Search…', prefixIcon: Icon(Icons.search)))),
        Expanded(child: TabBarView(controller: _tabs, children: [
          ListView(padding: const EdgeInsets.all(16), children: [for (final e in byModule.entries) _group(e.key, e.value, _pOn, 'displayName')]),
          ListView(padding: const EdgeInsets.all(16), children: [
            for (final s in _sections) ...() {
              final items = _modules.where((m) => m['sectionModuleId'] == s['id'] && m['isActive'] != false && (q.isEmpty || _s(m, 'name').toLowerCase().contains(q))).toList();
              return items.isEmpty ? <Widget>[] : [_group(_s(s, 'name'), items, _mOn, 'name')];
            }(),
          ]),
        ])),
      ]),
    );
  }
}

// ---- menus and modules ---------------------------------------------------------------------------------------------

const _iconNames = ['home', 'cpu', 'receipt', 'upload', 'calendar', 'cloud-upload', 'key', 'bolt', 'building', 'users', 'shield', 'squares', 'clipboard', 'chart', 'file-text', 'check', 'layers', 'lock', 'stack', 'card', 'bell', 'mail', 'send', 'message', 'pulse', 'repeat', 'cart', 'user-check', 'toggle', 'folder', 'template'];
final _iconOpts = [for (final i in _iconNames) Opt(i, i)];

final sectionsSpec = ResourceSpec(
  title: 'Sections', listPath: '/api/v1/modules', itemsKey: 'sections', itemTitle: (j) => _s(j, 'name'), itemSubtitle: (j) => 'Order ${j['sortOrder']}', badges: (j) => [if (j['status'] == false) ('Hidden', Tone.warn)], leadingIcon: Icons.view_agenda_outlined,
  createLabel: 'New section', createPerm: 'modules.manage', savePath: '/api/v1/modules/sections',
  fields: const [Fld('name', 'Name', required: true), Fld('color', 'Colour', initial: '#475569', help: 'A hex colour such as #2563eb'), Fld('sortOrder', 'Order', kind: Kind.integer, initial: 1), Fld('status', 'Shown in the menu', kind: Kind.boolean, initial: true)],
  shape: (v, e) => {'id': e?['id'] ?? 0, ...v, 'sortOrder': v['sortOrder'] ?? 0}, deletePath: (j) => '/api/v1/modules/sections/${j['id']}', deletePerm: 'modules.manage',
);

final modulesSpec = ResourceSpec(
  title: 'Modules', listPath: '/api/v1/modules', itemsKey: 'modules', itemTitle: (j) => _s(j, 'name'), itemSubtitle: (j) => '${_s(j, 'slugname')} · needs ${_s(j, 'accessName').isEmpty ? 'nothing' : _s(j, 'accessName')}', badges: (j) => [if (j['isActive'] == false) ('Off', Tone.warn)], leadingIcon: Icons.apps,
  createLabel: 'New module', createPerm: 'modules.manage', savePath: '/api/v1/modules/modules',
  fields: [
    const Fld('sectionModuleId', 'Section', kind: Kind.select, required: true, optionsPath: '/api/v1/modules', optionsKey: 'sections'), const Fld('name', 'Name', required: true), const Fld('slugname', 'Page (slug)', required: true, help: 'The page it opens, e.g. receipts'),
    Fld('icon', 'Icon', kind: Kind.select, required: true, options: _iconOpts, initial: 'squares'), const Fld('description', 'Description'), const Fld('accessName', 'Permission needed', kind: Kind.select, optionsPath: '/api/v1/modules/permissions', optionValue: 'name', optionLabel: 'displayName'),
    const Fld('sortOrder', 'Order', kind: Kind.integer, initial: 1), const Fld('isActive', 'In use', kind: Kind.boolean, initial: true),
  ],
  shape: (v, e) => {'id': e?['id'] ?? 0, ...v, 'sectionModuleId': int.tryParse('${v['sectionModuleId']}') ?? 0, 'sortOrder': v['sortOrder'] ?? 0},
  deletePath: (j) => '/api/v1/modules/modules/${j['id']}', deletePerm: 'modules.manage',
);

final submodulesSpec = ResourceSpec(
  title: 'Sub-pages', listPath: '/api/v1/modules', itemsKey: 'submodules', itemTitle: (j) => _s(j, 'name'), itemSubtitle: (j) => _s(j, 'route'), badges: (j) => [if (j['isActive'] == false) ('Off', Tone.warn)], leadingIcon: Icons.subdirectory_arrow_right,
  createLabel: 'New sub-page', createPerm: 'modules.manage', savePath: '/api/v1/modules/submodules',
  fields: [
    const Fld('moduleId', 'Module', kind: Kind.select, required: true, optionsPath: '/api/v1/modules', optionsKey: 'modules'), const Fld('name', 'Name', required: true), const Fld('route', 'Page', required: true, help: 'e.g. accounting/reports/ledger'),
    Fld('icon', 'Icon', kind: Kind.select, required: true, options: _iconOpts, initial: 'file-text'), const Fld('accessName', 'Permission needed', kind: Kind.select, optionsPath: '/api/v1/modules/permissions', optionValue: 'name', optionLabel: 'displayName'),
    const Fld('order', 'Order', kind: Kind.integer, initial: 1), const Fld('isActive', 'In use', kind: Kind.boolean, initial: true),
  ],
  shape: (v, e) => {'id': e?['id'] ?? 0, ...v, 'moduleId': int.tryParse('${v['moduleId']}') ?? 0, 'order': v['order'] ?? 0},
  deletePath: (j) => '/api/v1/modules/submodules/${j['id']}', deletePerm: 'modules.manage',
);

final permissionsSpec = ResourceSpec(
  title: 'Permissions', listPath: '/api/v1/modules/permissions', itemTitle: (j) => _s(j, 'displayName'), itemSubtitle: (j) => '${_s(j, 'name')} · ${_s(j, 'module')}', leadingIcon: Icons.verified_user_outlined,
  createLabel: 'New permission', createPerm: 'modules.manage', savePath: '/api/v1/modules/permissions',
  fields: const [Fld('name', 'Name', required: true, help: 'For example reports.view'), Fld('displayName', 'What it allows', required: true), Fld('module', 'Group', required: true)],
  shape: (v, e) => {'id': e?['id'] ?? 0, ...v}, deletePath: (j) => '/api/v1/modules/permissions/${j['id']}', deletePerm: 'modules.manage',
);

/// The menu tree and permission list: sections, modules, sub-pages and permissions, each with add, change and delete.
class ModulesAdminScreen extends StatelessWidget {
  const ModulesAdminScreen({super.key});
  @override
  Widget build(BuildContext context) => DefaultTabController(
        length: 4,
        child: Column(children: [
          const Material(color: Colors.transparent, child: TabBar(isScrollable: true, tabAlignment: TabAlignment.start, tabs: [Tab(text: 'Sections'), Tab(text: 'Modules'), Tab(text: 'Sub-pages'), Tab(text: 'Permissions')])),
          Expanded(child: TabBarView(children: [ResourceScreen(sectionsSpec), ResourceScreen(modulesSpec), ResourceScreen(submodulesSpec), ResourceScreen(permissionsSpec)])),
        ]),
      );
}
