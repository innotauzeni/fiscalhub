import 'dart:async';

import 'package:dio/dio.dart' show FormData, MultipartFile;
import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:provider/provider.dart';
import 'package:url_launcher/url_launcher.dart';

import '../core/api.dart';
import '../core/models.dart';
import '../ui/kit.dart';
import '../ui/premium.dart';

String _s(Map j, String k) => (j[k] ?? '').toString();

IconData _methodIcon(Map m) => switch (_s(m, 'id')) {
      'bank' => Icons.account_balance_rounded,
      'ecocash-manual' || 'ecocash' || 'paynow-ecocash' => Icons.phone_iphone_rounded,
      'paynow-onemoney' => Icons.phone_android_rounded,
      'paypal' => Icons.account_balance_wallet_outlined,
      'stripe' => Icons.credit_card_rounded,
      _ => Icons.payments_outlined,
    };

String _methodHint(Map m) => switch (_s(m, 'kind')) {
      'redirect' => 'Pay on a secure page; we confirm it automatically.',
      'mobile' => 'Approve the prompt on your phone.',
      _ => 'Pay yourself, then send us the reference.',
    };

/// Lets the person choose how to pay an invoice (the methods the operator switched on), then runs that payment.
Future<void> payInvoice(BuildContext context, Json overview, Json invoice, Future<void> Function() reload) async {
  final methods = (((overview['options'] as Map?)?['methods'] ?? []) as List).cast<Map>();
  if (methods.isEmpty) { toast(context, 'No payment method is available yet. Please contact support.', error: true); return; }
  final picked = await showModalBottomSheet<Map>(
    context: context, showDragHandle: true, isScrollControlled: true,
    builder: (c) => SafeArea(child: SingleChildScrollView(
      padding: const EdgeInsets.fromLTRB(20, 0, 20, 16),
      child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
        Text('Pay ${_s(invoice, 'number')}', style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w800)),
        Text(moneyIn(invoice['total'], _s(invoice, 'currency')), style: const TextStyle(fontSize: 26, fontWeight: FontWeight.w900)),
        const SizedBox(height: 14),
        for (final m in methods)
          Padding(
            padding: const EdgeInsets.only(bottom: 10),
            child: AppCard(
              onTap: () => Navigator.pop(c, m),
              padding: const EdgeInsets.all(14),
              child: Row(children: [
                Container(padding: const EdgeInsets.all(10), decoration: BoxDecoration(color: kBrand.withValues(alpha: 0.1), borderRadius: BorderRadius.circular(12)), child: Icon(_methodIcon(m), color: kBrand)),
                const SizedBox(width: 12),
                Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  Text(_s(m, 'name'), style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 15)),
                  Text(_methodHint(m), style: TextStyle(fontSize: 12, color: Theme.of(c).hintColor)),
                ])),
                Icon(Icons.chevron_right_rounded, color: Theme.of(c).hintColor),
              ]),
            ),
          ),
      ]),
    )),
  );
  if (picked == null || !context.mounted) return;
  final api = context.read<Api>();
  final id = _s(invoice, 'id');
  switch (_s(picked, 'kind')) {
    case 'manual':
      final done = await Navigator.push<bool>(context, MaterialPageRoute(builder: (_) => ManualPaymentScreen(method: Map<String, dynamic>.from(picked), invoice: invoice, bank: (overview['options'] as Map?)?['bank'] as Map?)));
      if (done == true) await reload();
      return;
    case 'mobile':
      final phone = await promptText(context, 'Number that will approve the payment', hint: '0771234567', ok: 'Send prompt', keyboard: TextInputType.phone);
      if (phone == null || phone.trim().isEmpty || !context.mounted) return;
      final r = await guardedJson(context, () => _s(picked, 'id') == 'ecocash'
          ? api.post('/api/v1/billing/invoices/$id/pay/ecocash', body: {'phone': phone.trim()})
          : api.post('/api/v1/billing/invoices/$id/pay/paynow', body: {'method': _s(picked, 'id') == 'paynow-onemoney' ? 'onemoney' : 'ecocash', 'phone': phone.trim()}));
      if (r == null || !context.mounted) return;
      await waitForPayment(context, _s(r, 'id'), 'Approve the payment on ${phone.trim()}', reload);
      return;
    default:
      final r = await guardedJson(context, () => _s(picked, 'id') == 'stripe'
          ? api.post('/api/v1/billing/invoices/$id/pay/stripe', body: {})
          : api.post('/api/v1/billing/invoices/$id/pay/paynow', body: {'method': 'web'}));
      if (r == null || !context.mounted) return;
      final url = _s(r, 'redirectUrl');
      if (url.isNotEmpty) await launchUrl(Uri.parse(url), mode: LaunchMode.externalApplication);
      if (!context.mounted) return;
      await waitForPayment(context, _s(r, 'id'), 'Finish paying on the page that opened, then come back here.', reload);
  }
}

