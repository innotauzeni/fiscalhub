import 'package:fdms_mobile/core/api.dart';
import 'package:fdms_mobile/core/auth.dart';
import 'package:fdms_mobile/core/config.dart';
import 'package:fdms_mobile/core/models.dart';
import 'package:fdms_mobile/modules/registry.dart';
import 'package:fdms_mobile/modules/devices.dart';
import 'package:fdms_mobile/modules/receipts.dart';
import 'package:fdms_mobile/modules/resource.dart';
import 'package:fdms_mobile/modules/specs.dart';
import 'dart:convert';
import 'dart:typed_data';

import 'package:fdms_mobile/modules/docs.dart';
import 'package:fdms_mobile/modules/payments.dart';
import 'package:fdms_mobile/modules/system_views.dart';
import 'package:fdms_mobile/modules/templates.dart';
import 'package:fdms_mobile/modules/invoice_builder.dart';
import 'package:fdms_mobile/screens/about.dart';
import 'package:fdms_mobile/screens/login.dart';
import 'package:fdms_mobile/screens/register.dart';
import 'package:fdms_mobile/screens/notifications.dart';
import 'package:fdms_mobile/screens/forgot_password.dart';
import 'package:fdms_mobile/screens/welcome.dart';
import 'package:fdms_mobile/screens/profile.dart';
import 'package:fdms_mobile/screens/shell.dart';
import 'package:fdms_mobile/ui/kit.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

/// A gateway that answers from memory, so every screen can be drawn on any screen size without a server.
const builderCanvas = r'''{"background":"#ffffff","elements":[{"id":"2ncbtkdbjwug","type":"shape","x":0,"y":0,"w":210,"h":48,"style":{"fontSize":10,"color":"#0f172a","align":"left","vAlign":"top","padding":0,"lineHeight":1.2,"opacity":1,"fill":"#fdf2f8","fill2":"#fce7f3"},"shape":"rect"},{"id":"aa0r6zb3jwuh","type":"logo","x":12,"y":8,"w":50,"h":30,"style":{"fontSize":10,"color":"#0f172a","align":"left","vAlign":"top","padding":1,"lineHeight":1.2,"opacity":1}},{"id":"9z00msltjwuh","type":"field","x":66,"y":10,"w":132,"h":12,"style":{"fontSize":22,"color":"#831843","align":"left","vAlign":"top","padding":1,"lineHeight":1.2,"opacity":1,"bold":true,"font":"Georgia","uppercase":true,"letterSpacing":0.1},"field":"company"},{"id":"zxhd0kc0jwuh","type":"text","x":66,"y":23,"w":132,"h":8,"style":{"fontSize":10,"color":"#9d174d","align":"left","vAlign":"top","padding":1,"lineHeight":1.2,"opacity":1,"italic":true,"font":"Georgia"},"text":"Your slogan here"},{"id":"vt89xtkfjwuh","type":"text","x":12,"y":51,"w":186,"h":14,"style":{"fontSize":9,"color":"#831843","align":"left","vAlign":"top","padding":2.5,"lineHeight":1.2,"opacity":1,"fill":"#fdf2f8","radius":3},"text":"{address}\nTIN: {tin}   VAT: {vat}\nTax: {taxRates} Standard VAT"},{"id":"8wnu3yj8jwuh","type":"field","x":55,"y":69,"w":100,"h":12,"style":{"fontSize":20,"color":"#ffffff","align":"center","vAlign":"middle","padding":1,"lineHeight":1.2,"opacity":1,"bold":true,"fill":"#9d174d","fill2":"#db2777","radius":6,"font":"Georgia","letterSpacing":0.2},"field":"title"},{"id":"j1oyikz5jwuh","type":"text","x":12,"y":85,"w":105,"h":30,"style":{"fontSize":9,"color":"#0f172a","align":"left","vAlign":"top","padding":3,"lineHeight":1.2,"opacity":1,"border":"#f9a8d4","borderWidth":0.8,"radius":3},"text":"BILL TO:\nName: {buyerName}\nAddress: {buyerAddress}\nTIN (if applicable): {buyerTin}"},{"id":"n34gf3dnjwuh","type":"text","x":122,"y":85,"w":76,"h":30,"style":{"fontSize":9,"color":"#0f172a","align":"left","vAlign":"top","padding":3,"lineHeight":1.6,"opacity":1,"fill":"#fdf2f8","radius":3},"text":"Invoice No:  {invoiceNo}\nInvoice Date:  {date}\nCurrency:  {currency}"},{"id":"arw6a2xnjwuh","type":"items","x":12,"y":119,"w":186,"h":100,"style":{"fontSize":8.5,"color":"#0f172a","align":"left","vAlign":"top","padding":1,"lineHeight":1.2,"opacity":1,"headerFill":"#9d174d","headerColor":"#ffffff","stripeFill":"#fdf2f8","lineColor":"#fbcfe8","rowPadding":1.5},"columns":["no","item","hs","qty","price","total"],"labels":{"item":"DESCRIPTION (Product / Service)","price":"UNIT PRICE ({currency})","total":"AMOUNT ({currency})"}},{"id":"n4oz7xy7jwuh","type":"text","x":12,"y":223,"w":100,"h":24,"style":{"fontSize":8.5,"color":"#0f172a","align":"left","vAlign":"top","padding":3,"lineHeight":1.2,"opacity":1,"fill":"#fdf2f8","radius":3},"text":"PAYMENT TERMS:\n• Payment due as per agreement.\n• All amounts are in {currency}."},{"id":"0uq7sip5jwuh","type":"totals","x":120,"y":223,"w":78,"h":28,"style":{"fontSize":9.5,"color":"#0f172a","align":"left","vAlign":"top","padding":1,"lineHeight":1.2,"opacity":1,"headerFill":"#9d174d","headerColor":"#ffffff","lineColor":"#fbcfe8","rowPadding":1.5},"columns":["subtotal","taxes","total"],"labels":{"subtotal":"SUBTOTAL ({currency})","total":"TOTAL ({currency})"}},{"id":"698jkj1rjwuh","type":"line","x":12,"y":252,"w":186,"h":1,"style":{"fontSize":10,"color":"#0f172a","align":"left","vAlign":"top","padding":0,"lineHeight":1.2,"opacity":1,"border":"#db2777","borderWidth":1}},{"id":"p5ve0c6ujwuh","type":"text","x":12,"y":255,"w":100,"h":10,"style":{"fontSize":15,"color":"#9d174d","align":"left","vAlign":"top","padding":1,"lineHeight":1.2,"opacity":1,"italic":true,"font":"Georgia"},"text":"Thank you for your business!"},{"id":"zk0a3hwgjwuh","type":"text","x":120,"y":256,"w":78,"h":7,"style":{"fontSize":8,"color":"#831843","align":"right","vAlign":"top","padding":1,"lineHeight":1.2,"opacity":1},"text":"{phone}   {email}"},{"id":"4lklvgvpjwuh","type":"qr","x":12,"y":266,"w":26,"h":28,"style":{"fontSize":7,"color":"#0f172a","align":"left","vAlign":"top","padding":0,"lineHeight":1.2,"opacity":1},"label":"Scan to verify"},{"id":"hrsrzfo9jwuh","type":"fiscal","x":42,"y":268,"w":156,"h":24,"style":{"fontSize":7,"color":"#6b7280","align":"left","vAlign":"top","padding":1,"lineHeight":1.2,"opacity":1}}]}''';

