/** The invoice builder's layout: elements placed on an A4 page, all measures in millimetres from the top-left corner. Mirrors CanvasLayout on the server. */

export const PAGE_W = 210;
export const PAGE_H = 297;

export type ElementType = 'text' | 'field' | 'image' | 'logo' | 'shape' | 'line' | 'items' | 'totals' | 'qr' | 'fiscal';

export interface CanvasStyle {
  fontSize: number; font?: string | null; bold?: boolean; italic?: boolean; underline?: boolean; uppercase?: boolean; letterSpacing?: number; lineHeight?: number;
  color: string; align?: 'left' | 'center' | 'right'; vAlign?: 'top' | 'middle' | 'bottom';
  fill?: string | null; fill2?: string | null; gradientDir?: 'horizontal' | 'vertical' | 'diagonal';
  border?: string | null; borderWidth?: number; radius?: number; opacity?: number; padding?: number;
  headerFill?: string | null; headerColor?: string | null; stripeFill?: string | null; lineColor?: string | null; rowPadding?: number;
  fit?: 'contain' | 'stretch';
}

export interface CanvasElement {
  id: string; type: ElementType; x: number; y: number; w: number; h: number;
  text?: string | null; field?: string | null; label?: string | null; assetId?: string | null; shape?: 'rect' | 'ellipse' | null; locked?: boolean;
  columns?: string[] | null; labels?: Record<string, string> | null; style: CanvasStyle;
}

export interface CanvasLayout { background: string; elements: CanvasElement[]; }

export interface FieldInfo { key: string; label: string; group: string; }
export interface InvoiceDocLine { no: number; description: string; hsCode?: string; quantity: number; unitPrice: number; taxPercent?: number | null; amount: number; discount: boolean; }
export interface InvoiceDocument {
  fields: Record<string, string | null>; lines: InvoiceDocLine[]; taxes: { label: string; percent?: number | null; net: number; tax: number; gross: number }[];
  payments: { method: string; amount: number }[]; subtotal: number; taxTotal: number; total: number; currency: string;
  fiscal: { deviceId: number; fiscalDay: number; receiptCounter: number; globalNo: number; zimraReceiptId?: number | null; verificationUrl?: string | null; signature?: string | null; notices: string[] };
}
export interface TemplateAsset { id: string; contentType: string; size: number; name?: string; createdAt: string; }

export const ITEM_COLUMNS: { key: string; label: string }[] = [
  { key: 'no', label: '#' }, { key: 'item', label: 'Description' }, { key: 'hs', label: 'HS Code' }, { key: 'qty', label: 'Qty' },
  { key: 'price', label: 'Unit price' }, { key: 'tax', label: 'VAT' }, { key: 'total', label: 'Amount' },
];
export const TOTAL_ROWS: { key: string; label: string }[] = [
  { key: 'subtotal', label: 'Subtotal ({currency})' }, { key: 'taxes', label: 'VAT ({rate})' }, { key: 'total', label: 'TOTAL ({currency})' }, { key: 'payments', label: 'Payments' },
];
export const REQUIRED: ElementType[] = ['items', 'totals', 'qr', 'fiscal'];

/** ZIMRA's heading: the document title value, or text that says FISCAL TAX INVOICE. */
export const FISCAL_TITLE = 'FISCAL TAX INVOICE';
export function hasTitle(l: CanvasLayout): boolean {
  return l.elements.some(e => (e.type === 'field' && e.field === 'title') || (e.type === 'text' && !!e.text && (e.text.includes('{title}') || e.text.toUpperCase().includes(FISCAL_TITLE))));
}
/** A line that is a heading on its own ("INVOICE", "Tax invoice") instead of ZIMRA's wording. */
export function isBareHeading(line: string): boolean {
  const l = line.replace(/[^A-Za-z ]/g, ' ').replace(/\s+/g, ' ').trim().toUpperCase();
  return ['INVOICE', 'TAX INVOICE', 'SALES INVOICE', 'TAX INVOICE RECEIPT', 'INVOICE RECEIPT', 'RECEIPT', 'BILL', 'FISCAL INVOICE'].includes(l);
}

