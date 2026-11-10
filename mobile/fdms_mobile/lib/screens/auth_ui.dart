import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../ui/kit.dart';

/// Shared look of the screens before sign-in (welcome, sign in, forgot password, register): one deep brand gradient across the whole screen,
/// soft glows, and the form drawn straight on it in frosted-glass fields (no card). On a tablet or wide window a feature column sits on the
/// left. Nothing has a fixed size.

const kAuthGradient = LinearGradient(colors: [Color(0xFF0B1B4D), Color(0xFF1E3A8A), Color(0xFF4C1D95)], begin: Alignment.topLeft, end: Alignment.bottomRight);
const kAuthAccent = Color(0xFF3730A3);
/// Secondary text and hairlines on the gradient.
const kAuthHint = Color(0xA6FFFFFF);
const kAuthLine = Color(0x2EFFFFFF);

/// The FiscalHub emblem (the "F" with the receipt). It grows with [scale] and with the text size the person chose.
class BrandMark extends StatelessWidget {
  const BrandMark({super.key, this.onDark = true, this.scale = 1});
  final bool onDark;
  final double scale;
  @override
  Widget build(BuildContext context) {
    final side = MediaQuery.textScalerOf(context).scale(44) * scale;
    return Image.asset('assets/brand/emblem.png', width: side, height: side, fit: BoxFit.contain, filterQuality: FilterQuality.medium, semanticLabel: 'FiscalHub');
  }
}

/// "FiscalHub" in the logo's colours: blue "Fiscal", green "Hub" (white "Fiscal" on dark backgrounds).
class BrandWord extends StatelessWidget {
  const BrandWord({super.key, this.onDark = true, this.size = 18});
  final bool onDark;
  final double size;
  @override
  Widget build(BuildContext context) => Text.rich(
        TextSpan(children: [
          TextSpan(text: 'Fiscal', style: TextStyle(color: onDark ? Colors.white : const Color(0xFF0D2F7A))),
          const TextSpan(text: 'Hub', style: TextStyle(color: Color(0xFF4ADE2A))),
        ]),
        overflow: TextOverflow.ellipsis,
        style: TextStyle(fontSize: size, fontWeight: FontWeight.w900, letterSpacing: -0.3),
      );
}

class BrandName extends StatelessWidget {
  const BrandName({super.key, this.onDark = true});
  final bool onDark;
  @override
  Widget build(BuildContext context) => Row(mainAxisSize: MainAxisSize.min, children: [
        BrandMark(onDark: onDark),
        const SizedBox(width: 10),
        Flexible(child: BrandWord(onDark: onDark)),
      ]);
}

/// Soft light behind the content; sized from the space available.
class _Glow extends StatelessWidget {
  const _Glow();
  @override
  Widget build(BuildContext context) => LayoutBuilder(builder: (context, c) {
        final w = c.maxWidth.isFinite ? c.maxWidth : 400.0;
        Widget blob(Color color, double size) => Container(width: size, height: size, decoration: BoxDecoration(shape: BoxShape.circle, gradient: RadialGradient(colors: [color.withValues(alpha: 0.45), color.withValues(alpha: 0)])));
        return Stack(clipBehavior: Clip.hardEdge, children: [
          Positioned(right: -w * 0.35, top: -w * 0.3, child: blob(const Color(0xFF6366F1), w * 1.0)),
          Positioned(left: -w * 0.45, top: w * 0.9, child: blob(const Color(0xFF8B5CF6), w * 0.9)),
          Positioned(right: -w * 0.3, bottom: -w * 0.4, child: blob(const Color(0xFF0EA5E9), w * 0.8)),
        ]);
      });
}

class _Feature extends StatelessWidget {
  const _Feature(this.icon, this.title, this.text);
  final IconData icon;
  final String title, text;
  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.only(bottom: 20),
        child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Container(padding: const EdgeInsets.all(10), decoration: BoxDecoration(color: Colors.white.withValues(alpha: 0.1), borderRadius: BorderRadius.circular(14), border: Border.all(color: Colors.white.withValues(alpha: 0.14))), child: Icon(icon, color: Colors.white, size: 20)),
          const SizedBox(width: 14),
          Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(title, style: const TextStyle(color: Colors.white, fontWeight: FontWeight.w700, fontSize: 15)),
            const SizedBox(height: 3),
            Text(text, style: TextStyle(color: Colors.white.withValues(alpha: 0.72), fontSize: 13, height: 1.4)),
          ])),
        ]),
      );
}

