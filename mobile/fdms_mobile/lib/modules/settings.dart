import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/api.dart';
import '../core/models.dart';
import '../screens/profile.dart' show guardedValue;
import '../ui/kit.dart';
import 'resource.dart';

String _s(Json j, String k) => (j[k] ?? '').toString();

/// Platform settings for the operator, all in one place: public addresses, the company details printed on subscription invoices, payment gateways, the AI assistant, and the
/// terms, privacy policy and developer details shown in every app.
class SettingsScreen extends StatelessWidget {
  const SettingsScreen({super.key});

  Widget _tile(BuildContext context, IconData icon, String title, String sub, VoidCallback onTap) => Padding(
        padding: const EdgeInsets.only(bottom: 10),
        child: AppCard(onTap: onTap, padding: const EdgeInsets.all(14), child: Row(children: [
          CircleAvatar(backgroundColor: kBrand.withValues(alpha: 0.1), child: Icon(icon, color: kBrand)),
          const SizedBox(width: 14),
          Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [Text(title, style: const TextStyle(fontWeight: FontWeight.w700)), Text(sub, style: TextStyle(fontSize: 12, color: Theme.of(context).hintColor))])),
          const Icon(Icons.chevron_right, size: 18),
        ])),
      );

  void _go(BuildContext context, Widget w) => Navigator.push(context, MaterialPageRoute(builder: (_) => w));

  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    return ListView(padding: const EdgeInsets.all(16), children: [
      _tile(context, Icons.public, 'Public addresses', 'The API address and the web console address used in links and emails', () async {
        final s = await api.getMap('/api/v1/settings');
        if (!context.mounted) return;
        _go(context, FormPage(title: 'Public addresses', intro: 'Leave empty to use the address the request came from.', initial: s, fields: const [Fld('publicApiUrl', 'API address', kind: Kind.text, help: 'https://fiscalapi.example.com'), Fld('publicAppUrl', 'Web console address', help: 'https://fiscalapi.example.com/app')],
            onSave: (v) async { await api.put('/api/v1/settings', body: {'publicApiUrl': (v['publicApiUrl'] ?? '').toString(), 'publicAppUrl': (v['publicAppUrl'] ?? '').toString()}); }));
      }),
      _tile(context, Icons.mail_outline, 'Email', 'The mail server, and your address for alerts about registrations, payments and backups', () async {
        final s = await api.getMap('/api/v1/settings/email');
        if (!context.mounted) return;
        _go(context, FormPage(
          title: 'Email',
          intro: '${s['configured'] == true ? 'Email is being sent.' : 'Email is not set up yet.'} Port 587 uses STARTTLS, 465 encrypts from the start. The password is never shown: leave it empty to keep the saved one.',
          initial: {...s, 'password': null},
          fields: const [
            Fld('enabled', 'Send email', kind: Kind.boolean),
            Fld('host', 'Mail server', help: 'mail.yourcompany.com'),
            Fld('port', 'Port', kind: Kind.integer, nullIfEmpty: false),
            Fld('enableSsl', 'Encrypted', kind: Kind.boolean),
            Fld('user', 'User name'),
            Fld('password', 'Password', kind: Kind.password),
            Fld('fromAddress', 'Sender address', kind: Kind.email, help: 'noreply@yourcompany.com'),
            Fld('fromName', 'Sender name', help: 'FDMS Gateway'),
            Fld('notifyEmails', 'Alert addresses (your email)', kind: Kind.multiline, help: 'you@yourcompany.com, finance@yourcompany.com. New registrations, payments to confirm, go-live requests and failed backups are emailed here.'),
          ],
          onSave: (v) async {
            final pw = (v['password'] ?? '').toString();
            await api.put('/api/v1/settings/email', body: {...v, 'port': v['port'] ?? 587, 'enabled': v['enabled'] == true, 'enableSsl': v['enableSsl'] == true, 'password': pw.isEmpty ? null : pw, 'clearPassword': false});
          },
        ));
      }),
      _tile(context, Icons.outgoing_mail, 'Send a test email', 'Check that the saved mail settings work', () async {
        final to = await promptText(context, 'Send a test email to', hint: 'you@yourcompany.com', ok: 'Send', keyboard: TextInputType.emailAddress);
        if (to == null || to.trim().isEmpty || !context.mounted) return;
        final r = await guardedValue(context, () => api.post('/api/v1/settings/email/test', body: {'to': to.trim()}));
        if (r != null && context.mounted) toast(context, (r['message'] ?? '').toString(), error: r['ok'] != true);
      }),
      _tile(context, Icons.business_outlined, 'Billing details', 'Your company details, bank account and terms printed on subscription invoices', () async {
        final info = await api.getMap('/api/v1/settings/billing');
        if (!context.mounted) return;
        _go(context, FormPage(title: 'Billing details', initial: Map<String, dynamic>.from((info['profile'] ?? {}) as Map), fields: const [
          Fld('companyName', 'Company name', required: true), Fld('address', 'Address', kind: Kind.multiline), Fld('taxNumber', 'Tax number'), Fld('email', 'Email', kind: Kind.email), Fld('phone', 'Phone', kind: Kind.phone), Fld('website', 'Website'),
          Fld('accent', 'Invoice colour', help: '#2563eb'), Fld('invoicePrefix', 'Invoice number prefix'), Fld('taxPercent', 'Tax on subscriptions (%)', kind: Kind.number, nullIfEmpty: false),
          Fld('bankName', 'Bank'), Fld('accountName', 'Account name'), Fld('accountNumber', 'Account number'), Fld('branchCode', 'Branch code'), Fld('swift', 'SWIFT'),
          Fld('paymentInstructions', 'How to pay', kind: Kind.multiline), Fld('terms', 'Terms', kind: Kind.multiline), Fld('footer', 'Footer', kind: Kind.multiline),
        ], onSave: (v) async { await api.put('/api/v1/settings/billing', body: {...v, 'taxPercent': v['taxPercent'] ?? 0}); }));
      }),
      _tile(context, Icons.credit_card, 'Payment gateways', 'Paynow, EcoCash, Stripe, bank transfer and others', () => _go(context, const GatewaysScreen())),
      _tile(context, Icons.auto_awesome, 'AI assistant', 'The AI key and what it may be used for', () async {
        final c = await api.getMap('/api/v1/admin/ai');
        if (!context.mounted) return;
        _go(context, AiSettingsPage(initial: c));
      }),
      _tile(context, Icons.gavel_outlined, 'Terms, privacy and developer details', 'The words every app and the registration page show', () async {
        final l = await api.getMap('/api/v1/public/legal');
        if (!context.mounted) return;
        final dev = Map<String, dynamic>.from((l['developer'] ?? {}) as Map);
        _go(context, FormPage(
          title: 'Terms, privacy, developer', intro: 'Use "# Heading" lines and blank lines between paragraphs. Clear a box to go back to the starting text.',
          initial: {'terms': (l['terms'] as Map?)?['body'], 'privacy': (l['privacy'] as Map?)?['body'], 'devName': dev['name'], 'about': dev['about'], 'email': dev['email'], 'phone': dev['phone'], 'whatsApp': dev['whatsApp'], 'website': dev['website'], 'address': dev['address']},
          fields: const [
            Fld('terms', 'Terms and conditions', kind: Kind.multiline, nullIfEmpty: false), Fld('privacy', 'Privacy policy', kind: Kind.multiline, nullIfEmpty: false),
            Fld('devName', 'Developer or company name', required: true), Fld('about', 'About', kind: Kind.multiline), Fld('email', 'Email', kind: Kind.email), Fld('phone', 'Phone', kind: Kind.phone), Fld('whatsApp', 'WhatsApp number', kind: Kind.phone), Fld('website', 'Website'), Fld('address', 'Address'),
          ],
          onSave: (v) async { await api.put('/api/v1/settings/legal', body: {'terms': v['terms'] ?? '', 'privacy': v['privacy'] ?? '', 'developer': {'name': v['devName'], 'about': v['about'], 'email': v['email'], 'phone': v['phone'], 'whatsApp': v['whatsApp'], 'website': v['website'], 'address': v['address']}}); },
        ));
      }),
    ]);
  }
}

