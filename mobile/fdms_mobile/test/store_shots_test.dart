// Store listing pictures: phone screenshots of the real screens with tidy demo data, and the feature graphic.
// Not part of the normal test run. Make them with:
//   flutter test test/store_shots_test.dart --update-goldens --dart-define=STORE_SHOTS=true
// The PNGs land in test/store/.
import 'dart:io';

import 'package:fdms_mobile/core/api.dart';
import 'package:fdms_mobile/core/auth.dart';
import 'package:fdms_mobile/core/config.dart';
import 'package:fdms_mobile/core/models.dart';
import 'package:fdms_mobile/modules/integrations.dart';
import 'package:fdms_mobile/modules/invoice_builder.dart';
import 'package:fdms_mobile/modules/receipts.dart';
import 'package:fdms_mobile/modules/registry.dart';
import 'package:fdms_mobile/screens/notifications.dart';
import 'package:fdms_mobile/screens/shell.dart';
import 'package:fdms_mobile/screens/welcome.dart';
import 'package:fdms_mobile/ui/kit.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'responsive_test.dart' show FakeApi;

const _enabled = bool.fromEnvironment('STORE_SHOTS');

/// The same in-memory gateway as the layout tests, with believable data for a shop in Harare.
class StoreApi extends FakeApi {
  StoreApi(super.config, super.auth);

  static final _device = {
    'id': 'd1', 'zimraDeviceId': 39178, 'name': 'Till 1, front counter', 'environment': 'Production', 'serialNo': 'FH-0001', 'taxPayerName': 'Moyo Retail (Pvt) Ltd', 'branchName': 'Harare CBD',
    'isActive': true, 'operatingMode': 'Online', 'certificateValidTill': '2027-08-01T00:00:00Z', 'taxes': [{'taxId': 1, 'taxName': 'Standard rated 15%', 'taxPercent': 15}],
  };

  static Map<String, dynamic> _r(String id, String no, String type, String status, double total, String when) =>
      {'id': id, 'invoiceNo': no, 'type': type, 'currency': 'USD', 'status': status, 'fiscalDayNo': 41, 'receiptDate': when, 'total': total};

