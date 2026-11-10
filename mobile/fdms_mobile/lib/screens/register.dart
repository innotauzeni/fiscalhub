import 'dart:async';

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/api.dart';
import '../core/auth.dart';
import '../core/models.dart';
import '../ui/kit.dart';
import 'about.dart';
import 'auth_ui.dart';

/// Register a company from the phone: company, your sign-in, plan, then review. Same rules and endpoint as the website.
/// A referral code (another company's or a partner's) can be typed in, so whoever sent you is credited.
/// Then the email is confirmed with a 6-digit code and the company opens at once: a free demo with its first invoice ready.
/// [verifyEmail]: open straight on the code step (signing in with an unconfirmed email leads here).
class RegisterScreen extends StatefulWidget {
  const RegisterScreen({super.key, this.verifyEmail});
  final String? verifyEmail;
  @override
  State<RegisterScreen> createState() => _RegisterScreenState();
}

class _Step {
  const _Step(this.title, this.subtitle, this.icon);
  final String title, subtitle;
  final IconData icon;
}

const _steps = [
  _Step('Your company', 'Tell us who you are. Only the name is required now.', Icons.apartment_rounded),
  _Step('Your sign-in', 'Sign in with your email, or a user name of your own.', Icons.person_outline_rounded),
  _Step('Choose a plan', 'Start with a free trial where the plan has one. Change it any time.', Icons.workspace_premium_outlined),
  _Step('Review and send', 'Check your details. We then email you a code to confirm your address.', Icons.fact_check_outlined),
];

class _RegisterScreenState extends State<RegisterScreen> {
  int _step = 0;
  bool _tried = false, _busy = false, _done = false, _agree = false, _yearly = false, _plansLoaded = false;
  String? _error;
  int? _planId;
  List<Json> _plans = [];
  final _company = TextEditingController(), _tin = TextEditingController(), _phone = TextEditingController(), _first = TextEditingController(), _last = TextEditingController(),
      _email = TextEditingController(), _pw = TextEditingController(), _pw2 = TextEditingController(), _msg = TextEditingController(), _ref = TextEditingController(),
      _userName = TextEditingController(), _code = TextEditingController();
  /// form, then verify (the emailed code), then ready (the account is open).
  String _phase = 'form';
  Json? _ready;
  String? _note;
  int _cooldown = 0;
  Timer? _timer;

  @override
  void initState() {
    super.initState();
    _loadPlans();
    if (widget.verifyEmail != null) { _email.text = widget.verifyEmail!; _phase = 'verify'; }
  }

  @override
  void dispose() { _timer?.cancel(); for (final c in [_company, _tin, _phone, _first, _last, _email, _pw, _pw2, _msg, _ref, _userName, _code]) { c.dispose(); } super.dispose(); }

  Future<void> _loadPlans() async {
    try {
      final d = await context.read<Api>().anonymousGet('/api/v1/public/plans');
      if (mounted && d is List) setState(() => _plans = d.map((e) => Map<String, dynamic>.from(e as Map)).toList());
    } catch (_) { /* choosing a plan is optional */ }
    if (mounted) setState(() => _plansLoaded = true);
  }

  bool get _emailOk => RegExp(r'^[^@\s]+@[^@\s]+\.[^@\s]+$').hasMatch(_email.text.trim());
  bool get _userNameOk { final u = _userName.text.trim(); return u.isEmpty || RegExp(r'^[A-Za-z0-9][A-Za-z0-9._-]{2,31}$').hasMatch(u); }

  bool _ok(int i) => switch (i) {
        0 => _company.text.trim().isNotEmpty,
        1 => _first.text.trim().isNotEmpty && _emailOk && _userNameOk && passwordRules(_pw.text).every((r) => r.$2) && _pw.text == _pw2.text,
        _ => true,
      };

  void _go(int i) { FocusScope.of(context).unfocus(); setState(() { _step = i; _tried = false; _error = null; }); }
  void _next() { if (!_ok(_step)) { setState(() => _tried = true); return; } _go(_step + 1); }

  String _price(Json p) {
    final v = (_yearly ? p['priceYearly'] : p['priceMonthly']) ?? 0;
    return v == 0 ? 'Free' : moneyIn(v, (p['currency'] ?? 'USD').toString());
  }

