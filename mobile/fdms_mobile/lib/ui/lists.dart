import 'package:flutter/material.dart';

import 'kit.dart';
import 'premium.dart';

/// Building blocks for list pages: a search box, filter pills, a section header, rows grouped in one rounded panel, and an empty state.
/// They size from their content and the user's text size.

/// "ACTIVE" -> "Active" (status names from the gateway are sometimes upper case); other words stay as they are.
String niceLabel(String s) => s.length > 3 && s == s.toUpperCase() && RegExp(r'^[A-Z_]+$').hasMatch(s) ? s[0] + s.substring(1).toLowerCase().replaceAll('_', ' ') : s;

/// The rounded search field at the top of a list.
class SearchBox extends StatelessWidget {
  const SearchBox({super.key, required this.controller, this.hint = 'Search', this.onChanged, this.onSubmitted});
  final TextEditingController controller;
  final String hint;
  final ValueChanged<String>? onChanged, onSubmitted;
  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context);
    final none = OutlineInputBorder(borderRadius: BorderRadius.circular(16), borderSide: BorderSide.none);
    return DecoratedBox(
      decoration: BoxDecoration(borderRadius: BorderRadius.circular(16), boxShadow: [BoxShadow(color: Colors.black.withValues(alpha: t.brightness == Brightness.dark ? 0.25 : 0.05), blurRadius: 14, offset: const Offset(0, 4))]),
      child: TextField(
        controller: controller, onChanged: onChanged, onSubmitted: onSubmitted, textInputAction: TextInputAction.search,
        decoration: InputDecoration(
          hintText: hint, prefixIcon: const Icon(Icons.search_rounded), isDense: true,
          contentPadding: const EdgeInsets.symmetric(horizontal: 14, vertical: 14),
          suffixIcon: controller.text.isEmpty ? null : IconButton(tooltip: 'Clear', icon: const Icon(Icons.close_rounded), onPressed: () { controller.clear(); onChanged?.call(''); onSubmitted?.call(''); }),
          border: none, enabledBorder: none,
          focusedBorder: OutlineInputBorder(borderRadius: BorderRadius.circular(16), borderSide: const BorderSide(color: kBrand, width: 1.5)),
          fillColor: t.cardTheme.color,
        ),
      ),
    );
  }
}

/// One choice in a row of filter pills.
class PillOption {
  const PillOption(this.label, this.selected, this.onTap, {this.color, this.count});
  final String label;
  final bool selected;
  final VoidCallback onTap;
  final Color? color;
  final int? count;
}

/// Filter pills that scroll sideways; the chosen one is filled.
class FilterPills extends StatelessWidget {
  const FilterPills(this.options, {super.key});
  final List<PillOption> options;
  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context);
    return SingleChildScrollView(
      scrollDirection: Axis.horizontal,
      child: Row(children: [
        for (final o in options)
          Padding(
            padding: const EdgeInsets.only(right: 8),
            child: Material(
              color: o.selected ? (o.color ?? kBrand) : t.cardTheme.color,
              shape: StadiumBorder(side: BorderSide(color: o.selected ? Colors.transparent : t.dividerColor)),
              child: InkWell(
                customBorder: const StadiumBorder(),
                onTap: o.onTap,
                child: Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 8),
                  child: Row(mainAxisSize: MainAxisSize.min, children: [
                    if (o.color != null && !o.selected) ...[Container(width: 7, height: 7, decoration: BoxDecoration(color: o.color, shape: BoxShape.circle)), const SizedBox(width: 6)],
                    Text(o.label, style: TextStyle(fontSize: 13, fontWeight: FontWeight.w700, color: o.selected ? Colors.white : t.colorScheme.onSurface)),
                    if (o.count != null) ...[const SizedBox(width: 6), Text('${o.count}', style: TextStyle(fontSize: 12, fontWeight: FontWeight.w800, color: o.selected ? Colors.white70 : t.hintColor))],
                  ]),
                ),
              ),
            ),
          ),
      ]),
    );
  }
}

/// "12 CUSTOMERS" above a list, with an optional figure on the right.
class ListHeader extends StatelessWidget {
  const ListHeader(this.text, {super.key, this.trailing});
  final String text;
  final String? trailing;
  @override
  Widget build(BuildContext context) {
    final hint = Theme.of(context).hintColor;
    return Padding(
      padding: const EdgeInsets.fromLTRB(4, 16, 4, 8),
      child: Row(children: [
        Expanded(child: Text(text.toUpperCase(), style: TextStyle(fontSize: 11.5, fontWeight: FontWeight.w800, letterSpacing: 0.9, color: hint))),
        if (trailing != null) Flexible(child: Align(alignment: Alignment.centerRight, child: Text(trailing!, textAlign: TextAlign.right, style: TextStyle(fontSize: 12.5, fontWeight: FontWeight.w800, color: hint)))),
      ]),
    );
  }
}

