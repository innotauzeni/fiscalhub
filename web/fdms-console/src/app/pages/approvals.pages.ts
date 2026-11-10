import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { ApprovalList, ApprovalMatrix, ApprovalRequest, ApprovalRule } from '../core/models';
import { NotificationsStore } from '../core/notifications.store';
import { DialogService, ToastService } from '../core/ui.services';
import { EmptyComponent } from '../shared/list.components';
import { IconComponent, PageHeaderComponent } from '../shared/ui.components';

const STATUS_TONE: Record<string, string> = { Pending: 'badge-warn', Approved: 'badge-ok', Rejected: 'badge-bad', Cancelled: '', Failed: 'badge-bad' };

/** The approvals inbox: what waits for my decision, what I asked for, and everything (for administrators). */
@Component({
  selector: 'app-approvals',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, DecimalPipe, RouterLink, IconComponent, PageHeaderComponent, EmptyComponent],
  template: `
    <app-page-header title="Approvals" subtitle="Credit notes, cancellations, bills, payments and journals that need a second pair of eyes before they happen.">
      @if (auth.can('approvals.manage')) { <a routerLink="/approvals/matrix" class="btn-ghost"><app-icon name="layers" size="h-4 w-4" />Approval matrix</a> }
      <button class="btn-ghost" (click)="load()" aria-label="Refresh"><app-icon name="refresh" size="h-4 w-4" /></button>
    </app-page-header>

    <div class="mb-4 flex flex-wrap gap-2" role="tablist">
      @for (t of tabs; track t.key) {
        <button role="tab" class="rounded-full px-3.5 py-1.5 text-sm font-medium ring-1 transition" [attr.aria-selected]="scope() === t.key"
          [class]="scope() === t.key ? 'bg-brand-600 text-white ring-brand-600' : 'bg-white text-slate-600 ring-slate-200 hover:bg-slate-50 dark:bg-slate-900 dark:text-slate-300 dark:ring-slate-700'"
          (click)="setScope(t.key)">{{ t.label }}@if (t.key === 'mine' && list()?.waitingForMe) { <span class="ml-1.5 rounded-full bg-red-600 px-1.5 text-xs text-white">{{ list()!.waitingForMe }}</span> }</button>
      }
    </div>

    <div class="space-y-4">
      @for (r of list()?.items ?? []; track r.id) {
        <article class="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm dark:border-slate-800 dark:bg-slate-900">
          <div class="flex flex-wrap items-start justify-between gap-3">
            <div class="min-w-0">
              <div class="flex flex-wrap items-center gap-2"><span class="badge">{{ r.actionLabel }}</span><span class="badge" [class]="tone(r.status)">{{ r.status }}</span></div>
              <h2 class="mt-2 text-base font-semibold">{{ r.summary }}</h2>
              <p class="mt-0.5 text-sm text-slate-500">Asked by {{ r.requestedBy }} · {{ r.createdAt | date:'d MMM y, HH:mm' }}</p>
            </div>
            <div class="text-right"><div class="text-2xl font-semibold tabular-nums">{{ r.amount | number:'1.2-2' }}</div><div class="text-xs text-slate-500">{{ r.currency }}</div></div>
          </div>

          <!-- the levels, as a stepper -->
          <ol class="mt-4 flex flex-wrap items-center gap-2 text-xs">
            @for (lvl of r.levels; track $index) {
              @let d = decision(r, $index + 1);
              <li class="flex items-center gap-2">
                <span class="inline-flex items-center gap-1.5 rounded-full px-2.5 py-1 font-medium ring-1"
                  [class]="d?.decision === 'Approved' ? 'bg-emerald-50 text-emerald-700 ring-emerald-200 dark:bg-emerald-900/30 dark:text-emerald-300 dark:ring-emerald-800'
                    : d?.decision === 'Rejected' ? 'bg-red-50 text-red-700 ring-red-200 dark:bg-red-900/30 dark:text-red-300 dark:ring-red-800'
                    : r.status === 'Pending' && r.waitingForRole === lvl && !d ? 'bg-amber-50 text-amber-800 ring-amber-300 dark:bg-amber-900/30 dark:text-amber-200 dark:ring-amber-700'
                    : 'bg-slate-50 text-slate-500 ring-slate-200 dark:bg-slate-800 dark:text-slate-400 dark:ring-slate-700'">
                  <span class="flex h-4 w-4 items-center justify-center rounded-full bg-current/10 text-[10px]">{{ $index + 1 }}</span>{{ lvl }}
                  @if (d) { <span class="font-normal">· {{ d.userName }}</span> }
                </span>
                @if (!$last) { <app-icon name="chevron-right" size="h-3.5 w-3.5 text-slate-400" /> }
              </li>
            }
          </ol>
          @for (d of r.decisions; track $index) { @if (d.note) { <p class="mt-2 text-sm text-slate-600 dark:text-slate-300"><span class="font-medium">{{ d.userName }}:</span> “{{ d.note }}”</p> } }
          @if (r.error) { <p class="mt-2 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300">It was approved but could not be carried out: {{ r.error }}</p> }

          @if (r.canDecide || r.canCancel) {
            <div class="mt-4 flex flex-wrap justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800">
              @if (r.canCancel) { <button class="btn-ghost" [disabled]="busy() === r.id" (click)="cancel(r)">Withdraw</button> }
              @if (r.canDecide) {
                <button class="btn-danger" [disabled]="busy() === r.id" (click)="reject(r)"><app-icon name="x" size="h-4 w-4" />Reject</button>
                <button class="btn" [disabled]="busy() === r.id" (click)="approve(r)"><app-icon name="check" size="h-4 w-4" />Approve{{ r.levels.length > 1 ? ' (level ' + (r.decisions.length + 1) + ' of ' + r.levels.length + ')' : '' }}</button>
              }
            </div>
          }
        </article>
      } @empty {
        <app-empty icon="check" [title]="scope() === 'mine' ? 'Nothing waits for you' : 'No requests'" [hint]="scope() === 'mine' ? 'When something needs your approval it appears here and in the bell.' : 'Requests appear here when an action is held by the approval matrix.'" />
      }
    </div>
  `,
})
export class ApprovalsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  private readonly bell = inject(NotificationsStore);
  readonly auth = inject(AuthService);
  readonly list = signal<ApprovalList | null>(null);
  readonly scope = signal<'mine' | 'requested' | 'all'>('mine');
  readonly busy = signal<string | null>(null);
  readonly tabs = [{ key: 'mine' as const, label: 'Waiting for me' }, { key: 'requested' as const, label: 'Asked by me' }, { key: 'all' as const, label: 'All' }];

  ngOnInit(): void { void this.load(); }

  tone(s: string): string { return STATUS_TONE[s] ?? ''; }
  decision(r: ApprovalRequest, level: number) { return r.decisions.find(d => d.level === level) ?? null; }
  setScope(s: 'mine' | 'requested' | 'all'): void { this.scope.set(s); void this.load(); }

  async load(): Promise<void> {
    try { this.list.set(await this.api.get<ApprovalList>('/api/v1/approvals', { scope: this.scope() })); } catch (e) { this.toast.err(e); }
  }

  private async act(r: ApprovalRequest, verb: 'approve' | 'reject' | 'cancel', body?: unknown): Promise<void> {
    this.busy.set(r.id);
    try {
      const res = await this.api.post<ApprovalRequest>(`/api/v1/approvals/${r.id}/${verb}`, body);
      this.toast.ok(res.status === 'Approved' ? 'Approved and carried out.' : res.status === 'Failed' ? 'Approved, but it could not be carried out. See the request.' : verb === 'approve' ? `Approved. Now waiting for ${res.waitingForRole}.` : verb === 'reject' ? 'Rejected. The requester is told.' : 'Withdrawn.');
      await this.load(); void this.bell.load();
    } catch (e) { this.toast.err(e); }
    finally { this.busy.set(null); }
  }

  async approve(r: ApprovalRequest): Promise<void> {
    const note = await this.dialog.prompt(`Approve “${r.summary}” for ${r.amount.toFixed(2)} ${r.currency ?? ''}? You can add a note (optional).`, 'Note (optional)', 'Approve');
    if (note !== null) await this.act(r, 'approve', { note: note || null });
  }
  async reject(r: ApprovalRequest): Promise<void> {
    const note = await this.dialog.prompt(`Why is “${r.summary}” rejected? The requester sees this.`, 'Reason', 'Reject');
    if (note === null) return;
    if (!note.trim()) { this.toast.err(new Error('Give a reason for the rejection.')); return; }
    await this.act(r, 'reject', { note });
  }
  async cancel(r: ApprovalRequest): Promise<void> {
    if (await this.dialog.confirm(`Withdraw your request “${r.summary}”?`, 'Withdraw')) await this.act(r, 'cancel');
  }
}