class FakeApi extends Api {
  FakeApi(super.config, super.auth);
  static bool coinsMode = true;

  static final device = {
    'id': 'd1', 'zimraDeviceId': 4242, 'environment': 'Test', 'serialNo': 'SN-1', 'taxPayerName': 'A Very Long Taxpayer Trading Name (Private) Limited', 'branchName': 'Harare Central Branch Office',
    'isActive': true, 'operatingMode': 'Online', 'certificateValidTill': '2027-01-01T00:00:00Z', 'taxes': [{'taxId': 1, 'taxName': 'Standard rated 15%', 'taxPercent': 15}, {'taxId': 2, 'taxName': 'Exempt', 'taxPercent': null}],
  };
  static final receipt = {'id': 'r1', 'invoiceNo': 'INV-2026-0000123456-LONG-NUMBER', 'type': 'FiscalInvoice', 'currency': 'USD', 'status': 'Submitted', 'fiscalDayNo': 12, 'receiptDate': '2026-10-08T10:00:00Z', 'total': 1234567.89};

  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query}) async {
    if (path == '/api/v1/dashboard') {
      return {'devices': 2, 'openDays': 1, 'receiptsToday': 14, 'todayTotals': [{'currency': 'USD', 'total': 1234567.89, 'count': 14}], 'pending': 2, 'held': 1, 'offlineIssued': 0, 'rejected': 1, 'submitted': 99, 'deviceList': [{...device, 'dayStatus': 'FiscalDayOpened', 'pendingReceipts': 2}], 'recent': [receipt], 'alerts': ['One receipt was rejected by ZIMRA and needs your attention before the fiscal day can close.'], 'trend': [for (var i = 0; i < 14; i++) {'date': '2026-09-${10 + i}', 'count': i * 3, 'rejected': i % 4}], 'checklist': {'done': 1, 'total': 4, 'steps': [{'id': 'a', 'title': 'Register your device', 'help': 'Use the activation key ZIMRA gave you.', 'done': false}]}};
    }
    if (path == '/api/v1/devices') return [device];
    if (path == '/api/v1/audit/search') return {'total': 2, 'actions': ['user.login', 'receipt.submit'], 'items': [{'id': 1, 'action': 'receipt.submit', 'actorType': 'User', 'userName': 'Ada Administrator With A Long Name', 'clientName': 'Acme Trading Company With A Long Name', 'detail': 'Invoice INV-2026-0000123456-LONG-NUMBER sent to ZIMRA for device 4242', 'at': '2026-10-08T10:00:00Z', 'ipAddress': '10.0.0.1'}, {'id': 2, 'action': 'user.login.failed', 'actorType': 'Anonymous', 'at': '2026-10-08T09:00:00Z'}]};
    if (path == '/api/v1/system-logs') return {'total': 1, 'stats': {'unresolved': 3, 'criticalUnresolved': 1, 'today': 2, 'total': 40}, 'items': [{'id': 1, 'severity': 'Critical', 'message': 'System.Security.Cryptography.CryptographicException: Access denied while loading the device certificate into the machine key store', 'method': 'POST', 'path': '/api/v1/devices/register/with/a/long/path', 'occurrences': 12, 'lastSeenAt': '2026-10-08T10:00:00Z', 'clientName': 'Acme Trading Company', 'isResolved': false}]};
    if (path.startsWith('/api/v1/system-logs/')) return {'id': 1, 'severity': 'Error', 'message': 'Boom', 'stackTrace': 'at X.Y()\n at Z.W()', 'occurrences': 1};
    if (path == '/api/v1/referrals') {
      return {'enabled': true, 'code': 'REFAB12CD', 'referralPath': '/register?ref=REFAB12CD', 'percent': 10, 'description': 'Earn 10% of the first invoice of every company you bring.', 'referred': 4, 'pending': 1, 'paying': 2,
        'earned': [{'currency': 'USD', 'amount': 1234567.5}], 'owed': [{'currency': 'USD', 'amount': 20}, {'currency': 'ZWG', 'amount': 3500}], 'paid': [],
        'tiers': [{'label': 'Bronze', 'referrals': 1, 'freeMonths': 0, 'perk': 'Partner badge', 'reached': true, 'grantedAt': '2026-10-01'}, {'label': 'Silver', 'referrals': 3, 'freeMonths': 1, 'perk': 'One free month', 'reached': false}],
        'companies': [{'name': 'A Referred Company With A Long Name (Private) Limited', 'since': '2026-09-01', 'status': 'Active', 'paying': true}],
        'rewards': [{'company': 'A Referred Company With A Long Name', 'percent': 10, 'createdAt': '2026-10-01', 'amount': 1234567.5, 'currency': 'USD', 'status': 'Earned'}],
        'rewardType': coinsMode ? 'coins' : 'money', 'coinsPerUnit': 10, 'coins': 1234567, 'bonus': {'devices': 2, 'users': 10, 'branches': 1},
        'shop': [{'key': 'week', 'title': '7 extra days with a long reward name', 'description': 'Adds 7 days to your subscription period.', 'cost': 100, 'kind': 'free_days', 'value': 7, 'enabled': true}, {'key': 'invoice', 'title': 'Pay an invoice with coins', 'cost': 0, 'kind': 'pay_invoice', 'value': 0, 'enabled': true}],
        'coinHistory': [{'id': 'h1', 'amount': 12345, 'kind': 'referral', 'description': 'Referral reward: 10% of INV-2026-000123-LONG', 'createdAt': '2026-10-01T10:00:00Z'}, {'id': 'h2', 'amount': -100, 'kind': 'redeem', 'description': '7 extra days', 'createdAt': '2026-10-02T10:00:00Z'}],
        'payableInvoices': [{'id': 'i1', 'number': 'SUB-2026-0001', 'total': 25.0, 'currency': 'USD', 'coins': 250}]};
    }
    if (path == '/api/v1/referral-program') {
      return {'program': {'enabled': true, 'percent': 10, 'invoicesRewarded': 1, 'rewardType': 'coins', 'coinsPerUnit': 10, 'tiers': [{'label': 'Gold', 'referrals': 10, 'freeMonths': 3, 'perk': 'Three free months', 'bonusCoins': 500}], 'shop': [{'key': 'device', 'title': 'One more fiscal device for good', 'cost': 300, 'kind': 'extra_device', 'value': 1, 'enabled': false}]},
        'coinsIssued': 1234567, 'coinsSpent': 99999, 'holders': [{'clientId': 'c', 'company': 'A Company Holding Lots Of Coins With A Long Name', 'balance': 1234567}], 'referrers': 3, 'referredCompanies': 5, 'earned': [{'currency': 'USD', 'amount': 99999.99}], 'owed': [], 'paid': [],
        'rewards': [{'id': 'x', 'referrer': 'A Referrer Company With A Long Name', 'referred': 'Another Company With A Long Name', 'amount': 120, 'currency': 'USD', 'status': 'Earned', 'createdAt': '2026-10-01'}]};
    }
    if (path == '/api/v1/public/legal') {
      return {'terms': {'title': 'Terms and conditions', 'body': '# 1. Who we are\n\nWe run a fiscalisation gateway.\n\n# 2. Your account\n\nKeep your password safe.', 'updatedAt': '2026-10-01'}, 'privacy': {'title': 'Privacy policy', 'body': '# What we collect\n\nYour company details.'},
        'developer': {'name': 'A Developer Company With A Long Name', 'about': 'We build business software.', 'email': 'hello@developer-company-with-a-long-domain.example.com', 'phone': '+263 77 000 0000', 'whatsApp': '+263 77 000 0000', 'website': 'https://example.com', 'address': '1 Long Street, Harare'}};
    }
    if (path == '/api/v1/templates') return [{'id': 't1', 'name': 'A template with a long descriptive name for the head office', 'branchName': 'Harare Central Branch Office', 'isDefault': true, 'updatedAt': '2026-10-01', 'hasLogo': false, 'settings': {'accent': '#be185d', 'canvas': jsonDecode(builderCanvas)}}];
    if (path == '/api/v1/templates/fields') return {'fields': [{'key': 'invoiceNo', 'label': 'Invoice number', 'group': 'Invoice'}, {'key': 'company', 'label': 'Company name', 'group': 'Company'}, {'key': 'buyerName', 'label': 'Customer name', 'group': 'Customer'}, {'key': 'title', 'label': 'Title', 'group': 'Invoice'}, {'key': 'currency', 'label': 'Currency', 'group': 'Invoice'}], 'fonts': ['Lato', 'Georgia']};
    if (path == '/api/v1/templates/sample') return {'fields': {'invoiceNo': 'INV-0001', 'company': 'A Company With A Particularly Long Registered Name (Pvt) Ltd', 'buyerName': 'A Customer With A Long Name', 'title': 'FISCAL TAX INVOICE', 'currency': 'USD'}, 'lines': [{'no': 1, 'description': 'A product with a particularly long description', 'hsCode': '33049900', 'quantity': 20, 'unitPrice': 1234567.5, 'taxPercent': 15, 'amount': 24691350, 'discount': false}], 'taxes': [{'label': 'VAT 15%', 'percent': 15, 'net': 100, 'tax': 15, 'gross': 115}], 'payments': [{'method': 'Cash', 'amount': 115}], 'subtotal': 100, 'taxTotal': 15, 'total': 115, 'currency': 'USD', 'fiscal': {'deviceId': 4242, 'fiscalDay': 3, 'receiptCounter': 12, 'globalNo': 1234, 'zimraReceiptId': 987654, 'verificationUrl': 'https://fdmsapitest.zimra.co.zw/verify/sample', 'signature': 'SAMPLEHASH', 'notices': []}};
    if (path == '/api/v1/templates/assets') return [];
    if (path == '/api/v1/banks') return [{'id': 'b1', 'name': 'Commercial Bank of Zimbabwe With A Long Name', 'code': 'CBZ', 'isActive': true, 'accounts': [{'currencyCode': 'USD', 'accountName': 'Acme Trading', 'accountNumber': '01234567890123', 'isDefault': true}, {'currencyCode': 'ZWG', 'accountName': 'Acme', 'accountNumber': '999'}]}];
    if (path == '/api/v1/users') return [{'id': 'u1', 'name': 'Tendai', 'surname': 'Moyo-Chikwanha With Long Surname', 'email': 'tendai.moyo@acme-trading-company.example.com', 'roles': ['ClientAdmin', 'Accountant'], 'status': 'ACTIVE'}, {'id': 'u2', 'name': 'Rudo', 'surname': 'Banda', 'email': 'rudo@acme.test', 'roles': ['Operator'], 'status': 'INACTIVE'}];
    if (path == '/api/v1/branches') return [{'id': 'br1', 'code': 'HRE', 'name': 'Harare Central Branch Office', 'address': '12 Very Long Street Name, Harare', 'deviceIds': ['d1'], 'isActive': true}];
    if (path == '/api/v1/roles') return [{'id': 'r', 'name': 'ClientAdmin', 'description': 'Runs the company', 'permissionCount': 40, 'userCount': 2, 'menuCount': 20}];
    if (path == '/api/v1/receipts') return {'items': [receipt, {...receipt, 'id': 'r2', 'status': 'Pending', 'type': 'CreditNote'}], 'total': 2, 'page': 1, 'pageSize': 25};
    if (path.endsWith('/detail')) return {'receipt': {...receipt, 'validationErrors': [{'code': 'RCPT010', 'color': 'Red'}]}, 'lines': [{'receiptLineName': 'A product with a really quite long descriptive name', 'receiptLineQuantity': 3, 'receiptLinePrice': 1234.5, 'receiptLineTotal': 3703.5, 'taxPercent': 15, 'receiptLineHSCode': '85456852'}], 'payments': [{'moneyTypeCode': 'Cash', 'paymentAmount': 3703.5}], 'buyer': {'buyerRegisterName': 'Buyer', 'buyerTIN': '1234567890'}};
    if (path == '/api/v1/customers') return [{'id': 'c1', 'name': 'A Customer With A Particularly Long Registered Company Name Limited', 'tin': '1234567890', 'email': 'accounts@customer-with-long-address.example.com', 'phone': '+263 77 123 4567', 'isActive': true}];
    if (path == '/api/v1/notifications') return {'unread': 3, 'items': [
      {'id': 'n', 'kind': 'certificate', 'title': 'Certificate expiring', 'body': 'Device 4242 certificate expires in 5 days.', 'severity': 'warn', 'createdAt': '2026-10-08T08:00:00Z', 'read': false},
      {'id': 'n2', 'kind': 'billing', 'title': 'Invoice SUB-2026-000123-LONG-NUMBER is ready to pay', 'body': '1,234,567.89 USD for the Business plan with a long description, due 15 Oct 2026. Open Billing to pay.', 'severity': 'warn', 'link': '/billing', 'createdAt': '2026-10-08T09:00:00Z', 'read': false},
      {'id': 'n3', 'kind': 'application', 'title': 'New registration: A Company With A Particularly Long Registered Name (Private) Limited', 'severity': 'info', 'link': '/admin/applications', 'createdAt': '2026-10-08T07:00:00Z', 'read': true}]};
    if (path == '/api/v1/admin/billing/demo-accounts') return [{'clientId': 'c9', 'company': 'A Demo Company With A Particularly Long Registered Name', 'email': 'owner@demo-company-with-long-domain.example.com', 'plan': 'Business', 'status': 'PastDue', 'demoEndsAt': '2026-10-01T00:00:00Z', 'deleteOn': '2026-10-08T00:00:00Z', 'keep': false, 'devices': 2, 'receipts': 12345, 'willBeDeleted': true}];
    if (path == '/api/v1/admin/billing/subscriptions') return [{'clientId': 'c9', 'clientName': 'A Demo Company With A Particularly Long Registered Name', 'planName': 'Business', 'billingCycle': 'monthly', 'status': 'Trial', 'currentPeriodEnd': '2026-10-20T00:00:00Z', 'outstanding': 1234567.5}];
    if (path == '/api/v1/approvals') {
      return {'waitingForMe': 1, 'items': [{'id': 'a1', 'action': 'journal', 'actionLabel': 'Manual journal', 'status': 'Pending', 'summary': 'Journal: October rent for the head office with a long memo', 'amount': 1234567.89, 'currency': 'USD',
        'levels': ['Accountant', 'ClientAdmin', 'Operator'], 'currentLevel': 1, 'waitingForRole': 'ClientAdmin', 'requestedBy': 'Ada Administrator With A Long Name', 'createdAt': '2026-10-08T08:00:00Z', 'canDecide': true, 'canCancel': true,
        'decisions': [{'level': 1, 'role': 'Accountant', 'userName': 'Bob Accountant With A Long Name', 'decision': 'Approved', 'note': 'Checked against the lease agreement and the bank statement.', 'at': '2026-10-08T09:00:00Z'}]}]};
    }
    if (path == '/api/v1/approvals/matrix') {
      return {'roles': ['Accountant', 'ClientAdmin', 'Operator', 'Viewer'], 'rows': [
        {'action': {'key': 'journal', 'label': 'Manual journal', 'description': 'A journal entered by hand (not one posted by the system).', 'amountMeaning': 'the total of the debits'},
         'rules': [{'id': 'r1', 'action': 'journal', 'actionLabel': 'Manual journal', 'minAmount': 1000000, 'currency': 'USD', 'levels': ['Accountant', 'ClientAdmin', 'Operator'], 'isActive': true}, {'id': 'r2', 'action': 'journal', 'actionLabel': 'Manual journal', 'minAmount': 0, 'levels': ['Accountant'], 'isActive': false}]},
        {'action': {'key': 'credit_note', 'label': 'Credit note', 'description': 'Reversing a fiscal invoice.', 'amountMeaning': 'the invoice total'}, 'rules': []}]};
    }
    if (path == '/api/v1/admin/revenue') {
      final m = [{'currency': 'USD', 'amount': 1234567.89}, {'currency': 'ZWG', 'amount': 98765432.1}];
      return {'mrr': m, 'arr': m, 'thisMonth': m, 'lastMonth': m, 'last12Months': m, 'allTime': m, 'outstanding': m, 'overdue': m, 'arpuMonthly': m,
        'counts': {'companies': 1234, 'active': 1200, 'paying': 999, 'trial': 120, 'pastDue': 12, 'suspended': 3, 'cancelled': 2, 'newThisMonth': 45, 'trialsEndingIn7Days': 9, 'awaitingConfirmation': 4}, 'trialConversionPercent': 62.5,
        'months': [for (var i = 1; i <= 12; i++) {'month': '2026-' + (i < 10 ? '0' : '') + i.toString(), 'collected': [{'currency': 'USD', 'amount': i * 1000.0}], 'invoices': i}],
        'plans': [{'planId': 1, 'plan': 'Business plan with a long marketing name', 'priceMonthly': 25, 'currency': 'USD', 'paying': 900, 'trial': 100, 'mrr': 22500}],
        'methods': [{'method': 'Bank transfer', 'collected': m, 'payments': 321}],
        'topClients': [{'clientId': 'c', 'company': 'A Paying Company With A Very Long Registered Name', 'plan': 'Business', 'status': 'Active', 'paidAllTime': m, 'outstanding': []}],
        'owing': [{'clientId': 'd', 'company': 'An Owing Company With A Very Long Registered Name', 'status': 'PastDue', 'paidAllTime': [], 'outstanding': m}],
        'coinsOutstanding': 1234567, 'coinsLiability': m, 'referralMoneyOwed': [], 'partnerCommissionOwed': m, 'generatedAt': '2026-10-08T10:00:00Z'};
    }
    if (path == '/api/v1/accounting/meta') return {'currencies': ['USD', 'ZWG'], 'defaultCurrency': 'USD', 'cashAccounts': [{'id': 'a1', 'code': '1010', 'name': 'Cash on hand'}]};
    if (path == '/api/v1/accounting/reports/trial-balance') return {'rows': [{'code': '1010', 'name': 'Cash on hand and in the till at the front counter', 'debit': 1500.5, 'credit': 0}], 'totalDebit': 1500.5, 'totalCredit': 1500.5, 'balanced': true};
    if (path == '/api/v1/analytics/usage') return {'totals': {'activeCompanies': 3, 'companies': 4, 'onTrial': 1, 'locked': 1, 'atRisk': 2, 'receipts': 500, 'receiptsPrevious': 400, 'rejectedPercent': 1.5, 'rejected': 7}, 'companies': [{'name': 'Some Company', 'risk': 'High', 'plan': 'Business', 'devices': 2, 'receipts': 10, 'receiptsPrevious': 40, 'reasons': ['No receipts in the last 30 days (had 40 before) and nobody has signed in']}]};
    if (path.startsWith('/api/v1/accounting/bills')) return [{'id': 'b1', 'supplierName': 'A Supplier With A Long Name Limited', 'number': 'PM-1', 'dueDate': '2026-10-01', 'daysOverdue': 7, 'outstanding': 115.0, 'total': 115.0, 'paid': 0, 'currency': 'USD', 'status': 'Unpaid'}];
    if (path.endsWith('/day')) return {'status': 'FiscalDayOpened', 'fiscalDayNo': 12, 'lastReceiptCounter': 5, 'openedAt': '2026-10-08T06:00:00Z', 'counters': []};
    if (path.startsWith('/api/v1/devices/')) return device;
    if (path == '/api/v1/billing') {
      return {'plan': {'name': 'Business with a long plan name'}, 'subscription': {'status': 'Active', 'billingCycle': 'monthly', 'currentPeriodEnd': '2026-11-01'}, 'usage': {'devices': 2, 'users': 3, 'branches': 1, 'receiptsThisMonth': 1234},
        'invoices': [{'id': 'i1', 'number': 'SUB-2026-000123-LONG', 'description': 'Business plan, October 2026', 'total': 1234567.89, 'currency': 'USD', 'status': 'Open', 'dueAt': '2026-10-15', 'payments': [{'id': 'p1', 'method': 'Stripe', 'status': 'Pending', 'createdAt': DateTime.now().toUtc().toIso8601String()}]},
          {'id': 'i2', 'number': 'SUB-2026-000100', 'description': 'Business plan, September', 'total': 25, 'currency': 'USD', 'status': 'Paid', 'paidAt': '2026-09-02', 'payments': []}],
        'options': {'bank': {'bankName': 'Commercial Bank of Zimbabwe', 'accountName': 'Dara ERP (Private) Limited', 'accountNumber': '01234567890123', 'branchCode': '6101'}, 'methods': [{'id': 'bank', 'name': 'Bank transfer', 'kind': 'manual'}, {'id': 'ecocash-manual', 'name': 'EcoCash (Send Money)', 'kind': 'manual', 'details': '0771234567'}]}};
    }
    return path.contains('report') || path.endsWith('summary') || path.endsWith('statement') || path == '/api/v1/compliance' ? <String, dynamic>{} : <dynamic>[];
  }

  static const guide = '# Guide\n\nIntro.\n\n## Quick start with a rather long section title\n\nSend `POST /api/v1/receipts` with **your token**, see [the reference](https://x).\n\n- one\n- two\n\n'
      '```bash\ncurl -X POST https://fiscalapi.example.com/api/v1/devices/very-long-device-id/receipts -H "Authorization: Bearer fdms_xxx"\n```\n\n| Code | Meaning | What to do |\n|---|---|---|\n| 401 | Not signed in | Sign in again and retry the request |\n\n> **Note:** tokens are per device.\n\n## Errors\n\nText.';

  @override
  Future<Uint8List> bytes(String path, {Map<String, dynamic>? query, String method = 'GET', Object? body}) async {
    if (path == '/api/v1/docs/integration') return Uint8List.fromList(utf8.encode(guide));
    throw ApiException('No files in the test gateway.');
  }

  @override
  Future<dynamic> anonymousGet(String path) async => [{'id': 1, 'name': 'Business plan with a long name', 'priceMonthly': 25, 'priceYearly': 250, 'currency': 'USD', 'trialDays': 14, 'description': 'For growing companies with several tills.'}];

  @override
  Future<dynamic> post(String path, {Object? body, Map<String, dynamic>? query}) async => <String, dynamic>{};
}

