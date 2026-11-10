import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:url_launcher/url_launcher.dart';

import 'kit.dart';

/// Building blocks for detail screens: a coloured header with the headline facts, a row of round quick actions, and cards of icon-led rows.
/// Everything sizes from its content and the user's text size; nothing has a fixed height.

/// The colours of a detail header: brand blue/violet by default, or tinted by the item's status.
LinearGradient heroGradient([Tone? tone]) {
  final base = switch (tone) {
    Tone.ok => const [Color(0xFF047857), Color(0xFF0D9488)],
    Tone.warn => const [Color(0xFFB45309), Color(0xFFD97706)],
    Tone.bad => const [Color(0xFFB91C1C), Color(0xFFE11D48)],
    _ => const [Color(0xFF1D4ED8), Color(0xFF6D28D9)],
  };
  return LinearGradient(colors: base, begin: Alignment.topLeft, end: Alignment.bottomRight);
}

/// A chip on the header (white on the gradient).
class HeroChip extends StatelessWidget {
  const HeroChip(this.text, {super.key, this.icon});
  final String text;
  final IconData? icon;
  @override
  Widget build(BuildContext context) => Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 5),
        decoration: BoxDecoration(color: Colors.white.withValues(alpha: 0.18), borderRadius: BorderRadius.circular(30), border: Border.all(color: Colors.white.withValues(alpha: 0.22))),
        child: Row(mainAxisSize: MainAxisSize.min, children: [
          if (icon != null) ...[Icon(icon, size: 13, color: Colors.white), const SizedBox(width: 5)],
          Flexible(child: Text(text, style: const TextStyle(color: Colors.white, fontSize: 12, fontWeight: FontWeight.w700), overflow: TextOverflow.ellipsis)),
        ]),
      );
}

/// One round button in the quick-action row.
class QuickAction {
  const QuickAction(this.icon, this.label, this.onTap, {this.danger = false, this.primary = false});
  final IconData icon;
  final String label;
  final VoidCallback? onTap;
  final bool danger, primary;
}

/// A detail screen: gradient header (icon, title, subtitle, chips, a big figure) that continues under the app bar, quick actions floating on
/// its edge, then the content cards.
class DetailScaffold extends StatelessWidget {
  const DetailScaffold({
    super.key, required this.title, this.subtitle, this.icon = Icons.description_outlined, this.initials, this.chips = const [], this.figure, this.figureLabel,
    this.tone, this.actions = const [], this.appBarActions, required this.children, this.onRefresh, this.barTitle,
  });
  final String title;
  final String? subtitle, figure, figureLabel, initials, barTitle;
  final IconData icon;
  final List<Widget> chips;
  final Tone? tone;
  final List<QuickAction> actions;
  final List<Widget>? appBarActions;
  final List<Widget> children;
  final Future<void> Function()? onRefresh;

