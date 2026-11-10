import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { Branch, Client, Device, UserDto } from '../../core/models';
import { DialogService, ToastService } from '../../core/ui.services';
import { EmptyComponent, PagerComponent, slicePage } from '../../shared/list.components';
import { SecurityPolicyComponent } from '../security-policy.component';
import { ModalComponent } from '../../shared/modal.components';
import { IconComponent, PageHeaderComponent, StatusComponent } from '../../shared/ui.components';

interface UserForm { id?: string; name: string; surname: string; role: string; email: string; userName: string; password: string; clientId: string; status: string; }

@Component({
  selector: 'app-users',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, IconComponent, ModalComponent, PageHeaderComponent, StatusComponent, PagerComponent, EmptyComponent, SecurityPolicyComponent],
  template: `
    <app-page-header title="Users" subtitle="People who can sign in. Their role decides what they see and do.">
      <button class="btn" (click)="startNew()"><app-icon name="plus" size="h-4 w-4" />New user</button>
    </app-page-header>
    <app-security-policy />

    <div class="mb-5 grid gap-4 sm:grid-cols-3">
      <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">Total</div><div class="text-2xl font-semibold">{{ users().length }}</div></div>
      <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">Active</div><div class="text-2xl font-semibold text-emerald-600">{{ activeCount() }}</div></div>
      <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">Inactive</div><div class="text-2xl font-semibold text-slate-500">{{ users().length - activeCount() }}</div></div>
    </div>

    <div class="card mb-5">
      <div class="flex flex-wrap items-end gap-3">
        <label class="block min-w-56 flex-1"><span class="label">Search</span>
          <span class="relative block"><span class="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400"><app-icon name="search" size="h-4 w-4" /></span>
            <input class="input pl-9" placeholder="Search by name or email…" [ngModel]="search()" (ngModelChange)="search.set($event); page.set(1)" /></span></label>
        <label class="block"><span class="label">Status</span>
          <select class="input" [ngModel]="status()" (ngModelChange)="status.set($event); page.set(1)"><option value="">All</option><option value="ACTIVE">Active</option><option value="INACTIVE">Inactive</option></select></label>
        <label class="block"><span class="label">Role</span>
          <select class="input" [ngModel]="role()" (ngModelChange)="role.set($event); page.set(1)"><option value="">All roles</option>@for (r of roles(); track r) { <option [value]="r">{{ r }}</option> }</select></label>
        @if (!auth.user()?.clientId) {
          <label class="block"><span class="label">Client</span>
            <select class="input" [ngModel]="client()" (ngModelChange)="client.set($event); page.set(1)"><option value="">All clients</option><option value="none">Platform (no client)</option>@for (c of clients(); track c.id) { <option [value]="c.id">{{ c.name }}</option> }</select></label>
        }
        <button class="btn-ghost" (click)="clear()">Clear</button>
      </div>
    </div>

    <div class="card">
      <div class="mb-3 flex items-center gap-3"><div class="card-title mb-0 flex items-center gap-2"><app-icon name="users" size="h-4 w-4" />All users</div><span class="badge">{{ filtered().length }} shown</span>
        <span class="flex-1"></span><button class="icon-btn" (click)="load()" aria-label="Refresh"><app-icon name="refresh" size="h-4 w-4" /></button></div>
      <div class="overflow-x-auto">
        <table class="table">
          <thead><tr><th class="w-12">#</th><th>User</th><th>Client</th><th>Role</th><th>Devices</th><th>Status</th><th>Last login</th><th class="text-right">Actions</th></tr></thead>
          <tbody>
            @for (u of rows(); track u.id; let i = $index) {
              <tr>
                <td class="text-slate-400">{{ (page() - 1) * size + i + 1 }}</td>
                <td><div class="flex items-center gap-3">
                  <span class="flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-linear-to-br from-blue-500 to-violet-500 text-xs font-semibold text-white">{{ initials(u) }}</span>
                  <span class="min-w-0"><span class="block truncate font-medium">{{ u.name }} {{ u.surname }}</span><span class="block truncate text-xs text-slate-500">{{ u.email }}@if (u.userName.toLowerCase() !== u.email.toLowerCase()) { · &#64;{{ u.userName }} }</span></span></div></td>
                <td>{{ u.clientName ?? 'Platform' }}</td>
                <td>@for (r of u.roles; track r) { <span class="badge badge-info mr-1">{{ r }}</span> }</td>
                <td>@if (u.clientId) { <span class="badge" [class.badge-warn]="u.accessScope === 'restricted'" [class.badge-ok]="u.accessScope !== 'restricted'">{{ u.accessScope === 'restricted' ? 'Limited' : 'All devices' }}</span> } @else { <span class="text-slate-400">—</span> }</td>
                <td><app-status [value]="u.status" />@if (u.mustChangePassword) { <div class="hint mt-0.5">must change password</div> }</td>
                <td class="whitespace-nowrap text-slate-500">{{ u.lastLoginDate ? (u.lastLoginDate | date:'short') : 'never' }}</td>
                <td><div class="flex justify-end gap-1.5">
                  <button class="icon-btn" (click)="edit(u)" title="Edit" aria-label="Edit"><app-icon name="edit" size="h-4 w-4" /></button>
                  <button class="icon-btn" (click)="startRole(u)" title="Change role" aria-label="Change role"><app-icon name="shield" size="h-4 w-4" /></button>
                  @if (u.clientId && auth.can('branches.manage')) { <button class="icon-btn" (click)="startAccess(u)" title="Branch and device access" aria-label="Branch and device access"><app-icon name="building" size="h-4 w-4" /></button> }
                  <button class="icon-btn" (click)="startPassword(u)" title="Change password" aria-label="Change password"><app-icon name="key" size="h-4 w-4" /></button>
                  <button class="icon-btn" (click)="resetMfa(u)" title="Reset two-step sign-in and passkeys" aria-label="Reset two-step sign-in"><app-icon name="refresh" size="h-4 w-4" /></button>
                  <button class="icon-btn" [class.icon-btn-danger]="u.status === 'ACTIVE'" (click)="toggle(u)" [title]="u.status === 'ACTIVE' ? 'Deactivate' : 'Activate'" [attr.aria-label]="u.status === 'ACTIVE' ? 'Deactivate' : 'Activate'"><app-icon [name]="u.status === 'ACTIVE' ? 'toggle' : 'user-check'" size="h-4 w-4" /></button>
                </div></td>
              </tr>
            } @empty { <tr><td colspan="8"><app-empty icon="users" title="No users found" hint="Change the filters or add a user." /></td></tr> }
          </tbody>
        </table>
      </div>
      <app-pager [page]="page()" [pageSize]="size" [total]="filtered().length" (pageChange)="page.set($event)" />
    </div>

    @if (form(); as u) {
      <app-modal [title]="editing() ? 'Edit user' : 'New user'" [subtitle]="editing() ? u.email : 'They sign in with this email and must change the temporary password.'" (closed)="form.set(null)">
        <form (ngSubmit)="save()">
          <div class="grid gap-4 sm:grid-cols-2">
            <label class="block"><span class="label">Name</span><input class="input" name="n" [(ngModel)]="u.name" required /></label>
            <label class="block"><span class="label">Surname</span><input class="input" name="s" [(ngModel)]="u.surname" /></label>
            <label class="block"><span class="label">Email</span><input class="input" type="email" name="e" [(ngModel)]="u.email" required /></label>
            <label class="block"><span class="label">User name <span class="font-normal text-slate-400">(optional)</span></span><input class="input" name="un" [(ngModel)]="u.userName" maxlength="32" placeholder="e.g. Inno" /><span class="hint">They can sign in with this or with the email.</span></label>
            @if (!editing()) {
              <label class="block"><span class="label">Temporary password</span><input class="input" type="password" autocomplete="new-password" name="p" [(ngModel)]="u.password" required /></label>
            }
            <label class="block"><span class="label">Role</span>
              <select class="input" name="r" [(ngModel)]="u.role" required>@for (r of roles(); track r) { <option [value]="r">{{ r }}</option> }</select></label>
            @if (!editing() && !auth.user()?.clientId) {
              <label class="block"><span class="label">Client</span>
                <select class="input" name="c" [(ngModel)]="u.clientId" required><option value="">— choose a company —</option>@for (c of clients(); track c.id) { <option [value]="c.id">{{ c.name }}</option> }</select></label>
            }
            @if (editing()) {
              <label class="block"><span class="label">Status</span>
                <select class="input" name="st" [(ngModel)]="u.status"><option value="ACTIVE">Active</option><option value="INACTIVE">Inactive</option></select></label>
            }
          </div>
          <div class="mt-6 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="form.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy()">{{ editing() ? 'Save changes' : 'Create user' }}</button></div>
        </form>
      </app-modal>
    }

    @if (roleFor(); as u) {
      <app-modal title="Change role" [subtitle]="u.name + ' ' + u.surname + ' · ' + u.email" width="max-w-md" (closed)="roleFor.set(null)">
        <form (ngSubmit)="saveRole()">
          <label class="block"><span class="label">New role</span>
            <select class="input" name="nr" [(ngModel)]="newRole" required>@for (r of roles(); track r) { <option [value]="r">{{ r }}</option> }</select></label>
          <p class="hint">Takes effect the next time they sign in or their session refreshes.</p>
          <div class="mt-6 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="roleFor.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy()">Change role</button></div>
        </form>
      </app-modal>
    }

    @if (accessFor(); as u) {
      <app-modal title="Branch and device access" [subtitle]="u.name + ' ' + u.surname + ' · ' + u.email" width="max-w-2xl" (closed)="accessFor.set(null)">
        <form (ngSubmit)="saveAccess()">
          <div class="grid gap-3 sm:grid-cols-2">
            <label class="flex cursor-pointer items-start gap-3 rounded-xl border p-4 transition" [class]="scope() === 'all' ? 'border-brand-500/50 bg-brand-50 dark:bg-brand-500/10' : 'border-slate-200 dark:border-slate-800'">
              <input type="radio" class="mt-1" name="scope" value="all" [ngModel]="scope()" (ngModelChange)="scope.set($event)" />
              <span><span class="block font-medium">Everything in the company</span><span class="text-xs text-slate-500">All branches and devices, including ones added later.</span></span></label>
            <label class="flex cursor-pointer items-start gap-3 rounded-xl border p-4 transition" [class]="scope() === 'restricted' ? 'border-brand-500/50 bg-brand-50 dark:bg-brand-500/10' : 'border-slate-200 dark:border-slate-800'">
              <input type="radio" class="mt-1" name="scope" value="restricted" [ngModel]="scope()" (ngModelChange)="scope.set($event)" />
              <span><span class="block font-medium">Only selected branches and devices</span><span class="text-xs text-slate-500">For a cashier or branch manager.</span></span></label>
          </div>

          @if (scope() === 'restricted') {
            <div class="mt-5">
              <div class="label">Where can they work?</div>
              <p class="mb-2 text-xs text-slate-500">Tick a <strong>whole branch</strong> to give access to every machine in it, including machines added later. Or tick <strong>only the machines</strong> they use (for example one till of a busy branch).</p>
              <div class="max-h-80 space-y-3 overflow-y-auto rounded-xl border border-slate-200 p-3 dark:border-slate-800">
                @for (g of groups(); track g.key) {
                  <section>
                    <div class="flex flex-wrap items-center gap-2 rounded-lg bg-slate-50 px-2 py-1.5 dark:bg-slate-800/50">
                      @if (g.branch) { <input type="checkbox" class="h-4 w-4" [checked]="pickedBranches().has(g.branch.id)" [indeterminate]="partial(g)" (change)="flip(pickedBranches, g.branch.id)" [attr.aria-label]="'Whole branch ' + g.title" /> }
                      <span class="font-medium">{{ g.title }}</span>
                      @if (g.branch) { <span class="font-mono text-xs text-slate-400">{{ g.branch.code }}</span> }
                      <span class="ml-auto text-xs" [class]="groupOn(g) ? 'text-emerald-700 dark:text-emerald-300' : 'text-slate-400'">{{ groupSummary(g) }}</span>
                    </div>
                    <ul class="ml-6 mt-1 space-y-0.5">
                      @for (d of g.devices; track d.id) {
                        <li><label class="flex cursor-pointer items-center gap-2 rounded-lg px-2 py-1.5 text-sm hover:bg-slate-50 dark:hover:bg-slate-800/50" [class.opacity-60]="viaBranch(d)">
                          <input type="checkbox" class="h-4 w-4" [checked]="isOn(d)" [disabled]="viaBranch(d)" (change)="flip(pickedDevices, d.id)" />
                          <span class="flex-1 truncate">{{ d.name || ('Device ' + d.zimraDeviceId) }} <span class="text-xs text-slate-400">ZIMRA {{ d.zimraDeviceId }}</span></span>
                          @if (viaBranch(d)) { <span class="text-xs text-slate-400">with the branch</span> }
                        </label></li>
                      } @empty { <li class="px-2 py-1 text-xs text-slate-400">No machines in this branch yet.</li> }
                    </ul>
                  </section>
                } @empty { <p class="p-2 text-sm text-slate-500">No devices yet.</p> }
              </div>
            </div>
            <p class="mt-3 text-sm" [class]="reachable() ? 'text-slate-500' : 'text-red-600'">{{ reachable() ? 'They can use ' + reachable() + ' device(s). Changes apply within seconds, without signing out.' : 'Choose at least one branch or device.' }}</p>
          }
          <div class="mt-6 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="accessFor.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy() || (scope() === 'restricted' && !reachable())">Save access</button></div>
        </form>
      </app-modal>
    }

    @if (pwFor(); as u) {
      <app-modal title="Change password" [subtitle]="u.name + ' ' + u.surname + ' · ' + u.email" width="max-w-md" (closed)="pwFor.set(null)">
        <form (ngSubmit)="savePassword()">
          <div class="space-y-4">
            <label class="block"><span class="label">New password</span>
              <span class="flex gap-2"><input class="input" [type]="show() ? 'text' : 'password'" autocomplete="new-password" name="np" [(ngModel)]="pw" required minlength="10" />
                <button type="button" class="btn-ghost shrink-0" (click)="show.set(!show())">{{ show() ? 'Hide' : 'Show' }}</button></span></label>
            <label class="block"><span class="label">Confirm password</span><input class="input" [type]="show() ? 'text' : 'password'" autocomplete="new-password" name="cp" [(ngModel)]="pw2" required /></label>
            <div class="flex items-center justify-between text-xs">
              <span [class]="pw && pw2 && pw !== pw2 ? 'text-red-600' : 'text-slate-500'">{{ pw && pw2 && pw !== pw2 ? 'The two passwords do not match.' : 'At least 10 characters. They must change it at next sign-in.' }}</span>
              <button type="button" class="font-medium text-brand-600 hover:underline" (click)="generate()">Generate one</button>
            </div>
          </div>
          <div class="mt-6 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="pwFor.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy() || pw.length < 10 || pw !== pw2">Set password</button></div>
        </form>
      </app-modal>
    }
  `,
})
export class UsersComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly auth = inject(AuthService);

  readonly size = 10;
  readonly users = signal<UserDto[]>([]);
  readonly roles = signal<string[]>([]);
  readonly clients = signal<Client[]>([]);
  readonly search = signal('');
  readonly status = signal('');
  readonly role = signal('');
  readonly client = signal('');
  readonly page = signal(1);
  readonly busy = signal(false);
  readonly form = signal<UserForm | null>(null);
  readonly editing = signal(false);
  readonly roleFor = signal<UserDto | null>(null);
  readonly pwFor = signal<UserDto | null>(null);
  readonly show = signal(false);
  // ---- branch / device access ----
  readonly accessFor = signal<UserDto | null>(null);
  readonly branches = signal<Branch[]>([]);
  readonly devices = signal<Device[]>([]);
  readonly scope = signal<'all' | 'restricted'>('all');
  readonly pickedBranches = signal<Set<string>>(new Set());
  readonly pickedDevices = signal<Set<string>>(new Set());
  /** Branches with the machines in them, then the machines that belong to no branch. */
  readonly groups = computed(() => {
    const ds = this.devices();
    const g: { key: string; title: string; branch: Branch | null; devices: Device[] }[] = this.branches().slice().sort((x, y) => x.name.localeCompare(y.name))
      .map(b => ({ key: b.id, title: b.name, branch: b, devices: ds.filter(d => d.branchId === b.id) }));
    const loose = ds.filter(d => !d.branchId || !this.branches().some(b => b.id === d.branchId));
    if (loose.length) g.push({ key: 'none', title: 'Not in any branch', branch: null, devices: loose });
    return g;
  });
  viaBranch(d: Device): boolean { return !!d.branchId && this.pickedBranches().has(d.branchId); }
  isOn(d: Device): boolean { return this.viaBranch(d) || this.pickedDevices().has(d.id); }
  groupOn(g: { branch: Branch | null; devices: Device[] }): boolean { return (!!g.branch && this.pickedBranches().has(g.branch.id)) || g.devices.some(d => this.pickedDevices().has(d.id)); }
  /** some, but not all, of the machines are ticked and the branch itself is not: shown as a half-ticked box */
  partial(g: { branch: Branch | null; devices: Device[] }): boolean { return !!g.branch && !this.pickedBranches().has(g.branch.id) && g.devices.some(d => this.pickedDevices().has(d.id)); }
  groupSummary(g: { branch: Branch | null; devices: Device[] }): string {
    if (g.branch && this.pickedBranches().has(g.branch.id)) return `All ${g.devices.length} machine${g.devices.length === 1 ? '' : 's'}, and any added later`;
    const n = g.devices.filter(d => this.pickedDevices().has(d.id)).length;
    return n ? `${n} of ${g.devices.length} machine${g.devices.length === 1 ? '' : 's'}` : 'No access';
  }
  /** devices the user would reach: those of the ticked branches plus the ticked devices */
  readonly reachable = computed(() => {
    const ids = new Set(this.pickedDevices());
    for (const d of this.devices()) if (d.branchId && this.pickedBranches().has(d.branchId)) ids.add(d.id);
    return ids.size;
  });
  newRole = ''; pw = ''; pw2 = '';

  flip(sig: { update: (f: (s: Set<string>) => Set<string>) => void }, id: string): void {
    sig.update(s => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n; });
  }

  async startAccess(u: UserDto): Promise<void> {
    try {
      const [branches, devices, access] = await Promise.all([
        this.api.get<Branch[]>('/api/v1/branches'), this.api.get<Device[]>('/api/v1/devices'), this.api.get<{ scope: 'all' | 'restricted'; branchIds: string[]; deviceIds: string[] }>(`/api/v1/users/${u.id}/access`)]);
      this.branches.set(branches); this.devices.set(devices);
      this.scope.set(access.scope); this.pickedBranches.set(new Set(access.branchIds)); this.pickedDevices.set(new Set(access.deviceIds));
      this.accessFor.set(u);
    } catch (e) { this.toast.err(e); }
  }

  async saveAccess(): Promise<void> {
    const u = this.accessFor(); if (!u) return;
    this.busy.set(true);
    try {
      await this.api.put(`/api/v1/users/${u.id}/access`, { scope: this.scope(), branchIds: [...this.pickedBranches()], deviceIds: [...this.pickedDevices()].filter(id => !this.viaBranch(this.devices().find(d => d.id === id) ?? ({} as Device))) });
      this.toast.ok(this.scope() === 'all' ? `${u.name} can use every device.` : `${u.name} is limited to ${this.reachable()} device(s).`);
      this.accessFor.set(null); await this.load();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  readonly activeCount = computed(() => this.users().filter(u => u.status === 'ACTIVE').length);
  readonly filtered = computed(() => {
    const q = this.search().trim().toLowerCase(), c = this.client();
    return this.users().filter(u => (!q || `${u.name} ${u.surname} ${u.email} ${u.userName}`.toLowerCase().includes(q))
      && (!this.status() || u.status === this.status()) && (!this.role() || u.roles.includes(this.role()))
      && (!c || (c === 'none' ? !u.clientId : u.clientId === c)));
  });
  readonly rows = computed(() => slicePage(this.filtered(), this.page(), this.size));

  initials(u: UserDto): string { return ((u.name?.[0] ?? '') + (u.surname?.[0] ?? '') || u.email[0]).toUpperCase(); }
  clear(): void { this.search.set(''); this.status.set(''); this.role.set(''); this.client.set(''); this.page.set(1); }

  async ngOnInit(): Promise<void> {
    try {
      this.roles.set(await this.api.get<string[]>('/api/v1/users/roles'));
      if (!this.auth.user()?.clientId) this.clients.set(await this.api.get<Client[]>('/api/v1/clients'));
    } catch (e) { this.toast.err(e); }
    await this.load();
  }

  async load(): Promise<void> { try { this.users.set(await this.api.get<UserDto[]>('/api/v1/users')); } catch (e) { this.toast.err(e); } }

  startNew(): void {
    this.editing.set(false);
    this.form.set({ name: '', surname: '', role: this.roles().includes('Viewer') ? 'Viewer' : this.roles()[0] ?? '', email: '', userName: '', password: '', clientId: '', status: 'ACTIVE' });
  }

  edit(u: UserDto): void {
    this.editing.set(true);
    this.form.set({ id: u.id, name: u.name, surname: u.surname, role: u.roles[0] ?? '', email: u.email, userName: u.userName.toLowerCase() === u.email.toLowerCase() ? '' : u.userName, password: '', clientId: u.clientId ?? '', status: u.status });
  }

  private update(u: UserDto, change: Partial<{ role: string; status: string }>): Promise<unknown> {
    return this.api.put(`/api/v1/users/${u.id}`, { name: u.name, surname: u.surname, role: change.role ?? u.roles[0], status: change.status ?? u.status });
  }

  async save(): Promise<void> {
    const f = this.form(); if (!f) return;
    this.busy.set(true);
    try {
      if (this.editing()) await this.api.put(`/api/v1/users/${f.id}`, { name: f.name.trim(), surname: f.surname.trim(), role: f.role, status: f.status, email: f.email.trim(), userName: f.userName.trim() });
      else await this.api.post('/api/v1/users', { email: f.email.trim(), userName: f.userName.trim() || null, name: f.name.trim(), surname: f.surname.trim(), role: f.role, password: f.password, clientId: f.clientId || null });
      this.toast.ok('User saved.'); this.form.set(null); await this.load();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  startRole(u: UserDto): void { this.newRole = u.roles[0] ?? this.roles()[0] ?? ''; this.roleFor.set(u); }

  async saveRole(): Promise<void> {
    const u = this.roleFor(); if (!u) return;
    this.busy.set(true);
    try { await this.update(u, { role: this.newRole }); this.toast.ok(`${u.name} is now ${this.newRole}.`); this.roleFor.set(null); await this.load(); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async toggle(u: UserDto): Promise<void> {
    const next = u.status === 'ACTIVE' ? 'INACTIVE' : 'ACTIVE';
    if (next === 'INACTIVE' && !(await this.dialog.confirm(`Deactivate ${u.name} ${u.surname}? They will no longer be able to sign in.`, 'Deactivate', true))) return;
    try { await this.update(u, { status: next }); this.toast.ok(`${u.name} is now ${next.toLowerCase()}.`); await this.load(); } catch (e) { this.toast.err(e); }
  }

  startPassword(u: UserDto): void { this.pw = ''; this.pw2 = ''; this.show.set(false); this.pwFor.set(u); }

  async resetMfa(u: UserDto): Promise<void> {
    if (!(await this.dialog.confirm(`Reset the extra protection of ${u.name} ${u.surname}? Their authenticator app and passkeys are removed and they are signed out everywhere. Do this when they have lost their phone.`, 'Reset', true))) return;
    try { await this.api.post(`/api/v1/users/${u.id}/reset-mfa`, {}); this.toast.ok('Done. They can sign in with their password and set it up again.'); } catch (e) { this.toast.err(e); }
  }

  generate(): void {
    const chars = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789', bytes = crypto.getRandomValues(new Uint8Array(14));
    this.pw = Array.from(bytes, b => chars[b % chars.length]).join('') + '!7'; this.pw2 = this.pw; this.show.set(true);
  }

  async savePassword(): Promise<void> {
    const u = this.pwFor(); if (!u) return;
    this.busy.set(true);
    try { await this.api.post(`/api/v1/users/${u.id}/reset-password`, { newPassword: this.pw }); this.toast.ok('Password changed. They must set their own at next sign-in.'); this.pwFor.set(null); await this.load(); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }
}
