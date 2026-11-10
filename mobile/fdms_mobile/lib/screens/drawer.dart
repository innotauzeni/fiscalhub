import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/config.dart';
import '../core/models.dart';
import '../ui/kit.dart';
import '../ui/premium.dart';
import 'about.dart';
import 'auth_ui.dart' show BrandMark, BrandWord;

const _foldedKey = 'drawer.folded';

Color sectionColor(String hex) {
  final h = hex.replaceAll('#', '');
  final v = int.tryParse(h.length == 6 ? 'FF$h' : h, radix: 16);
  return v == null ? kBrand : Color(v);
}

/// The page a module opens: its only sub-page when it has exactly one, else the module itself (same rule as the web sidebar).
String moduleTarget(MenuNode m) => m.children.length == 1 ? m.children.first.route : m.slug;

/// The app's menu, laid out like the web console's sidebar: each section is a collapsible group (its colour dot and name) holding its
/// modules; a module with several pages opens like an accordion with a tree line; the page you are on is highlighted. Sections you fold
/// stay folded next time.
class AppDrawer extends StatefulWidget {
  const AppDrawer({super.key, required this.session, required this.route, required this.onOpen, required this.onProfile, required this.onSignOut});
  final Session session;
  final String? route;
  final ValueChanged<String> onOpen;
  final VoidCallback onProfile, onSignOut;
  @override
  State<AppDrawer> createState() => _AppDrawerState();
}

class _AppDrawerState extends State<AppDrawer> {
  late final AppConfig _config = context.read<AppConfig>();
  late final Set<String> _folded = {...((jsonDecode(_config.getString(_foldedKey) ?? '[]') as List).cast<String>())};
  final Set<int> _expanded = {};
  final _search = TextEditingController();

  @override
  void initState() {
    super.initState();
    // open the module that holds the current page
    for (final s in widget.session.sections) {
      for (final m in s.children) {
        if (m.children.length > 1 && m.children.any((c) => c.route == widget.route)) { _expanded.add(m.id); _folded.remove(s.name); }
      }
    }
  }

  @override
  void dispose() { _search.dispose(); super.dispose(); }

  void _toggleSection(String name) {
    setState(() => _folded.contains(name) ? _folded.remove(name) : _folded.add(name));
    _config.setString(_foldedKey, jsonEncode(_folded.toList()));
  }

  bool _active(MenuNode m) => widget.route == m.slug || m.children.any((c) => c.route == widget.route);

  void _go(String route) { Navigator.pop(context); widget.onOpen(route); }

  @override
  Widget build(BuildContext context) {
    final s = widget.session;
    final q = _search.text.trim().toLowerCase();
    return Drawer(
      width: (MediaQuery.sizeOf(context).width * 0.86).clamp(0, 360).toDouble(),
      shape: const RoundedRectangleBorder(borderRadius: BorderRadius.horizontal(right: Radius.circular(24))),
      child: Column(children: [
        _header(s),
        Expanded(
          child: ListView(padding: const EdgeInsets.fromLTRB(12, 12, 12, 12), children: [
            if (q.isEmpty) ...[for (final sec in s.sections) _section(sec)] else ..._results(s, q),
            if (s.sections.isEmpty) const Padding(padding: EdgeInsets.all(16), child: Text('Your role has no menu entries yet. Ask your administrator.')),
          ]),
        ),
        _footer(),
      ]),
    );
  }

