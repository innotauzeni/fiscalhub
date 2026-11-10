import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:provider/provider.dart';

import '../core/api.dart';
import '../core/config.dart';
import '../ui/kit.dart';

/// One "## Heading" section of the integration guide.
class DocSection {
  DocSection(this.title, this.body);
  final String title, body;
}

/// Splits the guide (Markdown) into its "##" sections; the "# Title" and intro before the first section are dropped (the hero replaces them).
List<DocSection> splitGuide(String md) {
  final out = <DocSection>[];
  String? title;
  final buf = StringBuffer();
  var inCode = false;
  for (final line in md.replaceAll('\r', '').split('\n')) {
    if (line.trimLeft().startsWith('```')) inCode = !inCode;
    if (!inCode && line.startsWith('## ')) {
      if (title != null) out.add(DocSection(title, buf.toString().trim()));
      title = line.substring(3).trim();
      buf.clear();
      continue;
    }
    if (title != null) buf.writeln(line);
  }
  if (title != null) out.add(DocSection(title, buf.toString().trim()));
  return out;
}

/// The developer guide, read natively: the same docs/INTEGRATION.md the website shows, split into sections you can search, with copyable code.
class DocsScreen extends StatefulWidget {
  const DocsScreen({super.key});
  @override
  State<DocsScreen> createState() => _DocsScreenState();
}

class _DocsScreenState extends State<DocsScreen> {
  String _q = '';

  Future<List<DocSection>> _load(Api api) async {
    final b = await api.bytes('/api/v1/docs/integration');
    return splitGuide(utf8.decode(b, allowMalformed: true));
  }

  @override
  Widget build(BuildContext context) {
    final api = context.read<Api>();
    final base = context.read<AppConfig>().baseUrl;
    return Loader<List<DocSection>>(
      load: () => _load(api),
      builder: (context, all, _) {
        final q = _q.trim().toLowerCase();
        final list = q.isEmpty ? all : all.where((s) => s.title.toLowerCase().contains(q) || s.body.toLowerCase().contains(q)).toList();
        return ListView(padding: const EdgeInsets.all(16), children: [
          Container(
            padding: const EdgeInsets.all(18),
            decoration: BoxDecoration(gradient: const LinearGradient(colors: [Color(0xFF2563EB), Color(0xFF7C3AED)], begin: Alignment.topLeft, end: Alignment.bottomRight), borderRadius: BorderRadius.circular(20)),
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              const Text('Developer documentation', style: TextStyle(color: Colors.white70, fontSize: 12, fontWeight: FontWeight.w700, letterSpacing: 0.6)),
              const SizedBox(height: 6),
              const Text('Connect your system to ZIMRA', style: TextStyle(color: Colors.white, fontSize: 22, fontWeight: FontWeight.w800)),
              const SizedBox(height: 12),
              InkWell(
                onTap: () { Clipboard.setData(ClipboardData(text: base)); toast(context, 'Base URL copied.'); },
                child: Container(
                  padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
                  decoration: BoxDecoration(color: Colors.white.withValues(alpha: 0.15), borderRadius: BorderRadius.circular(12)),
                  child: Row(children: [
                    const Text('Base URL  ', style: TextStyle(color: Colors.white70, fontSize: 12)),
                    Expanded(child: Text(base, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(color: Colors.white, fontFamily: 'monospace', fontSize: 13))),
                    const Icon(Icons.copy, color: Colors.white, size: 16),
                  ]),
                ),
              ),
            ]),
          ),
          const SizedBox(height: 14),
          TextField(onChanged: (v) => setState(() => _q = v), decoration: const InputDecoration(prefixIcon: Icon(Icons.search), hintText: 'Search the guide')),
          const SizedBox(height: 10),
          if (list.isEmpty) EmptyState(all.isEmpty ? 'The guide could not be loaded.' : 'Nothing matches "$_q".'),
          for (final s in list)
            Padding(
              padding: const EdgeInsets.only(bottom: 8),
              child: AppCard(
                padding: const EdgeInsets.all(14),
                onTap: () => Navigator.push(context, MaterialPageRoute(builder: (_) => DocSectionScreen(sections: all, index: all.indexOf(s)))),
                child: Row(children: [
                  CircleAvatar(backgroundColor: kBrand.withValues(alpha: 0.1), child: Text('${all.indexOf(s) + 1}', style: const TextStyle(color: kBrand, fontWeight: FontWeight.w700))),
                  const SizedBox(width: 12),
                  Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    Text(s.title, style: const TextStyle(fontWeight: FontWeight.w700)),
                    Text(_plain(s.body), maxLines: 2, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 12, color: Theme.of(context).hintColor)),
                  ])),
                  const Icon(Icons.chevron_right, size: 18),
                ]),
              ),
            ),
          const SizedBox(height: 24),
        ]);
      },
    );
  }
}

