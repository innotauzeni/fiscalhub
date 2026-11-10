import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import 'api.dart';
import 'config.dart';
import 'models.dart';

/// The signed-in session. The access token lives in memory; the refresh token is kept in the phone's secure storage so the app opens straight into the session.
/// The menus and permissions come from the sign-in answer, exactly as on the web console, so the app shows only what the person's roles allow.
class AuthController extends ChangeNotifier {
  AuthController(this.config);

  final AppConfig config;
  static const _store = FlutterSecureStorage();
  static const _key = 'fdms.refresh';

  Session? session;
  Future<bool>? _refreshing;
  bool restoring = true;

  bool get signedIn => session != null;
  UserInfo? get user => session?.user;
  bool can(String? permission) => permission == null || permission.isEmpty || (session?.permissions.contains(permission) ?? false);
  bool canAny(Iterable<String> p) => p.any(can);

  Dio _raw() => Dio(BaseOptions(baseUrl: config.baseUrl, connectTimeout: const Duration(seconds: 20), receiveTimeout: const Duration(seconds: 60)));

  Future<Json> _post(String path, Object body) async {
    try {
      final r = await _raw().post(path, data: body);
      return Map<String, dynamic>.from(r.data as Map);
    } catch (e) {
      throw ApiException.from(e);
    }
  }

  Future<void> _keep(Session s) async {
    session = s;
    try { await _store.write(key: _key, value: s.refreshToken); } catch (_) { /* storage unavailable: the session lasts until the app closes */ }
    notifyListeners();
  }

  /// Step one: email/username and password. With two-step sign-in on, the answer is a token to send with the code.
  Future<LoginResult> login(String identifier, String password) async {
    final j = await _post('/api/v1/auth/login', {'identifier': identifier.trim(), 'password': password});
    if (j['mfaRequired'] == true) return LoginResult(mfaToken: j['mfaToken']?.toString());
    final s = Session.fromJson(j);
    await _keep(s);
    return LoginResult(session: s);
  }

  /// Step two: the 6-digit code from the authenticator app (or a recovery code).
  Future<Session> loginMfa(String mfaToken, String code) async {
    final s = Session.fromJson(await _post('/api/v1/auth/login/mfa', {'mfaToken': mfaToken, 'code': code.trim()}));
    await _keep(s);
    return s;
  }

  /// Turns the stored refresh token back into a session when the app starts.
  Future<void> restore() async {
    String? token;
    try { token = await _store.read(key: _key); } catch (_) { token = null; }
    if (token != null && token.isNotEmpty) {
      session = Session(accessToken: '', refreshToken: token, expiresAt: DateTime.fromMillisecondsSinceEpoch(0), user: UserInfo(id: '', email: '', name: '', surname: '', roles: const [], mustChangePassword: false),
          permissions: {}, sections: [], menus: []);
      if (!await refresh()) session = null;
    }
    restoring = false;
    notifyListeners();
  }

  /// Refresh tokens rotate, so parallel refreshes would look like theft: everyone shares one in-flight request.
  Future<bool> refresh() {
    final s = session;
    if (s == null || s.refreshToken.isEmpty) return Future.value(false);
    return _refreshing ??= () async {
      try {
        final fresh = Session.fromJson(await _post('/api/v1/auth/refresh', {'refreshToken': s.refreshToken}));
        await _keep(fresh);
        return true;
      } catch (_) {
        session = null;
        try { await _store.delete(key: _key); } catch (_) {}
        notifyListeners();
        return false;
      } finally {
        _refreshing = null;
      }
    }();
  }

  /// The access token to send, refreshed first when it is about to expire.
  Future<String?> validToken() async {
    final s = session;
    if (s == null) return null;
    if (s.accessToken.isEmpty || s.expiresAt.difference(DateTime.now()).inSeconds < 30) {
      if (!await refresh()) return null;
    }
    return session?.accessToken;
  }

  Future<void> logout() async {
    final s = session;
    if (s != null && s.refreshToken.isNotEmpty) {
      try { await _raw().post('/api/v1/auth/logout', data: {'refreshToken': s.refreshToken}); } catch (_) {}
    }
    await _clear();
  }

  /// The server no longer accepts this session.
  void expire() { _clear(); }

  Future<void> _clear() async {
    session = null;
    try { await _store.delete(key: _key); } catch (_) {}
    notifyListeners();
  }

  /// After a password change the server hands back a fresh session.
  Future<void> adopt(Json j) => _keep(Session.fromJson(j));
}