  static final _receipts = [
    _r('r1', 'INV-1048', 'FiscalInvoice', 'Submitted', 862.50, '2026-10-12T09:41:00Z'),
    _r('r2', 'INV-1047', 'FiscalInvoice', 'Submitted', 145.00, '2026-10-12T09:12:00Z'),
    _r('r3', 'CN-0031', 'CreditNote', 'Submitted', 57.50, '2026-10-12T08:55:00Z'),
    _r('r4', 'INV-1046', 'FiscalInvoice', 'Submitted', 2300.00, '2026-10-12T08:20:00Z'),
    _r('r5', 'INV-1045', 'FiscalInvoice', 'Pending', 74.75, '2026-10-12T07:58:00Z'),
    _r('r6', 'INV-1044', 'FiscalInvoice', 'Submitted', 1265.00, '2026-10-11T16:30:00Z'),
    _r('r7', 'INV-1043', 'FiscalInvoice', 'Submitted', 310.50, '2026-10-11T15:02:00Z'),
  ];

  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query}) async {
    switch (path) {
      case '/api/v1/dashboard':
        return {'devices': 2, 'openDays': 2, 'receiptsToday': 38, 'todayTotals': [{'currency': 'USD', 'total': 9842.75, 'count': 34}, {'currency': 'ZWG', 'total': 15320.00, 'count': 4}],
          'pending': 1, 'held': 0, 'offlineIssued': 0, 'rejected': 0, 'submitted': 1287,
          'deviceList': [{..._device, 'dayStatus': 'FiscalDayOpened', 'pendingReceipts': 1}, {..._device, 'id': 'd2', 'zimraDeviceId': 39179, 'name': 'Till 2, Avondale', 'branchName': 'Avondale', 'dayStatus': 'FiscalDayOpened', 'pendingReceipts': 0}],
          'recent': _receipts.take(4).toList(), 'alerts': <String>[],
          'trend': [for (var i = 0; i < 14; i++) {'date': '2026-09-${29 + i > 30 ? (i - 1).toString().padLeft(2, '0') : 29 + i}', 'count': 22 + (i * 7) % 19, 'rejected': 0}],
          'checklist': {'done': 4, 'total': 4, 'steps': <Map<String, dynamic>>[]}};
      case '/api/v1/devices':
        return [_device, {..._device, 'id': 'd2', 'zimraDeviceId': 39179, 'name': 'Till 2, Avondale', 'branchName': 'Avondale'}];
      case '/api/v1/receipts':
        return {'items': _receipts, 'total': _receipts.length, 'page': 1, 'pageSize': 25};
      case '/api/v1/receipts/r1/detail':
        return {'receipt': {..._receipts.first, 'deviceId': 'd1', 'receiptCounter': 112, 'receiptGlobalNo': 20431, 'fdmsReceiptId': 9384011, 'qrUrl': 'https://fdms.zimra.co.zw/0000039178121020260000020431A1B2C3D4E5F60718', 'validationErrors': <Map<String, dynamic>>[]},
          'lines': [
            {'receiptLineName': 'Body lotion 500 ml', 'receiptLineQuantity': 24, 'receiptLinePrice': 8.5, 'receiptLineTotal': 204.0, 'taxPercent': 15, 'receiptLineHSCode': '33049900'},
            {'receiptLineName': 'Face cream 50 g', 'receiptLineQuantity': 30, 'receiptLinePrice': 12.0, 'receiptLineTotal': 360.0, 'taxPercent': 15, 'receiptLineHSCode': '33049900'},
            {'receiptLineName': 'Hair oil 250 ml', 'receiptLineQuantity': 25, 'receiptLinePrice': 11.94, 'receiptLineTotal': 298.5, 'taxPercent': 15, 'receiptLineHSCode': '33059000'},
          ],
          'payments': [{'moneyTypeCode': 'BankTransfer', 'paymentAmount': 862.5}], 'buyer': {'buyerRegisterName': 'Glow Salon (Pvt) Ltd', 'buyerTIN': '2000123456', 'vatNumber': '220099887'}};
      case '/api/v1/notifications':
        return {'unread': 2, 'items': [
          {'id': 'n1', 'kind': 'integration', 'title': 'Zoho invoice INV-000214 fiscalised', 'body': 'Fiscal number 39178/20431 written back to Zoho Books.', 'severity': 'info', 'createdAt': '2026-10-12T09:41:00Z', 'read': false},
          {'id': 'n2', 'kind': 'approval', 'title': 'Credit note CN-0031 approved', 'body': 'Approved by Tendai Moyo (Accountant).', 'severity': 'info', 'createdAt': '2026-10-12T08:56:00Z', 'read': false},
          {'id': 'n3', 'kind': 'certificate', 'title': 'Device certificate renews in 30 days', 'body': 'Till 1 certificate is valid until 1 Aug 2027.', 'severity': 'warn', 'createdAt': '2026-10-11T07:00:00Z', 'read': true},
          {'id': 'n4', 'kind': 'dayclose', 'title': 'Fiscal day 40 closed', 'body': 'ZIMRA accepted the day close for Till 1.', 'severity': 'info', 'createdAt': '2026-10-11T22:00:00Z', 'read': true},
        ]};
      case '/api/v1/integrations/zoho':
        return {'available': true, 'platformApp': true, 'regions': [{'key': 'com', 'label': 'zoho.com'}], 'id': 'z1', 'status': 'Connected', 'organizationId': '942600556', 'organizationName': 'Moyo Retail (Pvt) Ltd',
          'organizations': <Map<String, dynamic>>[], 'accountsServer': 'https://accounts.zoho.com', 'connectedBy': 'ada@moyoretail.co.zw', 'connectedAt': '2026-10-12T07:00:00Z', 'lastTokenRefreshAt': '2026-10-12T09:40:00Z',
          'accessTokenExpiresAt': '2026-10-12T10:40:00Z', 'lastSyncAt': '2026-10-12T09:41:00Z', 'apiCallsToday': 64, 'apiCallsTotal': 412, 'autoFiscalise': true, 'deviceId': 'd1', 'defaultHsCode': '33049900',
          'defaultMoneyType': 'Credit', 'postBack': true, 'startDate': '2026-10-01T00:00:00Z', 'webhookUrl': 'https://fiscalapi.daraerp.co.zw/api/v1/integrations/zoho/hook/…',
          'counts': {'pending': 1, 'fiscalised': 57, 'failed': 0, 'skipped': 2, 'fiscalisedThisMonth': 57, 'notPostedBack': 0}, 'pricing': {'charge': false, 'monthlyFee': 10, 'currency': 'USD', 'includedDocuments': 200, 'pricePerExtraDocument': 0.05}};
      case '/api/v1/integrations/zoho/documents':
        Map<String, dynamic> d(String no, String customer, double total, String status, {String kind = 'invoice', String? fiscal}) => {'id': no, 'kind': kind, 'externalId': no, 'externalNumber': no,
          'customerName': customer, 'documentDate': '2026-10-12T00:00:00Z', 'total': total, 'currency': 'USD', 'externalStatus': 'sent', 'status': status, 'fiscalInvoiceNo': fiscal, 'attempts': 1,
          'postedBackAt': fiscal == null ? null : '2026-10-12T09:41:00Z', 'changedAfterFiscalised': false, 'createdAt': '2026-10-12T09:30:00Z'};
        return {'items': [
          d('INV-000214', 'Glow Salon (Pvt) Ltd', 862.5, 'Fiscalised', fiscal: '39178/20431'),
          d('INV-000213', 'Kudzi Pharmacy', 145.0, 'Fiscalised', fiscal: '39178/20430'),
          d('CN-000012', 'Glow Salon (Pvt) Ltd', 57.5, 'Fiscalised', kind: 'creditnote', fiscal: '39178/20429'),
          d('INV-000215', 'Harare Spa & Wellness', 410.0, 'Pending'),
        ], 'total': 4, 'page': 1, 'pageSize': 25};
    }
    if (path == '/api/v1/templates') {
      final list = (await super.get(path, query: query)) as List;
      final t = Map<String, dynamic>.from(list.first as Map);
      final settings = Map<String, dynamic>.from(t['settings'] as Map);
      final canvas = Map<String, dynamic>.from(settings['canvas'] as Map);
      // a wider title box: the desktop serif font used here is wider than the phone's
      canvas['elements'] = [for (final e in canvas['elements'] as List) (e as Map)['field'] == 'title' ? {...e, 'x': 30, 'w': 150} : e];
      return [{...t, 'name': 'Moyo Retail A4', 'branchName': 'Harare CBD', 'settings': {...settings, 'canvas': canvas}}];
    }
    if (path == '/api/v1/templates/fields') {
      final f = (await super.get(path, query: query)) as Map<String, dynamic>;
      return {...f, 'fields': [
        for (final k in ['invoiceNo', 'company', 'buyerName', 'title', 'currency', 'date', 'address', 'tin', 'vat', 'taxRates', 'buyerAddress', 'buyerTin', 'phone', 'email'])
          {'key': k, 'label': k, 'group': 'Invoice'},
      ]};
    }
    if (path == '/api/v1/templates/sample') {
      return {'fields': {'invoiceNo': 'INV-1048', 'company': 'Moyo Retail', 'buyerName': 'Glow Salon (Pvt) Ltd', 'title': 'FISCAL TAX INVOICE', 'currency': 'USD', 'date': '12 Oct 2026',
          'address': '45 Samora Machel Ave, Harare', 'tin': '2000456789', 'vat': '220011223', 'taxRates': '15%', 'buyerAddress': '12 Fife Ave, Harare', 'buyerTin': '2000123456', 'phone': '+263 242 700 100', 'email': 'sales@moyoretail.co.zw'},
        'lines': [
          {'no': 1, 'description': 'Body lotion 500 ml', 'hsCode': '33049900', 'quantity': 24, 'unitPrice': 8.5, 'taxPercent': 15, 'amount': 204.0, 'discount': false},
          {'no': 2, 'description': 'Face cream 50 g', 'hsCode': '33049900', 'quantity': 30, 'unitPrice': 12.0, 'taxPercent': 15, 'amount': 360.0, 'discount': false},
          {'no': 3, 'description': 'Hair oil 250 ml', 'hsCode': '33059000', 'quantity': 25, 'unitPrice': 11.94, 'taxPercent': 15, 'amount': 298.5, 'discount': false},
        ],
        'taxes': [{'label': 'VAT 15%', 'percent': 15, 'net': 750.0, 'tax': 112.5, 'gross': 862.5}], 'payments': [{'method': 'Bank transfer', 'amount': 862.5}],
        'subtotal': 750.0, 'taxTotal': 112.5, 'total': 862.5, 'currency': 'USD',
        'fiscal': {'deviceId': 39178, 'fiscalDay': 41, 'receiptCounter': 112, 'globalNo': 20431, 'zimraReceiptId': 9384011, 'verificationUrl': 'https://fdms.zimra.co.zw/verify', 'signature': 'A1B2C3D4E5F60718', 'notices': []}};
    }
    return super.get(path, query: query);
  }
}

