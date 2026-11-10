import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import '../core/api.dart';

// ---- brand ---------------------------------------------------------------------------------------------------------

const Color kBrand = Color(0xFF2563EB);

ThemeData buildTheme(Brightness b) {
  final scheme = ColorScheme.fromSeed(seedColor: kBrand, brightness: b);
  final dark = b == Brightness.dark;
  final surface = dark ? const Color(0xFF111A2B) : Colors.white;
  final line = dark ? const Color(0xFF1E293B) : const Color(0xFFE2E8F0);
  final round12 = RoundedRectangleBorder(borderRadius: BorderRadius.circular(12));
  final base = ThemeData(useMaterial3: true, brightness: b);
  return ThemeData(
    useMaterial3: true,
    colorScheme: scheme,
    visualDensity: VisualDensity.standard,
    textTheme: base.textTheme.apply(bodyColor: scheme.onSurface, displayColor: scheme.onSurface).copyWith(
      titleLarge: base.textTheme.titleLarge?.copyWith(fontWeight: FontWeight.w800, letterSpacing: -0.2),
      titleMedium: base.textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w700),
    ),
    scaffoldBackgroundColor: dark ? const Color(0xFF0B1220) : const Color(0xFFF4F6FB),
    appBarTheme: AppBarTheme(centerTitle: false, elevation: 0, scrolledUnderElevation: 0.5, backgroundColor: surface, foregroundColor: scheme.onSurface, surfaceTintColor: Colors.transparent, titleTextStyle: TextStyle(fontSize: 18, fontWeight: FontWeight.w800, color: scheme.onSurface, letterSpacing: -0.2)),
    cardTheme: CardThemeData(elevation: 0, margin: EdgeInsets.zero, color: surface, surfaceTintColor: Colors.transparent, shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(18), side: BorderSide(color: line))),
    inputDecorationTheme: InputDecorationTheme(
      filled: true, fillColor: dark ? const Color(0xFF0F172A) : Colors.white, contentPadding: const EdgeInsets.symmetric(horizontal: 14, vertical: 14),
      border: OutlineInputBorder(borderRadius: BorderRadius.circular(12), borderSide: BorderSide(color: line)),
      enabledBorder: OutlineInputBorder(borderRadius: BorderRadius.circular(12), borderSide: BorderSide(color: line)),
      focusedBorder: OutlineInputBorder(borderRadius: BorderRadius.circular(12), borderSide: const BorderSide(color: kBrand, width: 1.6)),
    ),
    filledButtonTheme: FilledButtonThemeData(style: FilledButton.styleFrom(minimumSize: const Size(0, 48), shape: round12, textStyle: const TextStyle(fontWeight: FontWeight.w700))),
    outlinedButtonTheme: OutlinedButtonThemeData(style: OutlinedButton.styleFrom(minimumSize: const Size(0, 48), shape: round12, side: BorderSide(color: line), textStyle: const TextStyle(fontWeight: FontWeight.w600))),
    textButtonTheme: TextButtonThemeData(style: TextButton.styleFrom(shape: round12, textStyle: const TextStyle(fontWeight: FontWeight.w600))),
    floatingActionButtonTheme: FloatingActionButtonThemeData(backgroundColor: kBrand, foregroundColor: Colors.white, shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16))),
    chipTheme: ChipThemeData(shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(10)), side: BorderSide(color: line)),
    dialogTheme: DialogThemeData(backgroundColor: surface, surfaceTintColor: Colors.transparent, shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(20))),
    bottomSheetTheme: BottomSheetThemeData(backgroundColor: surface, surfaceTintColor: Colors.transparent, showDragHandle: true, shape: const RoundedRectangleBorder(borderRadius: BorderRadius.vertical(top: Radius.circular(24)))),
    popupMenuTheme: PopupMenuThemeData(color: surface, surfaceTintColor: Colors.transparent, shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(14))),
    drawerTheme: DrawerThemeData(backgroundColor: surface, surfaceTintColor: Colors.transparent),
    listTileTheme: ListTileThemeData(shape: round12, selectedColor: kBrand, selectedTileColor: kBrand.withValues(alpha: 0.08)),
    dividerTheme: DividerThemeData(color: line, space: 1),
    snackBarTheme: SnackBarThemeData(shape: round12),
    navigationBarTheme: NavigationBarThemeData(backgroundColor: surface, surfaceTintColor: Colors.transparent, indicatorColor: kBrand.withValues(alpha: 0.15), labelTextStyle: WidgetStateProperty.all(const TextStyle(fontSize: 12, fontWeight: FontWeight.w600))),
  );
}