class GatewaysScreen extends StatelessWidget {
  const GatewaysScreen({super.key});
  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    final key = GlobalKey<LoaderState<List<Json>>>();
    return Scaffold(
      appBar: AppBar(title: const Text('Payment gateways')),
      body: Loader<List<Json>>(
        key: key,
        load: () => api.getList('/api/v1/settings/gateways'),
        builder: (context, items, _) => ListView(padding: const EdgeInsets.all(16), children: [
          for (final g in items)
            Padding(padding: const EdgeInsets.only(bottom: 10), child: AppCard(
              onTap: () async {
                final fields = <Fld>[
                  const Fld('enabled', 'Switched on', kind: Kind.boolean),
                  for (final f in (g['fields'] ?? []) as List) Fld('v_${f['key']}', '${f['label']}${f['secret'] == true ? ' (secret)' : ''}', kind: f['secret'] == true ? Kind.password : Kind.text, help: f['secret'] == true ? (f['hasValue'] == true ? 'Saved. Leave empty to keep it.' : f['hint']?.toString()) : f['hint']?.toString(), initial: f['secret'] == true ? null : f['value']),
                ];
                final saved = await Navigator.push<bool>(context, MaterialPageRoute(builder: (_) => FormPage(
                  title: _s(g, 'name'), intro: [_s(g, 'description'), if (g['callbackUrl'] != null) 'Callback address: ${g['callbackUrl']}'].where((x) => x.isNotEmpty).join('\n'), fields: fields, initial: {'enabled': g['enabled']},
                  onSave: (v) async => await api.put('/api/v1/settings/gateways/${g['code']}', body: {'enabled': v['enabled'], 'values': {for (final f in (g['fields'] ?? []) as List) f['key']: v['v_${f['key']}']}, 'clearSecrets': false}),
                )));
                if (saved == true) key.currentState?.reload();
              },
              child: Row(children: [
                Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [Text(_s(g, 'name'), style: const TextStyle(fontWeight: FontWeight.w700)), Text(_s(g, 'description'), maxLines: 2, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 12, color: Theme.of(context).hintColor))])),
                const SizedBox(width: 8),
                Badge2(g['enabled'] == true ? 'On' : 'Off', tone: g['enabled'] == true ? Tone.ok : Tone.neutral),
              ]),
            )),
        ]),
      ),
    );
  }
}