/// Runs a call and returns its JSON answer, or shows the error and returns null.
Future<Json?> guardedJson(BuildContext context, Future<dynamic> Function() work) async {
  try {
    final r = await work();
    return r is Map ? Map<String, dynamic>.from(r) : <String, dynamic>{};
  } catch (e) {
    if (context.mounted) toast(context, e, error: true);
    return null;
  }
}

/// A dialog that asks the gateway every few seconds (up to about three minutes) until the payment is decided; the invoice turns to Paid by itself.
Future<void> waitForPayment(BuildContext context, String paymentId, String message, Future<void> Function() reload) async {
  final api = context.read<Api>();
  await showDialog<void>(
    context: context, barrierDismissible: false,
    builder: (c) => _PaymentWait(api: api, paymentId: paymentId, message: message),
  );
  await reload();
}

class _PaymentWait extends StatefulWidget {
  const _PaymentWait({required this.api, required this.paymentId, required this.message});
  final Api api;
  final String paymentId, message;
  @override
  State<_PaymentWait> createState() => _PaymentWaitState();
}

class _PaymentWaitState extends State<_PaymentWait> {
  Timer? _timer;
  String _status = 'Pending';
  int _ticks = 0;

  @override
  void initState() {
    super.initState();
    _timer = Timer.periodic(const Duration(seconds: 5), (_) => _check());
  }

  @override
  void dispose() { _timer?.cancel(); super.dispose(); }

  Future<void> _check() async {
    if (++_ticks > 36) { _timer?.cancel(); if (mounted) setState(() => _status = 'Slow'); return; }
    try {
      final p = await widget.api.getMap('/api/v1/billing/payments/${widget.paymentId}');
      final st = _s(p, 'status');
      if (!mounted) return;
      setState(() => _status = st);
      if (st != 'Pending') _timer?.cancel();
    } catch (_) { /* try again next tick */ }
  }

  @override
  Widget build(BuildContext context) {
    final (icon, color, title, text) = switch (_status) {
      'Paid' => (Icons.check_circle_rounded, const Color(0xFF059669), 'Payment received', 'Thank you! Your invoice is paid.'),
      'Pending' => (Icons.hourglass_top_rounded, kBrand, 'Waiting for the payment', widget.message),
      'Slow' => (Icons.schedule_rounded, const Color(0xFFD97706), 'Still being confirmed', 'It can take a few minutes. The invoice turns to Paid by itself; you can close this.'),
      _ => (Icons.error_outline_rounded, const Color(0xFFDC2626), 'The payment did not go through', 'Try again, or choose another way to pay.'),
    };
    return AlertDialog(
      content: Column(mainAxisSize: MainAxisSize.min, children: [
        const SizedBox(height: 8),
        if (_status == 'Pending') const SizedBox.square(dimension: 48, child: CircularProgressIndicator(strokeWidth: 4)) else Icon(icon, color: color, size: 52),
        const SizedBox(height: 16),
        Text(title, textAlign: TextAlign.center, style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w800)),
        const SizedBox(height: 6),
        Text(text, textAlign: TextAlign.center, style: TextStyle(color: Theme.of(context).hintColor)),
      ]),
      actions: [
        if (_status == 'Pending') TextButton(onPressed: _check, child: const Text('Check now')),
        FilledButton(onPressed: () => Navigator.pop(context), child: Text(_status == 'Pending' ? 'Close' : 'Done')),
      ],
    );
  }
}

/// Bank transfer, EcoCash "Send Money" or PayPal: the details to pay to, then the reference (required, used once) and an optional proof (PDF or picture).
class ManualPaymentScreen extends StatefulWidget {
  const ManualPaymentScreen({super.key, required this.method, required this.invoice, this.bank});
  final Json method, invoice;
  final Map? bank;
  @override
  State<ManualPaymentScreen> createState() => _ManualPaymentScreenState();
}

class _ManualPaymentScreenState extends State<ManualPaymentScreen> {
  final _ref = TextEditingController(), _note = TextEditingController();
  PlatformFile? _proof;
  int _proofSize = 0;
  bool _busy = false, _tried = false;
  String? _error;

  String get _id => _s(widget.method, 'id');
  String get _number => _s(widget.invoice, 'number');
  String get _amount => moneyIn(widget.invoice['total'], _s(widget.invoice, 'currency'));
  String get _ussd => '*153*1*1*${_s(widget.method, 'details').replaceAll(RegExp(r'\D'), '')}*${((widget.invoice['total'] ?? 0) as num).toStringAsFixed(2)}#';

  @override
  void dispose() { _ref.dispose(); _note.dispose(); super.dispose(); }