/// A group of rows in one rounded panel, separated by hairlines that start after the avatar.
class ListPanel extends StatelessWidget {
  const ListPanel({super.key, required this.children});
  final List<Widget> children;
  @override
  Widget build(BuildContext context) {
    final line = Theme.of(context).dividerColor.withValues(alpha: 0.45);
    return Card(
      clipBehavior: Clip.antiAlias,
      child: Column(children: [
        for (var i = 0; i < children.length; i++) ...[
          children[i],
          if (i < children.length - 1) Padding(padding: const EdgeInsets.only(left: 70), child: Divider(height: 1, color: line)),
        ],
      ]),
    );
  }
}

/// A row in a list panel: avatar, title, subtitle, small pills, and a figure (and status) on the right.
class ListRow extends StatelessWidget {
  const ListRow({super.key, required this.leading, required this.title, this.subtitle, this.pills = const [], this.figure, this.status, this.onTap, this.chevron = true});
  final Widget leading;
  final String title;
  final String? subtitle, figure, status;
  final List<(String, Tone)> pills;
  final VoidCallback? onTap;
  final bool chevron;
  @override
  Widget build(BuildContext context) {
    final hint = Theme.of(context).hintColor;
    return InkWell(
      onTap: onTap,
      child: Padding(
        padding: const EdgeInsets.fromLTRB(14, 12, 10, 12),
        child: Row(children: [
          leading,
          const SizedBox(width: 12),
          Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(title, maxLines: 2, overflow: TextOverflow.ellipsis, style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 15)),
            if ((subtitle ?? '').isNotEmpty) Padding(padding: const EdgeInsets.only(top: 2), child: Text(subtitle!, maxLines: 2, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 12.5, color: hint, height: 1.3))),
            if (pills.isNotEmpty) Padding(padding: const EdgeInsets.only(top: 6), child: Wrap(spacing: 6, runSpacing: 4, children: [for (final p in pills) Pill(niceLabel(p.$1), tone: p.$2)])),
          ])),
          if ((figure ?? '').isNotEmpty || status != null)
            Padding(padding: const EdgeInsets.only(left: 8), child: Side(fraction: 0.34, children: [
              if ((figure ?? '').isNotEmpty) AmountBox(figure!, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 14.5)),
              if (status != null) Padding(padding: const EdgeInsets.only(top: 4), child: StatusDot(status)),
            ])),
          if (chevron) Icon(Icons.chevron_right_rounded, size: 20, color: hint.withValues(alpha: 0.7)),
        ]),
      ),
    );
  }
}

/// A small rounded label (a role, a count, a state).
class Pill extends StatelessWidget {
  const Pill(this.text, {super.key, this.tone = Tone.neutral});
  final String text;
  final Tone tone;
  @override
  Widget build(BuildContext context) {
    final c = toneColor(tone);
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      decoration: BoxDecoration(color: c.withValues(alpha: 0.1), borderRadius: BorderRadius.circular(8)),
      child: Text(text, style: TextStyle(color: c, fontSize: 11.5, fontWeight: FontWeight.w700)),
    );
  }
}

/// The empty state of a list: a tinted icon, a short line, and the button to add the first one.
class BigEmpty extends StatelessWidget {
  const BigEmpty({super.key, required this.icon, required this.title, this.message, this.action});
  final IconData icon;
  final String title;
  final String? message;
  final Widget? action;
  @override
  Widget build(BuildContext context) {
    final hint = Theme.of(context).hintColor;
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 24, vertical: 40),
      child: Column(mainAxisSize: MainAxisSize.min, children: [
        Container(
          padding: const EdgeInsets.all(22),
          decoration: BoxDecoration(shape: BoxShape.circle, gradient: LinearGradient(colors: [kBrand.withValues(alpha: 0.14), const Color(0xFF7C3AED).withValues(alpha: 0.10)])),
          child: Icon(icon, size: 38, color: kBrand),
        ),
        const SizedBox(height: 16),
        Text(title, textAlign: TextAlign.center, style: const TextStyle(fontSize: 17, fontWeight: FontWeight.w800)),
        if (message != null) Padding(padding: const EdgeInsets.only(top: 6), child: Text(message!, textAlign: TextAlign.center, style: TextStyle(color: hint, height: 1.4))),
        if (action != null) Padding(padding: const EdgeInsets.only(top: 18), child: action!),
      ]),
    );
  }
}

/// A day heading for date-grouped lists: "Today", "Yesterday", or the date.
String dayHeading(DateTime? d) {
  if (d == null) return 'Undated';
  final now = DateTime.now();
  final today = DateTime(now.year, now.month, now.day);
  final day = DateTime(d.year, d.month, d.day);
  final diff = today.difference(day).inDays;
  if (diff == 0) return 'Today';
  if (diff == 1) return 'Yesterday';
  return fmtDate(d.toIso8601String());
}
