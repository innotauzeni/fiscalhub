import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../core/models.dart';
import '../ui/kit.dart';
import 'resource.dart';

String _s(Json j, String k) => (j[k] ?? '').toString();
String _join(Iterable<String?> parts, [String sep = ' · ']) => parts.where((p) => p != null && p.trim().isNotEmpty).join(sep);
List<(String, Tone)> _active(Json j) => [if (j['isActive'] == false) ('Inactive', Tone.warn)];

const _currencyFld = Fld('currency', 'Currency', kind: Kind.select, optionsPath: '/api/v1/currencies', optionValue: 'code', optionLabel: 'code', required: true, initial: 'USD');

// ---- everyday records ----------------------------------------------------------------------------------------------

final customersSpec = ResourceSpec(
  title: 'Customers', listPath: '/api/v1/customers', itemTitle: (j) => _s(j, 'name'), itemSubtitle: (j) => _join([j['tin'] != null ? 'TIN ${j['tin']}' : null, _s(j, 'email'), _s(j, 'phone')]),
  badges: _active, leadingIcon: Icons.people_outline, emptyText: 'No customers yet. Add the people and companies you invoice.', createLabel: 'Add customer', createPerm: 'customers.manage', savePath: '/api/v1/customers',
  fields: const [
    Fld('name', 'Name', required: true), Fld('tradeName', 'Trade name'), Fld('tin', 'TIN'), Fld('vatNumber', 'VAT number'), Fld('email', 'Email', kind: Kind.email), Fld('phone', 'Phone', kind: Kind.phone),
    Fld('address', 'Address'), Fld('notes', 'Notes', kind: Kind.multiline), Fld('isActive', 'In use', kind: Kind.boolean, initial: true),
  ],
  facts: (j) => [('Trade name', _s(j, 'tradeName')), ('TIN', _s(j, 'tin')), ('VAT number', _s(j, 'vatNumber')), ('Email', _s(j, 'email')), ('Phone', _s(j, 'phone')), ('Address', _s(j, 'address')), ('Notes', _s(j, 'notes'))],
  deletePath: null,
);

final productsSpec = ResourceSpec(
  title: 'Items', listPath: '/api/v1/products', itemTitle: (j) => _s(j, 'name'), itemSubtitle: (j) => _join([_s(j, 'code'), j['hsCode'] != null ? 'HS ${j['hsCode']}' : null, _s(j, 'unit')]),
  trailing: (j) => moneyIn(j['unitPrice'], _s(j, 'currency')), badges: (j) => [if (j['isExempt'] == true) ('Exempt', Tone.info) else if (j['taxPercent'] != null) ('${j['taxPercent']}% tax', Tone.neutral), ..._active(j)],
  leadingIcon: Icons.inventory_2_outlined, emptyText: 'No items yet. Add what you sell so invoices fill in quickly.', createLabel: 'Add item', createPerm: 'products.manage', savePath: '/api/v1/products',
  fields: const [
    Fld('name', 'Name', required: true), Fld('code', 'Code'), Fld('hsCode', 'HS code', kind: Kind.integer, help: '4, 6 or 8 digits'), Fld('unit', 'Unit'), Fld('unitPrice', 'Unit price', kind: Kind.number, required: true),
    _currencyFld, Fld('taxPercent', 'Tax percent', kind: Kind.number), Fld('isExempt', 'Exempt from tax', kind: Kind.boolean), Fld('isActive', 'In use', kind: Kind.boolean, initial: true),
  ],
  shape: (v, e) => {'id': e?['id'], ...v, 'hsCode': v['hsCode']?.toString(), 'unitPrice': v['unitPrice'] ?? 0, 'taxPercent': v['isExempt'] == true ? null : v['taxPercent']},
);

final branchesSpec = ResourceSpec(
  title: 'Branches', listPath: '/api/v1/branches', itemTitle: (j) => _s(j, 'name'), itemSubtitle: (j) => _join([_s(j, 'code'), _s(j, 'address'), _s(j, 'phone')]),
  badges: (j) => [('${(j['deviceIds'] as List?)?.length ?? 0} device(s)', Tone.info), ..._active(j)], leadingIcon: Icons.store_outlined, emptyText: 'No branches yet.', createLabel: 'Add branch', createPerm: 'branches.manage', savePath: '/api/v1/branches',
  fields: const [Fld('code', 'Code', required: true), Fld('name', 'Name', required: true), Fld('address', 'Address'), Fld('phone', 'Phone', kind: Kind.phone), Fld('isActive', 'In use', kind: Kind.boolean, initial: true)],
  deletePath: (j) => '/api/v1/branches/${j['id']}', deletePerm: 'branches.manage',
);