// ---- formatting ----------------------------------------------------------------------------------------------------

final _money = NumberFormat('#,##0.00');
String money(num? v) => v == null ? '' : _money.format(v);
String moneyIn(num? v, String? currency) => v == null ? '' : '${_money.format(v)}${currency == null ? '' : ' $currency'}';

DateTime? parseDate(dynamic v) => v == null ? null : DateTime.tryParse(v.toString())?.toLocal();
String fmtDate(dynamic v) { final d = parseDate(v); return d == null ? '' : DateFormat('d MMM y').format(d); }
String fmtDateTime(dynamic v) { final d = parseDate(v); return d == null ? '' : DateFormat('d MMM y, HH:mm').format(d); }
String isoDay(DateTime d) => DateFormat('yyyy-MM-dd').format(d);
String size(num bytes) => bytes >= 1048576 ? '${(bytes / 1048576).toStringAsFixed(1)} MB' : '${(bytes / 1024).ceil().clamp(1, 1 << 30)} KB';

/// Reads a nested value from a JSON map: path "a.b.c".
dynamic dig(Map m, String path) {
  dynamic cur = m;
  for (final p in path.split('.')) {
    if (cur is Map) { cur = cur[p]; } else { return null; }
  }
  return cur;
}

String humanize(String key) {
  final spaced = key.replaceAllMapped(RegExp(r'([a-z0-9])([A-Z])'), (m) => '${m[1]} ${m[2]}').replaceAll('_', ' ');
  return spaced.isEmpty ? key : spaced[0].toUpperCase() + spaced.substring(1);
}

// ---- feedback ------------------------------------------------------------------------------------------------------

void toast(BuildContext context, Object message, {bool error = false}) {
  final text = message is ApiException ? message.message : message.toString();
  ScaffoldMessenger.of(context)
    ..hideCurrentSnackBar()
    ..showSnackBar(SnackBar(content: Text(text), behavior: SnackBarBehavior.floating, backgroundColor: error ? Colors.red.shade700 : null, duration: Duration(seconds: error ? 5 : 3)));
}

Future<bool> confirm(BuildContext context, String message, {String ok = 'Confirm', bool danger = false}) async {
  final r = await showDialog<bool>(
    context: context,
    builder: (c) => AlertDialog(
      content: Text(message),
      actions: [
        TextButton(onPressed: () => Navigator.pop(c, false), child: const Text('Cancel')),
        FilledButton(style: danger ? FilledButton.styleFrom(backgroundColor: Colors.red.shade700) : null, onPressed: () => Navigator.pop(c, true), child: Text(ok)),
      ],
    ),
  );
  return r == true;
}

Future<String?> promptText(BuildContext context, String title, {String hint = '', String ok = 'OK', bool obscure = false, String initial = '', TextInputType? keyboard}) {
  final c = TextEditingController(text: initial);
  return showDialog<String>(
    context: context,
    builder: (d) => AlertDialog(
      title: Text(title),
      content: TextField(controller: c, autofocus: true, obscureText: obscure, keyboardType: keyboard, decoration: InputDecoration(hintText: hint)),
      actions: [TextButton(onPressed: () => Navigator.pop(d), child: const Text('Cancel')), FilledButton(onPressed: () => Navigator.pop(d, c.text), child: Text(ok))],
    ),
  );
}

/// Runs [work], showing a spinner on the button's behalf and any failure as a message. Returns true when it worked.
Future<bool> guarded(BuildContext context, Future<void> Function() work, {String? done}) async {
  try {
    ApprovalNotice.last = null;
    await work();
    final message = ApprovalNotice.take() ?? done;   // held for approval: say so instead of "done"
    if (message != null && context.mounted) toast(context, message);
    return true;
  } catch (e) {
    if (context.mounted) toast(context, e, error: true);
    return false;
  }
}

// ---- building blocks -----------------------------------------------------------------------------------------------

/// A value at the end of a row (an amount, a count). It never takes more than the room left: on a narrow screen or with large text it shrinks instead of overflowing.
class Amount extends StatelessWidget {
  const Amount(this.text, {super.key, this.style});
  final String text;
  final TextStyle? style;
  @override
  Widget build(BuildContext context) => Flexible(child: FittedBox(fit: BoxFit.scaleDown, alignment: Alignment.centerRight, child: Text(text, style: style, maxLines: 1)));
}