  Future<void> _submit() async {
    for (var i = 0; i < 2; i++) { if (!_ok(i)) { _go(i); setState(() => _tried = true); return; } }
    if (!_agree) { setState(() => _error = 'Tick the box to accept the terms and confirm your details.'); return; }
    setState(() { _busy = true; _error = null; });
    try {
      String? t(TextEditingController c) => c.text.trim().isEmpty ? null : c.text.trim();
      final r = await context.read<Api>().anonymousPost('/api/v1/public/register', {
        'companyName': _company.text.trim(), 'tin': t(_tin), 'contactName': _first.text.trim(), 'contactSurname': _last.text.trim(), 'email': _email.text.trim(), 'phone': t(_phone),
        'password': _pw.text, 'planId': _planId, 'billingCycle': _yearly ? 'yearly' : 'monthly', 'message': t(_msg), 'website': '', 'logoData': null, 'partnerCode': t(_ref)?.toUpperCase(), 'userName': t(_userName),
      });
      if (!mounted) return;
      if (r is Map && r['verificationRequired'] == true) { setState(() { _phase = 'verify'; _code.clear(); _note = null; }); _startCooldown(); } else { setState(() => _done = true); }
    } catch (e) {
      if (mounted) setState(() => _error = e.toString().replaceFirst(RegExp(r'^\[[A-Z_]+\]\s*'), ''));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    if (_phase == 'verify') return _verifyView();
    if (_phase == 'ready') return _readyView();
    if (_done) return _thanks();
    final s = _steps[_step];
    return PopScope(
      canPop: _step == 0,
      onPopInvokedWithResult: (didPop, _) { if (!didPop) _go(_step - 1); },
      child: AuthScaffold(
        title: s.title,
        subtitle: s.subtitle,
        headerExtra: _StepBar(step: _step, onTap: (i) { if (i < _step) _go(i); }),
        children: [
          if (_error != null) AuthMessage(_error!),
          ..._body(),
          const SizedBox(height: 10),
          Row(children: [
            if (_step > 0) ...[
              Expanded(child: OutlinedButton(
                onPressed: () => _go(_step - 1),
                style: OutlinedButton.styleFrom(padding: const EdgeInsets.symmetric(vertical: 15), shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(14))),
                child: const Text('Back', style: TextStyle(fontWeight: FontWeight.w700)),
              )),
              const SizedBox(width: 12),
            ],
            Expanded(flex: 2, child: _step < _steps.length - 1
                ? AuthButton(label: 'Continue', onPressed: _next)
                : AuthButton(label: 'Send registration', onPressed: _agree ? _submit : null, busy: _busy, icon: Icons.send_rounded)),
          ]),
          if (_step == 0) ...[
            const SizedBox(height: 8),
            Center(child: TextButton(onPressed: () => Navigator.maybePop(context), child: const Text('Already registered? Sign in'))),
          ],
          const SizedBox(height: 18),
          const LegalLinks(),
          const SizedBox(height: 12),
        ],
      ),
    );
  }

