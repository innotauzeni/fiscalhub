import 'dart:typed_data';

import 'package:dio/dio.dart';

import 'auth.dart';
import 'config.dart';
import 'models.dart';

/// What went wrong, in words a person can act on. [code] is the gateway's error code (for example PAYMENT_REQUIRED or MFA_SETUP_REQUIRED).
class ApiException implements Exception {
  ApiException(this.message, {this.status, this.code});
  final String message;
  final int? status;
  final String? code;
  @override
  String toString() => message;

  static ApiException from(Object e) {
    if (e is ApiException) return e;
    if (e is DioException) {
      final data = e.response?.data;
      if (data is Map) {
        final title = (data['title'] ?? data['message'] ?? data['detail'])?.toString();
        final code = data['errorCode']?.toString();
        if (title != null && title.isNotEmpty) return ApiException(title, status: e.response?.statusCode, code: code);
      }
      final s = e.response?.statusCode;
      if (s == null) {
        return ApiException(e.type == DioExceptionType.connectionTimeout || e.type == DioExceptionType.receiveTimeout
            ? 'The server took too long to answer. Try again.'
            : 'Cannot reach the server. Check your internet connection.');
      }
      if (s == 401) return ApiException('Your session has ended. Sign in again.', status: s, code: 'UNAUTHORIZED');
      if (s == 403) return ApiException('You do not have access to that.', status: s, code: 'FORBIDDEN');
      if (s == 404) return ApiException('That was not found.', status: s, code: 'NOT_FOUND');
      if (s == 429) return ApiException('Too many attempts. Wait a minute and try again.', status: s, code: 'RATE_LIMITED');
      return ApiException('The server could not do that (error $s).', status: s);
    }
    return ApiException(e.toString());
  }
}

/// The gateway's REST API. Adds the bearer token, refreshes it when it is about to expire (or the server says it has), and turns every failure into an [ApiException].
class Api {
  Api(this.config, this.auth) {
    _dio = Dio(BaseOptions(baseUrl: config.baseUrl, connectTimeout: const Duration(seconds: 20), receiveTimeout: const Duration(seconds: 90), headers: {'Accept': 'application/json'}));
    _dio.interceptors.add(InterceptorsWrapper(onRequest: _onRequest, onError: _onError));
  }

  final AppConfig config;
  final AuthController auth;
  late final Dio _dio;

  Future<void> _onRequest(RequestOptions o, RequestInterceptorHandler h) async {
    o.baseUrl = config.baseUrl;
    if (o.extra['anonymous'] != true && auth.session != null) {
      final t = await auth.validToken();
      if (t != null) o.headers['Authorization'] = 'Bearer $t';
    }
    h.next(o);
  }

  Future<void> _onError(DioException e, ErrorInterceptorHandler h) async {
    final o = e.requestOptions;
    if (e.response?.statusCode == 401 && o.extra['anonymous'] != true && o.extra['retried'] != true && auth.session != null) {
      if (await auth.refresh()) {
        o.extra['retried'] = true;
        o.headers['Authorization'] = 'Bearer ${auth.session!.accessToken}';
        try { return h.resolve(await _dio.fetch(o)); } on DioException catch (e2) { return h.next(e2); }
      }
      auth.expire();
    }
    h.next(e);
  }

  Future<T> _run<T>(Future<Response<dynamic>> Function() call, T Function(dynamic) map) async {
    try {
      final r = await call();
      final d = r.data;
      if (r.statusCode == 202 && d is Map && d['approvalRequired'] == true) {
        final role = (d['request'] as Map?)?['waitingForRole'];
        ApprovalNotice.last = 'Sent for approval: waiting for ${role ?? 'an approver'}. You will be told when it is decided.';
      }
      return map(d);
    } catch (e) {
      throw ApiException.from(e);
    }
  }

  Map<String, dynamic>? _q(Map<String, dynamic>? q) {
    if (q == null) return null;
    final out = <String, dynamic>{};
    q.forEach((k, v) { if (v != null && v.toString().isNotEmpty) out[k] = v is bool ? v.toString() : v; });
    return out;
  }

  /// GET that returns JSON (a map, a list or a scalar).
  Future<dynamic> get(String path, {Map<String, dynamic>? query}) => _run(() => _dio.get(path, queryParameters: _q(query)), (d) => d);

  Future<Json> getMap(String path, {Map<String, dynamic>? query}) async {
    final d = await get(path, query: query);
    return d is Map ? Map<String, dynamic>.from(d) : <String, dynamic>{};
  }

  /// A list, whether the server sent a bare array or an object holding it under [key] (or "items").
  Future<List<Json>> getList(String path, {Map<String, dynamic>? query, String? key}) async {
    final d = await get(path, query: query);
    dynamic list = d;
    if (d is Map) list = d[key ?? 'items'] ?? d.values.whereType<List>().firstOrNull ?? [];
    return (list as List).map((e) => e is Map ? Map<String, dynamic>.from(e) : <String, dynamic>{'value': e}).toList();
  }

  Future<dynamic> post(String path, {Object? body, Map<String, dynamic>? query}) => _run(() => _dio.post(path, data: body, queryParameters: _q(query)), (d) => d);
  Future<dynamic> put(String path, {Object? body}) => _run(() => _dio.put(path, data: body), (d) => d);
  Future<dynamic> delete(String path) => _run(() => _dio.delete(path), (d) => d);

  /// A file (PDF, picture, spreadsheet, backup).
  Future<Uint8List> bytes(String path, {Map<String, dynamic>? query, String method = 'GET', Object? body}) =>
      _run(() => _dio.request(path, data: body, queryParameters: _q(query), options: Options(method: method, responseType: ResponseType.bytes)), (d) => Uint8List.fromList(List<int>.from(d as List)));

  /// Multipart upload.
  Future<dynamic> upload(String path, FormData form, {Map<String, dynamic>? query}) => _run(() => _dio.post(path, data: form, queryParameters: _q(query)), (d) => d);

  // ---- calls that need no session (sign-in) ----
  Future<dynamic> anonymousPost(String path, Object body) =>
      _run(() => _dio.post(path, data: body, options: Options(extra: {'anonymous': true})), (d) => d);
  Future<dynamic> anonymousGet(String path) => _run(() => _dio.get(path, options: Options(extra: {'anonymous': true})), (d) => d);
}

/// Set when the last call was held by the approval matrix (HTTP 202 "approvalRequired"): what to tell the person instead of "done".
class ApprovalNotice {
  static String? last;
  static String? take() { final m = last; last = null; return m; }
}
