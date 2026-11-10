import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import 'auth_ui.dart';

/// Remembered once the tour was seen (or skipped).
const kWelcomeSeenKey = 'welcomeSeen';

class _Slide {
  const _Slide(this.icon, this.title, this.text, this.points);
  final IconData icon;
  final String title, text;
  final List<String> points;
}

const _slides = [
  _Slide(Icons.receipt_long_rounded, 'Fiscalise from your phone', 'Issue invoices, credit and debit notes that ZIMRA signs in seconds, wherever you are.',
      ['Quick sale and full invoices', 'QR code and PDF ready to share', 'Fiscal day opened and closed for you']),
  _Slide(Icons.insights_rounded, 'Your business at a glance', 'Sales, VAT, devices and alerts on one screen, with the books kept for you.',
      ['Sales and VAT reports', 'Accounting, budgets and assets', 'Alerts before anything goes wrong']),
  _Slide(Icons.card_giftcard_rounded, 'Refer and earn', 'Bring another company and earn a share of what it pays, plus free months as you grow.',
      ['Your own referral code', 'Rewards on every paying company', 'Bonus levels with free months']),
];

/// The first-launch tour: three short pages, then sign in or register.
class WelcomeScreen extends StatefulWidget {
  const WelcomeScreen({super.key, required this.onSignIn, required this.onRegister});
  final VoidCallback onSignIn, onRegister;
  @override
  State<WelcomeScreen> createState() => _WelcomeScreenState();
}

class _WelcomeScreenState extends State<WelcomeScreen> {
  final _pages = PageController();
  int _at = 0;

  @override
  void dispose() { _pages.dispose(); super.dispose(); }

  bool get _last => _at == _slides.length - 1;

  @override
  Widget build(BuildContext context) => AnnotatedRegion<SystemUiOverlayStyle>(
        value: SystemUiOverlayStyle.light,
        child: Scaffold(
          body: DecoratedBox(
            decoration: const BoxDecoration(gradient: kAuthGradient),
            child: SafeArea(
              child: Center(
                child: ConstrainedBox(
                  constraints: const BoxConstraints(maxWidth: 560),
                  child: Column(children: [
                    Padding(
                      padding: const EdgeInsets.fromLTRB(20, 12, 8, 0),
                      child: Row(children: [
                        const Expanded(child: Align(alignment: Alignment.centerLeft, child: BrandName())),
                        if (!_last) TextButton(onPressed: widget.onSignIn, child: const Text('Skip', style: TextStyle(color: Colors.white, fontWeight: FontWeight.w600))),
                      ]),
                    ),
                    Expanded(
                      child: PageView.builder(
                        controller: _pages, itemCount: _slides.length, onPageChanged: (i) => setState(() => _at = i),
                        itemBuilder: (context, i) => _page(_slides[i]),
                      ),
                    ),
                    Row(mainAxisAlignment: MainAxisAlignment.center, children: [
                      for (var i = 0; i < _slides.length; i++)
                        AnimatedContainer(
                          duration: const Duration(milliseconds: 250), margin: const EdgeInsets.symmetric(horizontal: 4), height: 8, width: i == _at ? 26 : 8,
                          decoration: BoxDecoration(color: Colors.white.withValues(alpha: i == _at ? 1 : 0.4), borderRadius: BorderRadius.circular(8)),
                        ),
                    ]),
                    Padding(
                      padding: const EdgeInsets.fromLTRB(22, 22, 22, 18),
                      child: _last
                          ? Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
                              FilledButton(
                                onPressed: widget.onRegister,
                                style: FilledButton.styleFrom(backgroundColor: Colors.white, foregroundColor: const Color(0xFF3730A3), padding: const EdgeInsets.symmetric(vertical: 16), shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(14))),
                                child: const Text('Register your company', style: TextStyle(fontSize: 16, fontWeight: FontWeight.w800)),
                              ),
                              const SizedBox(height: 10),
                              OutlinedButton(
                                onPressed: widget.onSignIn,
                                style: OutlinedButton.styleFrom(foregroundColor: Colors.white, side: BorderSide(color: Colors.white.withValues(alpha: 0.6)), padding: const EdgeInsets.symmetric(vertical: 16), shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(14))),
                                child: const Text('I already have an account', style: TextStyle(fontSize: 16, fontWeight: FontWeight.w700)),
                              ),
                            ])
                          : FilledButton(
                              onPressed: () => _pages.nextPage(duration: const Duration(milliseconds: 350), curve: Curves.easeOutCubic),
                              style: FilledButton.styleFrom(backgroundColor: Colors.white, foregroundColor: const Color(0xFF3730A3), padding: const EdgeInsets.symmetric(vertical: 16), shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(14))),
                              child: const Row(mainAxisAlignment: MainAxisAlignment.center, children: [Text('Next', style: TextStyle(fontSize: 16, fontWeight: FontWeight.w800)), SizedBox(width: 8), Icon(Icons.arrow_forward_rounded)]),
                            ),
                    ),
                  ]),
                ),
              ),
            ),
          ),
        ),
      );

  Widget _page(_Slide s) => LayoutBuilder(builder: (context, c) => SingleChildScrollView(
        padding: const EdgeInsets.fromLTRB(26, 24, 26, 12),
        child: ConstrainedBox(constraints: BoxConstraints(minHeight: (c.maxHeight - 36).clamp(0.0, double.infinity)), child: Column(mainAxisAlignment: MainAxisAlignment.center, crossAxisAlignment: CrossAxisAlignment.start, children: [
          Container(
            padding: const EdgeInsets.all(22),
            decoration: BoxDecoration(color: Colors.white.withValues(alpha: 0.14), borderRadius: BorderRadius.circular(28), border: Border.all(color: Colors.white.withValues(alpha: 0.2))),
            child: Icon(s.icon, color: Colors.white, size: 56),
          ),
          const SizedBox(height: 28),
          Text(s.title, style: const TextStyle(color: Colors.white, fontSize: 30, fontWeight: FontWeight.w800, height: 1.15, letterSpacing: -0.5)),
          const SizedBox(height: 12),
          Text(s.text, style: TextStyle(color: Colors.white.withValues(alpha: 0.85), fontSize: 16, height: 1.45)),
          const SizedBox(height: 22),
          for (final p in s.points)
            Padding(
              padding: const EdgeInsets.only(bottom: 10),
              child: Row(children: [
                Container(padding: const EdgeInsets.all(3), decoration: const BoxDecoration(color: Colors.white, shape: BoxShape.circle), child: const Icon(Icons.check_rounded, size: 14, color: Color(0xFF4F46E5))),
                const SizedBox(width: 12),
                Expanded(child: Text(p, style: const TextStyle(color: Colors.white, fontSize: 15, fontWeight: FontWeight.w600))),
              ]),
            ),
        ])),
      ));
}