  List<Widget> _body() {
    final hint = kAuthHint;
    switch (_step) {
      case 0:
        return [
          AuthField(controller: _company, label: 'Company name', icon: Icons.apartment_rounded, capitalization: TextCapitalization.words, autofill: const [AutofillHints.organizationName], onChanged: (_) => setState(() {}), error: _tried && _company.text.trim().isEmpty ? 'Enter your company name' : null),
          AuthField(controller: _tin, label: 'TIN (optional)', icon: Icons.badge_outlined, keyboard: TextInputType.number, hint: '10 digits; ZIMRA needs it for devices'),
          AuthField(controller: _phone, label: 'Company phone (optional)', icon: Icons.phone_outlined, keyboard: TextInputType.phone, autofill: const [AutofillHints.telephoneNumber]),
          _RefBox(controller: _ref),
        ];
      case 1:
        return [
          Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Expanded(child: AuthField(controller: _first, label: 'First name', icon: Icons.person_outline_rounded, capitalization: TextCapitalization.words, autofill: const [AutofillHints.givenName], onChanged: (_) => setState(() {}), error: _tried && _first.text.trim().isEmpty ? 'Required' : null)),
            const SizedBox(width: 12),
            Expanded(child: AuthField(controller: _last, label: 'Surname', icon: Icons.badge_outlined, capitalization: TextCapitalization.words, autofill: const [AutofillHints.familyName])),
          ]),
          AuthField(controller: _email, label: 'Work email', icon: Icons.alternate_email_rounded, keyboard: TextInputType.emailAddress, autofill: const [AutofillHints.email], onChanged: (_) => setState(() {}), error: _tried && !_emailOk ? 'Enter a valid email' : null),
          AuthField(controller: _userName, label: 'User name (optional)', icon: Icons.person_pin_outlined, hint: 'e.g. Inno. Sign in with this instead of your email.', autofill: const [AutofillHints.newUsername], onChanged: (_) => setState(() {}), error: _tried && !_userNameOk ? '3-32 letters or digits; no spaces or @' : null),
          AuthField(controller: _pw, label: 'Password', icon: Icons.lock_outline_rounded, secret: true, autofill: const [AutofillHints.newPassword], onChanged: (_) => setState(() {})),
          PasswordStrength(_pw.text),
          AuthField(controller: _pw2, label: 'Password again', icon: Icons.lock_reset_rounded, secret: true, action: TextInputAction.done, onChanged: (_) => setState(() {}), error: _tried && _pw.text != _pw2.text ? 'The passwords do not match' : null),
          if (_tried && !passwordRules(_pw.text).every((r) => r.$2)) const AuthMessage('Your password needs every item ticked above.'),
        ];
      case 2:
        return [
          Center(child: SegmentedButton<bool>(
            segments: const [ButtonSegment(value: false, label: Text('Monthly')), ButtonSegment(value: true, label: Text('Yearly'), icon: Icon(Icons.savings_outlined, size: 18))],
            selected: {_yearly}, onSelectionChanged: (v) => setState(() => _yearly = v.first),
          )),
          const SizedBox(height: 16),
          if (!_plansLoaded) const Padding(padding: EdgeInsets.all(24), child: Center(child: CircularProgressIndicator())),
          for (final p in _plans) _PlanCard(plan: p, price: _price(p), per: _yearly ? 'year' : 'month', selected: _planId == p['id'], onTap: () => setState(() => _planId = p['id'] as int?)),
          _PlanCard(plan: const {'name': 'Not sure yet', 'description': 'We will recommend the right plan after looking at your business.'}, price: null, per: '', selected: _planId == null, onTap: () => setState(() => _planId = null)),
          const SizedBox(height: 4),
          Text('Start with a free demo. Your first invoice is ready straight away: pay it when you are ready to go live.', textAlign: TextAlign.center, style: TextStyle(color: hint, fontSize: 12)),
          const SizedBox(height: 8),
        ];
      default:
        final plan = _plans.where((p) => p['id'] == _planId).firstOrNull;
        return [
          _ReviewCard(title: 'Company', icon: Icons.apartment_rounded, onEdit: () => _go(0), rows: [('Name', _company.text.trim()), ('TIN', _tin.text.trim()), ('Phone', _phone.text.trim()), ('Referral code', _ref.text.trim().toUpperCase())]),
          _ReviewCard(title: 'Your sign-in', icon: Icons.person_outline_rounded, onEdit: () => _go(1), rows: [('Name', '${_first.text.trim()} ${_last.text.trim()}'.trim()), ('Email', _email.text.trim()), ('Sign in with', _userName.text.trim().isEmpty ? _email.text.trim() : _userName.text.trim()), ('Password', '•' * _pw.text.length.clamp(0, 12))]),
          _ReviewCard(title: 'Plan', icon: Icons.workspace_premium_outlined, onEdit: () => _go(2), rows: [('Plan', plan == null ? 'Recommend one for me' : '${plan['name']}'), if (plan != null) ('Price', '${_price(plan)} / ${_yearly ? 'year' : 'month'}'), if (plan != null && (plan['trialDays'] ?? 0) > 0) ('Free trial', '${plan['trialDays']} days')]),
          TextField(controller: _msg, minLines: 2, maxLines: 4, decoration: const InputDecoration(labelText: 'Anything we should know? (optional)', hintText: 'Number of tills and branches, the system you want to connect…', prefixIcon: Icon(Icons.chat_bubble_outline_rounded))),
          const SizedBox(height: 12),
          InkWell(
            borderRadius: BorderRadius.circular(14),
            onTap: () => setState(() { _agree = !_agree; _error = null; }),
            child: Container(
              padding: const EdgeInsets.all(12),
              decoration: BoxDecoration(borderRadius: BorderRadius.circular(14), border: Border.all(color: _agree ? Colors.white : kAuthLine), color: Colors.white.withValues(alpha: _agree ? 0.1 : 0.04)),
              child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Checkbox(value: _agree, onChanged: (v) => setState(() { _agree = v ?? false; _error = null; }), visualDensity: VisualDensity.compact),
                const SizedBox(width: 4),
                Expanded(child: Padding(padding: const EdgeInsets.only(top: 10), child: Text('I confirm these details are correct, that I may register this company, and I accept the terms and conditions and the privacy policy.', style: TextStyle(fontSize: 13, height: 1.4, color: Colors.white)))),
              ]),
            ),
          ),
          const SizedBox(height: 8),
        ];
    }
  }

  void _startCooldown() {
    _timer?.cancel();
    setState(() => _cooldown = 60);
    _timer = Timer.periodic(const Duration(seconds: 1), (t) { if (!mounted) { t.cancel(); return; } setState(() => _cooldown = (_cooldown - 1).clamp(0, 60)); if (_cooldown == 0) t.cancel(); });
  }

  String _clean(Object e) => e.toString().replaceFirst(RegExp(r'^\[[A-Z_]+\]\s*'), '');

  Future<void> _resend() async {
    setState(() { _error = null; _note = null; });
    try {
      final r = await context.read<Api>().anonymousPost('/api/v1/public/register/resend', {'email': _email.text.trim()});
      if (mounted) setState(() => _note = (r is Map ? r['message'] : null)?.toString() ?? 'A new code is on its way.');
      _startCooldown();
    } catch (e) { if (mounted) setState(() => _error = _clean(e)); }
  }

  Future<void> _verify() async {
    if (_busy) return;
    if (_code.text.trim().length != 6 || !_emailOk) { setState(() => _error = 'Enter your email and the 6-digit code.'); return; }
    final api = context.read<Api>();
    final auth = context.read<AuthController>();
    FocusScope.of(context).unfocus();
    setState(() { _busy = true; _error = null; _note = null; });
    try {
      final r = Map<String, dynamic>.from(await api.anonymousPost('/api/v1/public/register/verify', {'email': _email.text.trim(), 'code': _code.text.trim()}) as Map);
      if (!mounted) return;
      if (r['status'] != 'Active') { setState(() { _phase = 'form'; _done = true; }); return; }
      setState(() { _ready = r; _phase = 'ready'; });
      // the password was typed a moment ago: sign straight in (coming from a link, they sign in themselves)
      if (_pw.text.isNotEmpty && r['signInName'] != null) {
        try { await auth.login(r['signInName'].toString(), _pw.text); } catch (_) { /* they can sign in by hand */ }
      }
    } catch (e) {
      if (mounted) setState(() { _error = _clean(e); _code.clear(); });
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Widget _verifyView() => AuthScaffold(
        title: 'Confirm your email',
        subtitle: _email.text.trim().isEmpty ? 'Enter the email you registered with and the code we sent you.' : 'We sent a 6-digit code to ${_email.text.trim()}. It works for 15 minutes.',
        children: [
          if (_note != null) AuthMessage(_note!, good: true),
          if (_error != null) AuthMessage(_error!),
          if (widget.verifyEmail != null || _email.text.trim().isEmpty)
            AuthField(controller: _email, label: 'Email', icon: Icons.alternate_email_rounded, keyboard: TextInputType.emailAddress, autofill: const [AutofillHints.email], onChanged: (_) => setState(() {})),
          TextField(
            controller: _code, autofocus: true, textAlign: TextAlign.center, keyboardType: TextInputType.number, maxLength: 6, autofillHints: const [AutofillHints.oneTimeCode], onSubmitted: (_) => _verify(),
            style: const TextStyle(fontSize: 26, letterSpacing: 8, fontWeight: FontWeight.w700, fontFamily: 'monospace'),
            decoration: const InputDecoration(hintText: '000000', counterText: ''),
          ),
          const SizedBox(height: 16),
          AuthButton(label: 'Confirm and open my account', onPressed: _verify, busy: _busy, icon: Icons.verified_rounded),
          const SizedBox(height: 8),
          Wrap(alignment: WrapAlignment.spaceBetween, crossAxisAlignment: WrapCrossAlignment.center, spacing: 8, children: [
            TextButton(onPressed: _cooldown > 0 || _busy ? null : _resend, child: Text(_cooldown > 0 ? 'New code in ${_cooldown}s' : 'Send a new code')),
            if (widget.verifyEmail == null) TextButton(onPressed: () => setState(() { _phase = 'form'; _error = null; }), child: const Text('Change my details')),
          ]),
          Text('No email? Check the spam folder.', textAlign: TextAlign.center, style: TextStyle(color: kAuthHint, fontSize: 12)),
          const SizedBox(height: 16),
        ],
      );

  Widget _readyView() {
    final r = _ready ?? {};
    final signedIn = context.watch<AuthController>().session != null;
    Widget tile(IconData icon, String title, String text) => Container(
          margin: const EdgeInsets.only(bottom: 12),
          padding: const EdgeInsets.all(14),
          decoration: BoxDecoration(borderRadius: BorderRadius.circular(16), color: Colors.white.withValues(alpha: 0.08), border: Border.all(color: kAuthLine)),
          child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Icon(icon, color: Colors.white),
            const SizedBox(width: 12),
            Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [Text(title, style: const TextStyle(fontWeight: FontWeight.w800, color: Colors.white)), const SizedBox(height: 2), Text(text, style: TextStyle(color: kAuthHint, fontSize: 13, height: 1.35))])),
          ]),
        );
    return AuthScaffold(
      title: '${_company.text.trim().isEmpty ? 'Your company' : _company.text.trim()} is ready',
      subtitle: 'Your email is confirmed. Sign in as ${r['signInName'] ?? _email.text.trim()}.',
      children: [
        tile(Icons.science_outlined, 'Free demo', 'Try everything with ZIMRA\'s test environment${r['trialEndsAt'] != null ? ' until ${fmtDate(r['trialEndsAt'])}' : ''}.'),
        tile(Icons.receipt_long_outlined, 'Your invoice', '${r['invoiceNumber'] != null ? '${r['invoiceNumber']} is under Billing. ' : ''}Pay it when you are ready to go live; the paid period starts when the demo ends.'),
        Text('A demo that is still unpaid a week after it ends is deleted with its data.', textAlign: TextAlign.center, style: TextStyle(color: kAuthHint, fontSize: 12)),
        const SizedBox(height: 16),
        AuthButton(
          label: signedIn ? 'Start my demo' : 'Sign in',
          icon: signedIn ? Icons.rocket_launch_outlined : Icons.login_rounded,
          onPressed: () => Navigator.of(context).popUntil((route) => route.isFirst),
        ),
        const SizedBox(height: 16),
      ],
    );
  }

  Widget _thanks() {
    final hint = kAuthHint;
    Widget item(int n, String title, String text, {bool done = false}) => Padding(
          padding: const EdgeInsets.only(bottom: 16),
          child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
            CircleAvatar(radius: 15, backgroundColor: done ? const Color(0xFF059669) : Colors.white.withValues(alpha: 0.14), child: done ? const Icon(Icons.check, size: 16, color: Colors.white) : Text('$n', style: const TextStyle(color: Colors.white, fontWeight: FontWeight.w800, fontSize: 13))),
            const SizedBox(width: 12),
            Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [Text(title, style: const TextStyle(fontWeight: FontWeight.w700)), const SizedBox(height: 2), Text(text, style: TextStyle(color: hint, fontSize: 13, height: 1.35))])),
          ]),
        );
    return AuthScaffold(
      title: 'You are all set!',
      subtitle: 'Your registration for ${_company.text.trim()} was received.',
      children: [
        Center(child: Container(padding: const EdgeInsets.all(18), margin: const EdgeInsets.only(bottom: 20), decoration: BoxDecoration(color: const Color(0xFF059669).withValues(alpha: 0.1), shape: BoxShape.circle), child: const Icon(Icons.mark_email_read_outlined, color: Color(0xFF059669), size: 40))),
        const Text('What happens next', style: TextStyle(fontSize: 17, fontWeight: FontWeight.w800)),
        const SizedBox(height: 14),
        item(1, 'Registration received', 'Your details are with us.', done: true),
        item(2, 'We review it', 'Usually within one working day.'),
        item(3, 'You get an email', 'At ${_email.text.trim()} as soon as the company is approved.'),
        item(4, 'Sign in and go live', 'Register your ZIMRA device and start fiscalising.'),
        const SizedBox(height: 8),
        AuthButton(label: 'Back to sign in', onPressed: () => Navigator.pop(context), icon: Icons.login_rounded),
        const SizedBox(height: 16),
      ],
    );
  }
}