/// Like [Amount] but for use inside a column (no flex), shrinking to the width the column allows.
class AmountBox extends StatelessWidget {
  const AmountBox(this.text, {super.key, this.style});
  final String text;
  final TextStyle? style;
  @override
  Widget build(BuildContext context) => FittedBox(fit: BoxFit.scaleDown, alignment: Alignment.centerRight, child: Text(text, style: style, maxLines: 1));
}

/// The right-hand column of a list row (an amount above a status): at most [fraction] of the screen wide, so the title beside it always keeps room.
class Side extends StatelessWidget {
  const Side({super.key, required this.children, this.fraction = 0.4});
  final List<Widget> children;
  final double fraction;
  @override
  Widget build(BuildContext context) => ConstrainedBox(
        constraints: BoxConstraints(maxWidth: MediaQuery.sizeOf(context).width * fraction),
        child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.end, children: children),
      );
}

class AppCard extends StatelessWidget {
  const AppCard({super.key, required this.child, this.padding = const EdgeInsets.all(16), this.onTap, this.margin});
  final Widget child;
  final EdgeInsets padding;
  final VoidCallback? onTap;
  final EdgeInsets? margin;
  @override
  Widget build(BuildContext context) => Padding(
        padding: margin ?? EdgeInsets.zero,
        child: Card(child: InkWell(borderRadius: BorderRadius.circular(16), onTap: onTap, child: Padding(padding: padding, child: child))),
      );
}

enum Tone { ok, warn, bad, info, neutral }

Color toneColor(Tone t) => switch (t) { Tone.ok => const Color(0xFF059669), Tone.warn => const Color(0xFFD97706), Tone.bad => const Color(0xFFDC2626), Tone.info => const Color(0xFF0284C7), Tone.neutral => const Color(0xFF64748B) };

class Badge2 extends StatelessWidget {
  const Badge2(this.text, {super.key, this.tone = Tone.neutral});
  final String text;
  final Tone tone;
  @override
  Widget build(BuildContext context) {
    final c = toneColor(tone);
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      decoration: BoxDecoration(color: c.withValues(alpha: 0.12), borderRadius: BorderRadius.circular(20)),
      child: Text(text, style: TextStyle(color: c, fontSize: 12, fontWeight: FontWeight.w600)),
    );
  }
}

Tone toneFor(String? status) {
  switch ((status ?? '').toLowerCase()) {
    case 'fiscaldayopened': return Tone.ok;
    case 'fiscaldayclosed': return Tone.neutral;
    case 'fiscaldaycloseinitiated': return Tone.warn;
    case 'fiscaldayclosefailed': return Tone.bad;
    case 'ok': case 'active': case 'paid': case 'submitted': case 'open': case 'approved': case 'accepted': case 'sent': case 'success': case 'done': case 'resolved':
      return Tone.ok;
    case 'pending': case 'held': case 'trial': case 'warn': case 'warning': case 'offlineissued': case 'awaitingconfirmation': case 'draft': case 'closing': case 'unpaid': case 'part paid': case 'medium':
      return Tone.warn;
    case 'rejected': case 'failed': case 'bad': case 'critical': case 'suspended': case 'cancelled': case 'void': case 'overdue': case 'locked': case 'high': case 'error': case 'pastdue':
      return Tone.bad;
    case 'info': case 'invoiced': case 'closed':
      return Tone.info;
    default:
      return Tone.neutral;
  }
}

/// Friendly words for the gateway's status names (FiscalDayOpened becomes Opened).
String statusText(String? s) {
  if (s == null) return '';
  const map = {'FiscalDayOpened': 'Open', 'FiscalDayClosed': 'Closed', 'FiscalDayCloseInitiated': 'Closing', 'FiscalDayCloseFailed': 'Close failed', 'OfflineIssued': 'Issued offline', 'PastDue': 'Past due', 'AwaitingConfirmation': 'Awaiting confirmation'};
  return map[s] ?? s;
}

bool dayIsOpen(String? s) => s == 'FiscalDayOpened' || s == 'Open';

class StatusBadge extends StatelessWidget {
  const StatusBadge(this.status, {super.key});
  final String? status;
  @override
  Widget build(BuildContext context) => Badge2(statusText(status), tone: toneFor(status));
}