String _plain(String md) => md.replaceAll(RegExp(r'```[\s\S]*?```'), ' ').replaceAll(RegExp(r'[#>*`|]'), '').replaceAll(RegExp(r'\[([^\]]+)\]\([^)]*\)'), r'$1').replaceAll(RegExp(r'\s+'), ' ').trim();

class DocSectionScreen extends StatelessWidget {
  const DocSectionScreen({super.key, required this.sections, required this.index});
  final List<DocSection> sections;
  final int index;

  @override
  Widget build(BuildContext context) {
    final s = sections[index];
    void go(int i) => Navigator.pushReplacement(context, MaterialPageRoute(builder: (_) => DocSectionScreen(sections: sections, index: i)));
    return Scaffold(
      appBar: AppBar(title: Text(s.title)),
      body: ListView(padding: const EdgeInsets.all(16), children: [
        Text(s.title, style: const TextStyle(fontSize: 22, fontWeight: FontWeight.w800)),
        const SizedBox(height: 8),
        MarkdownView(s.body),
        const SizedBox(height: 20),
        Row(children: [
          if (index > 0) Expanded(child: OutlinedButton(onPressed: () => go(index - 1), child: Text('← ${sections[index - 1].title}', maxLines: 1, overflow: TextOverflow.ellipsis))) else const Spacer(),
          const SizedBox(width: 10),
          if (index < sections.length - 1) Expanded(child: FilledButton(onPressed: () => go(index + 1), child: Text('${sections[index + 1].title} →', maxLines: 1, overflow: TextOverflow.ellipsis))) else const Spacer(),
        ]),
        const SizedBox(height: 24),
      ]),
    );
  }
}

/// A small Markdown renderer for the guide: headings, paragraphs, bullet lists, quotes, tables (scroll sideways) and code blocks with a copy button.
class MarkdownView extends StatelessWidget {
  const MarkdownView(this.md, {super.key});
  final String md;