export const newId = (): string => Math.random().toString(36).slice(2, 10) + Date.now().toString(36).slice(-4);
const s = (o: Partial<CanvasStyle> = {}): CanvasStyle => ({ fontSize: 10, color: '#0f172a', align: 'left', vAlign: 'top', padding: 1, lineHeight: 1.2, opacity: 1, ...o });

/** A new element of a kind, with a sensible size, dropped near the middle of the page. */
export function makeElement(type: ElementType, extra: Partial<CanvasElement> = {}): CanvasElement {
  const base = (w: number, h: number, style: Partial<CanvasStyle> = {}): CanvasElement => ({ id: newId(), type, x: Math.round((PAGE_W - w) / 2), y: 120, w, h, style: s(style) });
  const e: CanvasElement = {
    text: base(80, 12, { fontSize: 11 }),
    field: base(70, 8, { fontSize: 10 }),
    image: base(40, 30, { align: 'center', vAlign: 'middle', padding: 0 }),
    logo: base(45, 25, { align: 'center', vAlign: 'middle', padding: 0 }),
    shape: base(60, 30, { fill: '#e0e7ff', padding: 0 }),
    line: base(120, 2, { border: '#94a3b8', borderWidth: 1, padding: 0 }),
    items: base(186, 90, { fontSize: 9, headerFill: '#1d4ed8', headerColor: '#ffffff', stripeFill: '#f1f5f9', lineColor: '#e2e8f0', rowPadding: 1.5 }),
    totals: base(80, 28, { fontSize: 10, headerFill: '#1d4ed8', headerColor: '#ffffff', lineColor: '#e2e8f0', rowPadding: 1.5 }),
    qr: base(30, 34, { fontSize: 8, padding: 0 }),
    fiscal: base(130, 22, { fontSize: 7.5, color: '#475569' }),
  }[type];
  if (type === 'text') e.text = 'Double-click to edit text';
  // a new value element starts as the invoice number; one from a design keeps exactly what it was given
  if (type === 'field' && !extra.field) { e.field = 'invoiceNo'; e.label = 'Invoice No:'; }
  if (type === 'shape') e.shape = 'rect';
  if (type === 'items') e.columns = ['no', 'item', 'hs', 'qty', 'price', 'total'];
  if (type === 'totals') e.columns = ['subtotal', 'taxes', 'total'];
  if (type === 'qr') e.label = 'Scan to verify';
  return { ...e, ...extra, style: { ...e.style, ...(extra.style ?? {}) } };
}

/** {placeholders} filled with the sample invoice's values, like the server does. */
export function fill(text: string | null | undefined, fields: Record<string, string | null>, known: Set<string>): string {
  return (text ?? '').replace(/\{\s*([A-Za-z]+)\s*\}/g, (m, k: string) => known.has(k) ? (fields[k] ?? '') : m);
}

export const money = (v: number): string => v.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

// ---- ready-made designs to start from --------------------------------------------------------------------------------

type El = Partial<CanvasElement> & { type: ElementType };
const L = (els: El[], background = '#ffffff'): CanvasLayout => ({ background, elements: els.map(e => makeElement(e.type, { ...e, style: s(e.style as Partial<CanvasStyle>) })) });

export interface Preset { key: string; name: string; hint: string; swatch: string; build: () => CanvasLayout; }