/// Step dots in the brand header: done steps get a tick and can be tapped to go back.
class _StepBar extends StatelessWidget {
  const _StepBar({required this.step, required this.onTap});
  final int step;
  final ValueChanged<int> onTap;
  @override
  Widget build(BuildContext context) {
    final light = authHeaderOnLight(context);
    final on = light ? kBrand : Colors.white;
    final off = light ? kAuthLine : Colors.white.withValues(alpha: 0.3);
    return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
      Text('Step ${step + 1} of ${_steps.length}', style: TextStyle(color: light ? kAuthHint : Colors.white.withValues(alpha: 0.8), fontSize: 12, fontWeight: FontWeight.w700, letterSpacing: 0.4)),
      const SizedBox(height: 10),
      Row(children: [
        for (var i = 0; i < _steps.length; i++) ...[
          InkWell(
            onTap: () => onTap(i), customBorder: const CircleBorder(),
            child: AnimatedContainer(
              duration: const Duration(milliseconds: 250), padding: const EdgeInsets.all(7),
              decoration: BoxDecoration(shape: BoxShape.circle, color: i <= step ? on : Colors.transparent, border: Border.all(color: i <= step ? on : off, width: 2)),
              child: Icon(i < step ? Icons.check_rounded : _steps[i].icon, size: 16, color: i <= step ? (light ? Colors.white : const Color(0xFF4338CA)) : off),
            ),
          ),
          if (i < _steps.length - 1) Expanded(child: AnimatedContainer(duration: const Duration(milliseconds: 250), height: 3, margin: const EdgeInsets.symmetric(horizontal: 6), decoration: BoxDecoration(color: i < step ? on : off, borderRadius: BorderRadius.circular(3)))),
        ],
      ]),
    ]);
  }
}