class Kpi extends StatelessWidget {
  const Kpi(this.label, this.value, {super.key, this.sub, this.color, this.icon});
  final String label, value;
  final String? sub;
  final Color? color;
  final IconData? icon;
  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context);
    final c = color ?? kBrand;
    return AppCard(
      padding: const EdgeInsets.all(14),
      child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
        Row(children: [
          if (icon != null) ...[
            Container(padding: const EdgeInsets.all(7), decoration: BoxDecoration(color: c.withValues(alpha: 0.12), borderRadius: BorderRadius.circular(10)), child: Icon(icon, size: 17, color: c)),
            const SizedBox(width: 8),
          ],
          Expanded(child: Text(label, style: TextStyle(fontSize: 12.5, color: t.hintColor, fontWeight: FontWeight.w600), overflow: TextOverflow.ellipsis, maxLines: 2)),
        ]),
        const SizedBox(height: 10),
        FittedBox(fit: BoxFit.scaleDown, alignment: Alignment.centerLeft, child: Text(value, style: TextStyle(fontSize: 24, fontWeight: FontWeight.w800, letterSpacing: -0.5, color: color))),
        if (sub != null) Padding(padding: const EdgeInsets.only(top: 2), child: Text(sub!, style: TextStyle(fontSize: 11.5, color: t.hintColor), maxLines: 1, overflow: TextOverflow.ellipsis)),
      ]),
    );
  }
}

class SectionTitle extends StatelessWidget {
  const SectionTitle(this.text, {super.key, this.trailing});
  final String text;
  final Widget? trailing;
  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.fromLTRB(4, 20, 4, 8),
        child: Row(children: [Expanded(child: Text(text.toUpperCase(), style: TextStyle(fontSize: 11.5, fontWeight: FontWeight.w800, letterSpacing: 0.9, color: Theme.of(context).hintColor))), if (trailing != null) trailing!]),
      );
}

class EmptyState extends StatelessWidget {
  const EmptyState(this.message, {super.key, this.icon = Icons.inbox_outlined, this.action});
  final String message;
  final IconData icon;
  final Widget? action;
  @override
  Widget build(BuildContext context) => Center(
        child: Padding(
          padding: const EdgeInsets.all(32),
          child: Column(mainAxisSize: MainAxisSize.min, children: [
            Container(
              padding: const EdgeInsets.all(20),
              decoration: BoxDecoration(shape: BoxShape.circle, gradient: LinearGradient(colors: [kBrand.withValues(alpha: 0.13), const Color(0xFF7C3AED).withValues(alpha: 0.09)])),
              child: Icon(icon, size: 34, color: kBrand),
            ),
            const SizedBox(height: 14),
            Text(message, textAlign: TextAlign.center, style: TextStyle(color: Theme.of(context).hintColor, height: 1.4, fontWeight: FontWeight.w500)),
            if (action != null) ...[const SizedBox(height: 16), action!],
          ]),
        ),
      );
}

/// An empty message that fills the room there is and still scrolls, so pull-to-refresh keeps working.
class EmptyList extends StatelessWidget {
  const EmptyList(this.message, {super.key, this.icon = Icons.inbox_outlined});
  final String message;
  final IconData icon;
  @override
  Widget build(BuildContext context) => LayoutBuilder(builder: (c, box) => ListView(children: [SizedBox(height: box.maxHeight.isFinite ? box.maxHeight : MediaQuery.of(c).size.height * 0.6, child: EmptyState(message, icon: icon))]));
}

/// Lays tiles out in as many columns as fit (each at least [minTileWidth] wide), so it suits a small phone and a tablet alike.
class ResponsiveGrid extends StatelessWidget {
  const ResponsiveGrid({super.key, required this.children, this.minTileWidth = 150, this.spacing = 12});
  final List<Widget> children;
  final double minTileWidth, spacing;
  @override
  Widget build(BuildContext context) => LayoutBuilder(builder: (c, box) {
        final cols = ((box.maxWidth + spacing) / (minTileWidth + spacing)).floor().clamp(1, 6);
        final w = (box.maxWidth - spacing * (cols - 1)) / cols;
        return Wrap(spacing: spacing, runSpacing: spacing, children: [for (final ch in children) SizedBox(width: w, child: ch)]);
      });
}

