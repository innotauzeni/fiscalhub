import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import 'package:url_launcher/url_launcher.dart';

import '../core/api.dart';
import '../core/models.dart';
import '../ui/kit.dart';

/// Terms and conditions, privacy policy and the developer's details. The words come from the gateway (the operator edits them), so the app, the website and the registration page all say the same.
Future<Json> loadLegal(Api api) async {
  try { return await api.getMap('/api/v1/public/legal'); } catch (_) { return {}; }
}

/// Renders the simple text format used for the legal documents: "# Heading" lines and paragraphs separated by blank lines.
class LegalBody extends StatelessWidget {
  const LegalBody(this.text, {super.key});
  final String text;
  @override
  Widget build(BuildContext context) {
    final blocks = text.replaceAll('\r', '').split(RegExp(r'\n\s*\n'));
    final out = <Widget>[];
    for (final b in blocks) {
      final lines = b.trim().split('\n');
      var i = 0;
      while (i < lines.length && lines[i].startsWith('#')) {
        out.add(Padding(padding: const EdgeInsets.only(top: 18, bottom: 6), child: Text(lines[i].replaceFirst(RegExp(r'^#+\s*'), ''), style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w800))));
        i++;
      }
      final rest = lines.sublist(i).join('\n').trim();
      if (rest.isNotEmpty) out.add(Padding(padding: const EdgeInsets.only(bottom: 4), child: SelectableText(rest, style: const TextStyle(fontSize: 14.5, height: 1.5))));
    }
    return Column(crossAxisAlignment: CrossAxisAlignment.start, children: out);
  }
}

class LegalDocScreen extends StatelessWidget {
  const LegalDocScreen({super.key, required this.which});
  /// terms or privacy
  final String which;
  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    return Scaffold(
      appBar: AppBar(title: Text(which == 'terms' ? 'Terms and conditions' : 'Privacy policy')),
      body: Loader<Json>(
        load: () => loadLegal(api),
        builder: (context, d, _) {
          final doc = Map<String, dynamic>.from((d[which] ?? {}) as Map);
          final body = (doc['body'] ?? '').toString();
          return ListView(padding: const EdgeInsets.all(20), children: [
            Text(doc['title']?.toString() ?? '', style: const TextStyle(fontSize: 22, fontWeight: FontWeight.w800)),
            if (doc['updatedAt'] != null) Padding(padding: const EdgeInsets.only(top: 4), child: Text('Last updated ${fmtDate(doc['updatedAt'])}', style: TextStyle(fontSize: 12, color: Theme.of(context).hintColor))),
            const SizedBox(height: 8),
            if (body.isEmpty) const EmptyState('This document could not be loaded. Check your connection and try again.') else LegalBody(body),
            const SizedBox(height: 32),
          ]);
        },
      ),
    );
  }
}

class DeveloperScreen extends StatelessWidget {
  const DeveloperScreen({super.key});