final banksSpec = ResourceSpec(
  title: 'Banks', listPath: '/api/v1/banks', itemTitle: (j) => _s(j, 'name'), itemSubtitle: (j) { final acc = ((j['accounts'] ?? []) as List); final cur = acc.map((a) => (a['currencyCode'] ?? '').toString()).where((c) => c.isNotEmpty).toSet().join(', '); return _join(['${acc.length} ${acc.length == 1 ? 'account' : 'accounts'}', cur, _s(j, 'code')]); }, badges: _active,
  leadingIcon: Icons.account_balance_outlined, emptyText: 'No banks yet. Add the banks your company uses.', createLabel: 'Add bank', createPerm: 'banks.manage', savePath: '/api/v1/banks',
  fields: const [Fld('name', 'Bank name', required: true), Fld('code', 'Code'), Fld('isActive', 'In use', kind: Kind.boolean, initial: true)],
  facts: (j) => [('Code', _s(j, 'code')), for (final a in (j['accounts'] ?? []) as List) ('${a['currencyCode'] ?? ''} account', _join([_s(a, 'accountName'), _s(a, 'accountNumber'), a['isDefault'] == true ? 'default' : null]))],
  deletePath: (j) => '/api/v1/banks/${j['id']}', deletePerm: 'banks.manage',
);

final currenciesSpec = ResourceSpec(
  title: 'Currencies', listPath: '/api/v1/currencies/all', itemTitle: (j) => '${_s(j, 'code')}  ${_s(j, 'name')}', itemSubtitle: (j) => 'Symbol ${_s(j, 'symbol')} · order ${j['sortOrder']}', badges: _active,
  leadingIcon: Icons.currency_exchange, createLabel: 'Add currency', createPerm: 'currencies.manage', savePath: '/api/v1/currencies', idKey: 'code',
  fields: const [Fld('code', 'Code', required: true, createOnly: true, help: '3 letters, e.g. USD'), Fld('name', 'Name', required: true), Fld('symbol', 'Symbol', required: true), Fld('sortOrder', 'Order', kind: Kind.integer, initial: 0), Fld('isActive', 'In use', kind: Kind.boolean, initial: true)],
  shape: (v, e) => {...v, 'code': e?['code'] ?? v['code']?.toString().toUpperCase()},
  deletePath: (j) => '/api/v1/currencies/${j['code']}', deletePerm: 'currencies.manage',
);

// ---- people and access ---------------------------------------------------------------------------------------------

final usersSpec = ResourceSpec(
  title: 'Users', listPath: '/api/v1/users', itemTitle: (j) => '${_s(j, 'name')} ${_s(j, 'surname')}'.trim(), itemSubtitle: (j) => _join([_s(j, 'email'), _s(j, 'clientName')]),
  badges: (j) => [for (final r in (j['roles'] ?? []) as List) (r.toString(), Tone.info), (_s(j, 'status'), toneFor(j['status'] == 'ACTIVE' ? 'ok' : 'bad'))], leadingIcon: Icons.person_outline,
  createLabel: 'Add user', createPerm: 'users.manage', savePath: '/api/v1/users',
  saveFor: (e) => e == null ? ('POST', '/api/v1/users') : ('PUT', '/api/v1/users/${e['id']}'),
  fields: const [
    Fld('email', 'Email', kind: Kind.email, required: true), Fld('userName', 'User name (optional)', help: 'They can sign in with this or with the email, e.g. Inno. 3-32 letters or digits, no spaces.'),
    Fld('name', 'First name', required: true), Fld('surname', 'Surname'),
    Fld('role', 'Role', kind: Kind.select, required: true, optionsPath: '/api/v1/users/roles', optionValue: 'name', optionLabel: 'name'),
    Fld('password', 'Temporary password', kind: Kind.password, required: true, createOnly: true, help: 'At least 10 characters with upper, lower case and a digit. They choose their own at first sign-in.'),
    Fld('status', 'Status', kind: Kind.select, editOnly: true, options: [Opt('ACTIVE', 'Active'), Opt('INACTIVE', 'Inactive')], required: true, initial: 'ACTIVE'),
  ],
  shape: (v, e) {
    // a user name that is just the email means: sign in with the email
    final un = (v['userName'] ?? '').toString().trim();
    final own = un.contains('@') ? '' : un;
    return e == null ? {'email': v['email'], 'userName': own.isEmpty ? null : own, 'name': v['name'], 'surname': v['surname'] ?? '', 'role': v['role'], 'password': v['password'], 'clientId': null}
        : {'name': v['name'], 'surname': v['surname'] ?? '', 'role': v['role'] ?? ((e['roles'] as List?)?.firstOrNull), 'status': v['status'] ?? e['status'], 'email': v['email'] ?? e['email'], 'userName': own};
  },
  facts: (j) => [('Email', _s(j, 'email')), ('User name', _s(j, 'userName')), ('Company', _s(j, 'clientName')), ('Roles', ((j['roles'] ?? []) as List).join(', ')), ('Status', _s(j, 'status')), ('Last sign-in', fmtDateTime(j['lastLoginDate'])), ('Access', _s(j, 'accessScope'))],
  actions: [
    RowAction('Reset password', 'POST', (j) => '/api/v1/users/${j['id']}/reset-password', icon: Icons.lock_reset, perm: 'users.manage', ask: const [Fld('newPassword', 'New temporary password', kind: Kind.password, required: true)], body: (j, a) => {'newPassword': a['newPassword']}, done: 'Password changed. They must set their own at next sign-in.'),
    RowAction('Reset two-step', 'POST', (j) => '/api/v1/users/${j['id']}/reset-mfa', icon: Icons.shield_outlined, perm: 'users.manage', confirmText: 'Remove their authenticator app and passkeys and sign them out everywhere? Do this when they lost their phone.', done: 'Done. They can set it up again.'),
  ],
);