const _features = [
  _Feature(Icons.bolt_rounded, 'Fiscalise in seconds', 'Invoices, credit and debit notes signed and sent to ZIMRA FDMS straight from your phone.'),
  _Feature(Icons.cloud_done_outlined, 'Never lose a receipt', 'Offline files, retries and a full archive with QR codes and PDFs.'),
  _Feature(Icons.verified_user_outlined, 'Secure by design', 'Two-step sign-in, per-device tokens and an audit trail of every action.'),
];

/// The dark "glass" theme the sign-in screens use, so every field, button and checkbox looks right on the gradient.
ThemeData authTheme(BuildContext context) {
  final base = buildTheme(Brightness.dark);
  final glass = OutlineInputBorder(borderRadius: BorderRadius.circular(16), borderSide: BorderSide(color: Colors.white.withValues(alpha: 0.16)));
  return base.copyWith(
    scaffoldBackgroundColor: Colors.transparent,
    hintColor: Colors.white.withValues(alpha: 0.62),
    dividerColor: Colors.white.withValues(alpha: 0.18),
    colorScheme: base.colorScheme.copyWith(primary: Colors.white, onPrimary: kAuthAccent, surface: const Color(0xFF1E2A5A), onSurface: Colors.white, error: const Color(0xFFFCA5A5)),
    inputDecorationTheme: InputDecorationTheme(
      filled: true, fillColor: Colors.white.withValues(alpha: 0.08), contentPadding: const EdgeInsets.symmetric(horizontal: 16, vertical: 16),
      labelStyle: TextStyle(color: Colors.white.withValues(alpha: 0.72)), floatingLabelStyle: const TextStyle(color: Colors.white),
      hintStyle: TextStyle(color: Colors.white.withValues(alpha: 0.45)), prefixIconColor: Colors.white.withValues(alpha: 0.75), suffixIconColor: Colors.white.withValues(alpha: 0.75),
      border: glass, enabledBorder: glass,
      focusedBorder: OutlineInputBorder(borderRadius: BorderRadius.circular(16), borderSide: const BorderSide(color: Colors.white, width: 1.4)),
      errorBorder: OutlineInputBorder(borderRadius: BorderRadius.circular(16), borderSide: const BorderSide(color: Color(0xFFFCA5A5))),
      focusedErrorBorder: OutlineInputBorder(borderRadius: BorderRadius.circular(16), borderSide: const BorderSide(color: Color(0xFFFCA5A5), width: 1.4)),
      errorStyle: const TextStyle(color: Color(0xFFFCA5A5)),
    ),
    textSelectionTheme: const TextSelectionThemeData(cursorColor: Colors.white, selectionHandleColor: Colors.white),
    filledButtonTheme: FilledButtonThemeData(style: FilledButton.styleFrom(backgroundColor: Colors.white, foregroundColor: kAuthAccent, disabledBackgroundColor: Colors.white.withValues(alpha: 0.35), disabledForegroundColor: kAuthAccent.withValues(alpha: 0.6), minimumSize: const Size(0, 52), shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)), textStyle: const TextStyle(fontWeight: FontWeight.w800, fontSize: 16))),
    outlinedButtonTheme: OutlinedButtonThemeData(style: OutlinedButton.styleFrom(foregroundColor: Colors.white, side: BorderSide(color: Colors.white.withValues(alpha: 0.35)), minimumSize: const Size(0, 52), shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)), textStyle: const TextStyle(fontWeight: FontWeight.w700, fontSize: 15))),
    textButtonTheme: TextButtonThemeData(style: TextButton.styleFrom(foregroundColor: Colors.white, textStyle: const TextStyle(fontWeight: FontWeight.w700))),
    checkboxTheme: CheckboxThemeData(fillColor: WidgetStateProperty.resolveWith((s) => s.contains(WidgetState.selected) ? Colors.white : Colors.transparent), checkColor: const WidgetStatePropertyAll(kAuthAccent), side: BorderSide(color: Colors.white.withValues(alpha: 0.7), width: 1.5)),
    segmentedButtonTheme: SegmentedButtonThemeData(style: ButtonStyle(
      foregroundColor: WidgetStateProperty.resolveWith((s) => s.contains(WidgetState.selected) ? kAuthAccent : Colors.white),
      backgroundColor: WidgetStateProperty.resolveWith((s) => s.contains(WidgetState.selected) ? Colors.white : Colors.white.withValues(alpha: 0.06)),
      side: WidgetStatePropertyAll(BorderSide(color: Colors.white.withValues(alpha: 0.3))),
    )),
    progressIndicatorTheme: const ProgressIndicatorThemeData(color: Colors.white),
    textTheme: base.textTheme.apply(bodyColor: Colors.white, displayColor: Colors.white),
  );
}