class _RefBox extends StatelessWidget {
  const _RefBox({required this.controller});
  final TextEditingController controller;
  @override
  Widget build(BuildContext context) => Container(
        margin: const EdgeInsets.only(top: 4, bottom: 8),
        padding: const EdgeInsets.fromLTRB(14, 14, 14, 0),
        decoration: BoxDecoration(gradient: LinearGradient(colors: [Colors.white.withValues(alpha: 0.08), const Color(0xFF8B5CF6).withValues(alpha: 0.18)]), border: Border.all(color: Colors.white.withValues(alpha: 0.14)), borderRadius: BorderRadius.circular(16)),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          const Row(children: [Icon(Icons.card_giftcard_rounded, color: Color(0xFFC4B5FD), size: 20), SizedBox(width: 8), Expanded(child: Text('Were you referred?', style: TextStyle(fontWeight: FontWeight.w700)))]),
          const SizedBox(height: 4),
          Text('Type the code of the company or partner that sent you, so they get the credit.', style: TextStyle(fontSize: 12, color: kAuthHint)),
          const SizedBox(height: 12),
          AuthField(controller: controller, label: 'Referral code (optional)', icon: Icons.confirmation_number_outlined, capitalization: TextCapitalization.characters, hint: 'REF…'),
        ]),
      );
}