class ErrorState extends StatelessWidget {
  const ErrorState(this.error, {super.key, this.onRetry});
  final Object error;
  final VoidCallback? onRetry;
  @override
  Widget build(BuildContext context) => EmptyState(error is ApiException ? (error as ApiException).message : error.toString(), icon: Icons.error_outline,
      action: onRetry == null ? null : FilledButton.tonal(onPressed: onRetry, child: const Text('Try again')));
}

/// Loads something once (and again on pull-to-refresh), showing a spinner, the error with a retry, or the content.
class Loader<T> extends StatefulWidget {
  const Loader({super.key, required this.load, required this.builder, this.scrollable = true, this.frame});
  final Future<T> Function() load;
  final Widget Function(BuildContext context, T data, Future<void> Function() reload) builder;
  final bool scrollable;
  /// Wraps the spinner and the error (for a pushed screen whose content brings its own app bar: gives them one, with a back button).
  final Widget Function(Widget child)? frame;
  @override
  State<Loader<T>> createState() => LoaderState<T>();
}

class LoaderState<T> extends State<Loader<T>> {
  T? _data;
  Object? _error;
  bool _loading = true;

  @override
  void initState() { super.initState(); reload(); }

  Future<void> reload() async {
    if (mounted) setState(() { _loading = _data == null; _error = null; });
    try {
      final d = await widget.load();
      if (mounted) setState(() { _data = d; _loading = false; });
    } catch (e) {
      if (mounted) setState(() { _error = e; _loading = false; });
    }
  }

  @override
  Widget build(BuildContext context) {
    final frame = widget.frame ?? (Widget w) => w;
    if (_loading) return frame(const Center(child: CircularProgressIndicator()));
    if (_error != null && _data == null) return frame(ErrorState(_error!, onRetry: reload));
    return RefreshIndicator(onRefresh: reload, child: widget.builder(context, _data as T, reload));
  }
}

/// Labelled value rows for a detail screen.
class Facts extends StatelessWidget {
  const Facts(this.rows, {super.key});
  final List<(String, String?)> rows;
  @override
  Widget build(BuildContext context) {
    final shown = rows.where((r) => r.$2 != null && r.$2!.isNotEmpty).toList();
    return Column(children: [
      for (final r in shown)
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 5),
          child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
            SizedBox(width: 120, child: Text(r.$1, style: TextStyle(color: Theme.of(context).hintColor, fontSize: 13))),
            Expanded(child: Text(r.$2!, style: const TextStyle(fontSize: 14, fontWeight: FontWeight.w500))),
          ]),
        ),
    ]);
  }
}

/// Maps the web console's icon names to Material icons.
IconData iconFor(String? name) => switch (name) {
      'home' => Icons.home_outlined, 'cpu' => Icons.memory, 'plus' => Icons.add, 'receipt' => Icons.receipt_long_outlined, 'upload' => Icons.upload_outlined,
      'download' => Icons.download_outlined, 'calendar' => Icons.calendar_month_outlined, 'cloud-upload' => Icons.cloud_upload_outlined, 'key' => Icons.vpn_key_outlined,
      'bolt' => Icons.bolt, 'building' => Icons.apartment, 'users' => Icons.people_outline, 'shield' => Icons.shield_outlined, 'squares' => Icons.grid_view,
      'clipboard' => Icons.assignment_outlined, 'chart' => Icons.bar_chart, 'file-text' => Icons.description_outlined, 'check' => Icons.check_circle_outline,
      'trash' => Icons.delete_outline, 'refresh' => Icons.refresh, 'copy' => Icons.copy, 'search' => Icons.search, 'alert' => Icons.warning_amber_outlined,
      'layers' => Icons.layers_outlined, 'lock' => Icons.lock_outline, 'stack' => Icons.inventory_2_outlined, 'card' => Icons.credit_card, 'bell' => Icons.notifications_none,
      'mail' => Icons.mail_outline, 'send' => Icons.send_outlined, 'message' => Icons.chat_bubble_outline, 'pulse' => Icons.monitor_heart_outlined, 'repeat' => Icons.repeat,
      'cart' => Icons.shopping_cart_outlined, 'user-check' => Icons.how_to_reg_outlined, 'toggle' => Icons.toggle_on_outlined, 'folder' => Icons.folder_outlined,
      'template' => Icons.web_outlined, 'edit' => Icons.edit_outlined, 'eye' => Icons.visibility_outlined, 'link' => Icons.link, _ => Icons.circle_outlined,
    };