  Future<void> _pick() async {
    final r = await FilePicker.pickFiles(type: FileType.custom, allowedExtensions: ['pdf', 'jpg', 'jpeg', 'png', 'webp']);
    if (r.isEmpty || !mounted) return;
    final f = r.first;
    if (!RegExp(r'\.(pdf|jpe?g|png|webp)$', caseSensitive: false).hasMatch(f.name)) { toast(context, 'Only a PDF or a picture (PNG, JPEG, WebP) can be uploaded.', error: true); return; }
    final size = await f.length() ?? (await f.readAsBytes()).length;
    if (!mounted) return;
    if (size > 5000000) { toast(context, 'The proof of payment may be at most 5 MB.', error: true); return; }
    setState(() { _proof = f; _proofSize = size; });
  }

  Future<void> _submit() async {
    if (_ref.text.trim().isEmpty) { setState(() { _tried = true; _error = null; }); return; }
    setState(() { _busy = true; _error = null; });
    try {
      final form = FormData.fromMap({
        'Reference': _ref.text.trim(),
        if (_note.text.trim().isNotEmpty) 'Notes': _note.text.trim(),
        if (_proof != null) 'Proof': MultipartFile.fromBytes(await _proof!.readAsBytes(), filename: _proof!.name),
      });
      await context.read<Api>().upload('/api/v1/billing/invoices/${_s(widget.invoice, 'id')}/pay/manual/$_id', form);
      if (!mounted) return;
      await showDialog<void>(context: context, builder: (c) => AlertDialog(
        icon: const Icon(Icons.mark_email_read_outlined, color: Color(0xFF059669), size: 44),
        title: const Text('We have your payment details'),
        content: const Text('We confirm the payment once it reaches us, usually within one working day, and email you.'),
        actions: [FilledButton(onPressed: () => Navigator.pop(c), child: const Text('Done'))],
      ));
      if (mounted) Navigator.pop(context, true);
    } catch (e) {
      if (mounted) setState(() => _error = e is ApiException ? e.message : e.toString());
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  void _copy(String text, String what) { Clipboard.setData(ClipboardData(text: text)); toast(context, '$what copied.'); }

  @override
  Widget build(BuildContext context) {
    final b = widget.bank;
    final hint = Theme.of(context).hintColor;
    Widget copyRow(String label, String value) => InfoRow(label, value, trailing: IconButton(tooltip: 'Copy', icon: const Icon(Icons.copy_rounded, size: 18), onPressed: () => _copy(value, label)));
    return Scaffold(
      appBar: AppBar(title: Text(_s(widget.method, 'name'))),
      body: ListView(padding: const EdgeInsets.fromLTRB(16, 12, 16, 32), children: [
        Container(
          padding: const EdgeInsets.all(18),
          decoration: BoxDecoration(gradient: heroGradient(), borderRadius: BorderRadius.circular(20)),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text('AMOUNT TO PAY', style: TextStyle(color: Colors.white.withValues(alpha: 0.75), fontSize: 11, fontWeight: FontWeight.w800, letterSpacing: 1)),
            FittedBox(fit: BoxFit.scaleDown, alignment: Alignment.centerLeft, child: Text(_amount, style: const TextStyle(color: Colors.white, fontSize: 30, fontWeight: FontWeight.w900))),
            const SizedBox(height: 6),
            Wrap(spacing: 8, runSpacing: 6, children: [HeroChip('Invoice $_number', icon: Icons.receipt_outlined)]),
          ]),
        ),
        const SizedBox(height: 14),
        if (_id == 'bank')
          InfoCard(title: 'Pay into this account', icon: Icons.account_balance_rounded, children: [
            if (b == null || _s(b, 'accountNumber').isEmpty) const Padding(padding: EdgeInsets.only(bottom: 8), child: Text('Bank details are not set yet. Contact support.'))
            else ...[
              if (_s(b, 'bankName').isNotEmpty) InfoRow('Bank', _s(b, 'bankName')),
              if (_s(b, 'accountName').isNotEmpty) copyRow('Account name', _s(b, 'accountName')),
              copyRow('Account number', _s(b, 'accountNumber')),
              if (_s(b, 'branchCode').isNotEmpty) InfoRow('Branch code', _s(b, 'branchCode')),
              if (_s(b, 'swift').isNotEmpty) InfoRow('SWIFT', _s(b, 'swift')),
              copyRow('Use this reference', _number),
              if (_s(b, 'instructions').isNotEmpty) Padding(padding: const EdgeInsets.only(top: 8, bottom: 6), child: Text(_s(b, 'instructions'), style: TextStyle(color: hint))),
            ],
          ])
        else if (_id == 'ecocash-manual')
          InfoCard(title: 'Send money with EcoCash', icon: Icons.phone_iphone_rounded, children: [
            copyRow('Send to', _s(widget.method, 'details')),
            InfoRow('Amount', _amount),
            Padding(padding: const EdgeInsets.only(top: 10), child: Text('Dial this on your phone:', style: TextStyle(color: hint))),
            Padding(
              padding: const EdgeInsets.symmetric(vertical: 8),
              child: Row(children: [
                Expanded(child: SelectableText(_ussd, style: const TextStyle(fontFamily: 'monospace', fontSize: 16, fontWeight: FontWeight.w700))),
                IconButton(tooltip: 'Copy', onPressed: () => _copy(_ussd, 'Code'), icon: const Icon(Icons.copy_rounded)),
                FilledButton.icon(onPressed: () => launchUrl(Uri(scheme: 'tel', path: _ussd)), icon: const Icon(Icons.call_rounded, size: 18), label: const Text('Dial')),
              ]),
            ),
            Padding(padding: const EdgeInsets.only(bottom: 8), child: Text('EcoCash sends you a confirmation message with a reference. Type it below.', style: TextStyle(fontSize: 12, color: hint))),
          ])
        else
          InfoCard(title: 'Pay with PayPal', icon: Icons.account_balance_wallet_outlined, children: [
            copyRow('Send to', _s(widget.method, 'details')),
            InfoRow('Amount', _amount),
            Padding(padding: const EdgeInsets.only(top: 6, bottom: 8), child: Text('Put $_number in the note.', style: TextStyle(color: hint))),
          ]),
        InfoCard(title: 'After you have paid', icon: Icons.verified_outlined, children: [
          if (_error != null)
            Container(
              margin: const EdgeInsets.only(top: 4, bottom: 10), padding: const EdgeInsets.all(12),
              decoration: BoxDecoration(color: const Color(0xFFDC2626).withValues(alpha: 0.08), borderRadius: BorderRadius.circular(12), border: Border.all(color: const Color(0xFFDC2626).withValues(alpha: 0.25))),
              child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [const Icon(Icons.error_outline_rounded, color: Color(0xFFDC2626), size: 20), const SizedBox(width: 8), Expanded(child: Text(_error!, style: const TextStyle(color: Color(0xFFB91C1C), fontWeight: FontWeight.w600)))]),
            ),
          Padding(
            padding: const EdgeInsets.only(top: 6),
            child: TextField(
              controller: _ref, textCapitalization: TextCapitalization.characters, onChanged: (_) => setState(() => _error = null),
              decoration: InputDecoration(labelText: 'Payment reference (required)', hintText: _id == 'bank' ? 'From your bank slip, e.g. FT26283XYZ' : 'From the confirmation message', prefixIcon: const Icon(Icons.tag_rounded),
                  helperText: 'Each reference can be used once.', errorText: _tried && _ref.text.trim().isEmpty ? 'Enter the reference of your payment' : null),
            ),
          ),
          const SizedBox(height: 12),
          InkWell(
            onTap: _pick, borderRadius: BorderRadius.circular(14),
            child: Container(
              padding: const EdgeInsets.all(14),
              decoration: BoxDecoration(borderRadius: BorderRadius.circular(14), border: Border.all(color: _proof != null ? const Color(0xFF059669) : Theme.of(context).dividerColor)),
              child: Row(children: [
                Icon(_proof == null ? Icons.upload_file_rounded : Icons.check_circle_rounded, color: _proof == null ? kBrand : const Color(0xFF059669)),
                const SizedBox(width: 12),
                Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  Text(_proof?.name ?? 'Attach proof of payment (optional)', style: const TextStyle(fontWeight: FontWeight.w700), maxLines: 1, overflow: TextOverflow.ellipsis),
                  Text(_proof == null ? 'PDF or picture (PNG, JPEG, WebP), up to 5 MB' : '${(_proofSize / 1024).ceil()} KB · tap to change', style: TextStyle(fontSize: 12, color: hint)),
                ])),
                if (_proof != null) IconButton(tooltip: 'Remove', onPressed: () => setState(() => _proof = null), icon: const Icon(Icons.close_rounded)),
              ]),
            ),
          ),
          const SizedBox(height: 12),
          TextField(controller: _note, maxLength: 500, decoration: const InputDecoration(labelText: 'Note (optional)', prefixIcon: Icon(Icons.notes_rounded))),
          const SizedBox(height: 4),
          Padding(
            padding: const EdgeInsets.only(bottom: 8),
            child: FilledButton.icon(onPressed: _busy ? null : _submit, icon: _busy ? const SizedBox.square(dimension: 18, child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white)) : const Icon(Icons.send_rounded), label: const Text('I have paid')),
          ),
        ]),
      ]),
    );
  }
}