final _menus = [
  for (final (i, s, n, icon) in [(1, 'dashboard', 'Dashboard', 'home'), (2, 'receipts', 'Receipts', 'receipt'), (3, 'devices', 'Devices', 'cpu'), (4, 'days', 'Fiscal days', 'calendar'), (5, 'customers', 'Customers', 'users'), (6, 'integrations', 'Zoho Books', 'cloud-upload')])
    MenuNode(i, n, s, icon, null, const []),
];

Session _session() => Session(
      accessToken: 't', refreshToken: 'r', expiresAt: DateTime.now().add(const Duration(hours: 1)),
      user: UserInfo(id: 'u', clientId: 'c', clientName: 'Moyo Retail (Pvt) Ltd', email: 'ada@moyoretail.co.zw', name: 'Ada', surname: 'Moyo', roles: const ['ClientAdmin'], mustChangePassword: false),
      permissions: {'devices.view', 'receipts.view', 'receipts.submit', 'customers.manage', 'day.manage', 'integrations.manage', 'notifications.view'},
      sections: [SectionNode('Fiscalisation', '#2563eb', _menus.take(5).toList()), SectionNode('Integration', '#7c3aed', _menus.skip(5).toList())], menus: _menus);

Future<void> _fonts() async {
  final sdk = Platform.environment['FLUTTER_ROOT'] ?? 'C:/Users/Admin/flutter/flutter';   // set by `flutter test`
  final dir = '$sdk/bin/cache/artifacts/material_fonts';
  Future<void> family(String name, List<String> files) async {
    final loader = FontLoader(name);
    for (final f in files) { loader.addFont(Future.value(ByteData.sublistView(File('$dir/$f').readAsBytesSync()))); }
    await loader.load();
  }
  const roboto = ['roboto-regular.ttf', 'roboto-medium.ttf', 'roboto-bold.ttf', 'roboto-black.ttf', 'roboto-italic.ttf', 'roboto-light.ttf'];
  await family('Roboto', roboto);
  // text styles without a family fall back to the test font; on a phone they are Roboto too
  await family('FlutterTest', roboto);
  await family('MaterialIcons', ['materialicons-regular.otf']);
  const win = 'C:/Windows/Fonts';
  if (File('$win/georgia.ttf').existsSync()) {
    // the designer asks Android for "serif" when a design uses Georgia
    for (final name in ['Georgia', 'serif']) {
      final loader = FontLoader(name);
      for (final f in ['georgia.ttf', 'georgiab.ttf', 'georgiai.ttf', 'georgiaz.ttf']) { loader.addFont(Future.value(ByteData.sublistView(File('$win/$f').readAsBytesSync()))); }
      await loader.load();
    }
  }
}

