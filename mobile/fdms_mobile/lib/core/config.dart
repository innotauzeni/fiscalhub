import 'package:shared_preferences/shared_preferences.dart';

/// The gateway the app talks to. Compiled in, and changeable on the sign-in screen (for a test server).
const String kDefaultBaseUrl = String.fromEnvironment('API_BASE_URL', defaultValue: 'https://fiscalapi.daraerp.co.zw');

class AppConfig {
  AppConfig._(this._prefs, this.baseUrl);
  final SharedPreferences _prefs;
  String baseUrl;

  static Future<AppConfig> load() async {
    final prefs = await SharedPreferences.getInstance();
    return AppConfig._(prefs, _clean(prefs.getString('baseUrl') ?? kDefaultBaseUrl));
  }

  static String _clean(String url) => url.trim().replaceAll(RegExp(r'/+$'), '');

  Future<void> setBaseUrl(String url) async {
    baseUrl = _clean(url.isEmpty ? kDefaultBaseUrl : url);
    await _prefs.setString('baseUrl', baseUrl);
  }

  String? getString(String key) => _prefs.getString(key);
  Future<void> setString(String key, String value) => _prefs.setString(key, value);
  Future<void> remove(String key) => _prefs.remove(key);
}