final _slugs = ['dashboard', 'receipts', 'devices', 'days', 'customers', 'accounting/reports'];
final _menus = [
  for (var i = 0; i < _slugs.length; i++)
    MenuNode(i + 1, _slugs[i] == 'accounting/reports' ? 'Financial reports with a long name' : _slugs[i], _slugs[i], 'file-text', null,
        _slugs[i] == 'accounting/reports'
            ? [SubmoduleNode(1, 'Trial balance', 'layers', 'accounting/reports/trial-balance', null)]
            : _slugs[i] == 'devices'
                ? [SubmoduleNode(2, 'All devices', 'cpu', 'devices', null), SubmoduleNode(3, 'Register a new fiscal device', 'plus', 'devices/register', null)]
                : []),
];

Session _session() => Session(
      accessToken: 't', refreshToken: 'r', expiresAt: DateTime.now().add(const Duration(hours: 1)),
      user: UserInfo(id: 'u', clientId: 'c', clientName: 'Acme Trading Company With A Long Name', email: 'a@b.test', name: 'Ada', surname: 'Administrator', roles: const ['ClientAdmin'], mustChangePassword: false),
      permissions: {'devices.view', 'receipts.view', 'receipts.submit', 'accounting.view', 'accounting.manage', 'customers.manage', 'day.manage', 'devices.manage'},
      sections: [SectionNode('Fiscalisation', '#2563eb', _menus.take(4).toList()), SectionNode('Accounting and administration', '#0d9488', _menus.skip(4).toList())], menus: _menus);