final tokensSpec = ResourceSpec(
  title: 'API tokens', listPath: '/api/v1/tokens', itemTitle: (j) => _s(j, 'name'), itemSubtitle: (j) => _join(['Device ${j['zimraDeviceId']}', '${_s(j, 'prefix')}…', j['lastUsedAt'] != null ? 'used ${fmtDate(j['lastUsedAt'])}' : 'never used']),
  badges: (j) => [if (j['revokedAt'] != null) ('Revoked', Tone.bad) else ('Active', Tone.ok)], leadingIcon: Icons.vpn_key_outlined, emptyText: 'No tokens yet. A token lets your own system submit receipts for one device.',
  createLabel: 'Create token', createPerm: 'tokens.manage',
  saveFor: (e) => ('POST', '/api/v1/devices/${_pendingDevice}/tokens'), savePath: '/api/v1/tokens',
  fields: const [
    Fld('deviceId', 'Device', kind: Kind.select, required: true, optionsPath: '/api/v1/devices', optionValue: 'id', optionLabel: 'zimraDeviceId', help: 'The token works for this one device only.'),
    Fld('name', 'Name', required: true, help: 'For example: Till system'),
    Fld('scopes', 'What it may do', kind: Kind.multi, required: true, optionsPath: '/api/v1/tokens/scopes', optionValue: 'name', optionLabel: 'displayName'),
    Fld('expiresAt', 'Expires on (optional)', kind: Kind.date),
  ],
  shape: (v, e) { _pendingDevice = v['deviceId']?.toString() ?? ''; return {'name': v['name'], 'scopes': v['scopes'], 'expiresAt': v['expiresAt'] == null ? null : '${v['expiresAt']}T23:59:59'}; },
  onSaved: (context, r) async {
    final secret = r is Map ? (r['secret'] ?? '').toString() : '';
    if (secret.isEmpty) return;
    await showDialog<void>(context: context, barrierDismissible: false, builder: (d) => AlertDialog(
      title: const Text('Copy your token now'),
      content: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [const Text('This is the only time it is shown. Keep it secret.'), const SizedBox(height: 10), SelectableText(secret, style: const TextStyle(fontFamily: 'monospace'))]),
      actions: [TextButton(onPressed: () { Clipboard.setData(ClipboardData(text: secret)); toast(d, 'Copied.'); }, child: const Text('Copy')), FilledButton(onPressed: () => Navigator.pop(d), child: const Text('Done'))],
    ));
  },
  actions: [RowAction('Revoke', 'DELETE', (j) => '/api/v1/tokens/${j['id']}', icon: Icons.block, danger: true, perm: 'tokens.manage', confirmText: 'Revoke this token? Anything using it stops working.', when: (j) => j['revokedAt'] == null, done: 'Token revoked.')],
);
String _pendingDevice = '';

// ---- accounting ----------------------------------------------------------------------------------------------------

final accountsSpec = ResourceSpec(
  title: 'Chart of accounts', listPath: '/api/v1/accounting/accounts', itemTitle: (j) => '${_s(j, 'code')}  ${_s(j, 'name')}', itemSubtitle: (j) => _s(j, 'type'), badges: (j) => [if (j['isSystem'] == true) ('Standard', Tone.info), ..._active(j)],
  leadingIcon: Icons.menu_book_outlined, createLabel: 'Add account', createPerm: 'accounting.manage', savePath: '/api/v1/accounting/accounts',
  fields: const [Fld('code', 'Code', required: true), Fld('name', 'Name', required: true), Fld('type', 'Type', kind: Kind.select, required: true, options: [Opt('Asset', 'Asset'), Opt('Liability', 'Liability'), Opt('Equity', 'Equity'), Opt('Income', 'Income'), Opt('Expense', 'Expense')]), Fld('isActive', 'In use', kind: Kind.boolean, initial: true)],
  canEdit: (j) => j['isSystem'] != true || true,
  deletePath: (j) => '/api/v1/accounting/accounts/${j['id']}', deletePerm: 'accounting.manage',
);