  Widget _header(Session s) => DecoratedBox(
        decoration: const BoxDecoration(gradient: LinearGradient(colors: [Color(0xFF1D4ED8), Color(0xFF4F46E5), Color(0xFF7C3AED)], begin: Alignment.topLeft, end: Alignment.bottomRight), borderRadius: BorderRadius.only(topRight: Radius.circular(24))),
        child: SafeArea(
          bottom: false,
          child: Padding(
            padding: const EdgeInsets.fromLTRB(18, 14, 18, 16),
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Row(children: [
                const BrandMark(scale: 0.85),
                const SizedBox(width: 10),
                const Expanded(child: BrandWord(size: 18)),
                IconButton(tooltip: 'Close menu', onPressed: () => Navigator.pop(context), icon: const Icon(Icons.close_rounded, color: Colors.white)),
              ]),
              const SizedBox(height: 14),
              InkWell(
                onTap: () { Navigator.pop(context); widget.onProfile(); },
                borderRadius: BorderRadius.circular(16),
                child: Container(
                  padding: const EdgeInsets.all(10),
                  decoration: BoxDecoration(color: Colors.white.withValues(alpha: 0.13), borderRadius: BorderRadius.circular(16), border: Border.all(color: Colors.white.withValues(alpha: 0.18))),
                  child: Row(children: [
                    CircleAvatar(backgroundColor: Colors.white, radius: 20, child: Text(s.user.initials, style: const TextStyle(color: Color(0xFF4338CA), fontWeight: FontWeight.w800))),
                    const SizedBox(width: 12),
                    Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                      Text(s.user.displayName, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(color: Colors.white, fontWeight: FontWeight.w700, fontSize: 15)),
                      Text(s.user.clientName ?? 'Platform operator', maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(color: Colors.white.withValues(alpha: 0.8), fontSize: 12.5)),
                    ])),
                    if (s.user.roles.isNotEmpty) Flexible(child: Padding(padding: const EdgeInsets.only(left: 6), child: HeroChip(s.user.roles.first))),
                  ]),
                ),
              ),
              const SizedBox(height: 12),
              TextField(
                controller: _search, onChanged: (_) => setState(() {}),
                style: const TextStyle(color: Colors.white),
                cursorColor: Colors.white,
                decoration: InputDecoration(
                  isDense: true, hintText: 'Search the menu', hintStyle: TextStyle(color: Colors.white.withValues(alpha: 0.7)),
                  prefixIcon: Icon(Icons.search_rounded, color: Colors.white.withValues(alpha: 0.85)),
                  suffixIcon: _search.text.isEmpty ? null : IconButton(icon: const Icon(Icons.close_rounded, color: Colors.white), onPressed: () => setState(_search.clear)),
                  filled: true, fillColor: Colors.white.withValues(alpha: 0.14),
                  border: OutlineInputBorder(borderRadius: BorderRadius.circular(14), borderSide: BorderSide.none),
                  enabledBorder: OutlineInputBorder(borderRadius: BorderRadius.circular(14), borderSide: BorderSide.none),
                  focusedBorder: OutlineInputBorder(borderRadius: BorderRadius.circular(14), borderSide: BorderSide(color: Colors.white.withValues(alpha: 0.6))),
                ),
              ),
            ]),
          ),
        ),
      );

  Widget _section(SectionNode sec) {
    final color = sectionColor(sec.color);
    final folded = _folded.contains(sec.name);
    final hasActive = sec.children.any(_active);
    return Padding(
      padding: const EdgeInsets.only(bottom: 6),
      child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
        InkWell(
          borderRadius: BorderRadius.circular(10),
          onTap: () => _toggleSection(sec.name),
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 9),
            child: Row(children: [
              Container(width: 9, height: 9, decoration: BoxDecoration(color: color, shape: BoxShape.circle, boxShadow: [BoxShadow(color: color.withValues(alpha: 0.5), blurRadius: 6)])),
              const SizedBox(width: 10),
              Expanded(child: Text(sec.name.toUpperCase(), style: TextStyle(fontSize: 11.5, fontWeight: FontWeight.w800, letterSpacing: 1, color: hasActive ? color : Theme.of(context).hintColor))),
              if (folded) Container(
                margin: const EdgeInsets.only(right: 6), padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 2),
                decoration: BoxDecoration(color: color.withValues(alpha: 0.12), borderRadius: BorderRadius.circular(10)),
                child: Text('${sec.children.length}', style: TextStyle(fontSize: 11, fontWeight: FontWeight.w800, color: color)),
              ),
              AnimatedRotation(turns: folded ? -0.25 : 0, duration: const Duration(milliseconds: 200), child: Icon(Icons.expand_more_rounded, size: 18, color: Theme.of(context).hintColor)),
            ]),
          ),
        ),
        AnimatedSize(
          duration: const Duration(milliseconds: 220), curve: Curves.easeOutCubic, alignment: Alignment.topCenter,
          child: folded ? const SizedBox(width: double.infinity) : Container(
            margin: const EdgeInsets.only(left: 12),
            padding: const EdgeInsets.only(left: 8),
            decoration: BoxDecoration(border: Border(left: BorderSide(color: color.withValues(alpha: 0.28), width: 2))),
            child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [for (final m in sec.children) _module(m, color)]),
          ),
        ),
      ]),
    );
  }

  Widget _module(MenuNode m, Color color) {
    final active = _active(m);
    final accordion = m.children.length > 1;
    final open = _expanded.contains(m.id);
    final scheme = Theme.of(context).colorScheme;
    final row = InkWell(
      borderRadius: BorderRadius.circular(14),
      onTap: () => accordion ? setState(() => open ? _expanded.remove(m.id) : _expanded.add(m.id)) : _go(moduleTarget(m)),
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 180),
        margin: const EdgeInsets.symmetric(vertical: 2),
        padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 7),
        decoration: BoxDecoration(color: active && !accordion ? color.withValues(alpha: 0.10) : null, borderRadius: BorderRadius.circular(14)),
        child: Row(children: [
          AnimatedContainer(
            duration: const Duration(milliseconds: 180),
            padding: const EdgeInsets.all(7),
            decoration: BoxDecoration(
              gradient: active ? LinearGradient(colors: [color, Color.lerp(color, const Color(0xFF7C3AED), 0.35)!]) : null,
              color: active ? null : color.withValues(alpha: 0.10),
              borderRadius: BorderRadius.circular(10),
              boxShadow: active ? [BoxShadow(color: color.withValues(alpha: 0.35), blurRadius: 8, offset: const Offset(0, 3))] : null,
            ),
            child: Icon(iconFor(m.icon), size: 18, color: active ? Colors.white : color),
          ),
          const SizedBox(width: 12),
          Expanded(child: Text(m.name, style: TextStyle(fontSize: 14.5, fontWeight: active ? FontWeight.w800 : FontWeight.w600, color: active ? color : scheme.onSurface))),
          if (accordion) AnimatedRotation(turns: open ? 0.5 : 0, duration: const Duration(milliseconds: 200), child: Icon(Icons.expand_more_rounded, size: 20, color: Theme.of(context).hintColor)),
        ]),
      ),
    );
    if (!accordion) return row;
    return Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
      row,
      AnimatedSize(
        duration: const Duration(milliseconds: 200), curve: Curves.easeOutCubic, alignment: Alignment.topCenter,
        child: !open ? const SizedBox(width: double.infinity) : Container(
          margin: const EdgeInsets.only(left: 22, bottom: 4),
          padding: const EdgeInsets.only(left: 12),
          decoration: BoxDecoration(border: Border(left: BorderSide(color: Theme.of(context).dividerColor, width: 1.5))),
          child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
            for (final c in m.children)
              InkWell(
                borderRadius: BorderRadius.circular(10),
                onTap: () => _go(c.route),
                child: Container(
                  margin: const EdgeInsets.symmetric(vertical: 1),
                  padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 9),
                  decoration: BoxDecoration(color: widget.route == c.route ? color.withValues(alpha: 0.10) : null, borderRadius: BorderRadius.circular(10)),
                  child: Row(children: [
                    Icon(iconFor(c.icon), size: 16, color: widget.route == c.route ? color : Theme.of(context).hintColor),
                    const SizedBox(width: 10),
                    Expanded(child: Text(c.name, style: TextStyle(fontSize: 13.5, fontWeight: widget.route == c.route ? FontWeight.w800 : FontWeight.w500, color: widget.route == c.route ? color : scheme.onSurface.withValues(alpha: 0.8)))),
                  ]),
                ),
              ),
          ]),
        ),
      ),
    ]);
  }

  List<Widget> _results(Session s, String q) {
    final out = <Widget>[];
    for (final sec in s.sections) {
      final color = sectionColor(sec.color);
      for (final m in sec.children) {
        final pages = m.children.length > 1 ? m.children.map((c) => (c.name, c.route, c.icon)).toList() : [(m.name, moduleTarget(m), m.icon)];
        for (final p in pages) {
          final label = m.children.length > 1 ? '${m.name} · ${p.$1}' : p.$1;
          final words = '$label ${sec.name} ${m.children.length == 1 ? m.children.first.name : ''}'.toLowerCase();
          if (!words.contains(q)) continue;
          out.add(ListTile(
            shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(14)),
            leading: Container(padding: const EdgeInsets.all(7), decoration: BoxDecoration(color: color.withValues(alpha: 0.12), borderRadius: BorderRadius.circular(10)), child: Icon(iconFor(p.$3), size: 18, color: color)),
            title: Text(label, style: const TextStyle(fontWeight: FontWeight.w700)),
            subtitle: Text(sec.name, style: TextStyle(color: color, fontSize: 12, fontWeight: FontWeight.w600)),
            onTap: () => _go(p.$2),
          ));
        }
      }
    }
    if (out.isEmpty) out.add(Padding(padding: const EdgeInsets.all(20), child: Text('Nothing in the menu matches "${_search.text.trim()}".', textAlign: TextAlign.center, style: TextStyle(color: Theme.of(context).hintColor))));
    return out;
  }

  Widget _footer() => SafeArea(
        top: false,
        child: Container(
          padding: const EdgeInsets.fromLTRB(12, 8, 12, 8),
          decoration: BoxDecoration(border: Border(top: BorderSide(color: Theme.of(context).dividerColor))),
          child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.stretch, children: [
            Row(children: [
              Expanded(child: TextButton.icon(onPressed: () { Navigator.pop(context); widget.onProfile(); }, icon: const Icon(Icons.person_outline_rounded, size: 19), label: const Text('My profile'))),
              Expanded(child: TextButton.icon(onPressed: () { Navigator.pop(context); widget.onSignOut(); }, style: TextButton.styleFrom(foregroundColor: const Color(0xFFDC2626)), icon: const Icon(Icons.logout_rounded, size: 19), label: const Text('Sign out'))),
            ]),
            const Padding(padding: EdgeInsets.only(top: 2, bottom: 4), child: LegalLinks()),
          ]),
        ),
      );
}
