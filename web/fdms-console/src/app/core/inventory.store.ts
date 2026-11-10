import { Injectable, computed, inject, signal } from '@angular/core';
import { Api } from './api.service';

export interface StockLevel { locationId: string; location: string; quantity: number }
export interface StockItem {
  id: string; code?: string | null; name: string; barcode?: string | null; unit?: string | null; hsCode?: string | null; categoryId?: string | null; category?: string | null; unitPrice: number; currency: string;
  taxPercent?: number | null; isExempt: boolean; isActive: boolean; trackStock: boolean; averageCost: number; lastCost: number; reorderLevel: number; reorderQuantity: number; onHand: number; value: number;
  belowReorder: boolean; levels: StockLevel[];
}
export interface StockCategory { id: string; parentId?: string | null; name: string; code?: string | null; inventoryAccountId?: string | null; cogsAccountId?: string | null; isActive: boolean; items: number; level: number; path: string }
export interface StockLocation { id: string; code: string; name: string; branchId?: string | null; branchName?: string | null; isDefault: boolean; isActive: boolean; items: number; value: number }
export interface StockLine { id: string; productId: string; code?: string | null; name: string; unit?: string | null; quantity: number; counted?: number | null; expected?: number | null; unitCost: number; vatPercent: number; amount: number; onHand: number }
export interface StockDocument {
  id: string; kind: string; number?: string | null; date: string; status: string; locationId: string; location: string; toLocationId?: string | null; toLocation?: string | null; supplierId?: string | null; supplier?: string | null;
  supplierReference?: string | null; mode?: string | null; reason?: string | null; notes?: string | null; subtotal: number; vat: number; total: number; sourceId?: string | null; billId?: string | null;
  journalNumber?: string | null; createdBy?: string | null; createdAt: string; postedAt?: string | null; lines: StockLine[];
}
export interface InventorySettings { startDate?: string | null; currency: string; inventoryAccountId: string; cogsAccountId: string; adjustmentAccountId: string; defaultLocationId?: string | null }

export const qty = (v: number | null | undefined): string => (v == null ? '' : v.toLocaleString(undefined, { maximumFractionDigits: 4 }));

/** Items, locations and categories, loaded once and shared by the inventory pages. */
@Injectable({ providedIn: 'root' })
export class InventoryStore {
  private readonly api = inject(Api);
  readonly items = signal<StockItem[]>([]);
  readonly locations = signal<StockLocation[]>([]);
  readonly categories = signal<StockCategory[]>([]);
  readonly settings = signal<InventorySettings | null>(null);
  readonly activeLocations = computed(() => this.locations().filter(l => l.isActive));
  readonly stockItems = computed(() => this.items().filter(i => i.trackStock && i.isActive));

  async loadItems(): Promise<StockItem[]> { const i = await this.api.get<StockItem[]>('/api/v1/inventory/items', { includeInactive: true }); this.items.set(i); return i; }
  async loadLocations(): Promise<StockLocation[]> { const l = await this.api.get<StockLocation[]>('/api/v1/inventory/locations'); this.locations.set(l); return l; }
  async loadCategories(): Promise<StockCategory[]> { const c = await this.api.get<StockCategory[]>('/api/v1/inventory/categories'); this.categories.set(c); return c; }
  async loadSettings(): Promise<InventorySettings> { const s = await this.api.get<InventorySettings>('/api/v1/inventory/settings'); this.settings.set(s); return s; }
  async loadAll(): Promise<void> { await Promise.all([this.loadItems(), this.loadLocations(), this.loadCategories(), this.loadSettings()]); }

  item(id: string): StockItem | undefined { return this.items().find(i => i.id === id); }
  label(i: StockItem): string { return (i.code ? i.code + ' · ' : '') + i.name; }
  /** An item found by what was typed: code, barcode or the exact name (or the "code · name" label of the list). */
  find(text: string): StockItem | undefined {
    const t = (text ?? '').trim().toLowerCase(); if (!t) return undefined;
    return this.stockItems().find(i => this.label(i).toLowerCase() === t || i.code?.toLowerCase() === t || i.barcode === text.trim() || i.name.toLowerCase() === t);
  }
}