class _PlanCard extends StatelessWidget {
  const _PlanCard({required this.plan, required this.price, required this.per, required this.selected, required this.onTap});
  final Json plan;
  final String? price;
  final String per;
  final bool selected;
  final VoidCallback onTap;
  @override
  Widget build(BuildContext context) {
    final hint = kAuthHint;
    final trial = (plan['trialDays'] ?? 0) as num;
    String lim(dynamic n) => n == null ? '' : (n == 0 ? 'Unlimited' : '$n');
    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: InkWell(
        onTap: onTap, borderRadius: BorderRadius.circular(18),
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 200),
          padding: const EdgeInsets.all(16),
          decoration: BoxDecoration(
            borderRadius: BorderRadius.circular(18),
            border: Border.all(color: selected ? Colors.white : kAuthLine, width: selected ? 2 : 1),
            color: Colors.white.withValues(alpha: selected ? 0.12 : 0.05),
          ),
          child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Wrap(spacing: 8, runSpacing: 4, crossAxisAlignment: WrapCrossAlignment.center, children: [
                Text('${plan['name']}', style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w800)),
                if (trial > 0) Container(padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3), decoration: BoxDecoration(color: const Color(0xFF059669).withValues(alpha: 0.12), borderRadius: BorderRadius.circular(20)), child: Text('$trial-day free trial', style: const TextStyle(color: Color(0xFF059669), fontSize: 11, fontWeight: FontWeight.w700))),
              ]),
              if (price != null) ...[
                const SizedBox(height: 6),
                Text.rich(TextSpan(children: [TextSpan(text: price, style: const TextStyle(fontSize: 22, fontWeight: FontWeight.w800)), TextSpan(text: price == 'Free' ? '' : ' / $per', style: TextStyle(color: hint, fontSize: 13))])),
              ],
              if ((plan['description'] ?? '').toString().isNotEmpty) Padding(padding: const EdgeInsets.only(top: 6), child: Text('${plan['description']}', style: TextStyle(color: hint, fontSize: 13, height: 1.35))),
              if (plan['maxDevices'] != null) Padding(padding: const EdgeInsets.only(top: 8), child: Text('${lim(plan['maxDevices'])} devices · ${lim(plan['maxUsers'])} users · ${lim(plan['maxBranches'])} branches', style: TextStyle(color: hint, fontSize: 12))),
            ])),
            const SizedBox(width: 10),
            AnimatedContainer(
              duration: const Duration(milliseconds: 200), padding: const EdgeInsets.all(3),
              decoration: BoxDecoration(shape: BoxShape.circle, color: selected ? Colors.white : Colors.transparent, border: Border.all(color: selected ? Colors.white : kAuthLine, width: 2)),
              child: Icon(Icons.check, size: 14, color: selected ? kAuthAccent : Colors.transparent),
            ),
          ]),
        ),
      ),
    );
  }
}