class AuthScaffold extends StatelessWidget {
  const AuthScaffold({super.key, required this.title, this.subtitle, required this.children, this.headerExtra, this.onLogoLongPress, this.hero = false});
  final String title;
  final String? subtitle;
  final List<Widget> children;
  /// Shown under the subtitle (for example a step indicator).
  final Widget? headerExtra;
  final VoidCallback? onLogoLongPress;
  /// The big centred logo lockup (sign-in), instead of the small brand row.
  final bool hero;

  @override
  Widget build(BuildContext context) {
    final canPop = Navigator.of(context).canPop();
    return AnnotatedRegion<SystemUiOverlayStyle>(
      value: SystemUiOverlayStyle.light,
      child: Theme(
        data: authTheme(context),
        child: Scaffold(
          backgroundColor: const Color(0xFF0B1B4D),
          body: DecoratedBox(
            decoration: const BoxDecoration(gradient: kAuthGradient),
            child: Stack(children: [
              const Positioned.fill(child: _Glow()),
              SafeArea(child: LayoutBuilder(builder: (context, c) => c.maxWidth >= 900 ? _wide(context, canPop) : _form(context, canPop))),
            ]),
          ),
        ),
      ),
    );
  }

  Widget _back(BuildContext context) => IconButton(
        tooltip: 'Back', onPressed: () => Navigator.of(context).maybePop(),
        icon: const Icon(Icons.arrow_back_rounded, color: Colors.white),
        style: IconButton.styleFrom(backgroundColor: Colors.white.withValues(alpha: 0.1), side: BorderSide(color: Colors.white.withValues(alpha: 0.15))),
      );

  Widget _form(BuildContext context, bool canPop, {bool showBrand = true}) => Center(
        child: SingleChildScrollView(
          keyboardDismissBehavior: ScrollViewKeyboardDismissBehavior.onDrag,
          padding: const EdgeInsets.fromLTRB(24, 12, 24, 20),
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 460),
            child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
              if (canPop || (showBrand && !hero)) Row(children: [
                if (canPop) ...[_back(context), const SizedBox(width: 12)],
                if (showBrand && !hero) Flexible(child: GestureDetector(onLongPress: onLogoLongPress, child: const BrandName())),
              ]),
              if (hero && showBrand) ...[
                const SizedBox(height: 24),
                Center(child: GestureDetector(onLongPress: onLogoLongPress, child: const BrandMark(scale: 2.4))),
                const SizedBox(height: 20),
                const Center(child: BrandWord(size: 32)),
                const SizedBox(height: 6),
                Text('Fiscalisation simplified', textAlign: TextAlign.center, style: TextStyle(color: Colors.white.withValues(alpha: 0.72), fontSize: 15)),
                const SizedBox(height: 36),
                Text(title, style: const TextStyle(color: Colors.white, fontSize: 20, fontWeight: FontWeight.w800)),
              ] else ...[
                SizedBox(height: canPop || showBrand ? 28 : 8),
                Text(title, style: const TextStyle(color: Colors.white, fontSize: 30, fontWeight: FontWeight.w900, letterSpacing: -0.6, height: 1.15)),
              ],
              if (subtitle != null) Padding(padding: const EdgeInsets.only(top: 6), child: Text(subtitle!, style: TextStyle(color: Colors.white.withValues(alpha: 0.72), fontSize: 15, height: 1.4))),
              if (headerExtra != null) Padding(padding: const EdgeInsets.only(top: 20), child: headerExtra!),
              const SizedBox(height: 24),
              ...children,
            ]),
          ),
        ),
      );

  Widget _wide(BuildContext context, bool canPop) => Row(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
        Expanded(
          flex: 5,
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(48),
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              GestureDetector(onLongPress: onLogoLongPress, child: const BrandName()),
              const SizedBox(height: 64),
              const Text('Fiscalise every invoice with ZIMRA, from anywhere.', style: TextStyle(color: Colors.white, fontSize: 38, fontWeight: FontWeight.w900, height: 1.12, letterSpacing: -0.8)),
              const SizedBox(height: 14),
              Text('One gateway for your tills, your ERP and your phone.', style: TextStyle(color: Colors.white.withValues(alpha: 0.75), fontSize: 17)),
              const SizedBox(height: 40),
              ..._features,
            ]),
          ),
        ),
        Container(width: 1, margin: const EdgeInsets.symmetric(vertical: 48), color: Colors.white.withValues(alpha: 0.12)),
        Expanded(flex: 4, child: _form(context, canPop, showBrand: false)),
      ]);
}

