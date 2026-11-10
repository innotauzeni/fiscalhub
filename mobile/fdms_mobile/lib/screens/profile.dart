import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:provider/provider.dart';

import '../core/api.dart';
import '../core/auth.dart';
import '../ui/kit.dart';
import '../modules/whatsapp.dart';
import 'auth_ui.dart';

/// "My profile": details, password, extra protection (authenticator app, passkeys) and where the person is signed in.
class ProfileScreen extends StatefulWidget {
  const ProfileScreen({super.key, this.forcedSecurity = false});
  /// The company requires extra protection and this person has not set it up: only this screen works until they do.
  final bool forcedSecurity;
  @override
  State<ProfileScreen> createState() => _ProfileScreenState();
}

class _ProfileScreenState extends State<ProfileScreen> {
  final _name = TextEditingController(), _surname = TextEditingController(), _phone = TextEditingController();
  Map<String, dynamic>? _p;
  Map<String, dynamic>? _sec;
  List<Map<String, dynamic>> _sessions = [];
  Object? _error;
  bool _busy = false;

  Api get api => context.read<Api>();

  @override
  void initState() { super.initState(); _load(); }

  Future<void> _load() async {
    try {
      final p = await api.getMap('/api/v1/profile');
      final sec = await api.getMap('/api/v1/security');
      List<Map<String, dynamic>> sessions = [];
      try { sessions = await api.getList('/api/v1/profile/sessions'); } catch (_) {}
      if (!mounted) return;
      setState(() { _p = p; _sec = sec; _sessions = sessions; _name.text = p['name'] ?? ''; _surname.text = p['surname'] ?? ''; _phone.text = p['phone'] ?? ''; _error = null; });
    } catch (e) { if (mounted) setState(() => _error = e); }
  }

  Future<void> _save() async {
    setState(() => _busy = true);
    await guarded(context, () async {
      await api.put('/api/v1/profile', body: {'name': _name.text.trim(), 'surname': _surname.text.trim(), 'phone': _phone.text.trim()});
      await _load();
    }, done: 'Profile saved.');
    if (mounted) setState(() => _busy = false);
  }

  Future<String?> _password(String title, String text) =>
      promptText(context, title, hint: text, obscure: true, ok: 'Continue');

  Future<void> _setupMfa() async {
    final auth = context.read<AuthController>();
    final setup = await guardedValue(context, () => api.post('/api/v1/security/mfa/setup', body: {}));
    if (setup == null || !mounted) return;
    final code = TextEditingController();
    final img = (setup['qrImage'] ?? '').toString();
    final bytes = img.contains(',') ? base64Decode(img.split(',').last) : null;
    final ok = await showDialog<bool>(
      context: context,
      barrierDismissible: false,
      builder: (d) => StatefulBuilder(builder: (d, set) => AlertDialog(
        title: const Text('Turn on two-step sign-in'),
        content: SingleChildScrollView(child: Column(mainAxisSize: MainAxisSize.min, children: [
          const Text('Open your authenticator app (Google Authenticator, Microsoft Authenticator, Authy) and add an account by scanning this picture, or type the key.'),
          const SizedBox(height: 12),
          if (bytes != null) Container(color: Colors.white, padding: const EdgeInsets.all(6), child: Image.memory(bytes, width: 180, height: 180)),
          const SizedBox(height: 8),
          SelectableText(setup['secret']?.toString() ?? '', style: const TextStyle(fontFamily: 'monospace', fontSize: 14)),
          const SizedBox(height: 12),
          TextField(controller: code, keyboardType: TextInputType.number, textAlign: TextAlign.center, style: const TextStyle(fontSize: 20, letterSpacing: 4), decoration: const InputDecoration(labelText: 'Code from the app', hintText: '000000')),
        ])),
        actions: [TextButton(onPressed: () => Navigator.pop(d, false), child: const Text('Cancel')), FilledButton(onPressed: () => Navigator.pop(d, true), child: const Text('Turn on'))],
      )),
    );
    if (ok != true || !mounted) return;
    final res = await guardedValue(context, () => api.post('/api/v1/security/mfa/enable', body: {'code': code.text.trim()}));
    if (res == null || !mounted) return;
    final codes = ((res['recoveryCodes'] ?? []) as List).map((e) => e.toString()).toList();
    await _showCodes(codes);
    if (auth.session?.mfaSetupRequired == true) await auth.refresh();
    await _load();
  }