  Future<void> _open(BuildContext context, String url) async {
    final ok = await launchUrl(Uri.parse(url), mode: LaunchMode.externalApplication);
    if (!ok && context.mounted) toast(context, 'Could not open that on this phone.', error: true);
  }

  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    return Scaffold(
      appBar: AppBar(title: const Text('Developer details')),
      body: Loader<Json>(
        load: () => loadLegal(api),
        builder: (context, d, _) {
          final dev = Map<String, dynamic>.from((d['developer'] ?? {}) as Map);
          String s(String k) => (dev[k] ?? '').toString();
          final phone = s('phone'), wa = s('whatsApp').replaceAll(RegExp(r'[^0-9]'), ''), email = s('email'), web = s('website');
          Widget tile(IconData icon, String title, String value, String url) => Padding(padding: const EdgeInsets.only(bottom: 10), child: AppCard(onTap: () => _open(context, url), padding: const EdgeInsets.all(14), child: Row(children: [
                CircleAvatar(backgroundColor: kBrand.withValues(alpha: 0.1), child: Icon(icon, color: kBrand)),
                const SizedBox(width: 14),
                Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [Text(title, style: TextStyle(fontSize: 12, color: Theme.of(context).hintColor)), Text(value, style: const TextStyle(fontWeight: FontWeight.w600))])),
                const Icon(Icons.chevron_right, size: 18),
              ])));
          return ListView(padding: const EdgeInsets.all(16), children: [
            Container(
              padding: const EdgeInsets.all(20),
              decoration: BoxDecoration(gradient: const LinearGradient(colors: [Color(0xFF2563EB), Color(0xFF7C3AED)], begin: Alignment.topLeft, end: Alignment.bottomRight), borderRadius: BorderRadius.circular(22)),
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Container(width: 52, height: 52, decoration: BoxDecoration(color: Colors.white.withValues(alpha: 0.2), borderRadius: BorderRadius.circular(16)), alignment: Alignment.center, child: const Icon(Icons.code, color: Colors.white, size: 28)),
                const SizedBox(height: 14),
                Text(s('name'), style: const TextStyle(color: Colors.white, fontSize: 24, fontWeight: FontWeight.w800)),
                if (s('about').isNotEmpty) Padding(padding: const EdgeInsets.only(top: 8), child: Text(s('about'), style: const TextStyle(color: Colors.white70, height: 1.4))),
              ]),
            ),
            const SectionTitle('Get in touch'),
            if (email.isNotEmpty) tile(Icons.mail_outline, 'Email', email, 'mailto:$email'),
            if (phone.isNotEmpty) tile(Icons.call_outlined, 'Phone', phone, 'tel:${phone.replaceAll(' ', '')}'),
            if (wa.isNotEmpty) tile(Icons.chat_outlined, 'WhatsApp', s('whatsApp'), 'https://wa.me/$wa'),
            if (web.isNotEmpty) tile(Icons.language, 'Website', web, web),
            if (s('address').isNotEmpty) Padding(padding: const EdgeInsets.only(bottom: 10), child: AppCard(padding: const EdgeInsets.all(14), child: Row(children: [CircleAvatar(backgroundColor: kBrand.withValues(alpha: 0.1), child: const Icon(Icons.place_outlined, color: kBrand)), const SizedBox(width: 14), Expanded(child: Text(s('address'), style: const TextStyle(fontWeight: FontWeight.w600)))]))),
            const SectionTitle('Documents'),
            AppCard(padding: EdgeInsets.zero, child: Column(children: [
              ListTile(leading: const Icon(Icons.gavel_outlined), title: const Text('Terms and conditions'), trailing: const Icon(Icons.chevron_right), onTap: () => Navigator.push(context, MaterialPageRoute(builder: (_) => const LegalDocScreen(which: 'terms')))),
              const Divider(height: 1),
              ListTile(leading: const Icon(Icons.privacy_tip_outlined), title: const Text('Privacy policy'), trailing: const Icon(Icons.chevron_right), onTap: () => Navigator.push(context, MaterialPageRoute(builder: (_) => const LegalDocScreen(which: 'privacy')))),
            ])),
            const SizedBox(height: 24),
          ]);
        },
      ),
    );
  }
}

/// The links shown on the sign-in page and in the menu.
class LegalLinks extends StatelessWidget {
  const LegalLinks({super.key, this.center = true});
  final bool center;
  @override
  Widget build(BuildContext context) {
    void go(Widget w) => Navigator.push(context, MaterialPageRoute(builder: (_) => w));
    final style = TextStyle(fontSize: 12, color: Theme.of(context).colorScheme.primary, fontWeight: FontWeight.w600);
    return Wrap(alignment: center ? WrapAlignment.center : WrapAlignment.start, spacing: 14, runSpacing: 4, children: [
      InkWell(onTap: () => go(const LegalDocScreen(which: 'terms')), child: Text('Terms', style: style)),
      InkWell(onTap: () => go(const LegalDocScreen(which: 'privacy')), child: Text('Privacy', style: style)),
      InkWell(onTap: () => go(const DeveloperScreen()), child: Text('Developer', style: style)),
    ]);
  }
}