/** The approval matrix: for each action, from which amount (and currency) it needs approval, and by which roles in which order. */
@Component({
  selector: 'app-approval-matrix',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DecimalPipe, FormsModule, RouterLink, IconComponent, PageHeaderComponent],
  template: `
    <app-page-header title="Approval matrix" subtitle="Decide which actions need approval, from which amount, and who approves in which order. The most specific rule wins (the highest starting amount that applies).">
      <a routerLink="/approvals" class="btn-ghost"><app-icon name="arrow-left" size="h-4 w-4" />Approvals</a>
    </app-page-header>

    @if (m(); as m) {
      <div class="space-y-5">
        @for (row of m.rows; track row.action.key) {
          <section class="rounded-2xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
            <div class="flex flex-wrap items-start justify-between gap-3">
              <div><h2 class="text-base font-semibold">{{ row.action.label }}</h2><p class="text-sm text-slate-500">{{ row.action.description }}</p><p class="hint">Amount: {{ row.action.amountMeaning }}</p></div>
              @if (canManage()) { <button class="btn-ghost btn-sm" (click)="startNew(row.action.key)"><app-icon name="plus" size="h-4 w-4" />Add a rule</button> }
            </div>

            @if (row.rules.length) {
              <div class="mt-4 overflow-x-auto">
                <table class="table min-w-[560px]">
                  <thead><tr><th>From amount</th><th>Currency</th><th>Approved by (in order)</th><th>Active</th><th></th></tr></thead>
                  <tbody>
                    @for (r of row.rules; track r.id) {
                      <tr [class.opacity-50]="!r.isActive">
                        <td class="tabular-nums font-medium">{{ r.minAmount | number:'1.2-2' }}</td>
                        <td>{{ r.currency ?? 'Any' }}</td>
                        <td><div class="flex flex-wrap items-center gap-1.5">@for (l of r.levels; track $index) { <span class="badge">{{ $index + 1 }}. {{ l }}</span>@if (!$last) { <app-icon name="chevron-right" size="h-3 w-3 text-slate-400" /> } }</div></td>
                        <td>{{ r.isActive ? 'Yes' : 'No' }}</td>
                        <td class="whitespace-nowrap text-right">@if (canManage()) { <button class="btn-ghost btn-sm" (click)="edit(r)">Edit</button><button class="btn-ghost btn-sm text-red-600" (click)="remove(r)">Delete</button> }</td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            } @else { <p class="mt-3 text-sm text-slate-500">No rule: it happens straight away.</p> }

            @if (draft()?.action === row.action.key) {
              <form class="mt-4 grid gap-4 rounded-xl bg-slate-50 p-4 sm:grid-cols-2 dark:bg-slate-800/50" (ngSubmit)="save()">
                <label class="block"><span class="label">From amount</span><input class="input" type="number" min="0" step="0.01" name="min" [(ngModel)]="draft()!.minAmount" required /></label>
                <label class="block"><span class="label">Currency (empty = any)</span><input class="input uppercase" maxlength="3" name="cur" [(ngModel)]="draft()!.currency" placeholder="USD" /></label>
                <div class="sm:col-span-2">
                  <span class="label">Approved by, in this order (1 to 5 levels)</span>
                  <div class="flex flex-wrap items-center gap-2">
                    @for (l of draft()!.levels; track $index) {
                      <span class="inline-flex items-center gap-1 rounded-full bg-white px-2.5 py-1 text-sm ring-1 ring-slate-200 dark:bg-slate-900 dark:ring-slate-700">{{ $index + 1 }}. {{ l }}
                        <button type="button" class="text-slate-400 hover:text-red-600" (click)="removeLevel($index)" [attr.aria-label]="'Remove ' + l"><app-icon name="x" size="h-3.5 w-3.5" /></button></span>
                    }
                    @if (draft()!.levels.length < 5) {
                      <select class="input w-auto" name="add" [ngModel]="''" (ngModelChange)="addLevel($event)">
                        <option value="" disabled>Add a level…</option>
                        @for (role of m.roles; track role) { <option [value]="role">{{ role }}</option> }
                      </select>
                    }
                  </div>
                  <p class="hint mt-1">Each level must be decided by a different person, and never by the person who asked.</p>
                </div>
                <label class="flex items-center gap-2 text-sm"><input type="checkbox" name="active" [(ngModel)]="draft()!.isActive" />Active</label>
                <div class="flex justify-end gap-2 sm:col-span-2">
                  <button type="button" class="btn-ghost" (click)="draft.set(null)">Cancel</button>
                  <button class="btn" [disabled]="saving() || !draft()!.levels.length">Save rule</button>
                </div>
              </form>
            }
          </section>
        }
      </div>
    }
  `,
})
export class ApprovalMatrixComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  private readonly auth = inject(AuthService);
  readonly m = signal<ApprovalMatrix | null>(null);
  readonly draft = signal<{ id?: string; action: string; minAmount: number; currency: string; levels: string[]; isActive: boolean } | null>(null);
  readonly saving = signal(false);
  readonly canManage = computed(() => this.auth.can('approvals.manage'));

  ngOnInit(): void { void this.load(); }

  async load(): Promise<void> { try { this.m.set(await this.api.get<ApprovalMatrix>('/api/v1/approvals/matrix')); } catch (e) { this.toast.err(e); } }

  startNew(action: string): void { this.draft.set({ action, minAmount: 0, currency: '', levels: [], isActive: true }); }
  edit(r: ApprovalRule): void { this.draft.set({ id: r.id, action: r.action, minAmount: r.minAmount, currency: r.currency ?? '', levels: [...r.levels], isActive: r.isActive }); }
  addLevel(role: string): void { const d = this.draft(); if (d && role) this.draft.set({ ...d, levels: [...d.levels, role] }); }
  removeLevel(i: number): void { const d = this.draft(); if (d) this.draft.set({ ...d, levels: d.levels.filter((_, j) => j !== i) }); }

  async save(): Promise<void> {
    const d = this.draft();
    if (!d) return;
    this.saving.set(true);
    try {
      await this.api.put('/api/v1/approvals/rules', { id: d.id ?? null, action: d.action, minAmount: Number(d.minAmount) || 0, currency: d.currency.trim().toUpperCase() || null, levels: d.levels, isActive: d.isActive });
      this.toast.ok('Rule saved. It applies from now on.');
      this.draft.set(null); await this.load();
    } catch (e) { this.toast.err(e); }
    finally { this.saving.set(false); }
  }

  async remove(r: ApprovalRule): Promise<void> {
    if (!await this.dialog.confirm(`Delete the rule for ${r.actionLabel} from ${r.minAmount.toFixed(2)}? Requests already waiting are not affected.`, 'Delete', true)) return;
    try { await this.api.delete(`/api/v1/approvals/rules/${r.id}`); this.toast.ok('Rule deleted.'); await this.load(); } catch (e) { this.toast.err(e); }
  }
}