/// Pictures (the logo) are decoded for real before the shot is taken.
Future<void> _images(WidgetTester t) async {
  await t.runAsync(() async {
    for (final e in find.byType(Image).evaluate()) {
      await precacheImage((e.widget as Image).image, e);
    }
  });
  await t.pump();
}

Future<void> _shoot(WidgetTester t, String name, Widget Function() screen, {Size size = const Size(1080, 2160), double ratio = 3}) async {
  SharedPreferences.setMockInitialValues({'welcomeSeen': true});
  final config = await AppConfig.load();
  final auth = AuthController(config)..session = _session();
  t.view.physicalSize = size;
  t.view.devicePixelRatio = ratio;
  addTearDown(t.view.resetPhysicalSize);
  addTearDown(t.view.resetDevicePixelRatio);
  await t.pumpWidget(MultiProvider(
    providers: [Provider<AppConfig>.value(value: config), ChangeNotifierProvider<AuthController>.value(value: auth), Provider<Api>.value(value: StoreApi(config, auth))],
    child: MaterialApp(debugShowCheckedModeBanner: false, theme: _theme(), home: screen()),
  ));
  for (var i = 0; i < 6; i++) { await t.pump(const Duration(milliseconds: 150)); }
  await _images(t);
  for (var i = 0; i < 4; i++) { await t.pump(const Duration(milliseconds: 150)); }
  await expectLater(find.byType(MaterialApp), matchesGoldenFile('store/$name.png'));
}