final suppliersSpec = ResourceSpec(
  title: 'Suppliers', listPath: '/api/v1/accounting/suppliers', itemTitle: (j) => _s(j, 'name'), itemSubtitle: (j) => _join([j['tin'] != null ? 'TIN ${j['tin']}' : null, _s(j, 'phone'), '${j['termsDays']}-day terms']),
  trailing: (j) => ((j['outstanding'] ?? []) as List).map((o) => moneyIn(o['amount'], o['currency'])).join('\n'), badges: _active, leadingIcon: Icons.local_shipping_outlined, emptyText: 'No suppliers yet.',
  createLabel: 'Add supplier', createPerm: 'accounting.manage', savePath: '/api/v1/accounting/suppliers',
  fields: const [Fld('name', 'Name', required: true), Fld('tin', 'TIN'), Fld('email', 'Email', kind: Kind.email), Fld('phone', 'Phone', kind: Kind.phone), Fld('address', 'Address'), Fld('termsDays', 'Payment terms (days)', kind: Kind.integer, initial: 30), Fld('isActive', 'In use', kind: Kind.boolean, initial: true)],
  shape: (v, e) => {'id': e?['id'], ...v, 'termsDays': v['termsDays'] ?? 30},
);

final billsSpec = ResourceSpec(
  title: 'Bills', listPath: '/api/v1/accounting/bills', itemTitle: (j) => '${_s(j, 'supplierName')} · ${_s(j, 'number')}', itemSubtitle: (j) => _join(['Due ${fmtDate(j['dueDate'])}', if ((j['daysOverdue'] ?? 0) > 0) '${j['daysOverdue']} days late']),
  trailing: (j) => moneyIn(j['outstanding'], _s(j, 'currency')), badges: (j) => [(_s(j, 'status'), toneFor(_s(j, 'status')))], leadingIcon: Icons.request_quote_outlined, emptyText: 'No bills yet. Enter your suppliers\' invoices here.',
  filters: const [('Status', 'status', ['Unpaid', 'Part paid', 'Paid'])],
  createLabel: 'Enter a bill', createPerm: 'accounting.manage', savePath: '/api/v1/accounting/bills', saveMethod: 'POST',
  fields: const [
    Fld('supplierId', 'Supplier', kind: Kind.select, required: true, optionsPath: '/api/v1/accounting/suppliers'), Fld('number', 'Supplier\'s invoice number', required: true), Fld('date', 'Bill date', kind: Kind.date, required: true), Fld('dueDate', 'Due date (optional)', kind: Kind.date),
    _currencyFld, Fld('subtotal', 'Amount before VAT', kind: Kind.number, required: true), Fld('vat', 'VAT', kind: Kind.number, initial: 0), Fld('expenseAccountId', 'Expense account', kind: Kind.select, required: true, optionsPath: '/api/v1/accounting/accounts', optionLabel: 'name'), Fld('memo', 'Memo'),
  ],
  shape: (v, e) => {...v, 'vat': v['vat'] ?? 0},
  facts: (j) => [('Supplier', _s(j, 'supplierName')), ('Bill date', fmtDate(j['date'])), ('Due', fmtDate(j['dueDate'])), ('Total', moneyIn(j['total'], _s(j, 'currency'))), ('Paid', money(j['paid'])), ('Owing', moneyIn(j['outstanding'], _s(j, 'currency'))), ('Expense', _s(j, 'expenseAccountName')), ('Posted as', _s(j, 'journalNumber')), ('Memo', _s(j, 'memo'))],
  actions: [
    RowAction('Pay', 'POST', (j) => '/api/v1/accounting/bills/${j['id']}/pay', icon: Icons.payments_outlined, perm: 'accounting.manage', when: (j) => (j['outstanding'] ?? 0) > 0,
        ask: [const Fld('date', 'Date paid', kind: Kind.date, required: true), const Fld('amount', 'Amount', kind: Kind.number, required: true), const Fld('accountId', 'Paid from', kind: Kind.select, required: true, optionsPath: '/api/v1/accounting/accounts', optionLabel: 'name'), const Fld('reference', 'Reference')],
        body: (j, a) => {'date': a['date'], 'amount': a['amount'], 'accountId': a['accountId'], 'reference': a['reference']}, done: 'Payment recorded.'),
    RowAction('Void bill', 'POST', (j) => '/api/v1/accounting/bills/${j['id']}/void', icon: Icons.block, danger: true, perm: 'accounting.manage', when: (j) => (j['paid'] ?? 0) == 0 && j['status'] != 'Void', confirmText: 'Void this bill? It is reversed in the books.', done: 'Bill voided.'),
  ],
);

