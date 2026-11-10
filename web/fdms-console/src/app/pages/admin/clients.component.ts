import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../../core/api.service';
import { Client } from '../../core/models';
import { DialogService, ToastService } from '../../core/ui.services';
import { ModalComponent } from '../../shared/modal.components';
import { PageHeaderComponent, StatusComponent } from '../../shared/ui.components';

@Component({
  selector: 'app-clients',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, ModalComponent, PageHeaderComponent, StatusComponent],
  template: `
    <app-page-header title="Clients" subtitle="Each client is a separate company with its own devices, users and receipts.">
      <button class="btn" (click)="adding.set(true)">New client</button>
    </app-page-header>

    @if (adding()) {
      <app-modal title="New client" subtitle="A company with its own devices, users and receipts, plus its first administrator." (closed)="adding.set(false)">
      <form (ngSubmit)="create()">
        <div class="mb-2 text-xs font-semibold uppercase tracking-wide text-slate-400">Company</div>
        <div class="grid gap-4 sm:grid-cols-2">
          <label class="block"><span class="label">Company name</span><input class="input" name="n" [(ngModel)]="f.name" required /></label>
          <label class="block"><span class="label">Company email</span><input class="input" type="email" name="e" [(ngModel)]="f.email" /></label>
        </div>
        <div class="mb-2 mt-5 text-xs font-semibold uppercase tracking-wide text-slate-400">Administrator</div>
        <div class="grid gap-4 sm:grid-cols-2">
          <label class="block"><span class="label">Admin name</span><input class="input" name="an" [(ngModel)]="f.adminName" required /></label>
          <label class="block"><span class="label">Admin surname</span><input class="input" name="as" [(ngModel)]="f.adminSurname" /></label>
          <label class="block"><span class="label">Admin email (login)</span><input class="input" type="email" name="ae" [(ngModel)]="f.adminEmail" required /></label>
          <label class="block"><span class="label">Temporary password</span><input class="input" type="password" autocomplete="new-password" name="ap" [(ngModel)]="f.adminPassword" required /></label>
        </div>
        <p class="hint">The administrator must change this password at first login.</p>
        <div class="mt-6 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="adding.set(false)">Cancel</button><button class="btn" type="submit" [disabled]="busy()">Create client</button></div>
      </form>
      </app-modal>
    }

    <div class="card overflow-x-auto">
      <table class="table">
        <thead><tr><th>Name</th><th>Email</th><th>Users</th><th>Devices</th><th>Created</th><th>Status</th><th></th></tr></thead>
        <tbody>
          @for (c of clients(); track c.id) {
            <tr>
              <td class="font-medium">{{ c.name }}</td><td>{{ c.email }}</td><td>{{ c.userCount }}</td><td>{{ c.deviceCount }}</td>
              <td class="whitespace-nowrap">{{ c.createdAt | date:'mediumDate' }}</td>
              <td><app-status [value]="c.isActive ? 'Active' : 'Suspended'" /></td>
              <td class="text-right"><button class="btn-ghost btn-sm" (click)="toggle(c)">{{ c.isActive ? 'Suspend' : 'Reactivate' }}</button></td>
            </tr>
          } @empty { <tr><td colspan="7" class="text-slate-500">No clients.</td></tr> }
        </tbody>
      </table>
    </div>
  `,
})
export class ClientsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly clients = signal<Client[]>([]);
  readonly adding = signal(false);
  readonly busy = signal(false);
  f = { name: '', email: '', adminName: '', adminSurname: '', adminEmail: '', adminPassword: '' };

  ngOnInit(): Promise<void> { return this.load(); }

  async load(): Promise<void> { try { this.clients.set(await this.api.get<Client[]>('/api/v1/clients')); } catch (e) { this.toast.err(e); } }

  async create(): Promise<void> {
    this.busy.set(true);
    try {
      await this.api.post('/api/v1/clients', { name: this.f.name.trim(), email: this.f.email.trim() || null, adminEmail: this.f.adminEmail.trim(), adminName: this.f.adminName.trim(), adminSurname: this.f.adminSurname.trim() || null, adminPassword: this.f.adminPassword });
      this.toast.ok('Client created.');
      this.f = { name: '', email: '', adminName: '', adminSurname: '', adminEmail: '', adminPassword: '' };
      this.adding.set(false); await this.load();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async toggle(c: Client): Promise<void> {
    if (c.isActive && !(await this.dialog.confirm(`Suspend ${c.name}? Its users and API tokens stop working until reactivated.`, 'Suspend', true))) return;
    try { await this.api.put(`/api/v1/clients/${c.id}/active`, { active: !c.isActive }); await this.load(); } catch (e) { this.toast.err(e); }
  }
}