ThemeData _theme() {
  final t = buildTheme(Brightness.light);
  TextStyle? r(TextStyle? x) => x?.copyWith(fontFamily: 'Roboto');
  WidgetStateProperty<TextStyle?>? rs(WidgetStateProperty<TextStyle?>? x) => x == null ? null : WidgetStateProperty.resolveWith((st) => r(x.resolve(st)));
  ButtonStyle? b(ButtonStyle? x) => x?.copyWith(textStyle: rs(x.textStyle) ?? WidgetStateProperty.all(const TextStyle(fontFamily: 'Roboto')));
  return t.copyWith(
    appBarTheme: t.appBarTheme.copyWith(titleTextStyle: r(t.appBarTheme.titleTextStyle)),
    filledButtonTheme: FilledButtonThemeData(style: b(t.filledButtonTheme.style)),
    outlinedButtonTheme: OutlinedButtonThemeData(style: b(t.outlinedButtonTheme.style)),
    textButtonTheme: TextButtonThemeData(style: b(t.textButtonTheme.style)),
    elevatedButtonTheme: ElevatedButtonThemeData(style: b(t.elevatedButtonTheme.style) ?? const ButtonStyle(textStyle: WidgetStatePropertyAll(TextStyle(fontFamily: 'Roboto')))),
    navigationBarTheme: t.navigationBarTheme.copyWith(labelTextStyle: rs(t.navigationBarTheme.labelTextStyle)),
  );
}

Widget _titled(String title, Widget body) => Scaffold(appBar: AppBar(title: Text(title)), body: body);

