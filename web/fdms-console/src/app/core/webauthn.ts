/** The browser side of passkeys (WebAuthn): the server speaks base64url text, the browser wants raw bytes, so everything is converted here. */

export function passkeysSupported(): boolean {
  return typeof window !== 'undefined' && !!window.PublicKeyCredential && !!navigator.credentials;
}

function toBytes(s: string): ArrayBuffer {
  const b = atob(s.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(s.length / 4) * 4, '='));
  const out = new Uint8Array(b.length);
  for (let i = 0; i < b.length; i++) out[i] = b.charCodeAt(i);
  return out.buffer;
}

function toText(buf: ArrayBuffer | null | undefined): string | undefined {
  if (!buf) return undefined;
  let s = '';
  for (const b of new Uint8Array(buf)) s += String.fromCharCode(b);
  return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

/** Drops the null members the server writes, which some browsers reject. */
function clean<T>(o: T): T { return JSON.parse(JSON.stringify(o)) as T; }

interface Descriptor { id: string; type: string; transports?: string[] }

/** Asks the device to make a new passkey; returns what the server needs to check it. */
export async function createPasskey(options: Record<string, any>): Promise<unknown> {
  const o = clean(options);
  const cred = (await navigator.credentials.create({
    publicKey: {
      ...o,
      challenge: toBytes(o['challenge']),
      user: { ...o['user'], id: toBytes(o['user'].id) },
      excludeCredentials: (o['excludeCredentials'] as Descriptor[] | undefined)?.map(d => ({ ...d, id: toBytes(d.id) })),
    } as PublicKeyCredentialCreationOptions,
  })) as PublicKeyCredential | null;
  if (!cred) throw new Error('No passkey was made.');
  const r = cred.response as AuthenticatorAttestationResponse;
  return { id: cred.id, rawId: toText(cred.rawId), type: cred.type, response: { attestationObject: toText(r.attestationObject), clientDataJSON: toText(r.clientDataJSON) }, extensions: cred.getClientExtensionResults() };
}

/** Asks the device to sign the sign-in challenge with one of its passkeys for this site. */
export async function usePasskey(options: Record<string, any>): Promise<unknown> {
  const o = clean(options);
  const cred = (await navigator.credentials.get({
    publicKey: {
      ...o,
      challenge: toBytes(o['challenge']),
      allowCredentials: (o['allowCredentials'] as Descriptor[] | undefined)?.map(d => ({ ...d, id: toBytes(d.id) })),
    } as PublicKeyCredentialRequestOptions,
  })) as PublicKeyCredential | null;
  if (!cred) throw new Error('No passkey was chosen.');
  const r = cred.response as AuthenticatorAssertionResponse;
  return {
    id: cred.id, rawId: toText(cred.rawId), type: cred.type,
    response: { authenticatorData: toText(r.authenticatorData), clientDataJSON: toText(r.clientDataJSON), signature: toText(r.signature), userHandle: toText(r.userHandle) },
    extensions: cred.getClientExtensionResults(),
  };
}