final assetsSpec = ResourceSpec(
  title: 'Fixed assets', listPath: '/api/v1/accounting/assets', itemsKey: 'assets', itemTitle: (j) => _s(j, 'name'), itemSubtitle: (j) => _join([_s(j, 'code'), 'Bought ${fmtDate(j['purchaseDate'])}']),
  trailing: (j) => moneyIn(j['netBookValue'], _s(j, 'currency')), badges: (j) => [(_s(j, 'status'), toneFor(_s(j, 'status') == 'Disposed' ? 'warn' : 'ok'))], leadingIcon: Icons.precision_manufacturing_outlined, emptyText: 'No fixed assets yet.',
  createLabel: 'Add asset', createPerm: 'accounting.manage', savePath: '/api/v1/accounting/assets', saveMethod: 'POST',
  fields: const [
    Fld('name', 'Name', required: true), Fld('code', 'Asset code'), Fld('purchaseDate', 'Bought on', kind: Kind.date, required: true), _currencyFld, Fld('cost', 'Cost', kind: Kind.number, required: true), Fld('residualValue', 'Worth at end of life', kind: Kind.number, initial: 0),
    Fld('method', 'Method', kind: Kind.select, required: true, initial: 'straight_line', options: [Opt('straight_line', 'Straight line (equal every month)'), Opt('reducing', 'Reducing balance (yearly %)')]),
    Fld('usefulLifeMonths', 'Useful life (months)', kind: Kind.integer, initial: 60), Fld('ratePercent', 'Yearly rate % (reducing balance)', kind: Kind.number, initial: 20),
    Fld('assetAccountId', 'Asset account', kind: Kind.select, required: true, optionsPath: '/api/v1/accounting/accounts', optionLabel: 'name'), Fld('accumulatedAccountId', 'Accumulated depreciation account', kind: Kind.select, required: true, optionsPath: '/api/v1/accounting/accounts', optionLabel: 'name'),
    Fld('expenseAccountId', 'Depreciation expense account', kind: Kind.select, required: true, optionsPath: '/api/v1/accounting/accounts', optionLabel: 'name'),
  ],
  shape: (v, e) => {...v, 'residualValue': v['residualValue'] ?? 0, 'usefulLifeMonths': v['method'] == 'reducing' ? 60 : (v['usefulLifeMonths'] ?? 60), 'ratePercent': v['method'] == 'reducing' ? (v['ratePercent'] ?? 20) : 0, 'notes': null, 'paidFromAccountId': null},
  facts: (j) => [('Cost', moneyIn(j['cost'], _s(j, 'currency'))), ('Depreciated', money(j['accumulatedDepreciation'])), ('Net book value', money(j['netBookValue'])), ('Method', _s(j, 'method')), ('Bought', fmtDate(j['purchaseDate'])), ('Status', _s(j, 'status'))],
  actions: [
    RowAction('Sell or scrap', 'POST', (j) => '/api/v1/accounting/assets/${j['id']}/dispose', icon: Icons.sell_outlined, perm: 'accounting.manage', when: (j) => j['status'] != 'Disposed',
        ask: const [Fld('date', 'Date', kind: Kind.date, required: true), Fld('proceeds', 'Amount received', kind: Kind.number, initial: 0), Fld('receivedIntoAccountId', 'Received into', kind: Kind.select, optionsPath: '/api/v1/accounting/accounts', optionLabel: 'name')],
        body: (j, a) => {'date': a['date'], 'proceeds': a['proceeds'] ?? 0, 'receivedIntoAccountId': a['receivedIntoAccountId']}, done: 'Asset disposed of.'),
  ],
);

// ---- platform operator ---------------------------------------------------------------------------------------------

final clientsSpec = ResourceSpec(
  title: 'Clients', listPath: '/api/v1/clients', itemTitle: (j) => _s(j, 'name'), itemSubtitle: (j) => _join([_s(j, 'email'), '${j['userCount']} users', '${j['deviceCount']} devices']), badges: (j) => [(j['isActive'] == true ? 'Active' : 'Suspended', toneFor(j['isActive'] == true ? 'ok' : 'bad'))],
  leadingIcon: Icons.apartment, createLabel: 'New company', createPerm: 'clients.manage', savePath: '/api/v1/clients', saveMethod: 'POST',
  fields: const [Fld('name', 'Company name', required: true), Fld('email', 'Company email', kind: Kind.email), Fld('adminEmail', 'Administrator email', kind: Kind.email, required: true), Fld('adminName', 'Administrator first name', required: true), Fld('adminSurname', 'Administrator surname'), Fld('adminPassword', 'Temporary password', kind: Kind.password, required: true)],
  canEdit: (_) => false,
  actions: [
    RowAction('Suspend', 'PUT', (j) => '/api/v1/clients/${j['id']}/active', icon: Icons.pause_circle_outline, danger: true, perm: 'clients.manage', when: (j) => j['isActive'] == true, body: (j, a) => {'active': false}, confirmText: 'Suspend this company? Nobody there can sign in.', done: 'Company suspended.'),
    RowAction('Activate', 'PUT', (j) => '/api/v1/clients/${j['id']}/active', icon: Icons.play_circle_outline, perm: 'clients.manage', when: (j) => j['isActive'] != true, body: (j, a) => {'active': true}, done: 'Company activated.'),
  ],
);