/// 1024 x 500 banner for the store page.
class FeatureGraphic extends StatelessWidget {
  const FeatureGraphic({super.key});
  @override
  Widget build(BuildContext context) => Container(
        decoration: const BoxDecoration(gradient: LinearGradient(colors: [Color(0xFF0B1E4A), Color(0xFF123C9C), Color(0xFF0F766E)], begin: Alignment.topLeft, end: Alignment.bottomRight)),
        padding: const EdgeInsets.symmetric(horizontal: 64, vertical: 48),
        child: Row(children: [
          Image.asset('assets/brand/emblem.png', width: 250, height: 250),
          const SizedBox(width: 48),
          Expanded(child: Column(mainAxisAlignment: MainAxisAlignment.center, crossAxisAlignment: CrossAxisAlignment.start, children: [
            const Text.rich(TextSpan(children: [
              TextSpan(text: 'Fiscal', style: TextStyle(color: Colors.white)),
              TextSpan(text: 'Hub', style: TextStyle(color: Color(0xFF4ADE80))),
            ]), style: TextStyle(fontSize: 72, fontWeight: FontWeight.w900, letterSpacing: -1.5)),
            const SizedBox(height: 6),
            Text('ZIMRA fiscal invoices from your phone', style: TextStyle(fontSize: 30, color: Colors.white.withValues(alpha: 0.92), fontWeight: FontWeight.w500)),
            const SizedBox(height: 26),
            Wrap(spacing: 12, runSpacing: 12, children: [
              for (final s in ['FDMS compliant', 'Zoho Books', 'Fiscal days', 'Reports & VAT'])
                Container(
                  padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 9),
                  decoration: BoxDecoration(color: Colors.white.withValues(alpha: 0.14), borderRadius: BorderRadius.circular(30), border: Border.all(color: Colors.white.withValues(alpha: 0.3))),
                  child: Text(s, style: const TextStyle(color: Colors.white, fontSize: 20, fontWeight: FontWeight.w600)),
                ),
            ]),
          ])),
        ]),
      );
}

void main() {
  setUpAll(() async {
    if (_enabled) await _fonts();
  });

  final shots = <String, Widget Function()>{
    '01-welcome': () => WelcomeScreen(onSignIn: () {}, onRegister: () {}),
    '02-dashboard': () => const Shell(),
    '03-receipts': () => _titled('Receipts', buildModule('receipts', _menus[1], _session())),
    '04-fiscal-invoice': () => const ReceiptDetailScreen(receiptId: 'r1'),
    '05-zoho-books': () => _titled('Zoho Books', buildModule('integrations', _menus[5], _session())),
    '06-invoice-designer': () => const InvoiceBuilderScreen(templateId: 't1'),
    '07-notifications': () => const NotificationsScreen(),
  };
  for (final s in shots.entries) {
    testWidgets('store screenshot ${s.key}', (t) async {
      debugDisableShadows = false;
      addTearDown(() => debugDisableShadows = true);
      await _shoot(t, s.key, s.value);
      // the invoice designer draws its page after loading; give it one more moment
      if (s.key.contains('designer')) {
        for (var i = 0; i < 20; i++) { await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 50))); await t.pump(const Duration(milliseconds: 100)); }
        await expectLater(find.byType(MaterialApp), matchesGoldenFile('store/${s.key}.png'));
      }
      debugDisableShadows = true;
    }, skip: !_enabled);
  }

  testWidgets('store feature graphic', (t) async {
    t.view.physicalSize = const Size(1024, 500);
    t.view.devicePixelRatio = 1;
    addTearDown(t.view.resetPhysicalSize);
    addTearDown(t.view.resetDevicePixelRatio);
    debugDisableShadows = false;
    await t.pumpWidget(const MaterialApp(debugShowCheckedModeBanner: false, home: Material(child: FeatureGraphic())));
    await _images(t);
    await expectLater(find.byType(MaterialApp), matchesGoldenFile('store/feature-graphic.png'));
    debugDisableShadows = true;
  }, skip: !_enabled);

  // keeps the import of ZohoScreen used even when the registry changes
  test('screens exist', () => expect(const ZohoScreen(), isA<Widget>()));
}