  Future<void> _showCodes(List<String> codes) => showDialog<void>(
        context: context,
        barrierDismissible: false,
        builder: (d) => AlertDialog(
          title: const Text('Your recovery codes'),
          content: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
            const Text('Keep these somewhere safe. Each works once if you lose your phone. They will not be shown again.'),
            const SizedBox(height: 12),
            Wrap(spacing: 12, runSpacing: 6, children: [for (final c in codes) SelectableText(c, style: const TextStyle(fontFamily: 'monospace', fontSize: 15))]),
          ]),
          actions: [
            TextButton(onPressed: () { Clipboard.setData(ClipboardData(text: codes.join('\n'))); toast(context, 'Copied.'); }, child: const Text('Copy')),
            FilledButton(onPressed: () => Navigator.pop(d), child: const Text('I have saved them')),
          ],
        ),
      );

  Future<void> _mfaAction(String kind) async {
    final pw = await _password(kind == 'off' ? 'Turn off two-step sign-in' : 'New recovery codes', 'Your password');
    if (pw == null || pw.isEmpty || !mounted) return;
    if (kind == 'off') {
      await guarded(context, () => api.post('/api/v1/security/mfa/disable', body: {'password': pw}), done: 'Two-step sign-in is off.');
    } else {
      final r = await guardedValue(context, () => api.post('/api/v1/security/mfa/recovery-codes', body: {'password': pw}));
      if (r != null && mounted) await _showCodes(((r['recoveryCodes'] ?? []) as List).map((e) => e.toString()).toList());
    }
    await _load();
  }

  Future<void> _removePasskey(Map<String, dynamic> k) async {
    final pw = await _password('Remove passkey', 'Your password');
    if (pw == null || pw.isEmpty || !mounted) return;
    await guarded(context, () => api.post('/api/v1/security/passkeys/${k['id']}/remove', body: {'password': pw}), done: 'Passkey removed.');
    await _load();
  }

  /// The user name, or null when it is just the email.
  String? _ownUserName(Map p) {
    final u = (p['userName'] ?? '').toString();
    return u.isEmpty || u.toLowerCase() == (p['email'] ?? '').toString().toLowerCase() ? null : u;
  }

  Future<void> _changeUserName(Map p) async {
    final v = await promptText(context, 'Your user name', hint: '3-32 letters or digits, e.g. Inno. Leave empty to sign in with your email.', initial: _ownUserName(p) ?? '', ok: 'Save');
    if (v == null || !mounted) return;
    if (await guarded(context, () => api.put('/api/v1/profile/username', body: {'userName': v.trim()}), done: v.trim().isEmpty ? 'You sign in with your email.' : 'You can now sign in as ${v.trim()}.')) await _load();
  }

  Future<void> _changeEmail() async {
    final email = await promptText(context, 'Your new email', hint: 'you@company.com', keyboard: TextInputType.emailAddress, ok: 'Next');
    if (email == null || email.trim().isEmpty || !mounted) return;
    final pw = await _password('Change email', 'Your current password');
    if (pw == null || pw.isEmpty || !mounted) return;
    final r = await guardedValue(context, () => api.post('/api/v1/profile/email', body: {'newEmail': email.trim(), 'password': pw}));
    if (r == null || !mounted) return;
    final code = await promptText(context, (r['message'] ?? 'Enter the code we emailed to ${email.trim()}.').toString(), hint: '6-digit code', keyboard: TextInputType.number, ok: 'Confirm');
    if (code == null || code.trim().isEmpty || !mounted) return;
    if (await guarded(context, () => api.post('/api/v1/profile/email/confirm', body: {'newEmail': email.trim(), 'code': code.trim()}), done: 'Your email is now ${email.trim()}.')) await _load();
  }

  @override
  Widget build(BuildContext context) {
    final auth = context.watch<AuthController>();
    final p = _p;
    final mfa = _sec?['mfaEnabled'] == true;
    final passkeys = ((_sec?['passkeys'] ?? []) as List).cast<Map<String, dynamic>>();
    return PopScope(
      canPop: !widget.forcedSecurity,
      child: Scaffold(
        appBar: AppBar(title: const Text('My profile'), automaticallyImplyLeading: !widget.forcedSecurity, actions: [if (widget.forcedSecurity) TextButton(onPressed: () => auth.logout(), child: const Text('Sign out'))]),
        body: _error != null && p == null ? ErrorState(_error!, onRetry: _load) : p == null ? const Center(child: CircularProgressIndicator()) : RefreshIndicator(
          onRefresh: _load,
          child: ListView(padding: const EdgeInsets.all(16), children: [
            if (widget.forcedSecurity || auth.session?.mfaSetupRequired == true)
              Container(margin: const EdgeInsets.only(bottom: 14), padding: const EdgeInsets.all(14), decoration: BoxDecoration(color: Colors.amber.withValues(alpha: 0.15), borderRadius: BorderRadius.circular(12)),
                  child: const Text('Your company requires extra protection. Turn on two-step sign-in with an authenticator app below to continue to the rest of the app.', style: TextStyle(fontWeight: FontWeight.w600))),
            AppCard(child: Row(children: [
              CircleAvatar(radius: 28, backgroundColor: kBrand, child: Text(auth.user?.initials ?? '', style: const TextStyle(color: Colors.white, fontSize: 18, fontWeight: FontWeight.w700))),
              const SizedBox(width: 14),
              Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Text('${p['name'] ?? ''} ${p['surname'] ?? ''}', style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w700)),
                Text(p['email'] ?? '', style: TextStyle(color: Theme.of(context).hintColor)),
                Text(p['companyName'] ?? 'Platform operator', style: const TextStyle(fontSize: 13)),
                Wrap(spacing: 6, children: [for (final r in (p['roles'] ?? []) as List) Badge2(r.toString(), tone: Tone.info)]),
              ])),
            ])),
            const SectionTitle('Your details'),
            AppCard(child: Column(children: [
              TextField(controller: _name, decoration: const InputDecoration(labelText: 'First name')),
              const SizedBox(height: 12),
              TextField(controller: _surname, decoration: const InputDecoration(labelText: 'Surname')),
              const SizedBox(height: 12),
              TextField(controller: _phone, keyboardType: TextInputType.phone, decoration: const InputDecoration(labelText: 'Phone')),
              const SizedBox(height: 14),
              SizedBox(width: double.infinity, child: FilledButton(onPressed: _busy ? null : _save, child: const Text('Save details'))),
            ])),
            if (p['companyName'] != null) const WhatsAppLinkCard(),
            const SectionTitle('How you sign in'),
            AppCard(child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
              Row(children: [
                const Icon(Icons.person_pin_outlined),
                const SizedBox(width: 12),
                Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  const Text('User name', style: TextStyle(fontWeight: FontWeight.w700)),
                  Text(_ownUserName(p) ?? 'Not set: you sign in with your email', style: TextStyle(color: Theme.of(context).hintColor, fontSize: 13)),
                ])),
                TextButton(onPressed: () => _changeUserName(p), child: Text(_ownUserName(p) == null ? 'Set' : 'Change')),
              ]),
              const Divider(height: 22),
              Row(children: [
                const Icon(Icons.alternate_email_rounded),
                const SizedBox(width: 12),
                Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  const Text('Email', style: TextStyle(fontWeight: FontWeight.w700)),
                  Text((p['email'] ?? '').toString(), style: TextStyle(color: Theme.of(context).hintColor, fontSize: 13)),
                ])),
                TextButton(onPressed: _changeEmail, child: const Text('Change')),
              ]),
            ])),
            const SectionTitle('Extra protection'),
            AppCard(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Row(children: [
                const Icon(Icons.shield_outlined),
                const SizedBox(width: 10),
                const Expanded(child: Text('Two-step sign-in (authenticator app)', style: TextStyle(fontWeight: FontWeight.w700))),
                Badge2(mfa ? 'On' : 'Off', tone: mfa ? Tone.ok : Tone.warn),
              ]),
              const SizedBox(height: 6),
              Text(mfa ? 'After your password you type a 6-digit code. ${_sec?['recoveryCodesLeft'] ?? 0} recovery code(s) left.' : 'Protects your account even if someone learns your password.', style: TextStyle(color: Theme.of(context).hintColor, fontSize: 13)),
              const SizedBox(height: 10),
              if (mfa) Wrap(spacing: 8, children: [OutlinedButton(onPressed: () => _mfaAction('codes'), child: const Text('New recovery codes')), OutlinedButton(onPressed: () => _mfaAction('off'), style: OutlinedButton.styleFrom(foregroundColor: Colors.red.shade700), child: const Text('Turn off'))])
              else FilledButton(onPressed: _setupMfa, child: const Text('Turn on')),
              if (passkeys.isNotEmpty) ...[
                const Divider(height: 28),
                const Text('Passkeys', style: TextStyle(fontWeight: FontWeight.w700)),
                const Text('Passkeys are added from the web console in your browser.', style: TextStyle(fontSize: 12, color: Colors.grey)),
                for (final k in passkeys) ListTile(contentPadding: EdgeInsets.zero, leading: const Icon(Icons.key), title: Text(k['name'] ?? 'Passkey'), subtitle: Text(k['lastUsedAt'] != null ? 'Last used ${fmtDate(k['lastUsedAt'])}' : 'Not used yet'), trailing: IconButton(icon: const Icon(Icons.delete_outline), onPressed: () => _removePasskey(k))),
              ],
            ])),
            const SectionTitle('Password'),
            AppCard(onTap: () => Navigator.push(context, MaterialPageRoute(builder: (_) => const ChangePasswordScreen())), child: const Row(children: [Icon(Icons.lock_outline), SizedBox(width: 12), Expanded(child: Text('Change password')), Icon(Icons.chevron_right)])),
            if (_sessions.isNotEmpty) ...[
              const SectionTitle('Where you are signed in'),
              AppCard(padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4), child: Column(children: [
                for (final s in _sessions)
                  ListTile(
                    dense: true, leading: const Icon(Icons.devices_other),
                    title: Text('${s['browser'] ?? 'Unknown device'}${s['current'] == true ? '  (this one)' : ''}'),
                    subtitle: Text('${s['ipAddress'] ?? 'unknown address'} · ${fmtDateTime(s['createdAt'])}'),
                    trailing: s['current'] == true ? null : TextButton(onPressed: () async { await guarded(context, () => api.delete('/api/v1/profile/sessions/${s['id']}'), done: 'Signed out.'); _load(); }, child: const Text('Sign out')),
                  ),
              ])),
            ],
            const SizedBox(height: 24),
          ]),
        ),
      ),
    );
  }
}

