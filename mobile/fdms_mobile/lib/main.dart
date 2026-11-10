import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import 'core/api.dart';
import 'core/auth.dart';
import 'core/config.dart';
import 'screens/login.dart';
import 'screens/profile.dart';
import 'screens/shell.dart';
import 'ui/kit.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  final config = await AppConfig.load();
  final auth = AuthController(config);
  final api = Api(config, auth);
  runApp(MultiProvider(
    providers: [
      Provider<AppConfig>.value(value: config),
      ChangeNotifierProvider<AuthController>.value(value: auth),
      Provider<Api>.value(value: api),
    ],
    child: const FdmsApp(),
  ));
  auth.restore();
}

class FdmsApp extends StatelessWidget {
  const FdmsApp({super.key});

  @override
  Widget build(BuildContext context) => MaterialApp(
        title: 'FiscalHub',
        debugShowCheckedModeBanner: false,
        theme: buildTheme(Brightness.light),
        darkTheme: buildTheme(Brightness.dark),
        themeMode: ThemeMode.system,
        home: const Gate(),
      );
}

/// Decides what to show: the splash while the saved session is restored, sign-in, the forced password change, the forced security set-up, or the app.
class Gate extends StatelessWidget {
  const Gate({super.key});

  @override
  Widget build(BuildContext context) {
    final auth = context.watch<AuthController>();
    if (auth.restoring) return const Scaffold(body: Center(child: CircularProgressIndicator()));
    final s = auth.session;
    if (s == null) return const AuthEntry();
    if (s.user.mustChangePassword) return const ChangePasswordScreen(forced: true);
    if (s.mfaSetupRequired) return const ProfileScreen(forcedSecurity: true);
    return const Shell();
  }
}
