import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/api.dart';
import '../core/auth.dart';
import '../core/models.dart';
import '../modules/registry.dart';
import '../ui/kit.dart';
import 'about.dart';
import 'drawer.dart';
import 'notifications.dart';
import '../modules/approvals.dart' show RouteOpener;
import 'profile.dart';

/// The app's frame: a drawer built from the same menu tree the web console shows (so each role sees only its own modules), a few quick tabs, and the module itself.
class Shell extends StatefulWidget {
  const Shell({super.key});
  @override
  State<Shell> createState() => _ShellState();
}

class _ShellState extends State<Shell> {
  String? _route;
  String _title = '';
  int _unread = 0;
  final _scaffold = GlobalKey<ScaffoldState>();

  static const _home = ['dashboard', 'overview', 'partner/overview'];

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) { _open(_firstRoute()); _loadUnread(); });
  }

  Session get _s => context.read<AuthController>().session!;

  String? _firstRoute() {
    final menus = _s.menus;
    for (final h in _home) { final m = menus.where((m) => m.slug == h).firstOrNull; if (m != null) return m.slug; }
    return menus.firstOrNull?.slug;
  }

  MenuNode? _menuFor(String route) {
    for (final m in _s.menus) {
      if (m.slug == route) return m;
      if (m.children.any((c) => c.route == route)) return m;
    }
    return null;
  }

  void _open(String? route) {
    if (route == null) return;
    final m = _menuFor(route);
    if (m == null) return;
    final sub = m.children.where((c) => c.route == route).firstOrNull;
    setState(() { _route = route; _title = sub?.name ?? m.name; });
  }

  /// A notification or a dashboard card links to "/billing", "/approvals" ...: open that module if this person has it.
  void _openLink(String link) {
    final route = link.startsWith('/') ? link.substring(1) : link;
    if (_menuFor(route) != null) { _open(route); } else if (mounted) { toast(context, 'That page is not in your menu.'); }
    _loadUnread();
  }

  Future<void> _loadUnread() async {
    try {
      final j = await context.read<Api>().getMap('/api/v1/notifications', query: {'take': 1});
      if (mounted) setState(() => _unread = (j['unread'] ?? 0) as int);
    } catch (_) { /* the bell just stays quiet */ }
  }

  @override
  Widget build(BuildContext context) {
    final auth = context.watch<AuthController>();
    final s = auth.session;
    if (s == null) return const SizedBox.shrink();
    final quick = <(String, String, IconData)>[];
    for (final h in _home) { if (s.menus.any((m) => m.slug == h)) { quick.add((h, 'Home', Icons.home_outlined)); break; } }
    if (s.menus.any((m) => m.slug == 'receipts')) quick.add(('receipts', 'Receipts', Icons.receipt_long_outlined));
    if (s.menus.any((m) => m.slug == 'devices')) quick.add(('devices', 'Devices', Icons.memory));
    final selected = quick.indexWhere((q) => q.$1 == _route);

    return Scaffold(
      key: _scaffold,
      appBar: AppBar(
        title: Text(_title.isEmpty ? 'FiscalHub' : _title),
        actions: [
          IconButton(
            tooltip: 'Notifications',
            onPressed: () async { await Navigator.push(context, MaterialPageRoute(builder: (_) => NotificationsScreen(onOpen: _openLink))); _loadUnread(); },
            icon: Badge(isLabelVisible: _unread > 0, label: Text('$_unread'), child: const Icon(Icons.notifications_none)),
          ),
          PopupMenuButton<String>(
            tooltip: 'Account',
            icon: CircleAvatar(radius: 15, backgroundColor: kBrand, child: Text(s.user.initials, style: const TextStyle(color: Colors.white, fontSize: 12, fontWeight: FontWeight.w700))),
            onSelected: (v) async {
              if (v == 'profile') { if (context.mounted) await Navigator.push(context, MaterialPageRoute(builder: (_) => const ProfileScreen())); }
              if (v == 'refer') _open('referrals');
              if (v == 'terms' || v == 'privacy') { if (context.mounted) await Navigator.push(context, MaterialPageRoute(builder: (_) => LegalDocScreen(which: v))); }
              if (v == 'dev') { if (context.mounted) await Navigator.push(context, MaterialPageRoute(builder: (_) => const DeveloperScreen())); }
              if (v == 'out') { final ok = await confirm(context, 'Sign out of FiscalHub?', ok: 'Sign out'); if (ok) await auth.logout(); }
            },
            itemBuilder: (_) => [
              PopupMenuItem(enabled: false, child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [Text(s.user.displayName, style: const TextStyle(fontWeight: FontWeight.w700)), Text(s.user.clientName ?? 'Platform operator', style: const TextStyle(fontSize: 12))])),
              const PopupMenuDivider(),
              const PopupMenuItem(value: 'profile', child: ListTile(dense: true, leading: Icon(Icons.person_outline), title: Text('My profile'))),
              if (s.menus.any((m) => m.slug == 'referrals')) const PopupMenuItem(value: 'refer', child: ListTile(dense: true, leading: Icon(Icons.card_giftcard), title: Text('Refer and earn'))),
              const PopupMenuItem(value: 'terms', child: ListTile(dense: true, leading: Icon(Icons.gavel_outlined), title: Text('Terms and conditions'))),
              const PopupMenuItem(value: 'privacy', child: ListTile(dense: true, leading: Icon(Icons.privacy_tip_outlined), title: Text('Privacy policy'))),
              const PopupMenuItem(value: 'dev', child: ListTile(dense: true, leading: Icon(Icons.code), title: Text('Developer details'))),
              const PopupMenuDivider(),
              const PopupMenuItem(value: 'out', child: ListTile(dense: true, leading: Icon(Icons.logout), title: Text('Sign out'))),
            ],
          ),
          const SizedBox(width: 4),
        ],
      ),
      drawer: AppDrawer(
        session: s, route: _route, onOpen: _open,
        onProfile: () => Navigator.push(context, MaterialPageRoute(builder: (_) => const ProfileScreen())),
        onSignOut: () async { final ok = await confirm(context, 'Sign out of FiscalHub?', ok: 'Sign out'); if (ok) await auth.logout(); },
      ),
      body: _route == null ? const Center(child: CircularProgressIndicator()) : Provider<RouteOpener>.value(value: RouteOpener(_openLink), child: KeyedSubtree(key: ValueKey(_route), child: buildModule(_route!, _menuFor(_route!)!, s))),
      bottomNavigationBar: quick.length < 2 ? null : NavigationBar(
        selectedIndex: selected < 0 ? quick.length : selected,
        onDestinationSelected: (i) => i < quick.length ? _open(quick[i].$1) : _scaffold.currentState?.openDrawer(),
        destinations: [for (final q in quick) NavigationDestination(icon: Icon(q.$3), label: q.$2), const NavigationDestination(icon: Icon(Icons.menu), label: 'More')],
      ),
    );
  }
}