/// A text field with a leading icon, used by every sign-in screen.
class AuthField extends StatefulWidget {
  const AuthField({super.key, required this.controller, required this.label, required this.icon, this.secret = false, this.keyboard, this.autofill, this.action = TextInputAction.next,
      this.onSubmitted, this.onChanged, this.error, this.hint, this.capitalization = TextCapitalization.none, this.autofocus = false});
  final TextEditingController controller;
  final String label;
  final IconData icon;
  final bool secret, autofocus;
  final TextInputType? keyboard;
  final Iterable<String>? autofill;
  final TextInputAction action;
  final ValueChanged<String>? onSubmitted, onChanged;
  final String? error, hint;
  final TextCapitalization capitalization;
  @override
  State<AuthField> createState() => _AuthFieldState();
}

class _AuthFieldState extends State<AuthField> {
  bool _show = false;
  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.only(bottom: 14),
        child: TextField(
          controller: widget.controller, obscureText: widget.secret && !_show, keyboardType: widget.keyboard, autofillHints: widget.autofill, textInputAction: widget.action,
          onSubmitted: widget.onSubmitted, onChanged: widget.onChanged, textCapitalization: widget.capitalization, autofocus: widget.autofocus, autocorrect: !widget.secret,
          style: const TextStyle(fontSize: 15.5, fontWeight: FontWeight.w500),
          decoration: InputDecoration(
            labelText: widget.label, hintText: widget.hint, errorText: widget.error, errorMaxLines: 3,
            prefixIcon: Icon(widget.icon),
            suffixIcon: widget.secret ? IconButton(tooltip: _show ? 'Hide' : 'Show', icon: Icon(_show ? Icons.visibility_off_outlined : Icons.visibility_outlined), onPressed: () => setState(() => _show = !_show)) : null,
          ),
        ),
      );
}

/// The big call-to-action button with a busy spinner (white on the gradient).
class AuthButton extends StatelessWidget {
  const AuthButton({super.key, required this.label, required this.onPressed, this.busy = false, this.icon = Icons.arrow_forward_rounded});
  final String label;
  final VoidCallback? onPressed;
  final bool busy;
  final IconData icon;
  @override
  Widget build(BuildContext context) => DecoratedBox(
        decoration: BoxDecoration(borderRadius: BorderRadius.circular(16), boxShadow: onPressed == null || busy ? null : [BoxShadow(color: const Color(0xFF8B5CF6).withValues(alpha: 0.35), blurRadius: 20, offset: const Offset(0, 8))]),
        child: FilledButton(
          onPressed: busy ? null : onPressed,
          child: busy
              ? const SizedBox(height: 22, width: 22, child: CircularProgressIndicator(strokeWidth: 2.4, color: kAuthAccent))
              : Row(mainAxisAlignment: MainAxisAlignment.center, children: [Flexible(child: Text(label)), const SizedBox(width: 8), Icon(icon, size: 20)]),
        ),
      );
}

class AuthMessage extends StatelessWidget {
  const AuthMessage(this.text, {super.key, this.good = false});
  final String text;
  final bool good;
  @override
  Widget build(BuildContext context) {
    final c = good ? const Color(0xFF6EE7B7) : const Color(0xFFFCA5A5);
    return Container(
      margin: const EdgeInsets.only(bottom: 16),
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(color: c.withValues(alpha: 0.12), borderRadius: BorderRadius.circular(16), border: Border.all(color: c.withValues(alpha: 0.4))),
      child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Icon(good ? Icons.check_circle_outline_rounded : Icons.error_outline_rounded, color: c, size: 20),
        const SizedBox(width: 10),
        Expanded(child: Text(text, style: TextStyle(color: c, fontWeight: FontWeight.w600, height: 1.35))),
      ]),
    );
  }
}