  @override
  Widget build(BuildContext context) {
    final g = heroGradient(tone);
    final list = ListView(padding: EdgeInsets.zero, children: [
      DecoratedBox(
        decoration: BoxDecoration(gradient: g),
        child: Padding(
          padding: EdgeInsets.fromLTRB(20, 4, 20, actions.isEmpty ? 26 : 50),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Container(
                padding: const EdgeInsets.all(12),
                decoration: BoxDecoration(color: Colors.white.withValues(alpha: 0.18), borderRadius: BorderRadius.circular(18), border: Border.all(color: Colors.white.withValues(alpha: 0.25))),
                child: initials != null
                    ? SizedBox.square(dimension: 26, child: Center(child: FittedBox(child: Text(initials!, style: const TextStyle(color: Colors.white, fontWeight: FontWeight.w800, fontSize: 18)))))
                    : Icon(icon, color: Colors.white, size: 26),
              ),
              const SizedBox(width: 14),
              Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Text(title, style: const TextStyle(color: Colors.white, fontSize: 22, fontWeight: FontWeight.w800, height: 1.2, letterSpacing: -0.3)),
                if ((subtitle ?? '').isNotEmpty) Padding(padding: const EdgeInsets.only(top: 4), child: Text(subtitle!, style: TextStyle(color: Colors.white.withValues(alpha: 0.82), fontSize: 13.5, height: 1.35))),
              ])),
            ]),
            if (figure != null) ...[
              const SizedBox(height: 18),
              if (figureLabel != null) Text(figureLabel!.toUpperCase(), style: TextStyle(color: Colors.white.withValues(alpha: 0.7), fontSize: 11, fontWeight: FontWeight.w700, letterSpacing: 1)),
              FittedBox(fit: BoxFit.scaleDown, alignment: Alignment.centerLeft, child: Text(figure!, style: const TextStyle(color: Colors.white, fontSize: 34, fontWeight: FontWeight.w800, letterSpacing: -0.8))),
            ],
            if (chips.isNotEmpty) Padding(padding: const EdgeInsets.only(top: 14), child: Wrap(spacing: 8, runSpacing: 8, children: chips)),
          ]),
        ),
      ),
      if (actions.isNotEmpty) Transform.translate(offset: const Offset(0, -30), child: Padding(padding: const EdgeInsets.symmetric(horizontal: 16), child: QuickActionBar(actions))),
      Transform.translate(
        offset: Offset(0, actions.isEmpty ? -14 : -18),
        child: Padding(padding: const EdgeInsets.fromLTRB(16, 0, 16, 24), child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: children)),
      ),
    ]);
    return Scaffold(
      appBar: AppBar(
        title: Text(barTitle ?? '', style: const TextStyle(color: Colors.white)),
        foregroundColor: Colors.white,
        backgroundColor: Colors.transparent,
        flexibleSpace: SizedBox.expand(child: DecoratedBox(decoration: BoxDecoration(gradient: g))),
        surfaceTintColor: Colors.transparent,
        scrolledUnderElevation: 0,
        systemOverlayStyle: SystemUiOverlayStyle.light,
        actions: appBarActions,
      ),
      body: onRefresh == null ? list : RefreshIndicator(onRefresh: onRefresh!, child: list),
    );
  }
}

/// Round actions on a floating card; scrolls sideways when there are more than fit.
class QuickActionBar extends StatelessWidget {
  const QuickActionBar(this.actions, {super.key});
  final List<QuickAction> actions;
  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    Widget one(QuickAction a) {
      final c = a.danger ? const Color(0xFFDC2626) : kBrand;
      final disabled = a.onTap == null;
      return Opacity(
        opacity: disabled ? 0.4 : 1,
        child: InkWell(
          onTap: a.onTap, borderRadius: BorderRadius.circular(16),
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 10),
            child: Column(mainAxisSize: MainAxisSize.min, children: [
              Container(
                padding: const EdgeInsets.all(12),
                decoration: BoxDecoration(shape: BoxShape.circle, color: a.primary ? c : c.withValues(alpha: 0.1), boxShadow: a.primary ? [BoxShadow(color: c.withValues(alpha: 0.35), blurRadius: 12, offset: const Offset(0, 4))] : null),
                child: Icon(a.icon, color: a.primary ? Colors.white : c, size: 22),
              ),
              const SizedBox(height: 7),
              Text(a.label, textAlign: TextAlign.center, maxLines: 2, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 11.5, fontWeight: FontWeight.w600, color: a.danger ? c : scheme.onSurface)),
            ]),
          ),
        ),
      );
    }

    return Material(
      color: Theme.of(context).cardTheme.color ?? scheme.surface,
      elevation: 6, shadowColor: Colors.black.withValues(alpha: 0.18),
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(22)),
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 4),
        child: LayoutBuilder(builder: (context, c) {
          // up to 4 share the width; more scroll sideways at a comfortable width each
          final per = actions.length <= 4 ? c.maxWidth / actions.length : c.maxWidth / 4.4;
          final row = Row(children: [for (final a in actions) SizedBox(width: per, child: one(a))]);
          return actions.length <= 4 ? row : SingleChildScrollView(scrollDirection: Axis.horizontal, child: row);
        }),
      ),
    );
  }
}