final applicationsSpec = ResourceSpec(
  title: 'Applications', listPath: '/api/v1/applications', itemTitle: (j) => _s(j, 'companyName'), itemSubtitle: (j) => _join([_s(j, 'contactName'), _s(j, 'email'), _s(j, 'planName')]), badges: (j) => [(_s(j, 'status'), toneFor(_s(j, 'status')))],
  filters: const [('Status', 'status', ['Pending', 'Approved', 'Rejected'])], leadingIcon: Icons.how_to_reg_outlined, emptyText: 'No registrations.',
  facts: (j) => [('Contact', '${_s(j, 'contactName')} ${_s(j, 'contactSurname')}'), ('Email', _s(j, 'email')), ('Phone', _s(j, 'phone')), ('TIN', _s(j, 'tin')), ('Plan', _s(j, 'planName')), ('Billing', _s(j, 'billingCycle')), ('Message', _s(j, 'message')), ('Applied', fmtDateTime(j['createdAt'])), ('Decision', _s(j, 'decisionReason'))],
  actions: [
    RowAction('Approve', 'POST', (j) => '/api/v1/applications/${j['id']}/approve', icon: Icons.check_circle_outline, perm: 'applications.manage', when: (j) => j['status'] == 'Pending',
        ask: const [Fld('planId', 'Plan', kind: Kind.select, optionsPath: '/api/v1/plans', optionLabel: 'name'), Fld('trialDays', 'Free trial days (blank = plan default)', kind: Kind.integer), Fld('note', 'Note')],
        body: (j, a) => {'planId': int.tryParse('${a['planId'] ?? ''}'), 'billingCycle': null, 'trialDays': a['trialDays'], 'note': a['note']}, done: 'Approved. The company was emailed.'),
    RowAction('Reject', 'POST', (j) => '/api/v1/applications/${j['id']}/reject', icon: Icons.cancel_outlined, danger: true, perm: 'applications.manage', when: (j) => j['status'] == 'Pending', ask: const [Fld('reason', 'Reason (shown to the applicant)', required: true)], body: (j, a) => {'reason': a['reason']}, done: 'Rejected.'),
  ],
);

final plansSpec = ResourceSpec(
  title: 'Plans', listPath: '/api/v1/plans', itemTitle: (j) => _s(j, 'name'), itemSubtitle: (j) => _join([moneyIn(j['priceMonthly'], _s(j, 'currency')) + '/month', '${j['maxDevices']} devices · ${j['maxUsers']} users']), badges: (j) => [('${j['subscribers'] ?? 0} subscribers', Tone.info), ..._active(j)],
  leadingIcon: Icons.layers_outlined, facts: (j) => [('Code', _s(j, 'code')), ('Monthly', moneyIn(j['priceMonthly'], _s(j, 'currency'))), ('Yearly', moneyIn(j['priceYearly'], _s(j, 'currency'))), ('Devices', _s(j, 'maxDevices')), ('Users', _s(j, 'maxUsers')), ('Branches', _s(j, 'maxBranches')), ('Receipts / month', _s(j, 'maxReceiptsPerMonth')), ('Trial days', _s(j, 'trialDays'))],
);

final subscriptionsSpec = ResourceSpec(
  title: 'Subscriptions', listPath: '/api/v1/admin/billing/subscriptions', itemTitle: (j) => _s(j, 'clientName'), itemSubtitle: (j) => _join([_s(j, 'planName'), _s(j, 'billingCycle'), 'until ${fmtDate(j['currentPeriodEnd'])}']), trailing: (j) => (j['outstanding'] ?? 0) > 0 ? 'owes ${money(j['outstanding'])}' : null,
  badges: (j) => [(_s(j, 'status'), toneFor(_s(j, 'status')))], filters: const [('Status', 'status', ['Trial', 'Active', 'PastDue', 'Suspended', 'Cancelled'])], leadingIcon: Icons.autorenew,
  actions: [RowAction('Issue invoice', 'POST', (j) => '/api/v1/admin/billing/subscriptions/${j['clientId']}/invoice', icon: Icons.receipt_long_outlined, perm: 'payments.manage', confirmText: 'Issue the next invoice for this company now?', done: 'Invoice issued.')],
);