/// "or" divider with a label.
class AuthDivider extends StatelessWidget {
  const AuthDivider(this.label, {super.key});
  final String label;
  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 20),
        child: Row(children: [
          Expanded(child: Divider(color: Colors.white.withValues(alpha: 0.18))),
          Flexible(flex: 3, child: Padding(padding: const EdgeInsets.symmetric(horizontal: 12), child: Text(label, textAlign: TextAlign.center, style: TextStyle(color: Colors.white.withValues(alpha: 0.6), fontSize: 13)))),
          Expanded(child: Divider(color: Colors.white.withValues(alpha: 0.18))),
        ]),
      );
}

/// The password rules the gateway enforces, ticked as they are met.
List<(String, bool)> passwordRules(String p) => [
      ('10+ characters', p.length >= 10),
      ('Upper-case letter', RegExp('[A-Z]').hasMatch(p)),
      ('Lower-case letter', RegExp('[a-z]').hasMatch(p)),
      ('A number', RegExp(r'\d').hasMatch(p)),
    ];

class PasswordStrength extends StatelessWidget {
  const PasswordStrength(this.password, {super.key});
  final String password;
  @override
  Widget build(BuildContext context) {
    final rules = passwordRules(password);
    final met = rules.where((r) => r.$2).length;
    final level = password.isEmpty ? 0 : (password.length >= 14 && met == 4 ? 4 : met.clamp(1, 3));
    const colors = [Color(0xFFF87171), Color(0xFFFBBF24), Color(0xFFA3E635), Color(0xFF34D399)];
    const words = ['', 'Weak', 'Fair', 'Good', 'Strong'];
    final off = Colors.white.withValues(alpha: 0.6);
    return Padding(
      padding: const EdgeInsets.only(bottom: 14),
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Row(children: [
          for (var i = 1; i <= 4; i++) Expanded(child: Container(margin: EdgeInsets.only(right: i < 4 ? 4 : 0), height: 5, decoration: BoxDecoration(color: i <= level ? colors[level - 1] : Colors.white.withValues(alpha: 0.15), borderRadius: BorderRadius.circular(4)))),
          if (level > 0) Padding(padding: const EdgeInsets.only(left: 10), child: Text(words[level], maxLines: 1, style: TextStyle(fontSize: 12, fontWeight: FontWeight.w800, color: colors[level - 1]))),
        ]),
        const SizedBox(height: 8),
        Wrap(spacing: 12, runSpacing: 4, children: [
          for (final r in rules) Row(mainAxisSize: MainAxisSize.min, children: [
            Icon(r.$2 ? Icons.check_circle_rounded : Icons.circle_outlined, size: 15, color: r.$2 ? const Color(0xFF34D399) : off),
            const SizedBox(width: 4),
            Flexible(child: Text(r.$1, style: TextStyle(fontSize: 12, color: r.$2 ? const Color(0xFF34D399) : off))),
          ]),
        ]),
      ]),
    );
  }
}

/// Small reassurance chips under the sign-in form.
class TrustRow extends StatelessWidget {
  const TrustRow({super.key});
  @override
  Widget build(BuildContext context) {
    Widget chip(IconData i, String t) => Row(mainAxisSize: MainAxisSize.min, children: [Icon(i, size: 15, color: Colors.white.withValues(alpha: 0.8)), const SizedBox(width: 5), Flexible(child: Text(t, style: TextStyle(fontSize: 12, color: Colors.white.withValues(alpha: 0.7), fontWeight: FontWeight.w600)))]);
    return Wrap(alignment: WrapAlignment.center, spacing: 16, runSpacing: 8, children: [chip(Icons.verified_outlined, 'ZIMRA FDMS'), chip(Icons.lock_outline, 'Encrypted'), chip(Icons.devices_outlined, 'Phone and web')]);
  }
}

/// Kept for screens that ask whether they are drawn on the light panel; the sign-in screens are always on the dark gradient now.
bool authHeaderOnLight(BuildContext context) => false;
