import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/api.dart';
import '../ui/kit.dart';
import '../ui/lists.dart';
import '../ui/premium.dart';
import '../modules/approvals.dart' show noteIcon;

/// Alerts from the gateway (certificates, rejections, payments…), newest first and grouped by day; unread ones are marked.
class NotificationsScreen extends StatelessWidget {
  const NotificationsScreen({super.key, this.onOpen});
  /// Opens the screen a notification links to ("/billing", "/approvals" ...) in the shell.
  final void Function(String link)? onOpen;

  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    return Scaffold(
      appBar: AppBar(title: const Text('Notifications')),
      body: Loader<Map<String, dynamic>>(
        load: () => api.getMap('/api/v1/notifications', query: {'take': 100}),
        builder: (context, data, reload) {
          final items = ((data['items'] ?? []) as List).cast<Map<String, dynamic>>();
          final unread = (data['unread'] ?? 0) as num;
          final groups = <String, List<Map<String, dynamic>>>{};
          for (final n in items) { (groups[dayHeading(parseDate(n['createdAt']))] ??= []).add(n); }
          Tone tone(Map n) => n['severity'] == 'bad' ? Tone.bad : n['severity'] == 'warn' ? Tone.warn : Tone.info;
          IconData icon(Map n) => noteIcon(n);
          return ListView(padding: const EdgeInsets.fromLTRB(16, 8, 16, 24), children: [
            if (items.isEmpty) const BigEmpty(icon: Icons.notifications_none_rounded, title: 'You are all caught up', message: 'Invoices to pay, approvals, certificate reminders and payment news appear here.'),
            if (unread > 0)
              Padding(padding: const EdgeInsets.only(top: 4), child: Wrap(alignment: WrapAlignment.spaceBetween, crossAxisAlignment: WrapCrossAlignment.center, spacing: 8, runSpacing: 4, children: [
                Pill('$unread unread', tone: Tone.info),
                TextButton.icon(onPressed: () async { await api.post('/api/v1/notifications/read', body: {}); await reload(); }, icon: const Icon(Icons.done_all_rounded, size: 18), label: const Text('Mark all read')),
              ])),
            for (final g in groups.entries) ...[
              ListHeader(g.key, trailing: '${g.value.length}'),
              ListPanel(children: [
                for (final n in g.value)
                  ListRow(
                    leading: Avatar(n['title']?.toString() ?? '', icon: icon(n), color: toneColor(tone(n))),
                    title: (n['title'] ?? '').toString(),
                    subtitle: [if ((n['body'] ?? '').toString().isNotEmpty) n['body'].toString(), fmtDateTime(n['createdAt'])].join('\n'),
                    pills: [if (n['read'] != true) ('New', Tone.info)],
                    chevron: onOpen != null && (n['link'] ?? '').toString().isNotEmpty,
                    onTap: () async {
                      if (n['read'] != true) { try { await api.post('/api/v1/notifications/read', body: {'ids': [n['id']]}); } catch (_) {} }
                      final link = (n['link'] ?? '').toString();
                      if (link.isNotEmpty && onOpen != null && context.mounted) { Navigator.pop(context); onOpen!(link); } else { await reload(); }
                    },
                  ),
              ]),
            ],
          ]);
        },
      ),
    );
  }
}
