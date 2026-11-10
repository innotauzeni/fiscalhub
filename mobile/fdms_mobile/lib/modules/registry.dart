import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../core/api.dart';
import '../core/models.dart';
import '../ui/kit.dart';
import 'accounting.dart';
import 'approvals.dart';
import 'dashboard.dart';
import 'days.dart';
import 'devices.dart';
import 'access.dart';
import 'docs.dart';
import 'importer.dart';
import 'bridge.dart';
import 'integrations.dart';
import 'referrals.dart';
import 'settings.dart';
import 'system_views.dart';
import 'templates.dart';
import 'more.dart';
import 'receipts.dart';
import 'resource.dart';
import 'specs.dart';

/// Turns a menu entry (its slug, or a sub-page route) into the screen for it. The menu itself comes from the gateway, so a role sees only what it may use;
/// every slug the console knows has a native screen here, so everything the web console does is done in the app.
Widget buildModule(String route, MenuNode menu, Session session) {
  switch (route) {
    case 'dashboard': return const DashboardScreen();
    case 'overview': return const PlatformDashboardScreen();
    case 'devices': return const DevicesScreen();
    case 'devices/register': return const RegisterDeviceScreen(embedded: true);
    case 'receipts': return const ReceiptsScreen();
    case 'receipts/new': return const NewInvoiceScreen(embedded: true);
    case 'receipts/import': return const ImporterScreen();
    case 'pos': return const NewInvoiceScreen(embedded: true);
    case 'days': return const DaysScreen();
    case 'offline': return const OfflineScreen();
    case 'customers': return ResourceScreen(customersSpec);
    case 'products': return ResourceScreen(productsSpec);
    case 'quotes': return ResourceScreen(quotesSpec);
    case 'recurring': return ResourceScreen(recurringSpec);
    case 'reports': return const SalesReportScreen();
    case 'ai-reports': return const AiReportsScreen();
    case 'compliance': return const ComplianceScreen();
    case 'health': return const HealthScreen();
    case 'alerts': return const AlertsScreen();
    case 'tokens': return ResourceScreen(tokensSpec);
    case 'webhooks': return const WebhooksScreen();
    case 'activity': return const ActivityScreen();
    case 'docs': return const DocsScreen();
    case 'billing': return const BillingScreen();

    case 'admin/branches': return ResourceScreen(branchesSpec);
    case 'admin/banks': return ResourceScreen(banksSpec);
    case 'admin/users': return ResourceScreen(usersSpec);
    case 'admin/templates': return const TemplatesScreen();

    case 'accounting/journals': return ResourceScreen(journalsSpec);
    case 'accounting/accounts': return ResourceScreen(accountsSpec);
    case 'accounting/cashbook': return const CashbookScreen();
    case 'accounting/reconcile': return const ReconcileScreen();
    case 'accounting/suppliers': return ResourceScreen(suppliersSpec);
    case 'accounting/bills': return ResourceScreen(billsSpec);
    case 'accounting/receivables': return ResourceScreen(receivablesSpec);
    case 'accounting/assets': return ResourceScreen(assetsSpec);
    case 'accounting/vat': return const VatScreen();
    case 'accounting/budgets': return ResourceScreen(budgetsSpec);
    case 'accounting/documents': return const DocumentsScreen();

    case 'admin/clients': return ResourceScreen(clientsSpec);
    case 'admin/currencies': return ResourceScreen(currenciesSpec);
    case 'admin/roles': return ResourceScreen(rolesAdminSpec);
    case 'admin/audit': return const AuditScreen();
    case 'admin/logs': return const ErrorLogsScreen();
    case 'admin/applications': return ResourceScreen(applicationsSpec);
    case 'admin/plans': return ResourceScreen(plansSpec);
    case 'admin/subscriptions': return const _SubscriptionsTabs();
    case 'admin/payments': return ResourceScreen(paymentsSpec);
    case 'admin/go-live': return ResourceScreen(goLiveSpec);
    case 'admin/backups': return const BackupsScreen();
    case 'admin/analytics': return const AnalyticsScreen();
    case 'admin/partners': return ResourceScreen(partnersSpec);
    case 'admin/modules': return const ModulesAdminScreen();
    case 'admin/settings': return const SettingsScreen();
    case 'admin/ai': return const AiSettingsLoader();
    case 'admin/referral-program': return const ReferralProgramScreen();
    case 'referrals': return const ReferralScreen();
    case 'approvals': return const ApprovalsScreen();
    case 'approvals/matrix': return const ApprovalMatrixScreen();
    case 'admin/revenue': return const RevenueScreen();
    case 'integrations': return const ZohoScreen();
    case 'admin/integrations': return const IntegrationsMonitorScreen();

    case 'partner/overview': return const PartnerOverviewScreen();
    case 'partner/clients': return ResourceScreen(partnerClientsSpec);
    case 'partner/commission': return const PartnerCommissionScreen();
    case 'partner/bridge': return const PartnerBridgeScreen();
  }
  if (route.startsWith('accounting/reports/')) return AccountingReportScreen(route.substring('accounting/reports/'.length));
  if (route == 'accounting/reports') return const AccountingReportScreen('trial-balance');
  return UnknownModuleScreen(menu.name);
}

/// Backups: make one now, and download the files (as the gateway's compressed dump or a plain .sql).
class BackupsScreen extends StatefulWidget {
  const BackupsScreen({super.key});
  @override
  State<BackupsScreen> createState() => _BackupsScreenState();
}

class _BackupsScreenState extends State<BackupsScreen> {
  final _key = GlobalKey<ResourceScreenState>();
  bool _busy = false;

  Future<void> _backup() async {
    setState(() => _busy = true);
    await guarded(context, () async { await context.read<Api>().post('/api/v1/backups'); }, done: 'Backup made.');
    if (mounted) setState(() => _busy = false);
    _key.currentState?.reload();
  }

  @override
  Widget build(BuildContext context) => Column(children: [
        Padding(padding: const EdgeInsets.fromLTRB(16, 12, 16, 0), child: Row(children: [
          Expanded(child: FilledButton.icon(onPressed: _busy ? null : _backup, icon: _busy ? const SizedBox(width: 16, height: 16, child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white)) : const Icon(Icons.cloud_upload_outlined), label: Text(_busy ? 'Backing up…' : 'Back up now'))),
        ])),
        Expanded(child: ResourceScreen(backupsSpec, key: _key)),
      ]);
}

/// Subscriptions, with the unpaid demo accounts (and when they will be deleted) one tab away.
class _SubscriptionsTabs extends StatelessWidget {
  const _SubscriptionsTabs();
  @override
  Widget build(BuildContext context) => DefaultTabController(
        length: 2,
        child: Column(children: [
          const TabBar(tabs: [Tab(text: 'Subscriptions'), Tab(text: 'Unpaid demos')]),
          Expanded(child: TabBarView(children: [ResourceScreen(subscriptionsSpec), ResourceScreen(demoAccountsSpec)])),
        ]),
      );
}
