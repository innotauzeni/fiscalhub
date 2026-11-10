import { ChangeDetectionStrategy, Component } from '@angular/core';
import { DocumentsPanelComponent } from '../../shared/documents-panel.component';
import { PageHeaderComponent } from '../../shared/ui.components';

/** The company's file cabinet for its books: supplier invoices, contracts, tax papers and scans, kept for the years the law requires. */
@Component({
  selector: 'app-documents',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DocumentsPanelComponent, PageHeaderComponent],
  template: `
    <app-page-header title="Documents" subtitle="Keep supplier invoices, contracts and tax papers with your books. Records cannot be deleted before their keep-until date." />
    <div class="card"><app-documents-panel /></div>
  `,
})
export class DocumentsComponent {}