/// A titled card of rows.
class InfoCard extends StatelessWidget {
  const InfoCard({super.key, this.title, this.icon, required this.children, this.trailing, this.padding});
  final String? title;
  final IconData? icon;
  final List<Widget> children;
  final Widget? trailing;
  final EdgeInsets? padding;
  @override
  Widget build(BuildContext context) {
    final hint = Theme.of(context).hintColor;
    return Padding(
      padding: const EdgeInsets.only(bottom: 14),
      child: Card(
        clipBehavior: Clip.antiAlias,
        child: Padding(
          padding: padding ?? const EdgeInsets.fromLTRB(16, 14, 16, 8),
          child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
            if (title != null)
              Padding(
                padding: const EdgeInsets.only(bottom: 6),
                child: Row(children: [
                  if (icon != null) ...[Icon(icon, size: 16, color: kBrand), const SizedBox(width: 8)],
                  Expanded(child: Text(title!.toUpperCase(), style: TextStyle(fontSize: 11.5, fontWeight: FontWeight.w800, letterSpacing: 0.9, color: hint))),
                  if (trailing != null) trailing!,
                ]),
              ),
            ...children,
          ]),
        ),
      ),
    );
  }
}

/// One fact: a soft icon tile, the label above the value. Long-press copies; emails, phone numbers and web addresses open.
class InfoRow extends StatelessWidget {
  const InfoRow(this.label, this.value, {super.key, this.icon, this.valueStyle, this.trailing, this.last = false});
  final String label;
  final String value;
  final IconData? icon;
  final TextStyle? valueStyle;
  final Widget? trailing;
  final bool last;

  Uri? get _link {
    final v = value.trim();
    if (RegExp(r'^[^@\s]+@[^@\s]+\.[^@\s]+$').hasMatch(v)) return Uri.parse('mailto:$v');
    if (RegExp(r'^\+?[\d\s()-]{7,}$').hasMatch(v) && label.toLowerCase().contains('phone')) return Uri.parse('tel:${v.replaceAll(RegExp(r'[^\d+]'), '')}');
    if (v.startsWith('http://') || v.startsWith('https://')) return Uri.tryParse(v);
    return null;
  }

  @override
  Widget build(BuildContext context) {
    final hint = Theme.of(context).hintColor;
    final link = _link;
    return InkWell(
      borderRadius: BorderRadius.circular(10),
      onTap: link == null ? null : () => launchUrl(link, mode: LaunchMode.externalApplication),
      onLongPress: () { Clipboard.setData(ClipboardData(text: value)); toast(context, '$label copied.'); },
      child: Container(
        padding: const EdgeInsets.symmetric(vertical: 10),
        decoration: BoxDecoration(border: last ? null : Border(bottom: BorderSide(color: Theme.of(context).dividerColor.withValues(alpha: 0.6)))),
        child: Row(children: [
          Container(padding: const EdgeInsets.all(8), decoration: BoxDecoration(color: kBrand.withValues(alpha: 0.08), borderRadius: BorderRadius.circular(10)), child: Icon(icon ?? iconForLabel(label), size: 18, color: kBrand)),
          const SizedBox(width: 12),
          Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(label, style: TextStyle(fontSize: 12, color: hint)),
            const SizedBox(height: 1),
            Text(value, style: valueStyle ?? TextStyle(fontSize: 14.5, fontWeight: FontWeight.w600, color: link != null ? kBrand : null)),
          ])),
          if (trailing != null) trailing! else if (link != null) Icon(Icons.open_in_new_rounded, size: 16, color: hint),
        ]),
      ),
    );
  }
}

/// Rows from (label, value) pairs, leaving out the empty ones.
List<Widget> infoRows(List<(String, String?)> facts) {
  final shown = facts.where((f) => (f.$2 ?? '').trim().isNotEmpty).toList();
  return [for (var i = 0; i < shown.length; i++) InfoRow(shown[i].$1, shown[i].$2!, last: i == shown.length - 1)];
}