final paymentsSpec = ResourceSpec(
  title: 'Payments', listPath: '/api/v1/admin/billing/payments', itemTitle: (j) => '${_s(j, 'clientName')} · ${_s(j, 'invoiceNumber')}', itemSubtitle: (j) => _join([_s(j, 'method'), _s(j, 'reference'), fmtDate(j['createdAt'])]), trailing: (j) => moneyIn(j['amount'], _s(j, 'currency')),
  badges: (j) => [(statusText(_s(j, 'status')), toneFor(_s(j, 'status')))], filters: const [('Status', 'status', ['Pending', 'AwaitingConfirmation', 'Paid', 'Rejected'])], leadingIcon: Icons.payments_outlined,
  actions: [
    RowAction('Confirm payment', 'POST', (j) => '/api/v1/admin/billing/payments/${j['id']}/confirm', icon: Icons.check_circle_outline, perm: 'payments.manage', when: (j) => j['status'] == 'AwaitingConfirmation' || j['status'] == 'Pending', body: (j, a) => {'reference': null, 'sendCode': true}, confirmText: 'Confirm that this money was received?', done: 'Payment confirmed.'),
    RowAction('Reject', 'POST', (j) => '/api/v1/admin/billing/payments/${j['id']}/reject', icon: Icons.cancel_outlined, danger: true, perm: 'payments.manage', when: (j) => j['status'] == 'AwaitingConfirmation', ask: const [Fld('reason', 'Reason', required: true)], body: (j, a) => {'reason': a['reason']}, done: 'Payment rejected.'),
  ],
);

final goLiveSpec = ResourceSpec(
  title: 'Go-live requests', listPath: '/api/v1/admin/go-live', itemTitle: (j) => _s(j, 'clientName'), itemSubtitle: (j) => _join([_s(j, 'contactName'), _s(j, 'contactPhone'), fmtDate(j['createdAt'])]), badges: (j) => [(_s(j, 'status'), toneFor(_s(j, 'status')))],
  filters: const [('Status', 'status', ['Pending', 'Approved', 'Rejected'])], leadingIcon: Icons.bolt,
  facts: (j) => [('Contact', _s(j, 'contactName')), ('Phone', _s(j, 'contactPhone')), ('TIN', _s(j, 'zimraTin')), ('Notes', _s(j, 'notes')), ('Decision', _s(j, 'decisionReason'))],
  actions: [
    RowAction('Approve', 'POST', (j) => '/api/v1/admin/go-live/${j['id']}/approve', icon: Icons.check_circle_outline, perm: 'applications.manage', when: (j) => j['status'] == 'Pending', ask: const [Fld('note', 'Note')], body: (j, a) => {'note': a['note']}, done: 'Live devices enabled.'),
    RowAction('Reject', 'POST', (j) => '/api/v1/admin/go-live/${j['id']}/reject', icon: Icons.cancel_outlined, danger: true, perm: 'applications.manage', when: (j) => j['status'] == 'Pending', ask: const [Fld('reason', 'Reason', required: true)], body: (j, a) => {'reason': a['reason']}, done: 'Rejected.'),
    RowAction('Switch live off', 'POST', (j) => '/api/v1/admin/go-live/clients/${j['clientId']}/revoke', icon: Icons.power_settings_new, danger: true, perm: 'applications.manage', when: (j) => j['status'] == 'Approved', confirmText: 'Switch off live device access for this company?', done: 'Live access switched off.'),
  ],
);

final partnersSpec = ResourceSpec(
  title: 'Partners', listPath: '/api/v1/partners', itemTitle: (j) => _s(j, 'name'), itemSubtitle: (j) => _join(['Code ${j['code']}', '${j['commissionPercent']}%', '${j['clients']} companies']), trailing: (j) => ((j['owed'] ?? []) as List).map((o) => moneyIn(o['amount'], o['currency'])).join('\n'),
  badges: _active, leadingIcon: Icons.handshake_outlined, createLabel: 'New partner', createPerm: 'partners.manage', savePath: '/api/v1/partners',
  fields: const [Fld('name', 'Name', required: true), Fld('code', 'Referral code', help: 'Made from the name when blank'), Fld('commissionPercent', 'Commission %', kind: Kind.number, required: true, initial: 10), Fld('contactName', 'Contact person'), Fld('email', 'Email', kind: Kind.email), Fld('phone', 'Phone', kind: Kind.phone), Fld('notes', 'Notes'), Fld('isActive', 'Active', kind: Kind.boolean, initial: true)],
  facts: (j) => [('Code', _s(j, 'code')), ('Commission', '${j['commissionPercent']}%'), ('Companies', _s(j, 'clients')), ('Staff', _s(j, 'staff')), ('Earned', ((j['earned'] ?? []) as List).map((o) => moneyIn(o['amount'], o['currency'])).join(', ')), ('Paid out', ((j['paid'] ?? []) as List).map((o) => moneyIn(o['amount'], o['currency'])).join(', ')), ('Owed', ((j['owed'] ?? []) as List).map((o) => moneyIn(o['amount'], o['currency'])).join(', ')), ('Contact', _join([_s(j, 'contactName'), _s(j, 'email'), _s(j, 'phone')]))],
  actions: [
    RowAction('Add staff login', 'POST', (j) => '/api/v1/partners/${j['id']}/staff', icon: Icons.person_add_alt, perm: 'partners.manage', ask: const [Fld('name', 'First name', required: true), Fld('surname', 'Surname'), Fld('email', 'Email', kind: Kind.email, required: true), Fld('password', 'Temporary password', kind: Kind.password, required: true)], body: (j, a) => {'email': a['email'], 'name': a['name'], 'surname': a['surname'] ?? '', 'password': a['password']}, done: 'Added.'),
    RowAction('Record payout', 'POST', (j) => '/api/v1/partners/${j['id']}/payouts', icon: Icons.payments_outlined, perm: 'partners.manage', when: (j) => ((j['owed'] ?? []) as List).isNotEmpty, ask: const [Fld('currency', 'Currency', required: true, initial: 'USD'), Fld('amount', 'Amount', kind: Kind.number, required: true), Fld('date', 'Date paid', kind: Kind.date, required: true), Fld('reference', 'Reference')], body: (j, a) => {'date': a['date'], 'amount': a['amount'], 'currency': '${a['currency']}'.toUpperCase(), 'reference': a['reference'], 'notes': null}, done: 'Payout recorded.'),
  ],
);

