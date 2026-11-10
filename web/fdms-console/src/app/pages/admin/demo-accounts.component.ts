import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { Api } from '../../core/api.service';
import { DialogService, ToastService } from '../../core/ui.services';
import { IconComponent } from '../../shared/ui.components';

interface DemoAccount { clientId: string; company: string; email?: string; plan?: string; status: string; demoEndsAt: string; deleteOn: string; keep: boolean; devices: number; receipts: number; willBeDeleted: boolean; }

/** Demo companies that never paid: when each one will be deleted (a week after its demo ends), and the switch to keep one. */
@Component({
  selector: 'app-demo-accounts',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, IconComponent],
  template: `
    <section class="card mt-5">
      <div class="mb-3 flex flex-wrap items-center justify-between gap-3">
        <div>
          <div class="card-title mb-0! flex items-center gap-2"><app-icon name="trash" size="h-4 w-4" />Unpaid demo accounts</div>
          <p class="text-sm text-slate-500">A demo that is still unpaid a week after it ends is deleted with all its data. The company is warned when the demo ends and two days before. Keep one to stop that.</p>
        </div>
        <button class="btn-ghost btn-sm" (click)="load()"><app-icon name="refresh" size="h-4 w-4" />Refresh</button>
      </div>
      <div class="overflow-x-auto">
        <table class="table min-w-[640px]">
          <thead><tr><th>Company</th><th>Plan</th><th>Demo ends</th><th>Deleted on</th><th class="text-right">Devices / receipts</th><th class="text-right">Keep</th></tr></thead>
          <tbody>
            @for (d of rows(); track d.clientId) {
              <tr>
                <td><div class="font-medium">{{ d.company }}</div><div class="text-xs text-slate-500">{{ d.email }}</div></td>
                <td>{{ d.plan ?? '-' }} <span class="badge">{{ d.status }}</span></td>
                <td class="whitespace-nowrap">{{ d.demoEndsAt | date:'d MMM y' }}</td>
                <td class="whitespace-nowrap">
                  @if (d.willBeDeleted) { <span [class]="soon(d) ? 'font-semibold text-red-600' : ''">{{ d.deleteOn | date:'d MMM y' }}</span> }
                  @else { <span class="badge badge-ok">{{ d.keep ? 'Kept' : 'Off' }}</span> }
                </td>
                <td class="text-right tabular-nums">{{ d.devices }} / {{ d.receipts }}</td>
                <td class="text-right"><button class="btn-ghost btn-sm" (click)="toggle(d)">{{ d.keep ? 'Stop keeping' : 'Keep' }}</button></td>
              </tr>
            } @empty { <tr><td colspan="6" class="py-6 text-center text-sm text-slate-500">No unpaid demo accounts.</td></tr> }
          </tbody>
        </table>
      </div>
    </section>
  `,
})
export class DemoAccountsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly rows = signal<DemoAccount[]>([]);

  ngOnInit(): void { void this.load(); }

  soon(d: DemoAccount): boolean { return new Date(d.deleteOn).getTime() - Date.now() < 3 * 86_400_000; }

  async load(): Promise<void> {
    try { this.rows.set(await this.api.get<DemoAccount[]>('/api/v1/admin/billing/demo-accounts')); } catch (e) { this.toast.err(e); }
  }

  async toggle(d: DemoAccount): Promise<void> {
    if (d.keep && !(await this.dialog.confirm(`Stop keeping ${d.company}? If it is still unpaid it will be deleted on ${new Date(d.deleteOn).toLocaleDateString()}.`, 'Stop keeping', true))) return;
    try {
      await this.api.post(`/api/v1/admin/billing/demo-accounts/${d.clientId}/keep`, { keep: !d.keep });
      this.toast.ok(d.keep ? `${d.company} will be deleted if it stays unpaid.` : `${d.company} will be kept.`);
      await this.load();
    } catch (e) { this.toast.err(e); }
  }
}
