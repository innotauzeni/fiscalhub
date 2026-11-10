import { Injectable, inject, signal } from '@angular/core';
import { Api } from './api.service';
import { Profile } from './ai.models';

/** The signed-in person's picture, shared by the header and the profile page so a new picture shows everywhere at once. */
@Injectable({ providedIn: 'root' })
export class AvatarService {
  private readonly api = inject(Api);
  readonly url = signal<string | null>(null);

  async refresh(): Promise<void> {
    const old = this.url(); if (old) URL.revokeObjectURL(old);
    this.url.set(null);
    try {
      const me = await this.api.get<Profile>('/api/v1/profile');
      if (me.hasAvatar) this.url.set(URL.createObjectURL(await this.api.blob('/api/v1/profile/avatar')));
    } catch { /* no picture: the initials stay */ }
  }

  clear(): void { const old = this.url(); if (old) URL.revokeObjectURL(old); this.url.set(null); }
}