/// Runs [work] and returns its JSON map, or null after showing the failure.
Future<Map<String, dynamic>?> guardedValue(BuildContext context, Future<dynamic> Function() work) async {
  try {
    final r = await work();
    return r is Map ? Map<String, dynamic>.from(r) : <String, dynamic>{};
  } catch (e) {
    if (context.mounted) toast(context, e, error: true);
    return null;
  }
}

class ChangePasswordScreen extends StatefulWidget {
  const ChangePasswordScreen({super.key, this.forced = false});
  final bool forced;
  @override
  State<ChangePasswordScreen> createState() => _ChangePasswordScreenState();
}

class _ChangePasswordScreenState extends State<ChangePasswordScreen> {
  final _cur = TextEditingController(), _new = TextEditingController(), _rep = TextEditingController();
  bool _busy = false, _show = false;
  String? _error;

  Future<void> _save() async {
    if (_new.text != _rep.text) { setState(() => _error = 'The two new passwords are not the same.'); return; }
    if (_new.text.length < 10) { setState(() => _error = 'Use at least 10 characters with an upper-case letter, a lower-case letter and a digit.'); return; }
    setState(() { _busy = true; _error = null; });
    final auth = context.read<AuthController>();
    try {
      final r = await context.read<Api>().post('/api/v1/auth/change-password', body: {'currentPassword': _cur.text, 'newPassword': _new.text});
      await auth.adopt(Map<String, dynamic>.from(r as Map));
      if (mounted && !widget.forced) { Navigator.pop(context); toast(context, 'Password changed. Other devices were signed out.'); }
    } catch (e) {
      setState(() => _error = e.toString());
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    if (widget.forced) {
      // first sign-in with a temporary password: the same look as the sign-in screens
      return PopScope(
        canPop: false,
        child: AuthScaffold(
          title: 'Choose your password',
          subtitle: 'Your administrator gave you a temporary password. Pick your own to continue.',
          children: [
            if (_error != null) AuthMessage(_error!),
            AuthField(controller: _cur, label: 'Temporary password', icon: Icons.key_rounded, secret: true, autofill: const [AutofillHints.password]),
            AuthField(controller: _new, label: 'New password', icon: Icons.lock_outline_rounded, secret: true, autofill: const [AutofillHints.newPassword], onChanged: (_) => setState(() {})),
            PasswordStrength(_new.text),
            AuthField(controller: _rep, label: 'New password again', icon: Icons.lock_reset_rounded, secret: true, action: TextInputAction.done, onSubmitted: (_) => _save(), onChanged: (_) => setState(() {})),
            const SizedBox(height: 4),
            AuthButton(label: 'Save and continue', onPressed: _save, busy: _busy, icon: Icons.check_rounded),
            const SizedBox(height: 10),
            Center(child: TextButton.icon(onPressed: () => context.read<AuthController>().logout(), icon: const Icon(Icons.logout_rounded, size: 18), label: const Text('Sign out'))),
          ],
        ),
      );
    }
    return Scaffold(
      appBar: AppBar(title: const Text('Change password')),
      body: ListView(padding: const EdgeInsets.all(20), children: [
        TextField(controller: _cur, obscureText: !_show, decoration: const InputDecoration(labelText: 'Current password', prefixIcon: Icon(Icons.key_rounded))),
        const SizedBox(height: 14),
        TextField(controller: _new, obscureText: !_show, onChanged: (_) => setState(() {}), decoration: const InputDecoration(labelText: 'New password', prefixIcon: Icon(Icons.lock_outline_rounded), helperText: 'At least 10 characters with an upper-case letter, a lower-case letter and a digit', helperMaxLines: 2)),
        const SizedBox(height: 14),
        TextField(controller: _rep, obscureText: !_show, decoration: InputDecoration(labelText: 'Repeat new password', prefixIcon: const Icon(Icons.lock_reset_rounded), suffixIcon: IconButton(icon: Icon(_show ? Icons.visibility_off : Icons.visibility), onPressed: () => setState(() => _show = !_show)))),
        if (_error != null) Padding(padding: const EdgeInsets.only(top: 14), child: Text(_error!, style: TextStyle(color: Colors.red.shade700))),
        const SizedBox(height: 20),
        FilledButton(onPressed: _busy ? null : _save, child: const Text('Save password')),
      ]),
    );
  }
}
