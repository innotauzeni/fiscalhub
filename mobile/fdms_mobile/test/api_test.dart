import 'package:dio/dio.dart';
import 'package:fdms_mobile/core/api.dart';
import 'package:fdms_mobile/core/models.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  test('the gateway\'s problem answer becomes a readable message with its code', () {
    final e = DioException(requestOptions: RequestOptions(path: '/x'), response: Response(requestOptions: RequestOptions(path: '/x'), statusCode: 402, data: {'title': 'Your account is waiting for its first payment.', 'errorCode': 'PAYMENT_REQUIRED', 'status': 402}));
    final a = ApiException.from(e);
    expect(a.message, startsWith('Your account is waiting'));
    expect(a.code, 'PAYMENT_REQUIRED');
    expect(a.status, 402);
  });

  test('no connection reads as a plain sentence', () {
    final e = DioException(requestOptions: RequestOptions(path: '/x'), type: DioExceptionType.connectionError);
    expect(ApiException.from(e).message, contains('Cannot reach the server'));
  });

  test('the sign-in answer becomes a session with permissions and a menu', () {
    final s = Session.fromJson({
      'accessToken': 'a', 'refreshToken': 'r', 'expiresAt': '2030-01-01T00:00:00Z',
      'user': {'id': '1', 'email': 'ada@x.test', 'name': 'Ada', 'surname': 'Admin', 'roles': ['ClientAdmin'], 'mustChangePassword': false},
      'permissions': ['devices.view'],
      'sectionMenus': [{'name': 'Fiscalisation', 'color': '#2563eb', 'children': [{'id': 1, 'name': 'Devices', 'slugname': 'devices', 'icon': 'cpu', 'children': []}]}],
      'menus': [{'id': 1, 'name': 'Devices', 'slugname': 'devices', 'icon': 'cpu', 'children': []}],
      'mfaSetupRequired': true,
    });
    expect(s.user.displayName, 'Ada Admin');
    expect(s.permissions, {'devices.view'});
    expect(s.sections.single.children.single.slug, 'devices');
    expect(s.mfaSetupRequired, isTrue);
  });
}