  static List<InlineSpan> inline(String text, TextStyle base, Color codeBg) {
    final spans = <InlineSpan>[];
    final re = RegExp(r'`([^`]+)`|\*\*([^*]+)\*\*|\[([^\]]+)\]\(([^)]+)\)');
    var at = 0;
    for (final m in re.allMatches(text)) {
      if (m.start > at) spans.add(TextSpan(text: text.substring(at, m.start)));
      if (m.group(1) != null) {
        spans.add(TextSpan(text: m.group(1), style: TextStyle(fontFamily: 'monospace', fontSize: (base.fontSize ?? 14) - 1, backgroundColor: codeBg)));
      } else if (m.group(2) != null) {
        spans.add(TextSpan(text: m.group(2), style: const TextStyle(fontWeight: FontWeight.w700)));
      } else {
        spans.add(TextSpan(text: m.group(3), style: const TextStyle(color: kBrand, fontWeight: FontWeight.w600)));
      }
      at = m.end;
    }
    if (at < text.length) spans.add(TextSpan(text: text.substring(at)));
    return spans;
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final dark = theme.brightness == Brightness.dark;
    final codeBg = dark ? const Color(0xFF1E293B) : const Color(0xFFF1F5F9);
    final base = TextStyle(fontSize: 14.5, height: 1.5, color: theme.colorScheme.onSurface);
    Widget para(String t, {TextStyle? style}) => Padding(padding: const EdgeInsets.only(bottom: 10), child: SelectableText.rich(TextSpan(style: style ?? base, children: inline(t, style ?? base, codeBg))));

    final out = <Widget>[];
    final lines = md.split('\n');
    var i = 0;
    final buf = <String>[];
    void flush() { if (buf.isNotEmpty) { out.add(para(buf.join(' '))); buf.clear(); } }
    while (i < lines.length) {
      final l = lines[i];
      final t = l.trim();
      if (t.startsWith('```')) {
        flush();
        final lang = t.substring(3).trim();
        final code = <String>[];
        i++;
        while (i < lines.length && !lines[i].trim().startsWith('```')) { code.add(lines[i]); i++; }
        i++;
        out.add(CodeBox(code.join('\n'), lang: lang));
        continue;
      }
      if (t.isEmpty) { flush(); i++; continue; }
      if (t.startsWith('#')) {
        flush();
        out.add(Padding(padding: const EdgeInsets.only(top: 12, bottom: 6), child: Text(t.replaceFirst(RegExp(r'^#+\s*'), ''), style: const TextStyle(fontSize: 17, fontWeight: FontWeight.w800))));
        i++;
        continue;
      }
      if (RegExp(r'^([-*]|\d+\.)\s').hasMatch(t)) {
        flush();
        final m = RegExp(r'^([-*]|\d+\.)\s+(.*)$').firstMatch(t)!;
        final bullet = m.group(1)!.endsWith('.') ? m.group(1)! : '•';
        out.add(Padding(padding: const EdgeInsets.only(bottom: 6, left: 4), child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text('$bullet  ', style: base.copyWith(fontWeight: FontWeight.w700, color: kBrand)),
          Expanded(child: SelectableText.rich(TextSpan(style: base, children: inline(m.group(2)!, base, codeBg)))),
        ])));
        i++;
        continue;
      }
      if (t.startsWith('>')) {
        flush();
        final q = <String>[];
        while (i < lines.length && lines[i].trim().startsWith('>')) { q.add(lines[i].trim().replaceFirst(RegExp(r'^>\s?'), '')); i++; }
        out.add(Container(
          margin: const EdgeInsets.only(bottom: 10),
          padding: const EdgeInsets.all(12),
          decoration: BoxDecoration(color: kBrand.withValues(alpha: 0.07), borderRadius: BorderRadius.circular(12), border: const Border(left: BorderSide(color: kBrand, width: 3))),
          child: SelectableText.rich(TextSpan(style: base, children: inline(q.join(' '), base, codeBg))),
        ));
        continue;
      }
      if (t.startsWith('|')) {
        flush();
        final rows = <List<String>>[];
        while (i < lines.length && lines[i].trim().startsWith('|')) {
          final r = lines[i].trim();
          if (!RegExp(r'^\|[\s:|-]+\|?$').hasMatch(r)) rows.add(r.substring(1, r.endsWith('|') ? r.length - 1 : r.length).split('|').map((c) => c.trim()).toList());
          i++;
        }
        if (rows.isNotEmpty) {
          out.add(Padding(padding: const EdgeInsets.only(bottom: 12), child: SingleChildScrollView(scrollDirection: Axis.horizontal, child: DataTable(
            headingRowHeight: 36, dataRowMinHeight: 32, columnSpacing: 18,
            columns: [for (final c in rows.first) DataColumn(label: Text(c, style: const TextStyle(fontWeight: FontWeight.w700)))],
            rows: [for (final r in rows.skip(1)) DataRow(cells: [for (var k = 0; k < rows.first.length; k++) DataCell(Text.rich(TextSpan(children: inline(k < r.length ? r[k] : '', base, codeBg)), style: const TextStyle(fontSize: 13)))])],
          ))));
        }
        continue;
      }
      buf.add(t);
      i++;
    }
    flush();
    return Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: out);
  }
}

class CodeBox extends StatelessWidget {
  const CodeBox(this.code, {super.key, this.lang = ''});
  final String code, lang;
  static const _labels = {'bash': 'cURL', 'sh': 'cURL', 'csharp': 'C#', 'cs': 'C#', 'js': 'JavaScript', 'javascript': 'JavaScript', 'ts': 'TypeScript', 'php': 'PHP', 'python': 'Python', 'json': 'JSON', 'http': 'HTTP'};
  @override
  Widget build(BuildContext context) => Container(
        margin: const EdgeInsets.only(bottom: 12),
        decoration: BoxDecoration(color: const Color(0xFF0F172A), borderRadius: BorderRadius.circular(14)),
        child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(14, 4, 4, 0),
            child: Row(children: [
              Expanded(child: Text(_labels[lang.toLowerCase()] ?? (lang.isEmpty ? 'Text' : lang.toUpperCase()), style: const TextStyle(color: Colors.white60, fontSize: 12, fontWeight: FontWeight.w600))),
              TextButton.icon(onPressed: () { Clipboard.setData(ClipboardData(text: code)); toast(context, 'Copied.'); }, icon: const Icon(Icons.copy, size: 14, color: Colors.white70), label: const Text('Copy', style: TextStyle(color: Colors.white70, fontSize: 12))),
            ]),
          ),
          SingleChildScrollView(
            scrollDirection: Axis.horizontal,
            padding: const EdgeInsets.fromLTRB(14, 0, 14, 14),
            child: SelectableText(code, style: const TextStyle(fontFamily: 'monospace', fontSize: 12.5, height: 1.45, color: Color(0xFFE2E8F0))),
          ),
        ]),
      );
}
