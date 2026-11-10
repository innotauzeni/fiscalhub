import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/api.dart';

import '../core/auth.dart';
import '../core/config.dart';
import '../ui/kit.dart';
import 'about.dart';
import 'auth_ui.dart';
import 'forgot_password.dart';
import 'register.dart';
import 'welcome.dart';

/// What a signed-out person sees: the welcome tour the first time, then sign-in.
class AuthEntry extends StatefulWidget {
  const AuthEntry({super.key});
  @override
  State<AuthEntry> createState() => _AuthEntryState();
}

class _AuthEntryState extends State<AuthEntry> {
  late bool _welcome = context.read<AppConfig>().getString(kWelcomeSeenKey) == null;

  Future<void> _done({bool register = false}) async {
    await context.read<AppConfig>().setString(kWelcomeSeenKey, '1');
    if (!mounted) return;
    setState(() => _welcome = false);
    if (register) Navigator.push(context, MaterialPageRoute(builder: (_) => const RegisterScreen()));
  }

  @override
  Widget build(BuildContext context) => AnimatedSwitcher(
        duration: const Duration(milliseconds: 350),
        child: _welcome ? WelcomeScreen(key: const ValueKey('w'), onSignIn: () => _done(), onRegister: () => _done(register: true)) : const LoginScreen(key: ValueKey('l')),
      );
}

class LoginScreen extends StatefulWidget {
  const LoginScreen({super.key});
  @override
  State<LoginScreen> createState() => _LoginScreenState();
}

class _LoginScreenState extends State<LoginScreen> {
  final _id = TextEditingController();
  final _pw = TextEditingController();
  final _code = TextEditingController();
  bool _busy = false;
  String? _error, _mfaToken, _note;

  @override
  void initState() {
    super.initState();
    _id.text = context.read<AppConfig>().getString('lastLogin') ?? '';
  }

  @override
  void dispose() { _id.dispose(); _pw.dispose(); _code.dispose(); super.dispose(); }

  Future<void> _submit() async {
    if (_busy) return;
    if (_mfaToken == null && (_id.text.trim().isEmpty || _pw.text.isEmpty)) { setState(() => _error = 'Enter your email and password.'); return; }
    final auth = context.read<AuthController>();
    final config = context.read<AppConfig>();
    FocusScope.of(context).unfocus();
    setState(() { _busy = true; _error = null; _note = null; });
    try {
      if (_mfaToken != null) {
        await auth.loginMfa(_mfaToken!, _code.text.trim());
      } else {
        final r = await auth.login(_id.text.trim(), _pw.text);
        await config.setString('lastLogin', _id.text.trim());
        if (r.needsCode) setState(() { _mfaToken = r.mfaToken; _code.clear(); });
      }
    } catch (e) {
      final m = e.toString();
      // registered but the email code was never entered: go and enter it
      if (e is ApiException && e.code == 'EMAIL_NOT_CONFIRMED' && mounted) {
        setState(() => _error = null);
        await Navigator.push(context, MaterialPageRoute(builder: (_) => RegisterScreen(verifyEmail: _id.text.trim().contains('@') ? _id.text.trim() : '')));
        return;
      }
      setState(() {
        _error = m;
        if (_mfaToken != null) { _code.clear(); if (m.toLowerCase().contains('took too long')) _mfaToken = null; } else { _pw.clear(); }
      });
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _server() async {
    final config = context.read<AppConfig>();
    final v = await promptText(context, 'Server address', hint: kDefaultBaseUrl, initial: config.baseUrl, ok: 'Save', keyboard: TextInputType.url);
    if (v != null) { await config.setBaseUrl(v); if (mounted) setState(() {}); }
  }

  Future<void> _forgot() async {
    final email = await Navigator.push<String>(context, MaterialPageRoute(builder: (_) => ForgotPasswordScreen(email: _id.text.trim().contains('@') ? _id.text.trim() : '')));
    if (email != null && mounted) setState(() { _id.text = email; _pw.clear(); _error = null; _note = 'Password changed. Sign in with your new password.'; });
  }

  @override
  Widget build(BuildContext context) {
    final config = context.read<AppConfig>();
    final mfa = _mfaToken != null;
    final hint = kAuthHint;
    return AuthScaffold(
      hero: true,
      title: mfa ? 'Two-step sign-in' : 'Sign in',
      subtitle: mfa ? 'Enter the 6-digit code from your authenticator app.' : null,
      onLogoLongPress: _server,
      children: [
        if (_note != null) AuthMessage(_note!, good: true),
        if (_error != null) AuthMessage(_error!),
        if (!mfa) ...[
          AutofillGroup(child: Column(children: [
            AuthField(controller: _id, label: 'Email or user name', icon: Icons.alternate_email_rounded, keyboard: TextInputType.emailAddress, autofill: const [AutofillHints.username, AutofillHints.email]),
            AuthField(controller: _pw, label: 'Password', icon: Icons.lock_outline_rounded, secret: true, autofill: const [AutofillHints.password], action: TextInputAction.done, onSubmitted: (_) => _submit()),
          ])),
          Align(alignment: Alignment.centerRight, child: TextButton(onPressed: _forgot, child: const Text('Forgot password?'))),
          const SizedBox(height: 6),
          AuthButton(label: 'Sign in', onPressed: _submit, busy: _busy),
          const AuthDivider('New to FiscalHub?'),
          OutlinedButton.icon(
            onPressed: () => Navigator.push(context, MaterialPageRoute(builder: (_) => const RegisterScreen())),
            style: OutlinedButton.styleFrom(padding: const EdgeInsets.symmetric(vertical: 15), shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(14))),
            icon: const Icon(Icons.add_business_outlined),
            label: const Text('Register your company', style: TextStyle(fontWeight: FontWeight.w700)),
          ),
        ] else ...[
          Center(child: Container(padding: const EdgeInsets.all(16), margin: const EdgeInsets.only(bottom: 18), decoration: BoxDecoration(color: Colors.white.withValues(alpha: 0.1), shape: BoxShape.circle, border: Border.all(color: Colors.white.withValues(alpha: 0.2))), child: const Icon(Icons.shield_outlined, color: Colors.white, size: 34))),
          TextField(
            controller: _code, autofocus: true, textAlign: TextAlign.center, keyboardType: TextInputType.visiblePassword, autofillHints: const [AutofillHints.oneTimeCode], onSubmitted: (_) => _submit(),
            style: const TextStyle(fontSize: 26, letterSpacing: 8, fontWeight: FontWeight.w700, fontFamily: 'monospace'),
            decoration: const InputDecoration(hintText: '000000'),
          ),
          const SizedBox(height: 8),
          Text('Lost your phone? Type one of your recovery codes instead.', textAlign: TextAlign.center, style: TextStyle(color: hint, fontSize: 13)),
          const SizedBox(height: 18),
          AuthButton(label: 'Verify and sign in', onPressed: _submit, busy: _busy, icon: Icons.verified_user_outlined),
          TextButton(onPressed: () => setState(() { _mfaToken = null; _error = null; _pw.clear(); }), child: const Text('Use a different account')),
        ],
        const SizedBox(height: 26),
        const TrustRow(),
        const SizedBox(height: 18),
        const LegalLinks(),
        const SizedBox(height: 10),
        Center(child: GestureDetector(onLongPress: _server, child: Text(config.baseUrl.replaceFirst(RegExp(r'^https?://'), ''), style: TextStyle(fontSize: 11, color: hint)))),
        const SizedBox(height: 12),
      ],
    );
  }
}