class _ReviewCard extends StatelessWidget {
  const _ReviewCard({required this.title, required this.icon, required this.rows, required this.onEdit});
  final String title;
  final IconData icon;
  final List<(String, String)> rows;
  final VoidCallback onEdit;
  @override
  Widget build(BuildContext context) {
    final hint = kAuthHint;
    return Container(
      margin: const EdgeInsets.only(bottom: 12),
      padding: const EdgeInsets.fromLTRB(16, 8, 8, 12),
      decoration: BoxDecoration(borderRadius: BorderRadius.circular(16), border: Border.all(color: kAuthLine)),
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Row(children: [
          Icon(icon, size: 18, color: const Color(0xFFC4B5FD)),
          const SizedBox(width: 8),
          Expanded(child: Text(title, style: const TextStyle(fontWeight: FontWeight.w800))),
          TextButton.icon(onPressed: onEdit, icon: const Icon(Icons.edit_outlined, size: 16), label: const Text('Edit')),
        ]),
        for (final r in rows)
          Padding(
            padding: const EdgeInsets.only(top: 6, right: 8),
            child: Wrap(spacing: 8, children: [
              Text(r.$1, style: TextStyle(color: hint, fontSize: 13)),
              Text(r.$2.isEmpty ? '—' : r.$2, style: const TextStyle(fontWeight: FontWeight.w600, fontSize: 13)),
            ]),
          ),
      ]),
    );
  }
}
