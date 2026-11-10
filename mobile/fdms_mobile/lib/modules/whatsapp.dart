import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import 'package:url_launcher/url_launcher.dart';

import '../core/api.dart';
import '../core/models.dart';
import '../ui/kit.dart';

/// Link your WhatsApp number to FiscalHub, then fiscalise and get alerts by chatting with the FiscalHub number. Hidden when WhatsApp is not set up on the gateway.
class WhatsAppLinkCard extends StatefulWidget {
  const WhatsAppLinkCard({super.key});
  @override
  State<WhatsAppLinkCard> createState() => _WhatsAppLinkCardState();
}

class _WhatsAppLinkCardState extends State<WhatsAppLinkCard> with WidgetsBindingObserver {
  Json? _w;
  bool _busy = false;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    _load();
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    super.dispose();
  }

  /// Back from WhatsApp after sending the code: see whether the number is linked now.
  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state == AppLifecycleState.resumed && _w?['code'] != null) _load(keepCode: true);
  }

  Future<void> _load({bool keepCode = false}) async {
    try {
      final w = await context.read<Api>().getMap('/api/v1/profile/whatsapp');
      if (!mounted) return;
      final wasWaiting = _w?['code'] != null;
      setState(() => _w = keepCode && w['linkedNumber'] == null ? {...w, 'code': _w?['code'], 'waLink': _w?['waLink']} : w);
      if (wasWaiting && w['linkedNumber'] != null) toast(context, 'WhatsApp is linked. Say hi to FiscalHub on WhatsApp.');
    } catch (_) {
      if (mounted) setState(() => _w = null);
    }
  }

  Future<void> _start() async {
    final api = context.read<Api>();
    setState(() => _busy = true);
    await guarded(context, () async {
      final w = await api.post('/api/v1/profile/whatsapp/link') as Map;
      setState(() => _w = Map<String, dynamic>.from(w));
      final link = w['waLink']?.toString();
      if (link != null) await launchUrl(Uri.parse(link), mode: LaunchMode.externalApplication);
    });
    if (mounted) setState(() => _busy = false);
  }

  Future<void> _alerts(bool on) async {
    final api = context.read<Api>();
    await guarded(context, () async {
      final w = await api.put('/api/v1/profile/whatsapp', body: {'alerts': on, 'deviceId': _w?['deviceId']}) as Map;
      setState(() => _w = Map<String, dynamic>.from(w));
    });
  }

  Future<void> _unlink() async {
    if (!await confirm(context, 'Unlink this WhatsApp number? The assistant will stop answering it.', ok: 'Unlink', danger: true)) return;
    if (!mounted) return;
    final api = context.read<Api>();
    await guarded(context, () async {
      final w = await api.delete('/api/v1/profile/whatsapp') as Map;
      setState(() => _w = Map<String, dynamic>.from(w));
    });
  }

  @override
  Widget build(BuildContext context) {
    final w = _w;
    if (w == null || (w['available'] != true && w['linkedNumber'] == null)) return const SizedBox.shrink();
    final hint = Theme.of(context).hintColor;
    final bot = w['botNumber']?.toString();
    final linked = w['linkedNumber']?.toString();
    return Padding(
      padding: const EdgeInsets.only(top: 8),
      child: AppCard(
        child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
          Row(children: [
            const Icon(Icons.chat_rounded, color: Color(0xFF25D366)),
            const SizedBox(width: 10),
            const Expanded(child: Text('WhatsApp', style: TextStyle(fontWeight: FontWeight.w700, fontSize: 16))),
            if (linked != null) const Badge2('Linked', tone: Tone.ok),
          ]),
          const SizedBox(height: 8),
          if (linked != null) ...[
            Text('Linked to +$linked. Message +$bot and say "hi" to issue invoices, check today\'s sales or the fiscal day, and get PDFs.', style: TextStyle(color: hint)),
            SwitchListTile(contentPadding: EdgeInsets.zero, value: w['alerts'] == true, onChanged: _alerts, title: const Text('Send my alerts to WhatsApp too')),
            Wrap(spacing: 8, runSpacing: 8, children: [
              if (bot != null) FilledButton.icon(onPressed: () => launchUrl(Uri.parse('https://wa.me/$bot?text=hi'), mode: LaunchMode.externalApplication), icon: const Icon(Icons.open_in_new_rounded, size: 18), label: const Text('Open the chat')),
              TextButton(onPressed: _unlink, child: Text('Unlink', style: TextStyle(color: toneColor(Tone.bad)))),
            ]),
          ] else if (w['code'] != null) ...[
            Text('Send LINK ${w['code']} to +$bot from your WhatsApp (the button fills it in). The code works for 10 minutes.', style: TextStyle(color: hint)),
            const SizedBox(height: 10),
            Wrap(spacing: 8, runSpacing: 8, children: [
              FilledButton.icon(onPressed: () => launchUrl(Uri.parse(w['waLink'].toString()), mode: LaunchMode.externalApplication), icon: const Icon(Icons.send_rounded, size: 18), label: const Text('Send the code')),
              OutlinedButton(onPressed: () => _load(keepCode: true), child: const Text("I've sent it")),
            ]),
          ] else ...[
            Text('Issue fiscal invoices, check the fiscal day and get PDFs by chatting with FiscalHub on WhatsApp, and get your alerts there.', style: TextStyle(color: hint)),
            const SizedBox(height: 10),
            FilledButton.icon(onPressed: _busy ? null : _start, icon: const Icon(Icons.link_rounded, size: 18), label: const Text('Link WhatsApp')),
          ],
        ]),
      ),
    );
  }
}