final backupsSpec = ResourceSpec(
  title: 'Backups', listPath: '/api/v1/backups', itemsKey: 'items', itemTitle: (j) => _s(j, 'fileName'), itemSubtitle: (j) => _join([fmtDateTime(j['createdAt']), size((j['sizeBytes'] ?? 0) as num), '${j['tables']} tables']), badges: (j) => [(_s(j, 'status'), toneFor(_s(j, 'status'))), (_s(j, 'kind'), Tone.neutral)],
  leadingIcon: Icons.cloud_download_outlined, createLabel: 'Back up now', createPerm: 'backups.manage',
  facts: (j) => [('File', _s(j, 'fileName')), ('Size', size((j['sizeBytes'] ?? 0) as num)), ('Tables', _s(j, 'tables')), ('Rows', _s(j, 'rows')), ('Made by', _s(j, 'createdBy')), ('Checksum', _s(j, 'sha256')), ('Error', _s(j, 'error'))],
  deletePath: (j) => '/api/v1/backups/${j['id']}', deletePerm: 'backups.manage',
);

/// Demo companies that never paid: when each will be deleted (a week after its demo ends), and the switch to keep one.
final demoAccountsSpec = ResourceSpec(
  title: 'Unpaid demos', listPath: '/api/v1/admin/billing/demo-accounts', idKey: 'clientId', itemTitle: (j) => _s(j, 'company'),
  itemSubtitle: (j) => _join([_s(j, 'plan'), 'demo ended ${fmtDate(j['demoEndsAt'])}', '${j['devices'] ?? 0} devices · ${j['receipts'] ?? 0} receipts']),
  trailing: (j) => j['willBeDeleted'] == true ? 'deleted ${fmtDate(j['deleteOn'])}' : (j['keep'] == true ? 'kept' : null),
  badges: (j) => [(_s(j, 'status'), toneFor(_s(j, 'status'))), if (j['keep'] == true) ('Kept', Tone.ok)], leadingIcon: Icons.auto_delete_outlined,
  emptyText: 'No unpaid demo accounts.',
  facts: (j) => [('Company', _s(j, 'company')), ('Email', _s(j, 'email')), ('Plan', _s(j, 'plan')), ('Demo ended', fmtDate(j['demoEndsAt'])), ('Deleted on', j['willBeDeleted'] == true ? fmtDate(j['deleteOn']) : 'Not deleted (kept)'), ('Devices', '${j['devices'] ?? 0}'), ('Receipts', '${j['receipts'] ?? 0}')],
  actions: [
    RowAction('Keep', 'POST', (j) => '/api/v1/admin/billing/demo-accounts/${j['clientId']}/keep', icon: Icons.bookmark_add_outlined, perm: 'payments.manage', when: (j) => j['keep'] != true, body: (j, a) => {'keep': true}, done: 'Kept: it will not be deleted.'),
    RowAction('Stop keeping', 'POST', (j) => '/api/v1/admin/billing/demo-accounts/${j['clientId']}/keep', icon: Icons.bookmark_remove_outlined, danger: true, perm: 'payments.manage', when: (j) => j['keep'] == true, body: (j, a) => {'keep': false}, confirmText: 'If it is still unpaid, it will be deleted with all its data. Continue?', done: 'It will be deleted if it stays unpaid.'),
  ],
);
