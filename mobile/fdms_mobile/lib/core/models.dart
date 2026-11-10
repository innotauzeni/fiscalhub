/// Shapes returned by the gateway's sign-in. Everything else is read as plain JSON maps.
typedef Json = Map<String, dynamic>;

class SubmoduleNode {
  SubmoduleNode(this.id, this.name, this.icon, this.route, this.accessName);
  final int id;
  final String name, icon, route;
  final String? accessName;
  factory SubmoduleNode.fromJson(Json j) => SubmoduleNode(j['id'] ?? 0, j['name'] ?? '', j['icon'] ?? '', j['route'] ?? '', j['accessName']);
}

class MenuNode {
  MenuNode(this.id, this.name, this.slug, this.icon, this.accessName, this.children);
  final int id;
  final String name, slug, icon;
  final String? accessName;
  final List<SubmoduleNode> children;
  factory MenuNode.fromJson(Json j) => MenuNode(j['id'] ?? 0, j['name'] ?? '', j['slugname'] ?? '', j['icon'] ?? '', j['accessName'],
      ((j['children'] ?? []) as List).map((c) => SubmoduleNode.fromJson(c as Json)).toList());
}

class SectionNode {
  SectionNode(this.name, this.color, this.children);
  final String name, color;
  final List<MenuNode> children;
  factory SectionNode.fromJson(Json j) => SectionNode(j['name'] ?? '', j['color'] ?? '#475569', ((j['children'] ?? []) as List).map((c) => MenuNode.fromJson(c as Json)).toList());
}

class UserInfo {
  UserInfo({required this.id, this.clientId, this.clientName, required this.email, required this.name, required this.surname, required this.roles, required this.mustChangePassword});
  final String id, email, name, surname;
  final String? clientId, clientName;
  final List<String> roles;
  final bool mustChangePassword;
  String get displayName => ('$name $surname').trim().isEmpty ? email : ('$name $surname').trim();
  String get initials => displayName.split(' ').where((p) => p.isNotEmpty).take(2).map((p) => p[0].toUpperCase()).join();
  factory UserInfo.fromJson(Json j) => UserInfo(
        id: j['id'] ?? '', clientId: j['clientId'], clientName: j['clientName'], email: j['email'] ?? '', name: j['name'] ?? '', surname: j['surname'] ?? '',
        roles: ((j['roles'] ?? []) as List).map((e) => e.toString()).toList(), mustChangePassword: j['mustChangePassword'] ?? false);
}

class Session {
  Session({required this.accessToken, required this.refreshToken, required this.expiresAt, required this.user, required this.permissions, required this.sections,
      required this.menus, this.billingLocked = false, this.demoOnly = false, this.mfaSetupRequired = false});
  String accessToken, refreshToken;
  DateTime expiresAt;
  final UserInfo user;
  final Set<String> permissions;
  final List<SectionNode> sections;
  final List<MenuNode> menus;
  final bool billingLocked, demoOnly, mfaSetupRequired;

  factory Session.fromJson(Json j) => Session(
        accessToken: j['accessToken'] ?? '', refreshToken: j['refreshToken'] ?? '', expiresAt: DateTime.tryParse(j['expiresAt'] ?? '')?.toLocal() ?? DateTime.now(),
        user: UserInfo.fromJson((j['user'] ?? {}) as Json), permissions: ((j['permissions'] ?? []) as List).map((e) => e.toString()).toSet(),
        sections: ((j['sectionMenus'] ?? []) as List).map((e) => SectionNode.fromJson(e as Json)).toList(),
        menus: ((j['menus'] ?? []) as List).map((e) => MenuNode.fromJson(e as Json)).toList(),
        billingLocked: j['billingLocked'] ?? false, demoOnly: j['demoOnly'] ?? false, mfaSetupRequired: j['mfaSetupRequired'] ?? false);
}

/// The first answer of a sign-in: either a session, or "now enter your two-step code".
class LoginResult {
  LoginResult({this.session, this.mfaToken});
  final Session? session;
  final String? mfaToken;
  bool get needsCode => session == null && mfaToken != null;
}