Future<void> _pump(WidgetTester t, Widget Function() child, {required Size size, double textScale = 1.0}) async {
  SharedPreferences.setMockInitialValues({});
  final config = await AppConfig.load();
  final auth = AuthController(config)..session = _session();
  t.view.physicalSize = size;
  t.view.devicePixelRatio = 1.0;
  addTearDown(t.view.resetPhysicalSize);
  await t.pumpWidget(MultiProvider(
    providers: [Provider<AppConfig>.value(value: config), ChangeNotifierProvider<AuthController>.value(value: auth), Provider<Api>.value(value: FakeApi(config, auth))],
    child: MaterialApp(theme: buildTheme(Brightness.light), builder: (c, w) => MediaQuery(data: MediaQuery.of(c).copyWith(textScaler: TextScaler.linear(textScale)), child: w!), home: child()),
  ));
  await t.pump();
  await t.pump(const Duration(milliseconds: 300));
}

void main() {
  // phones from very small to large, a tablet, and people who enlarge their text
  final sizes = <String, (Size, double)>{
    'tiny phone 320x480': (const Size(320, 480), 1.0),
    'small phone 360x640, big text': (const Size(360, 640), 1.6),
    'phone 411x891': (const Size(411, 891), 1.0),
    'phone, largest text': (const Size(375, 667), 2.0),
    'tablet 1024x768': (const Size(1024, 768), 1.0),
  };

  for (final e in sizes.entries) {
    testWidgets('the app frame fits: ${e.key}', (t) async {
      await _pump(t, () => const Shell(), size: e.value.$1, textScale: e.value.$2);
      expect(tester(t), isNull);
      expect(find.byType(NavigationBar), findsOneWidget);
    });

    testWidgets('the drawer fits: ${e.key}', (t) async {
      await _pump(t, () => const Shell(), size: e.value.$1, textScale: e.value.$2);
      await t.tap(find.text('More'));
      for (var i = 0; i < 6; i++) { await t.pump(const Duration(milliseconds: 100)); }
      expect(tester(t), isNull);
      expect(find.text('FISCALISATION'), findsOneWidget);
      await t.ensureVisible(find.descendant(of: find.byType(Drawer), matching: find.text('devices')));
      await t.pump();
      await t.tap(find.descendant(of: find.byType(Drawer), matching: find.text('devices')));   // the accordion opens
      await t.pump(const Duration(milliseconds: 400));
      expect(tester(t), isNull);
      expect(find.text('Register a new fiscal device'), findsOneWidget);
      await t.enterText(find.descendant(of: find.byType(Drawer), matching: find.byType(TextField)), 'trial');
      await t.pump(const Duration(milliseconds: 300));
      expect(tester(t), isNull);
      expect(find.textContaining('Financial reports'), findsOneWidget);
    });

    testWidgets('the invoice builder draws the saved design and selects an element: ${e.key}', (t) async {
      await _pump(t, () => const InvoiceBuilderScreen(templateId: 't1'), size: e.value.$1, textScale: e.value.$2);
      final elements = find.byWidgetPredicate((w) => w.runtimeType.toString() == 'ElementView');
      for (var i = 0; i < 30 && elements.evaluate().isEmpty; i++) { await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 50))); await t.pump(const Duration(milliseconds: 100)); }
      expect(tester(t), isNull);
      expect(elements, findsNWidgets(16));
      expect(find.text('Save'), findsOneWidget);
      await t.tap(elements.at(9), warnIfMissed: false);
      await t.pump(const Duration(milliseconds: 400));   // past the double-tap wait
      expect(find.text('Properties'), findsOneWidget);
      expect(tester(t), isNull);
    });

    testWidgets('registration steps fit: ${e.key}', (t) async {
      await _pump(t, () => const RegisterScreen(), size: e.value.$1, textScale: e.value.$2);
      Future<void> next() async {
        await t.ensureVisible(find.text('Continue'));
        await t.tap(find.text('Continue'));
        await t.pump(const Duration(milliseconds: 400));
        expect(tester(t), isNull);
      }
      await t.enterText(find.byType(TextField).at(0), 'Acme Trading Company With A Long Name (Private) Limited');
      await t.pump();
      await next();
      final f = find.byType(TextField);
      await t.enterText(f.at(0), 'Ada');
      await t.enterText(f.at(2), 'ada@example.com');
      await t.enterText(f.at(3), 'Inno');   // user name (optional)
      await t.enterText(f.at(4), 'Str0ngPassword!');
      await t.enterText(f.at(5), 'Str0ngPassword!');
      await t.pump();
      await next();
      expect(find.text('Choose a plan'), findsWidgets);
      await next();
      expect(find.text('Review and send'), findsWidgets);
    });

    final screens = <String, Widget Function()>{
      'login': () => const LoginScreen(),
      'receipt detail': () => const ReceiptDetailScreen(receiptId: 'r1'),
      'new invoice': () => const NewInvoiceScreen(),
      'device detail': () => const DeviceDetailScreen(deviceId: 'd1'),
      'register device': () => const RegisterDeviceScreen(),
      'profile': () => const ProfileScreen(),
      'change password': () => const ChangePasswordScreen(),
      'customer form': () => ResourceFormScreen(customersSpec, null),
      'token form (multi select)': () => ResourceFormScreen(tokensSpec, null),
      'bill form': () => ResourceFormScreen(billsSpec, null),
      'asset form': () => ResourceFormScreen(assetsSpec, null),
      'item detail': () => ItemSheet(spec: customersSpec, item: const {'id': 'c1', 'name': 'A Customer With A Particularly Long Registered Company Name Limited', 'tin': '1234567890', 'email': 'accounts@customer-with-long-address.example.com', 'phone': '+263 77 123 4567', 'isActive': true}, onChanged: () async {}, onEdit: () {}),
      'register company': () => const RegisterScreen(),
      'confirm email code': () => const RegisterScreen(verifyEmail: 'someone.with.a.long.address@example-company.co.zw'),
      'notifications': () => const NotificationsScreen(),
      'bank payment': () => ManualPaymentScreen(method: const {'id': 'bank', 'name': 'Bank transfer', 'kind': 'manual'}, invoice: const {'id': 'i1', 'number': 'SUB-2026-000123-LONG', 'total': 1234567.89, 'currency': 'USD'}, bank: const {'bankName': 'Commercial Bank of Zimbabwe', 'accountName': 'Dara ERP (Private) Limited', 'accountNumber': '01234567890123', 'branchCode': '6101', 'instructions': 'Use the invoice number as the reference.'}),
      'ecocash payment': () => ManualPaymentScreen(method: const {'id': 'ecocash-manual', 'name': 'EcoCash (Send Money)', 'kind': 'manual', 'details': '0771234567'}, invoice: const {'id': 'i1', 'number': 'SUB-2026-000123-LONG', 'total': 1234567.89, 'currency': 'USD'}),
      'welcome tour': () => WelcomeScreen(onSignIn: () {}, onRegister: () {}),
      'forgot password': () => const ForgotPasswordScreen(email: 'someone.with.a.long.address@example-company.co.zw'),
      'terms': () => const LegalDocScreen(which: 'terms'),
      'developer': () => const DeveloperScreen(),
      'docs section': () => DocSectionScreen(sections: splitGuide(FakeApi.guide), index: 0),
      'error detail': () => const ErrorDetailScreen({'id': 1, 'severity': 'Critical', 'message': 'A long message about what failed in the gateway when loading the certificate', 'occurrences': 3}),
      'template editor': () => const TemplateEditorScreen(null),
      'invoice builder': () => const InvoiceBuilderScreen(templateId: 't1'),
    };
    for (final x in screens.entries) {
      testWidgets('${x.key} fits: ${e.key}', (t) async {
        await _pump(t, x.value, size: e.value.$1, textScale: e.value.$2);
        expect(tester(t), isNull);
      });
    }

    for (final slug in ['dashboard', 'devices', 'receipts', 'days', 'customers', 'accounting/reports/trial-balance', 'accounting/cashbook', 'accounting/bills', 'admin/analytics', 'billing', 'compliance', 'admin/audit', 'admin/logs', 'referrals', 'admin/referral-program', 'docs', 'admin/templates', 'admin/settings', 'admin/roles', 'admin/modules', 'receipts/import', 'devices/register', 'receipts/new', 'admin/banks', 'admin/users', 'admin/branches', 'approvals', 'approvals/matrix', 'admin/revenue', 'overview', 'admin/subscriptions']) {
      testWidgets('$slug fits: ${e.key}', (t) async {
        await _pump(t, () => Scaffold(body: buildModule(slug, _menus.first, _session())), size: e.value.$1, textScale: e.value.$2);
        expect(tester(t), isNull);
      });
    }
  }
}

/// A Flutter layout overflow is reported as an exception; this returns it (or null when the screen drew fine).
Object? tester(WidgetTester t) { final e = t.takeException(); if (e != null) { debugPrint(e is FlutterError ? e.toStringDeep() : e.toString()); } return e; }