/// A sensible icon for a fact from the words in its label.
IconData iconForLabel(String label) {
  final l = label.toLowerCase();
  bool has(List<String> w) => w.any(l.contains);
  if (has(['email', 'mail'])) return Icons.alternate_email_rounded;
  if (has(['phone', 'mobile', 'whatsapp'])) return Icons.phone_rounded;
  if (has(['address', 'location', 'branch'])) return Icons.place_outlined;
  if (has(['tin', 'vat', 'tax'])) return Icons.badge_outlined;
  if (has(['date', 'when', 'since', 'until', 'expires', 'created', 'updated', 'seen', 'applied', 'bought', 'due', 'day'])) return Icons.event_outlined;
  if (has(['total', 'amount', 'price', 'cost', 'paid', 'owing', 'owed', 'earned', 'balance', 'value', 'monthly', 'yearly', 'outstanding'])) return Icons.payments_outlined;
  if (has(['status', 'state', 'mode'])) return Icons.flag_outlined;
  if (has(['user', 'who', 'contact', 'name', 'person', 'staff'])) return Icons.person_outline_rounded;
  if (has(['company', 'client', 'supplier', 'customer', 'taxpayer'])) return Icons.apartment_rounded;
  if (has(['code', 'number', 'no.', 'reference', 'id', 'serial'])) return Icons.tag_rounded;
  if (has(['device', 'machine', 'till'])) return Icons.memory_rounded;
  if (has(['plan', 'level', 'type', 'kind', 'method', 'category'])) return Icons.category_outlined;
  if (has(['percent', '%', 'rate', 'commission'])) return Icons.percent_rounded;
  if (has(['note', 'message', 'about', 'detail', 'memo', 'description', 'reason', 'resolution'])) return Icons.notes_rounded;
  if (has(['website', 'url', 'link'])) return Icons.language_rounded;
  if (has(['account', 'bank'])) return Icons.account_balance_outlined;
  return Icons.label_outline_rounded;
}

/// Two initials from a name, for avatars.
String initialsOf(String name) {
  final parts = name.trim().split(RegExp(r'\s+')).where((p) => p.isNotEmpty && RegExp(r'[A-Za-z0-9]').hasMatch(p[0])).toList();
  if (parts.isEmpty) return '?';
  return (parts.length == 1 ? parts[0].substring(0, parts[0].length.clamp(1, 2)) : parts[0][0] + parts[1][0]).toUpperCase();
}

/// A stable soft colour for an avatar, picked from the name.
Color avatarColor(String name) {
  const colors = [Color(0xFF2563EB), Color(0xFF7C3AED), Color(0xFF0D9488), Color(0xFFDB2777), Color(0xFFEA580C), Color(0xFF059669), Color(0xFF4F46E5), Color(0xFF0891B2)];
  var h = 0;
  for (final c in name.codeUnits) { h = (h * 31 + c) & 0x7fffffff; }
  return colors[h % colors.length];
}

/// A rounded avatar with initials (or an icon), tinted by the name.
class Avatar extends StatelessWidget {
  const Avatar(this.name, {super.key, this.icon, this.color});
  final String name;
  final IconData? icon;
  final Color? color;
  @override
  Widget build(BuildContext context) {
    final c = color ?? avatarColor(name);
    return Container(
      padding: const EdgeInsets.all(10),
      decoration: BoxDecoration(gradient: LinearGradient(colors: [c.withValues(alpha: 0.16), c.withValues(alpha: 0.08)]), borderRadius: BorderRadius.circular(14)),
      child: SizedBox.square(
        dimension: 22,
        child: Center(child: icon != null ? Icon(icon, color: c, size: 21) : FittedBox(child: Text(initialsOf(name), style: TextStyle(color: c, fontWeight: FontWeight.w800, fontSize: 15)))),
      ),
    );
  }
}

/// The status dot + word used on list rows.
class StatusDot extends StatelessWidget {
  const StatusDot(this.status, {super.key});
  final String? status;
  @override
  Widget build(BuildContext context) {
    final c = toneColor(toneFor(status));
    return Row(mainAxisSize: MainAxisSize.min, children: [
      Container(width: 7, height: 7, decoration: BoxDecoration(color: c, shape: BoxShape.circle)),
      const SizedBox(width: 5),
      Flexible(child: Text(statusText(status), style: TextStyle(color: c, fontSize: 12, fontWeight: FontWeight.w700), overflow: TextOverflow.ellipsis)),
    ]);
  }
}