export const PRESETS: Preset[] = [
  {
    key: 'classic', name: 'Classic', hint: 'Logo and company on top, a coloured title band, a clean table.', swatch: 'linear-gradient(135deg,#1d4ed8,#60a5fa)',
    build: () => L([
      { type: 'logo', x: 15, y: 12, w: 45, h: 22 },
      { type: 'field', field: 'company', x: 65, y: 12, w: 130, h: 9, style: { fontSize: 16, bold: true, color: '#1d4ed8', align: 'right' } as CanvasStyle },
      { type: 'text', text: '{address}\n{phone} · {email}\nTIN {tin} · VAT {vat}', x: 65, y: 21, w: 130, h: 15, style: { fontSize: 9, align: 'right', color: '#475569' } as CanvasStyle },
      { type: 'field', field: 'title', x: 15, y: 40, w: 180, h: 10, style: { fontSize: 15, bold: true, color: '#ffffff', fill: '#1d4ed8', align: 'center', vAlign: 'middle' } as CanvasStyle },
      { type: 'text', text: 'Bill to:\n{buyerName}\nTIN {buyerTin} · VAT {buyerVat}\n{buyerAddress}', x: 15, y: 54, w: 95, h: 24, style: { fontSize: 9.5 } as CanvasStyle },
      { type: 'text', text: 'Invoice No: {invoiceNo}\nDate: {dateTime}\nCurrency: {currency}', x: 115, y: 54, w: 80, h: 24, style: { fontSize: 9.5, align: 'right' } as CanvasStyle },
      { type: 'items', x: 15, y: 82, w: 180, h: 120, columns: ['no', 'item', 'hs', 'qty', 'price', 'total'], style: { fontSize: 9, headerFill: '#1d4ed8', headerColor: '#ffffff', stripeFill: '#f1f5f9', lineColor: '#e2e8f0', rowPadding: 1.5 } as CanvasStyle },
      { type: 'totals', x: 115, y: 206, w: 80, h: 32, columns: ['subtotal', 'taxes', 'total'], style: { fontSize: 10, headerFill: '#1d4ed8', headerColor: '#ffffff', lineColor: '#e2e8f0', rowPadding: 1.5 } as CanvasStyle },
      { type: 'text', text: 'Payment details\n{bankName} · {accountName}\nAccount {accountNumber}', x: 15, y: 206, w: 95, h: 22, style: { fontSize: 8.5, color: '#475569' } as CanvasStyle },
      { type: 'qr', x: 15, y: 240, w: 32, h: 36, label: 'Scan to verify', style: { fontSize: 8, padding: 0 } as CanvasStyle },
      { type: 'fiscal', x: 50, y: 242, w: 145, h: 32, style: { fontSize: 7.5, color: '#475569' } as CanvasStyle },
    ]),
  },
  {
    key: 'elegant', name: 'Elegant pink', hint: 'Soft gradient header, a rounded title, pink table: beauty and fashion.', swatch: 'linear-gradient(135deg,#9d174d,#f9a8d4)',
    build: () => L([
      { type: 'shape', x: 0, y: 0, w: 210, h: 48, style: { fill: '#fdf2f8', fill2: '#fce7f3', padding: 0 } as CanvasStyle },
      { type: 'logo', x: 12, y: 8, w: 50, h: 30 },
      { type: 'field', field: 'company', x: 66, y: 10, w: 132, h: 12, style: { fontSize: 22, bold: true, color: '#831843', font: 'Georgia', uppercase: true, letterSpacing: 0.1 } as CanvasStyle },
      { type: 'text', text: 'Your slogan here', x: 66, y: 23, w: 132, h: 8, style: { fontSize: 10, italic: true, color: '#9d174d', font: 'Georgia' } as CanvasStyle },
      { type: 'text', text: '{address}\nTIN: {tin}   VAT: {vat}\nTax: {taxRates} Standard VAT', x: 12, y: 51, w: 186, h: 14, style: { fontSize: 9, fill: '#fdf2f8', radius: 3, padding: 2.5, color: '#831843' } as CanvasStyle },
      { type: 'field', field: 'title', x: 55, y: 69, w: 100, h: 12, style: { fontSize: 20, bold: true, color: '#ffffff', fill: '#9d174d', fill2: '#db2777', radius: 6, align: 'center', vAlign: 'middle', font: 'Georgia', letterSpacing: 0.2 } as CanvasStyle },
      { type: 'text', text: 'BILL TO:\nName: {buyerName}\nAddress: {buyerAddress}\nTIN (if applicable): {buyerTin}', x: 12, y: 85, w: 105, h: 30, style: { fontSize: 9, border: '#f9a8d4', borderWidth: 0.8, radius: 3, padding: 3 } as CanvasStyle },
      { type: 'text', text: 'Invoice No:  {invoiceNo}\nInvoice Date:  {date}\nCurrency:  {currency}', x: 122, y: 85, w: 76, h: 30, style: { fontSize: 9, fill: '#fdf2f8', radius: 3, padding: 3, lineHeight: 1.6 } as CanvasStyle },
      { type: 'items', x: 12, y: 119, w: 186, h: 100, columns: ['no', 'item', 'hs', 'qty', 'price', 'total'], labels: { item: 'DESCRIPTION (Product / Service)', price: 'UNIT PRICE ({currency})', total: 'AMOUNT ({currency})' }, style: { fontSize: 8.5, headerFill: '#9d174d', headerColor: '#ffffff', stripeFill: '#fdf2f8', lineColor: '#fbcfe8', rowPadding: 1.5 } as CanvasStyle },
      { type: 'text', text: 'PAYMENT TERMS:\n• Payment due as per agreement.\n• All amounts are in {currency}.', x: 12, y: 223, w: 100, h: 24, style: { fontSize: 8.5, fill: '#fdf2f8', radius: 3, padding: 3 } as CanvasStyle },
      { type: 'totals', x: 120, y: 223, w: 78, h: 28, columns: ['subtotal', 'taxes', 'total'], labels: { subtotal: 'SUBTOTAL ({currency})', total: 'TOTAL ({currency})' }, style: { fontSize: 9.5, headerFill: '#9d174d', headerColor: '#ffffff', lineColor: '#fbcfe8', rowPadding: 1.5 } as CanvasStyle },
      { type: 'line', x: 12, y: 252, w: 186, h: 1, style: { border: '#db2777', borderWidth: 1, padding: 0 } as CanvasStyle },
      { type: 'text', text: 'Thank you for your business!', x: 12, y: 255, w: 100, h: 10, style: { fontSize: 15, italic: true, color: '#9d174d', font: 'Georgia' } as CanvasStyle },
      { type: 'text', text: '{phone}   {email}', x: 120, y: 256, w: 78, h: 7, style: { fontSize: 8, color: '#831843', align: 'right' } as CanvasStyle },
      { type: 'qr', x: 12, y: 266, w: 26, h: 28, label: 'Scan to verify', style: { fontSize: 7, padding: 0 } as CanvasStyle },
      { type: 'fiscal', x: 42, y: 268, w: 156, h: 24, style: { fontSize: 7, color: '#6b7280' } as CanvasStyle },
    ]),
  },
  {
    key: 'modern', name: 'Modern dark', hint: 'A dark header band with white text and an accent line.', swatch: 'linear-gradient(135deg,#0f172a,#334155)',
    build: () => L([
      { type: 'shape', x: 0, y: 0, w: 210, h: 42, style: { fill: '#0f172a', fill2: '#1e293b', gradientDir: 'diagonal', padding: 0 } as CanvasStyle },
      { type: 'shape', x: 0, y: 42, w: 210, h: 2, style: { fill: '#f59e0b', padding: 0 } as CanvasStyle },
      { type: 'logo', x: 14, y: 9, w: 40, h: 24 },
      { type: 'field', field: 'company', x: 60, y: 10, w: 136, h: 10, style: { fontSize: 18, bold: true, color: '#ffffff', align: 'right' } as CanvasStyle },
      { type: 'text', text: '{address} · {phone}\nTIN {tin} · VAT {vat}', x: 60, y: 21, w: 136, h: 12, style: { fontSize: 8.5, color: '#cbd5e1', align: 'right' } as CanvasStyle },
      { type: 'field', field: 'title', x: 14, y: 50, w: 120, h: 12, style: { fontSize: 22, bold: true, color: '#0f172a', letterSpacing: 0.05 } as CanvasStyle },
      { type: 'text', text: 'No. {invoiceNo}\n{dateTime}', x: 136, y: 50, w: 60, h: 12, style: { fontSize: 9, align: 'right', color: '#475569' } as CanvasStyle },
      { type: 'text', text: 'BILLED TO\n{buyerName}\nTIN {buyerTin} · VAT {buyerVat}\n{buyerAddress}', x: 14, y: 66, w: 182, h: 22, style: { fontSize: 9, fill: '#f8fafc', padding: 3, radius: 2 } as CanvasStyle },
      { type: 'items', x: 14, y: 92, w: 182, h: 115, columns: ['item', 'qty', 'price', 'tax', 'total'], style: { fontSize: 9, headerFill: '#0f172a', headerColor: '#ffffff', lineColor: '#e2e8f0', rowPadding: 1.8 } as CanvasStyle },
      { type: 'totals', x: 116, y: 211, w: 80, h: 30, columns: ['subtotal', 'taxes', 'total'], style: { fontSize: 10, headerFill: '#f59e0b', headerColor: '#0f172a', lineColor: '#e2e8f0', rowPadding: 1.5 } as CanvasStyle },
      { type: 'qr', x: 14, y: 246, w: 30, h: 34, label: 'Scan to verify', style: { fontSize: 7.5, padding: 0 } as CanvasStyle },
      { type: 'fiscal', x: 48, y: 248, w: 148, h: 30, style: { fontSize: 7.5, color: '#475569' } as CanvasStyle },
    ]),
  },
  {
    key: 'minimal', name: 'Minimal', hint: 'Black and white, lots of space. Prints well anywhere.', swatch: 'linear-gradient(135deg,#e5e7eb,#ffffff)',
    build: () => L([
      { type: 'field', field: 'company', x: 18, y: 18, w: 120, h: 9, style: { fontSize: 15, bold: true } as CanvasStyle },
      { type: 'text', text: '{address}\nTIN {tin} · VAT {vat}', x: 18, y: 28, w: 120, h: 10, style: { fontSize: 8.5, color: '#4b5563' } as CanvasStyle },
      { type: 'field', field: 'title', x: 120, y: 18, w: 72, h: 9, style: { fontSize: 13, bold: true, align: 'right', letterSpacing: 0.15 } as CanvasStyle },
      { type: 'text', text: '{invoiceNo}\n{date}', x: 120, y: 28, w: 72, h: 10, style: { fontSize: 8.5, align: 'right', color: '#4b5563' } as CanvasStyle },
      { type: 'line', x: 18, y: 44, w: 174, h: 1, style: { border: '#111827', borderWidth: 0.6, padding: 0 } as CanvasStyle },
      { type: 'text', text: '{buyerName}\nTIN {buyerTin}\n{buyerAddress}', x: 18, y: 49, w: 120, h: 18, style: { fontSize: 9 } as CanvasStyle },
      { type: 'items', x: 18, y: 72, w: 174, h: 130, columns: ['item', 'qty', 'price', 'total'], style: { fontSize: 9, headerColor: '#111827', lineColor: '#e5e7eb', rowPadding: 2 } as CanvasStyle },
      { type: 'totals', x: 122, y: 206, w: 70, h: 28, columns: ['subtotal', 'taxes', 'total'], style: { fontSize: 9.5, lineColor: '#e5e7eb', rowPadding: 1.5 } as CanvasStyle },
      { type: 'qr', x: 18, y: 246, w: 28, h: 32, label: 'Scan to verify', style: { fontSize: 7, padding: 0 } as CanvasStyle },
      { type: 'fiscal', x: 50, y: 248, w: 142, h: 28, style: { fontSize: 7, color: '#6b7280' } as CanvasStyle },
    ]),
  },
  {
    key: 'blank', name: 'Blank page', hint: 'Only what ZIMRA requires. Build the rest yourself.', swatch: 'linear-gradient(135deg,#ffffff,#f1f5f9)',
    build: () => L([
      { type: 'field', field: 'title', x: 15, y: 20, w: 180, h: 12, style: { fontSize: 18, bold: true, align: 'center', vAlign: 'middle', letterSpacing: 0.1 } as CanvasStyle },
      { type: 'items', x: 15, y: 90, w: 180, h: 110 },
      { type: 'totals', x: 115, y: 205, w: 80, h: 28 },
      { type: 'qr', x: 15, y: 245, w: 30, h: 34 },
      { type: 'fiscal', x: 50, y: 247, w: 145, h: 30 },
    ]),
  },
];
