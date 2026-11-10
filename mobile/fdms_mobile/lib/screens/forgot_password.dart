import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/api.dart';
import 'auth_ui.dart';

/// Forgotten password, all in the app: email -> 6-digit code from the inbox + a new password. Pops with the email when the password was changed.
class ForgotPasswordScreen extends StatefulWidget {
  const ForgotPasswordScreen({super.key, this.email = ''});
  final String email;
  @override
  State<ForgotPasswordScreen> createState() => _ForgotPasswordScreenState();
}

class _ForgotPasswordScreenState extends State<ForgotPasswordScreen> {
  late final _email = TextEditingController(text: widget.email);
  final _code = TextEditingController(), _pw = TextEditingController(), _pw2 = TextEditingController();
  bool _sent = false, _busy = false, _tried = false;
  String? _error, _note;

  @override
  void dispose() { for (final c in [_email, _code, _pw, _pw2]) { c.dispose(); } super.dispose(); }

  /// The email or the user name a person signs in with.
  bool get _emailOk => _email.text.trim().length >= 3;

  Future<void> _send() async {
    if (!_emailOk) { setState(() { _tried = true; _error = 'Enter your email or user name.'; }); return; }
    setState(() { _busy = true; _error = null; });
    try {
      final r = await context.read<Api>().anonymousPost('/api/v1/auth/forgot-password', {'email': _email.text.trim()});
      if (mounted) setState(() { _sent = true; _tried = false; _note = (r is Map ? r['message'] : null)?.toString() ?? 'Check your email for the code.'; });
    } catch (e) {
      if (mounted) setState(() => _error = e.toString());
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _reset() async {
    final rulesOk = passwordRules(_pw.text).every((r) => r.$2);
    if (_code.text.trim().length < 6 || !rulesOk || _pw.text != _pw2.text) { setState(() { _tried = true; _error = null; }); return; }
    setState(() { _busy = true; _error = null; });
    try {
      await context.read<Api>().anonymousPost('/api/v1/auth/reset-password', {'email': _email.text.trim(), 'code': _code.text.trim(), 'newPassword': _pw.text});
      if (mounted) Navigator.pop(context, _email.text.trim());
    } catch (e) {
      if (mounted) setState(() => _error = e.toString());
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final hint = kAuthHint;
    return AuthScaffold(
      title: _sent ? 'Check your email' : 'Forgot your password?',
      subtitle: _sent ? 'We sent a 6-digit code to your email. Enter it with your new password.' : 'No problem. Enter your email or user name and we will email you a code to choose a new one.',
      children: [
        if (_error != null) AuthMessage(_error!),
        if (!_sent) ...[
          AuthField(controller: _email, label: 'Email or user name', icon: Icons.alternate_email_rounded, keyboard: TextInputType.emailAddress, autofill: const [AutofillHints.username, AutofillHints.email], action: TextInputAction.done, onSubmitted: (_) => _send(), autofocus: widget.email.isEmpty, error: _tried && !_emailOk ? 'Enter your email or user name' : null),
          const SizedBox(height: 4),
          AuthButton(label: 'Send me a code', onPressed: _send, busy: _busy, icon: Icons.send_rounded),
        ] else ...[
          if (_note != null) AuthMessage(_note!, good: true),
          TextField(
            controller: _code, autofocus: true, textAlign: TextAlign.center, keyboardType: TextInputType.number, maxLength: 6, autofillHints: const [AutofillHints.oneTimeCode], onChanged: (_) => setState(() {}),
            style: const TextStyle(fontSize: 26, letterSpacing: 8, fontWeight: FontWeight.w700, fontFamily: 'monospace'),
            decoration: InputDecoration(hintText: '000000', counterText: '', errorText: _tried && _code.text.trim().length < 6 ? 'Enter the 6 digits from the email' : null),
          ),
          const SizedBox(height: 14),
          AuthField(controller: _pw, label: 'New password', icon: Icons.lock_outline_rounded, secret: true, autofill: const [AutofillHints.newPassword], onChanged: (_) => setState(() {})),
          PasswordStrength(_pw.text),
          AuthField(controller: _pw2, label: 'New password again', icon: Icons.lock_reset_rounded, secret: true, action: TextInputAction.done, onSubmitted: (_) => _reset(), onChanged: (_) => setState(() {}), error: _tried && _pw.text != _pw2.text ? 'The passwords do not match' : null),
          const SizedBox(height: 4),
          AuthButton(label: 'Change password', onPressed: _reset, busy: _busy, icon: Icons.check_rounded),
          const SizedBox(height: 8),
          Wrap(alignment: WrapAlignment.center, crossAxisAlignment: WrapCrossAlignment.center, children: [
            Text('No email after a minute?', style: TextStyle(color: hint, fontSize: 13)),
            TextButton(onPressed: _busy ? null : _send, child: const Text('Send a new code')),
            TextButton(onPressed: _busy ? null : () => setState(() { _sent = false; _code.clear(); _error = null; }), child: const Text('Change email')),
          ]),
        ],
        const SizedBox(height: 20),
        Row(children: [
          Icon(Icons.info_outline, size: 16, color: Colors.white.withValues(alpha: 0.7)),
          const SizedBox(width: 8),
          Expanded(child: Text('After the change you are signed out on every phone and browser.', style: TextStyle(color: hint, fontSize: 12))),
        ]),
        const SizedBox(height: 12),
      ],
    );
  }
}