class AiSettingsPage extends StatefulWidget {
  const AiSettingsPage({super.key, required this.initial, this.embedded = false});
  final Json initial;
  final bool embedded;
  @override
  State<AiSettingsPage> createState() => _AiSettingsPageState();
}

class _AiSettingsPageState extends State<AiSettingsPage> {
  late Json _c = widget.initial;
  String? _test;
  bool _testing = false;

  Future<void> _testKey() async {
    setState(() { _testing = true; _test = null; });
    final r = await guardedValue(context, () => context.read<Api>().post('/api/v1/admin/ai/test'));
    if (mounted) setState(() { _testing = false; _test = r == null ? null : '${r['ok'] == true ? 'Works' : 'Problem'}: ${r['message']}'; });
  }

  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    final usage = (_c['usage'] ?? {}) as Map;
    return FormPage(
      title: 'AI assistant', popOnSave: false, embedded: widget.embedded,
      intro: '${_c['hasKey'] == true ? 'A key is saved (${_c['keyHint']}).' : 'No key saved yet.'} Today: ${usage['chats'] ?? 0} chats, ${usage['reports'] ?? 0} reports, ${usage['failures'] ?? 0} failures.${_test != null ? '\n$_test' : ''}',
      initial: _c,
      fields: const [Fld('apiKey', 'Gemini API key', kind: Kind.password, help: 'Leave empty to keep the saved key'), Fld('model', 'Model', initial: 'gemini-2.5-flash'), Fld('chatEnabled', 'Sign-up assistant on', kind: Kind.boolean), Fld('reportsEnabled', 'AI report builder on', kind: Kind.boolean), Fld('summariesEnabled', 'Report summaries on', kind: Kind.boolean), Fld('dailyChatLimit', 'Chats per address per day', kind: Kind.integer, nullIfEmpty: false), Fld('dailyReportLimitPerCompany', 'Reports per company per day', kind: Kind.integer, nullIfEmpty: false)],
      onSave: (v) async {
        await api.put('/api/v1/admin/ai', body: {'apiKey': v['apiKey'], 'clearKey': false, 'model': v['model'], 'chatEnabled': v['chatEnabled'], 'reportsEnabled': v['reportsEnabled'], 'summariesEnabled': v['summariesEnabled'], 'dailyChatLimit': v['dailyChatLimit'] ?? 30, 'dailyReportLimitPerCompany': v['dailyReportLimitPerCompany'] ?? 50});
        final fresh = await api.getMap('/api/v1/admin/ai');
        if (mounted) setState(() => _c = fresh);
        await _testKey();
      },
      saveLabel: _testing ? 'Testing…' : 'Save and test',
    );
  }
}

/// The AI settings as a menu page: loads the current settings, then shows the form.
class AiSettingsLoader extends StatelessWidget {
  const AiSettingsLoader({super.key});
  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    return Loader<Json>(load: () => api.getMap('/api/v1/admin/ai'), builder: (context, c, _) => AiSettingsPage(initial: c, embedded: true));
  }
}
